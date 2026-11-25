BEGIN READ ONLY;
SELECT jsonb_build_object('fingerprints',jsonb_build_object('extraction_runs',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM extraction_runs t),'extracted_medications',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM extracted_medications t),'extracted_investigations',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM extracted_investigations t),'emr_note_versions',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM emr_note_versions t),'emr_outbox_events',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM emr_outbox_events t),'emr_extraction_links',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM emr_extraction_links t),'emr_processed_messages',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\n' ORDER BY to_jsonb(t)::text))) FROM emr_processed_messages t)),
'amlVersions',(SELECT jsonb_agg(jsonb_build_object('noteId',v.note_id,'version',v.version,'sourceHash',v.source_hash,
 'source',v.source_text,'synthetic',p.synthetic,'eventId',o.event_id,'processingStatus',o.processing_status,
 'processingError',o.processing_error,'processingAttempts',o.processing_attempts,'publicationStatus',o.publication_status,
 'extractionId',l.extraction_id) ORDER BY v.version)
 FROM emr_note_versions v JOIN emr_encounters e ON e.id=v.encounter_id JOIN emr_patients p ON p.id=e.patient_id
 LEFT JOIN emr_outbox_events o ON o.note_id=v.note_id AND o.note_version=v.version
 LEFT JOIN emr_extraction_links l ON l.note_id=v.note_id AND l.note_version=v.version
 WHERE p.synthetic=true AND v.source_text ILIKE '%cytarabine%'));
COMMIT;
