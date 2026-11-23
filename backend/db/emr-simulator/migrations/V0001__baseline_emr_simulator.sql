-- =====================================================================================
-- V0001__baseline_emr_simulator.sql — EMR SIMULATOR database (development/test ONLY)
-- Simulates the hospital EMR's side of the integration: note versions written atomically with a
-- transactional outbox row. In production these objects belong to the hospital EMR team and
-- must be implemented to the contract in contracts/events/note-version-committed.v1.schema.json.
-- Status: DESIGN (Milestone 0); executed from Milestone 2.
-- =====================================================================================
CREATE SCHEMA IF NOT EXISTS emr;

CREATE TABLE emr.patient (
  patient_id   text PRIMARY KEY,                 -- synthetic UHID only
  display_name text NOT NULL,                    -- synthetic
  created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE emr.encounter (
  encounter_id   text PRIMARY KEY,
  patient_id     text NOT NULL REFERENCES emr.patient,
  facility_id    text NOT NULL,
  specialty_code text NULL,
  attending_user text NOT NULL,
  started_at     timestamptz NOT NULL
);

-- Simulated care-team relationship used by the simulated access-check API.
CREATE TABLE emr.encounter_care_team (
  encounter_id text NOT NULL REFERENCES emr.encounter,
  user_id      text NOT NULL,
  PRIMARY KEY (encounter_id, user_id)
);

CREATE TABLE emr.clinical_note (
  note_id         text PRIMARY KEY,
  encounter_id    text NOT NULL REFERENCES emr.encounter,
  note_type       text NOT NULL,
  current_version integer NOT NULL CHECK (current_version >= 1),
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE emr.clinical_note_version (
  note_id        text        NOT NULL REFERENCES emr.clinical_note,
  note_version   integer     NOT NULL CHECK (note_version >= 1),
  note_text      text        NOT NULL,
  content_sha256 bytea       NOT NULL CHECK (octet_length(content_sha256) = 32),
  authored_by    text        NOT NULL,
  committed_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (note_id, note_version)
);

-- Transactional outbox. Row inserted in the SAME transaction as clinical_note_version.
CREATE TABLE emr.extraction_outbox (
  outbox_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id         uuid        NOT NULL UNIQUE,
  event_type       text        NOT NULL,          -- 'note.version.committed'
  schema_version   text        NOT NULL,          -- '1.0'
  aggregate_id     text        NOT NULL,          -- note_id
  payload          jsonb       NOT NULL,          -- conforms to note-version-committed.v1 (no note text)
  correlation_id   text        NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  published_at     timestamptz NULL,
  publish_attempts integer     NOT NULL DEFAULT 0,
  last_error       text        NULL,
  next_attempt_at  timestamptz NOT NULL DEFAULT now()
);
-- Publisher polls unpublished rows in creation order.
CREATE INDEX ix_outbox_unpublished ON emr.extraction_outbox (next_attempt_at, outbox_id) WHERE published_at IS NULL;

-- Simulated order endpoint storage (unsigned orders) with idempotency.
CREATE TABLE emr.order_request (
  emr_order_id     text        PRIMARY KEY,
  idempotency_key  uuid        NOT NULL UNIQUE,
  encounter_id     text        NOT NULL REFERENCES emr.encounter,
  note_id          text        NOT NULL,
  expected_version integer     NOT NULL,
  order_type       text        NOT NULL CHECK (order_type IN ('medication','investigation')),
  master_code      text        NOT NULL,
  payload          jsonb       NOT NULL,
  status           text        NOT NULL CHECK (status IN ('unsigned','signed','cancelled')),
  requested_by     text        NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now()
);

