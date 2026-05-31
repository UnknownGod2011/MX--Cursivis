namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisTakeActionCommand : PluginDynamicCommand
    {
        public CursivisTakeActionCommand()
            : base(displayName: "Cursivis Take Action", description: "Run direct take action on the current selection", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("action").GetAwaiter().GetResult();
                PluginLog.Info("Sent take action trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send take action trigger.");
            }
        }
    }
}
