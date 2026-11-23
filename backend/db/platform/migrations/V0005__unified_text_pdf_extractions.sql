-- Preserve existing extraction rows while recording their source and complete replay data.
CREATE EXTENSION IF NOT EXISTS vector;

ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS note_text text NULL;
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS source_type text NOT NULL DEFAULT 'text';
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS source_file_name text NULL;
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS saved_at timestamptz NULL;
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS safety_alerts_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS note_embedding vector(64) NULL;

UPDATE extraction_runs
SET saved_at = COALESCE(saved_at, extracted_at)
WHERE saved_at IS NULL;
