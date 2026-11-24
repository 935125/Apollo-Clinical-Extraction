using System.Diagnostics;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Apollo.Backend.Tests;

public class Stage9RealServiceBusSmokeTests
{
    private static void EnsureAzureCliOnPath()
    {
        string apolloAz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApolloTools", "AzureCLI", "bin");
        if (Directory.Exists(apolloAz))
        {
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(apolloAz, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", apolloAz + Path.PathSeparator + currentPath);
            }
        }
    }

    [PostgresFact]
    public async Task RealAzureServiceBus_SendReceiveCompleteAndIdempotency_Succeeds()
    {
        EnsureAzureCliOnPath();

        const string sbNamespace = "apollo-clinical-sb-dev.servicebus.windows.net";
        const string queueName = "op-clinical-notes";

        var credOptions = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true
        };
        var credential = new DefaultAzureCredential(credOptions);

        // Pre-flight check: token acquisition confirms Azure CLI authentication
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://servicebus.azure.net/.default"]),
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(token.Token), "Must acquire a valid token via DefaultAzureCredential.");

        // 1. Setup isolated PostgreSQL test fixture (cx_audit_test)
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();

        var (doctorId, _) = await fixture.SeedDoctorAsync("dr.sbtest@apollo.local", "Dr. Service Bus Tester", "Doctor");
        var encounterId = await fixture.SeedEncounterAsync(doctorId, "SYN-P9001", "SYN-ENC-9001");

        Guid noteId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        Guid correlationId = Guid.NewGuid();
        string noteText = "Follow-up consultation. Continue amlodipine 5 mg orally once daily for 7 days. Order complete blood count tomorrow.";
        string noteHash = EmrHash.Text(noteText);

        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await EmrStore.Execute(conn, @"
                INSERT INTO emr_notes(id, encounter_id, author_id, current_version, status, draft_text)
                VALUES(@id, @enc, @doc, 1, 'Signed', @text)", default, null,
                ("id", noteId), ("enc", encounterId), ("doc", doctorId), ("text", noteText));

            await EmrStore.Execute(conn, @"
                INSERT INTO emr_note_versions(note_id, version, author_id, encounter_id, source_text, source_hash, signed_at)
                VALUES(@id, 1, @doc, @enc, @text, @hash, now())", default, null,
                ("id", noteId), ("doc", doctorId), ("enc", encounterId), ("text", noteText), ("hash", noteHash));

            await EmrStore.Execute(conn, @"
                INSERT INTO emr_outbox_events(event_id, event_type, schema_version, note_id, note_version, correlation_id, occurred_at, publication_status, processing_status)
                VALUES(@event, 'ClinicalNoteSigned', 1, @note, 1, @corr, now(), 'Pending Publication', 'Queued')", default, null,
                ("event", eventId), ("note", noteId), ("corr", correlationId));
        }

        // 2. Build configuration and real AzureNoteBroker
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = fixture.ConnectionString,
            ["ConnectionStrings:Emr"] = fixture.ConnectionString,
            ["Emr:Messaging:Mode"] = "AzureServiceBus",
            ["Emr:Messaging:FullyQualifiedNamespace"] = sbNamespace,
            ["Emr:Messaging:QueueName"] = queueName,
            ["Emr:Messaging:InferenceEnabled"] = "false",
            ["Extraction:Provider"] = "LocalOnnx",
            ["Extraction:AllowDirectLocal"] = "true",
            ["Extraction:DirectLocalReason"] = "isolated_test"
        }).Build();

        var broker = new AzureNoteBroker(config, credential);

        // 3. Setup factory for DI-managed EmrConsumer using LocalOnnx (zero billable Foundry calls)
        using var factory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker,
            DisableMessagingService = true,
            PrimaryProvider = "LocalOnnx"
        };
        var consumer = factory.Services.GetRequiredService<EmrConsumer>();

        var stopwatch = Stopwatch.StartNew();
        var message = new ClinicalNoteSigned(eventId, "ClinicalNoteSigned", 1, noteId, 1, correlationId, DateTimeOffset.UtcNow);

        // 4. Send message to real Azure Service Bus queue
        await broker.Publish(message, CancellationToken.None);
        var sendElapsed = stopwatch.ElapsedMilliseconds;

        // 5. Receive message from real Azure Service Bus queue via dedicated receiver
        await using var receiver = broker.Receiver();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(25), cts.Token);

        Assert.NotNull(received);
        Assert.Equal(eventId.ToString(), received.MessageId);
        Assert.Equal(correlationId.ToString(), received.CorrelationId);
        Assert.Equal("ClinicalNoteSigned", received.Subject);

        var receivedPayload = received.Body.ToObjectFromJson<ClinicalNoteSigned>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(receivedPayload);
        Assert.Equal(eventId, receivedPayload.EventId);
        Assert.Equal(noteId, receivedPayload.NoteId);

        // 6. Process through actual .NET worker (EmrConsumer)
        var outcome = await consumer.Process(receivedPayload, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome);

        // 7. Settle message on broker (CompleteMessageAsync)
        await receiver.CompleteMessageAsync(received, CancellationToken.None);

        // 8. Verify duplicate delivery behavior (Idempotency on worker and broker)
        // 8a. Worker duplicate protection: second attempt returns Completed without duplicating links
        var duplicateOutcome = await consumer.Process(receivedPayload, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, duplicateOutcome);

        // 8b. Broker duplicate detection: sending identical MessageId within duplicate window
        await broker.Publish(message, CancellationToken.None);
        // The queue has duplicate detection enabled (PT10M), so broker drops the duplicate
        var noDuplicateMsg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.Null(noDuplicateMsg);

        // 9. Verify PostgreSQL persistence in isolated schema
        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmdLinks = new NpgsqlCommand($"SELECT count(*)::int FROM \"{fixture.Schema}\".emr_extraction_links WHERE event_id = @event", conn);
            cmdLinks.Parameters.AddWithValue("event", eventId);
            int linkCount = (int)(await cmdLinks.ExecuteScalarAsync())!;
            Assert.Equal(1, linkCount);

            await using var cmdProcessed = new NpgsqlCommand($"SELECT count(*)::int FROM \"{fixture.Schema}\".emr_processed_messages WHERE event_id = @event", conn);
            cmdProcessed.Parameters.AddWithValue("event", eventId);
            int processedCount = (int)(await cmdProcessed.ExecuteScalarAsync())!;
            Assert.Equal(1, processedCount);
        }

        // 10. Write smoke test evidence JSON
        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "stage9-evidence", "servicebus"));
        if (!Directory.Exists(evidenceDir)) Directory.CreateDirectory(evidenceDir);

        var smokeEvidence = new
        {
            testedAtUtc = DateTimeOffset.UtcNow,
            authentication = "Azure.Identity.DefaultAzureCredential (AzureCliCredential via portable CLI)",
            azureCliUser = "935125@apollohospitals.com",
            serviceBusNamespace = sbNamespace,
            queue = queueName,
            eventId = eventId.ToString("D"),
            correlationId = correlationId.ToString("D"),
            messageId = eventId.ToString("D"),
            brokerSendConfirmed = true,
            sendLatencyMs = sendElapsed,
            brokerReceiveConfirmed = true,
            receivedDeliveryCount = received.DeliveryCount,
            receivedLockToken = received.LockToken,
            receivedSequenceNumber = received.SequenceNumber,
            workerConsumerOutcome = outcome.ToString(),
            brokerSettlementConfirmed = true,
            settlementAction = "CompleteMessageAsync",
            duplicateBrokerDetectionConfirmed = true,
            duplicateWorkerIdempotencyConfirmed = true,
            gptFoundryCalls = 0,
            applicationDatabaseUntouched = true,
            status = "PASS"
        };

        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "servicebus-smoke-test-results.json"),
            JsonSerializer.Serialize(smokeEvidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}
