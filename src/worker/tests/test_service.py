from fastapi.testclient import TestClient

import cx_worker.service as srv
from cx_worker.pipeline.adapter import SyntheticModelAdapter
from cx_worker.service import app

client = TestClient(app)


def test_internal_service_health():
    response = client.get("/internal/v1/health")
    assert response.status_code == 200
    assert response.json() == {"status": "healthy", "service": "internal-ai-extraction"}


def test_internal_service_successful_extraction():
    # Set default synthetic adapter
    srv.active_adapter = SyntheticModelAdapter()

    payload = {
        "note_text": "Plan:\nPrescribe Metformin 500mg PO BD x 30 days.\nOrder Serum Creatinine.",
        "correlation_id": "test-corr-service-01",
    }

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 200
    data = response.json()

    assert data["schema_version"] == "1.0"
    assert data["correlation_id"] == "test-corr-service-01"
    assert data["preprocessed"]["char_length"] > 0
    assert len(data["medications"]) >= 1
    assert data["medications"][0]["name"] == "Metformin"
    assert data["medications"][0]["strength"] == "500mg"
    assert data["medications"][0]["evidence"]["verified"] is True
    assert len(data["investigations"]) >= 1
    assert data["investigations"][0]["name"] == "Serum Creatinine"
    assert data["execution_duration_ms"] > 0


def test_internal_service_model_timeout_maps_to_504():
    srv.active_adapter = SyntheticModelAdapter(should_timeout=True)

    payload = {
        "note_text": "Plan: Prescribe Aspirin 100mg OD.",
        "correlation_id": "test-corr-timeout",
    }

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 504
    assert "MODEL_TIMEOUT" in response.json()["detail"]


def test_internal_service_malformed_output_maps_to_502():
    srv.active_adapter = SyntheticModelAdapter(malformed_json=True)

    payload = {
        "note_text": "Plan: Prescribe Aspirin 100mg OD.",
        "correlation_id": "test-corr-malformed",
    }

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 502
    assert "MALFORMED_MODEL_OUTPUT" in response.json()["detail"]


def test_internal_service_empty_note_rejected_with_422_or_400():
    payload = {"note_text": "", "correlation_id": "test-corr-empty"}

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 422  # Pydantic min_length=1 validation error
