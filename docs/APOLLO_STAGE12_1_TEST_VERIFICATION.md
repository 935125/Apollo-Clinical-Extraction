# Apollo Clinical Extraction Platform — Stage 12.1 Test Verification Report

**Date:** 25 November 2026  
**Cost Compliance:** **0** billable Microsoft Foundry API calls executed (`$0.00` spent). Live backend ran with explicit `Extraction__Provider=LocalOnnx`, `Extraction__AllowDirectLocal=true`, `Extraction__DirectLocalReason=authorized_offline`, and real Azure Service Bus (`op-clinical-notes`).

---

## 1. Automated Test Suite Summary (Tests A–G)

| Test Category | Suite / Script | Result | Details |
| --- | --- | --- | --- |
| Backend Release Build | `dotnet build backend/Apollo.Backend.csproj -c Release` | **PASS** | `0 warnings, 0 errors` |
| Backend Isolation, Queue, Foundry & Forensic Suite (Tests C, D, G) | `dotnet test` (`Stage12_1UhidIsolationTests`, `Stage12QueueTests`, `FoundryWorkflowTests`, `Stage8RoutingTests`, `Stage8ConfigurationTests`, `ForensicPipelineAuditTests`) | **PASS** | `77/77 passed, 0 failed` |
| Frontend Unit Tests (Tests A, B, C, F, G) | `npm test -- --watch=false` (`clinical-queue.spec.ts`, `emr.spec.ts`, `app.spec.ts`) | **PASS** | `51/51 passed (3/3 files)` |
| Frontend Production Build | `npm run build` | **PASS** | `0 warnings, 0 errors` (`82.66 kB` gzipped initial, `11.73 kB` `clinical-queue` lazy chunk) |
| Live Playwright E2E (Tests A, B, E, F) | `node frontend/e2e/stage12-1-uhid-queue-e2e.mjs` | **PASS** | `8/8 E2E assertions passed, 0 console errors, 0 unexpected 4xx/5xx` |
| Headless Port 5000 API Verification (No Angular) | Direct HTTP to `http://127.0.0.1:5000` (`/dev-session`, `/api/v1/queue`, `/api/v1/queue/patients/P100234`) | **PASS** | `Doctor=Dr. K. Krishnan, QueueItems=4, P100234Notes=5` |
| Live Paid Foundry Inference | Disabled by zero-cost policy | **NOT TESTED LIVE** | Offline `FoundryWorkflowTests` & `Stage8RoutingTests` all PASS (`0` cloud calls) |

---

## 2. Detailed Verification Matrix

- **Test A (Queue UI Removal):** Confirmed absence of `.filter-pills`, `.filter-pill`, `.queue-count`, `.status-pill`, `Ready · Verification recommended`, `Local ONNX` queue badges, word counts, `Doctor Dashboard`, KPI cards, 5 tabs, sidebar, Validation Warnings panel, Local ONNX limited-coverage paragraph, and review/submit controls in both Vitest and live Playwright.
- **Test B (Cross-Patient Isolation & Rapid Out-of-Order Switching):**
  - Vitest tested 3 distinct synthetic patients (`P100234`, `P100237`, `P100235`) and verified RxJS `switchMap` immediately cancelled `P100234`'s in-flight `HttpClient` request (`reqA.cancelled === true`) and cleared `selectedEntry` synchronously upon switching to `P100237`.
  - Live Playwright intentionally delayed `P100234`'s API response by `900 ms`, rapidly switched to `P100237`, waited `1300 ms`, and confirmed `P100237`'s source note and extractions remained untouched with zero `P100234` leakage.
- **Test C (Multiple Encounters & Versions for One Patient):** `Stage12_1UhidIsolationTests.SinglePatient_MultipleEncountersAndVersions_NeverMixesExtractions_AndPreservesBackendValidation` seeded `P910001` with 2 encounters (`ENC-910001` v2 `Amlodipine`, `ENC-910001` v4 `Metformin`, `ENC-910002` v2 `Atorvastatin`) plus separate `P910002` (`Warfarin`). Verified exact per-version extraction isolation (`detailV2.ExtractionId != detailV4.ExtractionId`), zero mixed medications, and within-patient selector returning strictly `P910001`'s 3 versions.
- **Test D (Backend Authorization):** Verified `404` on cross-doctor `GET /api/v1/queue/{noteB}`, `404` on mismatched `?patient=P910002`, `404` on unauthorized `GET /api/v1/queue/patients/P910002`, and `404`/`403` on cross-doctor `GET /api/v1/extractions/{extB}`.
- **Test E (Headless API Ingestion → Azure Service Bus → Live Polling):** Submitted `STAGE12-1-HEADLESS-638018` for `P100235` via direct HTTP API (without UI submission or page reload); verified PostgreSQL persistence, Azure Service Bus `op-clinical-notes` delivery, `.NET` worker extraction, and automatic Angular queue + detail update (`Polls=6->7`).
- **Test F (Frontend Clinical Result Accuracy):** Verified UI source text and extracted medications/investigations/assertions (`Azithromycin` `New order`, `Warfarin` `Discontinued`, `Amlodipine`/`Atorvastatin`, LOINC candidates) matched live backend API payloads verbatim.

---

## 3. Audited Screenshot Evidence (`docs/stage12-evidence/uhid-queue/`)

| # | File | Verified Content |
| --- | --- | --- |
| 1 | [`01-clean-queue-overview.png`](stage12-evidence/uhid-queue/01-clean-queue-overview.png) | Minimal queue overview (`UHID / Patient ID | Clinical Note Preview | Received | Action`) with zero filter boxes, status badges, ONNX chips, or word counts |
| 2 | [`02-selected-uhid-P100234.png`](stage12-evidence/uhid-queue/02-selected-uhid-P100234.png) | Patient-scoped `P100234` (`ENC-100567 · Version 11`) with zero unrelated patient IDs, zero Validation Warnings, and zero ONNX banner |
| 3 | [`03-selected-uhid-P100237.png`](stage12-evidence/uhid-queue/03-selected-uhid-P100237.png) | Patient-scoped `P100237` (`ENC-100570 · Version 15`) with all `P100234` data cleared and only `P100237`'s note + 4 medications + 2 investigations |
| 4 | [`04-side-by-side-source-results.png`](stage12-evidence/uhid-queue/04-side-by-side-source-results.png) | Deep-green Source Note (with highlighted verbatim quote) beside white Extracted Results + lightweight source quotation callout |
| 5 | [`05-processing-state.png`](stage12-evidence/uhid-queue/05-processing-state.png) | Selected `P100236` (`ENC-100569 · Version 1`) displaying stored source note alongside `"Extraction in progress"` |
| 6 | [`06-completed-extraction-state.png`](stage12-evidence/uhid-queue/06-completed-extraction-state.png) | Headlessly submitted `P100235` (`ENC-100568 · Version 10`) auto-updated to completed (`Azithromycin New order`, `Warfarin Discontinued`, `INR`, `Chest X-ray PA view`) |
| 7 | [`07-responsive-layout.png`](stage12-evidence/uhid-queue/07-responsive-layout.png) | Stacked `768x1024` layout with `0px` horizontal overflow |

---

## 4. PostgreSQL Historical Data Preservation (`cx_platform`)

- `extraction_runs`: **57** (strictly additive)
- `extracted_medications`: **130**
- `extracted_investigations`: **158**
- `entity_review_audit`: **12** (100% preserved)
- `emr_notes`: **4**
- `emr_note_versions`: **37**
