using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UpsGuardian;

class GuardLogicTests
{
    static readonly DateTime Epoch = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    static int passed;
    static NutSnapshot Sample(int at, string status, double? load, double? charge, double? runtime)
    {
        var values = new Dictionary<string, string>();
        values["ups.status"] = status; values["ups.realpower.nominal"] = "650";
        if (load.HasValue) values["ups.load"] = load.Value.ToString(CultureInfo.InvariantCulture);
        if (charge.HasValue) values["battery.charge"] = charge.Value.ToString(CultureInfo.InvariantCulture);
        if (runtime.HasValue) values["battery.runtime"] = runtime.Value.ToString(CultureInfo.InvariantCulture);
        return NutSnapshot.FromVariables(values, Epoch.AddSeconds(at));
    }
    static GuardDecision Run(GuardLogic logic, GuardSettings settings, int at, string status = "OL", double? load = 90, double? charge = 100, double? runtime = 600, bool armed = true)
    { return logic.Evaluate(Sample(at, status, load, charge, runtime), settings, Epoch.AddSeconds(at), armed); }
    static void Assert(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
    static int Main()
    {
        try
        {
            var defaults = new GuardSettings();
            defaults.Validate();
            Assert(defaults.Host == "" && defaults.UsePercent && defaults.LoadThreshold == 80 &&
                defaults.RecoveryMargin == 10 && defaults.GpuWatts == 0 && !defaults.ConnectionConfirmed,
                "new profile is generic, percent-based, and CPU-only until configured");
            defaults.Host = "bad host with spaces";
            bool malformedHostRejected = false;
            try { defaults.Validate(); } catch { malformedHostRejected = true; }
            Assert(malformedHostRejected, "non-empty malformed UPS host is rejected");
            string preservedPath = Path.Combine(Path.GetTempPath(), "ups-guardian-profile-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var existing = new GuardSettings { Host = "198.51.100.20", UsePercent = false, LoadThreshold = 750, RecoveryMargin = 50, GpuWatts = 150, ConnectionConfirmed = true };
                existing.Save(preservedPath);
                var loaded = GuardSettings.Load(preservedPath);
                Assert(loaded.Host == existing.Host && loaded.UsePercent == existing.UsePercent &&
                    loaded.LoadThreshold == existing.LoadThreshold && loaded.RecoveryMargin == existing.RecoveryMargin &&
                    loaded.GpuWatts == existing.GpuWatts,
                    "existing saved profile values remain unchanged");
                Assert(loaded.ConnectionConfirmed, "explicit UPS confirmation is remembered");
                File.WriteAllText(preservedPath, File.ReadAllText(preservedPath).Replace("<ConnectionConfirmed>true</ConnectionConfirmed>", ""));
                var legacy = GuardSettings.Load(preservedPath);
                Assert(!legacy.ConnectionConfirmed && legacy.LoadThreshold == 750 && legacy.Host == existing.Host,
                    "legacy endpoints require confirmation without changing saved thresholds");
            }
            finally { try { if (File.Exists(preservedPath)) File.Delete(preservedPath); } catch { } }
            var settings = new GuardSettings { UsePercent = true, LoadThreshold = 80, RecoveryMargin = 10 };
            var logic = new GuardLogic();
            Assert(!settings.Armed, "new configuration is disarmed");
            Assert(Run(logic, settings, 0).Reduce == null && Run(logic, settings, 4).Reduce == null && Run(logic, settings, 5).Reduce == true, "sustained high load required before reduction");
            Assert(Run(logic, settings, 6, load: 75).Reduce == null && Run(logic, settings, 40, load: 75).Reduce == null, "hysteresis band keeps existing limit");
            Assert(Run(logic, settings, 41, load: 69).Reduce == null && Run(logic, settings, 70, load: 69).Reduce == null && Run(logic, settings, 71, load: 69).Reduce == false, "recovery requires full stable thirty seconds");
            logic.Reset();
            Run(logic, settings, 0); Run(logic, settings, 3, load: 75);
            Assert(Run(logic, settings, 5).Reduce == null, "brief high spike does not accumulate across recovery");
            logic.Reset();
            Run(logic, settings, 0, "OL", charge: 10, runtime: 10);
            Assert(!Run(logic, settings, 100, "OL", charge: 10, runtime: 10).HibernateNow, "mains supply never causes low battery hibernation");
            logic.Reset();
            Run(logic, settings, 0, "OB", charge: 49, runtime: 600);
            Assert(Run(logic, settings, 5, "OB", charge: 49, runtime: 600).HibernateInSeconds == 20, "charge below fifty alone starts countdown");
            Assert(!Run(logic, settings, 24, "OB", charge: 49, runtime: 600).HibernateNow && Run(logic, settings, 25, "OB", charge: 49, runtime: 600).HibernateNow, "hibernate follows full countdown");
            Assert(Run(logic, settings, 26, "OB", charge: 49, runtime: 600).HibernateNow, "busy actuator does not lose pending hibernation");
            Assert(!Run(logic, settings, 27, "OB", charge: 49, runtime: 600, armed: false).HibernateNow, "disarming cancels all pending actions");
            logic.Reset();
            Run(logic, settings, 0, "OB", charge: 100, runtime: 179);
            Assert(Run(logic, settings, 5, "OB", charge: 100, runtime: 179).HibernateInSeconds.HasValue, "short runtime alone triggers on battery");
            Assert(!Run(logic, settings, 6, "OL", charge: 10, runtime: 10).HibernateInSeconds.HasValue, "mains return cancels countdown");
            logic.Reset();
            Run(logic, settings, 0, "OB", charge: 50, runtime: 180);
            Assert(!Run(logic, settings, 60, "OB", charge: 50, runtime: 180).HibernateNow, "strict less-than threshold boundaries");
            logic.Reset();
            Run(logic, settings, 0, "OB", charge: null, runtime: null);
            Assert(!Run(logic, settings, 60, "OB", charge: null, runtime: null).HibernateInSeconds.HasValue, "unknown battery fields are not zero");
            logic.Reset(); Run(logic, settings, 0, "OB", charge: 10, runtime: 10); Run(logic, settings, 5, "OB", charge: 10, runtime: 10);
            var stale = logic.Evaluate(Sample(5, "OB", 90, 10, 10), settings, Epoch.AddSeconds(20), true);
            Assert(!stale.Fresh && !stale.HibernateInSeconds.HasValue && !stale.Reduce.HasValue, "stale sample cancels new actions and countdown");
            Assert(!Run(logic, settings, 21, "OB", charge: 10, runtime: 10).HibernateInSeconds.HasValue, "reconnection restarts low battery confirmation");
            settings.UsePercent = false; settings.LoadThreshold = 520; settings.RecoveryMargin = 65; logic.Reset();
            Run(logic, settings, 0, load: 81);
            Assert(Run(logic, settings, 5, load: 81).Reduce == true, "watts mode uses real NUT load times nominal watts");
            bool rejected = false; settings.RecoveryMargin = settings.LoadThreshold;
            try { settings.Validate(); } catch { rejected = true; }
            Assert(rejected, "invalid recovery configuration rejected");
            Console.WriteLine("All " + passed + " policy checks passed; no hardware actions were invoked."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
