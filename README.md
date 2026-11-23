# AI Extraction

The repository contains two applications:

- `frontend/` — Angular clinical extraction dashboard.
- `backend/` — ASP.NET Core API, Microsoft Foundry primary extractor with a verified local ONNX fallback, PostgreSQL repository, database migrations (`backend/db/platform/migrations`), local Docker setup (`backend/infra/docker`), docs (`backend/docs`), contracts (`backend/contracts`), and .NET tests.

Python is not part of the runtime or build. Pasted notes and text-based PDFs both use `ExtractionWorkflow`, and a response is marked `saved: true` only after PostgreSQL accepts the record. Image-only PDFs return `422 OCR_REQUIRED` because OCR is not bundled.

See [Foundry configuration, migration, verification and handoff](backend/docs/foundry-integration.md). The configured development deployment is `gpt-6.1-sol`. Local development uses Azure CLI sign-in; Azure uses the server's Entra/managed identity credentials. Keys are supported through User Secrets or environment variables only.

## Start locally

Prerequisites: .NET 10 SDK, Node.js 24+, npm, and PostgreSQL with the `vector` extension. The supplied Compose file starts the database:

```powershell
docker compose -f backend/infra/docker/docker-compose.yml up -d
dotnet run --project backend/Apollo.Backend.csproj
```

In a second terminal:

```powershell
cd frontend
npm ci
npm start
```

Open `http://localhost:4200`. Angular proxies `/api`, `/v1`, `/predict`, and `/health` to the API at `http://127.0.0.1:5000`. A production Angular build is served by the API when present under `frontend/dist/cx-review-ui/browser`.

Override the development database with `ConnectionStrings__Postgres`. Run migrations in `backend/db/platform/migrations` in version order when upgrading an existing database; `V0005` adds source metadata without replacing existing extraction rows.

## Verify

```powershell
dotnet restore AIExtraction.slnx
dotnet build AIExtraction.slnx --configuration Release
dotnet test backend/tests/Apollo.Backend.Tests.csproj --configuration Release
npm --prefix frontend ci
npm --prefix frontend test -- --watch=false
npm --prefix frontend run build
```

The API retains the legacy `/v1/ner/predict` endpoint and PDF aliases for compatibility. The dashboard uses `/api/v1/extractions` and `/api/v1/extractions/pdf`.

## Documentation

- [Repository structure](backend/docs/repository-structure.md)
- [Build status](backend/docs/BUILD_STATUS.md)
- [Historical architecture & ADR index](backend/docs/00-index.md)
