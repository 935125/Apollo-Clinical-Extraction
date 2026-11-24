# APOLLO CLINICAL EXTRACTION PLATFORM
## Stage 8.1 — Live GPT-6.1 Sol Verification & Primary Model Acceptance Report

**Audit Date:** `2026-11-24`  
**Workspace:** `D:\AI Extraction`  
**Acceptance Verdict:** **`BLOCKED — AWAITING LIVE-CALL AUTHORIZATION`**  
**Billable Microsoft Foundry API Calls Executed:** `0`  
**Hospital Production Readiness:** **NOT READY FOR HOSPITAL PRODUCTION**

---

## 1. Executive Summary

All non-billable Stage 8.1 preflight, configuration, Foundry metadata readiness, offline fallback policy, regression, and preservation checks **passed (`0` failures, `0` billable Foundry calls)**.

Per Section 5 (**Authorization boundary**) of the Stage 8.1 directive, receiving the verification specification does **not** grant spending authorization for a live Microsoft Foundry completion call. Accordingly, **no `POST /openai/v1/chat/completions` request was executed**, and the acceptance verdict is **`BLOCKED — AWAITING LIVE-CALL AUTHORIZATION`**. Once explicit authorization is granted for one synthetic live request, Phases C–F can execute immediately using the single-request, zero-retry, zero-fallback, in-memory-persistence gate.

---

## 2. Effective Routing Configuration (Phase A)

Inspected `backend/appsettings.json`, `backend/appsettings.Development.json`, `backend/Configuration/LocalDevelopmentConfiguration.cs`, `Apollo-LaunchEnvironment.ps1`, `Start-Apollo.ps1`, `Start-Apollo.bat`, `ProviderRoutingPolicy.cs`, `ExtractionWorkflow.cs`, `FoundryClinicalExtractor.cs`, and `Stage8OptionalLiveTests.cs`. Evidence recorded in `docs/stage8.1-evidence/foundry-readiness.json` and `docs/stage8.1-evidence/running-listener-5000.json`.

| Check | Verified Result | Status |
|---|---|---|
| Primary provider (`appsettings.json`, `appsettings.Development.json`, launchers, running listener) | `MicrosoftFoundry` | PASS |
| Fallback provider | `LocalOnnx` | PASS |
| Foundry deployment / target model | `gpt-6.1-sol` / `gpt-6.1-sol` | PASS |
| Configured Foundry endpoint host | `935125-0774-resource.services.ai.azure.com` (`/openai/v1/`) | PASS |
| Credentials available without printing secrets | Loaded from ignored `.env` (`LocalDevelopmentConfiguration.cs`) in `Development`; `secretsPrinted=false`; `Production` fails closed with `AZURE_NOT_CONFIGURED` | PASS |
| Direct local execution | `directLocalAuthorized=false` (`AllowDirectLocal=false`) | PASS |
| Fallback policy intact | Eligible operational faults only; `AllowParsingFallback=false`; `AZURE_INVALID_EVIDENCE` and auth/config/policy faults never fall back | PASS |
| Running backend process on ports `5000`/`8000` | `Apollo.Backend.exe` (`PID 41148`) reports `GET /v1/routing` `HTTP 200` with `requestedPrimaryProvider="MicrosoftFoundry"`, `fallbackProvider="LocalOnnx"`, `foundryDeployment="gpt-6.1-sol"`, `directLocalAuthorized=false`, `configurationReady=true` (replaces pre-Stage-8 `LocalOnnx` `PID 10080`) | PASS |
| ASP.NET Core `ContentRootPath` | `D:\AI Extraction\backend` when launched via `Start-Apollo.ps1` / `Start-Apollo.bat` / `dotnet run --project backend/Apollo.Backend.csproj` | PASS |
| Database isolation | `cx_platform` untouched; destructive/automated tests restricted to `cx_audit_test` isolated schemas (`FoundryPostgresTests.cs`) and `InMemoryExtractionRepository` in `Stage8OptionalLiveTests.cs` | PASS |

Verified sanitized `--routing-diagnostics` output (`Development`, `0` inference calls):

```json
{
  "environment": "Development",
  "requestedPrimaryProvider": "MicrosoftFoundry",
  "fallbackProvider": "LocalOnnx",
  "directLocalAuthorized": false,
  "directLocalReason": null,
  "foundryDeployment": "gpt-6.1-sol",
  "targetModel": "gpt-6.1-sol",
  "authenticationMode": "ApiKey",
  "configurationError": null,
  "configurationReady": true,
  "extractionInferenceExecuted": false
}
```

---

## 3. Foundry Endpoint and Deployment Readiness (Phase B — Non-Billable Only)

Executed non-billable `GET` metadata checks against `https://935125-0774-resource.services.ai.azure.com/openai/v1/` (`docs/stage8.1-evidence/foundry-readiness.json`):

- `GET /openai/v1/models` $\rightarrow$ **`HTTP 200`**
- `GET /openai/v1/models/gpt-6.1-sol` $\rightarrow$ **`HTTP 200`** (`returnedModelId = "gpt-6.1-sol-2026-09-29"`, catalog matches: `["gpt-6.1-sol-2026-09-29", "gpt-6.1-sol"]`, combined latency `4101 ms`, `inferenceRequestsSent = 0`, `secretsPrinted = false`).
- **Scope Limitation:** Metadata `HTTP 200` confirms endpoint reachability, credential validity, and deployment visibility, but does **not** prove live `POST /openai/v1/chat/completions` inference execution.

---

## 4. Explicit Live-Call Authorization Status (Phase C)

- **Status:** **`AWAITING LIVE-CALL AUTHORIZATION`**
- **Existing Live Gate Inspected (`backend/tests/Stage8OptionalLiveTests.cs`):**
  - Method present: `OptionalSingleAuthorizedGptRequest_NoRetries_NoFallback_NoApplicationDatabase` (gated by `AUTHORIZE_STAGE8_SINGLE_LIVE_CALL=SYNTHETIC_ONE_REQUEST_NO_RETRIES`).
  - Verified safety guards: `Stage8SingleRequestBudget` (`DelegatingHandler` enforcing max `1` HTTP call), `MaxRetries = 0`, `AllowLocalFallback = false`, `AllowParsingFallback = false`, `MaxCompletionTokens = 2048`, `InMemoryExtractionRepository` (no writes to `cx_platform`), and pre-send `single-call.consumed` lockfile.
  - **Note for Execution Upon Approval:** The existing test uses `"Plan: Continue metformin 500 mg, one tablet by mouth once daily. Order CBC today."` and writes `result.json` without separately recording raw HTTP status, ONNX extraction invocation counter, or the Stage 8.1 synthetic note (`"Patient with hypertension. Continue amlodipine 5 mg orally once daily for 7 days. Order complete blood count tomorrow."`). Upon explicit user approval, we can either run the existing test unchanged or capture all 15 Phase D telemetry fields on the Stage 8.1 synthetic note under the same single-request, zero-retry, zero-fallback, in-memory guard.

---

## 5. Actual Live Call Status

**NOT EXECUTED** — awaiting explicit user authorization for one synthetic billable Microsoft Foundry completion request (`0` live requests sent).

## 6. Completion ID and Model Identity

**NOT EXECUTED** — awaiting live-call authorization. *(Non-billable metadata `GET /openai/v1/models/gpt-6.1-sol` resolved model ID `gpt-6.1-sol-2026-09-29`, which is metadata only.)*

## 7. Provider Execution Trace

**NOT EXECUTED (Live)** — awaiting live-call authorization.

## 8. GPT-versus-ONNX Invocation Evidence

**NOT EXECUTED (Live)** — awaiting live-call authorization. Expected acceptance assertions (`requestedPrimaryProvider=MicrosoftFoundry`, `actualExecutedProvider=MicrosoftFoundry`, `fallbackUsed=false`, `onnxExtractionInvocations=0`, `modelDeployment=gpt-6.1-sol`) remain pending live execution.

## 9. Clinical Extraction Quality Assessment (Live)

**NOT EXECUTED (Live)** — awaiting live-call authorization.

## 10. Source Grounding Results (Live)

**NOT EXECUTED (Live)** — awaiting live-call authorization.

## 11. Token Usage and Latency (Live)

**NOT EXECUTED (Live)** — awaiting live-call authorization.

## 12. Reported or Estimated Cost

- **Actual Spend in Stage 8.1 So Far:** `\$0.00` (`0` billable requests).
- **Estimated Single Authorized Synthetic Call:** `< 1,500` total tokens (single short synthetic note, `MaxRetries = 0`, `MaxCompletionTokens <= 2048`).

---

## 13. UI Provider-Provenance Verification (Offline Baseline)

- **Angular 21 Provider-Display Unit Suite (`frontend/src/app/app.spec.ts`):** Freshly executed in Stage 8.1 $\rightarrow$ **`36 passed, 0 failed`** (`1.98 s`), confirming `Microsoft Foundry — GPT-6.1 Sol`, `Microsoft Foundry — gpt-6.1-sol-2026-09-29 (GPT-6.1 Sol)`, `Local ONNX — Offline Mode`, and `Local ONNX — Fallback after Microsoft Foundry failure · <reason>`.
- **Playwright Chromium Provenance Suite (`frontend/e2e/stage8-e2e.mjs`):** Previously verified against the identical unchanged Release binary and Angular dist in Stage 8 (`docs/stage8-evidence/playwright/stage8-browser-results.json`: **`3/3` Stage 8 scenarios passed**, `14/14` total across Stages 5–8, `0` live Foundry calls, `0` page errors). Post-live UI verification with the captured live synthetic response remains pending Phase C authorization.

---

## 14. PostgreSQL Preservation & Historical Evidence Results (Phase G)

- **Application Database (`cx_platform`):** Verified read-only before (`docs/stage8.1-evidence/cx-platform-before.json`) and after (`docs/stage8.1-evidence/cx-platform-after.json`) Stage 8.1 offline suites:
  - `extraction_runs`: `44` (`17f0e1b4996eb2e300d46ed23ebf7509`) — **UNCHANGED**
  - `extracted_medications`: `101` (`d65822c8d89ffec2e9ce8dc59a24fe83`) — **UNCHANGED**
  - `extracted_investigations`: `136` (`39b55cfb228137af6a45a0aba46ef890`) — **UNCHANGED**
  - `entity_review_audit`: `12` (`35fda9dee81280a7097bb8a87008e15f`) — **UNCHANGED**
- **Historical Evidence Preservation:** **`118/118`** protected files in `docs/stage8-evidence/preservation-before.json` verified **byte-identical** (`SHA-256` match). Sealed 12-case evaluation dataset and 35-case benchmark untouched.

---

## 15. Offline Fallback Policy & Regression Tests (Phases H & I)

Freshly executed in Stage 8.1 (`0` billable Foundry calls):

| Suite | Result | Artifact |
|---|---|---|
| Backend & Isolated PostgreSQL (`cx_audit_test`) — includes `Stage8RoutingTests`, `Stage8ConfigurationTests`, `Stage8PostgresProvenanceTests`, Foundry response handling, ONNX fallback, clinical validation, and safety tests | **`197 passed, 0 failed, 11 skipped`** (`208` total, `19 s`) | `backend/tests/TestResults/stage8-1-offline.trx` |
| Angular 21 Unit & Provider-Display Tests | **`36 passed, 0 failed`** (`1.98 s`) | `frontend/src/app/app.spec.ts` |
| Playwright Chromium Provenance Suite *(Stage 8 baseline, unchanged build)* | **`14 passed, 0 failed`** (`3/3` Stage 8 scenarios) | `docs/stage8-evidence/playwright/stage8-browser-results.json` |

All 8 Phase H fallback policy scenarios are verified offline in `Stage8RoutingTests` and `Stage8ConfigurationTests` (`Foundry succeeds -> MicrosoftFoundry`, `timeout/unavailable/rate-limited -> LocalOnnx when enabled`, `invalid evidence -> 422 explicit validation failure`, `auth failure -> explicit error`, `direct unauthorized ONNX -> HTTP 503`, `explicit authorized offline -> LocalOnnx`).

---

## 16. Remaining Issues

1. **Live GPT-6.1 Sol Completion Pending Authorization:** No post-Stage-8 live `POST /openai/v1/chat/completions` call has been executed yet.
2. **Test Runner Dependency on `TEST_POSTGRES_CONNECTION`:** Running `dotnet test` without `TEST_POSTGRES_CONNECTION` set causes `ForensicPipelineAuditTests.CapturedLiveFoundryResponse_ValidatesAllFiveMedications_AllEightOrders_AndPersistsToIsolatedPostgres` to throw in `IsolatedDatabase.DisposeAsync()` instead of skipping cleanly (unlike `[PostgresFact]` tests). With `TEST_POSTGRES_CONNECTION` pointing to `cx_audit_test`, all `197` runnable tests pass (`0` failed).
3. **Hospital Production Controls:** Unchanged from Stage 8 — Apollo remains **NOT READY FOR HOSPITAL PRODUCTION**.

---

## 17. Acceptance Verdict

**`BLOCKED — AWAITING LIVE-CALL AUTHORIZATION`**

All offline prerequisites (Phases A, B, G, H, I) are verified and passing with zero Foundry spend. Reply with explicit authorization to execute **one** synthetic live Microsoft Foundry `gpt-6.1-sol` completion request (`0` retries, `0` fallback, `InMemoryExtractionRepository`) to complete Phases C–F and issue a final `PASS` or `FAIL` verdict.
