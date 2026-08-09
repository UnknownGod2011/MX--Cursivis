namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisLiveModeCommand : CompanionAwareCommand
    {
        public CursivisLiveModeCommand()
            : base(
                displayName: "Cursivis Live Mode",
                description: "Start or stop the permission-aware Cursivis voice assistant",
                groupName: "Cursivis",
                supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("live_mode").GetAwaiter().GetResult();
                PluginLog.Info("Sent Live Mode trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send Live Mode trigger.");
            }
        }
    }
}
