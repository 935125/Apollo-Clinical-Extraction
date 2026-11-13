"""
Idempotent Event Consumer for AI Extraction Worker.
Processes note-version-committed events and claims runs in PostgreSQL with fencing tokens.
"""

import hashlib
from dataclasses import dataclass
from typing import Any

import psycopg


@dataclass
class ProcessingResult:
    status: str  # 'succeeded', 'superseded', 'duplicate', 'failed_retryable', 'failed_terminal'
    run_id: str | None = None
    lease_token: int | None = None
    error: str | None = None


class IdempotentEventConsumer:
    def __init__(
        self, db_conn_string: str, pipeline_profile: str = "default", worker_id: str = "worker-1"
    ):
        self.db_conn_string = db_conn_string
        self.pipeline_profile = pipeline_profile
        self.worker_id = worker_id

    def _compute_dedup_key(self, source_system: str, note_id: str, note_version: int) -> bytes:
        raw = f"{source_system}|{note_id}|{note_version}|{self.pipeline_profile}|auto".encode()
        return hashlib.sha256(raw).digest()

    def process_event(self, event_dict: dict[str, Any]) -> ProcessingResult:
        """
        Processes a note-version-committed event idempotently.
        """
        required = [
            "eventId",
            "sourceSystem",
            "patientId",
            "encounterId",
            "noteId",
            "noteVersion",
            "contentSha256",
            "noteType",
            "facilityId",
            "committedAt",
        ]
        for field in required:
            if field not in event_dict:
                return ProcessingResult(
                    status="failed_terminal", error=f"POISON_MESSAGE: Missing field {field}"
                )

        source_system = event_dict["sourceSystem"]
        note_id = event_dict["noteId"]
        note_version = int(event_dict["noteVersion"])
        patient_id = event_dict["patientId"]
        encounter_id = event_dict["encounterId"]
        content_sha256 = bytes.fromhex(event_dict["contentSha256"])
        note_type = event_dict["noteType"]
        facility_id = event_dict["facilityId"]
        specialty_code = event_dict.get("specialtyCode")
        committed_at = event_dict["committedAt"]
        first_event_id = event_dict["eventId"]
        correlation_id = event_dict.get("correlationId", first_event_id)

        dedup_key = self._compute_dedup_key(source_system, note_id, note_version)

        with psycopg.connect(self.db_conn_string) as conn, conn.cursor() as cur:
            # 1. Upsert note reference
            cur.execute(
                """
                    INSERT INTO cx.clinical_note_reference (
                        source_system, patient_id, encounter_id, note_id, note_version,
                        content_sha256, note_type, specialty_code, facility_id,
                        note_committed_at, first_event_id
                    ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (source_system, note_id, note_version) DO NOTHING
                    RETURNING note_reference_id
                """,
                (
                    source_system,
                    patient_id,
                    encounter_id,
                    note_id,
                    note_version,
                    content_sha256,
                    note_type,
                    specialty_code,
                    facility_id,
                    committed_at,
                    first_event_id,
                ),
            )
            ref_row = cur.fetchone()

            if ref_row:
                note_reference_id = ref_row[0]
            else:
                cur.execute(
                    """
                        SELECT note_reference_id FROM cx.clinical_note_reference
                        WHERE source_system = %s AND note_id = %s AND note_version = %s
                    """,
                    (source_system, note_id, note_version),
                )
                ref_fetch = cur.fetchone()
                assert ref_fetch is not None
                note_reference_id = ref_fetch[0]

            # 2. Check head for monotonic version supersession
            cur.execute(
                """
                    SELECT latest_version FROM cx.clinical_note_head
                    WHERE source_system = %s AND note_id = %s
                """,
                (source_system, note_id),
            )
            head_row = cur.fetchone()

            if head_row and head_row[0] > note_version:
                cur.execute(
                    """
                        INSERT INTO cx.extraction_run (
                            note_reference_id, pipeline_profile, trigger, dedup_key,
                            status, source_event_id, correlation_id
                        ) VALUES (%s, %s, 'realtime', %s, 'superseded', %s, %s)
                        ON CONFLICT (dedup_key) DO UPDATE SET status = 'superseded'
                        RETURNING extraction_run_id
                    """,
                    (
                        note_reference_id,
                        self.pipeline_profile,
                        dedup_key,
                        first_event_id,
                        correlation_id,
                    ),
                )
                sup_row = cur.fetchone()
                assert sup_row is not None
                conn.commit()
                return ProcessingResult(status="superseded", run_id=str(sup_row[0]))

            cur.execute(
                """
                    INSERT INTO cx.clinical_note_head (source_system, note_id, latest_version, latest_note_reference_id)
                    VALUES (%s, %s, %s, %s)
                    ON CONFLICT (source_system, note_id) DO UPDATE
                    SET latest_version = EXCLUDED.latest_version,
                        latest_note_reference_id = EXCLUDED.latest_note_reference_id,
                        updated_at = now()
                    WHERE cx.clinical_note_head.latest_version < EXCLUDED.latest_version
                """,
                (source_system, note_id, note_version, note_reference_id),
            )

            # 3. Create or inspect extraction run
            cur.execute(
                """
                    INSERT INTO cx.extraction_run (
                        note_reference_id, pipeline_profile, trigger, dedup_key,
                        status, source_event_id, correlation_id
                    ) VALUES (%s, %s, 'realtime', %s, 'received', %s, %s)
                    ON CONFLICT (dedup_key) DO NOTHING
                    RETURNING extraction_run_id, status, lease_token
                """,
                (
                    note_reference_id,
                    self.pipeline_profile,
                    dedup_key,
                    first_event_id,
                    correlation_id,
                ),
            )
            run_row = cur.fetchone()

            if not run_row:
                cur.execute(
                    """
                        SELECT extraction_run_id, status, lease_token FROM cx.extraction_run
                        WHERE dedup_key = %s
                    """,
                    (dedup_key,),
                )
                existing_run = cur.fetchone()
                assert existing_run is not None
                run_id, status, _token = existing_run
                if status == "succeeded":
                    conn.commit()
                    return ProcessingResult(status="duplicate", run_id=str(run_id))
            else:
                run_id, status, _token = run_row

            # 4. Claim lease with fencing token increment
            cur.execute(
                """
                    UPDATE cx.extraction_run
                    SET status = 'processing',
                        lease_token = lease_token + 1,
                        lease_owner = %s,
                        lease_expires_at = now() + interval '5 minutes',
                        started_at = now(),
                        attempt_count = attempt_count + 1
                    WHERE extraction_run_id = %s
                      AND (status IN ('received', 'failed_retryable') 
                           OR (status = 'processing' AND lease_expires_at < now()))
                    RETURNING lease_token
                """,
                (self.worker_id, run_id),
            )
            claim_res = cur.fetchone()

            if not claim_res:
                conn.commit()
                return ProcessingResult(
                    status="duplicate",
                    run_id=str(run_id),
                    error="Already leased by another worker",
                )

            new_token = claim_res[0]
            conn.commit()
            return ProcessingResult(status="processing", run_id=str(run_id), lease_token=new_token)

    def commit_results(
        self, run_id: str, lease_token: int, structured_output: dict[str, Any]
    ) -> bool:
        """
        Commits extraction run results with ATOMIC FENCING TOKEN CHECK.
        If the worker's lease expired and another worker bumped lease_token, this update updates 0 rows and returns False.
        """
        with psycopg.connect(self.db_conn_string) as conn, conn.cursor() as cur:
            cur.execute(
                """
                    UPDATE cx.extraction_run
                    SET status = 'succeeded',
                        completed_at = now(),
                        lease_owner = NULL,
                        lease_expires_at = NULL
                    WHERE extraction_run_id = %s
                      AND lease_token = %s
                      AND status = 'processing'
                    RETURNING extraction_run_id;
                """,
                (run_id, lease_token),
            )
            row = cur.fetchone()
            if not row:
                conn.rollback()
                return False
            conn.commit()
            return True
