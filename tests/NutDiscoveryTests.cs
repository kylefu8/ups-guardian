using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UpsGuardian;

internal static class NutDiscoveryTests
{
    private static int passed;

    private static int Main()
    {
        Run("plan deduplicates and caps targets", PlanDeduplicatesAndCapsTargets);
        Run("scan bounds concurrency and verifies status", ScanBoundsConcurrencyAndVerifiesStatus);
        Run("scan returns no result for unreadable UPS", ScanReturnsNoResultForUnreadableUps);
        Run("scan cancellation is bounded", ScanCancellationIsBounded);
        Run("global budget is shared across probes", GlobalBudgetIsShared);
        Run("local plan is bounded", LocalPlanIsBounded);
        Console.WriteLine("Passed " + passed + " discovery tests.");
        return 0;
    }

    private static void PlanDeduplicatesAndCapsTargets()
    {
        List<NutDiscoveryTarget> targets = new List<NutDiscoveryTarget>();
        for (int i = 1; i <= 1025; i++)
            targets.Add(new NutDiscoveryTarget("192.0.2." + (i % 254 + 1), 3493));
        targets.Add(new NutDiscoveryTarget("192.0.2.2", 3493));
        NutDiscoveryPlan plan = new NutDiscoveryPlan(targets, new[] { "192.0.2.0/24" }, false, "test scope");
        Assert(plan.Targets.Count == 254, "duplicate target addresses must be removed");
        Assert(!plan.IsTruncated, "deduplication alone must not report truncation");

        targets.Clear();
        for (int i = 1; i <= 1100; i++)
            targets.Add(new NutDiscoveryTarget("198.51." + (i / 254) + "." + (i % 254 + 1), 3493));
        plan = new NutDiscoveryPlan(targets, new string[0], false, "large test scope");
        Assert(plan.Targets.Count == NutDiscoveryPlan.MaxTargetCount, "plans must cap at 1024 targets");
        Assert(plan.IsTruncated, "capped plans must report truncation");
    }

    private static void ScanBoundsConcurrencyAndVerifiesStatus()
    {
        List<NutDiscoveryTarget> targets = new List<NutDiscoveryTarget>();
        for (int i = 1; i <= 80; i++)
            targets.Add(new NutDiscoveryTarget("192.0.2." + i, 3493));
        NutDiscoveryPlan plan = new NutDiscoveryPlan(targets, "test scope", false);
        FakeProbe probe = new FakeProbe();
        probe.DelayMilliseconds = 20;
        List<NutDiscoveryCandidate> result = NutDiscovery.Scan(plan, probe, 3000, CancellationToken.None);
        Assert(result.Count == 80, "every readable fake endpoint must produce one candidate");
        Assert(probe.MaxActive <= NutDiscovery.MaxConcurrentProbes, "scan concurrency must remain bounded");
        Assert(result[0].UpsName == "ups" && result[0].Description == "Test UPS", "candidate metadata must be preserved");
    }

    private static void ScanReturnsNoResultForUnreadableUps()
    {
        NutDiscoveryPlan plan = new NutDiscoveryPlan(
            new[] { new NutDiscoveryTarget("192.0.2.200", 3493) }, "test scope", false);
        FakeProbe probe = new FakeProbe();
        probe.UpsEntries = new List<NutUpsInfo>
        {
            new NutUpsInfo("failed", "Failed"),
            new NutUpsInfo("readable", "Description unavailable"),
            new NutUpsInfo("Readable", "Case variant"),
            new NutUpsInfo("unreadable", "Unreadable"),
            new NutUpsInfo("readable", "Duplicate")
        };
        probe.ThrowNames.Add("failed");
        probe.ReadableNames.Add("readable");
        probe.Model = "Readable model";
        List<NutDiscoveryCandidate> result = NutDiscovery.Scan(plan, probe, 3000, CancellationToken.None);
        Assert(result.Count == 2, "only readable UPS entries must become candidates and names remain case-sensitive");
        Assert(result[0].UpsName == "Readable" || result[0].UpsName == "readable", "readable UPS entries must be retained");
        for (int i = 0; i < result.Count; i++)
            Assert(result[i].UpsName != "failed" && result[i].UpsName != "unreadable", "failed UPS entries must be omitted");
        for (int i = 0; i < result.Count; i++)
            if (result[i].UpsName == "readable")
                Assert(result[i].Description == "Readable model", "UPS model must replace the default unavailable description");
    }

    private static void ScanCancellationIsBounded()
    {
        List<NutDiscoveryTarget> targets = new List<NutDiscoveryTarget>();
        for (int i = 1; i <= 100; i++)
            targets.Add(new NutDiscoveryTarget("203.0.113." + i, 3493));
        NutDiscoveryPlan plan = new NutDiscoveryPlan(targets, "slow test scope", false);
        FakeProbe probe = new FakeProbe();
        probe.DelayMilliseconds = 5000;
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        using (Timer timer = new Timer(delegate { cancellation.Cancel(); }, null, 100, Timeout.Infinite))
        {
            Stopwatch watch = Stopwatch.StartNew();
            List<NutDiscoveryCandidate> result = NutDiscovery.Scan(plan, probe, 5000, cancellation.Token);
            watch.Stop();
            Assert(watch.ElapsedMilliseconds < 1500, "cancellation must stop a slow scan promptly");
            Assert(result.Count == 0, "cancelled slow probes must not produce candidates");
        }
    }

    private static void GlobalBudgetIsShared()
    {
        var targets = new List<NutDiscoveryTarget>();
        for (int i = 1; i <= 100; i++) targets.Add(new NutDiscoveryTarget("203.0.113." + i, 3493));
        var probe = new FakeProbe { DelayMilliseconds = 5000 };
        var watch = Stopwatch.StartNew();
        var result = NutDiscovery.Scan(new NutDiscoveryPlan(targets, "budget test", false), probe, 150, CancellationToken.None);
        watch.Stop();
        Assert(watch.ElapsedMilliseconds < 1500, "one global deadline must stop the entire scan without external cancellation");
        Assert(result.Count == 0, "unfinished probes must not become candidates when the deadline expires");
    }

    private static void LocalPlanIsBounded()
    {
        NutDiscoveryPlan plan = NutDiscovery.CreateLocalPlan(3493);
        Assert(plan != null && plan.Targets.Count <= NutDiscoveryPlan.MaxTargetCount, "local plan must be bounded");
        Assert(plan.Ranges != null && plan.ScopeDescription != null, "local plan must describe its scope");
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
            Environment.Exit(1);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeProbe : INutDiscoveryProbe
    {
        private int active;
        public int MaxActive;
        public int DelayMilliseconds;
        public string Model;
        public IList<NutUpsInfo> UpsEntries = new List<NutUpsInfo> { new NutUpsInfo("ups", "Test UPS") };
        public readonly HashSet<string> ReadableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ups" };
        public readonly HashSet<string> ThrowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IList<NutUpsInfo> ListUps(string host, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                Delay(cancellationToken);
                return new List<NutUpsInfo>(UpsEntries);
            }
            finally { Leave(); }
        }

        public NutSnapshot Read(string host, int port, string upsName, int timeoutMs, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                Delay(cancellationToken);
                if (ThrowNames.Contains(upsName))
                    throw new InvalidOperationException("fake LIST VAR failure");
                if (!ReadableNames.Contains(upsName))
                    return NutSnapshot.FromVariables(new Dictionary<string, string>(), DateTime.UtcNow);
                return NutSnapshot.FromVariables(new Dictionary<string, string>
                {
                    { "ups.status", "OL" },
                    { "device.model", Model }
                }, DateTime.UtcNow);
            }
            finally { Leave(); }
        }

        private void Enter()
        {
            int current = Interlocked.Increment(ref active);
            while (current > MaxActive)
            {
                int previous = Interlocked.CompareExchange(ref MaxActive, current, MaxActive);
                if (previous >= current)
                    break;
            }
        }

        private void Leave()
        {
            Interlocked.Decrement(ref active);
        }

        private void Delay(CancellationToken cancellationToken)
        {
            int elapsed = 0;
            while (elapsed < DelayMilliseconds)
            {
                if (cancellationToken.WaitHandle.WaitOne(Math.Min(10, DelayMilliseconds - elapsed)))
                    throw new OperationCanceledException(cancellationToken);
                elapsed += 10;
            }
        }
    }
}
