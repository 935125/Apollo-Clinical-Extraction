# Repository structure

```text
AI Extraction/
├── backend/                  ASP.NET Core API, ONNX pipeline, and backend support files
│   ├── Clinical/             shared text/PDF workflow and clinical rules
│   ├── Nlp/                  tokenizer, ONNX Runtime, and embeddings
│   ├── Storage/              PostgreSQL and test repositories
│   ├── tests/                .NET unit and API tests
│   ├── db/platform/migrations/ ordered PostgreSQL migrations
│   ├── infra/docker/         local pgvector database
│   ├── contracts/            retained interoperability/schema references
│   ├── docs/                 current notes plus historical architecture records
│   └── artifacts/            junction to local ONNX & tokenizer model files
├── frontend/                 Angular dashboard and UI tests
├── .github/workflows/        .NET and Angular CI
├── .gitignore                Git ignore rules
├── AIExtraction.slnx         backend solution
└── README.md                 project overview and startup instructions
```

Only `frontend/` and `backend/` contain runnable applications. The former `src/backend` gateway, `src/worker` Python service, and duplicate `src/cx-review-ui` application were removed. Database migrations live under `backend/db/platform/migrations` so existing installations can apply them independently and preserve extraction records.
