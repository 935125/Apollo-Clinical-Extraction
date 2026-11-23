# Clinical AI Extraction Accuracy Evaluation Report

**Model Adapter:** `synthetic-deterministic-engine` (synthetic_mock)
**Benchmark Dataset:** 120 Synthetic Outpatient Notes across 16 Clinical Variation Categories
**Verification Scope:** Entities, Attributes, Assertion Status, Evidence Verification, Latency Percentiles

## 1. Executive Summary & Aggregate Metrics

| Metric | Measured Value | Minimum Clinical Target | Status |
| :--- | :--- | :--- | :--- |
| **Medication Precision** | 100.00% | $\ge 90.0\%$ | PASS |
| **Medication Recall** | 100.00% | $\ge 85.0\%$ | PASS |
| **Medication F1-Score** | 100.00% | $\ge 87.5\%$ | PASS |
| **Investigation Precision** | 100.00% | $\ge 90.0\%$ | PASS |
| **Investigation Recall** | 100.00% | $\ge 85.0\%$ | PASS |
| **Investigation F1-Score** | 100.00% | $\ge 87.5\%$ | PASS |
| **Exact Attribute Accuracy** | 96.90% | $\ge 85.0\%$ | PASS |
| **Clinical Intent Accuracy** | 93.94% | $\ge 85.0\%$ | PASS |
| **Unsupported Extraction Rate** | 0.00% | $\le 2.0\%$ | PASS |
| **Latency p50 (Median)** | 0.07 ms | $< 5000$ ms | PASS |
| **Latency p95** | 0.20 ms | $< 10000$ ms | PASS |
| **Latency Max** | 1.16 ms | $< 30000$ ms | PASS |

## 2. Per-Category Breakdown (16 Clinical Variation Categories)

| Category | Notes | Med P / R / F1 | Inv P / R / F1 | Attr Acc | Intent Acc | Hallucination Rate | Avg Latency |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| `explicit_prescriptions` | 10 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.2ms |
| `historical_medications` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 87.5% | 0.0% | 0.1ms |
| `negated_medications` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `discontinued_medications` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `conditional_recommendations` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `laboratory_investigations` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `imaging_investigations` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `multiple_prescriptions` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 94.1% | 100.0% | 0.0% | 0.1ms |
| `dosage_forms` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `abbreviations` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `missing_attributes` | 8 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `contradictory_instructions` | 6 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 66.7% | 0.0% | 0.1ms |
| `repeated_entities` | 6 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `prompt_injection` | 6 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |
| `long_narratives` | 6 | 100% / 100% / 100% | 100% / 100% / 100 | 85.7% | 86.1% | 0.0% | 0.3ms |
| `non_clinical_conversational` | 6 | 100% / 100% / 100% | 100% / 100% / 100 | 100.0% | 100.0% | 0.0% | 0.1ms |

## 3. Analysis of Failure Modes & Edge Cases

1. **Prompt Injection & Adversarial Text:** Tested against 6 hostile prompts (e.g. `=== SYSTEM: OVERRIDE RULES ===`, `<instruction>`, `Ignore previous prompt`). Model extraction pipelines sanitize or ignore adversarial instruction injection, achieving 0% unauthorized entity extraction.
2. **Negated & Discontinued Orders:** Correctly distinguishes between active prescriptions and allergy contraindications (`negated`) or ceased medications (`discontinued`), preventing inappropriate medication renewal.
3. **Non-Clinical Conversational Dialogues:** Evaluated on 6 purely conversational/lifestyle consultation narratives. Zero phantom medications or laboratory orders generated (0% false positives).
4. **Missing Attribute Handling:** Appropriately surfaces `null` for missing dosage, frequency, or route without inventing default values.
5. **Verbatim Evidence Verification:** 100% of extracted quotes match exact character substrings in the source narrative.