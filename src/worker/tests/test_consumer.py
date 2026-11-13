import uuid
from datetime import UTC, datetime

import psycopg
import pytest

from cx_worker.consumer import IdempotentEventConsumer

CX_DB_CONN = "host=localhost port=5432 dbname=cx_platform user=postgres password="


@pytest.fixture(autouse=True)
def clean_cx_db():
    with psycopg.connect(CX_DB_CONN) as conn:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM cx.note_medication;")
            cur.execute("DELETE FROM cx.note_investigation;")
            cur.execute("DELETE FROM cx.note_extraction;")
            cur.execute("DELETE FROM cx.extraction_run;")
            cur.execute("DELETE FROM cx.clinical_note_head;")
            cur.execute("DELETE FROM cx.clinical_note_reference;")
        conn.commit()


def make_test_event(note_id: str, version: int = 1) -> dict:
    return {
        "eventId": str(uuid.uuid4()),
        "eventType": "note.version.committed",
        "schemaVersion": "1.0",
        "occurredAt": datetime.now(UTC).isoformat(),
        "correlationId": str(uuid.uuid4()),
        "sourceSystem": "EMR_HOSP_MAIN",
        "patientId": "P_1001",
        "encounterId": "ENC_2001",
        "noteId": note_id,
        "noteVersion": version,
        "contentSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        "charLength": 50,
        "noteType": "OP_CONSULT",
        "specialtyCode": "CARDIOLOGY",
        "facilityId": "FAC_MAIN",
        "authoredBy": "DOC_TEST",
        "committedAt": datetime.now(UTC).isoformat(),
    }


def test_first_delivery_creates_and_claims_run():
    consumer = IdempotentEventConsumer(CX_DB_CONN)
    event = make_test_event("note-alpha", 1)

    result = consumer.process_event(event)
    assert result.status == "processing"
    assert result.run_id is not None
    assert result.lease_token == 1


def test_duplicate_delivery_of_succeeded_event_is_safely_ignored():
    consumer = IdempotentEventConsumer(CX_DB_CONN)
    event = make_test_event("note-beta", 1)

    res1 = consumer.process_event(event)
    assert res1.status == "processing"

    with psycopg.connect(CX_DB_CONN) as conn:
        with conn.cursor() as cur:
            cur.execute(
                "UPDATE cx.extraction_run SET status = 'succeeded' WHERE extraction_run_id = %s",
                (res1.run_id,),
            )
        conn.commit()

    res2 = consumer.process_event(event)
    assert res2.status == "duplicate"
    assert res2.run_id == res1.run_id


def test_out_of_order_stale_version_is_marked_superseded():
    consumer = IdempotentEventConsumer(CX_DB_CONN)

    v2_event = make_test_event("note-gamma", version=2)
    res_v2 = consumer.process_event(v2_event)
    assert res_v2.status == "processing"

    v1_event = make_test_event("note-gamma", version=1)
    res_v1 = consumer.process_event(v1_event)
    assert res_v1.status == "superseded"


def test_poison_message_missing_required_fields_fails_terminally():
    consumer = IdempotentEventConsumer(CX_DB_CONN)
    poison_event = {"eventId": str(uuid.uuid4()), "invalid_field": "corrupted payload"}

    result = consumer.process_event(poison_event)
    assert result.status == "failed_terminal"
    assert "POISON_MESSAGE" in (result.error or "")


def test_reliability2_lease_expiration_and_fencing_token_blocks_stale_worker_commit():
    """
    Mandatory Reliability Verification A.2:
    Worker A claims lease (token 1).
    Worker A becomes unresponsive and its lease expires.
    Worker B acquires a new lease (token 2).
    Worker A awakens and attempts to commit using token 1.
    Database fencing check MUST reject Worker A's write.
    Worker B commits using token 2 successfully.
    """
    consumer_a = IdempotentEventConsumer(CX_DB_CONN, worker_id="worker-A")
    consumer_b = IdempotentEventConsumer(CX_DB_CONN, worker_id="worker-B")

    event = make_test_event("note-fencing-test", 1)

    # 1. Worker A claims the event
    res_a = consumer_a.process_event(event)
    assert res_a.status == "processing"
    assert res_a.lease_token == 1
    run_id = res_a.run_id

    # 2. Simulate Worker A lease expiring in database
    with psycopg.connect(CX_DB_CONN) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE cx.extraction_run 
                SET lease_expires_at = now() - interval '1 second'
                WHERE extraction_run_id = %s
            """,
                (run_id,),
            )
        conn.commit()

    # 3. Worker B claims the expired lease -> token increments to 2
    res_b = consumer_b.process_event(event)
    assert res_b.status == "processing"
    assert res_b.lease_token == 2
    assert res_b.run_id == run_id

    # 4. Worker A awakens and attempts to commit using stale token 1 -> MUST BE REJECTED!
    commit_a_success = consumer_a.commit_results(
        run_id, lease_token=res_a.lease_token, structured_output={}
    )
    assert commit_a_success is False, (
        "Stale worker commit with superseded fencing token must return False!"
    )

    # 5. Worker B commits using valid token 2 -> MUST SUCCEED!
    commit_b_success = consumer_b.commit_results(
        run_id, lease_token=res_b.lease_token, structured_output={}
    )
    assert commit_b_success is True, (
        "Authorized lease holder with current fencing token must commit successfully!"
    )

    # Verify database final state is succeeded
    with psycopg.connect(CX_DB_CONN) as conn, conn.cursor() as cur:
        cur.execute(
            "SELECT status, lease_token FROM cx.extraction_run WHERE extraction_run_id = %s",
            (run_id,),
        )
        final_status, final_token = cur.fetchone()
        assert final_status == "succeeded"
        assert final_token == 2
