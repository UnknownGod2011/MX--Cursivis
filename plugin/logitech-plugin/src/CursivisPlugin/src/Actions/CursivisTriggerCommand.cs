namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisTriggerCommand : PluginDynamicCommand
    {
        public CursivisTriggerCommand()
            : base(displayName: "Cursivis Trigger", description: "Send tap trigger to companion", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("tap").GetAwaiter().GetResult();
                PluginLog.Info("Sent tap trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send tap trigger.");
            }
        }
    }
}
