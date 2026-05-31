namespace Loupedeck.CursivisPlugin
{
    using System;

    public class CursivisPromptOptimizerCommand : PluginDynamicCommand
    {
        public CursivisPromptOptimizerCommand()
            : base(displayName: "Cursivis Prompt Optimizer", description: "Optimize selected rough prompts or notes with Cursivis", groupName: "Cursivis", supportedDevices: DeviceType.LoupedeckExtendedFamily)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                TriggerIpcClient.SendAsync("prompt_optimizer").GetAwaiter().GetResult();
                PluginLog.Info("Sent prompt optimizer trigger to companion.");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to send prompt optimizer trigger.");
            }
        }
    }
}
