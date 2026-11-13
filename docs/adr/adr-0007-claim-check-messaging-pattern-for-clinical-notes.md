# ADR-0007: Claim-Check Messaging Pattern for Clinical Note Ingestion

## Context
Clinical notes can range from small snippets to multi-page documents (up to 64 KiB or more). Passing full unencrypted patient note text directly inside message bus queues (Azure Service Bus) introduces severe compliance, security, and messaging risks:
1. Message size limits and queue memory bloat.
2. Unencrypted PHI circulating within messaging queues and dead-letter queues.
3. Multiple versions of notes in transit with potential race conditions.

## Decision
1. Implement the **Claim-Check Pattern**:
   - The EMR Transactional Outbox event (`note.version.committed`) contains **only metadata and identifiers**: `sourceSystem`, `patientId`, `encounterId`, `noteId`, `noteVersion`, `contentSha256`, `charLength`, and `committedAt`.
   - The queue message body contains **ZERO clinical text**.
2. **Worker Retrieval & Validation**:
   - The AI extraction worker receives the event, inspects the version, and retrieves the note text from the EMR Note Version Read API over an encrypted TLS connection with mutual service authentication.
   - The worker immediately calculates the SHA-256 hash of the received text and verifies it matches `contentSha256`. Any mismatch immediately halts processing as a security/integrity fault.
3. **Short-Lived Snapshot**:
   - The worker persists the verified text snapshot in `cx.clinical_note_content` with an explicit `purge_after = now() + interval '90 days'` retention deadline to allow clinician UI review highlighting.

## Consequences
- **Positive**: Service Bus messages remain lightweight (< 1 KB); dead-letter queues never hold raw patient clinical narratives; cryptographic verification guarantees text integrity.
- **Negative**: Adds one authenticated HTTP GET call between the worker and the EMR API per extraction.

