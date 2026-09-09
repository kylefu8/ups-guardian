using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Exercises the discovery/confirmation state machine with synthetic callbacks.
// It never starts the form, scans the network, confirms through the real worker,
// arms protection or calls a power action.
internal static class DiscoveryUiCheck
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type formType;
    private static Type candidateType;
    private static Type snapshotType;

    private static object Invoke(object target, string method, params object[] args)
    {
        MethodInfo info = target.GetType().GetMethod(method, Private);
        if (info == null)
            throw new InvalidOperationException("Missing private method: " + method);
        return info.Invoke(target, args);
    }

    private static object GetMember(object target, string name)
    {
        if (target == null)
            throw new ArgumentNullException("target");
        FieldInfo field = target.GetType().GetField(name, AnyInstance);
        if (field != null)
            return field.GetValue(target);
        PropertyInfo property = target.GetType().GetProperty(name, AnyInstance);
        if (property != null)
            return property.GetValue(target, null);
        throw new InvalidOperationException("Missing member: " + name);
    }

    private static void SetMember(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, AnyInstance);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }
        PropertyInfo property = target.GetType().GetProperty(name, AnyInstance);
        if (property != null && property.CanWrite)
        {
            property.SetValue(target, value, null);
            return;
        }
        throw new InvalidOperationException("Missing writable member: " + name);
    }

    private static object GetConfig(object form)
    {
        return GetMember(form, "config");
    }

    private static object GetConfigValue(object form, string name)
    {
        return GetMember(GetConfig(form), name);
    }

    private static void SetConfigValue(object form, string name, object value)
    {
        SetMember(GetConfig(form), name, value);
    }

    private static bool GetBool(object target, string name)
    {
        return (bool)GetMember(target, name);
    }

    private static int GetGeneration(object form)
    {
        return (int)GetMember(form, "connectionGeneration");
    }

    private static string ConfigPath(object form)
    {
        return (string)GetMember(form, "ConfigPath");
    }

    private static void SaveConfig(object form)
    {
        object config = GetConfig(form);
        MethodInfo save = config.GetType().GetMethod("Save", AnyInstance);
        if (save == null)
            throw new InvalidOperationException("GuardSettings.Save is unavailable.");
        save.Invoke(config, new object[] { ConfigPath(form) });
    }

    private static object LoadConfig(Assembly assembly, string path)
    {
        Type settingsType = assembly.GetType("UpsGuardian.GuardSettings", true);
        MethodInfo load = settingsType.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (load == null)
            throw new InvalidOperationException("GuardSettings.Load is unavailable.");
        return load.Invoke(null, new object[] { path });
    }

    private static object NewCandidate(string host, int port, string upsName, string description)
    {
        ConstructorInfo constructor = candidateType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new Type[] { typeof(string), typeof(int), typeof(string), typeof(string) }, null);
        if (constructor == null)
            throw new InvalidOperationException("NutDiscoveryCandidate constructor is unavailable.");
        return constructor.Invoke(new object[] { host, port, upsName, description });
    }

    private static object CandidateList(params object[] candidates)
    {
        Type listType = typeof(List<>).MakeGenericType(candidateType);
        IList list = (IList)Activator.CreateInstance(listType);
        foreach (object candidate in candidates)
            list.Add(candidate);
        return list;
    }

    private static object NewSnapshot()
    {
        MethodInfo fromVariables = snapshotType.GetMethod("FromVariables", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (fromVariables == null)
            throw new InvalidOperationException("NutSnapshot.FromVariables is unavailable.");
        return fromVariables.Invoke(null, new object[] {
            new Dictionary<string, string>
            {
                { "ups.status", "OL" },
                { "ups.load", "22" },
                { "ups.realpower.nominal", "750" },
                { "ups.realpower", "165" },
                { "battery.charge", "98" },
                { "battery.runtime", "3600" },
                { "device.model", "Synthetic UPS" }
            },
            DateTime.UtcNow
        });
    }

    private static Form NewForm()
    {
        return (Form)Activator.CreateInstance(formType, new object[] { false, false });
    }

    private static void Pump()
    {
        Application.DoEvents();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual(object expected, object actual, string message)
    {
        Assert(Object.Equals(expected, actual), message + " (expected " + expected + ", got " + actual + ")");
    }

    private static void CheckInitialAndDiscoveryCallbacks(Form form)
    {
        Timer timer = (Timer)GetMember(form, "timer");
        timer.Stop();
        Assert(!GetBool(form, "connectionReady"), "A new form is not connection-ready before confirmation");
        Assert(!GetBool(GetConfig(form), "ConnectionConfirmed"), "A fresh profile starts unconfirmed");

        var scanPort = (NumericUpDown)GetMember(form, "discoveryPort");
        scanPort.Value = 43493;
        AssertEqual(3493, GetConfigValue(form, "Port"), "Changing the scan port must not change the selected target");
        Assert(!GetBool(form, "connectionReady"), "Changing the scan port cannot enable monitoring");
        scanPort.Value = 3493;

        Invoke(form, "Poll");
        Assert(!GetBool(form, "fetching"), "Poll returns without a confirmed connection");

        SetConfigValue(form, "GpuWatts", 150);
        SetConfigValue(form, "CpuMaximum", 37);
        SetConfigValue(form, "UsePercent", false);
        SetConfigValue(form, "LoadThreshold", 750D);
        SetConfigValue(form, "HighConfirmSeconds", 17);
        SetConfigValue(form, "RecoverySeconds", 41);
        SetConfigValue(form, "LowConfirmSeconds", 7);
        SetConfigValue(form, "HibernateCountdownSeconds", 25);
        SetConfigValue(form, "ChargeThreshold", 42D);
        SetConfigValue(form, "RuntimeThresholdSeconds", 600);

        ListView list = (ListView)GetMember(form, "discoveryList");
        Button scan = (Button)GetMember(form, "scanUps");
        Button cancel = (Button)GetMember(form, "cancelDiscovery");
        Button confirm = (Button)GetMember(form, "confirmUps");
        Label status = (Label)GetMember(form, "discoveryStatus");
        Assert(!list.MultiSelect, "Discovery selection is limited to one UPS");
        Assert(!cancel.Enabled, "Cancel is disabled while discovery is idle");

        int generation = GetGeneration(form);
        Invoke(form, "CompleteDiscovery", generation, CandidateList(), null, false);
        Assert(list.Items.Count == 0, "No-result discovery leaves the list empty");
        Assert(status.Text.IndexOf("未找到", StringComparison.Ordinal) >= 0, "No-result status is visible");
        Assert(!GetBool(form, "discoveryBusy"), "No-result discovery clears the busy state");
        Assert(scan.Enabled, "A completed discovery can be started again");

        object first = NewCandidate("192.0.2.10", 3493, "ups-a", "Lab UPS A");
        object second = NewCandidate("192.0.2.11", 3493, "ups-b", "Lab UPS B");
        object stale = NewCandidate("192.0.2.12", 3493, "ups-stale", "Stale result");
        Invoke(form, "CompleteDiscovery", generation, CandidateList(first, second), null, false);
        Pump();
        Assert(list.Items.Count == 2, "Multiple discovery candidates are rendered");
        Assert(list.SelectedItems.Count == 0, "Multiple candidates are not selected automatically");
        Assert(!confirm.Enabled, "Confirmation is disabled until a candidate is selected");
        list.Items[1].Selected = true;
        Pump();
        Assert(list.SelectedItems.Count == 1, "The discovery list allows a single selection");
        Assert(confirm.Enabled, "Confirmation becomes enabled for the selected candidate");
        Assert(list.Items[1].Text.IndexOf("192.0.2.11:3493", StringComparison.Ordinal) >= 0,
            "Candidate endpoint is shown in the list");

        SetMember(form, "connectionGeneration", generation + 1);
        SetMember(form, "discoveryBusy", true);
        string beforeStatus = status.Text;
        Invoke(form, "CompleteDiscovery", generation, CandidateList(stale), new IOException("stale discovery"), false);
        Assert(list.Items.Count == 2, "A stale discovery callback does not replace current results");
        Assert(status.Text == beforeStatus, "A stale discovery callback does not change status");
        Assert(GetBool(form, "discoveryBusy"), "A stale discovery callback does not clear current busy state");

        generation++;
        Invoke(form, "CompleteDiscovery", generation, CandidateList(stale), null, false);
        Assert(list.Items.Count == 1 && list.SelectedItems.Count == 0, "A current single result is rendered without auto-selection");

        int cancelGeneration = (int)Invoke(form, "BeginDiscoveryOperation");
        Pump();
        Assert(cancel.Enabled, "Cancel is enabled during discovery");
        cancel.PerformClick();
        object cancellation = GetMember(form, "discoveryCancellation");
        Assert(cancellation != null && (bool)GetMember(cancellation, "IsCancellationRequested"),
            "Cancel requests cancellation without starting a scan in the test");
        Invoke(form, "CompleteDiscovery", cancelGeneration, CandidateList(), null, true);
        Assert(status.Text.IndexOf("取消", StringComparison.Ordinal) >= 0, "Canceled discovery is reported");
        Assert(!GetBool(form, "discoveryBusy"), "Canceled discovery clears the busy state");
    }

    private static object CheckValidationCallbacks(Form form)
    {
        ListView list = (ListView)GetMember(form, "discoveryList");
        Label status = (Label)GetMember(form, "discoveryStatus");
        object first = NewCandidate("192.0.2.20", 3493, "ups-confirm", "Confirmable UPS");
        int generation = (int)Invoke(form, "BeginDiscoveryOperation");
        ((System.Threading.CancellationTokenSource)GetMember(form, "discoveryCancellation")).Cancel();
        Invoke(form, "CompleteConnectionValidation", generation, first, NewSnapshot(), null, true);
        Assert(!GetBool(form, "connectionReady") && !GetBool(GetConfig(form), "ConnectionConfirmed"),
            "Canceling after a response arrives but before its callback must prevent confirmation");
        generation = (int)Invoke(form, "BeginDiscoveryOperation");
        Invoke(form, "CompleteDiscovery", generation, CandidateList(first), null, false);
        list.Items[0].Selected = true;
        Pump();

        object config = GetConfig(form);
        string oldHost = (string)GetMember(config, "Host");
        SetMember(form, "connectionGeneration", generation + 1);
        SetMember(form, "discoveryBusy", true);
        Invoke(form, "CompleteConnectionValidation", generation, first, NewSnapshot(), null, true);
        Assert(GetBool(form, "discoveryBusy"), "A stale validation callback leaves the current operation busy");
        Assert(!GetBool(form, "connectionReady"), "A stale validation callback cannot make the connection ready");
        AssertEqual(oldHost, GetMember(config, "Host"), "A stale validation callback cannot switch the saved endpoint");

        generation++;
        SetMember(form, "closing", true);
        Invoke(form, "CompleteConnectionValidation", generation, first, null, new IOException("synthetic validation failure"), true);
        SetMember(form, "closing", false);
        Assert(!GetBool(form, "connectionReady"), "Validation failure leaves monitoring unready");
        Assert(!GetBool(form, "discoveryBusy"), "Validation failure clears the busy state");
        AssertEqual(oldHost, GetMember(config, "Host"), "Validation failure does not switch to a failed candidate");
        Assert(status.Text.IndexOf("验证失败", StringComparison.Ordinal) >= 0, "Validation failure reason is visible");

        generation = (int)Invoke(form, "BeginDiscoveryOperation");
        Invoke(form, "CompleteDiscovery", generation, CandidateList(first), null, false);
        list.Items[0].Selected = true;
        object snapshot = NewSnapshot();
        Invoke(form, "CompleteConnectionValidation", generation, first, snapshot, null, true);
        config = GetConfig(form);
        Assert(GetBool(form, "connectionReady"), "Successful confirmation makes monitoring ready");
        Assert(GetBool(config, "ConnectionConfirmed"), "Successful confirmation persists the confirmed flag");
        Assert(!GetBool(config, "Armed"), "Confirmation keeps automatic protection disabled");
        AssertEqual("192.0.2.20", GetMember(config, "Host"), "Confirmation saves the selected host");
        AssertEqual(3493, GetMember(config, "Port"), "Confirmation saves the selected port");
        AssertEqual("ups-confirm", GetMember(config, "UpsName"), "Confirmation saves the selected UPS name");
        AssertEqual(150, GetMember(config, "GpuWatts"), "Confirmation preserves the GPU setting");
        AssertEqual(37, GetMember(config, "CpuMaximum"), "Confirmation preserves the CPU setting");
        AssertEqual(750D, GetMember(config, "LoadThreshold"), "Confirmation preserves the 750W load threshold");
        AssertEqual(17, GetMember(config, "HighConfirmSeconds"), "Confirmation preserves the high-load confirmation time");
        AssertEqual(41, GetMember(config, "RecoverySeconds"), "Confirmation preserves the recovery time");
        AssertEqual(7, GetMember(config, "LowConfirmSeconds"), "Confirmation preserves the battery confirmation time");
        AssertEqual(25, GetMember(config, "HibernateCountdownSeconds"), "Confirmation preserves the hibernate countdown");
        Assert(Object.ReferenceEquals(snapshot, GetMember(form, "sample")), "Confirmation accepts the synthetic current sample");
        Assert(!GetBool(form, "discoveryBusy"), "Successful confirmation clears the busy state");
        Assert(status.Text.IndexOf("只读监测", StringComparison.Ordinal) >= 0, "Confirmation reports read-only monitoring");
        Assert(File.Exists(ConfigPath(form)), "Confirmation writes the settings file");
        return first;
    }

    private static void CheckReloadValidation(Assembly assembly, Form original, object candidate)
    {
        string path = ConfigPath(original);
        SetConfigValue(original, "Armed", true);
        SaveConfig(original);
        object persisted = LoadConfig(assembly, path);
        Assert(GetBool(persisted, "ConnectionConfirmed"), "The confirmed endpoint is persisted");
        Assert(GetBool(persisted, "Armed"), "The persistence check starts from an armed saved profile");
        AssertEqual(150, GetMember(persisted, "GpuWatts"), "Persisted settings retain the GPU value");
        AssertEqual(750D, GetMember(persisted, "LoadThreshold"), "Persisted settings retain the 750W load threshold");
        AssertEqual(17, GetMember(persisted, "HighConfirmSeconds"), "Persisted settings retain the high-load confirmation time");

        Form reloaded = NewForm();
        try
        {
            // The second form has loaded the confirmed endpoint without being
            // shown. Close the original only after disabling persistence so a
            // later window can never auto-validate this test profile.
            SetConfigValue(original, "ConnectionConfirmed", false);
            SaveConfig(original);
            original.Dispose();
            ((Timer)GetMember(reloaded, "timer")).Stop();
            Assert(!GetBool(reloaded, "connectionReady"), "A reloaded form starts unready before validation");
            Assert(GetBool(GetConfig(reloaded), "ConnectionConfirmed"), "A reloaded form sees the saved endpoint without polling");
            Assert(!GetBool(GetConfig(reloaded), "Armed"), "A reloaded armed profile is immediately disarmed");
            Invoke(reloaded, "Poll");
            Assert(!GetBool(reloaded, "fetching"), "A reloaded unready form does not start a network poll");
            int generation = (int)Invoke(reloaded, "BeginDiscoveryOperation");
            SetMember(reloaded, "closing", true);
            Invoke(reloaded, "CompleteConnectionValidation", generation, candidate, null,
                new IOException("saved target validation failure"), false);
            SetMember(reloaded, "closing", false);
            Assert(!GetBool(reloaded, "connectionReady"), "Saved-target validation failure leaves monitoring unready");
            Assert(GetBool(GetConfig(reloaded), "ConnectionConfirmed"), "Saved-target validation failure retains the saved confirmation");
            AssertEqual("192.0.2.20", GetMember(GetConfig(reloaded), "Host"), "Saved-target validation failure retains the saved host");

            generation = (int)Invoke(reloaded, "BeginDiscoveryOperation");
            Invoke(reloaded, "CompleteConnectionValidation", generation, candidate, NewSnapshot(), null, false);
            Assert(GetBool(reloaded, "connectionReady"), "Remember=false validates the saved endpoint");
            Assert(!GetBool(GetConfig(reloaded), "Armed"), "Saved-endpoint validation remains read-only");
            AssertEqual(150, GetMember(GetConfig(reloaded), "GpuWatts"), "Saved-endpoint validation retains the GPU value");
            AssertEqual(750D, GetMember(GetConfig(reloaded), "LoadThreshold"), "Saved-endpoint validation retains the 750W load threshold");
        }
        finally
        {
            // Keep any later test process from treating this profile as eligible
            // for automatic network validation when it is shown.
            SetConfigValue(reloaded, "ConnectionConfirmed", false);
            SaveConfig(reloaded);
            SetMember(reloaded, "connectionReady", false);
            reloaded.Dispose();
        }
    }

    [STAThread]
    private static int Main()
    {
        Form form = null;
        try
        {
            Assert(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")),
                "Discovery test requires a fresh isolated profile");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UPSGuardian.exe"));
            formType = assembly.GetType("UpsGuardian.GuardianForm", true);
            candidateType = assembly.GetType("UpsGuardian.NutDiscoveryCandidate", true);
            snapshotType = assembly.GetType("UpsGuardian.NutSnapshot", true);
            form = NewForm();
            form.Show();
            Pump();
            ((Timer)GetMember(form, "timer")).Stop();
            CheckInitialAndDiscoveryCallbacks(form);
            object candidate = CheckValidationCallbacks(form);
            CheckReloadValidation(assembly, form, candidate);
            form = null;
            Console.WriteLine("PASS: discovery no-result/multi-result/single-select/cancel, stale generations, validation failure, read-only confirmation, persisted revalidation, and 750W settings preservation; no network or actuation");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (form != null)
            {
                try
                {
                    SetConfigValue(form, "ConnectionConfirmed", false);
                    SaveConfig(form);
                    SetMember(form, "connectionReady", false);
                }
                catch { }
                form.Dispose();
            }
        }
    }
}
