using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using UpsGuardian;

internal static class LocalizationTests
{
    static int passed;

    static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        passed++;
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
    }

    static Dictionary<string, string> Catalog(string code)
    {
        string name = "Guardian.Locale." + code + ".json";
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            Assert(stream != null, "embedded resource " + code);
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                new System.Runtime.Serialization.Json.DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            return (Dictionary<string, string>)serializer.ReadObject(stream);
        }
    }

    static string Placeholders(string value)
    {
        MatchCollection matches = Regex.Matches(value ?? String.Empty, @"\{\d+\}");
        var result = String.Empty;
        foreach (Match match in matches) result += match.Value + ";";
        return result;
    }

    static void TestCatalogParity()
    {
        var english = Catalog("en");
        Assert(english.Count >= 200, "English catalog has the complete UI catalog");
        foreach (string code in Localization.SupportedCodes)
        {
            var catalog = Catalog(code);
            Check(catalog.Count == english.Count, code + " catalog key count");
            foreach (var pair in english)
            {
                string translated;
                Check(catalog.TryGetValue(pair.Key, out translated), code + " contains " + pair.Key);
                Check(!String.IsNullOrWhiteSpace(translated), code + " has a non-empty translation for " + pair.Key);
                Check(Placeholders(pair.Value) == Placeholders(translated), code + " placeholders for " + pair.Key);
            }
            Assert(true, code + " catalog parity and placeholders");
        }
    }

    static void TestLanguageResolution()
    {
        Assert(Localization.ResolveLanguage("zh-Hans") == "zh-CN", "Chinese culture mapping");
        Assert(Localization.ResolveLanguage("ja-JP") == "ja", "Japanese culture mapping");
        Assert(Localization.ResolveLanguage("ko-KR") == "ko", "Korean culture mapping");
        Assert(Localization.ResolveLanguage("fr-FR") == "fr", "French culture mapping");
        Assert(Localization.ResolveLanguage("de-DE") == "de", "German culture mapping");
        Assert(Localization.ResolveLanguage("es-ES") == "es", "Spanish culture mapping");
        Assert(Localization.ResolveLanguage("unknown") == "en", "unknown culture falls back to English");
        Assert(Localization.ResolveLanguage("system").Length > 0, "system preference resolves");
    }

    static void TestTranslationsAndPatterns()
    {
        Localization.SetLanguage("en");
        Assert(Localization.T("保护策略") == "Protection", "exact English translation");
        Assert(Localization.T("2分8秒") == "2m 8s", "minute and second runtime pattern");
        Assert(Localization.T("电池保护 · 17 秒后休眠") == "Battery protection · hibernates in 17 seconds", "hibernate runtime pattern");
        Assert(Localization.T("额定 650W  ·  当前负载 37%") == "Rated 650W  ·  Current load 37%", "rated load runtime pattern");
        Assert(Localization.F("负载回落到 {0}{1} 以下并持续 30 秒后，恢复本机原设置。", 700, "W") ==
            "After load stays below 700W for 30 seconds, restore the original local settings.", "format template output");
        Assert(Localization.F("发现新版本 {0}", "v0.2.0") == "New version v0.2.0 available", "update version template output");
        Assert(Localization.F("下载进度：{0}%", 87) == "Download progress: 87%", "download progress template output");
        Assert(Localization.F("{0} · 负载 {1} · 额定 {2} · 更新 {3}", "Measured power", "37%", "650W", "12:00:00") ==
            "Measured power · Load 37% · Rated 650W · Updated 12:00:00", "status detail template output");
        Assert(Localization.F("GPU 功率范围为 {0}–{1}W；0 表示不控制 GPU。", 100, 285) ==
            "GPU power range is 100–285 W; 0 means do not control the GPU.", "GPU capability template output");

        Localization.SetLanguage("ja");
        Assert(Localization.T("保护策略") == "保護ポリシー", "Japanese exact translation");
        Assert(Localization.F("{0}分{1}秒", 2, 8) == "2分8秒", "Japanese template output");
        Localization.SetLanguage("ko");
        Assert(Localization.T("保护策略") == "보호 정책", "Korean exact translation");
        Localization.SetLanguage("fr");
        Assert(Localization.T("保护策略") == "Protection", "French exact translation");
        Localization.SetLanguage("de");
        Assert(Localization.T("保护策略") == "Schutz", "German exact translation");
        Localization.SetLanguage("es");
        Assert(Localization.T("保护策略") == "Protección", "Spanish exact translation");
    }

    static void TestFallbackAndUnknownText()
    {
        Localization.SetLanguage("de");
        const string device = "APC BX1200CI-CN";
        Assert(Localization.T(device) == device, "unknown device text remains unchanged");
        Assert(Localization.T(device + " / 保护策略") == device + " / 保护策略", "embedded device text is not globally replaced");
        Localization.SetLanguage("unsupported-code");
        Assert(Localization.Language == "en", "unsupported language uses English");
        Assert(Localization.T("保护策略") == "Protection", "English fallback translation");
        Assert(Localization.T(null) == null, "null source remains null");
    }

    public static int Main()
    {
        try
        {
            TestCatalogParity();
            TestLanguageResolution();
            TestTranslationsAndPatterns();
            TestFallbackAndUnknownText();
            Console.WriteLine("All " + passed + " localization checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
