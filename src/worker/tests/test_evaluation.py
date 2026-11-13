"""
Automated unit & regression tests for Clinical AI Extraction Accuracy Benchmark.
Validates that benchmark dataset loads 100+ cases across 16 categories,
and evaluator achieves required clinical quality and safety thresholds.
"""

from pathlib import Path

from cx_worker.evaluation.dataset import build_evaluation_dataset
from cx_worker.evaluation.evaluator import ExtractionEvaluator, run_evaluation
from cx_worker.pipeline.adapter import SyntheticModelAdapter


def test_evaluation_dataset_completeness():
    dataset = build_evaluation_dataset()
    assert len(dataset) >= 100, f"Benchmark dataset should have at least 100 cases, got {len(dataset)}"

    categories = {case.category for case in dataset}
    assert len(categories) >= 16, f"Expected 16 categories, got {len(categories)}: {categories}"

    expected_categories = {
        "explicit_prescriptions",
        "historical_medications",
        "negated_medications",
        "discontinued_medications",
        "conditional_recommendations",
        "laboratory_investigations",
        "imaging_investigations",
        "multiple_prescriptions",
        "dosage_forms",
        "abbreviations",
        "missing_attributes",
        "contradictory_instructions",
        "repeated_entities",
        "prompt_injection",
        "long_narratives",
        "non_clinical_conversational",
    }
    missing = expected_categories - categories
    assert not missing, f"Missing required clinical categories: {missing}"


def test_evaluator_benchmark_metrics():
    adapter = SyntheticModelAdapter()
    evaluator = ExtractionEvaluator(adapter=adapter)
    report = evaluator.evaluate()

    # Verify minimum clinical quality thresholds
    assert report.total_notes >= 100
    assert report.medication_metrics.precision >= 0.90, f"Med precision {report.medication_metrics.precision} < 0.90"
    assert report.medication_metrics.recall >= 0.85, f"Med recall {report.medication_metrics.recall} < 0.85"
    assert report.medication_metrics.f1 >= 0.875, f"Med F1 {report.medication_metrics.f1} < 0.875"

    assert report.investigation_metrics.precision >= 0.90, f"Inv precision {report.investigation_metrics.precision} < 0.90"
    assert report.investigation_metrics.recall >= 0.85, f"Inv recall {report.investigation_metrics.recall} < 0.85"
    assert report.investigation_metrics.f1 >= 0.875, f"Inv F1 {report.investigation_metrics.f1} < 0.875"

    assert report.attribute_accuracy >= 0.85, f"Attribute accuracy {report.attribute_accuracy} < 0.85"
    assert report.intent_accuracy >= 0.85, f"Intent accuracy {report.intent_accuracy} < 0.85"
    assert report.unsupported_rate <= 0.02, f"Unsupported rate {report.unsupported_rate} > 0.02"


def test_run_evaluation_generates_report(tmp_path: Path):
    report_file = tmp_path / "test-accuracy-report.md"
    run_evaluation(adapter=SyntheticModelAdapter(), output_file=report_file)
    assert report_file.exists()
    content = report_file.read_text(encoding="utf-8")
    assert "# Clinical AI Extraction Accuracy Evaluation Report" in content
    assert "Executive Summary & Aggregate Metrics" in content
    assert "prompt_injection" in content


def test_held_out_dataset_integrity():
    from cx_worker.evaluation.held_out_dataset import build_held_out_dataset
    from cx_worker.evaluation.live_smoke_test import run_held_out_evaluation

    held_out = build_held_out_dataset()
    assert len(held_out) == 20, f"Expected 20 held-out cases, got {len(held_out)}"

    specialties = {case.specialty for case in held_out}
    assert len(specialties) >= 10, f"Expected at least 10 specialties, got {len(specialties)}"

    # Run held-out evaluation with synthetic adapter
    adapter = SyntheticModelAdapter()
    res = run_held_out_evaluation(adapter)
    assert res["total_notes"] == 20
    assert res["unsupported_rate"] <= 0.05
    assert res["p50_latency_ms"] >= 0.0

