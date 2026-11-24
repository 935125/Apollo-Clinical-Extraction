using System.Security.Cryptography;
using System.Text;

namespace Apollo.Backend.Emr;

public sealed record DoctorProfile(Guid Id, string Login, string DisplayName, string Role);
public sealed record LoginRequest(string? Login, string? Password);
public sealed record DraftRequest(string? Text, int ExpectedVersion = 0);
public sealed record SignRequest(int ExpectedVersion);
public sealed record EncounterDto(Guid Id, string Reference, string PatientReference, DateTimeOffset EncounterDate,
    string Context, Guid? NoteId, int? Version, string? NoteStatus, string? ExtractionStatus, Guid? ExtractionId);
public sealed record NoteDto(Guid Id, Guid EncounterId, string PatientReference, string EncounterReference,
    DateTimeOffset EncounterDate, string Context, string Text, int Version, string Status,
    DateTimeOffset UpdatedAt, DateTimeOffset? SignedAt, string? ExtractionStatus, Guid? ExtractionId, string? Error);
public sealed record ClinicalNoteSigned(Guid EventId, string EventType, int SchemaVersion, Guid NoteId,
    int NoteVersion, Guid CorrelationId, DateTimeOffset OccurredAtUtc)
{
    public bool IsValid => EventId != Guid.Empty && NoteId != Guid.Empty && CorrelationId != Guid.Empty &&
        EventType == "ClinicalNoteSigned" && SchemaVersion == 1 && NoteVersion > 0 && OccurredAtUtc != default;
}
public sealed record OutboxClaim(ClinicalNoteSigned Event, Guid LeaseId, int AttemptCount);
public sealed record SignedSource(string Text, string Hash, Guid EncounterId);
public sealed class EmrConflictException : Exception;
public sealed class EmrNotFoundException : Exception;
public sealed class InvalidEmrEventException : Exception;
public static class EmrHash
{
    public static string Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
