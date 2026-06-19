#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Cursivis.Companion.Services;

public sealed class LiveModeAutomationService : IDisposable
{
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const uint WmClose = 0x0010;
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const uint KeyeventfKeyup = 0x0002;

    private readonly LiveModeNotificationService _notifications;
    private readonly LiveModeSettingsService _settings;
    private readonly LiveModeActionHistoryService _history;
    private readonly LearnedMappingService _mappings;
    private readonly AppResolverService _apps;
    private readonly SmartFileSearchService _files;
    private readonly BrowserLauncherService _browsers;
    private readonly GeminiScreenGuidanceService _screenGuidance = new();
    private readonly WebcamCaptureService _webcam = new();
    private readonly ExtensionAutomationClient _extensionAutomationClient;
    private readonly Action _openSettings;
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, System.Threading.Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _todosPath;
    private PendingJarvisAction _pending;
    private string _confirmedPendingActionId = "";
    private DateTimeOffset? _lastExplicitConfirmationAt;
    private bool _executingApprovedAction;
    private bool _disposed;

    public LiveModeAutomationService(
        LiveModeNotificationService notifications,
        LiveModeSettingsService settings,
        LiveModeActionHistoryService history,
        LearnedMappingService mappings,
        ExtensionAutomationClient extensionAutomationClient,
        Action openSettings)
    {
        _notifications = notifications;
        _settings = settings;
        _history = history;
        _mappings = mappings;
        _apps = new AppResolverService(mappings);
        _files = new SmartFileSearchService(mappings);
        _browsers = new BrowserLauncherService(_apps, settings);
        _extensionAutomationClient = extensionAutomationClient;
        _openSettings = openSettings;
        _todosPath = Path.Combine(settings.DataDir, "live-mode-todos.json");
    }

    public async Task<Dictionary<string, object>> ExecuteAsync(
        string toolName,
        JsonElement args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        toolName = (toolName ?? "").Trim();
        if (ShouldRequestPermission(toolName, args))
            return RequestRoutinePermission(toolName, args);

        return await ExecuteCoreAsync(toolName, args, token);
    }

    private async Task<Dictionary<string, object>> ExecuteCoreAsync(
        string toolName,
        JsonElement args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return toolName switch
        {
            "open_app" => OpenApp(GetString(args, "app_name")),
            "open_url" => OpenUrl(GetString(args, "url"), GetString(args, "browser")),
            "open_folder" => await OpenFolderAsync(GetString(args, "folder"), token),
            "open_path" => await OpenPathAsync(GetString(args, "path"), token),
            "window_action" => WindowAction(GetString(args, "action"), GetString(args, "app_name")),
            "browser_action" => BrowserAction(GetString(args, "action")),
            "web_search" => WebSearch(
                GetString(args, "query"),
                GetString(args, "engine") ?? GetString(args, "service"),
                GetString(args, "browser")),
            "get_desktop_context" => GetDesktopContext(),
            "get_browser_context" => await GetBrowserContextAsync(token),
            "get_clipboard_text" => GetClipboardText(),
            "get_selected_text" => await GetSelectedTextAsync(token),
            "replace_selected_text" => await ReplaceSelectedTextAsync(GetString(args, "text"), token),
            "type_text" => await TypeTextAsync(GetString(args, "text"), token),
            "press_key" => PressKey(GetString(args, "key")),
            "search_files" => await SearchFilesAsync(
                GetString(args, "query"),
                GetString(args, "location"),
                token),
            "save_note" => SaveNote(GetString(args, "text"), GetString(args, "title")),
            "add_todo" => AddTodo(GetString(args, "text")),
            "list_todos" => ListTodos(),
            "complete_todo" => CompleteTodo(GetString(args, "query")),
            "set_timer" => SetTimer(GetInt(args, "minutes"), GetString(args, "label")),
            "system_control" => SystemControl(GetString(args, "action"), GetInt(args, "value")),
            "system_status" => SystemStatus(),
            "play_media" => PlayMedia(GetString(args, "query"), GetString(args, "service")),
            "open_service_page" => OpenServicePage(
                GetString(args, "service"),
                GetString(args, "page"),
                GetString(args, "query")),
            "open_camera" => OpenCamera(),
            "take_photo" => await TakePhotoAsync(token),
            "take_screenshot" => TakeScreenshot(),
            "inspect_screen" => await InspectScreenAsync(GetString(args, "request"), token),
            "virtual_desktop_action" => VirtualDesktopAction(GetString(args, "action")),
            "windows_recording_action" => WindowsRecordingAction(GetString(args, "action")),
            "open_gmail_draft" => OpenGmailDraft(
                GetString(args, "recipient"),
                GetString(args, "subject"),
                GetString(args, "body")),
            "prepare_whatsapp_message" => PrepareWhatsAppMessage(
                GetString(args, "message"),
                GetString(args, "contact_name"),
                GetString(args, "phone_number")),
            "copy_text" => CopyText(GetString(args, "text")),
            "create_workflow" => CreateWorkflow(args),
            "remember_routine" => RememberRoutine(args),
            "list_workflows" => ListWorkflows(),
            "run_workflow" => await RunWorkflowAsync(GetString(args, "name"), token),
            "delete_workflow" => DeleteWorkflow(GetString(args, "name")),
            "remember_app_alias" => RememberAppAlias(
                GetString(args, "alias"),
                GetString(args, "app_name")),
            "remember_path_alias" => RememberPathAlias(
                GetString(args, "alias"),
                GetString(args, "path"),
                GetString(args, "kind")),
            "remember_link_alias" => RememberLinkAlias(
                GetString(args, "alias"),
                GetString(args, "url")),
            "remember_workflow_alias" => RememberWorkflowAlias(
                GetString(args, "alias"),
                GetString(args, "workflow_name")),
            "list_learned_mappings" => ListLearnedMappings(),
            "forget_learned_mapping" => ForgetLearnedMapping(
                GetString(args, "id_or_alias"),
                GetString(args, "kind")),
            "set_browser_preference" => SetBrowserPreference(GetString(args, "browser")),
            "request_sensitive_action" => RequestSensitiveAction(
                GetString(args, "action"),
                GetString(args, "target")),
            "confirm_sensitive_action" => await ConfirmPendingActionAsync(token),
            "cancel_sensitive_action" => CancelPendingAction(),
            _ => Result(false, $"{toolName} is not a supported desktop action yet.", supported: false),
        };
    }

    private bool ShouldRequestPermission(string toolName, JsonElement args)
    {
        return LiveModePermissionPolicy.RequiresConfirmation(
            toolName,
            GetString(args, "action"),
            toolName == "open_app" ? GetString(args, "app_name") : "",
            _settings.Current.LiveModePermissionMode,
            _executingApprovedAction);
    }

    private Dictionary<string, object> RequestRoutinePermission(string toolName, JsonElement args)
    {
        var description = DescribeRoutineAction(toolName, args);
        lock (_pendingLock)
        {
            _pending = new PendingJarvisAction
            {
                ToolName = toolName,
                Args = args.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { })
                    : args.Clone(),
                Action = toolName,
                Description = description,
            };
            ClearPendingConfirmationLocked();
        }

        LiveModeState.SetUi(LiveModeVoicePhase.Thinking, "Permission required", description);
        _history.Add("permission_requested", description, true, true);
        return Result(
            true,
            $"{description}. Ask the user to say confirm or cancel.",
            confirmationRequired: true,
            extras: new()
            {
                ["description"] = description,
                ["permission_mode"] = _settings.Current.LiveModePermissionMode.ToString(),
                ["always_requires_confirmation"] = toolName is "open_camera" or "take_photo"
                    or "take_screenshot" or "inspect_screen"
                    || (toolName == "virtual_desktop_action"
                        && NormalizeWords(GetString(args, "action")) is "close" or "close desktop" or "close current desktop"),
            });
    }

    private static string DescribeRoutineAction(string toolName, JsonElement args) => toolName switch
    {
        "open_app" => $"Open {GetString(args, "app_name")}",
        "open_url" => $"Open {GetString(args, "url")}",
        "open_folder" => $"Open the {GetString(args, "folder")} folder",
        "open_path" => $"Open {GetString(args, "path")}",
        "window_action" => $"{GetString(args, "action")} the requested window",
        "browser_action" => $"{GetString(args, "action")} in the active browser",
        "web_search" => $"Search for {GetString(args, "query")}",
        "replace_selected_text" => "Replace the selected text",
        "type_text" => "Type text into the active field",
        "press_key" => $"Press {GetString(args, "key")}",
        "save_note" => "Save a local note",
        "add_todo" => "Add a local to-do",
        "complete_todo" => "Complete a local to-do",
        "set_timer" => "Start a timer",
        "system_control" => $"{GetString(args, "action")}",
        "play_media" => $"Open {GetString(args, "query")} on {GetString(args, "service")}",
        "open_service_page" => $"Open {GetString(args, "page")} on {GetString(args, "service")}",
        "open_camera" => "Open the Windows Camera app",
        "take_photo" => "Open Windows Camera for a manually reviewed photo",
        "take_screenshot" => "Capture and save the current screen",
        "inspect_screen" => "Inspect the current screen once with Gemini",
        "virtual_desktop_action" => $"{GetString(args, "action")} virtual desktop",
        "windows_recording_action" => $"{GetString(args, "action")} Windows screen recording",
        "open_gmail_draft" => $"Prepare a Gmail draft for {GetString(args, "recipient")}",
        "prepare_whatsapp_message" => "Prepare a WhatsApp message",
        "copy_text" => "Copy prepared text to the clipboard",
        "create_workflow" => "Save a Live Mode workflow",
        "remember_routine" => $"Remember the phrase {GetString(args, "trigger")} as a routine",
        "run_workflow" => $"Run the {GetString(args, "name")} workflow",
        "delete_workflow" => $"Delete the {GetString(args, "name")} workflow",
        "remember_app_alias" => $"Remember {GetString(args, "alias")} as {GetString(args, "app_name")}",
        "remember_path_alias" => $"Remember {GetString(args, "alias")} as a local path",
        "remember_link_alias" => $"Remember {GetString(args, "alias")} as a web link",
        "remember_workflow_alias" => $"Remember {GetString(args, "alias")} as workflow {GetString(args, "workflow_name")}",
        "forget_learned_mapping" => $"Forget {GetString(args, "id_or_alias")}",
        "set_browser_preference" => $"Use {GetString(args, "browser")} as the preferred browser",
        _ => $"Run {toolName.Replace('_', ' ')}",
    };

    public void ObserveUserTranscript(string text)
    {
        PendingJarvisAction pending;
        lock (_pendingLock)
            pending = _pending;
        if (pending == null)
            return;

        var normalized = NormalizePhraseText(text);
        if (ContainsPhrase(normalized, "cancel", "no", "nope", "do not", "don't", "stop", "never mind"))
        {
            lock (_pendingLock)
            {
                _pending = null;
                ClearPendingConfirmationLocked();
            }
            _history.Add("confirmation_cancelled", pending.Description, true);
            return;
        }

        if (normalized.Length <= 80
            && ContainsPhrase(normalized, "yes", "yeah", "yep", "sure", "confirm", "go ahead", "proceed", "do it", "okay do it", "ok do it"))
        {
            lock (_pendingLock)
            {
                if (_pending?.Id == pending.Id)
                {
                    _pending.ConfirmedAt = DateTimeOffset.UtcNow;
                    _confirmedPendingActionId = pending.Id;
                    _lastExplicitConfirmationAt = _pending.ConfirmedAt;
                }
            }
        }
    }

    public object PendingSnapshot()
    {
        lock (_pendingLock)
        {
            if (_pending == null)
                return null;
            return new
            {
                _pending.Id,
                _pending.Action,
                _pending.Target,
                _pending.Description,
                _pending.ToolName,
                _pending.IsSensitive,
                _pending.RequestedAt,
                _pending.ConfirmedAt,
            };
        }
    }

    private Dictionary<string, object> OpenApp(string appName)
    {
        var name = NormalizeWords(appName);
        if (string.IsNullOrWhiteSpace(name))
            return Result(false, "Which app should I open?", needsClarification: true);

        if (name is "downloads" or "download folder" or "downloads folder"
            or "documents" or "documents folder" or "pictures" or "pictures folder")
        {
            var known = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return OpenKnownFolder(known);
        }

        var webApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail"] = "https://mail.google.com/",
            ["google drive"] = "https://drive.google.com/",
            ["drive"] = "https://drive.google.com/",
            ["google calendar"] = "https://calendar.google.com/",
            ["calendar"] = "https://calendar.google.com/",
            ["youtube"] = "https://www.youtube.com/",
            ["github"] = "https://github.com/",
            ["chatgpt"] = "https://chatgpt.com/",
            ["devpost"] = "https://devpost.com/",
            ["vercel"] = "https://vercel.com/",
            ["whatsapp"] = "https://web.whatsapp.com/",
        };
        if (webApps.TryGetValue(name, out var webUrl))
            return OpenUrl(webUrl, "");

        if (name is "settings" or "keyboard settings" or "Cursivis settings")
        {
            _openSettings();
            return Logged("open_app", "Cursivis settings", true, "Opened Cursivis settings.");
        }
        if (name is "camera" or "windows camera")
            return OpenCamera();
        var resolution = _apps.Resolve(appName);
        if (resolution.Status == AppResolutionStatus.Found)
        {
            if (_apps.TryLaunch(resolution.Best, out var error))
                return Logged(
                    "open_app",
                    $"{appName} -> {resolution.Best.Name} ({resolution.Best.Source})",
                    true,
                    $"Opened {resolution.Best.Name}.",
                    extras: new()
                    {
                        ["matched_name"] = resolution.Best.Name,
                        ["match_score"] = Math.Round(resolution.Best.Score, 3),
                        ["source"] = resolution.Best.Source,
                    });
            return Logged("open_app", appName, false, $"I found {resolution.Best.Name}, but Windows could not open it: {error}", supported: false);
        }

        if (resolution.Status == AppResolutionStatus.Ambiguous)
        {
            return Result(
                false,
                $"I found several possible apps for {appName}. Ask the user to choose one by name.",
                needsClarification: true,
                extras: new()
                {
                    ["candidates"] = resolution.Candidates.Select(candidate => new
                    {
                        name = candidate.Name,
                        source = candidate.Source,
                        score = Math.Round(candidate.Score, 3),
                    }).ToArray(),
                    ["searched"] = resolution.SearchSummary,
                });
        }

        if (string.Equals(
            AppResolverService.CanonicalizeNaturalName(appName),
            "Apple Music",
            StringComparison.OrdinalIgnoreCase))
        {
            var webResult = OpenUrl("https://music.apple.com/", "");
            webResult["message"] = "Apple Music was not installed, so I opened Apple Music on the web.";
            return webResult;
        }

        return Logged(
            "open_app",
            name,
            false,
            $"I could not find {appName}. I searched {resolution.SearchSummary}.",
            supported: false,
            extras: new() { ["searched"] = resolution.SearchSummary });
    }

    private Dictionary<string, object> OpenKnownFolder(string folder)
    {
        var name = NormalizeWords(folder);
        var path = name switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" or "photos" or "images" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "home" or "user folder" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "onedrive" or "one drive" => Environment.GetEnvironmentVariable("OneDrive"),
            "app data" or "keyboard data" or "cursivis data" => _settings.DataDir,
            "voice notes" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Cursivis"),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;
        if (!SmartFileSearchService.TryOpenSafe(path, out var error))
            return Logged("open_folder", path, false, error, supported: false);
        return Logged("open_folder", path, true, $"Opened {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}.");
    }

    private async Task<Dictionary<string, object>> OpenFolderAsync(string folder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Result(false, "Which folder should I open?", needsClarification: true);
        var known = OpenKnownFolder(folder);
        if (known != null)
            return known;

        var expanded = ExpandPath(folder);
        if (Directory.Exists(expanded))
        {
            if (SmartFileSearchService.TryOpenSafe(expanded, out var error))
                return Logged("open_folder", expanded, true, $"Opened {Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar))}.");
            return Logged("open_folder", expanded, false, error, supported: false);
        }

        var result = await _files.SearchAsync(
            folder,
            "",
            includeFiles: false,
            includeFolders: true,
            token);
        return OpenResolvedSearchResult("open_folder", folder, result);
    }

    private async Task<Dictionary<string, object>> OpenPathAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result(false, "Which file or folder should I open?", needsClarification: true);
        var expanded = ExpandPath(path);
        if (File.Exists(expanded) || Directory.Exists(expanded))
        {
            if (SmartFileSearchService.IsRiskyFile(expanded))
                return Logged(
                    "open_path",
                    expanded,
                    false,
                    "Opening executable, script, installer, shortcut, or registry files by path is blocked. Ask me to open an installed app by name instead.",
                    supported: false);
            if (SmartFileSearchService.TryOpenSafe(expanded, out var error))
                return Logged("open_path", expanded, true, $"Opened {Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar))}.");
            return Logged("open_path", expanded, false, error, supported: false);
        }

        var link = _mappings.FindExact(LearnedMappingKind.Link, path);
        if (link != null)
            return OpenUrl(link.Target, "");

        var result = await _files.SearchAsync(
            path,
            "",
            includeFiles: true,
            includeFolders: true,
            token);
        return OpenResolvedSearchResult("open_path", path, result);
    }

    private Dictionary<string, object> OpenResolvedSearchResult(
        string action,
        string query,
        FileSearchResult search)
    {
        if (search.Status == FileResolutionStatus.Found)
        {
            if (SmartFileSearchService.IsRiskyFile(search.Best.Path))
                return Logged(
                    action,
                    search.Best.Path,
                    false,
                    "I found a matching executable, script, installer, shortcut, or registry file, but automatic opening is blocked.",
                    supported: false);
            if (SmartFileSearchService.TryOpenSafe(search.Best.Path, out var error))
            {
                return Logged(
                    action,
                    $"{query} -> {search.Best.Path}",
                    true,
                    $"Opened {search.Best.Name}.",
                    extras: new()
                    {
                        ["path"] = search.Best.Path,
                        ["score"] = Math.Round(search.Best.Score, 3),
                        ["searched"] = search.SearchSummary,
                    });
            }
            return Logged(action, search.Best.Path, false, error, supported: false);
        }

        if (search.Status == FileResolutionStatus.Ambiguous)
        {
            return Result(
                false,
                $"I found several plausible matches for {query}. Ask the user to choose one.",
                needsClarification: true,
                extras: new()
                {
                    ["matches"] = search.Candidates.Select(candidate => new
                    {
                        candidate.Name,
                        candidate.Path,
                        type = candidate.IsDirectory ? "folder" : "file",
                        modified_at = candidate.ModifiedAt,
                        score = Math.Round(candidate.Score, 3),
                    }).ToArray(),
                    ["searched"] = search.SearchSummary,
                    ["scanned_entries"] = search.ScannedEntries,
                });
        }

        var timeout = search.TimedOut ? " The bounded search reached its time limit." : "";
        return Logged(
            action,
            query,
            false,
            $"No matching safe file or folder was found in {search.SearchSummary}.{timeout}",
            supported: false,
            extras: new()
            {
                ["searched"] = search.SearchSummary,
                ["scanned_entries"] = search.ScannedEntries,
                ["timed_out"] = search.TimedOut,
            });
    }

    private Dictionary<string, object> WindowAction(string action, string appName)
    {
        var normalized = NormalizeWords(action);
        if (normalized is "list" or "list windows")
        {
            var windows = EnumerateWindows()
                .Take(15)
                .Select(w => new { w.Title, w.ProcessName })
                .ToArray();
            return Result(true, windows.Length == 0 ? "No normal app windows were found." : "Open windows listed.",
                extras: new() { ["windows"] = windows });
        }

        var handle = string.IsNullOrWhiteSpace(appName)
            ? GetForegroundWindow()
            : FindWindowByName(appName);
        if (handle == IntPtr.Zero)
            return Logged("window_action", $"{action} {appName}", false, "I could not find that window.", supported: false);

        switch (normalized)
        {
            case "switch":
            case "focus":
            case "activate":
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return Logged("window_action", $"focus {appName}", true, "Switched to the window.");
            case "minimize":
                ShowWindow(handle, SwMinimize);
                return Logged("window_action", $"minimize {appName}", true, "Window minimized.");
            case "maximize":
                ShowWindow(handle, SwMaximize);
                return Logged("window_action", $"maximize {appName}", true, "Window maximized.");
            case "restore":
                ShowWindow(handle, SwRestore);
                return Logged("window_action", $"restore {appName}", true, "Window restored.");
            case "close":
            case "quit":
            case "exit":
                var title = GetWindowTitle(handle);
                var label = string.IsNullOrWhiteSpace(title)
                    ? string.IsNullOrWhiteSpace(appName) ? "the active window" : appName
                    : Limit(title, 80);
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("window_action", $"close {label}", true, $"Closed {label}.");
            default:
                return Result(false, "Supported window actions are list, switch, minimize, maximize, restore, and close.", supported: false);
        }
    }

    private Dictionary<string, object> BrowserAction(string action)
    {
        var activeProcess = GetProcessName(GetForegroundWindow());
        if (!IsBrowserProcess(activeProcess))
            return Result(
                false,
                "A supported browser must be the active window before I can control its tabs.",
                supported: false,
                needsClarification: true,
                extras: new() { ["active_process"] = activeProcess });

        var keys = BrowserShortcutForAction(action);
        if (keys == null)
            return Result(false, "That browser action is not supported yet.", supported: false);
        SendKeysOnUiThread(keys);
        return Logged(
            "browser_action",
            action,
            true,
            $"Sent the browser shortcut for {action}.",
            extras: new() { ["shortcut_sent"] = true, ["verified_browser_state"] = false });
    }

    public static string BrowserShortcutForAction(string action)
    {
        return NormalizeWords(action) switch
        {
            "new tab" => "^t",
            "close tab" or "close this tab" or "close website" or "close this website" => "^w",
            "next tab" or "change tab" or "switch tab" or "go to next tab" or "go to the next tab" => "^{TAB}",
            "previous tab" or "switch to previous tab" or "go to previous tab" or "go to the previous tab" => "^+{TAB}",
            "reopen tab" or "reopen closed tab" => "^+t",
            "refresh" or "reload" => "^r",
            "back" => "%{LEFT}",
            "forward" => "%{RIGHT}",
            "focus address" or "address bar" => "^l",
            "find" or "find on page" => "^f",
            "downloads" => "^j",
            "history" => "^h",
            _ => null,
        };
    }

    private Dictionary<string, object> WebSearch(string query, string engine, string browser)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What should I search for?", needsClarification: true);
        return OpenUrl(BrowserLauncherService.BuildSearchUrl(query, engine), browser);
    }

    private Dictionary<string, object> GetDesktopContext()
    {
        var handle = GetForegroundWindow();
        var title = GetWindowTitle(handle);
        var processName = GetProcessName(handle);
        var clipboard = Limit(ClipboardHelper.GetText(), 2500);
        return Result(true, "Desktop context captured.", extras: new()
        {
            ["active_window_title"] = title,
            ["active_process"] = processName,
            ["clipboard_text"] = clipboard,
            ["clipboard_truncated"] = clipboard.Length >= 2500,
        });
    }

    private async Task<Dictionary<string, object>> GetBrowserContextAsync(CancellationToken token)
    {
        var context = await _extensionAutomationClient.TryGetActiveTabContextAsync(token);
        if (context?.Ok != true)
        {
            return Result(
                false,
                "The Cursivis browser extension is not connected. Select the relevant text or use a simple browser-tab action instead.",
                supported: false);
        }

        var page = context.PageContext;
        return Result(true, "Active browser context captured.", extras: new()
        {
            ["browser"] = context.BrowserName ?? string.Empty,
            ["url"] = Limit(page.Url, 1200),
            ["title"] = Limit(page.Title, 500),
            ["visible_text"] = Limit(page.VisibleText, 5000),
            ["visible_text_truncated"] = page.VisibleText.Length > 5000,
            ["interactive_elements"] = page.InteractiveElements
                .Take(60)
                .Select(element => new
                {
                    role = Limit(element.Role, 80),
                    label = Limit(element.Label, 240),
                    type = Limit(element.Type, 80),
                })
                .ToArray(),
        });
    }

    private Dictionary<string, object> GetClipboardText()
    {
        var text = Limit(ClipboardHelper.GetText(), 6000);
        return Result(true, string.IsNullOrWhiteSpace(text) ? "Clipboard is empty." : "Clipboard text captured.",
            extras: new() { ["text"] = text });
    }

    private async Task<Dictionary<string, object>> GetSelectedTextAsync(CancellationToken token)
    {
        var previous = ClipboardHelper.Capture();
        var marker = $"keyboard-wtf-{Guid.NewGuid():N}";
        string selected;
        try
        {
            ClipboardHelper.SetText(marker);
            SendKeysOnUiThread("^c");
            await Task.Delay(180, token);
            selected = ClipboardHelper.GetText();
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
        if (selected == marker || string.IsNullOrWhiteSpace(selected))
            return Result(false, "No selected text was detected. Select text first and try again.", supported: false);
        return Result(true, "Selected text captured.", extras: new()
        {
            ["text"] = Limit(selected, 6000),
            ["truncated"] = selected.Length > 6000,
        });
    }

    private async Task<Dictionary<string, object>> ReplaceSelectedTextAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What text should replace the selection?", needsClarification: true);
        var previous = ClipboardHelper.Capture();
        try
        {
            ClipboardHelper.SetText(text);
            SendKeysOnUiThread("^v");
            await Task.Delay(220, token);
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
        return Logged("replace_selected_text", Limit(text, 120), true, "Replaced the selected text.");
    }

    private async Task<Dictionary<string, object>> TypeTextAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I type?", needsClarification: true);
        var previous = ClipboardHelper.Capture();
        try
        {
            ClipboardHelper.SetText(text);
            SendKeysOnUiThread("^v");
            await Task.Delay(220, token);
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
        return Logged("type_text", Limit(text, 120), true, "Typed the text into the active field.");
    }

    private Dictionary<string, object> PressKey(string key)
    {
        var sequence = NormalizeWords(key) switch
        {
            "tab" => "{TAB}",
            "escape" or "esc" => "{ESC}",
            "up" => "{UP}",
            "down" => "{DOWN}",
            "left" => "{LEFT}",
            "right" => "{RIGHT}",
            "page up" => "{PGUP}",
            "page down" => "{PGDN}",
            "home" => "{HOME}",
            "end" => "{END}",
            _ => null,
        };
        if (sequence == null)
            return Result(false, "That key is not supported.", supported: false);
        SendKeysOnUiThread(sequence);
        return Logged("press_key", key, true, $"Pressed {key}.");
    }

    private async Task<Dictionary<string, object>> SearchFilesAsync(
        string query,
        string location,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What file or folder should I search for?", needsClarification: true);

        var search = await _files.SearchAsync(
            query,
            location,
            includeFiles: true,
            includeFolders: true,
            token,
            maxResults: 15);
        var success = search.Status is FileResolutionStatus.Found or FileResolutionStatus.Ambiguous;
        _history.Add("search_files", $"{query} in {location}", success);
        return Result(
            success,
            search.Candidates.Count == 0
                ? $"No matching files or folders were found in {search.SearchSummary}."
                : $"Found {search.Candidates.Count} ranked matches.",
            extras: new()
            {
                ["matches"] = search.Candidates.Select(candidate => new
                {
                    candidate.Name,
                    candidate.Path,
                    type = candidate.IsDirectory ? "folder" : "file",
                    modified_at = candidate.ModifiedAt,
                    score = Math.Round(candidate.Score, 3),
                    risky = SmartFileSearchService.IsRiskyFile(candidate.Path),
                }).ToArray(),
                ["searched"] = search.SearchSummary,
                ["scanned_entries"] = search.ScannedEntries,
                ["timed_out"] = search.TimedOut,
            });
    }

    private Dictionary<string, object> SaveNote(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I save in the note?", needsClarification: true);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cursivis");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Live Mode Notes.md");
        var heading = string.IsNullOrWhiteSpace(title) ? "Quick note" : title.Trim();
        File.AppendAllText(path, $"{Environment.NewLine}## {heading} - {DateTime.Now:g}{Environment.NewLine}{text.Trim()}{Environment.NewLine}");
        return Logged("save_note", path, true, "Saved the note.", extras: new() { ["path"] = path });
    }

    private Dictionary<string, object> AddTodo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I add to the to-do list?", needsClarification: true);
        var todos = LoadTodos();
        todos.Add(new JarvisTodo { Text = Limit(text, 240) });
        SaveTodos(todos);
        return Logged("add_todo", text, true, "Added to the to-do list.");
    }

    private Dictionary<string, object> ListTodos()
    {
        var todos = LoadTodos();
        return Result(true, todos.Count == 0 ? "The to-do list is empty." : "To-do list loaded.", extras: new()
        {
            ["todos"] = todos.Select(t => new { t.Id, t.Text, t.Completed, t.CreatedAt }).ToArray(),
        });
    }

    private Dictionary<string, object> CompleteTodo(string query)
    {
        var todos = LoadTodos();
        var todo = todos.FirstOrDefault(t =>
            !t.Completed && t.Text.Contains(query ?? "", StringComparison.OrdinalIgnoreCase));
        if (todo == null)
            return Result(false, "I could not find a matching unfinished to-do.", supported: false);
        todo.Completed = true;
        SaveTodos(todos);
        return Logged("complete_todo", todo.Text, true, "Marked the to-do complete.");
    }

    private Dictionary<string, object> SetTimer(int minutes, string label)
    {
        minutes = Math.Clamp(minutes, 1, 1440);
        var timerLabel = string.IsNullOrWhiteSpace(label) ? $"{minutes} minute timer" : Limit(label, 80);
        var id = Guid.NewGuid().ToString("N");
        var timer = new System.Threading.Timer(_ =>
        {
            _notifications.Info("Live Mode timer", timerLabel);
            _history.Add("timer_finished", timerLabel, true);
            lock (_timers)
            {
                if (_timers.Remove(id, out var completed))
                    completed.Dispose();
            }
        }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        lock (_timers)
            _timers[id] = timer;
        return Logged("set_timer", $"{minutes} minutes: {timerLabel}", true, $"Timer set for {minutes} minutes.");
    }

    private Dictionary<string, object> SystemControl(string action, int value)
    {
        var normalized = NormalizeWords(action);
        switch (normalized)
        {
            case "volume up":
                PressMediaKey(VkVolumeUp, Math.Clamp(value <= 0 ? 2 : value, 1, 20));
                return Logged("system_control", action, true, "Volume increased.");
            case "volume down":
                PressMediaKey(VkVolumeDown, Math.Clamp(value <= 0 ? 2 : value, 1, 20));
                return Logged("system_control", action, true, "Volume decreased.");
            case "mute":
            case "toggle mute":
                PressMediaKey(VkVolumeMute, 1);
                return Logged("system_control", action, true, "Mute toggled.");
            case "bluetooth settings":
                return OpenUrl("ms-settings:bluetooth");
            case "wifi settings":
                return OpenUrl("ms-settings:network-wifi");
            case "display settings":
                return OpenUrl("ms-settings:display");
            case "sound settings":
                return OpenUrl("ms-settings:sound");
            default:
                return Result(false, "Supported direct controls are volume, mute, and opening Wi-Fi, Bluetooth, display, or sound settings.", supported: false);
        }
    }

    private Dictionary<string, object> SystemStatus()
    {
        var power = SystemInformation.PowerStatus;
        var batteryPercent = power.BatteryLifePercent < 0
            ? (int?)null
            : (int)Math.Round(power.BatteryLifePercent * 100);
        return Result(true, "System status captured.", extras: new()
        {
            ["battery_percent"] = batteryPercent,
            ["plugged_in"] = power.PowerLineStatus == PowerLineStatus.Online,
            ["uptime_minutes"] = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalMinutes,
            ["machine_name"] = Environment.MachineName,
            ["user_name"] = Environment.UserName,
            ["os"] = Environment.OSVersion.VersionString,
        });
    }

    private Dictionary<string, object> PlayMedia(string query, string service)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What should I play?", needsClarification: true);

        var cleanQuery = query.Trim();
        var normalizedService = NormalizeWords(service);
        if (string.IsNullOrWhiteSpace(normalizedService))
            normalizedService = "spotify";

        if (normalizedService is "spotify" or "spotify app")
        {
            var encoded = Uri.EscapeDataString(cleanQuery);
            if (TryStart($"spotify:search:{encoded}"))
            {
                return Logged(
                    "play_media",
                    $"Spotify: {cleanQuery}",
                    true,
                    $"Opened Spotify search for {cleanQuery}. Select the result and press Play.",
                    extras: new()
                    {
                        ["service"] = "spotify",
                        ["direct_playback"] = false,
                        ["requires_spotify_oauth"] = true,
                    });
            }

            var result = OpenUrl($"https://open.spotify.com/search/{encoded}");
            result["direct_playback"] = false;
            result["requires_spotify_oauth"] = true;
            result["message"] = $"Opened Spotify search for {cleanQuery}. Select the result and press Play.";
            return result;
        }

        if (normalizedService is "youtube" or "youtube music")
            return OpenUrl($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(cleanQuery)}");

        return Result(false, "Supported media services are Spotify and YouTube.", supported: false);
    }

    private Dictionary<string, object> OpenServicePage(string service, string page, string query)
    {
        var normalizedService = NormalizeWords(service);
        var normalizedPage = NormalizeWords(page);
        var encoded = Uri.EscapeDataString(query?.Trim() ?? "");

        if (normalizedService is "amazon" or "amazon india" or "amazon.in")
        {
            if (normalizedPage is not ("search" or "products"))
                return Result(
                    false,
                    "Amazon cart changes are not supported safely yet. I can open a product search for manual review.",
                    supported: false);
            if (string.IsNullOrWhiteSpace(query))
                return Result(false, "What should I search for on Amazon?", needsClarification: true);
            return OpenUrl($"https://www.amazon.in/s?k={encoded}");
        }

        if (normalizedService is "youtube")
        {
            if (normalizedPage is "liked videos" or "liked playlist" or "likes")
                return OpenUrl("https://www.youtube.com/playlist?list=LL");
            if (normalizedPage is "subscriptions")
                return OpenUrl("https://www.youtube.com/feed/subscriptions");
            if (normalizedPage is "history")
                return OpenUrl("https://www.youtube.com/feed/history");
            return Result(
                false,
                "Bulk unlike and account-changing YouTube actions are not supported safely. I can open Liked Videos, Subscriptions, or History.",
                supported: false);
        }

        if (normalizedService is "spotify")
        {
            if (normalizedPage is "liked songs" or "liked music" or "library")
            {
                if (TryStart("spotify:collection:tracks"))
                    return Logged("open_service_page", "Spotify liked songs", true, "Opened Spotify Liked Songs.");
                return OpenUrl("https://open.spotify.com/collection/tracks");
            }
            return Result(false, "Supported Spotify pages are Liked Songs and media search.", supported: false);
        }

        if (normalizedService is "discord")
        {
            if (normalizedPage is "home" or "app" or "friends")
            {
                if (TryStart("discord://"))
                    return Logged("open_service_page", "Discord", true, "Opened Discord.");
                return OpenUrl("https://discord.com/app");
            }
            return Result(
                false,
                "Discord messaging is not configured in this build. It requires a registered Discord app, OAuth permission, and a stable recipient ID; personal-account UI automation is intentionally blocked.",
                supported: false);
        }

        return Result(false, "That service page is not supported yet.", supported: false);
    }

    private Dictionary<string, object> OpenCamera()
    {
        var resolution = _apps.Resolve("Camera", refresh: true);
        var opened = resolution.Status == AppResolutionStatus.Found
            && _apps.TryLaunch(resolution.Best, out _);
        if (!opened)
            opened = TryStart("microsoft.windows.camera:");
        if (!opened)
        {
            return Logged(
                "open_camera",
                "",
                false,
                "Windows Camera could not be opened. Check that the Camera app is installed and camera access is enabled under Windows Settings > Privacy & security > Camera.",
                supported: false,
                extras: new() { ["settings_uri"] = "ms-settings:privacy-webcam" });
        }

        return Logged(
            "open_camera",
            "Windows Camera",
            true,
            "Opened Windows Camera.",
            extras: new() { ["automatic_capture"] = true });
    }

    public Dictionary<string, object> OpenCameraFromSettings() => OpenCamera();

    private Dictionary<string, object> TakeScreenshot()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return Logged("take_screenshot", "", false, "The screen could not be captured.", supported: false);

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Cursivis",
            "Screenshots");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        bitmap.Save(path, ImageFormat.Png);
        return Logged(
            "take_screenshot",
            path,
            true,
            "Screenshot saved.",
            extras: new() { ["path"] = path });
    }

    private async Task<Dictionary<string, object>> InspectScreenAsync(string request, CancellationToken token)
    {
        try
        {
            var guidance = await _screenGuidance.InspectAsync(request, token);
            return Logged(
                "inspect_screen",
                Limit(request, 180),
                true,
                guidance,
                extras: new()
                {
                    ["guidance"] = guidance,
                    ["capture_persisted"] = false,
                    ["capture_scope"] = "one approved screen image",
                    ["automation_performed"] = false,
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Screen guidance failed");
            return Logged(
                "inspect_screen",
                Limit(request, 180),
                false,
                $"I could not analyze the approved screen capture: {ex.Message}",
                supported: false,
                extras: new() { ["capture_persisted"] = false });
        }
    }

    public Task<Dictionary<string, object>> InspectScreenFromSettingsAsync(
        string request,
        CancellationToken token) =>
        InspectScreenAsync(request, token);

    private async Task<Dictionary<string, object>> TakePhotoAsync(CancellationToken token)
    {
        var result = await _webcam.CapturePhotoAsync(token);
        if (!result.Success)
        {
            LiveModeState.SetUi(LiveModeVoicePhase.Error, "Camera unavailable", result.Message);
            return Logged(
                "take_photo",
                "",
                false,
                result.Message,
                supported: false,
                extras: new()
                {
                    ["automatic_capture"] = true,
                    ["photo_saved"] = false,
                    ["settings_uri"] = "ms-settings:privacy-webcam",
                });
        }

        return Logged(
            "take_photo",
            result.Path,
            true,
            result.Message,
            extras: new()
            {
                ["automatic_capture"] = true,
                ["photo_saved"] = true,
                ["path"] = result.Path,
                ["camera_index"] = result.CameraIndex,
                ["backend"] = result.Backend,
            });
    }

    public Task<Dictionary<string, object>> CapturePhotoFromSettingsAsync(CancellationToken token) =>
        TakePhotoAsync(token);

    private Dictionary<string, object> VirtualDesktopAction(string action)
    {
        var normalized = NormalizeWords(action);
        var key = VirtualDesktopKeyForAction(normalized);
        if (key == 0)
            return Result(false, "Supported virtual desktop actions are create, next, previous, and close.", supported: false);

        SendChord([0x5B, 0x11, key]);
        var message = key switch
        {
            0x44 => "Requested a new virtual desktop.",
            0x27 => "Requested the next virtual desktop.",
            0x25 => "Requested the previous virtual desktop.",
            0x73 => "Requested closing the current virtual desktop.",
            _ => "Virtual desktop shortcut sent.",
        };
        return Logged(
            "virtual_desktop_action",
            normalized,
            true,
            message,
            extras: new() { ["shortcut_sent"] = true, ["verified_state"] = false });
    }

    public static byte VirtualDesktopKeyForAction(string action)
    {
        return NormalizeWords(action) switch
        {
            "create" or "new" or "new desktop" or "create desktop" or "create new desktop" or "create a new desktop" => (byte)0x44,
            "next" or "next desktop" or "switch next" or "switch to next desktop" or "move to next desktop" => (byte)0x27,
            "previous" or "previous desktop" or "switch previous" or "switch to previous desktop" => (byte)0x25,
            "close" or "close desktop" or "close current desktop" => (byte)0x73,
            _ => (byte)0,
        };
    }

    private Dictionary<string, object> WindowsRecordingAction(string action)
    {
        var normalized = NormalizeWords(action);
        var shortcut = WindowsRecordingShortcutForAction(normalized);
        if (shortcut == null)
        {
            return Result(
                false,
                "Supported recording actions are start, stop, toggle, open Game Bar, and select a recording region.",
                supported: false);
        }

        SendChord(shortcut);
        var regionCapture = shortcut.SequenceEqual(new byte[] { 0x5B, 0x10, 0x52 });
        var message = regionCapture
            ? "Opened the Windows screen-region recording picker. Windows controls the selected area and save result."
            : normalized is "open" or "open game bar" or "game bar"
                ? "Opened Xbox Game Bar."
                : "Recording shortcut sent. Xbox Game Bar controls whether recording actually starts or stops.";
        return Logged(
            "windows_recording_action",
            normalized,
            true,
            message,
            extras: new()
            {
                ["shortcut_sent"] = true,
                ["verified_recording_state"] = false,
                ["shortcut"] = regionCapture ? "Win+Shift+R" : shortcut.Length == 2 ? "Win+G" : "Win+Alt+R",
            });
    }

    public static byte[] WindowsRecordingShortcutForAction(string action)
    {
        return NormalizeWords(action) switch
        {
            "start" or "stop" or "toggle" or "record" or "start recording" or "stop recording"
                or "toggle recording" or "start screen recording" or "record my screen"
                or "start windows recording" or "use game bar recording" => [0x5B, 0x12, 0x52],
            "open" or "open game bar" or "game bar" => [0x5B, 0x47],
            "region" or "record region" or "select region" or "record screen region"
                or "snipping tool recording" => [0x5B, 0x10, 0x52],
            _ => null,
        };
    }

    private Dictionary<string, object> RememberAppAlias(string alias, string appName)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(appName))
            return Result(false, "Provide both the phrase to remember and the installed app name.", needsClarification: true);
        var resolution = _apps.Resolve(appName, refresh: true);
        if (resolution.Status != AppResolutionStatus.Found)
        {
            return Result(
                false,
                resolution.Status == AppResolutionStatus.Ambiguous
                    ? $"Several installed apps match {appName}. Ask the user to choose the exact app name first."
                    : $"I could not find an installed app matching {appName}.",
                supported: false,
                needsClarification: resolution.Status == AppResolutionStatus.Ambiguous,
                extras: new()
                {
                    ["candidates"] = resolution.Candidates.Select(candidate => candidate.Name).ToArray(),
                });
        }

        var entry = _mappings.Remember(
            LearnedMappingKind.App,
            alias,
            resolution.Best.LaunchTarget,
            resolution.Best.Name);
        return Logged(
            "remember_app_alias",
            $"{entry.Alias} -> {entry.DisplayName}",
            true,
            $"I will use {entry.DisplayName} when you say {entry.Alias}.",
            extras: new() { ["mapping"] = PublicMapping(entry) });
    }

    private Dictionary<string, object> RememberPathAlias(string alias, string path, string kind)
    {
        var expanded = ExpandPath(path);
        var isFolder = Directory.Exists(expanded);
        var isFile = File.Exists(expanded);
        if (!isFolder && !isFile)
            return Result(false, "That local file or folder does not exist.", supported: false);
        if (isFile && SmartFileSearchService.IsRiskyFile(expanded))
            return Result(false, "Executable, script, installer, shortcut, and registry files cannot be saved as openable aliases.", supported: false);
        var mappingKind = NormalizeWords(kind) == "folder" || isFolder
            ? LearnedMappingKind.Folder
            : LearnedMappingKind.File;
        var entry = _mappings.Remember(mappingKind, alias, expanded, Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar)));
        return Logged(
            "remember_path_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"Remembered {entry.Alias}.",
            extras: new() { ["mapping"] = PublicMapping(entry) });
    }

    private Dictionary<string, object> RememberLinkAlias(string alias, string url)
    {
        var entry = _mappings.Remember(LearnedMappingKind.Link, alias, url, url);
        return Logged(
            "remember_link_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"Remembered the link {entry.Alias}.",
            extras: new() { ["mapping"] = PublicMapping(entry) });
    }

    private Dictionary<string, object> RememberWorkflowAlias(string alias, string workflowName)
    {
        var workflow = FindWorkflow(workflowName);
        if (workflow == null)
            return Result(false, $"Workflow {workflowName} was not found.", supported: false);
        var entry = _mappings.Remember(
            LearnedMappingKind.Workflow,
            alias,
            workflow.Name,
            workflow.Name);
        return Logged(
            "remember_workflow_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"I will use workflow {entry.Target} when you say {entry.Alias}.",
            extras: new() { ["mapping"] = PublicMapping(entry) });
    }

    private Dictionary<string, object> ListLearnedMappings()
    {
        var entries = _mappings.Snapshot();
        return Result(
            true,
            entries.Count == 0 ? "No learned mappings are saved." : $"Loaded {entries.Count} learned mappings.",
            extras: new() { ["mappings"] = entries.Select(PublicMapping).ToArray() });
    }

    private Dictionary<string, object> ForgetLearnedMapping(string idOrAlias, string kind)
    {
        LearnedMappingKind? parsedKind = Enum.TryParse<LearnedMappingKind>(kind, true, out var value)
            ? value
            : null;
        var removed = _mappings.Forget(idOrAlias, parsedKind);
        return Logged(
            "forget_learned_mapping",
            idOrAlias,
            removed,
            removed ? "Learned mapping forgotten." : "No matching learned mapping was found.");
    }

    private Dictionary<string, object> SetBrowserPreference(string browser)
    {
        var normalized = BrowserLauncherService.NormalizeBrowser(browser);
        if (!string.IsNullOrWhiteSpace(normalized) && _browsers.FindBrowserExecutable(normalized) == null)
            return Result(false, $"{browser} is not installed, so the browser preference was not changed.", supported: false);
        _settings.SavePreferredBrowser(normalized);
        return Logged(
            "set_browser_preference",
            normalized,
            true,
            string.IsNullOrWhiteSpace(normalized)
                ? "Future links will use the Windows default browser."
                : $"Future links will use {browser} unless you name another browser.");
    }

    private Dictionary<string, object> OpenGmailDraft(string recipient, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            return Result(false, "Who should I address the email to?", needsClarification: true);
        if (string.IsNullOrWhiteSpace(body))
            return Result(false, "What should the email say?", needsClarification: true);

        var query = new List<string> { "view=cm", "fs=1" };
        query.Add($"to={Uri.EscapeDataString(recipient.Trim())}");
        if (!string.IsNullOrWhiteSpace(subject))
            query.Add($"su={Uri.EscapeDataString(subject.Trim())}");
        query.Add($"body={Uri.EscapeDataString(body.Trim())}");
        ClipboardHelper.SetText(body.Trim());

        var result = OpenUrl("https://mail.google.com/mail/?" + string.Join("&", query));
        result["message"] = "Gmail draft opened. Review it before sending.";
        result["manual_send_required"] = true;
        return result;
    }

    private Dictionary<string, object> PrepareWhatsAppMessage(string message, string contactName, string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Result(false, "What should the WhatsApp message say?", needsClarification: true);

        ClipboardHelper.SetText(message.Trim());
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.Length < 7)
                return Result(false, "That phone number does not look usable.", needsClarification: true);

            var encoded = Uri.EscapeDataString(message.Trim());
            if (!TryStart($"whatsapp://send?phone={digits}&text={encoded}"))
                OpenUrl($"https://wa.me/{digits}?text={encoded}");
            return Logged(
                "prepare_whatsapp_message",
                digits,
                true,
                "WhatsApp message prepared. Review it before sending.",
                extras: new() { ["manual_send_required"] = true });
        }

        if (!TryStart("whatsapp://"))
            OpenUrl("https://web.whatsapp.com/");
        var contact = string.IsNullOrWhiteSpace(contactName) ? "that contact" : contactName.Trim();
        return Logged(
            "prepare_whatsapp_message",
            contact,
            false,
            $"I cannot reliably select {contact} by name yet. I copied the message and opened WhatsApp.",
            supported: false,
            extras: new() { ["contact_lookup_supported"] = false });
    }

    private Dictionary<string, object> CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I copy?", needsClarification: true);
        ClipboardHelper.SetText(text);
        return Logged("copy_text", Limit(text, 120), true, "Copied to clipboard.");
    }

    private Dictionary<string, object> CreateWorkflow(JsonElement args)
    {
        var workflow = _settings.SaveWorkflow(
            GetString(args, "name"),
            GetString(args, "apps"),
            GetString(args, "urls"),
            GetString(args, "folder"));
        return Logged("create_workflow", workflow.Name, true, $"Saved workflow {workflow.Name}.");
    }

    private Dictionary<string, object> RememberRoutine(JsonElement args)
    {
        var trigger = CleanRoutineTrigger(GetString(args, "trigger") ?? GetString(args, "name"));
        if (string.IsNullOrWhiteSpace(trigger))
            return Result(false, "What phrase should trigger the routine?", needsClarification: true);

        var workflow = _settings.SaveWorkflow(
            trigger,
            GetString(args, "apps"),
            GetString(args, "urls"),
            GetString(args, "folder"));
        var mapping = _mappings.Remember(
            LearnedMappingKind.Workflow,
            trigger,
            workflow.Name,
            workflow.Name);
        return Logged(
            "remember_routine",
            trigger,
            true,
            $"Saved the routine. Say {trigger} to run it.",
            extras: new()
            {
                ["workflow"] = new { workflow.Name, workflow.Apps, workflow.Urls, workflow.Folder },
                ["mapping"] = PublicMapping(mapping),
            });
    }

    private Dictionary<string, object> ListWorkflows() =>
        Result(true, "Workflows loaded.", extras: new()
        {
            ["workflows"] = (_settings.Current.LiveModeWorkflows ?? new()).Select(w => new
            {
                w.Name,
                w.Apps,
                w.Urls,
                w.Folder,
            }).ToArray(),
            ["built_in_examples"] = new[] { "coding mode", "study mode", "hackathon mode" },
        });

    private async Task<Dictionary<string, object>> RunWorkflowAsync(string name, CancellationToken token)
    {
        var workflow = FindWorkflow(name);
        if (workflow == null)
            return Result(false, $"Workflow {name} was not found. Create it first or use a built-in example.", supported: false);

        var failures = new List<string>();
        foreach (var app in SplitList(workflow.Apps))
        {
            token.ThrowIfCancellationRequested();
            var result = OpenApp(app);
            if (!Succeeded(result))
                failures.Add(result["message"]?.ToString() ?? $"Could not open {app}.");
            await Task.Delay(180, token);
        }
        foreach (var url in SplitList(workflow.Urls))
        {
            token.ThrowIfCancellationRequested();
            var result = OpenUrl(NormalizeUrl(url));
            if (!Succeeded(result))
                failures.Add(result["message"]?.ToString() ?? $"Could not open {url}.");
            await Task.Delay(180, token);
        }
        if (!string.IsNullOrWhiteSpace(workflow.Folder))
        {
            var result = await OpenFolderAsync(workflow.Folder, token);
            if (!Succeeded(result))
                failures.Add(result["message"]?.ToString() ?? $"Could not open {workflow.Folder}.");
        }

        return Logged(
            "run_workflow",
            workflow.Name,
            failures.Count == 0,
            failures.Count == 0
                ? $"Started {workflow.Name}."
                : $"Started {workflow.Name} with {failures.Count} action failure(s).",
            extras: new() { ["failures"] = failures.ToArray() });
    }

    private Dictionary<string, object> DeleteWorkflow(string name)
    {
        var removed = _settings.DeleteWorkflow(name);
        return Logged("delete_workflow", name, removed, removed ? "Workflow deleted." : "Workflow not found.");
    }

    private Dictionary<string, object> RequestSensitiveAction(string action, string target)
    {
        var normalized = NormalizeWords(action);
        var allowed = new[]
        {
            "close active app", "close app", "shutdown", "restart", "sleep", "lock screen", "disable wifi",
        };
        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return Result(false, "That sensitive action is not supported.", supported: false);
        if (normalized == "close app" && string.IsNullOrWhiteSpace(target))
            return Result(false, "Which app should I close?", needsClarification: true);

        if (_settings.Current.LiveModePermissionMode == LiveModePermissionMode.AutoExecute
            && normalized is "close active app" or "close app")
        {
            var result = WindowAction("close", normalized == "close app" ? target : "");
            result["auto_executed"] = true;
            return result;
        }

        var description = normalized switch
        {
            "close active app" => "Close the currently active app",
            "close app" => $"Close {target}",
            "shutdown" => "Shut down this computer",
            "restart" => "Restart this computer",
            "sleep" => "Put this computer to sleep",
            "lock screen" => "Lock this computer",
            "disable wifi" => "Disable Wi-Fi and disconnect Live Mode",
            _ => normalized,
        };

        lock (_pendingLock)
        {
            _pending = new PendingJarvisAction
            {
                IsSensitive = true,
                Action = normalized,
                Target = target ?? "",
                Description = description,
            };
            ClearPendingConfirmationLocked();
        }
        LiveModeState.SetUi(LiveModeVoicePhase.Thinking, "Confirmation required", description);
        _history.Add("confirmation_requested", description, true, true);
        return Result(true, $"{description}. Ask the user to say confirm or cancel.", confirmationRequired: true,
            extras: new() { ["description"] = description });
    }

    private async Task<Dictionary<string, object>> ConfirmPendingActionAsync(CancellationToken token)
    {
        PendingJarvisAction pending;
        DateTimeOffset? confirmedAt;
        lock (_pendingLock)
        {
            pending = _pending;
            confirmedAt = pending != null
                && string.Equals(_confirmedPendingActionId, pending.Id, StringComparison.Ordinal)
                ? _lastExplicitConfirmationAt
                : pending?.ConfirmedAt;
        }
        if (pending == null)
            return Result(false, "There is no pending action.", supported: false);
        if (confirmedAt == null
            || confirmedAt < pending.RequestedAt
            || DateTimeOffset.UtcNow - confirmedAt > TimeSpan.FromSeconds(30))
        {
            LiveModeLog.Info(
                $"boundary=confirmation.rejected pending=True token_match={string.Equals(_confirmedPendingActionId, pending.Id, StringComparison.Ordinal)}");
            return Result(false, "The user has not confirmed this action yet. Ask them to say confirm or cancel.",
                confirmationRequired: true);
        }

        lock (_pendingLock)
        {
            if (_pending?.Id != pending.Id)
                return Result(false, "The pending action changed before it could be confirmed.", confirmationRequired: true);
            _pending = null;
            ClearPendingConfirmationLocked();
        }
        LiveModeLog.Info("boundary=confirmation.accepted");
        if (pending.IsSensitive)
            return ExecuteSensitiveAction(pending);

        _executingApprovedAction = true;
        try
        {
            return await ExecuteCoreAsync(pending.ToolName, pending.Args, token);
        }
        finally
        {
            _executingApprovedAction = false;
        }
    }

    private Dictionary<string, object> CancelPendingAction()
    {
        lock (_pendingLock)
        {
            _pending = null;
            ClearPendingConfirmationLocked();
        }
        return Result(true, "Pending action cancelled.");
    }

    private void ClearPendingConfirmationLocked()
    {
        _confirmedPendingActionId = "";
        _lastExplicitConfirmationAt = null;
    }

    private Dictionary<string, object> ExecuteSensitiveAction(PendingJarvisAction pending)
    {
        switch (pending.Action)
        {
            case "close active app":
            {
                var handle = GetForegroundWindow();
                if (handle == IntPtr.Zero)
                    return Logged("close_app", "active app", false, "No active app window was found.");
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("close_app", GetWindowTitle(handle), true, "Closed the active app.");
            }
            case "close app":
            {
                var handle = FindWindowByName(pending.Target);
                if (handle == IntPtr.Zero)
                    return Logged("close_app", pending.Target, false, "That app window was not found.");
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("close_app", pending.Target, true, $"Closed {pending.Target}.");
            }
            case "lock screen":
                LockWorkStation();
                return Logged("lock_screen", "", true, "Computer locked.");
            case "shutdown":
                Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/s /t 0", UseShellExecute = false });
                return Logged("shutdown", "", true, "Shutting down.");
            case "restart":
                Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/r /t 0", UseShellExecute = false });
                return Logged("restart", "", true, "Restarting.");
            case "sleep":
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = "powrprof.dll,SetSuspendState 0,1,0",
                    UseShellExecute = false,
                });
                return Logged("sleep", "", true, "Going to sleep.");
            case "disable wifi":
                Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface set interface name=\"Wi-Fi\" admin=disabled",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return Logged("disable_wifi", "", true, "Wi-Fi disable requested.");
            default:
                return Result(false, "Sensitive action is no longer supported.", supported: false);
        }
    }

    private Dictionary<string, object> OpenUrl(string url, string browser = null)
    {
        if (url?.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!string.IsNullOrWhiteSpace(browser))
                return Logged("open_url", url, false, "Windows settings pages cannot be targeted to a web browser.", supported: false);
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return Logged("open_settings_page", url, true, "Opened Windows settings.");
        }
        var learned = _mappings.FindExact(LearnedMappingKind.Link, url);
        if (learned != null)
            url = learned.Target;
        if (!_browsers.TryOpen(url, browser, out var message, out var browserUsed))
        {
            return Logged(
                "open_url",
                $"{url} browser={browser}",
                false,
                message,
                supported: false,
                extras: new()
                {
                    ["requested_browser"] = BrowserLauncherService.NormalizeBrowser(browser),
                    ["fallback_used"] = false,
                });
        }
        return Logged(
            "open_url",
            $"{url} browser={browserUsed}",
            true,
            message,
            extras: new() { ["browser"] = browserUsed });
    }

    private LiveModeWorkflowSettings FindWorkflow(string name)
    {
        var learned = _mappings.FindExact(LearnedMappingKind.Workflow, name);
        learned ??= _mappings.Search(name, LearnedMappingKind.Workflow, 2)
            .Select(entry => new
            {
                Entry = entry,
                Score = Math.Max(
                    FuzzyMatcher.Score(name, entry.Alias),
                    FuzzyMatcher.Score(name, entry.DisplayName)),
            })
            .Where(item => item.Score >= 0.78)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Entry)
            .FirstOrDefault();
        if (learned != null)
            name = learned.Target;
        var normalized = NormalizeWords(name);
        var saved = _settings.Current.LiveModeWorkflows?.FirstOrDefault(w =>
            string.Equals(NormalizeWords(w.Name), normalized, StringComparison.OrdinalIgnoreCase));
        if (saved != null)
            return saved;
        var ranked = _settings.Current.LiveModeWorkflows?
            .Select(workflow => (Workflow: workflow, Score: FuzzyMatcher.Score(normalized, workflow.Name)))
            .Where(item => item.Score >= 0.72)
            .OrderByDescending(item => item.Score)
            .Take(2)
            .ToArray() ?? Array.Empty<(LiveModeWorkflowSettings Workflow, double Score)>();
        if (ranked.Length > 0)
        {
            var top = ranked[0];
            var secondScore = ranked.Length > 1 ? ranked[1].Score : 0;
            if (top.Score >= 0.9 || top.Score - secondScore >= 0.08)
                return top.Workflow;
        }
        return normalized switch
        {
            "coding mode" => new LiveModeWorkflowSettings
            {
                Name = "Coding mode",
                Apps = "Visual Studio Code, Windows Terminal",
                Urls = "https://github.com/, https://chatgpt.com/",
            },
            "study mode" => new LiveModeWorkflowSettings
            {
                Name = "Study mode",
                Apps = "Notepad",
                Urls = "https://www.youtube.com/results?search_query=focus+music",
            },
            "hackathon mode" => new LiveModeWorkflowSettings
            {
                Name = "Hackathon mode",
                Apps = "Visual Studio Code, Windows Terminal",
                Urls = "https://devpost.com/, https://github.com/, https://vercel.com/, https://chatgpt.com/",
            },
            _ => null,
        };
    }

    private Dictionary<string, object> Logged(
        string action,
        string detail,
        bool success,
        string message,
        bool supported = true,
        Dictionary<string, object> extras = null)
    {
        _history.Add(action, detail, success);
        return Result(success, message, supported: supported, extras: extras);
    }

    private static Dictionary<string, object> Result(
        bool ok,
        string message,
        bool supported = true,
        bool needsClarification = false,
        bool confirmationRequired = false,
        Dictionary<string, object> extras = null)
    {
        var result = extras ?? new Dictionary<string, object>();
        result["ok"] = ok;
        result["supported"] = supported;
        result["needs_clarification"] = needsClarification;
        result["confirmation_required"] = confirmationRequired;
        result["message"] = message ?? "";
        return result;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString();
    }

    private static int GetInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out number) ? number : 0;
    }

    private static string NormalizeWords(string text) =>
        string.Join(" ", (text ?? "").Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CleanRoutineTrigger(string value)
    {
        var trigger = NormalizePhraseText(value);
        foreach (var prefix in new[]
        {
            "whenever i say ", "when i say ", "remember that whenever i say ",
            "remember whenever i say ", "remember this as ",
        })
        {
            if (trigger.StartsWith(prefix, StringComparison.Ordinal))
            {
                trigger = trigger[prefix.Length..].Trim();
                break;
            }
        }
        return Limit(trigger, 48);
    }

    private static string NormalizePhraseText(string text)
    {
        var wordsOnly = new string((text ?? "").ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == '\''
                ? character
                : ' ')
            .ToArray());
        return NormalizeWords(wordsOnly);
    }

    private static bool ContainsPhrase(string value, params string[] phrases)
    {
        var padded = $" {value} ";
        return phrases.Any(phrase =>
            padded.Contains($" {NormalizePhraseText(phrase)} ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Succeeded(Dictionary<string, object> result) =>
        result.TryGetValue("ok", out var ok) && ok is bool success && success;

    private static object PublicMapping(LearnedMappingEntry entry) => new
    {
        entry.Id,
        kind = entry.Kind.ToString(),
        entry.Alias,
        entry.DisplayName,
        target = entry.Kind == LearnedMappingKind.App ? entry.DisplayName : entry.Target,
        entry.UpdatedAt,
    };

    private static bool TryStart(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
    }

    private static List<WindowInfo> EnumerateWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            windows.Add(new WindowInfo(handle, title, GetProcessName(handle)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindWindowByName(string name)
    {
        var normalized = NormalizeWords(name);
        return EnumerateWindows()
            .FirstOrDefault(w => w.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            ?.Handle ?? IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return "";
        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return ""; }
    }

    private static bool IsBrowserProcess(string processName) =>
        processName is not null
        && new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" }
            .Contains(processName, StringComparer.OrdinalIgnoreCase);

    private static void SendKeysOnUiThread(string keys)
    {
        var form = Application.OpenForms.Cast<Form>().FirstOrDefault();
        if (form?.InvokeRequired == true)
            form.Invoke(() => SendKeys.SendWait(keys));
        else
            SendKeys.SendWait(keys);
    }

    private static void PressMediaKey(byte key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    private static void SendChord(byte[] keys)
    {
        foreach (var key in keys)
            keybd_event(key, 0, 0, UIntPtr.Zero);
        for (var index = keys.Length - 1; index >= 0; index--)
            keybd_event(keys[index], 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private List<JarvisTodo> LoadTodos()
    {
        try
        {
            if (!File.Exists(_todosPath))
                return new List<JarvisTodo>();
            return JsonSerializer.Deserialize<List<JarvisTodo>>(File.ReadAllText(_todosPath)) ?? new();
        }
        catch { return new List<JarvisTodo>(); }
    }

    private void SaveTodos(List<JarvisTodo> todos)
    {
        Directory.CreateDirectory(_settings.DataDir);
        File.WriteAllText(_todosPath, JsonSerializer.Serialize(todos, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<string> SplitList(string value) =>
        (value ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeUrl(string value)
    {
        var url = value?.Trim() ?? "";
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        return url;
    }

    private static string Limit(string value, int maxLength)
    {
        var text = value ?? "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_timers)
        {
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
        }
        _screenGuidance.Dispose();
    }

    private sealed class PendingJarvisAction
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string ToolName { get; init; } = "";
        public JsonElement Args { get; init; }
        public bool IsSensitive { get; init; }
        public string Action { get; init; } = "";
        public string Target { get; init; } = "";
        public string Description { get; init; } = "";
        public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ConfirmedAt { get; set; }
    }

    private sealed class JarvisTodo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = "";
        public bool Completed { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    }

    private sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}

