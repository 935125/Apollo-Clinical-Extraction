# Apollo Clinical Extraction Platform — Postman & OpenAPI Contracts

**Date:** 25 November 2026  
**Backend Entry Point:** `backend/Apollo.Backend.csproj` (`http://127.0.0.1:5000` and `http://127.0.0.1:8000`)  
**Development OpenAPI Spec:** `GET http://127.0.0.1:5000/openapi/v1.json` (OpenAPI 3.0.3)  
**Development Swagger UI:** `GET http://127.0.0.1:5000/swagger` (Development environment only; never weakens endpoint authorization)

---

## 1. Verified Implemented API Surface

| Folder / Domain | Method & Route | Auth / Security | Verified Status Codes |
| --- | --- | --- | --- |
| **1. Infrastructure** | `GET /health` | Anonymous JSON liveness | `200` (`status: Healthy`, `service: Apollo.Backend`) |
| **1. Infrastructure** | `GET /health/live` | Anonymous process liveness | `200` (`self=Healthy`) |
| **1. Infrastructure** | `GET /health/db` | Anonymous PostgreSQL probe (`SELECT 1`) | `200` (`database=Healthy`) |
| **1. Infrastructure** | `GET /health/ready` | Anonymous full readiness (`database`, `pipeline`, `provider_configuration`) | `200` |
| **1. Infrastructure** | `GET /openapi/v1.json`, `GET /swagger` | Development-only anonymous OpenAPI/Swagger | `200` (Dev), `404` (Prod) |
| **2. Authentication** | `GET /api/v1/emr/auth/csrf` | Anonymous; issues `XSRF-TOKEN` cookie + JSON token | `200` |
| **2. Authentication** | `POST /api/v1/emr/auth/login` | `X-XSRF-TOKEN` + rate-limited (`doctor-login`) | `200`, `401 LOGIN_REJECTED` |
| **2. Authentication** | `POST /api/v1/emr/auth/dev-session` | Strictly `Development && DemoMode && Loopback` | `200`, `403 DEV_SESSION_DISABLED` |
| **2. Authentication** | `GET /api/v1/emr/auth/me` | `DoctorEmr` session cookie (`Apollo.Doctor`) | `200`, `401` |
| **3. Ingestion** | `GET /api/v1/emr/encounters` | `DoctorEmr` + SQL `emr_encounter_assignments` join | `200`, `401` |
| **3. Ingestion** | `POST /api/v1/emr/notes/{id}/corrections` | `Doctor` role + `X-XSRF-TOKEN` + `expectedVersion` | `200`, `400 INVALID_NOTE`, `409 NOTE_VERSION_CONFLICT` |
| **3. Ingestion** | `POST /api/v1/emr/notes/{id}/sign` | `Doctor` role + `X-XSRF-TOKEN` + atomic outbox insert | `200`, `409 NOTE_VERSION_CONFLICT` |
| **3. Ingestion** | `GET /api/v1/emr/notes/{id}` | `DoctorEmr` + encounter assignment join | `200`, `404` |
| **4–7. Queue & Isolation** | `GET /api/v1/queue` | `DoctorEmr` + assignment join (read-only PG projection) | `200`, `401` |
| **4–7. Queue & Isolation** | `GET /api/v1/queue/{noteId}?version=&patient=` | `DoctorEmr` + `(note_id, version)` + optional patient check | `200`, `404 QUEUE_ENTRY_NOT_FOUND` |
| **7. Patient Isolation** | `GET /api/v1/queue/patients/{patientReference}` | `DoctorEmr` + assignment join | `200`, `404 PATIENT_NOT_FOUND` |
| **4 & 8. Results & Routing** | `GET /v1/routing` | Anonymous config snapshot (zero inference) | `200` |
| **4 & 8. Results** | `GET /api/v1/extractions/{id}` | Linked encounter ownership check (`CheckLinkedExtraction`) | `200`, `404` |
| **4. FHIR Export** | `GET /api/v1/extractions/{id}/fhir` | Linked encounter ownership check | `200` (`resourceType: Bundle`) |
| **2. Role Boundary** | `POST /api/v1/extractions` | Restricted from `Doctor` role (`403 CLINICAL_ACCESS_DENIED`) | `403` (Doctor), `200` (Reviewer/Admin) |
| **9. Unmatched API Paths** | `GET /api/v1/queue/not-a-valid-guid` | Guarded against Angular SPA fallback | `404 ENDPOINT_NOT_FOUND` (JSON) |
