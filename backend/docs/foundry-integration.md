# Foundry clinical extraction handoff

The development resource/deployment supplied in this session is configured server-side in `backend/appsettings.Development.json`. The selected deployment and target model are **gpt-6.1-sol**, superseding the initial gpt-6-sol request. No API key is stored in the checkout.

## Request path

`frontend/src/app/services/extraction.service.ts` sends pasted notes to `POST /api/v1/extractions` and multipart PDFs to `POST /api/v1/extractions/pdf`. `backend/Program.cs` validates input and PDFs. PdfPig obtains embedded text before either route invokes `Clinical/ExtractionWorkflow.cs` with the exact displayed source.

The shared workflow checks size, cancellation and the existing privacy validator before any cloud call. `FoundryClinicalExtractor` uses a reusable DI-managed HTTP client for Azure OpenAI v1 Chat Completions. The request is non-streaming, sends the configured Azure **deployment name** in `model`, and supplies a strict JSON Schema generated from the clinical candidate contracts. Every nullable property is required and must be null when undocumented. The note is a user message explicitly treated as source data by the system instruction.

`ClinicalResultValidator` verifies exact quotes and their explicitly selected occurrence, calculates UTF-16 offsets, scopes drug actions/attributes, and reuses the posology binder, terminology resolver, validation findings, clinical safety engine and embedding generator. It accepts a valid empty extraction. Unknown terminology remains visible and is quarantined. The whole note is sent; input over 65,536 characters is rejected, and a truncated/context-limited Azure response invokes local processing rather than accepting a partial answer.

An eligible Azure failure invokes `OnnxClinicalExtractor` once, sequentially. It lazily resolves the existing pipeline, requires loaded model weights **and** an artifact-backed tokenizer, and checks a request-local receipt incremented only after an actual ONNX `InferenceSession.Run`. A rule-only result cannot pass as ONNX fallback. The existing sliding windows cover the complete note. Explicit allergy recovery and scoped posology preserve the supplied metformin/amlodipine/CBC/chest X-ray/Penicillin example. FHIR exports represent allergies as `AllergyIntolerance` rather than active prescriptions.

Caller cancellation (499), input errors (400), privacy/policy rejection (422), and Azure access denial (403) do not invoke fallback. Azure 401/missing credentials/deployment faults are logged as operator faults and may use the explicitly enabled local fallback. Timeouts, unreachable endpoints, 429/5xx, invalid/truncated JSON and unsupported evidence have distinct fallback codes. Failure of both providers returns `EXTRACTION_UNAVAILABLE` (503).

One workflow ID is saved in one repository call. The SQL transaction contains the parent, medications, investigations and quarantine rows. Any insert failure rolls back the transaction and returns `PERSISTENCE_FAILED`; persistence errors cannot retry a model. Diagnostic logs exclude source text, model response bodies, credentials and exception bodies containing those values.

## Windows setup and run

Run from the repository root. Install Azure CLI if `Get-Command az` cannot find it:

```powershell
Set-Location 'D:\AI Extraction'
winget install --exact --id Microsoft.AzureCLI
```

Close and reopen the terminal after installation, then sign in interactively. Do not paste access tokens into configuration or chat:

```powershell
Set-Location 'D:\AI Extraction'
az login
az account show --query state --output tsv

# Optional overrides; replace placeholders with your resource and deployment.
$env:Foundry__Endpoint = 'https://<resource>.services.ai.azure.com/openai/v1/'
$env:Foundry__Deployment = '<your-gpt-6.1-sol-deployment>'
$env:Foundry__TargetModel = 'gpt-6.1-sol'
$env:Foundry__Authentication = 'Entra'
$env:Foundry__TimeoutSeconds = '45'
$env:Foundry__AllowLocalFallback = 'true'
```

For this checkout, omit those optional overrides to use the supplied development settings. The signed-in identity needs access to the Azure OpenAI resource (for example, the `Cognitive Services OpenAI User` role). Development uses `AzureCliCredential`; other environments use `DefaultAzureCredential`, including managed identity. A user-assigned identity can be selected through `Foundry__ManagedIdentityClientId`.

The portable PostgreSQL/pgvector instance in this workspace was provisioned during the preceding connection repair. Start it only if stopped:

```powershell
$pgCtl = Join-Path $PWD 'backend\tools\pgsql\bin\pg_ctl.exe'
$pgData = Join-Path $PWD 'backend\tools\data_cx'
$pgLog = Join-Path $PWD 'backend\logs\postgres-local.log'
& $pgCtl -D $pgData status
if ($LASTEXITCODE -ne 0) {
    & $pgCtl -D $pgData -l $pgLog -o '-p 5432 -h 127.0.0.1' start
}

# Additive upgrade of an existing application database; run as its owner.
& '.\backend\tools\pgsql\bin\psql.exe' -h 127.0.0.1 -p 5432 -U cx_admin -d cx_platform -v ON_ERROR_STOP=1 -f '.\backend\db\platform\migrations\V0006__provider_provenance.sql'

dotnet restore AIExtraction.slnx
dotnet run --project backend/Apollo.Backend.csproj
```

In a second terminal:

```powershell
Set-Location 'D:\AI Extraction\frontend'
# Run npm ci only if dependencies are missing, with ng serve stopped first.
npm start
```

Open `http://localhost:4200`. The Angular proxy targets ASP.NET Core at port 5000. A restart of the backend is needed after changing its environment variables or signing in from a terminal whose PATH has changed.

For a new database, use the existing PostgreSQL/pgvector Compose setup and the existing ordered migrations. The default development repository also initializes/upgrades its schema idempotently. Production deployments can apply migrations separately and set `Persistence__AutoMigrate=false`.

API-key development is optional; use User Secrets rather than checked-in settings:

```powershell
dotnet user-secrets set 'Foundry:Authentication' 'ApiKey' --project backend/Apollo.Backend.csproj
dotnet user-secrets set 'Foundry:ApiKey' '<API-KEY>' --project backend/Apollo.Backend.csproj
# Switch back to the requested Entra flow when finished.
dotnet user-secrets remove 'Foundry:ApiKey' --project backend/Apollo.Backend.csproj
dotnet user-secrets remove 'Foundry:Authentication' --project backend/Apollo.Backend.csproj
```

## Provider evidence and history

`modelMetadata.provider` identifies the accepted provider. `model` is Azure's returned model identifier or the verified local model label; `azureDeployment` identifies the successful Azure deployment. `fallbackReason` labels recovery. Each `attempts` entry records provider, deployment, outcome, reason, measured duration and whether inference was confirmed. ONNX receipts include the number of executed windows. Actual Azure usage belongs to its attempt, including rejected answers. ONNX token usage and unknown costs are null and displayed as N/A.

The additive migration adds `extraction_runs.provider_metadata` (JSONB), and `extracted_medications.dose_unit` and `action`. Existing columns and IDs remain intact. Historical runs with null provider metadata retain their original values and display “Provider not recorded”; historical ONNX token constants are not shown as measured usage.

The read-only query in `backend/db/platform/verify_extraction_providers.sql` displays provider, fallback reason, attempts and child counts without printing patient notes:

```powershell
& '.\backend\tools\pgsql\bin\psql.exe' -h 127.0.0.1 -p 5432 -U cx_admin -d cx_platform -v ON_ERROR_STOP=1 -f '.\backend\db\platform\verify_extraction_providers.sql'
```

## Checks and their limits

```powershell
Set-Location 'D:\AI Extraction'
dotnet build AIExtraction.slnx --configuration Release
# Use a test login that can create temporary schemas. Tests create and drop
# only their own foundry_test_<random UUID> schema, preserving application rows.
$env:TEST_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=cx_platform;Username=postgres'
dotnet test backend/tests/Apollo.Backend.Tests.csproj --configuration Release --logger 'console;verbosity=minimal'
Remove-Item Env:TEST_POSTGRES_CONNECTION
npm --prefix frontend test -- --watch=false
npm --prefix frontend run build
```

`FoundryWorkflowTests.cs` simulates Azure **at the outgoing HTTP boundary**. Primary success through both routes asserts zero local extractor calls. HTTP outages, rate limits, timeout, invalid evidence/JSON and truncation verify one local attempt with **real ONNX weights** and a real inference receipt. Other cases cover empty findings, privacy and policy refusal, access denial, caller cancellation, missing weights/tokenizer, medication scoping, repeated evidence, PDF line breaks and failed persistence.

`FoundryPostgresTests.cs` uses real PostgreSQL/pgvector in isolated schemas. Primary Azure responses are simulated; fallback executes real ONNX. It verifies one parent per request, matching child/quarantine counts, metadata/history restoration, legacy metadata and rollback when quarantine insertion fails. These tests are explicitly skipped unless `TEST_POSTGRES_CONNECTION` is supplied.

The current execution environment has the real ONNX artifacts and a working local PostgreSQL/pgvector database, but Azure CLI is not installed/on PATH. Consequently, **a synthetic note has not yet been verified against the real Foundry deployment**. The live integration can run and persist a clearly labeled ONNX fallback with `AZURE_CREDENTIALS_MISSING`; installing Azure CLI, signing in and rerunning the synthetic request are the remaining cloud verification steps. A fallback is not evidence of GPT execution.

The final build/test outcomes and saved execution receipts are recorded in `backend/artifacts/foundry-verification/verification.json` and the associated TRX file. Live browser evidence is in `frontend/verification/foundry-live-report.json`.

## Changed files

- Backend configuration and routes: `Apollo.Backend.csproj`, `appsettings.json`, `appsettings.Development.json`, `Program.cs`.
- Providers and workflow: `Clinical/ClinicalProviders.cs`, `FoundryClinicalExtractor.cs`, `OnnxClinicalExtractor.cs`, `ClinicalResultValidator.cs`, `ExtractionWorkflow.cs`, `ClinicalPipelineService.cs`, `PosologyBinder.cs`.
- Model evidence: `Nlp/WordPieceTokenizer.cs`, `Nlp/SlidingWindowOnnxEngine.cs`.
- Contracts/persistence: `Models/Contracts.cs`, `Storage/IExtractionRepository.cs`, `SqlExtractionRepository.cs`, `InMemoryExtractionRepository.cs`, `Fhir/FhirR4Mapper.cs`.
- Database: `db/platform/migrations/V0006__provider_provenance.sql`, `db/platform/verify_extraction_providers.sql`.
- Tests: `tests/FoundryWorkflowTests.cs`, `tests/FoundryPostgresTests.cs`, `tests/isolated-postgres-schema.sql`.
- Frontend: `src/app/models/extraction.model.ts`, `app.ts`, `app.html`, `app.scss`, `app.spec.ts`; live provider verification script/report under `verification/`.
- Handoff: root `README.md`, `backend/docs/BUILD_STATUS.md`, this document.

## Protocol references

- [Microsoft: structured outputs](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/structured-outputs)
- [Microsoft: Azure deployment names and authentication](https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/switching-endpoints)
- [Microsoft: install Azure CLI on Windows](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli-windows)
