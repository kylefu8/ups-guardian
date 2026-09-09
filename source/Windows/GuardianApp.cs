using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpsGuardian
{
    static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\KyleUPSGuardian", out created))
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (!created && Array.IndexOf(args, "--elevated-relaunch") >= 0)
                { try { created = mutex.WaitOne(5000); } catch (AbandonedMutexException) { created = true; } }
                if (!created)
                {
                    try { using (var show = EventWaitHandle.OpenExisting(@"Local\KyleUPSGuardianShow")) show.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("UPS 守护正在启动，请稍后从托盘打开。", "UPS 守护"); }
                    return;
                }
                Application.Run(new GuardianForm(Array.IndexOf(args, "--tray") >= 0, Array.IndexOf(args, "--update-failed") >= 0));
                mutex.ReleaseMutex();
            }
        }
    }

    sealed partial class GuardianForm : Form
    {
        readonly Color ink = Color.FromArgb(24, 38, 60), muted = Color.FromArgb(97, 111, 133);
        readonly Color blue = Color.FromArgb(35, 94, 214), red = Color.FromArgb(188, 43, 57);
        readonly string dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        readonly GuardLogic logic = new GuardLogic();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly EventWaitHandle showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KyleUPSGuardianShow");
        readonly Label[] cards = new Label[4];
        readonly Label state = new Label(), detail = new Label(), notice = new Label();
        readonly TextBox host = new TextBox(), upsName = new TextBox();
        readonly NumericUpDown port = new NumericUpDown(), threshold = new NumericUpDown(), margin = new NumericUpDown();
        readonly NumericUpDown cpu = new NumericUpDown(), gpu = new NumericUpDown(), charge = new NumericUpDown();
        readonly NumericUpDown runtime = new NumericUpDown(), countdown = new NumericUpDown();
        readonly ComboBox unit = new ComboBox();
        readonly CheckBox autoStart = new CheckBox();
        readonly Panel connection = new Panel(), rules = new Panel();
        readonly Button arm = new ModernButton(), pause = new ModernButton(), admin = new ModernButton();
        GuardSettings config;
        WindowsPowerActions actions;
        PowerCapabilities capabilities;
        NutSnapshot sample;
        bool fetching, busy, pausePending, exiting, exitPending, loading, reduced, firstRecovery, throttleFault, ratingNoticeShown;
        bool startHidden;
        volatile bool closing;
        bool resourcesDisposed;
        DateTime nextPoll = DateTime.MinValue;
        int connectionGeneration;
        string lastError = "", lastTransition = "";
        string ConfigPath { get { return Path.Combine(dataDirectory, "settings.xml"); } }

        public GuardianForm(bool hidden, bool updateFailed = false)
        {
            startHidden = hidden;
            Directory.CreateDirectory(dataDirectory);
            try { config = GuardSettings.Load(ConfigPath); }
            catch (Exception ex) { config = new GuardSettings(); lastError = "设置读取失败，已使用只监测模式：" + ex.Message; }
            try { actions = new WindowsPowerActions(dataDirectory); }
            catch (Exception ex) { lastError = "电源控制初始化失败：" + ex.Message; }
            capabilities = actions == null ? new PowerCapabilities() : actions.DetectCapabilities();
            InitializeLanguage();
            // A remembered UPS can resume monitoring after verification, never protection.
            config.Armed = false;
            firstRecovery = actions != null && actions.HasRecovery;
            if (firstRecovery) { config.Armed = false; lastError = "检测到上次未恢复的限制，请点击「暂停并恢复」。"; }
            BuildInterface();
            LoadControls(); OnSettingsSaved(); SetupTray(); AttachLocalization();
            timer.Interval = 1000; timer.Tick += Tick;
            Shown += delegate { timer.Start(); if (startHidden && config.ConnectionConfirmed) Hide(); InitializeConnection(); Tick(null, EventArgs.Empty); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2500, Localization.T("UPS 守护仍在运行"), Localization.T("双击托盘图标打开；从托盘菜单退出。"), ToolTipIcon.Info); }
            };
            Notice(lastError.Length > 0 ? lastError : "请先发现并确认一台 UPS，确认前不会开始监测或启用保护。", false);
            if (updateFailed) Notice("更新失败，已恢复之前的版本，请查看更新日志。", true);
        }
        void Number(Control owner, NumericUpDown input, int x, int y, int width, int minimum, int maximum, string name)
        { input.SetBounds(x, y, width, 30); input.Minimum = minimum; input.Maximum = maximum; input.AccessibleName = name; owner.Controls.Add(input); }
        void StyleButton(Button button, string text, int x, int y, int w, int h, Color background, Color color)
        { button.Text = text; button.SetBounds(x, y, w, h); button.BackColor = background; button.ForeColor = color; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.Cursor = Cursors.Hand; }
        void SetupTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示 UPS 信息", null, delegate { ShowWindow(); });
            menu.Items.Add("暂停保护并恢复限制", null, delegate { Pause(false); });
            menu.Items.Add("退出并恢复限制", null, delegate { Pause(true); });
            tray.ContextMenuStrip = menu; tray.Icon = brandIcon ?? SystemIcons.Shield; tray.Text = "UPS 守护 · 只读监测"; tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };
        }
        void ShowWindow() { if (closing) return; Show(); WindowState = FormWindowState.Normal; Activate(); }
        void Ui(Action action)
        {
            if (closing || IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!closing && !IsDisposed && !Disposing) action(); }); }
            catch (InvalidOperationException) { }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // A normal X click is canceled by the hide-to-tray handler.
            if (!e.Cancel) StopCallbacks();
        }
        void StopCallbacks()
        {
            if (closing) return;
            closing = true; timer.Stop();
            if (downloadCancellation != null) downloadCancellation.Cancel();
            if (discoveryCancellation != null) discoveryCancellation.Cancel();
        }
        protected override void Dispose(bool disposing)
        {
            if (!disposing || resourcesDisposed) { base.Dispose(disposing); return; }
            resourcesDisposed = true;
            StopCallbacks();
            timer.Dispose();
            var menu = tray.ContextMenuStrip;
            // NotifyIcon touches Icon.Handle while hiding. Keep the shared icon
            // alive until BOTH the tray and the Form have finished disposing.
            tray.Visible = false; tray.Icon = null; tray.ContextMenuStrip = null;
            tray.Dispose(); if (menu != null) menu.Dispose();
            languageMenu.Dispose();
            showRequest.Dispose(); tips.Dispose();
            base.Dispose(true);
            localizedView.Dispose();
            foreach (Image image in donationImages) image.Dispose();
            if (brandImage != null) brandImage.Dispose();
            if (brandIcon != null) brandIcon.Dispose();
            if (downloadCancellation != null) downloadCancellation.Dispose();
            if (discoveryCancellation != null) discoveryCancellation.Dispose();
        }
        void Log(string text)
        { try { File.AppendAllText(Path.Combine(dataDirectory, "events.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine); if (currentPage == 3) RefreshEvents(); } catch { } }
        void Notice(string text, bool balloon)
        { notice.Text = text; Log(text); if (balloon) tray.ShowBalloonTip(5000, Localization.T("UPS 守护"), Localization.T(text), ToolTipIcon.Warning); }
        void LoadControls()
        {
            loading = true; host.Text = config.Host; port.Value = config.Port; upsName.Text = config.UpsName;
            unit.SelectedIndex = config.UsePercent ? 1 : 0; threshold.Value = (decimal)config.LoadThreshold; margin.Value = (decimal)config.RecoveryMargin;
            cpu.Value = config.CpuMaximum; gpu.Value = capabilities.GpuLimitSupported ? config.GpuWatts : 0; charge.Value = (decimal)config.ChargeThreshold;
            runtime.Value = config.RuntimeThresholdSeconds; countdown.Value = config.HibernateCountdownSeconds; autoStart.Checked = config.StartAtLogon; loading = false;
        }
        bool SaveControls(bool reconnect)
        {
            if (config.Armed || busy || discoveryBusy) return false;
            try
            {
                var updated = config.Copy();
                updated.Host = host.Text.Trim(); updated.Port = (int)port.Value; updated.UpsName = upsName.Text.Trim();
                updated.UsePercent = unit.SelectedIndex == 1; updated.LoadThreshold = (double)threshold.Value; updated.RecoveryMargin = (double)margin.Value;
                updated.CpuMaximum = (int)cpu.Value; updated.GpuWatts = (int)gpu.Value; updated.ChargeThreshold = (double)charge.Value;
                updated.RuntimeThresholdSeconds = (int)runtime.Value; updated.HibernateCountdownSeconds = (int)countdown.Value;
                updated.StartAtLogon = autoStart.Checked;
                updated.AboveRatingAccepted = config.AboveRatingAccepted && config.UsePercent == updated.UsePercent && config.LoadThreshold == updated.LoadThreshold;
                updated.Validate();
                if (updated.GpuWatts > 0 && (!capabilities.GpuLimitSupported || updated.GpuWatts < capabilities.GpuMinimumWatts || updated.GpuWatts > capabilities.GpuMaximumWatts))
                    throw new InvalidDataException(Localization.F("GPU 功率范围为 {0}–{1}W；0 表示不控制 GPU。", capabilities.GpuMinimumWatts, capabilities.GpuMaximumWatts));
                if (updated.StartAtLogon != config.StartAtLogon) StartupTask.Set(updated.StartAtLogon);
                bool endpointChanged = config.Host != updated.Host || config.Port != updated.Port || config.UpsName != updated.UpsName;
                if (endpointChanged) updated.ConnectionConfirmed = false;
                updated.Save(ConfigPath); config = updated; logic.Reset(); ratingNoticeShown = false;
                if (endpointChanged) { chart.Reset(); plottedAt = DateTime.MinValue; }
                if (endpointChanged || reconnect)
                {
                    // Invalidate both the displayed sample and any read already in flight,
                    // including an A -> B -> A switch or reconnect to the same endpoint.
                    connectionGeneration++; sample = null; lastError = ""; nextPoll = DateTime.MinValue;
                    connectionReady = false;
                }
                OnSettingsSaved();
                Notice("设置已保存；自动保护尚未启用。", false); return true;
            }
            catch (Exception ex) { LocalizedMessage(ex.Message, "设置未保存"); return false; }
        }
        void Arm()
        {
            if (!connectionReady || !config.ConnectionConfirmed) { Navigate(2); return; }
            if (busy || discoveryBusy || updateBusy || actions == null || config.Armed) return;
            if (!WindowsPowerActions.IsAdministrator()) { LocalizedMessage("请先点击「以管理员身份打开」，以便设置本机功耗限制。", "需要管理员权限"); return; }
            if (actions.HasRecovery) { LocalizedMessage("请先暂停并恢复上次限制。", "有待恢复设置"); return; }
            if (!capabilities.SuspendSupported || !capabilities.CpuLimitSupported) { LocalizedMessage("当前电脑未提供所需的电源控制能力，可继续只读监测。", "无法启用保护"); return; }
            if (!SaveControls(false)) return;
            if (sample == null || (DateTime.UtcNow - sample.ReceivedUtc).TotalSeconds > config.StaleSeconds)
            { LocalizedMessage("请等待 UPS 数据连接成功后再启用。", "UPS 数据尚未就绪"); return; }
            double? rated = config.UsePercent ? (double?)100 : sample.NominalWatts;
            if (rated.HasValue && config.LoadThreshold > rated.Value && !config.AboveRatingAccepted)
            {
                if (LocalizedMessage("当前触发阈值高于 UPS 额定值，可能在过载后才动作或无法触发。仍使用此阈值？", "阈值高于额定值", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                config.AboveRatingAccepted = true;
            }
            try { config.Armed = true; config.Save(ConfigPath); logic.Reset(); throttleFault = false; Notice("自动保护已启用。低电量休眠仅在电池供电时触发。", false); }
            catch (Exception ex) { config.Armed = false; Notice(ex.Message, true); }
        }
        void Pause(bool quit)
        {
            config.Armed = false; logic.Reset(); exitPending = exitPending || quit;
            try { config.Save(ConfigPath); } catch (Exception ex) { Notice("暂停设置保存失败：" + ex.Message, true); }
            if (busy) { pausePending = true; Notice("已暂停新动作，正在等待当前操作结束后恢复设置。", false); return; }
            if (actions != null && actions.HasRecovery)
            {
                RunAction(delegate { actions.Restore(); }, delegate { reduced = false; firstRecovery = false; throttleFault = false; Notice(actions.RecoverySummary, false); if (exitPending) ExitNow(); });
            }
            else { Notice("自动保护已暂停。", false); if (exitPending) ExitNow(); }
        }
        void ExitNow() { exiting = true; Close(); }
        void RunAction(Action operation, Action completed, bool keepBatteryGuardOnFailure = false)
        {
            if (busy) return; busy = true;
            Task.Run(delegate
            {
                Exception error = null; try { operation(); } catch (Exception ex) { error = ex; }
                Ui(delegate
                {
                    busy = false;
                    if (error != null)
                    {
                        if (keepBatteryGuardOnFailure) throttleFault = true;
                        else { config.Armed = false; logic.Reset(); }
                        try { config.Save(ConfigPath); } catch { }
                        exitPending = false;
                        Notice((keepBatteryGuardOnFailure && config.Armed ? "功耗控制失败；电池休眠保护继续运行：" : "操作失败，自动保护已暂停：") + error.Message, true); ShowWindow();
                    }
                    else completed();
                    if (pausePending) { pausePending = false; Pause(exitPending); }
                });
            });
        }
        void RelaunchElevated()
        {
            if (config.Armed || busy || discoveryBusy) return;
            if (!SaveControls(false)) return;
            try
            {
                // The new process waits briefly for this instance to release its mutex.
                var info = new ProcessStartInfo(Application.ExecutablePath, "--elevated-relaunch") { UseShellExecute = true, Verb = "runas" };
                Process.Start(info); ExitNow();
            }
            catch (Exception ex) { Notice("未切换到管理员模式：" + ex.Message, false); }
        }
        void Poll()
        {
            if (!connectionReady || !config.ConnectionConfirmed || fetching || string.IsNullOrWhiteSpace(config.Host)) return; fetching = true;
            string targetHost = config.Host, name = config.UpsName; int targetPort = config.Port;
            int generation = connectionGeneration;
            Task.Run(delegate
            {
                NutSnapshot received = null; Exception error = null;
                try { received = NutClient.Read(targetHost, targetPort, name, 2500); } catch (Exception ex) { error = ex; }
                Ui(delegate { CompletePoll(generation, received, error); });
            });
        }
        void CompletePoll(int generation, NutSnapshot received, Exception error)
        {
            fetching = false;
            if (generation != connectionGeneration || !connectionReady || !config.ConnectionConfirmed) return;
            if (error != null) { if (lastError != error.Message) Notice("UPS 读取失败：" + error.Message, true); lastError = error.Message; }
            else { sample = received; lastError = ""; }
        }
        void Tick(object sender, EventArgs e)
        {
            if (closing) return;
            if (showRequest.WaitOne(0)) ShowWindow();
            DateTime now = DateTime.UtcNow;
            if (now >= nextPoll && !fetching) { nextPoll = now.AddSeconds(2); Poll(); }
            GuardDecision decision = logic.Evaluate(sample, config, now, config.Armed && connectionReady && config.ConnectionConfirmed);
            if (!connectionReady) decision.Message = discoveryBusy ? "正在发现或验证 UPS · 监测尚未开始" : "等待确认 UPS · 监测尚未开始";
            state.Text = "● " + Localization.T(decision.Message) + (throttleFault ? " · " + Localization.T("功耗控制待处理") : (reduced ? " · " + Localization.T("本机已限功耗") : ""));
            state.ForeColor = decision.HibernateInSeconds.HasValue ? red : (config.Armed ? blue : muted);
            string trayText = Localization.T("UPS 守护") + " · " + Localization.T(!connectionReady ? "等待确认 UPS" : (config.Armed ? "保护已启用" : "只读监测"));
            if (sample != null)
            {
                double? watts = sample.MeasuredWatts ?? sample.EstimatedWatts;
                cards[0].Text = watts.HasValue ? watts.Value.ToString("F0") + " W" : "—";
                cards[1].Text = sample.ChargePercent.HasValue ? sample.ChargePercent.Value.ToString("F0") + " %" : "—";
                cards[2].Text = sample.RuntimeSeconds.HasValue ? Localization.F("{0}分{1}秒", (int)sample.RuntimeSeconds.Value / 60, (int)sample.RuntimeSeconds.Value % 60) : "—";
                cards[3].Text = sample.OnBattery ? "电池供电" : (sample.OnLine ? "市电供电" : sample.Status);
                detail.Text = Localization.F("{0} · 负载 {1} · 额定 {2} · 更新 {3}", Localization.T(sample.MeasuredWatts.HasValue ? "实测功率" : "估算功率"),
                    sample.LoadPercent.HasValue ? sample.LoadPercent.Value.ToString("F0") + "%" : Localization.T("未知"),
                    sample.NominalWatts.HasValue ? sample.NominalWatts.Value.ToString("F0") + "W" : Localization.T("未知"),
                    sample.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss")) + (decision.Fresh ? "" : " · " + Localization.T("数据已失效"));
                trayText += " · " + cards[0].Text;
                if (!ratingNoticeShown && !config.Armed && !config.AboveRatingAccepted &&
                    (config.UsePercent || sample.NominalWatts.HasValue) && config.LoadThreshold > (config.UsePercent ? 100 : sample.NominalWatts.Value))
                {
                    ratingNoticeShown = true;
                    Notice("当前触发阈值高于 UPS 额定输出；请确认或调整阈值后再启用保护。", false);
                }
            }
            else detail.Text = Localization.T(discoveryBusy ? "正在发现或验证 UPS · 监测尚未开始" : "等待确认 UPS · 监测尚未开始");
            if (trayText.Length > 63) trayText = trayText.Substring(0, 63); tray.Text = trayText;
            connection.Enabled = !config.Armed && !busy;
            rules.Enabled = connectionReady && !config.Armed && !busy && !discoveryBusy;
            arm.Enabled = !config.Armed && !busy && !discoveryBusy && !updateBusy && actions != null && !firstRecovery;
            pause.Enabled = config.Armed || busy || (actions != null && actions.HasRecovery);
            UpdatePresentation(decision);
            UpdateDiscoveryControls();
            if (decision.Message != lastTransition)
            {
                if (decision.HibernateInSeconds.HasValue && lastTransition.IndexOf("秒后休眠", StringComparison.Ordinal) < 0)
                { ShowWindow(); tray.ShowBalloonTip(10000, Localization.T("UPS 电池保护"), Localization.T("即将休眠。点击「暂停并恢复限制」可取消。"), ToolTipIcon.Warning); }
                if (!decision.HibernateInSeconds.HasValue) Log(decision.Message);
                lastTransition = decision.Message;
            }
            if (!config.Armed || !connectionReady || !config.ConnectionConfirmed || busy || actions == null) return;
            if (decision.HibernateNow)
            {
                // Persist the disarmed state before sleeping, preventing a resume/hibernate loop.
                config.Armed = false;
                try { config.Save(ConfigPath); } catch (Exception ex) { Notice("无法保存休眠状态，已取消动作：" + ex.Message, true); return; }
                Notice("电池保护条件满足，准备休眠以保留当前会话。", true);
                RunAction(delegate { actions.Hibernate(); }, delegate { Notice("休眠调用已返回；自动保护已暂停，请确认供电后重新启用。", true); Pause(false); });
            }
            else if (decision.Reduce == true && !reduced && !throttleFault)
                RunAction(delegate { actions.Reduce(config.CpuMaximum, config.GpuWatts); }, delegate { reduced = true; Notice("已降低本机功耗；UPS 上其他设备不受此程序控制。", true); }, true);
            else if (decision.Reduce == false && reduced && !throttleFault)
                RunAction(delegate { actions.Restore(); }, delegate { reduced = false; Notice("整体负载持续回落，已恢复本机原设置。", false); }, true);
        }
    }

    static class StartupTask
    {
        public static void Set(bool enabled)
        {
            if (!WindowsPowerActions.IsAdministrator()) throw new InvalidOperationException("登录自启设置需要管理员权限。");
            // Use the Task Scheduler COM API so paths remain structured and need no shell quoting.
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            string name = "UPSGuardian-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            if (!enabled) { folder.DeleteTask(name, 0); return; }
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "登录后运行本地 UPS 监测与保护程序。";
            string userId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            task.Principal.UserId = userId; task.Principal.LogonType = 3; task.Principal.RunLevel = 1;
            dynamic trigger = task.Triggers.Create(9); trigger.UserId = userId;
            dynamic action = task.Actions.Create(0); action.Path = Application.ExecutablePath; action.Arguments = "--tray"; action.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false; task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.MultipleInstances = 2;
            folder.RegisterTaskDefinition(name, task, 6, userId, null, 3, null);
        }
    }
}
