# Apollo Clinical Extraction Platform — Postman CLI Test Results

**Date:** 25 November 2026  
**Command Executed:** `.\Run-Apollo-Postman-Tests.ps1 -StartBackend` (with Angular on port `4200` stopped: `ANGULAR_4200_UP=False`)  
**Postman CLI:** `1.94.0` (`C:\Users\intern_ali\AppData\Roaming\npm\postman.cmd`)  
**Transcript:** [`docs/postman-evidence/postman-cli-run-20261125-111227.log`](postman-evidence/postman-cli-run-20261125-111227.log)  
**Structured YAML Report:** [`docs/postman-evidence/apollo-clinical-extraction-backend-api-verificat-2026-11-25-11-12-29.collection-run-summary.yaml`](postman-evidence/apollo-clinical-extraction-backend-api-verificat-2026-11-25-11-12-29.collection-run-summary.yaml)

---

## 1. Postman CLI Summary

- **Iterations:** `1` (`0` failed)
- **Requests executed:** `36` (`0` failed; includes bounded polling loops)
- **Assertions executed:** `41` (`41` passed, `0` failed)
- **Total run duration:** `8.8 s` (average response time `18 ms`)
- **Exit code:** `0`

---

## 2. Folder-by-Folder Breakdown

| Folder | Assertions Executed | Passed | Failed | Key Verified Behavior |
| --- | ---: | ---: | ---: | --- |
| **1. Infrastructure** | 5 | 5 | 0 | `/health` JSON (not HTML), `/health/live`, `/health/db` (`cx_platform`), `/health/ready` (`database`, `pipeline`, `provider_configuration`), `/openapi/v1.json` (3.0.3) |
| **2. Authentication** | 8 | 8 | 0 | `401` without session, CSRF issuance, `401 LOGIN_REJECTED` on invalid login, dev loopback session, `/auth/me`, `404 PATIENT_NOT_FOUND` on unassigned patient, `403 CLINICAL_ACCESS_DENIED` for Doctor on direct `POST /api/v1/extractions` |
| **3. Clinical Note Ingestion** | 5 | 5 | 0 | Encounter discovery (`P100234`, `P100237`), `400 INVALID_NOTE` on blank text, draft v16 creation, atomic sign v17 + outbox enqueue, retrieval verification |
| **4. Asynchronous Processing** | 3 | 3 | 0 | Bounded live polling to `Ready` via Azure Service Bus `op-clinical-notes`, `GET /api/v1/extractions/{id}` parity, FHIR R4 `Bundle` export |
| **5. Medication Extraction** | 5 | 5 | 0 | Verified verbatim evidence, `Amlodipine 5 mg / 1 tablet` (continued/current), `Metformin 500 mg / half tablet` (`new_order`), `Warfarin` (`discontinued`), negated `Amoxicillin` not active |
| **6. Investigation Extraction** | 5 | 5 | 0 | Verified verbatim evidence, `CBC` LOINC `58410-2` exact/candidate, `Serum Creatinine`, `LFT`, `ECG`, conditional `Chest X-ray`, historical `HbA1c` |
| **7. Patient / UHID Isolation** | 4 | 4 | 0 | `P100234` multi-version scope excludes `ENC-100570`, `P100237` excludes `P100234` tag/extraction, `404` on tampered `?patient=P100237`, v15 isolated from v17 |
| **8. Provider Routing** | 2 | 2 | 0 | `/v1/routing` (`configurationReady=true`, `foundryDeployment=gpt-6.1-sol`, `extractionInferenceExecuted=false`), persisted `modelMetadata.provider=onnx` & active `validationSummary` |
| **9. Idempotency & Reliability** | 4 | 4 | 0 | Duplicate sign rejected (`409 NOTE_VERSION_CONFLICT`), missing extraction (`404`), malformed GUID (`404 ENDPOINT_NOT_FOUND` JSON), stale `expectedVersion` (`409`) |
