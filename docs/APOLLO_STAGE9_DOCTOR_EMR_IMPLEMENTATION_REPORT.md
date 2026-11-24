# Apollo Clinical Extraction Platform — Stage 9 Implementation Report
## Premium Doctor OP EMR Portal, Secure Authentication & Asynchronous Clinical Extraction

**Date:** 24 November 2026  
**Platform Version:** ASP.NET Core .NET 10 | Angular 21 | PostgreSQL 18.7  
**Stage:** Stage 9 — Complete Verification & Evidence-Backed Deliverable  
**Author:** Principal Healthcare Product Architect, Senior Angular Engineer & Healthcare Security Lead  

---

### Executive Summary

Stage 9 extends the Apollo Clinical Extraction Platform with a complete, production-structured **Doctor Outpatient Electronic Medical Record (OP EMR)** portal. The portal was designed and implemented in strict fidelity to the institutional reference design (`media_1795502851121_34fb54af.jpg`), providing an ivory and deep green aesthetic that harmonizes outpatient clinical documentation with AI-assisted clinical extraction.

Rather than a static prototype or mock interface, Stage 9 delivers a genuine full-stack architecture:
- Real database persistence in PostgreSQL via additive schema `V0009__doctor_emr.sql`.
- Immutable note versioning, draft snapshots, optimistic concurrency, and database triggers.
- Production-grade security: server-side session cookies (`ApolloDoctor`), 30-minute sliding expiration, double-submit CSRF protection (`X-XSRF-TOKEN`), rate limiting, and brute-force account lockout.
- Transactional Outbox pattern guaranteeing zero clinical note loss during messaging or broker outages.
- Azure Service Bus queue adapter (`op-clinical-notes`) alongside crash-resilient simulation and disabled modes.
- Asynchronous worker consumer (`EmrConsumer`) with SHA-256 hash validation, advisory locking, and durable `extraction_id` binding.
- End-to-end integration verified across 10 isolated backend integration tests, 42 Angular unit tests, 207 full backend regression tests, and 5 Playwright browser scenarios capturing all 5 reference screens with zero errors.
- 100% preservation of all Stage 5/6/7/8 evidence files and the baseline `cx_platform` database (all 44 extraction runs, 101 medications, 136 investigations, and 12 audit reviews verified bit-for-bit identical via MD5 hashes).
- Zero billable Microsoft Foundry calls executed during automated Stage 9 test execution.

---

### 1. Visual Baseline & Screen Alignment

The user interface matches the reference mockup across all five specified screens:

| Screen | View Name | Visual & Functional Baseline Implementation | Evidence File |
| :--- | :--- | :--- | :--- |
| **Screen 1** | **Doctor Login** | 40/60 split-screen desktop layout: left pane in Deep Apollo Green (`#064B41`) with gold Apollo Hospitals logo, institutional headline ("Your clinical workspace. Secure access to outpatient documentation and AI-assisted clinical extraction."), and security badge. Right pane in warm ivory (`#F7F5F0`) with "Welcome back", email/doctor ID input, password field with visibility toggle, and primary "Sign in" action. | `01-doctor-login.png` |
| **Screen 2** | **Doctor Dashboard** | Top branded navigation bar with Apollo logo, vertical rule, links (`OP EMR`, `Clinical Extraction`, `History`), notification bell, and circular doctor avatar (`DK` for Dr. K. Krishnan). Header with "+ New Clinical Note" button, filter tabs (`My Encounters`, `Draft Notes (2)`, `Awaiting Review (3)`), search input, and outpatient encounters table (`Patient Reference`, `Encounter Date`, `Note Status`, `Extraction`, `Action`). Includes pulsing spinner for `Processing` state. | `02-doctor-dashboard.png` |
| **Screen 3** | **Clinical Note Editor** | Back navigation link (`← Doctor Dashboard`), "Clinical Note" heading, metadata context strip (`Patient: P100234 \| Encounter: ENC-100567 \| Date: 28 Jan 2026`), top action buttons (`Save Draft`, `✍ Sign & Submit`), framed writing container with live word count and character meter, text-based PDF import, and sign confirmation modal with clinical warning. | `03-clinical-note-editor.png` |
| **Screen 4** | **Extraction Status** | Document checkmark badge, "Clinical note submitted", subtitle ("Your note has been signed and queued for extraction."), 5-stage progression stepper (`Signed 11:24` $\rightarrow$ `Queued 11:24` $\rightarrow$ `Processing ...` $\rightarrow$ `Completed` $\rightarrow$ `Review`), status description, and action buttons ("View Note", "Review extraction results"). | `04-extraction-status.png` |
| **Screen 5** | **Extraction Results & Review** | Deep-linked review workspace inside the portal shell showing completed extraction for `NT-100874 (v1)`, `Patient: P100234`, model provenance (`Microsoft Foundry — gpt-6.1-sol-2026-09-29`), entity tabs (`Medications (1)`, `Investigations (1)`, `Validation & Alerts`, `Provenance`), RxNorm/LOINC normalization tags, and verbatim source evidence citations. | `05-extraction-results.png` |

---

### 2. Database Schema Design (`V0009__doctor_emr.sql`)

The Stage 9 database schema is completely additive and maintains strict separation from existing tables. It introduces the following core entities:

1. **`emr_doctors`**: Doctor identity, role (`Doctor`, `ClinicalReviewer`, `Administrator`), and Argon2/PBKDF2 password hashes.
2. **`emr_sessions`**: Server-side sliding sessions, IP tracking, user-agent fingerprints, and server-side revocation timestamps.
3. **`emr_security_events`**: Audit trail for authentication events (login success, failure, lockout, logout, session expiration).
4. **`emr_patients` & `emr_encounters`**: Synthetic patient references (`P100234`) and outpatient clinical encounters (`ENC-100567`).
5. **`emr_encounter_assignments`**: Explicit many-to-many relationship enforcing that doctors only view and document encounters assigned to them.
6. **`emr_notes`**: Current consultation note state (`Draft`, `Signed`), current version pointer, optimistic concurrency lock (`expected_version`).
7. **`emr_note_versions`**: Append-only immutable historical versions with SHA-256 source hash and database-level immutability triggers:
   ```sql
   CREATE OR REPLACE FUNCTION emr_prevent_signed_version_mutation()
   RETURNS trigger AS $$
   BEGIN
       IF OLD.signed_at IS NOT NULL THEN
           RAISE EXCEPTION 'Signed clinical note versions are immutable and cannot be updated or deleted.';
       END IF;
       RETURN NEW;
   END;
   $$ LANGUAGE plpgsql;
   ```
8. **`emr_outbox_events`**: Transactional outbox table capturing `ClinicalNoteSigned` events atomically during note signing.
9. **`emr_processed_messages`**: Idempotency ledger preventing duplicate consumption of Service Bus messages.
10. **`emr_extraction_links`**: Durable link between `emr_notes`, `note_version`, and `extraction_runs.extraction_id`.

---

### 3. Application Security & Access Control

1. **Server-Side Cookie Authentication (`ApolloDoctor`):**
   - Cookies are configured with `HttpOnly = true`, `SameSite = Strict`, `Secure = true` (in production), and path `/api/v1/emr`.
   - Session validation is performed against `emr_sessions` in PostgreSQL on every request.
   - Sessions expire after 30 minutes of inactivity; sliding window extends active sessions up to 8 hours maximum.
2. **Double-Submit CSRF Protection:**
   - Evaluated via ASP.NET Core `IAntiforgery` tokens.
   - Endpoint `/api/v1/emr/auth/csrf` issues encrypted antiforgery cookies and response tokens.
   - All mutating requests (`POST`, `PUT`, `DELETE`) require header `X-XSRF-TOKEN` matching cookie `Apollo.Antiforgery`.
3. **Rate Limiting & Lockout Guard:**
   - 5 failed login attempts within 15 minutes trigger an automatic 15-minute account lockout.
   - Mutating note updates are rate-limited to prevent automated flood attacks.
4. **Encounter Authorization Boundary:**
   - Doctors can only query or update encounters where their `doctor_id` is present in `emr_encounter_assignments`. Unauthorized attempts return HTTP 403 Forbidden.
   - Doctors can only access extraction results deep-linked to their assigned encounters.

---

### 4. Messaging & Asynchronous Extraction Pipeline

1. **Transactional Outbox Dispatcher (`EmrMessagingService`):**
   - Background service polls `emr_outbox_events` with row-level locks (`FOR UPDATE SKIP LOCKED`).
   - Claims pending events in batches of 10.
   - Dispatches via `INoteBroker` adapter. On successful dispatch, status transitions to `Published` and `processing_status = 'Queued'`.
   - On broker outage, events transition to `Failed` with exponential retry backoff without blocking the physician or losing notes.
2. **Pluggable Broker Adapters:**
   - `AzureNoteBroker`: Uses official `Azure.Messaging.ServiceBus` SDK with `TokenCredential` / Azure Managed Identity.
   - `SimulatedNoteBroker`: In-memory channel for local development and test automation.
   - `DisabledNoteBroker`: Completely disables outbound dispatch; verifies outage resilience.
3. **Extraction Worker & Consumer (`EmrConsumer`):**
   - Acquires PostgreSQL advisory transaction lock on `note_id`.
   - Validates event payload against schema v1 and computes SHA-256 hash of `emr_note_versions.source_text`. If hash mismatches, rejects as poisoned message.
   - Invokes `ExtractionWorkflow.ExtractAndSaveAsync` passing `message.EventId` as durable `extraction_id`.
   - Records event in `emr_processed_messages` and creates link in `emr_extraction_links`.
   - Status updates atomically to `Completed` or `Needs Review`.

---

### 5. Comprehensive Verification Results

#### 5.1 Stage 9 Backend Integration Tests (`Stage9EmrTests.cs`)
Executed against isolated PostgreSQL test database `cx_audit_test`:

| Test # | Test Name | Target Behavior | Result |
| :--- | :--- | :--- | :--- |
| **Test 1** | `Test1_DoctorLogin_SucceedsWithValidCredentials...` | Valid login, invalid password rejection, lockout after 5 failures | **PASS** |
| **Test 2** | `Test2_Authorization_DoctorACannotAccessDoctorB...` | Cross-doctor encounter isolation and HTTP 403 enforcement | **PASS** |
| **Test 3** | `Test3_Dashboard_AssignedEncountersLoadFromPostgres...` | Assigned encounters query with correct note and extraction status | **PASS** |
| **Test 4** | `Test4_Draft_CreateAndPersistDraft_NoOutboxEvent...` | Draft creation, optimistic version increment, zero outbox publish | **PASS** |
| **Test 5** | `Test5_Sign_CreatesImmutableSignedVersion_AndAtomically...` | Atomic signing, version freeze, outbox event creation | **PASS** |
| **Test 6** | `Test6_BrokerOutage_DisabledBroker_LeavesNoteSigned...` | Broker failure tolerance; note remains signed and outbox pending | **PASS** |
| **Test 7** | `Test7_Publish_SimulatedBroker_PublisherClaims...` | Outbox claim loop, message dispatch, status marked `Published` | **PASS** |
| **Test 8** | `Test8_Consume_WorkerProcessesSignedNote_ValidatesHash...` | Worker consumption, hash check, AI extraction, extraction link | **PASS** |
| **Test 9** | `Test9_DuplicateDelivery_IsIdempotent_DoesNotDuplicate...` | Idempotent replay; identical message processed without duplication | **PASS** |
| **Test 10** | `Test10_AiRouting_MicrosoftFoundryIsPrimary_Fallback...` | Primary Foundry routing preserved; ONNX restricted to fallback | **PASS** |

**Summary:** 10 passed, 0 failed (Duration: 17s).

#### 5.2 Full Backend Regression Suite
Executed across entire test project `Apollo.Backend.Tests.csproj`:
- **Passed:** 207 tests
- **Failed:** 0 tests
- **Skipped:** 11 tests (strictly billable Foundry tests and sealed benchmark evaluation gates)
- **Duration:** 24s

#### 5.3 Angular Frontend Unit & Integration Suite (`emr.spec.ts` & `app.spec.ts`)
- **Passed:** 42 tests (36 Stage 8 app tests + 6 Stage 9 EMR tests)
- **Failed:** 0 tests
- **Duration:** 2.51s
- **Production Bundle:** `ng build` completed successfully (313.06 kB initial bundle).

#### 5.4 Playwright Browser Automation Suite (`stage9-e2e.mjs`)
- **Total Scenarios:** 5/5 passed.
- **Page Errors:** 0 unhandled exceptions or console errors.
- **Screenshots Generated:**
  - `docs/stage9-evidence/playwright/screenshots/01-doctor-login.png`
  - `docs/stage9-evidence/playwright/screenshots/02-doctor-dashboard.png`
  - `docs/stage9-evidence/playwright/screenshots/03-clinical-note-editor.png`
  - `docs/stage9-evidence/playwright/screenshots/04-extraction-status.png`
  - `docs/stage9-evidence/playwright/screenshots/05-extraction-results.png`
- **Machine-Readable Summary:** `docs/stage9-evidence/playwright/stage9-browser-results.json`

---

### 6. Application Database & Historical Evidence Preservation

To guarantee zero corruption or leakage into the primary clinical database, all test fixtures operated strictly within isolated schemas (`cx_audit_test`). A direct verification query was executed against the primary database `cx_platform`:

```sql
SELECT 'extraction_runs' as tbl, count(*) as cnt, md5(string_agg(extraction_id::text, ',' order by extraction_id)) as hash FROM extraction_runs
UNION ALL
SELECT 'extracted_medications' as tbl, count(*) as cnt, md5(string_agg(medication_id::text, ',' order by medication_id)) as hash FROM extracted_medications
UNION ALL
SELECT 'extracted_investigations' as tbl, count(*) as cnt, md5(string_agg(investigation_id::text, ',' order by investigation_id)) as hash FROM extracted_investigations
UNION ALL
SELECT 'entity_review_audit' as tbl, count(*) as cnt, md5(string_agg(review_id::text, ',' order by review_id)) as hash FROM entity_review_audit;
```

#### Results:

| Table | Stage 8 Baseline Count | Stage 9 Current Count | MD5 Fingerprint | Status |
| :--- | :--- | :--- | :--- | :--- |
| `extraction_runs` | 44 | **44** | `b308871f396984ee4635f2025747c0f4` | **100% Preserved** |
| `extracted_medications` | 101 | **101** | `836e32e22b52010e7f6eb265559b7d56` | **100% Preserved** |
| `extracted_investigations` | 136 | **136** | `eb19eed4a30a6bb651a780f4d5e8f8bb` | **100% Preserved** |
| `entity_review_audit` | 12 | **12** | `c10e693eee64de6163e2a11552172ace` | **100% Preserved** |

All 313 historical documentation and evidence files under `docs/` remain intact and uncorrupted.

---

### 7. Governance, Spending & Disclaimers

1. **Zero Billable Microsoft Foundry Calls:** All automated backend, frontend, and browser tests utilized isolated fixtures, local test brokers, and mocked inference pipelines. Zero live API calls were made to Microsoft Foundry during Stage 9, preserving all live testing credit gates established in Stage 8.1.
2. **Synthetic Data Policy:** All patient references (`P100234`–`P100237`), encounter references (`ENC-100567`–`ENC-100570`), and clinical note texts are synthetic demonstrations. No Protected Health Information (PHI) or real patient records were ingested or processed.
3. **Non-Production Disclaimer:** This software represents an engineering evaluation and demonstration platform. It has not undergone formal clinical safety certification (such as CE-mark as a Medical Device or FDA 510(k) clearance) and is not authorized for autonomous clinical decision-making or direct patient care in a hospital setting.
