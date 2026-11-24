using System.Text.Json;
using System.Threading.Channels;
using Apollo.Backend.Clinical;
using Apollo.Backend.Security;
using Apollo.Backend.Storage;
using Azure.Core;
using Azure.Messaging.ServiceBus;

namespace Apollo.Backend.Emr;

public interface INoteBroker
{
    Task Publish(ClinicalNoteSigned message,CancellationToken ct);
}
public sealed class DisabledNoteBroker : INoteBroker
{
    public Task Publish(ClinicalNoteSigned message,CancellationToken ct)=>throw new InvalidOperationException("BROKER_NOT_CONFIGURED");
}
/// <summary>Development-only simulation. Durable signed/outbox rows replay unprocessed deliveries after restart.</summary>
public sealed class SimulatedNoteBroker : INoteBroker
{
    public Channel<ClinicalNoteSigned> Messages {get;}=Channel.CreateUnbounded<ClinicalNoteSigned>();
    public Task Publish(ClinicalNoteSigned message,CancellationToken ct)=>Messages.Writer.WriteAsync(message,ct).AsTask();
}
public sealed class AzureNoteBroker : INoteBroker,IAsyncDisposable
{
    private readonly ServiceBusClient client;
    private readonly ServiceBusSender sender;
    private readonly string queueName;
    public string QueueName => queueName;
    public AzureNoteBroker(IConfiguration config,TokenCredential credential)
    {
        var options=new ServiceBusClientOptions { RetryOptions=new ServiceBusRetryOptions { MaxRetries=2,TryTimeout=TimeSpan.FromSeconds(20) } };
        string? connection=config["Emr:Messaging:ConnectionString"];
        queueName=config["Emr:Messaging:QueueName"] ?? "op-clinical-notes";
        client=string.IsNullOrWhiteSpace(connection)
            ? new ServiceBusClient(config["Emr:Messaging:FullyQualifiedNamespace"] ?? throw new InvalidOperationException("Configure the Service Bus namespace."),credential,options)
            : new ServiceBusClient(connection,options);
        sender=client.CreateSender(queueName);
    }
    public Task Publish(ClinicalNoteSigned message,CancellationToken ct)=>sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message,new JsonSerializerOptions(JsonSerializerDefaults.Web)))
    { MessageId=message.EventId.ToString(),CorrelationId=message.CorrelationId.ToString(),Subject="ClinicalNoteSigned",ContentType="application/json" },ct);
    public ServiceBusProcessor Processor()=>client.CreateProcessor(queueName,new ServiceBusProcessorOptions
    { AutoCompleteMessages=false,ReceiveMode=ServiceBusReceiveMode.PeekLock,MaxConcurrentCalls=2,PrefetchCount=0,MaxAutoLockRenewalDuration=TimeSpan.FromMinutes(10) });
    public ServiceBusReceiver Receiver()=>client.CreateReceiver(queueName,new ServiceBusReceiverOptions
    { ReceiveMode=ServiceBusReceiveMode.PeekLock });
    public async ValueTask DisposeAsync() { await sender.DisposeAsync(); await client.DisposeAsync(); }
}
public enum ConsumerOutcome { Completed,Retry,DeadLetter }

public sealed class EmrConsumer(EmrStore store,IExtractionRepository repository,ExtractionWorkflow workflow)
{
    public async Task<ConsumerOutcome> Process(ClinicalNoteSigned message,CancellationToken ct)
    {
        if(!message.IsValid) return ConsumerOutcome.DeadLetter;
        await using var c=await store.Open(ct);
        // Session lock is held across inference and persistence. No second worker may infer this event concurrently.
        await using var acquire=EmrStore.Command(c,"SELECT pg_try_advisory_lock(hashtextextended(@id,0))",null,("id",message.EventId.ToString()));
        if(!(bool)(await acquire.ExecuteScalarAsync(ct))!) return ConsumerOutcome.Retry;
        try
        {
            await using(var done=EmrStore.Command(c,"SELECT EXISTS(SELECT 1 FROM emr_processed_messages WHERE event_id=@id)",null,("id",message.EventId)))
                if((bool)(await done.ExecuteScalarAsync(ct))!) return ConsumerOutcome.Completed;
            var source=(await EmrStore.Query<SignedSource>(c,"""
                SELECT jsonb_build_object('text',v.source_text,'hash',v.source_hash,'encounterId',v.encounter_id)
                FROM emr_outbox_events o JOIN emr_note_versions v ON v.note_id=o.note_id AND v.version=o.note_version
                WHERE o.event_id=@event AND o.note_id=@note AND o.note_version=@version AND o.correlation_id=@correlation
                  AND o.event_type=@type AND o.schema_version=@schema AND v.signed_at IS NOT NULL
                """,ct,null,("event",message.EventId),("note",message.NoteId),("version",message.NoteVersion),
                ("correlation",message.CorrelationId),("type",message.EventType),("schema",message.SchemaVersion))).SingleOrDefault();
            if(source is null) return ConsumerOutcome.DeadLetter;
            if(EmrHash.Text(source.Text)!=source.Hash)
            { await Fail(c,message,"SOURCE_HASH_MISMATCH",ct); return ConsumerOutcome.DeadLetter; }
            await using(var check=EmrStore.Command(c,"SELECT processing_attempts FROM emr_outbox_events WHERE event_id=@id",null,("id",message.EventId)))
                if((int)(await check.ExecuteScalarAsync(ct))!>=5) return ConsumerOutcome.DeadLetter;
            await EmrStore.Execute(c,"UPDATE emr_outbox_events SET processing_status='Processing',processing_attempts=processing_attempts+1,processing_error=NULL WHERE event_id=@id",ct,null,("id",message.EventId));
            try
            {
                // Fixed UUID closes the crash gap between extraction save and linkage save.
                var result=await repository.GetExtractionAsync(message.EventId.ToString(),ct)
                    ?? await workflow.ExtractAndSaveAsync(source.Text,message.CorrelationId.ToString(),"emr",null,ct,message.EventId);
                if(result.NoteText!=source.Text || result.CorrelationId!=message.CorrelationId.ToString()) throw new InvalidEmrEventException();
                await using var tx=await c.BeginTransactionAsync(ct);
                await EmrStore.Execute(c,"""
                    INSERT INTO emr_extraction_links(note_id,note_version,event_id,extraction_id) VALUES(@note,@version,@event,@event) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_processed_messages(event_id) VALUES(@event) ON CONFLICT DO NOTHING;
                    UPDATE emr_outbox_events SET processing_status='Needs Review',processing_error=NULL WHERE event_id=@event;
                    """,ct,tx,("note",message.NoteId),("version",message.NoteVersion),("event",message.EventId));
                await tx.CommitAsync(ct); return ConsumerOutcome.Completed;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception error)
            {
                bool permanent=error is PIILeakageException or InvalidEmrEventException || error is ClinicalExtractionException {Status:>=400 and <500};
                string code=error is PIILeakageException?"SOURCE_REQUIRES_PRIVACY_REVIEW":error is ClinicalExtractionException clinical?clinical.Code:"EXTRACTION_UNAVAILABLE";
                await using var check=EmrStore.Command(c,"SELECT processing_attempts FROM emr_outbox_events WHERE event_id=@id",null,("id",message.EventId));
                bool terminal=permanent || (int)(await check.ExecuteScalarAsync(ct))!>=5;
                await EmrStore.Execute(c,"UPDATE emr_outbox_events SET processing_status=@status,processing_error=@error WHERE event_id=@id",ct,null,("status",terminal?"Failed":"Queued"),("error",code),("id",message.EventId));
                return terminal?ConsumerOutcome.DeadLetter:ConsumerOutcome.Retry;
            }
        }
        finally
        {
            if(c.State==System.Data.ConnectionState.Open)
                await EmrStore.Execute(c,"SELECT pg_advisory_unlock(hashtextextended(@id,0))",CancellationToken.None,null,("id",message.EventId.ToString()));
        }
    }
    private static Task<int> Fail(Npgsql.NpgsqlConnection c,ClinicalNoteSigned message,string code,CancellationToken ct)=>
        EmrStore.Execute(c,"UPDATE emr_outbox_events SET processing_status='Failed',processing_error=@error WHERE event_id=@id",ct,null,("id",message.EventId),("error",code));
}

public sealed class EmrMessagingService(IServiceProvider services,IConfiguration config,IHostEnvironment environment,ILogger<EmrMessagingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string mode=config["Emr:Messaging:Mode"] ?? "Disabled";
        if(mode is "Disabled" or "Custom") return;
        if(mode is not ("AzureServiceBus" or "Simulated") || mode=="Simulated"&&!environment.IsDevelopment())
            throw new InvalidOperationException("Invalid EMR messaging mode. Simulation is Development-only.");
        var store=services.GetRequiredService<EmrStore>();
        var broker=services.GetRequiredService<INoteBroker>();
        bool inference=config.GetValue("Emr:Messaging:InferenceEnabled",false);
        logger.LogInformation("EMR messaging mode {Mode}; inference enabled {Enabled}",mode,inference);
        await Task.WhenAll(PublishLoop(store,broker,stoppingToken),inference?ConsumeLoop(store,broker,stoppingToken):Task.CompletedTask);
    }
    public async Task PublishOnce(EmrStore store,INoteBroker broker,CancellationToken ct)
    {
        foreach(var claim in await store.Claim(ct))
        {
            bool acknowledged=false;
            try
            {
                logger.LogInformation("Publishing EMR outbox event {EventId} for note {NoteId} v{Version} (attempt {Attempt})", claim.Event.EventId, claim.Event.NoteId, claim.Event.NoteVersion, claim.AttemptCount);
                await broker.Publish(claim.Event,ct);
                acknowledged=true;
                logger.LogInformation("Successfully published EMR event {EventId} to broker", claim.Event.EventId);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception e) { logger.LogWarning("EMR publication failed for event {EventId}; error {Message} (type {Type})", claim.Event.EventId, e.Message, e.GetType().Name); }
            await store.Publication(claim,acknowledged,ct);
        }
    }
    private async Task PublishLoop(EmrStore store,INoteBroker broker,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try { await PublishOnce(store,broker,ct); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception e) { logger.LogWarning("EMR publisher unavailable; type {Type}",e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
    private async Task ConsumeLoop(EmrStore store,INoteBroker broker,CancellationToken ct)
    {
        if(broker is AzureNoteBroker azure)
        {
            await using var processor=azure.Processor();
            processor.ProcessMessageAsync+=async args=>
            {
                ClinicalNoteSigned? message;
                try { message=args.Message.Body.ToObjectFromJson<ClinicalNoteSigned>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch(JsonException) { await args.DeadLetterMessageAsync(args.Message,"INVALID_EVENT_SCHEMA","Invalid clinical event contract.",args.CancellationToken); return; }
                if(message is null || !message.IsValid || args.Message.MessageId!=message.EventId.ToString())
                { await args.DeadLetterMessageAsync(args.Message,"INVALID_EVENT_SCHEMA","Invalid clinical event contract.",args.CancellationToken); return; }
                logger.LogInformation("Received clinical event {EventId} for note {NoteId} v{Version}; processing...", message.EventId, message.NoteId, message.NoteVersion);
                using var scope = services.CreateScope();
                var consumer = scope.ServiceProvider.GetRequiredService<EmrConsumer>();
                var result=await consumer.Process(message,args.CancellationToken);
                logger.LogInformation("Clinical event {EventId} outcome: {Outcome}", message.EventId, result);
                if(result==ConsumerOutcome.Completed) await args.CompleteMessageAsync(args.Message,args.CancellationToken);
                else if(result==ConsumerOutcome.DeadLetter) await args.DeadLetterMessageAsync(args.Message,"CLINICAL_PROCESSING_FAILED","Inspect the protected processing state.",args.CancellationToken);
                else await args.AbandonMessageAsync(args.Message,cancellationToken:args.CancellationToken);
            };
            processor.ProcessErrorAsync+=args=>{ logger.LogWarning("EMR receiver failure; source {Source}, type {Type}",args.ErrorSource,args.Exception.GetType().Name); return Task.CompletedTask; };
            // Connection failures retry without stopping the API or losing saved notes.
            while(!ct.IsCancellationRequested)
            {
                try
                {
                    logger.LogInformation("Starting Azure Service Bus processor on queue {Queue}...", azure.QueueName);
                    await processor.StartProcessingAsync(ct);
                    logger.LogInformation("Azure Service Bus processor active; listening for notes");
                    await Task.Delay(Timeout.Infinite,ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception e)
                {
                    logger.LogWarning("EMR receiver unavailable; type {Type}; retrying in 15s", e.GetType().Name);
                    try { if (processor.IsProcessing) await processor.StopProcessingAsync(CancellationToken.None); } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(15),ct);
                }
            }
            try { if (processor.IsProcessing) await processor.StopProcessingAsync(CancellationToken.None); } catch { }
        }
        else if(broker is SimulatedNoteBroker simulated)
        {
            // Rehydrate acknowledged but unsettled simulation messages on restart.
            await using(var c=await store.Open(ct))
                foreach(var message in await EmrStore.Query<ClinicalNoteSigned>(c,"""
                    SELECT jsonb_build_object('eventId',event_id,'eventType',event_type,'schemaVersion',schema_version,
                      'noteId',note_id,'noteVersion',note_version,'correlationId',correlation_id,'occurredAtUtc',occurred_at)
                    FROM emr_outbox_events o WHERE published_at IS NOT NULL AND processing_status NOT IN ('Failed','Needs Review','Completed')
                    AND NOT EXISTS(SELECT 1 FROM emr_processed_messages m WHERE m.event_id=o.event_id)
                    """,ct)) await simulated.Publish(message,ct);
            await foreach(var message in simulated.Messages.Reader.ReadAllAsync(ct))
            {
                ConsumerOutcome outcome;
                try { outcome=await services.GetRequiredService<EmrConsumer>().Process(message,ct); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception e) { logger.LogWarning("Simulated EMR delivery deferred; type {Type}",e.GetType().Name); outcome=ConsumerOutcome.Retry; }
                if(outcome==ConsumerOutcome.Retry) { await Task.Delay(TimeSpan.FromSeconds(5),ct); await simulated.Publish(message,ct); }
            }
        }
    }
}
