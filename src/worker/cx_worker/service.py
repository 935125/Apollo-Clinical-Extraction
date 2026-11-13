"""
Internal Python AI Extraction Microservice.
Bound exclusively to loopback (127.0.0.1) for internal consumption by ASP.NET Core Web API.
Conforms to contracts/internal/internal-ai-extraction.v1.schema.json.
"""

import time
from typing import Any

from fastapi import FastAPI, HTTPException, status
from pydantic import BaseModel, Field

from cx_worker.pipeline.adapter import (
    IExtractionModelAdapter,
    create_model_adapter,
)
from cx_worker.pipeline.normalize import (
    ITerminologyNormalizer,
    ProductionTerminologyNormalizer,
)
from cx_worker.pipeline.preprocess import ClinicalPreprocessor
from cx_worker.pipeline.validate import ClinicalValidator, ValidationSummary

app = FastAPI(
    title="Internal AI Extraction Service",
    description="Internal NLP microservice providing synchronous 5-stage clinical extraction.",
    version="1.0.0",
)

# Active adapter (dynamically switches between SyntheticModelAdapter and FoundryModelAdapter via environment)
active_adapter: IExtractionModelAdapter = create_model_adapter()
active_normalizer: ITerminologyNormalizer = ProductionTerminologyNormalizer()


class InternalExtractionRequest(BaseModel):
    note_text: str = Field(
        ..., min_length=1, max_length=65536, description="Raw clinical note narrative"
    )
    correlation_id: str = Field(..., description="Distributed tracing correlation ID")
    model_deployment: str = Field(default="auto", description="Target model deployment")
    options: dict[str, Any] | None = Field(default=None, description="Optional pipeline flags")


class EvidenceDto(BaseModel):
    quote: str
    verified: bool
    start: int | None = Field(default=None, description="0-based UTF-16 code unit start offset")
    end: int | None = Field(default=None, description="0-based UTF-16 code unit end offset")


class FindingDto(BaseModel):
    code: str
    severity: str
    field: str | None = None
    message: str


class NormalizationDto(BaseModel):
    status: str
    matched_code: str | None = None
    display_name: str | None = None
    confidence: float = 0.0


class ExtractedMedicationDto(BaseModel):
    name: str
    strength: str | None = None
    dose: str | None = None
    route: str | None = None
    frequency: str | None = None
    duration: str | None = None
    formulation: str | None = None
    instructions: str | None = None
    assertion_status: str
    evidence: EvidenceDto
    quality_flags: list[str] = Field(default_factory=list)
    findings: list[FindingDto] = Field(default_factory=list)
    normalization: NormalizationDto


class ExtractedInvestigationDto(BaseModel):
    name: str
    timing: str | None = None
    specimen: str | None = None
    assertion_status: str
    evidence: EvidenceDto
    quality_flags: list[str] = Field(default_factory=list)
    findings: list[FindingDto] = Field(default_factory=list)
    normalization: NormalizationDto


class ValidationSummaryDto(BaseModel):
    has_errors: bool
    error_count: int
    warning_count: int


class SectionSpanDto(BaseModel):
    code: str
    heading: str
    start: int
    end: int


class PreprocessedDto(BaseModel):
    char_length: int
    sections: list[SectionSpanDto] = Field(default_factory=list)


class ModelMetadataDto(BaseModel):
    adapter_type: str
    model_deployment: str
    prompt_version: str


class InternalExtractionResponse(BaseModel):
    schema_version: str = "1.0"
    correlation_id: str
    model_metadata: ModelMetadataDto
    preprocessed: PreprocessedDto
    medications: list[ExtractedMedicationDto]
    investigations: list[ExtractedInvestigationDto]
    validation_summary: ValidationSummaryDto
    execution_duration_ms: float


@app.get("/internal/v1/health")
def health_check() -> dict[str, str]:
    return {
        "status": "healthy",
        "service": "internal-ai-extraction",
        "adapter": active_adapter.adapter_type,
        "model": active_adapter.model_name,
    }


@app.post("/internal/v1/extract", response_model=InternalExtractionResponse)
def extract_clinical_information(request: InternalExtractionRequest) -> InternalExtractionResponse:
    start_time = time.perf_counter()

    # Stage 1: Preprocessing & Context Normalization
    preprocessor = ClinicalPreprocessor()
    preprocessed = preprocessor.preprocess(request.note_text)
    if preprocessed.is_empty_or_malformed:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"MALFORMED_NOTE: {preprocessed.rejection_reason}",
        )

    # Stage 2: Model Inference via Abstracted Adapter
    try:
        raw_extraction = active_adapter.extract_structured(
            clinical_text=preprocessed.normalized_text, correlation_id=request.correlation_id
        )
    except TimeoutError as ex:
        raise HTTPException(
            status_code=status.HTTP_504_GATEWAY_TIMEOUT, detail=f"MODEL_TIMEOUT: {ex!s}"
        ) from ex
    except ValueError as ex:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY, detail=f"MALFORMED_MODEL_OUTPUT: {ex!s}"
        ) from ex
    except Exception as ex:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"INFERENCE_FAILURE: {ex!s}"
        ) from ex

    # Stage 3: Extraction Validation & Guardrails
    validator = ClinicalValidator()
    val_summary: ValidationSummary = validator.validate_extraction(
        extraction=raw_extraction, source_text=preprocessed.normalized_text
    )

    # Stage 4: Terminology Normalization Hook
    normalizer = active_normalizer

    # Build response DTOs
    med_dtos: list[ExtractedMedicationDto] = []
    for v_med in val_summary.medications:
        norm = normalizer.normalize_medication(
            v_med.item.name, v_med.item.strength, v_med.item.formulation, v_med.item.route
        )
        med_dtos.append(
            ExtractedMedicationDto(
                name=v_med.item.name,
                strength=v_med.item.strength,
                dose=v_med.item.dose,
                route=v_med.item.route,
                frequency=v_med.item.frequency,
                duration=v_med.item.duration,
                formulation=v_med.item.formulation,
                instructions=v_med.item.instructions,
                assertion_status=v_med.item.assertion_status,
                evidence=EvidenceDto(
                    quote=v_med.evidence.quote,
                    verified=v_med.evidence.verified,
                    start=v_med.evidence.start,
                    end=v_med.evidence.end,
                ),
                quality_flags=v_med.quality_flags,
                findings=[
                    FindingDto(code=f.code, severity=f.severity, field=f.field, message=f.message)
                    for f in v_med.findings
                ],
                normalization=NormalizationDto(
                    status=norm.status,
                    matched_code=norm.matched_code,
                    display_name=norm.matched_name,
                    confidence=norm.confidence,
                ),
            )
        )

    inv_dtos: list[ExtractedInvestigationDto] = []
    for v_inv in val_summary.investigations:
        norm_inv = normalizer.normalize_investigation(v_inv.item.name, v_inv.item.specimen)
        inv_dtos.append(
            ExtractedInvestigationDto(
                name=v_inv.item.name,
                timing=v_inv.item.timing,
                specimen=v_inv.item.specimen,
                assertion_status=v_inv.item.assertion_status,
                evidence=EvidenceDto(
                    quote=v_inv.evidence.quote,
                    verified=v_inv.evidence.verified,
                    start=v_inv.evidence.start,
                    end=v_inv.evidence.end,
                ),
                quality_flags=v_inv.quality_flags,
                findings=[
                    FindingDto(code=f.code, severity=f.severity, field=f.field, message=f.message)
                    for f in v_inv.findings
                ],
                normalization=NormalizationDto(
                    status=norm_inv.status,
                    matched_code=norm_inv.matched_code,
                    display_name=norm_inv.matched_name,
                    confidence=norm_inv.confidence,
                ),
            )
        )

    error_count = sum(
        sum(1 for f in m.findings if f.severity == "error") for m in val_summary.medications
    ) + sum(sum(1 for f in i.findings if f.severity == "error") for i in val_summary.investigations)

    warning_count = sum(
        sum(1 for f in m.findings if f.severity == "warning") for m in val_summary.medications
    ) + sum(
        sum(1 for f in i.findings if f.severity == "warning") for i in val_summary.investigations
    )

    duration_ms = round((time.perf_counter() - start_time) * 1000.0, 2)

    return InternalExtractionResponse(
        schema_version="1.0",
        correlation_id=request.correlation_id,
        model_metadata=ModelMetadataDto(
            adapter_type=active_adapter.adapter_type,
            model_deployment=active_adapter.model_name,
            prompt_version=active_adapter.prompt_version,
        ),
        preprocessed=PreprocessedDto(
            char_length=preprocessed.char_length,
            sections=[
                SectionSpanDto(code=s.code, heading=s.heading, start=s.start, end=s.end)
                for s in preprocessed.sections
            ],
        ),
        medications=med_dtos,
        investigations=inv_dtos,
        validation_summary=ValidationSummaryDto(
            has_errors=val_summary.has_errors, error_count=error_count, warning_count=warning_count
        ),
        execution_duration_ms=duration_ms,
    )
