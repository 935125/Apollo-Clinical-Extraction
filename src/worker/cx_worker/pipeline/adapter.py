"""
Stage 2: Model Adapter Interface & Implementations.
Physical separation of LLM invocation: SyntheticModelAdapter for deterministic test execution,
and FoundryModelAdapter for real Microsoft Foundry / Azure OpenAI deployments.
"""

import re
from abc import ABC, abstractmethod
from typing import ClassVar

from cx_worker.models import (
    ExtractedInvestigation,
    ExtractedMedication,
    InvestigationAssertion,
    LlmExtractionOutput,
    MedicationAssertion,
)


class IExtractionModelAdapter(ABC):
    @abstractmethod
    def extract_structured(self, clinical_text: str, correlation_id: str) -> LlmExtractionOutput:
        """
        Extract structured medications and investigations conforming strictly to LlmExtractionOutput.
        """


class SyntheticModelAdapter(IExtractionModelAdapter):
    """
    Deterministic model adapter for automated unit, integration, and reliability test suites.
    Returns tailored synthetic outputs or simulates timeouts/malformed responses.
    """

    KNOWN_DRUGS: ClassVar[list[str]] = [
        "Metformin",
        "Atorvastatin",
        "Aspirin",
        "Warfarin",
        "Amoxicillin",
        "Paracetamol",
        "Azithromycin",
        "Lisinopril",
        "Levothyroxine",
        "Omeprazole",
        "Fentanyl",
    ]

    KNOWN_TESTS: ClassVar[list[str]] = [
        "Serum Creatinine",
        "HbA1c",
        "CBC",
        "Complete Blood Count",
        "Lipid Profile",
        "ECG",
        "Chest X-ray",
        "2D Echocardiogram",
        "Urinalysis",
        "LFT",
        "Liver Function Test",
    ]

    def __init__(
        self,
        response_override: LlmExtractionOutput | None = None,
        should_timeout: bool = False,
        malformed_json: bool = False,
    ):
        self.response_override = response_override
        self.should_timeout = should_timeout
        self.malformed_json = malformed_json

    def extract_structured(self, clinical_text: str, correlation_id: str) -> LlmExtractionOutput:
        if self.should_timeout:
            raise TimeoutError("SIMULATED_FOUNDRY_ENDPOINT_TIMEOUT: Request timed out after 30s")

        if self.malformed_json:
            raise ValueError("SIMULATED_MALFORMED_OUTPUT: Model response failed schema parsing")

        if self.response_override is not None:
            return self.response_override

        # Deterministic extraction logic for interactive synthetic clinical notes
        medications: list[ExtractedMedication] = []
        investigations: list[ExtractedInvestigation] = []

        # 1. Match known drugs
        for drug in self.KNOWN_DRUGS:
            pattern = rf"(?i)(?:(no|avoid|stop|discontinue|took|history of)\s+)?([^\n.,;]*?\b{drug}\b[^\n.,;]*)"
            for match in re.finditer(pattern, clinical_text):
                sentence_fragment = match.group(0).strip()
                drug_text = match.group(2).strip()

                # Determine assertion
                lower_full = match.group(0).lower()
                assertion: MedicationAssertion = "new_order"
                if any(k in lower_full for k in ["no ", "not ", "allergy", "avoid"]):
                    assertion = "negated"
                elif any(k in lower_full for k in ["took", "past", "ceased", "history of"]):
                    assertion = "historical"
                elif any(k in lower_full for k in ["stop", "discontinue", "cease"]):
                    assertion = "discontinued"

                # Extract dosage if present (e.g. 500mg, 100mg, 20mg)
                dose_match = re.search(
                    r"\b(\d+\s*(?:mg|mcg|g|tab|tabs|caps?))\b", drug_text, re.IGNORECASE
                )
                strength = dose_match.group(1) if dose_match else None

                # Extract frequency if present (e.g. OD, BD, TDS, daily, SOS)
                freq_match = re.search(
                    r"\b(OD|BD|TDS|QID|daily|SOS|QOD)\b", drug_text, re.IGNORECASE
                )
                frequency = freq_match.group(1).upper() if freq_match else None

                # Extract route if present (PO, IV, SC)
                route_match = re.search(r"\b(PO|IV|SC|oral|topical)\b", drug_text, re.IGNORECASE)
                route = route_match.group(1).upper() if route_match else None

                medications.append(
                    ExtractedMedication(
                        name=drug,
                        strength=strength,
                        dose=None,
                        route=route,
                        frequency=frequency,
                        duration=None,
                        formulation="tablet" if "tab" in drug_text.lower() else None,
                        instructions=None,
                        assertion_status=assertion,
                        evidence_quote=sentence_fragment,
                    )
                )

        # 2. Match known investigations
        for test_name in self.KNOWN_TESTS:
            if test_name.lower() in clinical_text.lower():
                pattern = (
                    rf"(?i)(?:(if|in case of)\s+)?([^\n.,;]*?\b{re.escape(test_name)}\b[^\n.,;]*)"
                )
                m = re.search(pattern, clinical_text)
                if m:
                    quote = m.group(0).strip()
                    lower_q = quote.lower()
                    inv_assertion: InvestigationAssertion = (
                        "conditional"
                        if any(c in lower_q for c in ["if ", "in case", "should "])
                        else "new_order"
                    )
                    investigations.append(
                        ExtractedInvestigation(
                            name=test_name,
                            specimen="Blood"
                            if any(
                                b in test_name
                                for b in ["Serum", "Blood", "CBC", "HbA1c", "Lipid", "LFT"]
                            )
                            else None,
                            timing=None,
                            assertion_status=inv_assertion,
                            evidence_quote=quote,
                        )
                    )

        return LlmExtractionOutput(
            schema_version="1.0", medications=medications, investigations=investigations
        )
