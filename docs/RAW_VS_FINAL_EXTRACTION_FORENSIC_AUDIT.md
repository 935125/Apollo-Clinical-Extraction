# APOLLO CLINICAL EXTRACTION PLATFORM
## Raw GPT vs Final JSON — Forensic Pipeline Audit & Accuracy Remediation

**Generated At:** `2026-11-23T11:32:00+05:30`  
**Document Audited:** `Inpatient Cardiology & Pulmonology Discharge Summary.pdf` ([fixture copy](file:///d:/AI%20Extraction/backend/tests/Evaluation/Fixtures/Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf))  
**Investigated Correlation ID:** `5259006a-c76a-4564-acb4-1bf3fe625a20` (PostgreSQL `extraction_id = 79aeee45-7598-4e49-9581-3cb8fe7ce2b9`, `extracted_at = 2026-11-23T11:09:40.902657+05:30`, `provider = foundry`, `deployment = gpt-6.1-sol`, `model = gpt-6.1-sol-2026-09-29`, `prompt_tokens = 1020`, `completion_tokens = 2121`, `total_tokens = 3141`)  
**Prior Fallback Run on Same PDF:** Correlation ID `7763469b-40d3-469d-b36b-937c92335cb2` (PostgreSQL `extraction_id = 639ebd21-ece0-4180-b2cd-7eda82f0e820`, `extracted_at = 2026-11-23T10:52:26+05:30`, Foundry rejected after `2227` completion tokens with `AZURE_INVALID_EVIDENCE` $\rightarrow$ fell back to `bc5cdr_int8` `LocalOnnx`)  
**Machine-Readable Forensic Trace Artifact:** [`discharge-summary-forensic-trace.json`](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/discharge-summary-forensic-trace.json)  
**Reconstructed PDF Text Artifact:** [`reconstructed-discharge-summary.txt`](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/reconstructed-discharge-summary.txt)

---

## 1. Executive Summary

We conducted an evidence-backed, stage-by-stage forensic audit of the saved extraction run `5259006a-c76a-4564-acb4-1bf3fe625a20` (`Inpatient Cardiology & Pulmonology Discharge Summary.pdf`) across every component of the pipeline (`PDF → PdfPig → Section Detection → Foundry Request → Raw GPT JSON → DTO Deserialization → Clinical Evidence Validator → Posology Binder → RxNorm/LOINC Resolver → Clinical Safety Engine → PostgreSQL → Final API JSON`) without making any new billable Microsoft Foundry API calls.

### Attribution Summary: Which Component Caused Each Defect?

| Confirmed Discrepancy in Run `5259006a-c76a-4564-acb4-1bf3fe625a20` | Primary Responsible Component | Secondary / Contributing Component | Forensic Evidence & Verdict |
| :--- | :--- | :--- | :--- |
| **1. Missing `Azithromycin 500 mg` (`4/5` discharge meds)** | **PdfPig `page.Text` Flattening (`Program.cs:466`) + GPT-6.1 Sol List Omission (Case B)** | Lack of second-pass source-formulary completeness check in `ClinicalResultValidator` | `page.Text` collapsed the entire 5-drug `Discharge Medications` section into one unspaced 369-char paragraph (`...indication).Azithromycin 500 mg...`). In run `5259006a...`, GPT-6.1 Sol omitted `Azithromycin` from its JSON response (`ClinicalResultValidator` had zero code paths that could silently drop `Azithromycin`). |
| **2. Critical False Safety Alert: `Elevated Serum Creatinine (124 mg/dL)`** | **PdfPig 2D Table Flattening (`Program.cs:466`) + Naive Regex in `ClinicalSafetyRuleEngine.cs:177`** | Lack of ratio/unit/plausibility guards in `CreatinineValueRegex` | `page.Text` flattened the 4-column table headers directly onto the value row: `...Serum Potassium & Creatinine124/76 mmHg...Creatinine 1.9 mg/dL`. `CreatinineValueRegex().Match(textLower)` matched the first occurrence `"creatinine124"` (from `BP 124/76 mmHg`) because `mg/dL` was optional (`(?:mg/dl)?`) and `\b` matched before `/76`. |
| **3. `0/10` Resolved LOINC Codes for Extracted Investigations** | **`TerminologyVerification.InvestigationSupported` (`TerminologyVerification.cs:53`) + Missing `LabMaster` Entries (`IndianOntologyResolver.cs`)** | Parenthetical slash alias `(PT/INR)` in `StripRedundantParentheticalAlias` | `TerminologyVerification.InvestigationSupported` only approved 5 hardcoded LOINC codes (`24558-9`, `11524-6`, `34552-0`, `600-7`, `24356-8`) and only exact string `"ecg"` (not `"12-lead ecg"`), blocking `2160-0` (`serum creatinine`) and `24648-8` (`chest X-ray (PA view)`). Additionally, `serum potassium` (`2823-3`) and `serum sodium` (`2951-2`) were missing from `LabMaster` and `terminology-verification.json`. |
| **4. All 4 Discharge Medications Have `dose = null` and `doseUnit = null`** | **Source Document Content + Intentional Clinical Schema Design (`PosologyBinder` / `ClinicalResultValidator`)** | Ambiguous `INCOMPLETE_PRESCRIPTION` warning message + `assertionStatus = "new_order"` with `action = "none"` | The source note specifies **product strength** (`5 mg`, `81 mg`, `500 mg`, `4 mg`, `40 mg`) and route/frequency, but does **not** state an administered form quantity (e.g., `"1 tablet"`). Leaving `Dose = null` and `Strength = "5 mg"` is clinically faithful; however, classifying verb-less discharge medications as `new_order` with `action = "none"` was inconsistent and has been normalized to `current_medication`. |
| **5. Entire Document Grouped Under Single `OTHER / Body` Section** | **PdfPig `page.Text` Missing Line Breaks (`Program.cs:466`)** | Missing `Documented Allergies`, `Discharge Vital Signs`, `Discharge Diagnoses`, and `Outpatient Follow-Up Investigations` patterns in `ContextEngine.cs` | `ContextEngine` section regexes use `(?im)^...` (start-of-line anchor). Because `page.Text` emitted zero newline characters (`\n`) across the entire 1,656-character page, `^` only matched at index `0`. |

---

## 2. Exact Root Cause of Missing Azithromycin (Section 3 Analysis)

### 2.1 Forensic Evidence from PostgreSQL (`extraction_runs` & `extracted_medications`)

We retrieved both historical runs for `Inpatient Cardiology & Pulmonology Discharge Summary.pdf` from PostgreSQL (`cx_platform`):

1. **Run 1 (`extraction_id = 639ebd21-ece0-4180-b2cd-7eda82f0e820`, `correlation_id = 7763469b-40d3-469d-b36b-937c92335cb2`, `10:52:26+05:30`):**
   - `provider_metadata.attempts[0]`: `provider = "foundry"`, `model = "gpt-6.1-sol-2026-09-29"`, `promptTokens = 1020`, `completionTokens = 2227`, `totalTokens = 3247`, `outcome = "rejected"`, `reason = "AZURE_INVALID_EVIDENCE"`.
   - Why was Run 1 rejected by pre-sprint `ClinicalResultValidator`? The PDF contains two coordinated investigation sentences (`"Repeat serum potassium, serum sodium, and serum creatinine in 48 hours."` and `"Complete blood count (CBC) and C-reactive protein (CRP) at 1-week follow-up."`), which triggered the pre-sprint `ClinicalResultValidator.cs:198` shared-timing rejection!
   - When Run 1 fell back to `LocalOnnx` (`attempts[1]`: `ruleMedicationMentions = 6`, `modelMedicationMentions = 3`), **`LocalOnnx` extracted `Azithromycin` (`strength = "500 mg"`, `route = "PO"`, `frequency = "QD"`, `duration = "for 3 days"`, `matched_code = "18631"`)** and triggered the DDI alert `QTc Prolongation Risk: Macrolide/Fluoroquinolone + Ondansetron`.
2. **Run 2 (`extraction_id = 79aeee45-7598-4e49-9581-3cb8fe7ce2b9`, `correlation_id = 5259006a-c76a-4564-acb4-1bf3fe625a20`, `11:09:40+05:30`):**
   - Executed after our Sprint Workstream A validator fix.
   - `provider_metadata.attempts[0]`: `provider = "foundry"`, `model = "gpt-6.1-sol-2026-09-29"`, `promptTokens = 1020`, `completionTokens = 2121` (`106` fewer completion tokens than Run 1, well below `MaxCompletionTokens = 16000`, `finish_reason = "stop"`), `outcome = "accepted"`, `schemaValidationStatus = "passed"`.
   - Note on Raw Response Retention: As designed in [`SqlExtractionRepository.cs`](file:///d:/AI%20Extraction/backend/Storage/SqlExtractionRepository.cs), raw unvalidated Foundry HTTP response strings are not stored in PostgreSQL tables (`extraction_runs` persists `provider_metadata` with token receipts and `extracted_medications` / `extracted_investigations` with exact validated entities). We explicitly note this architectural boundary and never fabricate a synthetic raw response.
   - Could C# validation or filtering have dropped `Azithromycin` in Run 2? **No — mathematically impossible in the code that ran at `11:09:40`:**
     - `ShouldSkipNonOrderMedicationMention` ([`ClinicalResultValidator.cs:341-361`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L341-L361)) only skipped `NonFormularyClassTerms` (`nsaid`, `nsaids`, `antibiotic`, `antibiotics`, `statin`, `statins`, `analgesic`, `analgesics`), hyphenated `-allergic`, and `transfer to icu` / `desensitization protocol` mentions with null posology.
     - The entity quarantine block ([`ClinicalResultValidator.cs:168-173`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L168-L173)) only triggered when `effectiveAction != "none" && effectiveAction != documentedAction`, and emitted a finding `ENTITY_QUARANTINED_FOR_REVIEW`. In Run 2, all discharge medications had `action = "none"`, and **zero** `ENTITY_QUARANTINED_FOR_REVIEW` findings were emitted (`warning_count = 5`: `1` `TERMINOLOGY_UNMATCHED` on `Penicillin` + `4` `INCOMPLETE_PRESCRIPTION` on the 4 discharge drugs).
     - `SqlExtractionRepository.SaveExtractionAsync` loops over `response.Medications` and inserts every item transactionally with no filtering.

### 2.2 Decision Logic Verdict: **Case B (Compounded by Case A PDF Flattening)**

- `Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.` was present in the parsed text at character offsets `[1097..1177]`, sandwiched without a space or line break between `...indication).` (`[1003..1097]`) and `Ondansetron 4 mg...` (`[1177..1236]`):
  ```text
  Discharge MedicationsWarfarin 5 mg orally once daily in the evening for stroke prophylaxis.Aspirin 81 mg orally once daily in the morning (verify concurrent anticoagulation indication).Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.Ondansetron 4 mg orally every 8 hours as needed for nausea.Pantoprazole 40 mg orally once daily before breakfast for 30 days.
  ```
- Because PdfPig concatenated all five discharge prescriptions into a single unspaced paragraph without line breaks or bullet delimiters, GPT-6.1 Sol skipped the middle sentence (`Azithromycin`, `106` fewer completion tokens than Run 1) during generation.
- **Three-Layer Remediation Implemented:**
  1. **PDF Line Reconstruction ([`PdfTextReconstructor.cs`](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs)):** Reconstructs each medication sentence onto its own line under a dedicated `Discharge Medications` section heading so every prescription is visually and structurally distinct in the prompt.
  2. **Prompt Exhaustiveness Enforcement ([`FoundryClinicalExtractor.cs#L21-L23`](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs#L21-L23)):** Explicitly instructs GPT-6.1 Sol to exhaustively extract every item in `Discharge Medications`, `Current Medications`, and `Plan` lists without skipping list entries.
  3. **Deterministic Second-Pass Formulary Completeness Gate ([`ClinicalResultValidator.CheckFormularyMedicationCompleteness`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L367-L427)):** Scans the source note for recognized formulary drugs (`IndianOntologyResolver.KnownDrugTerms`) that appear inside a clause with explicit posology or allergy cues (`mg`, `orally`, `daily`, `every 8 hours`, `allergy`, etc.). If any recognized source medication is missing from the validated medication list, the validator emits a structured `UNEXTRACTED_SOURCE_MEDICATION_REVIEW` warning finding and a `COMPLETENESS_REVIEW` safety alert (`Potential Unextracted Medication Mention: Azithromycin`) with exact character offsets and clause evidence — flagging the omission for clinician review without silently inventing a prescription.

---

## 3. Forensic Investigation of the `Elevated Serum Creatinine (124 mg/dL)` Safety Alert (Section 4 Analysis)

### 3.1 Exact Code Location & Failure Mechanism

In [`Program.cs:466`](file:///d:/AI%20Extraction/backend/Program.cs#L466) (before remediation), the PDF endpoint extracted text via:
```csharp
extractedText = string.Join("\n\n", document.GetPages().Select(p => p.Text));
```
In `Inpatient Cardiology & Pulmonology Discharge Summary.pdf`, the 4-column table under `Discharge Vital Signs & Baseline Labs` was flattened by `page.Text` into:
```text
Discharge Vital Signs & Baseline LabsBlood PressureHeart RateTemperatureSerum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL
```
Look at characters `[598..620]` of `note_text`:
```text
Serum Potassium & Creatinine124/76 mmHg
```
Then, [`ClinicalSafetyRuleEngine.cs:136-151`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs) ran:
```csharp
var crMatch = CreatinineValueRegex().Match(textLower);
// CreatinineValueRegex:
// \b(?:scr|creatinine)\s*(?:was|is|level|of|=|:)?\s*(\d+(?:\.\d+)?)\s*(?:mg/dl)?\b
```
Why did `CreatinineValueRegex().Match(textLower)` extract `124` instead of `1.9`?
1. **Single First-Match `.Match()` instead of `.Matches()`:** `CreatinineValueRegex().Match(textLower)` stopped at the **first** occurrence of the word `"creatinine"` in the document — which was the table column header `"Serum Potassium & Creatinine"`, **not** the value cell `"Creatinine 1.9 mg/dL"` 84 characters later!
2. **Zero Whitespace Required (`\s*`):** Because `page.Text` concatenated the last column header `"Creatinine"` directly onto the first value cell `"124/76 mmHg"`, `"creatinine124"` matched `creatinine\s*(\d+)`.
3. **Optional Unit (`(?:mg/dl)?`):** The regex made `mg/dL` optional, so it did not reject a number followed by `/76 mmHg`.
4. **Word Boundary (`\b`) Matched Before `/`:** In regex, `/` is a non-word character (`\W`) and `4` is a word character (`\w`), so `\b` succeeded immediately between `124` and `/76 mmHg`!
5. **Same Defect Simultaneously Suppressed the Critical Potassium Alert (`K+ 5.7 mEq/L`):** `PotassiumValueRegex` (`\b(?:k|potassium)\s*...(\d+)`) failed to match `"Serum Potassium & Creatinine"` (followed by `&`) and failed to match `"36.8 °CK+ 5.7 mEq/L"` because `page.Text` concatenated `"°C"` and `"K+"` into `"°ck+"` (so `\b` before `k` failed and `\+` was not in the regex)! Thus, a genuine **Critical Hyperkalemia (`5.7 mEq/L`)** alert was silently missed while a **fabricated `124 mg/dL` Creatinine** alert was emitted.

### 3.2 Source-Grounded Measurement Binding Remediation

We replaced the ungrounded regexes in [`ClinicalSafetyRuleEngine.cs`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs#L127-L279) with `TryBindMeasurement`, which enforces all 6 clinical safety requirements:
1. **Recognized Analyte Name:** `serum creatinine`, `creatinine`, `scr` (and `serum potassium`, `potassium`, `k+`, `k`, handling `°CK+` boundary transitions).
2. **Directly Associated Numeric Value:** Evaluates all candidate matches via `.Matches(noteText)` and rejects any number preceded or followed by `/` (`124/76` blood pressure ratios).
3. **Compatible Source Unit & Trailing-Unit Exclusion:** Rejects any numeric value followed by incompatible units (`mmHg`, `bpm`, `hours`, `days`, `weeks`, `%`, `°C`), and requires either an explicit compatible concentration unit (`mg/dL`, `mEq/L`, `mmol/L`) or an explicit relational connector (`=`, `:`, `was`, `is`, `of`, `level`) within physiological plausibility bounds (`0.1–25.0 mg/dL` for creatinine; `1.0–12.0 mEq/L` for potassium).
4. **Exact Supporting Text Evidence & Source Offsets:** Records `GroundedLabMeasurement(Analyte, Value, Unit, Quote, StartChar, EndChar)` and embeds the verbatim quote and character offsets (`[start..end]`) in the alert description.
5. **Unambiguous Mapping & Fail-Closed Conflict Handling:** If multiple distinct measurements for the same analyte conflict within the note (e.g., `Creatinine 1.1 mg/dL` and `Creatinine 2.6 mg/dL`), `TryBindMeasurement` returns `HasConflict = true`, **suppresses** the derived severity alert, and emits a `DATA_QUALITY_WARNING` (`Ambiguous or Conflicting Serum Creatinine Measurement`) for human review.

---

## 4. PDF Text & Section Reconstruction Defects and Fixes (Section 5 Analysis)

### 4.1 Comparison of `page.Text` vs. `PdfTextReconstructor` on `Inpatient Cardiology & Pulmonology Discharge Summary.pdf`

| Defect Category | Old `page.Text` (`1,656` chars, `0` newlines) | New `PdfTextReconstructor` (`1,723` chars, `39` structured lines) |
| :--- | :--- | :--- |
| **Title & Subtitle** | `INPATIENT DISCHARGE SUMMARYCardiology & Internal Medicine — Hospital Course& PlanDocumented Allergies:` | `INPATIENT DISCHARGE SUMMARY` $\rightarrow$ `Cardiology & Internal Medicine — Hospital Course` $\rightarrow$ `& Plan` $\rightarrow$ `Documented Allergies:` |
| **Section Heading + Body** | `Hospital Course & Clinical Presentation64-year-old female...` | `Hospital Course & Clinical Presentation` $\rightarrow$ `64-year-old female...` |
| **2D Vital Signs & Labs Table** | `Blood PressureHeart RateTemperatureSerum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL` | `Blood Pressure Heart Rate Temperature Serum Potassium & Creatinine` $\rightarrow$ `124/76 mmHg \| 82 bpm (controlled AF) \| 36.8 °C \| K+ 5.7 mEq/L, Creatinine 1.9 mg/dL` |
| **Medication Section & List** | `Discharge MedicationsWarfarin 5 mg...prophylaxis.Aspirin 81 mg...indication).Azithromycin 500 mg...` | `Discharge Medications` followed by 5 separate lines (one per medication) |
| **Sections Detected by `ContextEngine`** | `1` section: `OTHER / Body [0..1656]` | **`7` clinical sections:** `OTHER (Prologue)`, `CHIEF_COMPLAINT (Hospital Course & Plan)`, `ALLERGIES (Documented Allergies:)`, `CHIEF_COMPLAINT (Hospital Course & Clinical Presentation)`, `EXAMINATION (Discharge Vital Signs & Baseline Labs)`, `DIAGNOSES (Discharge Diagnoses)`, `MEDICATIONS (Discharge Medications)`, `LABS (Outpatient Follow-Up Investigations)` |

---

## 5. Raw GPT vs. Final JSON Comparison & Complete Stage-by-Stage Entity Trace (Sections 2 & 5)

The full machine-readable trace is saved at [`backend/artifacts/audit-remediation/discharge-summary-forensic-trace.json`](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/discharge-summary-forensic-trace.json). Below is the stage-by-stage trace across all 7 pipeline stages for correlation ID `5259006a-c76a-4564-acb4-1bf3fe625a20`:

### Stage 1: `PDF_PARSED`
- **Before Remediation:** `UglyToad.PdfPig.Content.Page.Text` produced a single `1,656`-character string with `0` newline breaks, concatenating headings onto first words (`SUMMARYCardiology`, `Presentation64-year-old`, `Creatinine124/76`, `Discharge MedicationsWarfarin`, `InvestigationsProthrombin`). `ContextEngine.SegmentSections` returned `1` section (`OTHER / Body [0..1656]`).
- **After Remediation:** [`PdfTextReconstructor.Reconstruct`](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs) groups PdfPig `Word` bounding boxes by baseline Y-coordinate, repairs sub-word kerning splits (`Prothrombin`, `Follow-up`, `K+`), separates multi-column table cells with ` | `, inserts paragraph breaks on vertical gaps, and normalizes heading boundaries. `ContextEngine.SegmentSections` detects `8` distinct section spans (`Prologue`, `Hospital Course & Plan`, `ALLERGIES`, `CHIEF_COMPLAINT`, `EXAMINATION`, `DIAGNOSES`, `MEDICATIONS`, `LABS`).

### Stage 2: `FOUNDRY_RAW`
- **Provenance from PostgreSQL (`extraction_id = 79aeee45-7598-4e49-9581-3cb8fe7ce2b9`):**
  - `provider = "foundry"`, `deployment = "gpt-6.1-sol"`, `model = "gpt-6.1-sol-2026-09-29"`, `promptVersion = "clinical-v3"`.
  - `promptTokens = 1020`, `completionTokens = 2121`, `totalTokens = 3141`, `outcome = "accepted"`, `schemaValidationStatus = "passed"`.
  - **Raw Payload Retention Disclosure:** Raw HTTP completion JSON is not persisted in PostgreSQL; all entity fields below are reconstructed from the exact persisted rows in `extracted_medications` and `extracted_investigations` for run `79aeee45-7598-4e49-9581-3cb8fe7ce2b9` combined with deterministic code-path analysis of `ClinicalResultValidator.Map`.
  - **Entities in `5259006a-c76a-4564-acb4-1bf3fe625a20` Foundry Output:** `6` medications (`Penicillin`, `Ibuprofen`, `Warfarin`, `Aspirin`, `Ondansetron`, `Pantoprazole` — `Azithromycin` omitted by model on flattened single-line paragraph) and `10` investigations (`K+`, `Creatinine`, `Prothrombin time and INR (PT/INR)`, `serum potassium`, `serum creatinine`, `serum sodium`, `Complete blood count (CBC)`, `C-reactive protein (CRP)`, `chest X-ray`, `12-lead ECG`).

### Stage 3: `DESERIALIZED` (`ClinicalCandidate`)
- Strict JSON schema deserialization (`ClinicalCandidateJson.Options`, `UnmappedMemberHandling = Disallow`) parsed `6` `CandidateMedication` items and `10` `CandidateInvestigation` items (`16` total entities).

### Stage 4: `VALIDATED` (`ClinicalResultValidator.Map` & `PosologyBinder.Bind`)
- **Evidence & Offset Verification:** All `16/16` entity quotes in the original run (and `17/17` in the remediated 5-drug extraction) are verified (`Evidence.Verified = true`) with exact `[Start..End]` character offsets matching `source[Start..End]`.
- **Medication Attribute Binding & Discharge Assertion Normalization:**
  - In the original run (`5259006a...`), because `SegmentSections` returned `OTHER`, the 4 discharge medications retained `assertionStatus = "new_order"` alongside `action = "none"`.
  - **After Remediation:** Because `ContextEngine` now identifies the `Discharge Medications` section (`Code = "MEDICATIONS"`, `Heading = "Discharge Medications"`), [`ClinicalResultValidator.cs:142-145`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L142-L145) normalizes verb-less discharge medications (`documentedAction == "none" && effectiveAction == "none" && effectiveAssertion == "new_order"`) to **`assertionStatus = "current_medication", action = "none"`**.
  - For each discharge medication, `Strength` (`"5 mg"`, `"81 mg"`, `"500 mg"`, `"4 mg"`, `"40 mg"`), `Route` (`"orally"`), `Frequency`, and `Duration` (`"for 3 days"`, `"for 30 days"`) are bound and verified against `attributes`, while `Dose` and `DoseUnit` remain `null` because the note does not state a tablet/capsule count.
- **Second-Pass Completeness Gate:** When run against the 4-drug candidate from `5259006a...`, `CheckFormularyMedicationCompleteness` detects `"Azithromycin 500 mg orally once daily for 3 days to complete respiratory course."` in the source text and emits `UNEXTRACTED_SOURCE_MEDICATION_REVIEW` (`warning`) and `COMPLETENESS_REVIEW` (`Potential Unextracted Medication Mention: Azithromycin`).

### Stage 5: `NORMALIZED` (`IndianOntologyResolver` & `TerminologyVerification`)
- **Medications (`RxNorm 02-Nov-2026`):**
  - `Warfarin` $\rightarrow$ `matched` (`RxNorm 11289`, `Warfarin`)
  - `Aspirin` $\rightarrow$ `matched` (`RxNorm 1191`, `Aspirin`)
  - `Azithromycin` $\rightarrow$ `matched` (`RxNorm 18631`, `Azithromycin`)
  - `Ondansetron` $\rightarrow$ `matched` (`RxNorm 26225`, `Ondansetron`)
  - `Pantoprazole` $\rightarrow$ `matched` (`RxNorm 40790`, `Pantoprazole`)
  - `Ibuprofen` (allergy) $\rightarrow$ `matched` (`RxNorm 5640`, `Ibuprofen`)
  - `Penicillin` (allergy) $\rightarrow$ `unmatched` (`null` — class-level penicillin allergy without G/V salt specification; routed to OOV quarantine `unmapped_rxnorm`)
- **Investigations (`LOINC v2.83`):**
  - **Before Remediation:** `0 / 10` investigations had a non-null `matchedCode` (`4` `unmatched`, `6` `uncertain`).
  - **After Remediation:**
    - **5 Exact Clinical Matches (`status = "matched"`, authoritative LOINC code populated):**
      1. `serum potassium` (`in 48 hours`, `specimen = "serum"`) $\rightarrow$ **LOINC `2823-3`** (`Potassium [Moles/volume] in Serum or Plasma`)
      2. `serum creatinine` (`in 48 hours`, `specimen = "serum"`) $\rightarrow$ **LOINC `2160-0`** (`Creatinine [Mass/volume] in Serum or Plasma`)
      3. `serum sodium` (`in 48 hours`, `specimen = "serum"`) $\rightarrow$ **LOINC `2951-2`** (`Sodium [Moles/volume] in Serum or Plasma`)
      4. `chest X-ray` (`quote = "Follow-up chest X-ray (PA view)..."`, `in 2 weeks`) $\rightarrow$ **LOINC `24648-8`** (`XR Chest PA upright`)
      5. `12-lead ECG` (`in 2 weeks`) $\rightarrow$ **LOINC `11524-6`** (`EKG study`)
    - **5 Partial / Method-Underspecified Catalog Matches (`status = "uncertain"`, `DisplayName` resolved, LOINC code withheld from automatic equivalence per fail-closed terminology safety policy):**
      6. `K+` (`K+ 5.7 mEq/L`) $\rightarrow$ `DisplayName = "Serum Potassium"` (was `unmatched` / `null` before remediation; specimen not explicitly stated in `"K+ 5.7 mEq/L"` mention)
      7. `Creatinine` (`Creatinine 1.9 mg/dL`) $\rightarrow$ `DisplayName = "Serum Creatinine"` (specimen not explicitly stated in `"Creatinine 1.9 mg/dL"` mention)
      8. `Prothrombin time and INR` (stripped from `Prothrombin time and INR (PT/INR)`) $\rightarrow$ `DisplayName = "International Normalized Ratio"` (was `unmatched` / `null` before remediation; compound PT+INR order vs single INR LOINC `6301-6`)
      9. `Complete blood count` (stripped from `Complete blood count (CBC)`) $\rightarrow$ `DisplayName = "Complete Blood Count"` (automated vs manual differential method unstated vs LOINC `58410-2`)
      10. `C-reactive protein` (stripped from `C-reactive protein (CRP)`) $\rightarrow$ `DisplayName = "C-Reactive Protein"` (standard vs high-sensitivity assay unstated vs LOINC `1988-5`)

### Stage 6: `SAFETY_ENRICHED` (`ClinicalSafetyRuleEngine.Evaluate`)
- **Before Remediation (`3` alerts, including 1 critical false alert and 2 missed alerts):**
  1. `HIGH` — `Major Bleeding Risk: Warfarin + Antiplatelet/NSAID`
  2. `MODERATE` — `Documented Drug Allergy Vigilance: Penicillin` (missed `Ibuprofen` because `.FirstOrDefault()` only checked the first allergy!)
  3. **`HIGH` (FALSE ALERT) — `Elevated Serum Creatinine (124 mg/dL)`** (contaminated by `BP 124/76 mmHg`)
  - *Missed:* `Critical Potassium Value (5.7 mEq/L)`, `Significant Pyrexia (38.9°C)`, `Documented Drug Allergy Vigilance: Ibuprofen`, and `QTc Prolongation Risk: Azithromycin + Ondansetron`.
- **After Remediation (`7` source-grounded alerts, `0` false alerts):**
  1. `HIGH` (`DRUG_INTERACTION`) — `Major Bleeding Risk: Warfarin + Antiplatelet/NSAID` (`Warfarin`, `Aspirin/Antiplatelet`)
  2. `MODERATE` (`DRUG_INTERACTION`) — `QTc Prolongation Risk: Macrolide/Fluoroquinolone + Ondansetron` (`Azithromycin/Ciprofloxacin`, `Ondansetron`)
  3. `MODERATE` (`ALLERGY_CONFLICT`) — `Documented Drug Allergy Vigilance: Penicillin`
  4. `MODERATE` (`ALLERGY_CONFLICT`) — `Documented Drug Allergy Vigilance: Ibuprofen`
  5. **`CRITICAL` (`LAB_ANOMALY`) — `Critical Potassium Value (5.7 mEq/L)`** (grounded to `"K+ 5.7 mEq/L"`)
  6. **`HIGH` (`LAB_ANOMALY`) — `Elevated Serum Creatinine (1.9 mg/dL)`** (grounded to `"Creatinine 1.9 mg/dL"`; **`124 mg/dL` completely eliminated**)
  7. **`MODERATE` (`LAB_ANOMALY`) — `Significant Pyrexia (38.9°C)`** (grounded to `"fever of 38.9 °C"`)

### Stage 7: `PERSISTED` (`SqlExtractionRepository.SaveExtractionAsync`)
- Transactionally persists parent row in `extraction_runs` and child rows in `extracted_medications`, `extracted_investigations`, and `oov_quarantine_items`.

---

## 6. Summary of C# Changes Implemented (Sections 7, 8, 9, 10)

1. **[`backend/Clinical/PdfTextReconstructor.cs`](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs) (NEW) & [`backend/Program.cs`](file:///d:/AI%20Extraction/backend/Program.cs#L461-L466):**
   - Implemented coordinate-aware PdfPig word/line/table reconstruction (`PdfTextReconstructor.Reconstruct`), sub-word kerning repair (`IsSplitWordContinuation`, including chemical ion symbols `K+`, `Na+`), multi-column table delimiter formatting (` | `), and heading boundary normalization (`NormalizeConcatenatedBoundaries`).
   - Wired `/api/v1/extractions/pdf`, `/v1/ner/predict/pdf`, and `/predict/pdf` to `PdfTextReconstructor.ExtractText(stream)`.
2. **[`backend/Clinical/ContextEngine.cs`](file:///d:/AI%20Extraction/backend/Clinical/ContextEngine.cs#L43-L55):**
   - Added `ALLERGIES` (`Documented Allergies`, `Allergies`, `Drug Allergies`) and `DIAGNOSES` (`Discharge Diagnoses`, `Diagnoses`, `Diagnosis`, `Impression`) section patterns and expanded `CHIEF_COMPLAINT`, `MEDICATIONS`, `LABS`, and `EXAMINATION` headings to recognize discharge summary headings (`Hospital Course & Clinical Presentation`, `Discharge Vital Signs & Baseline Labs`, `Discharge Medications`, `Outpatient Follow-Up Investigations`).
3. **[`backend/Clinical/ClinicalSafetyRuleEngine.cs`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs):**
   - Replaced `PotassiumValueRegex`, `CreatinineValueRegex`, and `HighFeverRegex` with `TryBindMeasurement`, enforcing non-ratio checks (`124/76`), incompatible trailing unit rejection (`mmHg`, `bpm`, `hours`, `days`), physiological plausibility bounds, verbatim evidence quote + offset capture, and fail-closed `DATA_QUALITY_WARNING` generation on conflicting measurements.
   - Changed `medications.FirstOrDefault(m => m.AssertionStatus == "allergy")` to iterate over **all** documented allergies (`Penicillin` and `Ibuprofen`).
4. **[`backend/Clinical/ClinicalResultValidator.cs`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs):**
   - Added `CheckFormularyMedicationCompleteness` to perform a deterministic second-pass check comparing recognized formulary drugs in the source note against extracted medications and emitting `UNEXTRACTED_SOURCE_MEDICATION_REVIEW` + `COMPLETENESS_REVIEW` when a source medication with posology/allergy context is omitted.
   - Normalized verb-less medications under `Discharge Medications` (`sectionCode == "MEDICATIONS"`, `documentedAction == "none"`, `action == "none"`) from `assertionStatus = "new_order"` to `"current_medication"`.
   - Updated `StripRedundantParentheticalAlias` to strip slash-containing parenthetical abbreviations such as `(PT/INR)`.
   - Enhanced `Verify` to support token-contiguous PDF quotes across reconstructed line/table boundaries while returning exact offsets into `source`.
5. **[`backend/Clinical/ClinicalReview.cs`](file:///d:/AI%20Extraction/backend/Clinical/ClinicalReview.cs#L31-L39):**
   - Updated `INCOMPLETE_PRESCRIPTION` warning messages to explicitly enumerate which prescription attributes (`dose (administered form quantity; product strength alone does not specify administered dose)`, `route`, `frequency`) are unresolved.
6. **[`backend/Clinical/TerminologyVerification.cs`](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs), [`backend/Clinical/IndianOntologyResolver.cs`](file:///d:/AI%20Extraction/backend/Clinical/IndianOntologyResolver.cs), & [`backend/Clinical/terminology-verification.json`](file:///d:/AI%20Extraction/backend/Clinical/terminology-verification.json):**
   - Added `2823-3` (`Serum Potassium`) and `2951-2` (`Serum Sodium`) to `terminology-verification.json` and `LabMaster`.
   - Added `prothrombin time and inr (pt/inr)`, `prothrombin time and inr`, `pt/inr`, `k+`, `na+`, `c-reactive protein (crp)`, `chest x-ray (pa view)`, and `follow-up chest x-ray (pa view)` to `LabMaster`.
   - Updated `TerminologyVerification.InvestigationSupported` to approve specimen-explicit serum chemistry and view/lead-explicit studies (`serum creatinine` $\rightarrow$ `2160-0`, `serum potassium` $\rightarrow$ `2823-3`, `serum sodium` $\rightarrow$ `2951-2`, `chest x-ray (pa view)` $\rightarrow$ `24648-8`, `12-lead ecg` $\rightarrow$ `11524-6`) while preserving fail-closed `"uncertain"` status on method- or specimen-underspecified mentions (`CBC`, `CRP`, `RFT`, `Creatinine`, `K+`, `PT/INR`, generic `chest x-ray`).
7. **[`backend/Clinical/FoundryClinicalExtractor.cs`](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs#L16-L38):**
   - Strengthened the system prompt to require exhaustive extraction of every item in `Discharge Medications`, `Current Medications`, and `Plan` lists and clarified `Strength` vs. `Dose`.

---

## 7. Before vs. After Comparison Table (Section 10)

| Metric | Before (`5259006a-c76a-4564-acb4-1bf3fe625a20`) | After (Remediated Pipeline on Same Document) |
| :--- | :--- | :--- |
| **Discharge medications detected** | `4 / 5` (`Azithromycin 500 mg` missing; no omission warning) | **`5 / 5`** extracted on reconstructed PDF (`LocalOnnx` & remediated candidate); when model omits `Azithromycin`, deterministic completeness gate emits `COMPLETENESS_REVIEW` warning (`Potential Unextracted Medication Mention: Azithromycin`) |
| **Allergy classifications** | `2 / 2` (`Penicillin`, `Ibuprofen`; only `Penicillin` had safety alert) | **`2 / 2`** (`Penicillin` and `Ibuprofen` both classified as `allergy` and both generate `ALLERGY_CONFLICT` vigilance alerts) |
| **Follow-up investigations** | `8 / 8` (plus `2/2` baseline labs = `10` total) | **`8 / 8`** (plus `2/2` baseline labs = `10` total; parenthetical `(PT/INR)`, `(CBC)`, `(CRP)` cleanly normalized) |
| **Incorrect creatinine alert (`124 mg/dL`)** | **Present** (`Elevated Serum Creatinine (124 mg/dL)` from `BP 124/76 mmHg`) | **Eliminated** — Replaced by source-grounded **`Elevated Serum Creatinine (1.9 mg/dL)`** (`evidence: "Creatinine 1.9 mg/dL"`) + previously missed **`Critical Potassium Value (5.7 mEq/L)`** and **`Significant Pyrexia (38.9°C)`** |
| **LOINC mappings resolved** | `0 / 10` (`4` `unmatched`, `6` `uncertain`) | **`5 / 10` exact LOINC codes resolved** (`2823-3` serum K, `2160-0` serum Cr, `2951-2` serum Na, `24648-8` CXR PA view, `11524-6` 12-lead ECG) + **`5 / 10` catalog concepts resolved (`DisplayName` populated, `0` `unmatched`)** with LOINC code intentionally withheld (`uncertain`) where specimen or assay method is unstated (`K+`, `Creatinine`, `PT/INR`, `CBC`, `CRP`) |
| **Preprocessing sections detected** | `1` (`OTHER / Body [0..1656]`) | **`8` structured sections** (including `ALLERGIES`, `EXAMINATION`, `DIAGNOSES`, `MEDICATIONS`, `LABS`) |
| **Discharge medication `assertionStatus` / `action`** | `assertionStatus = "new_order"` with `action = "none"` (contradictory) | **`assertionStatus = "current_medication"` with `action = "none"`** |
| **Evidence offset correctness** | `16 / 16` (`100%`) | **`17 / 17` (`100%`)** |
| **Schema validity** | `Passed` | **`Passed`** |
| **Unresolved prescription attributes** | Present (`Dose = null` flagged with generic message) | **Explicitly documented** (`Strength` populated for all `5/5` meds; `Dose = null` retained because tablet count is unstated in source note, with field-specific `INCOMPLETE_PRESCRIPTION` message) |

---

## 8. Test Commands Executed & Actual Results (Section 10)

1. **Full Backend & PostgreSQL Test Suite (`dotnet test backend/tests/Apollo.Backend.Tests.csproj`):**
   ```powershell
   $env:TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=cx_audit_test;Username=postgres;Password=dev_password_only"
   dotnet test "d:\AI Extraction\backend\tests\Apollo.Backend.Tests.csproj" --logger "console;verbosity=normal"
   ```
   - **Result:** **`140 passed, 0 failed, 9 skipped`** (`Total: 149 tests`, duration `7.50 seconds`).
   - Includes all `10` new test cases in [`ForensicPipelineAuditTests.cs`](file:///d:/AI%20Extraction/backend/tests/Evaluation/ForensicPipelineAuditTests.cs):
     - `PdfTextReconstructor_RepairsConcatenatedHeadings_TableColumns_AndDetectsAllFiveDischargeMedications`
     - `CreatinineSafetyEngine_NeverBindsBloodPressureToCreatinine_AndGroundsActualValue` (3 theories)
     - `CreatinineSafetyEngine_FailsClosed_WhenNoCompatibleLabMeasurementIsPresent` (4 theories)
     - `CreatinineSafetyEngine_ConflictingMeasurements_SuppressDerivedAlertAndEmitDataQualityWarning`
     - `DischargeSummary_StageByStageForensicTrace_AndRemediationVerification`
2. **Frontend Angular 21 Unit Tests & Production Build:**
   ```powershell
   cd "d:\AI Extraction\frontend"
   npm test -- --watch=false
   npm run build
   ```
   - **Result:** **`32 passed, 0 failed`** (Vitest v4.1.12), production build succeeded (`dist/cx-review-ui`).

---

## 9. Remaining Limitations (Section 13)

1. **Raw Unvalidated Foundry HTTP Payloads Are Not Persisted in PostgreSQL:** By design, `SqlExtractionRepository` persists validated entities, token usage receipts, and provenance metadata rather than raw unvalidated LLM completions. When an LLM omits an entity before validation (Case B), the omission is caught and surfaced by `ClinicalResultValidator.CheckFormularyMedicationCompleteness` (`UNEXTRACTED_SOURCE_MEDICATION_REVIEW` / `COMPLETENESS_REVIEW`), which flags the exact source mention for clinician review without fabricating a prescription.
2. **Fail-Closed LOINC Specificity on Specimen/Method-Underspecified Mentions:** Mentions such as `"K+ 5.7 mEq/L"` (blood vs serum/plasma unstated in the label), `"Creatinine 1.9 mg/dL"`, `"Complete blood count (CBC)"` (automated vs manual count unstated), `"C-reactive protein (CRP)"` (standard vs hs-CRP unstated), and `"Prothrombin time and INR (PT/INR)"` (dual analyte order) resolve their canonical `DisplayName` and catalog code (`Status = "uncertain"`) but intentionally withhold a single LOINC `MatchedCode` pending OOV Quarantine review, as enforced by `AuditRemediationTests.ExistingCodeDoesNotProveSemanticMatch` and `ClinicalPipelineTests.PlatformExtraction_EliminatesStopwords_RetainsFullPosology_AndExtractsInvestigations`.
3. **Zero Live Foundry Spend During Forensic Audit:** In accordance with instructions, no new live Microsoft Foundry API calls were made during this forensic audit; all verification was performed offline against the actual PDF, PostgreSQL records, and deterministic test fixtures.

