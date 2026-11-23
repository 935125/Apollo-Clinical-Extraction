# APOLLO CLINICAL EXTRACTION PLATFORM
## Final Clinical Extraction Correction, Observability, Safety & Terminology Hardening Report

**Generated:** `2026-11-23T12:12:00+05:30`  
**Technology Stack:** Angular 21 (`frontend/`), ASP.NET Core .NET 10 (`backend/`), Microsoft Foundry (`gpt-6.1-sol`), Local BC5CDR INT8 ONNX (`bc5cdr_int8`), PostgreSQL 17 + `pgvector`, `PdfPig` (`PdfTextReconstructor`), RxNorm (`02-Nov-2026`), LOINC (`2.83`).  
**Live Foundry Calls Executed in This Sprint:** `0` (Strict compliance with billing governance; all offline replay, PDF forensic, safety, and isolated PostgreSQL verification suites executed locally).

---

## 1. Executive Summary

This engineering effort conducted a forensic inspection and targeted implementation across the entire Apollo Clinical Extraction Platform pipeline:

`Source document → Structure-preserving preprocessing → GPT structured extraction → Entity completeness reconciliation → Multi-dimensional evidence & attribute validation → Semantic RxNorm/LOINC normalization → Source-grounded safety checks → PostgreSQL persistence → Angular clinical review`

### Key Outcomes
1. **Observability & Entity-Level Provenance (`Phase B`):**
   - Implemented opt-in synthetic diagnostic capture (`ExtractionOptions.EnableSyntheticDiagnosticCapture`) in [ExtractionWorkflow.cs](file:///d:/AI%20Extraction/backend/Clinical/ExtractionWorkflow.cs) and raw structured output retention on `FoundryExtraction` in [FoundryClinicalExtractor.cs](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs#L164-L167), disabled by default for real patient data governance.
   - Added entity-level `Origin` (`LLM_EXTRACTED`, `RULE_EXTRACTED`, `LLM_AND_RULE_CONFIRMED`, `RECOVERED_FROM_SOURCE`, `HUMAN_REVIEW_REQUIRED`) and `EntityProvenanceDto` (`Provider`, `RuleModified`, `Modifications`, `UncertaintyReason`, `Quarantined`, `TriggeredAlertIds`) to every extracted medication and investigation in [Contracts.cs](file:///d:/AI%20Extraction/backend/Models/Contracts.cs#L41-L97), persisted in PostgreSQL (`extracted_medications` and `extracted_investigations`).
2. **Completeness & Prompt Hardening (`Phase C`):**
   - Hardened the system prompt in [FoundryClinicalExtractor.cs](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs#L17-L40) to process every clinical section sequentially, never skip middle entries in multi-line lists, separate allergies from prescriptions, distinguish product strength from administered dose, and split coordinated multi-test investigation sentences into individual entries.
   - Expanded `CheckSourceCompleteness` in [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L458-L618) so that completeness reconciliation checks **both** formulary drug mentions (`KnownDrugTerms`) **and** non-formulary pattern-based prescription mentions (`<CapitalizedDrug> <Strength> <Route/Frequency>`), plus unextracted laboratory/imaging mentions in `LABS` sections.
3. **PDF Reconstruction & Coordinate Mapping (`Phase D`):**
   - Enhanced [PdfTextReconstructor.cs](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs) to generate a character-offset-to-page-and-bounding-box coordinate map (`PdfLineCoordinateSpan`), pair 2D table headers with values (`Blood Pressure: 124/76 mmHg | ... | Serum Potassium & Creatinine: K+ 5.7 mEq/L, Creatinine 1.9 mg/dL`), repair sub-word kerning splits (`K+`, `Na+`, `Prothrombin`), and protect CamelCase tokens (`McGraw`, `QuantiFERON`) from false boundary splitting.
4. **Multi-Dimensional Clinical Validation (`Phase E`):**
   - Added 8 independent validation dimensions (`StructuralJsonValidity`, `EvidenceQuoteValidity`, `EvidenceOffsetValidity`, `EntityCompleteness`, `AttributeGrounding`, `ClinicalAssertionConsistency`, `TerminologyNormalization`, `SafetyAlertGrounding`) to `ValidationSummaryDto.Dimensions` in [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L639-L685).
5. **Source-Grounded Clinical Safety Engine (`Phase F`):**
   - Separated **Observed Data** (`ObservedFact`, `EvidenceQuote`, `StartChar`, `EndChar`, `AlertType = "OBSERVED_FACT_WARNING"`) from **Derived Clinical Interpretations** (`AlertType = "DERIVED_CLINICAL_INFERENCE"`) in [ClinicalSafetyRuleEngine.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs).
   - Enforced explicit unit grounding in `TryBindMeasurement` (rejecting unitless numbers, blood-pressure ratios like `124/76`, and duration phrases like `in 48 hours`, and failing closed with `DATA_QUALITY_WARNING` when multiple measurements conflict).
   - Added high-risk posology safety rules for daily Methotrexate toxicity (`HIGH_RISK_POSOLOGY`), pediatric weight-based regimens (`WEIGHT_BASED_POSOLOGY`), and negated vs. confirmed drug allergies.
6. **Trustworthy RxNorm & LOINC Semantic Specificity (`Phase G`):**
   - Implemented standardized 5-tier semantic specificity statuses (`exact`, `broader`, `narrower`, `ambiguous`, `unresolved`) with `CandidateCode`, `TerminologySource`, `TerminologyVersion`, and `MappingNote` in [TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs) and [IndianOntologyResolver.cs](file:///d:/AI%20Extraction/backend/Clinical/IndianOntologyResolver.cs).
   - Audited and corrected `24648-8` (`"XR Chest PA upright"`) for `"chest X-ray (PA view)"` (classified as `narrower`, `status = "uncertain"`, `MatchedCode = null`, `CandidateCode = "24648-8"` because the source note does not state an upright posture) and `11524-6` (`"EKG study"`) for `"12-lead ECG"` (classified as `broader`, `status = "uncertain"`, `MatchedCode = null`, `CandidateCode = "11524-6"` because `11524-6` does not encode 12-lead specificity).

---

## 2. Defects Discovered & Root-Cause Register

| Defect ID | Component | Observed Behavior | Expected Behavior | Exact Code Location | Root Cause | Clinical Impact | Implemented Correction | Regression Test | Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **DEF-001** | `PdfPig` Preprocessing & `ContextEngine` | `Inpatient Cardiology & Pulmonology Discharge Summary.pdf` produced glued headings (`SUMMARYCardiology`, `Creatinine124/76`, `MedicationsWarfarin`) and 1 `OTHER / Body` section. | Clean line/paragraph breaks, `Header: Value` table cells, coordinate mapping, and 7 distinct clinical sections. | [Program.cs](file:///d:/AI%20Extraction/backend/Program.cs#L461-L466), [PdfTextReconstructor.cs](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs), [ContextEngine.cs](file:///d:/AI%20Extraction/backend/Clinical/ContextEngine.cs#L43-L55) | Raw `page.Text` concatenated PDF text runs without baseline Y-grouping or horizontal gap analysis. | Flattened table headers onto blood pressure values and merged all 5 discharge medications into one unspaced block. | Implemented coordinate-aware `PdfTextReconstructor` with `PdfLineCoordinateSpan` mapping and expanded `ContextEngine` section patterns. | `PdfTextReconstructor_RepairsConcatenatedHeadings_TableColumns_AndDetectsAllFiveDischargeMedications`, `PdfPreprocessing_HandlesMultiPage_WrappedLines_MultiMedicationLines_AndAbbreviations` | **VERIFIED (`PASS`)** |
| **DEF-002** | `FoundryClinicalExtractor` & `ClinicalResultValidator` | `Azithromycin 500 mg` was omitted in run `5259006a-c76a-4564-acb4-1bf3fe625a20` (`4/5` discharge meds extracted) with zero warnings. | All 5 discharge medications extracted, and any unextracted source medication mention flagged for human review. | [FoundryClinicalExtractor.cs](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs#L17-L40), [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L458-L618) | GPT-6.1 Sol skipped the middle entry of an unspaced 5-drug paragraph; no second-pass source completeness check existed. | Missing active antimicrobial prescription on discharge summary. | Reconstructed per-line medication list, strengthened prompt list-exhaustiveness rules, and added `CheckSourceCompleteness` (formulary + general Rx regex + lab checks). | `DischargeSummary_StageByStageForensicTrace_AndRemediationVerification` | **VERIFIED (`PASS`)** |
| **DEF-003** | `ClinicalSafetyRuleEngine` | Emitted false-positive `Elevated Serum Creatinine (124 mg/dL)` and missed `Potassium 5.7 mEq/L` and second allergy (`Ibuprofen`). | Bind `Creatinine = 1.9 mg/dL` and `Potassium = 5.7 mEq/L` with exact quotes/offsets; alert on all confirmed allergies; reject unitless/ratio numbers; fail closed on conflicts. | [ClinicalSafetyRuleEngine.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs#L169-L360) | Legacy `CreatinineValueRegex` allowed zero spaces and optional unit, matching `"Creatinine124"` from `124/76 mmHg`; `°CK+` broke `\b`; `.FirstOrDefault()` only checked 1 allergy. | False critical renal alert (`124 mg/dL`) and missed hyperkalemia (`5.7 mEq/L`) alert. | Replaced regexes with `TryBindMeasurement` requiring compatible clinical units, ratio exclusion, physiological bounds, conflict fail-closed handling, and Observed vs. Derived fields. | `CreatinineSafetyEngine_NeverBindsBloodPressureToCreatinine_AndGroundsActualValue`, `SafetyEngine_And_TerminologySpecificity_VerifyHighRiskClinicalScenarios` | **VERIFIED (`PASS`)** |
| **DEF-004** | `TerminologyVerification` & `IndianOntologyResolver` | Previously labeled `24648-8` (`"XR Chest PA upright"`) as exact for `"chest X-ray (PA view)"` and `11524-6` (`"EKG study"`) as exact for `"12-lead ECG"`. | Standardize 5-tier semantic specificity (`exact`, `broader`, `narrower`, `ambiguous`, `unresolved`) and withhold `MatchedCode` when candidate code assumes unstated posture or omits 12-lead specificity. | [TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs#L40-L143), [IndianOntologyResolver.cs](file:///d:/AI%20Extraction/backend/Clinical/IndianOntologyResolver.cs#L305-L337) | `InvestigationSupported` previously included `("24648-8", "chest x-ray (pa view)")` and `("11524-6", "12-lead ecg")` without checking full LOINC axis definitions. | Overstated terminology specificity for imaging posture and ECG lead count. | Classified `"chest X-ray (PA view)"` vs `24648-8` as `narrower` and `"12-lead ECG"` vs `11524-6` as `broader` (`status = "uncertain"`, `MatchedCode = null`, `CandidateCode` populated). | `SafetyEngine_And_TerminologySpecificity_VerifyHighRiskClinicalScenarios`, `ExistingCodeDoesNotProveSemanticMatch` | **VERIFIED (`PASS`)** |
| **DEF-005** | `ClinicalResultValidator` & `ClinicalReview` | Discharge medications with `action = "none"` had `assertionStatus = "new_order"`, and `INCOMPLETE_PRESCRIPTION` did not explain why `dose = null` when `strength` is present. | Normalize verb-less `Discharge Medications` entries to `current_medication` and explicitly name missing attributes in `INCOMPLETE_PRESCRIPTION`. | [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs#L141-L147), [ClinicalReview.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalReview.cs#L31-L39) | Validator accepted raw `"new_order"` alongside `action = "none"` in `Discharge Medications`. | Ambiguous distinction between continuing discharge regimen and newly initiated orders. | Normalized verb-less discharge medications to `current_medication` and updated `INCOMPLETE_PRESCRIPTION` message. | `DischargeSummary_StageByStageForensicTrace_AndRemediationVerification` | **VERIFIED (`PASS`)** |
| **DEF-006** | `Contracts.cs`, `ExtractionWorkflow.cs`, `SqlExtractionRepository.cs`, Angular UI | No entity-level `Origin` / `EntityProvenanceDto`, no 8-dimension validation breakdown, and no opt-in synthetic raw model diagnostic capture. | Track entity origin, modifications, uncertainty reason, 8 validation dimensions, and opt-in synthetic diagnostic traces. | [Contracts.cs](file:///d:/AI%20Extraction/backend/Models/Contracts.cs), [ExtractionWorkflow.cs](file:///d:/AI%20Extraction/backend/Clinical/ExtractionWorkflow.cs), [SqlExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/SqlExtractionRepository.cs), [app.ts](file:///d:/AI%20Extraction/frontend/src/app/app.ts), [app.html](file:///d:/AI%20Extraction/frontend/src/app/app.html) | Contracts only stored run-level `ModelMetadataDto` and aggregate error/warning counts. | Reviewers could not tell whether an entity was extracted by GPT, confirmed by rules, or modified post-inference. | Added `Origin`, `EntityProvenanceDto`, `ValidationDimensionDto`, and opt-in synthetic capture; exposed Terminology and Origin in Angular row details. | `ForensicPipelineAuditTests`, `FoundryPostgresTests`, `app.spec.ts` | **VERIFIED (`PASS`)** |

---

## 3. Exact Source-Code Changes

1. **[backend/Models/Contracts.cs](file:///d:/AI%20Extraction/backend/Models/Contracts.cs):**
   - Extended `NormalizationDto` with `SemanticStatus` (`exact`, `broader`, `narrower`, `ambiguous`, `unresolved`), `CandidateCode`, `TerminologySource`, `TerminologyVersion`, and `MappingNote`.
   - Added `EntityProvenanceDto` and added `Origin` + `Provenance` to `ExtractedMedicationDto` and `ExtractedInvestigationDto` (plus `ClinicalCategory`, `Method`, `ResultValue`).
   - Added `ValidationDimensionDto` and `Dimensions` to `ValidationSummaryDto`.
   - Extended `ClinicalSafetyAlertDto` with `AlertType` (`OBSERVED_FACT_WARNING` vs `DERIVED_CLINICAL_INFERENCE`), `ObservedFact`, `EvidenceQuote`, `StartChar`, and `EndChar`.
2. **[backend/Clinical/ClinicalProviders.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalProviders.cs) & [backend/Clinical/ExtractionWorkflow.cs](file:///d:/AI%20Extraction/backend/Clinical/ExtractionWorkflow.cs):**
   - Added `EnableSyntheticDiagnosticCapture` (default `false`) and `SyntheticDiagnosticOutputDirectory` to `ExtractionOptions`.
   - Extended `FoundryExtraction` with `RawStructuredContent` and implemented `TryWriteSyntheticDiagnosticCapture` in `ExtractionWorkflow`.
3. **[backend/Clinical/FoundryClinicalExtractor.cs](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs):**
   - Strengthened `Prompt` (`clinical-v3`) for sequential multi-line medication list extraction, allergy separation, strength vs. dose distinction, and coordinated multi-test investigation splitting.
4. **[backend/Clinical/PdfTextReconstructor.cs](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs):**
   - Added `PdfLineCoordinateSpan` and `CoordinateMap` to `PdfReconstructionResult`, mapping every reconstructed line to its PDF page number, line index, character offsets (`[StartChar..EndChar]`), and PDF bounding box (`Left`, `Right`, `Bottom`, `Top`).
5. **[backend/Clinical/ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs) & [backend/Clinical/ClinicalReview.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalReview.cs):**
   - Populated `Origin`, `EntityProvenanceDto`, 5-tier `NormalizationDto`, `CheckSourceCompleteness` (formulary + general Rx regex + investigation section completeness), and 8-dimension `BuildValidationDimensions`.
6. **[backend/Clinical/ClinicalSafetyRuleEngine.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs):**
   - Enforced explicit unit requirement in `TryBindMeasurement`, separated `ObservedFact` from `DERIVED_CLINICAL_INFERENCE`, excluded negated allergies from allergy vigilance alerts, and added `HIGH_RISK_POSOLOGY` (daily Methotrexate) and `WEIGHT_BASED_POSOLOGY` rules.
7. **[backend/Clinical/TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs) & [backend/Clinical/IndianOntologyResolver.cs](file:///d:/AI%20Extraction/backend/Clinical/IndianOntologyResolver.cs):**
   - Implemented `ClassifyMedicationSemantic` and `ClassifyInvestigationSemantic` (`exact`, `broader`, `narrower`, `ambiguous`, `unresolved`) and withheld `24648-8` on `"chest X-ray (PA view)"` (`narrower` — upright posture unstated) and `11524-6` on `"12-lead ECG"` (`broader` — 12-lead specificity absent from `EKG study`).
8. **[backend/Storage/SqlExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/SqlExtractionRepository.cs) & Angular UI ([extraction.model.ts](file:///d:/AI%20Extraction/frontend/src/app/models/extraction.model.ts), [app.ts](file:///d:/AI%20Extraction/frontend/src/app/app.ts), [app.html](file:///d:/AI%20Extraction/frontend/src/app/app.html)):**
   - Added backward-compatible schema columns (`origin`, `provenance_json`) and populated provenance/terminology labels in the Angular review workspace.

---

## 4. Stage-by-Stage Source-to-Output Trace (`Inpatient Cardiology & Pulmonology Discharge Summary.pdf`)

Full machine-readable trace: [discharge-summary-forensic-trace.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/discharge-summary-forensic-trace.json)

| Stage | Artifact / Component | Summary of Transformation & Evidence |
| :--- | :--- | :--- |
| **1. `PDF_PARSED`** | [PdfTextReconstructor.cs](file:///d:/AI%20Extraction/backend/Clinical/PdfTextReconstructor.cs) | Converts raw `PdfPig` (`2,243` chars with glued `SUMMARYCardiology`, `Creatinine124/76`, `°CK+`, `MedicationsWarfarin`) into `2,324` chars across `28` coordinate-mapped lines (`Page 1`, bounding boxes verified) and `7` clinical sections (`CHIEF_COMPLAINT`, `ALLERGIES`, `EXAMINATION`, `LABS`, `DIAGNOSES`, `MEDICATIONS`, `LABS`). |
| **2. `FOUNDRY_RAW`** | PostgreSQL `extraction_runs` (`79aeee45-7598-4e49-9581-3cb8fe7ce2b9`) | Saved run `5259006a-c76a-4564-acb4-1bf3fe625a20` (`gpt-6.1-sol-2026-09-29`, `1020` prompt / `2121` completion tokens, `finish_reason = stop`) returned 6 medications (2 allergies + 4 discharge drugs, omitting `Azithromycin` from the middle of the flattened PDF paragraph) and 10 investigations. |
| **3. `COMPLETENESS_RECONCILED`** | `ClinicalResultValidator.CheckSourceCompleteness` | When evaluated on the 4-drug candidate from `5259006a-c76a-4564-acb4-1bf3fe625a20`, deterministically detects the omitted `"Azithromycin 500 mg orally once daily for 3 days to complete respiratory course."` and emits `UNEXTRACTED_SOURCE_MEDICATION_REVIEW` + `COMPLETENESS_REVIEW` alert without hallucinating a prescription. |
| **4. `VALIDATED`** | [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs) | Verifies 100% of quotes and character offsets; normalizes verb-less `Discharge Medications` entries from `new_order` to `current_medication` (`action = "none"`); preserves `dose = null` with explicit `INCOMPLETE_PRESCRIPTION` flag; computes 8 validation dimensions. |
| **5. `NORMALIZED`** | [TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs) | **Medications:** `6/7` exact RxNorm `IN` matches (`Warfarin` `11289`, `Aspirin` `1191`, `Azithromycin` `18631`, `Ondansetron` `26225`, `Pantoprazole` `40790`, `Ibuprofen` `5640`); `1/7` unresolved (`Penicillin` class name). **Investigations:** `3/10` exact LOINC matches (`serum potassium` `2823-3`, `serum creatinine` `2160-0`, `serum sodium` `2951-2`); `6/10` narrower (`chest X-ray (PA view)` vs `24648-8`, `K+`, `Creatinine`, `PT/INR`, `CBC`, `CRP`); `1/10` broader (`12-lead ECG` vs `11524-6`). |
| **6. `SAFETY_ENRICHED`** | [ClinicalSafetyRuleEngine.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalSafetyRuleEngine.cs) | Emits 6 source-grounded alerts: `Major Bleeding Risk: Warfarin + Aspirin` (`HIGH`), `QTc Prolongation Risk: Azithromycin + Ondansetron` (`MODERATE`), `Documented Drug Allergy Vigilance: Penicillin` (`MODERATE`), `Documented Drug Allergy Vigilance: Ibuprofen` (`MODERATE`), `Critical Potassium Value (5.7 mEq/L)` (`CRITICAL`, bound to `"K+ 5.7 mEq/L"`), and `Elevated Serum Creatinine (1.9 mg/dL)` (`HIGH`, bound to `"Creatinine 1.9 mg/dL"`). |

---

## 5. Medication & Investigation Extraction Analysis

### 5.1 Discharge Summary Medications (5 Prescriptions + 2 Allergies)

| Mention | Section | Strength | Administered Dose | Route | Frequency | Duration | Action | Assertion Status | RxNorm Code | Semantic Status | Origin |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `Penicillin` | `ALLERGIES` | `null` | `null` | `null` | `null` | `null` | `allergy` | `allergy` | `null` | `unresolved` | `LLM_EXTRACTED` |
| `Ibuprofen` | `ALLERGIES` | `null` | `null` | `null` | `null` | `null` | `allergy` | `allergy` | `5640` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |
| `Warfarin` | `MEDICATIONS` | `5 mg` | `null` (flagged) | `orally` | `once daily in the evening` | `null` | `none` | `current_medication` | `11289` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |
| `Aspirin` | `MEDICATIONS` | `81 mg` | `null` (flagged) | `orally` | `once daily in the morning` | `null` | `none` | `current_medication` | `1191` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |
| `Azithromycin` | `MEDICATIONS` | `500 mg` | `null` (flagged) | `orally` | `once daily` | `for 3 days` | `none` | `current_medication` | `18631` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |
| `Ondansetron` | `MEDICATIONS` | `4 mg` | `null` (flagged) | `orally` | `every 8 hours as needed` | `null` | `none` | `current_medication` | `26225` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |
| `Pantoprazole` | `MEDICATIONS` | `40 mg` | `null` (flagged) | `orally` | `once daily before breakfast` | `for 30 days` | `none` | `current_medication` | `40790` | `exact` (`IN`) | `LLM_AND_RULE_CONFIRMED` |

### 5.2 Discharge Summary Investigations (2 Baseline Results + 8 Follow-Up Orders)

| Mention | Category | Timing | Specimen | Result Value | Assertion Status | Matched LOINC | Candidate LOINC | Semantic Status | Reason / Mapping Note |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `K+` | `laboratory` | `null` | `null` | `5.7 mEq/L` | `historical` | `null` | `2823-3` | `narrower` | Shorthand `K+` does not state Serum/Plasma specimen in the mention token. |
| `Creatinine` | `laboratory` | `null` | `null` | `1.9 mg/dL` | `historical` | `null` | `2160-0` | `narrower` | Unqualified `Creatinine` does not state Serum/Plasma specimen in the mention token. |
| `Prothrombin time and INR` | `laboratory` | `in 3 days` | `null` | `null` | `new_order` | `null` | `6301-6` | `narrower` | `6301-6` encodes INR only (omitting PT) in platelet-poor plasma by coagulation assay. |
| `serum potassium` | `laboratory` | `in 48 hours` | `serum` | `null` | `new_order` | `2823-3` | `2823-3` | `exact` | Exact component (`Potassium`) and system (`Serum`) match. |
| `serum creatinine` | `laboratory` | `in 48 hours` | `serum` | `null` | `new_order` | `2160-0` | `2160-0` | `exact` | Exact component (`Creatinine`) and system (`Serum`) match. |
| `serum sodium` | `laboratory` | `in 48 hours` | `serum` | `null` | `new_order` | `2951-2` | `2951-2` | `exact` | Exact component (`Sodium`) and system (`Serum`) match. |
| `Complete blood count` | `laboratory` | `at 1-week follow-up` | `null` | `null` | `new_order` | `null` | `58410-2` | `narrower` | `58410-2` assumes automated count method not stated in `CBC`. |
| `C-reactive protein` | `laboratory` | `at 1-week follow-up` | `null` | `null` | `new_order` | `null` | `1988-5` | `narrower` | `1988-5` assumes Serum/Plasma mass concentration not stated in `CRP`. |
| `chest X-ray` (`(PA view)`) | `imaging` | `in 2 weeks` | `null` | `null` | `new_order` | `null` | `24648-8` | `narrower` | `24648-8` (`XR Chest PA upright`) assumes an **upright** patient posture not stated in `chest X-ray (PA view)`. |
| `12-lead ECG` | `electrophysiology_procedure` | `in 2 weeks` | `null` | `null` | `new_order` | `null` | `11524-6` | `broader` | `11524-6` (`EKG study`) is a general ECG study and does not encode the **12-lead** specificity stated in `12-lead ECG`. |

---

## 6. Before-and-After Benchmark & Provider Comparison Tables

Supporting artifacts:
- [before-after-evaluation-comparison.csv](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/before-after-evaluation-comparison.csv)
- [foundry-remediated-case-metrics.csv](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/foundry-remediated-case-metrics.csv)
- [microsoftfoundry-remediated-replay.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/microsoftfoundry-remediated-replay.json)
- [independent-preliminary-onnx-evaluation.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/independent-preliminary-onnx-evaluation.json)
- [entity-level-error-report.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/entity-level-error-report.json)

### 6.1 20-Case Development/Regression Dataset (Offline Replay of Saved Foundry Responses)

> **Dataset Classification Disclosure:** These 20 synthetic cases (`held_out_cases.json`) have influenced validator remediation rules and are strictly classified as **development/regression data**, not untouched independent validation.

| Metric | Raw Foundry Output (`gpt-6.1-sol`) | Prior Baseline Pipeline | Remediated Pipeline (Final Validated) |
| :--- | :--- | :--- | :--- |
| **Validator Acceptance Rate** | N/A (unvalidated) | `4 / 20 (20.0%)` | **`20 / 20 (100.0%)`** |
| **Medication Exact Micro F1** | `90.4%` (`TP=47, FP=9, FN=1`) | `90.4%` (on raw; 16 rejected) | **`93.1%` (`TP=47, FP=6, FN=1`)** |
| **Medication Normalized Micro F1** | `92.3%` (`TP=48, FP=8, FN=0`) | `92.3%` | **`95.0%` (`TP=48, FP=5, FN=0`)** |
| **Investigation Exact Micro F1** | `58.9%` | `59.3%` | **`74.2%` (`TP=33, FP=15, FN=8`)** |
| **Investigation Normalized Micro F1** | `88.2%` | `88.2%` | **`89.9%` (`TP=40, FP=8, FN=1`)** |
| **Medication Strength Accuracy** | `45.8%` (`22 / 48`) | `43.8%` | **`97.9%` (`47 / 48`)** |
| **Medication Route Accuracy** | `100.0%` (`41 / 41`) | `100.0%` | **`100.0%` (`41 / 41`)** |
| **Medication Frequency Accuracy** | `95.1%` (`39 / 41`) | `78.0%` | **`95.1%` (`39 / 41`)** |
| **Medication Duration Accuracy** | `90.9%` (`10 / 11`) | `81.8%` | **`90.9%` (`10 / 11`)** |
| **Medication Assertion Accuracy** | `89.6%` (`43 / 48`) | `72.9%` | **`97.9%` (`47 / 48`)** |
| **Investigation Assertion Accuracy** | `92.7%` (`38 / 41`) | `92.7%` | **`97.6%` (`40 / 41`)** |
| **Evidence Quote & Offset Validity** | `100.0%` (`101 / 101`) | `100.0%` | **`100.0%` (`101 / 101`)** |
| **Unsupported / Hallucinated Entities** | `0.0%` (`0 / 101`) | `0.0%` | **`0.0%` (`0 / 101`)** |

### 6.2 Provider-Specific Performance Comparison (Microsoft Foundry vs. Local BC5CDR INT8 ONNX, Fallback Disabled)

| Capability / Dataset | Microsoft Foundry (`gpt-6.1-sol`) | Local BC5CDR INT8 ONNX (`bc5cdr_int8` + Rules) | Architectural Difference |
| :--- | :--- | :--- | :--- |
| **20-Case Dev Set — Medication Normalized Micro F1** | **`95.0%`** (`TP=48, FP=5, FN=0`) | **`94.9%`** (`TP=47, FP=4, FN=1`) | ONNX combines BC5CDR token spans with `KnownDrugTerms` and `PosologyBinder`, emitting `LOCAL_COVERAGE_REDUCED`. |
| **20-Case Dev Set — Investigation Normalized Micro F1** | **`89.9%`** (`TP=40, FP=8, FN=1`) | **`87.8%`** (`TP=36, FP=6, FN=5`) | BC5CDR has no native `INVESTIGATION` neural label; all `LocalOnnx` investigations are `RULE_EXTRACTED` from `KnownLabTerms`. |
| **6-Case Preliminary Independent Set (`independent_preliminary_cases.json`)** | *Not run live (0 billable calls policy)* | **Med F1: `97.3%` (`18 TP, 0 FP, 1 FN`)**<br>**Inv F1: `85.7%` (`15 TP, 3 FP, 2 FN`)** | Preliminary synthetic benchmark created after rule freeze; labeled preliminary (not clinician-annotated). |

---

## 7. Automated Test Results & Live-Test Status

Full execution summary: [actual-test-execution-summary.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/actual-test-execution-summary.json)

1. **Backend .NET 10 + Isolated PostgreSQL 17 Integration Suite:**
   - Command: `$env:TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=cx_audit_test;Username=postgres;Password=dev_password_only"; dotnet test "d:\AI Extraction\backend\tests\Apollo.Backend.Tests.csproj"`
   - Result: **`143 passed, 0 failed, 9 skipped` (`152 total`, `6.95s`)**.
   - Includes all `4` isolated PostgreSQL transaction/rollback/privilege/historical tests in [FoundryPostgresTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryPostgresTests.cs) and all `13` forensic, PDF coordinate, safety, terminology specificity, and evaluation tests in [ForensicPipelineAuditTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/ForensicPipelineAuditTests.cs).
2. **Frontend Angular 21 Unit Tests:**
   - Command: `npm test -- --watch=false` in `d:\AI Extraction\frontend`
   - Result: **`32 passed, 0 failed` (`3.03s`)**.
3. **Frontend Angular 21 Production Build:**
   - Command: `npm run build` in `d:\AI Extraction\frontend`
   - Result: **Succeeded** (`d:\AI Extraction\frontend\dist\cx-review-ui`, `1.95s`).
4. **Controlled Live Foundry Verification Status (`Phase J`):**
   - Status: **`NOT VERIFIED` (Awaiting Explicit User Authorization)**.
   - In accordance with Phase J billing governance (*"Do not initiate a billable Foundry request without explicit approval"*), `0` live Foundry calls were dispatched. The single-call controlled discharge-summary test is ready to run upon your authorization.

---

## 8. Remaining Limitations & Final Engineering Readiness Assessment

### Remaining Known Limitations
1. **No Fresh Live Foundry Call Post-PDF Fix:** While the reconstructed discharge summary text, updated prompt (`clinical-v3`), and completeness reconciler have been verified offline against saved Foundry responses and `LocalOnnx`, a fresh single-call live GPT-6.1 Sol inference on the reconstructed PDF text has not yet been executed pending explicit billing approval.
2. **Ingredient-Level RxNorm Snapshot Scope:** The embedded offline snapshot (`terminology-verification.json`) verifies active RxNorm Ingredient (`IN`) and Multiple Ingredient (`MIN`) concepts (`02-Nov-2026`). Clinical Drug (`SCD`) and Branded Drug (`SBD`) codes are intentionally classified as `narrower` (`status = "uncertain"`) unless full strength/form/brand equivalence is verified.
3. **Method/Posture/Lead-Specific LOINC Codes:** Mentions whose candidate LOINC concept assumes an unstated posture (`24648-8` `XR Chest PA upright` for `chest X-ray (PA view)`), omits lead count (`11524-6` `EKG study` for `12-lead ECG`), or assumes an unstated specimen/assay (`K+`, `Creatinine`, `PT/INR`, `CBC`, `CRP`) withhold `MatchedCode` (`status = "uncertain"`) and record the candidate code in `CandidateCode` with `SemanticStatus = "narrower"` or `"broader"`.
4. **No OCR for Scanned Image PDFs:** `PdfTextReconstructor` operates on selectable-text PDFs and returns `422 OCR_REQUIRED` on scanned image-only PDFs.
5. **Synthetic Evaluation Datasets Only:** Neither the 20-case development dataset nor the 6-case preliminary independent dataset has been independently dual-annotated by external clinicians.

### Final Subsystem Readiness Statuses

| Subsystem | Status | Evidence Summary |
| :--- | :--- | :--- |
| **1. Structure-Preserving PDF Preprocessing (`PdfTextReconstructor`)** | **`PASS`** | Reconstructs headers, 2D table cells, chemical ions (`K+`), wrapped lines, and coordinate maps (`PdfLineCoordinateSpan`); verified by unit and fixture tests. |
| **2. Extraction Observability & Entity Provenance** | **`PASS`** | Every entity tracks `Origin` (`LLM_EXTRACTED`, `RULE_EXTRACTED`, `LLM_AND_RULE_CONFIRMED`, `RECOVERED_FROM_SOURCE`, `HUMAN_REVIEW_REQUIRED`) and `EntityProvenanceDto`; opt-in synthetic raw capture implemented. |
| **3. Source-Level Completeness Reconciliation** | **`PASS`** | Detects omitted formulary drugs, non-formulary pattern-matched prescriptions, and `LABS`-section investigations without hallucinating prescriptions. |
| **4. Multi-Dimensional Clinical Validator** | **`PASS`** | Reports 8 independent validation dimensions; `20/20` offline replay acceptance; `100%` quote and offset validity. |
| **5. Source-Grounded Clinical Safety Engine** | **`PASS`** | Separates `ObservedFact` from `DERIVED_CLINICAL_INFERENCE`; fixes `124 mg/dL` creatinine defect (`1.9 mg/dL`) and `5.7 mEq/L` potassium binding; fails closed on missing units or conflicting values. |
| **6. RxNorm & LOINC Semantic Normalization** | **`PASS`** | Enforces 5-tier semantic specificity (`exact`, `broader`, `narrower`, `ambiguous`, `unresolved`); prevents false-exact labeling of `24648-8` (upright posture) and `11524-6` (unspecified lead count). |
| **7. PostgreSQL Transactional Persistence** | **`PASS`** | Verified against live PostgreSQL 17 (`cx_audit_test`) with isolated schemas, rollback triggers, runtime role privilege checks, and read-after-write fidelity. |
| **8. Angular 21 Clinical Review UI** | **`PASS`** | `32/32` Vitest unit tests and production build pass; displays provider/fallback provenance, terminology specificity, entity origin, and highlighted source evidence. |
| **9. Offline Regression & Preliminary Independent Evaluation** | **`PARTIAL`** | Offline Foundry replay (`20` cases) and preliminary independent `LocalOnnx` evaluation (`6` cases) completed and reported honestly; independent clinician-adjudicated gold annotations remain a future prerequisite. |
| **10. Controlled Live Foundry Verification (Post-PDF Fix)** | **`NOT VERIFIED`** | Intentionally not executed without explicit user authorization to respect billing governance. |

