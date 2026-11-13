# Proposed Monorepo Structure

Directory layout for the Clinical AI Extraction Platform (`d:\AI Extraction`):

```text
d:\AI Extraction\
├── .github/                     # CI/CD workflows (GitHub Actions)
│   └── workflows/
│       ├── backend.yml          # .NET build, test, and SAST
│       ├── worker.yml           # Python lint (ruff), typecheck (mypy), pytest
│       ├── frontend.yml         # Angular build, unit tests (vitest), lint
│       └── contracts.yml        # Schema validation & contract testing
├── contracts/                   # AUTHORITATIVE contracts (single source of truth)
│   ├── enums.v1.json            # Authoritative shared domain enumerations
│   ├── events/
│   │   └── note-version-committed.v1.schema.json  # Service Bus event schema
│   ├── extraction/
│   │   └── llm-extraction-output.v1.schema.json   # Foundry structured output schema
│   └── openapi/
│       ├── platform-api.v1.yaml                   # Platform Review & Ops API spec
│       └── emr-integration.v1.yaml                # Assumed hospital EMR contract
├── db/                          # Database schema and migrations
│   ├── platform/
│   │   └── migrations/          # CX database migrations (DbUp PostgreSQL)
│   │       └── V0001__baseline_platform.sql
│   └── emr-simulator/
│       └── migrations/          # Simulated EMR database migrations
│           └── V0001__baseline_emr_simulator.sql
├── docs/                        # Complete technical specifications & ADRs
│   ├── 00-index.md              # Documentation index and governance
│   ├── versions.md              # Verified dependency versions & local toolchain findings
│   ├── repository-structure.md  # Repository layout guide (this file)
│   ├── architecture/            # Architecture specifications & diagrams
│   ├── requirements/            # Functional & non-functional requirements
│   ├── database/                # Database design, ERD, and capacity models
│   ├── api/                     # API specification and standards
│   ├── adr/                     # Architecture Decision Records (ADR 0001 - 0010)
│   ├── security/                # STRIDE threat model and LLM security
│   ├── integration/             # Open questions, assumptions & hospital boundaries
│   ├── plan/                    # Milestone roadmap and risk register
│   └── traceability/            # Requirements Traceability Matrix (RTM)
├── infra/                       # Cloud infrastructure & deployment assets
│   ├── docker/
│   │   ├── docker-compose.yml   # Multi-service local development stack
│   │   └── Dockerfile.*         # Container definitions per service
│   ├── bicep/                   # Azure Bicep templates for ACA, Postgres, KeyVault
│   └── k6/                      # Performance & load testing scripts
├── src/                         # Application source code
│   ├── backend/                 # .NET 10 solution (C#)
│   │   ├── Cx.Platform.sln
│   │   ├── src/
│   │   │   ├── Cx.Domain/       # Core domain entities, enums, business validation
│   │   │   ├── Cx.Infrastructure/ # EF Core, Npgsql, Service Bus client, KeyVault
│   │   │   ├── Cx.PlatformApi/  # ASP.NET Core Web API (Review, Ops, Health endpoints)
│   │   │   ├── Cx.WritebackDispatcher/ # Reliable EMR order dispatcher worker
│   │   │   ├── Cx.OutboxPublisher/     # Outbox publisher hosted service
│   │   │   ├── Cx.DbMigrator/          # Console tool executing DbUp SQL migrations
│   │   │   └── Cx.EmrSimulator/        # Standalone mock EMR (dev/test ONLY)
│   │   └── tests/
│   │       ├── Cx.Domain.Tests/        # Domain rule & enum parity unit tests
│   │       ├── Cx.PlatformApi.Tests/   # Integration & contract tests
│   │       └── Cx.Writeback.Tests/     # Write-back idempotency & failure tests
│   ├── worker/                  # Python 3.13 AI Extraction Worker
│   │   ├── pyproject.toml       # Managed with uv
│   │   ├── uv.lock              # Pinned lockfile
│   │   ├── cx_worker/
│   │   │   ├── config.py        # Pydantic-settings configuration
│   │   │   ├── main.py          # Async queue consumer entrypoint
│   │   │   ├── bus/             # Azure Service Bus peek-lock receiver & dead-letter
│   │   │   ├── emr/             # EMR claim-check client & hash verification
│   │   │   ├── pipeline/
│   │   │   │   ├── preprocess.py # Stage 1: Safe text normalization & sectioning
│   │   │   │   ├── extract.py    # Stage 2: Foundry client & structured outputs
│   │   │   │   ├── validate.py   # Stage 3: Pydantic & clinical rule validation
│   │   │   │   ├── normalize.py  # Stage 4: Multi-tier terminology matching
│   │   │   │   └── persist.py    # Stage 5: Scoring, fencing token & DB persistence
│   │   │   ├── backfill/        # Checkpointed batch orchestrator
│   │   │   └── telemetry.py     # OpenTelemetry setup & PHI log scrubber
│   │   └── tests/
│   │       ├── unit/            # Pipeline stage unit tests
│   │       ├── fixtures/        # Synthetic clinical notes & terminology masters
│   │       └── integration/     # End-to-end worker integration tests
│   └── frontend/                # Angular Frontend (Review Interface)
│       ├── package.json
│       ├── angular.json
│       ├── tsconfig.json
│       ├── src/
│       │   ├── app/
│       │   │   ├── core/        # Auth (MSAL), HTTP interceptors, API clients
│       │   │   ├── features/
│       │   │   │   ├── review-queue/    # Clinician review queue & keyset pagination
│       │   │   │   ├── review-detail/   # Evidence highlighting & inline decision panel
│       │   │   │   └── ops-dashboard/   # SRE run monitoring & dead-letter requeue
│       │   │   └── shared/      # Material components, pipes, accessibility controls
│       │   └── environments/
│       └── tests/               # Vitest unit tests & Playwright E2E specs
```

