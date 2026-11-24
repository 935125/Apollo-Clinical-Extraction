# APOLLO CLINICAL EXTRACTION PLATFORM
## Stage 8 — Primary Provider Routing, Configuration Verification & Evidence Reconciliation Report

**Verification Date:** `2026-11-24`  
**Workspace:** `D:\AI Extraction`  
**Stack:** Angular 21 · ASP.NET Core .NET 10 · Microsoft Foundry `gpt-6.1-sol` (Primary) · Local INT8 ONNX `bc5cdr_int8` (Fallback / Authorized Offline Only) · PostgreSQL 18.7  
**Billable Microsoft Foundry API Calls Executed:** `0`  
**Hospital Production Readiness:** **NOT READY FOR HOSPITAL PRODUCTION** *(local clinician-review prototype and engineering verification baseline only; see Section 6)*

---

## 1. Executive Summary

Stage 8 builds directly on completed Stages 5, 6, and 7 without repeating Stage 6 or mutating sealed evaluation assets:

1. **Preservation Verified (`100%`):**
   - Existing Stage 8 commits (`008a545`, `59d79db`) and uncommitted Stage 8 test/evidence work (`backend/tests/Stage8OptionalLiveTests.cs`, `docs/stage8-evidence/*`) were preserved and completed.
   - All `118` protected historical Stage 6/7/8 files recorded in `docs/stage8-evidence/preservation-before.json`, the sealed 12-case independent evaluation package (`backend/tests/Evaluation/stage6-independent/*`), and the frozen 35-case development benchmark (`backend/tests/Evaluation/stage5_independent_benchmark_35cases.json`, SHA-256 `3d2ee6afd0ac72157c92bddeedc43f94511ba170d7985a1f44177cb78eb9ad84`) remain **byte-identical**.
   - Application database `cx_platform` was **not modified** during Stage 8 verification (`44` `extraction_runs`, `101` `extracted_medications`, `136` `extracted_investigations`, `12` `entity_review_audit`).
2. **Stage 7 Preservation Incidents Explicitly Reconciled:**
   - **Reference extraction `1713c76e-b355-4be3-b02f-e9d581b82415` (`live-final-86985b4aaa88`):** Verified in `cx_platform` with intact clinical payload (`microsoft_foundry`, `gpt-6.1-sol` / `gpt-6.1-sol-2026-09-29`, `7` medications, `10` investigations, and all `12` `entity_review_audit` rows with unchanged MD5 `35fda9dee81280a7097bb8a87008e15f`). Only `saved_at` (`2026-11-23T15:36:14.129548+05:30`) and child UUID primary keys reflect the Stage 7 legacy test replay; because MD5 digests cannot be inverted without an external pre-incident backup, no speculative database edits were made. Duplicate `extraction_id` overwrites are permanently blocked in both `SqlExtractionRepository` and `InMemoryExtractionRepository`.
   - **Stage 5 non-benchmark Playwright outputs (`backend/artifacts/stage5-release-verification/`):** Confirmed gitignored (`git ls-tree -r 72e9ba0` contains zero `backend/artifacts/` objects), so the 7 non-benchmark PNG/JSON files overwritten during early Stage 7 cannot be reconstructed from SHA-256 hashes alone. Stage 6, Stage 7, and Stage 8 Playwright suites now write strictly to isolated stage-specific directories (`docs/stage8-evidence/playwright/`).
3. **Microsoft Foundry `gpt-6.1-sol` Primary Routing Completed & Hardened:**
   - Default primary provider across `ExtractionOptions`, `appsettings.json`, `appsettings.Development.json`, `Apollo-LaunchEnvironment.ps1`, `Start-Apollo.ps1`, and `Start-Apollo.bat` is `MicrosoftFoundry` targeting deployment `gpt-6.1-sol`.
   - `LocalOnnx` (`bc5cdr_int8`) executes **only** on eligible Microsoft Foundry operational failures (`AZURE_TIMEOUT`, `AZURE_UNREACHABLE`, `AZURE_UNAVAILABLE`, `AZURE_RATE_LIMITED`, `AZURE_CIRCUIT_OPEN`, plus `AZURE_UNUSABLE_JSON` / `AZURE_TRUNCATED` only when `AllowParsingFallback=true`, default `false`) or explicit authorized offline/test execution (`AllowDirectLocal=true` + `DirectLocalReason` in `{isolated_test, authorized_offline}`).
   - Unqualified `Extraction:Provider=LocalOnnx` (the exact misconfiguration captured in pre-Stage-8 process `PID 10080`) now fails closed with `HTTP 503 DIRECT_LOCAL_NOT_AUTHORIZED`.
   - Fixed UTF-8 mojibake (`?` replacing `—` and `·`) and snapshot provenance formatting in `frontend/src/app/app.ts`, `frontend/src/app/app.html`, and `frontend/src/app/app.spec.ts`.
4. **All Isolated Verification Suites Passed (`0` Billable Foundry Calls):**
   - **Backend & Isolated PostgreSQL (`cx_audit_test`):** `197 passed, 0 failed, 11 skipped` (`backend/tests/TestResults/stage8-verified.trx`).
   - **Angular 21 Unit Tests & Build:** `36 passed, 0 failed` (`Vitest`); production build succeeded (`364.17 kB` initial bundle).
   - **Playwright Chromium Real Browser Suites:** `14 passed, 0 failed` across `stage5-e2e.mjs` (7), `stage6-e2e.mjs` (2), `stage7-e2e.mjs` (2), and `stage8-e2e.mjs` (3), with isolated schema `foundry_test_f1baef149cdf4a8ca829f41c75c3c3b1` cleanly dropped afterward (`docs/stage8-evidence/browser-fixture-cleanup.txt`).

---

## 2. Stage 7 Reconciliation & Application Database Audit

### 2.1 Application Database (`cx_platform`) Forensic Timeline

Read-only inspection of `cx_platform` (`docs/stage8-evidence/reconciliation-and-preservation-after.json`) reconciled the exact row counts and table MD5 digests across Stage 7 and Stage 8:

| Table (`cx_platform.public`) | Pre-Stage-7 (`application-records-before.json`) | Post-Stage-7 (`application-records-after.json`) | Stage 8 Before & After (`reconciliation-and-preservation-after.json`) | Stage 8 Mutation |
|---|---|---|---|---|
| `extraction_runs` | `41` (`4e27aaed8205543e3b4f69168bf40768`) | `41` (`0f10a7eb17f92feb1c7439bf3da8a81f`) | `44` (`17f0e1b4996eb2e300d46ed23ebf7509`) | **`0` rows changed** |
| `extracted_medications` | `88` (`7fa874e2c265a1a69b0dc89bb86694a4`) | `88` (`ec49bcfdec5842dbe2c91a10b72c80dc`) | `101` (`d65822c8d89ffec2e9ce8dc59a24fe83`) | **`0` rows changed** |
| `extracted_investigations` | `120` (`1fa4bc808c3de96507df25da895c300f`) | `120` (`003405d9bd4ca39220b8e43828eecbcd`) | `136` (`39b55cfb228137af6a45a0aba46ef890`) | **`0` rows changed** |
| `entity_review_audit` | `12` (`35fda9dee81280a7097bb8a87008e15f`) | `12` (`35fda9dee81280a7097bb8a87008e15f`) | `12` (`35fda9dee81280a7097bb8a87008e15f`) | **`0` rows changed** |

**Root Cause of the 3 Runs Added Between Stage 7 (`15:46` IST) and Stage 8 (`16:31` IST on `2026-11-23`):**
- `docs/stage8-evidence/running-process-before.json` captured that an interactive Debug backend (`PID 10080`) had been launched prior to Stage 8 with `Extraction__Provider=LocalOnnx` in its process environment block.
- Three interactive runs were saved to `cx_platform` under that process (`d2dd7ad8-cd4d-457e-8de8-4b206b3e7bd0` at `16:06:38+05:30`, `6182e4e5-e371-428d-a5da-11aaff8e292e` at `16:22:04+05:30`, and `0fcbffd6-13c5-449d-a6af-8b29b4ffd262` at `16:30:46+05:30`), all with `adapter_type = onnx_neurosymbolic_int8` and `model_deployment = bc5cdr_int8`.
- Stage 8 eliminates this failure mode permanently via `ProviderRoutingPolicy.DirectLocalFault`: starting or calling the backend with `Extraction:Provider=LocalOnnx` without explicit `AllowDirectLocal=true` and an authorized `DirectLocalReason` (`isolated_test` or `authorized_offline`) is rejected with `HTTP 503 DIRECT_LOCAL_NOT_AUTHORIZED`.

### 2.2 Stage 7 Reference Extraction `1713c76e-b355-4be3-b02f-e9d581b82415`

- **Current Verified State in `cx_platform`:**
  - `extraction_id`: `1713c76e-b355-4be3-b02f-e9d581b82415`
  - `correlation_id`: `live-final-86985b4aaa88`
  - `adapter_type`: `microsoft_foundry` · `model_deployment`: `gpt-6.1-sol` · `provenance.model`: `gpt-6.1-sol-2026-09-29`
  - Child entities: `7` `extracted_medications` (`5` discharge + `2` non-active context), `10` `extracted_investigations` (`8` follow-up + `2` historical inpatient labs), and `12` clinician review audit entries (`entity_review_audit` MD5 `35fda9dee81280a7097bb8a87008e15f` identical to pre-Stage-7).
- **Reconciliation Disposition:**
  - During an early Stage 7 legacy replay test (`PostLiveVerificationAuditTests`), `SaveExtractionAsync` executed an `ON CONFLICT (extraction_id) DO UPDATE` + child re-insert on `cx_platform`, updating `saved_at` to `2026-11-23T15:36:14.129548+05:30` and regenerating child row UUIDs while preserving the clinical payload and all `12` review audit rows.
  - Because `docs/stage7-evidence/application-records-before.json` stores one-way MD5 digests rather than raw row dumps and no pre-incident PostgreSQL backup exists in the workspace, exact pre-incident `saved_at` and child UUIDs cannot be reconstructed without fabricating historical timestamps/UUIDs. Accordingly, `cx_platform` was preserved untouched, and duplicate `extraction_id` writes now throw `InvalidOperationException` in `SqlExtractionRepository` and `InMemoryExtractionRepository` (verified by `Stage8PostgresProvenanceTests` and `PostLiveVerificationAuditTests`).

### 2.3 Stage 5 Non-Benchmark Browser Artifacts (`backend/artifacts/stage5-release-verification/`)

- **Reconciliation Disposition:**
  - `docs/stage7-evidence/preservation-verification.json` documented that 7 non-benchmark Stage 5 Playwright files (`playwright-e2e-verification.json` and 6 screenshots under `backend/artifacts/stage5-release-verification/`) were overwritten before Stage 7 parameterized Playwright output paths, while all `33` clinical/benchmark/Stage 6/sealed artifacts remained `100%` intact.
  - Inspection of Git history (`git ls-tree -r 72e9ba0`) confirmed `backend/artifacts/` is gitignored and absent from Git object storage; therefore the pre-Stage-7 binary PNGs cannot be restored from SHA-256 hashes alone.
  - Non-recurrence is verified: Stage 8 passed `STAGE5_E2E_OUTPUT_DIR`, `STAGE6_E2E_OUTPUT_DIR`, `STAGE7_E2E_OUTPUT_DIR`, and `STAGE8_E2E_OUTPUT_DIR` pointing exclusively to `docs/stage8-evidence/playwright/`, and post-run SHA-256 verification confirmed `118/118` protected files in `docs/stage8-evidence/preservation-before.json` are untouched.

---

## 3. Primary Provider Routing (`MicrosoftFoundry` `gpt-6.1-sol`) & UI Provenance

### 3.1 Deterministic Routing Policy (`ProviderRoutingPolicy` & `ExtractionWorkflow`)

| Condition / Fault Code | Primary Attempted | Fallback to `LocalOnnx` (`bc5cdr_int8`) | Resulting API / Provenance Outcome |
|---|---|---|---|
| Healthy `MicrosoftFoundry` (`gpt-6.1-sol`) | Yes | **No** | `200 OK` · `executedProvider=MicrosoftFoundry`, `fallbackUsed=false` |
| `AZURE_TIMEOUT`, `AZURE_UNREACHABLE`, `AZURE_UNAVAILABLE`, `AZURE_RATE_LIMITED` | Yes (bounded retry: max `1` retry, `Retry-After` capped at `2000 ms`) | **Yes** (when `EnableFallback=true`) | `200 OK` · `executedProvider=LocalOnnx`, `fallbackUsed=true`, structured `fallbackReasonCode` + `fallbackReason` |
| `AZURE_CIRCUIT_OPEN` (`3` transient failures $\rightarrow$ `30 s` open window) | Short-circuited while open | **Yes** (when `EnableFallback=true`) | `200 OK` · `executedProvider=LocalOnnx`, `fallbackUsed=true`, `fallbackReasonCode=AZURE_CIRCUIT_OPEN` |
| `AZURE_UNUSABLE_JSON`, `AZURE_TRUNCATED` | Yes (no retry) | **Default: No** (`AllowParsingFallback=false`); **Optional: Yes** if `AllowParsingFallback=true` | Default `502 Bad Gateway` with fault code; fallback only when explicitly enabled |
| `AZURE_INVALID_EVIDENCE` (ungrounded spans / offsets) | Yes | **Never** | `422 Unprocessable Entity` (`CLINICAL_GROUNDING_FAILED`) — never masked by ONNX |
| `AZURE_NOT_CONFIGURED`, `AZURE_AUTH_FAILED`, `AZURE_DEPLOYMENT_NOT_FOUND`, `AZURE_BAD_REQUEST`, `AZURE_CONTENT_FILTERED` | Config/auth/policy failure | **Never** | Terminal `4xx/5xx` with explicit `ProviderFaultCode` |
| `Extraction:Provider=LocalOnnx` without `AllowDirectLocal=true` + valid `DirectLocalReason` | N/A | **Blocked** | `503 Service Unavailable` (`DIRECT_LOCAL_NOT_AUTHORIZED`) |
| `Extraction:Provider=LocalOnnx` + `AllowDirectLocal=true` + `DirectLocalReason` in `{isolated_test, authorized_offline}` | No (intentional offline mode) | Direct local | `200 OK` · `executedProvider=LocalOnnx`, `fallbackUsed=false`, `directLocalReason` recorded |

### 3.2 Stage 8 Refinements Implemented in This Session

1. **Angular 21 Provider Provenance & UTF-8 Restoration (`frontend/src/app/app.ts`, `app.html`, `app.spec.ts`):**
   - Replaced corrupted literal `?` separators introduced in commit `59d79db` with proper UTF-8 em-dashes (`—`) and middle dots (`·`) across `providerLabel()`, source candidate chips, and `data-testid="routing-provenance"`.
   - Updated `providerLabel()` so exact Foundry model snapshots render both snapshot provenance and canonical model family (`Microsoft Foundry — gpt-6.1-sol-2026-09-29 (GPT-6.1 Sol)`), plain deployment IDs render as `Microsoft Foundry — GPT-6.1 Sol`, authorized offline runs render as `Local ONNX — Offline Mode`, and fallback runs render as `Local ONNX — Fallback after Microsoft Foundry failure · <reason>`.
2. **Launcher Hardening (`Start-Apollo.bat`, `Start-Apollo.ps1`):**
   - `Start-Apollo.bat` now exits cleanly without interactive `pause` when `-ConfigurationOnly` or `-NoBrowser` is passed (`%~1` / `%~2`), enabling non-interactive CI/CLI verification.
   - `Start-Apollo.ps1` now checks `configurationReady == true` on `/v1/routing` even when reusing an already-running listener on port `5000`, surfacing `configurationError` immediately instead of silently reusing a misconfigured backend process.
3. **Browser Provenance Suite (`frontend/e2e/stage8-e2e.mjs`):**
   - Added 3 real-browser Chromium scenarios verifying `/v1/routing`, primary `MicrosoftFoundry` (`gpt-6.1-sol`) provenance banner, eligible `AZURE_RATE_LIMITED` fallback provenance banner (`Local ONNX — Fallback after Microsoft Foundry failure`), authorized offline mode provenance (`Local ONNX — Offline Mode`), and absence of `?` mojibake.

---

## 4. Effective Provider Configuration Verification

All launchers and ASP.NET Core entrypoints were executed with zero inference calls:

| Entrypoint / Command | Environment | `requestedPrimaryProvider` | `fallbackProvider` | `foundryDeployment` / `targetModel` | `directLocalAuthorized` | `configurationReady` (`configurationError`) |
|---|---|---|---|---|---|---|
| `Start-Apollo.bat -ConfigurationOnly` | `Development` | `MicrosoftFoundry` | `LocalOnnx` | `gpt-6.1-sol` (via `Apollo-LaunchEnvironment.ps1`) | `false` | Ready (`secretsPrinted=false`) |
| `Start-Apollo.ps1 -ConfigurationOnly` | `Development` | `MicrosoftFoundry` | `LocalOnnx` | `gpt-6.1-sol` | `false` | Ready (`secretsPrinted=false`) |
| `dotnet Apollo.Backend.dll --routing-diagnostics --environment Development` *(cwd: `backend/`)* | `Development` | `MicrosoftFoundry` | `LocalOnnx` | `gpt-6.1-sol` / `gpt-6.1-sol` (`ApiKey` loaded from ignored `appsettings.Local.json`) | `false` | **`true`** (`null`) |
| `Initialize-ApolloLaunchEnvironment` + `dotnet run --project backend/Apollo.Backend.csproj -- --routing-diagnostics` *(cwd: repo root)* | `Development` | `MicrosoftFoundry` | `LocalOnnx` | `gpt-6.1-sol` / `gpt-6.1-sol` (`ApiKey`) | `false` | **`true`** (`null`) |
| `dotnet Apollo.Backend.dll --routing-diagnostics --environment Production` *(cwd: `backend/`, no env secrets)* | `Production` | `MicrosoftFoundry` | `LocalOnnx` | `gpt-6.1-sol` / `gpt-6.1-sol` (`None`) | `false` | **`false`** (`AZURE_NOT_CONFIGURED`) |

> **Operational Note on `ContentRootPath`:** Invoking compiled `dotnet backend\bin\Release\net10.0\Apollo.Backend.dll` directly from `D:\AI Extraction` (without setting working directory to `D:\AI Extraction\backend` or passing `--contentRoot`) causes ASP.NET Core to look for `appsettings.Local.json` in the repo root and report `AZURE_NOT_CONFIGURED`. Both `Start-Apollo.ps1` (`WorkingDirectory = $backendDir`) and `dotnet run --project backend/Apollo.Backend.csproj` set `ContentRootPath` to `D:\AI Extraction\backend` properly.

**Non-Billable Foundry Metadata Probe (`docs/stage8-evidence/foundry-metadata-probe.json`):**
- `GET https://935125-0774-resource.services.ai.azure.com/openai/v1/models` $\rightarrow$ `HTTP 200`
- `GET https://935125-0774-resource.services.ai.azure.com/openai/v1/models/gpt-6.1-sol` $\rightarrow$ `HTTP 200` (`id = "gpt-6.1-sol-2026-09-29"`, `inferenceRequestsSent = 0`).
- *Limitation:* Control/data-plane model listing confirms endpoint reachability, API key validity, and model visibility (`gpt-6.1-sol-2026-09-29`), but does **not** prove live `POST /openai/v1/chat/completions` inference quota or execution post-Stage-8.

---

## 5. Isolated Verification Suite Results (`0` Billable Calls)

| Suite | Target / Isolation | Result | Evidence Artifact |
|---|---|---|---|
| Solution Release Build | `dotnet build AIExtraction.slnx -c Release` | **PASS** (`0` warnings, `0` errors) | `backend/bin/Release/net10.0/Apollo.Backend.dll` |
| Backend Unit, Routing, Config & PostgreSQL Provenance Tests | `dotnet test` against `cx_audit_test` per-test schemas (`FoundryPostgresTestFixture`) | **`197 passed, 0 failed, 11 skipped`** (`208` total; all `11` skips are live-Foundry spend gates + sealed evaluation gate) | `backend/tests/TestResults/stage8-verified.trx` |
| Angular 21 Unit Suite | `npm test -- --watch=false` (Vitest) | **`36 passed, 0 failed`** | `frontend/src/app/app.spec.ts` |
| Angular 21 Production Build | `npm run build` | **PASS** (`364.17 kB` initial bundle) | `frontend/dist/cx-review-ui/browser` |
| Playwright Chromium E2E (`stage5-e2e.mjs` + `stage6-e2e.mjs` + `stage7-e2e.mjs` + `stage8-e2e.mjs`) | Isolated server `127.0.0.1:5108` + temporary schema `foundry_test_f1baef149cdf4a8ca829f41c75c3c3b1` in `cx_audit_test` (dropped post-run) | **`14 passed, 0 failed`** (`7 + 2 + 2 + 3` scenarios, `0` page errors, `0` live Foundry calls) | `docs/stage8-evidence/playwright/stage8-browser-results.json`, `docs/stage8-evidence/browser-fixture-cleanup.txt` |
| 35-Case Frozen Development Regression (`LocalOnnx` only) | Offline `LocalOnnx` (`bc5cdr_int8` + deterministic posology/ontology) | Medication micro F1 **`0.9000`** (`72 TP, 0 FP, 16 FN`); Investigation micro F1 **`0.9677`** (`75 TP, 1 FP, 4 FN`); Matched-entity attributes **`100%`** | `docs/stage8-evidence/after.json` |
| Post-Verification Preservation Audit | `118` protected files + sealed dataset + `cx_platform` read-only MD5 digests | **`118/118` files byte-identical**; **`cx_platform` `100%` unchanged** | `docs/stage8-evidence/reconciliation-and-preservation-after.json` |

---

## 6. Remaining Limitations, Open Risks & Hospital Production Non-Readiness

Apollo **is NOT ready for hospital or clinical production deployment**. Remaining gaps and limitations:

1. **Post-Stage-8 Live `gpt-6.1-sol` Inference Not Executed (`Zero-Spend Constraint`):**
   - Routing, retry/circuit-breaker handling, grounding validation, and PostgreSQL provenance persistence were verified with deterministic HTTP doubles and captured Foundry responses, plus non-billable `GET /openai/v1/models/gpt-6.1-sol` (`HTTP 200`), but no live `POST /chat/completions` call was sent after the Stage 8 routing refactor.
2. **Local ONNX Fallback Recall Ceiling & Manifest Provenance Gap:**
   - When `MicrosoftFoundry` is unavailable and Apollo degrades to `LocalOnnx` (`bc5cdr_int8`), recall on the 35-case development set is `81.8%` for medications (`16` FNs, primarily multi-token/vaccine/insulin mentions missed by BC5CDR NER) and `94.9%` for investigations (`4` FNs).
   - As documented in Stage 7 (`docs/stage7-evidence/model-integrity-audit.json`), `backend/Models/bc5cdr_int8/training-manifest.json` records a `tokenizer.json` SHA-256 that differs from the on-disk tokenizer file and remains unresolved pending original export provenance.
3. **Unblinded External Clinician Evaluation Pending:**
   - The sealed 12-case independent evaluation set (`backend/tests/Evaluation/stage6-independent/*`) remains unexecuted because independent board-certified clinician annotation/adjudication has not yet been supplied.
4. **Institutional Security & Infrastructure Controls Pending:**
   - Local development runs with `Security:LocalDemoMode=true` over loopback HTTP (`127.0.0.1:5000`) and local PostgreSQL trust/password configuration (`data_cx`). Hospital deployment requires institutional OIDC/Entra ID authentication (`LocalDemoMode=false`), patient-chart entitlement enforcement, TLS termination, Key Vault / Managed Identity secret management, and PostgreSQL `scram-sha-256` `pg_hba.conf` enforcement (`docs/stage7-evidence/scram-rehearsal.json`).

---

## 7. Exact Next Action

1. **Owner Sign-Off on Reconciled `cx_platform` Baseline:** Confirm acceptance of the documented `44`-run `cx_platform` baseline (`docs/stage8-evidence/reconciliation-and-preservation-after.json`) and Stage 7 historical artifact disposition without synthetic row/file fabrication.
2. **Optional Single Authorized Live `gpt-6.1-sol` Smoke Test (Requires Explicit Spend Authorization):** When you are ready to authorize **one** synthetic Microsoft Foundry `POST /chat/completions` call (zero retries, ~500 tokens), run:
   ```powershell
   $env:AUTHORIZE_STAGE8_SINGLE_LIVE_CALL = "SYNTHETIC_ONE_REQUEST_NO_RETRIES"
   dotnet test "backend\tests\Apollo.Backend.Tests.csproj" -c Release --filter "FullyQualifiedName~Stage8OptionalLiveTests.AuthorizedSingleSyntheticFoundryCall_UsesGpt61SolWithoutFallback"
   Remove-Item Env:AUTHORIZE_STAGE8_SINGLE_LIVE_CALL
   ```
   Then launch the interactive workspace via `.\Start-Apollo.ps1` so port `5000` serves `MicrosoftFoundry` (`gpt-6.1-sol`) as primary provider with `LocalOnnx` (`bc5cdr_int8`) strictly on standby for eligible operational fallback.
