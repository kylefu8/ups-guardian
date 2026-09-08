using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace UpsGuardian
{
    /// <summary>
    /// Small, platform-neutral gettext-like catalog loader.  The Windows UI can
    /// call this class without taking a dependency on WinForms, and a future
    /// macOS front end can reuse the same catalogs and language rules.
    /// </summary>
    public static class Localization
    {
        const string EnglishCode = "en";
        static readonly object Sync = new object();
        static readonly string[] Codes = { "zh-CN", "en", "ja", "ko", "fr", "de", "es" };
        static readonly Dictionary<string, Dictionary<string, string>> Catalogs =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex MinutesSeconds = new Regex(
            "^(\\d+)分(\\d+)秒$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex HibernateCountdown = new Regex(
            "^电池保护 · (\\d+) 秒后休眠$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex RatedLoad = new Regex(
            "^额定 ([0-9]+(?:\\.[0-9]+)?)W  ·  当前负载 ([0-9]+(?:\\.[0-9]+)?)%$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex RecoverySummary = new Regex(
            "^负载回落到 ([0-9]+(?:\\.[0-9]+)?)(W|%) 以下并持续 30 秒后，恢复本机原设置。$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static string currentLanguage;

        static Localization()
        {
            currentLanguage = ResolveLanguage(null);
        }

        /// <summary>Current normalized language code.</summary>
        public static string Language
        {
            get { lock (Sync) return currentLanguage; }
        }

        /// <summary>Supported language codes in the order shown by the UI.</summary>
        public static string[] SupportedCodes
        {
            get { return (string[])Codes.Clone(); }
        }

        /// <summary>
        /// Resolves a user preference or BCP-47 culture name to a supported
        /// catalog.  Empty, "system", and "auto" use the current UI culture.
        /// Unknown cultures intentionally fall back to English.
        /// </summary>
        public static string ResolveLanguage(string preference)
        {
            string value = preference;
            if (String.IsNullOrWhiteSpace(value) ||
                String.Equals(value.Trim(), "system", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value.Trim(), "跟随系统", StringComparison.Ordinal))
                value = CultureInfo.CurrentUICulture == null ? "en" : CultureInfo.CurrentUICulture.Name;

            value = (value ?? String.Empty).Trim();
            if (value.Length == 0) return EnglishCode;
            string lower = value.ToLowerInvariant().Replace('_', '-');
            if (lower == "zh" || lower.StartsWith("zh-", StringComparison.Ordinal)) return "zh-CN";
            if (lower == "en" || lower.StartsWith("en-", StringComparison.Ordinal)) return "en";
            if (lower == "ja" || lower.StartsWith("ja-", StringComparison.Ordinal)) return "ja";
            if (lower == "ko" || lower.StartsWith("ko-", StringComparison.Ordinal)) return "ko";
            if (lower == "fr" || lower.StartsWith("fr-", StringComparison.Ordinal)) return "fr";
            if (lower == "de" || lower.StartsWith("de-", StringComparison.Ordinal)) return "de";
            if (lower == "es" || lower.StartsWith("es-", StringComparison.Ordinal)) return "es";
            return EnglishCode;
        }

        /// <summary>Changes the active catalog immediately.</summary>
        public static void SetLanguage(string code)
        {
            string resolved = ResolveLanguage(code);
            lock (Sync)
            {
                currentLanguage = resolved;
                // Load eagerly so a bad/missing embedded resource is discovered
                // on language selection, while lookup remains safe and fast.
                LoadCatalog(resolved);
            }
        }

        /// <summary>Translates one exact source string, with bounded runtime patterns.</summary>
        public static string T(string sourceText)
        {
            if (sourceText == null) return null;
            string language;
            lock (Sync) language = currentLanguage;

            string exact = Lookup(language, sourceText);
            if (exact != null) return exact;

            // Runtime labels are matched as complete strings.  This avoids
            // replacing text inside device names, server addresses, or errors.
            Match match = MinutesSeconds.Match(sourceText);
            if (match.Success)
                return FormatCatalog(language, "{0}分{1}秒", match.Groups[1].Value, match.Groups[2].Value);
            match = HibernateCountdown.Match(sourceText);
            if (match.Success)
                return FormatCatalog(language, "电池保护 · {0} 秒后休眠", match.Groups[1].Value);
            match = RatedLoad.Match(sourceText);
            if (match.Success)
                return FormatCatalog(language, "额定 {0}W  ·  当前负载 {1}%", match.Groups[1].Value, match.Groups[2].Value);
            match = RecoverySummary.Match(sourceText);
            if (match.Success)
                return FormatCatalog(language, "负载回落到 {0}{1} 以下并持续 30 秒后，恢复本机原设置。", match.Groups[1].Value, match.Groups[2].Value);
            return sourceText;
        }

        /// <summary>Translates a format template and applies String.Format.</summary>
        public static string F(string sourceTemplate, params object[] args)
        {
            string translated = T(sourceTemplate);
            return String.Format(CultureInfo.CurrentCulture, translated, args ?? new object[0]);
        }

        static string FormatCatalog(string language, string sourceTemplate, params object[] args)
        {
            string translated = Lookup(language, sourceTemplate);
            if (translated == null) translated = sourceTemplate;
            return String.Format(CultureInfo.CurrentCulture, translated, args);
        }

        static string Lookup(string language, string sourceText)
        {
            Dictionary<string, string> selected = LoadCatalog(language);
            string result;
            if (selected != null && selected.TryGetValue(sourceText, out result) && !String.IsNullOrEmpty(result))
                return result;
            if (!String.Equals(language, EnglishCode, StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, string> english = LoadCatalog(EnglishCode);
                if (english != null && english.TryGetValue(sourceText, out result) && !String.IsNullOrEmpty(result))
                    return result;
            }
            return null;
        }

        static Dictionary<string, string> LoadCatalog(string language)
        {
            string code = ResolveLanguage(language);
            Dictionary<string, string> cached;
            if (Catalogs.TryGetValue(code, out cached)) return cached;

            var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
            Assembly assembly = typeof(Localization).Assembly;
            string resourceName = "Guardian.Locale." + code + ".json";
            try
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                        var loaded = serializer.ReadObject(stream) as Dictionary<string, string>;
                        if (loaded != null)
                            foreach (var pair in loaded)
                                if (pair.Key != null && !String.IsNullOrEmpty(pair.Value)) catalog[pair.Key] = pair.Value;
                    }
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (SerializationException) { }
            Catalogs[code] = catalog;
            return catalog;
        }
    }
}
