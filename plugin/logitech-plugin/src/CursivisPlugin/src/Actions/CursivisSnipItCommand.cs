namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisSnipItCommand : PluginDynamicCommand
    {
        public CursivisSnipItCommand()
            : base(displayName: "Cursivis Snip-it", description: "Start image snipping for the current screen", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("snip-it").GetAwaiter().GetResult();
                PluginLog.Info("Sent snip-it trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send snip-it trigger.");
            }
        }
    }
}
