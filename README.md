# Apollo Clinical Extraction Platform

**Source-grounded clinical information extraction, terminology normalization, and human review.**

Apollo transforms unstructured clinical notes and text-based PDFs into structured medication and investigation records. It combines a **Microsoft Foundry primary extractor** with a **local ONNX fallback**, deterministic clinical validation, **RxNorm/LOINC terminology mapping**, and an auditable review experience.

**Angular 21 · ASP.NET Core / .NET 10 · Microsoft Foundry · ONNX · PostgreSQL / pgvector**

**Quick links:** [Live UI gallery](#frontend-screenshots) · [One-click Windows startup](#option-a-one-click-windows-launcher) · [Frontend walkthrough](#frontend-walkthrough) · [Architecture](#architecture) · [API reference](#api-overview) · [Testing](#verify-the-application)

> [!IMPORTANT]
> **Status: engineering demonstration / development candidate, not hospital-production ready.** Independent clinical validation, institutional authentication and access controls, security governance, and deployment approvals remain outstanding. Use synthetic data unless the institution explicitly authorizes real clinical data processing.

## Contents

- [What the platform does](#what-the-platform-does)
- [Frontend walkthrough](#frontend-walkthrough)
- [Frontend screenshots](#frontend-screenshots)
- [Architecture](#architecture)
- [Extraction lifecycle](#extraction-lifecycle)
- [Clinical validation and terminology](#clinical-validation-and-terminology)
- [Human review and audit trail](#human-review-and-audit-trail)
- [Technology stack](#technology-stack)
- [Repository layout](#repository-layout)
- [Run locally](#run-locally)
  - [Option A — one-click Windows launcher](#option-a-one-click-windows-launcher)
  - [Option B — manual setup](#option-b-manual-setup)
- [API overview](#api-overview)
- [Verify the application](#verify-the-application)
- [Security and limitations](#security-and-limitations)
- [Documentation](#documentation)

## What the platform does

| Capability | Description |
| --- | --- |
| **Clinical input** | Accepts pasted notes or text-based PDF documents through the same backend `ExtractionWorkflow`. |
| **Hybrid extraction** | Uses the configured Microsoft Foundry model as primary provider and a local BC5CDR INT8 ONNX pipeline as fallback. |
| **Source evidence** | Preserves source quotations and offsets for extracted items wherever supported. |
| **Clinical interpretation** | Separates planned orders, historical measurements, allergies, and relevant medication instructions. |
| **Terminology** | Attempts RxNorm medication and LOINC investigation mapping while preserving ambiguity and unresolved matches. |
| **Validation and review** | Applies source-grounded clinical checks and supports entity-level human review without overwriting original AI output. |
| **Persistence** | Saves validated extraction records to PostgreSQL; `saved: true` is returned only after a successful database write. |
| **Interoperability** | Provides versioned extraction APIs, compatibility endpoints, and FHIR-related export capabilities. |

> **PDF boundary:** Scanned or image-only PDFs are **not** OCR-processed. Such files return `422 OCR_REQUIRED`; OCR is not bundled.

## Frontend walkthrough

The Angular application is designed as a **single clinical extraction and review workflow**. The steps below describe the user journey—not a claim that each item is a separate route or navigation tab in the current UI.

```mermaid
flowchart LR
    START["Open dashboard"] --> INPUT["Paste clinical note<br/>or upload text PDF"]
    INPUT --> SUBMIT["Submit for extraction"]
    SUBMIT --> OUTPUT["Review extracted entities"]
    OUTPUT --> MED["Medications + allergies"]
    OUTPUT --> INVEST["Planned investigations<br/>+ historical results"]
    MED --> EVIDENCE["Inspect evidence,<br/>normalization + warnings"]
    INVEST --> EVIDENCE
    EVIDENCE --> DECIDE["Accept / correct / reject<br/>or request clarification"]
    DECIDE --> HISTORY["Versioned review history"]
    HISTORY -.->|"Reload saved result"| OUTPUT

    classDef entry fill:#e0f2fe,stroke:#0284c7,color:#0c4a6e
    classDef result fill:#eef2ff,stroke:#6366f1,color:#312e81
    classDef review fill:#dcfce7,stroke:#16a34a,color:#14532d
    class START,INPUT,SUBMIT entry
    class OUTPUT,MED,INVEST,EVIDENCE result
    class DECIDE,HISTORY review
```

| Where to go | What to do | What to check |
| --- | --- | --- |
| **Clinical input** | Paste a note or choose a text-based PDF and submit it. | Confirm source text and processing feedback. |
| **Extraction results** | Inspect the structured output returned by the API. | Distinguish active medications from allergies, and future investigations from historical results. |
| **Evidence and terminology** | Open the available evidence and normalization details. | Check source quotations, RxNorm/LOINC mapping status, unresolved codes, and rule/model provenance. |
| **Safety and uncertainty** | Review flagged interactions, measurements, missing details, and warnings. | Treat alerts as review aids rather than automated medical decisions. |
| **Clinical review** | Accept, correct, reject, or request clarification for individual entities. | Confirm the original extraction is preserved and review decisions are recorded separately. |
| **Saved extraction** | Open a previously persisted result using its extraction ID. | Verify the saved data and review history survive a page refresh. |

For a saved extraction, the development UI supports a query-string deep link:

```text
http://localhost:4200/?extractionId=<your-extraction-uuid>
```

The query string identifies an existing record; it **does not** create a new extraction or bypass API authorization. Actual controls and presentation may vary as the Angular UI evolves.

## Frontend screenshots

Actual screenshots from the Apollo Angular development interface, captured with **synthetic demonstration notes**. Select any image to open it at full resolution. The screenshots show functionality and layout from a development build; they do not represent hospital production approval.

### Clinical extraction in action

<a href="docs/screenshots/02-extraction-results.png">
  <img src="docs/screenshots/02-extraction-results.png" alt="Apollo clinical extraction workspace showing a synthetic respiratory note alongside extracted medications and investigations" width="100%" />
</a>

*Source text remains visible beside the structured results. This example displays the Local ONNX fallback with explicit reduced-coverage disclosure, medication status and investigation summaries.*

### Explore the interface

<table>
  <tr>
    <td width="50%" valign="top">
      <a href="docs/screenshots/01-workspace-input.png"><img src="docs/screenshots/01-workspace-input.png" alt="Empty Apollo workspace with pasted clinical note editor, PDF upload, and Extract action" width="100%" /></a>
      <br/><strong>01 · Clinical input workspace</strong><br/>
      Enter a note, select an example, or upload a text-based PDF.
    </td>
    <td width="50%" valign="top">
      <a href="docs/screenshots/03-pdf-extraction.png"><img src="docs/screenshots/03-pdf-extraction.png" alt="Apollo extraction view displaying a synthetic inpatient discharge PDF alongside structured medication results" width="100%" /></a>
      <br/><strong>02 · PDF extraction</strong><br/>
      Inspect a synthetic discharge summary side by side with extracted data.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <a href="docs/screenshots/04-validation-export.png"><img src="docs/screenshots/04-validation-export.png" alt="Apollo extraction details and validation warnings with JSON, CSV and FHIR export options" width="100%" /></a>
      <br/><strong>03 · Validation and export</strong><br/>
      Examine warnings, extraction metadata and JSON / CSV / FHIR export options.
    </td>
    <td width="50%" valign="top">
      <a href="docs/screenshots/05-extraction-history.png"><img src="docs/screenshots/05-extraction-history.png" alt="Apollo extraction history showing saved synthetic runs, dates, providers and entity counts" width="100%" /></a>
      <br/><strong>04 · Extraction history</strong><br/>
      Search prior runs and identify the provider and extraction timestamp.
    </td>
  </tr>
</table>

### Where to click

| Interface control | What it opens |
| --- | --- |
| **Workspace** | Input, extraction and result review in a single two-column layout. |
| **Upload PDF / Extract** | Submit a text-based document or the pasted clinical note. |
| **Structured** | Medication, allergy and investigation results. |
| **Source** | Source-oriented inspection in the results area. |
| **Details / View details** | Extraction metadata, validation findings and warnings. |
| **Export** | Download JSON, CSV or FHIR output where enabled. |
| **History** | Search and reopen previously saved extraction runs. |
| **Select an extracted entity** | Open entity-level review controls, where available in the current build. |

> **Additional screenshot welcome:** The entity-level clinical review and audit-history panel is implemented but is not shown in the five supplied screenshots. Add a real capture before presenting that panel as part of this gallery.

## Architecture

The platform separates browser presentation, the ASP.NET Core workflow, extraction providers, clinical processing, and persistent storage.

```mermaid
flowchart TB
    subgraph UI["Presentation"]
        USER["Reviewer / developer"]
        ANGULAR["Angular clinical dashboard"]
        USER --> ANGULAR
    end

    subgraph API["ASP.NET Core / .NET 10"]
        ENDPOINT["Extraction API"]
        WORKFLOW["ExtractionWorkflow"]
        VALIDATE["Evidence + clinical validation"]
        NORMALIZE["RxNorm / LOINC resolution"]
        SAFETY["Safety flags + uncertainty"]
        REVIEW["Entity review API + audit trail"]
    end

    subgraph PROVIDERS["Extraction providers"]
        FOUNDRY["Microsoft Foundry - primary"]
        ONNX["BC5CDR INT8 ONNX - fallback"]
        RULES["Source-grounded C# attribute rules"]
    end

    DB[("PostgreSQL + pgvector")]

    ANGULAR --> ENDPOINT --> WORKFLOW
    WORKFLOW -->|"Primary attempt"| FOUNDRY
    WORKFLOW -.->|"Fallback when configured / necessary"| ONNX
    ONNX --> RULES
    FOUNDRY --> VALIDATE
    RULES --> VALIDATE
    VALIDATE --> NORMALIZE --> SAFETY --> DB
    ANGULAR <-->|"Read and submit decisions"| REVIEW
    REVIEW <--> DB
    DB -->|"Saved extraction"| ENDPOINT

    classDef ui fill:#e0f2fe,stroke:#0284c7,color:#0c4a6e
    classDef core fill:#eef2ff,stroke:#6366f1,color:#312e81
    classDef provider fill:#f0fdf4,stroke:#16a34a,color:#14532d
    classDef storage fill:#fef3c7,stroke:#d97706,color:#78350f
    class ANGULAR,USER ui
    class ENDPOINT,WORKFLOW,VALIDATE,NORMALIZE,SAFETY,REVIEW core
    class FOUNDRY,ONNX,RULES provider
    class DB storage
```

**Important distinction:** The BC5CDR model detects medication/condition mentions; medication attributes and investigation extraction in the fallback path rely heavily on **deterministic C# processing**. It is not a fully equivalent replacement for a general-purpose clinical LLM. The provider and rule provenance should remain visible in results.

## Extraction lifecycle

The API does not report an extraction as saved until PostgreSQL confirms persistence.

```mermaid
sequenceDiagram
    autonumber
    actor User as User
    participant UI as Angular
    participant API as ASP.NET Core API
    participant WF as ExtractionWorkflow
    participant Model as Foundry / local fallback
    participant Checks as Validation + terminology
    participant DB as PostgreSQL

    User->>UI: Paste note or upload PDF
    UI->>API: POST extraction request
    API->>WF: Validate input and prepare text
    alt Image-only PDF
        WF-->>API: OCR_REQUIRED
        API-->>UI: HTTP 422
        UI-->>User: Explain OCR requirement
    else Supported clinical text
        WF->>Model: Execute configured extraction provider
        Model-->>WF: Entities + available provenance
        WF->>Checks: Evidence, scope, attributes, codes
        Checks-->>WF: Structured validated result + warnings
        WF->>DB: Persist extraction and related entities
        alt Database write succeeds
            DB-->>WF: Commit confirmed
            WF-->>API: Result with saved = true
            API-->>UI: Extraction ID and structured results
            UI-->>User: Show results for review
        else Write fails
            DB-->>WF: Persistence error
            WF-->>API: Failure (not saved)
            API-->>UI: Safe error response
            UI-->>User: Explain unsuccessful save
        end
    end
```

## Clinical validation and terminology

The workflow prioritizes **documented evidence** over guessed attributes or forced terminology codes. A valid code format or catalog hit does not itself guarantee that a clinical concept is an exact semantic match. The diagram flows **left to right**.

```mermaid
flowchart LR
    INPUT["Extracted entity + source span"] --> EVIDENCE{"Evidence supported by source?"}
    EVIDENCE -->|"No"| HOLD["Flag or withhold unsupported claim"]
    EVIDENCE -->|"Yes"| SCOPE["Classify current / historical / conditional / negated"]
    SCOPE --> ATTR["Bind documented attributes"]
    ATTR --> CODE["RxNorm or LOINC candidate lookup"]
    CODE --> MATCH{"Semantic correspondence"}
    MATCH -->|"Exact"| EXACT["Use verified exact code"]
    MATCH -->|"Narrower / broader / ambiguous"| REVIEW["Retain candidate with uncertainty"]
    MATCH -->|"Unresolved"| NULL["Leave unverified code unset"]
    EXACT --> OUTPUT["Validated entity + safety flags"]
    REVIEW --> OUTPUT
    NULL --> OUTPUT
    HOLD --> OUTPUT
    OUTPUT --> STORE["Persist and display for human review"]

    classDef good fill:#dcfce7,stroke:#16a34a,color:#14532d
    classDef caution fill:#fef3c7,stroke:#d97706,color:#78350f
    classDef error fill:#fee2e2,stroke:#dc2626,color:#7f1d1d
    class EXACT good
    class REVIEW,NULL caution
    class HOLD error
```

Examples of distinctions that matter: **active discharge medication vs. allergy**, **historical lab result vs. newly ordered investigation**, and **explicit serum specimen vs. undocumented specimen**. Reviewers should see uncertainty rather than a fabricated degree of precision.

## Human review and audit trail

Review decisions are modeled as separate records. The original extraction and its source evidence remain available after corrections.

```mermaid
flowchart LR
    STORED["Saved AI extraction"] --> QUEUE["Entity pending review"]
    QUEUE --> ACCEPT["ACCEPTED"]
    QUEUE --> CORRECT["CORRECTED"]
    QUEUE --> REJECT["REJECTED"]
    QUEUE --> CLARIFY["NEEDS_CLARIFICATION"]
    ACCEPT --> AUDIT["Versioned review audit"]
    CORRECT --> AUDIT
    REJECT --> AUDIT
    CLARIFY --> AUDIT
    AUDIT --> HISTORY["Review history + original evidence"]
    STORED -.->|"Preserved original"| HISTORY

    classDef review fill:#eef2ff,stroke:#6366f1,color:#312e81
    classDef history fill:#f0fdf4,stroke:#16a34a,color:#14532d
    class ACCEPT,CORRECT,REJECT,CLARIFY review
    class AUDIT,HISTORY history
```

The current local workflow may operate in **unauthenticated demonstration mode**. That is **not** equivalent to an authenticated clinician approval. Institutional sign-in, role-based/patient-scoped access, and deployment controls are prerequisites for real clinical operations.

## Technology stack

| Layer | Technology |
| --- | --- |
| User interface | Angular 21, TypeScript |
| API and workflow | ASP.NET Core / .NET 10, C# |
| Primary provider | Microsoft Foundry (`gpt-6.1-sol` development deployment) |
| Fallback provider | BC5CDR-trained INT8 ONNX + C# rules |
| Terminology | RxNorm, LOINC |
| Storage | PostgreSQL + `vector` extension |
| Testing | .NET xUnit, Angular unit tests, Playwright Chromium browser tests |
| Local infrastructure | Docker Compose database setup |

**Python is not required for the main runtime or build.** Some development/evaluation scripts may be maintained separately.

## Repository layout

```text
.
├── frontend/                         # Angular dashboard and browser tests
├── docs/screenshots/                 # Real development UI screenshots used by this README
├── Start-Apollo.bat                   # One-click Windows launcher
├── Start-Apollo.ps1                   # Local service orchestration
├── backend/
│   ├── Apollo.Backend.csproj          # ASP.NET Core application
│   ├── Clinical/                      # Extraction and validation logic
│   ├── Storage/                       # PostgreSQL persistence
│   ├── db/platform/migrations/        # Versioned database migrations
│   ├── infra/docker/                  # Local container configuration
│   ├── contracts/                     # Integration contracts
│   ├── docs/                          # Backend technical documentation
│   └── tests/                         # Backend and evaluation tests
└── AIExtraction.slnx                  # .NET solution
```

See [Repository structure](backend/docs/repository-structure.md) for more detail.

## Run locally

### Prerequisites

- .NET **10 SDK**
- Node.js **24+** and npm
- PostgreSQL with the `vector` extension, or Docker with the supplied Compose configuration
- Azure CLI sign-in and appropriate Foundry permissions **if using the cloud provider**

### Option A: One-click Windows launcher

For an **already provisioned local Windows development environment**, the repository's startup scripts provide a shortcut:

- [`Start-Apollo.bat`](Start-Apollo.bat) — double-click entry point; launches the PowerShell script and leaves the launcher window open to display any errors.
- [`Start-Apollo.ps1`](Start-Apollo.ps1) — checks prerequisites and ports, starts the development services as needed, and opens the Angular app in the browser.

Keep **both files in the repository root** (beside `frontend/` and `backend/`). In File Explorer, double-click **`Start-Apollo.bat`**; alternatively run:

```powershell
cd "D:\AI Extraction"
.\Start-Apollo.bat
```

Based on the supplied launcher scripts, the sequence is:

```mermaid
flowchart LR
    BAT["Start-Apollo.bat"] --> PS["Start-Apollo.ps1"]
    PS --> PG["Bundled PostgreSQL<br/>127.0.0.1:5432"]
    PS --> API["ASP.NET Core API<br/>127.0.0.1:5000"]
    PS --> UI["Angular dev server<br/>127.0.0.1:4200"]
    UI --> BROWSER["Open localhost:4200"]

    classDef entry fill:#e0f2fe,stroke:#0284c7,color:#0c4a6e
    classDef step fill:#eef2ff,stroke:#6366f1,color:#312e81
    classDef done fill:#dcfce7,stroke:#16a34a,color:#14532d
    class BAT,PS entry
    class PG,API,UI step
    class BROWSER done
```

**Launcher requirements and behavior:**

- Expects the **bundled PostgreSQL executable** at `backend/tools/pgsql/bin/pg_ctl.exe` and an **already initialized** local cluster at `backend/tools/data_cx/`. It **does not install PostgreSQL or initialize a database**.
- Requires `.NET` and `npm` on `PATH`; `frontend/node_modules/` must already exist. Run `npm --prefix frontend ci` once if dependencies have not been installed.
- Checks ports **5432**, **5000**, and **4200**; it skips launching a second service when the relevant port is already in use. Confirm that an existing listener is actually the expected Apollo service.
- Opens separate terminal windows for the API and Angular processes, then opens `http://localhost:4200/` when both ports respond. **Keep the service terminals open** while using the app.
- Does **not** stop existing processes or reset PostgreSQL data. The batch file invokes PowerShell with a process-specific `ExecutionPolicy Bypass`; review scripts before running them in managed environments.

> [!NOTE]
> **Fresh GitHub clone?** The bundled PostgreSQL binaries and initialized `data_cx` cluster may be absent (and database files should not be committed). If so, **use Option B with Docker Compose or your own PostgreSQL instance**. The one-click launcher is for a pre-provisioned local setup, not a replacement for initial installation or database migration.

### Option B: Manual setup

Use this path on macOS/Linux, when the bundled Windows database is unavailable, or when you prefer to start each component yourself.

#### 1. Start PostgreSQL

From the repository root:

```powershell
docker compose -f backend/infra/docker/docker-compose.yml up -d
```

Override the development database connection using `ConnectionStrings__Postgres` if required. When upgrading an existing database, inspect and apply `backend/db/platform/migrations/` **in order**, with the appropriate migration privileges and backup process. Do not reset existing clinical data as part of routine setup.

#### 2. Start the ASP.NET Core API

```powershell
dotnet run --project backend/Apollo.Backend.csproj
```

For cloud inference, see [Foundry configuration and handoff](backend/docs/foundry-integration.md). The documented development deployment name is `gpt-6.1-sol`. Store keys only in User Secrets or environment variables; local Azure CLI and deployed managed identities are supported as documented.

#### 3. Start Angular

In a second terminal:

```powershell
cd frontend
npm ci
npm start
```

Open **http://localhost:4200**. Angular proxies `/api`, `/v1`, `/predict`, and `/health` to `http://127.0.0.1:5000`. When available, the ASP.NET Core backend can serve the production Angular build from `frontend/dist/cx-review-ui/browser`.

## API overview

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/extractions` | Extract structured data from pasted clinical text |
| `POST /api/v1/extractions/pdf` | Extract from a text-based PDF |
| `GET /api/v1/extractions/{id}` | Retrieve a persisted extraction |
| `GET /api/v1/extractions/{id}/reviews` | Retrieve entity-level review history |
| `POST /api/v1/extractions/{id}/reviews` | Submit a review decision under applicable access rules |
| `/v1/ner/predict` | Legacy NER compatibility endpoint |

The backend also retains documented compatibility aliases and FHIR-related capabilities. See the linked integration documentation and current API contracts for exact payloads and permissions.

## Verify the application

From the repository root:

```powershell
# Backend
dotnet restore AIExtraction.slnx
dotnet build AIExtraction.slnx --configuration Release
dotnet test backend/tests/Apollo.Backend.Tests.csproj --configuration Release

# Frontend
npm --prefix frontend ci
npm --prefix frontend test -- --watch=false
npm --prefix frontend run build
```

Integration, authenticated review, browser, and live-provider tests may require additional configuration. **A passing software test suite is not independent clinical validation.**

### Mermaid diagrams in this README

The diagrams above use native GitHub Markdown `mermaid` code fences and **render on GitHub without installing an npm package**. The repository's Angular runtime does not need Mermaid just to display this README.

If you want to experiment locally with the newest **Mermaid v13 alpha** CLI, install it in a **separate documentation-only folder**, not the Angular production application:

```powershell
mkdir mermaid-preview
cd mermaid-preview
npm init -y
npm install mermaid@v13-alpha --save-exact
# Example after saving a diagram from this README into architecture.mmd:
npx mermaid render architecture.mmd --format svg > architecture.svg
```

Mermaid v13 is **alpha software**, and GitHub uses its **own renderer/version**. Keep README diagrams on well-supported syntax for compatibility. The Markdown definitions above are the source of truth; optional SVG previews are not needed in the repository.

## Security and limitations

- **No real patient data by default.** Obtain institutional approval and implement appropriate privacy and clinical controls first.
- **Never commit secrets.** Protect Foundry credentials, PostgreSQL passwords, `.env` files, test fixtures containing sensitive records, and generated test reports.
- **Model output needs review.** Medication instructions, investigation labels, terminology mappings, and safety alerts can be incomplete or wrong.
- **Independent clinical evaluation remains pending.** Synthetic development benchmarks are not a substitute for clinician-annotated, blinded assessment.
- **Authentication is environment-dependent.** Local demo mode is not a production clinical identity or approval mechanism.
- **No OCR bundled.** Image-only PDFs return `422 OCR_REQUIRED`.

## Documentation

**README navigation:** [Top](#apollo-clinical-extraction-platform) · [Frontend walkthrough](#frontend-walkthrough) · [Screenshots](#frontend-screenshots) · [One-click start](#option-a-one-click-windows-launcher) · [Manual start](#option-b-manual-setup)


- [Foundry integration, configuration, migration, verification and handoff](backend/docs/foundry-integration.md)
- [Repository structure](backend/docs/repository-structure.md)
- [Build status](backend/docs/BUILD_STATUS.md)
- [Architecture and ADR index](backend/docs/00-index.md)

---

**Apollo Clinical Extraction Platform** — clinical information extraction with source evidence, explicit uncertainty, persistent results, and auditable human review.
