#nullable enable

namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.Runtime.Versioning;
    using System.Threading;
    using System.Windows.Forms;

    [SupportedOSPlatform("windows6.1")]
    internal static class CompanionRecoveryPrompt
    {
        private static Int32 _isShowing;

        public static void Show()
        {
            if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _isShowing, 1) != 0)
            {
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                var getCompanionButton = new TaskDialogButton("Get Companion");
                var page = new TaskDialogPage
                {
                    Caption = "Cursivis",
                    Heading = "Companion Missing",
                    Text = "Cursivis Companion is required. Install it once, then this Logitech action will reconnect automatically.",
                    Icon = TaskDialogIcon.Information,
                    AllowCancel = true,
                };

                page.Buttons.Add(getCompanionButton);
                page.Buttons.Add(TaskDialogButton.Close);

                if (TaskDialog.ShowDialog(page) == getCompanionButton)
                {
                    CompanionRuntimeState.OpenDownloadPage();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Companion recovery dialog failed. Setup: {CompanionRuntimeState.DownloadUrl}");
                CompanionRuntimeState.OpenDownloadPage();
            }
            finally
            {
                Volatile.Write(ref _isShowing, 0);
            }
        }
    }
}
