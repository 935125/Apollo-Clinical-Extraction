# A. Architecture Specification

Status: REVISED v1.0 (Standalone Clinical Extraction Application). Related: [scope change analysis](scope-change-impact-analysis.md), [ADR-0011](../adr/adr-0011-standalone-architecture-aspnetcore-public-api-python-internal.md), [internal contract](../../contracts/internal/internal-ai-extraction.v1.schema.json).

## 1. System Context & Top-Level Architecture

The platform operates as a standalone clinical information extraction application:

```mermaid
flowchart LR
  Clinician["Clinician / Medical Officer"] --> UI["Angular 21 Review & Extraction UI"]
  UI -- "HTTP POST /api/v1/extractions (Exclusive Public Client)" --> CoreAPI["ASP.NET Core Web API Gateway (.NET 10)"]
  CoreAPI -- "HTTP POST /internal/v1/extract (Loopback, Versioned Contract)" --> PyAI["Internal Python AI Extraction Microservice"]
  PyAI -- "Structured Extraction (Strict JSON)" --> Model["Microsoft Foundry LLM / Adapter"]
  PyAI -- "Clinical Safety & Guardrails" --> PyAI
  CoreAPI -. "Optional History Logging" .-> CXDB[("PostgreSQL History Store")]
```

### Architectural Principles & Boundaries
1. **Public API Ownership:** ASP.NET Core Web API (.NET 10) exclusively owns all public-facing REST endpoints. Public clients (including Angular) communicate solely with ASP.NET Core.
2. **Internal AI Isolation:** Python is isolated as an internal-only AI/NLP microservice listening on loopback (`http://127.0.0.1:5005`). It is never directly exposed to the browser or external network.
3. **Explicit Versioned Contract:** Communication between .NET and Python is governed by `contracts/internal/internal-ai-extraction.v1.schema.json`.
4. **Validation Ownership:** Input note validation (size limits, non-empty text, encoding) is performed by ASP.NET Core; LLM output validation (Pydantic schema, verbatim quote offsets, negation consistency, ISMP safety flags) is performed by the Python service.
5. **Direct Synchronous Pipeline:** Interactive extractions execute synchronously without message queue brokers or outbox lag.
6. **Preserved Interoperability Artifacts:** EMR simulation, care-team access control, outbox leasing, and order writeback components are preserved in the repository for future hospital integration reference, but decoupled from the active standalone application runtime.

## 2. Components and Interaction Topology

```mermaid
flowchart TB
  subgraph ClientZone["Client Zone"]
    UI["Angular 21 Client Workspace (Interactive Extraction UI)"]
  end

  subgraph GatewayZone["Public API Gateway Zone"]
    CoreAPI["ASP.NET Core Web API (.NET 10)
    - Request Validation (<= 64 KiB ceiling)
    - X-Correlation-ID Assignment & Propagation
    - Cancellation & Timeout Management (30s)
    - Orchestration Client"]
    HistoryDB[("PostgreSQL History DB (Optional)")]
    CoreAPI -.-> HistoryDB
  end

  subgraph InternalAIZone["Internal AI Service Zone (Loopback 127.0.0.1:5005)"]
    PyServer["FastAPI Internal Server (/internal/v1/extract)"]
    subgraph Pipeline["5-Stage Clinical Pipeline"]
      P1["Stage 1: Preprocessor (Unicode NFC, Sanitize, XML Escaping)"]
      P2["Stage 2: Model Adapter (Synthetic / Foundry Claude & GPT)"]
      P3["Stage 3: ClinicalValidator & Pydantic Guardrails"]
      P4["Stage 4: Terminology Normalization Hook"]
    end
    PyServer --> P1 --> P2 --> P3 --> P4 --> PyServer
  end

  UI -- "POST /api/v1/extractions" --> CoreAPI
  CoreAPI -- "POST /internal/v1/extract" --> PyServer
  W --> Foundry["Foundry model"]
  BW --> Foundry
  UI --> API
  API --> CXDB
  API --> EMRAccess
  WB --> CXDB
  WB --> EMROrders
  SWP --> CXDB
```

| Component | Tech | Owns | Scales on |
|---|---|---|---|
| emr-simulator (dev/test only) | ASP.NET Core | Simulated notes, versions, outbox, orders, access policy | n/a |
| Outbox publisher | ASP.NET Core hosted service (in real EMR: EMR team) | Outbox → bus | Outbox lag |
| AI extraction worker | Python 3.13, Pydantic, openai SDK, azure-servicebus | Stages 1–5, extraction tables | Queue length + LLM TPM ceiling |
| Backfill worker | Same image, `CX_TRIGGER=backfill` | Same | Separate quota/budget |
| Backfill orchestrator | Python job | `backfill_job`, checkpoints | Single active instance per job (advisory lock) |
| Review & Ops API | ASP.NET Core 10 | Review, decisions, write-back intent, ops endpoints | HTTP RPS |
| Write-back dispatcher | ASP.NET Core 10 worker | `writeback_operation` state machine | Pending operations |
| Coverage sweeper | Scheduled job (Container Apps Job) | Alerts on stuck notes | Cron |
| Review UI | Angular 22 (or 21 LTS) + Material | — | Static (CDN) |
| DB migrator | .NET console + DbUp + SQL files | Schema | One-shot job |

Domain logic ownership (no duplication): extraction rules live only in Python; review/write-back eligibility rules live only in .NET (`Cx.Domain`); shared enums are generated/tested from `contracts/enums.v1.json`.

## 3. Realtime sequence (happy path + key failure points)

```mermaid
sequenceDiagram
  autonumber
  participant D as Doctor
  participant E as EMR API
  participant EDB as EMR DB
  participant P as Outbox Publisher
  participant SB as Service Bus
  participant W as Worker
  participant R as EMR Read API
  participant F as Foundry
  participant DB as CX PostgreSQL
  participant API as Review API
  participant WB as Write-back
  D->>E: Save/amend note
  E->>EDB: BEGIN; insert note_version; insert extraction_outbox; COMMIT
  E-->>D: 200 Saved (no AI dependency)
  P->>EDB: SELECT ... FOR UPDATE SKIP LOCKED (batch)
  P->>SB: Send(MessageId=event_id)
  P->>EDB: UPDATE published_at
  SB->>W: Deliver (peek-lock)
  W->>DB: TX1 upsert note_reference; claim run (lease, fencing token)
  alt newer version already known
    W->>DB: run=superseded; COMMIT
    W->>SB: Complete
  else
    W->>R: GET note version (verify sha256)
    W->>W: Stage1 preprocess
    W->>F: Stage2 structured extraction
    W->>W: Stage3 validate, verify evidence
    W->>DB: Stage4 terminology lookup (read-only)
    W->>DB: TX2 Stage5 persist (only if fencing token still valid); run=succeeded
    W->>SB: Complete (after commit)
  end
  D->>API: Open review queue / item
  API->>R: (via access policy) check patient/encounter access
  D->>API: Confirm/correct (If-Match)
  API->>DB: TX insert review_decision, update item (row version check), audit
  D->>API: Submit write-back (Idempotency-Key)
  API->>DB: TX insert writeback_operation + items (eligibility re-checked)
  WB->>DB: claim pending item
  WB->>E: POST unsigned order (Idempotency-Key = item key, expected note version)
  WB->>DB: item=succeeded (emr_order_id) / unknown_outcome / failed
```

## 4. Data flow and classification

```mermaid
flowchart LR
  N["Note text (PHI, high)"] -->|"TLS, claim-check"| W["Worker memory"]
  W -->|"snapshot, encrypted at rest, retention-limited"| C[("clinical_note_content")]
  W -->|"prompt: system instructions + delimited note (PHI)"| F["Foundry (in approved region, no training, abuse-monitoring per DPA)"]
  F -->|"structured JSON"| W
  W -->|"entities + evidence (PHI)"| X[("note_medication / note_investigation")]
  W -->|"raw output (policy flag)"| RAW[("note_extraction.raw_model_output")]
  W -.->|"metrics, IDs only, no text"| T["OpenTelemetry / App Insights"]
  X --> API["Review API (authZ + patient access)"] --> UI["Review UI"]
  API -->|"audit (no note text)"| A[("audit_event")]
```

Classification: **PHI-High** = note text, evidence quotes, raw model output; **PHI-Identifier** = patient/encounter IDs; **Operational** = run status, versions, metrics. Messages carry identifiers + hash only, never note text (ADR-0007).

## 5. Deployment (initial target: Azure Container Apps)

```mermaid
flowchart TB
  subgraph Sub["Azure subscription per environment"]
    subgraph VNet["VNet (private endpoints)"]
      subgraph ACA["Container Apps environment (workload profiles, internal)"]
        A1["cx-review-api (HTTP, min 2, scale on RPS)"]
        A2["cx-worker-realtime (KEDA: Service Bus queue length)"]
        A3["cx-worker-backfill (KEDA, max replicas capped by budget)"]
        A4["cx-writeback (min 1, KEDA on DB-polled metric/cron)"]
        J1["cx-db-migrator (Job, manual trigger)"]
        J2["cx-sweeper (Job, cron)"]
        J3["cx-backfill-orchestrator (Job)"]
      end
      PG[("Azure DB for PostgreSQL Flexible Server 18, zone-redundant HA")]
      SBN["Service Bus Premium (private endpoint)"]
      KV["Key Vault"]
      FDY["Foundry resource / model deployment"]
    end
    FD["Front Door / App Gateway + WAF"] --> A1
    SWA["Static UI hosting (private / behind WAF)"]
    MON["Azure Monitor / App Insights / Log Analytics"]
  end
  A1 & A2 & A3 & A4 -->|"managed identity"| PG & SBN & KV
  A2 & A3 -->|"managed identity"| FDY
```

Service Bus **Premium** is assumed for production: private endpoints, predictable throughput, larger messages, geo-DR options (Standard lacks private endpoints). Cost/approval: Q-14.

## 6. Trust boundaries

| TB | Boundary | Controls |
|---|---|---|
| TB-1 | Browser ↔ Review API | Entra ID OIDC (PKCE), JWT validation, RBAC, patient access check, WAF, rate limit, CSP |
| TB-2 | EMR ↔ Service Bus ↔ CX | Managed identity / SAS-free RBAC, private endpoint, event schema validation, no note text in events |
| TB-3 | CX ↔ EMR APIs (read/access/orders) | mTLS or Entra app-to-app tokens (Q-05), idempotency keys, timeouts |
| TB-4 | CX worker ↔ Foundry | Managed identity, private endpoint, **note text treated as untrusted data**, no tools, schema-constrained output |
| TB-5 | Services ↔ PostgreSQL | Per-service DB roles, Entra auth for PG, TLS, column/table grants, audit table insert-only |
| TB-6 | Operators ↔ Ops endpoints | `CX.Operator` role, PIM/JIT elevation, audited |

## 7. Failure and recovery flow

```mermaid
stateDiagram-v2
  [*] --> received: event consumed, note_reference upserted
  received --> superseded: newer version known (realtime)
  received --> processing: lease acquired (token++)
  processing --> succeeded: TX2 committed, message completed
  processing --> failed_retryable: transient (LLM 429/5xx, timeout, EMR read 5xx, DB conn)
  failed_retryable --> processing: redelivery / scheduled retry (backoff+jitter)
  processing --> processing: lease expired (worker crash) -> takeover by redelivery
  failed_retryable --> failed_terminal: attempts >= max
  processing --> failed_terminal: non-retryable (schema invalid after repair attempt, content hash mismatch, note too large)
  failed_terminal --> received: operator requeue (audited, new attempt series)
  succeeded --> [*]
  superseded --> [*]
```

| Failure | Detection | Behaviour | Data-loss guard |
|---|---|---|---|
| Service Bus unavailable | Publisher send fails | Outbox rows stay unpublished; publisher backs off; alert on outbox age | Outbox in EMR DB |
| Publisher crash after send, before mark | — | Re-send; broker duplicate detection (MessageId) + idempotent consumer | Dedup key |
| Worker crash mid-processing | Lock expires | Redelivery; run lease expired → new attempt with higher fencing token; old token writes rejected | Fencing token in TX2 `WHERE lease_token = $me` |
| DB down during TX2 | Exception | Message abandoned (not completed) → retry; LLM call repeated (cost, documented) | Ack after commit |
| Complete() fails after commit | Exception | Redelivery sees `succeeded` → complete, no-op | Idempotent |
| LLM throttled (429) | Status code | Respect `Retry-After`; global token-bucket limiter reduces concurrency; message lock renewed | Lock renewal |
| Poison message (bad schema) | Validation | Dead-letter immediately with reason; run not created or `failed_terminal` | DLQ + alert |
| Out-of-order / stale versions | Version compare | Older version → `superseded`; newer processed | Monotonic `latest_version` |
| Retry exhaustion | MaxDeliveryCount / attempt counter | `failed_terminal` + DLQ + alert; ops requeue | Sweeper |
| Write-back timeout | HTTP timeout | `unknown_outcome` → reconcile by idempotency key → succeeded or safe retry | Item key |
| Region outage | Monitoring | DR runbook (Q-13): restore PG geo-backup, Service Bus geo-DR alias | Backups |

Coverage sweeper (CS-009): periodically finds `clinical_note_reference` rows whose latest run is non-terminal and older than SLA, or (with EMR cooperation, Q-03) outbox events with no corresponding reference, and raises alerts.

## 8. Extraction pipeline internals (worker)

| Stage | Module | Deterministic? | Output |
|---|---|---|---|
| 1 Pre-process | `cx_worker.pipeline.preprocess` | Yes | `PreprocessedNote{text, offset_map, sections[], abbreviation_annotations[]}` |
| 2 LLM extract | `cx_worker.pipeline.extract` via `ExtractionModelAdapter` (Foundry impl; deterministic fixture impl for tests only, refused in non-test envs) | No (temperature 0, seed where supported) | `LlmExtractionOutput` (schema v1) |
| 3 Validate | `cx_worker.pipeline.validate` | Yes | per-item `ValidationFinding[]`, evidence spans |
| 4 Normalise | `cx_worker.pipeline.normalize` | Yes (given terminology release) | `TerminologyCandidate[]`, resolution |
| 5 Score & persist | `cx_worker.pipeline.persist` | Yes | DB rows, run outcome |

The orchestrator is a plain function pipeline (no autonomous agents, no tool calling). Prompt templates are versioned files (`prompts/extraction/v{n}.md`) whose SHA-256 is stored with each run.

