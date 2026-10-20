using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MultiTool;

public enum Language { ZhHant, ZhHans, En }

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

    public static string Code(Language l) => l switch { Language.ZhHans => "zh-Hans", Language.En => "en", _ => "zh-Hant" };

    public static string DisplayName(Language l) => l switch { Language.ZhHans => "简体中文", Language.En => "English", _ => "繁體中文" };

    private static Language? Parse(string s) => s.Trim() switch
    {
        "zh-Hant" => Language.ZhHant,
        "zh-Hans" => Language.ZhHans,
        "en" => Language.En,
        _ => null
    };

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

    /// <summary>系統語言：繁中（台 / 港 / 澳）、簡中（中國 / 新加坡），其他一律英文。</summary>
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
            if (ci.TwoLetterISOLanguageName == "zh") return Language.ZhHant;
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

    /// <summary>翻譯一句（搵唔到就照原文）。</summary>
    public static string T(string key)
    {
        if (Table.TryGetValue(key, out var v))
        {
            string s = v[(int)Current];
            if (s.Length > 0) return s;
        }
        return key;
    }

    /// <summary>翻譯再 string.Format。</summary>
    public static string F(string key, params object?[] args)
    {
        string fmt = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, key, args); }
    }
}
