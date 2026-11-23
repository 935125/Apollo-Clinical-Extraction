-- Apply as schema owner; runtime identity must never own the audit table.
-- Does not rewrite old audit entries. Duplicate versions require investigation,
-- not automatic deletion, before the unique index can be created.
CREATE TABLE IF NOT EXISTS entity_review_audit (
    review_id uuid PRIMARY KEY,
    extraction_id uuid NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
    entity_type text NOT NULL CHECK (entity_type IN ('MEDICATION', 'INVESTIGATION')),
    entity_index integer NOT NULL CHECK (entity_index >= 0),
    review_status text NOT NULL,
    original_json jsonb NOT NULL,
    corrected_json jsonb,
    evidence_json jsonb NOT NULL,
    review_action text NOT NULL,
    reviewer_identity text NOT NULL,
    auth_mode text NOT NULL,
    review_comment text,
    reviewed_at timestamptz NOT NULL DEFAULT now(),
    version_number integer NOT NULL CHECK (version_number > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_entity_review_version
    ON entity_review_audit(extraction_id, entity_type, entity_index, version_number);
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_runtime') THEN
        GRANT SELECT, INSERT ON entity_review_audit TO cx_runtime;
        REVOKE UPDATE, DELETE, TRUNCATE ON entity_review_audit FROM cx_runtime;
    END IF;
END $$;
