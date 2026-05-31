using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Cursivis.Companion.Views;

public partial class ActionPlanPreviewWindow : Window
{
    public ActionPlanPreviewWindow(BrowserActionPlanResponse plan, string currentAction, string? initialInstruction)
    {
        InitializeComponent();
        CompanionThemeService.ThemeChanged += CompanionThemeServiceOnThemeChanged;
        TitleText.Text = "Preview Take Action";
        SummaryText.Text = plan.Summary;
        CurrentActionText.Text = $"Current result action: {currentAction}";
        InstructionTextBox.Text = initialInstruction?.Trim() ?? string.Empty;
        RiskText.Visibility = plan.RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
        StepList.ItemsSource = plan.Steps.Select(FormatStep).ToList();
        ApplyThemeMode(CompanionThemeService.CurrentMode);
    }

    public string? AdditionalInstruction { get; private set; }

    public bool ChangeResultRequested { get; private set; }

    private static string FormatStep(BrowserActionStep step)
    {
        return step.Tool switch
        {
            "navigate" => $"Navigate to {step.Url}",
            "click_role" => $"Click {step.Role} \"{step.Name ?? step.Text}\"",
            "click_text" => $"Click text \"{step.Text ?? step.Name}\"",
            "fill_label" => $"Fill \"{step.Label ?? step.Name}\"",
            "fill_name" => $"Fill field {step.NameAttribute ?? step.Name}",
            "fill_placeholder" => $"Fill placeholder \"{step.Placeholder ?? step.Label}\"",
            "type_active" => "Type into the active editor",
            "select_option" => $"Select \"{step.Option ?? step.Text}\"",
            "check_radio" => $"Choose option \"{step.Option ?? step.Label ?? step.Name}\"",
            "check_checkbox" => $"Check \"{step.Option ?? step.Label ?? step.Name}\"",
            "press_key" => $"Press {step.Key}",
            "wait_for_text" => $"Wait for text \"{step.Text ?? step.Name}\"",
            "wait_ms" => $"Wait {step.WaitMs ?? 0} ms",
            _ => step.Tool
        };
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        AdditionalInstruction = NormalizeInstruction(InstructionTextBox.Text);
        DialogResult = true;
        Close();
    }

    private void ChangeResult_Click(object sender, RoutedEventArgs e)
    {
        AdditionalInstruction = NormalizeInstruction(InstructionTextBox.Text);
        ChangeResultRequested = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        CompanionThemeService.ThemeChanged -= CompanionThemeServiceOnThemeChanged;
        base.OnClosed(e);
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

    private static string? NormalizeInstruction(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private void CompanionThemeServiceOnThemeChanged(object? sender, CompanionThemeMode mode)
    {
        Dispatcher.Invoke(() => ApplyThemeMode(mode));
    }

    private void ApplyThemeMode(CompanionThemeMode mode)
    {
        RunButton.Style = (Style)FindResource("GlassButtonStyle");
    }
}
