"""Read-only local PostgreSQL forensic snapshot; never invokes a provider or broker."""
import json, subprocess, sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
E=ROOT/'docs/validator-fix-evidence'
phase=sys.argv[1] if len(sys.argv)>1 else 'before'
assert phase in ('before','after')
destination=E/f'{phase}-database.json'
if phase=='before' and destination.exists(): raise RuntimeError('Refusing to overwrite baseline')
tables=['extraction_runs','extracted_medications','extracted_investigations','emr_note_versions',
        'emr_outbox_events','emr_extraction_links','emr_processed_messages']
fingerprints=','.join("'"+t+"',(SELECT jsonb_build_object('rows',count(*),'fingerprint',md5(string_agg(to_jsonb(t)::text,E'\\n' ORDER BY to_jsonb(t)::text))) FROM "+t+" t)" for t in tables)
sql="""BEGIN READ ONLY;
SELECT jsonb_build_object('fingerprints',jsonb_build_object(%s),
'amlVersions',(SELECT jsonb_agg(jsonb_build_object('noteId',v.note_id,'version',v.version,'sourceHash',v.source_hash,
 'source',v.source_text,'synthetic',p.synthetic,'eventId',o.event_id,'processingStatus',o.processing_status,
 'processingError',o.processing_error,'processingAttempts',o.processing_attempts,'publicationStatus',o.publication_status,
 'extractionId',l.extraction_id) ORDER BY v.version)
 FROM emr_note_versions v JOIN emr_encounters e ON e.id=v.encounter_id JOIN emr_patients p ON p.id=e.patient_id
 LEFT JOIN emr_outbox_events o ON o.note_id=v.note_id AND o.note_version=v.version
 LEFT JOIN emr_extraction_links l ON l.note_id=v.note_id AND l.note_version=v.version
 WHERE p.synthetic=true AND v.source_text ILIKE '%%cytarabine%%'));
COMMIT;
"""%fingerprints
query=E/f'{phase}-readonly.sql';query.write_text(sql,encoding='utf-8')
result=subprocess.run([str(ROOT/'backend/tools/pgsql/bin/psql.exe'),'-w','-qAt','-h','127.0.0.1','-U','postgres','-d','cx_platform','-f',str(query)],capture_output=True,text=True,check=True)
data=json.loads(result.stdout);destination.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')
print('Read-only application snapshot:', {k:v['rows'] for k,v in data['fingerprints'].items()})
print('Synthetic AML versions:',[{k:v for k,v in row.items() if k not in ('source','noteId','eventId')} for row in data['amlVersions'] or []])
if phase=='after':
 before=json.loads((E/'before-database.json').read_text())
 assert data==before, 'Application rows or original AML state changed'
 print('PASS: complete saved extraction, entity, immutable note, outbox/link and processed-message fingerprints unchanged.')
