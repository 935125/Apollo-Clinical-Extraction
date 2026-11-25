# Apollo Clinical Extraction Platform — Postman CLI Debugging & Root-Cause Report

**Date:** 25 November 2026

---

## 1. Initial Failure Reproduced & Diagnosed

- **Reported Error:** `GET http://127.0.0.1:5000/health` → `connect ECONNREFUSED 127.0.0.1:5000` followed by `AssertionError: Backend returns HTTP 200`.
- **Error Categories & Root Causes Identified:**
  1. **Backend startup / Networking (`ECONNREFUSED`):** `Apollo.Backend` (`backend/Apollo.Backend.csproj`, bound via `Kestrel:Endpoints:Http5000` to `http://127.0.0.1:5000`) was not running when the initial Postman CLI command was executed.
  2. **API Contract Defect (`GET /health` returned Angular HTML):** Even after starting the backend, `GET http://127.0.0.1:5000/health` had no explicit endpoint mapping in `Program.cs` and fell through to `app.MapFallback(...)`, returning `200 OK text/html` (`index.html`) instead of JSON liveness.
  3. **API Contract Defect (Malformed `/api/...` routes returned Angular HTML `200 OK`):** Caught live by Postman test `9.3` (`GET /api/v1/queue/not-a-valid-guid` returned `200 OK` 2.06 KB HTML from `MapFallback` instead of `404 Not Found` JSON).
  4. **Missing Database Readiness Probe:** `/health/ready` previously checked `pipeline` and `provider_configuration` without probing PostgreSQL (`cx_platform`).
  5. **Test Harness Defect:** The original 1-request collection did not guard against missing `pm.response` on transport failures, causing secondary assertion failures when Kestrel was down.

---

## 2. Root-Cause Fixes Applied

| File | Root Cause Fixed | Verification |
| --- | --- | --- |
| [`backend/Configuration/OpenApiEndpoints.cs`](../backend/Configuration/OpenApiEndpoints.cs) | Added `PostgresReadinessHealthCheck` (`SELECT 1` with 3s timeout) and Development-only `/openapi/v1.json` (OpenAPI 3.0.3) + `/swagger` UI. | `GET /health/db`, `GET /health/ready`, `GET /openapi/v1.json`, `GET /swagger` all return `200`. |
| [`backend/Program.cs`](../backend/Program.cs) | Mapped explicit JSON `GET /health` and `GET /health/db`; registered `PostgresReadinessHealthCheck` on `/health/ready`; guarded `MapFallback` so unmatched `/api`, `/v1`, `/health`, and `/openapi` paths return JSON `404 ENDPOINT_NOT_FOUND` instead of `index.html`. | Postman `1.1`, `1.3`, `1.4`, `1.5`, and `9.3` all PASS (`0 warnings, 0 errors` in Debug & Release). |
| [`postman/Apollo-Clinical-Extraction.postman_collection.json`](../postman/Apollo-Clinical-Extraction.postman_collection.json) | Rebuilt into 9 structured folders (36 requests, 41 assertions) with transport-safe `BLOCKED` checks, CSRF/session auth, bounded live polling (`maxPollAttempts=15`), medication/investigation/UHID-isolation/provider-routing/idempotency tests. | `36/36` requests, `41/41` assertions PASS in `8.8 s`. |
| [`Run-Apollo-Postman-Tests.ps1`](../Run-Apollo-Postman-Tests.ps1) & [`Run-Apollo-Postman-Tests.bat`](../Run-Apollo-Postman-Tests.bat) | Automated CLI resolution, JSON validation, `BLOCKED` preflight detection (`exit 2` when unreachable), opt-in zero-cost `-StartBackend` startup, and redacted diagnostic transcripts. | Verified both stopped-backend `BLOCKED` exit (`2`) and live `-StartBackend` run (`0`). |
