# Apollo Clinical Extraction Platform — Postman CLI Setup & One-Command Runner

**Date:** 25 November 2026  
**Verified CLI:** `postman-cli@1.94.0` (`C:\Users\intern_ali\AppData\Roaming\npm\postman.cmd`)

---

## 1. Environment Discovery

- **Global npm prefix:** `C:\Users\intern_ali\AppData\Roaming\npm`
- **Executable:** `C:\Users\intern_ali\AppData\Roaming\npm\postman.cmd`
- **Admin privileges required:** None.
- **PATH independence:** [`Run-Apollo-Postman-Tests.ps1`](../Run-Apollo-Postman-Tests.ps1) automatically resolves `postman.cmd` via `Get-Command`, `npm config get prefix`, and `$env:APPDATA\npm\postman.cmd`, so execution succeeds even when the global npm folder is absent from `PATH`.

---

## 2. Prepared Files

| File | Purpose |
| --- | --- |
| [`postman/Apollo-Clinical-Extraction.postman_collection.json`](../postman/Apollo-Clinical-Extraction.postman_collection.json) | 9-folder Postman Collection v2.1 covering Infrastructure, Authentication, Ingestion, Async Polling, Medications, Investigations, UHID Isolation, Provider Routing, and Idempotency. |
| [`postman/Apollo-Local.postman_environment.json`](../postman/Apollo-Local.postman_environment.json) | Local development environment targeting `http://127.0.0.1:5000` with bounded polling settings (`maxPollAttempts=15`, `pollIntervalMs=2000`). |
| [`Run-Apollo-Postman-Tests.ps1`](../Run-Apollo-Postman-Tests.ps1) | One-command PowerShell runner with preflight reachability/readiness checks, optional `-StartBackend` zero-cost launcher, sanitized transcript logging, and real exit-code propagation. |
| [`Run-Apollo-Postman-Tests.bat`](../Run-Apollo-Postman-Tests.bat) | Windows batch wrapper invoking `Run-Apollo-Postman-Tests.ps1`. |

---

## 3. Usage

```powershell
# Run against an already-running backend on http://127.0.0.1:5000 (Angular not required)
cd "D:\AI Extraction"
.\Run-Apollo-Postman-Tests.ps1

# Opt-in startup mode: starts bundled PostgreSQL (5432) and ASP.NET Core backend (5000)
# in zero-cost LocalOnnx authorized_offline mode if not already listening
.\Run-Apollo-Postman-Tests.ps1 -StartBackend
```

- When the backend is unreachable, the runner exits with code `2` and prints `BLOCKED: Backend API unreachable at http://127.0.0.1:5000` instead of reporting misleading clinical extraction failures.
- Diagnostic run reports (`*.collection-run-summary.yaml`, `*.collection-run-executions.yaml`) and sanitized transcripts (`postman-cli-run-*.log`, with cookie/CSRF/Authorization values redacted) are written to `docs/postman-evidence/`.
