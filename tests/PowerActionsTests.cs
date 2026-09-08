using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UpsGuardian;

internal static class PowerActionsTests
{
    private static readonly Guid Scheme = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static int _passed;

    private sealed class FakeBackend : IPowerActionsBackend, IPowerCapabilitiesBackend
    {
        public bool IsAdministrator = true;
        public Guid ActiveScheme = Scheme;
        public int CpuMaximum = 100;
        public double GpuPower = 285.0;
        public double GpuMinimum = 100.0;
        public double GpuMaximum = 285.0;
        public string RecoveryPath;
        public bool FailGpuAfterMutation;
        public bool FailGpuBeforeMutation;
        public bool FailGpuRead;
        public readonly List<string> Calls = new List<string>();

        bool IPowerActionsBackend.IsAdministrator { get { return IsAdministrator; } }

        Guid IPowerActionsBackend.GetActiveScheme()
        {
            Calls.Add("active");
            return ActiveScheme;
        }

        int IPowerActionsBackend.ReadCpuMaximum(Guid scheme)
        {
            Calls.Add("read-cpu");
            if (scheme != Scheme)
                throw new InvalidOperationException("unexpected scheme");
            return CpuMaximum;
        }

        void IPowerActionsBackend.WriteCpuMaximum(Guid scheme, int value)
        {
            Calls.Add("write-cpu");
            RequireRecoveryBeforeWrite();
            if (scheme != Scheme)
                throw new InvalidOperationException("unexpected scheme");
            CpuMaximum = value;
        }

        void IPowerActionsBackend.RefreshActiveScheme(Guid scheme)
        {
            Calls.Add("refresh");
            if (scheme != ActiveScheme)
                throw new InvalidOperationException("refresh would switch scheme");
        }

        GpuPowerInfo IPowerActionsBackend.ReadGpuPower()
        {
            Calls.Add("read-gpu");
            if (FailGpuRead)
                throw new FileNotFoundException("fake nvidia-smi missing");
            return new GpuPowerInfo(GpuMinimum, GpuMaximum, GpuPower);
        }

        void IPowerActionsBackend.SetGpuPower(double watts)
        {
            Calls.Add("write-gpu-" + watts.ToString("0.###", CultureInfo.InvariantCulture));
            RequireRecoveryBeforeWrite();
            if (FailGpuBeforeMutation)
                throw new IOException("fake GPU write failed before mutation");
            GpuPower = watts;
            if (FailGpuAfterMutation)
            {
                FailGpuAfterMutation = false;
                throw new IOException("fake GPU write failed after mutation");
            }
        }

        void IPowerActionsBackend.Hibernate()
        {
            Calls.Add("hibernate");
        }

        PowerCapabilities IPowerCapabilitiesBackend.DetectCapabilities()
        {
            Calls.Add("capabilities");
            return new PowerCapabilities
            {
                PlatformName = "fake",
                CpuLimitSupported = true,
                GpuLimitSupported = !FailGpuRead,
                SuspendSupported = true,
                GpuMinimumWatts = GpuMinimum,
                GpuMaximumWatts = GpuMaximum,
                GpuCurrentWatts = GpuPower
            };
        }

        private void RequireRecoveryBeforeWrite()
        {
            if (String.IsNullOrEmpty(RecoveryPath) || !File.Exists(RecoveryPath))
                throw new InvalidOperationException("mutation happened before recovery backup");
        }
    }

    private static int Main()
    {
        Run("constructor-does-nothing", ConstructorDoesNothing);
        Run("constructor-reads-recovery-without-actuation", ConstructorReadsRecoveryWithoutActuation);
        Run("backup-before-write", BackupBeforeWrite);
        Run("cpu-only-does-not-query-gpu", CpuOnlyDoesNotQueryGpu);
        Run("missing-gpu-is-capability-only", MissingGpuIsCapabilityOnly);
        Run("detected-gpu-range-is-generic", DetectedGpuRangeIsGeneric);
        Run("legacy-v1-recovery-respects-user-change", LegacyV1RecoveryRespectsUserChange);
        Run("reduction-never-raises-stricter-caps", ReductionNeverRaisesStricterCaps);
        Run("rollback-after-second-action-fail", RollbackAfterSecondActionFail);
        Run("restore-old-settings", RestoreOldSettings);
        Run("failure-preserves-recovery", FailurePreservesRecovery);
        Console.WriteLine("Passed " + _passed.ToString(CultureInfo.InvariantCulture) + " PowerActions tests.");
        return 0;
    }

    private static void ConstructorDoesNothing()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = new FakeBackend();
            backend.RecoveryPath = Path.Combine(directory, "recovery.xml");
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            Assert(!actions.HasRecovery, "new directory must not report a recovery");
            Assert(backend.Calls.Count == 0, "constructor must not query or mutate the backend");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void ConstructorReadsRecoveryWithoutActuation()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend writerBackend = NewBackend(directory);
            WindowsPowerActions writer = new WindowsPowerActions(directory, writerBackend);
            writer.Reduce(50, 200);

            FakeBackend readerBackend = NewBackend(directory);
            WindowsPowerActions reader = new WindowsPowerActions(directory, readerBackend);
            Assert(reader.HasRecovery, "a valid recovery must be visible after restart");
            Assert(readerBackend.Calls.Count == 0, "constructor recovery load must not actuate or query backend");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void BackupBeforeWrite()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 200);
            Assert(File.Exists(backend.RecoveryPath), "recovery must exist after reduction");
            Assert(backend.CpuMaximum == 50 && NearlyEqual(backend.GpuPower, 200), "limits must be applied");
            Assert(backend.Calls.IndexOf("write-cpu") > backend.Calls.IndexOf("read-gpu"), "CPU write must follow capture");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void CpuOnlyDoesNotQueryGpu()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.FailGpuRead = true;
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 0);
            Assert(backend.CpuMaximum == 50, "CPU-only reduction must still apply CPU limit");
            Assert(backend.Calls.IndexOf("read-gpu") < 0, "CPU-only reduction must not query GPU");
            Assert(File.ReadAllText(Path.Combine(directory, "recovery.xml")).IndexOf("<GpuManaged>false</GpuManaged>", StringComparison.Ordinal) >= 0,
                "CPU-only recovery must persist an explicit unmanaged-GPU flag");
            actions.Restore();
            Assert(backend.CpuMaximum == 100, "CPU-only restore must recover CPU value");
            Assert(backend.Calls.IndexOf("read-gpu") < 0, "CPU-only restore must not query GPU");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void DetectedGpuRangeIsGeneric()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.GpuMinimum = 300.0;
            backend.GpuMaximum = 600.0;
            backend.GpuPower = 600.0;
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 500);
            Assert(NearlyEqual(backend.GpuPower, 500.0), "detected GPU limits above 285W must be accepted");
            actions.Restore();
            Assert(NearlyEqual(backend.GpuPower, 600.0), "generic GPU range must restore original value");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void MissingGpuIsCapabilityOnly()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.FailGpuRead = true;
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            PowerCapabilities capabilities = actions.DetectCapabilities();
            Assert(capabilities.CpuLimitSupported, "CPU capability remains available without GPU");
            Assert(!capabilities.GpuLimitSupported, "missing GPU must be reported as unavailable");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void LegacyV1RecoveryRespectsUserChange()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.GpuMinimum = 300.0;
            backend.GpuMaximum = 600.0;
            backend.CpuMaximum = 60;
            backend.GpuPower = 550.0;
            File.WriteAllText(Path.Combine(directory, "recovery.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<PowerRecovery><Version>1</Version><ActiveScheme>" + Scheme.ToString("D") +
                "</ActiveScheme><OriginalCpuMaximum>100</OriginalCpuMaximum><EnforcedCpuMaximum>50</EnforcedCpuMaximum>" +
                "<OriginalGpuPower>600</OriginalGpuPower><EnforcedGpuPower>500</EnforcedGpuPower>" +
                "<CreatedUtc>2026-09-07T00:00:00.0000000Z</CreatedUtc></PowerRecovery>");
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            Assert(actions.HasRecovery, "version 1 recovery must load");
            AssertThrows(delegate { actions.Restore(); }, "changed values in version 1 recovery must not be overwritten");
            Assert(backend.CpuMaximum == 60 && NearlyEqual(backend.GpuPower, 550.0), "legacy recovery must preserve user changes");
            Assert(File.Exists(Path.Combine(directory, "recovery.xml")), "legacy recovery must remain after skipped restore");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void RollbackAfterSecondActionFail()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.FailGpuAfterMutation = true;
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            AssertThrows(delegate { actions.Reduce(50, 200); }, "GPU failure must fail reduction");
            Assert(backend.CpuMaximum == 100, "CPU must roll back after GPU failure");
            Assert(NearlyEqual(backend.GpuPower, 285), "GPU must roll back after its write failed");
            Assert(File.Exists(backend.RecoveryPath), "recovery must remain after uncertain action failure");
            Assert(actions.HasRecovery, "action failure must keep recovery state");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void ReductionNeverRaisesStricterCaps()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            backend.CpuMaximum = 25;
            backend.GpuPower = 100.0;
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 150);
            Assert(backend.CpuMaximum == 25, "CPU reduction must not raise an existing 25% cap");
            Assert(NearlyEqual(backend.GpuPower, 100.0), "GPU reduction must not raise an existing 100W cap");
            Assert(actions.HasRecovery, "the original stricter caps still need a recovery record");
            actions.Restore();
            Assert(!actions.HasRecovery, "safe no-op reduction must still restore and remove recovery");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void RestoreOldSettings()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 200);
            actions.Restore();
            Assert(backend.CpuMaximum == 100, "restore must recover original CPU value");
            Assert(NearlyEqual(backend.GpuPower, 285), "restore must recover original GPU value");
            Assert(!actions.HasRecovery && !File.Exists(backend.RecoveryPath), "safe restore must delete recovery");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void FailurePreservesRecovery()
    {
        string directory = NewDirectory();
        try
        {
            FakeBackend backend = NewBackend(directory);
            WindowsPowerActions actions = new WindowsPowerActions(directory, backend);
            actions.Reduce(50, 200);
            backend.FailGpuBeforeMutation = true;
            AssertThrows(delegate { actions.Restore(); }, "restore failure must be reported");
            Assert(File.Exists(backend.RecoveryPath), "failed restore must preserve recovery");
            Assert(actions.HasRecovery, "failed restore must retain recovery state");
        }
        finally { DeleteDirectory(directory); }
    }

    private static FakeBackend NewBackend(string directory)
    {
        FakeBackend backend = new FakeBackend();
        backend.RecoveryPath = Path.Combine(directory, "recovery.xml");
        return backend;
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            string allowed = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cases")) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup target is outside the test workspace.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
        catch { }
    }

    private static bool NearlyEqual(double left, double right)
    {
        return Math.Abs(left - right) < 0.01;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new Exception(message);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
            Environment.Exit(1);
        }
    }
}
