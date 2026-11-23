# APOLLO CLINICAL EXTRACTION PLATFORM
## STAGE 5 — END-TO-END APPLICATION VERIFICATION, CLINICAL REVIEW WORKFLOW & INDEPENDENT QUALITY ASSURANCE

- **Report Path:** `docs/APOLLO_STAGE5_END_TO_END_RELEASE_VERIFICATION.md`
- **Audit Date:** 2026-11-23
- **Project Location:** `D:\AI Extraction`
- **Persisted Reference Extraction Verified:** `1713c76e-b355-4be3-b02f-e9d581b82415` (`correlationId = live-final-86985b4aaa88`)
- **New Billable Microsoft Foundry Calls Executed During Stage 5:** **`0`** (strictly zero; all new live Foundry inference tests remain skipped behind environment gates).

---

## 1. Executive Summary

Stage 5 transitioned the Apollo Clinical Extraction Platform from a verified backend extraction engine into an end-to-end, browser-verified, clinician-reviewable standalone web application:

1. **Real Browser End-to-End Verification (`PASS`):** Executed **Playwright Chromium `1.65.1`** against the live Angular 21 app (`http://127.0.0.1:4200`), ASP.NET Core .NET 10 backend (`http://127.0.0.1:5000`), and PostgreSQL 18.7 (`127.0.0.1:5432`). All **`7/7` E2E browser scenarios passed** (`0` console errors, `0` page errors, `0` live Foundry calls) with **8 real full-page browser screenshots** archived in `backend/artifacts/stage5-release-verification/screenshots/`.
2. **Non-Destructive Clinical Review Workflow (`PASS`):** Implemented end-to-end entity-level review across PostgreSQL (`entity_review_audit`), ASP.NET Core (`GET/POST /api/v1/extractions/{id}/reviews`), and Angular 21 (`ACCEPTED`, `CORRECTED`, `REJECTED`, `NEEDS_CLARIFICATION`, `PENDING_REVIEW`). Every decision preserves the immutable original AI candidate, original evidence span, reviewer identity (explicitly labeled `Unauthenticated Session (Local Demonstration Mode)` because user authentication is not yet configured), timestamp, comment, and strictly monotonic version number. Verified persistence across complete browser reloads (`page.reload()`).
3. **API $\rightarrow$ PostgreSQL $\rightarrow$ UI Parity (`PASS`):** Confirmed `GET /api/v1/extractions/1713c76e-b355-4be3-b02f-e9d581b82415` renders all **5 active discharge medications** separately from **2 documented allergies** and all **8 planned investigations** separately from **2 historical laboratory results**, with exact source-span highlighting, RxNorm/LOINC semantic statuses, entity origin labels, and all **7 temporal safety alerts**.
4. **Frozen 35-Case Independent Synthetic Benchmark (`PRELIMINARY ENGINEERING EVALUATION`):** Created and froze a brand-new 35-case synthetic evaluation dataset spanning 8 medical specialties ([stage5_independent_benchmark_35cases.json](file:///d:/AI%20Extraction/backend/tests/Evaluation/stage5_independent_benchmark_35cases.json), SHA-256: `3D2EE6AFD0AC72157C92BDDEEDC43F94511BA170D7985A1F44177CB78EB9AD84`). Evaluated strictly on **`LocalOnnx` (`bc5cdr_int8` + deterministic rules)** with **`0` live Foundry calls**; **GPT-6.1 Sol was NOT evaluated** on these 35 unseen notes because new billable calls were prohibited. Measured results expose concrete `LocalOnnx` gaps honestly (e.g., `70.5%` medication recall on OOV drugs, `20.0%` investigation timing accuracy, `0.0%` investigation specimen/category population in the ONNX-only path).
5. **Regression & Security Verification (`PASS` with explicit production-readiness caveats):** Backend: **`148 passed, 0 failed, 10 skipped`** (`6 s`); Angular Vitest: **`34 passed, 0 failed`** (`1.94 s`); Angular production build: **`PASS`** (`358.64 kB` initial bundle); Playwright Chromium E2E: **`7/7 passed`**.

---

## 2. Current Verified Environment

Verified directly against the live workstation processes and CLI tools:

| Component | Measured Runtime / Version | Local Endpoint / Process State |
| :--- | :--- | :--- |
| **.NET SDK & ASP.NET Core** | `.NET 10.0.403` (`Apollo.Backend` v2.1.0-dotnet) | `http://127.0.0.1:5000` (and `:8000`) — `GET /v1/health` `200 OK` |
| **Angular Frontend** | `Angular 21.2.30` (`@angular/core 21.2.0`, `Vitest 4.1.12`) | `http://127.0.0.1:4200` (dev proxy `/api`, `/v1`, `/predict`, `/health` $\rightarrow$ `http://127.0.0.1:5000`) |
| **Node.js / npm** | `Node v24.12.0` / `npm 11.6.2` | Active (`ng serve` + Playwright runner) |
| **Browser Automation** | `Playwright 1.65.1` (Chromium headless) | Verified live Chromium launch and full DOM/network verification |
| **PostgreSQL & pgvector** | `PostgreSQL 18.7` (`pgvector v0.8.7`) | `127.0.0.1:5432` — `cx_platform` (app DB, non-destructive) & `cx_audit_test` (isolated test schemas) |
| **Primary / Fallback AI Config** | `Extraction:Provider = MicrosoftFoundry` (`gpt-6.1-sol`), `FallbackProvider = LocalOnnx` (`bc5cdr_int8`) | Verified in [appsettings.Development.json](file:///d:/AI%20Extraction/backend/appsettings.Development.json) |
| **Authentication / Authorization** | **Not yet implemented** (no JWT/OIDC clinician identity middleware configured) | Explicitly disclosed in API (`authMode = "LOCAL_DEMO_MODE"`) and UI (`Unauthenticated Session · Local Demonstration Mode`) |

---

## 3. Code Changes Implemented in Stage 5

### Backend (ASP.NET Core & PostgreSQL)
- [Contracts.cs](file:///d:/AI%20Extraction/backend/Models/Contracts.cs#L172-L216): Added `EntityReviews` to `ClinicalExtractionResponse` plus `EntityReviewSubmissionRequest` and `EntityReviewRecordDto` contracts.
- [IExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/IExtractionRepository.cs#L13-L16) & [InMemoryExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/InMemoryExtractionRepository.cs#L140-L260): Added `AddEntityReviewAsync`, `ListEntityReviewsAsync`, and immutable `BuildEntitySnapshot` helper.
- [SqlExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/SqlExtractionRepository.cs#L145-L165): Added non-destructive `entity_review_audit` table DDL (`FOREIGN KEY` to `extraction_runs(extraction_id) ON DELETE CASCADE`), serializable version-increment insert transaction (`IsolationLevel.Serializable`), and automatic read-joining in `LoadSingleExtractionFromDbAsync`. Never mutates `extracted_medications` or `extracted_investigations`.
- [Program.cs](file:///d:/AI%20Extraction/backend/Program.cs#L260-L420): Added `GET /api/v1/extractions/{id}/reviews` and `POST /api/v1/extractions/{id}/reviews` with strict validation:
  - Enforces allowed review states (`PENDING_REVIEW`, `ACCEPTED`, `CORRECTED`, `REJECTED`, `NEEDS_CLARIFICATION`).
  - Requires a non-empty `reviewComment` for `REJECTED` and `NEEDS_CLARIFICATION` (`HTTP 400 REVIEW_COMMENT_REQUIRED`).
  - Validates corrected entity `name`, RxNorm numeric format (`^\d{2,10}$`, `INVALID_RXNORM_CODE`), and LOINC format (`^\d{1,7}-\d$`, `INVALID_LOINC_CODE`).
  - Stamps `reviewerIdentity` from `HttpContext.User` if authenticated, or `"Unauthenticated Session (Local Demonstration Mode)"` (`authMode = "LOCAL_DEMO_MODE"`) otherwise.

### Frontend (Angular 21)
- [extraction.model.ts](file:///d:/AI%20Extraction/frontend/src/app/models/extraction.model.ts#L173-L208) & [extraction.service.ts](file:///d:/AI%20Extraction/frontend/src/app/services/extraction.service.ts#L30-L108): Added entity review models, `listEntityReviews`, `submitEntityReview`, and hardened `handleHttpError` so malformed JSON / Angular `HttpClient` parse failures (`HttpErrorResponse` with `{ error: SyntaxError, text }`) return an actionable `"Malformed server response"` notice instead of leaking raw JavaScript `SyntaxError` messages to the UI.
- [app.ts](file:///d:/AI%20Extraction/frontend/src/app/app.ts), [app.html](file:///d:/AI%20Extraction/frontend/src/app/app.html), and [app.scss](file:///d:/AI%20Extraction/frontend/src/app/app.scss):
  - Added `?extractionId=<uuid>` deep-link retrieval via `openExtractionById` (`GET /api/v1/extractions/{id}` without triggering any AI call).
  - Added explicit clinical category counters in section headers (`5 active prescriptions · 2 documented allergies` and `8 planned investigations · 2 historical lab results`) and `data-assertion` attributes so allergies are never counted as active discharge prescriptions and historical lab values are never counted as newly ordered investigations.
  - Built the inline **Clinical Review Workflow** panel for both Medications (10 editable fields: name, strength, administered dose, formulation, route, frequency, duration, action, assertion status, RxNorm code) and Investigations (7 editable fields: name, timing, specimen, method, assertion status, LOINC code, clinical category), with audit history timeline, reviewer correction summary, and explicit `Local Demonstration Mode` badge.

### Automated Verification & Evaluation
- [stage5-e2e.mjs](file:///d:/AI%20Extraction/frontend/e2e/stage5-e2e.mjs): Playwright Chromium end-to-end suite verifying all 6 required scenarios plus Phase C/D review persistence across browser reloads.
- [FoundryPostgresTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryPostgresTests.cs#L138-L255): Added PostgreSQL integration tests for entity review validation, version history, original-candidate immutability, and concurrent serializable review submissions.
- [app.spec.ts](file:///d:/AI%20Extraction/frontend/src/app/app.spec.ts#L718-L765): Added unit test for entity review workflow validation and non-destructive correction rendering.
- [stage5_independent_benchmark_35cases.json](file:///d:/AI%20Extraction/backend/tests/Evaluation/stage5_independent_benchmark_35cases.json) & [Stage5IndependentBenchmarkTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/Stage5IndependentBenchmarkTests.cs): Frozen 35-case independent synthetic benchmark and evaluator.

---

## 4. Browser Testing Results (Playwright Chromium `1.65.1`)

- **Command Executed:** `Set-Location "d:\AI Extraction\frontend"; node e2e/stage5-e2e.mjs`
- **Machine-Readable Report:** [playwright-e2e-verification.json](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/playwright-e2e-verification.json)
- **Live Foundry Calls Made:** **`0`** | **Console Errors:** **`0`** | **Page Errors:** **`0`**

| Scenario | Status | Duration | Verified Behavior |
| :--- | :---: | :---: | :--- |
| **Scenario 1 — Application startup & backend health** | **PASS** | `752.54 ms` | Loaded `http://127.0.0.1:4200` (`661.23 ms`), verified `/v1/health` `200 OK`, `/api/v1/extractions?limit=50` `200 OK`, zero offline banners, zero console/page errors, zero background Foundry calls. |
| **Scenario 2 — Clinical note entry, validation & duplicate submission control** | **PASS** | `252.44 ms` | Verified `Extract` disabled on empty and whitespace-only input, enabled on synthetic note input, disabled during in-flight extraction with loading spinner visible, exactly `1` POST request dispatched (mocked response). |
| **Scenario 3 — PDF upload multipart & corrupted PDF handling** | **PASS** | `160.57 ms` | Uploaded [Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf](file:///d:/AI%20Extraction/backend/tests/Evaluation/Fixtures/Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf), verified `multipart/form-data` header & PDF label; uploaded corrupted PDF directly to real ASP.NET Core backend and verified `HTTP 400 INVALID_PDF` actionable alert (`"Invalid PDF — The uploaded file could not be read as a PDF."`) with zero browser crash. |
| **Scenario 4 — Display saved extraction (`1713c76e-b355-4be3-b02f-e9d581b82415`)** | **PASS** | `1,126.07 ms` | Opened `/?extractionId=1713c76e-b355-4be3-b02f-e9d581b82415` (`626.46 ms` render) directly from PostgreSQL; verified `5 active prescriptions`, `2 documented allergies`, `8 planned investigations`, `2 historical lab results`, Azithromycin `for 3 days` / `RxNorm: 18631 (exact)` / highlighted evidence span, Details tab `6 warnings · 0 errors`, all 7 safety alerts, and model token provenance (`1255 / 2305`, total `3560`). |
| **Scenario 5 — Provider & provenance distinction** | **PASS** | `398.74 ms` | Verified `Microsoft Foundry — gpt-6.1-sol-2026-09-29`, distinguished `Penicillin` (`LLM EXTRACTED`, `Unresolved`) from `Azithromycin` (`LLM AND RULE CONFIRMED`) and `chest X-ray` (`code withheld (narrower vs 24648-8)`), then opened historical ONNX fallback run (`639ebd21-ece0-4180-b2cd-7eda82f0e820`) via History tab and verified `Local ONNX` banner and `"LocalOnnx has reduced coverage"` notice. |
| **Phase C & D — Clinical review workflow & refresh persistence** | **PASS** | `1,871.50 ms` | Tested validation (Reject without comment blocked inline), corrected Warfarin (`dose = "1 tablet"`, `formulation = "tablet"`, saved to PostgreSQL), marked `Complete blood count` as `NEEDS_CLARIFICATION` with comment, executed full `page.reload()`, and verified both decisions and versioned audit history persisted from PostgreSQL while AI original Warfarin dose remained `Not documented`. |
| **Scenario 6 — Failure handling & recovery** | **PASS** | `1,103.16 ms` | Verified `HTTP 400`, `HTTP 422`, `HTTP 500`, `HTTP 503 PERSISTENCE_FAILED`, `MALFORMED_JSON_RESPONSE` (`"Malformed server response"` — caught and fixed a real `SyntaxError` leak defect discovered during our first Playwright run!), and `200 OK` empty extraction (`0` meds / `0` invs). Verified source note was retained across all failures with zero stack traces or secrets exposed. |

---

## 5. Real Browser Verification Screenshots

All 8 full-page PNG screenshots were captured directly by Playwright Chromium `1.65.1` on `2026-11-23`:

| # | Screenshot File | Size | What It Captures |
| :-: | :--- | ---: | :--- |
| 1 | [01-workspace-startup.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/01-workspace-startup.png) | `58,077 B` | Clean workspace startup in edit mode, backend healthy, no console errors |
| 2 | [02-clinical-note-entry-extracted.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/02-clinical-note-entry-extracted.png) | `161,581 B` | Synthetic note submission & structured extraction display |
| 3 | [03-corrupted-pdf-error.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/03-corrupted-pdf-error.png) | `53,796 B` | Real ASP.NET Core `HTTP 400 INVALID_PDF` actionable error banner |
| 4 | [04-saved-extraction-structured.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/04-saved-extraction-structured.png) | `284,749 B` | Saved extraction `1713c76e-...` showing `5 active prescriptions · 2 documented allergies`, `8 planned investigations · 2 historical lab results`, Azithromycin expanded with `RxNorm: 18631 (exact)` and highlighted source quote |
| 5 | [05-saved-extraction-details-alerts.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/05-saved-extraction-details-alerts.png) | `450,575 B` | Details tab showing `6 warnings · 0 errors`, all 7 safety alerts with temporality tags, and expanded Model information (`gpt-6.1-sol-2026-09-29`, `3560` tokens) |
| 6 | [06-provider-provenance-onnx-vs-foundry.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/06-provider-provenance-onnx-vs-foundry.png) | `456,375 B` | Historical ONNX fallback extraction displaying `Local ONNX — Fallback` and `"LocalOnnx has reduced coverage"` warning |
| 7 | [07-clinical-review-workflow-saved.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/07-clinical-review-workflow-saved.png) | `298,824 B` | Clinical Review panel showing persisted `CORRECTED` & `NEEDS_CLARIFICATION` statuses, non-destructive corrected summary, `Local Demonstration Mode` badge, and versioned audit trail |
| 8 | [08-empty-and-failure-handling.png](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/screenshots/08-empty-and-failure-handling.png) | `64,711 B` | Graceful empty extraction view after completing all HTTP 400/422/500/503/malformed-JSON failure checks |

---

## 6. Clinical Extraction UI Verification & 7. API-to-Database Parity

Verified against live `GET http://127.0.0.1:5000/api/v1/extractions/1713c76e-b355-4be3-b02f-e9d581b82415` and PostgreSQL `cx_platform`:

- **Medications (`7` total rows, strictly separated by clinical assertion):**
  - **5 Active Discharge Medications (`assertionStatus = current_medication`):** `Warfarin 5 mg`, `Aspirin 81 mg`, `Azithromycin 500 mg` (`for 3 days`), `Ondansetron 4 mg` (`every 8 hours as needed`), `Pantoprazole 40 mg` (`for 30 days`).
  - **2 Documented Allergies (`assertionStatus = allergy`):** `Penicillin` (`anaphylaxis`), `Ibuprofen` (`bronchospasm`). Never counted as active discharge prescriptions.
- **Investigations (`10` total rows, strictly separated by clinical assertion):**
  - **8 Planned Outpatient Follow-Up Investigations (`assertionStatus = new_order`):** `Prothrombin time and INR` (`in 3 days`), `serum potassium` (`in 48 hours`), `serum sodium` (`in 48 hours`), `serum creatinine` (`in 48 hours`), `Complete blood count` (`at 1-week follow-up`), `C-reactive protein` (`at 1-week follow-up`), `chest X-ray` (`in 2 weeks`), `12-lead ECG` (`in 2 weeks`).
  - **2 Historical Baseline Lab Results (`assertionStatus = historical`):** `K+` (`resultValue = "5.7 mEq/L"`), `Creatinine` (`resultValue = "1.9 mg/dL"`). Never counted as newly ordered investigations.
- **Database Parity & Immutability Check:** After submitting multiple review decisions on `1713c76e-b355-4be3-b02f-e9d581b82415`, live API inspection confirmed:
  - `OriginalWarfarinDose`: `null` (original AI candidate preserved untouched in `extracted_medications`).
  - `LatestWarfarinReviewStatus`: `"CORRECTED"` (`dose = "1 tablet"`, `formulation = "tablet"` stored exclusively in `entity_review_audit`).
  - `ApiGetLatencyMs`: `146.37 ms` (including extraction run + child medications + child investigations + `entity_review_audit` join).

---

## 8. Review Workflow Implementation Summary

| Capability | Pre-Stage 5 State | Stage 5 Verified State |
| :--- | :---: | :--- |
| Entity-level acceptance / rejection / needs clarification | Absent | **Implemented & Verified** (`ACCEPTED`, `REJECTED`, `NEEDS_CLARIFICATION`; comment enforced for Reject/Clarify) |
| Editing medication fields (10 attributes incl. RxNorm) | Absent | **Implemented & Verified** (non-destructive overlay; validates RxCUI format) |
| Editing investigation fields (7 attributes incl. LOINC & category) | Absent | **Implemented & Verified** (non-destructive overlay; validates LOINC `^\d{1,7}-\d$` format) |
| Preservation of original AI candidate & original evidence | N/A | **Implemented & Verified** (`original_json` + `evidence_json` snapshot persisted in `entity_review_audit`; source tables never overwritten) |
| Versioned audit history & concurrency control | Absent | **Implemented & Verified** (`IsolationLevel.Serializable` monotonic `version_number`; tested with 5 concurrent requests in `FoundryPostgresTests`) |
| Reviewer identity transparency | Absent | **Implemented & Verified** (explicitly displays `"Unauthenticated Session (Local Demonstration Mode)"` until clinician SSO/OIDC is added) |

---

## 9. Terminology Display Verification & 10. Safety Alert Display Verification

- **Terminology Display in UI:** Confirmed `exact` matches display standard code (`RxNorm: 18631 (exact)`, `LOINC: 2823-3 (exact)`), `narrower`/`broader` concepts explicitly withhold code while surfacing candidate (`Chest X-Ray PA — code withheld (narrower vs 24648-8)`), and `unresolved` drug classes display `Unresolved (unresolved)` (`Penicillin`).
- **Safety Alert Display in UI:** Confirmed all 7 grounded alerts render in the Details tab with severity, title, description, explicit `alertType · temporality` (`INTERACTION_RISK_REVIEW · CURRENT_ACTIVE`, `OBSERVED_FACT_WARNING · CURRENT_ACTIVE`, `DERIVED_CLINICAL_INFERENCE · BASELINE_MEASUREMENT`, `DERIVED_CLINICAL_INFERENCE · HISTORICAL_ADMISSION_COURSE`), and non-prescriptive review recommendation.

---

## 11. Independent Synthetic Evaluation Methodology

- **Dataset:** [stage5_independent_benchmark_35cases.json](file:///d:/AI%20Extraction/backend/tests/Evaluation/stage5_independent_benchmark_35cases.json) — **`35` brand-new synthetic clinical notes** across **8 specialties** (`Cardiology: 5`, `Pulmonology: 4`, `Endocrinology: 4`, `Pediatrics: 4`, `Oncology: 4`, `Nephrology: 4`, `Neurology: 4`, `General Medicine: 6`), covering all 16 clinical challenge patterns (multi-drug sentences, weekly vs daily dosing, PRN instructions, dose titration, pediatric suspension mL dosing, discontinuations, drug allergies, negated mentions, historical prescriptions, conditional orders, shared timing, ambiguous formulations, imaging views, conflicting labs, and OOV concepts like `Pirfenidone` and `High-resolution CT Thorax`).
- **Annotation Schema & Guidelines:** Each case defines `id`, `specialty`, `challenge`, `note_text`, `expected_medications` (`name`, `strength`, `dose`, `formulation`, `route`, `frequency`, `duration`, `assertion_status`), `expected_investigations` (`name`, `timing`, `specimen`, `clinical_category`, `assertion_status`), and `expected_alerts` (`category`, `temporality`). Gold annotations were authored directly from the synthetic source text before execution and **frozen** (SHA-256: `3D2EE6AFD0AC72157C92BDDEEDC43F94511BA170D7985A1F44177CB78EB9AD84`) prior to running the evaluator. Neither gold labels nor pipeline rules were modified after viewing results.
- **Label Provenance Classification:** **Preliminary synthetic engineering evaluation** (annotations are engineering-authored, not independently clinician-reviewed).
- **Evaluated Provider:** **`LocalOnnx` (`bc5cdr_int8` INT8 ONNX + deterministic posology/ontology pipeline)** — generated `100%` of evaluated predictions (`0` live Foundry calls). **Microsoft Foundry (`gpt-6.1-sol`) was NOT evaluated** on these 35 unseen notes because no saved GPT responses exist for them and new billable calls were strictly prohibited.

---

## 12. Measured Benchmark Results (`stage5_independent_benchmark_35cases.json` — `LocalOnnx` Only)

Full machine-readable output: [stage5-independent-benchmark-results.json](file:///d:/AI%20Extraction/backend/artifacts/stage5-release-verification/stage5-independent-benchmark-results.json). Every metric is reported with exact numerator and denominator:

### 12.1 Medication Extraction (`LocalOnnx`, 35 cases, 88 gold medications)
| Metric | Measured Result | Numerator / Denominator |
| :--- | :---: | :---: |
| **Precision** | **`98.4%`** | `62 / 63` (`TP=62, FP=1`) |
| **Recall** | **`70.5%`** | `62 / 88` (`TP=62, FN=26`) |
| **F1** | **`82.1%`** | `2*62 / (2*62 + 1 + 26)` |
| **Strength Accuracy** | **`70.5%`** | `62 / 88` |
| **Administered Dose Accuracy** | **`73.9%`** | `51 / 69` |
| **Route Accuracy** | **`72.2%`** | `57 / 79` |
| **Frequency Accuracy** | **`63.0%`** | `51 / 81` |
| **Duration Accuracy** | **`64.0%`** | `16 / 25` |
| **Formulation Accuracy** | **`0.0%`** | `0 / 3` (`LocalOnnx` does not populate `formulation`) |
| **Assertion Status Accuracy** | **`67.0%`** | `59 / 88` |

### 12.2 Investigation Extraction (`LocalOnnx`, 35 cases, 79 gold investigations)
| Metric | Measured Result | Numerator / Denominator |
| :--- | :---: | :---: |
| **Precision** | **`90.4%`** | `66 / 73` (`TP=66, FP=7`) |
| **Recall** | **`83.5%`** | `66 / 79` (`TP=66, FN=13`) |
| **F1** | **`86.8%`** | `2*66 / (2*66 + 7 + 13)` |
| **Timing Accuracy** | **`20.0%`** | `14 / 70` (`LocalOnnx` regex misses shared/multi-week timings) |
| **Clinical Category Accuracy** | **`0.0%`** | `0 / 79` (`LocalOnnx` leaves `clinicalCategory = null`; populated only in Foundry validator / SQL read-back) |
| **Specimen Accuracy** | **`0.0%`** | `0 / 24` (`LocalOnnx` leaves `specimen = null`) |
| **Assertion Status (Historical vs New Order / Conditional)** | **`75.9%`** | `60 / 79` |

### 12.3 Terminology Normalization (`LocalOnnx` on 63 extracted medications & 73 extracted investigations)
| Metric | RxNorm (`02-Nov-2026`) | LOINC (`2.83`) | Notes |
| :--- | :---: | :---: | :--- |
| **Exact-Code Precision vs. Clinician Gold** | **NOT MEASURED** | **NOT MEASURED** | Gold JSON contains no reference codes |
| **Catalog Validity of Assigned Exact Codes** | **`100.0%` (`60 / 60`)** | **`100.0%` (`32 / 32`)** | Zero invented or unverified codes |
| **Exact Code Assignment Coverage** | **`95.2%` (`60 / 63`)** | **`43.8%` (`32 / 73`)** | Conservative abstention on unqualified lab/imaging mentions |
| **Candidate Concept Coverage** | **`95.2%` (`60 / 63`)** | **`100.0%` (`73 / 73`)** | All 73 extracted investigations resolved to a verified candidate or exact concept |
| **Abstention Count (`matchedCode == null`)** | **`4.8%` (`3 / 63`)** | **`56.2%` (`41 / 73`)** | Includes Penicillin class and unqualified CBC/CRP/ECG/CXR mentions |
| **Explicitly Flagged Ambiguous** | **`0 / 63`** | **`3 / 73`** | Zero false-exact assignments observed |

### 12.4 Clinical Safety & Evidence Grounding (`LocalOnnx`, 35 cases)
| Metric | Measured Result | Numerator / Denominator |
| :--- | :---: | :---: |
| **Entity Evidence Quote Validity** | **`100.0%`** | `136 / 136` |
| **Entity Character Offset Validity** | **`100.0%`** | `136 / 136` |
| **Safety Alert Category & Temporality Recall** | **`100.0%`** | `14 / 14` expected alerts matched |
| **Grounded Emitted Alerts / Unsupported Alert Rate** | **`100.0%` (`15 / 15`) / `0.0%` (`0 / 15`)** | Zero unsupported alerts |
| **Lab Anomaly Measurement Grounding** | **`100.0%`** | `6 / 6` exact character-offset verified |
| **Clinician Severity Agreement** | **NOT AVAILABLE** | No clinical reviewer adjudication performed |

---

## 13. Security & Reliability Checks (Phase G)

| Security / Reliability Control | Verification Result | Evidence & Missing Production Controls |
| :--- | :---: | :--- |
| **Input size & PDF limits** | **PASS** | `64 KiB` (`65,536` chars) text limit (`HTTP 400`), `20 MiB` PDF limit (`HTTP 413`), invalid PDF (`HTTP 400 INVALID_PDF`), scanned PDF (`HTTP 422 OCR_REQUIRED`). |
| **PII / PHI Zero-Leakage Validator** | **PASS** | `9/9` `SecurityTests` pass (blocks Aadhaar, ABHA, PAN, email, phone, age > 89 while allowing clinical eponyms). |
| **No secrets in Git or Angular bundle** | **PASS** | `git ls-files .env` empty; `0` secret/password/API-key matches in tracked `backend/` or `frontend/`; `0` matches in `frontend/dist/cx-review-ui/browser/*.js`. |
| **Safe error handling & no stack-trace leakage** | **PASS** | Verified across `HTTP 400/422/500/503` and malformed JSON in Playwright Scenario 6. |
| **CORS policy, Correlation IDs & Timeouts** | **PASS** | Explicit allowed origins (`http://localhost:4200`, `http://127.0.0.1:4200`), `X-Correlation-ID` exposed header, Foundry `45s` timeout configured. |
| **Review concurrency & transactional atomicity** | **PASS** | `IsolationLevel.Serializable` verified with 5 concurrent review posts (`EntityReviewWorkflow_ConcurrentSubmissions_ProduceStrictlyMonotonicUniqueVersionNumbers`). |
| **Missing Production Security Controls (Disclosed)** | **GAP** | 1. **No user authentication/RBAC (OIDC/Entra ID)** on API endpoints.<br>2. **No API rate limiting** middleware.<br>3. **No TLS termination / HSTS** in local dev Kestrel profile.<br>4. **PostgreSQL connection string** uses local dev password in `.env` rather than Azure Key Vault / Managed Identity.<br>5. **Not certified for HIPAA / DISHA / NABH production healthcare compliance.** |

---

## 14. Operational Performance Observations

| Operation | Measured Latency / Metric | Measurement Context |
| :--- | :---: | :--- |
| **LocalOnnx Extraction (`35` unseen notes)** | Mean `9.85 ms` · p50 `6.18 ms` · p95 `21.29 ms` · Max `117.73 ms` | In-process `bc5cdr_int8` INT8 ONNX + deterministic rules |
| **Live Microsoft Foundry `gpt-6.1-sol` (Prior Authorized Run)** | `41,694.45 ms` (`3,560` tokens: `1,255` prompt + `2,305` completion; `$0.04902 USD`) | Single authorized live call `live-final-86985b4aaa88` (`0` new calls in Stage 5) |
| **API + PostgreSQL Read (`GET /api/v1/extractions/{id}`)** | `146.37 ms` | Live `cx_platform` retrieval including medications, investigations, and `entity_review_audit` join |
| **Angular Initial Page Load (Playwright Chromium)** | `661.23 ms` | Cold navigation to `http://127.0.0.1:4200` until `networkidle` |
| **Saved Extraction Browser Render (`1713c76e-...`)** | `626.46 ms` | Deep-link navigation + API fetch + DOM render of 17 entities & 7 alerts |

---

## 15. Complete Regression Test Results (Phase H)

| # | Test Suite / Command | Passed | Failed | Skipped | Duration |
| :-: | :--- | :---: | :---: | :---: | :---: |
| 1 | **Backend .NET 10 Build + Full xUnit Suite** (`$env:TEST_POSTGRES_CONNECTION="...cx_audit_test..."; dotnet test "d:\AI Extraction\backend\tests\Apollo.Backend.Tests.csproj"`) — covers unit tests, PostgreSQL integration, clinical pipeline regression, safety rules, RxNorm/LOINC normalization, review persistence & concurrency, and 35-case benchmark | **`148`** | **`0`** | **`10`** *(live Foundry gates)* | `6 s` |
| 2 | **Angular 21 Vitest Unit & DOM Suite** (`npm test -- --watch=false`) | **`34`** | **`0`** | **`0`** | `1.94 s` |
| 3 | **Angular 21 Production Build** (`npm run build`) | **PASS** | `0` | `0` | `2.14 s` |
| 4 | **Playwright Chromium Real Browser E2E Suite** (`node e2e/stage5-e2e.mjs`) | **`7`** | **`0`** | **`0`** | `5.67 s` |

---

## 16. Remaining Defects & Honest Limitations

1. **`LocalOnnx` Coverage Gaps Exposed by the 35-Case Frozen Benchmark:**
   - **OOV Medications (`26/88` FNs):** Drugs outside the local ONNX/lexicon vocabulary (`Bisoprolol`, `Furosemide`, `Spironolactone`, `Carvedilol`, `Montelukast`, `Theophylline`, `Enoxaparin`, `Apixaban`, `Glimepiride`, `Sitagliptin`, `Colchicine`, `Febuxostat`, `Dexamethasone`, `Cefixime`, `Levetiracetam`, `Cholecalciferol`, `Filgrastim`, `Tramadol`, `Hydrochlorothiazide`, `Sumatriptan`, `Pregabalin`, `Moxifloxacin`, `Pirfenidone`) are missed in fallback mode (`70.5%` recall).
   - **Investigation Attributes in `LocalOnnx`:** `LocalOnnx` does not populate `clinicalCategory` (`0/79`) or `specimen` (`0/24`) and extracts only `20.0%` (`14/70`) of investigation timings. (Note: `ClinicalResultValidator` and `SqlExtractionRepository` populate `clinicalCategory` for Foundry runs and persisted reloads).
2. **GPT-6.1 Sol Not Evaluated on the 35 New Unseen Cases:** Per strict zero-additional-spend rules, Foundry was not called on the 35 new notes.
3. **No Clinician Authentication / RBAC:** Review actions operate in explicitly labeled `LOCAL_DEMO_MODE` without OIDC/Entra ID user verification.
4. **No Clinician-Adjudicated Gold Standard:** All 35 synthetic notes and gold labels were authored by engineering and require dual-clinician adjudication before clinical validity claims.

---

## 17. Release Readiness Verdict

| Classification | Verdict | Rationale |
| :--- | :---: | :--- |
| **Standalone Engineering Release Candidate** | **PASS (CONDITIONAL)** | Backend builds and runs reliably; Angular 21 renders and passes real Playwright Chromium E2E tests (`7/7`); saved extraction `1713c76e-...` displays exact clinical categorization, provenance, terminology uncertainty, and temporal safety alerts; non-destructive versioned entity review persists across browser reloads; `148/148` backend and `34/34` Angular tests pass with zero high-risk safety defects. |
| **Hospital / Clinical Production Deployment** | **NOT READY (FAIL)** | Blocked until: (1) Microsoft Entra ID / OIDC clinician authentication & RBAC are enforced, (2) a clinician-adjudicated multi-specialty benchmark is executed on live GPT-6.1 Sol, (3) security penetration testing, TLS, rate limiting, and institutional privacy/compliance approval are completed. |

---

## 18. Recommended Next Engineering Milestone (Stage 6)

1. **Clinician Authentication & RBAC:** Integrate Microsoft Entra ID OIDC/JWT authentication in ASP.NET Core and Angular so `entity_review_audit` records cryptographically verified clinician identities and roles instead of `LOCAL_DEMO_MODE`.
2. **Clinician-Adjudicated Benchmark Execution on GPT-6.1 Sol:** Have two board-certified clinicians independently adjudicate the 35-case suite (adding reference RxNorm/LOINC codes and severity ratings) and run a single budgeted live GPT-6.1 Sol benchmark pass.
3. **LocalOnnx Fallback Hardening:** Populate `clinicalCategory`, `specimen`, and shared `timing` bindings in `ClinicalPipelineService` so fallback mode matches the structured schema richness of the Foundry validator path.
4. **Operational Hardening:** Move PostgreSQL credentials to Managed Identity / Key Vault, add ASP.NET Core rate limiting, and enable CI execution of `node e2e/stage5-e2e.mjs` on pull requests.

