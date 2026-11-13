"""
Stage 3: Clinical & Schema Validator.
Validates Pydantic types, verifies verbatim evidence spans against the source clinical note,
computes 0-based character offsets, and performs clinical rule consistency checks.
"""

from dataclasses import dataclass, field

from cx_worker.models import ExtractedInvestigation, ExtractedMedication, LlmExtractionOutput


@dataclass
class EvidenceMatch:
    quote: str
    verified: bool
    start: int | None
    end: int | None


@dataclass
class Finding:
    code: str
    severity: str  # 'info', 'warning', 'error'
    field: str | None
    message: str


@dataclass
class ValidatedMedication:
    item: ExtractedMedication
    evidence: EvidenceMatch
    findings: list[Finding] = field(default_factory=list)
    quality_flags: list[str] = field(default_factory=list)
    writeback_eligible: bool = False


@dataclass
class ValidatedInvestigation:
    item: ExtractedInvestigation
    evidence: EvidenceMatch
    findings: list[Finding] = field(default_factory=list)
    quality_flags: list[str] = field(default_factory=list)
    writeback_eligible: bool = False


@dataclass
class ValidationSummary:
    medications: list[ValidatedMedication] = field(default_factory=list)
    investigations: list[ValidatedInvestigation] = field(default_factory=list)
    has_errors: bool = False


HIGH_RISK_ABBREVIATIONS = {
    "OD": "once daily or oculus dexter (right eye)",
    "OS": "oculus sinister (left eye)",
    "OU": "oculus uterque (both eyes)",
    "QOD": "every other day (often mistaken for QD)",
    "U": "unit (often mistaken for 0 or 4)",
    "IU": "international unit (often mistaken for IV)",
}


class ClinicalValidator:
    def validate_extraction(
        self, extraction: LlmExtractionOutput, source_text: str
    ) -> ValidationSummary:
        summary = ValidationSummary()

        seen_med_names = set()
        # 1. Validate medications
        for med in extraction.medications:
            findings: list[Finding] = []
            flags: list[str] = []

            # Duplicate entity check
            med_key = (med.name or "").strip().lower()
            if med_key in seen_med_names:
                findings.append(
                    Finding(
                        code="DUPLICATE_ENTITY_MENTION",
                        severity="warning",
                        field="name",
                        message=f"Duplicate mention of medication '{med.name}' detected in extraction output.",
                    )
                )
                flags.append("DUPLICATE_ENTITY_MENTION")
            else:
                seen_med_names.add(med_key)

            # Evidence span verification
            evidence_match = self._verify_evidence(med.evidence_quote, source_text)
            if not evidence_match.verified:
                findings.append(
                    Finding(
                        code="EVIDENCE_NOT_FOUND",
                        severity="error",
                        field="evidence_quote",
                        message="The extracted evidence quote could not be located in the original clinical note.",
                    )
                )
                flags.append("EVIDENCE_NOT_FOUND")

            # Ambiguous abbreviation detection (ISMP safety guidelines)
            freq_upper = (med.frequency or "").strip().upper()
            if freq_upper in HIGH_RISK_ABBREVIATIONS:
                findings.append(
                    Finding(
                        code="AMBIGUOUS_ABBREVIATION",
                        severity="warning",
                        field="frequency",
                        message=f"High-risk abbreviation '{freq_upper}' ({HIGH_RISK_ABBREVIATIONS[freq_upper]}) requires clinician confirmation.",
                    )
                )
                flags.append("AMBIGUOUS_ABBREVIATION")

            # Missing mandatory order attributes
            if med.assertion_status == "new_order":
                if not med.dose and not med.strength:
                    findings.append(
                        Finding(
                            code="MANDATORY_ORDER_ATTRIBUTE_MISSING",
                            severity="warning",
                            field="dose",
                            message="New medication order is missing both explicit dose and strength.",
                        )
                    )
                    flags.append("MANDATORY_ORDER_ATTRIBUTE_MISSING")

                if not med.frequency:
                    findings.append(
                        Finding(
                            code="FREQUENCY_MISSING",
                            severity="warning",
                            field="frequency",
                            message="New medication order lacks administration frequency.",
                        )
                    )
                    flags.append("FREQUENCY_UNRECOGNISED")

                if not med.duration:
                    findings.append(
                        Finding(
                            code="DURATION_MISSING",
                            severity="info",
                            field="duration",
                            message="New medication order lacks explicit duration.",
                        )
                    )
                    flags.append("DURATION_MISSING")

                # Contradictory instruction check: new_order with negation or discontinuation words
                lower_quote = (med.evidence_quote or "").lower()
                conflict_cues = ["stop ", "cease", "discontinue", "do not ", "avoid ", "hold "]
                if any(cue in lower_quote for cue in conflict_cues):
                    findings.append(
                        Finding(
                            code="CONTRADICTORY_INSTRUCTION",
                            severity="warning",
                            field="assertion_status",
                            message="New order marked but evidence contains contradictory cessation/hold instructions.",
                        )
                    )
                    flags.append("CONTRADICTORY_INSTRUCTION")

            # Check negation consistency
            if med.assertion_status == "negated":
                # Check for negation cue in evidence
                lower_quote = (med.evidence_quote or "").lower()
                negation_cues = [
                    "no ",
                    "not ",
                    "denies",
                    "never",
                    "avoid",
                    "stop",
                    "ceased",
                    "allergy",
                ]
                if not any(cue in lower_quote for cue in negation_cues):
                    findings.append(
                        Finding(
                            code="ASSERTION_EVIDENCE_CONFLICT",
                            severity="warning",
                            field="assertion_status",
                            message="Medication is marked negated but evidence quote lacks obvious negation cues.",
                        )
                    )
                    flags.append("ASSERTION_EVIDENCE_CONFLICT")

            # Clinical safety writeback rule: Only confirmed new_order with verified evidence can ever be eligible
            is_eligible = (
                med.assertion_status == "new_order"
                and evidence_match.verified
                and not any(f.severity == "error" for f in findings)
            )

            summary.medications.append(
                ValidatedMedication(
                    item=med,
                    evidence=evidence_match,
                    findings=findings,
                    quality_flags=flags,
                    writeback_eligible=is_eligible,
                )
            )

        # 2. Validate investigations
        for inv in extraction.investigations:
            findings = []
            flags = []

            evidence_match = self._verify_evidence(inv.evidence_quote, source_text)
            if not evidence_match.verified:
                findings.append(
                    Finding(
                        code="EVIDENCE_NOT_FOUND",
                        severity="error",
                        field="evidence_quote",
                        message="Investigation evidence quote not found in source text.",
                    )
                )
                flags.append("EVIDENCE_NOT_FOUND")

            is_eligible = (
                inv.assertion_status == "new_order"
                and evidence_match.verified
                and not any(f.severity == "error" for f in findings)
            )

            summary.investigations.append(
                ValidatedInvestigation(
                    item=inv,
                    evidence=evidence_match,
                    findings=findings,
                    quality_flags=flags,
                    writeback_eligible=is_eligible,
                )
            )

        summary.has_errors = any(
            any(f.severity == "error" for f in m.findings) for m in summary.medications
        ) or any(any(f.severity == "error" for f in i.findings) for i in summary.investigations)

        return summary

    def _verify_evidence(self, quote: str | None, text: str) -> EvidenceMatch:
        if not quote or not quote.strip():
            return EvidenceMatch(
                quote="" if quote is None else quote, verified=False, start=None, end=None
            )

        clean_quote = quote.strip()
        idx = text.find(clean_quote)
        if idx != -1:
            return EvidenceMatch(
                quote=clean_quote, verified=True, start=idx, end=idx + len(clean_quote)
            )

        return EvidenceMatch(quote=clean_quote, verified=False, start=None, end=None)
