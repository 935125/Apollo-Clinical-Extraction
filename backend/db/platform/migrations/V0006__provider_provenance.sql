-- Additive migration: preserve historical rows with unknown provider metadata.
ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS provider_metadata jsonb NULL;
ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS dose_unit text NULL;
ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS action text NULL;
