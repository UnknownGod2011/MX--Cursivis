using Cursivis.Companion.Models;
using System.Windows;

namespace Cursivis.Companion.Views;

public partial class ModeSelectionWindow : Window
{
    public ModeSelectionWindow()
    {
        InitializeComponent();
    }

    public InteractionMode SelectedMode { get; private set; } = InteractionMode.Smart;

    private void Smart_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = InteractionMode.Smart;
        DialogResult = true;
        Close();
    }

    private void Guided_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = InteractionMode.Guided;
        DialogResult = true;
        Close();
    }
}
