using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace UpsGuardian
{
    sealed partial class GuardianForm
    {
        readonly Color canvas = Color.FromArgb(244, 247, 251), navy = Color.FromArgb(11, 23, 38);
        readonly Color teal = Color.FromArgb(13, 148, 136), line = Color.FromArgb(226, 232, 240);
        readonly Panel[] pages = new Panel[7];
        readonly NavButton[] navigation = new NavButton[7];
        readonly Label protectionDescription = new Label(), loadSummary = new Label(), modelName = new Label();
        readonly Label connectionBadge = new Label(), ruleSummary = new Label(), batteryRuleSummary = new Label();
        readonly Label saveState = new Label(), settingsSummary = new Label();
        readonly Label ratingWarning = new Label(), permissionLabel = new Label(), sourceLabel = new Label();
        readonly Label thresholdUnit = new Label(), marginUnit = new Label(), highRuleSummary = new Label(), powerActionSummary = new Label();
        Button advancedPolicyToggle, trendToggle;
        SurfacePanel advancedPolicyPanel, trendSurface;
        Panel policySaveBar;
        bool advancedPolicyExpanded, trendExpanded, importantNotice;
        readonly LoadChart chart = new LoadChart();
        readonly MiniMeter loadMeter = new MiniMeter(), batteryMeter = new MiniMeter();
        readonly ListView eventList = new ListView();
        readonly ToolTip tips = new ToolTip();
        SurfacePanel noticeSurface;
        Icon brandIcon;
        Image brandImage;
        int currentPage;
        bool viewDirty;
        DateTime plottedAt = DateTime.MinValue;

        void BuildInterface()
        {
            SuspendLayout();
            Text = "UPS 守护"; Font = new Font("Microsoft YaHei UI", 10F); ForeColor = ink; BackColor = canvas;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1140, 724); FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Guardian.Icon"))
                if (stream != null) { using (var icon = new Icon(stream)) brandIcon = (Icon)icon.Clone(); Icon = brandIcon; }
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Guardian.Image"))
                if (stream != null) { using (Image image = Image.FromStream(stream)) brandImage = new Bitmap(image); }

            var sidebar = new Panel { Dock = DockStyle.Left, Width = 248, BackColor = navy }; Controls.Add(sidebar);
            if (brandImage != null) sidebar.Controls.Add(new PictureBox { Image = brandImage, Bounds = new Rectangle(22, 28, 43, 43), SizeMode = PictureBoxSizeMode.Zoom });
            ViewLabel(sidebar, "UPS 守护", 76, 27, 168, 29, 16, true, Color.White);
            ViewLabel(sidebar, "本地电源保护", 77, 59, 168, 22, 8, false, Color.FromArgb(143, 162, 184));
            string[] names = { "概览", "保护策略", "连接设置", "事件记录", "使用说明", "版本更新", "支持开发" };
            for (int i = 0; i < names.Length; ++i)
            {
                int pageIndex = i;
                var nav = new NavButton { Text = names[i], Bounds = new Rectangle(14, 130 + i * 52, 220, 44), Symbol = i,
                    Font = new Font("Microsoft YaHei UI", 10.5F), ForeColor = Color.FromArgb(166, 182, 203), BackColor = navy,
                    AccessibleName = names[i], Cursor = Cursors.Hand, TabStop = true };
                nav.Click += delegate { Navigate(pageIndex); }; sidebar.Controls.Add(nav); navigation[i] = nav;
                pages[i] = new Panel { Bounds = new Rectangle(248, 0, 892, 686), BackColor = canvas, AutoScroll = true, Visible = i == 0 };
                Controls.Add(pages[i]);
            }
            ViewLabel(sidebar, "关闭窗口后仍在托盘运行", 24, 552, 210, 42, 8.5F, false, Color.FromArgb(143, 162, 184));
            var tuck = MakeButton(sidebar, "收起到托盘", 20, 630, 208, 34, Color.FromArgb(26, 43, 62), Color.FromArgb(213, 224, 238));
            tuck.Click += delegate { Hide(); };
            var quit = MakeButton(sidebar, "退出守护", 20, 674, 208, 30, navy, Color.FromArgb(143, 162, 184));
            quit.Click += delegate { Pause(true); };
            var footer = new Panel { Bounds = new Rectangle(248, 687, 892, 37), BackColor = Color.White }; Controls.Add(footer);
            detail.SetBounds(30, 9, 610, 23); detail.Font = new Font("Microsoft YaHei UI", 8.5F); detail.ForeColor = muted; footer.Controls.Add(detail);
            ViewLabel(footer, "确认后每 2 秒更新", 658, 9, 216, 23, 8.5F, false, muted).TextAlign = ContentAlignment.MiddleRight;

            BuildOverview(); BuildPolicies(); BuildConnection(); BuildEvents(); BuildExtraPages(sidebar);
            foreach (Control input in new Control[] { host, upsName, unit }) input.TextChanged += delegate { MarkViewDirty(); };
            foreach (var input in new[] { port, threshold, margin, cpu, gpu, charge, runtime, countdown }) input.ValueChanged += delegate { MarkViewDirty(); };
            autoStart.CheckedChanged += delegate { MarkViewDirty(); };
            unit.SelectedIndexChanged += delegate { if (!loading) { threshold.Value = unit.SelectedIndex == 1 ? 80 : 520; margin.Value = unit.SelectedIndex == 1 ? 10 : 65; } };
            arm.Click += delegate { if (!connectionReady) Navigate(2); else if (WindowsPowerActions.IsAdministrator()) Arm(); else RelaunchElevated(); };
            pause.Click += delegate { Pause(false); }; admin.Click += delegate { RelaunchElevated(); };
            admin.Enabled = !WindowsPowerActions.IsAdministrator();
            MakeLabelsTransparent(this); Navigate(0); ResumeLayout(false);
        }

        void BuildOverview()
        {
            Panel page = pages[0];
            PageHeading(page, "电力状态，一眼掌握", "UPS 整体负载与本机保护状态");
            cards[3] = ViewLabel(page, "连接中", 694, 30, 162, 31, 10, true, teal);
            cards[3].TextAlign = ContentAlignment.MiddleRight; cards[3].BringToFront();
            var guard = Surface(page, 30, 102, 832, 92);
            state.SetBounds(20, 16, 542, 29); state.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold); guard.Controls.Add(state);
            protectionDescription.SetBounds(22, 48, 540, 40); protectionDescription.Font = new Font("Microsoft YaHei UI", 9F); protectionDescription.ForeColor = muted; guard.Controls.Add(protectionDescription);
            StyleButton(arm, "启用自动保护", 592, 26, 219, 42, teal, Color.White); guard.Controls.Add(arm);
            StyleButton(pause, "暂停并恢复限制", 592, 26, 219, 42, red, Color.White); guard.Controls.Add(pause); pause.Visible = false;

            var power = Surface(page, 30, 212, 412, 177);
            ViewLabel(power, "UPS 总负载", 22, 17, 215, 24, 10, false, muted);
            sourceLabel.SetBounds(287, 17, 104, 24); sourceLabel.TextAlign = ContentAlignment.MiddleRight; sourceLabel.Font = new Font("Microsoft YaHei UI", 9F); sourceLabel.ForeColor = teal; power.Controls.Add(sourceLabel);
            cards[0] = ViewLabel(power, "—", 20, 49, 370, 65, 36, true, ink);
            loadSummary.SetBounds(23, 122, 360, 24); loadSummary.Font = new Font("Microsoft YaHei UI", 9F); loadSummary.ForeColor = muted; power.Controls.Add(loadSummary);
            loadMeter.Bounds = new Rectangle(23, 154, 366, 5); power.Controls.Add(loadMeter);
            var battery = Surface(page, 458, 212, 194, 177);
            ViewLabel(battery, "电池电量", 19, 18, 163, 25, 10, false, muted);
            cards[1] = ViewLabel(battery, "—", 17, 65, 164, 57, 30, true, ink);
            batteryMeter.Bounds = new Rectangle(20, 151, 154, 5); batteryMeter.HighIsDanger = false; battery.Controls.Add(batteryMeter);
            var remaining = Surface(page, 668, 212, 194, 177);
            ViewLabel(remaining, "预计续航", 19, 18, 162, 25, 10, false, muted);
            cards[2] = ViewLabel(remaining, "—", 17, 67, 168, 55, 24, true, ink);
            ViewLabel(remaining, "按 UPS 当前估算", 20, 136, 163, 27, 8.5F, false, muted);

            var device = Surface(page, 30, 407, 832, 116);
            ViewLabel(device, "连接的 UPS", 20, 15, 460, 25, 10, true, ink);
            modelName.SetBounds(20, 52, 540, 43); modelName.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold); modelName.ForeColor = ink; device.Controls.Add(modelName);
            connectionBadge.SetBounds(512, 15, 299, 28); connectionBadge.TextAlign = ContentAlignment.MiddleRight;
            connectionBadge.Font = new Font("Microsoft YaHei UI", 9F); connectionBadge.ForeColor = muted; device.Controls.Add(connectionBadge);
            MakeButton(device, "管理连接  →", 590, 61, 221, 34, Color.FromArgb(239, 246, 248), teal).Click += delegate { Navigate(2); };
            trendToggle = MakeButton(page, "显示负载曲线", 30, 539, 832, 34, Color.White, teal);
            trendToggle.Click += delegate { trendExpanded = !trendExpanded; LayoutOverview(); if (!trendExpanded) page.AutoScrollPosition = Point.Empty; };
            trendSurface = Surface(page, 30, 589, 832, 191);
            ViewLabel(trendSurface, "负载变化 · W", 20, 15, 202, 27, 11, true, ink);
            ViewLabel(trendSurface, "本次会话 · 最长 10 分钟", 470, 18, 339, 23, 8.5F, false, muted).TextAlign = ContentAlignment.MiddleRight;
            chart.Bounds = new Rectangle(17, 47, 798, 129); trendSurface.Controls.Add(chart);

            noticeSurface = Surface(page, 30, 589, 832, 56); noticeSurface.Fill = Color.FromArgb(255, 248, 231); noticeSurface.Border = Color.FromArgb(247, 224, 172);
            notice.SetBounds(17, 10, 646, 40); notice.ForeColor = Color.FromArgb(135, 93, 27); notice.Font = new Font("Microsoft YaHei UI", 9F); notice.AutoEllipsis = true; noticeSurface.Controls.Add(notice);
            MakeButton(noticeSurface, "查看策略  →", 686, 12, 126, 32, Color.FromArgb(255, 248, 231), Color.FromArgb(135, 93, 27)).Click += delegate { Navigate(1); };
        }

        void LayoutOverview()
        {
            if (trendSurface == null || noticeSurface == null) return;
            trendSurface.Visible = trendExpanded;
            trendToggle.Text = trendExpanded ? "收起负载曲线" : "显示负载曲线";
            int gap = Math.Max(1, (int)Math.Round(trendToggle.Height * 16.0 / 34));
            noticeSurface.Top = trendExpanded ? trendSurface.Bottom + gap : trendSurface.Top;
            noticeSurface.Visible = importantNotice;
        }

        void BuildPolicies()
        {
            Panel page = pages[1];
            PageHeading(page, "保护策略", "清楚地定义：何时触发、如何处理、何时恢复");
            rules.SetBounds(0, 96, 872, 579); rules.BackColor = canvas; page.Controls.Add(rules);
            var load = Surface(rules, 30, 0, 264, 176);
            ViewLabel(load, "负载阈值", 20, 18, 224, 30, 13, true, ink);
            Number(load, threshold, 24, 67, 138, 1, 10000, "降功耗触发阈值");
            thresholdUnit.SetBounds(178, 69, 57, 30); thresholdUnit.ForeColor = muted; load.Controls.Add(thresholdUnit);
            highRuleSummary.SetBounds(20, 114, 224, 50); highRuleSummary.Font = new Font("Microsoft YaHei UI", 9F); highRuleSummary.ForeColor = muted; load.Controls.Add(highRuleSummary);
            var battery = Surface(rules, 314, 0, 264, 176);
            ViewLabel(battery, "电量低于", 20, 18, 224, 30, 13, true, ink);
            Number(battery, charge, 24, 67, 138, 1, 100, "休眠电量阈值"); ViewLabel(battery, "%", 178, 69, 57, 30, 10, false, muted);
            var remaining = Surface(rules, 598, 0, 264, 176);
            ViewLabel(remaining, "或续航低于", 20, 18, 224, 30, 13, true, ink);
            Number(remaining, runtime, 24, 67, 138, 30, 3600, "休眠续航阈值秒"); ViewLabel(remaining, "秒", 178, 69, 57, 30, 10, false, muted);
            ViewLabel(battery, "仅电池供电时生效", 20, 114, 224, 50, 9, false, muted);
            ViewLabel(remaining, "仅电池供电时生效", 20, 114, 224, 50, 9, false, muted);

            var summary = Surface(rules, 30, 192, 832, 186);
            ViewLabel(summary, "启用后的动作", 22, 15, 780, 28, 13, true, ink);
            powerActionSummary.SetBounds(24, 49, 784, 36); powerActionSummary.Font = new Font("Microsoft YaHei UI", 9F); powerActionSummary.ForeColor = ink; summary.Controls.Add(powerActionSummary);
            ruleSummary.SetBounds(24, 91, 784, 35); ruleSummary.Font = new Font("Microsoft YaHei UI", 9F); ruleSummary.ForeColor = teal; summary.Controls.Add(ruleSummary);
            batteryRuleSummary.SetBounds(24, 132, 784, 45); batteryRuleSummary.Font = new Font("Microsoft YaHei UI", 9F); batteryRuleSummary.ForeColor = muted; summary.Controls.Add(batteryRuleSummary);
            ratingWarning.SetBounds(34, 390, 801, 44); ratingWarning.Font = new Font("Microsoft YaHei UI", 9F); ratingWarning.ForeColor = Color.FromArgb(166, 102, 28); rules.Controls.Add(ratingWarning);
            advancedPolicyToggle = MakeButton(rules, "高级设置", 30, 446, 832, 36, Color.White, teal);
            advancedPolicyToggle.Click += delegate { advancedPolicyExpanded = !advancedPolicyExpanded; LayoutPolicyOptions(); if (!advancedPolicyExpanded) page.AutoScrollPosition = Point.Empty; };
            advancedPolicyPanel = Surface(rules, 30, 494, 832, 204);
            ViewLabel(advancedPolicyPanel, "负载阈值单位", 24, 24, 155, 28, 10, false, muted);
            unit.SetBounds(190, 20, 198, 32); unit.DropDownStyle = ComboBoxStyle.DropDownList; unit.Items.AddRange(new object[] { "估算功率（W）", "负载率（%）" }); unit.AccessibleName = "负载阈值单位"; advancedPolicyPanel.Controls.Add(unit);
            ViewLabel(advancedPolicyPanel, "恢复回差", 430, 24, 155, 28, 10, false, muted); Number(advancedPolicyPanel, margin, 610, 20, 108, 1, 1000, "恢复回差");
            marginUnit.SetBounds(732, 24, 75, 28); marginUnit.ForeColor = muted; advancedPolicyPanel.Controls.Add(marginUnit);
            ViewLabel(advancedPolicyPanel, "CPU 最大状态", 24, 78, 155, 28, 10, false, muted); Number(advancedPolicyPanel, cpu, 190, 74, 108, 1, 100, "CPU 最大处理器状态"); ViewLabel(advancedPolicyPanel, "%", 310, 78, 70, 28, 10, false, muted);
            ViewLabel(advancedPolicyPanel, "GPU 功率上限", 430, 78, 155, 28, 10, false, muted); Number(advancedPolicyPanel, gpu, 610, 74, 108, 0, 5000, "GPU 功率上限瓦数"); ViewLabel(advancedPolicyPanel, "W", 732, 78, 70, 28, 10, false, muted);
            gpu.Enabled = capabilities.GpuLimitSupported;
            ViewLabel(advancedPolicyPanel, "休眠倒计时", 24, 132, 155, 28, 10, false, muted); Number(advancedPolicyPanel, countdown, 190, 128, 108, 0, 120, "休眠倒计时秒"); ViewLabel(advancedPolicyPanel, "秒", 310, 132, 70, 28, 10, false, muted);
            ViewLabel(advancedPolicyPanel, "Windows 最大处理器状态，不等于 CPU 使用率或瓦数上限。", 430, 129, 378, 61, 9, false, muted);
            policySaveBar = new Panel { Bounds = new Rectangle(30, 494, 832, 52), BackColor = canvas }; rules.Controls.Add(policySaveBar);
            saveState.SetBounds(5, 9, 590, 38); saveState.Font = new Font("Microsoft YaHei UI", 9F); saveState.ForeColor = muted; policySaveBar.Controls.Add(saveState);
            MakeButton(policySaveBar, "保存保护策略", 617, 0, 215, 42, teal, Color.White).Click += delegate { SaveControls(false); };
            LayoutPolicyOptions();
            tips.SetToolTip(cpu, Localization.T("Windows 最大处理器状态，不等于 CPU 使用率或瓦数上限。"));
            tips.SetToolTip(margin, Localization.T("恢复阈值 = 触发阈值 − 回差。负载低于恢复阈值持续 30 秒后恢复原设置。"));
            tips.SetToolTip(gpu, capabilities.GpuLimitSupported ? Localization.F("GPU 功率范围为 {0}–{1}W；0 表示不控制 GPU。", capabilities.GpuMinimumWatts, capabilities.GpuMaximumWatts) : Localization.T("GPU 控制不可用，将只限制 CPU。"));
        }

        void LayoutPolicyOptions()
        {
            if (advancedPolicyPanel == null || policySaveBar == null) return;
            advancedPolicyPanel.Visible = advancedPolicyExpanded;
            advancedPolicyToggle.Text = advancedPolicyExpanded ? "收起高级设置" : "高级设置";
            int gap = Math.Max(1, (int)Math.Round(advancedPolicyToggle.Height * 12.0 / 36));
            policySaveBar.Top = advancedPolicyExpanded ? advancedPolicyPanel.Bottom + gap : advancedPolicyPanel.Top;
            rules.Height = policySaveBar.Bottom + gap;
        }

        void BuildEvents()
        {
            Panel page = pages[3]; PageHeading(page, "事件记录", "了解连接、保护动作与恢复过程");
            var panel = Surface(page, 30, 103, 832, 510);
            eventList.SetBounds(15, 15, 802, 480); eventList.View = View.Details; eventList.FullRowSelect = true;
            eventList.GridLines = false; eventList.BorderStyle = BorderStyle.None; eventList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            eventList.Font = new Font("Microsoft YaHei UI", 9F); eventList.ForeColor = ink; eventList.BackColor = Color.White;
            eventList.Columns.Add("时间", 171); eventList.Columns.Add("事件", 607); eventList.AccessibleName = "最近的守护事件"; eventList.ShowItemToolTips = true; panel.Controls.Add(eventList);
            ViewLabel(page, "显示最近 100 条记录；完整日志保存在本机。", 33, 634, 590, 30, 9, false, muted);
            MakeButton(page, "打开日志文件夹", 659, 629, 203, 39, Color.White, ink).Click += delegate { Process.Start("explorer.exe", dataDirectory); };
        }

        void PageHeading(Control page, string title, string subtitle)
        { ViewLabel(page, title, 30, 24, 630, 40, 23, true, ink); ViewLabel(page, subtitle, 32, 70, 825, 27, 9.5F, false, muted); }
        void MakeLabelsTransparent(Control root)
        { foreach (Control child in root.Controls) { if (child is Label) child.BackColor = Color.Transparent; if (child.HasChildren) MakeLabelsTransparent(child); } }
        Label ViewLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            var label = new Label { Bounds = new Rectangle(x, y, w, h), Text = text, ForeColor = color, BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true };
            parent.Controls.Add(label); return label;
        }
        SurfacePanel Surface(Control parent, int x, int y, int w, int h)
        { var panel = new SurfacePanel { Bounds = new Rectangle(x, y, w, h), BackColor = Color.White }; parent.Controls.Add(panel); return panel; }
        Button MakeButton(Control parent, string text, int x, int y, int w, int h, Color background, Color color)
        { var button = new ModernButton(); StyleButton(button, text, x, y, w, h, background, color); parent.Controls.Add(button); return button; }
        void Navigate(int index)
        {
            currentPage = index;
            for (int i = 0; i < pages.Length; ++i) { pages[i].Visible = i == index; navigation[i].Selected = i == index; navigation[i].Invalidate(); }
            if (index == 3) RefreshEvents();
        }
        void MarkViewDirty()
        { if (!loading) { viewDirty = true; UpdateRuleDescriptions(); } }
        void OnSettingsSaved()
        { viewDirty = false; UpdateRuleDescriptions(); }
        void UpdateRuleDescriptions()
        {
            string suffix = unit.SelectedIndex == 1 ? "%" : "W";
            thresholdUnit.Text = marginUnit.Text = suffix;
            highRuleSummary.Text = Localization.F("持续 {0} 秒后降低本机功耗。", config.HighConfirmSeconds);
            powerActionSummary.Text = PowerLimitSummary(cpu.Value, gpu.Value);
            ruleSummary.Text = Localization.F("低于 {0}{1} 持续 {2} 秒后恢复原设置。", threshold.Value - margin.Value, suffix, config.RecoverySeconds);
            batteryRuleSummary.Text = Localization.F("电池条件持续 {0} 秒，倒计时 {1} 秒后休眠；来电、条件解除或数据失效时取消。", config.LowConfirmSeconds, countdown.Value);
            saveState.Text = viewDirty ? "● 有未保存的更改" : "设置已保存 · 启用保护期间需先暂停再修改";
            saveState.ForeColor = viewDirty ? teal : muted;
            settingsSummary.Text = viewDirty ? "有未保存的更改" : "连接设置已保存";
            double? nominal = sample != null ? sample.NominalWatts : null;
            bool excessive = unit.SelectedIndex == 1 ? threshold.Value > 100 : (nominal.HasValue && (double)threshold.Value > nominal.Value);
            ratingWarning.Text = excessive ? "注意：当前触发阈值高于 UPS 额定输出，可能在过载后才动作。请先确认或调整。" :
                (!nominal.HasValue && unit.SelectedIndex == 0 ? "UPS 未提供额定功率，可使用负载率设置阈值。" : "功率为估算值；该规则只能降低本机功耗，不影响 UPS 上的其他设备。");
            LayoutPolicyOptions(); LayoutOverview();
            tips.SetToolTip(powerActionSummary, powerActionSummary.Text);
            tips.SetToolTip(batteryRuleSummary, batteryRuleSummary.Text);
            tips.SetToolTip(margin, ruleSummary.Text);
        }
        string PowerLimitSummary(decimal cpuLimit, decimal gpuLimit)
        {
            string gpuText = gpuLimit == 0 ? Localization.T("不控制 GPU") : Localization.F("GPU 上限 {0}W", gpuLimit);
            return Localization.F("降功耗：CPU 最大状态上限 {0}%；{1}。", cpuLimit, gpuText);
        }
        void UpdatePresentation(GuardDecision decision)
        {
            bool elevated = WindowsPowerActions.IsAdministrator();
            bool showPause = config.Armed || busy || (actions != null && actions.HasRecovery);
            arm.Visible = !showPause; pause.Visible = showPause;
            arm.Text = !connectionReady ? "发现并确认 UPS" : (elevated ? "启用自动保护" : "以管理员身份打开");
            protectionDescription.Text = !connectionReady ? Localization.T("确认唯一 UPS 后才开始监测，保护仍需手动开启。") :
                PowerLimitSummary(config.CpuMaximum, config.GpuWatts) + " " + Localization.T("电池不足时按策略休眠。");
            tips.SetToolTip(protectionDescription, protectionDescription.Text);
            permissionLabel.Text = elevated ? (connectionReady ? "当前：管理员权限，可以启用自动保护。" : "当前：管理员权限，确认 UPS 后可启用保护。") : "当前：普通权限，可以连接 UPS 并查看数据。";
            admin.Visible = !elevated; admin.Enabled = !discoveryBusy && !busy && !config.Armed;
            cards[3].ForeColor = decision.Fresh ? (sample != null && sample.OnBattery ? Color.FromArgb(173, 104, 14) : teal) : muted;
            if (!decision.Fresh) cards[3].Text = !connectionReady ? "等待确认 UPS" : (sample == null ? "连接中" : "数据已失效");
            if (sample != null)
            {
                sourceLabel.Text = sample.MeasuredWatts.HasValue ? "实测" : "估算";
                sourceLabel.ForeColor = decision.Fresh ? teal : muted;
                loadSummary.Text = Localization.F("当前负载 {0} · 额定 {1}", sample.LoadPercent.HasValue ? sample.LoadPercent.Value.ToString("F0") + "%" : Localization.T("未知"), sample.NominalWatts.HasValue ? sample.NominalWatts.Value.ToString("F0") + "W" : Localization.T("未知"));
                loadMeter.Value = sample.LoadPercent ?? 0; batteryMeter.Value = sample.ChargePercent ?? 0;
                modelName.Text = sample.Model ?? "UPS 设备"; connectionBadge.Text = "● " + Localization.F(decision.Fresh ? "已连接 {0}" : "数据失效 {0}", config.Host);
                connectionBadge.ForeColor = decision.Fresh ? teal : muted;
                for (int i = 0; i < 3; ++i) cards[i].ForeColor = decision.Fresh ? ink : muted;
                double? power = sample.MeasuredWatts ?? sample.EstimatedWatts;
                if (sample.ReceivedUtc > plottedAt && decision.Fresh && power.HasValue)
                { chart.Add(sample.ReceivedUtc, power.Value, sample.NominalWatts ?? 0); plottedAt = sample.ReceivedUtc; }
            }
            else
            {
                modelName.Text = "等待 UPS 数据"; connectionBadge.Text = config.Host; connectionBadge.ForeColor = muted;
                loadSummary.Text = "正在读取整体负载"; sourceLabel.Text = "—"; sourceLabel.ForeColor = muted;
                loadMeter.Value = batteryMeter.Value = 0;
                for (int i = 0; i < 3; ++i) { cards[i].Text = "—"; cards[i].ForeColor = muted; }
            }
            tips.SetToolTip(notice, notice.Text);
            UpdateRuleDescriptions();
        }
        void RefreshEvents()
        {
            if (eventList.Columns.Count == 0) return;
            string path = Path.Combine(dataDirectory, "events.log");
            try
            {
                if (!File.Exists(path)) return;
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                { long offset = Math.Max(0, stream.Length - 65536); stream.Seek(offset, SeekOrigin.Begin); using (var reader = new StreamReader(stream)) { if (offset > 0) reader.ReadLine(); text = reader.ReadToEnd(); } }
                string[] rows = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                eventList.BeginUpdate(); eventList.Items.Clear();
                foreach (string row in rows.Reverse().Take(100))
                {
                    string time = row.Length >= 19 ? row.Substring(0, 19) : "";
                    string message = row.Length > 20 ? row.Substring(20) : row;
                    string display = Localization.T(message); var item = new ListViewItem(new[] { time, display }) { ToolTipText = display }; eventList.Items.Add(item);
                }
                eventList.EndUpdate();
            }
            catch (IOException) { }
        }
    }

    static class ViewDrawing
    {
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)); var p = new GraphicsPath();
            p.AddArc(rect.Left, rect.Top, d, d, 180, 90); p.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); p.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
    }
    sealed class SurfacePanel : Panel
    {
        public Color Fill = Color.White, Border = Color.FromArgb(229, 235, 242);
        public SurfacePanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ViewDrawing.Round(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 13 * e.Graphics.DpiX / 96F))
            using (var fill = new SolidBrush(Fill)) using (var pen = new Pen(Border)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path); }
        }
    }
    class ModernButton : Button
    {
        bool hover, pressed;
        public ModernButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            SurfacePanel surface = Parent as SurfacePanel;
            e.Graphics.Clear(surface != null ? surface.Fill : (Parent == null ? Color.White : Parent.BackColor)); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Enabled ? BackColor : Color.FromArgb(230, 235, 241);
            if (Enabled && (pressed || hover)) fill = ControlPaint.Dark(fill, pressed ? 0.12F : 0.04F);
            using (var path = ViewDrawing.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8 * e.Graphics.DpiX / 96F)) using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(133, 148, 165), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
    sealed class LanguageMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(26, 43, 62); } }
        public override Color MenuBorder { get { return Color.FromArgb(49, 68, 83); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(37, 68, 85); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(37, 68, 85); } }
        public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
        public override Color CheckBackground { get { return Color.FromArgb(13, 148, 136); } }
        public override Color CheckSelectedBackground { get { return CheckBackground; } }
        public override Color SeparatorDark { get { return MenuBorder; } }
        public override Color SeparatorLight { get { return ToolStripDropDownBackground; } }
    }
    sealed class LanguageButton : ModernButton
    {
        bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float s = e.Graphics.DpiX / 96F, cy = Height / 2F;
            using (var path = ViewDrawing.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8 * s))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(37, 68, 85) : BackColor)) e.Graphics.FillPath(fill, path);
            using (var pen = new Pen(ForeColor, 1.2F * s))
            {
                e.Graphics.DrawEllipse(pen, 10 * s, cy - 8 * s, 16 * s, 16 * s);
                e.Graphics.DrawEllipse(pen, 14 * s, cy - 8 * s, 8 * s, 16 * s);
                e.Graphics.DrawLine(pen, 10 * s, cy, 26 * s, cy);
                e.Graphics.DrawLines(pen, new[] { new PointF(Width - 15 * s, cy + 2 * s), new PointF(Width - 11 * s, cy - 2 * s), new PointF(Width - 7 * s, cy + 2 * s) });
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(32 * s), 0, Width - (int)(51 * s), Height),
                ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
    sealed class NavButton : ModernButton
    {
        public bool Selected; public int Symbol;
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(11, 23, 38)); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float s = e.Graphics.DpiX / 96F;
            if (Selected) using (var path = ViewDrawing.Round(new RectangleF(0, 0, Width - 1, Height - 1), 9 * s)) using (var fill = new SolidBrush(Color.FromArgb(25, 48, 64))) e.Graphics.FillPath(fill, path);
            Color color = Selected ? Color.FromArgb(124, 228, 211) : ForeColor;
            using (var pen = new Pen(color, 1.6F * s))
            {
                float x = 17 * s, y = Height / 2F - 8 * s;
                if (Symbol == 0) { for (int i = 0; i < 4; ++i) e.Graphics.DrawRectangle(pen, x + i % 2 * 10 * s, y + i / 2 * 10 * s, 6 * s, 6 * s); }
                else if (Symbol == 1) { e.Graphics.DrawPolygon(pen, new[] { new PointF(x, y), new PointF(x + 16 * s, y), new PointF(x + 14 * s, y + 12 * s), new PointF(x + 8 * s, y + 18 * s), new PointF(x + 2 * s, y + 12 * s) }); }
                else if (Symbol == 2) { for (int i = 0; i < 3; ++i) { e.Graphics.DrawLine(pen, x, y + i * 7 * s, x + 17 * s, y + i * 7 * s); e.Graphics.DrawEllipse(pen, x + (i % 2 == 0 ? 4 : 10) * s, y + i * 7 * s - 2 * s, 4 * s, 4 * s); } }
                else if (Symbol == 3)
                {
                    e.Graphics.DrawRectangle(pen, x + s, y, 16 * s, 18 * s);
                    for (int i = 0; i < 3; ++i)
                        e.Graphics.DrawLine(pen, x + 5 * s, y + (5 + i * 4) * s, x + 13 * s, y + (5 + i * 4) * s);
                }
                else if (Symbol == 4)
                {
                    using (var book = new GraphicsPath())
                    {
                        book.AddBezier(x, y + 2 * s, x + 4 * s, y, x + 7 * s, y + s, x + 9 * s, y + 3 * s);
                        book.AddBezier(x + 9 * s, y + 3 * s, x + 11 * s, y + s, x + 14 * s, y, x + 18 * s, y + 2 * s);
                        book.AddLine(x + 18 * s, y + 2 * s, x + 18 * s, y + 17 * s);
                        book.AddBezier(x + 18 * s, y + 17 * s, x + 14 * s, y + 15 * s, x + 11 * s, y + 16 * s, x + 9 * s, y + 18 * s);
                        book.AddBezier(x + 9 * s, y + 18 * s, x + 7 * s, y + 16 * s, x + 4 * s, y + 15 * s, x, y + 17 * s);
                        book.CloseFigure(); e.Graphics.DrawPath(pen, book);
                    }
                    e.Graphics.DrawLine(pen, x + 9 * s, y + 3 * s, x + 9 * s, y + 18 * s);
                }
                else if (Symbol == 5)
                {
                    e.Graphics.DrawLine(pen, x + 9 * s, y, x + 9 * s, y + 13 * s);
                    e.Graphics.DrawLines(pen, new[] { new PointF(x + 4 * s, y + 8 * s), new PointF(x + 9 * s, y + 13 * s), new PointF(x + 14 * s, y + 8 * s) });
                    e.Graphics.DrawLines(pen, new[] { new PointF(x + s, y + 14 * s), new PointF(x + s, y + 18 * s), new PointF(x + 17 * s, y + 18 * s), new PointF(x + 17 * s, y + 14 * s) });
                }
                else if (Symbol == 6)
                {
                    using (var heart = new GraphicsPath())
                    {
                        heart.AddBezier(x + 9 * s, y + 18 * s, x + 6 * s, y + 15 * s, x, y + 10 * s, x, y + 5 * s);
                        heart.AddBezier(x, y + 5 * s, x, y, x + 6 * s, y - s, x + 9 * s, y + 4 * s);
                        heart.AddBezier(x + 9 * s, y + 4 * s, x + 12 * s, y - s, x + 18 * s, y, x + 18 * s, y + 5 * s);
                        heart.AddBezier(x + 18 * s, y + 5 * s, x + 18 * s, y + 10 * s, x + 12 * s, y + 15 * s, x + 9 * s, y + 18 * s);
                        heart.CloseFigure(); e.Graphics.DrawPath(pen, heart);
                    }
                }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(47 * s), 0, Width - (int)(50 * s), Height), Selected ? Color.White : ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
    sealed class MiniMeter : Control
    {
        double value;
        public bool HighIsDanger = true;
        public double Value { get { return value; } set { this.value = value; Invalidate(); } }
        public MiniMeter() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(230, 239, 241));
            using (var brush = new SolidBrush((HighIsDanger ? value > 90 : value < 50) ? Color.FromArgb(190, 133, 38) : Color.FromArgb(20, 184, 166)))
                e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * Math.Max(0, Math.Min(100, value)) / 100), Height);
        }
    }
    sealed class LoadChart : Control
    {
        sealed class Reading { public DateTime Time; public double Watts; }
        readonly List<Reading> readings = new List<Reading>(); double nominal;
        public LoadChart() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        public void Reset() { readings.Clear(); Invalidate(); }
        public void Add(DateTime time, double watts, double rating)
        { nominal = rating; readings.Add(new Reading { Time = time, Watts = watts }); readings.RemoveAll(r => r.Time < time.AddMinutes(-10)); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = g.DpiX / 96F;
            RectangleF plot = new RectangleF(39 * s, 6 * s, Width - 49 * s, Height - 32 * s);
            if (plot.Width < 10 || plot.Height < 10) return;
            using (var small = new Font("Segoe UI", 8F)) using (var muted = new SolidBrush(Color.FromArgb(136, 152, 168))) using (var grid = new Pen(Color.FromArgb(235, 241, 246)))
            {
                double max = Math.Max(1, Math.Max(nominal, readings.Count == 0 ? nominal : readings.Max(r => r.Watts) * 1.12));
                for (int i = 0; i < 3; ++i) { float y = plot.Top + i * plot.Height / 2; g.DrawLine(grid, plot.Left, y, plot.Right, y); g.DrawString((max * (1 - i / 2.0)).ToString("F0"), small, muted, 0, y - 5 * s); }
                if (readings.Count < 2) { g.DrawString(Localization.T("等待更多负载数据…"), Font, muted, plot.Left + 25 * s, plot.Top + plot.Height / 2 - 8 * s); return; }
                DateTime end = readings[readings.Count - 1].Time;
                double duration = Math.Max(30, Math.Min(600, (end - readings[0].Time).TotalSeconds)); DateTime begin = end.AddSeconds(-duration);
                PointF[] points = readings.Select(r => new PointF(plot.Left + (float)((r.Time - begin).TotalSeconds / duration) * plot.Width, plot.Bottom - (float)(r.Watts / max) * plot.Height)).ToArray();
                using (var area = new GraphicsPath())
                { area.AddLines(points); area.AddLine(points[points.Length - 1], new PointF(points[points.Length - 1].X, plot.Bottom)); area.AddLine(new PointF(points[0].X, plot.Bottom), points[0]); area.CloseFigure(); using (var fill = new LinearGradientBrush(plot, Color.FromArgb(56, 20, 184, 166), Color.FromArgb(1, 20, 184, 166), LinearGradientMode.Vertical)) g.FillPath(fill, area); }
                using (var pen = new Pen(Color.FromArgb(15, 158, 143), 2 * s)) g.DrawLines(pen, points);
                g.DrawString(begin.ToLocalTime().ToString("HH:mm:ss"), small, muted, plot.Left, plot.Bottom + 6 * s);
                string last = end.ToLocalTime().ToString("HH:mm:ss"); SizeF width = g.MeasureString(last, small); g.DrawString(last, small, muted, plot.Right - width.Width, plot.Bottom + 6 * s);
            }
        }
    }
}
