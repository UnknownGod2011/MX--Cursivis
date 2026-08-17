#nullable disable

namespace Cursivis.Companion.LiveMode;

using Cursivis.Companion.Services;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;

using NAudio.Wave;

public sealed class GeminiLiveConversationService : IDisposable
{
    private const string Model = "gemini-3.1-flash-live-preview";
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly LiveModeNotificationService _notifications;
    private readonly LiveModeSettingsService _settings;
    private readonly IntentMemoryService _memory;

    private Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> _toolExecutor;
    private Action<string> _userTranscriptObserver;
    private ClientWebSocket _socket;
    private CancellationTokenSource _sessionCts;
    private WaveInEvent _microphone;
    private WaveOutEvent _speaker;
    private BufferedWaveProvider _playbackBuffer;
    private Task _receiveTask;
    private TaskCompletionSource<bool> _setupReady;
    private bool _autoEnding;
    private bool _disposed;

    public Exception LastStartError { get; private set; }

    public GeminiLiveConversationService(
        LiveModeNotificationService notifications,
        LiveModeSettingsService settings,
        IntentMemoryService memory)
    {
        _notifications = notifications;
        _settings = settings;
        _memory = memory;
    }

    public bool IsActive => _socket?.State == WebSocketState.Open && _microphone != null;

    public void SetToolExecutor(Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> executor) =>
        _toolExecutor = executor;

    public void SetUserTranscriptObserver(Action<string> observer) =>
        _userTranscriptObserver = observer;

    public async Task ToggleAsync()
    {
        if (IsActive)
        {
            await StopAsync();
            return;
        }

        await StartAsync();
    }

    public async Task<bool> StartAsync(bool suppressFailureNotification = false)
    {
        LastStartError = null;
        if (string.IsNullOrWhiteSpace(LiveModeState.GeminiApiKey))
        {
            LiveModeState.SetUi(LiveModeVoicePhase.Error, $"{AssistantName()} unavailable", "Add a Gemini API key in settings.");
            if (!suppressFailureNotification)
            {
                _notifications.Warning("Cursivis Live Mode", "Add a Gemini API key in settings first.");
            }
            return false;
        }

        await StopAsync(showDone: false);
        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;
        _setupReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoEnding = false;

        try
        {
            LiveModeState.SetUi(LiveModeVoicePhase.Thinking, "Connecting", $"Starting {AssistantName()}...");
            _socket = new ClientWebSocket();
            var endpoint = new Uri(
                "wss://generativelanguage.googleapis.com/ws/" +
                "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" +
                $"?key={Uri.EscapeDataString(LiveModeState.GeminiApiKey)}");
            await _socket.ConnectAsync(endpoint, token);
            LiveModeLog.Info("boundary=gemini.socket.connected");

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), token);
            await SendJsonAsync(new
            {
                setup = new
                {
                    model = $"models/{Model}",
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        speechConfig = new
                        {
                            voiceConfig = new
                            {
                                prebuiltVoiceConfig = new { voiceName = LiveModeState.GeminiVoice },
                            },
                        },
                    },
                    tools = BuildTools(),
                    inputAudioTranscription = new { },
                    outputAudioTranscription = new { },
                    realtimeInputConfig = new
                    {
                        automaticActivityDetection = new
                        {
                            disabled = false,
                            prefixPaddingMs = 80,
                            silenceDurationMs = 550,
                        },
                    },
                    systemInstruction = new
                    {
                        parts = new[]
                        {
                            new { text = BuildSystemInstruction() },
                        },
                    },
                },
            }, token);

            await _setupReady.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            StartPlayback();
            StartMicrophone();
            LiveModeState.SetUi(
                LiveModeVoicePhase.Listening,
                AssistantName(),
                "Talk naturally. Press Cursivis Live Mode again or say bye to finish.");
            _notifications.Info("Cursivis Live Mode", "Conversation started.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LiveModeLog.Warning(ex, "Cursivis Live Mode connection failed");
            LastStartError = ex;
            await StopAsync(showDone: false);
            if (!suppressFailureNotification)
            {
                LiveModeState.SetUi(LiveModeVoicePhase.Error, "Cursivis Live Mode failed", FriendlyError(ex));
                _notifications.Error("Cursivis Live Mode failed", FriendlyError(ex));
            }
            return false;
        }
    }

    public async Task StopAsync(bool showDone = true)
    {
        var sessionCts = _sessionCts;
        _sessionCts = null;
        sessionCts?.Cancel();
        StopMicrophone();
        StopPlayback();

        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { }
            socket.Dispose();
        }

        var receiveTask = _receiveTask;
        _receiveTask = null;
        if (receiveTask != null)
        {
            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Cancellation and socket shutdown are expected during session cleanup.
            }
        }

        _setupReady = null;
        sessionCts?.Dispose();

        if (showDone)
        {
            LiveModeState.SetUi(LiveModeVoicePhase.Done, $"{AssistantName()} ended", "Press Cursivis Live Mode to start another conversation.");
            _notifications.Info("Cursivis Live Mode", "Conversation ended.");
        }
    }

    public async Task SendTextTurnAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        await SendJsonAsync(new
        {
            clientContent = new
            {
                turns = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text } },
                    },
                },
                turnComplete = true,
            },
        }, token);
    }

    private void StartMicrophone()
    {
        var micIndex = LiveModeState.SelectedMicrophoneIndex;
        _microphone = new WaveInEvent
        {
            DeviceNumber = micIndex >= 0 ? micIndex : 0,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 40,
        };
        _microphone.DataAvailable += OnMicrophoneData;
        _microphone.StartRecording();
        LiveModeLog.Info($"boundary=microphone.started device={_microphone.DeviceNumber}");
    }

    private void StopMicrophone()
    {
        if (_microphone == null)
            return;

        try { _microphone.StopRecording(); } catch { }
        _microphone.DataAvailable -= OnMicrophoneData;
        _microphone.Dispose();
        _microphone = null;
        LiveModeState.InputLevelDb = -96;
        LiveModeLog.Info("boundary=microphone.stopped");
    }

    private async void OnMicrophoneData(object sender, WaveInEventArgs e)
    {
        try
        {
            var bytes = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
            LiveModeState.InputLevelDb = PeakDb(bytes);
            await SendJsonAsync(new
            {
                realtimeInput = new
                {
                    audio = new
                    {
                        data = Convert.ToBase64String(bytes),
                        mimeType = "audio/pcm;rate=16000",
                    },
                },
            }, _sessionCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Live Mode microphone send failed");
        }
    }

    private void StartPlayback()
    {
        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(12),
            DiscardOnBufferOverflow = true,
        };
        _speaker = new WaveOutEvent();
        _speaker.Init(_playbackBuffer);
        _speaker.Play();
    }

    private void StopPlayback()
    {
        if (_speaker != null)
        {
            try { _speaker.Stop(); } catch { }
            _speaker.Dispose();
            _speaker = null;
        }
        _playbackBuffer = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var json = await ReceiveMessageAsync(_socket, token);
                if (string.IsNullOrWhiteSpace(json))
                    continue;
                await HandleServerMessageAsync(json, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Live Mode receive loop ended");
            if (!token.IsCancellationRequested)
            {
                LiveModeState.SetUi(LiveModeVoicePhase.Error, "Live Mode disconnected", FriendlyError(ex));
                StopMicrophone();
                StopPlayback();
            }
        }
    }

    private async Task HandleServerMessageAsync(string json, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : error.ToString();
            LiveModeLog.Warning($"Live Mode server error: {message}");
            _setupReady?.TrySetException(new InvalidOperationException(message));
            return;
        }

        if (root.TryGetProperty("setupComplete", out _))
        {
            LiveModeLog.Info($"Live Mode connected using {Model}");
            _setupReady?.TrySetResult(true);
            return;
        }

        if (root.TryGetProperty("toolCall", out var toolCall))
        {
            await HandleToolCallAsync(toolCall, token);
            return;
        }

        if (root.TryGetProperty("toolCallCancellation", out var cancelled)
            && cancelled.TryGetProperty("ids", out var ids))
        {
            LiveModeLog.Info($"Live Mode tool call cancelled: {ids}");
            return;
        }

        if (!root.TryGetProperty("serverContent", out var content))
            return;

        if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            _playbackBuffer?.ClearBuffer();

        if (content.TryGetProperty("inputTranscription", out var input)
            && input.TryGetProperty("text", out var inputText)
            && !string.IsNullOrWhiteSpace(inputText.GetString()))
        {
            var text = inputText.GetString();
            LiveModeState.SetUi(LiveModeVoicePhase.Listening, "You", text);
            _userTranscriptObserver?.Invoke(text);
            if (ShouldAutoEnd(text))
                ScheduleAutoEnd("Heard that you are done.");
        }

        if (content.TryGetProperty("outputTranscription", out var output)
            && output.TryGetProperty("text", out var outputText)
            && !string.IsNullOrWhiteSpace(outputText.GetString()))
        {
            LiveModeState.SetUi(LiveModeVoicePhase.Speaking, AssistantName(), outputText.GetString());
        }

        if (content.TryGetProperty("modelTurn", out var modelTurn)
            && modelTurn.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inline)
                    || !inline.TryGetProperty("data", out var data))
                    continue;

                var audio = Convert.FromBase64String(data.GetString() ?? "");
                if (audio.Length > 0)
                {
                    _playbackBuffer?.AddSamples(audio, 0, audio.Length);
                    LiveModeState.SetUi(LiveModeVoicePhase.Speaking, AssistantName(), "Speaking...");
                }
            }
        }

        if (content.TryGetProperty("turnComplete", out var complete) && complete.GetBoolean())
        {
            LiveModeState.SetUi(
                LiveModeVoicePhase.Listening,
                AssistantName(),
                "Listening for your next request...");
        }
    }

    private async Task HandleToolCallAsync(JsonElement toolCall, CancellationToken token)
    {
        if (!toolCall.TryGetProperty("functionCalls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            return;

        var responses = new List<object>();
        foreach (var call in calls.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString("N");
            var name = call.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
            var args = call.TryGetProperty("args", out var argsEl) ? argsEl : default;

            var response = await ExecuteToolAsync(name, args, token);
            responses.Add(new
            {
                id,
                name,
                response,
            });
        }

        await SendJsonAsync(new
        {
            toolResponse = new
            {
                functionResponses = responses,
            },
        }, token);
    }

    private async Task<Dictionary<string, object>> ExecuteToolAsync(string name, JsonElement args, CancellationToken token)
    {
        switch ((name ?? "").Trim())
        {
            case "remember_intent":
            {
                var entry = _memory.Remember(GetArg(args, "key"), GetArg(args, "value") ?? GetArg(args, "text"));
                var count = _memory.Snapshot().Count;
                return ToolOk("Saved to intent memory.", new()
                {
                    ["key"] = entry.Key,
                    ["value"] = entry.Value,
                    ["count"] = count,
                    ["max_entries"] = IntentMemoryService.MaxEntries,
                });
            }
            case "recall_intent":
            {
                var entries = _memory.Search(GetArg(args, "query") ?? "", 5)
                    .Select(e => new { e.Id, e.Key, e.Value, updated_at = e.UpdatedAt })
                    .ToArray();
                return ToolOk(entries.Length == 0 ? "No matching intent memory found." : "Intent memory loaded.", new()
                {
                    ["entries"] = entries,
                });
            }
            case "list_intent_memory":
            {
                var snapshot = _memory.Snapshot();
                return ToolOk("Intent memory loaded.", new()
                {
                    ["entries"] = snapshot.Entries.Select(e => new { e.Id, e.Key, e.Value, updated_at = e.UpdatedAt }).ToArray(),
                    ["count"] = snapshot.Count,
                    ["max_entries"] = snapshot.MaxEntries,
                });
            }
            case "forget_intent_memory":
            {
                var removed = _memory.Forget(GetArg(args, "key_or_id") ?? GetArg(args, "key") ?? GetArg(args, "id"));
                return ToolOk(removed ? "Removed from intent memory." : "No matching intent memory was found.", new()
                {
                    ["removed"] = removed,
                });
            }
            case "end_conversation":
                ScheduleAutoEnd(GetArg(args, "reason") ?? "Conversation ended.");
                return ToolOk("Conversation will end now.");
            default:
                if (_toolExecutor == null)
                    return ToolError("No action executor is available.", supported: false);
                return await _toolExecutor(name, args, token);
        }
    }

    private async Task SendJsonAsync(object message, CancellationToken token)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
            return;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return "";
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string BuildSystemInstruction()
    {
        var name = AssistantName();
        var autoExecute = _settings.Current.LiveModePermissionMode == LiveModePermissionMode.AutoExecute;
        var permissionInstruction = autoExecute
            ? "Routine actions auto-execute. An explicit one-shot screen or camera request counts as approval; irreversible system actions still require confirmation. "
            : "Routine actions require permission. If a tool response says confirmation_required, ask the user to say confirm or cancel, then call confirm_sensitive_action only after a fresh spoken confirmation. ";
        var closeAppInstruction = autoExecute
            ? "For close-app requests, call window_action with action close. Do not ask for confirmation for normal app/window closes in auto-execute mode. For shutdown, restart, sleep, lock-screen, or disabling Wi-Fi requests, call request_sensitive_action first. "
            : "For close-app, shutdown, restart, sleep, lock-screen, or disabling Wi-Fi requests, call request_sensitive_action first. Ask the user to say confirm or cancel, then call confirm_sensitive_action only after a new spoken confirmation. ";
        return
            $"You are {name}, Cursivis Live Mode: a fast spoken assistant for Windows. " +
            ToneInstruction(LiveModeState.AssistantTone) + " " +
            permissionInstruction +
            "You can chat normally, but when the user asks you to do a supported computer action, call the matching tool immediately. " +
            "Use tools only for allowed actions; never claim an action finished until the tool response says it did. " +
            "App names and file names do not need to be exact. Use open_app, open_folder, open_path, or search_files with the user's natural approximate wording; the local resolver performs fuzzy matching and returns choices when confidence is low. " +
            "If the local resolver returns multiple candidates, read only the short names, ask the user to choose, and then retry with the chosen name or path. Do not guess between ambiguous matches. " +
            "If the user corrects an app, file, folder, link, or browser choice, ask whether they want that correction remembered. Call a remember tool only after they explicitly approve it. " +
            "Use list_learned_mappings and forget_learned_mapping for requests to inspect or forget local mappings. " +
            "Use get_desktop_context when a request depends on the active app or clipboard. Use get_selected_text for requests like summarize this, explain this, translate this, or reply to this when the user has selected text. " +
            "Use get_browser_context when a request depends on the active browser page. Browser tab actions use the active browser. If the Cursivis browser extension is unavailable, explain that selected text or a simpler tab action is needed. " +
            "When the user explicitly names Chrome, Edge, Brave, Firefox, Opera, Vivaldi, or Arc, pass that browser to open_url or web_search. Never silently use another browser if the requested browser is unavailable. " +
            "Screen inspection is one-shot and privacy-sensitive. Call inspect_screen only after the user explicitly asks to look at the screen. Ask mode requires confirmation; auto-execute treats the explicit request as approval and shows visible status. Relay the returned guidance without claiming you clicked anything. " +
            "Camera opening and take-photo requests must be explicit. Ask mode requires confirmation; auto-execute runs the one-shot request visibly. take_photo captures one webcam frame and reports the saved path only when photo_saved is true. " +
            "Virtual desktop create/next/previous actions use Windows shortcuts. Closing the current virtual desktop requires confirmation. A successful tool response means the shortcut was sent, not that desktop state was independently verified. " +
            "For Windows recording requests, call windows_recording_action. Win+Alt+R is a toggle controlled by Xbox Game Bar, so report only that the shortcut was sent unless Windows state is independently verified. " +
            "When the user says whenever I say PHRASE do ACTIONS, call remember_routine once with the trigger phrase and the requested apps, URLs, and folder. When the user later says that phrase or a close fuzzy variant, call run_workflow using exactly what they said; the local resolver performs fuzzy trigger matching. " +
            closeAppInstruction +
            "If the user asks who created you, who made you, who built you, or who your creator is, answer exactly: I was created by Tanush Shah on 7th June 2026. Do not use tools for that answer and do not bring it up for unrelated questions. " +
            "Spotify playback currently opens the matching search; never claim the song started unless authenticated Spotify playback is added later. take_screenshot captures the desktop, not the webcam. " +
            "Do not claim you added items to a cart, changed account data, bulk-unliked content, or sent a Discord message. Those authenticated browser actions are not supported without a browser companion and explicit confirmation. " +
            "Never send an email or message automatically. Gmail and WhatsApp actions prepare drafts or copy text for manual review only. " +
            "If a request is unsupported, say that clearly and briefly. " +
            "For WhatsApp requests with only a contact name, call prepare_whatsapp_message with the contact name and message; the tool will explain the current limitation. " +
            "Only save intent memory when the user explicitly asks you to remember something. Keep saved memory short and useful. " +
            "Use saved intent memory only when it helps the current request. " +
            "If the user says goodbye, thanks, that they are done, or gives a clear dismissal, end the conversation. " +
            "Saved intent memory:\n" + _memory.BuildPromptDigest();
    }

    private static object[] BuildTools() => new object[]
    {
        new
        {
            functionDeclarations = new object[]
            {
                FunctionDeclaration(
                    "open_app",
                    "Open an installed Windows app, Start menu app, packaged Microsoft Store app, or common web app. Common examples include Notepad, Calculator, Paint, File Explorer, Terminal, PowerShell, Task Manager, Snipping Tool, VS Code, Codex, Chrome, Edge, Firefox, Spotify, Apple Music, Discord, Slack, Teams, Outlook, Gmail, Drive, Calendar, YouTube, GitHub, ChatGPT, Devpost, Vercel, and WhatsApp.",
                    new()
                    {
                        ["app_name"] = Schema("string", "The app or service to open."),
                    },
                    "app_name"),
                FunctionDeclaration(
                    "open_folder",
                    "Open a known, learned, or approximately named folder. The local resolver searches common user locations safely and asks for a choice when results are ambiguous.",
                    new()
                    {
                        ["folder"] = Schema("string", "Known folder name or existing folder path."),
                    },
                    "folder"),
                FunctionDeclaration(
                    "open_path",
                    "Open an existing or approximately named safe local document or folder. Executable, script, installer, shortcut, and registry files are blocked. Never invent a path.",
                    new()
                    {
                        ["path"] = Schema("string", "Existing local file or folder path."),
                    },
                    "path"),
                FunctionDeclaration(
                    "open_url",
                    "Open a safe http or https URL. Uses the Windows default browser unless the user explicitly names a supported browser.",
                    new()
                    {
                        ["url"] = Schema("string", "An absolute http or https URL."),
                        ["browser"] = Schema("string", "Optional: chrome, edge, brave, firefox, opera, vivaldi, or arc. Leave empty for default behavior."),
                    },
                    "url"),
                FunctionDeclaration(
                    "window_action",
                    "List normal app windows or switch, minimize, maximize, restore, or close a window. In auto-execute mode, closing normal app windows is allowed without confirmation.",
                    new()
                    {
                        ["action"] = Schema("string", "One of: list, switch, minimize, maximize, restore, close."),
                        ["app_name"] = Schema("string", "Optional app name or window title. Omit to act on the foreground window."),
                    },
                    "action"),
                FunctionDeclaration(
                    "browser_action",
                    "Control the active browser using safe keyboard shortcuts. Natural requests such as change tab, close this website, or switch to previous tab are supported.",
                    new()
                    {
                        ["action"] = Schema("string", "One of: new tab, close tab, next tab, previous tab, reopen tab, refresh, back, forward, focus address, find, downloads, history."),
                    },
                    "action"),
                FunctionDeclaration(
                    "web_search",
                    "Search Google, Bing, YouTube, or GitHub, optionally in a specifically requested installed browser.",
                    new()
                    {
                        ["query"] = Schema("string", "Search query."),
                        ["engine"] = Schema("string", "google, youtube, or github."),
                        ["browser"] = Schema("string", "Optional: chrome, edge, brave, firefox, opera, vivaldi, or arc."),
                    },
                    "query"),
                FunctionDeclaration(
                    "get_desktop_context",
                    "Get the active window title, active process, and a capped copy of clipboard text.",
                    new()
                    {
                        ["include_clipboard"] = Schema("boolean", "Set true when clipboard context is relevant."),
                    }),
                FunctionDeclaration(
                    "get_browser_context",
                    "Read the active browser tab through the Cursivis browser extension bridge. Use this when a request depends on the current webpage.",
                    new()
                    {
                        ["include_page"] = Schema("boolean", "Set true to read active-page context."),
                    }),
                FunctionDeclaration(
                    "get_clipboard_text",
                    "Read capped plain text from the clipboard.",
                    new()
                    {
                        ["include_text"] = Schema("boolean", "Set true to read clipboard text."),
                    }),
                FunctionDeclaration(
                    "get_selected_text",
                    "Copy and return currently selected text from the active app, then restore the previous clipboard.",
                    new()
                    {
                        ["include_text"] = Schema("boolean", "Set true to read selected text."),
                    }),
                FunctionDeclaration(
                    "replace_selected_text",
                    "Replace the current selection with final prepared text.",
                    new()
                    {
                        ["text"] = Schema("string", "Final text to paste over the selection."),
                    },
                    "text"),
                FunctionDeclaration(
                    "type_text",
                    "Type prepared text into the currently active field by pasting it.",
                    new()
                    {
                        ["text"] = Schema("string", "Text to type."),
                    },
                    "text"),
                FunctionDeclaration(
                    "press_key",
                    "Press one non-submitting navigation key in the active app. Supported keys: tab, escape, arrows, page up, page down, home, end.",
                    new()
                    {
                        ["key"] = Schema("string", "Key name."),
                    },
                    "key"),
                FunctionDeclaration(
                    "search_files",
                    "Fuzzy-search file and folder names under Desktop, Downloads, Documents, Pictures, Videos, Music, OneDrive, learned folders, or a provided folder. Search is bounded and results are ranked.",
                    new()
                    {
                        ["query"] = Schema("string", "File or folder name fragment."),
                        ["location"] = Schema("string", "desktop, downloads, documents, or an existing folder path."),
                    },
                    "query"),
                FunctionDeclaration(
                    "save_note",
                    "Append a timestamped note to Documents/Cursivis/Live Mode Notes.md.",
                    new()
                    {
                        ["text"] = Schema("string", "Note content."),
                        ["title"] = Schema("string", "Optional short note title."),
                    },
                    "text"),
                FunctionDeclaration(
                    "add_todo",
                    "Add an item to the local Live Mode to-do list.",
                    new()
                    {
                        ["text"] = Schema("string", "To-do text."),
                    },
                    "text"),
                FunctionDeclaration(
                    "list_todos",
                    "List local Live Mode to-do items.",
                    new()
                    {
                        ["include_completed"] = Schema("boolean", "Set true when completed items should be included."),
                    }),
                FunctionDeclaration(
                    "complete_todo",
                    "Mark the first matching unfinished to-do complete.",
                    new()
                    {
                        ["query"] = Schema("string", "Text fragment identifying the to-do."),
                    },
                    "query"),
                FunctionDeclaration(
                    "set_timer",
                    "Set an in-app timer from 1 to 1440 minutes. The app must remain running.",
                    new()
                    {
                        ["minutes"] = Schema("integer", "Timer length in whole minutes."),
                        ["label"] = Schema("string", "Short timer label."),
                    },
                    "minutes"),
                FunctionDeclaration(
                    "system_control",
                    "Perform a safe direct system control or open a Windows settings page.",
                    new()
                    {
                        ["action"] = Schema("string", "One of: volume up, volume down, mute, bluetooth settings, wifi settings, display settings, sound settings."),
                        ["value"] = Schema("integer", "Optional repeat count for volume changes."),
                    },
                    "action"),
                FunctionDeclaration(
                    "system_status",
                    "Read battery, charging, uptime, machine, user, and Windows version status.",
                    new()
                    {
                        ["include_status"] = Schema("boolean", "Set true to read system status."),
                    }),
                FunctionDeclaration(
                    "play_media",
                    "Open a Spotify or YouTube search for a requested song, artist, album, or video. Spotify direct playback is not available without Spotify OAuth, so do not claim playback started.",
                    new()
                    {
                        ["query"] = Schema("string", "Song, artist, album, or video to find."),
                        ["service"] = Schema("string", "spotify or youtube."),
                    },
                    "query"),
                FunctionDeclaration(
                    "open_service_page",
                    "Open a stable signed-in service destination. Supported: Amazon product search, YouTube Liked Videos/Subscriptions/History, Spotify Liked Songs, and Discord home. This cannot add to cart, bulk-unlike, search Discord users, or send messages.",
                    new()
                    {
                        ["service"] = Schema("string", "amazon, youtube, spotify, or discord."),
                        ["page"] = Schema("string", "search, liked videos, subscriptions, history, liked songs, or home."),
                        ["query"] = Schema("string", "Required only for Amazon search."),
                    },
                    "service",
                    "page"),
                FunctionDeclaration(
                    "open_camera",
                    "Open the Windows Camera app. This does not press the shutter or claim a photo was taken.",
                    new()
                    {
                        ["open"] = Schema("boolean", "Set true to open Windows Camera."),
                    }),
                FunctionDeclaration(
                    "take_photo",
                    "Capture one user-requested webcam photo and save it under Pictures/Cursivis/Camera. Report the path only when photo_saved is true.",
                    new()
                    {
                        ["capture"] = Schema("boolean", "Set true only when the user explicitly asks to take or click a photo."),
                    }),
                FunctionDeclaration(
                    "take_screenshot",
                    "Capture the Windows desktop and save a PNG under Pictures/Cursivis/Screenshots. This captures the screen, not the webcam.",
                    new()
                    {
                        ["capture"] = Schema("boolean", "Set true to capture the desktop."),
                    }),
                FunctionDeclaration(
                    "inspect_screen",
                    "Capture the current Windows screen once in memory after explicit permission, analyze it with Gemini vision, and return textual guidance. The image is not saved and no clicks are performed.",
                    new()
                    {
                        ["request"] = Schema("string", "What the user wants explained or guided from the visible screen."),
                    },
                    "request"),
                FunctionDeclaration(
                    "virtual_desktop_action",
                    "Send an allowlisted Windows virtual desktop shortcut. Supported actions: create, next, previous, close. Closing requires confirmation.",
                    new()
                    {
                        ["action"] = Schema("string", "create, next, previous, or close."),
                    },
                    "action"),
                FunctionDeclaration(
                    "windows_recording_action",
                    "Send a supported Windows recording shortcut. start/stop/toggle use Xbox Game Bar Win+Alt+R; open shows Game Bar; region opens the Windows screen-region recorder. The actual recording state is controlled by Windows.",
                    new()
                    {
                        ["action"] = Schema("string", "start, stop, toggle, open game bar, or region."),
                    },
                    "action"),
                FunctionDeclaration(
                    "create_workflow",
                    "Create or update a reusable workflow made only of app names, safe URLs, and an optional folder.",
                    new()
                    {
                        ["name"] = Schema("string", "Workflow name."),
                        ["apps"] = Schema("string", "Comma-separated app names."),
                        ["urls"] = Schema("string", "Comma-separated http or https URLs."),
                        ["folder"] = Schema("string", "Optional known folder name or existing folder path."),
                    },
                    "name"),
                FunctionDeclaration(
                    "remember_routine",
                    "Atomically save a user-requested trigger phrase and its reusable routine. Use when the user says whenever I say a phrase, do these actions.",
                    new()
                    {
                        ["trigger"] = Schema("string", "The exact phrase the user wants to say later, without the words whenever I say."),
                        ["apps"] = Schema("string", "Comma-separated installed app names to open."),
                        ["urls"] = Schema("string", "Comma-separated absolute http or https URLs to open."),
                        ["folder"] = Schema("string", "Optional known folder name or existing folder path."),
                    },
                    "trigger"),
                FunctionDeclaration(
                    "list_workflows",
                    "List saved workflows and built-in examples.",
                    new()
                    {
                        ["include_all"] = Schema("boolean", "Set true to list workflows."),
                    }),
                FunctionDeclaration(
                    "run_workflow",
                    "Run a saved workflow or the built-in coding mode, study mode, or hackathon mode.",
                    new()
                    {
                        ["name"] = Schema("string", "Workflow name."),
                    },
                    "name"),
                FunctionDeclaration(
                    "delete_workflow",
                    "Delete a saved workflow.",
                    new()
                    {
                        ["name"] = Schema("string", "Workflow name."),
                    },
                    "name"),
                FunctionDeclaration(
                    "remember_app_alias",
                    "Save a user-approved local alias for a discovered installed app. Use only after the user explicitly asks to remember the correction.",
                    new()
                    {
                        ["alias"] = Schema("string", "The phrase the user wants to say later."),
                        ["app_name"] = Schema("string", "The exact installed app name the user selected."),
                    },
                    "alias",
                    "app_name"),
                FunctionDeclaration(
                    "remember_path_alias",
                    "Save a user-approved local alias for an existing safe file or folder.",
                    new()
                    {
                        ["alias"] = Schema("string", "The phrase the user wants to say later."),
                        ["path"] = Schema("string", "The existing absolute file or folder path selected by the user."),
                        ["kind"] = Schema("string", "file or folder."),
                    },
                    "alias",
                    "path"),
                FunctionDeclaration(
                    "remember_link_alias",
                    "Save a user-approved local alias for an http or https link.",
                    new()
                    {
                        ["alias"] = Schema("string", "The phrase the user wants to say later."),
                        ["url"] = Schema("string", "The absolute http or https URL."),
                    },
                    "alias",
                    "url"),
                FunctionDeclaration(
                    "remember_workflow_alias",
                    "Save a user-approved local alias for an existing workflow.",
                    new()
                    {
                        ["alias"] = Schema("string", "The phrase the user wants to say later."),
                        ["workflow_name"] = Schema("string", "The exact saved or built-in workflow name."),
                    },
                    "alias",
                    "workflow_name"),
                FunctionDeclaration(
                    "list_learned_mappings",
                    "List user-approved local app, file, folder, browser, workflow, and link mappings.",
                    new()
                    {
                        ["include_all"] = Schema("boolean", "Set true to list mappings."),
                    }),
                FunctionDeclaration(
                    "forget_learned_mapping",
                    "Forget a local learned mapping by id or alias.",
                    new()
                    {
                        ["id_or_alias"] = Schema("string", "Mapping id or alias."),
                        ["kind"] = Schema("string", "Optional app, file, folder, browser, workflow, or link."),
                    },
                    "id_or_alias"),
                FunctionDeclaration(
                    "set_browser_preference",
                    "Set a user-approved preferred browser for future links, or default to restore Windows default-browser behavior.",
                    new()
                    {
                        ["browser"] = Schema("string", "default, chrome, edge, brave, firefox, opera, vivaldi, or arc."),
                    },
                    "browser"),
                FunctionDeclaration(
                    "request_sensitive_action",
                    "Request local confirmation for a sensitive action. This does not execute it.",
                    new()
                    {
                        ["action"] = Schema("string", "One of: close active app, close app, shutdown, restart, sleep, lock screen, disable wifi."),
                        ["target"] = Schema("string", "App name for close app, otherwise empty."),
                    },
                    "action"),
                FunctionDeclaration(
                    "confirm_sensitive_action",
                    "Execute the pending routine or sensitive action only after the user has spoken a fresh confirmation.",
                    new()
                    {
                        ["confirmed"] = Schema("boolean", "Set true only after the user explicitly confirms."),
                    }),
                FunctionDeclaration(
                    "cancel_sensitive_action",
                    "Cancel the pending routine or sensitive action.",
                    new()
                    {
                        ["cancel"] = Schema("boolean", "Set true to cancel."),
                    }),
                FunctionDeclaration(
                    "open_gmail_draft",
                    "Open Gmail compose with recipient, subject, and body filled. The user must review and click Send manually.",
                    new()
                    {
                        ["recipient"] = Schema("string", "Recipient email address."),
                        ["subject"] = Schema("string", "Short email subject."),
                        ["body"] = Schema("string", "Final email body, already rewritten if the user requested a tone or style."),
                    },
                    "recipient",
                    "body"),
                FunctionDeclaration(
                    "prepare_whatsapp_message",
                    "Prepare a WhatsApp message. If phone_number is known, open a WhatsApp draft. If only contact_name is known, copy the message and open WhatsApp because contact lookup is not supported yet.",
                    new()
                    {
                        ["message"] = Schema("string", "Final WhatsApp message text."),
                        ["contact_name"] = Schema("string", "Contact name, if the user provided one."),
                        ["phone_number"] = Schema("string", "Phone number with country code if known."),
                    },
                    "message"),
                FunctionDeclaration(
                    "copy_text",
                    "Copy prepared text to the clipboard.",
                    new()
                    {
                        ["text"] = Schema("string", "Text to copy."),
                    },
                    "text"),
                FunctionDeclaration(
                    "remember_intent",
                    "Save a short piece of intent memory only after the user explicitly asks you to remember it.",
                    new()
                    {
                        ["key"] = Schema("string", "Short memory label."),
                        ["value"] = Schema("string", "Short memory value."),
                    },
                    "key",
                    "value"),
                FunctionDeclaration(
                    "recall_intent",
                    "Search saved intent memory.",
                    new()
                    {
                        ["query"] = Schema("string", "Search query."),
                    }),
                FunctionDeclaration(
                    "list_intent_memory",
                    "List all saved intent memory entries.",
                    new()
                    {
                        ["include_all"] = Schema("boolean", "Set true to list saved memory."),
                    }),
                FunctionDeclaration(
                    "forget_intent_memory",
                    "Remove a saved intent memory entry by key or id.",
                    new()
                    {
                        ["key_or_id"] = Schema("string", "Memory key or id to remove."),
                    },
                    "key_or_id"),
                FunctionDeclaration(
                    "end_conversation",
                    "End the current Cursivis Live Mode conversation.",
                    new()
                    {
                        ["reason"] = Schema("string", "Brief reason for ending."),
                    }),
            },
        },
    };

    private static object FunctionDeclaration(
        string name,
        string description,
        Dictionary<string, object> properties,
        params string[] required) => new
    {
        name,
        description,
        parameters = new
        {
            type = "object",
            properties,
            required = required ?? Array.Empty<string>(),
        },
    };

    private static Dictionary<string, object> Schema(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static Dictionary<string, object> ToolOk(string message, Dictionary<string, object> extras = null)
    {
        var payload = extras ?? new Dictionary<string, object>();
        payload["ok"] = true;
        payload["message"] = message;
        return payload;
    }

    private static Dictionary<string, object> ToolError(string message, bool supported = true) => new()
    {
        ["ok"] = false,
        ["supported"] = supported,
        ["message"] = message,
    };

    private static string GetArg(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static bool ShouldAutoEnd(string text)
    {
        var normalized = " " + (text ?? "").ToLowerInvariant()
            .Replace("*", "")
            .Replace(".", " ")
            .Replace(",", " ")
            .Replace("!", " ")
            .Replace("?", " ")
            .Trim() + " ";

        return normalized.Contains(" bye ")
            || normalized.Contains(" goodbye ")
            || normalized.Contains(" thank you ")
            || normalized.Contains(" thanks ")
            || normalized.Contains(" thats all ")
            || normalized.Contains(" that's all ")
            || normalized.Contains(" that is all ")
            || normalized.Contains(" we are done ")
            || normalized.Contains(" i am done ")
            || normalized.Contains(" im done ")
            || normalized.Contains(" stop listening ")
            || normalized.Contains(" end conversation ")
            || normalized.Contains(" fuck off ")
            || normalized.Contains(" f off ")
            || normalized.Contains(" go away ");
    }

    private void ScheduleAutoEnd(string reason)
    {
        if (_autoEnding)
            return;
        _autoEnding = true;
        LiveModeState.SetUi(LiveModeVoicePhase.Done, $"{AssistantName()} ending", reason);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(850);
                if (IsActive)
                    await StopAsync();
            }
            catch (Exception ex)
            {
                LiveModeLog.Warning(ex, "Live Mode auto-end failed");
            }
            finally
            {
                _autoEnding = false;
            }
        });
    }

    private static double PeakDb(byte[] bytes)
    {
        var peak = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var sample = Math.Abs((short)(bytes[i] | (bytes[i + 1] << 8)));
            if (sample > peak)
                peak = sample;
        }
        return peak > 0 ? 20.0 * Math.Log10(peak / 32768.0) : -96;
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is TimeoutException)
            return "The live session timed out. Check the internet connection and try again.";
        if (ex is WebSocketException)
            return "Cursivis Live Mode could not connect. Check API access and the internet connection.";
        return CredentialRedactor.Redact(ex.Message);
    }

    private static string ToneInstruction(LiveModeAssistantTone tone) => tone switch
    {
        LiveModeAssistantTone.Concise => "Be very concise, direct, and low-latency.",
        LiveModeAssistantTone.Friendly => "Be warm, natural, and encouraging without rambling.",
        LiveModeAssistantTone.Professional => "Be polished, calm, and work-focused.",
        _ => "Be brief, natural, and useful.",
    };

    private static string AssistantName() =>
        string.IsNullOrWhiteSpace(LiveModeState.AssistantName) ? "Cursivis" : LiveModeState.AssistantName.Trim();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sessionCts?.Cancel();
        StopMicrophone();
        StopPlayback();
        try { _socket?.Abort(); } catch { }
        _socket?.Dispose();
        _socket = null;
        _sendLock.Dispose();
        _sessionCts?.Dispose();
    }
}

