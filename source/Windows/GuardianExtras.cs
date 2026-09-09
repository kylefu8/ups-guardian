using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpsGuardian
{
    sealed partial class GuardianForm
    {
        readonly LanguageButton languageButton = new LanguageButton();
        readonly ContextMenuStrip languageMenu = new ContextMenuStrip();
        readonly LinkLabel sidebarVersion = new LinkLabel();
        readonly Label updateStatus = new Label(), versionValue = new Label();
        readonly ModernButton checkUpdate = new ModernButton(), installUpdate = new ModernButton();
        readonly RichTextBox updateNotes = new RichTextBox();
        readonly LocalizedView localizedView = new LocalizedView();
        readonly FlowLayoutPanel guideReading = new FlowLayoutPanel();
        readonly List<Image> donationImages = new List<Image>();
        UiPreferences uiPreferences;
        UpdateRelease availableRelease;
        bool updateBusy;
        CancellationTokenSource downloadCancellation;
        string UiPreferencesPath { get { return Path.Combine(dataDirectory, "ui.xml"); } }

        sealed class LanguageOption
        {
            public string Code, Name;
            public LanguageOption(string code, string name) { Code = code; Name = name; }
        }

        void InitializeLanguage()
        {
            uiPreferences = UiPreferences.Load(UiPreferencesPath);
            Localization.SetLanguage(Localization.ResolveLanguage(uiPreferences.Language));
        }
        void BuildExtraPages(Panel sidebar)
        {
            StyleButton(languageButton, "", 20, 500, 116, 34, Color.FromArgb(26, 43, 62), Color.FromArgb(213, 224, 238));
            languageButton.Font = new Font("Microsoft YaHei UI", 9F); languageButton.AccessibleName = "语言";
            sidebar.Controls.Add(languageButton);
            languageMenu.Font = Font; languageMenu.ShowImageMargin = false; languageMenu.ShowCheckMargin = true;
            languageMenu.ForeColor = Color.FromArgb(213, 224, 238);
            languageMenu.Renderer = new ToolStripProfessionalRenderer(new LanguageMenuColors()) { RoundedEdges = false };
            var options = new[] {
                new LanguageOption("auto", "跟随系统"), new LanguageOption("zh-CN", "简体中文"),
                new LanguageOption("en", "English"), new LanguageOption("ja", "日本語"), new LanguageOption("ko", "한국어"),
                new LanguageOption("fr", "Français"), new LanguageOption("de", "Deutsch"), new LanguageOption("es", "Español") };
            foreach (LanguageOption option in options)
            {
                string code = option.Code;
                var item = new ToolStripMenuItem(option.Name) { Tag = option, Padding = new Padding(8, 5, 16, 5) };
                item.Click += delegate { ChangeLanguage(code); languageMenu.Close(); };
                languageMenu.Items.Add(item);
                if (code == "auto") languageMenu.Items.Add(new ToolStripSeparator());
            }
            languageButton.Click += delegate {
                UpdateLanguageMenu();
                languageMenu.Show(languageButton, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
            };
            sidebarVersion.SetBounds(142, 500, 90, 34); sidebarVersion.Text = "v" + BuildInfo.Version;
            sidebarVersion.Font = new Font("Segoe UI", 8.5F); sidebarVersion.TextAlign = ContentAlignment.MiddleRight;
            sidebarVersion.LinkBehavior = LinkBehavior.HoverUnderline; sidebarVersion.LinkColor = Color.FromArgb(143, 162, 184);
            sidebarVersion.ActiveLinkColor = Color.FromArgb(124, 228, 211); sidebarVersion.AccessibleName = "版本更新";
            sidebarVersion.LinkClicked += delegate { Navigate(5); };
            sidebar.Controls.Add(sidebarVersion);
            UpdateLanguageMenu();
            BuildGuidePage(); BuildUpdatePage(); BuildSupportPage();
        }
        void AttachLocalization()
        {
            localizedView.Attach(this); localizedView.Attach(tray.ContextMenuStrip); localizedView.ApplyAll();
            bool oldLoading = loading; loading = true;
            try { unit.Items[0] = Localization.T("估算功率（W）"); unit.Items[1] = Localization.T("负载率（%）"); }
            finally { loading = oldLoading; }
            UpdateLanguageMenu();
        }
        void UpdateLanguageMenu()
        {
            string current = Localization.Language;
            string preference = uiPreferences.Language;
            bool automatic = String.IsNullOrWhiteSpace(preference) || preference == "auto" || preference == "system";
            foreach (ToolStripItem entry in languageMenu.Items)
            {
                var item = entry as ToolStripMenuItem;
                if (item == null) continue;
                var option = (LanguageOption)item.Tag;
                item.Text = option.Code == "auto" ? Localization.T("跟随系统") : option.Name;
                item.Checked = automatic ? option.Code == "auto" : option.Code == Localization.ResolveLanguage(preference);
                if (option.Code == current) languageButton.Text = option.Name;
            }
            tips.SetToolTip(languageButton, (automatic ? Localization.T("跟随系统") + " · " : "") + languageButton.Text + "\n" + Localization.T("选择语言后立即生效。"));
            tips.SetToolTip(sidebarVersion, Localization.T("版本更新"));
        }
        void ChangeLanguage(string code)
        {
            if (uiPreferences.Language == code) return;
            string previous = uiPreferences.Language;
            try
            {
                uiPreferences.Language = code; uiPreferences.Save(UiPreferencesPath);
                Localization.SetLanguage(Localization.ResolveLanguage(code));
                localizedView.ApplyAll();
                bool oldLoading = loading; loading = true;
                try { unit.Items[0] = Localization.T("估算功率（W）"); unit.Items[1] = Localization.T("负载率（%）"); }
                finally { loading = oldLoading; }
                UpdateRuleDescriptions(); if (currentPage == 3) RefreshEvents();
                UpdateLanguageMenu();
            }
            catch (Exception ex) { uiPreferences.Language = previous; UpdateLanguageMenu(); Notice("语言设置保存失败：" + ex.Message, true); }
        }
        DialogResult LocalizedMessage(string message, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        { return MessageBox.Show(this, Localization.T(message), Localization.T(caption), buttons, icon); }

        Panel BuildExtraPageBody(int index, string title, string subtitle)
        {
            Panel page = pages[index]; page.AutoScroll = false;
            PageHeading(page, title, subtitle);
            var body = new Panel { Bounds = new Rectangle(0, 105, 892, 581), BackColor = canvas };
            page.Controls.Add(body);
            return body;
        }
        void BuildGuidePage()
        {
            Panel page = BuildExtraPageBody(4, "使用说明", "连接、保护与日常操作");
            var reading = guideReading;
            reading.Dock = DockStyle.Fill; reading.AutoScroll = true;
            reading.FlowDirection = FlowDirection.TopDown; reading.WrapContents = false; reading.Padding = new Padding(30, 0, 0, 16);
            page.Controls.Add(reading);
            foreach (string[] section in GuideContent.Sections)
            {
                var card = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White,
                    Padding = new Padding(24, 20, 24, 20), Margin = new Padding(0, 0, 0, 14),
                    MinimumSize = new Size(814, 0), MaximumSize = new Size(814, 0) };
                var title = new Label { Text = section[0], AutoSize = true, MaximumSize = new Size(758, 0),
                    Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), ForeColor = ink,
                    Margin = new Padding(0, 0, 0, 12), UseMnemonic = false };
                var body = new Label { Text = section[1], AutoSize = true, MaximumSize = new Size(758, 0),
                    Font = new Font("Microsoft YaHei UI", 10.5F), ForeColor = muted,
                    Margin = Padding.Empty, UseMnemonic = false };
                card.Controls.Add(title); card.Controls.Add(body); reading.Controls.Add(card);
            }
        }
        void BuildUpdatePage()
        {
            Panel page = BuildExtraPageBody(5, "版本更新", "Windows 版本");
            var release = Surface(page, 30, 0, 832, 138);
            versionValue.SetBounds(24, 18, 482, 46); versionValue.Font = new Font("Segoe UI", 24, FontStyle.Bold); versionValue.Text = BuildInfo.Version; versionValue.ForeColor = ink; release.Controls.Add(versionValue);
            ViewLabel(release, "Windows 版本", 25, 76, 480, 28, 10, false, muted);
            StyleButton(checkUpdate, "检查更新", 592, 24, 214, 42, teal, Color.White); release.Controls.Add(checkUpdate);
            checkUpdate.Click += delegate { CheckForUpdates(); };
            updateStatus.SetBounds(24, 108, 781, 24); updateStatus.Font = new Font("Microsoft YaHei UI", 9F); updateStatus.ForeColor = muted; release.Controls.Add(updateStatus);
            var notes = Surface(page, 30, 155, 832, 258);
            ViewLabel(notes, "更新日志", 22, 15, 782, 28, 12, true, ink);
            updateNotes.SetBounds(23, 56, 784, 182); updateNotes.ReadOnly = true; updateNotes.BorderStyle = BorderStyle.None;
            updateNotes.BackColor = Color.White; updateNotes.ForeColor = ink; updateNotes.Font = new Font("Microsoft YaHei UI", 10F); updateNotes.DetectUrls = false;
            updateNotes.Text = PlainReleaseNotes(ReadEmbeddedText("Guardian.Changelog")); notes.Controls.Add(updateNotes);
            ViewLabel(page, "自动保护运行时，请先暂停并恢复限制，再安装更新。", 33, 428, 801, 40, 9F, false, muted);
            StyleButton(installUpdate, "下载并安装", 644, 480, 218, 42, teal, Color.White); installUpdate.Enabled = false; page.Controls.Add(installUpdate);
            installUpdate.Click += delegate { DownloadAndInstall(); };
            MakeButton(page, "查看完整更新记录", 30, 480, 270, 42, Color.White, ink).Click += delegate { Process.Start("https://github.com/kylefu8/ups-guardian/releases"); };
        }
        void BuildSupportPage()
        {
            Panel page = BuildExtraPageBody(6, "支持开发", "打赏完全自愿，不影响任何功能。");
            string[] methods = { "微信", "支付宝" }; string[] resources = { "Guardian.Donation.wechat", "Guardian.Donation.alipay" };
            for (int i = 0; i < methods.Length; i++)
            {
                var card = Surface(page, 30 + i * 424, 0, 408, 400);
                ViewLabel(card, methods[i], 23, 20, 361, 31, 15, true, ink);
                Image code = LoadDonationImage(resources[i]);
                if (code != null)
                {
                    var image = new PictureBox { Image = code, Bounds = new Rectangle(74, 75, 260, 260), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
                    card.Controls.Add(image); donationImages.Add(code);
                }
                else
                {
                    ViewLabel(card, "收款码尚未配置。", 37, 168, 333, 70, 14, true, muted).TextAlign = ContentAlignment.MiddleCenter;
                }
                ViewLabel(card, "扫码支持开发", 30, 356, 347, 31, 10, false, teal).TextAlign = ContentAlignment.MiddleCenter;
            }
            ViewLabel(page, "收款码由项目维护者提供。", 33, 481, 580, 32, 10, false, muted);
            MakeButton(page, "项目主页", 644, 480, 218, 42, Color.White, ink).Click += delegate { OpenProject(); };
        }
        static Image LoadDonationImage(string name)
        { using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (stream == null) return null; using (Image image = Image.FromStream(stream)) return new Bitmap(image); } }
        static string ReadEmbeddedText(string name)
        { using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (stream == null) return ""; using (var reader = new StreamReader(stream)) return reader.ReadToEnd(); } }
        static string PlainReleaseNotes(string markdown)
        {
            var text = new System.Text.StringBuilder();
            using (var reader = new StringReader(markdown ?? ""))
            { string line; while ((line = reader.ReadLine()) != null) text.AppendLine(line.TrimStart('#', ' ')); }
            return text.ToString().Trim();
        }
        void OpenProject() { Process.Start("https://github.com/kylefu8/ups-guardian"); }
        void CheckForUpdates()
        {
            if (updateBusy) return; updateBusy = true; checkUpdate.Enabled = false; installUpdate.Enabled = false;
            updateStatus.Text = "正在检查更新…";
            Task.Run(delegate
            {
                UpdateCheckResult result = null; Exception error = null;
                try { result = UpdateService.Check(BuildInfo.Version); } catch (Exception ex) { error = ex; }
                Ui(delegate
                {
                    updateBusy = false; checkUpdate.Enabled = true;
                    if (error != null) { updateStatus.Text = Localization.F("更新检查失败：{0}", error.Message); return; }
                    availableRelease = result.Release;
                    if (result.Status == UpdateStatus.Available)
                    { updateStatus.Text = Localization.F("发现新版本 {0}", result.Release.Version); installUpdate.Enabled = true; updateNotes.Text = PlainReleaseNotes(result.Release.Notes); }
                    else updateStatus.Text = result.Status == UpdateStatus.NoReleases ? "暂无发布版本。" : "已经是最新版本。";
                });
            });
        }
        void DownloadAndInstall()
        {
            if (updateBusy || discoveryBusy || availableRelease == null) return;
            if (config.Armed || busy || (actions != null && actions.HasRecovery))
            { LocalizedMessage("自动保护运行时，请先暂停并恢复限制，再安装更新。", "版本更新"); return; }
            if (LocalizedMessage("安装更新需要退出程序，继续？", "版本更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            updateBusy = true; checkUpdate.Enabled = installUpdate.Enabled = false; updateStatus.Text = "正在下载更新…";
            if (downloadCancellation != null) downloadCancellation.Dispose();
            downloadCancellation = new CancellationTokenSource();
            CancellationToken cancellation = downloadCancellation.Token; UpdateRelease release = availableRelease;
            string stageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPSGuardian", "updates");
            Task.Run(delegate
            {
                StagedUpdate update = null; Exception error = null;
                try { update = UpdateService.Download(release, stageRoot, delegate(int progress) { Ui(delegate { updateStatus.Text = Localization.F("下载进度：{0}%", progress); }); }, cancellation); }
                catch (Exception ex) { error = ex; }
                Ui(delegate
                {
                    updateBusy = false; checkUpdate.Enabled = true; installUpdate.Enabled = true;
                    if (error != null) { updateStatus.Text = Localization.F("更新下载失败：{0}", error.Message); return; }
                    if (config.Armed || busy || (actions != null && actions.HasRecovery)) { updateStatus.Text = "自动保护运行时，请先暂停并恢复限制，再安装更新。"; return; }
                    try
                    {
                        config.Armed = false; config.Save(ConfigPath);
                        WindowsUpdateInstaller.Start(update, AppDomain.CurrentDomain.BaseDirectory, Process.GetCurrentProcess().Id);
                        ExitNow();
                    }
                    catch (Exception ex) { updateStatus.Text = Localization.F("更新安装失败：{0}", ex.Message); }
                });
            });
        }
    }
}
