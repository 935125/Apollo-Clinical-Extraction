"""Offline, reproducible failure inventory. Never changes gold or calls inference.

Run from the repository root after the Stage6ReliabilityBenchmarkTests test.
Classification is engineering triage from source and captured stages, not clinical adjudication.
"""
from collections import Counter
from pathlib import Path
import csv
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / "docs/stage6-evidence"
GOLD = ROOT / "backend/tests/Evaluation/stage5_independent_benchmark_35cases.json"
assert hashlib.sha256(GOLD.read_bytes()).hexdigest().upper() == "3D2EE6AFD0AC72157C92BDDEEDC43F94511BA170D7985A1F44177CB78EB9AD84"
cases = {c["id"]: c for c in json.loads(GOLD.read_text(encoding="utf-8-sig"))}
baseline = {c["id"]: c for c in json.loads((OUT / "real-model-before.json").read_text())["cases"]}


def classify(f, case, captured, original):
    name, field = f["entity"], f["field"]
    is_med = any(g["name"].lower() == name.lower() for g in case["expected_medications"]) or any(p["name"].lower() == name.lower() for p in captured.get("finalPipeline", {}).get("medications", []))
    group = "medications" if is_med else "investigations"
    predictions = captured.get("finalPipeline", {}).get(group, [])
    matching = [p for p in predictions if p["name"].lower() == name.lower()]
    quote = " | ".join(dict.fromkeys(p["evidence"]["quote"] for p in matching))
    if not quote:
        quote = " | ".join(s for s in case["note_text"].splitlines() if name.lower() in s.lower())
    absent = any(x["kind"] == "FN" and x["entity"] == name for x in captured["failures"])
    # An attribute on a missing entity is detection-dependent, not a binding attempt.
    if f["kind"] == "FN" or f["kind"] == "attribute" and absent:
        if is_med:
            if original:
                return group, "Evaluation contract mismatch", "Original benchmark loaded no weights; deterministic scorer/catalog omission. Attribute misses inherit entity detection failure.", quote
            raw = captured.get("rawOnnxSpans", [])
            emitted = [s for s in raw if s["category"] == "MEDICATION" and name.lower() == s["text"].lower()]
            if emitted:
                confidences = ", ".join(f"{s['confidence']:.4f}" for s in emitted)
                return group, "Clinical scope error", f"Model emitted exact medication span(s), confidence {confidences}; C# confidence/context gating suppressed them. Conservative abstention is not model omission. Attribute misses inherit detection failure.", quote
            return group, "Model entity omission", "No exact medication span in real raw model output and no catalog recovery. Attribute misses inherit detection failure; partial-span decoding may contribute.", quote
        if any(name.lower() in p["name"].lower() for p in predictions):
            return group, "Evaluation contract mismatch", "Source-qualified investigation name carries additional documented view information; frozen evaluator does not match the broader gold name. Gold and aliases remain unchanged.", quote
        if any(p["name"].lower() in name.lower() for p in predictions):
            return group, "Normalization issue", "Catalog span truncates documented organ/subtype/modality qualifiers. This is genuine boundary/specificity loss, not an acceptable semantic equivalence.", quote
        return group, "Unsupported model capability", "BC5CDR has no investigation label; catalog rules did not recover the named investigation.", quote
    if f["kind"] == "FP":
        if name.lower() == "patient":
            return group, "Clinical scope error", "Allergy-prefix regex incorrectly captured the generic token Patient as a drug.", quote
        if name.lower() in ("uric acid", "potassium"):
            return group, "Clinical scope error", "A conditional threshold mention was retained as an investigation although no test order was stated. Frozen gold excludes it.", quote
        if is_med or name.lower() == "penicillin":
            return group, "Evaluation contract mismatch", "Source-grounded negated/allergy mention is absent from frozen gold. Retaining it does not imply an active prescription.", quote
        if name.lower() in ("troponin", "mri", "ct"):
            return group, "Normalization issue", "Catalog extraction returned a partial name without documented organ/subtype qualifiers, producing an FP paired with a qualified-name FN.", quote
        return group, "Evaluation contract mismatch", "Source-qualified view is broader in frozen gold and not matched by the evaluator; source is preserved for adjudication.", quote
    if field == "specimen":
        # The frozen suite sometimes assumes serum from an unqualified analyte.
        if not re.search(r"\b" + re.escape(f["expected"] or "") + r"\b", quote, re.I):
            return group, "Missing source information", "Expected specimen is not stated in the entity's source evidence. Abstention is required; the frozen label is retained.", quote
        return group, "Incorrect attribute binding", "Explicit specimen was omitted or replaced using terminology metadata / an adjacent test rather than local source evidence.", quote
    if field == "clinical_category":
        return group, "Unsupported model capability", "Category is a C# classification; original direct fallback path never populated it. This is a pipeline capability gap, not model category accuracy.", quote
    if field == "assertion_status":
        return group, "Assertion classification error", "Order/result or dose-change classification failed; broad line/section cues and generic lab-order regex contributed.", quote
    if field == "timing":
        return group, "Incorrect attribute binding", "Original fallback recognized only today; shared list timing and interval phrases were not bound.", quote
    if field == "formulation":
        return group, "Normalization issue", "Formulation qualifier was reduced to suspension or an unsupported solution form; source phrase must be preserved.", quote
    if field == "frequency":
        return group, "Incorrect attribute binding", "Primary interval was returned without PRN instruction, or competing daily/time-of-day tokens caused abstention.", quote
    return group, "Incorrect attribute binding", "Source attribute was omitted or attached incorrectly; inspect captured source, raw spans and final entity.", quote


rows = []
for label, filename in [("original-stage5", "original-stage5-results.json"), ("real-model-before", "real-model-before.json"), ("real-model-after", "after.json")]:
    report = json.loads((OUT / filename).read_text(encoding="utf-8-sig"))
    for c in report["cases"]:
        original = label == "original-stage5"
        captured = baseline[c["id"]] if original else c
        # Use original failure membership for detection dependency, but real captured stages
        # only as diagnostic context. Original raw neural outputs do not exist.
        captured = dict(captured, failures=c["failures"])
        for number, failure in enumerate(c["failures"], 1):
            group, cause, rationale, evidence = classify(failure, cases[c["id"]], captured, original)
            rows.append(dict(run=label, case_id=c["id"], failure_index=number, entity_type=group, **failure,
                             cause=cause, rationale=rationale, source_evidence=evidence,
                             original_model_executed=False if original else True))

(OUT / "failure-classification.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
with (OUT / "failure-classification.csv").open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
counts = {label: dict(Counter(r["cause"] for r in rows if r["run"] == label)) for label in dict.fromkeys(r["run"] for r in rows)}
(OUT / "failure-counts.json").write_text(json.dumps(counts, indent=2))
lines = ["# Stage 6 failure inventory", "", "Engineering triage, not independent clinical adjudication. Every evaluator failure, including attributes on missed entities, has a row in the linked JSON/CSV. Counts overlap at the entity level because one omission produces multiple attribute failures.", "", "| Case | Original failures | After failures | Original primary causes |", "|---|---:|---:|---|"]
for case_id in cases:
    before = [r for r in rows if r["case_id"] == case_id and r["run"] == "original-stage5"]
    after = [r for r in rows if r["case_id"] == case_id and r["run"] == "real-model-after"]
    lines.append(f"| {case_id} | {len(before)} | {len(after)} | {', '.join(sorted(set(r['cause'] for r in before)))} |")
(OUT / "FAILED_CASE_INVENTORY.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(json.dumps(counts, indent=2))
