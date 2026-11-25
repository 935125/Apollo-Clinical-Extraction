# Apollo Stage 13 — Read-Only Clinical Audit of the 10 Live Microsoft Foundry Extractions

**Audit Date:** 25 November 2026  
**Model Audited:** Microsoft Foundry `gpt-6.1-sol` (resolved model `gpt-6.1-sol-2026-09-29`, deployment `gpt-6.1-sol`)  
**Transport Verified:** PostgreSQL Outbox → Real Azure Service Bus (`apollo-clinical-sb-dev.servicebus.windows.net` / `op-clinical-notes`) → `EmrConsumer` → `ExtractionWorkflow` → PostgreSQL `extraction_runs`  
**Audit Mode:** **100% Read-Only (ZERO new paid Foundry inference calls executed)**  
**Preserved Source Evidence:** [live-foundry-10-results.json](file:///d:/AI%20Extraction/docs/foundry-live-10-evidence/live-foundry-10-results.json), [run-10-live-foundry.mjs](file:///d:/AI%20Extraction/docs/foundry-live-10-evidence/run-10-live-foundry.mjs), [APOLLO_LIVE_FOUNDRY_10_EXTRACTIONS_REPORT.md](file:///d:/AI%20Extraction/docs/APOLLO_LIVE_FOUNDRY_10_EXTRACTIONS_REPORT.md)

> [!IMPORTANT]
> **Methodological Disclosure on Ground-Truth Benchmark:**  
> The entity-level Precision, Recall, and F1 scores below are computed against an **author-generated draft gold-standard reference annotation** derived directly from the 10 synthetic source clinical notes in [run-10-live-foundry.mjs](file:///d:/AI%20Extraction/docs/foundry-live-10-evidence/run-10-live-foundry.mjs). Because these synthetic notes and expected annotations were not independently adjudicated by a board-certified physician or external clinical NLP team, **all benchmark scores must be treated as a draft engineering audit requiring independent clinical review before any clinical governance or deployment claims.**  
> Furthermore, **processing persistence rate (`9/10 = 90.0%`) is strictly separated from clinical extraction accuracy** and is never reported as "90% clinical accuracy."

---

## 1. Executive Metric Summary (Strictly Separated Dimensions)

| Metric Category | Scope | Exact Numerator / Denominator | Score | Clinical Interpretation |
| --- | --- | --- | ---: | --- |
| **Pipeline Persistence Success Rate** | All 10 attempted runs | `9 / 10` persisted | **90.0%** | 9 persisted cleanly via Foundry (`fallbackTriggered=false`); 1 (`Case 4`) rejected by strict evidence validator (`AZURE_INVALID_EVIDENCE`) and dead-lettered cleanly. **Not clinical accuracy.** |
| **Medication Precision** | 9 persisted cases (draft gold) | `24 TP / 24 extracted` (`0 FP`) | **100.0%** | Zero hallucinated medications across all 9 persisted extractions. |
| **Medication Recall (Persisted Only)** | 9 persisted cases (draft gold) | `24 TP / 25 expected` (`1 FN`) | **96.0%** | **1 true omission:** `Sucralfate 1 g 10 mL orally TID before meals for 7 days` was missed in **Case 7** (`P100234 v25`). |
| **Medication F1 (Persisted Only)** | 9 persisted cases (draft gold) | `2*24 / (24+25)` | **97.96%** | Strong recall/precision on persisted notes, with 1 genuine omission (`Sucralfate`). |
| **Medication Recall (End-to-End, All 10)** | All 10 attempted cases (incl. rejected Case 4) | `24 TP / 27 expected` (`3 FN`) | **88.9%** | Counts Case 4's 2 unpersisted meds (`Warfarin` discontinued, `Bisoprolol` continued) + Case 7 `Sucralfate` as misses. |
| **Medication F1 (End-to-End, All 10)** | All 10 attempted cases | `2*24 / (24+27)` | **94.12%** | End-to-end effective medication F1 accounting for Case 4 rejection. |
| **Investigation Precision** | 9 persisted cases (draft gold) | `22 TP / 22 extracted` (`0 FP`) | **100.0%** | Zero hallucinated investigations across all 9 persisted cases. |
| **Investigation Recall (Persisted Only)** | 9 persisted cases (draft gold) | `22 TP / 22 expected` (`0 FN`) | **100.0%** | Every investigation in the 9 persisted notes was captured. |
| **Investigation F1 (Persisted Only)** | 9 persisted cases (draft gold) | `2*22 / (22+22)` | **100.0%** | Complete investigation capture on persisted notes. |
| **Investigation Recall (End-to-End, All 10)** | All 10 attempted cases (incl. rejected Case 4) | `22 TP / 26 expected` (`4 FN`) | **84.6%** | Counts Case 4's 4 unpersisted investigation mentions (`Previous INR 4.8`, `repeat PT`, `repeat INR`, `CBC automated`) as misses. |
| **Investigation F1 (End-to-End, All 10)** | All 10 attempted cases | `2*22 / (22+26)` | **91.67%** | End-to-end effective investigation F1 accounting for Case 4 rejection. |
| **Combined Entity F1 (Persisted 9)** | 9 persisted cases (draft gold) | `46 TP, 0 FP, 1 FN` | **98.92%** | Precision `100.0%`, Recall `97.87%`. |
| **Combined Entity F1 (All 10)** | All 10 attempted cases (draft gold) | `46 TP, 0 FP, 7 FN` | **92.93%** | Precision `100.0%`, Recall `86.79%`. |
| **Assertion & Intent Correctness** | 46 extracted entities | `46 / 46` | **100.0%** | Allergy (`Aspirin`), Negation (`Ibuprofen`), Historical (`Allopurinol`), Current vs New orders, and Conditional labs (`Serum Creatinine`, `Spirometry`, `Chest X-ray`) all accurately typed. |
| **Verbatim Evidence Grounding** | 46 persisted entities | `46 / 46` (`evidence_verified=true`) | **100.0%** | Strict validator enforced verbatim source spans; zero ungrounded records persisted. |
| **Exact RxNorm Coverage** | 24 extracted medications | `14 / 24` exact matched | **58.3%** | Compact local ontology catalog resolved 14 exact RxNorm CUIs; 10 remained `null` intentionally (zero fabricated codes). |
| **Exact LOINC Coverage** | 22 extracted investigations | `6 / 22` exact matched | **27.3%** | 6 exact LOINCs assigned; 10 withheld as narrower `candidateLoinc` (preventing false-specific coding); 6 unmapped in local catalog. |

---

## 2. Database-to-Evidence Reconciliation (All 10 Cases)

Every case in [live-foundry-10-results.json](file:///d:/AI%20Extraction/docs/foundry-live-10-evidence/live-foundry-10-results.json) was reconciled against PostgreSQL `cx_platform` (`emr_outbox_events`, `emr_note_versions`, `emr_extraction_links`, and `extraction_runs`):

- **10/10 events verified in `emr_outbox_events`** with matching `event_id`, `note_id`, `note_version`, and SHA-256 `source_hash`.
- **9/10 linked to `extraction_runs`** with `provider = foundry`, `model = gpt-6.1-sol-2026-09-29`, `azureDeployment = gpt-6.1-sol`, `fallbackTriggered = false`, total `8,578` prompt tokens + `4,386` completion tokens = `12,964` persisted tokens.
- **1/10 (`Case 4`, `P100234 v23`)** reconciled as `processing_status = Failed`, `processing_error = AZURE_INVALID_EVIDENCE`, `extraction_id = NULL`, dead-lettered cleanly without ONNX fallback.

---

## 3. Case-by-Case Clinical Audit (Cases 1–10)

### Case 1 — `P100234` (`ENC-100567`) `v21` — Hypertension & Dyslipidaemia Follow-up
- **Event / Extraction ID:** `67e7b3d3-0141-4c6e-8c8c-5d8134d70137` | **State:** `Ready` | **Inference:** `7078.91 ms` | **Tokens:** `948 / 390 / 1338`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - `Amlodipine` `5 mg`, dose `1`, `orally`, `once daily`, `for 30 days` — `current_medication` (`RxNorm 17767`) — **Exact**.
  - `Atorvastatin` `10 mg`, dose `1`, `orally`, `at bedtime`, `for 30 days` — `current_medication` (`RxNorm 83367`) — **Exact**.
  - `Telmisartan` `40 mg`, dose `half`, `orally`, `once daily in the morning`, `for 14 days` — `new_order` (`RxNorm 73494`) — **Exact** (correctly preserved `"half"` tablet dose).
- **Investigations (2/2 TP, 0 FP, 0 FN):**
  - `Complete Blood Count automated` — `new_order`, timing `tomorrow morning` (`LOINC null`, `candidateLoinc null`) — **Entity/intent exact; terminology gap** (catalog lookup failed to resolve exact phrase `"Complete Blood Count automated"`).
  - `Serum Creatinine` — `new_order`, timing `tomorrow morning` (`LOINC 2160-0`) — **Exact**.

### Case 2 — `P100235` (`ENC-100568`) `v12` — Community Acquired Pneumonia
- **Event / Extraction ID:** `82f08d30-a029-417a-a5cd-7bc20636cd62` | **State:** `Ready` | **Inference:** `5978.41 ms` | **Tokens:** `960 / 395 / 1355`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - `Amoxicillin-Clavulanate` `625 mg`, `1`, `orally`, `three times daily`, `for 7 days` — `new_order` (`RxNorm null` — combination drug not in compact local catalog).
  - `Azithromycin` `500 mg`, `1`, `orally`, `once daily`, `for 3 days` — `new_order` (`RxNorm 18631`) — **Exact**.
  - `Paracetamol` `650 mg`, `1`, `orally`, `as needed` — `new_order` (`RxNorm 161`) — **Exact** (PRN correctly preserved; PRN threshold `"for fever above 38.5 C"` omitted from structured fields, acceptable as duration is null).
- **Investigations (2/2 TP, 0 FP, 0 FN):**
  - `Chest X-ray PA view` — `new_order`, timing `today` (`LOINC null`, `candidateLoinc 24648-8` withheld conservatively).
  - `Complete Blood Count` — `new_order`, timing `tomorrow` (`LOINC null`, `candidateLoinc 58410-2` withheld conservatively because CBC without differential vs automated panel is ambiguous).

### Case 3 — `P100237` (`ENC-100570`) `v17` — Type 2 Diabetes Escalation & Aspirin Allergy
- **Event / Extraction ID:** `5e6984a3-375e-4d03-8b4d-2f0a9f643ee1` | **State:** `Ready` | **Inference:** `6274.98 ms` | **Tokens:** `954 / 436 / 1390`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - **`Aspirin`** — **`allergy`** (`RxNorm 1191`) — **CRITICAL SAFETY PASS:** Correctly classified `"Aspirin causes severe bronchospasm and facial swelling"` as `allergy` rather than an active medication or new order!
  - `Metformin` `500 mg`, `1`, `orally`, `twice daily` — `current_medication` (`RxNorm 6809`) — **Exact**.
  - `Sitagliptin` `100 mg`, `1`, `orally`, `once daily`, `for 30 days` — `new_order` (`RxNorm null`).
- **Investigations (3/3 TP, 0 FP, 0 FN):**
  - `HbA1c` — `new_order`, timing `next week` (`LOINC 4548-4`) — **Exact**.
  - `Lipid Profile` — `new_order`, timing `next week` (`LOINC null`, `candidateLoinc 24331-1` withheld).
  - **`Serum Creatinine`** — **`conditional`**, timing `if systolic blood pressure exceeds 160 mmHg` (`LOINC 2160-0`) — **CRITICAL INTENT PASS:** Properly distinguished conditional order from unconditional orders in the same Plan section.

### Case 4 — `P100234` (`ENC-100567`) `v23` — Anticoagulation Safety & Discontinuation (**FAILED — REJECTED**)
- **Event ID:** `6547cb5e-9e0b-429a-8986-0fd90e4be925` | **Extraction ID:** `null` | **State:** `Failed` | **Error:** `AZURE_INVALID_EVIDENCE`
- **Expected Entities (Draft Gold Standard — 0/6 Persisted):**
  - Medications (2): `Warfarin 5 mg` (`discontinued`), `Bisoprolol 2.5 mg 1 tablet orally once daily` (`current_medication`).
  - Investigations (4): `Previous INR 4.8` (`historical`), `repeat Prothrombin Time` (`new_order`, `today`), `repeat INR` (`new_order`, `today`), `Complete Blood Count automated` (`new_order`, `today`).
- **Clinical Impact:** **High-safety clinical note** (active anticoagulation discontinuation for supratherapeutic INR `4.8` with bruising). Strict evidence validation prevented ungrounded data from entering the chart, **but also resulted in zero structured extraction output for this note**, requiring clinician review of the original note (which the repaired Stage 13 UI now exposes truthfully alongside the `Failed` status). See [APOLLO_STAGE13_CASE4_EVIDENCE_FORENSICS.md](file:///d:/AI%20Extraction/docs/APOLLO_STAGE13_CASE4_EVIDENCE_FORENSICS.md) for the complete forensic investigation.

### Case 5 — `P100235` (`ENC-100568`) `v14` — Asthma Exacerbation & Inhaler Therapy
- **Event / Extraction ID:** `48dc9ed4-47a5-4286-9bba-6a67ada136a5` | **State:** `Ready` | **Inference:** `6382.08 ms` | **Tokens:** `962 / 421 / 1383`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - `Budesonide-Formoterol` `200/6 mcg`, dose `2`, `inhaled`, `twice daily`, `for 30 days` — `new_order` (`RxNorm null`).
  - `Salbutamol` `100 mcg`, dose `2`, `inhaled`, `as needed` — `new_order` (`RxNorm 435`).
  - `Montelukast` `10 mg`, `1`, `orally`, `at night`, `for 14 days` — `new_order` (`RxNorm null`).
  - *Minor posology observation:* Dose units `"puffs"` were stripped to numeric `"2"` (consistent with system schema where `dose` captures quantity and `route = inhaled` implies inhalation unit, though capturing `"2 puffs"` explicitly would improve clinical pharmacy clarity).
- **Investigations (2/2 TP, 0 FP, 0 FN):**
  - **`Spirometry`** — **`conditional`**, timing `if symptoms persist after 1 week` (`LOINC null`).
  - **`Chest X-ray`** — **`conditional`**, timing `if symptoms persist after 1 week` (`LOINC null`, `candidateLoinc 36643-5`).
  - **CRITICAL INTENT PASS:** Both conditional investigations accurately tagged `conditional` rather than `new_order`.

### Case 6 — `P100237` (`ENC-100570`) `v19` — Hypothyroidism & Iron Deficiency Anaemia
- **Event / Extraction ID:** `58c4721d-7f76-48b5-b400-5c1bd8012c7c` | **State:** `Ready` | **Inference:** `5810.36 ms` | **Tokens:** `945 / 405 / 1350`
- **Medications (2/2 TP, 0 FP, 0 FN):**
  - `Ferrous Sulfate` `200 mg`, `1`, `orally`, `once daily`, `for 60 days` — `new_order` (`RxNorm null`).
  - `Levothyroxine` `50 mcg`, `1`, `orally`, `once daily` — `current_medication` (`RxNorm 10582`).
  - *Minor clinical detail:* Meal-timing instructions (`before breakfast`, `after lunch`) are not stored as separate structured attributes in the current medication schema.
- **Investigations (3/3 TP, 0 FP, 0 FN):**
  - `Thyroid Stimulating Hormone` — `new_order`, timing `in 6 weeks` (`LOINC null`, `candidateLoinc 3016-3`).
  - `Serum Ferritin` — `new_order`, timing `in 6 weeks` (`LOINC null`).
  - `Complete Blood Count automated` — `new_order`, timing `in 6 weeks` (`LOINC null`).

### Case 7 — `P100234` (`ENC-100567`) `v25` — Acid Peptic Disease & Negated NSAID (**1 GENUINE MEDICATION OMISSION**)
- **Event / Extraction ID:** `c1cb5a61-5f02-4a93-b464-ed2927ec454c` | **State:** `Ready` | **Inference:** `8931.12 ms` | **Tokens:** `953 / 594 / 1547`
- **Medications (2/3 TP, 0 FP, **1 FN**):**
  - **`Ibuprofen`** — **`negated`** (`RxNorm 5640`) — **CRITICAL SAFETY PASS:** Accurately typed `"Ibuprofen was discussed but not prescribed due to gastritis risk"` as `negated`.
  - `Pantoprazole` `40 mg`, `1`, `orally`, `once daily`, `for 14 days` — `new_order` (`RxNorm 40790`) — **Exact**.
  - **MISSED MEDICATION (FALSE NEGATIVE):** Source text explicitly states: `"Take Sucralfate 1 g 10 mL orally three times daily before meals for 7 days."` **GPT-6.1 Sol completely omitted `Sucralfate`.** Because `validation_summary` had `warningCount = 0, errorCount = 0`, this omission was silent—demonstrating why clinician review of the side-by-side original note remains clinically mandatory.
- **Investigations (2/2 TP, 0 FP, 0 FN):**
  - `Stool H. pylori antigen test` — `new_order`, timing `this week` (`LOINC null`).
  - `Liver Function Tests` — `new_order`, timing `this week` (`LOINC null`, `candidateLoinc 24325-3`).

### Case 8 — `P100235` (`ENC-100568`) `v16` — Acute Uncomplicated Urinary Tract Infection
- **Event / Extraction ID:** `b885cff2-dd0a-4cce-a6b3-3b01925ee3a6` | **State:** `Ready` | **Inference:** `5263.51 ms` | **Tokens:** `936 / 326 / 1262`
- **Medications (2/2 TP, 0 FP, 0 FN):**
  - `Nitrofurantoin` `100 mg`, `1`, `orally`, `twice daily`, `for 5 days` — `new_order` (`RxNorm 7454`).
  - `Potassium Citrate`, dose `15`, `orally`, `twice daily`, `for 5 days` — `new_order` (`RxNorm null`). (*Note:* liquid volume `"15 mL"` stored as numeric `dose = "15"` without unit `"mL"`, same schema limitation as `"puffs"` in Case 5.)
- **Investigations (2/2 TP, 0 FP, 0 FN):**
  - `Urine Routine Examination` — `new_order`, timing `prior to first antibiotic dose` (`LOINC null`).
  - `Urine Culture and Sensitivity` — `new_order`, timing `prior to first antibiotic dose` (`LOINC null`, `candidateLoinc 630-4`).

### Case 9 — `P100237` (`ENC-100570`) `v21` — Stable Angina & Cardiology Workup
- **Event / Extraction ID:** `47304742-1d92-4095-a95f-daaf34918807` | **State:** `Ready` | **Inference:** `9284.83 ms` | **Tokens:** `956 / 686 / 1642`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - `Rosuvastatin` `20 mg`, `1`, `orally`, `at night` — `current_medication` (`RxNorm null`).
  - `Isosorbide Mononitrate` `30 mg`, `1`, `orally`, `once daily in the morning`, `for 30 days` — `new_order` (`RxNorm null`).
  - `Clopidogrel` `75 mg`, `1`, `orally`, `once daily` — `current_medication` (`RxNorm 32968`).
- **Investigations (3/3 TP, 0 FP, 0 FN):**
  - `12-lead ECG` — `new_order`, timing `today` (`LOINC null`, `candidateLoinc 11524-6`).
  - `Echocardiogram` — `new_order` (`LOINC 34552-0`) — **Exact**.
  - `Fasting Lipid Profile` — `new_order` (`LOINC null`, `candidateLoinc 24331-1`).

### Case 10 — `P100234` (`ENC-100567`) `v27` — Gout Flare & Historical Allopurinol
- **Event / Extraction ID:** `0ab4f6f8-4484-431d-be47-7c244a9f1524` | **State:** `Ready` | **Inference:** `10113.04 ms` | **Tokens:** `964 / 733 / 1697`
- **Medications (3/3 TP, 0 FP, 0 FN):**
  - **`Allopurinol`** `100 mg`, frequency `daily` — **`historical`** (`RxNorm 519`) — **CRITICAL SAFETY PASS:** Accurately identified `"Patient took Allopurinol 100 mg daily in 2024, ceased last year"` as `historical` instead of active therapy.
  - `Colchicine` `0.5 mg`, `1`, `orally`, `twice daily`, `for 5 days` — `new_order` (`RxNorm null`).
  - `Etoricoxib` `90 mg`, `1`, `orally`, `once daily`, `for 5 days` — `new_order` (`RxNorm null`).
- **Investigations (3/3 TP, 0 FP, 0 FN):**
  - `Serum Uric Acid` — `new_order`, timing `tomorrow` (`LOINC 3084-1`) — **Exact**.
  - `Serum Creatinine` — `new_order`, timing `tomorrow` (`LOINC 2160-0`) — **Exact**.
  - `C-Reactive Protein` — `new_order`, timing `tomorrow` (`LOINC null`, `candidateLoinc 1988-5`).

---

## 4. Key Clinical Quality Takeaways & Prioritized Backlog

1. **Zero Hallucinations, Strong Assertion Fidelity:** Across all 9 persisted notes (`46` extracted entities), precision was `100.0%` (`0 FP`), verbatim grounding was `100.0%`, and safety-critical assertion types (`allergy`, `negated`, `historical`, `conditional`) were `100.0%` accurate.
2. **Silent Omission Risk (`Case 7 — Sucralfate`):** LLMs can still silently drop valid third-line medications even when generating `0` validation warnings. Preserving the original clinical note side-by-side with extracted tables is an essential safety control.
3. **Dose Unit Preservation (`mL`, `puffs`):** Numeric dose values (`15` for Potassium Citrate `15 mL`; `2` for Budesonide-Formoterol/Salbutamol `2 puffs`) drop explicit unit labels. Adding a dedicated `doseUnit` field is recommended before downstream e-prescribing integration.
4. **Terminology Catalog Expansion (Non-Blocking):** Exact RxNorm (`58.3%`) and exact LOINC (`27.3%`) coverage reflects the compact local Indian outpatient catalog plus deliberate conservative withholding of narrower LOINC candidates (`10/22` investigations). Expanding the verified local RxNorm/LOINC dictionary (including mapping `"Complete Blood Count automated"` cleanly) will boost exact terminology coverage without compromising coding precision.
5. **High-Safety Failure Visibility (`Case 4`):** Repaired Stage 13 UI now ensures rejected notes like Case 4 (`Warfarin` discontinuation) surface transparently in the queue with their immutable source text and exact error code (`AZURE_INVALID_EVIDENCE`).
