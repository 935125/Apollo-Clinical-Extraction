"""
Live Microsoft Foundry Model Verification & Smoke Test Runner.
Executes real API requests against deployed gpt-4.1-mini on Microsoft Foundry.
Evaluates:
- Live network round-trip & authorization
- Exact model deployment recording (gpt-4.1-mini, Global Standard)
- Schema conformity & Pydantic validation
- Verbatim evidence quote integrity
- Token consumption & cost tracking
- 20-note held-out evaluation dataset benchmark
"""

import os
import time
from typing import Any

from cx_worker.evaluation.held_out_dataset import build_held_out_dataset
from cx_worker.pipeline.adapter import (
    FoundryModelAdapter,
    IExtractionModelAdapter,
)
from cx_worker.pipeline.normalize import ProductionTerminologyNormalizer
from cx_worker.pipeline.preprocess import ClinicalPreprocessor
from cx_worker.pipeline.validate import ClinicalValidator


def run_live_smoke_test() -> dict[str, Any]:
    provider = os.getenv("EXTRACTION_MODEL_PROVIDER", "").strip().lower()
    endpoint = os.getenv("AZURE_AI_FOUNDRY_ENDPOINT") or os.getenv("AZURE_OPENAI_ENDPOINT")
    api_key = os.getenv("AZURE_AI_FOUNDRY_API_KEY") or os.getenv("AZURE_OPENAI_API_KEY")
    deployment = os.getenv("FOUNDRY_MODEL_DEPLOYMENT", "gpt-4.1-mini")

    report: dict[str, Any] = {
        "timestamp": time.strftime("%Y-%m-%d %H:%M:%S UTC", time.gmtime()),
        "target_platform": "Microsoft Foundry",
        "target_model": "gpt-4.1-mini",
        "model_version": "2025-04-14",
        "deployment_name": deployment,
        "deployment_type": "Global Standard",
        "provider_env": provider or "(unset)",
        "endpoint_configured": bool(endpoint),
        "api_key_configured": bool(api_key),
        "live_call_executed": False,
        "smoke_test_passed": False,
        "errors": [],
        "telemetry": {},
        "extractions": {},
    }

    if not endpoint or not api_key:
        msg = (
            "MISSING_FOUNDRY_CREDENTIALS: Real inference requires AZURE_AI_FOUNDRY_ENDPOINT and AZURE_AI_FOUNDRY_API_KEY. "
            "Silent fallback to mock is strictly prohibited when verifying real inference."
        )
        report["errors"].append(msg)
        return report

    # 1. Initialize FoundryModelAdapter
    try:
        adapter = FoundryModelAdapter(
            endpoint=endpoint,
            api_key=api_key,
            model_deployment=deployment,
            prompt_version="2025-04-14",
            timeout_seconds=30.0,
            max_retries=3,
        )
    except Exception as ex:  # noqa: BLE001
        report["errors"].append(f"Adapter initialization error: {ex}")
        return report

    # 2. Execute Smoke Test on Clinical Note
    sample_note = """Patient: 54-year-old female presenting for hypertension and dyslipidemia follow-up.
Current Meds: Metformin 500mg PO BD.
Allergies: NKDA.
Plan:
1. Prescribe Lisinopril 10mg PO daily for blood pressure control.
2. Start Atorvastatin 20mg PO at bedtime.
3. Order Serum Creatinine and Fasting Lipid Profile in 4 weeks.
4. Order 12-lead ECG."""

    t0 = time.perf_counter()
    try:
        raw_output = adapter.extract_structured(sample_note, "smoke-test-001")
        latency_ms = (time.perf_counter() - t0) * 1000.0
        report["live_call_executed"] = True
    except Exception as ex:  # noqa: BLE001
        report["errors"].append(f"Foundry inference call failed: {ex}")
        return report

    # 3. Validate Output via Pydantic & ClinicalValidator
    preprocessor = ClinicalPreprocessor()
    prep = preprocessor.preprocess(sample_note)
    validator = ClinicalValidator()
    val = validator.validate_extraction(raw_output, prep.normalized_text)
    normalizer = ProductionTerminologyNormalizer()

    # 4. Record Metrics & Verifications
    usage = adapter.last_usage
    report["telemetry"] = {
        "latency_ms": round(latency_ms, 2),
        "prompt_tokens": usage.get("prompt_tokens"),
        "completion_tokens": usage.get("completion_tokens"),
        "total_tokens": usage.get("total_tokens"),
        "estimated_cost_usd": usage.get("estimated_cost_usd"),
        "adapter_type": adapter.adapter_type,
        "model_deployment": adapter.model_name,
        "prompt_version": adapter.prompt_version,
    }

    # Verify entities and verbatim quotes
    med_list = []
    all_quotes_verified = True
    for vm in val.medications:
        if not vm.evidence.verified:
            all_quotes_verified = False
        norm = normalizer.normalize_medication(vm.item.name, vm.item.strength, vm.item.formulation, vm.item.route)
        med_list.append({
            "name": vm.item.name,
            "strength": vm.item.strength,
            "route": vm.item.route,
            "frequency": vm.item.frequency,
            "assertion_status": vm.item.assertion_status,
            "evidence_quote": vm.item.evidence_quote,
            "evidence_verified": vm.evidence.verified,
            "normalized_code": norm.matched_code,
            "normalized_name": norm.matched_name,
            "confidence": norm.confidence,
        })

    inv_list = []
    for vi in val.investigations:
        if not vi.evidence.verified:
            all_quotes_verified = False
        norm_i = normalizer.normalize_investigation(vi.item.name, vi.item.specimen)
        inv_list.append({
            "name": vi.item.name,
            "assertion_status": vi.item.assertion_status,
            "evidence_quote": vi.item.evidence_quote,
            "evidence_verified": vi.evidence.verified,
            "normalized_code": norm_i.matched_code,
            "normalized_name": norm_i.matched_name,
            "confidence": norm_i.confidence,
        })

    report["extractions"] = {
        "medications": med_list,
        "investigations": inv_list,
        "all_quotes_verified": all_quotes_verified,
        "validation_has_errors": val.has_errors,
    }

    report["smoke_test_passed"] = (
        len(med_list) >= 2 and len(inv_list) >= 2 and all_quotes_verified and not val.has_errors
    )

    return report


def run_held_out_evaluation(adapter: IExtractionModelAdapter) -> dict[str, Any]:
    dataset = build_held_out_dataset()
    validator = ClinicalValidator()

    total_notes = len(dataset)
    med_tp, med_fp, med_fn = 0, 0, 0
    inv_tp, inv_fp, inv_fn = 0, 0, 0
    attr_correct, attr_total = 0, 0
    intent_correct, intent_total = 0, 0
    unsupported_extractions = 0
    total_extractions = 0
    latencies: list[float] = []
    total_prompt_tokens = 0
    total_completion_tokens = 0
    total_cost_usd = 0.0

    for case in dataset:
        t0 = time.perf_counter()
        raw = adapter.extract_structured(case.note_text, case.id)
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        latencies.append(elapsed_ms)

        usage = adapter.last_usage
        total_prompt_tokens += usage.get("prompt_tokens", 0)
        total_completion_tokens += usage.get("completion_tokens", 0)
        total_cost_usd += usage.get("estimated_cost_usd", 0.0)

        val = validator.validate_extraction(raw, case.note_text)

        # Evaluate Medications
        exp_meds = list(case.expected_medications)
        matched_exp_indices = set()
        for act_m in val.medications:
            total_extractions += 1
            if not act_m.evidence.verified:
                unsupported_extractions += 1

            matched_idx = None
            for idx, exp_m in enumerate(exp_meds):
                if idx in matched_exp_indices:
                    continue
                if exp_m["name"].lower() == act_m.item.name.lower():
                    matched_idx = idx
                    break

            if matched_idx is not None:
                matched_exp_indices.add(matched_idx)
                med_tp += 1
                target = exp_meds[matched_idx]

                if "assertion_status" in target:
                    intent_total += 1
                    if target["assertion_status"] == act_m.item.assertion_status:
                        intent_correct += 1

                if target.get("strength"):
                    attr_total += 1
                    act_str = (act_m.item.strength or "").lower().replace(" ", "")
                    exp_str = target["strength"].lower().replace(" ", "")
                    if act_str == exp_str:
                        attr_correct += 1

                if target.get("frequency"):
                    attr_total += 1
                    if (act_m.item.frequency or "").lower() == target["frequency"].lower():
                        attr_correct += 1
            else:
                med_fp += 1

        med_fn += len(exp_meds) - len(matched_exp_indices)

        # Evaluate Investigations
        exp_invs = list(case.expected_investigations)
        matched_inv_indices = set()
        for act_i in val.investigations:
            total_extractions += 1
            if not act_i.evidence.verified:
                unsupported_extractions += 1

            matched_idx = None
            for idx, exp_i in enumerate(exp_invs):
                if idx in matched_inv_indices:
                    continue
                if exp_i["name"].lower() in act_i.item.name.lower() or act_i.item.name.lower() in exp_i["name"].lower():
                    matched_idx = idx
                    break

            if matched_idx is not None:
                matched_inv_indices.add(matched_idx)
                inv_tp += 1
            else:
                inv_fp += 1

        inv_fn += len(exp_invs) - len(matched_inv_indices)

    med_p = med_tp / (med_tp + med_fp) if (med_tp + med_fp) > 0 else 1.0
    med_r = med_tp / (med_tp + med_fn) if (med_tp + med_fn) > 0 else 1.0
    med_f1 = (2 * med_p * med_r) / (med_p + med_r) if (med_p + med_r) > 0 else 0.0

    inv_p = inv_tp / (inv_tp + inv_fp) if (inv_tp + inv_fp) > 0 else 1.0
    inv_r = inv_tp / (inv_tp + inv_fn) if (inv_tp + inv_fn) > 0 else 1.0
    inv_f1 = (2 * inv_p * inv_r) / (inv_p + inv_r) if (inv_p + inv_r) > 0 else 0.0

    sorted_lat = sorted(latencies)
    p50_lat = sorted_lat[len(sorted_lat) // 2] if sorted_lat else 0.0
    p95_lat = sorted_lat[min(len(sorted_lat) - 1, int(len(sorted_lat) * 0.95))] if sorted_lat else 0.0

    return {
        "total_notes": total_notes,
        "adapter_name": adapter.model_name,
        "adapter_type": adapter.adapter_type,
        "medication_precision": round(med_p, 4),
        "medication_recall": round(med_r, 4),
        "medication_f1": round(med_f1, 4),
        "investigation_precision": round(inv_p, 4),
        "investigation_recall": round(inv_r, 4),
        "investigation_f1": round(inv_f1, 4),
        "attribute_accuracy": round(attr_correct / attr_total, 4) if attr_total > 0 else 1.0,
        "intent_accuracy": round(intent_correct / intent_total, 4) if intent_total > 0 else 1.0,
        "unsupported_rate": round(unsupported_extractions / total_extractions, 4) if total_extractions > 0 else 0.0,
        "p50_latency_ms": round(p50_lat, 2),
        "p95_latency_ms": round(p95_lat, 2),
        "total_prompt_tokens": total_prompt_tokens,
        "total_completion_tokens": total_completion_tokens,
        "total_tokens": total_prompt_tokens + total_completion_tokens,
        "total_estimated_cost_usd": round(total_cost_usd, 6),
    }


if __name__ == "__main__":
    res = run_live_smoke_test()
    print("=== LIVE FOUNDRY SMOKE TEST REPORT ===")
    print(f"Target Platform: {res['target_platform']}")
    print(f"Deployment: {res['deployment_name']} ({res['deployment_type']})")
    print(f"Provider Setting: {res['provider_env']}")
    print(f"Endpoint Configured: {res['endpoint_configured']}")
    print(f"API Key Configured: {res['api_key_configured']}")
    print(f"Live Call Executed: {res['live_call_executed']}")

    if res["errors"]:
        print("\nERRORS / BLOCKS:")
        for err in res["errors"]:
            print(f"  - {err}")
    else:
        print(f"Smoke Test Passed: {res['smoke_test_passed']}")
        print(f"Latency: {res['telemetry'].get('latency_ms')} ms")
        print(f"Tokens: {res['telemetry'].get('total_tokens')}")
        print(f"Est Cost: ${res['telemetry'].get('estimated_cost_usd')}")
