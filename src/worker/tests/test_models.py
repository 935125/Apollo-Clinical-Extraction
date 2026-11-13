import json
from pathlib import Path

from cx_worker.models import LlmExtractionOutput


def test_llm_extraction_output_matches_authoritative_json_schema():
    # Load authoritative JSON schema (root is parents[3] from src/worker/tests/test_models.py)
    schema_path = (
        Path(__file__).resolve().parents[3]
        / "contracts"
        / "extraction"
        / "llm-extraction-output.v1.schema.json"
    )
    assert schema_path.exists(), f"Contract must exist at {schema_path}"

    with open(schema_path, "r", encoding="utf-8") as f:
        auth_schema = json.load(f)

    # Export schema from Pydantic model
    generated_schema = LlmExtractionOutput.model_json_schema()

    # Verify root required properties
    assert set(generated_schema["required"]) == set(auth_schema["required"])
    assert "medications" in generated_schema["properties"]
    assert "investigations" in generated_schema["properties"]


def test_synthetic_extraction_parsing_and_validation():
    synthetic_payload = {
        "schema_version": "1.0",
        "medications": [
            {
                "name": "Metformin",
                "strength": "500 mg",
                "dose": "1 tab",
                "route": "PO",
                "frequency": "BD",
                "duration": "x 1 month",
                "formulation": "tablet",
                "instructions": "after meals",
                "assertion_status": "new_order",
                "evidence_quote": "Metformin 500mg 1 tab PO BD after meals x 1 month",
            },
            {
                "name": "Aspirin",
                "strength": "75 mg",
                "dose": None,
                "route": None,
                "frequency": None,
                "duration": None,
                "formulation": None,
                "instructions": None,
                "assertion_status": "negated",
                "evidence_quote": "No aspirin due to prior allergy",
            },
        ],
        "investigations": [
            {
                "name": "HbA1c",
                "specimen": "blood",
                "timing": "fasting",
                "assertion_status": "new_order",
                "evidence_quote": "Check fasting blood HbA1c",
            }
        ],
    }

    parsed = LlmExtractionOutput.model_validate(synthetic_payload)
    assert len(parsed.medications) == 2
    assert parsed.medications[0].name == "Metformin"
    assert parsed.medications[0].assertion_status == "new_order"
    assert parsed.medications[1].assertion_status == "negated"
    assert len(parsed.investigations) == 1
    assert parsed.investigations[0].name == "HbA1c"
