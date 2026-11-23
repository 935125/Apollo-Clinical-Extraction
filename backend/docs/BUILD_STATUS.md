# Build status

The active topology is Angular (`frontend/`) → ASP.NET Core (`backend/`) → Microsoft Foundry Chat Completions, with verified local ONNX inference as a sequential fallback → PostgreSQL/pgvector. There is no Python runtime, FastAPI process, or Cx.PlatformApi gateway.

See [Foundry integration and verification](foundry-integration.md) for provider setup, execution receipts, migrations and verification evidence.

See [`README.md`](../../README.md) for current commands. This file intentionally contains no historical pass counts; verification results should come from the current CI run or the implementation handoff.
