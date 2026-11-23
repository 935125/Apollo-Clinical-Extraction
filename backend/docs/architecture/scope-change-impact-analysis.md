# Scope-Change Impact Analysis: Standalone Clinical AI Extraction Architecture

**Date:** 2026-11-13  
**Status:** Approved Architecture Transition  
**Context:** Mandated transition from Hospital EMR Write-Back Integration Platform to a Standalone Clinical AI Extraction Application.

---

## 1. Executive Summary of Changes

| Dimension | Previous Architecture (Hospital EMR Platform) | New Architecture (Standalone Clinical Application) |
|---|---|---|
| **Primary Goal** | High-volume hospital-wide EMR outbox publisher & doctor writeback pipeline | Interactive, low-latency clinical information extraction application |
| **Public API Gateway** | ASP.NET Core Web API (Care-team checks, EMR integration endpoints, batch queues) | **ASP.NET Core Web API (.NET 10)** exclusively owns all public-facing REST endpoints |
| **Frontend Communication** | Angular $\leftrightarrow$ Platform API + simulated EMR orders write-back | **Angular 21 communicates exclusively with ASP.NET Core Web API** |
| **Python Service Role** | Asynchronous Azure Service Bus message queue consumer / batch worker | **Internal synchronous AI/NLP microservice** (`POST /internal/v1/extract`) on loopback |
| **Interactive Latency** | Asynchronous outbox $\rightarrow$ broker $\rightarrow$ worker polling ($\sim 5\text{--}30\text{ s}$) | **Synchronous request-response pipeline** ($< 2.5\text{ s}$ target) |
| **Message Broker / Outbox** | Transactional outbox table (`emr.extraction_outbox`) + broker leasing | **Removed from active interactive path**; direct HTTP microservice orchestration |
| **EMR Integration & Writeback**| Simulated EMR notes, care team check, doctor approval writeback | **Retired from active scope**; historical code and tests preserved in Git |
| **Database Requirement** | Multi-schema PostgreSQL (EMR Simulator + CX Platform dual instances) | Lightweight PostgreSQL retained **only** for extraction history persistence |
| **AI Extraction Core** | Python 5-stage pipeline (Preprocess, Inference, Validate, Normalize, Persist) | **Retained**: Python 5-stage pipeline executes synchronously per request |

---

## 2. Component Disposition Matrix

| Component / Subsystem | Former Status | New Status | Impact & Action Plan |
|---|---|---|---|
| **Angular Review UI** (`src/cx-review-ui`) | Review & confirmation dashboard | **Main Application UI** | Refactor to interactive clinical extraction workspace: note input, synthetic sample loading, real-time extraction execution, rich entity inspector cards. |
| **ASP.NET Core API** (`src/backend/src/Cx.PlatformApi`) | Ops & Review API | **Authoritative Public API Gateway** | Owns `/api/v1/extractions`, input note validation, timeout management, correlation ID propagation, orchestrating internal Python service. |
| **Internal Python Service** (`src/worker`) | Standalone queue worker | **Internal AI Extraction Service** | Expose FastAPI internal daemon (`http://127.0.0.1:5005/internal/v1/extract`); execute Stages 1–4 synchronously; return validated structured output. |
| **Clinical Pipeline** (`cx_worker.pipeline`) | Worker pipeline | **Core AI Engine** | Unchanged; preprocess, model adapter, Pydantic & clinical validator, terminology normalizer remain core components. |
| **EMR Simulator** (`src/backend/src/Cx.EmrSimulator`) | Active milestone module | **Archived / Preserved** | Retained in repository for future interoperability reference; omitted from active standalone deployment and test runtime. |
| **Outbox Publisher** (`src/backend/src/Cx.OutboxPublisher`) | Active publisher daemon | **Archived / Preserved** | Retained in repository; interactive pipeline bypasses outbox polling for direct HTTP orchestration. |
| **PostgreSQL Clusters** | Dual cluster requirement | **Single Database Optional** | `cx_platform` used for historical extraction logging; EMR database no longer required in active runtime. |

---

## 3. End-to-End Interactive Extraction Flow

```
+-------------------------------------------------------------+
|                      Angular 21 Client                      |
|  - Clinical note narrative entry (or synthetic sample)       |
|  - Correlation ID generation (or server assignment)         |
|  - Interactive "Extract Clinical Entities" trigger           |
+------------------------------+------------------------------+
                               |
                               | HTTP POST /api/v1/extractions
                               | (Payload: noteText, options)
                               v
+-------------------------------------------------------------+
|           ASP.NET Core Web API Gateway (.NET 10)            |
|  - Owns all public REST endpoints                           |
|  - Request validation (non-empty, <= 64 KiB ceiling)        |
|  - Assigns / propagates X-Correlation-ID                    |
|  - Configures 30s CancellationToken & circuit breaker       |
|  - Dispatches to Internal Python Service                    |
+------------------------------+------------------------------+
                               |
                               | HTTP POST http://127.0.0.1:5005/internal/v1/extract
                               | (Versioned contract: InternalExtractionRequest)
                               v
+-------------------------------------------------------------+
|         Internal Python AI Microservice (FastAPI/uvicorn)   |
|  - Stage 1: Preprocessing, Unicode NFC, sanitize control    |
|             bytes, XML delimiter wrapping                   |
|  - Stage 2: Model Inference (IExtractionModelAdapter:       |
|             SyntheticModelAdapter / FoundryModelAdapter)    |
|  - Stage 3: Pydantic parsing & ClinicalValidator (verbatim   |
|             evidence quote match, offset indexing,          |
|             ISMP ambiguous abbreviation check, quality flags)|
|  - Stage 4: Terminology Normalizer hook                     |
+------------------------------+------------------------------+
                               |
                               | HTTP 200 OK
                               | (Versioned contract: InternalExtractionResponse)
                               v
+-------------------------------------------------------------+
|           ASP.NET Core Web API Gateway (.NET 10)            |
|  - Validates internal response schema                       |
|  - Maps to Public ApiExtractionResult                       |
|  - (Optional) Asynchronously logs extraction run to history |
+------------------------------+------------------------------+
                               |
                               | HTTP 200 OK (ApiExtractionResult)
                               v
+-------------------------------------------------------------+
|                      Angular 21 Client                      |
|  - Displays structured Medications & Investigations cards   |
|  - Highlights verbatim evidence spans in note narrative     |
|  - Surfaces quality flags, warnings, and confidence scores  |
+-------------------------------------------------------------+
```

---

## 4. Contract Boundaries & Separation of Concerns

1. **Input Validation Ownership:**
   - **ASP.NET Core** owns public request validation: note text presence, character length limit ($\le 65,536$ characters), encoding checks, and rate limiting.
2. **AI & Response Validation Ownership:**
   - **Python Service** owns AI model response validation: Pydantic schema validation, LLM response parsing, clinical safety rules (verbatim substring search, 0-based character offsets, assertion status consistency, and ISMP safety flags).
3. **Resilience & Fault Tolerance:**
   - Both services propagate `X-Correlation-ID`.
   - ASP.NET Core configures HTTP client timeout (default 30 seconds) and cancellation propagation (`CancellationToken`).
   - If Python service is unreachable, ASP.NET Core returns HTTP 503 `SERVICE_UNAVAILABLE` with structured problem details.
   - If the AI model times out or returns malformed output, the Python service captures it and returns a structured error response (`MODEL_TIMEOUT` or `MALFORMED_MODEL_OUTPUT`), which ASP.NET Core presents cleanly to Angular without crashing.

