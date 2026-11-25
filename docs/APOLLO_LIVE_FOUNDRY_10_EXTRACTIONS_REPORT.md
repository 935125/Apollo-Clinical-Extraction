# Apollo Clinical Extraction Platform — 10 Live Paid Microsoft Foundry (`gpt-6.1-sol`) Extractions via Azure Service Bus

**Date:** 25 November 2026  
**Pipeline Exercised:** `POST /api/v1/emr/notes/{id}/corrections` + `POST /api/v1/emr/notes/{id}/sign` → PostgreSQL `emr_note_versions` + `emr_outbox_events` → `EmrMessagingService` → Azure Service Bus (`apollo-clinical-sb-dev.servicebus.windows.net` / `op-clinical-notes`) → `.NET EmrConsumer` → **Microsoft Foundry (`gpt-6.1-sol-2026-09-29`)** → Clinical Evidence Validation & RxNorm/LOINC Normalization → PostgreSQL `extraction_runs` → REST `/api/v1/queue/{noteId}` & Angular UI (`http://localhost:4200/`).

**Guardrails Enforced:**
- `Extraction:Provider = MicrosoftFoundry`
- `Foundry:AllowLocalFallback = false` (zero ONNX fallback permitted)
- `Foundry:MaxRetries = 0` (strict 10-call cap; zero automatic retries)
- `Foundry:MaxCompletionTokens = 2000`
- Synthetic clinical notes only (zero real PHI)

---

## Summary Metrics

- **Total Paid Live Foundry Calls Attempted:** `10 / 10`
- **Completed & Persisted via `gpt-6.1-sol-2026-09-29`:** `9 / 10` (`Cases 1, 2, 3, 5, 6, 7, 8, 9, 10`)
- **Rejected by Strict Clinical Evidence Guard (`DeadLetter`, no fallback):** `1 / 10` (`Case 4` — `AZURE_INVALID_EVIDENCE`, single HTTP 200 Foundry response rejected before persistence)
- **Recorded Tokens (9 persisted runs):** `8,578` prompt + `4,386` completion = **`12,964` total tokens** (`estimatedCostUsd` is not emitted by the Foundry response payload; actual Azure billing appears in Azure Portal)
- **Raw JSON Evidence:** [`docs/foundry-live-10-evidence/live-foundry-10-results.json`](foundry-live-10-evidence/live-foundry-10-results.json)

---

## Per-Case Results Table

| # | Patient / Ver | Clinical Scenario | Outcome | Model | Tokens (In / Out / Total) | Inference / E2E | Extracted Highlights |
| ---: | --- | --- | --- | --- | --- | --- | --- |
| 1 | `P100234` v21 | Hypertension & Dyslipidaemia | **Ready** | `gpt-6.1-sol-2026-09-29` | 948 / 390 / **1,338** | 7.08s / 10.17s | 3 meds (`Amlodipine`, `Atorvastatin`, `Telmisartan` half), 2 invs (`CBC automated`, `Serum Creatinine` LOINC `2160-0`) |
| 2 | `P100235` v12 | Community Acquired Pneumonia | **Ready** | `gpt-6.1-sol-2026-09-29` | 960 / 395 / **1,355** | 5.98s / 8.07s | 3 meds (`Amoxicillin-Clavulanate`, `Azithromycin`, `Paracetamol` PRN), 2 invs (`Chest X-ray PA view`, `CBC`) |
| 3 | `P100237` v17 | Type 2 Diabetes & Aspirin Allergy | **Ready** | `gpt-6.1-sol-2026-09-29` | 954 / 436 / **1,390** | 6.27s / 8.07s | 3 meds (`Aspirin` **allergy**, `Metformin`, `Sitagliptin`), 3 invs (`HbA1c` `4548-4`, `Lipid Profile`, `Serum Creatinine` **conditional**) |
| 4 | `P100234` v23 | Anticoagulation Discontinuation | **Failed (`AZURE_INVALID_EVIDENCE`)** | `gpt-6.1-sol` (HTTP 200, rejected) | *(not persisted)* | 13.89s / 18.18s | Evidence guard rejected non-verbatim/invalid span; dead-lettered cleanly with zero retries and zero ONNX fallback |
| 5 | `P100235` v14 | Asthma Exacerbation & Inhalers | **Ready** | `gpt-6.1-sol-2026-09-29` | 962 / 421 / **1,383** | 6.38s / 10.07s | 3 meds (`Budesonide-Formoterol` inhaled, `Salbutamol` PRN, `Montelukast`), 2 invs (`Spirometry` **conditional**, `Chest X-ray` **conditional**) |
| 6 | `P100237` v19 | Hypothyroidism & Iron Anaemia | **Ready** | `gpt-6.1-sol-2026-09-29` | 945 / 405 / **1,350** | 5.81s / 8.07s | 2 meds (`Levothyroxine` current, `Ferrous Sulfate` new), 3 invs (`TSH`, `Serum Ferritin`, `CBC automated`) |
| 7 | `P100234` v25 | Acid Peptic Disease & Negated NSAID | **Ready** | `gpt-6.1-sol-2026-09-29` | 953 / 594 / **1,547** | 8.93s / 12.11s | 2 meds (`Ibuprofen` **negated**, `Pantoprazole` new), 2 invs (`Stool H. pylori antigen`, `LFT`) |
| 8 | `P100235` v16 | Uncomplicated UTI | **Ready** | `gpt-6.1-sol-2026-09-29` | 936 / 326 / **1,262** | 5.26s / 8.07s | 2 meds (`Nitrofurantoin`, `Potassium Citrate`), 2 invs (`Urine Routine`, `Urine Culture & Sensitivity`) |
| 9 | `P100237` v21 | Stable Angina & Cardiology | **Ready** | `gpt-6.1-sol-2026-09-29` | 956 / 686 / **1,642** | 9.28s / 10.07s | 3 meds (`Clopidogrel`, `Rosuvastatin`, `Isosorbide Mononitrate`), 3 invs (`12-lead ECG`, `Echocardiogram` `34552-0`, `Fasting Lipid Profile`) |
| 10 | `P100234` v27 | Gout Flare & Historical Allopurinol | **Ready** | `gpt-6.1-sol-2026-09-29` | 964 / 733 / **1,697** | 10.11s / 14.12s | 3 meds (`Allopurinol` **historical**, `Colchicine`, `Etoricoxib`), 3 invs (`Serum Uric Acid` `3084-1`, `Serum Creatinine` `2160-0`, `CRP`) |
