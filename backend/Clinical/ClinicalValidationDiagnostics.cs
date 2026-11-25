using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

// Only allowlisted identifiers reach exceptions, structured logs and failure routing metadata.
// No entity text, quote, attribute value, patient identifier or provider body is retained here.
public enum ClinicalValidationRule
{
    LOCAL_EVIDENCE_REJECTED, CANDIDATE_COLLECTION_INVALID, CANDIDATE_ENTITY_NULL,
    EVIDENCE_CONTRACT_INVALID, EVIDENCE_QUOTE_NOT_IN_SOURCE, EVIDENCE_OCCURRENCE_NOT_FOUND,
    ENTITY_NAME_NOT_IN_EVIDENCE,
    MEDICATION_CONDITIONAL_ASSERTION_MISMATCH, MEDICATION_NEGATION_ASSERTION_MISMATCH,
    MEDICATION_ACTION_UNSUPPORTED, MEDICATION_INACTIVE_ASSERTION_MISMATCH,
    MEDICATION_NEGATED_ACTION_CONFLICT, MEDICATION_NEGATION_UNSUPPORTED,
    MEDICATION_ACTION_ASSERTION_INVALID, MEDICATION_ACTION_ASSERTION_CONFLICT,
    MEDICATION_ACTION_TEMPORAL_CONFLICT,
    MEDICATION_ATTRIBUTE_UNSUPPORTED, MEDICATION_DURATION_UNSUPPORTED, MEDICATION_INSTRUCTIONS_UNSUPPORTED,
    MEDICATION_DOSE_UNIT_UNSUPPORTED, MEDICATION_DOSE_BINDING_MISMATCH,
    MEDICATION_EMPTY_ROUTE_OR_FREQUENCY, MEDICATION_ROUTE_UNSUPPORTED, MEDICATION_FREQUENCY_UNSUPPORTED,
    MEDICATION_STRENGTH_BINDING_MISMATCH, MEDICATION_RELATIVE_DOSE_TRUNCATED,
    MEDICATION_HISTORICAL_ASSERTION_MISMATCH,
    INVESTIGATION_ASSERTION_STATUS_INVALID, INVESTIGATION_CONDITIONAL_ASSERTION_MISMATCH,
    INVESTIGATION_CONDITION_UNSUPPORTED, INVESTIGATION_HISTORICAL_CONTEXT_UNSUPPORTED,
    INVESTIGATION_FINAL_RESULT_UNSUPPORTED, INVESTIGATION_NEGATION_ASSERTION_MISMATCH,
    INVESTIGATION_TIMING_UNSUPPORTED, INVESTIGATION_SPECIMEN_UNSUPPORTED,
    INVESTIGATION_TEMPORAL_ASSERTION_MISMATCH,
    VALIDATION_SUMMARY_ERRORS
}

public enum ClinicalValidationEntity { document, medication, investigation }
public enum ClinicalValidationField { entity, evidence, name, action, assertionStatus, status, strength, dose, doseUnit, route, frequency, duration, formulation, instructions, timing, specimen }

public sealed class InvalidClinicalEvidenceException : Exception
{
    public ClinicalValidationRule Rule { get; }
    public ClinicalValidationDiagnosticDto Diagnostic { get; private set; }
    public override string Message => $"Unsupported clinical evidence: rule={Diagnostic.RuleId}; entityType={Diagnostic.EntityType}; entityIndex={Diagnostic.EntityIndex}; field={Diagnostic.Field}";

    public InvalidClinicalEvidenceException(ClinicalValidationRule rule = ClinicalValidationRule.LOCAL_EVIDENCE_REJECTED,
        ClinicalValidationField field = ClinicalValidationField.evidence) : base("Unsupported clinical evidence")
    {
        Rule = rule;
        string category = rule switch
        {
            ClinicalValidationRule.EVIDENCE_CONTRACT_INVALID or ClinicalValidationRule.EVIDENCE_QUOTE_NOT_IN_SOURCE or
            ClinicalValidationRule.EVIDENCE_OCCURRENCE_NOT_FOUND or ClinicalValidationRule.ENTITY_NAME_NOT_IN_EVIDENCE => "source_evidence",
            ClinicalValidationRule.INVESTIGATION_HISTORICAL_CONTEXT_UNSUPPORTED or ClinicalValidationRule.INVESTIGATION_FINAL_RESULT_UNSUPPORTED or
            ClinicalValidationRule.INVESTIGATION_TEMPORAL_ASSERTION_MISMATCH => "investigation_temporality",
            _ when rule.ToString().Contains("ASSERTION") || rule.ToString().Contains("ACTION") || rule.ToString().Contains("NEGATION") || rule.ToString().Contains("CONDITION") => "clinical_assertion",
            _ when rule.ToString().StartsWith("MEDICATION_") => "medication_attribute",
            _ when rule.ToString().StartsWith("INVESTIGATION_") => "investigation_attribute",
            _ => "structure"
        };
        Diagnostic = new(rule.ToString(), category, "document", null, field.ToString());
    }

    internal InvalidClinicalEvidenceException At(ClinicalValidationEntity entity, int? index)
    {
        Diagnostic = Diagnostic with { EntityType = entity.ToString(), EntityIndex = index };
        return this;
    }
}
