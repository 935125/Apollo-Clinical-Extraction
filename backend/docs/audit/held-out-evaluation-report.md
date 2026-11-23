# Held-Out Evaluation & Microsoft Foundry Inference Verification Report

**Platform:** Microsoft Foundry  
**Target Model:** `gpt-4.1-mini` (Model Version: `2025-04-14`, Global Standard)  
**Evaluation Scope:** 20 Unseen Clinical Narratives across 18 Specialties  
**Execution Environment:** Windows 11 Pro, Python 3.13, ASP.NET Core .NET 10, Angular 21  

---

## 1. Executive Summary & Anti-Mock Integrity Audit

### Strict Fallback Policy:
In accordance with engineering directives, **silent fallback to synthetic extraction has been eliminated**:
- Setting `EXTRACTION_MODEL_PROVIDER=foundry` strictly requires valid `AZURE_AI_FOUNDRY_ENDPOINT` and `AZURE_AI_FOUNDRY_API_KEY`.
- If credentials or endpoint configurations are missing, the service raises an explicit `ConfigurationError` and returns HTTP 503 `CONFIGURATION_ERROR` to the gateway.
- Synthetic extraction is isolated strictly to `EXTRACTION_MODEL_PROVIDER=mock` for deterministic offline automated test suites.

| Verification Item | Status | Measured Behavior |
| :--- | :---: | :--- |
| **Silent Mock Fallback** | **ELIMINATED** | `ConfigurationError` raised when `EXTRACTION_MODEL_PROVIDER=foundry` without valid credentials. |
| **Model Identity Telemetry** | **VERIFIED** | Model deployment identity (`gpt-4.1-mini`), version (`2025-04-14`), and provider type (`microsoft_foundry_live`) propagated to API & UI. |
| **Token Usage Tracking** | **VERIFIED** | Tracks `prompt_tokens`, `completion_tokens`, `total_tokens`, and estimated USD cost. |
| **Attribute Normalization** | **VERIFIED** | Deterministic strength & formulation matching; RxNorm CUIs & LOINC separated from local codes. |
| **Unseen Evaluation Dataset** | **VERIFIED** | 20 complex, held-out clinical encounters never exposed to prompt engineering or heuristics. |

---

## 2. Microsoft Foundry gpt-4.1-mini Configuration

### Deployment Parameters:
- **Platform:** Microsoft Foundry (Global Standard)
- **Model:** `gpt-4.1-mini`
- **Model Version:** `2025-04-14`
- **Deployment Name:** `gpt-4.1-mini`
- **Structured Output Format:** Strict JSON Schema (`LlmExtractionOutput`) via `client.beta.chat.completions.parse`
- **Timeout Budget:** 30.0 seconds per call
- **Retry Policy:** Bounded exponential backoff (Max 3 retries, $2^{\text{attempt}}\text{s}$ backoff on RateLimitError `429` or transient errors)
- **Token Pricing:** $\$0.15$ per 1,000,000 input tokens, $\$0.60$ per 1,000,000 output tokens

### Windows Development Configuration Instructions:
To run against the live Microsoft Foundry endpoint on Windows development machines, set the environment variables via PowerShell:

```powershell
# Set for current PowerShell session
$env:EXTRACTION_MODEL_PROVIDER = "foundry"
$env:AZURE_AI_FOUNDRY_ENDPOINT = "https://<your-resource-name>.openai.azure.com/"
$env:AZURE_AI_FOUNDRY_API_KEY = "<your-azure-foundry-api-key>"
$env:FOUNDRY_MODEL_DEPLOYMENT = "gpt-4.1-mini"

# Or persist permanently to User environment variables
[System.Environment]::SetEnvironmentVariable("EXTRACTION_MODEL_PROVIDER", "foundry", "User")
[System.Environment]::SetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT", "https://<your-resource-name>.openai.azure.com/", "User")
[System.Environment]::SetEnvironmentVariable("AZURE_AI_FOUNDRY_API_KEY", "<your-azure-foundry-api-key>", "User")
[System.Environment]::SetEnvironmentVariable("FOUNDRY_MODEL_DEPLOYMENT", "gpt-4.1-mini", "User")
```

---

## 3. Held-Out Evaluation Dataset (20 Previously Unseen Scenarios)

The held-out benchmark ([held_out_dataset.py](file:///D:/AI%20Extraction/src/worker/cx_worker/evaluation/held_out_dataset.py)) tests previously unseen clinical encounters:

1. **Geriatric Polypharmacy & Renal Dosing:** Allopurinol dose escalation (100mg to 200mg), Lisinopril conditional hold, renal panel surveillance.
2. **Post-PCI Acute Coronary Syndrome:** Aspirin 100mg OD 12-month duration, Atorvastatin 40mg, penicillin allergy vs aspirin tolerance, 2D Echo EF follow-up.
3. **Anticoagulation Bridging & Discontinuation:** Discontinuation of Warfarin 5mg 5 days pre-procedure, Paracetamol PRN, avoidance of NSAIDs, morning INR check.
4. **Endocrinology T2D Escalation:** SGLT2i (Empagliflozin 10mg) + DPP-4i (Linagliptin 5mg), historical Metformin GI intolerance.
5. **Pulmonology Asthma Exacerbation:** Oral Prednisolone burst (40mg x 5 days), inhaled Salbutamol 100mcg QDS PRN, PA chest radiograph.
6. **Rheumatology Methotrexate Protocol:** Weekly Methotrexate 15mg + weekly Folic Acid 5mg (avoiding co-administration day), baseline LFT/CBC.
7. **Gastroenterology H. pylori Eradication:** Triple therapy (Omeprazole 20mg BD + Amoxicillin 1000mg BD + Clarithromycin 500mg BD x 14 days).
8. **Infectious Disease Oral Stepdown:** Cellulitis transition from IV cefazolin to oral Cephalexin 500mg QID x 10 days.
9. **Neurology Focal Epilepsy:** Levetiracetam dose escalation (500mg to 750mg BD) with therapeutic EEG and LFT/CBC monitoring.
10. **Nephrology CKD Stage 4:** Emergency discontinuation of Lisinopril 20mg for potassium 5.4 mEq/L, initiation of Amlodipine 5mg and Sodium Bicarbonate.
11. **Medical Oncology Supportive Care:** Antiemetic regimen (Ondansetron 8mg BD x 3d, Dexamethasone 4mg x 3d, Metoclopramide PRN), ANC nadir blood count.
12. **Heart Failure GDMT Quad Therapy:** Sacubitril/Valsartan 24/26mg BD, Dapagliflozin 10mg, Bisoprolol 5mg, Spironolactone 25mg.
13. **Dermatology Biologic Screening:** Adalimumab pre-screening with Quantiferon-TB Gold, Hepatitis B/C serologies, LFTs.
14. **Psychiatry Antidepressant Cross-Taper:** Mirtazapine taper/cessation, Sertraline initiation, baseline ECG for QTc evaluation.
15. **Pain Medicine Neuropathic Titration:** Gabapentin 300mg titration protocol with renal creatinine check.
16. **Urology BPH Dual Therapy:** Tamsulosin 0.4mg + Finasteride 5mg, PSA level, renal/bladder ultrasound.
17. **Pediatric Acute Otitis Media:** Weight-based Amoxicillin high-dose regimen (800mg BD x 10 days), PRN Paracetamol suspension.
18. **Ophthalmology Cataract Post-Op Taper:** Prednisolone 1% eye drops taper, Moxifloxacin 0.5% drops, conditional Aspirin hold for intraocular bleed.
19. **Endocrinology Post-Thyroidectomy Surveillance:** Levothyroxine titration (100mcg to 125mcg) with 6-week TSH/Free T4 monitoring.
20. **Allergy & Immunology Desensitization:** ICU penicillin desensitization protocol, historical anaphylaxis contraindication, cardiac monitoring.

---

## 4. Terminology Normalization Audit & Attribute Verification

### Catalog Restructuring:
- **Verified Standard Identifiers:**
  - Standardized RxNorm Concept Unique Identifiers (e.g. `860975` for Metformin 500mg, `860981` for Metformin 1000mg).
  - Standardized LOINC Numbers (e.g. `2160-0` for Serum Creatinine, `4548-4` for HbA1c, `58410-2` for CBC).
- **Separation of Local Formulary:** Internal inventory codes (e.g. `RX-MET-500`, `LAB-CREAT`) are maintained alongside standard clinical concepts.
- **Deterministic Strength Checks:**
  - If a patient is prescribed *Metformin 1000mg*, the normalizer deterministically binds to RxNorm `860981` (`RX-MET-1000`). It is strictly prohibited from binding to `RX-MET-500` even though both share the generic token *Metformin*.
  - When strength is omitted in the clinical narrative, unit-dose formulation codes are not fabricated.
- **Fuzzy Token Retrieval Calibration:**
  - Fuzzy similarity scores from RapidFuzz are utilized exclusively as an initial candidate retrieval signal ($\ge 80.0$).
  - Calibrated clinical confidence is assigned based on verified attribute alignment, never directly from raw string token overlap.

---

## 5. Live vs. Mock Verification Status

| Check | Provider: `mock` | Provider: `foundry` (Credentials Configured) | Provider: `foundry` (Credentials Missing) |
| :--- | :--- | :--- | :--- |
| **Inference Source** | `SyntheticModelAdapter` | Azure AI Foundry `gpt-4.1-mini` | Fails fast with `ConfigurationError` |
| **HTTP Status Code** | 200 OK | 200 OK | 503 Service Unavailable |
| **Fallback Behavior** | Deterministic local | Live model execution | Zero silent fallback |
| **Use Case** | Offline unit & CI regression tests | Production live clinical extraction | Misconfiguration detection |

