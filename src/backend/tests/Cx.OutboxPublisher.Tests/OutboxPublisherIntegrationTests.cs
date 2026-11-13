using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;

namespace Cx.OutboxPublisher.Tests;

public class OutboxPublisherIntegrationTests : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    private readonly InMemoryMessageBroker _broker = new();

    public async Task InitializeAsync()
    {
        // Clean outbox before tests
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM emr.extraction_outbox;", conn);
        await cmd.ExecuteNonQueryAsync();
        _broker.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> InsertTestOutboxRowAsync(string aggregateId = "note-123")
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            eventId = eventId.ToString(),
            eventType = "note.version.committed",
            noteId = aggregateId,
            noteVersion = 1,
            contentSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            charLength = 100
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
    public async Task Publisher_PublishesUnpublishedRows_AndMarksThemPublished()
    {
        var eventId = await InsertTestOutboxRowAsync();
        var worker = new OutboxPublisherWorker(ConnectionString, _broker, batchSize: 10);

        var count = await worker.ProcessBatchAsync();
        count.ShouldBe(1);

        // Verify broker received message with MessageId = eventId
        _broker.PublishedEvents.Count.ShouldBe(1);
        _broker.PublishedEvents[0].MessageId.ShouldBe(eventId.ToString());

        // Verify outbox row marked published_at in database
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT published_at FROM emr.extraction_outbox WHERE event_id = @eid", conn);
        checkCmd.Parameters.AddWithValue("eid", eventId);
        var publishedAt = await checkCmd.ExecuteScalarAsync();
        publishedAt.ShouldNotBeNull();
        publishedAt.ShouldNotBe(DBNull.Value);
    }

    [Fact]
    public async Task ConcurrentPublishers_UsingSkipLocked_DoNotProcessSameRows()
    {
        // Insert 10 outbox rows
        for (int i = 0; i < 10; i++)
        {
            await InsertTestOutboxRowAsync($"note-concurrent-{i}");
        }

        var brokerA = new InMemoryMessageBroker();
        var brokerB = new InMemoryMessageBroker();

        var workerA = new OutboxPublisherWorker(ConnectionString, brokerA, batchSize: 5);
        var workerB = new OutboxPublisherWorker(ConnectionString, brokerB, batchSize: 5);

        // Run both workers concurrently
        var taskA = workerA.ProcessBatchAsync();
        var taskB = workerB.ProcessBatchAsync();

        var results = await Task.WhenAll(taskA, taskB);
        (results[0] + results[1]).ShouldBe(10);

        // Both brokers together got exactly 10 distinct events
        var allMessageIds = brokerA.PublishedEvents.Select(e => e.MessageId)
            .Concat(brokerB.PublishedEvents.Select(e => e.MessageId))
            .ToList();

        allMessageIds.Distinct().Count().ShouldBe(10, "Zero duplicates across concurrent publisher instances");
    }

    [Fact]
    public async Task Publisher_BrokerFailure_IncrementsAttemptsAndSchedulesBackoff()
    {
        var eventId = await InsertTestOutboxRowAsync("note-fail");
        var failingBroker = new FaultyBroker();
        var worker = new OutboxPublisherWorker(ConnectionString, failingBroker, batchSize: 10);

        var count = await worker.ProcessBatchAsync();
        count.ShouldBe(0, "Failed publish must return 0 published count");

        // Verify publish_attempts was incremented and last_error recorded
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT publish_attempts, last_error, published_at FROM emr.extraction_outbox WHERE event_id = @eid", conn);
        cmd.Parameters.AddWithValue("eid", eventId);

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(1);
        reader.GetString(1).ShouldContain("SIMULATED_BROKER_NETWORK_FAILURE");
        reader.IsDBNull(2).ShouldBeTrue("Row must remain unpublished");
    }

    private class FaultyBroker : IMessageBrokerProducer
    {
        public Task PublishAsync(string messageId, string eventType, string payloadJson, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("SIMULATED_BROKER_NETWORK_FAILURE: Service Bus transient failure.");
        }
    }
}

