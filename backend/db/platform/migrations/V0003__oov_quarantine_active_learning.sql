-- =====================================================================================
-- V0003__oov_quarantine_active_learning.sql — Active Learning & OOV Quarantine Table
-- Stores low-confidence and unmapped RxNorm/LOINC clinical mentions for clinician review.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS oov_quarantine_items (
    quarantine_id      uuid             PRIMARY KEY,
    extraction_id      uuid             NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
    category           text             NOT NULL,
    mention            text             NOT NULL,
    normalized_mention text             NOT NULL,
    confidence         double precision NOT NULL DEFAULT 0,
    reason             text             NOT NULL DEFAULT 'fail_closed_oov',
    start_char         integer          NULL,
    end_char           integer          NULL,
    scrubbed_context   text             NOT NULL,
    review_status      text             NOT NULL DEFAULT 'pending' CHECK (review_status IN ('pending', 'approved', 'rejected')),
    mapped_code        text             NULL,
    mapped_display     text             NULL,
    reviewer_notes     text             NULL,
    created_at         timestamptz      NOT NULL DEFAULT now(),
    reviewed_at        timestamptz      NULL
);

CREATE INDEX IF NOT EXISTS ix_oov_quarantine_items_status
    ON oov_quarantine_items (review_status, created_at DESC);

