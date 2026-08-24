namespace Loupedeck.CursivisPlugin
{
    using System;

    // The SDK requires a ClientApplication implementation even for universal plugins.
    // Keep it deliberately unbound: Companion health is handled by TriggerIpcClient.

    public class CursivisApplication : ClientApplication
    {
        public CursivisApplication()
        {
        }

        // A universal plugin must not be activated by any foreground process.
        protected override String GetProcessName() => "";

        // This method can be used to link the plugin to a macOS application.
        protected override String GetBundleName() => "";

        // Companion installation is runtime health, not Logitech application status.
        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
