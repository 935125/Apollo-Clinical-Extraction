# ADR-0011: Standalone Clinical Extraction Architecture — ASP.NET Core Public Gateway & Internal Python AI Service

## Status
Accepted (Mandatory Scope Realignment)

## Context
The project scope has transitioned from a hospital-wide EMR write-back integration platform (with asynchronous transactional outbox publishing, message queuing, and simulated clinician order approvals) to a high-performance standalone clinical information extraction application.

The core technology stack requirements mandate:
1. **ASP.NET Core Web API (.NET 10)** must exclusively own all public-facing REST endpoints.
2. **Angular 21 + TypeScript** must communicate exclusively with the ASP.NET Core backend.
3. **Python 3.13** must be isolated as an internal-only AI/NLP microservice.
4. An explicit, versioned contract must govern .NET $\leftrightarrow$ Python communication.
5. No unnecessary message brokers or background queue workers for interactive extraction workflows.
6. Real Microsoft Foundry integration must be preceded by a verified working vertical slice with a mock model adapter.

## Decision

1. **Topological Role Separation:**
   - **ASP.NET Core:** Acts as the Public API Gateway and Orchestration Host. Validates incoming clinical note payloads (length limits, non-empty, encoding), assigns and propagates `X-Correlation-ID`, manages cancellation tokens and timeouts (30-second ceiling), and routes extraction calls to the internal Python AI service over HTTP loopback.
   - **Internal Python AI Service:** Runs as a loopback microservice on `http://127.0.0.1:5005` (unexposed to browser/external network). Implements the 5-stage clinical AI pipeline: Stage 1 (Unicode NFC preprocessing and XML escaping), Stage 2 (LLM inference via `IExtractionModelAdapter`), Stage 3 (Pydantic schema validation, verbatim evidence verification, and ISMP safety rule enforcement), and Stage 4 (terminology normalization).
   - **Angular Frontend:** Client interface communicating exclusively with ASP.NET Core (`POST /api/v1/extractions`). Renders interactive extraction workspace, loads realistic synthetic notes, and visualizes structured entity cards with quality flags and confidence scores.

2. **Synchronous Contract & Fault Isolation:**
   - Communication between .NET and Python is governed by `contracts/internal/internal-ai-extraction.v1.schema.json`.
   - Timeouts, request cancellations (`CancellationToken`), and correlation IDs are propagated across both layers.
   - If the internal Python service or underlying AI model adapter experiences a timeout or malformed output, the error is classified into standard RFC 7807 Problem Details and returned to Angular without application crash.

3. **Preservation of Prior Milestones:**
   - Completed EMR simulation and transactional outbox modules (`Cx.EmrSimulator`, `Cx.OutboxPublisher`) are preserved in Git and the repository structure for future integration reference, but decoupled from the active standalone application runtime.

## Consequences

- **Positive:**
  - Dramatically reduced architectural complexity: interactive extractions execute in milliseconds without multi-queue polling or outbox lag.
  - Strict compliance with ASP.NET Core public API ownership.
  - Zero leakage of internal AI engine details to public clients.
  - Reproducible, automated end-to-end integration testing from Angular through .NET to Python.
- **Negative / Trade-offs:**
  - Running two processes locally (ASP.NET Core Web API + internal Python service); addressed by standard development orchestration scripts.

