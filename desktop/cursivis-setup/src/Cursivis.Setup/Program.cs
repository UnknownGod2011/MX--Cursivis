namespace Cursivis.Setup;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var setupMutex = new Mutex(true, @"Local\Cursivis.Companion.Setup", out var ownsSetupMutex);
        if (!ownsSetupMutex)
        {
            MessageBox.Show(
                "Cursivis Companion Setup is already running. Please wait for it to finish before starting another update.",
                "Cursivis Companion Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }    
}
