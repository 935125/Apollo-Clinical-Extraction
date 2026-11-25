-- Read-only forensic inspection. No extraction dispatch, replay, or historical writes.
BEGIN READ ONLY;
SELECT jsonb_build_object(
  'noteId',v.note_id,'noteVersion',v.version,'patientReference',p.reference,
  'encounterReference',e.reference,'synthetic',p.synthetic,'sourceHash',v.source_hash,
  'note_text',v.source_text,'extractionId',l.extraction_id,
  'persistedRun',(SELECT to_jsonb(r) - 'note_embedding' FROM extraction_runs r WHERE r.extraction_id=l.extraction_id),
  'persistedMedications',(SELECT coalesce(jsonb_agg(to_jsonb(m)),'[]') FROM extracted_medications m WHERE m.extraction_id=l.extraction_id),
  'persistedInvestigations',(SELECT coalesce(jsonb_agg(to_jsonb(i)),'[]') FROM extracted_investigations i WHERE i.extraction_id=l.extraction_id)
)
FROM emr_note_versions v JOIN emr_encounters e ON e.id=v.encounter_id
JOIN emr_patients p ON p.id=e.patient_id
LEFT JOIN emr_extraction_links l ON l.note_id=v.note_id AND l.note_version=v.version
WHERE p.reference='P100234' AND e.reference='ENC-100567' AND v.version=33 AND p.synthetic=true;
COMMIT;
