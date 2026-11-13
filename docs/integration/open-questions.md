# H. Hospital Integration: Unknowns vs. Safe Development Assumptions

Status: **Milestone 0 (Architecture & Feasibility)**  
Purpose: Explicitly catalog all hospital EMR integration unknowns, separate them from safe development assumptions, and define simulator boundaries.

---

## 1. Matrix of Unknowns & Safe Development Assumptions

| Area | Unknown Hospital Reality | Safe Engineering Assumption for Development | Simulator / Isolation Control | Production Verification Required Before Go-Live |
|---|---|---|---|---|
| **Outbox Capability (A-01)** | Does the existing EMR transactional database permit adding an `extraction_outbox` table and writing outbox rows within the note save transaction? | Assume the EMR supports the Transactional Outbox pattern natively. | `emr-simulator` commits note versions and outbox events in a single PostgreSQL transaction. | Hospital DBA & EMR vendor architecture review. If not allowed, fallback to Change Data Capture (CDC / Debezium) or EMR webhook publisher with compensating consistency checks. |
| **Note Version Immutability (A-02)** | Does the EMR assign monotonic version numbers to amended notes, or does it overwrite rows in-place? | Assume notes are versioned and queryable by `(noteId, noteVersion)` with immutable text snapshots. | `emr-simulator` enforces version increments and returns 404 for non-existent versions. | Verify EMR data model. If EMR mutates in place, CX must hash and timestamp incoming snapshots to construct virtual versions. |
| **Outbox Delivery SLA (A-03)** | How quickly does the hospital network publish outbox events to Azure Service Bus? | Assume publish delay is under 2 seconds during peak hours. | Simulator outbox publisher runs with a 1-second polling interval. | End-to-end telemetry monitoring outbox lag. |
| **Service Bus Auth (A-04)** | Does hospital security policy permit Microsoft Entra ID Managed Identity across tenant boundaries? | Development uses local connection strings or emulator; production targets Managed Identity. | Service Bus client configured with unified abstraction supporting both `DefaultAzureCredential` and connection strings. | Coordinate with Hospital IT Security for Azure subscription federations. |
| **EMR Read Auth (A-05)** | What authentication mechanism is required by the hospital EMR for back-channel note retrieval? | Assume OAuth2 / Entra ID client credentials grant (app-to-app bearer token). | Simulator expects `Authorization: Bearer <token>`. | Obtain hospital API gateway specs and certificate/mTLS requirements. |
| **Order Write-Back State (A-07)** | Does the EMR order entry system allow external creation of "unsigned / draft" orders pending doctor co-signature? | Assume the EMR accepts draft/unsigned orders with an `Idempotency-Key` and returns an `emr_order_id`. | Simulator writes to `emr.order_request` in state `unsigned`. | Clinical governance sign-off. Ensure hospital allows AI-suggested draft orders to appear in doctor's pending signing tray. |
| **Write-Back Idempotency (A-08)** | Does the EMR natively enforce idempotency keys on order creation? | Assume EMR supports unique constraint on `Idempotency-Key` and returns existing order on replay. | Simulator enforces unique constraint on `idempotency_key`. | Verify if EMR API provides native idempotency; if not, CX dispatcher must perform pre-flight query before re-submission. |
| **Patient Access Control (A-09)** | Is there an existing hospital service to check whether a given doctor has legitimate care-team access to a patient? | Assume an endpoint `POST /emr/v1/access-checks` exists returning `{ allowed: boolean }`. | Simulator provides mock access check verifying doctor-to-encounter assignment. | Integrate with hospital Active Directory / EMR role-based access matrix. |
| **Data Retention Policies (A-10)** | What are the medico-legal retention mandates for intermediate AI extraction artifacts in this hospital network? | Assume 90-day retention for raw note text snapshots, 8 years for review decisions and audit logs. | Automated purge job with configurable retention schedules. | Hospital Legal & Compliance Officer formal approval. |
| **Master Data Formats (A-11)** | How are the hospital's drug and lab test catalogs maintained and exported? | Assume periodic versioned JSON/CSV releases containing hospital codes, generic names, routes, and formulations. | Terminology loader accepts standardized schema with release tracking. | Establish automated pipeline with Hospital Pharmacy & Laboratory Information System (LIS). |

---

## 2. Guardrails Against Mock Misrepresentation

1. **No Silent Mock Masquerading**: The `emr-simulator` is explicitly isolated in a separate project namespace (`Cx.EmrSimulator`) and container image. Under no circumstances may simulator code be compiled into the production API (`Cx.PlatformApi`) or worker (`cx_worker`).
2. **Environment Enforcement**: The platform checks `Environment.IsProduction()`. If simulator URLs or configuration flags are detected in staging or production environments, application startup immediately terminates with a fatal configuration error.

