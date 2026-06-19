#nullable disable

namespace Cursivis.Companion.LiveMode;

public static class LiveModePermissionPolicy
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "get_desktop_context", "get_browser_context", "get_clipboard_text", "get_selected_text", "search_files",
        "list_todos", "list_workflows", "system_status", "list_learned_mappings",
    };

    private static readonly HashSet<string> SideEffectingTools = new(StringComparer.Ordinal)
    {
        "open_app", "open_url", "open_folder", "open_path", "window_action", "browser_action",
        "web_search", "replace_selected_text", "type_text", "press_key", "save_note", "add_todo",
        "complete_todo", "set_timer", "system_control", "play_media", "open_service_page",
        "open_camera", "take_photo", "take_screenshot", "inspect_screen", "virtual_desktop_action",
        "windows_recording_action",
        "open_gmail_draft", "prepare_whatsapp_message", "copy_text", "create_workflow",
        "remember_routine",
        "run_workflow", "delete_workflow", "remember_app_alias", "remember_path_alias",
        "remember_link_alias", "remember_workflow_alias", "forget_learned_mapping", "set_browser_preference",
    };

    public static bool RequiresConfirmation(
        string toolName,
        string action,
        string appName,
        LiveModePermissionMode mode,
        bool executingApprovedAction = false)
    {
        toolName = (toolName ?? "").Trim();
        var normalizedAction = FuzzyMatcher.Normalize(action);
        if (executingApprovedAction
            || toolName is "request_sensitive_action" or "confirm_sensitive_action" or "cancel_sensitive_action")
            return false;
        if (ReadOnlyTools.Contains(toolName))
            return false;
        if (toolName == "window_action" && normalizedAction is "list" or "list windows")
            return false;
        if (!SideEffectingTools.Contains(toolName))
            return false;
        if (toolName == "virtual_desktop_action"
            && normalizedAction is "close" or "close desktop" or "close current desktop")
            return true;
        return mode == LiveModePermissionMode.AlwaysAsk;
    }
}

