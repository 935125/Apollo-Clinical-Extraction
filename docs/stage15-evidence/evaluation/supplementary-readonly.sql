BEGIN READ ONLY;
SELECT jsonb_build_object(
 'scope','Read-only saved synthetic supplementary comparison; not ground truth',
 'foundryRuns',(SELECT jsonb_agg(jsonb_build_object(
    'extractionId',r.extraction_id,'noteId',l.note_id,'noteVersion',l.note_version,
    'patient',p.reference,'synthetic',p.synthetic,'adapter',r.adapter_type,
    'providerMetadata',r.provider_metadata,
    'medications',(SELECT count(*) FROM extracted_medications m WHERE m.extraction_id=r.extraction_id),
    'investigations',(SELECT count(*) FROM extracted_investigations i WHERE i.extraction_id=r.extraction_id),
    'invalidMedicationEvidence',(SELECT count(*) FROM extracted_medications m WHERE m.extraction_id=r.extraction_id AND (m.evidence_start IS NULL OR substring(r.note_text from m.evidence_start+1 for m.evidence_end-m.evidence_start) IS DISTINCT FROM m.evidence_quote)),
    'invalidInvestigationEvidence',(SELECT count(*) FROM extracted_investigations i WHERE i.extraction_id=r.extraction_id AND (i.evidence_start IS NULL OR substring(r.note_text from i.evidence_start+1 for i.evidence_end-i.evidence_start) IS DISTINCT FROM i.evidence_quote))
 )) FROM extraction_runs r JOIN emr_extraction_links l ON l.extraction_id=r.extraction_id
 JOIN emr_note_versions v ON v.note_id=l.note_id AND v.version=l.note_version
 JOIN emr_encounters e ON e.id=v.encounter_id JOIN emr_patients p ON p.id=e.patient_id
 WHERE r.extraction_id IN ('67e7b3d3-0141-4c6e-8c8c-5d8134d70137'::uuid,'82f08d30-a029-417a-a5cd-7bc20636cd62'::uuid,'5e6984a3-375e-4d03-8b4d-2f0a9f643ee1'::uuid,'48dc9ed4-47a5-4286-9bba-6a67ada136a5'::uuid,'58c4721d-7f76-48b5-b400-5c1bd8012c7c'::uuid,'c1cb5a61-5f02-4a93-b464-ed2927ec454c'::uuid,'b885cff2-dd0a-4cce-a6b3-3b01925ee3a6'::uuid,'47304742-1d92-4095-a95f-daaf34918807'::uuid,'0ab4f6f8-4484-431d-be47-7c244a9f1524'::uuid) AND p.synthetic=true),
 'applicationAdapterCounts',(SELECT jsonb_object_agg(adapter_type,n) FROM (SELECT adapter_type,count(*) n FROM extraction_runs GROUP BY adapter_type) q),
 'amlIdentity',(SELECT jsonb_build_object('sourceHash',v.source_hash,'noteVersion',v.version,'extractionId',l.extraction_id,'synthetic',p.synthetic)
 FROM emr_note_versions v JOIN emr_encounters e ON e.id=v.encounter_id JOIN emr_patients p ON p.id=e.patient_id
 JOIN emr_extraction_links l ON l.note_id=v.note_id AND l.note_version=v.version
 WHERE p.reference='P100234' AND e.reference='ENC-100567' AND v.version=33)
);
COMMIT;
