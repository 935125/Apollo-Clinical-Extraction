namespace Cx.Domain.Enums;

public enum ExtractionRunStatus
{
    Received,
    Processing,
    Succeeded,
    FailedRetryable,
    FailedTerminal,
    Superseded
}

public enum ReviewState
{
    Pending,
    InReview,
    Completed,
    Superseded,
    NoItems
}

public enum ItemReviewStatus
{
    Pending,
    Confirmed,
    Corrected,
    Rejected,
    Superseded
}

public enum ReviewDecisionType
{
    Confirm,
    Correct,
    Reject,
    SelectAlternative
}

public enum MedicationAssertionStatus
{
    NewOrder,
    CurrentMedication,
    Historical,
    Discontinued,
    Negated,
    Suggested,
    Conditional,
    Uncertain
}

public enum InvestigationAssertionStatus
{
    NewOrder,
    Historical,
    Negated,
    Suggested,
    Conditional,
    Uncertain
}

public enum NormalizationStatus
{
    Matched,
    Ambiguous,
    Unmatched,
    NotAttempted
}

