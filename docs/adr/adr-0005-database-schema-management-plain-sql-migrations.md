# ADR-0005: Database Schema Management via Plain SQL Migrations (DbUp)

## Context
The platform is polyglot: ASP.NET Core services handle APIs and write-backs, Python handles extraction pipeline workers, and DB administrators monitor performance. Using ORM-managed migrations (such as EF Core Migrations) couples database evolution to C# classes, generates complex C# migration files, and impairs cross-language schema sharing and advanced PostgreSQL feature usage (e.g. `uuidv7()`, generated tsvector columns, CHECK constraints with JSON operators, range partitions).

## Decision
1. Schema is authored strictly as versioned, plain SQL scripts (`db/platform/migrations/V####__name.sql`).
2. Schema execution is performed by an isolated utility `Cx.DbMigrator` using **DbUp-PostgreSQL (v7.0.1)**.
3. EF Core in the ASP.NET Core API and psycopg3 in Python are configured to map to the established schema; neither ORM has permission to alter DDL.
4. Enumerations are defined once in `contracts/enums.v1.json` and mirrored in PostgreSQL as `text` columns with `CHECK (col IN (...))` constraints, verified via automated schema contract tests.

## Consequences
- **Positive**: Complete DBA transparency; language-agnostic; native support for Postgres 18 features and partitioning; zero ORM migration drift across C# and Python.
- **Negative**: Developers must write explicit SQL for migrations rather than relying on automated C# model diffing.

