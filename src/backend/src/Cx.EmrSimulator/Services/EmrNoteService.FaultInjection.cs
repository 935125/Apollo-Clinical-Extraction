using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cx.EmrSimulator.Models;
using Npgsql;

namespace Cx.EmrSimulator.Services;

public partial class EmrNoteService
{
    /// <summary>
    /// Explicit fault-injection method for verification testing:
    /// Simulates a failure during outbox event insertion to verify complete transactional rollback of the clinical note.
    /// </summary>
    public async Task CreateNoteWithFaultInjectedOutboxAsync(string encounterId, NoteWriteRequest request)
    {
        var noteId = Guid.NewGuid().ToString();
        var noteVersion = 1;
        var sha256Bytes = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text));
        var committedAt = DateTime.UtcNow;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // 1. Insert clinical_note
        await using var noteCmd = new NpgsqlCommand(
            "INSERT INTO emr.clinical_note (note_id, encounter_id, note_type, current_version, created_at, updated_at) " +
            "VALUES (@noteId, @encId, @type, @ver, @now, @now)", conn, tx);
        noteCmd.Parameters.AddWithValue("noteId", noteId);
        noteCmd.Parameters.AddWithValue("encId", encounterId);
        noteCmd.Parameters.AddWithValue("type", request.NoteType);
        noteCmd.Parameters.AddWithValue("ver", noteVersion);
        noteCmd.Parameters.AddWithValue("now", committedAt);
        await noteCmd.ExecuteNonQueryAsync();

        // 2. Insert clinical_note_version
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

        // 3. FAULT INJECTION: Simulate database or serialization crash during outbox insert
        throw new InvalidOperationException("FAULT_INJECTION_SIMULATED_OUTBOX_FAILURE: Transaction must roll back.");
        
        // tx.CommitAsync() is intentionally unreachable
    }
}

