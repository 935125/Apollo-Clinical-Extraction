# Apollo Clinical Extraction Platform — Stage 12.1 UI Simplification Report

**Date:** 25 November 2026  
**Scope:** Removal of queue filter boxes, row status/model/word-count clutter, Validation Warnings panels, Local ONNX coverage paragraphs, and unrelated UHID chips while preserving all backend clinical validation and provider provenance.

---

## 1. Removed Frontend Clutter (Presentation-Only Simplification)

| Area | Removed Elements | Preserved Backend Behavior |
| --- | --- | --- |
| **Queue Toolbar** | `All / Ready / Extracting / Waiting / Failed` filter bar, `"4 notes · 1 active"` summary counter | `GET /api/v1/queue` still supports optional `search`, `state`, `limit`, `offset` for API/Swagger/Postman consumers. |
| **Queue Table Rows** | `Ready · Verification recommended`, `Local ONNX` chips, word counts (`23 words`), large status pills | Full metadata remains in `QueueItemDto` JSON for developer API clients; UI shows only a subtle 7px pulsing dot when a job is active. |
| **Selected Patient Header** | Multi-patient `.quick-switch-strip` chips | Replaced with prominent `Patient ID (Synthetic)` header + `Back to Queue` + within-patient `Note version` selector (only when the selected patient has >1 note/version). |
| **Extracted Results Panel** | `Validation warnings` panel, `Clinical validation diagnostics` toggle, warning badge counts, warning cards (`LOCAL_COVERAGE_REDUCED`, `TERMINOLOGY_UNMATCHED`, `TIMING_NOT_DOCUMENTED`, `INCOMPLETE_PRESCRIPTION`), `Local ONNX limited-coverage mode` paragraph, provider banners | **Backend validation is 100% untouched:** `ClinicalReview`, `PosologyBinder`, `TerminologyVerification`, `validationSummary`, `findings`, `qualityFlags`, and `modelMetadata` remain persisted in PostgreSQL (`P100234` v11 = `7` warnings, `P100237` v15 = `16` warnings) and returned in authenticated API responses. |

---

## 2. Final Visual Layout (Matching Apollo Reference)

- **Queue Overview:** Warm ivory canvas (`#F7F5F0`), Apollo Hospitals lockup, `Clinical Extraction` serif title, clean search box, live polling pill, compact `Refresh`, separate development-only `Submit Test Note` drawer, and minimal 4-column table (`UHID / Patient ID | Clinical Note Preview | Received | Action`).
- **Selected Patient Detail:**
  - **Left Column (`Source note`):** Deep Apollo green (`#064B41`) outer frame with warm ivory (`#F7F5F0`) scrollable read-only document preserving exact immutable stored text, section headings, and line breaks; no editing or rerun controls.
  - **Right Column (`Extracted results`):** Clean white surface (`#FFFFFF`) showing structured **Medications** (`Medication | Strength | Dose | Route | Frequency | Duration | Clinical Assertion`) and **Investigations** (`Investigation | Order Intent | Timing | Terminology Code`), optional lightweight row click to highlight supporting text and display the verbatim quotation, and honest states (`"Extraction in progress"`, concise failure state, or `"No supported medications or investigations identified."`).
