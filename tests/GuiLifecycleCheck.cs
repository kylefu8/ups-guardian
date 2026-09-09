using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Runs beside a separate GUI build with an empty, unarmed profile. Never calls
// Arm, RelaunchElevated, or any power action; normal exit shares their cleanup.
internal static class GuiLifecycleCheck
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Invoke(Form form, string method, params object[] args)
    { return form.GetType().GetMethod(method, Private).Invoke(form, args); }
    static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    static void CheckConnectionChanges(Form form)
    {
        var type = form.GetType();
        var timer = (Timer)type.GetField("timer", Private).GetValue(form);
        timer.Stop(); // All readings below are synthetic; never contact a UPS.
        var host = (TextBox)type.GetField("host", Private).GetValue(form);
        var port = (NumericUpDown)type.GetField("port", Private).GetValue(form);
        var upsName = (TextBox)type.GetField("upsName", Private).GetValue(form);
        var sampleField = type.GetField("sample", Private);
        var generationField = type.GetField("connectionGeneration", Private);
        var configField = type.GetField("config", Private);
        Action confirmForPolling = delegate {
            object settings = configField.GetValue(form);
            settings.GetType().GetField("ConnectionConfirmed").SetValue(settings, true);
            type.GetField("connectionReady", Private).SetValue(form, true);
        };
        var cards = (Label[])type.GetField("cards", Private).GetValue(form);
        var logic = type.GetField("logic", Private).GetValue(form);
        var evaluate = logic.GetType().GetMethod("Evaluate");
        var snapshotType = type.Assembly.GetType("UpsGuardian.NutSnapshot");
        var snapshot = snapshotType.GetMethod("FromVariables").Invoke(null, new object[] {
            new Dictionary<string, string> { { "ups.status", "OB" }, { "ups.load", "99" },
                { "battery.charge", "1" }, { "battery.runtime", "1" } }, DateTime.UtcNow });

        host.Text = "192.0.2.1";
        Assert((bool)Invoke(form, "SaveControls", false), "Initial test endpoint should save");
        confirmForPolling();
        int generation = (int)generationField.GetValue(form);
        Invoke(form, "CompletePoll", generation, snapshot, null);
        var threshold = (NumericUpDown)type.GetField("threshold", Private).GetValue(form);
        threshold.Value++;
        Assert((bool)Invoke(form, "SaveControls", false), "Policy-only save should succeed");
        Assert(Object.ReferenceEquals(sampleField.GetValue(form), snapshot), "Policy-only save must retain the current sample");

        foreach (Action changeEndpoint in new Action[] {
            delegate { host.Text = "192.0.2.2"; }, delegate { port.Value++; }, delegate { upsName.Text = "other-ups"; } })
        {
            generation = (int)generationField.GetValue(form);
            confirmForPolling();
            Invoke(form, "CompletePoll", generation, snapshot, null);
            for (int i = 0; i < 3; i++) cards[i].Text = "999";
            changeEndpoint();
            // Arm calls this same save path before inspecting the sample.
            Assert((bool)Invoke(form, "SaveControls", false), "Changed endpoint should save without explicit reconnect");
            Assert(sampleField.GetValue(form) == null, "Endpoint change must discard the previous UPS reading before arming");
            Assert((DateTime)type.GetField("nextPoll", Private).GetValue(form) == DateTime.MinValue, "Endpoint change must request a fresh read");
            Invoke(form, "CompletePoll", generation, snapshot, null);
            Assert(sampleField.GetValue(form) == null, "Late result from the previous connection must be discarded");
            Invoke(form, "CompletePoll", generation, null, new IOException("obsolete connection error"));
            Assert((string)type.GetField("lastError", Private).GetValue(form) == "", "Obsolete errors must not affect the new connection");
            object config = configField.GetValue(form);
            Assert(!(bool)config.GetType().GetField("Armed").GetValue(config), "GUI regression must remain disarmed");
            object decision = evaluate.Invoke(logic, new object[] { sampleField.GetValue(form), config, DateTime.UtcNow, true });
            Assert(!(bool)decision.GetType().GetField("Fresh").GetValue(decision) &&
                decision.GetType().GetField("Reduce").GetValue(decision) == null &&
                !(bool)decision.GetType().GetField("HibernateNow").GetValue(decision), "No fresh connection means no protection decision");
            Invoke(form, "UpdatePresentation", decision);
            for (int i = 0; i < 3; i++) Assert(cards[i].Text == "—", "Old endpoint values must disappear from the overview");
        }

        // Even when the endpoint string matches again, a pre-switch read is obsolete.
        generation = (int)generationField.GetValue(form);
        host.Text = "192.0.2.3"; Assert((bool)Invoke(form, "SaveControls", false), "Switch to B");
        host.Text = "192.0.2.2"; Assert((bool)Invoke(form, "SaveControls", false), "Switch back to A");
        Invoke(form, "CompletePoll", generation, snapshot, null);
        Assert(sampleField.GetValue(form) == null, "A -> B -> A must reject the earlier A result");
        generation = (int)generationField.GetValue(form);
        confirmForPolling();
        Invoke(form, "CompletePoll", generation, snapshot, null);
        Assert(Object.ReferenceEquals(sampleField.GetValue(form), snapshot), "A fresh result for the current connection must be accepted");
        Assert((bool)Invoke(form, "SaveControls", true), "Reconnect to the same endpoint");
        Invoke(form, "CompletePoll", generation, snapshot, null);
        Assert(sampleField.GetValue(form) == null, "Explicit reconnect must reject the earlier result too");

        host.Text = ""; Assert((bool)Invoke(form, "SaveControls", true), "Restore an unconfigured profile");
        timer.Start();
    }

    static void CheckGuide(Form form)
    {
        var type = form.GetType();
        var pages = (Panel[])type.GetField("pages", Private).GetValue(form);
        var navigation = (Button[])type.GetField("navigation", Private).GetValue(form);
        Assert(pages.Length == 7 && navigation.Length == 7, "Expected seven top-level sidebar pages");
        Invoke(form, "Navigate", 4);
        var selector = (Button)type.GetField("languageButton", Private).GetValue(form);
        var languageMenu = (ContextMenuStrip)type.GetField("languageMenu", Private).GetValue(form);
        var languageItems = new List<ToolStripMenuItem>();
        foreach (ToolStripItem item in languageMenu.Items)
            if (item is ToolStripMenuItem) languageItems.Add((ToolStripMenuItem)item);
        Assert(languageItems.Count == 8, "Language menu must include system preference and seven native language names");
        var sidebarVersion = (LinkLabel)type.GetField("sidebarVersion", Private).GetValue(form);
        Assert(sidebarVersion.Text == "v" + (string)type.Assembly.GetType("UpsGuardian.BuildInfo").GetField("Version").GetRawConstantValue(),
            "Persistent sidebar version must come from BuildInfo");
        var notes = (RichTextBox)type.GetField("updateNotes", Private).GetValue(form);
        var content = (string[][])type.Assembly.GetType("UpsGuardian.GuideContent").GetField("Sections").GetValue(null);
        var translate = type.Assembly.GetType("UpsGuardian.Localization").GetMethod("T");
        for (int language = 1; language < languageItems.Count; language++)
        {
            selector.PerformClick(); Application.DoEvents();
            Assert(languageMenu.Visible, "Language button must open its menu");
            languageItems[language].PerformClick(); Application.DoEvents();
            Assert(!languageMenu.Visible, "Selecting a language must close its menu");
            for (int item = 0; item < languageItems.Count; item++)
                Assert(languageItems[item].Checked == (item == language), "Exactly the chosen language must be checked");
            Assert(selector.Text == languageItems[language].Text, "Language button must show the current native language name");
            using (var graphics = selector.CreateGraphics())
                Assert(TextRenderer.MeasureText(graphics, selector.Text, selector.Font).Width <= selector.Width - 51 * graphics.DpiX / 96F,
                    "Compact language name must fit beside the globe and arrow");
            Assert(sidebarVersion.Visible && selector.Right <= sidebarVersion.Left, "Language control must not overlap the persistent version");
            using (var graphics = sidebarVersion.CreateGraphics())
                Assert(TextRenderer.MeasureText(graphics, sidebarVersion.Text, sidebarVersion.Font).Width <= sidebarVersion.Width,
                    "Persistent version must fit in the sidebar");
            if (language > 1)
            {
                foreach (string caption in new[] { "操作指南", "连接、保护与日常操作" })
                    Assert((string)translate.Invoke(null, new object[] { caption }) != caption, "Guide caption fell back to Chinese");
                foreach (string[] section in content)
                    Assert((string)translate.Invoke(null, new object[] { section[1] }) != section[1], "Guide body fell back to Chinese");
            }
            Invoke(form, "Navigate", 4);
            Application.DoEvents();
            var reading = (FlowLayoutPanel)type.GetField("guideReading", Private).GetValue(form);
            Assert(reading.Controls.Count >= 9 && reading.VerticalScroll.Visible, "Detailed guide must scroll");
            foreach (Control card in reading.Controls)
                foreach (Control label in card.Controls)
                {
                    Assert(label.Bottom <= card.ClientSize.Height - card.Padding.Bottom, "Guide text clipped vertically");
                    Assert(label.Width <= card.ClientSize.Width - card.Padding.Horizontal, "Guide text clipped horizontally");
                }
            Invoke(form, "Navigate", 2);
            Application.DoEvents();
            Assert(!pages[2].HorizontalScroll.Visible, "Discovery page should scroll vertically without horizontal clipping");
            foreach (string field in new[] { "scanUps", "cancelDiscovery", "confirmUps" })
            {
                var button = (Button)type.GetField(field, Private).GetValue(form);
                using (var graphics = button.CreateGraphics())
                    Assert(TextRenderer.MeasureText(graphics, button.Text, button.Font).Width <= button.ClientSize.Width,
                        "Discovery button caption clipped after changing language: " + field);
                if (language > 1)
                    Assert(!String.IsNullOrWhiteSpace(button.Text) &&
                        button.Text != (field == "scanUps" ? "搜索局域网" : field == "cancelDiscovery" ? "取消搜索" : "确认并连接所选 UPS"),
                        "Discovery button fell back to Chinese: " + field);
            }
            Assert(navigation[6].Bottom <= selector.Top, "Seven sidebar entries must not overlap the language selector");
            Invoke(form, "Navigate", 5);
            Application.DoEvents();
            string previousNotes = notes.Text;
            var updateStatus = (Label)type.GetField("updateStatus", Private).GetValue(form);
            string previousStatus = updateStatus.Text;
            updateStatus.Text = "test-download-progress";
            type.GetField("updateBusy", Private).SetValue(form, true);
            foreach (int index in new[] { 6, 4, 5 })
            {
                navigation[index].PerformClick();
                Application.DoEvents();
                for (int page = 0; page < pages.Length; page++)
                    Assert(pages[page].Visible == (page == index), "Sidebar navigation must show exactly the selected page");
                Assert((bool)type.GetField("updateBusy", Private).GetValue(form) && updateStatus.Text == "test-download-progress",
                    "Sidebar navigation must retain update state");
            }
            type.GetField("updateBusy", Private).SetValue(form, false);
            updateStatus.Text = previousStatus;
            Assert(notes.Text == previousNotes, "Sidebar navigation discarded update notes");
        }
        languageItems[0].PerformClick();
        Assert(languageItems[0].Checked, "System language preference must remain available");
        var preferences = type.GetField("uiPreferences", Private).GetValue(form);
        Assert((string)preferences.GetType().GetField("Language").GetValue(preferences) == "auto", "System preference must be saved as auto");
    }

    [STAThread] static int Main()
    {
        try
        {
            Assert(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")), "Requires a fresh isolated profile");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UPSGuardian.exe"));
            var type = assembly.GetType("UpsGuardian.GuardianForm", true);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var form = (Form)Activator.CreateInstance(type, new object[] { false, false });
                var tray = (NotifyIcon)type.GetField("tray", Private).GetValue(form);
                var menu = tray.ContextMenuStrip;
                var languageMenu = (ContextMenuStrip)type.GetField("languageMenu", Private).GetValue(form);
                bool trayDisposed = false, lateCallbackRan = false;
                tray.Disposed += delegate { trayDisposed = true; };
                form.Show();
                Application.DoEvents();
                Assert(tray.Visible && tray.Icon != null, "Tray must be visible with a real icon");
                if (cycle == 0) { CheckConnectionChanges(form); CheckGuide(form); }
                // The X button must only hide; callbacks and tray remain alive.
                form.Close();
                Assert(!form.IsDisposed && !form.Visible && tray.Visible, "Close-to-tray changed");
                form.Show();
                if (cycle == 2) form.Dispose();
                else
                {
                    Invoke(form, "Ui", (Action)delegate { Invoke(form, "ExitNow"); });
                    Invoke(form, "Ui", (Action)delegate { lateCallbackRan = true; tray.Text = "late callback"; });
                    Application.DoEvents();
                }
                Assert(form.IsDisposed && trayDisposed, "Form and tray must be disposed");
                Assert(menu.IsDisposed, "Owned tray menu must be disposed");
                Assert(languageMenu.IsDisposed, "Owned language menu must be disposed");
                Assert(!lateCallbackRan, "Queued callback ran after shutdown started");
                Invoke(form, "Ui", (Action)delegate { lateCallbackRan = true; });
                form.Dispose();
                Application.DoEvents();
                Assert(!lateCallbackRan, "Disposed form accepted a callback");
            }
            Console.WriteLine("PASS: connection changes/reconnect reject old readings without network or actuation; language popup, persistent version, seven sidebar pages, guide layout and retained update state in seven languages; visible tray, hide/reopen, exit, direct/repeated dispose and queued callbacks (3 cycles)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
