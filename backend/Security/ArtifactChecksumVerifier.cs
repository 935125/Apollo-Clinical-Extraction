using System.Security.Cryptography;
using System.Text.Json;

namespace Apollo.Backend.Security;

public class ArtifactChecksumException : Exception
{
    public ArtifactChecksumException(string message) : base(message) { }
    public ArtifactChecksumException(string message, Exception inner) : base(message, inner) { }
}

public class ArtifactChecksumVerifier
{
    public static string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Artifact file not found: {filePath}", filePath);
        }

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hashBytes);
    }

    public static Dictionary<string, string> VerifyManifest(string manifestPath, bool allowMissing = false)
    {
        if (!File.Exists(manifestPath))
        {
            throw new ArtifactChecksumException($"Artifact manifest not found: {manifestPath}");
        }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
        string jsonContent = File.ReadAllText(manifestPath);

        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        if (!root.TryGetProperty("files", out var filesProp) || filesProp.ValueKind != JsonValueKind.Object)
        {
            throw new ArtifactChecksumException("Manifest must declare a non-empty 'files' object mapping.");
        }

        var verified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var prop in filesProp.EnumerateObject())
        {
            string relPath = prop.Name.Replace('\\', '/');
            string expectedDigest;

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                expectedDigest = prop.Value.GetString()!.ToLowerInvariant();
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("sha256", out var shaProp))
            {
                expectedDigest = shaProp.GetString()!.ToLowerInvariant();
            }
            else
            {
                failures.Add($"{relPath}: invalid digest specification in manifest");
                continue;
            }

            expectedDigest = expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? expectedDigest[7..]
                : expectedDigest;

            string fullTarget = Path.GetFullPath(Path.Combine(baseDir, relPath));
            if (!fullTarget.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{relPath}: path escapes artifact directory");
                continue;
            }

            if (!File.Exists(fullTarget))
            {
                if (!allowMissing)
                {
                    failures.Add($"{relPath}: file missing on disk");
                }
                continue;
            }

            string actualDigest = ComputeSha256(fullTarget);
            if (!string.Equals(expectedDigest, actualDigest, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{relPath}: SHA-256 mismatch (expected {expectedDigest}, got {actualDigest})");
            }
            else
            {
                verified[relPath] = actualDigest;
            }
        }

        if (failures.Count > 0)
        {
            throw new ArtifactChecksumException("Artifact verification failed:\n" + string.Join("\n", failures));
        }

        return verified;
    }

    public static string? LocateArtifactsDirectory(string? explicitPath = null)
    {
        string[] candidates = [
            explicitPath ?? "",
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "bc5cdr_latest"),
            Path.Combine(Directory.GetCurrentDirectory(), "backend", "artifacts", "bc5cdr_latest"),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts"),
            Path.Combine(AppContext.BaseDirectory, "artifacts", "bc5cdr_latest"),
            Path.Combine(AppContext.BaseDirectory, "artifacts"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "artifacts", "bc5cdr_latest"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "bc5cdr_latest"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "backend", "artifacts", "bc5cdr_latest"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "NER", "artifacts", "bc5cdr_latest"),
            @"D:\AI Extraction\backend\artifacts\bc5cdr_latest",
            @"D:\NER\artifacts\bc5cdr_latest"
        ];

        foreach (var dir in candidates)
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                string manifest = Path.Combine(dir, "artifact_manifest.json");
                if (File.Exists(manifest))
                {
                    return Path.GetFullPath(dir);
                }
            }
        }

        return null;
    }
}

