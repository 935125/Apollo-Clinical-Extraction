# ADR-0006: Python Runtime & Dependency Management via `uv`

## Context
The workstation contains `uv 0.13.5`, Python 3.10.22, and Python 3.15.1. The master build directive requires Python 3.12 or a verified compatible supported version.
Python 3.12 is in maintenance/security phase; Python 3.13 is fully supported with mature wheel availability for scientific and cloud libraries. Python 3.15 is too bleeding-edge (unsupported by multiple production binary wheels).

## Decision
1. Standardize the AI worker on **Python 3.13** (managed seamlessly via `uv python install 3.13`).
2. Utilize `uv` as the single fast tool for virtual environment creation, package resolution, and lockfile generation (`uv.lock`).
3. Core dependencies pinned and locked:
   - `pydantic >= 2.14.0` (strict schema validation).
   - `openai >= 3.50.0` (Microsoft Foundry structured outputs via `/openai/v1/` endpoint).
   - `azure-servicebus >= 7.15.0` (asynchronous queue consumption).
   - `azure-identity >= 1.27.0` (Entra ID bearer token provider).
   - `psycopg[binary,pool] >= 3.3.9` (native PostgreSQL driver with connection pooling).
   - `rapidfuzz >= 3.14.0` (high-performance C++ based string distance calculation for terminology matching).

## Consequences
- **Positive**: Extremely fast, reproducible virtual environments; deterministic lockfile; complete wheel compatibility.
- **Action**: Issue `uv python install 3.13` as a user-level directory operation during Milestone 1.

