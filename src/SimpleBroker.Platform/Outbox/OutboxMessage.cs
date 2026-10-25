namespace SimpleBroker.Platform.Outbox;

using System.Text.Json;

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public string Type { get; set; } = "";

    /// <summary>JSON document (jsonb column).</summary>
    public string Payload { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public static OutboxMessage Create<T>(string type, T payload) =>
        new() { Type = type, Payload = JsonSerializer.Serialize(payload), CreatedAt = DateTime.UtcNow };
}
