# ADR-0001: Isolation of EMR and Extraction Storage Boundaries

## Context
The platform is designed to operate within a hospital network housing ~80 TB of historical health data and high-concurrency outpatient clinics. A naive design might query the production EMR database directly or duplicate EMR tables into the AI database.

## Decision
1. **EMR Source of Truth**: The operational EMR remains the sole authoritative source of truth for patient identity, encounters, clinical notes, and active clinical orders.
2. **Dedicated Extraction Database**: The CX platform maintains an isolated PostgreSQL database storing only note-version references, short-lived note snapshots (purged after 90 days), extracted entities, terminology candidates, clinician review decisions, write-back transaction states, and audit trails.
3. **No Direct EMR Analytics**: No analytical or background extraction queries may run directly against the live EMR OLTP database. Realtime notes enter via the Transactional Outbox pattern; historical backfills read from read-replicas or batch data exports.

## Consequences
- **Positive**: Zero risk of AI extraction queries degrading live doctor EMR charting latency; clean separation of compliance boundaries; independent database scaling and indexing.
- **Negative**: Requires maintaining reference identifiers (`patientId`, `encounterId`, `noteId`, `noteVersion`) and coordinating distributed state via asynchronous events and idempotent write-backs.

