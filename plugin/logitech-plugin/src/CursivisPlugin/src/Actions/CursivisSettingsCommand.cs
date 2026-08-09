namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisSettingsCommand : CompanionAwareCommand
    {
        public CursivisSettingsCommand()
            : base(displayName: "Cursivis Settings", description: "Open Cursivis settings or download the Companion setup", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily, isRecoveryAction: true)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("settings").GetAwaiter().GetResult();
                PluginLog.Info("Sent settings trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send settings trigger.");
            }
        }
    }
}
