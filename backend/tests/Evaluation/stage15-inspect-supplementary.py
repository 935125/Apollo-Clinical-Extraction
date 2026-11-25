"""Read-only PostgreSQL inspection of saved synthetic Foundry runs and AML identity.
Never dispatches a provider request, signs a note, or updates an existing extraction.
"""
import json, re, subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
evidence=ROOT/'docs/stage15-evidence'
saved=json.loads((ROOT/'docs/foundry-live-10-evidence/live-foundry-10-results.json').read_text(encoding='utf-8-sig'))
ids=[c['extractionId'] for c in saved['cases'] if c.get('extractionId')]
assert len(ids)==9 and all(re.fullmatch(r'[0-9a-f-]{36}',i) for i in ids)
values=','.join("'"+i+"'::uuid" for i in ids)
sql=f"""BEGIN READ ONLY;
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
 WHERE r.extraction_id IN ({values}) AND p.synthetic=true),
 'applicationAdapterCounts',(SELECT jsonb_object_agg(adapter_type,n) FROM (SELECT adapter_type,count(*) n FROM extraction_runs GROUP BY adapter_type) q),
 'amlIdentity',(SELECT jsonb_build_object('sourceHash',v.source_hash,'noteVersion',v.version,'extractionId',l.extraction_id,'synthetic',p.synthetic)
 FROM emr_note_versions v JOIN emr_encounters e ON e.id=v.encounter_id JOIN emr_patients p ON p.id=e.patient_id
 JOIN emr_extraction_links l ON l.note_id=v.note_id AND l.note_version=v.version
 WHERE p.reference='P100234' AND e.reference='ENC-100567' AND v.version=33)
);
COMMIT;
"""
path=evidence/'evaluation/supplementary-readonly.sql'
path.write_text(sql,encoding='utf-8')
result=subprocess.run([str(ROOT/'backend/tools/pgsql/bin/psql.exe'),'-w','-qAt','-h','127.0.0.1','-U','postgres','-d','cx_platform','-f',str(path)],capture_output=True,text=True,check=True)
data=json.loads(result.stdout)
assert len(data['foundryRuns'])==9
baseline=json.loads((evidence/'baseline/aml-v33-historical.json').read_text(encoding='utf-8-sig'))
assert data['amlIdentity']['sourceHash']==baseline['sourceHash'] and data['amlIdentity']['extractionId']==baseline['extractionId']
(evidence/'evaluation/supplementary-persisted-inspection.json').write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')
print('Read-only inspection: nine saved synthetic Foundry runs; original AML source/extraction identity unchanged.')
print('Application adapter counts:',data['applicationAdapterCounts'])
