-- =====================================================================================
-- V0002__outbox_leasing.sql — Emr Simulator Outbox Lease & Claim State
-- Purpose: Permits publisher workers to claim a batch of events with a lease token,
-- commit the claim, and perform broker network calls OUTSIDE long-running DB transactions.
-- Prevents long-running row locks on emr.extraction_outbox.
-- =====================================================================================

ALTER TABLE emr.extraction_outbox
  ADD COLUMN IF NOT EXISTS lease_token bigint NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS lease_owner text NULL,
  ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_outbox_claimable ON emr.extraction_outbox (next_attempt_at, outbox_id)
  WHERE published_at IS NULL;

