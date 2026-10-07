using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Real windows in a fresh profile. No display settings, network or power actions.
internal static class WindowLayoutCheck
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    static Form form;
    static Type type;
    static object Field(string name) { return type.GetField(name, Private).GetValue(form); }
    static void Call(string name, params object[] args) { type.GetMethod(name, Private).Invoke(form, args); }
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static Button LastButton(Control parent)
    {
        Button result = null;
        foreach (Control child in parent.Controls)
        {
            Button candidate = child as Button;
            if (candidate != null && (result == null || candidate.Bottom > result.Bottom)) result = candidate;
        }
        Assert(result != null, "Expected an action button");
        return result;
    }

    static void Reach(ScrollableControl viewport, Control target)
    {
        viewport.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
        Point location = viewport.PointToClient(target.PointToScreen(Point.Empty));
        viewport.AutoScrollPosition = new Point(Math.Max(0, location.X + target.Width - viewport.ClientSize.Width + 8),
            Math.Max(0, location.Y + target.Height - viewport.ClientSize.Height + 8));
        Application.DoEvents();
        Rectangle visible = viewport.RectangleToClient(target.RectangleToScreen(target.ClientRectangle));
        Assert(viewport.ClientRectangle.Contains(visible), "Action remains clipped after scrolling: " + target.Text + " " + visible + " in " + viewport.ClientRectangle);
    }

    static void CheckSize(Size size)
    {
        form.ClientSize = size;
        Application.DoEvents();
        var pages = (Panel[])Field("pages");
        var sidebar = (Panel)Field("sidebarViewport");
        var content = (Panel)Field("sidebarContent");
        var footer = (Panel)Field("windowFooter");
        Assert(sidebar.Height == form.ClientSize.Height && !sidebar.HorizontalScroll.Visible, "Sidebar is clipped horizontally");
        Assert(footer.Bottom == form.ClientSize.Height && footer.Right == form.ClientSize.Width,
            "Footer left the window; requested=" + size + "; client=" + form.ClientSize + "; footer=" + footer.Bounds + "; sidebar=" + sidebar.Bounds);
        for (int i = 0; i < pages.Length; i++)
        {
            Call("Navigate", i); Application.DoEvents();
            Panel page = pages[i];
            Assert(page.Left == sidebar.Right && page.Right == form.ClientSize.Width && page.Bottom == footer.Top,
                "Page does not follow the window: " + i);
            if (i == 0) Reach(page, (Control)Field("arm"));
            if (i == 1)
            {
                ((Panel)Field("rules")).Enabled = true;
                if (!(bool)Field("advancedPolicyExpanded")) ((Button)Field("advancedPolicyToggle")).PerformClick();
                Reach(page, LastButton((Panel)Field("policySaveBar")));
                Rectangle saveBounds = ((Panel)Field("policySaveBar")).Bounds;
                Call("UpdateRuleDescriptions"); Application.DoEvents();
                Assert(((Panel)Field("policySaveBar")).Bounds == saveBounds, "Refreshing summaries moved the scrolled save controls");
            }
            if (i == 2) Reach(page, (Control)Field("saveConnectionSettings"));
            if (i == 3) Reach(page, LastButton(page));
            if (i == 4)
            {
                var reading = (FlowLayoutPanel)Field("guideReading");
                Assert(page.ClientRectangle.Contains(reading.Bounds), "Guide viewport is outside the page");
                reading.AutoScrollPosition = new Point(Int32.MaxValue / 2, Int32.MaxValue / 2);
                Application.DoEvents();
                Assert(reading.VerticalScroll.Visible && reading.AutoScrollPosition.Y < 0, "Last guide sections are unreachable");
                if (reading.Controls[0].Width > reading.ClientSize.Width)
                    Assert(reading.HorizontalScroll.Visible && reading.AutoScrollPosition.X < 0, "Narrow guide cannot scroll right");
            }
            if (i == 5) Reach(page, (Control)Field("installUpdate"));
            if (i == 6)
                foreach (Control child in page.Controls)
                    if (child is Panel) Reach(page, LastButton(child));
        }
        Reach(sidebar, LastButton(content));
        Reach(sidebar, (Control)Field("languageButton"));
        foreach (Panel page in pages) page.AutoScrollPosition = Point.Empty;
        sidebar.AutoScrollPosition = Point.Empty;
    }

    static void CheckWorkingAreas()
    {
        foreach (Rectangle area in new[] { new Rectangle(0, 0, 800, 600), new Rectangle(-1600, -100, 700, 480), new Rectangle(100, 80, 500, 360) })
        {
            form.Bounds = new Rectangle(area.Left - 400, area.Top - 200, 1600, 1000);
            Call("FitWindowToWorkingArea", area); Application.DoEvents();
            Assert(area.Contains(form.Bounds), "Window is outside the new working area: " + form.Bounds);
            Assert(form.MinimumSize.Width <= area.Width && form.MinimumSize.Height <= area.Height, "Minimum size prevents fitting a smaller display");
        }
        // Exercise the real display-message route, without changing a display.
        SendMessage(form.Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
        Application.DoEvents();
        Assert(Screen.FromControl(form).WorkingArea.Contains(form.Bounds), "Display-change message did not recover the window");
        form.WindowState = FormWindowState.Maximized; Application.DoEvents();
        Call("FitWindowToWorkingArea", Screen.FromControl(form).WorkingArea);
        Assert(form.WindowState == FormWindowState.Maximized, "Work-area changes unexpectedly restore a maximized window");
        form.WindowState = FormWindowState.Normal; Application.DoEvents();
    }

    [STAThread] static int Main()
    {
        try
        {
            Assert(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")), "Requires a fresh isolated profile");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            type = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UPSGuardian.exe")).GetType("UpsGuardian.GuardianForm", true);
            form = (Form)Activator.CreateInstance(type, new object[] { false, false });
            form.Show(); Application.DoEvents(); ((Timer)Field("timer")).Stop();
            Assert(form.FormBorderStyle == FormBorderStyle.Sizable && form.MaximizeBox, "Window must allow resizing and maximizing");
            int x = form.Left + 2, y = form.Top + form.Height / 2;
            Assert(SendMessage(form.Handle, 0x84, IntPtr.Zero, new IntPtr((y << 16) | (x & 0xffff))).ToInt32() == 10,
                "Native window edge is not a resize hit target");
            foreach (string language in new[] { "zh-CN", "en", "de" })
            {
                Call("ChangeLanguage", language);
                foreach (Size size in new[] { new Size(1140, 724), new Size(800, 520), new Size(640, 420), new Size(1140, 724) }) CheckSize(size);
                var sidebar = (Panel)Field("sidebarViewport");
                Assert(sidebar.VerticalScroll.Visible == (sidebar.ClientSize.Height < ((Panel)Field("sidebarContent")).Height)
                    && sidebar.AutoScrollPosition == Point.Empty, "Resizing leaves stale sidebar scrolling");
            }
            CheckWorkingAreas();
            form.Scale(new SizeF(1.5F, 1.5F));
            Call("FitWindowToScreen");
            CheckSize(new Size(1080, 650));
            CheckWorkingAreas();
            object config = Field("config");
            Assert(!(bool)config.GetType().GetField("Armed").GetValue(config), "Layout checks armed protection");
            Assert(!(bool)config.GetType().GetField("ConnectionConfirmed").GetValue(config), "Layout checks confirmed a real UPS");
            Console.WriteLine("PASS: native resize border, maximize, seven pages at narrow/large sizes, sidebar and action reachability, guide scrolling, three languages, 150% scaled geometry and display/work-area recovery; no network or power actions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (form != null) form.Dispose(); }
    }
}
