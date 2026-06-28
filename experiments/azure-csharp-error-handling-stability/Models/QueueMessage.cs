namespace AzureErrorHandlingDemo.Models;

public class QueueMessage
{
    public string MessageId { get; set; } = string.Empty;
    public OrderRequest? Payload { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
