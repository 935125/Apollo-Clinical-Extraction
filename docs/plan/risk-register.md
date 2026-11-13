# J. Clinical, Architectural, Security, and Scalability Risk Register

Status: **Milestone 0 (Architecture & Feasibility)**  
Severity Scale: **Critical** > **High** > **Medium** > **Low**.

---

## 1. Risk Register

| Risk ID | Category | Description | Severity | Likelihood | Impact | Mitigation Strategy | Owner |
|---|---|---|---|---|---|---|---|
| **RSK-01** | **Clinical Safety** | Extracted medication is matched to an incorrect formulation or route (e.g. oral vs. intravenous), potentially causing dosing errors. | **Critical** | Low | High | Mandatory Stage 4 clinical attribute filtering (ADR-0010). Formulation and route must be compatible with master catalog. Any ambiguity blocks auto-selection and mandates clinician confirmation. | Clinical Lead / AI Eng |
| **RSK-02** | **Clinical Safety** | Clinician approves an extraction based on an outdated clinical note version that was subsequently amended by another doctor. | **Critical** | Medium | High | Database optimistic concurrency and monotonic note version check. Review API checks `clinical_note_head.latest_version == expected_version`. Mismatch immediately returns `409 NOTE_VERSION_STALE`. | Backend Eng |
| **RSK-03** | **Clinical Safety** | Negated or conditional medications (e.g. "Do not give Aspirin", "Start Statin if LDL > 190") extracted as new active orders. | **Critical** | Medium | High | Model prompt explicitly extracts assertion status (`negated`, `conditional`). Stage 3 validator checks assertion. Only `new_order` items are write-back eligible. Negated items cannot be written back without explicit manual doctor re-classification. | Clinical Lead / AI Eng |
| **RSK-04** | **Security / Compliance** | Raw patient notes, doctor narratives, or access tokens leak into telemetry or application logs. | **High** | Medium | High | OpenTelemetry custom processors enforce PHI and token redaction. Log-scanning unit tests verify that structured log output never contains synthetic PHI or authorization headers. | Security Eng / SRE |
| **RSK-05** | **Security** | Malicious injection embedded inside clinical text tricks the LLM into initiating unintended tool calls or leaking system prompts. | **High** | Low | High | Clinical text treated as untrusted data wrapped in XML delimiters. Model has zero tool-calling capabilities. Structured output strictly enforced. No autonomous agent execution permitted. | Security Eng / AI Eng |
| **RSK-06** | **Scalability** | Peak morning outpatient clinic rush floods LLM endpoint, hitting Microsoft Foundry TPM/RPM limits (HTTP 429). | **High** | High | Medium | Worker implements proactive token-bucket rate limiting matching provisioned capacity; exponential backoff with jitter on 429; Service Bus queue acts as shock absorber; provisioned throughput (PTU) reserved. | SRE / Cloud Arch |
| **RSK-07** | **Data Integrity** | Network timeout during EMR order POST causes uncertain outcome; subsequent retry creates a duplicate prescription. | **Critical** | Medium | High | End-to-end idempotency (ADR-0007). Unique `emr_idempotency_key` per item. Timeout moves state to `unknown_outcome`. Dispatcher queries EMR by idempotency key before attempting any re-submission. | Backend Eng |
| **RSK-08** | **Operational** | Historical note backfill workload consumes excessive database connections and LLM quota, degrading realtime doctor extractions. | **High** | High | Medium | Physical separation of queues (`note-events` vs `note-events-backfill`); separate worker replica pools with hard-capped concurrency; backfill reads from read replicas/exports, never the live OLTP database. | SRE / Database Arch |
| **RSK-09** | **Architecture** | Direct dependency on external cloud services during local unit testing leads to brittle builds and cost overruns. | **Medium** | High | Low | Complete adapter isolation. `SyntheticModelAdapter` and local testcontainers / in-memory fakes provide 100% deterministic local test execution without external cloud calls. | QA / Backend Eng |
| **RSK-10** | **Governance** | Medico-legal inability to reproduce why an extraction failed or produced a specific candidate two years later. | **High** | Low | High | Auditability by design: every run captures prompt version hash, model deployment, output schema version, terminology release ID, and raw validation findings. | Compliance / Architect |

