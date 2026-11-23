# F. Requirements Traceability Matrix (RTM)

Status: **Milestone 0 (Architecture & Feasibility)**  
Traceability Map: Business Requirements -> Owning Component -> Implementation Milestone -> Verification Test & Acceptance Criteria.

---

## 1. Traceability Table

| Req ID | Requirement Summary | Owning Component | Milestone | Verification Test / Method | Acceptance Criteria |
|---|---|---|---|---|---|
| **FR-001** | Atomic Note Save & Outbox Write | `Cx.EmrSimulator` / EMR | M2 | `OutboxAtomicityTests.cs` | Note version and outbox event commit in single DB transaction. Rolling back one rolls back both. |
| **FR-002** | Doctor Save Unblocked by AI | `Cx.EmrSimulator` | M2 | `NoteApiPerformanceTests.cs` | Note save returns HTTP 201 in < 100ms without synchronously waiting for extraction. |
| **FR-003** | Monotonic Note Versioning | `Cx.EmrSimulator` | M2 | `NoteVersioningTests.cs` | Every amendment increments `note_version` and stores new SHA-256 hash. |
| **FR-004** | Outbox Reliable Publisher | `Cx.OutboxPublisher` | M2/M3 | `OutboxPublisherTests.cs` | Outbox rows published to Service Bus with `MessageId = event_id`. Rows marked published only on broker ACK. |
| **FR-005** | Claim-Check Note Read API | `Cx.EmrSimulator` | M2 | `NoteReadApiTests.cs` | Exposes authenticated endpoint returning immutable note version text matching SHA-256. |
| **FR-010** | At-least-once Idempotent Worker | `cx_worker` | M3 | `test_idempotent_consumer.py` | Repeated delivery of same event produces exactly one extraction run; duplicate skipped safely. |
| **FR-011** | Message Complete After Durable Commit | `cx_worker` | M3 | `test_crash_recovery.py` | Service Bus message is completed only AFTER PostgreSQL transaction commits. Crash mid-processing triggers safe redelivery. |
| **FR-012** | Superseded Version Skip | `cx_worker` | M3 | `test_version_supersession.py` | Out-of-order events for older note versions are marked `superseded` without calling LLM. |
| **FR-013** | Exponential Backoff & DLQ | `cx_worker` | M3 | `test_retry_and_dlq.py` | Transient failures retry with jitter up to max attempts; unrecoverable poison messages dead-lettered cleanly. |
| **FR-014** | Stage 1 Text Preprocessing | `cx_worker.pipeline.preprocess` | M4 | `test_preprocess.py` | Normalizes whitespace and line endings without altering clinical words, numbers, or negation cues; computes offset map. |
| **FR-015** | Stage 2 Structured LLM Extraction | `cx_worker.pipeline.extract` | M4 | `test_foundry_adapter.py` | Invokes Foundry `/openai/v1/` endpoint with strict JSON schema; returns structured `LlmExtractionOutput`. |
| **FR-016** | Extraction of Meds & Investigations | `cx_worker.pipeline.extract` | M4 | `test_entity_extraction.py` | Accurately extracts drug name, strength, dose, route, frequency, duration, formulation; absent fields are strictly null. |
| **FR-017** | Assertion Status Detection | `cx_worker.pipeline.extract` | M4 | `test_assertion_detection.py` | Accurately classifies items as `new_order`, `historical`, `negated`, `conditional`, `discontinued`, or `uncertain`. |
| **FR-018** | Stage 3 Schema & Clinical Validation | `cx_worker.pipeline.validate` | M4 | `test_clinical_validation.py` | Pydantic structural validation; checks dosage/frequency formatting; records structured validation findings. |
| **FR-019** | Deterministic Evidence Verification | `cx_worker.pipeline.validate` | M4 | `test_evidence_spans.py` | Verifies model's verbatim quote in source note; computes character offsets; flags missing quotes with `EVIDENCE_NOT_FOUND`. |
| **FR-020** | Stage 4 Terminology Normalization | `cx_worker.pipeline.normalize` | M5 | `test_terminology_matching.py` | Hierarchical matching: exact code -> alias -> trigram -> clinical filtering. |
| **FR-021** | Clinical Attribute Filtering Gate | `cx_worker.pipeline.normalize` | M5 | `test_attribute_gate.py` | Rejects master candidates with incompatible formulation (e.g. oral vs IV) or specimen (serum vs urine). |
| **FR-022** | Stage 5 Quality & Persistence | `cx_worker.pipeline.persist` | M6 | `test_persistence.py` | Stores structured output, spans, model version, prompt hash, schema version, and validation findings in PostgreSQL. |
| **FR-024** | Checkpointed Historical Backfill | `cx_worker.backfill` | M3/M9 | `test_backfill_orchestrator.py` | Batch job reads from non-OLTP source; processes in batches with restartable cursor checkpoints. |
| **FR-030** | Review Queue Keyset Pagination | `Cx.PlatformApi` | M6 | `ReviewQueueApiTests.cs` | Keyset pagination returns active pending items ordered by creation time; zero table scans. |
| **FR-031** | Angular Review UI Evidence Highlighting | `cx-review-ui` | M6 | `review-queue.spec.ts` | Displays extracted items, warnings, normalized candidates, and highlights verbatim evidence in source text. |
| **FR-032** | Clinician Decision Actions | `Cx.PlatformApi` + UI | M6 | `ClinicianDecisionTests.cs` | Allows doctors to confirm, correct, reject, or select alternative master items. |
| **FR-033** | Immutable Append-Only Decisions | `Cx.PlatformApi` | M6 | `ReviewDecisionAuditTests.cs` | Every doctor action written to `cx.review_decision` with user ID, role, timestamp, and row version. |
| **FR-034** | Optimistic Concurrency Control | `Cx.PlatformApi` | M6 | `ConcurrencyControlTests.cs` | Prevents lost updates using `If-Match` ETags; concurrent modifications return HTTP 412. |
| **FR-035** | Stale Note Version Guard | `Cx.PlatformApi` | M6 | `StaleNoteGuardTests.cs` | Rejects decisions on extractions whose note version is no longer current with `409 NOTE_VERSION_STALE`. |
| **FR-040** | Write-Back Eligibility Validation | `Cx.Domain` | M7 | `WritebackEligibilityTests.cs` | Verifies items are confirmed, active, current version, and have active master item before allowing write-back. |
| **FR-041** | Idempotent EMR Order Write-Back | `Cx.WritebackDispatcher` | M7 | `WritebackIdempotencyTests.cs` | Client `Idempotency-Key` and item-level EMR keys prevent duplicate orders across network retries. |
| **FR-042** | Uncertain Outcome Reconciliation | `Cx.WritebackDispatcher` | M7 | `ReconciliationTests.cs` | Timeouts put items in `unknown_outcome`; queries EMR by idempotency key before attempting retry. |
| **FR-044** | External Unsigned Order Creation | `Cx.WritebackDispatcher` | M7 | `EmrOrderSubmissionTests.cs` | External orders created strictly in draft/unsigned state; hospital EMR enforces physician signing. |
| **SEC-001** | Microsoft Entra ID Authentication | `Cx.PlatformApi` | M8 | `AuthenticationTests.cs` | Validates JWT tokens against Entra ID authority; rejects expired or untrusted tokens (HTTP 401). |
| **SEC-002** | Role-Based Access Control | `Cx.PlatformApi` | M8 | `AuthorizationTests.cs` | Enforces roles (`CX.Reviewer`, `CX.Operator`, `CX.Auditor`) on corresponding endpoints (HTTP 403). |
| **SEC-003** | Patient Care-Team Access Checks | `Cx.PlatformApi` | M8 | `PatientAccessPolicyTests.cs` | Verifies doctor has active encounter care-team relationship; unauthorized access logged and denied. |
| **SEC-004** | Least-Privilege DB Security | Infrastructure / PostgreSQL | M8 | `DatabaseRoleTests.cs` | Verifies `cx_worker_role` and `cx_api_role` cannot alter DDL or delete audit rows. |
| **SEC-010** | Strict Telemetry PHI Redaction | Observability | M8 | `TelemetryRedactionTests.cs` | Automated tests verify application logs and traces contain zero PHI, doctor notes, or bearer tokens. |
| **SEC-011** | Prompt Injection Defense | `cx_worker.pipeline.extract` | M4/M8 | `test_prompt_injection.py` | Injected instructions inside clinical notes do not alter extraction schema or execute commands. |
| **NFR-005** | Bounded Pagination & Index Queries | PostgreSQL & API | M6/M9 | `ExplainAnalyzeTests.cs` | CI test runs `EXPLAIN ANALYZE` on seeded database; verifies index-backed scans and absence of seq scans. |
| **NFR-007** | Worker Backpressure on LLM 429 | `cx_worker` | M3/M9 | `test_worker_backpressure.py` | Ingestion slows down upon receiving Foundry 429 throttling; respects `Retry-After` without crashing. |
| **NFR-009** | OpenTelemetry Trace Propagation | All Services | M8 | `DistributedTracingTests.cs` | Correlation ID traced from EMR event -> Service Bus -> Worker -> DB -> Review API -> Write-back. |

