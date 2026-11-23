using Apollo.Backend.Models;

namespace Apollo.Backend.Storage;

public interface IExtractionRepository
{
    Task SaveExtractionAsync(string runId, ClinicalExtractionResponse response, CancellationToken ct = default, IReadOnlyList<QuarantineItemDto>? quarantineItems = null);
    Task<ClinicalExtractionResponse?> GetExtractionAsync(string extractionId, CancellationToken ct = default);
    Task<IReadOnlyList<ClinicalExtractionResponse>> ListExtractionsAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<SemanticSearchResultDto>> SearchBySemanticSimilarityAsync(string query, int limit = 10, double minSimilarity = 0.05, CancellationToken ct = default);
    Task<QuarantineItemDto> AddQuarantineItemAsync(QuarantineItemDto item, CancellationToken ct = default);
    Task<IReadOnlyList<QuarantineItemDto>> ListQuarantineItemsAsync(string? status = null, int limit = 100, CancellationToken ct = default);
    Task<QuarantineItemDto?> ResolveQuarantineItemAsync(string quarantineId, QuarantineResolutionRequest resolution, CancellationToken ct = default);
    Task<EntityReviewRecordDto> AddEntityReviewAsync(string extractionId, EntityReviewSubmissionRequest request, string reviewerIdentity, string authMode, CancellationToken ct = default);
    Task<IReadOnlyList<EntityReviewRecordDto>> ListEntityReviewsAsync(string extractionId, CancellationToken ct = default);
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}

public sealed class ReviewConflictException : Exception;
