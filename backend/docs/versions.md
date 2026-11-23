# Technology Versions & Environment Findings

Verified on 2026-11-13 against live package registries (nuget.org, npmjs.com, PyPI) and vendor documentation searches.
Exact transitive versions will be frozen by lockfiles (`packages.lock.json`, `package-lock.json`, `uv.lock`) in Milestone 1.

## 1. Local development machine (observed, not assumed)

| Tool | Observed | Required | Status |
|---|---|---|---|
| .NET SDK | 10.0.403 (also 9.0.315) | 10.0.x | OK |
| Node.js | **24.12.0** | Angular 22 requires `^22.22.3 \|\| ^24.15.0 \|\| >=26` | **BLOCKER for Angular 22** (see §3) |
| npm | bundled with Node 24.12 | — | OK |
| Python | 3.15.1, 3.10.22 installed; 3.12.15 / 3.13.17 downloadable via `uv` | 3.12 or verified compatible | Needs `uv python install` (approval requested) |
| uv | 0.13.5 | — | OK |
| Git | 2.56.0 | — | OK |
| Docker / Docker Compose | **not installed** | Required for Compose, Testcontainers, Service Bus emulator | **BLOCKER for integration tests** |
| WSL | **not installed** | Docker Desktop backend | Blocker (admin install) |
| PostgreSQL / psql | **not installed** | 18.x | Blocked unless Docker or native install approved |
| Azure CLI, Terraform/Bicep, k6 | not installed | Needed from M8/M9 | Not needed for M1 |

## 2. Selected versions

| Layer | Choice | Evidence / reason |
|---|---|---|
| Backend runtime | **.NET 10 (LTS)**, SDK 10.0.403 | .NET 11 (STS) GA 2026-11-10 and both reach EOS 2028-11-14, but **Npgsql.EntityFrameworkCore.PostgreSQL 11 is only RC (11.0.0-rc.3)**; stable provider is 10.0.3. See ADR-0003. |
| EF Core | 10.0.x (match provider) | Provider 10.0.3 targets EF Core 10. |
| Npgsql | 10.0.3 | Stable on nuget.org. |
| ASP.NET Core OpenAPI | Microsoft.AspNetCore.OpenApi 10.0.x | Built-in OpenAPI doc generation; Swashbuckle not needed. |
| Service Bus SDK (.NET) | Azure.Messaging.ServiceBus 7.21.0 | Latest stable. |
| Migrations | dbup-postgresql 7.0.1 running plain SQL files | Language-neutral schema (ADR-0005). |
| Auth | Microsoft.Identity.Web 4.18.0 | Entra ID JWT validation. |
| Telemetry (.NET) | OpenTelemetry.Extensions.Hosting 1.21.0; Azure.Monitor.OpenTelemetry.AspNetCore 1.6.0 | Latest stable. |
| Tests (.NET) | xunit.v3 4.0.3, Microsoft.NET.Test.Sdk 18.12.0, Testcontainers.PostgreSql 4.16.0, Microsoft.AspNetCore.Mvc.Testing 10.0.x, Shouldly 4.3.0 | **FluentAssertions 8.x rejected** — commercial licence since v8. |
| Frontend | **Angular 22.2.x + Angular Material 22.2.x + TypeScript (per Angular peer range)** if Node ≥ 24.15 is approved; otherwise **Angular 21.2.x LTS** | See §3 and ADR-0004. Vitest is the CLI default unit runner. |
| E2E | @playwright/test 1.65.0 | Latest stable. |
| AI worker runtime | **Python 3.13** (fallback 3.12) | 3.12 is security-fix-only; 3.13 has mature wheel coverage. Confirmed by `uv lock` in M1 (ADR-0006). |
| Pydantic | 2.14.x | PyPI latest observed 2.14.3. |
| Foundry access | `openai` Python SDK against Foundry `/openai/v1/` endpoint, Entra ID token via `azure-identity` | Microsoft-documented pattern for structured outputs. Adapter-isolated (ADR-0008). |
| Service Bus SDK (Python) | `azure-servicebus` latest 7.x | Resolved by `uv lock`. |
| Database | **PostgreSQL 18** | Native `uuidv7()`; supported on Azure Database for PostgreSQL Flexible Server; pgvector 0.8.x available there if later justified. |
| Local messaging | Azure Service Bus **emulator** container (requires Docker) | Same SDK/protocol as production. |

## 3. Open environment decisions (need approval)

1. **Node.js**: upgrade to Node 24 LTS ≥ 24.15 (same major, enables Angular 22) — *recommended* — or stay on Node 24.12 and use Angular 21 LTS.
2. **Docker Desktop (+ WSL2)**: required for PostgreSQL, Service Bus emulator, Testcontainers and Compose verification. Without it, Milestone 1 can only verify builds, unit tests and lint — not database or messaging integration.
3. **Python**: `uv python install 3.13` (user-level download, no admin).

