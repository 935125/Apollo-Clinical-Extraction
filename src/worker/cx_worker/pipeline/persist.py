"""
Stage 5: Score and Persist Pipeline Stage.
Persists structured extractions, validation findings, candidates, and raw note content
with proper lease and fencing token verification before committing.
"""

import json

import psycopg

from cx_worker.pipeline.normalize import ITerminologyNormalizer, NormalizationResult
from cx_worker.pipeline.validate import ValidationSummary


class ExtractionPersister:
    def __init__(self, db_conn_string: str):
        self.db_conn_string = db_conn_string

    def persist_extraction(
        self,
        run_id: str,
        lease_token: int,
        note_reference_id: str,
        source_system: str,
        patient_id: str,
        encounter_id: str,
        facility_id: str,
        validation: ValidationSummary,
        normalizer: ITerminologyNormalizer,
        model_deployment: str = "gpt-4o-clinical",
        prompt_version: str = "v1.0",
    ) -> bool:
        """
        Persists extracted entities atomically.
        Fencing check: Verifies lease_token on extraction_run before committing results.
        """
        with psycopg.connect(self.db_conn_string) as conn, conn.cursor() as cur:
            # 1. ATOMIC FENCING CHECK: Is this worker still the authorized lease holder?
            cur.execute(
                """
                    SELECT status, lease_token FROM cx.extraction_run
                    WHERE extraction_run_id = %s
                      AND lease_token = %s
                      AND status = 'processing'
                    FOR UPDATE;
                """,
                (run_id, lease_token),
            )
            row = cur.fetchone()
            if not row:
                conn.rollback()
                return False  # Stale lease holder rejected

            med_count = len(validation.medications)
            inv_count = len(validation.investigations)
            pending_count = med_count + inv_count

            # 2. Insert cx.note_extraction
            cur.execute(
                """
                    INSERT INTO cx.note_extraction (
                        extraction_run_id, note_reference_id, source_system,
                        patient_id, encounter_id, facility_id, is_active,
                        review_state, medication_count, investigation_count,
                        pending_item_count, structured_output, validation_summary
                    ) VALUES (
                        %s, %s, %s, %s, %s, %s, true,
                        'pending', %s, %s, %s, %s::jsonb, %s::jsonb
                    ) RETURNING note_extraction_id;
                """,
                (
                    run_id,
                    note_reference_id,
                    source_system,
                    patient_id,
                    encounter_id,
                    facility_id,
                    med_count,
                    inv_count,
                    pending_count,
                    json.dumps(
                        {
                            "medications": [m.item.model_dump() for m in validation.medications],
                            "investigations": [
                                i.item.model_dump() for i in validation.investigations
                            ],
                        }
                    ),
                    json.dumps({"has_errors": validation.has_errors}),
                ),
            )
            ext_row = cur.fetchone()
            assert ext_row is not None
            note_extraction_id = ext_row[0]

            # 3. Insert note_medication entities
            for idx, v_med in enumerate(validation.medications):
                norm: NormalizationResult = normalizer.normalize_medication(
                    v_med.item.name,
                    v_med.item.strength,
                    v_med.item.formulation,
                    v_med.item.route,
                )

                cur.execute(
                    """
                        INSERT INTO cx.note_medication (
                            note_extraction_id, item_index, name_text, strength_text,
                            dose_text, route_code, frequency_text, duration_text,
                            formulation_code, instructions_text, assertion_status,
                            evidence_text, evidence_start, evidence_end, evidence_verified,
                            normalization_status, quality_flags, validation_findings,
                            review_status
                        ) VALUES (
                            %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, 'pending'
                        );
                    """,
                    (
                        note_extraction_id,
                        idx,
                        v_med.item.name,
                        v_med.item.strength,
                        v_med.item.dose,
                        v_med.item.route,
                        v_med.item.frequency,
                        v_med.item.duration,
                        v_med.item.formulation,
                        v_med.item.instructions,
                        v_med.item.assertion_status,
                        v_med.evidence.quote,
                        v_med.evidence.start,
                        v_med.evidence.end,
                        v_med.evidence.verified,
                        norm.status,
                        v_med.quality_flags,
                        json.dumps(
                            [
                                {"code": f.code, "severity": f.severity, "message": f.message}
                                for f in v_med.findings
                            ]
                        ),
                    ),
                )

            # 4. Insert note_investigation entities
            for idx, v_inv in enumerate(validation.investigations):
                norm = normalizer.normalize_investigation(v_inv.item.name, v_inv.item.specimen)

                cur.execute(
                    """
                        INSERT INTO cx.note_investigation (
                            note_extraction_id, item_index, name_text, specimen_text,
                            timing_text, assertion_status, evidence_text,
                            evidence_start, evidence_end, evidence_verified,
                            normalization_status, quality_flags, validation_findings,
                            review_status
                        ) VALUES (
                            %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, 'pending'
                        );
                    """,
                    (
                        note_extraction_id,
                        idx,
                        v_inv.item.name,
                        v_inv.item.specimen,
                        v_inv.item.timing,
                        v_inv.item.assertion_status,
                        v_inv.evidence.quote,
                        v_inv.evidence.start,
                        v_inv.evidence.end,
                        v_inv.evidence.verified,
                        norm.status,
                        v_inv.quality_flags,
                        json.dumps(
                            [
                                {"code": f.code, "severity": f.severity, "message": f.message}
                                for f in v_inv.findings
                            ]
                        ),
                    ),
                )

            # 5. Mark extraction_run as succeeded
            cur.execute(
                """
                    UPDATE cx.extraction_run
                    SET status = 'succeeded',
                        completed_at = now(),
                        model_deployment = %s,
                        prompt_version = %s,
                        lease_owner = NULL,
                        lease_expires_at = NULL
                    WHERE extraction_run_id = %s
                      AND lease_token = %s;
                """,
                (model_deployment, prompt_version, run_id, lease_token),
            )

            conn.commit()
            return True
