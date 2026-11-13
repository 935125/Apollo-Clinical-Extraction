"""
Configuration settings for CX AI Extraction Worker.
"""

import os
from pathlib import Path
from typing import Any

from dotenv import load_dotenv
from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


def load_environment() -> Path | None:
    """
    Explicitly loads .env from candidate paths in priority order:
    1. src/worker/.env (worker local)
    2. Repository root .env (repo shared)
    Returns the Path of the loaded file, or None if neither exists.
    """
    worker_dir = Path(__file__).resolve().parent.parent
    candidate_paths = [
        worker_dir / ".env",
        worker_dir.parent.parent / ".env",
    ]

    for candidate in candidate_paths:
        if candidate.is_file():
            load_dotenv(dotenv_path=candidate, override=True)
            return candidate

    # Fallback to standard dotenv resolution
    load_dotenv(override=True)
    return None


def get_configuration_diagnostics() -> dict[str, Any]:
    """
    Returns non-sensitive configuration diagnostics for audit reports and startup logs.
    Never prints or returns secrets or raw authorization keys.
    """
    loaded_file = load_environment()
    provider = os.getenv("EXTRACTION_MODEL_PROVIDER", "mock").strip().lower()
    endpoint = os.getenv("AZURE_AI_FOUNDRY_ENDPOINT") or os.getenv("AZURE_OPENAI_ENDPOINT")
    api_key = os.getenv("AZURE_AI_FOUNDRY_API_KEY") or os.getenv("AZURE_OPENAI_API_KEY")
    deployment = os.getenv("FOUNDRY_MODEL_DEPLOYMENT", "gpt-4.1-mini")
    api_mode = os.getenv("FOUNDRY_API_MODE", "auto").strip().lower()
    api_version = os.getenv("AZURE_OPENAI_API_VERSION", "2024-10-21")

    sanitized_endpoint = endpoint.split("?")[0] if endpoint else None

    return {
        "env_file_loaded": str(loaded_file) if loaded_file else None,
        "provider": provider,
        "endpoint_configured": bool(endpoint),
        "endpoint_sanitized": sanitized_endpoint,
        "api_key_configured": bool(api_key),
        "deployment": deployment,
        "api_mode": api_mode,
        "api_version": api_version,
    }


class WorkerSettings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="CX_", case_sensitive=False)

    service_bus_connection_string: str = Field(
        default="Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock",
        description="Azure Service Bus connection string or emulator endpoint",
    )
    queue_name: str = Field(
        default="note-events", description="Queue for realtime note committed events"
    )
    backfill_queue_name: str = Field(
        default="note-events-backfill", description="Queue for historical backfill events"
    )

    postgres_host: str = Field(default="localhost")
    postgres_port: int = Field(default=5432)
    postgres_db: str = Field(default="cx_platform")
    postgres_user: str = Field(default="cx_worker")
    postgres_password: str = Field(default="dev_password_only")

    foundry_endpoint: str = Field(default="https://mock-foundry.openai.azure.com/openai/v1/")
    foundry_deployment: str = Field(default="gpt-4o-clinical")

    max_note_char_length: int = Field(default=65536, description="64 KiB safety limit")
    worker_concurrency: int = Field(default=3, description="Async worker tasks per replica")


settings = WorkerSettings()
