-- =====================================================================================
-- V0002__extraction_runs_persistence.sql — Relational Persistence for Clinical AI Extractions
-- Creates extraction_runs, extracted_medications, and extracted_investigations tables
-- with UUID primary keys, pgvector extension support, and ON DELETE CASCADE foreign keys.
-- =====================================================================================

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS extraction_runs (
    extraction_id          uuid             PRIMARY KEY,
    correlation_id        text             NOT NULL,
    patient_id            text             NULL,
    encounter_id          text             NULL,
    note_text             text             NULL,
    char_length           integer          NOT NULL DEFAULT 0,
    schema_version        text             NOT NULL DEFAULT '1.0.0',
    extracted_at          timestamptz      NOT NULL DEFAULT now(),
    execution_duration_ms double precision NOT NULL DEFAULT 0,
    adapter_type          text             NOT NULL,
    model_deployment      text             NOT NULL,
    prompt_version        text             NOT NULL,
    prompt_tokens         integer          NULL,
    completion_tokens     integer          NULL,
    total_tokens          integer          NULL,
    estimated_cost_usd    double precision NULL,
    has_errors            boolean          NOT NULL DEFAULT false,
    error_count           integer          NOT NULL DEFAULT 0,
    warning_count         integer          NOT NULL DEFAULT 0,
    sections_json         jsonb            NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_extraction_runs_extracted_at
    ON extraction_runs (extracted_at DESC);

CREATE TABLE IF NOT EXISTS extracted_medications (
    medication_id         uuid             PRIMARY KEY,
    extraction_id         uuid             NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
    name                  text             NOT NULL,
    strength              text             NULL,
    dose                  text             NULL,
    route                 text             NULL,
    frequency             text             NULL,
    duration              text             NULL,
    formulation           text             NULL,
    instructions          text             NULL,
    assertion_status      text             NOT NULL,
    evidence_quote        text             NOT NULL,
    evidence_verified     boolean          NOT NULL DEFAULT true,
    evidence_start        integer          NULL,
    evidence_end          integer          NULL,
    normalization_status  text             NOT NULL,
    matched_code          text             NULL,
    display_name          text             NULL,
    confidence            double precision NOT NULL DEFAULT 0,
    quality_flags_json    jsonb            NOT NULL DEFAULT '[]'::jsonb,
    findings_json         jsonb            NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_extracted_medications_extraction_id
    ON extracted_medications (extraction_id);

CREATE TABLE IF NOT EXISTS extracted_investigations (
    investigation_id      uuid             PRIMARY KEY,
    extraction_id         uuid             NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
    name                  text             NOT NULL,
    timing                text             NULL,
    specimen              text             NULL,
    assertion_status      text             NOT NULL,
    status                text             NOT NULL DEFAULT 'preliminary',
    evidence_quote        text             NOT NULL,
    evidence_verified     boolean          NOT NULL DEFAULT true,
    evidence_start        integer          NULL,
    evidence_end          integer          NULL,
    normalization_status  text             NOT NULL,
    matched_code          text             NULL,
    display_name          text             NULL,
    confidence            double precision NOT NULL DEFAULT 0,
    quality_flags_json    jsonb            NOT NULL DEFAULT '[]'::jsonb,
    findings_json         jsonb            NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_extracted_investigations_extraction_id
    ON extracted_investigations (extraction_id);

