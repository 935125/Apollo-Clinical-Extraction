using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Apollo.Backend.Configuration;

public static class LocalDevelopmentConfiguration
{
    public static void AddLocalDevelopmentDefaults(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment()) return;
        string apolloAz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApolloTools", "AzureCLI", "bin");
        if (Directory.Exists(apolloAz))
        {
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(apolloAz, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", apolloAz + Path.PathSeparator + currentPath);
            }
        }
        string path = Path.Combine(builder.Environment.ContentRootPath, "..", ".env");
        if (!File.Exists(path)) path = Path.Combine(builder.Environment.ContentRootPath, ".env");
        if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(path)) return;
        var values = new Dictionary<string, string?>();
        foreach (string line in File.ReadLines(path))
        {
            string text = line.Trim(); int equals = text.IndexOf('=');
            if (text.StartsWith('#') || equals <= 0) continue;
            string value = text[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            values[text[..equals].Trim().Replace("__", ":")] = value;
        }
        // Local file is a Development default, below User Secrets, real environment and CLI.
        int index = builder.Configuration.Sources.ToList().FindIndex(s => s is JsonConfigurationSource j && j.Path == "secrets.json" ||
            s is EnvironmentVariablesConfigurationSource e && string.IsNullOrEmpty(e.Prefix));
        builder.Configuration.Sources.Insert(index < 0 ? builder.Configuration.Sources.Count : index, new MemoryConfigurationSource { InitialData = values });
    }
}
