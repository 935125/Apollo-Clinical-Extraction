-- =====================================================================================
-- V0004__semantic_vector_search_and_safety.sql — pgvector Semantic Search & Safety Alerts
-- Adds 64-dimensional clinical vector embeddings, HNSW cosine index, safety_alerts_json,
-- and active_learning_feedback table for bi-directional clinician feedback loops.
-- =====================================================================================

CREATE EXTENSION IF NOT EXISTS vector;

ALTER TABLE extraction_runs
    ADD COLUMN IF NOT EXISTS note_text TEXT NULL,
    ADD COLUMN IF NOT EXISTS note_embedding vector(64) NULL,
    ADD COLUMN IF NOT EXISTS safety_alerts_json JSONB NOT NULL DEFAULT '[]'::jsonb;

CREATE INDEX IF NOT EXISTS ix_extraction_runs_embedding_hnsw
    ON extraction_runs USING hnsw (note_embedding vector_cosine_ops);

CREATE TABLE IF NOT EXISTS active_learning_feedback (
    feedback_id        uuid        PRIMARY KEY,
    quarantine_id      uuid        NOT NULL REFERENCES oov_quarantine_items(quarantine_id) ON DELETE CASCADE,
    category           text        NOT NULL,
    mention            text        NOT NULL,
    normalized_mention text        NOT NULL,
    mapped_code        text        NOT NULL,
    mapped_display     text        NOT NULL,
    scrubbed_context   text        NOT NULL,
    prompt_exemplar    text        NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_active_learning_feedback_created_at
    ON active_learning_feedback (created_at DESC);

