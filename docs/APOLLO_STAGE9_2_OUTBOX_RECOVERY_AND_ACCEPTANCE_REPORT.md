# Apollo Clinical Extraction Platform — Stage 9.2 Report
## Root Cause Diagnosis, Outbox Recovery & Live Service Bus End-to-End Acceptance Test

**Date:** 24 November 2026  
**Infrastructure Target:** Azure Service Bus Standard (`apollo-clinical-sb-dev.servicebus.windows.net`), Queue: `op-clinical-notes`  
**Execution Environment:** Windows, ASP.NET Core .NET 10, Angular 21, PostgreSQL 18.7  
**Inference Routing:** Local ONNX (`bc5cdr_int8`, `onnx_neurosymbolic_int8`) in authorized offline mode ($0 Foundry spend)  
**Author:** Principal Healthcare Architect & Lead Systems Engineer  

---

### 1. Executive Summary

During Stage 9.2, the Apollo Clinical Extraction Platform diagnosed and resolved the root cause behind signed outpatient clinical notes remaining stuck at **Pending Publication**. 

Both existing synthetic notes:
- **Patient P100234 (Version 4, Note ID `7b83e7bc-c817-4284-9306-8318b107adbb`)**
- **Patient P100235 (Version 3, Note ID `748adbb3-578b-471c-abac-5846019ae167`)**

have been safely published to Azure Service Bus, processed by the `.NET` worker (`EmrConsumer`), extracted via local neurosymbolic ONNX, and advanced to their terminal state `Needs Review` with extraction links permanently committed.

A pristine new synthetic encounter (**Patient P100237, ENC-100570**) was then executed through the entire end-to-end lifecycle in the live browser:
$$\text{Doctor Login} \longrightarrow \text{Create Note} \longrightarrow \text{Save Draft} \longrightarrow \text{Sign \& Submit} \longrightarrow \text{Outbox} \longrightarrow \text{Azure Service Bus} \longrightarrow \text{Worker} \longrightarrow \text{Local AI Extraction} \longrightarrow \text{Results Page}$$

All stages completed with **PASS**. Post-execution Azure Service Bus queue inspection confirmed `activeMessageCount = 0` and `deadLetterMessageCount = 0`. Historical Stage 8 records in `cx_platform` remain 100% preserved.

---

### 2. Diagnosis & Root Cause Analysis

An exhaustive investigation across the running processes, PostgreSQL outbox rows, and background worker lifecycles answered each diagnostic item:

| Diagnostic Item | Investigation Finding | Root Cause / Impact |
| :--- | :--- | :--- |
| **1. Exact PostgreSQL outbox rows** | Queried `emr_outbox_events`:<br>• `49cc8b3c-051f-4d02-8111-c197eb07ed24` (P100234 v4)<br>• `5cff9fae-98bc-4e52-8894-71db419d9b60` (P100235 v3) | Both rows had `publication_status = 'Pending Publication'`, `attempt_count = 0`, `last_error = NULL`, `published_at = NULL`. Neither event had ever been claimed or attempted. |
| **2. Atomicity of Sign & Submit** | Inspected `store.Sign` in [EmrStore.cs](file:///d:/AI%20Extraction/backend/Emr/EmrStore.cs) and database rows. | **Verified Atomic.** Both note version update (`UPDATE emr_notes`), immutable version snapshot (`INSERT INTO emr_note_versions`), and outbox record (`INSERT INTO emr_outbox_events`) executed within a single transaction `tx.CommitAsync()`. |
| **3. Running vs Test Configuration** | Queried `Win32_Process` PID 30120 (`Apollo.Backend.exe`) and PID 42420 (`dotnet run`). | **Process Stale / Configuration Disconnect.** Process 30120 was started at `12:56:54` before Stage 9.1 added the `Emr:Messaging` block. At startup, `Emr:Messaging:Mode` was `null` (defaulted to `"Disabled"`). |
| **4. Broker in Use** | Checked `INoteBroker` resolution. | In the running backend process, `INoteBroker` resolved to `DisabledNoteBroker` due to the unconfigured mode. In `appsettings.Development.json`, it was configured for `AzureNoteBroker`. |
| **5. Outbox Publisher Lifecycle** | Checked `EmrMessagingService.ExecuteAsync`. | In process 30120, `ExecuteAsync` exited immediately at line 116 (`if(mode is "Disabled" or "Custom") return;`). The background publisher was dead and never polling. |
| **6. EmrConsumer Worker Lifecycle** | Inspected `EmrMessagingService.ConsumeLoop`. | `ConsumeLoop` was not running because process 30120 exited early, and `Emr:Messaging:InferenceEnabled` was set to `false`. |
| **7. Retry Counters & Worker Logs** | Inspected `ApolloBackend.stdout.log` and outbox table. | `attempt_count = 0`, `processing_attempts = 0`, `last_error = NULL`. No retry counter incremented because the publisher loop had exited on process boot. |
| **8. Portable Azure CLI Discovery** | Verified `LocalDevelopmentConfiguration.cs` and `Program.cs`. | Discoverable at `C:\Users\intern_ali\AppData\Local\ApolloTools\AzureCLI\bin\az.cmd` and prepended to `PATH`. However, process 30120 was running code compiled before this discovery was injected. |
| **9. Target Namespace & Queue** | Inspected `.env` and `appsettings.Development.json`. | Confirmed: `apollo-clinical-sb-dev.servicebus.windows.net`, queue `op-clinical-notes`. |
| **10. Configuration Precedence** | Inspected `LocalDevelopmentConfiguration.cs` and `Apollo-LaunchEnvironment.ps1`. | Precedence: Command-line args > Process Environment Variables > User Secrets > `.env` Memory Provider > `appsettings.Development.json` > `appsettings.json`. `Apollo-LaunchEnvironment.ps1` previously only forwarded `Provider` and `FallbackProvider`. |
| **11. Parity with Stage 9.1 Smoke Test** | Compared smoke test factory with host. | Stage 9.1 smoke test created its own in-memory `WebApplicationFactory` with live `AzureNoteBroker`. The running host process had never been restarted to pick up the Stage 9.1 settings. |
| **12. Queue Duplicate Detection** | Queried Azure Service Bus metrics. | Azure Service Bus did NOT reject the messages. Neither `MessageId` had ever reached the broker (`attempt_count = 0`). |

---

### 3. Changes Implemented

1. **Publisher & Worker Lifecycle Hardening** ([EmrMessaging.cs](file:///d:/AI%20Extraction/backend/Emr/EmrMessaging.cs)):
   - Added scoped service resolution `using var scope = services.CreateScope();` inside `processor.ProcessMessageAsync` to ensure clean DI scoping for `EmrConsumer`.
   - Hardened `ConsumeLoop` against duplicate `StartProcessingAsync` invocations during reconnection backoff.
   - Enhanced structured logging with event IDs, note IDs, version numbers, attempt counts, and outcomes.
   - Exposed `QueueName` property on `AzureNoteBroker`.
2. **Offline AI Extraction & Messaging Configuration** ([appsettings.Development.json](file:///d:/AI%20Extraction/backend/appsettings.Development.json) & [.env](file:///d:/AI%20Extraction/.env)):
   - Configured `Extraction:Provider` = `"LocalOnnx"`
   - Configured `Extraction:AllowDirectLocal` = `true`
   - Configured `Extraction:DirectLocalReason` = `"authorized_offline"`
   - Configured `Emr:Messaging:Mode` = `"AzureServiceBus"`
   - Configured `Emr:Messaging:InferenceEnabled` = `true`
3. **Configuration Precedence & Launch Environment** ([LocalDevelopmentConfiguration.cs](file:///d:/AI%20Extraction/backend/Configuration/LocalDevelopmentConfiguration.cs) & [Apollo-LaunchEnvironment.ps1](file:///d:/AI%20Extraction/Apollo-LaunchEnvironment.ps1)):
   - Expanded `.env` file discovery to search parent (`..`), current (`.`), and project root directories.
   - Updated `Apollo-LaunchEnvironment.ps1` to forward `AllowDirectLocal` and `DirectLocalReason`.
   - Updated `Start-Terminal` in [Start-Apollo.ps1](file:///d:/AI%20Extraction/Start-Apollo.ps1) to ensure `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` are always exported into child processes.

---

### 4. Recovery of Existing Stuck Notes

Upon restarting the backend process with the live configuration:
1. `EmrMessagingService.PublishLoop` immediately claimed the eligible pending outbox rows via `store.Claim()` (`FOR UPDATE SKIP LOCKED`).
2. Both events were published to Azure Service Bus queue `op-clinical-notes`.
3. `store.Publication(claim, true)` updated:
   - `publication_status` $\longrightarrow$ **`Published`**
   - `published_at` $\longrightarrow$ Recorded UTC timestamp
   - `processing_status` $\longrightarrow$ **`Queued`**
4. `EmrMessagingService.ConsumeLoop` received both messages from Azure Service Bus, processed them via `EmrConsumer.Process`, linked them in `emr_extraction_links`, and completed the messages via `CompleteMessageAsync`.
5. Both notes transitioned to **`Needs Review`** with active extraction links.

#### Database Recovery Proof:

```text
               event_id               |               note_id                | note_version | publication_status |           published_at           | processing_status | processing_attempts 
--------------------------------------+--------------------------------------+--------------+--------------------+----------------------------------+-------------------+---------------------
 49cc8b3c-051f-4d02-8111-c197eb07ed24 | 7b83e7bc-c817-4284-9306-8318b107adbb |            4 | Published          | 2026-11-24 13:35:11.213437+05:30 | Needs Review      |                   1
 5cff9fae-98bc-4e52-8894-71db419d9b60 | 748adbb3-578b-471c-abac-5846019ae167 |            3 | Published          | 2026-11-24 13:35:11.133938+05:30 | Needs Review      |                   1
```

```text
               note_id                | note_version |               event_id               |            extraction_id             |            created_at            
--------------------------------------+--------------+--------------------------------------+--------------------------------------+----------------------------------
 7b83e7bc-c817-4284-9306-8318b107adbb |            4 | 49cc8b3c-051f-4d02-8111-c197eb07ed24 | 49cc8b3c-051f-4d02-8111-c197eb07ed24 | 2026-11-24 13:35:12.182439+05:30
 748adbb3-578b-471c-abac-5846019ae167 |            3 | 5cff9fae-98bc-4e52-8894-71db419d9b60 | 5cff9fae-98bc-4e52-8894-71db419d9b60 | 2026-11-24 13:35:12.18244+05:30
```

---

### 5. Live Acceptance Test Execution & Verification

A live automated Playwright acceptance test ([stage9-2-acceptance.mjs](file:///d:/AI%20Extraction/frontend/e2e/stage9-2-acceptance.mjs)) was executed against the running application at `http://localhost:4200` using pristine encounter **ENC-100570** (Patient **P100237**).

#### Test Evidence Matrix:

| Stage | Operation / Workflow Step | Verification Result | Evidence / Details | Status |
| :---: | :--- | :--- | :--- | :---: |
| **1** | **Doctor Login** | Logged in as `doctor.krishnan@apollo.local`, authenticated via session cookie, redirected to `/emr`. | [01-doctor-login-page.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/01-doctor-login-page.png)<br>[02-login-credentials-entered.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/02-login-credentials-entered.png) | **PASS** |
| **2** | **Doctor Dashboard** | Verified header shows `DK` / `Dr. K. Krishnan`. Existing notes P100234 & P100235 show recovered status. | [03-doctor-dashboard-recovered-notes.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/03-doctor-dashboard-recovered-notes.png) | **PASS** |
| **3** | **Create Note** | Opened pristine encounter `ENC-100570` for patient `P100237`. Loaded empty note editor. | [04-new-clinical-note-editor.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/04-new-clinical-note-editor.png) | **PASS** |
| **4** | **Save Draft** | Entered 25-word clinical text with medications and investigations. Clicked "Save Draft". Success alert displayed. | [05-clinical-note-draft-entered.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/05-clinical-note-draft-entered.png)<br>[06-draft-saved-confirmation.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/06-draft-saved-confirmation.png) | **PASS** |
| **5** | **Sign & Submit** | Opened modal, confirmed sign. Atomic transaction committed note status `Signed`, version 3, and outbox event `39d97797-492c-4327-9f29-d9d046dd1436`. | [07-sign-confirmation-modal.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/07-sign-confirmation-modal.png)<br>[08-extraction-status-initial.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/08-extraction-status-initial.png) | **PASS** |
| **6** | **Azure Service Bus & Worker** | Event dispatched to Azure queue `op-clinical-notes`, received by worker, processed via `LocalOnnx`, settled on broker. Status stepper automatically reached `Needs Review`. | [09-extraction-status-terminal.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/09-extraction-status-terminal.png) | **PASS** |
| **7** | **Extraction Results Page** | Clicked "Review extraction results". Results page loaded `Metoprolol` (BID), `Electrocardiogram` (11524-6), and `Lipid Profile` with source evidence quotes. | [10-extraction-results-page.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/10-extraction-results-page.png) | **PASS** |

#### Live Note Extraction Entities Proof:

```text
Medication: metoprolol | Dose: 50 mg | Frequency: BID | Assertion: current_medication
Evidence Quote: "Continue metoprolol 50 mg orally twice daily for 30 days"

Investigation 1: electrocardiogram | LOINC: 11524-6 | Assertion: new_order
Evidence Quote: "Order electrocardiogram and lipid panel next week"

Investigation 2: lipid panel | Display: Lipid Profile | Assertion: new_order
Evidence Quote: "Order electrocardiogram and lipid panel next week"
```

#### Final Cloud Queue State:

```json
{
  "activeMessageCount": 0,
  "deadLetterMessageCount": 0,
  "scheduledMessageCount": 0,
  "transferDeadLetterMessageCount": 0,
  "transferMessageCount": 0
}
```

---

### 6. Historical Data Preservation & Zero Billable Spend

- **Foundry API Spend:** **$0.00** (Zero billable Foundry inference tokens consumed; offline `LocalOnnx` executed).
- **Historical Stage 8 Runs:** **44 rows** (100% identical; pre-2026-11-24 timestamp filter matches baseline).
- **Historical Medications:** **101 rows** (100% identical).
- **Historical Investigations:** **136 rows** (100% identical).
- **Historical Audits:** **12 rows** (100% identical).

---

### 7. Evidence Files & Reports

- **Acceptance Evidence JSON:** [stage9-2-acceptance-evidence.json](file:///d:/AI%20Extraction/docs/stage9-evidence/stage9-2-acceptance-evidence.json)
- **Live Playwright Screenshots:** `docs/stage9-evidence/playwright/stage9-2/screenshots/`
  - [01-doctor-login-page.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/01-doctor-login-page.png)
  - [02-login-credentials-entered.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/02-login-credentials-entered.png)
  - [03-doctor-dashboard-recovered-notes.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/03-doctor-dashboard-recovered-notes.png)
  - [04-new-clinical-note-editor.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/04-new-clinical-note-editor.png)
  - [05-clinical-note-draft-entered.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/05-clinical-note-draft-entered.png)
  - [06-draft-saved-confirmation.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/06-draft-saved-confirmation.png)
  - [07-sign-confirmation-modal.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/07-sign-confirmation-modal.png)
  - [08-extraction-status-initial.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/08-extraction-status-initial.png)
  - [09-extraction-status-terminal.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/09-extraction-status-terminal.png)
  - [10-extraction-results-page.png](file:///d:/AI%20Extraction/docs/stage9-evidence/playwright/stage9-2/screenshots/10-extraction-results-page.png)
