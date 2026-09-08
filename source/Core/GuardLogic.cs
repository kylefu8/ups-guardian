using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace UpsGuardian
{
    public sealed class GuardSettings
    {
        // A new profile starts unconfigured. The Windows UI must not poll
        // until the user supplies a NUT host; an empty value remains valid so
        // settings can be saved while setup is incomplete.
        public string Host = "";
        public int Port = 3493;
        public string UpsName = "ups";
        public bool Armed = false;
        public bool UsePercent = true;
        public double LoadThreshold = 80;
        public double RecoveryMargin = 10;
        public int HighConfirmSeconds = 5;
        public int RecoverySeconds = 30;
        public int CpuMaximum = 50;
        // Zero explicitly means CPU-only reduction until a supported GPU
        // limit is detected and selected by the user.
        public int GpuWatts = 0;
        public double ChargeThreshold = 50;
        public int RuntimeThresholdSeconds = 180;
        public int LowConfirmSeconds = 5;
        public int HibernateCountdownSeconds = 20;
        public int StaleSeconds = 12;
        public bool StartAtLogon = false;
        public bool AboveRatingAccepted = false;

        private static readonly Regex HostPattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._:%-]{0,252}\z",
            RegexOptions.CultureInvariant);

        public void Validate()
        {
            string host = Host ?? "";
            if (host.Length > 253 || (host.Length > 0 &&
                (string.IsNullOrWhiteSpace(host) || !HostPattern.IsMatch(host))) || Port < 1 || Port > 65535 ||
                string.IsNullOrWhiteSpace(UpsName)) throw new InvalidDataException("UPS 地址、端口或设备名称无效。");
            if (double.IsNaN(LoadThreshold) || double.IsInfinity(LoadThreshold) || LoadThreshold <= 0 ||
                LoadThreshold > (UsePercent ? 150 : 10000)) throw new InvalidDataException("负载阈值无效。");
            if (RecoveryMargin <= 0 || RecoveryMargin >= LoadThreshold || double.IsNaN(RecoveryMargin) || double.IsInfinity(RecoveryMargin))
                throw new InvalidDataException("恢复回差必须大于 0，并小于触发阈值。");
            if (CpuMaximum < 1 || CpuMaximum > 100 || GpuWatts < 0 || GpuWatts > 5000)
                throw new InvalidDataException("CPU 限制为 1–100%；GPU 限制应为 0（CPU-only）或 1–5000W。");
            if (ChargeThreshold < 1 || ChargeThreshold > 100 || double.IsNaN(ChargeThreshold) || double.IsInfinity(ChargeThreshold) ||
                RuntimeThresholdSeconds < 30 || RuntimeThresholdSeconds > 3600 ||
                HibernateCountdownSeconds < 0 || HibernateCountdownSeconds > 120)
                throw new InvalidDataException("电池或休眠设置无效。");
            if (HighConfirmSeconds < 1 || HighConfirmSeconds > 120 || RecoverySeconds < 1 || RecoverySeconds > 300 ||
                LowConfirmSeconds < 1 || LowConfirmSeconds > 60 || StaleSeconds < 5 || StaleSeconds > 60)
                throw new InvalidDataException("确认时间或数据有效期无效。");
        }
        public static GuardSettings Load(string path)
        {
            if (!File.Exists(path)) return new GuardSettings();
            using (var stream = File.OpenRead(path))
            { var value = (GuardSettings)new XmlSerializer(typeof(GuardSettings)).Deserialize(stream); value.Validate(); return value; }
        }
        public void Save(string path)
        {
            Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var stream = File.Create(temp)) new XmlSerializer(typeof(GuardSettings)).Serialize(stream, this);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }

    public sealed class GuardDecision
    {
        public bool Fresh;
        public bool? Reduce;
        public int? HibernateInSeconds;
        public bool HibernateNow;
        public string Message;
    }

    // Pure decision logic: no network, power configuration, shutdown or sleep calls.
    public sealed class GuardLogic
    {
        DateTime? highSince, recoverSince, lowSince, countdownSince;
        public void Reset() { highSince = recoverSince = lowSince = countdownSince = null; }
        public GuardDecision Evaluate(NutSnapshot sample, GuardSettings settings, DateTime now, bool armed)
        {
            var result = new GuardDecision();
            result.Fresh = sample != null && now >= sample.ReceivedUtc &&
                (now - sample.ReceivedUtc).TotalSeconds <= settings.StaleSeconds;
            if (!armed) { Reset(); result.Message = "只读监测 · 自动动作关闭"; return result; }
            if (!result.Fresh)
            {
                highSince = recoverSince = lowSince = countdownSince = null;
                result.Message = "UPS 数据失效 · 暂停新动作，保留已有功耗限制"; return result;
            }
            double? power = settings.UsePercent ? sample.LoadPercent : (sample.MeasuredWatts ?? sample.EstimatedWatts);
            if (power.HasValue && power.Value > settings.LoadThreshold)
            {
                recoverSince = null;
                if (!highSince.HasValue) highSince = now;
                if ((now - highSince.Value).TotalSeconds >= settings.HighConfirmSeconds) result.Reduce = true;
            }
            else if (power.HasValue && power.Value < settings.LoadThreshold - settings.RecoveryMargin)
            {
                highSince = null;
                if (!recoverSince.HasValue) recoverSince = now;
                if ((now - recoverSince.Value).TotalSeconds >= settings.RecoverySeconds) result.Reduce = false;
            }
            else { highSince = recoverSince = null; }

            bool batteryLow = sample.OnBattery &&
                ((sample.ChargePercent.HasValue && sample.ChargePercent.Value < settings.ChargeThreshold) ||
                 (sample.RuntimeSeconds.HasValue && sample.RuntimeSeconds.Value < settings.RuntimeThresholdSeconds));
            if (!batteryLow) { lowSince = countdownSince = null; }
            else
            {
                if (!lowSince.HasValue) lowSince = now;
                if ((now - lowSince.Value).TotalSeconds >= settings.LowConfirmSeconds)
                {
                    if (!countdownSince.HasValue) countdownSince = now;
                    int remaining = Math.Max(0, settings.HibernateCountdownSeconds - (int)(now - countdownSince.Value).TotalSeconds);
                    result.HibernateInSeconds = remaining;
                    // The caller acknowledges by disarming; a busy actuator must not lose this decision.
                    if (remaining == 0) result.HibernateNow = true;
                }
            }
            result.Message = result.HibernateInSeconds.HasValue ?
                "电池保护 · " + result.HibernateInSeconds.Value + " 秒后休眠" :
                (sample.OnBattery ? "电池供电 · 自动保护已启用" : "市电供电 · 自动保护已启用");
            if (!power.HasValue) result.Message += " · 负载数据缺失";
            return result;
        }
    }
}
