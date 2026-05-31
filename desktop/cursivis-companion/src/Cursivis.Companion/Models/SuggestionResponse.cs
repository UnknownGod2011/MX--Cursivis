using System.Text.Json.Serialization;

namespace Cursivis.Companion.Models;

public sealed class SuggestionResponse
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "general_text";

    [JsonPropertyName("bestAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BestAction { get; set; }

    [JsonPropertyName("recommendedAction")]
    public string RecommendedAction { get; set; } = "summarize";

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Confidence { get; set; }

    [JsonPropertyName("alternatives")]
    public List<string> Alternatives { get; set; } = [];

    [JsonPropertyName("extendedAlternatives")]
    public List<string> ExtendedAlternatives { get; set; } = [];
}
