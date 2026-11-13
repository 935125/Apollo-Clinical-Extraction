using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cx.EmrSimulator.Models;
using Npgsql;

namespace Cx.EmrSimulator.Services;

public partial class EmrNoteService
{
    private readonly string _connectionString;
    private const string SourceSystem = "EMR_HOSP_MAIN";

    public EmrNoteService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("EmrDatabase") 
            ?? "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    }

    public async Task<NoteVersionResponse> CreateNoteAsync(string encounterId, NoteWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Note text cannot be empty.", nameof(request.Text));

        if (request.Text.Length > 65536)
            throw new ArgumentOutOfRangeException(nameof(request.Text), "Note length exceeds maximum allowed limit (64 KiB).");

        var noteId = Guid.NewGuid().ToString();
        var noteVersion = 1;
        var sha256Bytes = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text));
        var sha256Hex = Convert.ToHexStringLower(sha256Bytes);
        var committedAt = DateTime.UtcNow;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // 1. Verify encounter exists
        await using var encCmd = new NpgsqlCommand(
            "SELECT patient_id, facility_id, specialty_code FROM emr.encounter WHERE encounter_id = @encId", conn, tx);
        encCmd.Parameters.AddWithValue("encId", encounterId);
        await using var reader = await encCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new KeyNotFoundException($"Encounter '{encounterId}' not found.");
        }
        var patientId = reader.GetString(0);
        var facilityId = reader.GetString(1);
        var specialtyCode = reader.IsDBNull(2) ? null : reader.GetString(2);
        await reader.CloseAsync();

        // 2. Insert clinical_note
        await using var noteCmd = new NpgsqlCommand(
            "INSERT INTO emr.clinical_note (note_id, encounter_id, note_type, current_version, created_at, updated_at) " +
            "VALUES (@noteId, @encId, @type, @ver, @now, @now)", conn, tx);
        noteCmd.Parameters.AddWithValue("noteId", noteId);
        noteCmd.Parameters.AddWithValue("encId", encounterId);
        noteCmd.Parameters.AddWithValue("type", request.NoteType);
        noteCmd.Parameters.AddWithValue("ver", noteVersion);
        noteCmd.Parameters.AddWithValue("now", committedAt);
        await noteCmd.ExecuteNonQueryAsync();

        // 3. Insert clinical_note_version
        await using var verCmd = new NpgsqlCommand(
            "INSERT INTO emr.clinical_note_version (note_id, note_version, note_text, content_sha256, authored_by, committed_at) " +
            "VALUES (@noteId, @ver, @text, @sha, @auth, @now)", conn, tx);
        verCmd.Parameters.AddWithValue("noteId", noteId);
        verCmd.Parameters.AddWithValue("ver", noteVersion);
        verCmd.Parameters.AddWithValue("text", request.Text);
        verCmd.Parameters.AddWithValue("sha", sha256Bytes);
        verCmd.Parameters.AddWithValue("auth", request.AuthoredBy);
        verCmd.Parameters.AddWithValue("now", committedAt);
        await verCmd.ExecuteNonQueryAsync();

        // 4. Insert transactional outbox event (SAME TRANSACTION)
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var outboxPayload = new
        {
            eventId = eventId.ToString(),
            eventType = "note.version.committed",
            schemaVersion = "1.0",
            occurredAt = committedAt.ToString("O"),
            correlationId = correlationId,
            sourceSystem = SourceSystem,
            patientId = patientId,
            encounterId = encounterId,
            noteId = noteId,
            noteVersion = noteVersion,
            contentSha256 = sha256Hex,
            charLength = request.Text.Length,
            noteType = request.NoteType,
            specialtyCode = specialtyCode,
            facilityId = facilityId,
            authoredBy = request.AuthoredBy,
            committedAt = committedAt.ToString("O"),
            trigger = "realtime"
        };

        await using var outboxCmd = new NpgsqlCommand(
            "INSERT INTO emr.extraction_outbox (event_id, event_type, schema_version, aggregate_id, payload, correlation_id, created_at, next_attempt_at) " +
            "VALUES (@eid, 'note.version.committed', '1.0', @aggId, @payload::jsonb, @corr, @now, @now)", conn, tx);
        outboxCmd.Parameters.AddWithValue("eid", eventId);
        outboxCmd.Parameters.AddWithValue("aggId", noteId);
        outboxCmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(outboxPayload));
        outboxCmd.Parameters.AddWithValue("corr", correlationId);
        outboxCmd.Parameters.AddWithValue("now", committedAt);
        await outboxCmd.ExecuteNonQueryAsync();

        // Commit both note and outbox atomically
        await tx.CommitAsync();

        return new NoteVersionResponse(
            SourceSystem, patientId, encounterId, noteId, noteVersion,
            request.NoteType, specialtyCode, facilityId, request.AuthoredBy,
            request.Text, sha256Hex, committedAt
        );
    }

    public async Task<NoteVersionResponse> AmendNoteAsync(string noteId, int expectedVersion, NoteWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Note text cannot be empty.", nameof(request.Text));

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // 1. Fetch current note with row lock
        await using var getCmd = new NpgsqlCommand(
            "SELECT n.encounter_id, n.note_type, n.current_version, e.patient_id, e.facility_id, e.specialty_code " +
            "FROM emr.clinical_note n " +
            "JOIN emr.encounter e ON e.encounter_id = n.encounter_id " +
            "WHERE n.note_id = @noteId FOR UPDATE", conn, tx);
        getCmd.Parameters.AddWithValue("noteId", noteId);

        await using var reader = await getCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new KeyNotFoundException($"Note '{noteId}' not found.");
        }

        var encounterId = reader.GetString(0);
        var noteType = reader.GetString(1);
        var currentVersion = reader.GetInt32(2);
        var patientId = reader.GetString(3);
        var facilityId = reader.GetString(4);
        var specialtyCode = reader.IsDBNull(5) ? null : reader.GetString(5);
        await reader.CloseAsync();

        if (currentVersion != expectedVersion)
        {
            throw new InvalidOperationException($"NOTE_VERSION_STALE: Current version is {currentVersion}, but expected {expectedVersion}.");
        }

        var nextVersion = currentVersion + 1;
        var committedAt = DateTime.UtcNow;
        var sha256Bytes = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text));
        var sha256Hex = Convert.ToHexStringLower(sha256Bytes);

        // 2. Update clinical_note current version
        await using var updateCmd = new NpgsqlCommand(
            "UPDATE emr.clinical_note SET current_version = @ver, updated_at = @now WHERE note_id = @noteId", conn, tx);
        updateCmd.Parameters.AddWithValue("ver", nextVersion);
        updateCmd.Parameters.AddWithValue("now", committedAt);
        updateCmd.Parameters.AddWithValue("noteId", noteId);
        await updateCmd.ExecuteNonQueryAsync();

        // 3. Insert new clinical_note_version
        await using var verCmd = new NpgsqlCommand(
            "INSERT INTO emr.clinical_note_version (note_id, note_version, note_text, content_sha256, authored_by, committed_at) " +
            "VALUES (@noteId, @ver, @text, @sha, @auth, @now)", conn, tx);
        verCmd.Parameters.AddWithValue("noteId", noteId);
        verCmd.Parameters.AddWithValue("ver", nextVersion);
        verCmd.Parameters.AddWithValue("text", request.Text);
        verCmd.Parameters.AddWithValue("sha", sha256Bytes);
        verCmd.Parameters.AddWithValue("auth", request.AuthoredBy);
        verCmd.Parameters.AddWithValue("now", committedAt);
        await verCmd.ExecuteNonQueryAsync();

        // 4. Insert outbox event
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var outboxPayload = new
        {
            eventId = eventId.ToString(),
            eventType = "note.version.committed",
            schemaVersion = "1.0",
            occurredAt = committedAt.ToString("O"),
            correlationId = correlationId,
            sourceSystem = SourceSystem,
            patientId = patientId,
            encounterId = encounterId,
            noteId = noteId,
            noteVersion = nextVersion,
            contentSha256 = sha256Hex,
            charLength = request.Text.Length,
            noteType = noteType,
            specialtyCode = specialtyCode,
            facilityId = facilityId,
            authoredBy = request.AuthoredBy,
            committedAt = committedAt.ToString("O"),
            trigger = "realtime"
        };

        await using var outboxCmd = new NpgsqlCommand(
            "INSERT INTO emr.extraction_outbox (event_id, event_type, schema_version, aggregate_id, payload, correlation_id, created_at, next_attempt_at) " +
            "VALUES (@eid, 'note.version.committed', '1.0', @aggId, @payload::jsonb, @corr, @now, @now)", conn, tx);
        outboxCmd.Parameters.AddWithValue("eid", eventId);
        outboxCmd.Parameters.AddWithValue("aggId", noteId);
        outboxCmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(outboxPayload));
        outboxCmd.Parameters.AddWithValue("corr", correlationId);
        outboxCmd.Parameters.AddWithValue("now", committedAt);
        await outboxCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();

        return new NoteVersionResponse(
            SourceSystem, patientId, encounterId, noteId, nextVersion,
            noteType, specialtyCode, facilityId, request.AuthoredBy,
            request.Text, sha256Hex, committedAt
        );
    }

    public async Task<NoteVersionResponse?> GetNoteVersionAsync(string noteId, int version)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT n.encounter_id, n.note_type, v.note_version, e.patient_id, e.facility_id, e.specialty_code, " +
            "v.authored_by, v.note_text, v.content_sha256, v.committed_at " +
            "FROM emr.clinical_note_version v " +
            "JOIN emr.clinical_note n ON n.note_id = v.note_id " +
            "JOIN emr.encounter e ON e.encounter_id = n.encounter_id " +
            "WHERE v.note_id = @noteId AND v.note_version = @ver", conn);
        cmd.Parameters.AddWithValue("noteId", noteId);
        cmd.Parameters.AddWithValue("ver", version);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var sha256Bytes = (byte[])reader["content_sha256"];
        return new NoteVersionResponse(
            SourceSystem,
            reader.GetString(3),
            reader.GetString(0),
            noteId,
            reader.GetInt32(2),
            reader.GetString(1),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(4),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            Convert.ToHexStringLower(sha256Bytes),
            reader.GetDateTime(9)
        );
    }
}

