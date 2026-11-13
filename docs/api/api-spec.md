# D. API Specification & Integration Guidelines

Status: **Milestone 0 (Architecture & Feasibility)**  
Authoritative OpenAPI Definitions:
- [Platform Review & Operations API](../../contracts/openapi/platform-api.v1.yaml)
- [EMR Integration Assumed Contract](../../contracts/openapi/emr-integration.v1.yaml)

---

## 1. Design & Protocol Standards

### 1.1 Wire Format & Conventions
- **Protocols**: HTTPS / TLS 1.2+ mandatory.
- **Data Format**: `application/json` (or `application/problem+json` on errors).
- **Casing**:
  - JSON properties: `camelCase`.
  - HTTP Headers: `Header-Case` (e.g., `Idempotency-Key`, `If-Match`).
  - Query parameters: `camelCase`.
- **Temporal Values**: ISO 8601 / RFC 3339 strings strictly in UTC ending with `Z` (e.g. `2026-11-13T11:00:00.000Z`).
- **Identifiers**:
  - Platform internal keys: UUID v7 represented as standard 36-char lowercase strings.
  - External hospital keys: Opaque strings (max 64 characters, sanitized).

---

## 2. Error Handling (RFC 9457 Problem Details)

All non-2xx responses MUST return `application/problem+json` conforming to RFC 9457. Under no circumstances may an endpoint return an unhandled stack trace or raw exception.

### Standard Problem Schema
```json
{
  "type": "https://cx.hospital.internal/problems/note-version-stale",
  "title": "Note Version Stale",
  "status": 409,
  "code": "NOTE_VERSION_STALE",
  "detail": "The clinical note has been amended in the EMR. Current version is 3, but approval was attempted on version 2.",
  "instance": "/api/v1/medications/018c1b82-7e02-7a0e-bc5d-6387d891bca2/decisions",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": {}
}
```

### Authoritative Machine Error Codes
| HTTP Status | Machine Code | Trigger Scenario |
|---|---|---|
| `400` | `VALIDATION_FAILED` | Request schema violation or unparseable payload. Details in `errors` map. |
| `401` | `UNAUTHORIZED` | Missing, expired, or invalid Microsoft Entra ID JWT. |
| `403` | `FORBIDDEN` | Missing application role (`CX.Reviewer`, `CX.Operator`, etc.). |
| `403` | `PATIENT_ACCESS_DENIED` | Clinician does not have care-team authorization for this patient/encounter. |
| `404` | `RESOURCE_NOT_FOUND` | Resource ID does not exist or caller is unauthorized to know of its existence. |
| `409` | `NOTE_VERSION_STALE` | Note was amended after extraction; approval is blocked for clinical safety. |
| `409` | `EXTRACTION_SUPERSEDED` | A newer extraction run has superseded this record. |
| `409` | `POSSIBLE_DUPLICATE_ORDER` | An active order for this drug/test already exists in this encounter. |
| `410` | `NOTE_CONTENT_PURGED` | The raw text snapshot has been purged per retention policy (90 days). |
| `412` | `CONCURRENT_MODIFICATION` | `If-Match` ETag precondition failed (row version changed by another user). |
| `422` | `ITEM_NOT_ELIGIBLE` | Item state is ineligible for write-back (e.g. negated or unconfirmed). |
| `422` | `IDEMPOTENCY_KEY_REUSED` | `Idempotency-Key` was submitted previously with a mismatched payload fingerprint. |
| `428` | `PRECONDITION_REQUIRED` | Missing required `If-Match` header on state-modifying action. |
| `429` | `RATE_LIMIT_EXCEEDED` | Request rate limit throttled. Includes `Retry-After` header. |
| `500` | `INTERNAL_SERVER_ERROR` | Unhandled error. Sanitized message; details in Application Insights. |

---

## 3. Concurrency & Mutation Control

### Optimistic Concurrency Control (ETags)
All mutable entities (medications, investigations, extraction summaries) carry an integer `row_version`.
1. `GET` operations return an `ETag: W/"<row_version>"` header.
2. `POST / PUT` decision endpoints require the incoming `If-Match: W/"<row_version>"` header.
3. If database `row_version` does not match, return `412 Precondition Failed`. Clinician UI must prompt user to refresh and inspect recent changes.

---

## 4. Idempotency Guarantees

Write-back operations and batch operations MUST supply an `Idempotency-Key` HTTP header (UUID or 16–128 character token):
1. **First Request**: Operation executes, records `(requested_by, idempotency_key, payload_sha256)`, returns `202 Accepted` or `201 Created`.
2. **Identical Replay**: If re-sent with exact matching payload fingerprint, return the existing status and `emr_order_id` immediately with `200 OK`.
3. **Mismatched Replay**: If the same key is re-used with different payload attributes, reject with `422 IDEMPOTENCY_KEY_REUSED`.

---

## 5. Keyset Pagination Pattern

Offset-based pagination (`OFFSET ... LIMIT ...`) is forbidden on high-volume tables to avoid linear scan degradation.
All list endpoints utilize deterministic keyset pagination:
- **Query parameters**: `pageSize` (default 25, max 100), `cursor` (opaque base64-encoded tuple: e.g., `createdAt` + `id`).
- **Response**: Returns `nextCursor: "<opaque-token>"` or `null` if last page.

