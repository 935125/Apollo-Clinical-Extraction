# Apollo Clinical Extraction Platform — Stage 12 Functional Verification Report

**Date:** 24 November 2026  
**Environment:** Windows local development (`PostgreSQL 127.0.0.1:5432`, `ASP.NET Core .NET 10 :5000`, `Angular 21 :4200`, `Azure Service Bus op-clinical-notes`).

---

## 1. Automated Verification Summary

| Verification Suite | Command / Target | Result | Evidence |
| --- | --- | --- | --- |
| Backend Release Build | `dotnet build backend/Apollo.Backend.csproj -c Release` | **PASS** | `0 warnings, 0 errors` |
| Backend Automated Suite (excluding live SB smoke) | `dotnet test backend/tests/Apollo.Backend.Tests.csproj --filter "FullyQualifiedName!~Stage9RealServiceBusSmokeTests"` | **PASS** | `213 passed, 0 failed, 11 intentional live/benchmark skipped` |
| Stage 12 Backend Queue & Defect Tests | `Stage12QueueTests` (3 tests: auth/isolation/polling, dev-session loopback, deduplication + dose + CBC LOINC) | **PASS** | `3/3 passed` |
| Real Azure Service Bus Smoke Test | `Stage9RealServiceBusSmokeTests` | **PASS** | `1/1 passed (22 s)` |
| Frontend Unit Tests (Vitest) | `npm test -- --watch=false` (`app.spec.ts`, `emr.spec.ts`, `clinical-queue.spec.ts`) | **PASS** | `53/53 passed (3/3 files)` |
| Frontend Production Build | `npm run build` | **PASS** | `0 warnings, 0 errors` (`82.81 kB` gzipped initial bundle) |
| Live Browser E2E (Playwright Chromium) | `node frontend/e2e/stage12-queue-e2e.mjs` | **PASS** | `10/10 checks passed, 0 console errors, 0 unexpected 4xx/5xx` |
| Live Billable Microsoft Foundry Calls | Explicitly disabled (`LocalOnnx` `authorized_offline`) | **NOT TESTED (Zero-Cost Policy)** | `0 billable API calls`; offline Foundry contract tests + real historical Foundry record verified |

---

## 2. Playwright E2E Checklist Results (`docs/stage12-evidence/stage12-e2e-results.json`)

1. **Direct Load & Minimal Queue Overview:** `PASS` — Opened `http://localhost:4200/` directly with zero visible login wall, zero KPI cards, zero 5-tab bar, zero left sidebar, zero review buttons; verified exact 4 columns (`QUEUE ENTRY | CLINICAL NOTE | RECEIVED | ACTION`).
2. **Legacy Route Redirects:** `PASS` — `/login`, `/emr`, `/review`, `/admin` all cleanly redirected to `/`.
3. **Live Note Submission (`STAGE12-LIVE-398057`):** `PASS` — Submitted through authorized EMR API → PostgreSQL transactional outbox → Azure Service Bus `op-clinical-notes`.
4. **Live Automatic RxJS Polling:** `PASS` — Observed queue transition to `Ready` automatically without manual refresh (`Polls observed: 5 -> 7`).
5. **Side-by-Side Source Note & Extracted Results:** `PASS` — Complete immutable source text displayed in Apollo deep green frame; clicking medication row highlighted exact verbatim evidence quote (`<mark>`); separate `Strength` and `Dose` columns (`500 mg` / `half tablet`); LOINC `58410-2` rendered.
6. **Local ONNX Disclosure & Deduplicated Warnings:** `PASS` — `Local ONNX · bc5cdr_int8`, `N/A (Local ONNX)` tokens, limited-coverage disclosure visible; expanded diagnostics `BadgeCount=5, RenderedItems=5` (zero duplicates).
7. **Responsive Layout (`768x1024`):** `PASS` — Source note stacks cleanly above extracted results; `HorizontalOverflow=0px`.
8. **Real Historical Foundry Run Provenance (`6430f85d-41f5-4dc8-86be-aa208dc34b9d`):** `PASS` — Rendered actual persisted `gpt-6.1-sol-2026-09-29` (`1,274 tokens`, `6.43 s`) provenance cleanly with zero billable API calls.
9. **Browser Reload Persistence:** `PASS` — Reloading preserved `STAGE12-LIVE-398057` in PostgreSQL `cx_platform`.
10. **Runtime Cleanliness:** `PASS` — Zero console errors, zero uncaught page errors, zero unexpected 4xx/5xx API responses.

---

## 3. Captured Stage 12 Screenshot Evidence

All 7 screenshots are stored under [`docs/stage12-evidence/screenshots/`](stage12-evidence/screenshots/) and were visually audited:

| Screenshot | What It Verifies |
| --- | --- |
| [`01-queue-overview.png`](stage12-evidence/screenshots/01-queue-overview.png) | Minimal landing queue at `http://localhost:4200/` without login, KPIs, sidebar, tabs, or review UI |
| [`02-queue-processing.png`](stage12-evidence/screenshots/02-queue-processing.png) | Newly submitted synthetic note showing `Waiting` alongside an `Extracting` entry in real time |
| [`03-queue-completed.png`](stage12-evidence/screenshots/03-queue-completed.png) | Same queue automatically updated via live polling to `Ready · Verification recommended` |
| [`04-side-by-side-source-results.png`](stage12-evidence/screenshots/04-side-by-side-source-results.png) | Side-by-side Apollo deep green Source Note (with highlighted evidence) and white Extracted Results |
| [`05-foundry-provenance.png`](stage12-evidence/screenshots/05-foundry-provenance.png) | Persisted historical Microsoft Foundry (`gpt-6.1-sol-2026-09-29`) provenance bar (`6.43 s`, `1,274 tokens`) |
| [`06-onnx-fallback-display.png`](stage12-evidence/screenshots/06-onnx-fallback-display.png) | Candidate LOINC `58410-2 (narrower — withheld)` + expanded deduplicated diagnostics (`5` badge = `5` cards) |
| [`07-responsive-layout.png`](stage12-evidence/screenshots/07-responsive-layout.png) | Stacked `768x1024` responsive layout with zero horizontal overflow |

---

## 4. PostgreSQL Historical Data Preservation

Verified read-only on `cx_platform` after Stage 12 completion:
- `extraction_runs`: **56** (strictly additive; historical 52+ runs intact)
- `extracted_medications`: **128**
- `extracted_investigations`: **156**
- `entity_review_audit`: **12** (100% untouched)
- `emr_notes`: **4**
- `emr_note_versions`: **33**
