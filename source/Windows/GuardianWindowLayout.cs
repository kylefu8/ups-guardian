using System;
using System.Drawing;
using System.Windows.Forms;

namespace UpsGuardian
{
    sealed partial class GuardianForm
    {
        Panel sidebarViewport, sidebarContent, windowFooter;
        Label footerRefresh;
        bool windowLayoutReady, layingOutWindow;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!windowLayoutReady || layingOutWindow) return;
            layingOutWindow = true;
            try
            {
                // Preserve readable controls at the current DPI; smaller viewports
                // scroll content instead of cropping an oversized page.
                int sidebarWidth = sidebarContent.Width;
                if (ClientSize.Height < sidebarContent.Height)
                    sidebarWidth += SystemInformation.VerticalScrollBarWidth;
                sidebarViewport.Bounds = new Rectangle(0, 0, sidebarWidth, ClientSize.Height);
                int width = Math.Max(0, ClientSize.Width - sidebarWidth);
                int height = Math.Max(0, ClientSize.Height - windowFooter.Height);
                foreach (Panel page in pages)
                    page.Bounds = new Rectangle(sidebarWidth, 0, width, height);
                windowFooter.Bounds = new Rectangle(sidebarWidth, height, width, windowFooter.Height);
                int gap = Math.Max(1, detail.Left / 2);
                footerRefresh.Visible = width >= sidebarContent.Width * 3;
                footerRefresh.Left = Math.Max(0, width - footerRefresh.Width - gap);
                detail.Width = Math.Max(0, (footerRefresh.Visible ? footerRefresh.Left : width) - detail.Left - gap);
            }
            finally { layingOutWindow = false; }
        }

        void FitWindowToWorkingArea(Rectangle workArea)
        {
            if (!windowLayoutReady || workArea.Width <= 0 || workArea.Height <= 0) return;
            double scale = sidebarContent.Width / 248.0;
            MinimumSize = new Size(Math.Min(workArea.Width, (int)Math.Round(640 * scale)),
                Math.Min(workArea.Height, (int)Math.Round(420 * scale)));
            if (WindowState != FormWindowState.Normal) return;
            int width = Math.Min(Width, workArea.Width), height = Math.Min(Height, workArea.Height);
            Bounds = new Rectangle(Math.Max(workArea.Left, Math.Min(Left, workArea.Right - width)),
                Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - height)), width, height);
        }

        void FitWindowToScreen()
        {
            FitWindowToWorkingArea(Screen.FromControl(this).WorkingArea);
        }

        protected override void OnShown(EventArgs e)
        {
            FitWindowToScreen();
            base.OnShown(e);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // No static display-event subscription to outlive a disposed window.
            if (m.Msg == 0x007E || m.Msg == 0x001A) // WM_DISPLAYCHANGE / WM_SETTINGCHANGE
                Ui(FitWindowToScreen);
        }
    }
}
