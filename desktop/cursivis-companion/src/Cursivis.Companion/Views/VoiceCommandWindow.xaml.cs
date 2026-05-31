using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Cursivis.Companion.Views;

public partial class VoiceCommandWindow : Window
{
    public VoiceCommandWindow(string? initialCommand = null, string? statusTitle = null, string? statusMessage = null)
    {
        InitializeComponent();
        HeaderText.Text = statusTitle ?? (!string.IsNullOrWhiteSpace(initialCommand) ? "Voice captured" : "Voice fallback");
        StatusText.Text = statusMessage ?? (!string.IsNullOrWhiteSpace(initialCommand)
            ? "Review or refine the command before running it."
            : "Voice input was not available this time, so you can type the command instead.");
        StateBadgeText.Text = !string.IsNullOrWhiteSpace(initialCommand) ? "Voice captured" : "Type fallback";
        StateBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(!string.IsNullOrWhiteSpace(initialCommand) ? "#9920122A" : "#99312212"));
        StateBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(!string.IsNullOrWhiteSpace(initialCommand) ? "#66F562E7" : "#66FFD36A"));
        CommandTextBox.Text = string.IsNullOrWhiteSpace(initialCommand) ? string.Empty : initialCommand.Trim();

        Loaded += (_, _) =>
        {
            CommandTextBox.Focus();
            CommandTextBox.SelectAll();
        };
    }

    public string? VoiceCommand { get; private set; }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show("Please enter a command.", "Voice Command", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        VoiceCommand = command;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        VoiceCommand = null;
        DialogResult = false;
        Close();
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
}
