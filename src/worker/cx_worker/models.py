"""
Authoritative Pydantic models conforming strictly to contracts/extraction/llm-extraction-output.v1.schema.json
"""

from typing import Literal

from pydantic import BaseModel, Field

MedicationAssertion = Literal[
    "new_order",
    "current_medication",
    "historical",
    "discontinued",
    "negated",
    "suggested",
    "conditional",
    "uncertain",
]

InvestigationAssertion = Literal[
    "new_order", "historical", "negated", "suggested", "conditional", "uncertain"
]


class ExtractedMedication(BaseModel):
    name: str = Field(..., description="Drug name exactly as written")
    strength: str | None = Field(default=None, description="Verbatim strength e.g. 500 mg")
    dose: str | None = Field(default=None, description="Verbatim dose per administration")
    route: str | None = Field(default=None)
    frequency: str | None = Field(default=None)
    duration: str | None = Field(default=None)
    formulation: str | None = Field(default=None)
    instructions: str | None = Field(default=None)
    assertion_status: MedicationAssertion = Field(...)
    evidence_quote: str = Field(
        ..., description="Shortest exact contiguous substring supporting this item"
    )


class ExtractedInvestigation(BaseModel):
    name: str = Field(..., description="Test name exactly as written")
    specimen: str | None = Field(default=None)
    timing: str | None = Field(default=None)
    assertion_status: InvestigationAssertion = Field(...)
    evidence_quote: str = Field(...)


class LlmExtractionOutput(BaseModel):
    schema_version: Literal["1.0"] = Field(...)
    medications: list[ExtractedMedication] = Field(...)
    investigations: list[ExtractedInvestigation] = Field(...)
