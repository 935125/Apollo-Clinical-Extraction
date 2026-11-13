"""
Configuration settings for CX AI Extraction Worker.
"""

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


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
