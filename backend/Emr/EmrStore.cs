using System.Text.Json;
using Npgsql;

namespace Apollo.Backend.Emr;

/// <summary>All doctor-facing queries join the assignment table. Roles never bypass the join.</summary>
public sealed class EmrStore(IConfiguration configuration)
{
    public string ConnectionString => configuration.GetConnectionString("Emr") ?? configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:Emr or ConnectionStrings:Postgres.");
    public async Task<NpgsqlConnection> Open(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try { await connection.OpenAsync(ct); return connection; } catch { await connection.DisposeAsync(); throw; }
    }
    public static NpgsqlCommand Command(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null, params (string, object?)[] args)
    {
        var command = new NpgsqlCommand(sql, c, tx);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    public static async Task<int> Execute(NpgsqlConnection c, string sql, CancellationToken ct = default,
        NpgsqlTransaction? tx = null, params (string, object?)[] args)
    {
        await using var cmd = Command(c, sql, tx, args); return await cmd.ExecuteNonQueryAsync(ct);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task<List<T>> Query<T>(NpgsqlConnection c, string sql, CancellationToken ct = default,
        NpgsqlTransaction? tx = null, params (string, object?)[] args)
    {
        await using var cmd = Command(c, sql, tx, args); await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct)) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return items;
    }
    public async Task<bool> HasEncounter(Guid doctor, Guid encounter, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = Command(c, "SELECT EXISTS(SELECT 1 FROM emr_encounter_assignments WHERE encounter_id=@encounter AND doctor_id=@doctor)", null, ("encounter", encounter), ("doctor", doctor));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<bool> HasExtraction(Guid doctor, Guid extraction, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = Command(c, """
            SELECT EXISTS(SELECT 1 FROM emr_extraction_links l JOIN emr_notes n ON n.id=l.note_id
            JOIN emr_encounter_assignments a ON a.encounter_id=n.encounter_id
            WHERE a.doctor_id=@doctor AND l.extraction_id=@extraction)
            """, null, ("doctor", doctor), ("extraction", extraction));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<List<EncounterDto>> Encounters(Guid doctor, CancellationToken ct)
    {
        await using var c = await Open(ct);
        return await Query<EncounterDto>(c, """
            SELECT jsonb_build_object('id',e.id,'reference',e.reference,'patientReference',p.reference,
              'encounterDate',e.encounter_date,'context',e.context,'noteId',n.id,'version',n.current_version,
              'noteStatus',n.status,'extractionStatus',o.processing_status,'extractionId',l.extraction_id)
            FROM emr_encounters e JOIN emr_patients p ON p.id=e.patient_id
            JOIN emr_encounter_assignments a ON a.encounter_id=e.id
            LEFT JOIN LATERAL (SELECT * FROM emr_notes WHERE encounter_id=e.id ORDER BY updated_at DESC LIMIT 1) n ON true
            LEFT JOIN emr_outbox_events o ON o.note_id=n.id AND o.note_version=n.current_version
            LEFT JOIN emr_extraction_links l ON l.note_id=n.id AND l.note_version=n.current_version
            WHERE a.doctor_id=@doctor ORDER BY e.encounter_date DESC,e.reference
            """, ct, null, ("doctor", doctor));
    }
    public async Task<(List<QueueItemDto> Items, int Total)> QueueItems(
        Guid doctor, string? search, string? state, int limit, int offset, CancellationToken ct)
    {
        await using var c = await Open(ct);
        int clampedLimit = Math.Clamp(limit, 1, 100);
        int clampedOffset = Math.Max(0, offset);
        string like = string.IsNullOrWhiteSpace(search) ? "" : $"%{search.Trim()}%";
        string normalizedState = string.IsNullOrWhiteSpace(state) || state.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? "" : state.Trim().ToLowerInvariant();

        const string cte = """
            WITH projected AS (
              SELECT
                p.id AS patient_id,
                e.id AS encounter_id,
                n.id AS note_id,
                o.note_version,
                p.reference AS patient_reference,
                e.reference AS encounter_reference,
                e.encounter_date,
                o.occurred_at AS received_at,
                LEFT(BTRIM(REGEXP_REPLACE(v.source_text, E'[\\r\\n\\s]+', ' ', 'g')), 180) AS source_preview,
                COALESCE(array_length(regexp_split_to_array(NULLIF(BTRIM(v.source_text), ''), E'\\s+'), 1), 0) AS word_count,
                CASE WHEN v.signed_at IS NOT NULL THEN 'Signed' ELSE n.status END AS note_status,
                CASE
                  WHEN l.extraction_id IS NOT NULL AND r.extraction_id IS NOT NULL THEN 'Ready'
                  WHEN o.processing_status = 'Failed' OR o.publication_status = 'Failed' THEN 'Failed'
                  WHEN o.processing_status = 'Processing' THEN 'Extracting'
                  WHEN o.processing_status = 'Queued' OR o.publication_status = 'Published' THEN 'Queued'
                  ELSE 'Waiting'
                END AS queue_state,
                (o.processing_status = 'Needs Review' OR COALESCE(r.warning_count, 0) > 0 OR COALESCE(r.error_count, 0) > 0) AS verification_recommended,
                o.processing_status AS raw_processing_status,
                o.event_id,
                l.extraction_id,
                COALESCE(r.provider_metadata->>'provider', CASE WHEN r.adapter_type LIKE 'onnx%' THEN 'onnx' WHEN r.adapter_type LIKE '%foundry%' THEN 'foundry' END) AS provider,
                COALESCE(r.provider_metadata->>'model', r.model_deployment) AS model,
                COALESCE((r.provider_metadata->>'fallbackTriggered')::boolean, (r.provider_metadata->>'fallbackReason') IS NOT NULL, false) AS fallback_triggered,
                r.warning_count,
                r.error_count,
                COALESCE(o.processing_error, o.last_error) AS error,
                v.source_text AS searchable_text,
                COUNT(*) OVER (PARTITION BY p.id)::int AS patient_note_count
              FROM emr_outbox_events o
              JOIN emr_note_versions v ON v.note_id = o.note_id AND v.version = o.note_version
              JOIN emr_notes n ON n.id = o.note_id
              JOIN emr_encounters e ON e.id = n.encounter_id
              JOIN emr_patients p ON p.id = e.patient_id
              JOIN emr_encounter_assignments a ON a.encounter_id = e.id
              LEFT JOIN emr_extraction_links l ON l.event_id = o.event_id
              LEFT JOIN extraction_runs r ON r.extraction_id = l.extraction_id
              WHERE a.doctor_id = @doctor
            ),
            filtered AS (
              SELECT * FROM projected
              WHERE (@like = '' OR patient_reference ILIKE @like OR encounter_reference ILIKE @like OR note_id::text ILIKE @like OR event_id::text ILIKE @like OR searchable_text ILIKE @like)
                AND (@state = '' OR lower(queue_state) = @state)
            )
            """;

        await using var countCmd = Command(c, cte + " SELECT COUNT(*)::int FROM filtered", null,
            ("doctor", doctor), ("like", like), ("state", normalizedState));
        int total = (int)(await countCmd.ExecuteScalarAsync(ct))!;

        var items = await Query<QueueItemDto>(c, cte + """
            SELECT jsonb_build_object(
              'patientId', patient_id,
              'encounterId', encounter_id,
              'noteId', note_id,
              'noteVersion', note_version,
              'patientReference', patient_reference,
              'encounterReference', encounter_reference,
              'encounterDate', encounter_date,
              'receivedAt', received_at,
              'sourcePreview', source_preview,
              'wordCount', word_count,
              'noteStatus', note_status,
              'queueState', queue_state,
              'verificationRecommended', verification_recommended,
              'rawProcessingStatus', raw_processing_status,
              'eventId', event_id,
              'extractionId', extraction_id,
              'provider', provider,
              'model', model,
              'fallbackTriggered', fallback_triggered,
              'warningCount', warning_count,
              'errorCount', error_count,
              'error', error,
              'patientNoteCount', patient_note_count
            )
            FROM filtered
            ORDER BY received_at DESC, event_id DESC
            LIMIT @limit OFFSET @offset
            """, ct, null,
            ("doctor", doctor), ("like", like), ("state", normalizedState), ("limit", clampedLimit), ("offset", clampedOffset));

        return (items, total);
    }

    public async Task<QueueEntryHeaderDto?> QueueEntryHeader(
        Guid doctor, Guid noteId, int? requestedVersion, string? expectedPatientReference, CancellationToken ct)
    {
        await using var c = await Open(ct);
        int targetVersion = requestedVersion ?? 0;
        string expectedPatient = string.IsNullOrWhiteSpace(expectedPatientReference) ? "" : expectedPatientReference.Trim();

        return (await Query<QueueEntryHeaderDto>(c, """
            SELECT jsonb_build_object(
              'patientId', p.id,
              'noteId', n.id,
              'encounterId', e.id,
              'noteVersion', COALESCE(v.version, n.current_version),
              'patientReference', p.reference,
              'encounterReference', e.reference,
              'encounterDate', e.encounter_date,
              'context', e.context,
              'noteStatus', CASE WHEN v.signed_at IS NOT NULL THEN 'Signed' WHEN @ver > 0 THEN 'Draft' ELSE n.status END,
              'sourceText', COALESCE(v.source_text, n.draft_text),
              'sourceHash', v.source_hash,
              'signedAt', v.signed_at,
              'updatedAt', COALESCE(v.signed_at, v.created_at, n.updated_at),
              'queueState', CASE
                WHEN l.extraction_id IS NOT NULL AND r.extraction_id IS NOT NULL THEN 'Ready'
                WHEN o.processing_status = 'Failed' OR o.publication_status = 'Failed' THEN 'Failed'
                WHEN o.processing_status = 'Processing' THEN 'Extracting'
                WHEN o.processing_status = 'Queued' OR o.publication_status = 'Published' THEN 'Queued'
                ELSE 'Waiting'
              END,
              'verificationRecommended', (o.processing_status = 'Needs Review' OR COALESCE(r.warning_count, 0) > 0 OR COALESCE(r.error_count, 0) > 0),
              'rawProcessingStatus', o.processing_status,
              'eventId', o.event_id,
              'extractionId', l.extraction_id,
              'error', COALESCE(o.processing_error, o.last_error)
            )
            FROM emr_notes n
            JOIN emr_encounters e ON e.id = n.encounter_id
            JOIN emr_patients p ON p.id = e.patient_id
            JOIN emr_encounter_assignments a ON a.encounter_id = e.id
            LEFT JOIN emr_note_versions v ON v.note_id = n.id AND v.version = CASE WHEN @ver > 0 THEN @ver ELSE n.current_version END
            LEFT JOIN emr_outbox_events o ON o.note_id = n.id AND o.note_version = COALESCE(v.version, n.current_version)
            LEFT JOIN emr_extraction_links l ON l.note_id = n.id AND l.note_version = COALESCE(v.version, n.current_version)
            LEFT JOIN extraction_runs r ON r.extraction_id = l.extraction_id
            WHERE n.id = @note
              AND a.doctor_id = @doctor
              AND (@ver = 0 OR v.version IS NOT NULL)
              AND (@patient = '' OR lower(p.reference) = lower(@patient))
            """, ct, null, ("doctor", doctor), ("note", noteId), ("ver", targetVersion), ("patient", expectedPatient))).SingleOrDefault();
    }

    public async Task<List<PatientNoteOptionDto>> PatientNotesForPatient(Guid doctor, Guid patientId, CancellationToken ct)
    {
        await using var c = await Open(ct);
        return await Query<PatientNoteOptionDto>(c, """
            SELECT jsonb_build_object(
              'noteId', n.id,
              'encounterId', e.id,
              'encounterReference', e.reference,
              'noteVersion', v.version,
              'receivedAt', COALESCE(v.signed_at, v.created_at),
              'noteStatus', CASE WHEN v.signed_at IS NOT NULL THEN 'Signed' ELSE 'Draft' END,
              'queueState', CASE
                WHEN l.extraction_id IS NOT NULL AND r.extraction_id IS NOT NULL THEN 'Ready'
                WHEN o.processing_status = 'Failed' OR o.publication_status = 'Failed' THEN 'Failed'
                WHEN o.processing_status = 'Processing' THEN 'Extracting'
                WHEN o.processing_status = 'Queued' OR o.publication_status = 'Published' THEN 'Queued'
                ELSE 'Waiting'
              END,
              'extractionId', l.extraction_id
            )
            FROM emr_notes n
            JOIN emr_encounters e ON e.id = n.encounter_id
            JOIN emr_encounter_assignments a ON a.encounter_id = e.id
            JOIN emr_note_versions v ON v.note_id = n.id
            LEFT JOIN emr_outbox_events o ON o.note_id = n.id AND o.note_version = v.version
            LEFT JOIN emr_extraction_links l ON l.note_id = n.id AND l.note_version = v.version
            LEFT JOIN extraction_runs r ON r.extraction_id = l.extraction_id
            WHERE e.patient_id = @patient
              AND a.doctor_id = @doctor
              AND (v.signed_at IS NOT NULL OR v.version = n.current_version)
            ORDER BY COALESCE(v.signed_at, v.created_at) DESC, v.version DESC
            LIMIT 25
            """, ct, null, ("doctor", doctor), ("patient", patientId));
    }

    public async Task<(Guid PatientId, string PatientReference, List<PatientNoteOptionDto> Notes)?> PatientScope(
        Guid doctor, string patientReference, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = Command(c, """
            SELECT p.id, p.reference
            FROM emr_patients p
            JOIN emr_encounters e ON e.patient_id = p.id
            JOIN emr_encounter_assignments a ON a.encounter_id = e.id
            WHERE lower(p.reference) = lower(@ref) AND a.doctor_id = @doctor
            LIMIT 1
            """, null, ("ref", patientReference.Trim()), ("doctor", doctor));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        Guid pid = reader.GetGuid(0);
        string pref = reader.GetString(1);
        await reader.CloseAsync();
        var notes = await PatientNotesForPatient(doctor, pid, ct);
        return (pid, pref, notes);
    }

    public async Task<NoteDto?> Note(Guid doctor, Guid note, CancellationToken ct)
    {
        await using var c = await Open(ct);
        return (await Query<NoteDto>(c, """
            SELECT jsonb_build_object('id',n.id,'encounterId',e.id,'patientReference',p.reference,
              'encounterReference',e.reference,'encounterDate',e.encounter_date,'context',e.context,
              'text',n.draft_text,'version',n.current_version,'status',n.status,'updatedAt',n.updated_at,
              'signedAt',v.signed_at,'extractionStatus',o.processing_status,'extractionId',l.extraction_id,
              'error',coalesce(o.processing_error,o.last_error))
            FROM emr_notes n JOIN emr_encounters e ON e.id=n.encounter_id
            JOIN emr_patients p ON p.id=e.patient_id
            JOIN emr_encounter_assignments a ON a.encounter_id=e.id
            LEFT JOIN emr_note_versions v ON v.note_id=n.id AND v.version=n.current_version
            LEFT JOIN emr_outbox_events o ON o.note_id=n.id AND o.note_version=n.current_version
            LEFT JOIN emr_extraction_links l ON l.note_id=n.id AND l.note_version=n.current_version
            WHERE n.id=@note AND a.doctor_id=@doctor
            """, ct, null, ("doctor", doctor), ("note", note))).SingleOrDefault();
    }
    public async Task<Guid> CreateDraft(Guid doctor, Guid encounter, string text, CancellationToken ct, string? attachmentName=null, byte[]? attachmentContent=null)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        Guid id = Guid.NewGuid();
        int count = await Execute(c, """
            INSERT INTO emr_notes(id,encounter_id,author_id,current_version,status,draft_text)
            SELECT @id,@encounter,@doctor,1,'Draft',@text WHERE EXISTS
            (SELECT 1 FROM emr_encounter_assignments WHERE encounter_id=@encounter AND doctor_id=@doctor)
            """, ct, tx, ("id", id), ("encounter", encounter), ("doctor", doctor), ("text", text));
        if (count == 0) throw new EmrNotFoundException();
        if(attachmentName is not null && attachmentContent is not null)
            await Execute(c,"INSERT INTO emr_attachments(id,note_id,file_name,content,content_hash) VALUES(@attachment,@id,@name,@content,@hash)",ct,tx,
                ("attachment",Guid.NewGuid()),("id",id),("name",attachmentName),("content",attachmentContent),("hash",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(attachmentContent))));
        await Snapshot(c, tx, id, false, ct); await tx.CommitAsync(ct); return id;
    }
    public async Task UpdateDraft(Guid doctor, Guid id, string text, int expected, bool correction, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        int count = await Execute(c, """
            UPDATE emr_notes n SET draft_text=@text,current_version=current_version+1,status='Draft',updated_at=now()
            WHERE n.id=@id AND n.author_id=@doctor AND n.current_version=@expected AND n.status=@status
            AND EXISTS(SELECT 1 FROM emr_encounter_assignments a WHERE a.encounter_id=n.encounter_id AND a.doctor_id=@doctor)
            """, ct, tx, ("id", id), ("doctor", doctor), ("text", text), ("expected", expected), ("status", correction ? "Signed" : "Draft"));
        if (count == 0) throw new EmrConflictException();
        await Snapshot(c, tx, id, false, ct); await tx.CommitAsync(ct);
    }
    public async Task Sign(Guid doctor, Guid id, int expected, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        int count = await Execute(c, """
            UPDATE emr_notes n SET current_version=current_version+1,status='Signed',updated_at=now()
            WHERE n.id=@id AND n.author_id=@doctor AND n.current_version=@expected AND n.status='Draft'
            AND EXISTS(SELECT 1 FROM emr_encounter_assignments a WHERE a.encounter_id=n.encounter_id AND a.doctor_id=@doctor)
            """, ct, tx, ("id", id), ("doctor", doctor), ("expected", expected));
        if (count == 0) throw new EmrConflictException();
        await Snapshot(c, tx, id, true, ct);
        await Execute(c, """
            INSERT INTO emr_outbox_events(event_id,event_type,schema_version,note_id,note_version,correlation_id)
            SELECT @event,'ClinicalNoteSigned',1,id,current_version,@correlation FROM emr_notes WHERE id=@id
            """, ct, tx, ("event", Guid.NewGuid()), ("correlation", Guid.NewGuid()), ("id", id));
        await tx.CommitAsync(ct);
    }
    private static async Task Snapshot(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, bool signed, CancellationToken ct)
    {
        // Hash is computed over the exact stored text, without normalization.
        await using var read = Command(c, "SELECT draft_text FROM emr_notes WHERE id=@id", tx, ("id", id));
        string text = (string)(await read.ExecuteScalarAsync(ct))!;
        await Execute(c, """
            INSERT INTO emr_note_versions(note_id,version,author_id,encounter_id,source_text,source_hash,signed_at)
            SELECT id,current_version,author_id,encounter_id,draft_text,@hash,CASE WHEN @signed THEN now() END FROM emr_notes WHERE id=@id;
            INSERT INTO emr_version_attachments(note_id,version,attachment_id)
            SELECT n.id,n.current_version,a.id FROM emr_notes n JOIN emr_attachments a ON a.note_id=n.id WHERE n.id=@id;
            """, ct, tx, ("id", id), ("hash", EmrHash.Text(text)), ("signed", signed));
    }
    public async Task<Guid> Attach(Guid doctor, Guid note, int expected, string name, byte[] bytes, string text, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        Guid attachment = Guid.NewGuid();
        int count = await Execute(c, """
            UPDATE emr_notes n SET draft_text=@text,current_version=current_version+1,updated_at=now()
            WHERE id=@note AND author_id=@doctor AND current_version=@expected AND status='Draft'
            AND EXISTS(SELECT 1 FROM emr_encounter_assignments a WHERE a.encounter_id=n.encounter_id AND a.doctor_id=@doctor)
            """, ct, tx, ("note", note), ("doctor", doctor), ("expected", expected), ("text", text));
        if(count == 0) throw new EmrConflictException();
        await Execute(c, "INSERT INTO emr_attachments(id,note_id,file_name,content,content_hash) VALUES(@id,@note,@name,@content,@hash)", ct, tx,
            ("id", attachment), ("note", note), ("name", name), ("content", bytes), ("hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
        await Snapshot(c, tx, note, false, ct); await tx.CommitAsync(ct); return attachment;
    }
    public async Task<List<OutboxClaim>> Claim(CancellationToken ct)
    {
        await using var c = await Open(ct); Guid lease = Guid.NewGuid();
        return await Query<OutboxClaim>(c, """
            WITH eligible AS (SELECT event_id FROM emr_outbox_events
              WHERE published_at IS NULL AND attempt_count<8 AND next_retry_at<=now()
              AND (lease_until IS NULL OR lease_until<now()) ORDER BY occurred_at FOR UPDATE SKIP LOCKED LIMIT 10),
            claimed AS (UPDATE emr_outbox_events o SET publication_status='Publishing',lease_id=@lease,
              lease_until=now()+interval '2 minutes',attempt_count=attempt_count+1 FROM eligible e
              WHERE o.event_id=e.event_id RETURNING o.*)
            SELECT jsonb_build_object('event',jsonb_build_object('eventId',event_id,'eventType',event_type,
              'schemaVersion',schema_version,'noteId',note_id,'noteVersion',note_version,'correlationId',correlation_id,
              'occurredAtUtc',occurred_at),'leaseId',lease_id,'attemptCount',attempt_count) FROM claimed
            """, ct, null, ("lease", lease));
    }
    public async Task Publication(OutboxClaim claim, bool acknowledged, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await Execute(c, """
            UPDATE emr_outbox_events SET publication_status=CASE WHEN @ack THEN 'Published' WHEN attempt_count>=8 THEN 'Failed' ELSE 'Pending Publication' END,
              published_at=CASE WHEN @ack THEN now() END, lease_id=NULL,lease_until=NULL,
              processing_status=CASE WHEN @ack AND processing_status='Pending Publication' THEN 'Queued'
                WHEN NOT @ack AND attempt_count>=8 AND processing_status='Pending Publication' THEN 'Failed' ELSE processing_status END,
              last_error=CASE WHEN @ack THEN NULL ELSE 'BROKER_UNAVAILABLE' END,
              next_retry_at=now()+make_interval(secs=>least(300,power(2,attempt_count)::int))
            WHERE event_id=@id AND lease_id=@lease
            """, ct, null, ("ack", acknowledged), ("id", claim.Event.EventId), ("lease", claim.LeaseId));
    }
}
