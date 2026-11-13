using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;

namespace Cx.OutboxPublisher.Tests;

public class OutboxPublisherReliabilityTests
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";

    private async Task<Guid> InsertOutboxRowAsync(string aggregateId = "note-test")
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            eventId = eventId.ToString(),
            eventType = "note.version.committed",
            noteId = aggregateId,
            noteVersion = 1,
            contentSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        });

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO emr.extraction_outbox (event_id, event_type, schema_version, aggregate_id, payload, correlation_id, created_at, next_attempt_at)
            VALUES (@eid, 'note.version.committed', '1.0', @aggId, @payload::jsonb, @corr, now(), now())", conn);
        cmd.Parameters.AddWithValue("eid", eventId);
        cmd.Parameters.AddWithValue("aggId", aggregateId);
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("corr", correlationId);
        await cmd.ExecuteNonQueryAsync();

        return eventId;
    }

    [Fact]
    public async Task Reliability1_CrashAfterBrokerAcceptance_LeavesOutboxEventRecoverableForRePublishing()
    {
        // Clean outbox for this isolated reliability test
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var delCmd = new NpgsqlCommand("DELETE FROM emr.extraction_outbox;", conn);
            await delCmd.ExecuteNonQueryAsync();
        }

        var eventId = await InsertOutboxRowAsync("note-crash-sim");

        var crashingBroker = new CrashAfterPublishBroker();
        var workerA = new OutboxPublisherWorker(
            ConnectionString, crashingBroker, publisherId: "worker-A", leaseDuration: TimeSpan.FromMilliseconds(500));

        var publishedA = await workerA.ProcessBatchAsync();
        publishedA.ShouldBe(0, "Worker A failed to publish cleanly");
        crashingBroker.PublishedEvents.Count.ShouldBe(1, "Broker accepted message before publisher failure");

        // Verify row remains unpublished
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT published_at FROM emr.extraction_outbox WHERE event_id = @eid", conn);
            cmd.Parameters.AddWithValue("eid", eventId);
            var pubAt = await cmd.ExecuteScalarAsync();
            pubAt.ShouldBe(DBNull.Value);

            // Reset next_attempt_at so worker B can immediately claim
            await using var resetCmd = new NpgsqlCommand("UPDATE emr.extraction_outbox SET next_attempt_at = now() WHERE event_id = @eid", conn);
            resetCmd.Parameters.AddWithValue("eid", eventId);
            await resetCmd.ExecuteNonQueryAsync();
        }

        // Wait for lease to expire
        await Task.Delay(650);

        // Worker B claims the un-acknowledged row and publishes it
        var workerB = new OutboxPublisherWorker(ConnectionString, crashingBroker, publisherId: "worker-B");
        var publishedB = await workerB.ProcessBatchAsync();
        publishedB.ShouldBeGreaterThanOrEqualTo(1, "Event must be recovered by worker B");

        // Broker received 2 publishes for same eventId (broker deduplication via MessageId)
        crashingBroker.PublishedEvents.Count.ShouldBeGreaterThanOrEqualTo(2);
        crashingBroker.PublishedEvents.Count(e => e.MessageId == eventId.ToString()).ShouldBe(2);

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var checkCmd = new NpgsqlCommand(
                "SELECT published_at, lease_owner FROM emr.extraction_outbox WHERE event_id = @eid", conn);
            checkCmd.Parameters.AddWithValue("eid", eventId);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.IsDBNull(0).ShouldBeFalse("Row must be marked published");
            reader.IsDBNull(1).ShouldBeTrue("Lease owner must be cleared");
        }
    }

    private class CrashAfterPublishBroker : IMessageBrokerProducer
    {
        public List<PublishedEvent> PublishedEvents { get; } = new();

        public Task PublishAsync(string messageId, string eventType, string payloadJson, CancellationToken cancellationToken)
        {
            PublishedEvents.Add(new PublishedEvent(messageId, eventType, payloadJson, DateTime.UtcNow));
            if (PublishedEvents.Count == 1)
            {
                // Throw error simulating network/publisher failure
                throw new HttpRequestException("SIMULATED_PROCESS_CRASH_AFTER_BROKER_ACCEPTANCE");
            }
            return Task.CompletedTask;
        }
    }
}
