#nullable enable

namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    // Keep Cursivis actions honest in Options+ without making the recovery action disappear.
    internal static class CompanionActionAvailabilityMonitor
    {
        private static readonly Object SyncRoot = new Object();
        private static readonly List<ICompanionAwareAction> Registrations = new List<ICompanionAwareAction>();
        private static Timer? _timer;

        public static void Register(ICompanionAwareAction action)
        {
            lock (SyncRoot)
            {
                Registrations.Add(action);
            }

            Refresh();
        }

        public static void Start()
        {
            lock (SyncRoot)
            {
                _timer ??= new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            }
        }

        public static void Stop()
        {
            lock (SyncRoot)
            {
                _timer?.Dispose();
                _timer = null;
                Registrations.Clear();
            }
        }

        private static void Refresh()
        {
            var companionAvailable = CompanionRuntimeState.GetSnapshot(refresh: true).IsInstalled;
            lock (SyncRoot)
            {
                foreach (var registration in Registrations)
                {
                    try
                    {
                        registration.RefreshCompanionAvailability(companionAvailable);
                    }
                    catch
                    {
                        // The plugin service can dispose actions during an asynchronous refresh.
                    }
                }
            }
        }

    }

    internal interface ICompanionAwareAction
    {
        void RefreshCompanionAvailability(Boolean companionAvailable);
    }

    public abstract class CompanionAwareCommand : PluginDynamicCommand, ICompanionAwareAction
    {
        private readonly Boolean _isRecoveryAction;

        protected CompanionAwareCommand(
            String displayName,
            String description,
            String groupName,
            DeviceType supportedDevices,
            Boolean isRecoveryAction = false)
            : base(displayName, description, groupName, supportedDevices)
        {
            this._isRecoveryAction = isRecoveryAction;
            CompanionActionAvailabilityMonitor.Register(this);
        }

        public void RefreshCompanionAvailability(Boolean companionAvailable)
        {
            this.IsEnabled = this._isRecoveryAction || companionAvailable;
        }
    }

    public abstract class CompanionAwareAdjustment : PluginDynamicAdjustment, ICompanionAwareAction
    {
        protected CompanionAwareAdjustment(
            String displayName,
            String description,
            String groupName,
            Boolean hasReset,
            DeviceType supportedDevices)
            : base(displayName, description, groupName, hasReset, supportedDevices)
        {
            CompanionActionAvailabilityMonitor.Register(this);
        }

        public void RefreshCompanionAvailability(Boolean companionAvailable)
        {
            this.IsEnabled = companionAvailable;
        }
    }
}
