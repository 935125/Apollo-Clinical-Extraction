# Apollo Stage 13 — Regression & Verification Test Results

**Date:** 25 November 2026  
**Execution Policy:** **100% Zero Paid Foundry Calls** (Backend running in safe `LocalOnnx` / `authorized_offline` mode; all Playwright & Postman checks strictly read-only)  
**Machine-Readable E2E Evidence:** [stage13-e2e-results.json](file:///d:/AI%20Extraction/docs/stage13-evidence/stage13-e2e-results.json)

---

## 1. Master Verification Matrix

| Suite / Check | Classification | Result | Details |
| --- | --- | --- | --- |
| **Backend Release Build** (`dotnet build backend/Apollo.Backend.csproj -c Release`) | **PASS** | `0 warnings, 0 errors` | Compiled cleanly with Stage 13 SQL, API, and OpenAPI changes. |
| **Backend Automated Tests (Stage 12 + Stage 12.1 + Stage 13)** | **PASS** | **`6 / 6` passed** (`2.89 s`) | Includes `Stage13Top10QueueAndForensicsTests` (Top-10 SQL/pagination/failed-event test + Case 4 offline forensic validator test). |
| **Frontend Production Build** (`npm run build`) | **PASS** | `0 warnings, 0 errors` | Initial gzipped bundle: `82.66 kB`. |
| **Frontend Vitest Unit & Component Suite** (`npx ng test --watch=false`) | **PASS** | **`52 / 52` passed** (`3 / 3` suites) | Includes new Stage 13 repeated-UHID Top-10 & failed-entry Vitest spec in [clinical-queue.spec.ts](file:///d:/AI%20Extraction/frontend/src/app/queue/clinical-queue.spec.ts). |
| **Playwright Read-Only E2E (`stage13-top10-queue-e2e.mjs`) — Test A & B: Top 10 Individual Submissions** | **PASS** | **`10` rows rendered** | `P100234=4, P100235=3, P100237=3` (all 10 live Foundry cases in exact descending order). |
| **Playwright E2E — Repeated UHID Filter (`P100234`)** | **PASS** | **`4` rows shown** | `v27`, `v25`, `v23 (Failed)`, `v21` cleanly separated with distinct `ENC-100567 · vXX` subtitles. |
| **Playwright E2E — Test D: Same-Patient Version Isolation** | **PASS** | **Isolated** | Switching `P100234 v21 → v25 → v27` rendered distinct source text and version-exact extractions with zero cross-version bleed. |
| **Playwright E2E — Test E: Cross-Patient Isolation & Delayed Response Race Guard** | **PASS** | **Isolated** | 900 ms delayed response for `P100237 v21` was discarded after switching to `P100235 v16`; zero cross-patient bleed. |
| **Playwright E2E — Test F: Failed Case 4 (`P100234 v23`) Truthful Display** | **PASS** | **Truthful & Safe** | Rendered exact `v23` Warfarin/INR source note + `Extraction failed` + `AZURE_INVALID_EVIDENCE` with **0 fabricated medication/investigation tables**. |
| **Playwright E2E — Test C: Live Read-Only 4 s Polling & Reload Retention** | **PASS** | **`Polls=8->11`, `ReloadRows=10`** | Verified automatic read-only `GET /api/v1/queue?limit=10` polling and full 10-row persistence after browser reload. |
| **Playwright E2E — Responsive Stacked Layout (`768x1024`)** | **PASS** | **`HorizontalOverflow=0px`** | Zero horizontal overflow, clean vertical stacking. |
| **Playwright E2E — Browser Runtime Cleanliness** | **PASS** | **`0` console errors, `0` failed API calls** | Zero `NG0955` duplicate-track warnings or unexpected 4xx/5xx responses. |
| **Postman CLI Read-Only Infrastructure Smoke (`1. Infrastructure`)** | **PASS** | **`5 / 5` assertions passed** (`331 ms`) | Verified `/health`, `/v1/routing`, and `/openapi/v1.json` directly against `http://127.0.0.1:5000` without Angular. |
| **Direct Backend API Top-10 Verification (`GET http://127.0.0.1:5000/api/v1/queue?limit=10`)** | **PASS** | **`Total=28, Returned=10`** | Exact match to PostgreSQL `emr_outbox_events` and all 10 live Foundry cases. |
| **Paid Microsoft Foundry Inference Calls During Stage 13** | **PASS (ZERO CALLS)** | **`0` new billable calls** | Verified via `GET /v1/routing` (`LocalOnnx`, `authorized_offline`) and full `task-4079.log` inspection. |
| **Full Write-Enabled Postman Mutation Suite (`2. Auth` – `6. Ingestion`)** | **NOT TESTED (INTENTIONALLY SKIPPED)** | *Skipped per zero-mutation / zero-paid-call rule* | Stage 13 strictly prohibited submitting new notes or triggering new inference; write-enabled Postman verification (`41/41 PASS`) remains intact from prior Stage 12.2 evidence. |

---

## 2. New Automated Tests Added in Stage 13

1. **[Stage13Top10QueueAndForensicsTests.cs](file:///d:/AI%20Extraction/backend/tests/Stage13Top10QueueAndForensicsTests.cs):**
   - `QueueApi_ReturnsTop10IndividualSubmissionEvents_RetainsRepeatedPatientsAndFailedEvents_AndPaginates` — Seeds 12 distinct `emr_outbox_events` across 3 patients plus 1 failed `AZURE_INVALID_EVIDENCE` event; asserts default `GET /api/v1/queue` returns 10 items, `total == 12`, repeated UHIDs are preserved, failed event is included with `extractionId == null`, and `offset=10` returns the remaining 2 events.
   - `Case4_OfflineForensicRegression_ValidCandidatePasses_WhileUngroundedOrMisclassifiedEvidenceIsStrictlyRejected` — Offline proof that Case 4's anticoagulation note passes strict clinical validation when quotes and historical scopes are valid, while paraphrased quotes and ungrounded `historical`/`final` claims strictly throw `InvalidClinicalEvidenceException`.
2. **[clinical-queue.spec.ts](file:///d:/AI%20Extraction/frontend/src/app/queue/clinical-queue.spec.ts):**
   - Added Stage 13 Top-10 repeated-UHID + failed entry test asserting distinct DOM row rendering (`eventId` tracking), `ENC-100567 · vXX` subtitle differentiation, and truthful failed-state display without fabricated tables.
3. **[stage13-top10-queue-e2e.mjs](file:///d:/AI%20Extraction/frontend/e2e/stage13-top10-queue-e2e.mjs):**
   - 100% read-only Playwright E2E harness asserting all 8 Stage 13 behavioral gates and capturing the 7 audited screenshots.

---

## 3. Visually Audited Screenshot Evidence (`docs/stage13-evidence/screenshots/`)

All 7 screenshots were inspected directly for layout fidelity, data accuracy, and minimal Apollo aesthetic:

1. [01-top10-queue-entries.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/01-top10-queue-entries.png) — Top 10 individual submissions visible (`P100234 v27` selected by default; 10 chronological rows in left rail; clean Apollo green/ivory UI).
2. [02-repeated-uhid-versions.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/02-repeated-uhid-versions.png) — UHID filter `P100234` showing all 4 versions (`v27`, `v25`, `v23` with red failed indicator dot, `v21`) cleanly separated.
3. [03-P100234-v27-source-results.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/03-P100234-v27-source-results.png) — `P100234 v27` Gout Flare original clinical note alongside `Allopurinol` (`Historical`), `Colchicine`, `Etoricoxib`, and 3 lab investigations (`Serum Uric Acid`, `Serum Creatinine`, `C-Reactive Protein`).
4. [04-P100237-v21-distinct-patient.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/04-P100237-v21-distinct-patient.png) — Distinct patient `P100237 v21` Stable Angina & Cardiology Workup isolated cleanly (`Rosuvastatin`, `Isosorbide Mononitrate`, `Clopidogrel`, `12-lead ECG`, `Echocardiogram`, `Fasting Lipid Profile`).
5. [05-P100234-v23-failed-case4.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/05-P100234-v23-failed-case4.png) — Failed Case 4 (`P100234 v23` Warfarin discontinuation / INR 4.8) displaying exact immutable source note and truthful `Extraction failed` / `AZURE_INVALID_EVIDENCE` alert with zero fabricated tables.
6. [06-queue-after-auto-poll.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/06-queue-after-auto-poll.png) — Stable 10-entry queue after multiple automatic 4-second read-only polling cycles and full browser reload.
7. [07-responsive-layout.png](file:///d:/AI%20Extraction/docs/stage13-evidence/screenshots/07-responsive-layout.png) — Clean responsive stacked layout (`768x1024`, `0px` horizontal overflow).
