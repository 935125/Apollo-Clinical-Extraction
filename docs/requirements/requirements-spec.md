# B. Requirements Specification

Status: DRAFT v0.1 (Milestone 0). Requirement IDs are stable; never renumber — deprecate instead.
Priority: **M** = must (release-blocking), **S** = should, **C** = could.

## 1. Functional requirements

### 1.1 Note intake (EMR side — simulated in development)
| ID | Requirement | Pri |
|---|---|---|
| FR-001 | When a clinical note version is committed in the EMR, an extraction event shall be written in the **same database transaction** (transactional outbox). | M |
| FR-002 | The doctor-facing note save shall not wait on, or fail because of, AI extraction. | M |
| FR-003 | Every note amendment shall create a new immutable note version (`note_id`, `note_version`, `content_sha256`). | M |
| FR-004 | An outbox publisher shall publish unpublished outbox rows to Azure Service Bus with `MessageId = event_id`, and mark them published only after broker acknowledgement. | M |
| FR-005 | The EMR (or simulator) shall expose a read API for an immutable note version, used by the worker (claim-check, ADR-0007). | M |

### 1.2 Extraction pipeline
| ID | Requirement | Pri |
|---|---|---|
| FR-010 | The worker shall consume events with at-least-once semantics and process each `(note version, pipeline profile)` at most once to a terminal state (idempotent). | M |
| FR-011 | Messages shall be completed (acknowledged) only after the processing outcome is durably committed. | M |
| FR-012 | Events for a note version older than the latest known version shall be marked `superseded` and not sent to the LLM (realtime trigger). | M |
| FR-013 | Transient failures shall retry with exponential backoff + jitter, bounded by a configured maximum; exhaustion shall produce a `failed_terminal` run and a dead-lettered message, both visible to operators. | M |
| FR-014 | Stage 1 preprocessing: safe normalisation (whitespace, unicode NFC, line endings), section detection, abbreviation annotation (non-destructive), and an offset map back to the original text. Negations, units and numbers shall never be removed or altered. | M |
| FR-015 | Stage 2: LLM extraction through a Microsoft Foundry deployment using **structured outputs** constrained to `llm-extraction-output.v1`. | M |
| FR-016 | Medication attributes: name, strength, dose, route, frequency, duration, formulation, assertion status, evidence quote. Investigation attributes: name, specimen (if stated), assertion status, evidence quote. Absent values are `null`. | M |
| FR-017 | Supported assertion statuses: `new_order`, `current_medication`, `historical`, `discontinued`, `negated`, `suggested`, `conditional`, `uncertain` (subset rules in `enums.v1.json`). | M |
| FR-018 | Stage 3: Pydantic structural validation plus clinical rule validation (dose/unit form, frequency vocabulary, evidence alignment, assertion consistency, mandatory order attributes). Findings are stored per item with severity. | M |
| FR-019 | Evidence spans shall be computed by the worker by locating the model's quote in the source text; the model's offsets (if any) are never trusted. Items whose evidence cannot be located are flagged `EVIDENCE_NOT_FOUND` and are not write-back eligible. | M |
| FR-020 | Stage 4: terminology normalisation in order: exact code → alias/synonym → keyword → (optional) vector → clinical attribute filter → rank → resolve or mark ambiguous/unmatched. | M |
| FR-021 | A medication shall not be matched to a master item with an incompatible strength, formulation or route. An investigation shall not be matched across clinically distinct specimen/method variants. | M |
| FR-022 | Stage 5: persist structured output, (policy-permitting) raw model output, spans, model deployment + version, prompt version, schema version, terminology release, preprocessor version, validation findings, candidates, timestamps, outcome, token usage and redacted errors. | M |
| FR-023 | A per-item `quality_score` (heuristic, **uncalibrated**) and `quality_flags` shall be stored; they shall never be presented as probabilities. | M |
| FR-024 | Historical backfill shall run as checkpointed, restartable batch jobs on a separate queue and worker pool, reading from a non-transactional source (replica/export), never the live EMR primary. | M |
| FR-025 | Operators shall be able to list failed/dead-lettered runs and requeue them; requeue is audited. | M |

### 1.3 Clinician review
| ID | Requirement | Pri |
|---|---|---|
| FR-030 | Authorised clinicians shall see a review queue filtered by facility/specialty/encounter, with keyset pagination. | M |
| FR-031 | For each item show: extracted values, evidence highlighted in source text, normalised candidate(s), warnings, assertion status, review status. | M |
| FR-032 | Clinicians may confirm, correct (field-level), reject (with reason), or select an alternative valid master item. | M |
| FR-033 | Every decision is append-only (`review_decision`), records reviewer, role, timestamp, entity row version and note version. | M |
| FR-034 | Decisions shall use optimistic concurrency (`If-Match` ETag); conflicting edits return 412. | M |
| FR-035 | Decisions on an extraction whose note version is no longer current shall be rejected with `409 NOTE_VERSION_STALE`. | M |
| FR-036 | Clinicians may view processing details (model/prompt/schema/terminology versions, validation findings). | S |

### 1.4 EMR write-back
| ID | Requirement | Pri |
|---|---|---|
| FR-040 | Only items that are (a) confirmed/corrected by an authorised clinician, (b) `new_order` intent, (c) matched to an active master item, (d) evidence-verified, and (e) from the current note version are write-back eligible. | M |
| FR-041 | Write-back requests are idempotent end-to-end: client `Idempotency-Key` → `writeback_operation`; each item carries a stable key passed to the EMR. | M |
| FR-042 | Timeouts / uncertain outcomes put the item in `unknown_outcome`; reconciliation queries the EMR by idempotency key before any retry. No blind retries. | M |
| FR-043 | Partial failures are reported per item; successful items are never resubmitted. | M |
| FR-044 | Write-back creates orders in the EMR's unsigned/pending state (assumption A-07) — the EMR enforces signing and clinical authorisation. | M |
| FR-045 | There is no code path from LLM output to the EMR that bypasses a human decision. | M |

### 1.5 Terminology masters
| ID | Requirement | Pri |
|---|---|---|
| FR-050 | Local drug and investigation masters are loaded as versioned releases; extraction records the release used. | M |
| FR-051 | Masters support aliases/synonyms, and optional RxNorm / LOINC / SNOMED CT mappings subject to licence. | S |
| FR-052 | Clinicians can search masters (for alternative selection) with attribute filters. | M |

## 2. Non-functional requirements
Targets marked *(proposed)* are placeholders pending agreement with the project owner (open question Q-20). No SLO is claimed until measured.

| ID | Requirement | Target |
|---|---|---|
| NFR-001 | Horizontal scaling with separate boundaries: EMR-facing API, outbox publisher, extraction workers, backfill workers, review API, write-back dispatcher. | Architecture |
| NFR-002 | End-to-end latency (note commit → reviewable extraction), realtime path | p95 ≤ 120 s *(proposed)* |
| NFR-003 | Sustained realtime throughput | ≥ peak modelled rate in capacity model ×2 headroom *(proposed)* |
| NFR-004 | Review API latency for queue and detail endpoints | p95 ≤ 300 ms at modelled concurrency *(proposed)* |
| NFR-005 | Interactive endpoints shall use bounded pagination (max page size 100) and index-backed queries — no unbounded scans (verified by EXPLAIN in CI on seeded data). | M |
| NFR-006 | Worker concurrency, LLM concurrency, DB pool size, prefetch, batch size, timeouts, retry limits, max note size and rate limits are configuration, validated at startup. | M |
| NFR-007 | Backpressure: workers shall not exceed configured LLM TPM/RPM or DB pool size; throttling (429) slows consumption rather than failing messages. | M |
| NFR-008 | Reproducibility: any persisted extraction can be traced to exact note content hash, pipeline component versions and terminology release. | M |
| NFR-009 | Observability: OpenTelemetry traces/metrics/logs with correlation ID propagated EMR → bus → worker → DB → API. | M |
| NFR-010 | Metrics: notes/min, E2E latency, queue age, DLQ depth, DB latency, CPU/mem, tokens, 429 rate, error rate, cost/note, review throughput. | M |
| NFR-011 | Maintainability: modular monorepo, no circular deps, lint + type checks enforced in CI. | M |
| NFR-012 | Accessibility: review UI meets WCAG 2.2 AA (verified with axe + manual check). | S |

## 3. Clinical safety requirements
| ID | Requirement |
|---|---|
| CS-001 | The system is an assistant: it shall not prescribe, diagnose, sign orders, or modify clinical records autonomously. |
| CS-002 | No auto-approval: every write-back item requires an explicit clinician decision regardless of quality score. |
| CS-003 | Negated, historical, discontinued, conditional, suggested and uncertain items shall be visually distinct and **not** write-back eligible unless the clinician explicitly re-classifies them to `new_order` (recorded as a correction). |
| CS-004 | Missing attributes shall remain null/unresolved — never defaulted or inferred by the model or code. |
| CS-005 | Normalisation shall never silently substitute strength, formulation or route. |
| CS-006 | Stale extractions (note amended after extraction) shall not be approvable or written back. |
| CS-007 | Duplicate orders shall be prevented across retries, re-extractions and repeated clicks. |
| CS-008 | Clinically significant false extractions (e.g., negated drug extracted as new order) shall be measured separately in evaluation and have a release threshold agreed with clinical safety officer. |
| CS-009 | A note version shall never silently disappear: every received event reaches a terminal, visible state; a sweeper alerts on notes exceeding the SLA without terminal state. |
| CS-010 | A clinical safety case / hazard log shall be maintained (hazard IDs in risk register) and signed off before any clinical use. |

## 4. Security & privacy requirements
| ID | Requirement |
|---|---|
| SEC-001 | Authentication via Microsoft Entra ID (OIDC/JWT) in all deployed environments; development auth mode refuses to start outside `Development`. |
| SEC-002 | Role-based authorisation (`CX.Reviewer`, `CX.Viewer`, `CX.Operator`, `CX.TerminologyAdmin`, `CX.Auditor`). |
| SEC-003 | Patient/encounter-level access check on every patient-scoped request via `IPatientAccessPolicy` (EMR-backed; assumption A-09). |
| SEC-004 | Managed identities, least privilege; separate DB roles per service; no shared admin credentials. |
| SEC-005 | Secrets only in Key Vault (deployed) / user-secrets or `.env` excluded from git (local). |
| SEC-006 | TLS 1.2+ in transit; encryption at rest (platform-managed or CMK per Q-12). |
| SEC-007 | Private endpoints for PostgreSQL, Service Bus, Key Vault, Foundry, AI Search where supported. |
| SEC-008 | Input validation, request size limits (note ≤ configured max, default 64 KiB), rate limiting per user and per client. |
| SEC-009 | Audit trail for: view of source note, decisions, write-back, requeue, terminology changes, access denials. |
| SEC-010 | No PHI, note text, tokens or secrets in application logs/traces; enforced by log redaction + automated log-scan tests. |
| SEC-011 | Clinical note text is untrusted LLM input: fixed system prompt, delimited data, no tools/functions exposed to the model, output schema-constrained, evidence verification. |
| SEC-012 | Dependency/vulnerability scanning (NuGet, npm, pip, containers) and SAST in CI; failing high/critical blocks merge. |
| SEC-013 | Synthetic data only until institutional approval (Q-01). Real data must not be sent to any external service without a data-processing agreement. |
| SEC-014 | Startup configuration validation rejects insecure settings (e.g., HTTP endpoints, dev auth, missing TLS) in non-dev environments. |
| SEC-015 | Data retention and deletion policies implemented per table (see database design §Retention). |
| SEC-016 | Environment isolation: separate subscriptions/resource groups and identities for dev/test/prod; no prod data in lower environments. |

## 5. Availability & recovery requirements *(targets proposed, Q-20)*
| ID | Requirement |
|---|---|
| AVL-001 | Review API availability ≥ 99.9 % in business hours *(proposed)*. |
| AVL-002 | Extraction is asynchronous: an AI outage degrades to delayed extraction; the EMR is unaffected. |
| AVL-003 | PostgreSQL: zone-redundant HA; PITR backups; RPO ≤ 5 min, RTO ≤ 1 h *(proposed)*. |
| AVL-004 | Messages survive worker crashes (peek-lock + lease); in-flight work resumes after lock expiry. |
| AVL-005 | DR runbook with tested restore (M9) and region strategy (Q-13). |
| AVL-006 | Queue outage: outbox accumulates in EMR DB and drains after recovery (no loss). |

## 6. Integration assumptions (summary — full list in open-questions.md)
A-01 EMR can write an outbox row in the note-commit transaction · A-02 EMR exposes immutable note-version reads · A-07 EMR accepts unsigned orders with an idempotency key · A-09 EMR exposes an access-check API · A-11 Masters exportable with stable codes.

## 7. Explicitly out of scope
- OOS-01 Autonomous prescribing, diagnosis, order signing, clinical decision support/alerts (drug interactions, allergy checks — EMR responsibility).
- OOS-02 Inpatient, discharge summaries, radiology/pathology report extraction (future).
- OOS-03 Speech/ambient capture.
- OOS-04 Copying or warehousing the full EMR dataset; analytics on EMR data.
- OOS-05 Model fine-tuning (until an approved, governed dataset exists).
- OOS-06 Patient-facing features.
- OOS-07 Regulatory certification (SaMD classification, CDSCO/MDR) — assessment is a stakeholder activity; the platform supplies evidence only.

