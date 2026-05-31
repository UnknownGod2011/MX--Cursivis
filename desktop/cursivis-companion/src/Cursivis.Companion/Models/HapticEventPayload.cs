using System.Text.Json.Serialization;

namespace Cursivis.Companion.Models;

public sealed class HapticEventPayload
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "1.0.0";

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "haptic";

    [JsonPropertyName("hapticType")]
    public string HapticType { get; set; } = "processing_start";

    [JsonPropertyName("intensity")]
    public string Intensity { get; set; } = "medium";

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
