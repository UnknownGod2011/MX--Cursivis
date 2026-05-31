namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisLongPressCommand : PluginDynamicCommand
    {
        public CursivisLongPressCommand()
            : base(displayName: "Cursivis Talk", description: "Start voice command capture in companion", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("long_press").GetAwaiter().GetResult();
                PluginLog.Info("Sent long press trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send long press trigger.");
            }
        }
    }
}
