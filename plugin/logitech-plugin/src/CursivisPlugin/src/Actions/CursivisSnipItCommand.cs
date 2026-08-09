namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisSnipItCommand : CompanionAwareCommand
    {
        public CursivisSnipItCommand()
            : base(displayName: "Cursivis Snip", description: "Capture and understand part of the current screen", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
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
