# APOLLO STAGE 6 — CLINICAL RELIABILITY AND SECURITY REPORT

23 November 2026 (Asia/Calcutta). **Zero billable Foundry calls. Independent clinical evaluation: PENDING EXTERNAL REVIEW. Hospital production: NOT READY.**

## 1. Root-cause analysis

The most consequential finding is a benchmark execution defect. The Stage 5 runner loaded `backend/artifacts/model_int8.onnx`, which does not exist. The weights reside at `backend/artifacts/bc5cdr_latest/onnx/model_int8.onnx`. `SlidingWindowOnnxEngine` substitutes deterministic token scoring when its model is absent. Stage 5 did not assert artifact availability or executed inference windows. Its 70.5% medication recall **was not a measurement of BC5CDR inference**.

Replaying the original paths reproduced medication TP=62, FP=1, FN=26 and investigation TP=66, FP=7, FN=13 exactly. A newly added `HasOnnxModel` assertion then intentionally failed, exposing the wiring error. No original raw neural outputs exist for this benchmark because neural inference did not occur. The captured legacy replay is explicitly identified as deterministic scoring.

Enabling the existing real weights before clinical-rule edits yielded medication TP=72, FP=1, FN=16. This is a corrected measurement configuration, not model training or a model performance improvement. **Raw real-model spans are identical before and after the clinical corrections for all 35 notes.**

Other exact causes:

- Direct fallback investigation timing recognized only `today`; interval phrases and trailing shared timing were not bound.
- Category was never populated on the direct fallback path. Foundry/read-back enrichment did not make that direct result complete.
- Specimen depended on terminology metadata and could choose `Blood` from broad evidence instead of documented `Serum`. Unqualified K+/creatinine results do not establish specimen.
- Frequency discarded PRN when a primary interval existed; formulation reduced `oral suspension` to `suspension`.
- Dose increases were classified as current medication. Generic `labs`/`repeat` cues and multi-line scope caused historical measurements to become orders.
- The allergy-prefix rule captured `Patient` from `Allergies: Patient denies ...` as a drug.
- The evaluator scores attributes on undetected entities as incorrect, combining entity recall and attribute binding in its headline scores.

Evidence: [original Stage 5 archive](stage6-evidence/original-stage5-results.json), [legacy deterministic replay](stage6-evidence/before.json), [real-model baseline](stage6-evidence/real-model-before.json), [actual fallback-adapter final results](stage6-evidence/after.json). Captures retain source text and pipeline output; real-model captures include raw spans. Final captures also assert model/tokenizer hashes and executed windows.

## 2. Failed-case classification

Every evaluator failure in the original 35 cases and both real-model replays has a row containing case, entity, field, expected/actual value, cause, rationale and source evidence in [failure-classification.csv](stage6-evidence/failure-classification.csv) and [JSON](stage6-evidence/failure-classification.json). The [35-case inventory](stage6-evidence/FAILED_CASE_INVENTORY.md) summarizes counts. Reproduce triage with `python backend/tests/Evaluation/stage6_audit.py`. These are engineering judgments, not independent clinical adjudication.

| Cause | Original Stage 5 | Real model before rules | Final fallback |
|---|---:|---:|---:|
| Model entity omission | 0 | 53 | 53 |
| Unsupported model capability | 98 | 98 | 32 |
| Incorrect attribute binding | 70 | 71 | 0 |
| Clinical scope error | 3 | 38 | 37 |
| Assertion classification error | 9 | 9 | 0 |
| Normalization issue | 21 | 21 | 19 |
| Evaluation contract mismatch | 157 | 5 | 5 |
| Missing source information | 4 | 4 | 4 |

These are **failure rows**, not distinct clinical errors: a missed entity also produces failures for each annotated attribute. Totals are 362 original, 299 corrected baseline and 150 final. Original medication omissions inherit the evaluation-contract classification because the runner claimed ONNX while using deterministic scoring; zero original model-omission rows does not assert a perfect model.

| Cases | Failure and disposition |
|---|---|
| 002, 023, 024 | Four unqualified baseline K+/creatinine results have gold `Serum` without local source support. Classified as missing source information; preserve null and frozen gold. |
| 003 | Generic allergy-prefix capture of `Patient`: scope error, corrected. |
| 004, 010, 029 | Dose escalation classified as current medication: assertion error, corrected while preserving prior mentions. |
| 006, 007, 014, 015, 018, 033 | PRN lost from documented interval: binding error, corrected. |
| 014 | Qualified suspension reduced to generic formulation: normalization error, corrected. |
| 005 | Source-qualified `Chest X-ray (PA view)` fails matching to broader gold `Chest X-ray`: evaluation-contract mismatch; no alias expansion or deletion of source view. |
| 003, 026, 027, 035 | Catalog output `Troponin`, `MRI`, or `CT` omits documented subtype/organ/modality: genuine normalization/boundary loss, not an acceptable equivalence. Remains unresolved. |
| 013, 022 | Uric-acid/potassium thresholds retained without explicit investigation orders: scope limitation remains. |

Final medication entity misses comprise **nine raw-model omissions and seven post-model confidence/context suppressions**. The latter are conservative policy abstentions, not absent raw predictions; their confidences are recorded in the inventory. Final investigation misses comprise eight catalog omissions, four truncated qualified names and one source-view matching discrepancy. Gold and evaluator equivalences remain unchanged.

## 3. Source-code corrections

| Source | Correction |
|---|---|
| [ClinicalPipelineService.cs](../backend/Clinical/ClinicalPipelineService.cs) | Local assertion scope, numeric-result classification, dose changes, generic allergy-token guard, category/specimen/timing population, weekly/PRN instructions, explicit ONNX origins and rule-modification provenance. Receipt model counts now describe raw mentions before filtering and overlap rule counts. |
| [LocalClinicalAttributes.cs](../backend/Clinical/LocalClinicalAttributes.cs) | Source-substring timing with list/separate-order boundaries; explicit local specimen only. |
| [PosologyBinder.cs](../backend/Clinical/PosologyBinder.cs) | Interval plus PRN, qualified suspension/solution, and an attribute boundary at an adjacent prescription even when its drug was omitted. |
| [ClinicalResultValidator.cs](../backend/Clinical/ClinicalResultValidator.cs) | Share existing category classification across provider paths. |
| [ClinicalReview.cs](../backend/Clinical/ClinicalReview.cs) | Missing local timing/specimen findings; retain conservative complex-regimen review. |
| [Stage 5 evaluator runner](../backend/tests/Evaluation/Stage5IndependentBenchmarkTests.cs) | Correct artifact paths, require real artifacts, write corrected output separately from archived Stage 5 evidence. |
| [Stage 6 evaluator runner](../backend/tests/Evaluation/Stage6ReliabilityBenchmarkTests.cs) | Freeze gold hash; assert artifact hashes/windows; capture raw spans, unreviewed pipeline and actual `OnnxClinicalExtractor` output; separate entity and attribute measurements. |
| [Security](../backend/Security/ClinicalApiSecurity.cs), [API](../backend/Program.cs) | JWT/RBAC, fail-closed secure configuration, rate limits, origin/loopback checks, HTTPS enforcement, safe errors, review validation and server-derived identity. |
| [Repository](../backend/Storage/SqlExtractionRepository.cs), [contracts](../backend/Models/Contracts.cs), [migration V0008](../backend/db/platform/migrations/V0008__clinical_review_audit.sql) | Expected-version contract, transactional parent-row lock, unique versions and prepared restricted audit grants. |
| [Angular app](../frontend/src/app/app.ts), [template](../frontend/src/app/app.html) | Send observed review version, preserve state after 409, disclose fallback rules, show missing values as `Not documented`. |

Original evidence quotes and offsets remain attached; reviewer corrections remain separate overlays. No benchmark-answer formulary entries or evaluator aliases were added. No Foundry prompt/model tuning occurred.

## 4. ONNX capability limitations

The artifact is a local BiLSTM BC5CDR-trained INT8 model. Its manifest declares nine output labels, but training counts contain only O, B/I-CONDITION and B/I-MEDICATION. Declaring DOSAGE/anatomical labels does not establish a trained or validated dosage capability. There is no investigation label.

| Field | Actual capability/source |
|---|---|
| Medication mention spans | BC5CDR B/I-MEDICATION, plus catalog recovery and confidence/context filtering. |
| Conditions | Raw condition spans; not investigation predictions. |
| Strength, administered quantity, route, frequency, duration, formulation, PRN/weekly directions, action | C# source-scoped rules and validation. Raw DOSAGE output is not used for this binding. |
| Assertion status | Context/rule classification, not BC5CDR assertion prediction. |
| Investigation entity, timing, specimen, category | Catalog and C# rules. Specimen/timing require source support; category is concept classification. |
| RxNorm/LOINC | Verified terminology resolver with abstention; not learned reference-code prediction. |
| Complex/ambiguous regimen or unfamiliar test | Unsupported or uncertain fallback capability; preserve source, null unsafe simplifications and request review. |

Raw model medication detection: **77 TP / 21 FP / 11 FN; precision 78.6%, recall 87.5%, F1 82.8%**. Final fallback: **72 TP / 0 FP / 16 FN**. Filtering removes false positives and some true mentions; rules also recover mentions. These measurements must not be conflated or added together.

Weights SHA-256: `59830ca601c97246f068cc265c215d00c1024e2d3ae3549a5d053723f4a65cd0`. Tokenizer SHA-256: `00a99d2e62f065e43eb57d570e38ee7e814b3044f27e2da70755e9587ff33c81`. [Implementation hashes](stage6-evidence/implementation-sha256.json).

**GPT-6.1 Sol was not evaluated on these cases.** Prior saved Foundry results are display fixtures. No comparative GPT accuracy or improved ONNX weights are claimed.

## 5. Medication attribute improvements

Corrected real-model baseline route extraction was already 67/67 on detected, route-annotated medication entities. Duration was already 21/21 on detected, duration-annotated entities. Their end-to-end differences from Stage 5 come from correcting inference wiring, not improved route/duration rules.

Frequency now preserves `every 6/8 hours as needed`; the indication remains in source instructions. Weekly directions stay distinct from daily dosing, including a `never daily` counterexample. Explicit dose increases/decreases retain full instructions and previous mentions. Complex from/to and weight-based plans still receive conservative review/nulling rather than a falsely simple prescription.

A new regression prevents a detected medication from borrowing strength, route, frequency or duration from `and start unknownDrug ...` when the adjacent drug was omitted. No attribute is inferred from customary drug use. The set contains only three formulation annotations, so formulation improvements are small-denominator engineering evidence.

## 6. Investigation attribute improvements

Timing supports source intervals, today/tomorrow and follow-up phrases. A trailing time can apply to a simple coordinated list; independently timed tests keep their own times. `Order CBC` does not acquire an invented schedule.

Specimen comes from the entity's explicit source name or a local specimen/sample field. `Serum creatinine` supports serum; unqualified creatinine does not. Four unsupported serum gold assumptions intentionally remain wrong under frozen scoring. Category assignment distinguishes laboratory/imaging/electrophysiology and does not imply exact LOINC matching.

Historical numeric measurements no longer become orders because `Labs` or `Repeat Labs` occurs nearby. Conditional/historical/new-order status stays separate. No test-specific catalog expansion concealed investigation detection gaps.

## 7. Before/after regression metrics

The frozen set has 35 engineering-authored synthetic notes, 88 gold medications and 79 gold investigations. Byte SHA-256 remains `3D2EE6AFD0AC72157C92BDDEEDC43F94511BA170D7985A1F44177CB78EB9AD84`. No gold, denominator or matching-equivalence edits were made.

| Entity precision / recall / F1 | Stage 5 archived (deterministic scoring) | Real model before rules | Final real-model fallback |
|---|---|---|---|
| Medication | 98.4% / 70.5% / 82.1% (TP 62, FP 1, FN 26) | 98.6% / 81.8% / 89.4% (TP 72, FP 1, FN 16) | 100.0% / 81.8% / 90.0% (TP 72, FP 0, FN 16) |
| Investigation | 90.4% / 83.5% / 86.8% (TP 66, FP 7, FN 13) | 90.4% / 83.5% / 86.8% (TP 66, FP 7, FN 13) | 90.4% / 83.5% / 86.8% (TP 66, FP 7, FN 13) |

| End-to-end attribute | Stage 5 archived | Real model before rules | Final fallback |
|---|---:|---:|---:|
| Medication: strength | 62/88 (70.5%) | 72/88 (81.8%) | 72/88 (81.8%) |
| Medication: dose | 51/69 (73.9%) | 60/69 (87.0%) | 60/69 (87.0%) |
| Medication: route | 57/79 (72.2%) | 67/79 (84.8%) | 67/79 (84.8%) |
| Medication: frequency | 51/81 (63.0%) | 60/81 (74.1%) | 67/81 (82.7%) |
| Medication: assertion_status | 59/88 (67.0%) | 69/88 (78.4%) | 72/88 (81.8%) |
| Medication: duration | 16/25 (64.0%) | 21/25 (84.0%) | 21/25 (84.0%) |
| Medication: formulation | 0/3 (0.0%) | 0/3 (0.0%) | 2/3 (66.7%) |
| Investigation: timing | 14/70 (20.0%) | 14/70 (20.0%) | 58/70 (82.9%) |
| Investigation: clinical_category | 0/79 (0.0%) | 0/79 (0.0%) | 66/79 (83.5%) |
| Investigation: assertion_status | 60/79 (75.9%) | 60/79 (75.9%) | 66/79 (83.5%) |
| Investigation: specimen | 0/24 (0.0%) | 0/24 (0.0%) | 20/24 (83.3%) |

The actual deterministic frequency gain is **60/81 to 67/81** with real inference held constant. Duration stays **21/25**. Investigation timing improves **14/70 to 58/70**; category **0/79 to 66/79**; specimen **0/24 to 20/24**. These are pipeline gains.

| Attribute conditional on entity match | Final result |
|---|---:|
| Medication: strength | 72/72 (100.0%) |
| Medication: dose | 60/60 (100.0%) |
| Medication: route | 67/67 (100.0%) |
| Medication: frequency | 67/67 (100.0%) |
| Medication: assertion_status | 72/72 (100.0%) |
| Medication: duration | 21/21 (100.0%) |
| Medication: formulation | 2/2 (100.0%) |
| Investigation: timing | 58/58 (100.0%) |
| Investigation: clinical_category | 66/66 (100.0%) |
| Investigation: assertion_status | 66/66 (100.0%) |
| Investigation: specimen | 20/24 (83.3%) |

Matched-entity denominators exclude undetected entities, while end-to-end denominators retain them. The unchanged evaluator matches repeated unanchored mentions using attributes; missing gold offsets remain a limitation. These high conditional scores follow tuning on this development set and do not establish independent clinical accuracy.

An omitted gold key is **unannotated**, not a verified absence. Only four medication-strength labels explicitly annotate absence; 4/4 remain null. Documented-only strength recovery is 68/84; the combined strength score includes four correct nulls. General absent-attribute specificity, day-of-week/PRN-indication correctness and complete-prescription accuracy are not established by these sparse annotations.

Evidence: **145/145 emitted entity quotes and offsets valid**. Safety: **14/14** expected category/temporality matches, **15/15** emitted alerts grounded, **0/15** unsupported emitted alerts, **6/6** measurement offsets valid. Source-substring integrity is not proof of clinical safety/completeness. Gold has no independent reference codes, so clinical exact-code precision remains unmeasured.

## 8. Authentication and authorization status

| Control | Implemented and tested | Deployment limitation |
|---|---|---|
| Authenticated reviewer | Official JwtBearer 10.0.14 validates signature, issuer, audience, expiration and HTTPS metadata; identity derives from validated `issuer\|sub` | No institutional authority/audience, app registration or real sign-in configured. Synthetic signed subjects exist only in isolated tests. |
| Roles | ClinicalReader reads; ClinicalReviewer extracts/reviews; ClinicalAdministrator can also resolve quarantine. Clinical, audit, FHIR and legacy NER routes inherit policy | Tenant roles, patient/ward/tenant access scoping and clinical credential governance remain pending. |
| Fail-closed configuration | Outside Development demo, missing HTTPS authority/audience stops startup. Base config disables demo; Development enables it | Angular OIDC/PKCE sign-in is not implemented. Secure-mode API architecture is not integrated institutional SSO. |
| Local demo | Existing workflow retained, explicitly unauthenticated, loopback clients only | No real identity assurance or patient-data deployment claim. |
| Review decisions | Existing status/comment/code-format checks plus correction-field allowlist, string/null schema, payload limits and CORRECTED-only correction values | Code format checks do not clinically verify reviewer-selected codes. |
| Concurrency | UI sends observed expectedVersion; required in secure mode; stale write returns 409 with reload instruction | Older demo clients may omit it; versions serialize but stale-client detection then does not apply. |
| Rate limits | Subject/IP partition, default 240/minute, zero queue, HTTP 429 and Retry-After | Per-process limiter, not distributed abuse/DDoS protection; deployment quotas need tuning. |
| Protected responses | no-store, nosniff, no-referrer, origin checks, generic exceptions; no source/secret logging added | Patient-level access, encryption-at-rest assurance, retention and institutional privacy approval remain open. |
| HTTPS/TLS | Secure mode applies HTTPS redirection/HSTS and refuses non-HTTPS if redirection is unavailable | Certificates/proxy trust/TLS deployment not provided or penetration tested. Demo remains loopback HTTP. |
| Secrets | Production no longer auto-loads .env; existing environment/config injection and managed-identity credential path retained | Local .env remains. No deployed Key Vault or managed PostgreSQL identity; DB TLS/rotation remain deployment work. |

Implementation references: Microsoft's [JWT validation guidance](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) and [rate-limiting middleware](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0). These references do not certify this deployment.

Deployment requires real `Security__Authority`/`Security__Audience`, institutional app roles, exact HTTPS origins, certificates/trusted termination, secret-injected PostgreSQL credentials, verified database TLS and a restricted runtime role. Do not enable demo to bypass identity setup. Arbitrary forwarded identity/role headers are never trusted. Known-proxy forwarding must be explicitly configured before proxy deployment.

Exact locally configured API-key/password values were checked against production source/config and built browser JavaScript: zero matches. [Bounded scan scope/result](stage6-evidence/secret-scan.json). This is not a comprehensive host/repository secret audit.

## 9. Database and review audit verification

**Eight real PostgreSQL integration/measurement tests passed** against `127.0.0.1:5432/cx_audit_test`, using isolated random schemas and pgvector. Fixtures reject destructive testing outside `cx_audit_test*`. No application database reset or destructive migration occurred.

Five simultaneous reviews with expectedVersion=0 produced **one HTTP 200, four HTTP 409 and one audit row at version 1**. Original evidence and medication quantity remain unchanged after reload. Parent-row locking serializes version allocation, including the first review; a unique entity/version index adds database protection. Existing persistence, runtime-role, rollback and audit-history tests also passed.

V0008 was executed in the isolated test schema. Runtime audit privileges verified: SELECT/INSERT=true, UPDATE/DELETE=false. The migration preserves old rows and fails on duplicate historical version keys instead of deleting them. **V0008 has not been applied to the application schema.** Owner/admin access and parent deletion can affect retention; this is not a tamper-proof ledger or external immutable archive.

Live application read-back after browser reviews: 7 medications, 10 investigations and 12 audit rows. Original Warfarin dose remains null; review corrections are separate. All live identity labels remain `Unauthenticated Session (Local Demonstration Mode)` / `LOCAL_DEMO_MODE`. [Audit snapshot](stage6-evidence/application-review-audit.json). No authenticated clinician identity was invented.

Review-history DB errors now fail the request instead of silently returning empty in-memory history. Legacy missing-table read compatibility remains explicit and is not proof of a deployed production audit schema.

## 10. Automated test results

| Suite | Final result | Evidence |
|---|---|---|
| Complete backend xUnit | **158 passed, 0 failed, 10 skipped**, approximately 8 seconds | [TRX](stage6-evidence/backend-tests.trx) |
| PostgreSQL integration/measurement subset | **8 passed**, included above | Named tests in TRX |
| Angular Vitest | **34 passed, 0 failed** | `npm test -- --watch=false`; review test verifies version progression |
| Angular production build | **PASS**, initial bundle 358.88 kB | `npm run build` |
| Existing Chromium E2E | **7 passed**, zero console/page errors | [JSON](stage6-evidence/playwright/playwright-e2e-verification.json), eight screenshots |
| Stage 6 Chromium checks | **2 passed**, zero extraction POSTs/page errors | [JSON](stage6-evidence/playwright/stage6-browser-results.json), two screenshots |
| Frozen gold, artifacts, executed inference | **PASS** | Stage 6 assertions and [verification summary](stage6-evidence/verification-summary.json) |

The ten skips are explicit live-spend or independent-evaluation gates, not passed clinical tests. Security tests reject anonymous/malformed/wrong-audience/expired/wrong-key tokens; forbid reader decisions and reviewer quarantine administration; verify subject stamping, version requirements, 409, rate limits and origin rejection. Clinical tests cover timing boundaries, explicit specimen, weekly/PRN and an omitted neighbouring drug.

The existing browser suite verifies live PostgreSQL reads/reviews and mocks extraction where needed. New browser checks use captured real ONNX outputs and intercepted writes. They verify weekly/PRN display, missing timing/specimen, fallback disclosure and stale-review handling. An incorrect source-wording assertion and an Angular-render wait in the new test were corrected; final assertions pass. No final failure was hidden by skips.

Reproduction in PowerShell from the workspace root:

```powershell
# Supply a dedicated cx_audit_test* connection through your secret/environment setup.
$env:TEST_POSTGRES_CONNECTION = '<dedicated test database connection>'
$env:FOUNDRY_LIVE_TEST = 'false'
$env:AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST = ''
$env:STAGE6_BENCHMARK_OUTPUT = 'after.json'
dotnet test backend/tests/Apollo.Backend.Tests.csproj --logger 'trx;LogFileName=stage6-full.trx'
python backend/tests/Evaluation/stage6_audit.py
Push-Location frontend
npm test -- --watch=false
npm run build
Pop-Location
# Start API in Development with Extraction__Provider=LocalOnnx, Angular on :4200.
$env:E2E_ARTIFACT_DIR = Join-Path (Get-Location) 'docs/stage6-evidence/playwright'
Push-Location frontend
node e2e/stage5-e2e.mjs
node e2e/stage6-e2e.mjs
Pop-Location
```

The existing artifacts directory is a junction to `D:/NER/artifacts`; older tests write there and need filesystem permission. Stage 6 evidence is inside this workspace. Browser fixtures require the saved Stage 5 extraction, so these browser checks are not a portable fresh-database setup. The benchmark itself requires no PostgreSQL or Foundry.

Pre-change captures were made before edits and retained; the current runner reproduces final results, not historical source. Hashes/raw predictions permit attribution checks. Diagnostic capture executes extra local inference, so its latency is not an operational speed benchmark.

## 11. Remaining risks and independent evaluation

**PENDING EXTERNAL REVIEW:** [sealed inputs](../backend/tests/Evaluation/stage6-independent/sealed-inputs.json), [manifest](../backend/tests/Evaluation/stage6-independent/manifest.json), [packet A](../backend/tests/Evaluation/stage6-independent/annotation-packet-A.json), [packet B](../backend/tests/Evaluation/stage6-independent/annotation-packet-B.json), [annotation protocol](../backend/tests/Evaluation/stage6-independent/ANNOTATION_PROTOCOL.md).

Twelve new engineering-authored synthetic notes are separate from the 35 development cases, hash-sealed, excluded from runners and **not executed through any model/pipeline**. Packets have null annotations and no assigned reviewer; they must never be interpreted as empty gold. The manifest is ineligible for final evaluation. Two qualified independent reviewers must annotate without predictions or each other's answers; an adjudicator resolves differences and freezes gold before execution. No reviewer, approval, inter-rater agreement or gold label was invented. Per user direction, this status remains pending external annotation.

Remaining risks:

- Sixteen medication and thirteen investigation name misses remain; seven emitted investigations are unmatched. Conditional attribute accuracy does not compensate for omissions.
- Catalog omissions, organ/subtype truncation and threshold mentions remain unresolved. No benchmark-specific vocabulary expansion concealed them.
- Complex regimens, unusual abbreviations, PDF/cross-line layouts and contradictory plans require human review. Timing rules are intentionally limited.
- All measured clinical metrics are engineering development evidence after inspecting this set. Sparse explicit-null labels cannot establish general hallucination/absence specificity.
- No independent clinical extraction/code/severity adjudication or comparative GPT-6.1 Sol assessment exists.
- Institutional SSO/SPA login, patient/tenant authorization, deployed TLS/secrets, external audit retention, backups/restore, monitoring, scale limits, penetration testing and institutional privacy/compliance approval remain open.
- Bundled local PostgreSQL uses loopback trust authentication in its development configuration. Isolated runtime-grant tests do not certify live cluster security.
- Browser reviews append explicitly unauthenticated demo decisions to a synthetic saved extraction, not real clinician approvals.

## 12. Release readiness verdict

**Engineering demonstration / offline regression candidate: PASS WITH DOCUMENTED LIMITATIONS.** The working provider architecture is retained; real-model benchmark wiring and source-grounded fallback attributes are corrected; all requested offline test layers passed. Zero billable Foundry calls occurred.

**Hospital / clinical production: NOT READY.** Independent clinical validation, substantial fallback coverage gaps, live identity/access scoping and deployment security/governance remain unresolved. Stage 6 does not certify hospital readiness or clinician-approved accuracy.
