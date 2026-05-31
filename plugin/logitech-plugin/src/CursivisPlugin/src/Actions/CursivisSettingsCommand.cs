namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisSettingsCommand : PluginDynamicCommand
    {
        public CursivisSettingsCommand()
            : base(displayName: "Cursivis Settings", description: "Open the Cursivis companion settings window", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
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
