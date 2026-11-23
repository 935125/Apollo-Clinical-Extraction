-- Read-only provenance and child-count verification. Does not return patient notes.
SELECT
    r.extraction_id,
    r.extracted_at,
    r.source_type,
    COALESCE(r.provider_metadata->>'provider', 'historical: not recorded') AS provider,
    COALESCE(r.provider_metadata->>'model', r.model_deployment) AS model,
    r.provider_metadata->>'azureDeployment' AS azure_deployment,
    r.provider_metadata->>'fallbackReason' AS fallback_reason,
    r.provider_metadata->>'inferenceExecuted' AS inference_executed,
    r.execution_duration_ms,
    r.prompt_tokens,
    r.completion_tokens,
    r.total_tokens,
    r.provider_metadata->'attempts' AS provider_attempts,
    (SELECT count(*) FROM public.extracted_medications m WHERE m.extraction_id = r.extraction_id) AS medications,
    (SELECT count(*) FROM public.extracted_investigations i WHERE i.extraction_id = r.extraction_id) AS investigations,
    (SELECT count(*) FROM public.oov_quarantine_items q WHERE q.extraction_id = r.extraction_id) AS quarantine_items
FROM public.extraction_runs r
ORDER BY r.extracted_at DESC
LIMIT 20;
