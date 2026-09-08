using System;
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
        readonly ComboBox languageSelector = new ComboBox();
        readonly Label updateStatus = new Label(), versionValue = new Label();
        readonly ModernButton checkUpdate = new ModernButton(), installUpdate = new ModernButton();
        readonly RichTextBox updateNotes = new RichTextBox();
        readonly LocalizedView localizedView = new LocalizedView();
        UiPreferences uiPreferences;
        UpdateRelease availableRelease;
        bool languageChanging, updateBusy;
        CancellationTokenSource downloadCancellation;
        string UiPreferencesPath { get { return Path.Combine(dataDirectory, "ui.xml"); } }

        sealed class LanguageOption
        {
            public string Code, Name;
            public LanguageOption(string code, string name) { Code = code; Name = name; }
            public override string ToString() { return Name; }
        }

        void InitializeLanguage()
        {
            uiPreferences = UiPreferences.Load(UiPreferencesPath);
            Localization.SetLanguage(Localization.ResolveLanguage(uiPreferences.Language));
        }
        void BuildExtraPages(Panel sidebar)
        {
            languageSelector.SetBounds(20, 500, 208, 30); languageSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            languageSelector.Font = new Font("Microsoft YaHei UI", 9F); languageSelector.AccessibleName = "语言";
            languageSelector.Items.AddRange(new object[] {
                new LanguageOption("auto", "System / 自动"), new LanguageOption("zh-CN", "简体中文"),
                new LanguageOption("en", "English"), new LanguageOption("ja", "日本語"), new LanguageOption("ko", "한국어"),
                new LanguageOption("fr", "Français"), new LanguageOption("de", "Deutsch"), new LanguageOption("es", "Español") });
            sidebar.Controls.Add(languageSelector);
            languageChanging = true; languageSelector.SelectedIndex = 0;
            for (int i = 0; i < languageSelector.Items.Count; i++) if (((LanguageOption)languageSelector.Items[i]).Code == uiPreferences.Language) languageSelector.SelectedIndex = i;
            languageChanging = false;
            languageSelector.SelectedIndexChanged += delegate { ChangeLanguage(); };
            tips.SetToolTip(languageSelector, Localization.T("选择语言后立即生效。"));
            BuildGuidePage(); BuildUpdatePage(); BuildSupportPage();
        }
        void AttachLocalization()
        {
            localizedView.Attach(this); localizedView.Attach(tray.ContextMenuStrip); localizedView.ApplyAll();
            bool oldLoading = loading; loading = true;
            try { unit.Items[0] = Localization.T("估算功率（W）"); unit.Items[1] = Localization.T("负载率（%）"); }
            finally { loading = oldLoading; }
            FormClosed += delegate { if (downloadCancellation != null) downloadCancellation.Cancel(); localizedView.Dispose(); };
        }
        void ChangeLanguage()
        {
            if (languageChanging || languageSelector.SelectedItem == null) return;
            var selected = (LanguageOption)languageSelector.SelectedItem;
            string previous = uiPreferences.Language;
            try
            {
                uiPreferences.Language = selected.Code; uiPreferences.Save(UiPreferencesPath);
                Localization.SetLanguage(Localization.ResolveLanguage(selected.Code));
                localizedView.ApplyAll();
                bool oldLoading = loading; loading = true;
                try { unit.Items[0] = Localization.T("估算功率（W）"); unit.Items[1] = Localization.T("负载率（%）"); }
                finally { loading = oldLoading; }
                UpdateRuleDescriptions(); if (currentPage == 3) RefreshEvents();
                tips.SetToolTip(languageSelector, Localization.T("选择语言后立即生效。"));
            }
            catch (Exception ex) { uiPreferences.Language = previous; Notice("语言设置保存失败：" + ex.Message, true); }
        }
        DialogResult LocalizedMessage(string message, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        { return MessageBox.Show(this, Localization.T(message), Localization.T(caption), buttons, icon); }

        void BuildGuidePage()
        {
            Panel page = pages[4]; PageHeading(page, "使用说明", "简要使用说明");
            var steps = Surface(page, 30, 104, 832, 310);
            string[] lines = {
                "1. 连接你的 UPS 服务器。", "2. 确认功率口径及保护阈值。",
                "3. 启用保护前，先验证本机限功耗与恢复。", "4. 仅在电池供电且满足条件时倒计时休眠。",
                "5. 可随时暂停保护并恢复原设置。" };
            for (int i = 0; i < lines.Length; i++) ViewLabel(steps, lines[i], 25, 24 + i * 54, 776, 45, 11, i == 0, ink);
            var explanation = Surface(page, 30, 431, 832, 179);
            ViewLabel(explanation, "关于功率估算", 24, 18, 780, 31, 12, true, ink);
            ViewLabel(explanation, "总负载包括 UPS 上的所有设备；显示的瓦数可能是估算值。", 25, 60, 777, 43, 10, false, muted);
            ViewLabel(explanation, "休眠保留会话，但不会替每个应用保存文件。", 25, 114, 777, 43, 10, false, muted);
            MakeButton(page, "查看 GitHub", 644, 627, 218, 40, Color.White, ink).Click += delegate { OpenProject(); };
        }
        void BuildUpdatePage()
        {
            Panel page = pages[5]; PageHeading(page, "版本更新", "当前版本");
            var release = Surface(page, 30, 104, 832, 138);
            versionValue.SetBounds(24, 18, 482, 46); versionValue.Font = new Font("Segoe UI", 24, FontStyle.Bold); versionValue.Text = BuildInfo.Version; versionValue.ForeColor = ink; release.Controls.Add(versionValue);
            ViewLabel(release, "Windows 版本", 25, 76, 480, 28, 10, false, muted);
            StyleButton(checkUpdate, "检查更新", 592, 24, 214, 42, teal, Color.White); release.Controls.Add(checkUpdate);
            checkUpdate.Click += delegate { CheckForUpdates(); };
            updateStatus.SetBounds(24, 108, 781, 24); updateStatus.Font = new Font("Microsoft YaHei UI", 9F); updateStatus.ForeColor = muted; release.Controls.Add(updateStatus);
            var notes = Surface(page, 30, 259, 832, 291);
            ViewLabel(notes, "更新日志", 22, 15, 782, 28, 12, true, ink);
            updateNotes.SetBounds(23, 56, 784, 215); updateNotes.ReadOnly = true; updateNotes.BorderStyle = BorderStyle.None;
            updateNotes.BackColor = Color.White; updateNotes.ForeColor = ink; updateNotes.Font = new Font("Microsoft YaHei UI", 10F); updateNotes.DetectUrls = false;
            updateNotes.Text = PlainReleaseNotes(ReadEmbeddedText("Guardian.Changelog")); notes.Controls.Add(updateNotes);
            ViewLabel(page, "自动保护运行时，请先暂停并恢复限制，再安装更新。", 33, 568, 801, 40, 9F, false, muted);
            StyleButton(installUpdate, "下载并安装", 644, 624, 218, 42, teal, Color.White); installUpdate.Enabled = false; page.Controls.Add(installUpdate);
            installUpdate.Click += delegate { DownloadAndInstall(); };
            MakeButton(page, "查看完整更新记录", 30, 624, 270, 42, Color.White, ink).Click += delegate { Process.Start("https://github.com/kylefu8/ups-guardian/releases"); };
        }
        void BuildSupportPage()
        {
            Panel page = pages[6]; PageHeading(page, "支持开发", "打赏完全自愿，不影响任何功能。");
            string[] methods = { "微信", "支付宝" }; string[] resources = { "Guardian.Donation.wechat", "Guardian.Donation.alipay" };
            for (int i = 0; i < methods.Length; i++)
            {
                var card = Surface(page, 30 + i * 424, 106, 408, 449);
                ViewLabel(card, methods[i], 23, 20, 361, 31, 15, true, ink);
                Image code = LoadDonationImage(resources[i]);
                if (code != null)
                {
                    var image = new PictureBox { Image = code, Bounds = new Rectangle(66, 92, 276, 276), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
                    card.Controls.Add(image); FormClosed += delegate { code.Dispose(); };
                }
                else
                {
                    ViewLabel(card, "收款码尚未配置。", 37, 168, 333, 70, 14, true, muted).TextAlign = ContentAlignment.MiddleCenter;
                }
                ViewLabel(card, "扫码支持开发", 30, 391, 347, 31, 10, false, teal).TextAlign = ContentAlignment.MiddleCenter;
            }
            ViewLabel(page, "收款码由项目维护者提供。", 33, 578, 799, 32, 10, false, muted);
            MakeButton(page, "项目主页", 644, 624, 218, 42, Color.White, ink).Click += delegate { OpenProject(); };
        }
        static Image LoadDonationImage(string name)
        { using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (stream == null) return null; using (Image image = Image.FromStream(stream)) return new Bitmap(image); } }
        static string ReadEmbeddedText(string name)
        { using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (stream == null) return ""; using (var reader = new StreamReader(stream)) return reader.ReadToEnd(); } }
        static string PlainReleaseNotes(string markdown)
        {
            var text = new System.Text.StringBuilder();
            using (var reader = new StringReader(markdown ?? ""))
            { string line; while ((line = reader.ReadLine()) != null) { if (!line.TrimStart().StartsWith("#", StringComparison.Ordinal)) text.AppendLine(line); } }
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
            if (updateBusy || availableRelease == null) return;
            if (config.Armed || busy || (actions != null && actions.HasRecovery))
            { LocalizedMessage("自动保护运行时，请先暂停并恢复限制，再安装更新。", "版本更新"); return; }
            if (LocalizedMessage("安装更新需要退出程序，继续？", "版本更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            updateBusy = true; checkUpdate.Enabled = installUpdate.Enabled = false; updateStatus.Text = "正在下载更新…";
            downloadCancellation = new CancellationTokenSource(); UpdateRelease release = availableRelease;
            string stageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPSGuardian", "updates");
            Task.Run(delegate
            {
                StagedUpdate update = null; Exception error = null;
                try { update = UpdateService.Download(release, stageRoot, delegate(int progress) { Ui(delegate { updateStatus.Text = Localization.F("下载进度：{0}%", progress); }); }, downloadCancellation.Token); }
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
