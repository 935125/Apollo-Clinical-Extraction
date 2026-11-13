-- =====================================================================================
-- V0001__baseline_platform.sql  — CX platform database (PostgreSQL 18)
-- AUTHORITATIVE schema definition. Described (not redefined) by docs/database/database-design.md
-- Status: DESIGN (Milestone 0). Not yet executed — no PostgreSQL instance available on the
--         development machine at time of writing. First execution + tests: Milestone 1/2.
-- Conventions:
--   * Keys: uuid generated with PostgreSQL 18 uuidv7() (time-ordered → index locality).
--   * Timestamps: timestamptz, UTC, column suffix _at.
--   * Enumerations: text + CHECK constraints mirroring contracts/enums.v1.json (ADR-0005).
--   * Optimistic concurrency: explicit row_version bigint, incremented by the writer.
--   * No ON DELETE CASCADE on clinical data; deletion only through retention procedures.
-- =====================================================================================

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE SCHEMA IF NOT EXISTS terminology;
CREATE SCHEMA IF NOT EXISTS cx;
CREATE SCHEMA IF NOT EXISTS audit;

-- -------------------------------------------------------------------------------------
-- Group roles (NOLOGIN). Login principals (Entra managed identities) are granted
-- membership by environment provisioning (infra), never by migrations.
-- -------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_worker_role')    THEN CREATE ROLE cx_worker_role NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_api_role')       THEN CREATE ROLE cx_api_role NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_writeback_role') THEN CREATE ROLE cx_writeback_role NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_readonly_role')  THEN CREATE ROLE cx_readonly_role NOLOGIN; END IF;
END $$;

-- =====================================================================================
-- TERMINOLOGY (versioned, immutable releases)
-- =====================================================================================
CREATE TABLE terminology.terminology_release (
  terminology_release_id uuid        PRIMARY KEY DEFAULT uuidv7(),
  release_label          text        NOT NULL UNIQUE,
  source_description     text        NOT NULL,
  content_sha256         bytea       NOT NULL CHECK (octet_length(content_sha256) = 32),
  status                 text        NOT NULL CHECK (status IN ('staged','active','retired')),
  loaded_at              timestamptz NOT NULL DEFAULT now(),
  activated_at           timestamptz NULL,
  retired_at             timestamptz NULL,
  loaded_by              text        NOT NULL
);
-- At most one active release at any time.
CREATE UNIQUE INDEX uq_terminology_release_single_active
  ON terminology.terminology_release ((status)) WHERE status = 'active';

CREATE TABLE terminology.drug_master (
  drug_master_id         uuid          PRIMARY KEY DEFAULT uuidv7(),
  terminology_release_id uuid          NOT NULL REFERENCES terminology.terminology_release,
  hospital_drug_code     text          NOT NULL,
  display_name           text          NOT NULL,
  generic_name           text          NOT NULL,
  brand_name             text          NULL,
  strength_text          text          NULL,          -- authoritative strength as in master (e.g. "500 mg/125 mg")
  strength_value         numeric(14,4) NULL,          -- single-ingredient numeric strength, if applicable
  strength_unit          text          NULL,
  formulation_code       text          NOT NULL,      -- e.g. TAB, CAP, SYR, INJ
  route_code             text          NOT NULL,      -- e.g. PO, IV, IM, TOP
  is_orderable           boolean       NOT NULL,
  is_active              boolean       NOT NULL,
  rxnorm_rxcui           text          NULL,          -- licence-dependent (FR-051)
  snomed_ct_id           text          NULL,          -- licence-dependent
  search_document        tsvector      GENERATED ALWAYS AS (
                            to_tsvector('simple', coalesce(display_name,'') || ' ' || coalesce(generic_name,'') || ' ' || coalesce(brand_name,''))
                          ) STORED,
  CONSTRAINT uq_drug_master_code UNIQUE (terminology_release_id, hospital_drug_code)
);
CREATE INDEX ix_drug_master_search ON terminology.drug_master USING gin (search_document);
CREATE INDEX ix_drug_master_trgm   ON terminology.drug_master USING gin (lower(generic_name) gin_trgm_ops);

CREATE TABLE terminology.drug_alias (
  drug_alias_id    uuid  PRIMARY KEY DEFAULT uuidv7(),
  drug_master_id   uuid  NOT NULL REFERENCES terminology.drug_master,
  alias_text       text  NOT NULL,
  alias_normalized text  NOT NULL,                    -- lower-case, NFC, whitespace-collapsed
  alias_type       text  NOT NULL CHECK (alias_type IN ('brand','abbreviation','synonym','misspelling','local')),
  CONSTRAINT uq_drug_alias UNIQUE (drug_master_id, alias_normalized)
);
CREATE INDEX ix_drug_alias_lookup ON terminology.drug_alias (alias_normalized);

CREATE TABLE terminology.investigation_master (
  investigation_master_id uuid     PRIMARY KEY DEFAULT uuidv7(),
  terminology_release_id  uuid     NOT NULL REFERENCES terminology.terminology_release,
  hospital_test_code      text     NOT NULL,
  display_name            text     NOT NULL,
  specimen_code           text     NULL,              -- e.g. SERUM, WB, URINE; distinguishes clinically distinct tests
  method_text             text     NULL,
  department_code         text     NOT NULL,          -- LAB, RAD, CARD, ...
  is_panel                boolean  NOT NULL DEFAULT false,
  is_orderable            boolean  NOT NULL,
  is_active               boolean  NOT NULL,
  loinc_code              text     NULL,              -- licence-dependent
  snomed_ct_id            text     NULL,
  search_document         tsvector GENERATED ALWAYS AS (to_tsvector('simple', coalesce(display_name,''))) STORED,
  CONSTRAINT uq_investigation_master_code UNIQUE (terminology_release_id, hospital_test_code)
);
CREATE INDEX ix_investigation_master_search ON terminology.investigation_master USING gin (search_document);
CREATE INDEX ix_investigation_master_trgm   ON terminology.investigation_master USING gin (lower(display_name) gin_trgm_ops);

CREATE TABLE terminology.investigation_alias (
  investigation_alias_id  uuid PRIMARY KEY DEFAULT uuidv7(),
  investigation_master_id uuid NOT NULL REFERENCES terminology.investigation_master,
  alias_text              text NOT NULL,
  alias_normalized        text NOT NULL,
  alias_type              text NOT NULL CHECK (alias_type IN ('abbreviation','synonym','misspelling','local')),
  CONSTRAINT uq_investigation_alias UNIQUE (investigation_master_id, alias_normalized)
);
CREATE INDEX ix_investigation_alias_lookup ON terminology.investigation_alias (alias_normalized);

-- =====================================================================================
-- NOTE REFERENCES (immutable note versions; EMR is source of truth)
-- =====================================================================================
CREATE TABLE cx.clinical_note_reference (
  note_reference_id  uuid        PRIMARY KEY DEFAULT uuidv7(),
  source_system      text        NOT NULL,
  patient_id         text        NOT NULL,            -- EMR patient identifier (e.g. UHID) — PHI identifier
  encounter_id       text        NOT NULL,
  note_id            text        NOT NULL,
  note_version       integer     NOT NULL CHECK (note_version >= 1),
  content_sha256     bytea       NOT NULL CHECK (octet_length(content_sha256) = 32),
  note_type          text        NOT NULL,
  specialty_code     text        NULL,
  facility_id        text        NOT NULL,
  authored_by        text        NULL,                -- EMR user id of note author
  note_committed_at  timestamptz NOT NULL,
  first_event_id     uuid        NOT NULL,
  received_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_note_reference_version UNIQUE (source_system, note_id, note_version)
);
CREATE INDEX ix_note_reference_encounter ON cx.clinical_note_reference (source_system, encounter_id, note_id, note_version DESC);
CREATE INDEX ix_note_reference_patient   ON cx.clinical_note_reference (source_system, patient_id, note_committed_at DESC);

-- Latest known version per note; monotonic (updated only to a higher version).
CREATE TABLE cx.clinical_note_head (
  source_system            text        NOT NULL,
  note_id                  text        NOT NULL,
  latest_version           integer     NOT NULL,
  latest_note_reference_id uuid        NOT NULL REFERENCES cx.clinical_note_reference,
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (source_system, note_id)
);

-- Text snapshot of the exact version processed (ADR-0007). Retention-limited.
CREATE TABLE cx.clinical_note_content (
  note_reference_id uuid        PRIMARY KEY REFERENCES cx.clinical_note_reference,
  note_text         text        NOT NULL,
  char_length       integer     NOT NULL CHECK (char_length >= 0),
  content_sha256    bytea       NOT NULL CHECK (octet_length(content_sha256) = 32),
  stored_at         timestamptz NOT NULL DEFAULT now(),
  purge_after       timestamptz NOT NULL
);
CREATE INDEX ix_note_content_purge ON cx.clinical_note_content (purge_after);

-- =====================================================================================
-- EXTRACTION RUNS & RESULTS
-- =====================================================================================
CREATE TABLE cx.extraction_run (
  extraction_run_id      uuid        PRIMARY KEY DEFAULT uuidv7(),
  note_reference_id      uuid        NOT NULL REFERENCES cx.clinical_note_reference,
  pipeline_profile       text        NOT NULL,
  trigger                text        NOT NULL CHECK (trigger IN ('realtime','backfill','reprocess')),
  -- sha256(source_system|note_id|note_version|pipeline_profile|scope); scope='auto' for realtime+backfill,
  -- 'reprocess:<request id>' for operator reprocess. Guarantees one automatic run per note version+profile.
  dedup_key              bytea       NOT NULL CHECK (octet_length(dedup_key) = 32),
  status                 text        NOT NULL CHECK (status IN ('received','processing','succeeded','failed_retryable','failed_terminal','superseded')),
  attempt_count          integer     NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
  lease_token            bigint      NOT NULL DEFAULT 0,   -- fencing token, incremented on every claim
  lease_owner            text        NULL,
  lease_expires_at       timestamptz NULL,
  next_attempt_at        timestamptz NULL,
  source_event_id        uuid        NOT NULL,
  correlation_id         text        NOT NULL,
  backfill_job_id        uuid        NULL,
  preprocessor_version   text        NULL,
  prompt_version         text        NULL,
  prompt_sha256          bytea       NULL,
  output_schema_version  text        NULL,
  model_deployment       text        NULL,
  model_version          text        NULL,
  terminology_release_id uuid        NULL REFERENCES terminology.terminology_release,
  error_code             text        NULL,
  error_detail           text        NULL,                 -- redacted; MUST NOT contain note text / PHI
  created_at             timestamptz NOT NULL DEFAULT now(),
  started_at             timestamptz NULL,
  completed_at           timestamptz NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_extraction_run_dedup UNIQUE (dedup_key),
  CONSTRAINT ck_extraction_run_lease CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL))
);
CREATE INDEX ix_extraction_run_note   ON cx.extraction_run (note_reference_id, created_at DESC);
CREATE INDEX ix_extraction_run_active ON cx.extraction_run (status, updated_at)
  WHERE status IN ('received','processing','failed_retryable');
CREATE INDEX ix_extraction_run_failed ON cx.extraction_run (completed_at DESC)
  WHERE status = 'failed_terminal';
CREATE INDEX ix_extraction_run_backfill ON cx.extraction_run (backfill_job_id, status)
  WHERE backfill_job_id IS NOT NULL;

CREATE TABLE cx.note_extraction (
  note_extraction_id     uuid        PRIMARY KEY DEFAULT uuidv7(),
  extraction_run_id      uuid        NOT NULL UNIQUE REFERENCES cx.extraction_run,
  note_reference_id      uuid        NOT NULL REFERENCES cx.clinical_note_reference,
  -- denormalised from note reference for index-only review queue queries (immutable copies)
  source_system          text        NOT NULL,
  patient_id             text        NOT NULL,
  encounter_id           text        NOT NULL,
  facility_id            text        NOT NULL,
  specialty_code         text        NULL,
  is_active              boolean     NOT NULL DEFAULT true,
  review_state           text        NOT NULL CHECK (review_state IN ('pending','in_review','completed','superseded','no_items')),
  medication_count       integer     NOT NULL CHECK (medication_count >= 0),
  investigation_count    integer     NOT NULL CHECK (investigation_count >= 0),
  pending_item_count     integer     NOT NULL CHECK (pending_item_count >= 0),
  structured_output      jsonb       NOT NULL,             -- validated llm-extraction-output.v1
  raw_model_output       jsonb       NULL,                 -- stored only if policy flag enabled
  validation_summary     jsonb       NOT NULL,
  input_tokens           integer     NULL,
  output_tokens          integer     NULL,
  llm_latency_ms         integer     NULL,
  model_response_id      text        NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  row_version            bigint      NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX uq_note_extraction_active ON cx.note_extraction (note_reference_id) WHERE is_active;
CREATE INDEX ix_note_extraction_review_queue ON cx.note_extraction (facility_id, created_at, note_extraction_id)
  INCLUDE (specialty_code, encounter_id, pending_item_count)
  WHERE is_active AND review_state IN ('pending','in_review');
CREATE INDEX ix_note_extraction_encounter ON cx.note_extraction (source_system, encounter_id, created_at DESC);

CREATE TABLE cx.note_medication (
  note_medication_id        uuid          PRIMARY KEY DEFAULT uuidv7(),
  note_extraction_id        uuid          NOT NULL REFERENCES cx.note_extraction,
  item_index                smallint      NOT NULL CHECK (item_index >= 0),
  -- extracted values (immutable after insert; corrections live in review_decision)
  name_text                 text          NOT NULL,
  strength_text             text          NULL,
  strength_value            numeric(14,4) NULL,
  strength_unit             text          NULL,
  dose_text                 text          NULL,
  dose_value                numeric(14,4) NULL,
  dose_unit                 text          NULL,
  route_text                text          NULL,
  route_code                text          NULL,
  frequency_text            text          NULL,
  frequency_code            text          NULL,
  duration_text             text          NULL,
  duration_value            numeric(10,2) NULL,
  duration_unit             text          NULL,
  formulation_text          text          NULL,
  formulation_code          text          NULL,
  instructions_text         text          NULL,
  assertion_status          text          NOT NULL CHECK (assertion_status IN ('new_order','current_medication','historical','discontinued','negated','suggested','conditional','uncertain')),
  evidence_text             text          NOT NULL,
  evidence_start            integer       NULL,           -- Unicode code-point offsets into clinical_note_content.note_text (ADR-0009)
  evidence_end              integer       NULL,
  evidence_verified         boolean       NOT NULL,
  section_code              text          NULL,
  normalization_status      text          NOT NULL CHECK (normalization_status IN ('matched','ambiguous','unmatched','not_attempted')),
  matched_drug_master_id    uuid          NULL REFERENCES terminology.drug_master,
  quality_score             numeric(5,4)  NULL CHECK (quality_score BETWEEN 0 AND 1),   -- heuristic, UNCALIBRATED
  quality_flags             text[]        NOT NULL DEFAULT '{}',
  validation_findings       jsonb         NOT NULL DEFAULT '[]',
  -- review state (mutable, API-owned)
  review_status             text          NOT NULL DEFAULT 'pending' CHECK (review_status IN ('pending','confirmed','corrected','rejected','superseded')),
  latest_review_decision_id uuid          NULL,            -- FK added after review_decision is created
  row_version               bigint        NOT NULL DEFAULT 1,
  created_at                timestamptz   NOT NULL DEFAULT now(),
  updated_at                timestamptz   NOT NULL DEFAULT now(),
  CONSTRAINT uq_note_medication_index UNIQUE (note_extraction_id, item_index),
  CONSTRAINT ck_note_medication_span CHECK (
    (evidence_start IS NULL AND evidence_end IS NULL) OR (evidence_start >= 0 AND evidence_end > evidence_start)),
  CONSTRAINT ck_note_medication_verified CHECK (NOT evidence_verified OR evidence_start IS NOT NULL),
  CONSTRAINT ck_note_medication_match CHECK ((normalization_status = 'matched') = (matched_drug_master_id IS NOT NULL))
);
CREATE INDEX ix_note_medication_master ON cx.note_medication (matched_drug_master_id) WHERE matched_drug_master_id IS NOT NULL;

CREATE TABLE cx.note_investigation (
  note_investigation_id       uuid         PRIMARY KEY DEFAULT uuidv7(),
  note_extraction_id          uuid         NOT NULL REFERENCES cx.note_extraction,
  item_index                  smallint     NOT NULL CHECK (item_index >= 0),
  name_text                   text         NOT NULL,
  specimen_text               text         NULL,
  specimen_code               text         NULL,
  timing_text                 text         NULL,
  assertion_status            text         NOT NULL CHECK (assertion_status IN ('new_order','historical','negated','suggested','conditional','uncertain')),
  evidence_text               text         NOT NULL,
  evidence_start              integer      NULL,
  evidence_end                integer      NULL,
  evidence_verified           boolean      NOT NULL,
  section_code                text         NULL,
  normalization_status        text         NOT NULL CHECK (normalization_status IN ('matched','ambiguous','unmatched','not_attempted')),
  matched_investigation_master_id uuid     NULL REFERENCES terminology.investigation_master,
  quality_score               numeric(5,4) NULL CHECK (quality_score BETWEEN 0 AND 1),
  quality_flags               text[]       NOT NULL DEFAULT '{}',
  validation_findings         jsonb        NOT NULL DEFAULT '[]',
  review_status               text         NOT NULL DEFAULT 'pending' CHECK (review_status IN ('pending','confirmed','corrected','rejected','superseded')),
  latest_review_decision_id   uuid         NULL,
  row_version                 bigint       NOT NULL DEFAULT 1,
  created_at                  timestamptz  NOT NULL DEFAULT now(),
  updated_at                  timestamptz  NOT NULL DEFAULT now(),
  CONSTRAINT uq_note_investigation_index UNIQUE (note_extraction_id, item_index),
  CONSTRAINT ck_note_investigation_span CHECK (
    (evidence_start IS NULL AND evidence_end IS NULL) OR (evidence_start >= 0 AND evidence_end > evidence_start)),
  CONSTRAINT ck_note_investigation_verified CHECK (NOT evidence_verified OR evidence_start IS NOT NULL),
  CONSTRAINT ck_note_investigation_match CHECK ((normalization_status = 'matched') = (matched_investigation_master_id IS NOT NULL))
);
CREATE INDEX ix_note_investigation_master ON cx.note_investigation (matched_investigation_master_id) WHERE matched_investigation_master_id IS NOT NULL;

CREATE TABLE cx.terminology_candidate (
  terminology_candidate_id uuid         PRIMARY KEY DEFAULT uuidv7(),
  note_medication_id       uuid         NULL REFERENCES cx.note_medication,
  note_investigation_id    uuid         NULL REFERENCES cx.note_investigation,
  drug_master_id           uuid         NULL REFERENCES terminology.drug_master,
  investigation_master_id  uuid         NULL REFERENCES terminology.investigation_master,
  match_method             text         NOT NULL CHECK (match_method IN ('exact_code','alias','keyword','vector')),
  retrieval_score          numeric(8,4) NULL,
  candidate_rank           smallint     NOT NULL CHECK (candidate_rank >= 1),
  attribute_check          text         NOT NULL CHECK (attribute_check IN ('compatible','incompatible','not_evaluable')),
  attribute_conflicts      text[]       NOT NULL DEFAULT '{}',
  is_auto_selected         boolean      NOT NULL DEFAULT false,
  created_at               timestamptz  NOT NULL DEFAULT now(),
  CONSTRAINT ck_candidate_one_entity CHECK (num_nonnulls(note_medication_id, note_investigation_id) = 1),
  CONSTRAINT ck_candidate_type_match CHECK (
    (note_medication_id IS NOT NULL AND drug_master_id IS NOT NULL AND investigation_master_id IS NULL) OR
    (note_investigation_id IS NOT NULL AND investigation_master_id IS NOT NULL AND drug_master_id IS NULL)),
  CONSTRAINT ck_candidate_auto_compatible CHECK (NOT is_auto_selected OR attribute_check = 'compatible')
);
CREATE INDEX ix_candidate_medication    ON cx.terminology_candidate (note_medication_id, candidate_rank)    WHERE note_medication_id IS NOT NULL;
CREATE INDEX ix_candidate_investigation ON cx.terminology_candidate (note_investigation_id, candidate_rank) WHERE note_investigation_id IS NOT NULL;

-- =====================================================================================
-- REVIEW (append-only decisions)
-- =====================================================================================
CREATE TABLE cx.review_decision (
  review_decision_id               uuid        PRIMARY KEY DEFAULT uuidv7(),
  note_extraction_id               uuid        NOT NULL REFERENCES cx.note_extraction,
  note_medication_id               uuid        NULL REFERENCES cx.note_medication,
  note_investigation_id            uuid        NULL REFERENCES cx.note_investigation,
  decision                         text        NOT NULL CHECK (decision IN ('confirm','correct','reject','select_alternative')),
  corrected_values                 jsonb       NULL,
  selected_drug_master_id          uuid        NULL REFERENCES terminology.drug_master,
  selected_investigation_master_id uuid        NULL REFERENCES terminology.investigation_master,
  reason_code                      text        NULL CHECK (reason_code IN ('not_in_note','wrong_assertion','wrong_attributes','duplicate','not_clinically_appropriate','other')),
  reason_text                      text        NULL CHECK (char_length(reason_text) <= 1000),
  reviewer_user_id                 text        NOT NULL,
  reviewer_role                    text        NOT NULL,
  note_version_at_decision         integer     NOT NULL,
  item_row_version_at_decision     bigint      NOT NULL,
  supersedes_review_decision_id    uuid        NULL REFERENCES cx.review_decision,
  correlation_id                   text        NOT NULL,
  decided_at                       timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_review_one_entity   CHECK (num_nonnulls(note_medication_id, note_investigation_id) = 1),
  CONSTRAINT ck_review_correct      CHECK (decision <> 'correct' OR corrected_values IS NOT NULL),
  CONSTRAINT ck_review_reject       CHECK (decision <> 'reject'  OR reason_code IS NOT NULL),
  CONSTRAINT ck_review_alternative  CHECK (decision <> 'select_alternative' OR
                                           num_nonnulls(selected_drug_master_id, selected_investigation_master_id) = 1),
  CONSTRAINT ck_review_master_type  CHECK (
    (selected_drug_master_id IS NULL OR note_medication_id IS NOT NULL) AND
    (selected_investigation_master_id IS NULL OR note_investigation_id IS NOT NULL))
);
CREATE INDEX ix_review_decision_medication    ON cx.review_decision (note_medication_id, decided_at DESC)    WHERE note_medication_id IS NOT NULL;
CREATE INDEX ix_review_decision_investigation ON cx.review_decision (note_investigation_id, decided_at DESC) WHERE note_investigation_id IS NOT NULL;
CREATE INDEX ix_review_decision_reviewer      ON cx.review_decision (reviewer_user_id, decided_at DESC);

ALTER TABLE cx.note_medication
  ADD CONSTRAINT fk_note_medication_latest_decision FOREIGN KEY (latest_review_decision_id) REFERENCES cx.review_decision;
ALTER TABLE cx.note_investigation
  ADD CONSTRAINT fk_note_investigation_latest_decision FOREIGN KEY (latest_review_decision_id) REFERENCES cx.review_decision;

-- =====================================================================================
-- EMR WRITE-BACK
-- =====================================================================================
CREATE TABLE cx.writeback_operation (
  writeback_operation_id uuid        PRIMARY KEY DEFAULT uuidv7(),
  client_idempotency_key text        NOT NULL CHECK (char_length(client_idempotency_key) BETWEEN 16 AND 128),
  requested_by           text        NOT NULL,
  request_fingerprint    bytea       NOT NULL CHECK (octet_length(request_fingerprint) = 32),
  note_extraction_id     uuid        NOT NULL REFERENCES cx.note_extraction,
  note_reference_id      uuid        NOT NULL REFERENCES cx.clinical_note_reference,
  source_system          text        NOT NULL,
  encounter_id           text        NOT NULL,
  expected_note_version  integer     NOT NULL,
  status                 text        NOT NULL CHECK (status IN ('pending','in_progress','completed','partially_failed','failed')),
  correlation_id         text        NOT NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  completed_at           timestamptz NULL,
  row_version            bigint      NOT NULL DEFAULT 1,
  CONSTRAINT uq_writeback_client_key UNIQUE (requested_by, client_idempotency_key)
);
CREATE INDEX ix_writeback_operation_extraction ON cx.writeback_operation (note_extraction_id, created_at DESC);

CREATE TABLE cx.writeback_item (
  writeback_item_id      uuid        PRIMARY KEY DEFAULT uuidv7(),
  writeback_operation_id uuid        NOT NULL REFERENCES cx.writeback_operation,
  review_decision_id     uuid        NOT NULL REFERENCES cx.review_decision,
  note_medication_id     uuid        NULL REFERENCES cx.note_medication,
  note_investigation_id  uuid        NULL REFERENCES cx.note_investigation,
  source_system          text        NOT NULL,
  encounter_id           text        NOT NULL,
  master_item_code       text        NOT NULL,          -- hospital_drug_code / hospital_test_code at submission
  emr_idempotency_key    uuid        NOT NULL UNIQUE,   -- sent to EMR; stable across all retries
  order_payload          jsonb       NOT NULL,          -- immutable snapshot of the approved order request
  status                 text        NOT NULL CHECK (status IN ('pending','submitting','succeeded','unknown_outcome','failed_retryable','failed_terminal','rejected_by_emr')),
  attempt_count          integer     NOT NULL DEFAULT 0,
  lease_token            bigint      NOT NULL DEFAULT 0,
  lease_owner            text        NULL,
  lease_expires_at       timestamptz NULL,
  next_attempt_at        timestamptz NULL,
  emr_order_id           text        NULL,
  emr_response_code      text        NULL,
  last_error_code        text        NULL,
  last_error_detail      text        NULL,              -- redacted
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  completed_at           timestamptz NULL,
  CONSTRAINT ck_writeback_item_one_entity CHECK (num_nonnulls(note_medication_id, note_investigation_id) = 1),
  CONSTRAINT ck_writeback_item_success    CHECK (status <> 'succeeded' OR emr_order_id IS NOT NULL)
);
-- An extracted item can have at most one live (not definitively failed) write-back.
CREATE UNIQUE INDEX uq_writeback_item_medication_live ON cx.writeback_item (note_medication_id)
  WHERE note_medication_id IS NOT NULL AND status NOT IN ('failed_terminal','rejected_by_emr');
CREATE UNIQUE INDEX uq_writeback_item_investigation_live ON cx.writeback_item (note_investigation_id)
  WHERE note_investigation_id IS NOT NULL AND status NOT IN ('failed_terminal','rejected_by_emr');
CREATE INDEX ix_writeback_item_due ON cx.writeback_item (next_attempt_at)
  WHERE status IN ('pending','failed_retryable','unknown_outcome');
-- Cross-extraction duplicate-order detection (same encounter + same master item).
CREATE INDEX ix_writeback_item_encounter_code ON cx.writeback_item (source_system, encounter_id, master_item_code)
  WHERE status IN ('pending','submitting','succeeded','unknown_outcome','failed_retryable');
CREATE INDEX ix_writeback_item_operation ON cx.writeback_item (writeback_operation_id);

-- =====================================================================================
-- HISTORICAL BACKFILL
-- =====================================================================================
CREATE TABLE cx.backfill_job (
  backfill_job_id      uuid        PRIMARY KEY DEFAULT uuidv7(),
  job_name             text        NOT NULL UNIQUE,
  source_description   text        NOT NULL,
  filter_criteria      jsonb       NOT NULL,           -- facility/date/specialty scope; validated by orchestrator
  pipeline_profile     text        NOT NULL,
  status               text        NOT NULL CHECK (status IN ('created','running','paused','completed','failed','cancelled')),
  batch_size           integer     NOT NULL CHECK (batch_size BETWEEN 1 AND 10000),
  max_notes_per_minute integer     NOT NULL CHECK (max_notes_per_minute > 0),
  cursor_position      jsonb       NULL,               -- last committed keyset cursor
  notes_enqueued       bigint      NOT NULL DEFAULT 0,
  notes_skipped        bigint      NOT NULL DEFAULT 0,
  created_by           text        NOT NULL,
  created_at           timestamptz NOT NULL DEFAULT now(),
  started_at           timestamptz NULL,
  completed_at         timestamptz NULL,
  last_heartbeat_at    timestamptz NULL,
  row_version          bigint      NOT NULL DEFAULT 1
);

CREATE TABLE cx.backfill_batch (
  backfill_job_id  uuid        NOT NULL REFERENCES cx.backfill_job,
  batch_number     integer     NOT NULL CHECK (batch_number >= 1),
  cursor_from      jsonb       NULL,
  cursor_to        jsonb       NOT NULL,
  enqueued_count   integer     NOT NULL,
  skipped_count    integer     NOT NULL,
  committed_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (backfill_job_id, batch_number)
);

ALTER TABLE cx.extraction_run
  ADD CONSTRAINT fk_extraction_run_backfill_job FOREIGN KEY (backfill_job_id) REFERENCES cx.backfill_job;

-- =====================================================================================
-- AUDIT (append-only, monthly range partitions — justified in database-design.md §7)
-- =====================================================================================
CREATE TABLE audit.audit_event (
  audit_event_id  uuid        NOT NULL DEFAULT uuidv7(),
  occurred_at     timestamptz NOT NULL DEFAULT now(),
  actor_type      text        NOT NULL CHECK (actor_type IN ('user','service','system')),
  actor_id        text        NOT NULL,
  actor_roles     text[]      NOT NULL DEFAULT '{}',
  action          text        NOT NULL,              -- e.g. note_source.viewed, review.decided, writeback.requested
  outcome         text        NOT NULL CHECK (outcome IN ('success','denied','failure')),
  resource_type   text        NOT NULL,
  resource_id     text        NOT NULL,
  source_system   text        NULL,
  patient_id      text        NULL,
  encounter_id    text        NULL,
  correlation_id  text        NOT NULL,
  client_address  inet        NULL,
  details         jsonb       NOT NULL DEFAULT '{}', -- MUST NOT contain note text or evidence quotes
  PRIMARY KEY (audit_event_id, occurred_at)
) PARTITION BY RANGE (occurred_at);
CREATE INDEX ix_audit_patient  ON audit.audit_event (source_system, patient_id, occurred_at DESC) WHERE patient_id IS NOT NULL;
CREATE INDEX ix_audit_actor    ON audit.audit_event (actor_id, occurred_at DESC);
CREATE INDEX ix_audit_resource ON audit.audit_event (resource_type, resource_id, occurred_at DESC);

-- Creates monthly partitions from the current month up to N months ahead (idempotent).
CREATE OR REPLACE FUNCTION audit.ensure_partitions(months_ahead integer DEFAULT 3)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  m date;
  part_name text;
BEGIN
  FOR i IN 0..months_ahead LOOP
    m := (date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => i))::date;
    part_name := format('audit_event_%s', to_char(m, 'YYYYMM'));
    IF to_regclass(format('audit.%I', part_name)) IS NULL THEN
      EXECUTE format(
        'CREATE TABLE audit.%I PARTITION OF audit.audit_event FOR VALUES FROM (%L) TO (%L)',
        part_name, m::timestamptz, (m + interval '1 month')::timestamptz);
    END IF;
  END LOOP;
END $$;

SELECT audit.ensure_partitions(3);

-- =====================================================================================
-- GRANTS (least privilege; see database-design.md §9)
-- =====================================================================================
GRANT USAGE ON SCHEMA cx, terminology, audit TO cx_worker_role, cx_api_role, cx_writeback_role, cx_readonly_role;

-- Worker: owns extraction write path
GRANT SELECT ON ALL TABLES IN SCHEMA terminology TO cx_worker_role;
GRANT SELECT, INSERT ON cx.clinical_note_reference, cx.clinical_note_content, cx.note_extraction,
                       cx.note_medication, cx.note_investigation, cx.terminology_candidate TO cx_worker_role;
GRANT SELECT, INSERT, UPDATE ON cx.clinical_note_head, cx.extraction_run, cx.backfill_job TO cx_worker_role;
GRANT INSERT, SELECT ON cx.backfill_batch TO cx_worker_role;
-- Worker may deactivate a superseded extraction (column-restricted)
GRANT UPDATE (is_active, review_state, updated_at, row_version) ON cx.note_extraction TO cx_worker_role;
GRANT UPDATE (review_status, updated_at, row_version) ON cx.note_medication, cx.note_investigation TO cx_worker_role;
GRANT INSERT ON audit.audit_event TO cx_worker_role;

-- Review API
GRANT SELECT ON ALL TABLES IN SCHEMA cx, terminology TO cx_api_role;
GRANT INSERT ON cx.review_decision, cx.writeback_operation, cx.writeback_item TO cx_api_role;
GRANT UPDATE (review_status, latest_review_decision_id, updated_at, row_version) ON cx.note_medication, cx.note_investigation TO cx_api_role;
GRANT UPDATE (review_state, pending_item_count, updated_at, row_version) ON cx.note_extraction TO cx_api_role;
GRANT UPDATE (status, next_attempt_at, attempt_count, updated_at) ON cx.extraction_run TO cx_api_role;  -- operator requeue
GRANT INSERT, UPDATE ON cx.backfill_job TO cx_api_role;
GRANT INSERT ON audit.audit_event TO cx_api_role;
GRANT SELECT ON audit.audit_event TO cx_api_role;   -- auditor endpoints (role-checked in API)

-- Write-back dispatcher
GRANT SELECT ON cx.writeback_operation, cx.writeback_item, cx.review_decision, cx.note_extraction,
                cx.clinical_note_reference, cx.clinical_note_head TO cx_writeback_role;
GRANT UPDATE (status, updated_at, completed_at, row_version) ON cx.writeback_operation TO cx_writeback_role;
GRANT UPDATE (status, attempt_count, lease_token, lease_owner, lease_expires_at, next_attempt_at, emr_order_id,
              emr_response_code, last_error_code, last_error_detail, updated_at, completed_at) ON cx.writeback_item TO cx_writeback_role;
GRANT INSERT ON audit.audit_event TO cx_writeback_role;

-- Read-only (analytics/evaluation within the platform; no note text)
GRANT SELECT ON ALL TABLES IN SCHEMA terminology TO cx_readonly_role;
GRANT SELECT ON cx.clinical_note_reference, cx.extraction_run, cx.note_extraction, cx.note_medication,
                cx.note_investigation, cx.terminology_candidate, cx.review_decision,
                cx.writeback_operation, cx.writeback_item TO cx_readonly_role;
-- Note: NO role receives UPDATE/DELETE on audit.audit_event or review_decision.
