using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.Models;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Cursivis.Companion.Services;

public sealed class ActiveBrowserAutomationService
{
    private static readonly Regex UrlRegex = new(@"^(https?:\/\/|about:|chrome:\/\/|edge:\/\/|brave:\/\/|moz-extension:|chrome-extension:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ClipboardService _clipboardService;

    public ActiveBrowserAutomationService(ClipboardService clipboardService)
    {
        _clipboardService = clipboardService;
    }

    public BrowserPageContext? TryBuildPageContext(IntPtr windowHandle)
    {
        var root = TryGetRoot(windowHandle);
        if (root is null)
        {
            return null;
        }

        var elements = GetDescendants(root, maxCount: 700);
        var visibleBits = new List<string>();
        var interactiveElements = new List<BrowserElementSummary>();
        var seenVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            if (element.Current.IsOffscreen)
            {
                continue;
            }

            var name = Normalize(element.Current.Name);
            var type = element.Current.ControlType;
            if (string.IsNullOrWhiteSpace(name) && type != ControlType.Edit && type != ControlType.Document)
            {
                continue;
            }

            if (IsInteractive(type))
            {
                interactiveElements.Add(new BrowserElementSummary
                {
                    Role = ControlTypeToRole(type),
                    Label = name,
                    NameAttribute = Normalize(element.Current.AutomationId),
                    Type = Normalize(type.ProgrammaticName?.Split('.').LastOrDefault()),
                    Options = []
                });
            }

            if (ShouldContributeVisibleText(type) && !string.IsNullOrWhiteSpace(name) && seenVisible.Add(name))
            {
                visibleBits.Add(name);
            }

            if (interactiveElements.Count >= 140 && visibleBits.Count >= 220)
            {
                break;
            }
        }

        return new BrowserPageContext
        {
            Url = TryGetAddressBarValue(root) ?? string.Empty,
            Title = Normalize(root.Current.Name),
            VisibleText = Clip(string.Join(" ", visibleBits), 4000),
            InteractiveElements = interactiveElements
        };
    }

    public async Task<BrowserExecutionResponse> ExecutePlanAsync(
        IntPtr windowHandle,
        BrowserActionPlanResponse plan,
        CancellationToken cancellationToken)
    {
        var root = TryGetRoot(windowHandle);
        if (root is null)
        {
            return new BrowserExecutionResponse
            {
                Ok = false,
                Success = false,
                Message = "Could not access the active browser window."
            };
        }

        var logs = new List<string>();
        var executedSteps = 0;

        try
        {
            NativeMethods.BringToFront(windowHandle);
            await Task.Delay(60, cancellationToken);

            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteStepAsync(root, windowHandle, step, logs, cancellationToken);
                executedSteps += 1;
            }

            return new BrowserExecutionResponse
            {
                Ok = true,
                Success = true,
                ExecutedSteps = executedSteps,
                Message = executedSteps > 0
                    ? "Applied in the active browser session."
                    : "No browser actions were executed.",
                Logs = logs,
                PageContext = TryBuildPageContext(windowHandle)
            };
        }
        catch (Exception ex)
        {
            return new BrowserExecutionResponse
            {
                Ok = false,
                Success = false,
                ExecutedSteps = executedSteps,
                Message = "Active browser execution failed.",
                Details = ex.Message,
                Logs = logs,
                PageContext = TryBuildPageContext(windowHandle)
            };
        }
    }

    private async Task ExecuteStepAsync(
        AutomationElement root,
        IntPtr windowHandle,
        BrowserActionStep step,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        logs.Add(step.Tool);

        switch (step.Tool.Trim().ToLowerInvariant())
        {
            case "navigate":
                if (string.IsNullOrWhiteSpace(step.Url))
                {
                    throw new InvalidOperationException("navigate step requires url.");
                }

                await NavigateAsync(root, windowHandle, step.Url, cancellationToken);
                return;
            case "open_new_tab":
                await OpenNewTabAsync(windowHandle, step.Url, cancellationToken);
                return;
            case "switch_tab":
                NativeMethods.BringToFront(windowHandle);
                await Task.Delay(35, cancellationToken);
                NativeMethods.SendCtrlTab();
                await Task.Delay(250, cancellationToken);
                return;
            case "click_role":
                await ClickByRoleAsync(root, step.Role, step.Name ?? step.Text, cancellationToken);
                return;
            case "click_text":
                await ClickByTextAsync(root, step.Text ?? step.Name, cancellationToken);
                return;
            case "fill_label":
                await FillFieldAsync(root, step.Label ?? step.Name, step.Text, cancellationToken);
                return;
            case "fill_name":
                await FillFieldAsync(root, step.NameAttribute ?? step.Name, step.Text, cancellationToken);
                return;
            case "fill_placeholder":
                await FillFieldAsync(root, step.Placeholder ?? step.Label ?? step.Name, step.Text, cancellationToken);
                return;
            case "type_active":
                await PasteToFocusedElementAsync(windowHandle, step.Text ?? string.Empty, overwrite: false, cancellationToken);
                return;
            case "check_radio":
                await ToggleChoiceAsync(root, ControlType.RadioButton, step.Question, step.Option ?? step.Label ?? step.Name, cancellationToken);
                return;
            case "check_checkbox":
                await ToggleChoiceAsync(root, ControlType.CheckBox, step.Question, step.Option ?? step.Label ?? step.Name, cancellationToken);
                return;
            case "apply_answer_key":
                await ApplyAnswerKeyAsync(windowHandle, step, logs, cancellationToken);
                return;
            case "press_key":
                NativeMethods.BringToFront(windowHandle);
                await Task.Delay(45, cancellationToken);
                NativeMethods.SendKeyChord(step.Key ?? "Enter");
                return;
            case "scroll":
                NativeMethods.BringToFront(windowHandle);
                await Task.Delay(35, cancellationToken);
                NativeMethods.Scroll(step.Text ?? step.Name ?? "down");
                await Task.Delay(150, cancellationToken);
                return;
            case "extract_dom":
                _ = TryBuildPageContext(windowHandle);
                return;
            case "wait_for_text":
                await WaitForTextAsync(windowHandle, step.Text ?? step.Name, cancellationToken);
                return;
            case "wait_ms":
                await Task.Delay(step.WaitMs ?? 250, cancellationToken);
                return;
            case "select_option":
                await FillFieldAsync(root, step.Label ?? step.Name, step.Option ?? step.Text, cancellationToken);
                return;
            default:
                throw new InvalidOperationException($"Unsupported active browser tool: {step.Tool}");
        }
    }

    private async Task NavigateAsync(AutomationElement root, IntPtr windowHandle, string url, CancellationToken cancellationToken)
    {
        var addressBar = FindAddressBar(root);
        if (addressBar is not null && TrySetValue(addressBar, url))
        {
            NativeMethods.BringToFront(windowHandle);
            await Task.Delay(35, cancellationToken);
            NativeMethods.SendEnter();
            await Task.Delay(850, cancellationToken);
            return;
        }

        var backup = await _clipboardService.CaptureAsync();
        try
        {
            await _clipboardService.SetTextAsync(url);
            NativeMethods.BringToFront(windowHandle);
            await Task.Delay(50, cancellationToken);
            NativeMethods.SendCtrlL();
            await Task.Delay(40, cancellationToken);
            NativeMethods.SendCtrlV();
            await Task.Delay(40, cancellationToken);
            NativeMethods.SendEnter();
            await Task.Delay(900, cancellationToken);
        }
        finally
        {
            await _clipboardService.RestoreAsync(backup);
        }
    }

    private async Task OpenNewTabAsync(IntPtr windowHandle, string? url, CancellationToken cancellationToken)
    {
        NativeMethods.BringToFront(windowHandle);
        await Task.Delay(35, cancellationToken);
        NativeMethods.SendCtrlT();
        await Task.Delay(120, cancellationToken);

        if (!string.IsNullOrWhiteSpace(url))
        {
            var backup = await _clipboardService.CaptureAsync();
            try
            {
                await _clipboardService.SetTextAsync(url);
                NativeMethods.SendCtrlL();
                await Task.Delay(40, cancellationToken);
                NativeMethods.SendCtrlV();
                await Task.Delay(40, cancellationToken);
                NativeMethods.SendEnter();
                await Task.Delay(900, cancellationToken);
            }
            finally
            {
                await _clipboardService.RestoreAsync(backup);
            }
        }
    }

    private async Task ClickByRoleAsync(AutomationElement root, string? role, string? name, CancellationToken cancellationToken)
    {
        var controlType = RoleToControlType(role);
        foreach (var candidateName in ExpandRoleQueries(role, name))
        {
            var element = FindBestMatch(root, candidateName, controlType);
            if (element is null)
            {
                continue;
            }

            await ActivateElementAsync(element, cancellationToken);
            return;
        }

        if (string.Equals(Normalize(role), "button", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = FindLikelyNavigationElement(root, name);
            if (fallback is not null)
            {
                await ActivateElementAsync(fallback, cancellationToken);
                return;
            }
        }

        throw new InvalidOperationException($"Could not find {role ?? "element"} '{name}'.");
    }

    private async Task ClickByTextAsync(AutomationElement root, string? text, CancellationToken cancellationToken)
    {
        var element = FindBestMatch(root, text, null);
        if (element is null)
        {
            throw new InvalidOperationException($"Could not find text '{text}'.");
        }

        await ActivateElementAsync(PromoteToClickableAncestor(element) ?? element, cancellationToken);
    }

    private async Task FillFieldAsync(AutomationElement root, string? label, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new InvalidOperationException("Field label is required.");
        }

        foreach (var candidateLabel in ExpandFieldQueries(label))
        {
            var field = FindEditableField(root, candidateLabel);
            if (field is null)
            {
                continue;
            }

            if (TrySetValue(field, text ?? string.Empty))
            {
                await Task.Delay(60, cancellationToken);
                return;
            }

            field.SetFocus();
            await Task.Delay(35, cancellationToken);
            await PasteToFocusedElementAsync(IntPtr.Zero, text ?? string.Empty, overwrite: true, cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Could not find field '{label}'.");
    }

    private async Task<AutomationElement> ToggleChoiceAsync(
        AutomationElement root,
        ControlType controlType,
        string? question,
        string? option,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            throw new InvalidOperationException("Choice option is required.");
        }

        var candidates = GetDescendants(root, maxCount: 900)
            .Where(element => element.Current.ControlType == controlType)
            .Where(element => Matches(element.Current.Name, option))
            .ToList();

        if (!string.IsNullOrWhiteSpace(question))
        {
            candidates = candidates
                .OrderByDescending(element => AncestorContext(element).Contains(question, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var match = candidates.FirstOrDefault();
        if (match is null)
        {
            match = FindChoiceLikeElement(root, question, option);
        }

        if (match is null)
        {
            throw new InvalidOperationException($"Could not find option '{option}'.");
        }

        await ActivateElementAsync(match, cancellationToken);
        return match;
    }

    private async Task ApplyAnswerKeyAsync(
        IntPtr windowHandle,
        BrowserActionStep step,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        var pending = (step.Answers ?? [])
            .Where(answer => !string.IsNullOrWhiteSpace(answer.Option))
            .Select(answer => new BrowserAnswerKeyEntry
            {
                Question = answer.Question,
                Option = answer.Option
            })
            .ToList();

        if (pending.Count == 0)
        {
            throw new InvalidOperationException("apply_answer_key requires at least one answer.");
        }

        var applied = 0;
        var maxPages = Math.Min(Math.Max(pending.Count + 1, 2), 12);
        for (var pageIndex = 0; pageIndex < maxPages && pending.Count > 0; pageIndex += 1)
        {
            var root = TryGetRoot(windowHandle);
            if (root is null)
            {
                throw new InvalidOperationException("Could not access the active browser window.");
            }

            var appliedThisPage = 0;
            var pagePass = 0;
            var madeProgress = true;
            while (pagePass < 3 && madeProgress && pending.Count > 0)
            {
                madeProgress = false;
                for (var index = pending.Count - 1; index >= 0; index -= 1)
                {
                    var answer = pending[index];
                    if (!await TryToggleAnswerAsync(root, windowHandle, answer, cancellationToken))
                    {
                        continue;
                    }

                    pending.RemoveAt(index);
                    applied += 1;
                    appliedThisPage += 1;
                    madeProgress = true;
                }

                pagePass += 1;
                if (madeProgress)
                {
                    await Task.Delay(85, cancellationToken);
                    root = TryGetRoot(windowHandle) ?? root;
                }
            }

            logs.Add($"apply_answer_key:applied={appliedThisPage}");

            if (pending.Count == 0)
            {
                return;
            }

            if (step.AdvancePages != true)
            {
                break;
            }

            root = TryGetRoot(windowHandle);
            var nextElement = root is null
                ? null
                : FindLikelyNavigationElement(root, "Next") ??
                  FindLikelyNavigationElement(root, "Continue") ??
                  FindLikelyNavigationElement(root, "Done") ??
                  FindLikelyNavigationElement(root, "Submit");
            if (nextElement is null)
            {
                break;
            }

            await ActivateElementAsync(nextElement, cancellationToken);
            await Task.Delay(appliedThisPage > 0 ? 820 : 620, cancellationToken);
        }

        if (applied == 0)
        {
            throw new InvalidOperationException("Could not match the answer key to responsive quiz options in the active browser session.");
        }

        if (pending.Count > 0)
        {
            throw new InvalidOperationException($"Applied {applied} answer(s), but {pending.Count} question(s) could not be matched yet.");
        }
    }

    private async Task<bool> TryToggleAnswerAsync(
        AutomationElement root,
        IntPtr windowHandle,
        BrowserAnswerKeyEntry answer,
        CancellationToken cancellationToken)
    {
        if (await TryApplyChoiceAsync(root, ControlType.RadioButton, answer.Question, answer.Option, cancellationToken))
        {
            return true;
        }

        if (await TryApplyChoiceAsync(root, ControlType.CheckBox, answer.Question, answer.Option, cancellationToken))
        {
            return true;
        }

        return await TryFillAnswerAsync(root, windowHandle, answer, cancellationToken);
    }

    private async Task<bool> TryApplyChoiceAsync(
        AutomationElement root,
        ControlType controlType,
        string? question,
        string? option,
        CancellationToken cancellationToken)
    {
        AutomationElement match;
        try
        {
            match = await ToggleChoiceAsync(root, controlType, question, option, cancellationToken);
        }
        catch
        {
            return false;
        }

        if (IsChoiceSelected(match))
        {
            return true;
        }

        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            await ActivateElementAsync(match, cancellationToken);
            await Task.Delay(80 + (attempt * 50), cancellationToken);
            if (IsChoiceSelected(match))
            {
                return true;
            }
        }

        return IsChoiceSelected(match);
    }

    private async Task<bool> TryFillAnswerAsync(
        AutomationElement root,
        IntPtr windowHandle,
        BrowserAnswerKeyEntry answer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(answer.Question) || string.IsNullOrWhiteSpace(answer.Option))
        {
            return false;
        }

        AutomationElement? field = null;
        foreach (var candidateLabel in ExpandFieldQueries(answer.Question))
        {
            field = FindEditableField(root, candidateLabel);
            if (field is not null)
            {
                break;
            }
        }

        if (field is null)
        {
            return false;
        }

        if (TrySetValue(field, answer.Option))
        {
            await Task.Delay(70, cancellationToken);
            if (string.Equals(TryReadElementValue(field), Normalize(answer.Option), StringComparison.Ordinal))
            {
                return true;
            }
        }

        field.SetFocus();
        await Task.Delay(35, cancellationToken);
        await PasteToFocusedElementAsync(windowHandle, answer.Option, overwrite: true, cancellationToken);
        return string.Equals(TryReadElementValue(field), Normalize(answer.Option), StringComparison.Ordinal);
    }

    private async Task WaitForTextAsync(IntPtr windowHandle, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = TryBuildPageContext(windowHandle);
            if (context is not null && context.VisibleText.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(140, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for text '{text}'.");
    }

    private async Task ActivateElementAsync(AutomationElement element, CancellationToken cancellationToken)
    {
        element = PromoteToClickableAncestor(element) ?? element;

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) && invokePattern is InvokePattern invoke)
        {
            invoke.Invoke();
            await Task.Delay(120, cancellationToken);
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) && selectionPattern is SelectionItemPattern selection)
        {
            selection.Select();
            await Task.Delay(120, cancellationToken);
            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) && togglePattern is TogglePattern toggle)
        {
            toggle.Toggle();
            await Task.Delay(120, cancellationToken);
            return;
        }

        if (element.TryGetClickablePoint(out var point))
        {
            NativeMethods.LeftClickAt((int)point.X, (int)point.Y);
            await Task.Delay(140, cancellationToken);
            return;
        }

        element.SetFocus();
        await Task.Delay(60, cancellationToken);
        NativeMethods.SendEnter();
        await Task.Delay(100, cancellationToken);
    }

    private static bool IsChoiceSelected(AutomationElement element)
    {
        if (TryIsElementSelected(element))
        {
            return true;
        }

        foreach (var descendant in GetDescendants(element, maxCount: 24))
        {
            if (TryIsElementSelected(descendant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryIsElementSelected(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) &&
                selectionPattern is SelectionItemPattern selectionItem &&
                selectionItem.Current.IsSelected)
            {
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) &&
                togglePattern is TogglePattern toggle &&
                toggle.Current.ToggleState == ToggleState.On)
            {
                return true;
            }
        }
        catch
        {
            // Ignore inaccessible patterns and continue verifying elsewhere.
        }

        return false;
    }

    private async Task PasteToFocusedElementAsync(IntPtr windowHandle, string text, bool overwrite, CancellationToken cancellationToken)
    {
        var backup = await _clipboardService.CaptureAsync();
        try
        {
            await _clipboardService.SetTextAsync(text);
            if (windowHandle != IntPtr.Zero)
            {
                NativeMethods.BringToFront(windowHandle);
                await Task.Delay(35, cancellationToken);
            }

            if (overwrite)
            {
                NativeMethods.SendCtrlA();
                await Task.Delay(25, cancellationToken);
            }

            NativeMethods.SendCtrlV();
            await Task.Delay(60, cancellationToken);
        }
        finally
        {
            await _clipboardService.RestoreAsync(backup);
        }
    }

    private static AutomationElement? TryGetRoot(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return AutomationElement.FromHandle(windowHandle);
        }
        catch
        {
            return null;
        }
    }

    private static List<AutomationElement> GetDescendants(AutomationElement root, int maxCount)
    {
        try
        {
            return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Take(maxCount)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static AutomationElement? FindAddressBar(AutomationElement root)
    {
        return GetDescendants(root, maxCount: 150)
            .Where(element => element.Current.ControlType == ControlType.Edit || element.Current.ControlType == ControlType.ComboBox)
            .FirstOrDefault(element =>
            {
                var label = Normalize(element.Current.Name);
                var value = TryReadElementValue(element);
                return label.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                       label.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                       UrlRegex.IsMatch(value ?? string.Empty);
            });
    }

    private static string? TryGetAddressBarValue(AutomationElement root)
    {
        return FindAddressBar(root) is { } addressBar
            ? TryReadElementValue(addressBar)
            : null;
    }

    private static AutomationElement? FindEditableField(AutomationElement root, string label)
    {
        var candidates = GetDescendants(root, maxCount: 900)
            .Where(element => element.Current.ControlType == ControlType.Edit ||
                              element.Current.ControlType == ControlType.Document ||
                              element.Current.ControlType == ControlType.ComboBox)
            .OrderByDescending(element => ScoreElement(element, label, editableOnly: true))
            .ToList();

        return candidates.FirstOrDefault(element => ScoreElement(element, label, editableOnly: true) > 0);
    }

    private static AutomationElement? FindBestMatch(AutomationElement root, string? query, ControlType? controlType)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var candidates = GetDescendants(root, maxCount: 900)
            .Where(element => controlType is null || element.Current.ControlType == controlType)
            .OrderByDescending(element => ScoreElement(element, query, editableOnly: false))
            .ToList();

        return candidates.FirstOrDefault(element => ScoreElement(element, query, editableOnly: false) > 0);
    }

    private static AutomationElement? FindChoiceLikeElement(AutomationElement root, string? question, string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return null;
        }

        var normalizedQuestion = Normalize(question);
        return GetDescendants(root, maxCount: 1200)
            .Select(element => new
            {
                Element = PromoteToClickableAncestor(element),
                Score = ScoreChoiceElement(element, normalizedQuestion, option)
            })
            .Where(entry => entry.Element is not null && entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Element)
            .FirstOrDefault();
    }

    private static int ScoreChoiceElement(AutomationElement element, string normalizedQuestion, string? option)
    {
        var optionText = Normalize(option);
        if (string.IsNullOrWhiteSpace(optionText))
        {
            return 0;
        }

        var candidate = Normalize(element.Current.Name);
        var ancestor = AncestorContext(element);
        if (!Matches(candidate, optionText) && !Matches(ancestor, optionText))
        {
            return 0;
        }

        var score = 0;
        if (Matches(candidate, optionText))
        {
            score += 30;
        }

        if (Matches(ancestor, optionText))
        {
            score += 14;
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuestion) && Matches(ancestor, normalizedQuestion))
        {
            score += 18;
        }

        if (CanActivateElement(element))
        {
            score += 12;
        }

        return score;
    }

    private static AutomationElement? FindLikelyNavigationElement(AutomationElement root, string? intentName)
    {
        var normalizedIntent = Normalize(intentName);
        return GetDescendants(root, maxCount: 900)
            .Select(element =>
            {
                var clickable = PromoteToClickableAncestor(element);
                return new
                {
                    Element = clickable,
                    Score = clickable is null ? 0 : ScoreNavigationElement(clickable, normalizedIntent)
                };
            })
            .Where(entry => entry.Element is not null && entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Element)
            .FirstOrDefault();
    }

    private static int ScoreNavigationElement(AutomationElement element, string normalizedIntent)
    {
        var name = Normalize(element.Current.Name);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(normalizedIntent) && Matches(name, normalizedIntent))
        {
            score += 50;
        }

        if (Matches(name, "next") || Matches(name, "continue") || Matches(name, "submit") || Matches(name, "finish") || Matches(name, "done"))
        {
            score += 35;
        }

        if (!CanActivateElement(element))
        {
            return score;
        }

        score += 10;
        var rect = element.Current.BoundingRectangle;
        if (!rect.IsEmpty)
        {
            if (rect.Left > 900)
            {
                score += 10;
            }

            if (rect.Top > 400)
            {
                score += 10;
            }
        }

        return score;
    }

    private static AutomationElement? PromoteToClickableAncestor(AutomationElement element)
    {
        AutomationElement? current = element;
        for (var depth = 0; current is not null && depth < 5; depth += 1)
        {
            if (CanActivateElement(current))
            {
                return current;
            }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool CanActivateElement(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                return true;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
            {
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out _))
            {
                return true;
            }

            return element.TryGetClickablePoint(out _);
        }
        catch
        {
            return false;
        }
    }

    private static int ScoreElement(AutomationElement element, string query, bool editableOnly)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var normalizedQuery = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0;
        }

        var type = element.Current.ControlType;
        if (editableOnly && type != ControlType.Edit && type != ControlType.Document && type != ControlType.ComboBox)
        {
            return 0;
        }

        var score = 0;
        foreach (var candidate in new[]
                 {
                     Normalize(element.Current.Name),
                     Normalize(element.Current.HelpText),
                     Normalize(element.Current.AutomationId),
                     Normalize(TryReadElementValue(element)),
                     AncestorContext(element)
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (string.Equals(candidate, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 120);
            }
            else if (candidate.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 80);
            }
            else if (normalizedQuery.Contains(candidate, StringComparison.OrdinalIgnoreCase) && candidate.Length > 3)
            {
                score = Math.Max(score, 40);
            }
        }

        if (type == ControlType.Edit || type == ControlType.Document)
        {
            score += 8;
        }

        if (element.Current.IsOffscreen)
        {
            score -= 12;
        }

        return score;
    }

    private static bool TrySetValue(AutomationElement element, string text)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) && valuePattern is ValuePattern value)
            {
                value.SetValue(text);
                return true;
            }
        }
        catch
        {
            // Fall through to keyboard paste.
        }

        return false;
    }

    private static string? TryReadElementValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) && valuePattern is ValuePattern value)
            {
                return Normalize(value.Current.Value);
            }
        }
        catch
        {
            // Ignore inaccessible patterns.
        }

        return Normalize(element.Current.Name);
    }

    private static string AncestorContext(AutomationElement element)
    {
        try
        {
            var parts = new List<string>();
            var walker = TreeWalker.ControlViewWalker;
            var current = walker.GetParent(element);
            var depth = 0;
            while (current is not null && depth < 4)
            {
                var name = Normalize(current.Current.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    parts.Add(name);
                }

                current = walker.GetParent(current);
                depth += 1;
            }

            return string.Join(" ", parts);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Matches(string? candidate, string? query)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedQuery = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return false;
        }

        return normalizedCandidate.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
               normalizedQuery.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldContributeVisibleText(ControlType controlType)
    {
        return controlType == ControlType.Text ||
               controlType == ControlType.Document ||
               controlType == ControlType.Button ||
               controlType == ControlType.Hyperlink ||
               controlType == ControlType.RadioButton ||
               controlType == ControlType.CheckBox ||
               controlType == ControlType.Edit ||
               controlType == ControlType.ListItem;
    }

    private static bool IsInteractive(ControlType controlType)
    {
        return controlType == ControlType.Button ||
               controlType == ControlType.Hyperlink ||
               controlType == ControlType.Edit ||
               controlType == ControlType.Document ||
               controlType == ControlType.RadioButton ||
               controlType == ControlType.CheckBox ||
               controlType == ControlType.ComboBox ||
               controlType == ControlType.ListItem;
    }

    private static ControlType? RoleToControlType(string? role)
    {
        return Normalize(role) switch
        {
            "button" => ControlType.Button,
            "link" => ControlType.Hyperlink,
            "textbox" => ControlType.Edit,
            "radio" => ControlType.RadioButton,
            "checkbox" => ControlType.CheckBox,
            "combobox" => ControlType.ComboBox,
            _ => null
        };
    }

    private static string ControlTypeToRole(ControlType controlType)
    {
        if (controlType == ControlType.Button) return "button";
        if (controlType == ControlType.Hyperlink) return "link";
        if (controlType == ControlType.Edit || controlType == ControlType.Document) return "textbox";
        if (controlType == ControlType.RadioButton) return "radio";
        if (controlType == ControlType.CheckBox) return "checkbox";
        if (controlType == ControlType.ComboBox) return "combobox";
        return Normalize(controlType.ProgrammaticName?.Split('.').LastOrDefault());
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
    }

    private static string Clip(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static IReadOnlyList<string> ExpandRoleQueries(string? role, string? name)
    {
        var queries = new List<string>();
        var normalizedRole = Normalize(role);
        var normalizedName = Normalize(name);

        if (!string.IsNullOrWhiteSpace(name))
        {
            queries.Add(name);
        }

        if (normalizedRole == "button")
        {
            if (normalizedName.Contains("compose", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueQuery(queries, "Compose");
                AddUniqueQuery(queries, "New message");
                AddUniqueQuery(queries, "New mail");
                AddUniqueQuery(queries, "Compose mail");
            }
            else if (normalizedName.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                     normalizedName.Contains("continue", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueQuery(queries, "Next");
                AddUniqueQuery(queries, "Continue");
                AddUniqueQuery(queries, "Next question");
                AddUniqueQuery(queries, "Go to next");
            }
            else if (normalizedName.Contains("send", StringComparison.OrdinalIgnoreCase) &&
                     !normalizedName.Contains("option", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueQuery(queries, "Send");
                AddUniqueQuery(queries, "Send now");
                AddUniqueQuery(queries, "Send email");
            }
            else if (normalizedName.Contains("more send", StringComparison.OrdinalIgnoreCase) ||
                     normalizedName.Contains("schedule", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueQuery(queries, "More send options");
                AddUniqueQuery(queries, "Schedule send");
                AddUniqueQuery(queries, "Send later");
            }
            else if (normalizedName.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                     normalizedName.Contains("finish", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueQuery(queries, "Submit");
                AddUniqueQuery(queries, "Finish");
                AddUniqueQuery(queries, "Done");
            }
        }

        return queries;
    }

    private static IReadOnlyList<string> ExpandFieldQueries(string label)
    {
        var queries = new List<string> { label };
        var normalized = Normalize(label);

        if (normalized.Contains("to", StringComparison.OrdinalIgnoreCase))
        {
            AddUniqueQuery(queries, "To");
            AddUniqueQuery(queries, "To recipients");
            AddUniqueQuery(queries, "Recipients");
        }
        else if (normalized.Contains("subject", StringComparison.OrdinalIgnoreCase))
        {
            AddUniqueQuery(queries, "Subject");
            AddUniqueQuery(queries, "Add a subject");
        }
        else if (normalized.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("body", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("compose", StringComparison.OrdinalIgnoreCase))
        {
            AddUniqueQuery(queries, "Message Body");
            AddUniqueQuery(queries, "Message body");
            AddUniqueQuery(queries, "Message");
            AddUniqueQuery(queries, "Compose email");
        }

        return queries;
    }

    private static void AddUniqueQuery(ICollection<string> queries, string value)
    {
        if (queries.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        queries.Add(value);
    }
}
