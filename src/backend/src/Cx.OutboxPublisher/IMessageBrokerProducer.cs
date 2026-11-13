namespace Cx.OutboxPublisher;

public interface IMessageBrokerProducer
{
    Task PublishAsync(string messageId, string eventType, string payloadJson, CancellationToken cancellationToken);
}

public class InMemoryMessageBroker : IMessageBrokerProducer
{
    private readonly List<PublishedEvent> _publishedEvents = new();
    private readonly object _lock = new();

    public IReadOnlyList<PublishedEvent> PublishedEvents
    {
        get
        {
            lock (_lock) return _publishedEvents.ToList();
        }
    }

    public Task PublishAsync(string messageId, string eventType, string payloadJson, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _publishedEvents.Add(new PublishedEvent(messageId, eventType, payloadJson, DateTime.UtcNow));
        }
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_lock) _publishedEvents.Clear();
    }
}

public record PublishedEvent(string MessageId, string EventType, string PayloadJson, DateTime PublishedAt);

