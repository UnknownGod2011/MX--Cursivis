using System.Text.Json.Serialization;

namespace Cursivis.Companion.Models;

public sealed class AgentRequest
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "1.0.0";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "smart";

    [JsonPropertyName("actionHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionHint { get; set; }

    [JsonPropertyName("voiceCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceCommand { get; set; }

    [JsonPropertyName("selection")]
    public SelectionPayload Selection { get; set; } = new();

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RequestContextPayload? Context { get; set; }

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

public sealed class SelectionPayload
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("imageBase64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageBase64 { get; set; }

    [JsonPropertyName("imageMimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageMimeType { get; set; }
}

public sealed class RequestContextPayload
{
    [JsonPropertyName("activeApp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveApp { get; set; }

    [JsonPropertyName("cursorX")]
    public int CursorX { get; set; }

    [JsonPropertyName("cursorY")]
    public int CursorY { get; set; }
}

public sealed class AgentResponse
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "1.0.0";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("alternatives")]
    public List<string> Alternatives { get; set; } = [];

    [JsonPropertyName("latencyMs")]
    public int LatencyMs { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = string.Empty;
}
