using System.Data;
using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Apollo.Backend.Storage;

public class SqlExtractionRepository : IExtractionRepository
{
    private readonly string? _connectionString;
    private readonly InMemoryExtractionRepository _fallbackStore = new();
    private readonly ILogger<SqlExtractionRepository> _logger;
    private readonly bool _autoInitializeSchema;
    private volatile bool _schemaInitialized;

    public const string SchemaDdl = """
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS extraction_runs (
            extraction_id          uuid             PRIMARY KEY,
            correlation_id        text             NOT NULL,
            patient_id            text             NULL,
            encounter_id          text             NULL,
            note_text             text             NULL,
            source_type           text             NOT NULL DEFAULT 'text',
            source_file_name      text             NULL,
            saved_at              timestamptz      NULL,
            char_length           integer          NOT NULL DEFAULT 0,
            schema_version        text             NOT NULL DEFAULT '1.0.0',
            extracted_at          timestamptz      NOT NULL DEFAULT now(),
            execution_duration_ms double precision NOT NULL DEFAULT 0,
            adapter_type          text             NOT NULL,
            model_deployment      text             NOT NULL,
            prompt_version        text             NOT NULL,
            prompt_tokens         integer          NULL,
            completion_tokens     integer          NULL,
            total_tokens          integer          NULL,
            estimated_cost_usd    double precision NULL,
            has_errors            boolean          NOT NULL DEFAULT false,
            error_count           integer          NOT NULL DEFAULT 0,
            warning_count         integer          NOT NULL DEFAULT 0,
            sections_json         jsonb            NOT NULL DEFAULT '[]'::jsonb,
            safety_alerts_json    jsonb            NOT NULL DEFAULT '[]'::jsonb,
            note_embedding        vector(64)       NULL
        );

        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS note_text text NULL;
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS source_type text NOT NULL DEFAULT 'text';
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS source_file_name text NULL;
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS saved_at timestamptz NULL;
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS safety_alerts_json jsonb NOT NULL DEFAULT '[]'::jsonb;
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS note_embedding vector(64) NULL;

        CREATE INDEX IF NOT EXISTS ix_extraction_runs_extracted_at
            ON extraction_runs (extracted_at DESC);

        CREATE INDEX IF NOT EXISTS ix_extraction_runs_embedding_hnsw
            ON extraction_runs USING hnsw (note_embedding vector_cosine_ops);

        CREATE TABLE IF NOT EXISTS extracted_medications (
            medication_id         uuid             PRIMARY KEY,
            extraction_id         uuid             NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
            name                  text             NOT NULL,
            strength              text             NULL,
            dose                  text             NULL,
            route                 text             NULL,
            frequency             text             NULL,
            duration              text             NULL,
            formulation           text             NULL,
            instructions          text             NULL,
            assertion_status      text             NOT NULL,
            evidence_quote        text             NOT NULL,
            evidence_verified     boolean          NOT NULL DEFAULT true,
            evidence_start        integer          NULL,
            evidence_end          integer          NULL,
            normalization_status  text             NOT NULL,
            matched_code          text             NULL,
            display_name          text             NULL,
            confidence            double precision NOT NULL DEFAULT 0,
            quality_flags_json    jsonb            NOT NULL DEFAULT '[]'::jsonb,
            findings_json         jsonb            NOT NULL DEFAULT '[]'::jsonb
        );

        CREATE INDEX IF NOT EXISTS ix_extracted_medications_extraction_id
            ON extracted_medications (extraction_id);

        CREATE TABLE IF NOT EXISTS extracted_investigations (
            investigation_id      uuid             PRIMARY KEY,
            extraction_id         uuid             NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
            name                  text             NOT NULL,
            timing                text             NULL,
            specimen              text             NULL,
            assertion_status      text             NOT NULL,
            status                text             NOT NULL DEFAULT 'preliminary',
            evidence_quote        text             NOT NULL,
            evidence_verified     boolean          NOT NULL DEFAULT true,
            evidence_start        integer          NULL,
            evidence_end          integer          NULL,
            normalization_status  text             NOT NULL,
            matched_code          text             NULL,
            display_name          text             NULL,
            confidence            double precision NOT NULL DEFAULT 0,
            quality_flags_json    jsonb            NOT NULL DEFAULT '[]'::jsonb,
            findings_json         jsonb            NOT NULL DEFAULT '[]'::jsonb
        );

        CREATE INDEX IF NOT EXISTS ix_extracted_investigations_extraction_id
            ON extracted_investigations (extraction_id);

        CREATE TABLE IF NOT EXISTS oov_quarantine_items (
            quarantine_id      uuid             PRIMARY KEY,
            extraction_id      uuid             NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
            category           text             NOT NULL,
            mention            text             NOT NULL,
            normalized_mention text             NOT NULL,
            confidence         double precision NOT NULL DEFAULT 0,
            reason             text             NOT NULL DEFAULT 'fail_closed_oov',
            start_char         integer          NULL,
            end_char           integer          NULL,
            scrubbed_context   text             NOT NULL,
            review_status      text             NOT NULL DEFAULT 'pending' CHECK (review_status IN ('pending', 'approved', 'rejected')),
            mapped_code        text             NULL,
            mapped_display     text             NULL,
            reviewer_notes     text             NULL,
            created_at         timestamptz      NOT NULL DEFAULT now(),
            reviewed_at        timestamptz      NULL
        );

        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS provider_metadata jsonb NULL;
        ALTER TABLE extraction_runs ADD COLUMN IF NOT EXISTS validation_dimensions_json jsonb NULL;
        ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS dose_unit text NULL;
        ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS action text NULL;
        ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS origin text NULL;
        ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS provenance_json jsonb NULL;
        ALTER TABLE extracted_medications ADD COLUMN IF NOT EXISTS normalization_json jsonb NULL;
        ALTER TABLE extracted_investigations ADD COLUMN IF NOT EXISTS origin text NULL;
        ALTER TABLE extracted_investigations ADD COLUMN IF NOT EXISTS provenance_json jsonb NULL;
        ALTER TABLE extracted_investigations ADD COLUMN IF NOT EXISTS normalization_json jsonb NULL;
        ALTER TABLE extracted_investigations ADD COLUMN IF NOT EXISTS clinical_category text NULL;
        ALTER TABLE extracted_investigations ADD COLUMN IF NOT EXISTS result_value text NULL;

        CREATE INDEX IF NOT EXISTS ix_oov_quarantine_items_status
            ON oov_quarantine_items (review_status, created_at DESC);

        CREATE TABLE IF NOT EXISTS entity_review_audit (
            review_id           uuid        PRIMARY KEY,
            extraction_id       uuid        NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
            entity_type         text        NOT NULL,
            entity_index        integer     NOT NULL,
            review_status       text        NOT NULL,
            original_json       jsonb       NOT NULL,
            corrected_json      jsonb       NULL,
            evidence_json       jsonb       NOT NULL,
            review_action       text        NOT NULL,
            reviewer_identity   text        NOT NULL,
            auth_mode           text        NOT NULL,
            review_comment      text        NULL,
            reviewed_at         timestamptz NOT NULL DEFAULT now(),
            version_number      integer     NOT NULL DEFAULT 1
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_entity_review_version
            ON entity_review_audit (extraction_id, entity_type, entity_index, version_number);

        CREATE INDEX IF NOT EXISTS ix_entity_review_audit_extraction_id
            ON entity_review_audit (extraction_id, entity_type, entity_index, version_number ASC);
        """;

    public SqlExtractionRepository(string? connectionString, ILogger<SqlExtractionRepository> logger, bool autoInitializeSchema = true)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
        _logger = logger;
        _autoInitializeSchema = autoInitializeSchema;
    }

    public bool IsSqlConfigured => _connectionString != null;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_connectionString == null || _schemaInitialized || !_autoInitializeSchema)
            return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SchemaDdl, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaInitialized = true;
    }

    public async Task SaveExtractionAsync(string runId, ClinicalExtractionResponse response, CancellationToken ct = default, IReadOnlyList<QuarantineItemDto>? quarantineItems = null)
    {
        if (_connectionString == null)
            throw new InvalidOperationException("PostgreSQL is not configured; extraction was not saved.");

        try
        {
            await EnsureSchemaAsync(ct);

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            Guid extractionUuid = TryParseOrDeterministicGuid(response.ExtractionId);
            DateTimeOffset extractedAt = DateTimeOffset.TryParse(response.ExtractedAt, out var dt)
                ? dt.ToUniversalTime()
                : DateTimeOffset.UtcNow;

            const string insertRunSql = """
                INSERT INTO extraction_runs (
                    extraction_id, correlation_id, note_text, source_type, source_file_name, saved_at,
                    char_length, schema_version,
                    extracted_at, execution_duration_ms, adapter_type, model_deployment,
                    prompt_version, prompt_tokens, completion_tokens, total_tokens,
                    estimated_cost_usd, has_errors, error_count, warning_count, sections_json,
                    safety_alerts_json, note_embedding, provider_metadata
                ) VALUES (
                    @extraction_id, @correlation_id, @note_text, @source_type, @source_file_name, @saved_at,
                    @char_length, @schema_version,
                    @extracted_at, @execution_duration_ms, @adapter_type, @model_deployment,
                    @prompt_version, @prompt_tokens, @completion_tokens, @total_tokens,
                    @estimated_cost_usd, @has_errors, @error_count, @warning_count, @sections_json,
                    @safety_alerts_json, @note_embedding::vector, @provider_metadata
                )
                ;
                """;

            await using (var runCmd = new NpgsqlCommand(insertRunSql, conn, tx))
            {
                runCmd.Parameters.AddWithValue("extraction_id", extractionUuid);
                runCmd.Parameters.AddWithValue("correlation_id", response.CorrelationId);
                runCmd.Parameters.AddWithValue("note_text", (object?)response.NoteText ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("source_type", response.InputType);
                runCmd.Parameters.AddWithValue("source_file_name", (object?)response.SourceFileName ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("saved_at", DateTimeOffset.UtcNow);
                runCmd.Parameters.AddWithValue("char_length", response.Preprocessed.CharLength);
                runCmd.Parameters.AddWithValue("schema_version", response.SchemaVersion);
                runCmd.Parameters.AddWithValue("extracted_at", extractedAt);
                runCmd.Parameters.AddWithValue("execution_duration_ms", response.ExecutionDurationMs);
                runCmd.Parameters.AddWithValue("adapter_type", response.ModelMetadata.AdapterType);
                runCmd.Parameters.AddWithValue("model_deployment", response.ModelMetadata.ModelDeployment);
                runCmd.Parameters.AddWithValue("prompt_version", response.ModelMetadata.PromptVersion);
                runCmd.Parameters.AddWithValue("prompt_tokens", (object?)response.ModelMetadata.PromptTokens ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("completion_tokens", (object?)response.ModelMetadata.CompletionTokens ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("total_tokens", (object?)response.ModelMetadata.TotalTokens ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("estimated_cost_usd", (object?)response.ModelMetadata.EstimatedCostUsd ?? DBNull.Value);
                runCmd.Parameters.AddWithValue("has_errors", response.ValidationSummary.HasErrors);
                runCmd.Parameters.AddWithValue("error_count", response.ValidationSummary.ErrorCount);
                runCmd.Parameters.AddWithValue("warning_count", response.ValidationSummary.WarningCount);
                runCmd.Parameters.Add(new NpgsqlParameter("sections_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(response.Preprocessed.Sections)
                });
                runCmd.Parameters.Add(new NpgsqlParameter("safety_alerts_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(response.SafetyAlerts ?? [])
                });
                runCmd.Parameters.AddWithValue("note_embedding", response.Embedding is { Length: > 0 }
                    ? $"[{string.Join(',', response.Embedding.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]"
                    : (object)DBNull.Value);

                runCmd.Parameters.Add(new NpgsqlParameter("provider_metadata", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(response.ModelMetadata) });
                await runCmd.ExecuteNonQueryAsync(ct);
            }

            // Extraction candidates are immutable. Duplicate IDs fail at the parent
            // INSERT before any child or audit rows can change.

            const string insertMedSql = """
                INSERT INTO extracted_medications (
                    medication_id, extraction_id, name, strength, dose, route,
                    frequency, duration, formulation, instructions, assertion_status,
                    evidence_quote, evidence_verified, evidence_start, evidence_end,
                    normalization_status, matched_code, display_name, confidence,
                    quality_flags_json, findings_json, dose_unit, action, origin, provenance_json, normalization_json
                ) VALUES (
                    @medication_id, @extraction_id, @name, @strength, @dose, @route,
                    @frequency, @duration, @formulation, @instructions, @assertion_status,
                    @evidence_quote, @evidence_verified, @evidence_start, @evidence_end,
                    @normalization_status, @matched_code, @display_name, @confidence,
                    @quality_flags_json, @findings_json, @dose_unit, @action, @origin, @provenance_json, @normalization_json
                );
                """;

            foreach (var med in response.Medications)
            {
                await using var medCmd = new NpgsqlCommand(insertMedSql, conn, tx);
                medCmd.Parameters.AddWithValue("medication_id", Guid.NewGuid());
                medCmd.Parameters.AddWithValue("extraction_id", extractionUuid);
                medCmd.Parameters.AddWithValue("name", med.Name);
                medCmd.Parameters.AddWithValue("strength", (object?)med.Strength ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("dose", (object?)med.Dose ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("route", (object?)med.Route ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("frequency", (object?)med.Frequency ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("duration", (object?)med.Duration ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("formulation", (object?)med.Formulation ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("instructions", (object?)med.Instructions ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("assertion_status", med.AssertionStatus);
                medCmd.Parameters.AddWithValue("evidence_quote", med.Evidence.Quote);
                medCmd.Parameters.AddWithValue("evidence_verified", med.Evidence.Verified);
                medCmd.Parameters.AddWithValue("evidence_start", (object?)med.Evidence.Start ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("evidence_end", (object?)med.Evidence.End ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("normalization_status", med.Normalization.Status);
                medCmd.Parameters.AddWithValue("matched_code", (object?)med.Normalization.MatchedCode ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("display_name", (object?)med.Normalization.DisplayName ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("confidence", med.Normalization.Confidence);
                medCmd.Parameters.Add(new NpgsqlParameter("quality_flags_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(med.QualityFlags)
                });
                medCmd.Parameters.Add(new NpgsqlParameter("findings_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(med.Findings)
                });

                medCmd.Parameters.AddWithValue("dose_unit", (object?)med.DoseUnit ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("action", (object?)med.Action ?? DBNull.Value);
                medCmd.Parameters.AddWithValue("origin", med.Origin);
                medCmd.Parameters.Add(new NpgsqlParameter("provenance_json", NpgsqlDbType.Jsonb) { Value = med.Provenance == null ? DBNull.Value : JsonSerializer.Serialize(med.Provenance) });
                medCmd.Parameters.Add(new NpgsqlParameter("normalization_json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(med.Normalization) });
                await medCmd.ExecuteNonQueryAsync(ct);
            }

            const string insertInvSql = """
                INSERT INTO extracted_investigations (
                    investigation_id, extraction_id, name, timing, specimen,
                    assertion_status, status, evidence_quote, evidence_verified,
                    evidence_start, evidence_end, normalization_status, matched_code,
                    display_name, confidence, quality_flags_json, findings_json, origin, provenance_json, normalization_json, clinical_category, result_value
                ) VALUES (
                    @investigation_id, @extraction_id, @name, @timing, @specimen,
                    @assertion_status, @status, @evidence_quote, @evidence_verified,
                    @evidence_start, @evidence_end, @normalization_status, @matched_code,
                    @display_name, @confidence, @quality_flags_json, @findings_json, @origin, @provenance_json, @normalization_json, @clinical_category, @result_value
                );
                """;

            foreach (var inv in response.Investigations)
            {
                await using var invCmd = new NpgsqlCommand(insertInvSql, conn, tx);
                invCmd.Parameters.AddWithValue("investigation_id", Guid.NewGuid());
                invCmd.Parameters.AddWithValue("extraction_id", extractionUuid);
                invCmd.Parameters.AddWithValue("name", inv.Name);
                invCmd.Parameters.AddWithValue("timing", (object?)inv.Timing ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("specimen", (object?)inv.Specimen ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("assertion_status", inv.AssertionStatus);
                invCmd.Parameters.AddWithValue("status", inv.Status);
                invCmd.Parameters.AddWithValue("evidence_quote", inv.Evidence.Quote);
                invCmd.Parameters.AddWithValue("evidence_verified", inv.Evidence.Verified);
                invCmd.Parameters.AddWithValue("evidence_start", (object?)inv.Evidence.Start ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("evidence_end", (object?)inv.Evidence.End ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("normalization_status", inv.Normalization.Status);
                invCmd.Parameters.AddWithValue("matched_code", (object?)inv.Normalization.MatchedCode ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("display_name", (object?)inv.Normalization.DisplayName ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("confidence", inv.Normalization.Confidence);
                invCmd.Parameters.Add(new NpgsqlParameter("quality_flags_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(inv.QualityFlags)
                });
                invCmd.Parameters.Add(new NpgsqlParameter("findings_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(inv.Findings)
                });

                invCmd.Parameters.AddWithValue("origin", inv.Origin);
                invCmd.Parameters.Add(new NpgsqlParameter("provenance_json", NpgsqlDbType.Jsonb) { Value = inv.Provenance == null ? DBNull.Value : JsonSerializer.Serialize(inv.Provenance) });
                invCmd.Parameters.Add(new NpgsqlParameter("normalization_json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(inv.Normalization) });
                invCmd.Parameters.AddWithValue("clinical_category", (object?)inv.ClinicalCategory ?? DBNull.Value);
                invCmd.Parameters.AddWithValue("result_value", (object?)inv.ResultValue ?? DBNull.Value);
                await invCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var item in quarantineItems ?? [])
            {
                await using var quarantineCmd = new NpgsqlCommand("""
                    INSERT INTO oov_quarantine_items
                    (quarantine_id, extraction_id, category, mention, normalized_mention, confidence, reason,
                     start_char, end_char, scrubbed_context, review_status)
                    VALUES (@id, @run, @category, @mention, @normalized, @confidence, @reason, @start, @end, @context, @status);
                    """, conn, tx);
                quarantineCmd.Parameters.AddWithValue("id", Guid.Parse(item.QuarantineId));
                quarantineCmd.Parameters.AddWithValue("run", extractionUuid);
                quarantineCmd.Parameters.AddWithValue("category", item.Category);
                quarantineCmd.Parameters.AddWithValue("mention", item.Mention);
                quarantineCmd.Parameters.AddWithValue("normalized", item.NormalizedMention);
                quarantineCmd.Parameters.AddWithValue("confidence", item.Confidence);
                quarantineCmd.Parameters.AddWithValue("reason", item.Reason);
                quarantineCmd.Parameters.AddWithValue("start", (object?)item.StartChar ?? DBNull.Value);
                quarantineCmd.Parameters.AddWithValue("end", (object?)item.EndChar ?? DBNull.Value);
                quarantineCmd.Parameters.AddWithValue("context", item.ScrubbedContext);
                quarantineCmd.Parameters.AddWithValue("status", item.ReviewStatus);
                await quarantineCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError("PostgreSQL persistence failed for {ExtractionId}; error type {ErrorType}.", response.ExtractionId, ex.GetType().Name);
            throw;
        }
    }

    public async Task<ClinicalExtractionResponse?> GetExtractionAsync(string extractionId, CancellationToken ct = default)
    {
        if (_connectionString != null && Guid.TryParse(extractionId, out Guid extractionUuid))
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                var fromDb = await LoadSingleExtractionFromDbAsync(conn, extractionUuid, ct);
                if (fromDb != null)
                    return fromDb;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return await _fallbackStore.GetExtractionAsync(extractionId, ct);
    }

    public async Task<IReadOnlyList<ClinicalExtractionResponse>> ListExtractionsAsync(int limit = 50, CancellationToken ct = default)
    {
        int clampedLimit = Math.Clamp(limit, 1, 500);

        if (_connectionString != null)
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                var ids = new List<Guid>();
                const string listIdsSql = "SELECT extraction_id FROM extraction_runs ORDER BY extracted_at DESC LIMIT @limit;";
                await using (var cmd = new NpgsqlCommand(listIdsSql, conn))
                {
                    cmd.Parameters.AddWithValue("limit", clampedLimit);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        ids.Add(reader.GetGuid(0));
                    }
                }

                var results = new List<ClinicalExtractionResponse>(ids.Count);
                foreach (var id in ids)
                {
                    var item = await LoadSingleExtractionFromDbAsync(conn, id, ct);
                    if (item != null)
                        results.Add(item);
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return await _fallbackStore.ListExtractionsAsync(clampedLimit, ct);
    }

    public async Task<IReadOnlyList<SemanticSearchResultDto>> SearchBySemanticSimilarityAsync(
        string query,
        int limit = 10,
        double minSimilarity = 0.05,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        int clampedLimit = Math.Clamp(limit, 1, 100);
        var queryEmbedding = ClinicalEmbeddingGenerator.GenerateEmbedding(query);

        if (_connectionString != null)
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                string pgVecLiteral = ClinicalEmbeddingGenerator.FormatPgVectorLiteral(queryEmbedding);
                const string searchSql = """
                    SELECT extraction_id, 1 - (note_embedding <=> @query_vec::vector) AS similarity_score
                    FROM extraction_runs
                    WHERE note_embedding IS NOT NULL
                    ORDER BY note_embedding <=> @query_vec::vector ASC
                    LIMIT @limit;
                    """;

                var matches = new List<(Guid Id, double Score)>();
                await using (var cmd = new NpgsqlCommand(searchSql, conn))
                {
                    cmd.Parameters.AddWithValue("query_vec", pgVecLiteral);
                    cmd.Parameters.AddWithValue("limit", clampedLimit);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        double score = Math.Round(reader.GetDouble(1), 4);
                        if (score >= minSimilarity)
                        {
                            matches.Add((reader.GetGuid(0), score));
                        }
                    }
                }

                if (matches.Count > 0)
                {
                    var results = new List<SemanticSearchResultDto>(matches.Count);
                    foreach (var (id, score) in matches)
                    {
                        var ext = await LoadSingleExtractionFromDbAsync(conn, id, ct);
                        if (ext != null)
                        {
                            var highlights = ext.Medications.Take(2).Select(m => $"Medication: {m.Name}").ToList();
                            results.Add(new SemanticSearchResultDto(ext, score, highlights));
                        }
                    }
                    return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return await _fallbackStore.SearchBySemanticSimilarityAsync(query, clampedLimit, minSimilarity, ct);
    }

    public async Task<QuarantineItemDto> AddQuarantineItemAsync(QuarantineItemDto item, CancellationToken ct = default)
    {
        var saved = await _fallbackStore.AddQuarantineItemAsync(item, ct);

        if (_connectionString != null)
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string insertSql = """
                    INSERT INTO oov_quarantine_items (
                        quarantine_id, extraction_id, category, mention, normalized_mention,
                        confidence, reason, start_char, end_char, scrubbed_context,
                        review_status, mapped_code, mapped_display, reviewer_notes, created_at
                    ) VALUES (
                        @quarantine_id, @extraction_id, @category, @mention, @normalized_mention,
                        @confidence, @reason, @start_char, @end_char, @scrubbed_context,
                        @review_status, @mapped_code, @mapped_display, @reviewer_notes, @created_at
                    ) ON CONFLICT (quarantine_id) DO NOTHING;
                    """;

                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("quarantine_id", TryParseOrDeterministicGuid(saved.QuarantineId));
                cmd.Parameters.AddWithValue("extraction_id", saved.ExtractionId != null && Guid.TryParse(saved.ExtractionId, out var extGuid) ? extGuid : DBNull.Value);
                cmd.Parameters.AddWithValue("category", saved.Category);
                cmd.Parameters.AddWithValue("mention", saved.Mention);
                cmd.Parameters.AddWithValue("normalized_mention", saved.NormalizedMention);
                cmd.Parameters.AddWithValue("confidence", saved.Confidence);
                cmd.Parameters.AddWithValue("reason", saved.Reason);
                cmd.Parameters.AddWithValue("start_char", (object?)saved.StartChar ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end_char", (object?)saved.EndChar ?? DBNull.Value);
                cmd.Parameters.AddWithValue("scrubbed_context", saved.ScrubbedContext);
                cmd.Parameters.AddWithValue("review_status", saved.ReviewStatus);
                cmd.Parameters.AddWithValue("mapped_code", (object?)saved.MappedCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("mapped_display", (object?)saved.MappedDisplay ?? DBNull.Value);
                cmd.Parameters.AddWithValue("reviewer_notes", (object?)saved.ReviewerNotes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return saved;
    }

    public async Task<IReadOnlyList<QuarantineItemDto>> ListQuarantineItemsAsync(string? status = null, int limit = 100, CancellationToken ct = default)
    {
        int clamped = Math.Clamp(limit, 1, 500);

        if (_connectionString != null)
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                string sql = string.IsNullOrWhiteSpace(status)
                    ? "SELECT quarantine_id, extraction_id, category, mention, normalized_mention, confidence, reason, start_char, end_char, scrubbed_context, review_status, mapped_code, mapped_display, reviewer_notes, created_at, reviewed_at FROM oov_quarantine_items ORDER BY created_at DESC LIMIT @limit;"
                    : "SELECT quarantine_id, extraction_id, category, mention, normalized_mention, confidence, reason, start_char, end_char, scrubbed_context, review_status, mapped_code, mapped_display, reviewer_notes, created_at, reviewed_at FROM oov_quarantine_items WHERE lower(review_status) = lower(@status) ORDER BY created_at DESC LIMIT @limit;";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("limit", clamped);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    cmd.Parameters.AddWithValue("status", status);
                }

                var list = new List<QuarantineItemDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new QuarantineItemDto(
                        QuarantineId: reader.GetGuid(0).ToString("D"),
                        ExtractionId: reader.IsDBNull(1) ? null : reader.GetGuid(1).ToString("D"),
                        Category: reader.GetString(2),
                        Mention: reader.GetString(3),
                        NormalizedMention: reader.GetString(4),
                        Confidence: reader.GetDouble(5),
                        Reason: reader.GetString(6),
                        StartChar: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        EndChar: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        ScrubbedContext: reader.GetString(9),
                        ReviewStatus: reader.GetString(10),
                        MappedCode: reader.IsDBNull(11) ? null : reader.GetString(11),
                        MappedDisplay: reader.IsDBNull(12) ? null : reader.GetString(12),
                        ReviewerNotes: reader.IsDBNull(13) ? null : reader.GetString(13),
                        CreatedAt: reader.GetFieldValue<DateTimeOffset>(14).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                        ReviewedAt: reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15).ToString("yyyy-MM-ddTHH:mm:sszzz")
                    ));
                }

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return await _fallbackStore.ListQuarantineItemsAsync(status, clamped, ct);
    }

    public async Task<QuarantineItemDto?> ResolveQuarantineItemAsync(string quarantineId, QuarantineResolutionRequest resolution, CancellationToken ct = default)
    {
        var updated = await _fallbackStore.ResolveQuarantineItemAsync(quarantineId, resolution, ct);

        if (_connectionString != null && Guid.TryParse(quarantineId, out var qUuid))
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string updateSql = """
                    UPDATE oov_quarantine_items
                    SET review_status = @status,
                        mapped_code = COALESCE(@mapped_code, mapped_code),
                        mapped_display = COALESCE(@mapped_display, mapped_display),
                        reviewer_notes = COALESCE(@reviewer_notes, reviewer_notes),
                        reviewed_at = now()
                    WHERE quarantine_id = @id;
                    """;

                await using var cmd = new NpgsqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("id", qUuid);
                cmd.Parameters.AddWithValue("status", resolution.ReviewStatus);
                cmd.Parameters.AddWithValue("mapped_code", (object?)resolution.MappedCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("mapped_display", (object?)resolution.MappedDisplay ?? DBNull.Value);
                cmd.Parameters.AddWithValue("reviewer_notes", (object?)resolution.ReviewerNotes ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Database operation failed; error type {ErrorType}", ex.GetType().Name);
            }
        }

        return updated;
    }

    public async Task<EntityReviewRecordDto> AddEntityReviewAsync(
        string extractionId,
        EntityReviewSubmissionRequest request,
        string reviewerIdentity,
        string authMode,
        CancellationToken ct = default)
    {
        if (_connectionString != null && Guid.TryParse(extractionId, out var extractionUuid))
        {
            await EnsureSchemaAsync(ct);
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var extraction = await LoadSingleExtractionFromDbAsync(conn, extractionUuid, ct);
            if (extraction == null)
                throw new KeyNotFoundException($"Extraction '{extractionId}' was not found.");

            var (origSnapshot, origEvidence) = InMemoryExtractionRepository.BuildEntitySnapshot(
                extraction,
                request.EntityType,
                request.EntityIndex);

            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Serialize decisions on the parent row before reading the version. Unlike
            // MAX+1 alone this also protects the first review, when no audit row exists.
            await using (var parentLock = new NpgsqlCommand("SELECT extraction_id FROM extraction_runs WHERE extraction_id=@id FOR UPDATE", conn, tx))
            {
                parentLock.Parameters.AddWithValue("id", extractionUuid);
                await parentLock.ExecuteScalarAsync(ct);
            }
            int nextVersion = 1;
            const string versionSql = """
                SELECT COALESCE(MAX(version_number), 0) + 1
                FROM entity_review_audit
                WHERE extraction_id = @extraction_id
                  AND upper(entity_type) = upper(@entity_type)
                  AND entity_index = @entity_index;
                """;
            try
            {
                await using var verCmd = new NpgsqlCommand(versionSql, conn, tx);
                verCmd.Parameters.AddWithValue("extraction_id", extractionUuid);
                verCmd.Parameters.AddWithValue("entity_type", request.EntityType.ToUpperInvariant());
                verCmd.Parameters.AddWithValue("entity_index", request.EntityIndex);
                var scalar = await verCmd.ExecuteScalarAsync(ct);
                nextVersion = Convert.ToInt32(scalar);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "42P01")
            {
                await tx.RollbackAsync(ct);
                await using (var ddlCmd = new NpgsqlCommand("""
                    CREATE TABLE IF NOT EXISTS entity_review_audit (
                        review_id           uuid        PRIMARY KEY,
                        extraction_id       uuid        NOT NULL REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE,
                        entity_type         text        NOT NULL,
                        entity_index        integer     NOT NULL,
                        review_status       text        NOT NULL,
                        original_json       jsonb       NOT NULL,
                        corrected_json      jsonb       NULL,
                        evidence_json       jsonb       NOT NULL,
                        review_action       text        NOT NULL,
                        reviewer_identity   text        NOT NULL,
                        auth_mode           text        NOT NULL,
                        review_comment      text        NULL,
                        reviewed_at         timestamptz NOT NULL DEFAULT now(),
                        version_number      integer     NOT NULL DEFAULT 1
                    );
                    CREATE INDEX IF NOT EXISTS ix_entity_review_audit_extraction_id
                        ON entity_review_audit (extraction_id, entity_type, entity_index, version_number ASC);
                    """, conn))
                {
                    await ddlCmd.ExecuteNonQueryAsync(ct);
                }
                return await AddEntityReviewAsync(extractionId, request, reviewerIdentity, authMode, ct);
            }

            if (request.ExpectedVersion is int expected && expected != nextVersion - 1) throw new ReviewConflictException();

            Guid reviewUuid = Guid.NewGuid();
            DateTimeOffset reviewedAt = DateTimeOffset.UtcNow;
            string statusUpper = request.ReviewStatus.ToUpperInvariant();
            string typeUpper = request.EntityType.ToUpperInvariant();

            const string insertSql = """
                INSERT INTO entity_review_audit (
                    review_id, extraction_id, entity_type, entity_index, review_status,
                    original_json, corrected_json, evidence_json, review_action,
                    reviewer_identity, auth_mode, review_comment, reviewed_at, version_number
                ) VALUES (
                    @review_id, @extraction_id, @entity_type, @entity_index, @review_status,
                    @original_json, @corrected_json, @evidence_json, @review_action,
                    @reviewer_identity, @auth_mode, @review_comment, @reviewed_at, @version_number
                );
                """;
            await using (var insCmd = new NpgsqlCommand(insertSql, conn, tx))
            {
                insCmd.Parameters.AddWithValue("review_id", reviewUuid);
                insCmd.Parameters.AddWithValue("extraction_id", extractionUuid);
                insCmd.Parameters.AddWithValue("entity_type", typeUpper);
                insCmd.Parameters.AddWithValue("entity_index", request.EntityIndex);
                insCmd.Parameters.AddWithValue("review_status", statusUpper);
                insCmd.Parameters.Add(new NpgsqlParameter("original_json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(origSnapshot) });
                insCmd.Parameters.Add(new NpgsqlParameter("corrected_json", NpgsqlDbType.Jsonb)
                {
                    Value = request.CorrectedEntity != null ? JsonSerializer.Serialize(request.CorrectedEntity) : DBNull.Value
                });
                insCmd.Parameters.Add(new NpgsqlParameter("evidence_json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(origEvidence) });
                insCmd.Parameters.AddWithValue("review_action", statusUpper);
                insCmd.Parameters.AddWithValue("reviewer_identity", reviewerIdentity);
                insCmd.Parameters.AddWithValue("auth_mode", authMode);
                insCmd.Parameters.AddWithValue("review_comment", (object?)request.ReviewComment ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("reviewed_at", reviewedAt);
                insCmd.Parameters.AddWithValue("version_number", nextVersion);
                await insCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new EntityReviewRecordDto(
                ReviewId: reviewUuid.ToString("D"),
                ExtractionId: extractionUuid.ToString("D"),
                EntityType: typeUpper,
                EntityIndex: request.EntityIndex,
                ReviewStatus: statusUpper,
                OriginalEntity: origSnapshot,
                CorrectedEntity: request.CorrectedEntity,
                OriginalEvidence: origEvidence,
                ReviewAction: statusUpper,
                ReviewerIdentity: reviewerIdentity,
                AuthMode: authMode,
                ReviewComment: request.ReviewComment,
                ReviewedAt: reviewedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                VersionNumber: nextVersion
            );
        }

        return await _fallbackStore.AddEntityReviewAsync(extractionId, request, reviewerIdentity, authMode, ct);
    }

    public async Task<IReadOnlyList<EntityReviewRecordDto>> ListEntityReviewsAsync(string extractionId, CancellationToken ct = default)
    {
        if (_connectionString != null && Guid.TryParse(extractionId, out var extractionUuid))
        {
            try
            {
                await EnsureSchemaAsync(ct);
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                return await LoadEntityReviewsFromConnAsync(conn, extractionUuid, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Review history unavailable; error type {ErrorType}", ex.GetType().Name);
                throw;
            }
        }

        return await _fallbackStore.ListEntityReviewsAsync(extractionId, ct);
    }

    private static async Task<List<EntityReviewRecordDto>> LoadEntityReviewsFromConnAsync(
        NpgsqlConnection conn,
        Guid extractionUuid,
        CancellationToken ct)
    {
        var reviews = new List<EntityReviewRecordDto>();
        try
        {
            const string sql = """
                SELECT review_id, extraction_id, entity_type, entity_index, review_status,
                       original_json, corrected_json, evidence_json, review_action,
                       reviewer_identity, auth_mode, review_comment, reviewed_at, version_number
                FROM entity_review_audit
                WHERE extraction_id = @id
                ORDER BY entity_type ASC, entity_index ASC, version_number ASC;
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", extractionUuid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var orig = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(5)) ?? [];
                var corr = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(6));
                var ev = JsonSerializer.Deserialize<EvidenceDto>(reader.GetString(7)) ?? new EvidenceDto("", true, null, null);
                reviews.Add(new EntityReviewRecordDto(
                    ReviewId: reader.GetGuid(0).ToString("D"),
                    ExtractionId: reader.GetGuid(1).ToString("D"),
                    EntityType: reader.GetString(2),
                    EntityIndex: reader.GetInt32(3),
                    ReviewStatus: reader.GetString(4),
                    OriginalEntity: orig,
                    CorrectedEntity: corr,
                    OriginalEvidence: ev,
                    ReviewAction: reader.GetString(8),
                    ReviewerIdentity: reader.GetString(9),
                    AuthMode: reader.GetString(10),
                    ReviewComment: reader.IsDBNull(11) ? null : reader.GetString(11),
                    ReviewedAt: reader.GetFieldValue<DateTimeOffset>(12).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    VersionNumber: reader.GetInt32(13)
                ));
            }
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            // Table does not exist yet in read-only or pre-migration schema
        }
        return reviews;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_connectionString == null)
            return true;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ClinicalExtractionResponse?> LoadSingleExtractionFromDbAsync(
        NpgsqlConnection conn,
        Guid extractionUuid,
        CancellationToken ct)
    {
        const string selectRunSql = """
            SELECT extraction_id, correlation_id, char_length, schema_version,
                   extracted_at, execution_duration_ms, adapter_type, model_deployment,
                   prompt_version, prompt_tokens, completion_tokens, total_tokens,
                   estimated_cost_usd, has_errors, error_count, warning_count, sections_json,
                   note_text, source_type, source_file_name, safety_alerts_json,
                   note_embedding::text, provider_metadata
            FROM extraction_runs
            WHERE extraction_id = @id;
            """;

        string correlationId;
        int charLength;
        string schemaVersion;
        DateTimeOffset extractedAt;
        double executionDurationMs;
        string adapterType;
        string modelDeployment;
        string promptVersion;
        int? promptTokens;
        int? completionTokens;
        int? totalTokens;
        double? estimatedCostUsd;
        bool hasErrors;
        int errorCount;
        int warningCount;
        List<SectionSpanDto> sections;
        string? noteText;
        string inputType;
        string? sourceFileName;
        List<ClinicalSafetyAlertDto> safetyAlerts;
        float[]? embedding;
        ModelMetadataDto? savedMetadata;

        await using (var runCmd = new NpgsqlCommand(selectRunSql, conn))
        {
            runCmd.Parameters.AddWithValue("id", extractionUuid);
            await using var reader = await runCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            correlationId = reader.GetString(1);
            charLength = reader.GetInt32(2);
            schemaVersion = reader.GetString(3);
            extractedAt = reader.GetFieldValue<DateTimeOffset>(4);
            executionDurationMs = reader.GetDouble(5);
            adapterType = reader.GetString(6);
            modelDeployment = reader.GetString(7);
            promptVersion = reader.GetString(8);
            promptTokens = reader.IsDBNull(9) ? null : reader.GetInt32(9);
            completionTokens = reader.IsDBNull(10) ? null : reader.GetInt32(10);
            totalTokens = reader.IsDBNull(11) ? null : reader.GetInt32(11);
            estimatedCostUsd = reader.IsDBNull(12) ? null : reader.GetDouble(12);
            hasErrors = reader.GetBoolean(13);
            errorCount = reader.GetInt32(14);
            warningCount = reader.GetInt32(15);
            string sectionsJson = reader.GetString(16);
            sections = JsonSerializer.Deserialize<List<SectionSpanDto>>(sectionsJson) ?? [];
            noteText = reader.IsDBNull(17) ? null : reader.GetString(17);
            inputType = reader.IsDBNull(18) ? "text" : reader.GetString(18);
            sourceFileName = reader.IsDBNull(19) ? null : reader.GetString(19);
            safetyAlerts = reader.IsDBNull(20)
                ? []
                : JsonSerializer.Deserialize<List<ClinicalSafetyAlertDto>>(reader.GetString(20)) ?? [];
            embedding = reader.IsDBNull(21)
                ? null
                : JsonSerializer.Deserialize<float[]>(reader.GetString(21));
            savedMetadata = reader.IsDBNull(22) ? null : JsonSerializer.Deserialize<ModelMetadataDto>(reader.GetString(22));
        }

        var medications = new List<ExtractedMedicationDto>();
        const string selectMedsSql = """
            SELECT name, strength, dose, route, frequency, duration, formulation,
                   instructions, assertion_status, evidence_quote, evidence_verified,
                   evidence_start, evidence_end, normalization_status, matched_code,
                   display_name, confidence, quality_flags_json, findings_json, dose_unit, action, origin, provenance_json, normalization_json
            FROM extracted_medications
            WHERE extraction_id = @id;
            """;

        await using (var medCmd = new NpgsqlCommand(selectMedsSql, conn))
        {
            medCmd.Parameters.AddWithValue("id", extractionUuid);
            await using var reader = await medCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var qFlags = JsonSerializer.Deserialize<List<string>>(reader.GetString(17)) ?? [];
                var findings = JsonSerializer.Deserialize<List<FindingDto>>(reader.GetString(18)) ?? [];

                string medName = reader.GetString(0);
                string? medStrength = reader.IsDBNull(1) ? null : reader.GetString(1);
                string defaultProvider = savedMetadata?.Provider ?? (adapterType.StartsWith("onnx", StringComparison.OrdinalIgnoreCase) ? "onnx" : "foundry");
                string normStatus = reader.GetString(13);
                string? matchedCode = reader.IsDBNull(14) ? null : reader.GetString(14);
                string? displayName = reader.IsDBNull(15) ? null : reader.GetString(15);

                var resolvedMed = new Apollo.Backend.Clinical.IndianOntologyResolver().ResolveMedication(medName, medStrength);
                string defaultOrigin = adapterType.StartsWith("onnx", StringComparison.OrdinalIgnoreCase)
                    ? "RULE_EXTRACTED"
                    : normStatus == "matched"
                        ? "LLM_AND_RULE_CONFIRMED"
                        : "LLM_EXTRACTED";

                medications.Add(new ExtractedMedicationDto(
                    Name: medName,
                    Strength: medStrength,
                    Dose: reader.IsDBNull(2) ? null : reader.GetString(2),
                    Route: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Frequency: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Duration: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Formulation: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Instructions: reader.IsDBNull(7) ? null : reader.GetString(7),
                    AssertionStatus: reader.GetString(8),
                    Evidence: new EvidenceDto(
                        Quote: reader.GetString(9),
                        Verified: reader.GetBoolean(10),
                        Start: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        End: reader.IsDBNull(12) ? null : reader.GetInt32(12)
                    ),
                    QualityFlags: qFlags,
                    Findings: findings,
                    Normalization: !reader.IsDBNull(23) ? JsonSerializer.Deserialize<NormalizationDto>(reader.GetString(23))! : new NormalizationDto(
                        Status: normStatus,
                        MatchedCode: matchedCode,
                        DisplayName: displayName,
                        Confidence: reader.GetDouble(16),
                        SemanticStatus: resolvedMed.SemanticStatus,
                        CandidateCode: resolvedMed.CandidateCode,
                        TerminologySource: resolvedMed.TerminologySource ?? "RxNorm",
                        TerminologyVersion: resolvedMed.TerminologyVersion ?? Apollo.Backend.Clinical.TerminologyVerification.Version,
                        MappingNote: resolvedMed.MappingNote
                    ),
                    DoseUnit: reader.IsDBNull(19) ? null : reader.GetString(19),
                    Action: reader.IsDBNull(20) ? null : reader.GetString(20),
                    Origin: reader.IsDBNull(21) ? defaultOrigin : reader.GetString(21),
                    Provenance: !reader.IsDBNull(22) ? JsonSerializer.Deserialize<EntityProvenanceDto>(reader.GetString(22)) : new EntityProvenanceDto(
                        Origin: defaultOrigin,
                        Provider: defaultProvider,
                        UncertaintyReason: normStatus != "matched" ? resolvedMed.MappingNote : null,
                        Quarantined: normStatus != "matched")
                ));
            }
        }

        var investigations = new List<ExtractedInvestigationDto>();
        const string selectInvsSql = """
            SELECT name, timing, specimen, assertion_status, status, evidence_quote,
                   evidence_verified, evidence_start, evidence_end, normalization_status,
                   matched_code, display_name, confidence, quality_flags_json, findings_json, origin, provenance_json, normalization_json, clinical_category, result_value
            FROM extracted_investigations
            WHERE extraction_id = @id;
            """;

        await using (var invCmd = new NpgsqlCommand(selectInvsSql, conn))
        {
            invCmd.Parameters.AddWithValue("id", extractionUuid);
            await using var reader = await invCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var qFlags = JsonSerializer.Deserialize<List<string>>(reader.GetString(13)) ?? [];
                var findings = JsonSerializer.Deserialize<List<FindingDto>>(reader.GetString(14)) ?? [];
                string invName = reader.GetString(0);
                string evQuote = reader.GetString(5);
                string? matchedCode = reader.IsDBNull(10) ? null : reader.GetString(10);
                string? displayName = reader.IsDBNull(11) ? null : reader.GetString(11);

                var ontology = new Apollo.Backend.Clinical.IndianOntologyResolver();
                var resolvedInv = ontology.ResolveInvestigation(invName);
                if (invName.Equals("chest x-ray", StringComparison.OrdinalIgnoreCase) &&
                    System.Text.RegularExpressions.Regex.IsMatch(evQuote, @"\bchest\s+x-ray\s*\(\s*pa\s+view\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    resolvedInv = ontology.ResolveInvestigation("chest x-ray (pa view)");
                }

                string defaultInvOrigin = adapterType.StartsWith("onnx", StringComparison.OrdinalIgnoreCase) ? "RULE_EXTRACTED" : "LLM_AND_RULE_CONFIRMED";
                string defaultInvProvider = savedMetadata?.Provider ?? (adapterType.StartsWith("onnx", StringComparison.OrdinalIgnoreCase) ? "onnx" : "foundry");
                string invNormStatus = reader.GetString(9);
                string invCategory = System.Text.RegularExpressions.Regex.IsMatch(invName, @"(?i)\b(?:x-ray|radiograph|cxr|ultrasound|usg|ct|ncct|cect|mri|echo|echocardiogram)\b")
                    ? "imaging"
                    : System.Text.RegularExpressions.Regex.IsMatch(invName, @"(?i)\b(?:ecg|ekg|electrocardiogram|eeg|electroencephalogram|pft|spirometry)\b")
                        ? "electrophysiology_procedure"
                        : "laboratory";
                var numMatches = System.Text.RegularExpressions.Regex.Matches(evQuote, @"\b\d+(?:\.\d+)?\s*(?:mg/dL|mEq/L|mmol/L|g/dL|%|U/L|IU/L|ng/mL|pg/mL|mIU/L|mL/min)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string? resultVal = null;
                if (numMatches.Count > 0)
                {
                    int namePos = Math.Max(0, evQuote.LastIndexOf(invName, StringComparison.OrdinalIgnoreCase));
                    var after = numMatches.FirstOrDefault(m => m.Index >= namePos);
                    resultVal = (after ?? numMatches.OrderBy(m => Math.Abs(m.Index - namePos)).First()).Value.Trim();
                }

                investigations.Add(new ExtractedInvestigationDto(
                    Name: invName,
                    Timing: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Specimen: reader.IsDBNull(2) ? null : reader.GetString(2),
                    AssertionStatus: reader.GetString(3),
                    Evidence: new EvidenceDto(
                        Quote: evQuote,
                        Verified: reader.GetBoolean(6),
                        Start: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        End: reader.IsDBNull(8) ? null : reader.GetInt32(8)
                    ),
                    QualityFlags: qFlags,
                    Findings: findings,
                    Normalization: !reader.IsDBNull(17) ? JsonSerializer.Deserialize<NormalizationDto>(reader.GetString(17))! : new NormalizationDto(
                        Status: invNormStatus,
                        MatchedCode: matchedCode,
                        DisplayName: displayName,
                        Confidence: reader.GetDouble(12),
                        SemanticStatus: resolvedInv.SemanticStatus,
                        CandidateCode: resolvedInv.CandidateCode,
                        TerminologySource: resolvedInv.TerminologySource ?? "LOINC",
                        TerminologyVersion: resolvedInv.TerminologyVersion ?? Apollo.Backend.Clinical.TerminologyVerification.LoincVersion,
                        MappingNote: resolvedInv.MappingNote
                    ),
                    MatchedCode: matchedCode,
                    DisplayName: displayName,
                    Status: reader.GetString(4),
                    ClinicalCategory: reader.IsDBNull(18) ? invCategory : reader.GetString(18),
                    ResultValue: !reader.IsDBNull(19) ? reader.GetString(19) : reader.IsDBNull(17) ? resultVal : null,
                    Origin: reader.IsDBNull(15) ? defaultInvOrigin : reader.GetString(15),
                    Provenance: !reader.IsDBNull(16) ? JsonSerializer.Deserialize<EntityProvenanceDto>(reader.GetString(16)) : new EntityProvenanceDto(
                        Origin: defaultInvOrigin,
                        Provider: defaultInvProvider,
                        UncertaintyReason: invNormStatus != "matched" ? resolvedInv.MappingNote : null,
                        Quarantined: invNormStatus != "matched")
                ));
            }
        }

        var restoredDimensions = Apollo.Backend.Clinical.ClinicalResultValidator.BuildValidationDimensions(
            medications,
            investigations,
            [],
            [],
            safetyAlerts);

        var entityReviews = await LoadEntityReviewsFromConnAsync(conn, extractionUuid, ct);

        return new ClinicalExtractionResponse(
            SchemaVersion: schemaVersion,
            ExtractionId: extractionUuid.ToString("D"),
            CorrelationId: correlationId,
            ExtractedAt: extractedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ExecutionDurationMs: executionDurationMs,
            ModelMetadata: savedMetadata ?? new ModelMetadataDto(
                AdapterType: adapterType,
                ModelDeployment: modelDeployment,
                PromptVersion: promptVersion,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens,
                EstimatedCostUsd: estimatedCostUsd
            ),
            Preprocessed: new PreprocessedDto(
                CharLength: charLength,
                Sections: sections
            ),
            Medications: medications,
            Investigations: investigations,
            ValidationSummary: new ValidationSummaryDto(
                HasErrors: hasErrors,
                ErrorCount: errorCount,
                WarningCount: warningCount,
                Dimensions: restoredDimensions
            ),
            SafetyAlerts: safetyAlerts,
            Embedding: embedding,
            NoteText: noteText,
            InputType: inputType,
            SourceFileName: sourceFileName,
            Saved: true,
            EntityReviews: entityReviews
        );
    }

    private static Guid TryParseOrDeterministicGuid(string id)
    {
        if (Guid.TryParse(id, out var parsed))
            return parsed;

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return new Guid(hash.AsSpan(0, 16));
    }
}

