namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.Threading;

    // Plugin-level logic for the Cursivis Logitech integration.

    public class CursivisPlugin : Plugin
    {
        private CompanionHapticClient _companionHapticClient;

        // Gets a value indicating whether this is an API-only plugin.
        public override Boolean UsesApplicationApiOnly => true;

        // Gets a value indicating whether this is a Universal plugin or an Application plugin.
        public override Boolean HasNoApplication => true;

        // Initializes a new instance of the plugin class.
        public CursivisPlugin()
        {
            // Initialize the plugin log.
            PluginLog.Init(this.Log);

            // Initialize the plugin resources.
            PluginResources.Init(this.Assembly);

            this._companionHapticClient = new CompanionHapticClient(this);
        }

        public override void Load()
        {
            this.PluginEvents.AddEvent("action_change", "Action Change", "Raised when the selected Cursivis action changes.");
            this.PluginEvents.AddEvent("action_execute", "Action Execute", "Raised when Cursivis executes the selected action.");
            this.PluginEvents.AddEvent("processing_start", "Processing Start", "Raised when Cursivis begins processing.");
            this.PluginEvents.AddEvent("processing_complete", "Processing Complete", "Raised when Cursivis completes processing.");

            // Dynamic actions are discovered by the current Logi Actions SDK template,
            // so there is no explicit registration call needed here.
            this._companionHapticClient.Start();
        }

        public override void Unload()
        {
            this._companionHapticClient.Dispose();
            this._companionHapticClient = new CompanionHapticClient(this);
        }
    }
}
