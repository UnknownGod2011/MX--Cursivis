namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisTriggerCommand : CompanionAwareCommand
    {
        public CursivisTriggerCommand()
            : base(displayName: "Cursivis Go", description: "Understand the selected text or image", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
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
