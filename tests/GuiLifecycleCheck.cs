using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Runs beside a separate GUI build with an empty, unarmed profile. Never calls
// Arm, RelaunchElevated, or any power action; normal exit shares their cleanup.
internal static class GuiLifecycleCheck
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Invoke(Form form, string method, params object[] args)
    { form.GetType().GetMethod(method, Private).Invoke(form, args); }
    static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    static void CheckGuide(Form form)
    {
        var type = form.GetType();
        var pages = (Panel[])type.GetField("pages", Private).GetValue(form);
        var guidePages = (Panel[])type.GetField("guidePages", Private).GetValue(form);
        Assert(pages.Length == 5 && guidePages.Length == 3, "Expected five sidebar pages and three guide sections");
        Invoke(form, "Navigate", 4);
        var selector = (ComboBox)type.GetField("languageSelector", Private).GetValue(form);
        var notes = (RichTextBox)type.GetField("updateNotes", Private).GetValue(form);
        var content = (string[][])type.Assembly.GetType("UpsGuardian.GuideContent").GetField("Sections").GetValue(null);
        var translate = type.Assembly.GetType("UpsGuardian.Localization").GetMethod("T");
        for (int language = 1; language < selector.Items.Count; language++)
        {
            selector.SelectedIndex = language;
            if (language > 1)
            {
                foreach (string caption in new[] { "操作指南", "连接、保护与日常操作" })
                    Assert((string)translate.Invoke(null, new object[] { caption }) != caption, "Guide caption fell back to Chinese");
                foreach (string[] section in content)
                    Assert((string)translate.Invoke(null, new object[] { section[1] }) != section[1], "Guide body fell back to Chinese");
            }
            Invoke(form, "NavigateGuide", 0);
            Application.DoEvents();
            var reading = (FlowLayoutPanel)guidePages[0].Controls[0];
            Assert(reading.Controls.Count >= 9 && reading.VerticalScroll.Visible, "Detailed guide must scroll");
            foreach (Control card in reading.Controls)
                foreach (Control label in card.Controls)
                {
                    Assert(label.Bottom <= card.ClientSize.Height - card.Padding.Bottom, "Guide text clipped vertically");
                    Assert(label.Width <= card.ClientSize.Width - card.Padding.Horizontal, "Guide text clipped horizontally");
                }
            Invoke(form, "NavigateGuide", 1);
            Application.DoEvents();
            string previousNotes = notes.Text;
            for (int section = 1; section < 3; section++)
            {
                Invoke(form, "NavigateGuide", section);
                Application.DoEvents();
                Assert(guidePages[section].Visible && !guidePages[0].Visible, "Guide subnavigation failed");
            }
            Assert(notes.Text == previousNotes, "Subnavigation discarded update notes");
        }
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
                bool trayDisposed = false, lateCallbackRan = false;
                tray.Disposed += delegate { trayDisposed = true; };
                form.Show();
                Application.DoEvents();
                Assert(tray.Visible && tray.Icon != null, "Tray must be visible with a real icon");
                if (cycle == 0) CheckGuide(form);
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
                Assert(!lateCallbackRan, "Queued callback ran after shutdown started");
                Invoke(form, "Ui", (Action)delegate { lateCallbackRan = true; });
                form.Dispose();
                Application.DoEvents();
                Assert(!lateCallbackRan, "Disposed form accepted a callback");
            }
            Console.WriteLine("PASS: guide tabs/layout in seven languages; visible tray, hide/reopen, exit, direct/repeated dispose and queued callbacks (3 cycles)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
