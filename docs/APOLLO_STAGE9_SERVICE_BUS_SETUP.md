# Apollo Clinical Extraction Platform — Stage 9 Service Bus Setup
## Azure Service Bus Integration Architecture, Queue Topology & Operational Runbook

---

### 1. Architectural Overview & Design Pattern

The Apollo Doctor OP EMR uses an asynchronous, decoupled messaging architecture to bridge physician documentation in the outpatient clinic with AI extraction workers:

```
[ Doctor OP EMR ]
       │
       ▼ (Atomic Transaction)
[ PostgreSQL: emr_notes + emr_outbox_events ]
       │
       ▼ (Background Service: EmrMessagingService)
[ Azure Service Bus Queue: op-clinical-notes ]
       │
       ▼ (Peeking & Settlement)
[ Extraction Consumer: EmrConsumer ]
       │
       ├── SHA-256 Hash Integrity Check
       ├── Advisory Lock Concurrency Guard
       └── Extraction Execution (Microsoft Foundry GPT-6.1 Sol / ONNX)
       │
       ▼
[ PostgreSQL: extraction_runs + emr_extraction_links ]
```

#### Guarantees Provided:
1. **Zero Note Loss During Broker Outages:** The physician's signed note is persisted atomically in PostgreSQL alongside an outbox event row. Even if Azure Service Bus is unreachable, the note remains in `Signed` status and outbox status is marked `Failed` or `Pending Publication`.
2. **At-Least-Once Delivery with Idempotent Ingestion:** The worker enforces duplicate message protection via table `emr_processed_messages` and durable `extraction_id` UUID binding. Replayed messages do not trigger duplicate extraction runs or create orphaned records.
3. **Audit Provenance:** Every event payload carries `schemaVersion`, `eventId`, `noteId`, `noteVersion`, `correlationId`, and SHA-256 `sourceHash`.

---

### 2. Azure Infrastructure Requirements

| Parameter | Recommended Production Value | Justification |
| :--- | :--- | :--- |
| **Resource Type** | Azure Service Bus Namespace | High-throughput, enterprise-grade cloud message broker. |
| **SKU / Tier** | Standard Tier | Supports dead-lettering, transactions, scheduled messages, and duplicate detection. |
| **Queue Name** | `op-clinical-notes` | Dedicated queue for signed outpatient clinical notes. |
| **Max Delivery Count** | `10` | Permits transient network retries before routing to DLQ. |
| **Lock Duration** | `5 minutes` (`PT5M`) | Accommodates multi-section clinical note extraction and normalization latency. |
| **Max Auto Lock Renewal** | `10 minutes` | Prevents message lock expiry during complex long-form inference. |
| **Message TTL** | `7 days` (`P7D`) | Ample time for worker recovery during extended maintenance windows. |
| **Dead-Letter on Expiration** | `Enabled` | Prevents silent data loss of unconsumed clinical notes. |
| **Prefetch Count** | `0` (Disabled) | Ensures fair worker distribution without local queue starvation. |
| **Concurrency Mode** | `2 Concurrent Calls` | Controlled worker thread utilization per container instance. |

---

### 3. Identity & Access Management (RBAC)

Apollo mandates Azure Managed Identity for cloud authentication. Connection strings containing shared access keys (`SharedAccessKey=...`) are strictly prohibited in institutional environments.

#### Required Azure RBAC Roles:
1. **Producer Application (Web API):**
   - **Role:** `Azure Service Bus Data Sender`
   - **Scope:** `/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{sbNamespace}/queues/op-clinical-notes`
   - **Purpose:** Authorizes `AzureNoteBroker` to enqueue `ClinicalNoteSigned` events.

2. **Consumer Worker (Extraction Engine):**
   - **Role:** `Azure Service Bus Data Receiver`
   - **Scope:** `/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{sbNamespace}/queues/op-clinical-notes`
   - **Purpose:** Authorizes `EmrConsumer` to peek, receive, complete, abandon, and dead-letter messages.

---

### 4. Application Configuration

In `backend/appsettings.json` or environment variables:

```json
{
  "Emr": {
    "Messaging": {
      "Mode": "AzureServiceBus",
      "FullyQualifiedNamespace": "sb-apollo-prod-eastus.servicebus.windows.net",
      "QueueName": "op-clinical-notes",
      "InferenceEnabled": true
    }
  }
}
```

#### Messaging Modes:
- **`AzureServiceBus`:** Connects to real Azure Service Bus queue using DefaultAzureCredential / Managed Identity.
- **`Simulated`:** Development and automated test mode using in-memory channels with durable PostgreSQL outbox replay.
- **`Disabled`:** Disables outbound message dispatch for maintenance or offline evaluation. Notes remain saved in PostgreSQL.

---

### 5. Event Contract Specification

```json
{
  "eventId": "a7b3c9d1-0000-0000-0000-000000000001",
  "eventType": "ClinicalNoteSigned",
  "schemaVersion": 1,
  "noteId": "n100874-0000-0000-0000-000000000001",
  "noteVersion": 2,
  "correlationId": "live-final-86985b4aaa88",
  "occurredAtUtc": "2026-01-28T11:24:00Z"
}
```

#### Envelope Headers:
- `MessageId`: Matches `eventId` string.
- `CorrelationId`: Preserves end-to-end distributed trace.
- `Subject`: `ClinicalNoteSigned`
- `ContentType`: `application/json`

---

### 6. Operational Runbook & Troubleshooting

#### 6.1 Monitoring Outbox Backlog
Execute against the PostgreSQL database:
```sql
SELECT publication_status, count(*) AS pending_count, min(occurred_at) AS oldest_pending
FROM emr_outbox_events
WHERE publication_status != 'Published'
GROUP BY publication_status;
```
*Remediation:* If `Failed` or `Pending Publication` exceeds threshold, verify Service Bus network connectivity and producer Managed Identity token lifecycle.

#### 6.2 Dead-Letter Queue (DLQ) Inspection
Messages exceeding 10 failed deliveries or carrying corrupted JSON payloads are diverted to:
`op-clinical-notes/$DeadLetterQueue`

To inspect dead-letter reasons via Azure CLI:
```bash
az servicebus queue show \
  --resource-group rg-apollo-clinical \
  --namespace-name sb-apollo-prod-eastus \
  --name op-clinical-notes \
  --query "countDetails.deadLetterMessageCount"
```

Common dead-letter error codes emitted by `EmrMessaging.cs`:
- `INVALID_EVENT_SCHEMA`: The event payload failed schema validation or MessageId did not match EventId.
- `CLINICAL_PROCESSING_FAILED`: The extraction engine encountered a non-recoverable error. The signed note in PostgreSQL remains safe and unmodified.

#### 6.3 Poison Message Recovery
1. Query `emr_outbox_events` and `emr_note_versions` for the affected `note_id`.
2. Inspect `source_hash` against computed SHA-256.
3. Once the root cause (e.g. malformed synthetic input or downstream service outage) is resolved, trigger manual redelivery by setting `publication_status = 'Pending Publication'` in `emr_outbox_events`.
