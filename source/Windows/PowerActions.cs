using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace UpsGuardian
{
    /// <summary>Owns a reversible CPU/GPU reduction and a durable recovery record.</summary>
    public sealed class WindowsPowerActions : IPowerActions
    {
        private const int RecoveryVersion = 2;
        private const int LegacyRecoveryVersion = 1;
        private const long MaxRecoveryBytes = 64 * 1024;
        private const double NumericTolerance = 0.01;

        private readonly string _dataDirectory;
        private readonly string _recoveryPath;
        private readonly IPowerActionsBackend _backend;
        private RecoveryState _recovery;
        private bool _hasRecovery;
        private string _recoverySummary;

        public WindowsPowerActions(string dataDirectory)
            : this(dataDirectory, new WindowsPowerActionsBackend())
        {
        }

        /// <summary>
        /// Test seam.  Constructing this object only reads recovery.xml; the
        /// backend is not queried and no power action is performed.
        /// </summary>
        public WindowsPowerActions(string dataDirectory, IPowerActionsBackend backend)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("dataDirectory must be non-empty.", "dataDirectory");
            if (backend == null)
                throw new ArgumentNullException("backend");

            _dataDirectory = Path.GetFullPath(dataDirectory);
            _recoveryPath = Path.Combine(_dataDirectory, "recovery.xml");
            _backend = backend;
            _recoverySummary = "无恢复备份。";
            LoadRecovery();
        }

        public bool HasRecovery
        {
            get { return _hasRecovery; }
        }

        public string RecoverySummary
        {
            get { return _recoverySummary; }
        }

        /// <summary>
        /// Performs only read-only capability probes. Missing NVIDIA tooling
        /// is reported as an unavailable GPU while CPU/UPS monitoring can
        /// continue normally.
        /// </summary>
        public PowerCapabilities DetectCapabilities()
        {
            try
            {
                IPowerCapabilitiesBackend capabilityBackend = _backend as IPowerCapabilitiesBackend;
                PowerCapabilities result = capabilityBackend == null ? null : capabilityBackend.DetectCapabilities();
                if (result == null)
                {
                    result = new PowerCapabilities();
                    result.PlatformName = "Windows";
                    result.SessionProtectionMode = "SeShutdownPrivilege";
                    result.SuspendSupported = true;
                    result.SuspendStatus = "Windows SetSuspendState";
                }
                return result ?? new PowerCapabilities();
            }
            catch (Exception ex)
            {
                PowerCapabilities unavailable = new PowerCapabilities();
                unavailable.PlatformName = "Windows";
                unavailable.SessionProtectionMode = "SeShutdownPrivilege";
                unavailable.GpuStatus = "能力探测失败：" + ex.Message;
                unavailable.SuspendStatus = "Windows SetSuspendState";
                return unavailable;
            }
        }

        /// <summary>Returns whether this process currently has administrator rights.</summary>
        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public void Reduce(int cpuMaximumPercent, int gpuWatts)
        {
            ValidateCpuMaximum(cpuMaximumPercent);
            if (gpuWatts < 0 || gpuWatts > 5000)
                throw new ArgumentOutOfRangeException("gpuWatts", "gpuWatts must be 0 (CPU-only) or between 1 and 5000.");
            bool gpuManaged = gpuWatts > 0;
            if (_hasRecovery)
                throw new InvalidOperationException("已有恢复备份；请先恢复当前限制，再应用新的限制。");
            if (!_backend.IsAdministrator)
                throw new UnauthorizedAccessException("修改 CPU/GPU 功耗限制需要管理员权限。");

            Guid scheme = _backend.GetActiveScheme();
            int originalCpu = _backend.ReadCpuMaximum(scheme);
            if (originalCpu < 1 || originalCpu > 100)
                throw new InvalidOperationException("Windows 返回的 CPU 最大处理器状态无效。");

            GpuPowerInfo gpu = null;
            if (gpuManaged)
            {
                gpu = _backend.ReadGpuPower();
                ValidateGpuInfo(gpu);
                if (gpuWatts < gpu.MinimumWatts - NumericTolerance || gpuWatts > gpu.MaximumWatts + NumericTolerance)
                    throw new ArgumentOutOfRangeException("gpuWatts", "gpuWatts 超出当前显卡报告的功率范围。");
            }

            // A reduction request must never raise an existing stricter cap.
            // Store the actual value that will be enforced so a later restore
            // can distinguish our setting from a user change.
            int effectiveCpuMaximum = Math.Min(cpuMaximumPercent, originalCpu);
            double effectiveGpuPower = gpuManaged ? Math.Min((double)gpuWatts, gpu.CurrentWatts) : 0.0;

            RecoveryState state = new RecoveryState();
            state.Version = RecoveryVersion;
            state.ActiveScheme = scheme;
            state.OriginalCpuMaximum = originalCpu;
            state.EnforcedCpuMaximum = effectiveCpuMaximum;
            state.GpuManaged = gpuManaged;
            state.OriginalGpuPower = gpuManaged ? gpu.CurrentWatts : 0.0;
            state.EnforcedGpuPower = gpuManaged ? effectiveGpuPower : 0.0;
            state.CreatedUtc = DateTime.UtcNow;

            // The durable backup is written and flushed before the first
            // platform mutation.  If any subsequent action is uncertain it
            // remains available for a later explicit Restore call.
            SaveRecovery(state);
            _recovery = state;
            _hasRecovery = true;
            _recoverySummary = "已保存原始 CPU/GPU 设置，准备应用限制。";

            bool cpuAttempted = false;
            bool gpuAttempted = false;
            try
            {
                if (originalCpu != effectiveCpuMaximum)
                {
                    EnsureSchemeStillActive(scheme);
                    cpuAttempted = true;
                    _backend.WriteCpuMaximum(scheme, effectiveCpuMaximum);
                    // Refresh only when the captured scheme is still active;
                    // never switch a scheme changed by the user.
                    EnsureSchemeStillActive(scheme);
                    _backend.RefreshActiveScheme(scheme);
                }

                if (gpuManaged && !NearlyEqual(gpu.CurrentWatts, effectiveGpuPower))
                {
                    gpuAttempted = true;
                    _backend.SetGpuPower(effectiveGpuPower);
                }

                VerifyCpu(scheme, effectiveCpuMaximum);
                if (gpuManaged)
                    VerifyGpu(effectiveGpuPower);

                _recoverySummary = "已应用 CPU " + effectiveCpuMaximum.ToString(CultureInfo.InvariantCulture) +
                    (gpuManaged ? "%、GPU " + FormatWatts(effectiveGpuPower) + "W 限制" : "% 限制（CPU-only）") +
                    "；恢复备份已保留。";
            }
            catch (Exception actionFailure)
            {
                List<string> rollbackMessages = new List<string>();
                if (gpuAttempted)
                    TryRollbackGpu(state, rollbackMessages);
                if (cpuAttempted)
                    TryRollbackCpu(state, rollbackMessages);

                _recoverySummary = "应用限制失败，恢复备份已保留。" +
                    (rollbackMessages.Count == 0 ? "" : "回滚结果：" + String.Join("；", rollbackMessages.ToArray()));
                throw new InvalidOperationException(_recoverySummary, actionFailure);
            }
        }

        public void Restore()
        {
            if (!_hasRecovery)
            {
                if (File.Exists(_recoveryPath))
                    throw new InvalidOperationException("恢复备份存在但无法读取，文件已保留：" + _recoveryPath);
                _recoverySummary = "无恢复备份。";
                return;
            }

            List<string> messages = new List<string>();
            bool safe = true;

            try
            {
                RestoreCpu(_recovery, messages, ref safe);
            }
            catch (Exception ex)
            {
                safe = false;
                messages.Add("CPU 恢复失败：" + ex.Message);
            }

            // CPU and GPU are independent controls.  A failure while reading
            // or restoring one must not prevent the other from being brought
            // back to its owned value.  Keep the recovery file whenever any
            // part is uncertain so a later explicit retry can finish it.
            if (_recovery != null && _recovery.GpuManaged)
            {
                try
                {
                    RestoreGpu(_recovery, messages, ref safe);
                }
                catch (Exception ex)
                {
                    safe = false;
                    messages.Add("GPU 恢复失败：" + ex.Message);
                }
            }

            if (!safe)
            {
                _recoverySummary = "恢复未完成，备份已保留。" + JoinMessages(messages);
                throw new InvalidOperationException(_recoverySummary);
            }

            try
            {
                File.Delete(_recoveryPath);
            }
            catch (Exception ex)
            {
                _recoverySummary = "设置已恢复，但无法删除恢复备份；文件已保留。" + ex.Message;
                throw new IOException(_recoverySummary, ex);
            }

            _recovery = null;
            _hasRecovery = false;
            _recoverySummary = messages.Count == 0 ? "已恢复原始 CPU/GPU 设置。" :
                "已恢复原始 CPU/GPU 设置。" + JoinMessages(messages);
        }

        public void Hibernate()
        {
            _backend.Hibernate();
        }

        private void RestoreCpu(RecoveryState state, List<string> messages, ref bool safe)
        {
            int current = _backend.ReadCpuMaximum(state.ActiveScheme);
            if (current == state.OriginalCpuMaximum)
                return;
            if (current != state.EnforcedCpuMaximum)
            {
                safe = false;
                messages.Add("CPU 当前值为 " + current.ToString(CultureInfo.InvariantCulture) +
                    "%，与本程序限制不同，已跳过覆盖。");
                return;
            }

            _backend.WriteCpuMaximum(state.ActiveScheme, state.OriginalCpuMaximum);
            Guid active = _backend.GetActiveScheme();
            if (active == state.ActiveScheme)
                _backend.RefreshActiveScheme(state.ActiveScheme);
            else
                messages.Add("活动电源方案已被外部更改；已恢复原方案设置，未强制切换方案。");
            int restored = _backend.ReadCpuMaximum(state.ActiveScheme);
            if (restored != state.OriginalCpuMaximum)
                throw new InvalidOperationException("CPU 恢复读回值与原始值不符。");
        }

        private void RestoreGpu(RecoveryState state, List<string> messages, ref bool safe)
        {
            GpuPowerInfo currentInfo = _backend.ReadGpuPower();
            ValidateGpuInfo(currentInfo);
            double current = currentInfo.CurrentWatts;
            if (NearlyEqual(current, state.OriginalGpuPower))
                return;
            if (!NearlyEqual(current, state.EnforcedGpuPower))
            {
                safe = false;
                messages.Add("GPU 当前功率限制为 " + FormatWatts(current) +
                    "W，与本程序限制不同，已跳过覆盖。");
                return;
            }

            _backend.SetGpuPower(state.OriginalGpuPower);
            GpuPowerInfo restoredInfo = _backend.ReadGpuPower();
            ValidateGpuInfo(restoredInfo);
            if (!NearlyEqual(restoredInfo.CurrentWatts, state.OriginalGpuPower))
                throw new InvalidOperationException("GPU 恢复读回值与原始值不符。");
        }

        private void TryRollbackCpu(RecoveryState state, List<string> messages)
        {
            try
            {
                int current = _backend.ReadCpuMaximum(state.ActiveScheme);
                if (current == state.OriginalCpuMaximum)
                {
                    messages.Add("CPU 已处于原始值");
                    return;
                }
                if (current != state.EnforcedCpuMaximum)
                {
                    messages.Add("CPU 回滚跳过：当前值已变化");
                    return;
                }
                _backend.WriteCpuMaximum(state.ActiveScheme, state.OriginalCpuMaximum);
                if (_backend.GetActiveScheme() == state.ActiveScheme)
                    _backend.RefreshActiveScheme(state.ActiveScheme);
                if (_backend.ReadCpuMaximum(state.ActiveScheme) != state.OriginalCpuMaximum)
                {
                    messages.Add("CPU 回滚读回值不符");
                    return;
                }
                messages.Add("CPU 已回滚");
            }
            catch (Exception ex)
            {
                messages.Add("CPU 回滚不确定：" + ex.Message);
            }
        }

        private void TryRollbackGpu(RecoveryState state, List<string> messages)
        {
            try
            {
                GpuPowerInfo current = _backend.ReadGpuPower();
                ValidateGpuInfo(current);
                if (NearlyEqual(current.CurrentWatts, state.OriginalGpuPower))
                {
                    messages.Add("GPU 已处于原始值");
                    return;
                }
                if (!NearlyEqual(current.CurrentWatts, state.EnforcedGpuPower))
                {
                    messages.Add("GPU 回滚跳过：当前功率限制已变化");
                    return;
                }
                _backend.SetGpuPower(state.OriginalGpuPower);
                GpuPowerInfo restored = _backend.ReadGpuPower();
                ValidateGpuInfo(restored);
                if (!NearlyEqual(restored.CurrentWatts, state.OriginalGpuPower))
                {
                    messages.Add("GPU 回滚读回值不符");
                    return;
                }
                messages.Add("GPU 已回滚");
            }
            catch (Exception ex)
            {
                messages.Add("GPU 回滚不确定：" + ex.Message);
            }
        }

        private void EnsureSchemeStillActive(Guid captured)
        {
            Guid current = _backend.GetActiveScheme();
            if (current != captured)
                throw new InvalidOperationException("活动电源方案已被外部更改，未强制切换方案。");
        }

        private void VerifyCpu(Guid scheme, int expected)
        {
            int actual = _backend.ReadCpuMaximum(scheme);
            if (actual != expected)
                throw new InvalidOperationException("CPU 限制读回值与目标不符。");
        }

        private void VerifyGpu(double expected)
        {
            GpuPowerInfo actual = _backend.ReadGpuPower();
            ValidateGpuInfo(actual);
            if (!NearlyEqual(actual.CurrentWatts, expected))
                throw new InvalidOperationException("GPU 功率限制读回值与目标不符。");
        }

        private void LoadRecovery()
        {
            if (!File.Exists(_recoveryPath))
                return;
            try
            {
                FileInfo info = new FileInfo(_recoveryPath);
                if (info.Length <= 0 || info.Length > MaxRecoveryBytes)
                    throw new InvalidDataException("恢复文件大小无效。");

                XmlDocument document = new XmlDocument();
                document.XmlResolver = null;
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                using (FileStream stream = new FileStream(_recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader reader = XmlReader.Create(stream, settings))
                {
                    document.Load(reader);
                }

                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "PowerRecovery")
                    throw new InvalidDataException("恢复文件根节点无效。");
                RecoveryState state = new RecoveryState();
                state.Version = ParseInt(root, "Version");
                if (state.Version != LegacyRecoveryVersion && state.Version != RecoveryVersion)
                    throw new InvalidDataException("不支持的恢复文件版本。");
                state.ActiveScheme = ParseGuid(root, "ActiveScheme");
                state.OriginalCpuMaximum = ParseInt(root, "OriginalCpuMaximum");
                state.EnforcedCpuMaximum = ParseInt(root, "EnforcedCpuMaximum");
                // Version 1 always managed a GPU. Version 2 records the
                // explicit flag so CPU-only recovery never probes NVIDIA.
                state.GpuManaged = state.Version == LegacyRecoveryVersion || ParseBool(root, "GpuManaged");
                state.OriginalGpuPower = state.GpuManaged ? ParseDouble(root, "OriginalGpuPower") : ParseOptionalDouble(root, "OriginalGpuPower", 0.0);
                state.EnforcedGpuPower = state.GpuManaged ? ParseDouble(root, "EnforcedGpuPower") : ParseOptionalDouble(root, "EnforcedGpuPower", 0.0);
                state.CreatedUtc = ParseDateTime(root, "CreatedUtc");
                ValidateRecovery(state);
                _recovery = state;
                _hasRecovery = true;
                _recoverySummary = "存在恢复备份（创建于 " + state.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "）。";
            }
            catch (Exception ex)
            {
                // Never delete or overwrite a recovery file that cannot be
                // understood.  Surface it as present so the UI can warn.
                _hasRecovery = true;
                _recoverySummary = "恢复备份无法读取，文件已保留：" + ex.Message;
            }
        }

        private void SaveRecovery(RecoveryState state)
        {
            Directory.CreateDirectory(_dataDirectory);
            string temporary = _recoveryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Encoding = new UTF8Encoding(false);
                settings.Indent = true;
                settings.OmitXmlDeclaration = false;
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        writer.WriteStartDocument();
                        writer.WriteStartElement("PowerRecovery");
                        writer.WriteElementString("Version", state.Version.ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("ActiveScheme", state.ActiveScheme.ToString("D"));
                        writer.WriteElementString("OriginalCpuMaximum", state.OriginalCpuMaximum.ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("EnforcedCpuMaximum", state.EnforcedCpuMaximum.ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("GpuManaged", state.GpuManaged ? "true" : "false");
                        writer.WriteElementString("OriginalGpuPower", state.OriginalGpuPower.ToString("0.###", CultureInfo.InvariantCulture));
                        writer.WriteElementString("EnforcedGpuPower", state.EnforcedGpuPower.ToString("0.###", CultureInfo.InvariantCulture));
                        writer.WriteElementString("CreatedUtc", state.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
                        writer.WriteEndElement();
                        writer.WriteEndDocument();
                    }
                    stream.Flush(true);
                }

                if (File.Exists(_recoveryPath))
                    throw new IOException("恢复备份已在写入过程中出现，拒绝覆盖它。");
                File.Move(temporary, _recoveryPath);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch { /* Preserve the durable recovery; orphan temp is harmless. */ }
                }
            }
        }

        private static void ValidateCpuMaximum(int value)
        {
            if (value < 1 || value > 100)
                throw new ArgumentOutOfRangeException("cpuMaximumPercent", "cpuMaximumPercent 必须在 1 到 100 之间。");
        }

        private static void ValidateGpuInfo(GpuPowerInfo info)
        {
            if (info == null || !IsFinite(info.MinimumWatts) || !IsFinite(info.MaximumWatts) || !IsFinite(info.CurrentWatts) ||
                info.MinimumWatts < 0.0 || info.MaximumWatts < info.MinimumWatts || info.MaximumWatts > 5000.0 ||
                info.CurrentWatts < info.MinimumWatts - NumericTolerance || info.CurrentWatts > info.MaximumWatts + NumericTolerance)
                throw new InvalidOperationException("nvidia-smi 返回的显卡功率范围或当前值无效。");
        }

        private static void ValidateRecovery(RecoveryState state)
        {
            ValidateCpuMaximum(state.OriginalCpuMaximum);
            ValidateCpuMaximum(state.EnforcedCpuMaximum);
            if (state.GpuManaged && (!IsFinite(state.OriginalGpuPower) || !IsFinite(state.EnforcedGpuPower) ||
                state.OriginalGpuPower <= 0.0 || state.EnforcedGpuPower <= 0.0 ||
                state.OriginalGpuPower > 5000.0 || state.EnforcedGpuPower > 5000.0))
                throw new InvalidDataException("恢复文件中的 GPU 功率无效。");
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) <= NumericTolerance;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static string FormatWatts(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string JoinMessages(List<string> messages)
        {
            return messages.Count == 0 ? "" : " " + String.Join("；", messages.ToArray());
        }

        private static string Required(XmlElement root, string name)
        {
            XmlElement child = root[name];
            if (child == null || String.IsNullOrWhiteSpace(child.InnerText))
                throw new InvalidDataException("恢复文件缺少 " + name + "。");
            return child.InnerText.Trim();
        }

        private static int ParseInt(XmlElement root, string name)
        {
            int value;
            if (!Int32.TryParse(Required(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("恢复文件中的 " + name + " 无效。");
            return value;
        }

        private static double ParseDouble(XmlElement root, string name)
        {
            double value;
            if (!Double.TryParse(Required(root, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("恢复文件中的 " + name + " 无效。");
            return value;
        }

        private static bool ParseBool(XmlElement root, string name)
        {
            bool value;
            if (!Boolean.TryParse(Required(root, name), out value))
                throw new InvalidDataException("恢复文件中的 " + name + " 无效。");
            return value;
        }

        private static double ParseOptionalDouble(XmlElement root, string name, double fallback)
        {
            XmlElement child = root[name];
            if (child == null || String.IsNullOrWhiteSpace(child.InnerText))
                return fallback;
            return ParseDouble(root, name);
        }

        private static Guid ParseGuid(XmlElement root, string name)
        {
            Guid value;
            if (!Guid.TryParse(Required(root, name), out value))
                throw new InvalidDataException("恢复文件中的 " + name + " 无效。");
            return value;
        }

        private static DateTime ParseDateTime(XmlElement root, string name)
        {
            DateTime value;
            if (!DateTime.TryParse(Required(root, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                throw new InvalidDataException("恢复文件中的 " + name + " 无效。");
            return value.ToUniversalTime();
        }

        private sealed class RecoveryState
        {
            public int Version;
            public Guid ActiveScheme;
            public int OriginalCpuMaximum;
            public int EnforcedCpuMaximum;
            public bool GpuManaged;
            public double OriginalGpuPower;
            public double EnforcedGpuPower;
            public DateTime CreatedUtc;
        }
    }

    internal sealed class WindowsPowerActionsBackend : IPowerActionsBackend, IPowerCapabilitiesBackend
    {
        private static readonly Guid SubgroupProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid ProcessorThrottleMaximum = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        private const int CommandTimeoutMilliseconds = 5000;

        public bool IsAdministrator
        {
            get { return WindowsPowerActions.IsAdministrator(); }
        }

        public PowerCapabilities DetectCapabilities()
        {
            PowerCapabilities result = new PowerCapabilities();
            result.PlatformName = "Windows";
            result.SessionProtectionMode = "Hibernate";
            try
            {
                result.SuspendSupported = IsPwrHibernateAllowed();
                result.SuspendStatus = result.SuspendSupported ? "Windows hibernation is available." : "Windows hibernation is unavailable.";
            }
            catch (Exception ex)
            {
                result.SuspendSupported = false;
                result.SuspendStatus = "Could not detect Windows hibernation support: " + ex.Message;
            }

            try
            {
                Guid scheme = GetActiveScheme();
                int cpu = ReadCpuMaximum(scheme);
                result.CpuLimitSupported = cpu >= 1 && cpu <= 100;
            }
            catch (Exception ex)
            {
                result.CpuLimitSupported = false;
                result.SuspendStatus = result.SuspendStatus + "；CPU 能力探测失败：" + ex.Message;
            }

            try
            {
                GpuPowerInfo gpu = ReadGpuPower();
                result.GpuLimitSupported = true;
                result.GpuMinimumWatts = gpu.MinimumWatts;
                result.GpuMaximumWatts = gpu.MaximumWatts;
                result.GpuCurrentWatts = gpu.CurrentWatts;
                result.GpuStatus = "GPU 0 可用";
            }
            catch (Exception ex)
            {
                // nvidia-smi is optional. Monitoring and CPU limiting remain
                // available when the NVIDIA tool or a supported GPU is absent.
                result.GpuLimitSupported = false;
                result.GpuStatus = "GPU 不可用：" + ex.Message;
            }
            return result;
        }

        public Guid GetActiveScheme()
        {
            IntPtr pointer = IntPtr.Zero;
            uint result = PowerGetActiveScheme(IntPtr.Zero, out pointer);
            if (result != 0)
                throw new InvalidOperationException("PowerGetActiveScheme failed with Win32 error " + result.ToString(CultureInfo.InvariantCulture) + ".");
            try
            {
                return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid));
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                    LocalFree(pointer);
            }
        }

        public int ReadCpuMaximum(Guid scheme)
        {
            uint value;
            Guid subgroup = SubgroupProcessor;
            Guid setting = ProcessorThrottleMaximum;
            uint result = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
            if (result != 0)
                throw new InvalidOperationException("PowerReadACValueIndex failed with Win32 error " + result.ToString(CultureInfo.InvariantCulture) + ".");
            if (value > 100)
                throw new InvalidOperationException("Windows returned an invalid processor maximum.");
            return (int)value;
        }

        public void WriteCpuMaximum(Guid scheme, int value)
        {
            Guid subgroup = SubgroupProcessor;
            Guid setting = ProcessorThrottleMaximum;
            uint result = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, (uint)value);
            if (result != 0)
                throw new InvalidOperationException("PowerWriteACValueIndex failed with Win32 error " + result.ToString(CultureInfo.InvariantCulture) + ".");
        }

        public void RefreshActiveScheme(Guid scheme)
        {
            uint result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (result != 0)
                throw new InvalidOperationException("PowerSetActiveScheme failed with Win32 error " + result.ToString(CultureInfo.InvariantCulture) + ".");
        }

        public GpuPowerInfo ReadGpuPower()
        {
            string output = RunNvidiaSmi("-i 0 --query-gpu=power.min_limit,power.max_limit,power.limit --format=csv,noheader,nounits");
            string[] lines = output.Replace("\r", "").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                throw new InvalidOperationException("nvidia-smi 没有返回显卡功率信息。");
            string[] fields = lines[0].Split(new char[] { ',' });
            if (fields.Length < 3)
                throw new InvalidOperationException("nvidia-smi 功率信息格式无效。");
            double minimum = ParseGpuNumber(fields[0]);
            double maximum = ParseGpuNumber(fields[1]);
            double current = ParseGpuNumber(fields[2]);
            return new GpuPowerInfo(minimum, maximum, current);
        }

        public void SetGpuPower(double watts)
        {
            RunNvidiaSmi("-i 0 -pl " + watts.ToString("0.###", CultureInfo.InvariantCulture));
        }

        public void Hibernate()
        {
            const uint tokenAdjustPrivileges = 0x20;
            const uint tokenQuery = 0x8;
            const uint privilegeEnabled = 0x2;
            const int errorNotAllAssigned = 1300;
            IntPtr token = IntPtr.Zero;
            if (!OpenProcessToken(GetCurrentProcess(), tokenAdjustPrivileges | tokenQuery, out token))
                throw new InvalidOperationException("OpenProcessToken 失败，Win32 错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".");

            try
            {
                Luid shutdownPrivilege;
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out shutdownPrivilege))
                    throw new InvalidOperationException("LookupPrivilegeValue(SeShutdownPrivilege) 失败，Win32 错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".");

                TokenPrivileges enable = new TokenPrivileges();
                enable.PrivilegeCount = 1;
                enable.Luid = shutdownPrivilege;
                enable.Attributes = privilegeEnabled;
                int structureSize = Marshal.SizeOf(typeof(TokenPrivileges));
                IntPtr enablePointer = Marshal.AllocHGlobal(structureSize);
                IntPtr previousPointer = Marshal.AllocHGlobal(structureSize);
                IntPtr returnLengthPointer = Marshal.AllocHGlobal(sizeof(uint));
                Exception suspendFailure = null;
                Exception restoreFailure = null;
                try
                {
                    Marshal.StructureToPtr(enable, enablePointer, false);
                    bool adjusted = AdjustTokenPrivileges(token, false, enablePointer, structureSize,
                        previousPointer, returnLengthPointer);
                    int adjustError = Marshal.GetLastWin32Error();
                    if (!adjusted)
                        throw new InvalidOperationException("AdjustTokenPrivileges 启用失败，Win32 错误 " + adjustError.ToString(CultureInfo.InvariantCulture) + ".");
                    if (adjustError == errorNotAllAssigned)
                        throw new InvalidOperationException("当前进程没有 SeShutdownPrivilege。");
                    TokenPrivileges previous = (TokenPrivileges)Marshal.PtrToStructure(previousPointer, typeof(TokenPrivileges));

                    try
                    {
                        if (!SetSuspendState(true, false, false))
                        {
                            int suspendError = Marshal.GetLastWin32Error();
                            suspendFailure = new InvalidOperationException("SetSuspendState 休眠调用失败，Win32 错误 " + suspendError.ToString(CultureInfo.InvariantCulture) + ".");
                        }
                    }
                    catch (Exception ex)
                    {
                        suspendFailure = ex;
                    }
                    finally
                    {
                        try
                        {
                            Marshal.StructureToPtr(previous, previousPointer, true);
                            bool restored = AdjustTokenPrivileges(token, false, previousPointer, structureSize,
                                IntPtr.Zero, IntPtr.Zero);
                            int restoreError = Marshal.GetLastWin32Error();
                            if (!restored || restoreError == errorNotAllAssigned)
                                restoreFailure = new InvalidOperationException("恢复 SeShutdownPrivilege 状态失败，Win32 错误 " + restoreError.ToString(CultureInfo.InvariantCulture) + ".");
                        }
                        catch (Exception ex)
                        {
                            restoreFailure = ex;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(enablePointer);
                    Marshal.FreeHGlobal(previousPointer);
                    Marshal.FreeHGlobal(returnLengthPointer);
                }

                if (restoreFailure != null)
                    throw new InvalidOperationException("休眠后无法恢复 SeShutdownPrivilege 状态。", restoreFailure);
                if (suspendFailure != null)
                    throw suspendFailure;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        private static double ParseGpuNumber(string text)
        {
            string value = text.Trim();
            double parsed;
            if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                Double.IsNaN(parsed) || Double.IsInfinity(parsed))
                throw new InvalidOperationException("nvidia-smi 返回了无效功率值：" + value);
            return parsed;
        }

        private static string RunNvidiaSmi(string arguments)
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string executable = Path.Combine(systemDirectory, "nvidia-smi.exe");
            if (!Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new FileNotFoundException("未找到受支持的 nvidia-smi.exe（要求位于 System32）。", executable);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                if (!process.Start())
                    throw new InvalidOperationException("无法启动 nvidia-smi。");
                // Read both pipes concurrently; a redirected stderr pipe must
                // never be allowed to block the five-second process budget.
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(CommandTimeoutMilliseconds))
                {
                    try { process.Kill(); }
                    catch { }
                    try { process.WaitForExit(1000); }
                    catch { }
                    throw new TimeoutException("nvidia-smi 超过 5 秒未返回。");
                }
                string stdout = stdoutTask.Result;
                string stderr = stderrTask.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("nvidia-smi 失败：" + stderr.Trim());
                return stdout;
            }
        }

        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool IsPwrHibernateAllowed();

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            IntPtr newState,
            int bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
            [MarshalAs(UnmanagedType.U1)] bool forceCritical,
            [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
    }
}
