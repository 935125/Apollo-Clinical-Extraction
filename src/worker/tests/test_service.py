from pathlib import Path

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


def test_clinical_terminology_safety_formulation_and_lab_specificity():
    normalizer = ProductionTerminologyNormalizer()

    # 1. Omeprazole 20mg with NO formulation must not fabricate 'Delayed-Release Capsule'
    res_ome_no_form = normalizer.normalize_medication("Omeprazole", strength="20mg", formulation=None)
    assert res_ome_no_form.status == "exact_match"
    assert res_ome_no_form.matched_code == "RX-OME-20"
    assert "Formulation Unspecified" in (res_ome_no_form.matched_name or "")
    assert "Delayed-Release Capsule" not in (res_ome_no_form.matched_name or "")

    # 2. Omeprazole 20mg with explicit 'capsule' formulation assigns specific dosage form
    res_ome_capsule = normalizer.normalize_medication("Omeprazole", strength="20mg", formulation="capsule")
    assert res_ome_capsule.status == "exact_match"
    assert res_ome_capsule.matched_name == "Omeprazole 20mg Delayed-Release Capsule"

    # 3. CBC without differential mentions must map to automated hemogram panel (58410-2), NOT with diff
    res_cbc = normalizer.normalize_investigation("Complete Blood Count")
    assert res_cbc.status == "exact_match"
    assert res_cbc.matched_code == "LAB-CBC"
    assert res_cbc.loinc_num == "58410-2"
    assert "Automated Hemogram Panel" in (res_cbc.matched_name or "")
    assert "with Automated Differential" not in (res_cbc.matched_name or "")

    # 4. CBC with differential explicitly orders differential panel (57022-6)
    res_cbc_diff = normalizer.normalize_investigation("CBC with diff")
    assert res_cbc_diff.status == "synonym_match"
    assert res_cbc_diff.matched_code == "LAB-CBC-DIFF"
    assert res_cbc_diff.loinc_num == "57022-6"
    assert "with Automated Differential Panel" in (res_cbc_diff.matched_name or "")

    # 5. General Ultrasound without anatomical site must be flagged ambiguous
    res_us = normalizer.normalize_investigation("Ultrasound")
    assert res_us.status == "ambiguous"
    assert res_us.matched_code is None
    assert res_us.confidence == 0.0
    assert len(res_us.candidates) >= 2

    # 6. Combination medications: Sacubitril/Valsartan (Entresto)
    res_entresto = normalizer.normalize_medication("Entresto", strength="24/26mg")
    assert res_entresto.status == "synonym_match"
    assert res_entresto.matched_code == "RX-SAC-VAL-24-26"
    assert res_entresto.rxnorm_cui == "1656336"

    # 7. Combination medications: Augmentin (Amoxicillin/Clavulanate)
    res_augmentin = normalizer.normalize_medication("Augmentin", strength="500/125mg")
    assert res_augmentin.status == "synonym_match"
    assert res_augmentin.matched_code == "RX-AMX-CLV-500"
    assert res_augmentin.rxnorm_cui == "308182"



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


def test_existing_env_vars_have_precedence_over_dotenv(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    import os

    from dotenv import load_dotenv

    # 1. Simulate existing environment variable in session
    monkeypatch.setenv("FOUNDRY_MODEL_DEPLOYMENT", "session-explicit-model")

    # 2. Create temporary .env file with a conflicting value
    env_file = tmp_path / ".env"
    env_file.write_text("FOUNDRY_MODEL_DEPLOYMENT=env-file-model\n", encoding="utf-8")

    # 3. Load with override=False
    load_dotenv(dotenv_path=env_file, override=False)

    # 4. Existing environment variable must retain precedence
    assert os.getenv("FOUNDRY_MODEL_DEPLOYMENT") == "session-explicit-model"


