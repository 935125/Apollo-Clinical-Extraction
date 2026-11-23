"""Read development captures only; do not inspect or execute sealed evaluation inputs."""
from pathlib import Path
import json
from collections import Counter

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / "docs/stage7-evidence"
before = json.loads((ROOT / "docs/stage6-evidence/after.json").read_text())
after = json.loads((OUT / "stage7-development-results.json").read_text())
old_cases = {c["id"]: c for c in before["cases"]}
new_cases = {c["id"]: c for c in after["cases"]}
gold_cases = {c["id"]: c for c in json.loads((ROOT / "backend/tests/Evaluation/stage5_independent_benchmark_35cases.json").read_text())}
rows = []
for cid, original in old_cases.items():
    current = new_cases[cid]
    for entity_type, evaluation in [("MEDICATION", "medicationEvaluation"), ("INVESTIGATION", "investigationEvaluation")]:
        group = "medications" if entity_type == "MEDICATION" else "investigations"
        gold_names = {g["name"].lower() for g in gold_cases[cid]["expected_" + group]}
        for failure in original["failures"]:
            if failure["kind"] not in ("FN", "FP"): continue
            if (failure["entity"].lower() in gold_names) != (failure["kind"] == "FN") and failure["kind"] == "FN": continue
            # The frozen development set has no medication FP at Stage 6.
            if entity_type == "MEDICATION" and failure["kind"] == "FP": continue
            if entity_type == "INVESTIGATION" and failure["kind"] == "FN" and failure["entity"].lower() not in gold_names: continue
            if entity_type == "MEDICATION" and failure["entity"].lower() not in gold_names: continue
            name = failure["entity"]
            raw_exact = [s for s in original["rawOnnxSpans"] if s["category"] == entity_type and s["text"].lower() == name.lower()]
            candidates = current["finalApiResult"]["modelMetadata"].get("reviewCandidates") or []
            candidate = [p for p in candidates if p["name"].lower() == name.lower()]
            final_matches = [p for p in current["finalApiResult"][group] if p["name"].lower() == name.lower()]
            still_fails = any(f["kind"] == failure["kind"] and f["entity"] == name for f in current[evaluation]["failures"])
            if entity_type == "MEDICATION":
                if raw_exact:
                    confidence = raw_exact[0]["confidence"]
                    reason = "BELOW_NEURAL_CONFIDENCE_THRESHOLD" if confidence < .85 else "NO_EXPLICIT_PRESCRIPTION_PREFIX"
                    safety = "Safe to surface the exact source span as an unapproved review candidate; do not add a prescription or lower the threshold."
                else:
                    reason = "RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY"
                    safety = "No validated automatic recovery in this release; human source reconciliation is required."
            else:
                if name.lower() in ("uric acid", "potassium"):
                    reason = "CONDITIONAL_THRESHOLD_IS_NOT_A_TEST_ORDER"
                    safety = "Suppress as an investigation order; do not allow analyte predictions to escape as drugs."
                elif name.lower() in ("troponin", "mri", "ct") or any(n in name.lower() for n in ("troponin i", "mri brain", "ct head", "ct thorax")):
                    reason = "CATALOG_TRUNCATED_SOURCE_QUALIFIERS"
                    safety = "Source-based qualifier expansion is appropriate; withhold unverified codes."
                elif "x-ray" in name.lower():
                    reason = "FROZEN_MATCHING_CONTRACT_DIFFERS_ON_DOCUMENTED_PA_VIEW"
                    safety = "Preserve source view and frozen evaluator; clinician adjudication is needed for semantic scoring."
                else:
                    reason = "CATALOG_ENTITY_OMISSION"
                    safety = "Use productive diagnostic grammar only when source supports it; otherwise retain the miss for review."
            rows.append({"caseId": cid, "entityType": entity_type, "failureKind": failure["kind"], "sourceText": current["source"],
                         "engineeringGoldEntity": name if failure["kind"] == "FN" else None,
                         "stage6Entity": name, "rawPrediction": raw_exact, "allRawModelOutput": original["rawOnnxSpans"],
                         "rawCapability": "MEDICATION_SPAN" if entity_type == "MEDICATION" else "UNSUPPORTED_INVESTIGATION_LABEL",
                         "confidence": [s["confidence"] for s in raw_exact], "exactReason": reason,
                         "stage6ProcessingOutcome": "omitted" if failure["kind"] == "FN" else "retained unmatched entity",
                         "stage6AdapterResult": original["finalPipeline"][group],
                         "stage7DeterministicOutcome": current["deterministicEntityRecovery"][group],
                         "finalApiResult": current["finalApiResult"], "sourceReviewCandidate": candidate,
                         "stillFailsFrozenMatching": still_fails, "safeRecoveryAssessment": safety, "humanReviewNecessary": True})

assert len(rows) == 36, (len(rows), Counter((r["entityType"], r["failureKind"]) for r in rows))
assert sum(r["entityType"] == "MEDICATION" and not r["rawPrediction"] for r in rows) == 9
assert sum(r["entityType"] == "MEDICATION" and bool(r["rawPrediction"]) for r in rows) == 7
raw_identical = all(c["rawOnnxSpans"] == new_cases[cid]["rawModelSpans"] for cid, c in old_cases.items())
assert raw_identical
(OUT / "original-36-entity-failures.json").write_text(json.dumps(rows, indent=2))
summary = {"originalFailureRows": len(rows), "originalMedicationFn": 16, "originalInvestigationFn": 13, "originalInvestigationFp": 7,
           "rawModelMedicationOmissions": 9, "postModelMedicationSuppressions": 7, "rawModelOutputsIdentical": raw_identical,
           "remainingOriginalRowsUnderFrozenMatching": sum(r["stillFailsFrozenMatching"] for r in rows),
           "sourceCandidatesTotal": sum(len(c["finalApiResult"]["modelMetadata"].get("reviewCandidates") or []) for c in after["cases"]),
           "matchedOriginalSuppressionCandidates": sum(bool(r["sourceReviewCandidate"]) for r in rows),
           "classification": "Engineering failure audit only; no independent clinical validation"}
(OUT / "entity-audit-summary.json").write_text(json.dumps(summary, indent=2))
lines = ["# Original entity failure audit", "", "Engineering annotations, not independent clinical gold. Full source/raw/final API results and safety rationale are in original-36-entity-failures.json.", "", "| Case | Type | Kind | Entity | Exact cause | Still fails |", "|---|---|---|---|---|---|"]
for r in rows: lines.append(f"| {r['caseId']} | {r['entityType']} | {r['failureKind']} | {r['stage6Entity']} | {r['exactReason']} | {r['stillFailsFrozenMatching']} |")
(OUT / "ENTITY_FAILURE_AUDIT.md").write_text("\n".join(lines) + "\n")
print(json.dumps(summary, indent=2))
