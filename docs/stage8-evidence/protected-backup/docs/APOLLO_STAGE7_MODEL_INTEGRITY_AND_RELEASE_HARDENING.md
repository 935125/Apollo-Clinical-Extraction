# APOLLO STAGE 7 — MODEL INTEGRITY AND RELEASE HARDENING

23 November 2026, Asia/Calcutta. **Zero billable Foundry calls. Independent clinical validation: PENDING EXTERNAL REVIEW. Engineering release: HOLD. Hospital production: NOT READY.**

Real ONNX execution, source-grounded investigation detection, withheld-candidate review and secure record-access boundaries are implemented. Final regression passes. Two legacy-test preservation incidents prevent an unqualified completion/release claim: a forensic replay re-saved the existing reference extraction before its application-database side effect was discovered and removed; the old browser runner overwrote prior browser report/screenshots before output routing was corrected. Clinical benchmark JSON, frozen gold, all Stage 6 evidence and sealed evaluation files remain unchanged. Sections 8–11 disclose the exact limits of preservation evidence.

## 1. Model-execution verification

The normal `SlidingWindowOnnxEngine` constructor now rejects missing weights/tokenizer instead of substituting token scoring. Production registration uses one configured artifact directory, with optional explicit model/tokenizer paths; an incorrect configured path fails rather than guessing another installation. Every neural session verifies the unchanged Stage 6 pair:

- Model SHA-256: `59830ca601c97246f068cc265c215d00c1024e2d3ae3549a5d053723f4a65cd0`.
- Tokenizer SHA-256: `00a99d2e62f065e43eb57d570e38ee7e814b3044f27e2da70755e9587ff33c81`.
- Vocabulary size 20,000; expected special-token IDs; two int64 rank-two graph inputs; one float rank-three output with nine labels; runtime batch/sequence/label tensor dimensions and token ID ranges.

The loaded model bytes and parsed tokenizer bytes are hashed directly. The session loads the verified byte array, preventing a path replacement between validation and graph load from changing accepted weights. `OnnxExecutionReceipt` records execution mode, executed windows, inference duration and model/rule counts. Each LocalOnnx extraction requires positive actual windows. API metadata persists hashes and `NEURAL_ONNX`; model/rule counts overlap and are not additive. Readiness executes a harmless local `aspirin` inference and returns unhealthy on artifact/inference failure.

Deterministic scoring remains available only through an explicit `allowDeterministicTestScoring` argument used by two legacy rule tests. It has zero neural windows and is labelled `deterministic_test_scoring` / `no_neural_model`, including the legacy NER version label. The production registration never enables it, and the LocalOnnx adapter rejects it.

Tests cover missing model/tokenizer, corrupt model bytes, corrupt tokenizer JSON, altered/incompatible tokenizer, invalid token IDs, incorrect configured path, safe HTTP 503, unhealthy readiness and genuine multi-window inference. Wrong-key/expired/audience security failures are separate JWT tests, not model-integrity evidence.

**Training provenance caveat:** the tokenizer's dedicated manifest matches the actual file, but `run_manifest.json` contains a different historical tokenizer digest. Original model artifacts/manifests were not edited to conceal this. Runtime guarantees the exact Stage 6 baseline pair and compatible graph execution; it does not prove the training-manifest discrepancy is harmless or establish training provenance approval. See [model-integrity-audit.json](stage7-evidence/model-integrity-audit.json).

BC5CDR's manifest declares nine labels, but training counts contain only O, B/I-CONDITION and B/I-MEDICATION. There is no investigation label, and no validated trained dosage-attribute capability. This release does not change weights, confidence threshold or label interpretation.

## 2. Remaining entity failures and causes

The original Stage 6 cohort contains 16 medication FNs, 13 investigation FNs and seven investigation FPs: **36 failure rows**. [Original failure audit](stage7-evidence/original-36-entity-failures.json) contains, for every row, full source text, engineering gold entity, raw output/confidence, Stage 6 processing result, Stage 7 deterministic result, actual final API response, exact reason, recovery assessment and human-review requirement. The [compact inventory](stage7-evidence/ENTITY_FAILURE_AUDIT.md) makes all rows reviewable. Reproduce with `python backend/tests/Evaluation/stage7_failure_audit.py`.

| Original failure group | Exact cause | Stage 7 disposition |
|---|---|---|
| Nine medication misses without raw medication spans | Montelukast, Apixaban, Insulin Glargine, Sitagliptin, Colchicine, Febuxostat, Cholecalciferol, Filgrastim, Pregabalin: raw-model omission plus no catalog recovery | Remain omitted. No benchmark-specific drug lexicon or replacement model introduced. Human source reconciliation required. |
| Seven medication suppressions | Six below 0.85: Enoxaparin 0.2996, Glimepiride 0.8409, Cefixime 0.3127, Ramipril 0.2922, Hydrochlorothiazide 0.8339, Pirfenidone 0.4830. Theophylline 0.9989 lacked a recognized prescription prefix in a historical/stopped-medication sentence | Exact source spans retained as unapproved review candidates, separate from prescriptions. No threshold reduction or automatic promotion. |
| Four truncated investigation names | Troponin I, MRI Brain, CT Head, High-resolution CT Thorax lost subtype/organ/modality qualifiers | Preserve source qualifiers; abstain from unsupported terminology coding. |
| Five productive investigation mentions | Sputum culture; Fasting Blood Sugar in three cases; Platelet count | General specimen/test/count grammars recover documented mentions. No case-ID rules or answer-list additions. |
| Three remaining catalog omissions | NT-proBNP once and Hemoglobin twice | Remain omitted; require broader independently validated detection coverage. |
| One investigation FN/FP pair | Source `Chest X-ray (PA view)` differs from broader frozen gold `Chest X-ray` under the unchanged evaluator | Preserve documented PA view and the frozen matching result. Do not remove specificity to improve scoring. |
| Two threshold investigation FPs | Uric acid and potassium thresholds were treated as investigation mentions without orders | Suppress threshold mentions as orders, with an additional guard preventing raw chemical/analyte predictions from becoming medications when the investigation span is removed. |

The raw model has eleven medication FNs across the complete dataset; two are recovered by catalog rules. Thus nine raw omissions remain among the final sixteen misses. The cohort counts are not interchangeable.

Raw neural output is identical to Stage 6 for all 35 notes. Of the 36 original failure rows, 21 remain under frozen matching: sixteen medication FNs, four investigation FNs and one investigation FP. Human review remains necessary even for source-grounded recovered mentions; these engineering annotations have not been independently clinically adjudicated.

## 3. Confirmed code corrections

| Source | Change and purpose |
|---|---|
| [OnnxArtifactIntegrity.cs](../backend/Nlp/OnnxArtifactIntegrity.cs), [engine](../backend/Nlp/SlidingWindowOnnxEngine.cs), [tokenizer](../backend/Nlp/WordPieceTokenizer.cs) | Pin baseline pair; reject missing/corrupt/incompatible inputs; validate graph/tensors; eliminate implicit deterministic execution. |
| [Program.cs](../backend/Program.cs), [readiness](../backend/Security/OnnxReadinessHealthCheck.cs), [adapter](../backend/Clinical/OnnxClinicalExtractor.cs) | Paired path configuration, actual inference readiness, persisted execution/hash metadata, sanitized typed errors. Structured console logging removes dependence on Windows Event Log write privileges. |
| [InvestigationMentionRules.cs](../backend/Clinical/InvestigationMentionRules.cs), [pipeline](../backend/Clinical/ClinicalPipelineService.cs) | Productive source grammars, imaging/subtype preservation, threshold suppression and analyte-to-drug escape prevention. |
| [Contracts.cs](../backend/Models/Contracts.cs), [Angular template](../frontend/src/app/app.html), [model](../frontend/src/app/models/extraction.model.ts) | Source-linked withheld candidates with score/reason, visibly outside extracted prescriptions. Metadata storage preserves them across database reload. |
| [ClinicalDataAccess.cs](../backend/Security/ClinicalDataAccess.cs), [JWT/API security](../backend/Security/ClinicalApiSecurity.cs) | Institutional entitlement interface and default-deny secure boundary covering records, reviews and FHIR; block global extraction collections/search until scoped queries exist. |
| [SQL repository](../backend/Storage/SqlExtractionRepository.cs), [memory repository](../backend/Storage/InMemoryExtractionRepository.cs) | Extraction IDs now insert-only. Duplicate-ID saves fail before existing candidates/children can change. Review overlays remain separate. |
| [Forensic replay test](../backend/tests/Evaluation/ForensicPipelineAuditTests.cs) | Remove legacy hardcoded application connection and reference-record re-save; retain isolated replay/persistence assertions. Correct two additional legacy benchmark model paths. |
| [Stage 5](../backend/tests/Evaluation/Stage5IndependentBenchmarkTests.cs) / [Stage 6](../backend/tests/Evaluation/Stage6ReliabilityBenchmarkTests.cs) runners | Route rerun output to Stage 7 or an explicit output root, preserving frozen historical benchmark JSON. |
| [Browser runners](../frontend/e2e/stage5-e2e.mjs), [Stage 6 runner](../frontend/e2e/stage6-e2e.mjs), [Stage 7 checks](../frontend/e2e/stage7-e2e.mjs) | Explicit output directory/base URL; isolated synthetic fixture; candidate and qualified-imaging checks. |

No production case IDs occur in extraction rules. New test cases are regression counterexamples, not changes to gold. The thresholds remain 0.85, and there are no new pretrained weights or Foundry prompt changes.

## 4. Raw ONNX versus deterministic processing and final API

| Measurement | TP | FP | FN | Precision | Recall | F1 |
|---|---:|---:|---:|---:|---:|---:|
| Raw ONNX medication spans | 77 | 21 | 11 | 77/98 (78.6%) | 77/88 (87.5%) | 154/186 (82.8%) |
| Rule/catalog medication-origin subset | 62 | 0 | 26 | 62/62 (100.0%) | 62/88 (70.5%) | 124/150 (82.7%) |
| Deterministic investigations | 75 | 1 | 4 | 75/76 (98.7%) | 75/79 (94.9%) | 150/155 (96.8%) |
| Final API medications | 72 | 0 | 16 | 72/72 (100.0%) | 72/88 (81.8%) | 144/160 (90.0%) |
| Final API investigations | 75 | 1 | 4 | 75/76 (98.7%) | 75/79 (94.9%) | 150/155 (96.8%) |

Stage 6 final investigations: TP=66, FP=7, FN=13; precision 66/73, recall 66/79, F1 132/152. Stage 6 final medications: TP=72, FP=0, FN=16; precision 72/72, recall 72/88, F1 144/160.

Deterministic medication recovery is measured as the rule/catalog-origin subset, including `ONNX_AND_RULE_CONFIRMED` overlaps. It is not an additive count and not a separate model-off attribute experiment. Investigations are entirely deterministic. Raw investigation capability is **unsupported**, not a newly measured neural investigation classifier. All reported recognition rows use the unchanged engineering gold: 88 medications or 79 investigations.

[Full measured results](stage7-evidence/stage7-development-results.json) include raw spans, inference receipts, deterministic subsets and actual in-process API responses for all 35 cases. The local API factory asserts zero simulated Foundry calls; no live provider is used. These are development/regression results, not independent accuracy.

## 5. Entity recall and false-positive tradeoffs

Medication extraction remains TP=72, FP=0, FN=16. Withheld candidates **do not increase medication recall**. Eight candidates are surfaced: seven original suppressed gold medication mentions plus one source-grounded negated Penicillin mention absent from that case's frozen medication gold. On the missed-mention cohort, the candidate pool has TP=7, FP=1, FN=9: name precision 7/8 and coverage 7/16. All seven prior suppressions are visible (7/7); none is an approved prescription or clinical decision. Confidence scores are not validated probabilities of clinical correctness.

Investigation TP increases by nine (66 to 75), while FP decreases by six (7 to 1) and FN decreases by nine (13 to 4). The remaining PA-view FP is retained for transparency. Newly qualified/unlisted source names may remain terminology-unresolved; recovering a mention does not establish an exact LOINC code.

The code does not relax medication gates to maximize recall. Historical/stopped Theophylline, low-score drugs and ambiguous conditional instructions remain review tasks. No false clinician review or candidate acceptance was created in the application database.

## 6. Attribute reliability

| End-to-end field | Stage 6 | Stage 7 |
|---|---:|---:|
| Medication: strength | 72/88 (81.8%) | 72/88 (81.8%) |
| Medication: dose | 60/69 (87.0%) | 60/69 (87.0%) |
| Medication: route | 67/79 (84.8%) | 67/79 (84.8%) |
| Medication: frequency | 67/81 (82.7%) | 67/81 (82.7%) |
| Medication: assertion_status | 72/88 (81.8%) | 72/88 (81.8%) |
| Medication: duration | 21/25 (84.0%) | 21/25 (84.0%) |
| Medication: formulation | 2/3 (66.7%) | 2/3 (66.7%) |
| Investigation: timing | 58/70 (82.9%) | 66/70 (94.3%) |
| Investigation: clinical_category | 66/79 (83.5%) | 75/79 (94.9%) |
| Investigation: assertion_status | 66/79 (83.5%) | 75/79 (94.9%) |
| Investigation: specimen | 20/24 (83.3%) | 20/24 (83.3%) |

These end-to-end attribute denominators include missed entities. Medication binding behavior is retained from Stage 6: weekly/PRN instructions, dose escalation, discontinuation and conservative handling of complex schedules remain covered by regression tests. A new complex-schedule solver is **not** claimed. The ONNX model does not predict these structured attributes.

Four baseline K+/creatinine gold specimens assume serum without explicit source support. Specimen remains null for them; the frozen score stays 20/24. Missing duration, route, specimen or schedule is never filled from customary practice. Only four gold medication-strength labels explicitly annotate absence; sparse null labels cannot establish broad hallucination/absence specificity. Formulation has only three annotations. Repeated mentions without gold offsets and optimistic attribute-based matching remain evaluator limitations.

Source entity quotes/offsets and withheld-candidate evidence remain attached. Evidence substring validity does not prove correct clinical binding, completeness or safety. Independent code precision, clinician severity agreement and GPT-6.1 Sol comparison remain unmeasured.

## 7. Security implementation and deployment blockers

JWT issuer/audience/signature/lifetime validation and role policies remain. Identity is derived from validated issuer plus subject, never a client reviewer name. Tests use explicitly synthetic signed principals only. No institutional clinician identity or approval was invented.

The new `IClinicalDataAccess` contract requires a trusted extraction-to-tenant/patient/encounter binding and current subject entitlement. Secure mode denies record access by default when this integration is absent; other records produce indistinguishable 404 responses. Existing global list/search queries remain forbidden even to administrators. The current request's patientId alone is not proof of entitlement. New patient-bound creation, scoped collection queries, transactional binding storage and Angular institutional sign-in remain deployment work; the code does not claim these integrations are complete.

Tests create two real records in an isolated host, authorize one, and deny the other across read/review/FHIR paths; fake patient headers do not grant access. Default-deny connectors, reader role restrictions, missing versions, stale decisions, malformed/expired/wrong-key/wrong-audience JWTs, HTTP rejection, rate limits and safe errors are verified. ClinicalAdministrator is not an implicit record-access bypass.

Existing row-lock/version handling, per-process rate limiting, no-store/nosniff/no-referrer, origin checks and Development-only loopback demo remain. TLS certificates/trusted proxy setup, secret-provider activation, distributed abuse controls, patient/tenant query enforcement, institutional app roles, real SSO and clinical governance are not deployed.

See the [institutional integration and reversible deployment procedure](STAGE7_INSTITUTIONAL_SECURITY_DEPLOYMENT.md). This follows [ASP.NET resource authorization guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0); the institutional adapter is deliberately unconfigured rather than pretending roles establish patient access.

## 8. Database and audit migration status

The live cluster's read-only audit confirms `ssl=off` and `trust` rules for loopback connections for all roles, including postgres. Any local process able to connect can select a PostgreSQL role without password verification. A least-privilege role name does not protect against selecting a superuser under trust. [Observed configuration](stage7-evidence/database-auth-audit.json). No live HBA rules or credentials were changed.

A **new isolated cluster** on port 55447 rehearsed SCRAM password authentication, rejection of an incorrect password, an explicit reject rule and restoration of the captured HBA. All four checks passed; the temporary server stopped and password files were removed. This did not test TLS or change the live cluster. [Rehearsal result](stage7-evidence/scram-rehearsal.json), [reproducible script](../backend/tests/Verification/stage7-scram-rehearsal.ps1). PostgreSQL documents [trust/HBA behavior](https://www.postgresql.org/docs/current/auth-pg-hba-conf.html) and cautions that [file-rule diagnostics](https://www.postgresql.org/docs/18/view-pg-hba-file-rules.html) describe file contents, not proof of last-loaded rules.

V0008 received read-only application-schema preflight: 12 audit rows, no duplicate version keys, zero invalid keys, zero orphan reviews; owner cx_admin; the unique review-version index is not deployed. [Preflight](stage7-evidence/application-v0008-preflight.txt), [SQL](../backend/db/platform/stage7_review_preflight.sql). **V0008 was not applied to clinical data.** In an isolated schema, transaction rollback restored prior privileges/index state and reapplication preserved audit rows while denying runtime UPDATE/DELETE. The separate procedure requires exact pre-deployment ACL/index capture and never rolls back by deleting the audit table or review rows. Existing tables do not acquire CREATE TABLE CHECK constraints automatically; owner access and parent-delete cascade remain retention risks.

### Preservation incident and confirmed repair

An early full regression executed legacy `ForensicPipelineAuditTests` code that, after its isolated assertions, explicitly connected to cx_platform and re-saved extraction `1713c76e-b355-4be3-b02f-e9d581b82415`. The repository then allowed upsert and deleted/reinserted child rows. This changed the parent/medication/investigation row digests, including saved metadata/row identity. Counts stayed at 41 extractions, 88 medications and 120 investigations. The 12 review-audit rows and their digest remained unchanged.

This violated the requested original-record preservation constraint. The legacy application connection/re-save and hardcoded credential were removed. Both repository implementations now reject an existing extraction ID, and a real PostgreSQL regression verifies that a duplicate save cannot replace source/children. Full reruns additionally pin the default connection to the dedicated test database.

The complete corrected rerun left application row digests unchanged from the post-incident baseline. **This does not restore or prove preservation of the initial rows.** No speculative restoration was attempted: hashes are not a restorable backup, and original saved timestamps/child identities cannot be reconstructed reliably from them. Owner-led reconciliation against a trusted backup is required before release. [Initial snapshot](stage7-evidence/application-records-before.json), [post-legacy snapshot](stage7-evidence/application-records-after-legacy-replay.json), [final snapshot](stage7-evidence/application-records-after.json), [preservation assessment](stage7-evidence/preservation-verification.json).

All browser review writes occurred in a separately seeded cx_audit_test schema, not the application schema. They are explicitly synthetic demonstration checks, not clinical approvals.

## 9. Complete regression results

| Suite | Final result | Evidence |
|---|---|---|
| Release solution build | PASS, zero warnings/errors | `dotnet build AIExtraction.slnx -c Release --no-restore` |
| Complete backend suite | **173 passed, 0 failed, 10 skipped** | [TRX](stage7-evidence/backend-tests.trx) |
| Explicit PostgreSQL integration tests | **12 passed**, included above | PostgresFact cases: persistence/rollback, review concurrency, runtime grants, candidate read-back, V0008 reversal, fixture isolation and immutable extraction IDs |
| Genuine ONNX/integrity | PASS | Multi-window receipt, strict artifacts, wrong-path health/API, raw-vs-final replay assertions |
| Angular tests | **35 passed, 0 failed** | Includes source candidate separation and existing review/uncertainty behavior |
| Angular production build | PASS, initial 363.34 kB | `npm run build` |
| Chromium | **11 scenarios passed, 0 failed** | [Existing seven](stage7-evidence/playwright/playwright-e2e-verification.json), [two Stage 6 checks](stage7-evidence/playwright/stage6-browser-results.json), [two Stage 7 checks](stage7-evidence/playwright/stage7-browser-results.json) |
| Isolated SCRAM/HBA reversal | Four checks passed | [Result](stage7-evidence/scram-rehearsal.json); no TLS claim |

Some legacy provider fixtures construct stub envelopes from engineering annotations. Those runs test plumbing and are not used as model accuracy or independent clinical evidence here. Forensic replay output now has a separate Stage 7 directory, with archived inputs treated as read-only.

Ten backend skips remain the live-Foundry spend gates or independent-evaluation gate. They are not passed clinical tests. Genuine local inference requires no cloud. Saved Foundry outputs are fixtures; no new GPT-6.1 Sol result was requested.

The browser API ran on ports 5107/8107 against a separate test schema with copied synthetic provider results. Existing browser reviews were not submitted to the original application records. An initial legacy browser invocation ignored the requested output directory and overwrote its previous Stage 5 browser report and several screenshots. Output routing was fixed and the final run wrote Stage 7 evidence separately. The previous overwritten browser files cannot be restored from hashes; **clinical benchmark JSON and all Stage 6 evidence were not changed**. The preservation assessment lists these nonbenchmark changes rather than claiming all files were preserved.

Reproduction requires installed baseline artifacts, a dedicated cx_audit_test connection and test-only browser fixture. Never target the application database:

```powershell
$env:TEST_POSTGRES_CONNECTION = '<dedicated cx_audit_test* connection>'
$env:ConnectionStrings__Postgres = $env:TEST_POSTGRES_CONNECTION
$env:APOLLO_EVALUATION_OUTPUT_ROOT = Join-Path (Get-Location) 'docs/stage7-evidence'
$env:FOUNDRY_LIVE_TEST = 'false'
$env:AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST = ''
Remove-Item Env:FINAL_EVALUATION_MANIFEST -ErrorAction SilentlyContinue
Remove-Item Env:STAGE7_RETAIN_BROWSER_FIXTURE -ErrorAction SilentlyContinue
dotnet build AIExtraction.slnx -c Release
dotnet test backend/tests/Apollo.Backend.Tests.csproj -c Release --no-build
python backend/tests/Evaluation/stage7_failure_audit.py
Push-Location frontend
npm test -- --watch=false
npm run build
Pop-Location
```

To run browser persistence tests, execute `Stage7BrowserFixtureTests` once with `STAGE7_RETAIN_BROWSER_FIXTURE=true`, then unset it. Launch the API against **that generated schema**, LocalOnnx only, on separate ports; serve the built Angular app there. Set E2E_BASE_URL and E2E_ARTIFACT_DIR to the isolated endpoint/Stage 7 directory, then run stage5-e2e.mjs, stage6-e2e.mjs and stage7-e2e.mjs. Stop that test server and remove only its validated temporary schema afterwards. No sealed-evaluation environment variable may be enabled.

## 10. Remaining limitations and evaluation integrity

- All metrics are engineering regression on the reused 35 cases. No frozen gold/aliases/denominators changed. The twelve independent cases remain **PENDING EXTERNAL REVIEW**; their inputs were not inspected for optimization, executed or edited. Their manifests/annotation packets are byte-identical. [Protection verification](stage7-evidence/preservation-verification.json).
- Sixteen medication misses, four investigation name misses and one investigation FP remain. Candidate visibility does not establish validated recovery or prescribing safety.
- General morphology/context rules can mis-handle uncommon investigations, cross-line orders, complex imaging descriptions and contradictory source text. No complete clinical grammar or dosage model is claimed.
- Sparse negative/absent labels and no independently adjudicated terminology/severity references limit evaluation. Raw model scores are not calibrated clinical certainty.
- The tokenizer training-manifest digest discrepancy remains a provenance blocker despite pinned runtime execution.
- Institutional patient bindings, scoped query storage, real sign-in, deployment TLS/secrets/database authentication, immutable external audit retention, backup/restore verification and penetration/privacy review remain incomplete.
- Preservation was not perfect: the legacy forensic test rewrote the reference extraction and the browser runner overwrote nonbenchmark artifacts. Repairs prevent recurrence in the final rerun; they cannot retroactively certify original-state preservation. Do not present this stage as having met every non-negotiable constraint.

## 11. Engineering-release verdict

**HOLD — implementation/regression candidate only.** The hardening changes are reviewable and all final offline test layers pass. Real inference is enforced, source-grounded investigation detection improves, and unsafe medication promotion is avoided. Release approval remains withheld pending reference-record reconciliation, acknowledgement/recovery of historical browser artifacts as appropriate, and resolution of artifact provenance and institutional deployment gates. A passing suite does not erase the disclosed preservation incidents.

## 12. Hospital-production readiness verdict

**NOT READY.** Independent clinical validation is pending; medication omissions and clinical scope limitations remain; institutional identity/patient authorization and deployment controls are not operational; reference-record preservation requires owner-led reconciliation. No clinician approval, hospital compliance certification or validated GPT-versus-BC5CDR comparison is claimed.
