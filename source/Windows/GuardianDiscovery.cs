using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpsGuardian
{
    sealed partial class GuardianForm
    {
        readonly ListView discoveryList = new ListView();
        readonly NumericUpDown discoveryPort = new NumericUpDown();
        readonly Button scanUps = new ModernButton(), cancelDiscovery = new ModernButton(), confirmUps = new ModernButton();
        readonly Label discoveryStatus = new Label(), discoveryScope = new Label();
        Button saveConnectionSettings;
        CancellationTokenSource discoveryCancellation;
        bool connectionReady, discoveryBusy;

        void BuildDiscoverySurface()
        {
            var surface = Surface(connection, 30, 0, 832, 451);
            ViewLabel(surface, "发现局域网 UPS", 23, 18, 520, 28, 13, true, ink);
            ViewLabel(surface, "扫描端口", 560, 23, 120, 24, 9, false, muted);
            Number(surface, discoveryPort, 697, 20, 110, 1, 65535, "扫描端口"); discoveryPort.Value = config.Port;
            ViewLabel(surface, "先搜索，再选择并确认一台 UPS。确认后只开始监测，保护仍需手动开启。", 24, 53, 780, 34, 9, false, muted);
            StyleButton(scanUps, "搜索局域网", 24, 96, 225, 36, teal, Color.White);
            StyleButton(cancelDiscovery, "取消搜索", 265, 96, 180, 36, Color.White, ink);
            StyleButton(confirmUps, "确认并连接所选 UPS", 487, 96, 320, 36, blue, Color.White);
            surface.Controls.Add(scanUps); surface.Controls.Add(cancelDiscovery); surface.Controls.Add(confirmUps);
            scanUps.Click += delegate { StartDiscovery(); };
            cancelDiscovery.Click += delegate { if (discoveryCancellation != null) discoveryCancellation.Cancel(); };
            confirmUps.Click += delegate { ConfirmDiscoveredUps(); };
            discoveryScope.SetBounds(24, 142, 783, 22); discoveryScope.Font = new Font("Microsoft YaHei UI", 8.5F); discoveryScope.ForeColor = muted;
            discoveryStatus.SetBounds(24, 169, 783, 34); discoveryStatus.Font = new Font("Microsoft YaHei UI", 9F); discoveryStatus.ForeColor = ink;
            discoveryStatus.Text = "尚未确认 UPS，监测和保护均未启用。";
            surface.Controls.Add(discoveryScope); surface.Controls.Add(discoveryStatus);
            discoveryList.SetBounds(24, 211, 783, 144); discoveryList.View = View.Details;
            discoveryList.FullRowSelect = true; discoveryList.MultiSelect = false; discoveryList.HideSelection = false;
            discoveryList.HeaderStyle = ColumnHeaderStyle.Nonclickable; discoveryList.AccessibleName = "发现的 UPS（只能选择一台）";
            discoveryList.Columns.Add("服务器地址", 230); discoveryList.Columns.Add("设备名称", 175); discoveryList.Columns.Add("设备描述", 350);
            discoveryList.SelectedIndexChanged += delegate { UpdateDiscoveryControls(); };
            surface.Controls.Add(discoveryList);
            ViewLabel(surface, "服务器地址", 24, 365, 329, 24, 9, false, muted);
            host.SetBounds(24, 397, 329, 31); host.ReadOnly = true; host.AccessibleName = "已选择的 UPS 服务器"; surface.Controls.Add(host);
            ViewLabel(surface, "端口", 378, 365, 137, 24, 9, false, muted);
            Number(surface, port, 378, 397, 137, 1, 65535, "NUT 端口"); port.Enabled = false;
            ViewLabel(surface, "设备名称", 542, 365, 265, 24, 9, false, muted);
            upsName.SetBounds(542, 397, 265, 31); upsName.ReadOnly = true; upsName.AccessibleName = "已选择的 UPS 设备"; surface.Controls.Add(upsName);
            UpdateDiscoveryControls();
        }

        void InitializeConnection()
        {
            connectionReady = false; config.Armed = false;
            if (config.ConnectionConfirmed && !String.IsNullOrWhiteSpace(config.Host))
                BeginConnectionValidation(new NutDiscoveryCandidate(config.Host, config.Port, config.UpsName, ""), false);
            else Navigate(2);
        }

        void UpdateDiscoveryControls()
        {
            bool available = !config.Armed && !busy && !updateBusy && (actions == null || !actions.HasRecovery);
            scanUps.Enabled = available && !discoveryBusy;
            confirmUps.Enabled = available && !discoveryBusy && discoveryList.SelectedItems.Count == 1;
            cancelDiscovery.Enabled = discoveryBusy;
            discoveryList.Enabled = available && !discoveryBusy;
            discoveryPort.Enabled = available && !discoveryBusy;
            autoStart.Enabled = !discoveryBusy;
            if (saveConnectionSettings != null) saveConnectionSettings.Enabled = !discoveryBusy;
            tips.SetToolTip(discoveryStatus, discoveryStatus.Text);
        }

        int BeginDiscoveryOperation()
        {
            if (discoveryCancellation != null) discoveryCancellation.Dispose();
            discoveryCancellation = new CancellationTokenSource();
            discoveryBusy = true; connectionReady = false; config.Armed = false;
            sample = null; logic.Reset(); connectionGeneration++; nextPoll = DateTime.MinValue;
            UpdateDiscoveryControls();
            return connectionGeneration;
        }

        void StartDiscovery()
        {
            if (discoveryBusy || config.Armed || busy || updateBusy || (actions != null && actions.HasRecovery)) return;
            int generation = BeginDiscoveryOperation();
            int targetPort = (int)discoveryPort.Value;
            discoveryList.Items.Clear(); discoveryScope.Text = "";
            discoveryStatus.Text = "正在搜索局域网，可随时取消。监测和保护尚未启用。";
            CancellationToken cancellation = discoveryCancellation.Token;
            Task.Run(delegate
            {
                List<NutDiscoveryCandidate> found = null; Exception error = null;
                try
                {
                    NutDiscoveryPlan plan = NutDiscovery.CreateLocalPlan(targetPort);
                    Ui(delegate
                    {
                        if (generation != connectionGeneration) return;
                        discoveryScope.Text = Localization.F("扫描范围：{0}", String.Join(", ", plan.Ranges));
                        if (plan.IsTruncated) discoveryScope.Text += " · " + Localization.T("大网段仅扫描本机所在 /24，范围有限");
                        tips.SetToolTip(discoveryScope, discoveryScope.Text);
                    });
                    found = NutDiscovery.Scan(plan, cancellation);
                }
                catch (Exception ex) { error = ex; }
                Ui(delegate { CompleteDiscovery(generation, found, error, cancellation.IsCancellationRequested); });
            });
        }

        void CompleteDiscovery(int generation, List<NutDiscoveryCandidate> found, Exception error, bool canceled)
        {
            if (generation != connectionGeneration) return;
            discoveryBusy = false; discoveryList.Items.Clear();
            if (canceled) discoveryStatus.Text = "搜索已取消。尚未确认 UPS，监测和保护均未启用。";
            else if (error != null) discoveryStatus.Text = Localization.F("发现失败：{0}", error.Message);
            else if (found == null || found.Count == 0)
                discoveryStatus.Text = "未找到可读取的 UPS。请检查局域网、NUT 服务和客户端白名单后重试。";
            else
            {
                foreach (NutDiscoveryCandidate candidate in found)
                {
                    var row = new ListViewItem(new[] { candidate.Host + ":" + candidate.Port, candidate.UpsName, candidate.Description });
                    row.Tag = candidate; discoveryList.Items.Add(row);
                }
                discoveryStatus.Text = Localization.F("发现 {0} 台 UPS。请选择一台，再点击“确认并连接”。", found.Count);
            }
            UpdateDiscoveryControls();
        }

        void ConfirmDiscoveredUps()
        {
            if (discoveryBusy || config.Armed || busy || updateBusy || (actions != null && actions.HasRecovery) || discoveryList.SelectedItems.Count != 1) return;
            var candidate = discoveryList.SelectedItems[0].Tag as NutDiscoveryCandidate;
            if (candidate != null) BeginConnectionValidation(candidate, true);
        }

        void BeginConnectionValidation(NutDiscoveryCandidate candidate, bool remember)
        {
            int generation = BeginDiscoveryOperation();
            discoveryStatus.Text = Localization.F("正在验证 {0}:{1} / {2}，暂不监测。", candidate.Host, candidate.Port, candidate.UpsName);
            CancellationToken cancellation = discoveryCancellation.Token;
            Task.Run(delegate
            {
                NutSnapshot received = null; Exception error = null;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    received = NutClient.Read(candidate.Host, candidate.Port, candidate.UpsName, 2500, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                }
                catch (Exception ex) { error = ex; }
                Ui(delegate { CompleteConnectionValidation(generation, candidate, received, error, remember); });
            });
        }

        void CompleteConnectionValidation(int generation, NutDiscoveryCandidate candidate, NutSnapshot received, Exception error, bool remember)
        {
            if (generation != connectionGeneration) return;
            discoveryBusy = false; connectionReady = false; sample = null; config.Armed = false;
            if (error == null && discoveryCancellation != null && discoveryCancellation.IsCancellationRequested)
                error = new OperationCanceledException();
            if (error == null && (received == null || String.IsNullOrWhiteSpace(received.Status) ||
                received.ReceivedUtc > DateTime.UtcNow || (DateTime.UtcNow - received.ReceivedUtc).TotalSeconds > config.StaleSeconds))
                error = new InvalidOperationException(Localization.T("UPS 未返回有效的当前状态。"));
            if (error == null)
            {
                try
                {
                    var updated = config.Copy();
                    if (remember)
                    {
                        bool changed = updated.Host != candidate.Host || updated.Port != candidate.Port || updated.UpsName != candidate.UpsName;
                        updated.Host = candidate.Host; updated.Port = candidate.Port; updated.UpsName = candidate.UpsName;
                        updated.ConnectionConfirmed = true;
                        if (changed) { updated.AboveRatingAccepted = false; chart.Reset(); plottedAt = DateTime.MinValue; }
                    }
                    if (!updated.ConnectionConfirmed || updated.Host != candidate.Host || updated.Port != candidate.Port || updated.UpsName != candidate.UpsName)
                        throw new InvalidOperationException(Localization.T("请先选择并确认一台 UPS。"));
                    updated.Armed = false; updated.Save(ConfigPath); config = updated;
                    sample = received; connectionReady = true; nextPoll = DateTime.UtcNow.AddSeconds(2); lastError = "";
                    LoadControls(); OnSettingsSaved();
                    discoveryStatus.Text = Localization.F("已确认 {0}:{1} / {2}。正在只读监测，自动保护关闭。", config.Host, config.Port, config.UpsName);
                    Notice(discoveryStatus.Text, false); Navigate(0);
                }
                catch (Exception ex) { error = ex; }
            }
            if (error != null)
            {
                connectionReady = false; sample = null;
                discoveryStatus.Text = error is OperationCanceledException ? Localization.T("验证已取消，监测和保护均未启用。") :
                    Localization.F("目标验证失败，监测和保护均未启用：{0}", error.Message);
                Notice(discoveryStatus.Text, false); Navigate(2); ShowWindow();
            }
            UpdateDiscoveryControls();
        }
    }
}
