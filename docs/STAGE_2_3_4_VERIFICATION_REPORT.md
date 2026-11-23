# Apollo Clinical Extraction Platform — Stage 2, 3 & 4 Comprehensive Verification and Hardening Report

**Audit Timestamp:** 2026-11-20T16:42:00+05:30  
**Workspace Root:** `d:\AI Extraction`  
**Platform Stack:** Angular 21 (`frontend/`), ASP.NET Core / .NET 10 (`backend/`), PostgreSQL 17 + `pgvector` (`backend/tools/pgsql`)  
**Primary Cloud Model:** Microsoft Foundry (`gpt-6.1-sol`, actual model snapshot `gpt-6.1-sol-2026-09-29`, `/openai/v1/chat/completions`)  
**Fallback Local Model:** Local BC5CDR INT8 ONNX (`clinical_model_int8.onnx`) + rule-based posology/catalog pipeline  

---

## 1. Executive Summary & Honest Stage Determinations

| Stage | Scope | Determination | Rationale & Audited Evidence |
|---|---|:---:|---|
| **Stage 2** | Complete End-to-End Application Integration | **PARTIAL** | Real E2E pipeline (`Angular 21 → ASP.NET Core → Microsoft Foundry / LocalOnnx → ClinicalResultValidator → IndianOntologyResolver → PostgreSQL → Angular UI`) is wired and verified via live HTTP E2E ([stage2-e2e.ps1](file:///d:/AI%20Extraction/backend/tests/Verification/stage2-e2e.ps1)), Playwright browser automation ([check-foundry-live.mjs](file:///d:/AI%20Extraction/frontend/verification/check-foundry-live.mjs), [e2e_verify.mjs](file:///d:/AI%20Extraction/frontend/e2e_verify.mjs)), and isolated PostgreSQL schema/role tests ([FoundryPostgresTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryPostgresTests.cs)). Marked **PARTIAL** because `npm test` (Vitest 4 vs `@angular/build:unit-test`) fails at runner discovery (`Vitest failed to find the runner`), and duplicate POST submissions intentionally create distinct `extraction_runs` rows rather than enforcing idempotency keys. |
| **Stage 3** | Automatic ONNX Fallback and Reliability | **PASS** | All 15 fault-injection scenarios in [FoundryWorkflowTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryWorkflowTests.cs) and [AuditRemediationTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/AuditRemediationTests.cs) pass. Transient errors (`408`, `429`, `500`, `502`, `503`, `504`, network failure, timeout, malformed JSON, invalid evidence) trigger `LocalOnnx` fallback when enabled and fail closed (`503 EXTRACTION_UNAVAILABLE`) when `AllowLocalFallback=false` or `FallbackProvider=None`. Operator/config errors (`401`, `403`, `404`, missing endpoint/credentials, model mismatch) and content policy refusals (`422 AZURE_CONTENT_POLICY`) never fall back silently. Reliability harness ([ReliabilityMeasurementTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/ReliabilityMeasurementTests.cs)) executed 45 local requests (30 sequential + 3×5 concurrent) against isolated PostgreSQL (`cx_audit_test`) with 0 errors and verified singleton `SlidingWindowOnnxEngine` reuse. |
| **Stage 4** | Clinical Extraction Accuracy, Safety & Normalization | **PARTIAL** | All 125 local/isolated tests pass (`0 failed`) and the single-pass, budget-capped 20-call live Microsoft Foundry benchmark ([microsoftfoundry-20261120T110623773.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/microsoftfoundry-20261120T110623773.json)) completed within budget (`20/20` HTTP 200 responses, `$0.294738` total estimated cost vs `$5.00` cap). Marked **PARTIAL** for three honest engineering and scientific reasons: (1) the 20 scenarios in [held_out_cases.json](file:///d:/AI%20Extraction/backend/tests/Evaluation/held_out_cases.json) were inspected during prior development and serve only as a **development/regression benchmark** (no separately sealed, independently clinically annotated post-freeze test set exists); (2) with fail-closed validation and ONNX fallback disabled, `ClinicalResultValidator` accepted **4/20** live Foundry cases end-to-end and rejected **16/20** cases (`HTTP_503` / `AZURE_INVALID_EVIDENCE`) due to strict intra-clause attribute partitioning, even though raw Foundry candidates achieved **97.9% medication recall (90.4% F1)** and **100% verbatim quote/offset validity**; and (3) terminology verification ([TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs)) restricts `Status="matched"` to semantically equivalent concepts (**53/87** medication entries, **10/91** investigation entries), quarantining brand/strength aliases and method/view-assuming LOINC codes as `uncertain`. |

---

## 2. Stage 2 — End-to-End Application Trace & Verification

### 2.1 Pipeline Code Trace
1. **Frontend Input & Service Layer:**
   - [app.ts](file:///d:/AI%20Extraction/frontend/src/app/app.ts) and [app.html](file:///d:/AI%20Extraction/frontend/src/app/app.html) bind clinical note input, PDF upload, loading states, error banners, and provenance badges.
   - [extraction.service.ts](file:///d:/AI%20Extraction/frontend/src/app/services/extraction.service.ts) dispatches `POST /api/v1/extractions` (JSON) and `POST /api/v1/extractions/pdf` (multipart PDF), `GET /api/v1/extractions`, `GET /api/v1/extractions/{id}`, and `GET /api/v1/extractions/{id}/fhir`.
   - [extraction.model.ts](file:///d:/AI%20Extraction/frontend/src/app/models/extraction.model.ts) matches backend DTO contracts in [Contracts.cs](file:///d:/AI%20Extraction/backend/Models/Contracts.cs).
2. **Backend Startup & Dependency Injection:**
   - [Program.cs](file:///d:/AI%20Extraction/backend/Program.cs) loads `.env` via `LoadLocalDotEnv()`, binds `ExtractionOptions` and `FoundryOptions`, registers singletons for `WordPieceTokenizer` and `SlidingWindowOnnxEngine`, registers `IFoundryClinicalExtractor` (`FoundryClinicalExtractor`), `ILocalClinicalExtractor` (`OnnxClinicalExtractor`), `ClinicalResultValidator`, and `ExtractionWorkflow`.
   - `Database:AutoMigrate` (default `false`) separates schema DDL initialization from normal least-privilege runtime requests.
3. **Provider Execution & Strict Schema Enforcement:**
   - [FoundryClinicalExtractor.cs](file:///d:/AI%20Extraction/backend/Clinical/FoundryClinicalExtractor.cs) calls `POST {Endpoint}/openai/v1/chat/completions` with `response_format.type = "json_schema"`, `strict = true`, and schema exported by `ClinicalCandidateJson.Schema` in [ClinicalProviders.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalProviders.cs).
4. **Deterministic Evidence Grounding & Clinical Safety Validation:**
   - [ClinicalResultValidator.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalResultValidator.cs) verifies verbatim evidence quotes (`Verify`), exact character start/end offsets (`FindName`), scoped actions (`ClinicalScope.Action`), partitioned entity attribute spans (`ClinicalScope.AttributesForEntity`), frequency grounding ([ClinicalAttributeGrounding.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalAttributeGrounding.cs)), and complex/weight-based regimen flags ([ClinicalReview.cs](file:///d:/AI%20Extraction/backend/Clinical/ClinicalReview.cs)).
5. **Terminology Normalization & Quarantine:**
   - [IndianOntologyResolver.cs](file:///d:/AI%20Extraction/backend/Clinical/IndianOntologyResolver.cs) and [TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs) verify drug mentions against pinned NLM RxNorm (`02-Nov-2026`) and lab mentions against LOINC (`tx.fhir.org`), emitting `matched` only for verified semantic equivalents and routing `uncertain`/`unmatched` items to `oov_quarantine_items`.
6. **Atomic PostgreSQL Persistence:**
   - [SqlExtractionRepository.cs](file:///d:/AI%20Extraction/backend/Storage/SqlExtractionRepository.cs) writes `extraction_runs`, `extracted_medications`, `extracted_investigations`, and `oov_quarantine_items` in a single `ReadCommitted` transaction.

### 2.2 Live End-to-End Verification Evidence
- **Live HTTP E2E Script:** [stage2-e2e.ps1](file:///d:/AI%20Extraction/backend/tests/Verification/stage2-e2e.ps1) submitted the synthetic clinical note with correlation tag `STAGE2-E2E-20261120150729` ([STAGE2-E2E-20261120150729.post.json](file:///d:/AI%20Extraction/backend/tests/Verification/out/STAGE2-E2E-20261120150729.post.json), [STAGE2-E2E-20261120150729.get.json](file:///d:/AI%20Extraction/backend/tests/Verification/out/STAGE2-E2E-20261120150729.get.json)):
  - **15/15 checks passed:** HTTP 200 OK, `provider: "foundry"`, `model: "gpt-6.1-sol-2026-09-29"`, `inferenceExecuted: true`, `schemaValidationStatus: "passed"`, `saved: true`, both medications (`metformin` continue, `amoxicillin` start) and investigations (`CBC`, `serum creatinine`, conditional `chest X-ray`) extracted with exact character offsets, negated penicillin allergy not treated as active allergy, and round-trip `GET /api/v1/extractions/{id}` matched the persisted record.
- **Playwright Browser Verification:**
  - [check-foundry-live.mjs](file:///d:/AI%20Extraction/frontend/verification/check-foundry-live.mjs) and [e2e_verify.mjs](file:///d:/AI%20Extraction/frontend/e2e_verify.mjs) verified desktop and mobile rendering, provenance display, and confirmed the UI never displays `"Powered by Microsoft Foundry"` when `LocalOnnx` performs the extraction.
- **Database Integrity & Role Separation ([FoundryPostgresTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryPostgresTests.cs)):**
  - Verified on dedicated test database `cx_audit_test` (never targeting `cx_platform` for destructive fixtures):
    1. `RuntimeRole_CanSaveAndReadWithoutSchemaOrDatabaseCreation`: Least-privilege role `cx_runtime` (`NOSUPERUSER NOCREATEDB NOCREATEROLE`, denied DDL with SQLSTATE `42501`) saves and reads extractions with `Database:AutoMigrate = false`.
    2. `TextAndPdf_PrimaryAndRealOnnx_SaveOneParentWithChildrenAndProviderMetadata`: 4 runs (Text/PDF × Foundry/ONNX) persist 4 parent runs, 12 medications, 8 investigations, and 8 quarantine items (`Penicillin` unmapped + `chest X-ray` view-uncertain).
    3. `FailureDuringQuarantineInsert_RollsBackParentAndAllChildren_WithoutModelRetry`: Trigger-induced failure on `oov_quarantine_items` rolls back parent and child rows atomically (`0` rows left in any table) and returns HTTP 503 `PERSISTENCE_FAILED` without retrying Foundry or falling back to ONNX.
    4. `HistoricalRun_WithNullProviderMetadata_RemainsReadable`: Legacy rows with `provider_metadata = NULL` deserialize cleanly.

---

## 3. Stage 3 — Automatic ONNX Fallback, Fault Matrix & Reliability

### 3.1 Fault Injection Matrix Verification
All fault-injection tests execute against injectable HTTP handlers (`SimulatedAzure` in [FoundryWorkflowTests.cs](file:///d:/AI%20Extraction/backend/tests/FoundryWorkflowTests.cs) and [AuditRemediationTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/AuditRemediationTests.cs)) without consuming Azure quota:

| Injected Failure | Configured Policy | Verified Behavior | HTTP Status | Fallback Reason / Error Code |
|---|---|---|:---:|---|
| **HTTP 408 Request Timeout** | `MaxRetries=1`, fallback enabled | 2 Foundry attempts $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_TIMEOUT` |
| **HTTP 429 Too Many Requests** | `MaxRetries=2`, `Retry-After` header | Honors retry policy $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_RATE_LIMITED` |
| **HTTP 500 Internal Server Error** | `MaxRetries=1`, fallback enabled | 2 Foundry attempts $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_UNAVAILABLE` |
| **HTTP 502 / 503 / 504** | `MaxRetries=1`, fallback enabled | 2 Foundry attempts $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_UNAVAILABLE` |
| **Network Connection Failure** | `HttpRequestException` | Bounded retries $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_UNREACHABLE` |
| **Request / Retry Timeout** | `TimeoutSeconds` deadline exceeded | Cancels in-flight call $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_TIMEOUT` |
| **Malformed Model JSON** | Invalid JSON / schema mismatch | Rejects response $\rightarrow$ `LocalOnnx` fallback (`schemaValidationStatus: "failed"`) | `200` | `AZURE_UNUSABLE_JSON` |
| **Invalid Evidence Offsets** | Fabricated quote / cross-drug attribute | `ClinicalResultValidator` rejects $\rightarrow$ `LocalOnnx` fallback | `200` | `AZURE_INVALID_EVIDENCE` |
| **Transient Failure + Fallback Disabled** | `AllowLocalFallback=false` or `FallbackProvider=None` | Fails closed; zero ONNX calls; zero DB writes | `503` | `EXTRACTION_UNAVAILABLE` |
| **HTTP 401 Unauthorized** | Invalid API key / token | Explicit auth error; **no fallback** | `401` | `AZURE_CREDENTIALS_INVALID` |
| **HTTP 403 Forbidden** | RBAC / deployment access denied | Explicit access error; **no fallback** | `403` | `AZURE_ACCESS_DENIED` |
| **HTTP 404 Not Found** | Missing deployment | Explicit config error; **no fallback** | `503` | `AZURE_DEPLOYMENT_MISCONFIGURED` |
| **Missing Endpoint / Credentials** | Blank endpoint or key | Explicit config error; **no fallback** | `401` / `503` | `AZURE_CREDENTIALS_MISSING` / `AZURE_NOT_CONFIGURED` |
| **Content Policy Refusal** | `content_filter` / refusal string | Explicit 422 error; **no silent fallback** | `422` | `AZURE_CONTENT_POLICY` |
| **Database Unavailable** | Repository throw / SQL trigger failure | Transaction rolled back; **no model retry or fallback** | `503` | `PERSISTENCE_FAILED` |

### 3.2 Local Reliability & Performance Measurement
Artifact: [reliability.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/reliability.json) (generated by `ReliabilityMeasurementTests.RepeatedLocalRequests_RecordLatencyErrorsAndMemoryWithoutScalabilityClaims` on isolated PostgreSQL database `cx_audit_test`):
- **Workload:** 30 sequential requests followed by 3 batches of 5 concurrent requests (**45 total requests**, 0 cloud calls).
- **Full API Request Latency (including real PostgreSQL transaction):**
  - **p50:** `54.75 ms`
  - **p95:** `162.58 ms`
- **ONNX Session Inference Latency (`InferenceDurationMs`):**
  - **p50:** `2.50 ms`
  - **p95:** `33.54 ms`
- **Error Rate:** `0 / 45` (`0.0%`), all 45 runs persisted to `extraction_runs`.
- **Singleton Reuse:** `ReferenceEquals(engine, factory.Services.GetRequiredService<SlidingWindowOnnxEngine>()) == true`.
- **Memory Observation (test-host process including xUnit & GC):** Working set ranged from `151.1 MB` (request 0) to `174.1 MB` (request 45); managed heap ranged from `11.1 MB` to `15.4 MB`. *(Scope note: This 45-request in-process observation verifies singleton reuse and short-run stability; it does not claim multi-hour leak freedom or production throughput limits.)*

---

## 4. Stage 4 — Clinical Extraction Accuracy, Safety & Normalization

### 4.1 Dataset Provenance & Governance Disclosure
- The 20 clinical scenarios in [held_out_cases.json](file:///d:/AI%20Extraction/backend/tests/Evaluation/held_out_cases.json) (`SHA-256: 55C343DAD9CFDC78699B4EFB23DA93E7A6879BC0B09D477E942827A2A43046DC`) originate from the repository's original 20-case evaluation set.
- **Methodological Classification:** Because these 20 scenarios were inspected during prior pipeline development and validator debugging, they are classified strictly as a **development/regression benchmark**, not an untouched held-out test set. Furthermore, the original reference annotations contain known omissions (e.g., current baseline medications in Case 1 `Allopurinol 100mg` and `Lisinopril 10mg`, documented allergies in Case 2 `Penicillin` and Case 20 `penicillin`, and penicillin desensitization in Case 20 are present in the clinical notes but omitted from `expected_medications`) and 4 ambiguous duplicate drug mentions without `mention_start` offsets (`Allopurinol` in Case 1, `Aspirin` in Case 2, `Levetiracetam` in Case 9, `Levothyroxine` in Case 19).
- A one-time post-freeze evaluation gate (`SealedFinalEvaluation_OnceAfterImplementationFreeze` in [HeldOutBenchmarkRunner.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/HeldOutBenchmarkRunner.cs)) is implemented and remains `[SKIP]` until an external, independently annotated dataset and SHA-256 freeze manifest (`FINAL_EVALUATION_MANIFEST`) are supplied.

### 4.2 Live Microsoft Foundry Budget & Per-Request Ledger
- **Ledger Artifact:** [foundry-development-ledger.jsonl](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/foundry-development-ledger.jsonl)
- **Benchmark Artifact:** [microsoftfoundry-20261120T110623773.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/microsoftfoundry-20261120T110623773.json)
- **Enforced Constraints:**
  - Maximum calls: **20** (`Calls = 20`, `Stopped = false`)
  - Automatic retries: **0** (`MaxRetries = 0`)
  - Local ONNX fallback: **Disabled** (`AllowLocalFallback = false`, `FallbackProvider = "None"`)
  - Output token cap per call (including reasoning): **2,000** (`max_completion_tokens = 2000`; observed max was `1,035`)
  - Spending budget cap: **$5.00 USD** (conservative Standard long-context rates: `$6.00 / 1M` input tokens, `$18.00 / 1M` output tokens)
  - Total tokens consumed across 20 calls: **14,293 input tokens**, **11,610 output tokens** (including **936 reasoning tokens**), **25,903 total tokens**
  - Total estimated spend: **$0.294738 USD** (**$4.705262 USD** remaining under the $5.00 cap)

| # | Case ID | HTTP | Model Snapshot | Input Tokens | Output Tokens (incl. Reasoning) | Reasoning Tokens | Latency (ms) | Est. Cost (USD) | Remaining Budget (USD) | Raw Meds / Invs | Validator Gate (`AllowLocalFallback=false`) |
|---:|---|:---:|---|---:|---:|---:|---:|---:|---:|:---:|---|
| 1 | `heldout-001-geriatric` | 200 | `gpt-6.1-sol-2026-09-29` | 752 | 993 | 102 | 16,145.4 | `$0.022386` | `$4.977614` | 4 / 4 | `HTTP_503` (shared timing `"in 2 weeks"` on coordinated lab) |
| 2 | `heldout-002-cardiology` | 200 | `gpt-6.1-sol-2026-09-29` | 749 | 1,035 | 167 | 17,244.0 | `$0.023124` | `$4.954490` | 5 / 3 | **ACCEPTED** (`HTTP 200`) |
| 3 | `heldout-003-hematology` | 200 | `gpt-6.1-sol-2026-09-29` | 707 | 483 | 0 | 7,802.2 | `$0.012936` | `$4.941554` | 4 / 3 | `HTTP_503` (shared instructions `"avoid NSAIDs like Ibuprofen."`) |
| 4 | `heldout-004-endocrinology` | 200 | `gpt-6.1-sol-2026-09-29` | 734 | 496 | 0 | 7,567.8 | `$0.013332` | `$4.928222` | 3 / 4 | `HTTP_503` (shared timing `"in 3 months"` on coordinated lab) |
| 5 | `heldout-005-pulmonology` | 200 | `gpt-6.1-sol-2026-09-29` | 698 | 536 | 56 | 8,545.1 | `$0.013836` | `$4.914386` | 2 / 2 | `HTTP_503` (dose `"40"` + unit `"mg"` vs binder `Dose=null`) |
| 6 | `heldout-006-rheumatology` | 200 | `gpt-6.1-sol-2026-09-29` | 708 | 646 | 94 | 11,301.9 | `$0.015876` | `$4.898510` | 2 / 3 | `HTTP_503` (shared timing `"baseline"` on coordinated lab) |
| 7 | `heldout-007-gastroenterology` | 200 | `gpt-6.1-sol-2026-09-29` | 716 | 717 | 51 | 10,685.2 | `$0.017202` | `$4.881308` | 3 / 3 | `HTTP_503` (`Endoscopy` historical cue outside single sentence) |
| 8 | `heldout-008-infectious-disease` | 200 | `gpt-6.1-sol-2026-09-29` | 714 | 740 | 87 | 12,845.7 | `$0.017604` | `$4.863704` | 3 / 2 | `HTTP_503` (`cefazolin` historical action vs `documentedAction`) |
| 9 | `heldout-009-neurology` | 200 | `gpt-6.1-sol-2026-09-29` | 682 | 320 | 0 | 6,750.8 | `$0.009852` | `$4.853852` | 2 / 3 | `HTTP_503` (shared timing `"baseline"` on coordinated lab) |
| 10 | `heldout-010-nephrology` | 200 | `gpt-6.1-sol-2026-09-29` | 732 | 836 | 0 | 12,168.1 | `$0.019440` | `$4.834412` | 3 / 5 | `HTTP_503` (shared timing `"in 7 days"` on coordinated lab) |
| 11 | `heldout-011-oncology` | 200 | `gpt-6.1-sol-2026-09-29` | 726 | 370 | 0 | 6,107.6 | `$0.011016` | `$4.823396` | 3 / 1 | `HTTP_503` (single-line numbered plan `Instructions` prefix clipped) |
| 12 | `heldout-012-hfref` | 200 | `gpt-6.1-sol-2026-09-29` | 722 | 448 | 0 | 8,137.9 | `$0.012396` | `$4.810000` | 4 / 2 | `HTTP_503` (shared timing `"in 14 days"` on coordinated lab) |
| 13 | `heldout-013-dermatology` | 200 | `gpt-6.1-sol-2026-09-29` | 697 | 468 | 0 | 7,490.1 | `$0.012606` | `$4.798394` | 2 / 5 | `HTTP_503` (`adalimumab` conditional cue `"Screen... prior to initiating"`) |
| 14 | `heldout-014-psychiatry` | 200 | `gpt-6.1-sol-2026-09-29` | 703 | 579 | 167 | 13,831.2 | `$0.014640` | `$4.783754` | 2 / 1 | **ACCEPTED** (`HTTP 200`) |
| 15 | `heldout-015-pain` | 200 | `gpt-6.1-sol-2026-09-29` | 701 | 406 | 0 | 6,637.7 | `$0.011514` | `$4.772240` | 2 / 1 | `HTTP_503` (complex titration frequency `"at bedtime for 3 days, then BD"`) |
| 16 | `heldout-016-urology` | 200 | `gpt-6.1-sol-2026-09-29` | 710 | 327 | 0 | 5,372.0 | `$0.010146` | `$4.762094` | 2 / 2 | **ACCEPTED** (`HTTP 200`) |
| 17 | `heldout-017-pediatrics` | 200 | `gpt-6.1-sol-2026-09-29` | 708 | 328 | 0 | 5,460.4 | `$0.010152` | `$4.751942` | 2 / 0 | `HTTP_503` (`Instructions` prefix `"Prescribe "` before `Amoxicillin`) |
| 18 | `heldout-018-ophthalmology` | 200 | `gpt-6.1-sol-2026-09-29` | 711 | 450 | 0 | 6,718.3 | `$0.012366` | `$4.739576` | 3 / 0 | `HTTP_503` (`Moxifloxacin` inferred route `"ophthalmic"` from `"eye drops"`) |
| 19 | `heldout-019-thyroid` | 200 | `gpt-6.1-sol-2026-09-29` | 711 | 551 | 76 | 9,195.1 | `$0.014184` | `$4.725392` | 2 / 2 | **ACCEPTED** (`HTTP 200`) |
| 20 | `heldout-020-allergy` | 200 | `gpt-6.1-sol-2026-09-29` | 712 | 881 | 136 | 14,691.4 | `$0.020130` | `$4.705262` | 3 / 4 | `HTTP_503` (compound timing `"baseline; prior to protocol"` on coordinated labs) |

### 4.3 Side-by-Side Development Benchmark Comparison (`audit-v1` Evaluator)
Artifacts:
- Local ONNX: [localonnx-20261120T105634790.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/localonnx-20261120T105634790.json)
- Microsoft Foundry (Both Raw Structured Output & Validator-Gated End-to-End with Fallback Disabled): [microsoftfoundry-20261120T110623773.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/microsoftfoundry-20261120T110623773.json)

| Metric | LocalOnnx Pipeline (20 Cases) | Raw Microsoft Foundry Candidate (`gpt-6.1-sol`, 20 Cases) | End-to-End Foundry + Fail-Closed Validator (`AllowLocalFallback=false`, 4/20 Accepted) |
|---|:---:|:---:|:---:|
| **API Request Latency (p50 / p95)** | `11.30 ms` / `113.10 ms` | `8,162.89 ms` / `16,406.40 ms` | `8,162.89 ms` / `16,406.40 ms` |
| **HTTP Error / Rejection Rate** | `0 / 20` (`0.0%`) | `0 / 20` (`0.0%` transport/schema) | `16 / 20` (`80.0%` validator `HTTP_503`) |
| **Medication Exact Micro (TP / FP / FN)** | `48 / 7 / 0` | `47 / 9 / 1` | `9 / 2 / 39` |
| **Medication Exact Micro (P / R / F1)** | `87.3% / 100.0% / 93.2%` | `83.9% / 97.9% / 90.4%` | `81.8% / 18.8% / 30.5%` |
| **Medication Normalized Micro (P / R / F1)** | `87.3% / 100.0% / 93.2%` | `83.9% / 97.9% / 90.4%` | `81.8% / 18.8% / 30.5%` |
| **Medication Normalized Macro (P / R / F1)** | `89.7% / 100.0% / 93.4%` | `87.6% / 98.3% / 91.2%` | `90.0% / 20.0% / 18.8%` |
| **Med Strength Accuracy (All / Documented)** | `37/48 (77.1%)` / `32/43 (74.4%)` | `21/48 (43.8%)` / `16/43 (37.2%)` | `2/48 (4.2%)` / `2/43 (4.7%)` |
| **Med Route Accuracy (All / Documented)** | `35/41 (85.4%)` / `32/37 (86.5%)` | `36/41 (87.8%)` / `33/37 (89.2%)` | `8/41 (19.5%)` / `7/37 (18.9%)` |
| **Med Frequency Accuracy (All / Documented)** | `29/41 (70.7%)` / `29/41 (70.7%)` | `32/41 (78.0%)` / `32/41 (78.0%)` | `5/41 (12.2%)` / `5/41 (12.2%)` |
| **Med Duration Accuracy (All / Documented)** | `7/11 (63.6%)` / `7/11 (63.6%)` | `9/11 (81.8%)` / `9/11 (81.8%)` | `0/11 (0.0%)` / `0/11 (0.0%)` |
| **Med Assertion Status Accuracy** | `26/48 (54.2%)` | `35/48 (72.9%)` | `5/48 (10.4%)` |
| **Med Missing-Attribute Null Accuracy (Route / Strength)** | `3/4 (75.0%)` / `5/5 (100.0%)` | `3/4 (75.0%)` / `5/5 (100.0%)` | `1/4 (25.0%)` / `0/5 (0.0%)` |
| **Med Verbatim Evidence Quote Validity** | `55/55 (100.0%)` | `56/56 (100.0%)` | `11/11 (100.0%)` |
| **Med Character Offset Validity** | `55/55 (100.0%)` | `56/56 (100.0%)` | `11/11 (100.0%)` |
| **Med Unsupported Entity Rate** | `0/55 (0.0%)` | `0/56 (0.0%)` | `0/11 (0.0%)` |
| **Investigation Exact Micro (TP / FP / FN)** | `31 / 11 / 10` | `27 / 23 / 14` | `1 / 7 / 40` |
| **Investigation Exact Micro (P / R / F1)** | `73.8% / 75.6% / 74.7%` | `54.0% / 65.9% / 59.3%` | `12.5% / 2.4% / 4.1%` |
| **Investigation Normalized Micro (TP / FP / FN)** | `35 / 7 / 6` | `30 / 20 / 11` | `2 / 6 / 39` |
| **Investigation Normalized Micro (P / R / F1)** | `83.3% / 85.4% / 84.3%` | `60.0% / 73.2% / 65.9%` | `25.0% / 4.9% / 8.2%` |
| **Investigation Normalized Macro (P / R / F1)** | `79.2% / 80.6% / 79.8%` | `56.9% / 67.6% / 60.7%` | `16.7% / 3.7% / 3.7%` |
| **Investigation Assertion Status Accuracy** | `34/41 (82.9%)` | `28/41 (68.3%)` | `2/41 (4.9%)` |
| **Investigation Verbatim Evidence Quote Validity** | `42/42 (100.0%)` | `50/50 (100.0%)` | `8/8 (100.0%)` |
| **Investigation Character Offset Validity** | `42/42 (100.0%)` | `50/50 (100.0%)` | `8/8 (100.0%)` |
| **Investigation Unsupported Entity Rate** | `0/42 (0.0%)` | `0/50 (0.0%)` | `0/8 (0.0%)` |
| **Full-Prescription Evaluable Cases** | `0 / 20` (gold lacks full 8-field annotation) | `0 / 20` (gold lacks full 8-field annotation) | `0 / 20` (gold lacks full 8-field annotation) |

#### Why Strength Accuracy Differs Between Raw Foundry (`43.8%`) and Reference Gold (`strength` vs `dose`/`doseUnit`):
In `FoundryClinicalExtractor.Prompt`, the system prompt instructs:
> *"Strength is product strength per tablet or concentration; dose is the administered amount (e.g. 'two tablets'), doseUnit its unit. Do not substitute strength for dose."*
When a clinical note writes `"Prescribe Allopurinol 200mg PO daily"` without specifying a separate tablet strength vs tablet count, `gpt-6.1-sol` frequently populates `Strength: null, Dose: "200", DoseUnit: "mg"` (22 of 43 documented gold strength mentions), whereas the reference gold places `"200mg"` in `strength` and omits `dose`/`dose_unit`. Because `ClinicalEvaluator` strictly compares `strength` to `strength` without conflating `dose + doseUnit` with `strength`, those 22 predictions are scored as `strength` attribute mismatches.

### 4.4 Failure Taxonomy (`audit-v1` Evaluator)

| Pipeline / Layer | Domain | Failure Kind & Category | Count | Root Cause Analysis |
|---|---|---|---:|---|
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `attribute / attribute_mismatch` | 56 | Primarily `Strength: null` when model put `"200"` + `"mg"` into `Dose` + `DoseUnit` (22 cases), plus complex regimen frequencies (`"QDS PRN"`, `"at bedtime for 3 days, then BD"`), and route phrasing (`"eye drops"` vs `"ophthalmic"`). |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FP / Evaluation matching error / incomplete gold` | 4 | Baseline meds present in note (`Allopurinol 100mg`, `Lisinopril 10mg` in Case 1; `penicillin` desensitization in Case 20) omitted from reference gold. |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FP / Allergy mention` | 2 | Documented allergies (`Penicillin` in Case 2, `penicillin` in Case 20) extracted per prompt instructions but omitted from reference gold. |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FP / Negated mention` | 1 | `Denies Aspirin allergy` (Case 2) extracted as negated mention; not in reference gold. |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FP / Historical mention` | 1 | `IV cefazolin` (Case 8) extracted as historical; not in reference gold. |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FP / Conditional instruction` | 1 | `Hold Lisinopril 10mg` duplicate mention in Case 1 without `mention_start` disambiguation. |
| **Raw Foundry (`gpt-6.1-sol`)** | Medications | `FN / unrecognized` | 1 | `Prednisolone` vs `Prednisolone acetate` (Case 18) where strict evaluator refuses substring/salt equivalence. |
| **Raw Foundry (`gpt-6.1-sol`)** | Investigations | `FN / unrecognized` | 11 | Strict name matching rejects qualified phrasing (`Fasting Lipid Profile` vs `Lipid Profile`, `Chest X-ray PA view` vs `Chest X-ray`, `routine 30-minute EEG` vs `EEG`, `12-lead ECG` vs `ECG`, `Serum PSA (Prostate Specific Antigen)` vs `Serum PSA`, `Ultrasound Kidneys, Ureters, and Bladder...` vs `Ultrasound`, `Serum Free T4` vs `Free T4`). |
| **Raw Foundry (`gpt-6.1-sol`)** | Investigations | `FP / Evaluation matching error / incomplete gold` | 13 | Corresponding qualified predictions above plus procedures (`colonoscopy`, `cardiac monitoring`, ` albumin-creatinine ratio`). |
| **Raw Foundry (`gpt-6.1-sol`)** | Investigations | `FP / Historical mention` | 6 | Baseline results (`Stable eGFR 38 mL/min`, `Current HbA1c 8.6%`, `peak flow 65%`, `Endoscopy`, `rapid urease test`, `eGFR 24`, `potassium 5.4`) extracted as historical; omitted from reference gold. |
| **LocalOnnx Pipeline** | Medications | `attribute / attribute_mismatch` | 55 | Rule-based `PosologyBinder` and `ClinicalReview.Apply` clearing posology on complex/taper/weight-based regimens (`COMPLEX_REGIMEN_REVIEW`). |
| **LocalOnnx Pipeline** | Medications | `FP` (Allergy: 2, Negated: 1, Conditional: 1, Incomplete gold: 3) | 7 | Extra mentions present in note text (`Allopurinol 100mg`, `Lisinopril 10mg`, `Penicillin` allergy, `Denies Aspirin allergy`). |
| **LocalOnnx Pipeline** | Investigations | `FN / unrecognized` | 6 | `Chest X-ray PA view` vs `Chest X-ray`, `12-lead ECG` vs `ECG`, `Serum Free T4` vs `Free T4`. |

### 4.5 Clinical Safety & Adversarial Verification
Verified deterministically in [AuditRemediationTests.cs](file:///d:/AI%20Extraction/backend/tests/Evaluation/AuditRemediationTests.cs) (`0` cloud calls):
1. **Methotrexate Weekly vs. Daily (`WrongFrequencyCannotPassWordOrNumericOverlap`, `WeeklyFrequencyIsGroundedExactly`):**
   - `"Prescribe methotrexate once weekly; never take daily."` with candidate frequency `"daily"` throws `InvalidClinicalEvidenceException` because `ClinicalAttributeGrounding.Frequencies` excludes negated frequency tokens (`never take daily`).
2. **Insulin Concentration vs. Administered Units (`InsulinConcentrationIsNotTheAdministeredUnits`):**
   - `"Start insulin 100 units/mL, inject 12 units SC nightly."` binds `Dose = "12 units"` (rejecting `"100 units"` concentration as the administered dose).
3. **Pediatric Weight-Based Regimens (`WeightBasedRegimenRemainsUnresolvedAndPreservesDirections`):**
   - `"Start amoxicillin 20 mg/kg/dose twice daily."` triggers `ClinicalReview.IsComplex` $\rightarrow$ clears `Strength`, `Dose`, `Frequency`, preserves verbatim `Instructions`, and attaches `COMPLEX_REGIMEN_REVIEW` and `INCOMPLETE_PRESCRIPTION` warnings rather than emitting an unsafe uncalculated dose.
4. **Stop/Hold/Restart Complex Instructions (`StopRestartInstructionsAreNotOneCompletePrescription`):**
   - `"Hold warfarin then restart at 2 mg daily."` clears single-prescription posology and flags `COMPLEX_REGIMEN_REVIEW`.
5. **Coordinated Drug Attribute Isolation (`CoordinatedDrugAttributesDoNotBleedWithoutASecondActionVerb`):**
   - `"Start metformin 500 mg twice daily and amlodipine 5 mg daily."` rejects cross-drug attachment of `"twice daily"` to `amlodipine` via `ClinicalScope.AttributesForEntity`.
6. **Cross-Test Timing/Specimen Isolation (`TimingAndSpecimenCannotBeBorrowedFromAnotherTest`):**
   - `"Order CBC today. Collect urine culture next week."` rejects borrowing `"next week"` or `"urine"` onto `CBC`.

### 4.6 Terminology Normalization Audit (RxNorm & LOINC)
Artifacts: [terminology-current.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/terminology-current.json), [terminology-verification.json](file:///d:/AI%20Extraction/backend/Clinical/terminology-verification.json), [TerminologyVerification.cs](file:///d:/AI%20Extraction/backend/Clinical/TerminologyVerification.cs):
- **Pinned Terminology Versions:** NLM RxNorm `02-Nov-2026` (`RxNav REST API`); HL7/LOINC via `tx.fhir.org R4 CodeSystem/$lookup`.
- **Code Existence vs. Semantic Equivalence:**
  - All **87 medication entries** and **91 investigation entries** in `IndianOntologyResolver` point to active, existing RxNorm CUIs (`0` retired/alien CUIs) and existing LOINC codes (`0` lookup failures).
  - However, to prevent false clinical precision:
    - **Medications:** `TerminologyVerification.IngredientSupported` emits `Status = "matched"` (with an RxNorm CUI) only when the clinical mention matches the generic ingredient name (`IN` / `MIN`) in RxNorm (**53 / 87 entries**). The remaining **34 / 87 entries** (Indian brand names such as `Thyronorm`, `Glycomet`, `Pan 40`, `Dolo 650`, `Augmentin`, `Combodart`, and strength-suffixed aliases such as `Atorva 20`, `Telma 40`) return `Status = "uncertain"`, `StandardId = null`, emit `TERMINOLOGY_UNCERTAIN`, and are quarantined into `oov_quarantine_items` because ingredient codes do not verify brand, salt, or tablet strength.
    - **Investigations:** `TerminologyVerification.InvestigationSupported` emits `Status = "matched"` (with a LOINC code) only for the **10 / 91 entries** (5 LOINC codes: `24558-9` abdominal US, `11524-6` ECG, `34552-0` 2D Echo, `600-7` blood culture, `24356-8` complete urine examination) where the LOINC concept introduces no unstated method, view count, panel subtype, or vascular Doppler assumption. The remaining **81 / 91 entries** (including `chest x-ray` mapped to 2-view `36643-5`, `ultrasound renal` mapped to vascular Doppler `24604-1`, `troponin-i` mapped to Troponin T `6598-7`, `quantiferon-tb gold` mapped to `71447-7`, `eeg` mapped to `11502-2`, and `cbc` mapped to automated blood panel `58410-2`) return `Status = "uncertain"`, `StandardId = null`, and are routed to `oov_quarantine_items` for human terminology review.

---

## 5. Complete Verification Command & Artifact Matrix

| Verification Suite | Command / Artifact | Result |
|---|---|---|
| **Local + Isolated PostgreSQL Suite (134 total tests, 0 cloud calls)** | `$env:TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=cx_audit_test;Username=postgres;Password=dev_password_only"; dotnet test backend/tests/Apollo.Backend.Tests.csproj` ([local-suite-verification.trx](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/local-suite-verification.trx)) | **125 Passed, 0 Failed, 9 Skipped** (8 live Foundry facts + 1 post-freeze final evaluation fact skipped) |
| **Budgeted 20-Call Live Foundry Benchmark** | [microsoftfoundry-20261120T110623773.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/microsoftfoundry-20261120T110623773.json) & [foundry-development-ledger.jsonl](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/foundry-development-ledger.jsonl) | **20/20 HTTP 200 OK**, `$0.294738` total spend (`$4.705262` remaining), **4/20** validator-accepted end-to-end without fallback, **16/20** rejected with `HTTP_503` (`AZURE_INVALID_EVIDENCE`) |
| **LocalOnnx 20-Case Development Benchmark** | [localonnx-20261120T105634790.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/development/localonnx-20261120T105634790.json) | **20/20 HTTP 200 OK**, Medication Normalized Micro F1 `93.2%`, Investigation Normalized Micro F1 `84.3%` |
| **45-Request Local Reliability & Concurrency Benchmark** | [reliability.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/reliability.json) | **45/45 HTTP 200 OK** (`0%` error rate), full request p50 `54.75 ms` / p95 `162.58 ms`, ONNX session p50 `2.50 ms` / p95 `33.54 ms` |
| **Pinned Terminology Verification** | [terminology-current.json](file:///d:/AI%20Extraction/backend/artifacts/audit-remediation/terminology-current.json) & [terminology-verification.json](file:///d:/AI%20Extraction/backend/Clinical/terminology-verification.json) | **87 medications** (53 matched, 34 uncertain), **91 investigations** (10 matched, 81 uncertain) |

