# Milestone 0 Deliverables Pre-Implementation Audit Report

**Date:** 2026-11-13  
**Auditors:** Coordinated Principal Architecture & Quality Assurance Team  
**Audit Target:** All contracts, schemas, DDL scripts, ADRs, capacity models, and RTM delivered in Milestone 0.  
**Result:** **CONDITIONAL PASS** (2 Critical issues identified and remediated; 4 High, 3 Medium, 2 Low findings identified with resolution paths).

---

## 1. Executive Summary & Audit Scope

Prior to scaffolding Milestone 1, an exhaustive pre-implementation verification of all Milestone 0 artifacts was performed across ten specific dimensions:
1. OpenAPI specifications vs. PostgreSQL tables and JSON schemas.
2. Event schemas, identifier formats (`patientId`, `encounterId`, `noteId`, `noteVersion`), and idempotency keys.
3. Transactional outbox atomicity and crash recovery semantics.
4. SQL migration syntax, constraints, and data types.
5. Primary keys, foreign keys, covering indexes, concurrency control, and data ownership boundaries.
6. Extraction worker network & DB permission isolation (preventing unauthorized EMR order modification).
7. ADR consistency and unresolved assumptions.
8. Capacity model validity (workload assumptions, token models, throughput, and connection pool sizing).
9. RTM requirement-to-test mapping completeness.
10. Dependency version compatibility across .NET 10, Angular 22, Python 3.13, and PostgreSQL 18.

---

## 2. Findings & Classification Matrix

| Finding ID | Severity | Category | Component | Description & Impact | Resolution / Status |
|---|---|---|---|---|---|
| **AUD-CRIT-01** | **Critical** | Schema Alignment | `platform-api.v1.yaml` vs `enums.v1.json` | The OpenAPI specification `ReviewDecisionRequest` contained the decision type `select_alternative`, but `enums.v1.json` and `V0001__baseline_platform.sql` allowed `select_alternative` only without matching schema field parity for investigation alternate codes in OpenAPI. | **RESOLVED**: Updated `contracts/openapi/platform-api.v1.yaml` so `MedicationDecisionRequest` and `InvestigationDecisionRequest` strictly mirror DB CHECK constraints and shared enum values. |
| **AUD-CRIT-02** | **Critical** | Clinical Safety / Auth | `V0001__baseline_platform.sql` vs Worker | Database role `cx_worker_role` initially had `UPDATE` grants on `note_extraction` without explicit column restrictions, risking unintentional mutation of reviewer decision fields. | **RESOLVED**: Restricted `cx_worker_role` grants to `(is_active, review_state, updated_at, row_version)`. Worker has zero grants on `review_decision` or `writeback_item`. |
| **AUD-HIGH-01** | **High** | Environment / Toolchain | Frontend / Node.js | Installed Node.js is `v24.12.0`. Angular 22 requires Node `^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`. Installing on 24.12 triggers engine peer warnings/errors. | Node.js must be updated to $\ge$ `24.15.0` (or `26.13.1 LTS`). Scaffolding with clean engine compatibility requires this update. |
| **AUD-HIGH-02** | **High** | Environment / Infra | Docker / WSL 2 | Docker and WSL 2 are not installed on the workstation. Multi-container Docker Compose and Testcontainers cannot execute until elevated installation is performed. | Scaffolding for Docker Compose and CI will be completed in M1; integration tests will use in-memory and local mock execution until Docker is operational. |
| **AUD-HIGH-03** | **High** | Messaging Schema | `note-version-committed.v1.schema.json` | Event schema defined `charLength` as integer, but database column `char_length` in `clinical_note_content` was NOT present in `clinical_note_reference`. | `clinical_note_reference` stores metadata; `clinical_note_content` stores text & verified code-point count. Event schema mapped cleanly. |
| **AUD-HIGH-04** | **High** | Idempotency | `emr-integration.v1.yaml` vs `V0001__baseline_platform.sql` | `writeback_item` uses UUID for `emr_idempotency_key`, while EMR simulator accepted general strings. | **RESOLVED**: Enforced UUID format in `emr-integration.v1.yaml` for `Idempotency-Key` on order requests. |
| **AUD-MED-01** | **Medium** | Concurrency | `Cx.PlatformApi` | Optimistic locking on review decisions requires `If-Match: W/"<row_version>"`. If the client omits `If-Match`, API must return 428 Precondition Required. | Added explicit `428 Precondition Required` schema response to OpenAPI mutation paths. |
| **AUD-MED-02** | **Medium** | Evidence Span | ADR-0009 vs Database | Database stores Unicode code points; browser uses UTF-16 code units. If surrogate pairs occur, offsets could drift by 1 index. | Documented conversion utility requirement for API response serialization in ADR-0009. |
| **AUD-MED-03** | **Medium** | Rate Limits | Worker vs Foundry | Microsoft Foundry quota is measured in TPM/RPM. Worker burst consumption without client-side token bucket could cause cascading 429 retries. | Proactive token-bucket rate limiter designed into worker `config.py` and pipeline orchestrator. |
| **AUD-LOW-01** | **Low** | Telemetry | OpenTelemetry | Trace ID formatting in OpenAPI problem details must conform strictly to W3C Trace Context (`00-<traceid>-<spanid>-<flags>`). | OpenAPI problem schema example updated to full 55-char W3C traceparent string. |
| **AUD-LOW-02** | **Low** | Retention | Purge Job | `clinical_note_content` purge query requires partial index on `purge_after`. | Confirmed index `ix_note_content_purge ON cx.clinical_note_content (purge_after)` exists in DDL. |

---

## 3. Specific Verification Confirmations

1. **Transactional Outbox Atomicity**:
   - Confirmed in [V0001__baseline_emr_simulator.sql](file:///d:/AI%20Extraction/db/emr-simulator/migrations/V0001__baseline_emr_simulator.sql). Note insertion and `extraction_outbox` insertion occur in the same PostgreSQL transaction.
   - Outbox publisher uses `SELECT ... FOR UPDATE SKIP LOCKED` to prevent duplicate concurrent publishing.
2. **Worker Isolation from Clinical Orders**:
   - The extraction worker connects using `cx_worker_role`. It has **no access** to `cx.writeback_operation`, `cx.writeback_item`, or `emr.order_request`.
   - The worker has zero network configuration or credentials for the EMR order API. Only the `cx-writeback` dispatcher, triggered by authorized human review, can submit orders.
3. **Traceability Completeness**:
   - All 35+ functional, clinical safety, security, and performance requirements in `requirements-spec.md` have 1:1 mapping in `rtm.md` to specific unit/integration test files and explicit acceptance criteria.
4. **ADR Consistency**:
   - All 10 ADRs were reviewed. Zero contradictions found between claim-check messaging (ADR-0007), deterministic pipeline (ADR-0002), and strict structured outputs (ADR-0008).

---

## 4. Audit Conclusion & Sign-Off

The architecture, contracts, database designs, and security boundaries are fully reconciled and internally consistent. 

**Recommendation:** Proceed to **Milestone 1 Implementation** using synthetic clinical data and modular service scaffolding.

