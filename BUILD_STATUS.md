# BUILD & VERIFICATION STATUS

Last Updated: 2026-11-13 (Architecture Correction & Vertical Slice Verified)

---

## 1. System Prerequisites & Environment Status

| Component | Status | Details |
|---|---|---|
| **OS** | Windows 11 Pro 10.0.26100 | Supported |
| **Topology** | **Standalone Application** | Angular 21 $\rightarrow$ ASP.NET Core Web API (.NET 10) $\rightarrow$ Internal Python AI Service (FastAPI) $\rightarrow$ Model Adapter |
| **Public API Gateway** | **ASP.NET Core Web API (.NET 10)** | Exclusively owns `/api/v1/extractions`, input validation, timeouts, CORS, trace correlation |
| **Internal AI Service** | **Python 3.13 via uv** | Loopback microservice on `http://127.0.0.1:5005/internal/v1/extract`, strict typing, 5-stage pipeline |
| **PostgreSQL 18** | **RUNNING & VERIFIED** | Portable PostgreSQL 18.7 running background daemons on `localhost:5432` (`cx_platform`) and `localhost:5433` (`emr_sim`). |
| **Node.js** | `v24.12.0` | Angular 21.2 LTS (`21.2.30`) with Angular Material 21 |

---

## 2. Test Execution Summary

### Total Automated Tests Executed: **66 Passed, 0 Failed** across 3 Stacks

---

### Backend (.NET 10 LTS) — **31 Passed, 0 Failed**
- **Cx.PlatformApi.Tests**: **10 Passed, 0 Failed**
  - `VerticalSlice_RealLiveRoundTrip_DotNetToPython_ExtractsClinicalEntities`: **Live end-to-end integration test** verifying full HTTP round-trip from ASP.NET Core to running Python AI microservice on `127.0.0.1:5005`, extracting medications (`Metformin`, `Atorvastatin`), investigations (`Serum Creatinine`, `HbA1c`), verbatim quotes, and offset indices.
  - `Extract_EmptyNoteText_Returns400BadRequest`: Verified request validation rejects empty text with `EMPTY_NOTE_TEXT`.
  - `Extract_NoteExceeds64KiB_Returns400BadRequest`: Verified request validation enforces 64 KiB ceiling with `NOTE_EXCEEDS_64KIB_LIMIT`.
  - `Extract_PropagatesCorrelationIdHeader`: Verified header `X-Correlation-ID` round-trip propagation.
  - `Extract_SuccessfulExtraction_ReturnsStructuredResults`: Verified mapping of medications, investigations, and safety flags.
  - `Extract_InternalAiTimeout_Returns504GatewayTimeout`: Verified 504 ProblemDetails with `AI_MODEL_TIMEOUT`.
  - `Extract_InternalAiServiceUnavailable_Returns503`: Verified 503 ProblemDetails with `AI_SERVICE_UNAVAILABLE`.
  - `Extract_InternalAiMalformedOutput_Returns502BadGateway`: Verified 502 ProblemDetails with `MALFORMED_MODEL_OUTPUT`.
  - `HealthLive_ReturnsOkWithHealthyStatus`: Liveness endpoint verification.
  - `HealthReady_ReturnsOkWithReadyStatus`: Readiness endpoint verification.
- **Cx.Domain.Tests**: **5 Passed, 0 Failed**
  - Enum parity with `contracts/enums.v1.json`.
  - Clinical safety write-back rules.
- **Cx.EmrSimulator.Tests**: **11 Passed, 0 Failed** (Preserved in repo for interoperability reference)
  - Fault injection rollback, optimistic concurrency, and Care Team access checks.
- **Cx.OutboxPublisher.Tests**: **5 Passed, 0 Failed** (Preserved in repo)
  - Crash recovery, multi-publisher `SKIP LOCKED` claiming, and backoff scheduling.

---

### AI Worker & Internal Service (Python 3.13 via uv) — **30 Passed, 0 Failed**
- **tests/test_service.py**: **5 Passed, 0 Failed** (Internal AI Microservice Suite)
  - `test_internal_service_health`: Verified `/internal/v1/health` endpoint.
  - `test_internal_service_successful_extraction`: Verified `/internal/v1/extract` 5-stage synchronous pipeline.
  - `test_internal_service_model_timeout_maps_to_504`: Verified model timeout error mapping.
  - `test_internal_service_malformed_output_maps_to_502`: Verified malformed model output mapping.
  - `test_internal_service_empty_note_rejected_with_422_or_400`: Verified Pydantic request schema validation.
- **tests/test_clinical_extraction.py**: **18 Passed, 0 Failed** (Mandatory 17-Case Clinical Test Matrix)
  - All 17 clinical edge cases (new orders, historical, negated, discontinued, conditional, multiple doses, ambiguous abbreviations, missing frequency/duration, contradictory instructions, repeated entities, timeouts, malformed JSON, out-of-order versions, duplicate events, fencing tokens, prompt injection, and Unicode edge cases).
- **tests/test_consumer.py**: **5 Passed, 0 Failed**
  - Monotonic versioning, dedup keys, poison messages, and lease expiration fencing.
- **tests/test_models.py**: **2 Passed, 0 Failed**
  - Schema parity with `llm-extraction-output.v1.schema.json`.
- **Static Analysis**:
  - `ruff check`: All checks passed.
  - `mypy cx_worker`: Success: no issues found in 10 source files.

---

### Frontend (Angular 21 LTS with Vitest) — **5 Passed, 0 Failed**
- Unit test suite (`app.spec.ts`): **5 Passed in 213ms**.
  - Component creation, header rendering, synthetic preset loading, extraction service invocation, entity card rendering, and RFC 7807 error alert handling.
- Production build: **Bundle generation succeeded** (283.43 kB raw / 75.08 kB transfer).

---

## 3. End-to-End Architecture Topology Status

```
Angular 21 Client (localhost:4200)
    │
    │ HTTP POST /api/v1/extractions
    ▼
ASP.NET Core Web API Gateway (.NET 10, localhost:5000)
    │ [Owns public endpoints, input validation, timeouts, X-Correlation-ID]
    │ HTTP POST http://127.0.0.1:5005/internal/v1/extract
    ▼
Internal Python AI Microservice (FastAPI/uvicorn, localhost:5005)
    │ [Stage 1: Preprocessor & XML Escaping]
    │ [Stage 2: Model Inference via Abstracted Adapter]
    │ [Stage 3: Pydantic Validation & Verbatim Offset Check]
    │ [Stage 4: Terminology Normalization Hook]
    │
    │ HTTP 200 OK (Versioned Internal Contract)
    ▼
ASP.NET Core Web API Gateway
    │
    │ HTTP 200 OK (ClinicalExtractionResponse)
    ▼
Angular Results Interface (Interactive Entity Inspector Cards)
```
