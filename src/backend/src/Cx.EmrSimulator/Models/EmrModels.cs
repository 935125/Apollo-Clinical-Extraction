namespace Cx.EmrSimulator.Models;

public record NoteWriteRequest(
    string NoteType,
    string Text,
    string AuthoredBy
);

public record NoteVersionResponse(
    string SourceSystem,
    string PatientId,
    string EncounterId,
    string NoteId,
    int NoteVersion,
    string NoteType,
    string? SpecialtyCode,
    string FacilityId,
    string? AuthoredBy,
    string Text,
    string ContentSha256,
    DateTime CommittedAt
);

public record ProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string Code,
    string Detail
);

