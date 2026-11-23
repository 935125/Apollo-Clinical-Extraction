-- Read-only. Run as schema owner against the intended schema before V0008.
BEGIN READ ONLY;
SELECT current_database(), current_schema();
SELECT count(*) AS review_rows FROM entity_review_audit;
SELECT extraction_id, entity_type, entity_index, version_number, count(*) AS duplicates
FROM entity_review_audit GROUP BY extraction_id, entity_type, entity_index, version_number HAVING count(*) > 1;
SELECT count(*) AS invalid_review_keys FROM entity_review_audit
WHERE entity_type NOT IN ('MEDICATION', 'INVESTIGATION') OR entity_index < 0 OR version_number < 1;
SELECT count(*) AS orphan_reviews FROM entity_review_audit r
LEFT JOIN extraction_runs e ON e.extraction_id=r.extraction_id WHERE e.extraction_id IS NULL;
SELECT c.relname, pg_get_userbyid(c.relowner) AS owner, c.relacl
FROM pg_class c WHERE c.oid = to_regclass('entity_review_audit');
SELECT indexname, indexdef FROM pg_indexes WHERE schemaname=current_schema() AND tablename='entity_review_audit';
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid=to_regclass('entity_review_audit');
COMMIT;
