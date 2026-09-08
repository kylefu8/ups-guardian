using System;

namespace UpsGuardian
{
    /// <summary>
    /// Local power actions used by the UPS policy. Reduce and Restore are
    /// deliberately separate from Hibernate so a policy engine cannot
    /// accidentally hibernate while it is only changing performance limits.
    /// A GPU value of zero passed to Reduce means CPU-only reduction.
    /// </summary>
    public interface IPowerActions
    {
        bool HasRecovery { get; }
        string RecoverySummary { get; }
        void Reduce(int cpuMaximumPercent, int gpuWatts);
        void Restore();
        void Hibernate();
    }

    /// <summary>One GPU's detected limits and current power limit.</summary>
    public sealed class GpuPowerInfo
    {
        public GpuPowerInfo(double minimumWatts, double maximumWatts, double currentWatts)
        {
            MinimumWatts = minimumWatts;
            MaximumWatts = maximumWatts;
            CurrentWatts = currentWatts;
        }

        public double MinimumWatts { get; private set; }
        public double MaximumWatts { get; private set; }
        public double CurrentWatts { get; private set; }
    }

    /// <summary>
    /// Read-only platform capabilities used by the UI and policy setup.
    /// Nullable GPU values mean that no usable GPU power interface was found.
    /// </summary>
    public sealed class PowerCapabilities
    {
        public bool CpuLimitSupported { get; set; }
        public bool GpuLimitSupported { get; set; }
        public bool SuspendSupported { get; set; }
        public double? GpuMinimumWatts { get; set; }
        public double? GpuMaximumWatts { get; set; }
        public double? GpuCurrentWatts { get; set; }
        public string PlatformName { get; set; }
        public string SessionProtectionMode { get; set; }
        public string GpuStatus { get; set; }
        public string SuspendStatus { get; set; }

        public PowerCapabilities()
        {
            PlatformName = String.Empty;
            SessionProtectionMode = String.Empty;
            GpuStatus = String.Empty;
            SuspendStatus = String.Empty;
        }
    }

    /// <summary>
    /// Narrow platform seam used by the Windows adapter. The seam is
    /// platform-neutral so the policy and tests can be reused by a future
    /// macOS adapter without importing Windows types.
    /// </summary>
    public interface IPowerActionsBackend
    {
        bool IsAdministrator { get; }
        Guid GetActiveScheme();
        int ReadCpuMaximum(Guid scheme);
        void WriteCpuMaximum(Guid scheme, int value);
        void RefreshActiveScheme(Guid scheme);
        GpuPowerInfo ReadGpuPower();
        void SetGpuPower(double watts);
        void Hibernate();
    }

    /// <summary>
    /// Optional capability probe seam. Kept separate so adding capability
    /// discovery does not break existing IPowerActionsBackend implementations.
    /// </summary>
    public interface IPowerCapabilitiesBackend
    {
        PowerCapabilities DetectCapabilities();
    }
}
