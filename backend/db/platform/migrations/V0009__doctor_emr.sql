-- Additive Stage 9 portal schema. Apply as schema owner, never as runtime.
-- The emr-simulator database is a separate design fixture, not portal storage.
CREATE TABLE IF NOT EXISTS emr_doctors (
  id uuid PRIMARY KEY, login text NOT NULL UNIQUE, display_name text NOT NULL,
  role text NOT NULL CHECK (role IN ('Doctor','ClinicalReviewer','Administrator')),
  password_hash text NOT NULL, failed_attempts int NOT NULL DEFAULT 0,
  locked_until timestamptz, enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_sessions (
  id uuid PRIMARY KEY, doctor_id uuid NOT NULL REFERENCES emr_doctors,
  expires_at timestamptz NOT NULL, revoked_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_emr_sessions_expiry ON emr_sessions(expires_at);
CREATE TABLE IF NOT EXISTS emr_security_events (
  id uuid PRIMARY KEY, doctor_id uuid REFERENCES emr_doctors,
  event_type text NOT NULL, occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_patients (
  id uuid PRIMARY KEY, reference text NOT NULL UNIQUE,
  synthetic boolean NOT NULL CHECK (synthetic = true)
);
CREATE TABLE IF NOT EXISTS emr_encounters (
  id uuid PRIMARY KEY, reference text NOT NULL UNIQUE,
  patient_id uuid NOT NULL REFERENCES emr_patients,
  encounter_date timestamptz NOT NULL, context text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_encounter_assignments (
  encounter_id uuid NOT NULL REFERENCES emr_encounters,
  doctor_id uuid NOT NULL REFERENCES emr_doctors,
  PRIMARY KEY(encounter_id,doctor_id)
);
CREATE TABLE IF NOT EXISTS emr_notes (
  id uuid PRIMARY KEY, encounter_id uuid NOT NULL REFERENCES emr_encounters,
  author_id uuid NOT NULL REFERENCES emr_doctors,
  current_version int NOT NULL CHECK(current_version > 0),
  status text NOT NULL CHECK(status IN ('Draft','Signed')),
  draft_text text NOT NULL CHECK(length(draft_text) BETWEEN 1 AND 65536),
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_note_versions (
  note_id uuid NOT NULL REFERENCES emr_notes, version int NOT NULL CHECK(version > 0),
  author_id uuid NOT NULL REFERENCES emr_doctors,
  encounter_id uuid NOT NULL REFERENCES emr_encounters,
  source_text text NOT NULL CHECK(length(source_text) BETWEEN 1 AND 65536),
  source_hash text NOT NULL CHECK(length(source_hash)=64),
  signed_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(note_id,version)
);
CREATE TABLE IF NOT EXISTS emr_attachments (
  id uuid PRIMARY KEY, note_id uuid NOT NULL REFERENCES emr_notes,
  file_name text NOT NULL, content bytea NOT NULL CHECK(octet_length(content) <= 20971520),
  content_hash text NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_version_attachments (
  note_id uuid NOT NULL, version int NOT NULL, attachment_id uuid NOT NULL REFERENCES emr_attachments,
  FOREIGN KEY(note_id,version) REFERENCES emr_note_versions,
  PRIMARY KEY(note_id,version,attachment_id)
);
CREATE TABLE IF NOT EXISTS emr_outbox_events (
  event_id uuid PRIMARY KEY, event_type text NOT NULL CHECK(event_type='ClinicalNoteSigned'),
  schema_version int NOT NULL CHECK(schema_version=1),
  note_id uuid NOT NULL, note_version int NOT NULL,
  correlation_id uuid NOT NULL UNIQUE, occurred_at timestamptz NOT NULL DEFAULT now(),
  publication_status text NOT NULL DEFAULT 'Pending Publication' CHECK(publication_status IN ('Pending Publication','Publishing','Published','Failed')),
  attempt_count int NOT NULL DEFAULT 0, next_retry_at timestamptz NOT NULL DEFAULT now(),
  lease_id uuid, lease_until timestamptz, last_error text, published_at timestamptz,
  processing_status text NOT NULL DEFAULT 'Pending Publication' CHECK(processing_status IN ('Pending Publication','Queued','Processing','Needs Review','Completed','Failed')),
  processing_attempts int NOT NULL DEFAULT 0, processing_error text,
  FOREIGN KEY(note_id,note_version) REFERENCES emr_note_versions,
  UNIQUE(note_id,note_version)
);
CREATE INDEX IF NOT EXISTS ix_emr_outbox_pending ON emr_outbox_events(next_retry_at) WHERE published_at IS NULL;
CREATE TABLE IF NOT EXISTS emr_processed_messages (
  event_id uuid PRIMARY KEY REFERENCES emr_outbox_events,
  completed_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS emr_extraction_links (
  note_id uuid NOT NULL, note_version int NOT NULL,
  event_id uuid NOT NULL UNIQUE REFERENCES emr_outbox_events,
  extraction_id uuid NOT NULL UNIQUE REFERENCES extraction_runs,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY(note_id,note_version) REFERENCES emr_note_versions,
  PRIMARY KEY(note_id,note_version)
);
CREATE OR REPLACE FUNCTION emr_reject_immutable_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'EMR audit snapshots are immutable' USING ERRCODE='23514'; END $$;
DO $$ DECLARE t text; BEGIN
  FOREACH t IN ARRAY ARRAY['emr_note_versions','emr_version_attachments','emr_attachments','emr_security_events','emr_processed_messages','emr_extraction_links'] LOOP
    IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgname=t||'_immutable' AND tgrelid=t::regclass) THEN
      EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION emr_reject_immutable_change()',t||'_immutable',t);
    END IF;
  END LOOP;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='cx_runtime') THEN
    GRANT SELECT,INSERT,UPDATE ON emr_doctors,emr_sessions,emr_notes,emr_outbox_events TO cx_runtime;
    GRANT SELECT ON emr_patients,emr_encounters,emr_encounter_assignments TO cx_runtime;
    GRANT SELECT,INSERT ON emr_note_versions,emr_version_attachments,emr_attachments,emr_security_events,emr_processed_messages,emr_extraction_links TO cx_runtime;
    REVOKE UPDATE,DELETE,TRUNCATE ON emr_note_versions,emr_version_attachments,emr_attachments,emr_security_events,emr_processed_messages,emr_extraction_links FROM cx_runtime;
  END IF;
END $$;
