# Stage 15 implementation and verification

Measured on 25 November 2026. The modular local pipeline is implemented and has passed the executed engineering checks. `LocalOnnx:PipelineVersion=Legacy` remains the checked-in default; `CandidateV2` is enabled in the isolated evaluated instance. Microsoft Foundry remains the configured primary provider. Independent clinical deployment clearance is blocked on clinical adjudication and reference-code validation.

## Implemented behavior

Medication recognition combines real neural spans, recognition aliases and scoped prescription grammar. A missing catalog code or prescription prefix no longer removes a defensible mention. The assertion layer preserves allergy, negation, history, completed courses, held/discontinued treatment, conditions and uncertainty. Dedicated investigation recognition covers laboratory, microbiology, pathology, imaging and oncology phrases independently of chemical NER. Appointment times no longer become laboratory results.

Entity-local attributes distinguish supported product strength from administered dose; `mg/m2`, `mg/kg`, concentrations, administration method and documented chemotherapy days retain their meaning. No absolute dose is calculated. Exact evidence and source offsets are checked before local provider acceptance. Terminology recognition and standard-code assignment are separate; uncertain ingredient, salt, formulation or investigation specificity leaves the code unresolved. PostgreSQL saves and reloads the accepted provenance/context rather than reconstructing it from changed rules. Angular renders the persisted result and actual provider.

The graph/tokenizer are unchanged. V2 uses 510 content tokens plus CLS/SEP per 512-token window, stride 448, complete content coverage, per-run cancellation and a configured timeout. Legacy behavior remains available for reproducibility and rollback. Candidate receipts expose rejection reasons to the secured internal/offline evaluation path; public results do not expose debug traces.

## Executed verification

| Status | Check | Observed result and evidence |
|---|---|---|
| PASS | Backend Release build | 0 warnings, 0 errors; `stage15-evidence/release-build-command.log` |
| PASS | Full selected backend suite | 305 passed, 13 skipped, 0 failed; `final-verification-command.log`, `regression/test-results/stage15-full-final.trx` |
| PASS | Explicit real-model ablations | 1 passed; component-only variants, integrated pipeline and both full adapters; `evaluation/test-results/stage15-ablation-final.trx` |
| PASS | Real PostgreSQL/local ONNX integration | Exact entity/provenance/normalization roundtrip, separate insert/reload timing, doctor/UHID/version isolation and idempotence; `integration/real-postgres-roundtrip.json`, backend TRX |
| PASS | Angular unit tests and production build | 55 tests; build completed; `angular-test-command.log`, `angular-build-command.log` |
| PASS | New Stage 15 browser flow | 19 checks through real asynchronous local inference, saved PostgreSQL output and Angular selection; messaging simulated; `playwright-stage15-command.log`, `playwright/stage15-local-onnx-results.json` |
| PASS | Existing browser regression flows | Provenance: 8 checks; page-size/search/polling: 6; top-ten/version/patient/failed-result/responsive flow: 8; corresponding Playwright command logs and JSON |
| PASS | Postman CLI | 34 requests, 41 assertions, zero failures on port 5015; `postman-final-command.log`, `postman/` |
| PASS | Development Swagger/OpenAPI endpoints | Swagger HTML and OpenAPI 3.0.3 JSON returned 200; 20 documented paths; `integration/swagger-final.json`. External CDN asset rendering was not tested. |
| PASS | Default routing configuration | Primary MicrosoftFoundry, fallback LocalOnnx, local version Legacy, no inference; `routing-final-command.log` |
| PASS | Read-only historical inspection | Original AML identity/hash unchanged; application adapter counts Foundry 41, ONNX 31; nine saved synthetic Foundry comparisons inspected without inference; `evaluation/supplementary-persisted-inspection.json` |
| PASS | Final evidence/source verification | All 35 frozen original source snapshots and both dataset hashes unchanged; 54 current source/fixture/tool hashes recorded; tested/served assemblies identical; `implementation-manifest.json` |
| FAIL | Final executed engineering checks | None. Earlier development failures and the repeated-fixture browser selector failure are retained in command logs; the final runs above passed. |
| BLOCKED | Independent clinical readiness | Neither frozen dataset has independent clinician adjudication. No independently validated reference-code mapping set is supplied. A deployment-quality clinical claim cannot be made. |
| NOT TESTED | Real Azure Service Bus cloud smoke | Explicitly excluded: the existing smoke targets a shared queue. Simulated transport integration does not establish real broker interoperability in this run. |
| NOT TESTED | New Foundry inference | Intentionally not invoked under the zero-cost requirement. Contract validation, mocked transport and saved outputs were used. |
| NOT TESTED | Replacement neural models | Model cards/licenses researched; no new weights exported, quantized or benchmarked. |
| NOT TESTED | Obsolete Stage 12 single-row-per-UHID browser flow | Superseded by current top-ten submission queue checks; its old dashboard assumptions do not describe the current UI. |

The 13 skips are explicit in TRX: four live Foundry smoke/authorization tests, five older live-Foundry clinical safety tests, one Foundry benchmark, the independent final-evaluation gate, isolated fixture setup, and the ablation test. The ablation was then run explicitly and passed. Isolated setup was executed separately earlier. No skipped test is counted as passed. New local challenge tests cover the corresponding medication/assertion safety boundaries without a paid provider.

## Reproduction

Run from the repository root with the existing .NET 10 SDK, Python, npm dependencies, local PostgreSQL and installed Postman CLI. Do not point verification at the application database. The backend runner requires a dedicated `cx_audit_test` database when PostgreSQL is enabled and excludes the shared cloud queue smoke.

```powershell
$env:TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=5432;Database=cx_audit_test;Username=postgres;Pooling=false'
.\Run-Apollo-Stage15-Verification.ps1 -IncludePostgres
python backend/tests/Evaluation/stage15-report.py
dotnet build backend/Apollo.Backend.csproj -c Release --no-restore -p:UseAppHost=false
```

This produces new evaluation/test artifacts while the pre-edit `baseline/` is retained. Do not rerun baseline capture against changed code. `stage15-make-fixtures.py` and the catalog builder refuse to overwrite their frozen destinations; ordinary verification does not need them.

The retained isolated API manifest is in `stage15-evidence/integration/isolated-environment.json`, targeting only a generated schema in `cx_audit_test`. To recreate that environment, run `Stage15E2EEnvironmentTests` explicitly with `APOLLO_STAGE15_PREPARE_E2E=1`, the isolated PostgreSQL connection, and the same zero-live-provider settings as the verification runner. Restore that flag to `0` afterward. The start helper consumes the retained manifest and the Stage15 build; it refuses a different database/schema. Do not start a second process on occupied ports 5015/8015.

```powershell
powershell.exe -NoProfile -File backend/tests/Verification/stage15-start-isolated.ps1
node frontend/e2e/stage15-local-onnx-e2e.mjs
.\Run-Apollo-Postman-Tests.ps1 -BaseUrl http://127.0.0.1:5015 -EvidenceDirectory 'D:\AI Extraction\docs\stage15-evidence\postman'
```

The helper configures CandidateV2, an invalid Foundry endpoint/key, simulated messaging and the existing Development synthetic policy. The loopback origin is explicitly allowed only in that isolated process. Authentication, CSRF and exact evidence validation remain active. The Postman runner checks local routing before its first collection request and refuses an unauthorized/live-primary target.

```powershell
Push-Location frontend
npm test -- --watch=false
npm run build
$env:APOLLO_E2E_OUTPUT_ROOT='D:\AI Extraction\docs\stage15-evidence\playwright'
node e2e/model-provenance-e2e.mjs
node e2e/queue-page-size-e2e.mjs
node e2e/stage13-top10-queue-e2e.mjs
Pop-Location
python backend/tests/Evaluation/stage15-inspect-supplementary.py
python backend/tests/Evaluation/stage15-verify-evidence.py
```

The three existing browser suites use read-only saved application records. The new Stage 15 browser suite creates a fresh synthetic note exclusively in the isolated database. Windows sandbox restrictions required normal child-process/registry permissions for esbuild, Chromium and the installed Postman CLI. No administrator installation was performed. The main application was not reconfigured or restarted for this verification.

## Source file change list

Paths below identify Stage 15 work. Existing unrelated working-tree edits were preserved; their initial list is `baseline/pre-existing-working-tree.txt`. Files that already existed and had unrelated edits received only the Stage 15 additions described here. Model binaries, tokenizer files, production privacy/routing policy and historical clinical records were not replaced. A hash for every listed source/fixture/tool is recorded by `stage15-verify-evidence.py` in `implementation-manifest.json`.

| Purpose | Files |
|---|---|
| New clinical components | `backend/Clinical/LocalClinicalPipelineOptions.cs`, `ClinicalSectionDetector.cs`, `LocalClinicalScope.cs`, `VersionedClinicalTerminology.cs`, `InvestigationRecognizer.cs`, `MedicationCandidateClassifier.cs`, `ClinicalAssertionClassifier.cs`, `MedicationAttributeExtractor.cs`, `LocalEvidenceValidator.cs`, `UpgradedClinicalPipeline.cs`, `ClinicalPipelineService.UpgradedNer.cs` |
| Versioned recognition resource | `backend/Clinical/Terminology/clinical-recognition-v1.json` |
| Existing clinical integration | `backend/Clinical/ClinicalPipelineService.cs`, `OnnxClinicalExtractor.cs`, `ClinicalResultValidator.cs`, `ClinicalReview.cs`, `ProviderDiagnostics.cs`, `TerminologyVerification.cs` |
| Runtime, schema and persistence | `backend/Nlp/SlidingWindowOnnxEngine.cs`, `backend/Models/Contracts.cs`, `backend/Storage/SqlExtractionRepository.cs`, `backend/Program.cs`, `backend/Apollo.Backend.csproj`, `backend/appsettings.json` |
| New real-model and integration tests | `backend/tests/Stage15ClinicalPipelineTests.cs`, `Stage15SafetyChallengeTests.cs`, `Stage15PostgresTests.cs`, `Stage15E2EEnvironmentTests.cs`, `Evaluation/Stage15BenchmarkTests.cs`, `Evaluation/Stage15AblationTests.cs` |
| Existing test isolation and artifact destinations | `backend/tests/Stage9EmrTests.cs`, `backend/tests/Evaluation/AuditRemediationTests.cs`, `ReliabilityMeasurementTests.cs`, `HeldOutBenchmarkRunner.cs`, `ForensicPipelineAuditTests.cs` |
| New evaluation data/tools | `backend/tests/Evaluation/stage15_frozen_cases.json`, `stage15-make-fixtures.py`, `stage15-score.py`, `stage15-report.py`, `stage15-inspect-supplementary.py`, `stage15-export-historical.sql`, `stage15-verify-evidence.py`; `backend/tests/Terminology/build-stage15-catalog.py` |
| Local verification runners | `Run-Apollo-Stage15-Verification.ps1`, `Run-Apollo-Postman-Tests.ps1`, `backend/tests/Verification/stage15-start-isolated.ps1` |
| Angular persistence display/types | `frontend/src/app/models/extraction.model.ts`, `frontend/src/app/queue/clinical-queue.ts`, `frontend/src/app/queue/clinical-queue.html` |
| Browser verification | New `frontend/e2e/stage15-local-onnx-e2e.mjs`; updated `model-provenance-e2e.mjs`, `queue-page-size-e2e.mjs`, `stage13-top10-queue-e2e.mjs`, `stage12-1-uhid-queue-e2e.mjs` in the same directory |

All Stage 15 Markdown reports under `docs/APOLLO_STAGE15_*.md` are deliverables. Raw baseline snapshots, traces, predictions, metrics, confusion matrices, bootstrap intervals, command logs, TRX, saved synthetic output and screenshots live under `docs/stage15-evidence/`. These are evidence artifacts rather than production source. Existing artifact-output writers now direct new regression output there instead of replacing older audit results. The obsolete Stage 12 script only received the optional evidence destination; it was not counted as a passing current-layout test.

## Activation, rollback and remaining work

`LocalOnnx__PipelineVersion=CandidateV2` selects the new path in an explicitly authorized local process. It does not change the primary provider, direct-local authorization or fallback policy. `LocalOnnx__PipelineVersion=Legacy` restores the original local path; restart that process to apply configuration. No data migration or historical re-extraction is needed. Old rows with null JSON retain the compatibility read path; new rows preserve their original accepted context. Do not rewrite older extractions to make UI results resemble the offline benchmark.

Before clinical activation, obtain an independent representative clinical set with adjudicated spans, actions, relations and terminology identifiers; freeze a separate validation/test split; reconcile the training-tokenizer manifest; assess specific failure classes and code specificity; and measure the selected pipeline or replacement model on that untouched set. The current engine remains limited by finite English terminology, heuristic clause/section relationships, wrapped multiline regimens, ambiguous abbreviations and missing reference mappings. The favorable development metrics and observed zero critical unsafe-active cases establish neither comprehensive safety nor GPT-equivalent performance.
