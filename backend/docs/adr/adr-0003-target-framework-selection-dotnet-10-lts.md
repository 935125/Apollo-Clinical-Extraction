# ADR-0003: Target Framework Selection: .NET 10 LTS vs .NET 11 STS

## Context
As of November 2026, .NET 11 has recently been released (November 10, 2026) as a Standard Term Support (STS, 2 years) release. .NET 10 is an active Long Term Support (LTS, 3 years) release. Both reach End of Support (EOS) concurrently on November 14, 2028.
Crucially, verification against NuGet confirms that `Npgsql.EntityFrameworkCore.PostgreSQL` for version 11 is currently in Release Candidate status (`11.0.0-rc.3`), whereas version 10 is fully stable (`10.0.3`).

## Decision
1. Standardize the hospital-facing backend on **.NET 10 (LTS)** targeting `net10.0` with C# 13/14 language features.
2. Standardize database libraries on stable packages: `Npgsql 10.0.3` and `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`.
3. Do not adopt .NET 11 preview/RC data providers in an enterprise healthcare system where database stability is critical.

## Consequences
- **Positive**: High enterprise stability; production-tested Npgsql data access provider; zero risk of pre-release runtime bugs in clinical transactions.
- **Negative**: Must perform a planned framework upgrade before the November 2028 LTS cutoff.

