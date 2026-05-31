using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Cursivis.Companion.Views;

public partial class ActionMenuWindow : Window
{
    private readonly ObservableCollection<ActionMenuItem> _items = [];
    private readonly IReadOnlyList<string> _deferredActions;
    private readonly CancellationTokenSource _deferredLoadCts = new();

    public ActionMenuWindow(IReadOnlyList<string> initialActions, IReadOnlyList<string> deferredActions, string contentType)
    {
        InitializeComponent();
        HeaderText.Text = $"Suggested actions for {contentType.Replace('_', ' ')}";
        SubtitleText.Text = "Quick actions are ready now. Gemini is also generating a few smarter context-aware options.";
        _deferredActions = deferredActions;

        foreach (var action in initialActions)
        {
            _items.Add(new ActionMenuItem(action, DescribeAction(action, isContextual: false)));
        }

        ActionList.ItemsSource = _items;
        if (_items.Count > 0)
        {
            ActionList.SelectedIndex = 0;
        }

        Loaded += async (_, _) => await AppendDeferredOptionsAsync();
        Closed += (_, _) => _deferredLoadCts.Cancel();
    }

    public string? SelectedAction { get; private set; }

    private async Task AppendDeferredOptionsAsync()
    {
        if (_deferredActions.Count == 0)
        {
            ThinkingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ThinkingPanel.Visibility = Visibility.Visible;
        ThinkingText.Text = "Gemini is thinking...";
        ThinkingSubtext.Text = "Generating 3-4 smarter, context-aware options for this selection.";

        try
        {
            await Task.Delay(850, _deferredLoadCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var customIndex = _items
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => string.Equals(x.item.Title, "Custom Voice Command", StringComparison.OrdinalIgnoreCase))
            ?.index ?? _items.Count;

        foreach (var action in _deferredActions)
        {
            if (_items.Any(item => string.Equals(item.Title, action, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _items.Insert(customIndex++, new ActionMenuItem(action, DescribeAction(action, isContextual: true)));
        }

        ThinkingText.Text = "Context-aware options ready";
        ThinkingSubtext.Text = "You can keep a quick action, choose one of the new Gemini options, or use custom voice.";
        await Task.Delay(900);
        ThinkingPanel.Visibility = Visibility.Collapsed;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = (ActionList.SelectedItem as ActionMenuItem)?.Title;
        if (string.IsNullOrWhiteSpace(SelectedAction))
        {
            MessageBox.Show("Please choose an action.", "Action Menu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ActionList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Run_Click(sender, new RoutedEventArgs());
    }

    private void HeaderCard_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag interruption.
        }
    }

    private void ResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private static string DescribeAction(string action, bool isContextual)
    {
        if (string.Equals(action, "Custom Voice Command", StringComparison.OrdinalIgnoreCase))
        {
            return "Speak or type exactly what you want Cursivis to do with the current selection.";
        }

        if (action.StartsWith("... (AI Suggest:", StringComparison.OrdinalIgnoreCase))
        {
            return "This is Gemini's current best guess for the most useful next action.";
        }

        return isContextual
            ? "A context-aware option generated specifically for this selection."
            : "Run this action immediately on the current selection.";
    }

    private sealed record ActionMenuItem(string Title, string Description);
}
