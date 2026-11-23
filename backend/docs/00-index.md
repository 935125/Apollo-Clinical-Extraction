# Clinical AI Extraction Platform — Documentation Index

Status: **Milestone 0 (Architecture & Feasibility) — DRAFT, awaiting architecture review**
Last updated: 2026-11-18

> This directory is primarily a historical design archive. The current two-application build, startup, and layout instructions are in the root [`README.md`](../../README.md) and [`repository-structure.md`](repository-structure.md). References to the retired Python worker, Cx.PlatformApi, EMR simulator, outbox, or Service Bus are not runtime instructions.

## Sources of truth (authoritative artifacts)

| Concern | Authoritative artifact | Rule |
|---|---|---|
| Platform HTTP API | [`contracts/openapi/platform-api.v1.yaml`](../contracts/openapi/platform-api.v1.yaml) | Code must conform; contract tests validate responses against it. |
| EMR simulator / assumed EMR integration API | [`contracts/openapi/emr-integration.v1.yaml`](../contracts/openapi/emr-integration.v1.yaml) | **Assumed** contract. Replace when the hospital supplies the real one. |
| Note-committed event | [`contracts/events/note-version-committed.v1.schema.json`](../contracts/events/note-version-committed.v1.schema.json) | Producer (outbox publisher) and consumer (worker) both validate. |
| LLM structured output | [`contracts/extraction/llm-extraction-output.v1.schema.json`](../contracts/extraction/llm-extraction-output.v1.schema.json) | Pydantic models are tested to generate an equivalent schema. |
| Shared enumerations | [`contracts/enums.v1.json`](../contracts/enums.v1.json) | DB CHECK constraints, C#, Python, TS enums are tested against it. |
| Database schema | [`db/migrations/`](../db/migrations/) | Versioned SQL; nothing else may create schema objects. |

Prose documents below *describe* these artifacts; if prose and an artifact disagree, the artifact wins and the prose is a defect.

## Documents

| # | Document | Purpose |
|---|---|---|
| A | [architecture/architecture-spec.md](architecture/architecture-spec.md) | Context, component, sequence, data-flow, deployment, trust boundaries, failure/recovery |
| B | [requirements/requirements-spec.md](requirements/requirements-spec.md) | FR / NFR / clinical safety / security / availability / out-of-scope |
| C | [database/database-design.md](database/database-design.md) | ERD, tables, keys, indexes, concurrency, retention, partitioning |
| C2 | [database/capacity-model.md](database/capacity-model.md) | Parametric volume, storage, throughput and token model |
| D | [api/api-spec.md](api/api-spec.md) | API conventions: errors, auth, pagination, idempotency, versioning |
| E | [adr/](adr/) | Architecture Decision Records |
| F | [traceability/rtm.md](traceability/rtm.md) | Requirements traceability matrix |
| G | [security/threat-model.md](security/threat-model.md) | STRIDE + LLM-specific threat model |
| H | [integration/open-questions.md](integration/open-questions.md) | Unknown hospital integration facts vs. safe dev assumptions |
| I | [plan/milestone-plan.md](plan/milestone-plan.md) | Milestones, acceptance criteria, verification plan |
| J | [plan/risk-register.md](plan/risk-register.md) | Architectural, clinical, security, scalability risks |
| K | [versions.md](versions.md) | Verified technology versions and environment findings |
| L | [repository-structure.md](repository-structure.md) | Proposed monorepo layout |

