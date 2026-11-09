using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MultiTool;

/// <summary>介面語言。頭三個喺 LangTable.cs；其他喺 lang\&lt;code&gt;.json（包咗入 exe），冇翻譯就用英文。</summary>
public enum Language { ZhHant, ZhHans, En, Ja, Ko, Ru, Es, PtBr, De, Fr }

/// <summary>
/// 介面語言。程式碼入面寫嘅原文（廣東話）就係 key，對照表喺 LangTable.cs（繁中書面語 / 簡中 / 英文）。
/// 第一次開用系統語言；揀過就記喺 data\settings.txt。
/// </summary>
public static partial class Lang
{
    private static Language? _current;

    private static string SettingsFile => Path.Combine(AppPaths.Data, "settings.txt");

    public static Language Current
    {
        get
        {
            if (_current is { } c) return c;
            _current = Load() ?? FromSystem();
            return _current.Value;
        }
    }

    /// <summary>選單次序。</summary>
    public static readonly Language[] All =
        { Language.ZhHant, Language.ZhHans, Language.En, Language.Ja, Language.Ko, Language.Ru, Language.Es, Language.PtBr, Language.De, Language.Fr };

    public static string Code(Language l) => l switch
    {
        Language.ZhHans => "zh-Hans", Language.En => "en", Language.Ja => "ja", Language.Ko => "ko", Language.Ru => "ru",
        Language.Es => "es", Language.PtBr => "pt-BR", Language.De => "de", Language.Fr => "fr", _ => "zh-Hant"
    };

    /// <summary>每種語言用自己嘅文字顯示。</summary>
    public static string DisplayName(Language l) => l switch
    {
        Language.ZhHans => "简体中文", Language.En => "English", Language.Ja => "日本語", Language.Ko => "한국어", Language.Ru => "Русский",
        Language.Es => "Español", Language.PtBr => "Português (Brasil)", Language.De => "Deutsch", Language.Fr => "Français", _ => "繁體中文"
    };

    /// <summary>英文名（俾 AI 指示用）。</summary>
    public static string EnglishName(Language l) => l switch
    {
        Language.ZhHans => "Simplified Chinese", Language.En => "English", Language.Ja => "Japanese", Language.Ko => "Korean", Language.Ru => "Russian",
        Language.Es => "Spanish", Language.PtBr => "Brazilian Portuguese", Language.De => "German", Language.Fr => "French", _ => "Traditional Chinese"
    };

    /// <summary>只有繁 / 簡 / 英三個版本嘅內容（範本、AI 指示）用邊個：中文用返中文，其他一律英文。</summary>
    public static Language Base => Current is Language.ZhHant or Language.ZhHans ? Current : Language.En;

    private static Language? Parse(string s)
    {
        foreach (var l in All)
            if (Code(l).Equals(s.Trim(), StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    private static Language? Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return null;
            foreach (var line in File.ReadAllLines(SettingsFile))
                if (line.StartsWith("lang=")) return Parse(line[5..]);
        }
        catch { }
        return null;
    }

    /// <summary>系統語言：繁中（台 / 港 / 澳）、簡中（中國 / 新加坡）、日韓俄西葡德法，其他一律英文。</summary>
    private static Language FromSystem()
    {
        try
        {
            var ci = CultureInfo.CurrentUICulture;
            for (var c = ci; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            {
                string n = c.Name;
                if (n.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) || n is "zh-TW" or "zh-HK" or "zh-MO" or "zh-CHT") return Language.ZhHant;
                if (n.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) || n is "zh-CN" or "zh-SG" or "zh-CHS") return Language.ZhHans;
                if (c.Parent == c) break;
            }
            switch (ci.TwoLetterISOLanguageName)
            {
                case "zh": return Language.ZhHant;
                case "ja": return Language.Ja;
                case "ko": return Language.Ko;
                case "ru": return Language.Ru;
                case "es": return Language.Es;
                case "pt": return Language.PtBr;
                case "de": return Language.De;
                case "fr": return Language.Fr;
            }
        }
        catch { }
        return Language.En;
    }

    /// <summary>儲存（下次開程式生效）。</summary>
    public static void Save(Language l)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            var lines = new List<string>();
            if (File.Exists(SettingsFile))
                foreach (var line in File.ReadAllLines(SettingsFile))
                    if (!line.StartsWith("lang=")) lines.Add(line);
            lines.Add("lang=" + Code(l));
            File.WriteAllLines(SettingsFile, lines);
        }
        catch (Exception ex) { AppPaths.LogError("儲存語言", ex); }
    }

    /// <summary>翻譯一句（搵唔到就用英文，再搵唔到就照原文）。</summary>
    public static string T(string key)
    {
        if (Branded.TryGetValue(key, out var done)) return done;
        return Branded[key] = Brandify(Raw(key));
    }

    private static string Raw(string key)
    {
        var cur = Current;
        if (cur > Language.En && Extra.TryGetValue(key, out var x) && x.Length > 0) return x;
        if (Table.TryGetValue(key, out var v))
        {
            string s = v[cur <= Language.En ? (int)cur : (int)Language.En];
            if (s.Length > 0) return s;
        }
        return key;
    }

    // ---- 程式名：介面一律顯示 Brand（翻譯 key 照用舊名，插件嘅 [key="多功能工具"] 照樣揀得到）----
    /// <summary>程式名：中文介面叫「拉比工具箱」，其他語言叫「Rabi Toolkit」。</summary>
    public static string Brand => Current is Language.ZhHant or Language.ZhHans ? "拉比工具箱" : "Rabi Toolkit";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Branded = new();
    // 「MultiTool」做檔名 / 網址嘅一部分（MultiTool.exe、MultiTool-Releases、data\MultiTool…）唔換
    private static readonly System.Text.RegularExpressions.Regex BrandRx =
        new(@"(?<!\w|\w[.\-]|[\\/])MultiTool(?!\w|[.\-]\w|[\\/])|多功能工具", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static string Brandify(string s)
    {
        if (s.IndexOf("MultiTool", StringComparison.Ordinal) < 0 && s.IndexOf("多功能工具", StringComparison.Ordinal) < 0) return s;
        s = BrandRx.Replace(s, Brand);
        // 中文名：拎走「使用 拉比工具箱 編輯」入面多餘嘅空格
        if (Brand == "拉比工具箱")
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\p{IsCJKUnifiedIdeographs}|[，。、：（「])[ ]+(?=拉比工具箱)", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=拉比工具箱)[ ]+(?=\p{IsCJKUnifiedIdeographs}|[，。、：）」？！])", "");
        }
        return s;
    }

    /// <summary>介面上見到嘅字 → 原本嘅 key（插件用 [key="…"] 揀元件，唔理介面係咩語言）；唔係翻譯出嚟嘅就 null。</summary>
    public static string? KeyOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (_rev == null)
        {
            var d = new Dictionary<string, string>();
            foreach (var k in Table.Keys) d.TryAdd(T(k), k);
            foreach (var k in Extra.Keys) d.TryAdd(T(k), k);
            _rev = d;
        }
        if (_rev.TryGetValue(text, out var key)) return key;
        return Table.ContainsKey(text) ? text : null;
    }
    private static Dictionary<string, string>? _rev;

    // ---- 其他語言：lang.<code>.json（resource），再用 data\lang\<code>.json 覆蓋（用家自己改譯本）----
    private static Dictionary<string, string>? _extra;
    private static Dictionary<string, string> Extra => _extra ??= LoadExtra();

    private static Dictionary<string, string> LoadExtra()
    {
        var d = new Dictionary<string, string>();
        if (Current <= Language.En) return d;
        string code = Code(Current);
        try
        {
            using var s = typeof(Lang).Assembly.GetManifestResourceStream("lang." + code + ".json");
            if (s != null)
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new()) d[kv.Key] = kv.Value;
        }
        catch (Exception ex) { AppPaths.LogError("讀語言檔 " + code, ex); }
        try
        {
            string f = Path.Combine(AppPaths.Data, "lang", code + ".json");
            if (File.Exists(f))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? new()) d[kv.Key] = kv.Value;
        }
        catch (Exception ex) { AppPaths.LogError("讀 data\\lang\\" + code + ".json", ex); }
        return d;
    }

    /// <summary>翻譯再 string.Format。</summary>
    public static string F(string key, params object?[] args)
    {
        string fmt = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, key, args); }
    }
}
