using System.Collections.Concurrent;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Storage;

public class InMemoryExtractionRepository : IExtractionRepository
{
    private readonly ConcurrentDictionary<string, (DateTime SavedAt, ClinicalExtractionResponse Response)> _extractions = new();
    private readonly ConcurrentDictionary<string, (DateTime CreatedAt, QuarantineItemDto Item)> _quarantineItems = new();
    private readonly ConcurrentDictionary<string, List<EntityReviewRecordDto>> _entityReviews = new();
    private readonly object _reviewLock = new();

    public virtual Task SaveExtractionAsync(string runId, ClinicalExtractionResponse response, CancellationToken ct = default, IReadOnlyList<QuarantineItemDto>? quarantineItems = null)
    {
        var withEmbedding = response.Embedding != null
            ? response
            : response with
            {
                Embedding = ClinicalEmbeddingGenerator.GenerateEmbedding(
                    response.NoteText ?? string.Join(" ", response.Medications.Select(m => m.Name).Concat(response.Investigations.Select(i => i.Name))))
            };
        ct.ThrowIfCancellationRequested();
        foreach (var item in quarantineItems ?? []) _quarantineItems[item.QuarantineId] = (DateTime.UtcNow, item);
        _extractions[response.ExtractionId] = (DateTime.UtcNow, withEmbedding);
        return Task.CompletedTask;
    }

    public Task<ClinicalExtractionResponse?> GetExtractionAsync(string extractionId, CancellationToken ct = default)
    {
        if (_extractions.TryGetValue(extractionId, out var entry))
        {
            List<EntityReviewRecordDto>? reviews = null;
            if (_entityReviews.TryGetValue(extractionId, out var list))
            {
                lock (_reviewLock)
                {
                    reviews = list.OrderBy(r => r.EntityType).ThenBy(r => r.EntityIndex).ThenBy(r => r.VersionNumber).ToList();
                }
            }
            return Task.FromResult<ClinicalExtractionResponse?>(entry.Response with { EntityReviews = reviews ?? entry.Response.EntityReviews ?? [] });
        }
        return Task.FromResult<ClinicalExtractionResponse?>(null);
    }

    public Task<IReadOnlyList<ClinicalExtractionResponse>> ListExtractionsAsync(int limit = 50, CancellationToken ct = default)
    {
        int clampedLimit = Math.Clamp(limit, 1, 500);
        IReadOnlyList<ClinicalExtractionResponse> list = _extractions.Values
            .OrderByDescending(v => v.SavedAt)
            .Take(clampedLimit)
            .Select(v => v.Response)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<SemanticSearchResultDto>> SearchBySemanticSimilarityAsync(
        string query,
        int limit = 10,
        double minSimilarity = 0.05,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SemanticSearchResultDto>>([]);

        int clampedLimit = Math.Clamp(limit, 1, 100);
        var queryEmbedding = ClinicalEmbeddingGenerator.GenerateEmbedding(query);
        var queryTokens = query.ToLowerInvariant().Split([' ', ',', '.', ';'], StringSplitOptions.RemoveEmptyEntries);

        IReadOnlyList<SemanticSearchResultDto> results = _extractions.Values
            .Select(v =>
            {
                var emb = v.Response.Embedding ?? ClinicalEmbeddingGenerator.GenerateEmbedding(v.Response.NoteText);
                double sim = ClinicalEmbeddingGenerator.ComputeCosineSimilarity(queryEmbedding, emb);

                var highlights = new List<string>();
                foreach (var med in v.Response.Medications)
                {
                    if (queryTokens.Any(qt => med.Name.Contains(qt, StringComparison.OrdinalIgnoreCase) ||
                                              (med.Normalization.DisplayName?.Contains(qt, StringComparison.OrdinalIgnoreCase) ?? false)))
                    {
                        highlights.Add($"Medication: {med.Name} ({med.Dose ?? med.Strength ?? "Active"})");
                    }
                }
                foreach (var inv in v.Response.Investigations)
                {
                    if (queryTokens.Any(qt => inv.Name.Contains(qt, StringComparison.OrdinalIgnoreCase) ||
                                              (inv.Normalization.DisplayName?.Contains(qt, StringComparison.OrdinalIgnoreCase) ?? false)))
                    {
                        highlights.Add($"Investigation: {inv.Name}");
                    }
                }
                if (highlights.Count == 0 && v.Response.Medications.Count > 0)
                {
                    highlights.AddRange(v.Response.Medications.Take(2).Select(m => $"Medication: {m.Name}"));
                }

                return new SemanticSearchResultDto(v.Response, sim, highlights);
            })
            .Where(r => r.SimilarityScore >= minSimilarity)
            .OrderByDescending(r => r.SimilarityScore)
            .Take(clampedLimit)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<QuarantineItemDto> AddQuarantineItemAsync(QuarantineItemDto item, CancellationToken ct = default)
    {
        string id = string.IsNullOrWhiteSpace(item.QuarantineId) ? Guid.NewGuid().ToString("D") : item.QuarantineId;
        string createdAt = string.IsNullOrWhiteSpace(item.CreatedAt) ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz") : item.CreatedAt;
        var saved = item with { QuarantineId = id, CreatedAt = createdAt };
        _quarantineItems[id] = (DateTime.UtcNow, saved);
        return Task.FromResult(saved);
    }

    public Task<IReadOnlyList<QuarantineItemDto>> ListQuarantineItemsAsync(string? status = null, int limit = 100, CancellationToken ct = default)
    {
        int clamped = Math.Clamp(limit, 1, 500);
        var query = _quarantineItems.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(q => q.Item.ReviewStatus.Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        IReadOnlyList<QuarantineItemDto> list = query
            .OrderByDescending(q => q.CreatedAt)
            .Take(clamped)
            .Select(q => q.Item)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<QuarantineItemDto?> ResolveQuarantineItemAsync(string quarantineId, QuarantineResolutionRequest resolution, CancellationToken ct = default)
    {
        if (!_quarantineItems.TryGetValue(quarantineId, out var existing))
        {
            return Task.FromResult<QuarantineItemDto?>(null);
        }

        var updated = existing.Item with
        {
            ReviewStatus = resolution.ReviewStatus,
            MappedCode = resolution.MappedCode ?? existing.Item.MappedCode,
            MappedDisplay = resolution.MappedDisplay ?? existing.Item.MappedDisplay,
            ReviewerNotes = resolution.ReviewerNotes ?? existing.Item.ReviewerNotes,
            ReviewedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz")
        };

        _quarantineItems[quarantineId] = (existing.CreatedAt, updated);
        return Task.FromResult<QuarantineItemDto?>(updated);
    }

    public Task<EntityReviewRecordDto> AddEntityReviewAsync(
        string extractionId,
        EntityReviewSubmissionRequest request,
        string reviewerIdentity,
        string authMode,
        CancellationToken ct = default)
    {
        if (!_extractions.TryGetValue(extractionId, out var entry))
            throw new KeyNotFoundException($"Extraction '{extractionId}' was not found.");

        var (origSnapshot, origEvidence) = BuildEntitySnapshot(entry.Response, request.EntityType, request.EntityIndex);
        var list = _entityReviews.GetOrAdd(extractionId, _ => new List<EntityReviewRecordDto>());
        EntityReviewRecordDto record;
        lock (_reviewLock)
        {
            int nextVersion = list
                .Where(r => r.EntityType.Equals(request.EntityType, StringComparison.OrdinalIgnoreCase) && r.EntityIndex == request.EntityIndex)
                .Select(r => r.VersionNumber)
                .DefaultIfEmpty(0)
                .Max() + 1;

            if (request.ExpectedVersion is int expected && expected != nextVersion - 1) throw new ReviewConflictException();

            record = new EntityReviewRecordDto(
                ReviewId: Guid.NewGuid().ToString("D"),
                ExtractionId: extractionId,
                EntityType: request.EntityType.ToUpperInvariant(),
                EntityIndex: request.EntityIndex,
                ReviewStatus: request.ReviewStatus.ToUpperInvariant(),
                OriginalEntity: origSnapshot,
                CorrectedEntity: request.CorrectedEntity,
                OriginalEvidence: origEvidence,
                ReviewAction: request.ReviewStatus.ToUpperInvariant(),
                reviewerIdentity,
                authMode,
                ReviewComment: request.ReviewComment,
                ReviewedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                VersionNumber: nextVersion
            );
            list.Add(record);
        }

        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<EntityReviewRecordDto>> ListEntityReviewsAsync(string extractionId, CancellationToken ct = default)
    {
        if (_entityReviews.TryGetValue(extractionId, out var list))
        {
            lock (_reviewLock)
            {
                IReadOnlyList<EntityReviewRecordDto> copy = list
                    .OrderBy(r => r.EntityType)
                    .ThenBy(r => r.EntityIndex)
                    .ThenBy(r => r.VersionNumber)
                    .ToList();
                return Task.FromResult(copy);
            }
        }
        return Task.FromResult<IReadOnlyList<EntityReviewRecordDto>>([]);
    }

    public static (Dictionary<string, object?> Snapshot, EvidenceDto Evidence) BuildEntitySnapshot(
        ClinicalExtractionResponse extraction,
        string entityType,
        int entityIndex)
    {
        if (entityType.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
        {
            if (entityIndex < 0 || entityIndex >= extraction.Medications.Count)
                throw new ArgumentOutOfRangeException(nameof(entityIndex), $"Medication index {entityIndex} is out of bounds.");
            var m = extraction.Medications[entityIndex];
            return (new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["strength"] = m.Strength,
                ["dose"] = m.Dose,
                ["doseUnit"] = m.DoseUnit,
                ["formulation"] = m.Formulation,
                ["route"] = m.Route,
                ["frequency"] = m.Frequency,
                ["duration"] = m.Duration,
                ["instructions"] = m.Instructions,
                ["action"] = m.Action,
                ["assertionStatus"] = m.AssertionStatus,
                ["rxNormCode"] = m.Normalization.MatchedCode ?? m.Normalization.CandidateCode,
                ["semanticStatus"] = m.Normalization.SemanticStatus,
                ["origin"] = m.Origin
            }, m.Evidence);
        }

        if (entityType.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase))
        {
            if (entityIndex < 0 || entityIndex >= extraction.Investigations.Count)
                throw new ArgumentOutOfRangeException(nameof(entityIndex), $"Investigation index {entityIndex} is out of bounds.");
            var inv = extraction.Investigations[entityIndex];
            return (new Dictionary<string, object?>
            {
                ["name"] = inv.Name,
                ["timing"] = inv.Timing,
                ["specimen"] = inv.Specimen,
                ["method"] = inv.Method,
                ["assertionStatus"] = inv.AssertionStatus,
                ["loincCode"] = inv.Normalization.MatchedCode ?? inv.Normalization.CandidateCode,
                ["semanticStatus"] = inv.Normalization.SemanticStatus,
                ["clinicalCategory"] = inv.ClinicalCategory,
                ["resultValue"] = inv.ResultValue,
                ["origin"] = inv.Origin
            }, inv.Evidence);
        }

        throw new ArgumentException($"Unsupported entityType '{entityType}'. Expected MEDICATION or INVESTIGATION.");
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}
