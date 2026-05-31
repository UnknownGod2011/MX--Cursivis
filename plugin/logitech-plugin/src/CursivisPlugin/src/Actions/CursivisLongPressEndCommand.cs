namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisLongPressEndCommand : PluginDynamicCommand
    {
        public CursivisLongPressEndCommand()
            : base(displayName: "Cursivis Long Press End", description: "Send long press end trigger to companion", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("long_press_end").GetAwaiter().GetResult();
                PluginLog.Info("Sent long press end trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send long press end trigger.");
            }
        }
    }
}
