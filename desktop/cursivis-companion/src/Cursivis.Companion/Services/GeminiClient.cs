using Cursivis.Companion.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cursivis.Companion.Services;

public sealed class GeminiClient : IDisposable
{
    private const string LocalOllamaProviderId = "local_ollama";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex CodeKeywordRegex = new(@"\b(function|class|const|let|var|public|private|protected|return|if\s*\(|for\s*\(|while\s*\(|try|catch|throw|await|async|import|export|console\.log|print\s*\(|SELECT\s+.+\s+FROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeInlineFeatureRegex = new(@"(=>|==={0,1}|!==|::|</?[a-z][^>]*>|#include\b|using\s+[A-Z][A-Za-z0-9_.]+;)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodePunctuationFeatureRegex = new(@"[{};]", RegexOptions.Compiled);
    private static readonly Regex ProductRegex = new(@"\b(price|buy|discount|deal|review|specs?|model|compare|amazon|flipkart|walmart|usd|rs\.?|inr)\b|\$\d+|\u20B9\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionRegex = new(@"^\s*(who|what|when|where|why|how|which|is|are|can|could|should|would|will|do|does|did|name|define|explain|tell me|give me|find|list)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhraseQueryRegex = new(@"\b(richest|poorest|largest|smallest|highest|lowest|best|top|cheapest|latest|current|capital|population|price|weather|time|meaning|difference|vs|versus)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex McqRegex = new(@"\b(mcq|multiple choice|choose (the )?correct|select (the )?correct)\b|(?:\b[A-Da-d][\).:-]\s+.+){2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionSetRegex = new(@"(?:^|\n)\s*(?:question\s*\d+|\d+[\).:-])\s+[^\n]+(?:\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"\b(subject:|dear\s+\w+|hi\s+\w+|thanks[,!]|best regards|sincerely[,!])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailHeaderRegex = new(@"(^|\n)\s*(from|to|cc|bcc|sent|date|subject):", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailAddressRegex = new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailClosingRegex = new(@"\b(best regards|regards|sincerely|thanks|thank you|warm regards|kind regards)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailGreetingRegex = new(@"\b(dear|hi|hello|hey)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailInlineAddressRegex = new(@"<[^>\r\n]*@[^\r\n>]+>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CaptionRegex = new(@"\b(caption|hashtags?|yt|youtube|thumbnail|hook|title ideas?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReportRegex = new(@"\b(summary|findings|analysis|report)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeErrorRegex = new(@"\b(syntaxerror|typeerror|referenceerror|exception|traceback|unexpected token|unexpected end|unterminated|nullreferenceexception|runtime error)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncompleteCodeRegex = new(@"(^|\n)\s*(if|else if|for|while|switch|try|catch|finally|function|class)\b[^\n{};]*$|(^|\n)\s*(return|throw|await)\s*$|[=+\-*/%&|?:.,]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly HttpClient _localImageHttpClient;
    private readonly RuntimeLaunchProfileService _runtimeLaunchProfileService = new();

    public GeminiClient()
    {
        var backendUrl = Environment.GetEnvironmentVariable("CURSIVIS_BACKEND_URL")
            ?? "http://127.0.0.1:51880";
        var backendUri = new Uri(backendUrl);

        _httpClient = new HttpClient
        {
            BaseAddress = backendUri,
            Timeout = TimeSpan.FromMinutes(4)
        };

        _localImageHttpClient = new HttpClient
        {
            BaseAddress = backendUri,
            Timeout = ResolveLocalImageTimeout()
        };
    }

    public async Task<AgentResponse> AnalyzeTextAsync(
        string text,
        string? actionHint,
        string mode,
        string? activeApp,
        string? voiceCommand,
        System.Windows.Point cursor,
        string? imageBase64,
        string? imageMimeType,
        CancellationToken cancellationToken)
    {
        var request = BuildBaseRequest(mode, actionHint, activeApp, voiceCommand, cursor);
        request.Selection = new SelectionPayload
        {
            Kind = !string.IsNullOrWhiteSpace(imageBase64) && !string.IsNullOrWhiteSpace(imageMimeType) ? "text_image" : "text",
            Text = text,
            ImageBase64 = string.IsNullOrWhiteSpace(imageBase64) ? null : imageBase64,
            ImageMimeType = string.IsNullOrWhiteSpace(imageMimeType) ? null : imageMimeType
        };

        return await AnalyzeAsync(request, cancellationToken);
    }

    public async Task<AgentResponse> AnalyzeImageAsync(
        string imageBase64,
        string imageMimeType,
        string? actionHint,
        string mode,
        string? activeApp,
        string? voiceCommand,
        System.Windows.Point cursor,
        CancellationToken cancellationToken)
    {
        var request = BuildBaseRequest(mode, actionHint, activeApp, voiceCommand, cursor);
        request.Selection = new SelectionPayload
        {
            Kind = "image",
            ImageBase64 = imageBase64,
            ImageMimeType = imageMimeType
        };

        return await AnalyzeAsync(request, cancellationToken);
    }

    public async Task<SuggestionResponse> SuggestTextActionsAsync(
        string text,
        string mode,
        string? activeApp,
        System.Windows.Point cursor,
        string? imageBase64,
        string? imageMimeType,
        CancellationToken cancellationToken)
    {
        var request = BuildBaseRequest(mode, actionHint: null, activeApp, voiceCommand: null, cursor);
        request.Selection = new SelectionPayload
        {
            Kind = !string.IsNullOrWhiteSpace(imageBase64) && !string.IsNullOrWhiteSpace(imageMimeType) ? "text_image" : "text",
            Text = text,
            ImageBase64 = string.IsNullOrWhiteSpace(imageBase64) ? null : imageBase64,
            ImageMimeType = string.IsNullOrWhiteSpace(imageMimeType) ? null : imageMimeType
        };

        return await SuggestActionsAsync(
            request,
            fallbackFactory: () => FallbackTextSuggestion(text, includeVisualContext: !string.IsNullOrWhiteSpace(imageBase64)),
            cancellationToken);
    }

    public async Task<SuggestionResponse> SuggestImageActionsAsync(
        string imageBase64,
        string imageMimeType,
        string mode,
        string? activeApp,
        System.Windows.Point cursor,
        CancellationToken cancellationToken)
    {
        var request = BuildBaseRequest(mode, actionHint: null, activeApp, voiceCommand: null, cursor);
        request.Selection = new SelectionPayload
        {
            Kind = "image",
            ImageBase64 = imageBase64,
            ImageMimeType = imageMimeType
        };

        return await SuggestActionsAsync(
            request,
            fallbackFactory: () => new SuggestionResponse
            {
                ContentType = "image",
                BestAction = "describe_image",
                RecommendedAction = "describe_image",
                Confidence = 0.72,
                Alternatives = ["describe_image", "extract_key_details", "identify_objects", "extract_dominant_colors", "generate_captions"],
                ExtendedAlternatives = ["extract_dominant_colors", "generate_alt_text", "translate", "summarize"]
            },
            cancellationToken);
    }

    public async Task<string?> TranscribeVoiceAsync(
        byte[] audioBytes,
        string mimeType,
        CancellationToken cancellationToken)
    {
        if (audioBytes.Length == 0)
        {
            return null;
        }

        var response = await _httpClient.PostAsJsonAsync(
            "/transcribe",
            new TranscribeRequest
            {
                AudioBase64 = Convert.ToBase64String(audioBytes),
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? "audio/wav" : mimeType
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        var parsed = JsonSerializer.Deserialize<TranscribeResponse>(body, JsonOptions);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Text))
        {
            return null;
        }

        return parsed.Text.Trim();
    }

    public async Task<BrowserActionPlanResponse> PlanBrowserActionAsync(
        BrowserActionPlanRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/plan-browser-action", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        var parsed = JsonSerializer.Deserialize<BrowserActionPlanResponse>(body);
        if (parsed is null)
        {
            throw new InvalidOperationException("Backend returned an empty browser action plan.");
        }

        return parsed;
    }

    public async Task<BrowserActionPlanResponse> RefineBrowserActionPlanAsync(
        BrowserActionPlanRefineRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/refine-browser-action", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        var parsed = JsonSerializer.Deserialize<BrowserActionPlanResponse>(body);
        if (parsed is null)
        {
            throw new InvalidOperationException("Backend returned an empty refined browser action plan.");
        }

        return parsed;
    }

    public async Task UpdateRuntimeApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var normalized = string.Join(
            ",",
            (apiKey ?? string.Empty)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter a valid Gemini API key before pressing Set.");
        }

        var response = await _httpClient.PostAsJsonAsync(
            "/runtime/api-key",
            new RuntimeApiKeyUpdateRequest { ApiKey = normalized },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }
    }

    public async Task<RuntimeAiProviderStatus> GetRuntimeAiProviderAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync("/runtime/ai-provider", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        return JsonSerializer.Deserialize<RuntimeAiProviderStatus>(body, JsonOptions)
            ?? new RuntimeAiProviderStatus();
    }

    public async Task<RuntimeAiProviderStatus> UpdateRuntimeAiProviderAsync(
        RuntimeAiProviderUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/runtime/ai-provider", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        return JsonSerializer.Deserialize<RuntimeAiProviderStatus>(body, JsonOptions)
            ?? new RuntimeAiProviderStatus { Provider = request.Provider };
    }

    public async Task<RuntimeAiProviderTestResult> TestRuntimeAiProviderAsync(
        RuntimeAiProviderUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/runtime/ai-provider/test", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var parsedError = TryDeserialize<RuntimeAiProviderTestResult>(body);
            if (parsedError is not null)
            {
                return parsedError;
            }

            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        return JsonSerializer.Deserialize<RuntimeAiProviderTestResult>(body, JsonOptions)
            ?? new RuntimeAiProviderTestResult { Ok = false, Error = "Backend returned an empty provider test result." };
    }

    public async Task<LocalLlmStatus> GetLocalLlmStatusAsync(
        string ollamaUrl,
        string localModel,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/runtime/local-llm/status",
            new LocalLlmStatusRequest { OllamaUrl = ollamaUrl, LocalModel = localModel },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize<LocalLlmStatus>(body, JsonOptions);
        if (parsed is not null)
        {
            return parsed;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        return new LocalLlmStatus { Ok = false, Error = "Backend returned an empty local LLM status." };
    }

    public async Task<LocalModelPullResult> PullLocalModelAsync(
        string ollamaUrl,
        string localModel,
        CancellationToken cancellationToken)
    {
        using var setupClient = new HttpClient
        {
            BaseAddress = _httpClient.BaseAddress,
            Timeout = TimeSpan.FromMinutes(30)
        };

        var response = await setupClient.PostAsJsonAsync(
            "/runtime/local-llm/pull",
            new LocalLlmStatusRequest { OllamaUrl = ollamaUrl, LocalModel = localModel },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var parsedError = TryDeserialize<LocalModelPullResult>(body);
            if (parsedError is not null)
            {
                return parsedError;
            }

            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        return JsonSerializer.Deserialize<LocalModelPullResult>(body, JsonOptions)
            ?? new LocalModelPullResult { Ok = false, Error = "Backend returned an empty model download result." };
    }

    public async Task<LocalModelPullResult> PullLocalModelWithProgressAsync(
        string ollamaUrl,
        string localModel,
        IProgress<LocalModelPullProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var setupClient = new HttpClient
        {
            BaseAddress = _httpClient.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var response = await setupClient.PostAsJsonAsync(
            "/runtime/local-llm/pull-stream",
            new LocalLlmStatusRequest { OllamaUrl = ollamaUrl, LocalModel = localModel },
            cancellationToken);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);

        LocalModelPullProgress? lastProgress = null;
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = TryDeserialize<LocalModelPullProgress>(line);
            if (parsed is null)
            {
                continue;
            }

            lastProgress = parsed;
            progress?.Report(parsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new LocalModelPullResult
            {
                Ok = false,
                Model = localModel,
                Error = lastProgress?.Error ?? "Ollama model download failed.",
                Details = lastProgress?.Details ?? response.ReasonPhrase ?? string.Empty,
                Status = lastProgress?.Status ?? string.Empty
            };
        }

        return new LocalModelPullResult
        {
            Ok = string.IsNullOrWhiteSpace(lastProgress?.Error),
            Model = localModel,
            Status = lastProgress?.Status ?? "success",
            Error = lastProgress?.Error ?? string.Empty,
            Details = lastProgress?.Details ?? string.Empty
        };
    }

    private AgentRequest BuildBaseRequest(string mode, string? actionHint, string? activeApp, string? voiceCommand, System.Windows.Point cursor)
    {
        return new AgentRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Mode = mode,
            ActionHint = actionHint,
            VoiceCommand = string.IsNullOrWhiteSpace(voiceCommand) ? null : voiceCommand,
            Context = new RequestContextPayload
            {
                ActiveApp = activeApp,
                CursorX = (int)cursor.X,
                CursorY = (int)cursor.Y
            },
            TimestampUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private async Task<AgentResponse> AnalyzeAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        var useExtendedLocalImageTimeout = await ShouldUseExtendedLocalImageTimeoutAsync(request);
        try
        {
            response = await SendWithFallbackAsync(request, useExtendedLocalImageTimeout, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (useExtendedLocalImageTimeout)
            {
                throw new InvalidOperationException(
                    "The local image request timed out after the extended processing window. Try a smaller image, a faster local model, or switch to API LLM for image-heavy work.");
            }

            throw new InvalidOperationException(
                "The AI request timed out. Try a shorter selection or retry in a few seconds.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
        }

        var parsed = await response.Content.ReadFromJsonAsync<AgentResponse>(cancellationToken: cancellationToken);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Result))
        {
            throw new InvalidOperationException("Backend returned an empty response payload.");
        }

        return parsed;
    }

    private async Task<SuggestionResponse> SuggestActionsAsync(
        AgentRequest request,
        Func<SuggestionResponse> fallbackFactory,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        var useExtendedLocalImageTimeout = await ShouldUseExtendedLocalImageTimeoutAsync(request);
        var client = useExtendedLocalImageTimeout ? _localImageHttpClient : _httpClient;
        try
        {
            response = await client.PostAsJsonAsync("/suggest-actions", request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return fallbackFactory();
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(FormatBackendError(response.StatusCode, body));
            }

            var suggestion = await response.Content.ReadFromJsonAsync<SuggestionResponse>(cancellationToken: cancellationToken);
            return suggestion ?? fallbackFactory();
        }
        catch (HttpRequestException)
        {
            return fallbackFactory();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (useExtendedLocalImageTimeout)
            {
                throw new InvalidOperationException(
                    "The local image suggestion request timed out after the extended processing window. Try a smaller image, a faster local model, or switch to API LLM for image-heavy Guided Mode work.");
            }

            throw new InvalidOperationException(
                "The AI suggestion request timed out. Try a shorter selection or retry in a few seconds.");
        }
        catch
        {
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static SuggestionResponse FallbackTextSuggestion(string text, bool includeVisualContext)
    {
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var type = LooksLikeEmail(text)
            ? "email"
            : LooksLikeCode(text)
            ? "code"
            : EmailRegex.IsMatch(text)
                ? "email"
            : ProductRegex.IsMatch(text)
                ? "product"
            : McqRegex.IsMatch(text) || QuestionSetRegex.IsMatch(text)
                    ? "mcq"
                : CaptionRegex.IsMatch(text)
                    ? "social_caption"
                    : text.Length > 500 || ReportRegex.IsMatch(text) || LooksLikeLongInformationalText(text)
                        ? "report"
                    : LooksLikeQuestion(text) || (PhraseQueryRegex.IsMatch(text) && wordCount <= 12)
                        ? "question"
                        : "general_text";

        return type switch
        {
            "question" => new SuggestionResponse
            {
                ContentType = "question",
                BestAction = "answer_question",
                RecommendedAction = "answer_question",
                Confidence = 0.78,
                Alternatives = ["answer_question", "explain", "rewrite"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "fact_check", "compare_answers", "turn_into_flashcards", "translate")
            },
            "mcq" => new SuggestionResponse
            {
                ContentType = "mcq",
                BestAction = "answer_question",
                RecommendedAction = "answer_question",
                Confidence = 0.8,
                Alternatives = ["answer_question", "explain", "bullet_points"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "create_answer_key", "eliminate_wrong_options", "translate", "compare_answers")
            },
            "code" => new SuggestionResponse
            {
                ContentType = "code",
                BestAction = LooksLikeBrokenCode(text) ? "debug_code" : "explain_code",
                RecommendedAction = LooksLikeBrokenCode(text) ? "debug_code" : "explain_code",
                Confidence = 0.8,
                Alternatives = LooksLikeBrokenCode(text)
                    ? ["debug_code", "explain_code", "improve_code", "optimize_code"]
                    : ["explain_code", "improve_code", "debug_code", "optimize_code"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "write_tests", "refactor_code", "add_comments", "find_edge_cases")
            },
            "product" => new SuggestionResponse
            {
                ContentType = "product",
                BestAction = "extract_product_info",
                RecommendedAction = "extract_product_info",
                Confidence = 0.79,
                Alternatives = ["extract_product_info", "compare_prices", "find_reviews"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "show_product_details", "pros_cons", "buyer_checklist", "identify_red_flags")
            },
            "email" => new SuggestionResponse
            {
                ContentType = "email",
                BestAction = ShouldPreferDraftReply(text) ? "draft_reply" : "polish_email",
                RecommendedAction = ShouldPreferDraftReply(text) ? "draft_reply" : "polish_email",
                Confidence = 0.79,
                Alternatives = ShouldPreferDraftReply(text)
                    ? ["draft_reply", "polish_email", "rewrite", "bullet_points"]
                    : ["polish_email", "draft_reply", "rewrite", "bullet_points"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "change_tone", "shorten_email", "expand_text", "translate")
            },
            "social_caption" => new SuggestionResponse
            {
                ContentType = "social_caption",
                BestAction = "suggest_captions",
                RecommendedAction = "suggest_captions",
                Confidence = 0.76,
                Alternatives = ["suggest_captions", "rewrite", "bullet_points"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "generate_hashtags", "create_hook_variants", "short_caption", "long_caption")
            },
            "report" => new SuggestionResponse
            {
                ContentType = "report",
                BestAction = "extract_insights",
                RecommendedAction = "extract_insights",
                Confidence = 0.78,
                Alternatives = ["extract_insights", "bullet_points", "summarize", "rewrite_structured"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "executive_summary", "extract_action_items", "extract_metrics", "expand_text")
            },
            _ => new SuggestionResponse
            {
                ContentType = "general_text",
                BestAction = "extract_insights",
                RecommendedAction = "extract_insights",
                Confidence = 0.72,
                Alternatives = ["extract_insights", "rewrite_structured", "summarize", "bullet_points"],
                ExtendedAlternatives = BuildExtendedFallbacks(includeVisualContext, "expand_text", "grammar_fix", "translate", "extract_key_details")
            }
        };
    }

    private static bool ShouldPreferDraftReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (LooksLikeRoughEmailDraft(text))
        {
            return false;
        }

        return EmailHeaderRegex.IsMatch(text) || LooksLikeBriefEmailText(text) || LooksLikePolishedEmail(text);
    }

    private static bool LooksLikeBriefEmailText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var hasEmailAddress = EmailAddressRegex.IsMatch(trimmed) || EmailInlineAddressRegex.IsMatch(trimmed);
        if (!hasEmailAddress)
        {
            return false;
        }

        var lineCount = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        var hasEmailCue =
            EmailHeaderRegex.IsMatch(trimmed) ||
            EmailGreetingRegex.IsMatch(trimmed) ||
            EmailClosingRegex.IsMatch(trimmed) ||
            Regex.IsMatch(trimmed, @"\b(thanks|thank you|please|regards|re:|fwd:|to)\b", RegexOptions.IgnoreCase);

        return hasEmailCue || lineCount >= 2;
    }

    private static bool LooksLikePolishedEmail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var punctuationHits = Regex.Matches(trimmed, @"[.!?](?:\s|$)").Count;
        var lineCount = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return EmailGreetingRegex.IsMatch(trimmed) && EmailClosingRegex.IsMatch(trimmed) && punctuationHits >= 3 && lineCount >= 4;
    }

    private static bool LooksLikeRoughEmailDraft(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasEmailSignal =
            EmailHeaderRegex.IsMatch(text) ||
            EmailAddressRegex.IsMatch(text) ||
            EmailGreetingRegex.IsMatch(text) ||
            EmailClosingRegex.IsMatch(text);

        if (!hasEmailSignal)
        {
            return false;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !Regex.IsMatch(line, @"^(from|to|cc|bcc|sent|date|subject):", RegexOptions.IgnoreCase))
            .ToList();
        if (lines.Count == 0)
        {
            return false;
        }

        var punctuationEndedLines = lines.Count(line => Regex.IsMatch(line, @"[.!?]$"));
        var lowercaseLines = lines.Count(line => Regex.IsMatch(line, @"^[a-z]"));
        var score = 0;
        if (!EmailGreetingRegex.IsMatch(text))
        {
            score += 1;
        }

        if (!EmailClosingRegex.IsMatch(text))
        {
            score += 1;
        }

        if (lines.Count >= 2 && punctuationEndedLines < Math.Max(1, lines.Count / 2))
        {
            score += 1;
        }

        if (lines.Count >= 2 && lowercaseLines >= Math.Max(2, (int)Math.Ceiling(lines.Count / 2d)))
        {
            score += 1;
        }

        if (!LooksLikePolishedEmail(text) && Regex.IsMatch(text, @"[a-z0-9][\r\n]+[a-z]"))
        {
            score += 1;
        }

        return score >= 2;
    }

    private static List<string> BuildExtendedFallbacks(bool includeVisualContext, params string[] actions)
    {
        var extended = actions.ToList();
        if (includeVisualContext)
        {
            foreach (var action in new[] { "extract_key_details", "ocr_extract_text", "generate_captions" })
            {
                if (!extended.Contains(action, StringComparer.OrdinalIgnoreCase))
                {
                    extended.Add(action);
                }
            }
        }

        return extended;
    }

    private static bool LooksLikeBrokenCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeCode(text))
        {
            return false;
        }

        if (CodeErrorRegex.IsMatch(text) || IncompleteCodeRegex.IsMatch(text))
        {
            return true;
        }

        return HasUnbalancedDelimiters(text);
    }

    private static bool HasUnbalancedDelimiters(string text)
    {
        var stack = new Stack<char>();
        var pairs = new Dictionary<char, char>
        {
            [')'] = '(',
            [']'] = '[',
            ['}'] = '{'
        };

        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inTemplate = false;
        var escaped = false;

        foreach (var ch in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (!inDoubleQuote && !inTemplate && ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (!inSingleQuote && !inTemplate && ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && ch == '`')
            {
                inTemplate = !inTemplate;
                continue;
            }

            if (inSingleQuote || inDoubleQuote || inTemplate)
            {
                continue;
            }

            if (ch is '(' or '[' or '{')
            {
                stack.Push(ch);
                continue;
            }

            if (!pairs.TryGetValue(ch, out var expected))
            {
                continue;
            }

            if (stack.Count == 0 || stack.Pop() != expected)
            {
                return true;
            }
        }

        return stack.Count > 0 || inSingleQuote || inDoubleQuote || inTemplate;
    }

    private static bool LooksLikeEmail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return EmailRegex.IsMatch(text) || EmailHeaderRegex.IsMatch(text) || LooksLikeBriefEmailText(text);
    }

    private static bool LooksLikeCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (LooksLikeEmail(text) || LooksLikePolishedEmail(text) || LooksLikeRoughEmailDraft(text))
        {
            return false;
        }

        if (CodeKeywordRegex.IsMatch(text) || CodeInlineFeatureRegex.IsMatch(text))
        {
            return true;
        }

        var punctuationMatches = CodePunctuationFeatureRegex.Matches(text).Count;
        var structuredLines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => Regex.IsMatch(
                line,
                @"^(if|else|for|while|switch|try|catch|finally|function|class|public|private|protected|const|let|var|return|import|export|using|namespace)\b",
                RegexOptions.IgnoreCase));

        return punctuationMatches >= 3 && structuredLines >= 1;
    }

    private static bool LooksLikeQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (QuestionSetRegex.IsMatch(text) || McqRegex.IsMatch(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        var lines = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var questionLines = lines.Count(line => line.Contains('?'));
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (trimmed.EndsWith("?", StringComparison.Ordinal))
        {
            return true;
        }

        if (lines.Count <= 5 && questionLines >= 1 && questionLines >= Math.Ceiling(lines.Count / 2d))
        {
            return true;
        }

        if (LooksLikeLongInformationalText(trimmed))
        {
            return false;
        }

        return wordCount <= 18 && QuestionRegex.IsMatch(trimmed);
    }

    private static bool LooksLikeLongInformationalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var lines = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var sentenceCount = Regex.Matches(trimmed, @"[.!?](?:\s|$)").Count;
        var questionLines = lines.Count(line => line.Contains('?'));

        return (wordCount >= 60 || trimmed.Length >= 420) &&
            sentenceCount >= 3 &&
            questionLines <= Math.Max(1, lines.Count / 4);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _localImageHttpClient.Dispose();
    }

    private async Task<bool> ShouldUseExtendedLocalImageTimeoutAsync(AgentRequest request)
    {
        if (!HasImageContext(request))
        {
            return false;
        }

        var envProvider = NormalizeProviderForTimeout(Environment.GetEnvironmentVariable("CURSIVIS_AI_PROVIDER"));
        if (envProvider == LocalOllamaProviderId)
        {
            return true;
        }

        var profile = await _runtimeLaunchProfileService.TryLoadAsync();
        return NormalizeProviderForTimeout(profile?.AiProvider) == LocalOllamaProviderId;
    }

    private static bool HasImageContext(AgentRequest request)
    {
        var selection = request.Selection;
        if (!string.Equals(selection.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(selection.Kind, "text_image", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(selection.ImageBase64) &&
            !string.IsNullOrWhiteSpace(selection.ImageMimeType);
    }

    private static string NormalizeProviderForTimeout(string? provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "local" or "ollama" or "local_ollama" => LocalOllamaProviderId,
            _ => "gemini"
        };
    }

    private static TimeSpan ResolveLocalImageTimeout()
    {
        var minutes = 6;
        var configured = Environment.GetEnvironmentVariable("CURSIVIS_LOCAL_IMAGE_HTTP_TIMEOUT_MINUTES");
        if (int.TryParse(configured, out var parsedMinutes))
        {
            minutes = Math.Clamp(parsedMinutes, 5, 15);
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string FormatBackendError(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BackendErrorResponse>(responseBody);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Error))
            {
                var message = parsed.Error;
                if (!string.IsNullOrWhiteSpace(parsed.Details))
                {
                    message = $"{message} {parsed.Details}";
                }

                if ((int)statusCode == 429)
                {
                    var retry = parsed.RetryAfterSec ?? 30;
                    return $"Gemini quota/rate limit reached for the current API key or project. Retry after about {retry} seconds, or switch to a different key/project with available quota.";
                }

                return CredentialRedactor.Redact(message);
            }
        }
        catch
        {
            // Fallback to raw response below.
        }

        return $"Backend error {(int)statusCode}: {CredentialRedactor.Redact(responseBody)}";
    }

    private static T? TryDeserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private async Task<HttpResponseMessage> SendWithFallbackAsync(
        AgentRequest request,
        bool useExtendedLocalImageTimeout,
        CancellationToken cancellationToken)
    {
        var client = useExtendedLocalImageTimeout ? _localImageHttpClient : _httpClient;
        var analyzeResponse = await client.PostAsJsonAsync("/analyze", request, cancellationToken);
        if (analyzeResponse.StatusCode != HttpStatusCode.NotFound)
        {
            return analyzeResponse;
        }

        analyzeResponse.Dispose();
        return await client.PostAsJsonAsync("/api/intent", request, cancellationToken);
    }

    private sealed class TranscribeRequest
    {
        public string AudioBase64 { get; init; } = string.Empty;

        public string MimeType { get; init; } = "audio/wav";
    }

    private sealed class RuntimeApiKeyUpdateRequest
    {
        public string ApiKey { get; init; } = string.Empty;
    }

    private sealed class LocalLlmStatusRequest
    {
        public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

        public string LocalModel { get; init; } = "granite3.2-vision:2b";
    }

    private sealed class TranscribeResponse
    {
        public string? Text { get; init; }
    }

    private sealed class BackendErrorResponse
    {
        public string? Error { get; init; }

        public string? Details { get; init; }

        public int? RetryAfterSec { get; init; }
    }
}

public sealed class RuntimeAiProviderUpdateRequest
{
    public string Provider { get; init; } = "gemini";

    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

    public string LocalModel { get; init; } = "granite3.2-vision:2b";

    public string OpenAiBaseUrl { get; init; } = string.Empty;

    public string OpenAiApiKey { get; init; } = string.Empty;

    public string OpenAiModel { get; init; } = string.Empty;

    public string HostedApiUrl { get; init; } = string.Empty;

    public string HostedToken { get; init; } = string.Empty;
}

public sealed class RuntimeAiProviderStatus
{
    public bool Ok { get; init; }

    public string Provider { get; init; } = "gemini";

    public string DisplayName { get; init; } = "Gemini";

    public string BackendMode { get; init; } = "api";

    public string LocalModel { get; init; } = "granite3.2-vision:2b";

    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

    public string HostedApiUrl { get; init; } = string.Empty;

    public bool HasHostedToken { get; init; }
}

public sealed class RuntimeAiProviderTestResult
{
    public bool Ok { get; init; }

    public string Provider { get; init; } = "gemini";

    public string DisplayName { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int LatencyMs { get; init; }

    public string Preview { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;
}

public sealed class LocalLlmStatus
{
    public bool Ok { get; init; }

    public bool Reachable { get; init; }

    public bool ModelInstalled { get; init; }

    public string Model { get; init; } = "granite3.2-vision:2b";

    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

    public string[] InstalledModels { get; init; } = [];

    public string Error { get; init; } = string.Empty;
}

public sealed class LocalModelPullResult
{
    public bool Ok { get; init; }

    public string Model { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;
}

public sealed class LocalModelPullProgress
{
    public string Status { get; init; } = string.Empty;

    public string Digest { get; init; } = string.Empty;

    public long Total { get; init; }

    public long Completed { get; init; }

    public string Error { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public double Percent => Total > 0
        ? Math.Clamp((double)Completed / Total * 100.0, 0.0, 100.0)
        : 0.0;
}
