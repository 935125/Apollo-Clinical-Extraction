-- Test-only compatibility schema for PostgreSQL installations without pgvector.
-- Production and upgrade environments must use db/platform/migrations instead.
CREATE DOMAIN vector AS text;

CREATE TABLE extraction_runs (
  extraction_id uuid PRIMARY KEY,
  correlation_id text NOT NULL,
  note_text text NULL,
  source_type text NOT NULL DEFAULT 'text',
  source_file_name text NULL,
  saved_at timestamptz NULL,
  char_length integer NOT NULL DEFAULT 0,
  schema_version text NOT NULL DEFAULT '1.0.0',
  extracted_at timestamptz NOT NULL DEFAULT now(),
  execution_duration_ms double precision NOT NULL DEFAULT 0,
  adapter_type text NOT NULL,
  model_deployment text NOT NULL,
  prompt_version text NOT NULL,
  prompt_tokens integer NULL,
  completion_tokens integer NULL,
  total_tokens integer NULL,
  estimated_cost_usd double precision NULL,
  has_errors boolean NOT NULL DEFAULT false,
  error_count integer NOT NULL DEFAULT 0,
  warning_count integer NOT NULL DEFAULT 0,
  sections_json jsonb NOT NULL DEFAULT '[]'::jsonb,
  safety_alerts_json jsonb NOT NULL DEFAULT '[]'::jsonb,
  note_embedding vector NULL
);

CREATE TABLE extracted_medications (
  medication_id uuid PRIMARY KEY,
  extraction_id uuid NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
  name text NOT NULL, strength text NULL, dose text NULL, route text NULL,
  frequency text NULL, duration text NULL, formulation text NULL, instructions text NULL,
  assertion_status text NOT NULL, evidence_quote text NOT NULL,
  evidence_verified boolean NOT NULL DEFAULT true, evidence_start integer NULL, evidence_end integer NULL,
  normalization_status text NOT NULL, matched_code text NULL, display_name text NULL,
  confidence double precision NOT NULL DEFAULT 0,
  quality_flags_json jsonb NOT NULL DEFAULT '[]'::jsonb,
  findings_json jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE TABLE extracted_investigations (
  investigation_id uuid PRIMARY KEY,
  extraction_id uuid NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
  name text NOT NULL, timing text NULL, specimen text NULL, assertion_status text NOT NULL,
  status text NOT NULL DEFAULT 'preliminary', evidence_quote text NOT NULL,
  evidence_verified boolean NOT NULL DEFAULT true, evidence_start integer NULL, evidence_end integer NULL,
  normalization_status text NOT NULL, matched_code text NULL, display_name text NULL,
  confidence double precision NOT NULL DEFAULT 0,
  quality_flags_json jsonb NOT NULL DEFAULT '[]'::jsonb,
  findings_json jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE TABLE oov_quarantine_items (
  quarantine_id uuid PRIMARY KEY,
  extraction_id uuid NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
  category text NOT NULL, mention text NOT NULL, normalized_mention text NOT NULL,
  confidence double precision NOT NULL DEFAULT 0, reason text NOT NULL,
  start_char integer NULL, end_char integer NULL, scrubbed_context text NOT NULL,
  review_status text NOT NULL DEFAULT 'pending', mapped_code text NULL, mapped_display text NULL,
  reviewer_notes text NULL, created_at timestamptz NOT NULL DEFAULT now(), reviewed_at timestamptz NULL
);

-- Additive provider provenance fields used by the shared repository.
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS provider_metadata jsonb NULL;
ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS dose_unit text NULL;
ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS action text NULL;
