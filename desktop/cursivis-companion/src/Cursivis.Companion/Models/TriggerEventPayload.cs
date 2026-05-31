using System.Text.Json.Serialization;

namespace Cursivis.Companion.Models;

public sealed class TriggerEventPayload
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "1.0.0";

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "trigger";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("source")]
    public string Source { get; set; } = "mock-trigger-ui";

    [JsonPropertyName("pressType")]
    public string PressType { get; set; } = "tap";

    [JsonPropertyName("dialDelta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DialDelta { get; set; }

    [JsonPropertyName("cursor")]
    public TriggerCursor Cursor { get; set; } = new();

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

public sealed class TriggerCursor
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}
