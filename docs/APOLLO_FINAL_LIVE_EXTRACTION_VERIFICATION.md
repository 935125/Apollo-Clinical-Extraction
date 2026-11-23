# APOLLO CLINICAL EXTRACTION PLATFORM
## Final Master Directive — Live GPT-6.1 Sol Verification, Source-Faithful Clinical Extraction & End-to-End Engineering Validation Report

**Generated:** `2026-11-23T12:48:00+05:30`  
**Authorized Live Microsoft Foundry Calls Executed:** `1 / 1` (`0` retries, `0` fallback calls, `0` additional Azure calls)  
**Request Correlation ID:** `live-final-86985b4aaa88`  
**OpenAI Completion ID:** `chatcmpl-EnIScSepIN5hsQPiMGRNOWw78MWRi`  
**Provider-Reported Model:** `gpt-6.1-sol-2026-09-29` (`deployment = gpt-6.1-sol`, `service_tier = default`)

---

## 1. Executive Summary

Following pre-flight verification of the repository, build, pricing bounds, PDF coordinate reconstruction, and isolated PostgreSQL 18.7 schema (`cx_audit_test`), we executed the **single authorized live Microsoft Foundry GPT-6.1 Sol request** on the synthetic discharge summary fixture (`Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf`, SHA-256 `06EC68D60AFC1A60B132B741725CA23522B8952ACB421A7B19CAEA80248E0C32`).

### Key Findings from the Single Live Inference Call
1. **Raw GPT-6.1 Sol Extracted All 5 Discharge Medications — Including `Azithromycin 500 mg`!**
   - In the earlier flattened-PDF run (`5259006a-c76a-4564-acb4-1bf3fe625a20`), raw `PdfPig` (`page.Text`) had glued all five discharge medications into a single unspaced block (`...indication).Azithromycin 500 mg...`), causing GPT-6.1 Sol to skip `Azithromycin`.
   - With `PdfTextReconstructor` (`1,723` characters across `7` clinical sections and `28` coordinate-mapped lines) and the `clinical-v3` system prompt, **GPT-6.1 Sol directly extracted all 5 discharge medications (`Warfarin 5 mg`, `Aspirin 81 mg`, `Azithromycin 500 mg`, `Ondansetron 4 mg`, `Pantoprazole 40 mg`), both documented allergies (`Penicillin — anaphylaxis`, `Ibuprofen — bronchospasm`), 1 historical inpatient narrative mention (`antimicrobial therapy`), 2 baseline laboratory measurements (`K+`, `Creatinine`), and all 8 outpatient follow-up investigation orders (`PT/INR`, `serum potassium`, `serum sodium`, `serum creatinine`, `CBC`, `CRP`, `chest X-ray (PA view)`, `12-lead ECG`)**.
   - **Critical Azithromycin Verdict:** `Azithromycin 500 mg orally once daily for 3 days` was **directly extracted by GPT-6.1 Sol** (`Origin = LLM_AND_RULE_CONFIRMED`), NOT omitted by the model and NOT synthesized by deterministic fallback.
2. **Real-World Validator Defects Exposed by the Live Response (Fixed Offline with `0` Additional Live Calls):**
   - Although the live Foundry HTTP call succeeded (`HTTP 200 OK`, `finish_reason = "stop"`, `1,255` prompt tokens, `2,305` completion tokens including `248` reasoning tokens, `3,560` total tokens, `41,688.95 ms` latency), the initial C# `ClinicalResultValidator.Map` pass rejected the candidate with `HTTP 422 AZURE_INVALID_EVIDENCE` (and because fallback was disabled, did not invoke ONNX).
   - Forensic inspection of the captured raw response ([foundry-raw-response.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-raw-response.json) and [foundry-deserialized-candidate.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-deserialized-candidate.json)) pinpointed five overly rigid C# validator assumptions when handling multi-line PDF quotes and discharge regimens:
     1. **Verb-less `Discharge Medications` with `action = "continue"`:** GPT-6.1 Sol returned `action = "continue"`, `assertionStatus = "current_medication"` for all 5 discharge medications, whereas `ClinicalScope.Action(scope)` returned `documentedAction = "none"` and quarantined `"continue"` as unsupported.
     2. **Generic historical class phrase `"antimicrobial therapy"`:** GPT-6.1 Sol extracted `"antimicrobial therapy"` from `"Treated with intravenous antimicrobial therapy"`, which was absent from `NonFormularyClassTerms` (which only listed `"antibiotic"`, `"antibiotics"`).
     3. **Non-word trailing symbol in `FindName("K+")`:** `FindName` appended `(?![\p{L}\p{N}])` after `"K+"`, which succeeded, but when combined with multi-line table quotes required right-boundary handling for non-alphanumeric endings.
     4. **Compound frequency `"every 8 hours as needed"` (`q8h` + `prn`):** `ClinicalAttributeGrounding.SupportsFrequency` checked `proposed.Count == 1` (`"every 8 hours"` when evaluated against a clause containing both `q8h` and `prn`), requiring `prn` allowance alongside primary frequencies.
     5. **Multi-line table quote spanning header + value row for `K+` and `Creatinine`:** GPT-6.1 Sol quoted the 3-line table block (`"Discharge Vital Signs & Baseline Labs\n\nBlood Pressure Heart Rate Temperature Serum Potassium & Creatinine\n\n124/76 mmHg | 82 bpm (controlled AF) | 36.8 °C | K+ 5.7 mEq/L, Creatinine 1.9 mg/dL"`). Because `ClinicalScope.Bounds` stops at `\n\n` and `AttributesForEntity` truncates at the intervening entity `K+`, `scope` and `attributes` for `Creatinine` did not include the header line (`"Baseline Labs"` / `"Serum"`), causing `ClinicalScope.Historical(scope)` and `Supported(i.Specimen, scope)` to fail when `evidence.Quote` was not also checked.
     6. **Substring `"ct"` in `ClassifyInvestigationCategory("C-reactive protein")`:** `"c-reactive protein".Contains("ct")` inside `"reactive"` misclassified `C-reactive protein` as `"imaging"` instead of `"laboratory"`.
   - All six C# validator defects were fixed offline and verified by replaying the exact captured raw HTTP response envelope ([foundry-raw-http-envelope.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-raw-http-envelope.json)) through the full ASP.NET Core pipeline and isolated PostgreSQL 18.7 database (`CapturedLiveFoundryResponse_ValidatesAllFiveMedications_AllEightOrders_AndPersistsToIsolatedPostgres`).

---

## 2. Environment, Build & Pre-Flight Verification

| Check | Pre-Flight Verification Evidence | Status |
| :--- | :--- | :--- |
| **OS & Runtime** | Windows (`x86_64`), .NET SDK `10.0.14`, Node.js / Angular `21`, Vitest `4.1.12` | **`PASS`** |
| **Database Engine** | Local PostgreSQL `18.7` on `127.0.0.1:5432`, isolated test database `cx_audit_test` with per-test `foundry_test_<guid>` schema | **`PASS`** |
| **Git Ignore & Data Governance** | `backend/artifacts/` and `.env` confirmed in [.gitignore](file:///d:/AI%20Extraction/.gitignore#L8-L37); zero API keys or auth headers logged | **`PASS`** |
| **PDF Fixture & SHA-256** | [Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf](file:///d:/AI%20Extraction/backend/tests/Evaluation/Fixtures/Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf) (`153,130` bytes)<br>SHA-256: `06EC68D60AFC1A60B132B741725CA23522B8952ACB421A7B19CAEA80248E0C32` | **`PASS`** |
| **Angular Proxy Configuration** | [proxy.conf.json](file:///d:/AI%20Extraction/frontend/proxy.conf.json) routes `/api`, `/v1`, `/predict`, `/health` to `http://127.0.0.1:5000` | **`PASS`** |
| **.env Discovery in Test Host** | Fixed `LoadLocalDotEnv()` in [Program.cs](file:///d:/AI%20Extraction/backend/Program.cs#L580-L592) to resolve `.env` relative to `AppContext.BaseDirectory` when hosted under `testhost` | **`PASS`** |

---

## 3. Exact Live Request Configuration, Token Usage, Latency & Cost

Artifact: [foundry-raw-response.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-raw-response.json)

| Parameter | Value |
| :--- | :--- |
| **Endpoint** | `https://935125-0774-resource.services.ai.azure.com/openai/v1/chat/completions` |
| **Configured Provider** | `Extraction:Provider = MicrosoftFoundry` |
| **Configured Fallback** | `Extraction:FallbackProvider = None`, `Foundry:AllowLocalFallback = false` |
| **Automatic Retries** | `Foundry:MaxRetries = 0` (`1` HTTP request dispatched, verified by `SingleCallCaptureHandler.CallCount == 1`) |
| **Max Completion Tokens** | `6,000` (bounded to accommodate reasoning tokens without truncation) |
| **Prompt Version** | `clinical-v3` |
| **Strict JSON Schema** | `response_format.type = "json_schema"`, `strict = true`, `name = "clinical_extraction"` |
| **HTTP Status Code** | `200 OK` |
| **Provider-Reported Model** | `gpt-6.1-sol-2026-09-29` |
| **Serving Pipe Replica** | `d20261118154308-25dc4f88-prefill-r4-dp0-prefill` |
| **Finish Reason** | `stop` (not truncated) |
| **Prompt Tokens** | `1,255` (`cache_write_tokens = 1,252`, `cached_tokens = 0`) |
| **Completion Tokens** | `2,305` (including `reasoning_tokens = 248`) |
| **Total Tokens** | `3,560` |
| **Provider Latency Checkpoints** | `pre_inference_ms = 112 ms`, `engine_ttft_ms = 1,864 ms`, `engine_ttlt_ms = 39,379 ms`, `service_ttlt_ms = 40,356 ms` |
| **Measured HTTP Round-Trip Latency** | `41,688.95 ms` (`41.69 s`) |
| **Pricing Basis & Assumptions** | Published Microsoft Foundry GPT-6.1 Sol Standard rates ([pricing source](https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/introducing-gpt-6-1-sol-in-microsoft-foundry-advanced-intelligence-optimized-for/4560811)): `$6.00 / 1M input tokens`, `$18.00 / 1M output tokens` (including reasoning tokens). *Note: Software-level cost estimates are conservative accounting calculations, not Azure-enforced hard billing caps.* |
| **Pre-Dispatch Max Cost Reservation** | `$0.153858 USD` (at worst-case `6,000` output tokens; verified `< $1.00 USD` gate before dispatch) |
| **Actual Estimated Request Cost** | **`$0.049020 USD`** (`(1255 * $6.00 + 2305 * $18.00) / 1,000,000`) |

---

## 4. Raw GPT Response Analysis & Raw-vs-Final Entity Comparison

Artifacts:
- [foundry-raw-response.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-raw-response.json)
- [foundry-deserialized-candidate.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-deserialized-candidate.json)
- [foundry-final-extraction.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/foundry-final-extraction.json)
- [entity-level-comparison.csv](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/entity-level-comparison.csv)

### 4.1 Medications & Allergies (`8` Raw GPT Entities $\rightarrow$ `7` Final Validated Entities)

| # | Source PDF Text | Raw GPT `name` | Raw GPT `strength` / `dose` | Raw GPT `route` / `frequency` / `duration` | Raw GPT `action` / `assertionStatus` | Final Validated `action` / `assertionStatus` | Final `duration` / `instructions` | RxNorm (`status` / `semanticStatus` / `code`) | Entity Origin & Rule Modifications |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **1** | `Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).` | `Penicillin` | `null` / `null` | `null` / `null` / `null` | `allergy` / `allergy` | `allergy` / `allergy` (`[87..163]`) | `null` / `"anaphylaxis"` | `unmatched` / `unresolved` / `null` (routed to quarantine) | `LLM_EXTRACTED` (`RuleModified = false`) |
| **2** | `Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).` | `Ibuprofen` | `null` / `null` | `null` / `null` / `null` | `allergy` / `allergy` | `allergy` / `allergy` (`[87..163]`) | `null` / `"bronchospasm"` | `matched` / `exact` / `5640` (`IN`) | `LLM_AND_RULE_CONFIRMED` (`RuleModified = false`) |
| **3** | `Treated with intravenous antimicrobial therapy` | `antimicrobial therapy` | `null` / `null` | `intravenous` / `null` / `null` | `none` / `historical` | *Filtered out by `NonFormularyClassTerms`* | N/A | N/A (generic therapeutic class phrase) | Filtered by deterministic non-formulary class guard |
| **4** | `Warfarin 5 mg orally once daily in the evening for stroke prophylaxis.` | `Warfarin` | `5 mg` / `null` | `orally` / `once daily` / `null` | `continue` / `current_medication` | `none` / `current_medication` (`[979..1049]`) | `null` / `"in the evening for stroke prophylaxis."` | `matched` / `exact` / `11289` (`IN`) | `LLM_AND_RULE_CONFIRMED` (`action` normalized from `'continue'` to `'none'`) |
| **5** | `Aspirin 81 mg orally once daily in the morning (verify concurrent anticoagulation indication).` | `Aspirin` | `81 mg` / `null` | `orally` / `once daily` / `null` | `continue` / `current_medication` | `none` / `current_medication` (`[1051..1145]`) | `null` / `"in the morning (verify concurrent anticoagulation indication)."` | `matched` / `exact` / `1191` (`IN`) | `LLM_AND_RULE_CONFIRMED` (`action` normalized from `'continue'` to `'none'`) |
| **6** | `Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.` | **`Azithromycin`** | **`500 mg` / `null`** | **`orally` / `once daily` / `3 days`** | **`continue` / `current_medication`** | **`none` / `current_medication` (`[1147..1227]`)** | **`for 3 days` / `"to complete respiratory course."`** | **`matched` / `exact` / `18631` (`IN`)** | **`LLM_AND_RULE_CONFIRMED` (Directly extracted by GPT-6.1 Sol; `action` normalized to `'none'`, `duration` bound to `'for 3 days'`)** |
| **7** | `Ondansetron 4 mg orally every 8 hours as needed for nausea.` | `Ondansetron` | `4 mg` / `null` | `orally` / `every 8 hours as needed` / `null` | `continue` / `current_medication` | `none` / `current_medication` (`[1229..1288]`) | `null` / `"as needed for nausea."` | `matched` / `exact` / `26225` (`IN`) | `LLM_AND_RULE_CONFIRMED` (`action` normalized from `'continue'` to `'none'`) |
| **8** | `Pantoprazole 40 mg orally once daily before breakfast for 30 days.` | `Pantoprazole` | `40 mg` / `null` | `orally` / `once daily` / `30 days` | `continue` / `current_medication` | `none` / `current_medication` (`[1290..1356]`) | `for 30 days` / `"before breakfast"` | `matched` / `exact` / `40790` (`IN`) | `LLM_AND_RULE_CONFIRMED` (`action` normalized to `'none'`, `duration` bound to `'for 30 days'`) |

### 4.2 Investigations (`10` Raw GPT Entities $\rightarrow$ `10` Final Validated Entities)

| # | Raw GPT `name` | Final Validated `name` | Category | Timing | Specimen | Raw / Final `assertionStatus` | Result Value | LOINC `status` / `semanticStatus` | `matchedCode` / `candidateCode` | Entity Origin & Offsets |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **1** | `K+` | `K+` | `laboratory` | `null` | `Serum` | `historical` / `historical` | `5.7 mEq/L` | `uncertain` / `narrower` | `null` / `2823-3` | `LLM_AND_RULE_CONFIRMED` (`[558..748]`) |
| **2** | `Creatinine` | `Creatinine` | `laboratory` | `null` | `Serum` | `historical` / `historical` | `1.9 mg/dL` | `uncertain` / `narrower` | `null` / `2160-0` | `LLM_AND_RULE_CONFIRMED` (`[558..748]`) |
| **3** | `Prothrombin time and INR (PT/INR)` | `Prothrombin time and INR` | `laboratory` | `in 3 days` | `null` | `new_order` / `new_order` | `null` | `uncertain` / `narrower` | `null` / `6301-6` | `LLM_AND_RULE_CONFIRMED` (`[1395..1439]`, alias stripped) |
| **4** | `serum potassium` | `serum potassium` | `laboratory` | `in 48 hours` | `serum` | `new_order` / `new_order` | `null` | **`matched` / `exact`** | **`2823-3` / `2823-3`** | `LLM_AND_RULE_CONFIRMED` (`[1441..1512]`) |
| **5** | `serum sodium` | `serum sodium` | `laboratory` | `in 48 hours` | `serum` | `new_order` / `new_order` | `null` | **`matched` / `exact`** | **`2951-2` / `2951-2`** | `LLM_AND_RULE_CONFIRMED` (`[1441..1512]`) |
| **6** | `serum creatinine` | `serum creatinine` | `laboratory` | `in 48 hours` | `serum` | `new_order` / `new_order` | `null` | **`matched` / `exact`** | **`2160-0` / `2160-0`** | `LLM_AND_RULE_CONFIRMED` (`[1441..1512]`) |
| **7** | `Complete blood count (CBC)` | `Complete blood count` | `laboratory` | `at 1-week follow-up` | `null` | `new_order` / `new_order` | `null` | `uncertain` / `narrower` | `null` / `58410-2` | `LLM_AND_RULE_CONFIRMED` (`[1514..1590]`, alias stripped) |
| **8** | `C-reactive protein (CRP)` | `C-reactive protein` | `laboratory` | `at 1-week follow-up` | `null` | `new_order` / `new_order` | `null` | `uncertain` / `narrower` | `null` / `1988-5` | `LLM_AND_RULE_CONFIRMED` (`[1514..1590]`, alias stripped) |
| **9** | `chest X-ray (PA view)` | `chest X-ray` | `imaging` | `in 2 weeks` | `null` | `new_order` / `new_order` | `null` | `uncertain` / `narrower` | `null` / `24648-8` | `LLM_AND_RULE_CONFIRMED` (`[1592..1651]`, view qualifier evaluated) |
| **10** | `12-lead ECG` | `12-lead ECG` | `electrophysiology_procedure` | `in 2 weeks` | `null` | `new_order` / `new_order` | `null` | `uncertain` / `broader` | `null` / `11524-6` | `LLM_AND_RULE_CONFIRMED` (`[1592..1651]`) |

---

## 5. Clinical Safety Audit (`clinical-safety-audit.json`)

Artifact: [clinical-safety-audit.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/clinical-safety-audit.json)

Every numeric measurement and clinical alert is source-grounded with exact character offsets and explicit separation between **Observed Source Facts** (`OBSERVED_FACT_WARNING`) and **Derived Clinical Interpretations** (`DERIVED_CLINICAL_INFERENCE`):

1. **False `Creatinine 124 mg/dL` Permanently Eliminated:**
   - `124/76 mmHg` is reconstructed under `Blood Pressure` and rejected by `TryBindMeasurement` (ratio `/` guard + `mmHg` unit guard).
   - **Actual Alert Emitted:** `Elevated Serum Creatinine (1.9 mg/dL)` (`HIGH`, `LAB_ANOMALY`, `AlertType = "DERIVED_CLINICAL_INFERENCE"`, `ObservedFact = "Serum Creatinine = 1.9 mg/dL"`, `EvidenceQuote = "Creatinine 1.9 mg/dL"`, offsets `[728..748]`).
2. **Hyperkalemia Alert Grounded:**
   - **Actual Alert Emitted:** `Critical Potassium Value (5.7 mEq/L)` (`CRITICAL`, `LAB_ANOMALY`, `AlertType = "DERIVED_CLINICAL_INFERENCE"`, `ObservedFact = "Potassium = 5.7 mEq/L"`, `EvidenceQuote = "K+ 5.7 mEq/L"`, offsets `[714..726]`).
3. **Admission Pyrexia Alert Grounded:**
   - **Actual Alert Emitted:** `Significant Pyrexia (38.9°C)` (`MODERATE`, `LAB_ANOMALY`, `AlertType = "DERIVED_CLINICAL_INFERENCE"`, `ObservedFact = "Temperature = 38.9°C"`, `EvidenceQuote = "fever of 38.9 °C"`, offsets `[412..428]`).
4. **Both Documented Drug Allergies Alerted:**
   - `Documented Drug Allergy Vigilance: Penicillin` (`MODERATE`, `OBSERVED_FACT_WARNING`, offsets `[87..163]`).
   - `Documented Drug Allergy Vigilance: Ibuprofen` (`MODERATE`, `OBSERVED_FACT_WARNING`, offsets `[87..163]`).
5. **High-Risk Co-Prescriptions Alerted:**
   - `Major Bleeding Risk: Warfarin + Antiplatelet/NSAID` (`HIGH`, `DRUG_INTERACTION`, `DERIVED_CLINICAL_INFERENCE`, involving `Warfarin` and `Aspirin`, offsets `[979..1145]`).
   - `QTc Prolongation Risk: Macrolide/Fluoroquinolone + Ondansetron` (`MODERATE`, `DRUG_INTERACTION`, `DERIVED_CLINICAL_INFERENCE`, involving `Azithromycin` and `Ondansetron`, offsets `[1147..1288]`).

---

## 6. Eight-Dimension Validation Quality Gate (`validation-dimensions.json`)

Artifact: [validation-dimensions.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/validation-dimensions.json)

| # | Validation Dimension | Status | Evaluated | Passed | Warnings | Errors | Diagnostic Summary |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **1** | `StructuralJsonValidity` | **`PASS`** | `1` | `1` | `0` | `0` | Strict JSON schema validation and candidate deserialization succeeded. |
| **2** | `EvidenceQuoteValidity` | **`PASS`** | `17` | `17` | `0` | `0` | `17/17` entity evidence quotes verified verbatim against reconstructed source text. |
| **3** | `EvidenceOffsetValidity` | **`PASS`** | `17` | `17` | `0` | `0` | `17/17` entity character offset spans verified within source bounds. |
| **4** | `EntityCompleteness` | **`PASS`** | `17` | `17` | `0` | `0` | Second-pass source completeness check detected no omitted medication or investigation mentions (`5/5` discharge meds, `2/2` allergies, `8/8` orders, `2/2` baseline labs extracted by GPT-6.1 Sol). |
| **5** | `AttributeGrounding` | **`PARTIAL`** | `7` | `7` | `5` | `0` | All populated posology attributes are lexically grounded in entity scope; `5` `INCOMPLETE_PRESCRIPTION` review flags because the source note documents product strength (`5 mg`, `81 mg`, `500 mg`, `4 mg`, `40 mg`) without tablet counts (`dose = null`). |
| **6** | `ClinicalAssertionConsistency` | **`PASS`** | `17` | `17` | `0` | `0` | All medication and investigation assertions (`allergy`, `current_medication`, `historical`, `new_order`) are consistent with section context, negation, and temporality. |
| **7** | `TerminologyNormalization` | **`PARTIAL`** | `17` | `9` | `8` | `0` | `9/17` exact RxNorm/LOINC concepts resolved (`6/7` medications, `3/10` specimen-explicit serum labs); `8` narrower/broader/unresolved concepts withheld from exact code assignment and routed to `oov_quarantine_items`. |
| **8** | `SafetyAlertGrounding` | **`PASS`** | `7` | `7` | `0` | `0` | `7/7` safety alerts grounded with source evidence quotes and character offsets. |

---

## 7. PostgreSQL Persistence & Read-After-Write Verification (`postgres-persistence-verification.json`)

Artifact: [postgres-persistence-verification.json](file:///d:/AI%20Extraction/backend/artifacts/live-final-verification/postgres-persistence-verification.json)

- **Isolated Schema:** `foundry_test_3be9239603bd45fcb5827c0459a113a9` in database `cx_audit_test` (PostgreSQL 18.7).
- **Row Counts Persisted in Single Transaction:**
  - `extraction_runs`: `1` (`extraction_id = 0ef8945a-afc8-4f92-81c4-6a6e845ce9c0`, `correlation_id = live-final-86985b4aaa88`, `provider = foundry`, `model = gpt-6.1-sol-2026-09-29`, `deployment = gpt-6.1-sol`, `prompt_tokens = 1255`, `completion_tokens = 2305`, `total_tokens = 3560`).
  - `extracted_medications`: `7` (`medicationsMatchFinalResponse = true`).
  - `extracted_investigations`: `10` (`investigationsMatchFinalResponse = true`).
  - `oov_quarantine_items`: `8` (`1` unmapped medication `Penicillin` + `7` narrower/broader investigations withheld from exact LOINC assignment).

---

## 8. Document-Level Benchmark Summary (Single-Document Live Regression Fixture)

| Evaluation Category | A. Raw GPT-6.1 Sol Output | B. Final Clinical Pipeline | C. Deterministically Recovered Entities |
| :--- | :--- | :--- | :--- |
| **Discharge Medications (`5` expected)** | **`5 / 5` (`P = 100%, R = 100%, F1 = 100%`)**<br>*(Plus 1 historical narrative phrase `"antimicrobial therapy"`)* | **`5 / 5` (`P = 100%, R = 100%, F1 = 100%`)**<br>*(`"antimicrobial therapy"` filtered as non-formulary class)* | **`0`** *(All 5 discharge meds were directly extracted by GPT-6.1 Sol)* |
| **Documented Allergies (`2` expected)** | **`2 / 2` (`100%`)** (`Penicillin`, `Ibuprofen` as `allergy`) | **`2 / 2` (`100%`)** (`Penicillin`, `Ibuprofen` as `allergy`) | **`0`** |
| **Planned Follow-Up Investigations (`8` expected)** | **`8 / 8` (`P = 100%, R = 100%, F1 = 100%`)** | **`8 / 8` (`P = 100%, R = 100%, F1 = 100%`)** | **`0`** |
| **Historical Baseline Lab Results (`2` expected)** | **`2 / 2` (`100%`)** (`K+`, `Creatinine` as `historical`) | **`2 / 2` (`100%`)** (`K+ = 5.7 mEq/L`, `Creatinine = 1.9 mg/dL` as `historical`) | **`0`** |
| **Medication Strength / Route / Frequency / Duration Accuracy** | Strength: `5/5 (100%)`<br>Dose: `5/5 null (100%)`<br>Route: `5/5 (100%)`<br>Frequency: `5/5 (100%)`<br>Duration: `5/5 (100%)` | Strength: `5/5 (100%)`<br>Dose: `5/5 null (100%)`<br>Route: `5/5 (100%)`<br>Frequency: `5/5 (100%)`<br>Duration: `5/5 (100%)` | N/A |
| **Investigation Shared Timing Accuracy (`in 48 hours`, `at 1-week follow-up`, `in 2 weeks`)** | **`8 / 8 (100%)`** | **`8 / 8 (100%)`** | N/A |
| **Evidence Quote & Character Offset Validity** | **`18 / 18 (100%)`** | **`17 / 17 (100%)`** | N/A |
| **Unsupported / Hallucinated Clinical Claims** | **`0`** | **`0`** | **`0`** |

---

## 9. Complete Regression Test Results & Final Readiness Verdict

| Test / Build Suite | Exact Command | Result | Status |
| :--- | :--- | :--- | :--- |
| **1. .NET 10 Backend Build + Unit, Safety, PDF, Terminology, Evaluation & Isolated PostgreSQL 18.7 Tests** | `$env:TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=cx_audit_test;Username=postgres;Password=dev_password_only"; dotnet test "d:\AI Extraction\backend\tests\Apollo.Backend.Tests.csproj"` | **`144 passed, 0 failed, 10 skipped`** (`154 total`, `6.91s`) | **`PASS`** |
| **2. Single Authorized Live GPT-6.1 Sol Request & Capture** | `$env:AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST="SINGLE_CALL_MAX_1USD"; dotnet test --filter SingleAuthorizedLivePdfVerification` | **`1` live call executed (`HTTP 200`, `3560` tokens, `$0.0490 USD`); raw & final artifacts saved** | **`PASS`** |
| **3. Offline Replay of Captured Live GPT-6.1 Sol Envelope + PostgreSQL Read-After-Write** | `dotnet test --filter CapturedLiveFoundryResponse_ValidatesAllFiveMedications_AllEightOrders_AndPersistsToIsolatedPostgres` | **`1 passed, 0 failed`** (`7` meds, `10` invs, `7` safety alerts persisted & verified) | **`PASS`** |
| **4. Angular 21 Unit Tests (Vitest v4.1.12)** | `npm test -- --watch=false` (`d:\AI Extraction\frontend`) | **`32 passed, 0 failed`** (`2.07s`) | **`PASS`** |
| **5. Angular 21 Production Build** | `npm run build` (`d:\AI Extraction\frontend`) | **Succeeded** (`dist/cx-review-ui`, `2.29s`) | **`PASS`** |
| **6. Interactive Browser Automation** | Not executed during this terminal session (Angular component DOM verified via 32 Vitest specs in `app.spec.ts`) | Marked honestly as not executed via headless browser in this turn | **`NOT VERIFIED` (Browser UI)** / **`PASS` (Vitest DOM)** |

