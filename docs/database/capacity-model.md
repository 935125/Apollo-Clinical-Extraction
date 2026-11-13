# Capacity Model & Workload Sizing

Status: **Milestone 0 (Architecture & Feasibility) — DRAFT for Review**  
Context: Target hospital network scenario (~80 TB historical hospital data environment planning scenario).

---

## 1. Workload Categorization & Sizing Assumptions

The platform does **not** ingest the entire 80 TB historical hospital warehouse. The 80 TB represents hospital-wide historical diagnostic archives, PACS imaging, lab runs, billing, and full clinical history. 

Only OP clinical notes containing unstructured text are in-scope for extraction.

### Core Hospital Sizing Parameters (Planning Baseline)
- **Hospital Network Scale**: 10 multi-specialty hospitals, 40 satellite outpatient clinics.
- **Outpatient (OP) Volume**: 25,000 patient encounters per working day (approx. 7.5 million encounters/year).
- **Clinical Notes**: Average 1.2 clinical notes/encounter (some visits have initial + amendment).
- **Daily New Note Volume**: ~30,000 note versions / day.
- **Operating Window (Peak)**: 8 hours (09:00 - 17:00), with 70% of notes created during peak hours.
- **Peak Realtime Rate**: 
  $$\text{Peak Rate} = \frac{30,000 \times 0.70}{8 \times 3600} \approx 0.73\text{ notes/second} \approx 44\text{ notes/minute}.$$
- **Design Target with Headroom (3x Surge / Catch-up)**: **150 notes/minute** (~2.5 notes/second) for realtime processing.
- **Historical Backfill Target**: 10 million historical notes processed across 120 off-peak days:
  $$\frac{10,000,000}{120 \times 12 \times 3600} \approx 2\text{ notes/second} \approx 120\text{ notes/minute}.$$

---

## 2. Text, Token, and LLM Capacity Model

### Per-Note Characteristics
- **Average Character Length**: 2,200 characters (~400–500 words).
- **95th Percentile Length**: 6,500 characters (~1,200 words).
- **Maximum Enforced Cut-off**: 64 KiB (~65,536 characters). Notes exceeding this are flagged and routed to manual review.
- **Estimated Prompt Size (Stage 2)**:
  - System Prompt + Few-shot instructions: ~1,100 tokens.
  - Clinical Note Text (p95): ~1,500 tokens.
  - Input Tokens per Note (p95): **~2,600 tokens**.
- **Estimated Output Size (Structured Output)**:
  - Average entities per OP note: 4 medications + 2 investigations.
  - JSON payload (with verbatim quotes & assertion): ~450 tokens.
  - Output Tokens per Note (p95): **~800 tokens**.

### LLM Quota & Rate Requirements (Microsoft Foundry)
At peak surge throughput of 150 notes/minute:
- **Requests Per Minute (RPM)**: 150 RPM.
- **Tokens Per Minute (TPM)**:
  $$\text{TPM} = 150 \times (2,600 + 800) = 510,000\text{ TPM}.$$
- **Recommended Provisioning**:
  - Dedicated Microsoft Foundry Provisioned Throughput Units (PTU) or Multi-Region Standard Pay-As-You-Go deployment with at least **700k TPM** reservation to absorb burstiness.
  - Separate deployment/quota for historical backfill (capped at 400k TPM off-peak) to prevent starving realtime doctor approvals.

---

## 3. Database Storage & Growth Projections (PostgreSQL)

Estimated yearly storage requirements on the CX Platform Database (excluding live EMR DB):

| Table / Artifact | Rows / Note | Year 1 Rows (7.5M Notes) | Avg Row Size | Year 1 Data Size | Index Overhead (~60%) | Total Storage / Year |
|---|---|---|---|---|---|---|
| `clinical_note_reference` | 1 | 7.5M | 280 B | ~2.1 GB | ~1.3 GB | ~3.4 GB |
| `clinical_note_content` (90d purge) | 1 | ~1.9M (active snapshot) | 2.5 KB | ~4.8 GB | ~0.5 GB | ~5.3 GB (steady state) |
| `extraction_run` | 1.05 (with retries) | 7.9M | 420 B | ~3.3 GB | ~2.0 GB | ~5.3 GB |
| `note_extraction` | 1 | 7.5M | 1.8 KB (with JSONB) | ~13.5 GB | ~5.0 GB | ~18.5 GB |
| `note_medication` | 4.2 | 31.5M | 450 B | ~14.2 GB | ~8.5 GB | ~22.7 GB |
| `note_investigation` | 2.1 | 15.8M | 380 B | ~6.0 GB | ~3.6 GB | ~9.6 GB |
| `terminology_candidate` | 18 | 135M | 210 B | ~28.4 GB | ~17.0 GB | ~45.4 GB |
| `review_decision` | 4.5 | 33.8M | 320 B | ~10.8 GB | ~6.5 GB | ~17.3 GB |
| `writeback_operation` | 0.85 | 6.4M | 310 B | ~2.0 GB | ~1.2 GB | ~3.2 GB |
| `writeback_item` | 3.8 | 28.5M | 380 B | ~10.8 GB | ~6.5 GB | ~17.3 GB |
| `audit.audit_event` (Monthly partitions) | 9 | 67.5M | 400 B | ~27.0 GB | ~16.2 GB | ~43.2 GB |
| **Total Platform Growth** | — | — | — | **~122.9 GB** | **~68.3 GB** | **~191.2 GB / Year** |

### Key Sizing Insights
1. **5-Year Growth**: Approximately **1.0 to 1.2 TB** for the extraction platform (including indexes, backups, and WAL).
2. **PostgreSQL Flexible Server SKU Recommendation**:
   - Initial: Standard_D4ds_v5 (4 vCPU, 16 GiB RAM, 512 GB Premium SSD, 3,000 IOPS).
   - Year 2+: Scale to Standard_D8ds_v5 (8 vCPU, 32 GiB RAM, 2 TB Storage, 6,400 IOPS).
   - Zone-redundant High Availability enabled with automated PITR retention for 35 days.

---

## 4. Compute & Worker Concurrency Model

### Worker Throughput
- Average LLM structured extraction latency (Foundry `gpt-4o` / comparable deployment): 2.5 seconds (p50), 5.5 seconds (p95).
- Pipeline overhead (Preprocessing + Validation + Normalization + DB transactions): ~250 ms.
- Total processing time per note: ~3.5 to 6.0 seconds.

### Concurrency Calculation
To process 150 notes/minute (2.5 notes/second) with average latency 4.0 seconds:
$$\text{Concurrent Workers} = \text{Throughput} \times \text{Latency} = 2.5 \times 4.0 = 10\text{ concurrent workers}.$$

- **Realtime Worker Pool**: 
  - Azure Container Apps replica count: Min 2, Max 8 replicas.
  - Concurrency per replica: 3 async worker tasks per replica (Total: 6 to 24 concurrent processing slots).
- **Database Connection Pool**:
  - API Replicas (2–4): 20 connections max each = 80.
  - Worker Replicas (2–8): 10 connections max each = 80.
  - Dispatcher & Sweeper: 10 connections.
  - Total Active Connections: ~170 (well within PostgreSQL 500 connection limit without PgBouncer; PgBouncer enabled for surge margin).

