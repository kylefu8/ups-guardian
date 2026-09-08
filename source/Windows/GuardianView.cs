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
            ViewLabel(sidebar, "后台持续监测", 24, 540, 204, 26, 10, true, Color.FromArgb(123, 210, 196));
            ViewLabel(sidebar, "关闭窗口后仍在托盘运行", 24, 572, 210, 42, 8.5F, false, Color.FromArgb(143, 162, 184));
            var tuck = MakeButton(sidebar, "收起到托盘", 20, 630, 208, 34, Color.FromArgb(26, 43, 62), Color.FromArgb(213, 224, 238));
            tuck.Click += delegate { Hide(); };
            var quit = MakeButton(sidebar, "退出守护", 20, 674, 208, 30, navy, Color.FromArgb(143, 162, 184));
            quit.Click += delegate { Pause(true); };
            var footer = new Panel { Bounds = new Rectangle(248, 687, 892, 37), BackColor = Color.White }; Controls.Add(footer);
            detail.SetBounds(30, 9, 610, 23); detail.Font = new Font("Microsoft YaHei UI", 8.5F); detail.ForeColor = muted; footer.Controls.Add(detail);
            ViewLabel(footer, "每 2 秒更新", 658, 9, 216, 23, 8.5F, false, muted).TextAlign = ContentAlignment.MiddleRight;

            BuildOverview(); BuildPolicies(); BuildConnection(); BuildEvents(); BuildExtraPages(sidebar);
            foreach (Control input in new Control[] { host, upsName, unit }) input.TextChanged += delegate { MarkViewDirty(); };
            foreach (var input in new[] { port, threshold, margin, cpu, gpu, charge, runtime, countdown }) input.ValueChanged += delegate { MarkViewDirty(); };
            autoStart.CheckedChanged += delegate { MarkViewDirty(); };
            unit.SelectedIndexChanged += delegate { if (!loading) { threshold.Value = unit.SelectedIndex == 1 ? 80 : 520; margin.Value = unit.SelectedIndex == 1 ? 10 : 65; } };
            arm.Click += delegate { if (string.IsNullOrWhiteSpace(config.Host)) Navigate(2); else if (WindowsPowerActions.IsAdministrator()) Arm(); else RelaunchElevated(); };
            pause.Click += delegate { Pause(false); }; admin.Click += delegate { RelaunchElevated(); };
            admin.Enabled = !WindowsPowerActions.IsAdministrator();
            FormClosed += delegate { tips.Dispose(); if (brandImage != null) brandImage.Dispose(); if (brandIcon != null) brandIcon.Dispose(); };
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
            protectionDescription.SetBounds(22, 53, 540, 26); protectionDescription.Font = new Font("Microsoft YaHei UI", 9F); protectionDescription.ForeColor = muted; guard.Controls.Add(protectionDescription);
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

            var graph = Surface(page, 30, 407, 522, 191);
            ViewLabel(graph, "负载变化 · W", 20, 15, 202, 27, 11, true, ink);
            ViewLabel(graph, "本次会话 · 最长 10 分钟", 255, 18, 246, 23, 8.5F, false, muted).TextAlign = ContentAlignment.MiddleRight;
            chart.Bounds = new Rectangle(17, 47, 488, 129); graph.Controls.Add(chart);
            var device = Surface(page, 570, 407, 292, 191);
            ViewLabel(device, "连接的 UPS", 19, 15, 258, 25, 10, true, ink);
            modelName.SetBounds(20, 51, 251, 46); modelName.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold); modelName.ForeColor = ink; device.Controls.Add(modelName);
            connectionBadge.SetBounds(20, 104, 251, 28); connectionBadge.Font = new Font("Microsoft YaHei UI", 9F); connectionBadge.ForeColor = muted; device.Controls.Add(connectionBadge);
            MakeButton(device, "管理连接  →", 16, 143, 258, 31, Color.FromArgb(239, 246, 248), teal).Click += delegate { Navigate(2); };

            noticeSurface = Surface(page, 30, 616, 832, 56); noticeSurface.Fill = Color.FromArgb(255, 248, 231); noticeSurface.Border = Color.FromArgb(247, 224, 172);
            notice.SetBounds(17, 10, 646, 40); notice.ForeColor = Color.FromArgb(135, 93, 27); notice.Font = new Font("Microsoft YaHei UI", 9F); notice.AutoEllipsis = true; noticeSurface.Controls.Add(notice);
            MakeButton(noticeSurface, "查看策略  →", 686, 12, 126, 32, Color.FromArgb(255, 248, 231), Color.FromArgb(135, 93, 27)).Click += delegate { Navigate(1); };
        }

        void BuildPolicies()
        {
            Panel page = pages[1];
            PageHeading(page, "保护策略", "清楚地定义：何时触发、如何处理、何时恢复");
            rules.SetBounds(0, 96, 892, 579); rules.BackColor = canvas; page.Controls.Add(rules);
            var load = Surface(rules, 30, 0, 832, 246);
            ViewLabel(load, "01  负载过高时，降低本机功耗", 22, 17, 770, 31, 13, true, ink);
            ViewLabel(load, "UPS 整体负载超过阈值，持续 5 秒后执行。", 23, 55, 782, 26, 9, false, muted);
            ViewLabel(load, "触发阈值", 24, 98, 90, 28, 10, false, muted);
            unit.SetBounds(125, 94, 160, 32); unit.DropDownStyle = ComboBoxStyle.DropDownList; unit.Items.AddRange(new object[] { "估算功率（W）", "负载率（%）" }); unit.AccessibleName = "负载阈值单位"; load.Controls.Add(unit);
            Number(load, threshold, 300, 94, 105, 1, 10000, "降功耗触发阈值");
            ViewLabel(load, "恢复回差", 464, 98, 97, 28, 10, false, muted); Number(load, margin, 570, 94, 105, 1, 1000, "恢复回差");
            ViewLabel(load, "与阈值同单位", 690, 99, 128, 27, 8.5F, false, muted);
            ViewLabel(load, "CPU 最大状态", 24, 151, 133, 28, 10, false, muted); Number(load, cpu, 171, 148, 89, 1, 100, "CPU 最大处理器状态"); ViewLabel(load, "%", 269, 153, 30, 26, 10, false, muted);
            ViewLabel(load, "GPU 功率上限", 464, 151, 133, 28, 10, false, muted); Number(load, gpu, 609, 148, 92, 0, 5000, "GPU 功率上限瓦数"); ViewLabel(load, "W", 710, 153, 40, 26, 10, false, muted);
            gpu.Enabled = capabilities.GpuLimitSupported;
            ruleSummary.SetBounds(24, 201, 776, 29); ruleSummary.Font = new Font("Microsoft YaHei UI", 9F); ruleSummary.ForeColor = teal; load.Controls.Add(ruleSummary);
            var low = Surface(rules, 30, 262, 832, 199);
            ViewLabel(low, "02  电池不足时，保留会话并休眠", 22, 17, 771, 31, 13, true, ink);
            ViewLabel(low, "仅在 UPS 使用电池供电时，满足以下任意一个条件。", 23, 55, 782, 26, 9, false, muted);
            ViewLabel(low, "电量低于", 24, 99, 94, 28, 10, false, muted); Number(low, charge, 125, 95, 83, 1, 100, "休眠电量阈值"); ViewLabel(low, "%", 218, 100, 30, 25, 10, false, muted);
            ViewLabel(low, "或续航低于", 279, 99, 113, 28, 10, false, muted); Number(low, runtime, 397, 95, 92, 30, 3600, "休眠续航阈值秒"); ViewLabel(low, "秒", 499, 100, 30, 25, 10, false, muted);
            ViewLabel(low, "休眠倒计时", 561, 99, 114, 28, 10, false, muted); Number(low, countdown, 681, 95, 80, 0, 120, "休眠倒计时秒"); ViewLabel(low, "秒", 771, 100, 30, 25, 10, false, muted);
            batteryRuleSummary.SetBounds(24, 151, 783, 30); batteryRuleSummary.Font = new Font("Microsoft YaHei UI", 9F); batteryRuleSummary.ForeColor = muted; low.Controls.Add(batteryRuleSummary);
            ratingWarning.SetBounds(34, 475, 801, 42); ratingWarning.Font = new Font("Microsoft YaHei UI", 9F); ratingWarning.ForeColor = Color.FromArgb(166, 102, 28); rules.Controls.Add(ratingWarning);
            saveState.SetBounds(35, 535, 470, 28); saveState.Font = new Font("Microsoft YaHei UI", 9F); saveState.ForeColor = muted; rules.Controls.Add(saveState);
            MakeButton(rules, "保存保护策略", 647, 525, 215, 42, teal, Color.White).Click += delegate { SaveControls(false); };
            tips.SetToolTip(cpu, Localization.T("Windows 最大处理器状态，不等于 CPU 使用率或瓦数上限。"));
            tips.SetToolTip(margin, Localization.T("恢复阈值 = 触发阈值 − 回差。负载低于恢复阈值持续 30 秒后恢复原设置。"));
            tips.SetToolTip(gpu, capabilities.GpuLimitSupported ? Localization.F("GPU 功率范围为 {0}–{1}W；0 表示不控制 GPU。", capabilities.GpuMinimumWatts, capabilities.GpuMaximumWatts) : Localization.T("GPU 控制不可用，将只限制 CPU。"));
        }

        void BuildConnection()
        {
            Panel page = pages[2]; PageHeading(page, "连接与启动", "设备连接、后台运行和系统权限");
            connection.SetBounds(0, 96, 892, 579); connection.BackColor = canvas; page.Controls.Add(connection);
            var endpoint = Surface(connection, 30, 0, 832, 185);
            ViewLabel(endpoint, "UPS 服务器", 23, 18, 782, 28, 13, true, ink);
            ViewLabel(endpoint, "通过 NUT 读取数据，不修改 UPS 的配置。", 24, 55, 780, 27, 9, false, muted);
            ViewLabel(endpoint, "服务器地址", 24, 103, 170, 24, 9, false, muted); host.SetBounds(24, 132, 329, 31); host.AccessibleName = "UPS 服务器"; endpoint.Controls.Add(host);
            ViewLabel(endpoint, "端口", 378, 103, 134, 24, 9, false, muted); Number(endpoint, port, 378, 132, 137, 1, 65535, "NUT 端口");
            ViewLabel(endpoint, "设备名称", 542, 103, 240, 24, 9, false, muted); upsName.SetBounds(542, 132, 265, 31); upsName.AccessibleName = "UPS 设备名称"; endpoint.Controls.Add(upsName);
            var behavior = Surface(connection, 30, 201, 832, 129);
            ViewLabel(behavior, "后台运行", 23, 16, 780, 30, 13, true, ink);
            autoStart.SetBounds(24, 58, 776, 28); autoStart.Text = "登录 Windows 后自动启动"; autoStart.BackColor = Color.White; behavior.Controls.Add(autoStart);
            ViewLabel(behavior, "关闭窗口只收起到托盘。自启任务需管理员设置，初始关闭。", 25, 94, 777, 27, 9, false, muted);
            var permissions = Surface(connection, 30, 347, 832, 161);
            ViewLabel(permissions, "权限", 23, 17, 784, 28, 13, true, ink);
            permissionLabel.SetBounds(24, 54, 775, 29); permissionLabel.Font = new Font("Microsoft YaHei UI", 10F); permissionLabel.ForeColor = ink; permissions.Controls.Add(permissionLabel);
            ViewLabel(permissions, "当前版本将降功耗和休眠放在同一个保护开关内；启用该开关需管理员运行。", 24, 91, 775, 22, 9, false, muted);
            StyleButton(admin, "以管理员身份重新打开", 535, 119, 272, 31, Color.FromArgb(234, 244, 246), teal); permissions.Controls.Add(admin);
            settingsSummary.SetBounds(35, 540, 555, 27); settingsSummary.Font = new Font("Microsoft YaHei UI", 9F); settingsSummary.ForeColor = muted; connection.Controls.Add(settingsSummary);
            MakeButton(connection, "保存并重新连接", 647, 527, 215, 42, teal, Color.White).Click += delegate { SaveControls(true); };
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
            ruleSummary.Text = "负载回落到 " + (threshold.Value - margin.Value) + suffix + " 以下并持续 30 秒后，恢复本机原设置。";
            batteryRuleSummary.Text = "条件持续 5 秒后倒计时。来电、条件解除或数据失效时取消休眠。";
            saveState.Text = viewDirty ? "● 有未保存的更改" : "设置已保存 · 启用保护期间需先暂停再修改";
            saveState.ForeColor = viewDirty ? teal : muted;
            settingsSummary.Text = viewDirty ? "有未保存的更改" : "连接设置已保存";
            double? nominal = sample != null ? sample.NominalWatts : null;
            bool excessive = unit.SelectedIndex == 1 ? threshold.Value > 100 : (nominal.HasValue && (double)threshold.Value > nominal.Value);
            ratingWarning.Text = excessive ? "注意：当前触发阈值高于 UPS 额定输出，可能在过载后才动作。请先确认或调整。" :
                (!nominal.HasValue && unit.SelectedIndex == 0 ? "UPS 未提供额定功率，可使用负载率设置阈值。" : "功率为估算值；该规则只能降低本机功耗，不影响 UPS 上的其他设备。");
        }
        void UpdatePresentation(GuardDecision decision)
        {
            bool elevated = WindowsPowerActions.IsAdministrator();
            bool showPause = config.Armed || busy || (actions != null && actions.HasRecovery);
            arm.Visible = !showPause; pause.Visible = showPause;
            arm.Text = string.IsNullOrWhiteSpace(config.Host) ? "设置 UPS 连接" : (elevated ? "启用自动保护" : "以管理员身份打开");
            protectionDescription.Text = config.Armed ? "规则已启用，可随时暂停并恢复本机原设置。" :
                (elevated ? "只读监测中，点击右侧按钮启用已保存的保护规则。" : "普通权限只读监测；提升权限后，仍需手动启用保护。" );
            permissionLabel.Text = elevated ? "当前：管理员权限，可以启用自动保护。" : "当前：普通权限，可以连接 UPS 并查看数据。";
            admin.Visible = !elevated;
            cards[3].ForeColor = decision.Fresh ? (sample != null && sample.OnBattery ? Color.FromArgb(173, 104, 14) : teal) : muted;
            if (!decision.Fresh) cards[3].Text = sample == null ? (string.IsNullOrWhiteSpace(config.Host) ? "待配置" : "连接中") : "数据已失效";
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
            else { modelName.Text = "等待 UPS 数据"; connectionBadge.Text = config.Host; loadSummary.Text = "正在读取整体负载"; }
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
                else { for (int i = 0; i < 3; ++i) e.Graphics.DrawLine(pen, x, y + i * 7 * s, x + (i == 2 ? 11 : 17) * s, y + i * 7 * s); }
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
