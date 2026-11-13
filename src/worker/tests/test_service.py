import pytest
from fastapi.testclient import TestClient

import cx_worker.service as srv
from cx_worker.pipeline.adapter import (
    ConfigurationError,
    SyntheticModelAdapter,
    create_model_adapter,
)
from cx_worker.pipeline.normalize import ProductionTerminologyNormalizer
from cx_worker.service import app

client = TestClient(app)


@pytest.fixture(autouse=True)
def reset_adapter_override():
    srv.set_adapter_override(None)
    yield
    srv.set_adapter_override(None)


def test_internal_service_health():
    srv.set_adapter_override(SyntheticModelAdapter())
    response = client.get("/internal/v1/health")
    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "healthy"
    assert data["service"] == "internal-ai-extraction"


def test_internal_service_successful_extraction():
    srv.set_adapter_override(SyntheticModelAdapter())

    payload = {
        "note_text": "Plan:\nPrescribe Metformin 500mg PO BD x 30 days.\nOrder Serum Creatinine.",
        "correlation_id": "test-corr-service-01",
    }

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 200
    data = response.json()

    assert data["schema_version"] == "1.0"
    assert data["correlation_id"] == "test-corr-service-01"
    assert data["model_metadata"]["adapter_type"] == "synthetic_mock"
    assert data["preprocessed"]["char_length"] > 0
    assert len(data["medications"]) >= 1
    assert data["medications"][0]["name"] == "Metformin"
    assert data["medications"][0]["strength"] == "500mg"
    assert data["medications"][0]["evidence"]["verified"] is True
    assert data["medications"][0]["normalization"]["status"] == "exact_match"
    assert data["medications"][0]["normalization"]["matched_code"] == "RX-MET-500"

    assert len(data["investigations"]) >= 1
    assert data["investigations"][0]["name"] == "Serum Creatinine"
    assert data["investigations"][0]["normalization"]["status"] == "exact_match"
    assert data["investigations"][0]["normalization"]["matched_code"] == "LAB-CREAT"
    assert data["execution_duration_ms"] > 0


def test_internal_service_model_timeout_maps_to_504():
    srv.set_adapter_override(SyntheticModelAdapter(should_timeout=True))

    payload = {
        "note_text": "Plan: Prescribe Aspirin 100mg OD.",
        "correlation_id": "test-corr-timeout",
    }

    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 504
    assert "MODEL_TIMEOUT" in response.json()["detail"]


def test_internal_service_malformed_output_maps_to_502():
    srv.set_adapter_override(SyntheticModelAdapter(malformed_json=True))

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
    assert response.status_code == 422


def test_create_model_adapter_no_silent_fallback(monkeypatch):
    # Enforce EXTRACTION_MODEL_PROVIDER=foundry with missing credentials
    monkeypatch.setenv("EXTRACTION_MODEL_PROVIDER", "foundry")
    monkeypatch.delenv("AZURE_AI_FOUNDRY_ENDPOINT", raising=False)
    monkeypatch.delenv("AZURE_OPENAI_ENDPOINT", raising=False)
    monkeypatch.delenv("AZURE_AI_FOUNDRY_API_KEY", raising=False)
    monkeypatch.delenv("AZURE_OPENAI_API_KEY", raising=False)

    with pytest.raises(ConfigurationError) as exc_info:
        create_model_adapter()

    assert "MISSING_FOUNDRY_CONFIGURATION" in str(exc_info.value)
    assert "Silent fallback to mock is strictly disabled" in str(exc_info.value)


def test_create_model_adapter_mock_provider(monkeypatch):
    monkeypatch.setenv("EXTRACTION_MODEL_PROVIDER", "mock")
    adapter = create_model_adapter()
    assert isinstance(adapter, SyntheticModelAdapter)
    assert adapter.adapter_type == "synthetic_mock"


def test_create_model_adapter_invalid_provider(monkeypatch):
    monkeypatch.setenv("EXTRACTION_MODEL_PROVIDER", "unsupported_provider_xyz")
    with pytest.raises(ConfigurationError) as exc_info:
        create_model_adapter()
    assert "INVALID_MODEL_PROVIDER" in str(exc_info.value)


def test_internal_service_foundry_unconfigured_fails_with_503(monkeypatch):
    monkeypatch.setenv("EXTRACTION_MODEL_PROVIDER", "foundry")
    monkeypatch.delenv("AZURE_AI_FOUNDRY_ENDPOINT", raising=False)
    monkeypatch.delenv("AZURE_AI_FOUNDRY_API_KEY", raising=False)

    payload = {
        "note_text": "Plan: Prescribe Metformin 500mg BD.",
        "correlation_id": "test-corr-unconfigured",
    }
    response = client.post("/internal/v1/extract", json=payload)
    assert response.status_code == 503
    assert "CONFIGURATION_ERROR" in response.json()["detail"]


def test_deterministic_terminology_strength_matching():
    normalizer = ProductionTerminologyNormalizer()

    # 1. Metformin 500mg should deterministically match 500mg entry, not 1000mg entry
    res_500 = normalizer.normalize_medication("Metformin", strength="500mg")
    assert res_500.status == "exact_match"
    assert res_500.matched_code == "RX-MET-500"
    assert res_500.rxnorm_cui == "860975"

    # 2. Metformin 1000mg should deterministically match 1000mg entry, not 500mg entry
    res_1000 = normalizer.normalize_medication("Metformin", strength="1000mg")
    assert res_1000.status == "exact_match"
    assert res_1000.matched_code == "RX-MET-1000"
    assert res_1000.rxnorm_cui == "860981"

    # 3. Verified brand name Lipitor with 20mg should match Atorvastatin 20mg
    res_lipitor = normalizer.normalize_medication("Lipitor", strength="20mg")
    assert res_lipitor.status == "synonym_match"
    assert res_lipitor.matched_code == "RX-ATO-20"
    assert res_lipitor.rxnorm_cui == "617318"

    # 4. Unknown entity remains unresolved without fabricating codes
    res_unknown = normalizer.normalize_medication("Unobtainium", strength="100mg")
    assert res_unknown.status == "unresolved"
    assert res_unknown.matched_code is None
    assert res_unknown.confidence == 0.0

    # 5. Laboratory normalization with verified LOINC
    res_creat = normalizer.normalize_investigation("Serum Creatinine")
    assert res_creat.status == "exact_match"
    assert res_creat.matched_code == "LAB-CREAT"
    assert res_creat.loinc_num == "2160-0"


def test_foundry_adapter_api_mode_validation(monkeypatch: pytest.MonkeyPatch):
    from openai import AzureOpenAI, OpenAI

    from cx_worker.pipeline.adapter import FoundryModelAdapter

    # 1. Mode azure_openai with valid root endpoint succeeds
    monkeypatch.setenv("FOUNDRY_API_MODE", "azure_openai")
    adapter_az = FoundryModelAdapter(
        endpoint="https://myresource.openai.azure.com/",
        api_key="mock_secret_key",
    )
    assert isinstance(adapter_az._client, AzureOpenAI)

    # 2. Mode azure_openai with /openai/v1 raises ConfigurationError
    with pytest.raises(ConfigurationError, match="ENDPOINT_MODE_MISMATCH"):
        FoundryModelAdapter(
            endpoint="https://myresource.openai.azure.com/openai/v1",
            api_key="mock_secret_key",
        )

    # 3. Mode openai_v1 with bare resource root raises ConfigurationError
    monkeypatch.setenv("FOUNDRY_API_MODE", "openai_v1")
    with pytest.raises(ConfigurationError, match="ENDPOINT_MODE_MISMATCH"):
        FoundryModelAdapter(
            endpoint="https://myresource.openai.azure.com",
            api_key="mock_secret_key",
        )

    # 4. Mode openai_v1 with /openai/v1 succeeds
    adapter_v1 = FoundryModelAdapter(
        endpoint="https://myresource.openai.azure.com/openai/v1",
        api_key="mock_secret_key",
    )
    assert isinstance(adapter_v1._client, OpenAI)

    # 5. Invalid mode raises ConfigurationError
    monkeypatch.setenv("FOUNDRY_API_MODE", "unsupported_protocol")
    with pytest.raises(ConfigurationError, match="INVALID_FOUNDRY_API_MODE"):
        FoundryModelAdapter(
            endpoint="https://myresource.openai.azure.com",
            api_key="mock_secret_key",
        )


def test_configuration_diagnostics_never_leaks_api_key(monkeypatch: pytest.MonkeyPatch):
    from cx_worker.config import get_configuration_diagnostics

    secret_key = "super_confidential_secret_key_12345"
    monkeypatch.setenv("AZURE_AI_FOUNDRY_API_KEY", secret_key)
    monkeypatch.setenv("AZURE_AI_FOUNDRY_ENDPOINT", "https://test-hosp.openai.azure.com/?token=secret")

    diag = get_configuration_diagnostics()
    assert diag["api_key_configured"] is True
    assert secret_key not in str(diag)
    assert "token=secret" not in diag["endpoint_sanitized"]

