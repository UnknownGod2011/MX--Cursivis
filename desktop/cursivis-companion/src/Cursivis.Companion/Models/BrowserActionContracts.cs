using System.Text.Json.Serialization;

namespace Cursivis.Companion.Models;

public sealed class BrowserPageContextResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("browserChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrowserChannel { get; set; }

    [JsonPropertyName("pageContext")]
    public BrowserPageContext PageContext { get; set; } = new();
}

public sealed class BrowserEnsureRequest
{
    [JsonPropertyName("preferredChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferredChannel { get; set; }

    [JsonPropertyName("openUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenUrl { get; set; }
}

public sealed class BrowserPageContext
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("visibleText")]
    public string VisibleText { get; set; } = string.Empty;

    [JsonPropertyName("browserChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrowserChannel { get; set; }

    [JsonPropertyName("interactiveElements")]
    public List<BrowserElementSummary> InteractiveElements { get; set; } = [];
}

public sealed class BrowserElementSummary
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("nameAttribute")]
    public string NameAttribute { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];
}

public sealed class BrowserActionPlanRequest
{
    [JsonPropertyName("originalText")]
    public string OriginalText { get; set; } = string.Empty;

    [JsonPropertyName("resultText")]
    public string ResultText { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("voiceCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceCommand { get; set; }

    [JsonPropertyName("executionInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionInstruction { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "general_text";

    [JsonPropertyName("browserContext")]
    public BrowserPageContext BrowserContext { get; set; } = new();
}

public sealed class BrowserActionPlanRefineRequest
{
    [JsonPropertyName("originalText")]
    public string OriginalText { get; set; } = string.Empty;

    [JsonPropertyName("resultText")]
    public string ResultText { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("voiceCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceCommand { get; set; }

    [JsonPropertyName("executionInstruction")]
    public string ExecutionInstruction { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "general_text";

    [JsonPropertyName("browserContext")]
    public BrowserPageContext BrowserContext { get; set; } = new();

    [JsonPropertyName("currentPlan")]
    public BrowserActionPlanResponse CurrentPlan { get; set; } = new();
}

public sealed class BrowserActionPlanResponse
{
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("requiresConfirmation")]
    public bool RequiresConfirmation { get; set; }

    [JsonPropertyName("steps")]
    public List<BrowserActionStep> Steps { get; set; } = [];
}

public sealed class BrowserActionStep
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("nameAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NameAttribute { get; set; }

    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; set; }

    [JsonPropertyName("question")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Question { get; set; }

    [JsonPropertyName("option")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Option { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    [JsonPropertyName("answers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BrowserAnswerKeyEntry>? Answers { get; set; }

    [JsonPropertyName("advancePages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AdvancePages { get; set; }

    [JsonPropertyName("waitMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WaitMs { get; set; }
}

public sealed class BrowserAnswerKeyEntry
{
    [JsonPropertyName("question")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Question { get; set; }

    [JsonPropertyName("option")]
    public string Option { get; set; } = string.Empty;

    [JsonPropertyName("questionIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QuestionIndex { get; set; }

    [JsonPropertyName("choiceIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChoiceIndex { get; set; }
}

public sealed class BrowserExecutionRequest
{
    [JsonPropertyName("steps")]
    public List<BrowserActionStep> Steps { get; set; } = [];
}

public sealed class BrowserExecutionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("executedSteps")]
    public int ExecutedSteps { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    [JsonPropertyName("logs")]
    public List<string> Logs { get; set; } = [];

    [JsonPropertyName("pageContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserPageContext? PageContext { get; set; }
}

public sealed class ExtensionBridgeHealthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("extensionConnected")]
    public bool ExtensionConnected { get; set; }

    [JsonPropertyName("browserName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrowserName { get; set; }

    [JsonPropertyName("extensionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtensionId { get; set; }

    [JsonPropertyName("connectedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectedAtUtc { get; set; }

    [JsonPropertyName("lastSeenUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSeenUtc { get; set; }

    [JsonPropertyName("lastError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];
}

public sealed class ExtensionPageContextResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("browserName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrowserName { get; set; }

    [JsonPropertyName("extensionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtensionId { get; set; }

    [JsonPropertyName("tabId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TabId { get; set; }

    [JsonPropertyName("pageContext")]
    public BrowserPageContext PageContext { get; set; } = new();
}
