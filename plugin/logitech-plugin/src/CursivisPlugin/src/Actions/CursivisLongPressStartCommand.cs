namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisLongPressStartCommand : PluginDynamicCommand
    {
        public CursivisLongPressStartCommand()
            : base(displayName: "Cursivis Long Press Start", description: "Send long press start trigger to companion", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("long_press_start").GetAwaiter().GetResult();
                PluginLog.Info("Sent long press start trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send long press start trigger.");
            }
        }
    }
}
