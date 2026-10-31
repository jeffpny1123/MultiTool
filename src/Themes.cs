using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace MultiTool.UI;

// =====================================================================
//  主題：日光 / 夜間（內置）+ 插件加嘅主題（顏色、背景圖、字型、卡片透明度）
//  • data\theme.txt：light / dark / mod:<插件 id>/<主題 id>
//  • 插件主題存喺 data\themes\<插件 id>\<主題 id>\theme.json（連圖片 / 字型副本），開程式時讀
//  • 換主題要重新啟動（顏色喺開程式嗰陣定咗）
// =====================================================================

/// <summary>一個主題嘅設定。</summary>
internal sealed class ThemeSpec
{
    public string Id = "", Name = "", Mod = "", Dir = "";
    public bool Light;
    public readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase);
    public string? Font, FontFile, Wall;
    public float Dim = 0.35f, SideDim = 0.55f, CardOpacity = 0.86f;
}

internal static class ThemeStore
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    public static string Dir => Path.Combine(Data, "themes");
    private static string CurFile => Path.Combine(Data, "theme.txt");

    public static readonly string[] ColorKeys =
        { "bg", "panel", "card", "cardHi", "input", "border", "borderHi", "text", "dim", "faint", "accent", "accent2", "success", "danger" };

    private static bool _loaded;
    private static ThemeSpec? _active;

    /// <summary>而家用緊嘅插件主題（內置日光 / 夜間就 null）。開程式時讀一次。</summary>
    public static ThemeSpec? Active
    {
        get
        {
            if (_loaded) return _active;
            _loaded = true;
            try
            {
                string id = File.Exists(CurFile) ? File.ReadAllText(CurFile).Trim() : "";
                if (id.StartsWith("mod:")) _active = Load(id);
            }
            catch { _active = null; }
            return _active;
        }
    }

    /// <summary>theme.txt 入面嘅 id（冇就 dark）。</summary>
    public static string CurrentId
    {
        get
        {
            try { var s = File.Exists(CurFile) ? File.ReadAllText(CurFile).Trim() : ""; return s.Length > 0 ? s : "dark"; }
            catch { return "dark"; }
        }
    }

    public static void SetCurrent(string id)
    {
        try { Directory.CreateDirectory(Data); File.WriteAllText(CurFile, id); } catch { }
    }

    /// <summary>插件有冇啟用（直接讀 data\plugins_mods.txt，開程式好早就要用）。</summary>
    private static bool ModOn(string mod)
    {
        try
        {
            string f = Path.Combine(Data, "plugins_mods.txt");
            return File.Exists(f) && File.ReadAllLines(f).Any(l => l.Trim() == mod + "|1");
        }
        catch { return false; }
    }

    /// <summary>讀插件主題；插件停用咗 / 唔存在就 null。</summary>
    public static ThemeSpec? Load(string id)
    {
        if (!id.StartsWith("mod:")) return null;
        var parts = id[4..].Split('/');
        if (parts.Length != 2 || !Safe(parts[0]) || !Safe(parts[1]) || !ModOn(parts[0])) return null;
        string dir = Path.Combine(Dir, parts[0], parts[1]);
        string file = Path.Combine(dir, "theme.json");
        if (!File.Exists(file)) return null;
        var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        if (o == null) return null;
        var t = new ThemeSpec { Id = id, Mod = parts[0], Dir = dir };
        t.Name = o["name"]?.ToString() ?? parts[1];
        t.Light = (o["base"]?.ToString() ?? "light").ToLowerInvariant() != "dark";
        if (o["colors"] is JsonObject cs)
            foreach (var (k, v) in cs)
                if (v != null && TryColor(v.ToString(), out var c)) t.Colors[k] = c;
        t.Font = o["font"]?.ToString();
        t.FontFile = FileIn(dir, o["fontFile"]?.ToString());
        t.Wall = FileIn(dir, o["wallpaper"]?.ToString());
        t.Dim = Num(o["dim"], t.Dim);
        t.SideDim = Num(o["sideDim"], t.SideDim);
        t.CardOpacity = Num(o["cardOpacity"], t.CardOpacity);
        return t;
    }

    private static string? FileIn(string dir, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string p = Path.Combine(dir, Path.GetFileName(name));
        return System.IO.File.Exists(p) ? p : null;
    }

    private static float Num(JsonNode? n, float d) =>
        n is JsonValue v && v.TryGetValue<double>(out var x) ? (float)Math.Clamp(x, 0, 1) : d;

    private static bool Safe(string s) => s.Length > 0 && s.Length <= 60 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>"#rgb" / "#rrggbb"（主題色唔准半透明）。</summary>
    public static bool TryColor(string s, out Color c)
    {
        c = Color.Empty;
        s = s.Trim();
        if (!s.StartsWith("#")) return false;
        string h = s[1..];
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var n)) return false;
        c = Color.FromArgb(255, (n >> 16) & 255, (n >> 8) & 255, n & 255);
        return true;
    }

    /// <summary>可以揀嘅主題：內置 + 已啟用插件嘅主題。</summary>
    public static List<(string Id, string Name)> All()
    {
        var l = new List<(string, string)> { ("light", Lang.T("☀ 日光")), ("dark", Lang.T("🌙 夜間")) };
        try
        {
            if (!Directory.Exists(Dir)) return l;
            foreach (var md in Directory.GetDirectories(Dir).OrderBy(x => x))
                foreach (var td in Directory.GetDirectories(md).OrderBy(x => x))
                {
                    string id = "mod:" + Path.GetFileName(md) + "/" + Path.GetFileName(td);
                    try { if (Load(id) is { } t) l.Add((id, "🎨 " + t.Name)); } catch { }
                }
        }
        catch { }
        return l;
    }

    public static string NameOf(string id) => All().FirstOrDefault(x => x.Id == id).Name ?? (id == "light" ? Lang.T("☀ 日光") : Lang.T("🌙 夜間"));

    /// <summary>插件登記主題：檢查設定、複製背景圖 / 字型，寫 theme.json。回傳主題 id。</summary>
    public static string Save(string mod, string modDir, JsonObject spec)
    {
        string tid = spec["id"]?.ToString() ?? "theme";
        if (!Safe(mod) || !Safe(tid)) throw new ArgumentException(Lang.T("主題 id 只可以用英文字母、數字、- 同 _"));
        string name = (spec["name"]?.ToString() ?? tid).Trim();
        if (name.Length == 0 || name.Length > 40) throw new ArgumentException(Lang.T("主題名稱要 1 至 40 個字"));
        string dir = Path.Combine(Dir, mod, tid);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) try { System.IO.File.Delete(f); } catch { }
        var o = new JsonObject
        {
            ["name"] = name,
            ["base"] = (spec["base"]?.ToString() ?? "light").ToLowerInvariant() == "dark" ? "dark" : "light"
        };
        var colors = new JsonObject();
        if (spec["colors"] is JsonObject cs)
            foreach (var (k, v) in cs)
            {
                string key = ColorKeys.FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException(Lang.F("主題冇呢隻顏色：{0}（可以用 {1}）", k, string.Join("、", ColorKeys)));
                if (v == null || !TryColor(v.ToString(), out _)) throw new ArgumentException(Lang.F("顏色格式唔啱：{0}", v?.ToString() ?? ""));
                colors[key] = v.ToString();
            }
        o["colors"] = colors;
        string Copy(string? file, string[] exts)
        {
            if (string.IsNullOrWhiteSpace(file)) return "";
            string root = Path.GetFullPath(modDir) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(Path.Combine(root, file));
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(p)) throw new FileNotFoundException(Lang.F("插件資料夾入面冇「{0}」", file));
            if (!exts.Contains(Path.GetExtension(p).ToLowerInvariant())) throw new ArgumentException(Lang.F("唔支援呢種檔案：{0}", file));
            if (new FileInfo(p).Length > 30L << 20) throw new ArgumentException(Lang.F("「{0}」太大（上限 30 MB）", file));
            string dest = Path.Combine(dir, Path.GetFileName(p));
            System.IO.File.Copy(p, dest, true);
            return Path.GetFileName(p);
        }
        if (spec["wallpaper"] is JsonNode w) o["wallpaper"] = Copy(w.ToString(), new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" });
        if (spec["font"] is JsonObject fo)
        {
            if (fo["file"] != null) o["fontFile"] = Copy(fo["file"]!.ToString(), new[] { ".ttf", ".otf" });
            if (fo["family"] != null) o["font"] = fo["family"]!.ToString();
        }
        else if (spec["font"] is JsonValue fv) o["font"] = fv.ToString();
        foreach (var k in new[] { "dim", "sideDim", "cardOpacity" })
            if (spec[k] is JsonValue nv && nv.TryGetValue<double>(out var x)) o[k] = Math.Clamp(x, 0, 1);
        System.IO.File.WriteAllText(Path.Combine(dir, "theme.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return "mod:" + mod + "/" + tid;
    }

    /// <summary>刪插件：連佢嘅主題一齊刪；用緊就下次開程式變返預設。</summary>
    public static void RemoveMod(string mod)
    {
        if (!Safe(mod)) return;
        try { string d = Path.Combine(Dir, mod); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    // ---------- 字型 ----------

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceEx(string file, uint fl, IntPtr res);
    private static PrivateFontCollection? _fonts;

    /// <summary>主題字型檔：登記俾 GDI（TextRenderer）同 GDI+ 用；回傳字型名。</summary>
    public static FontFamily? LoadFont(string file)
    {
        try
        {
            AddFontResourceEx(file, 0x10 /* FR_PRIVATE */, IntPtr.Zero);
            _fonts ??= new PrivateFontCollection();
            _fonts.AddFontFile(file);
            return _fonts.Families.LastOrDefault();
        }
        catch { return null; }
    }
}

/// <summary>主題背景圖：畫喺主視窗底，上面嘅底色元件變透明，令成個程式都見到背景。</summary>
internal static class Wallpaper
{
    private static Image? _img;
    private static Form? _form;
    private static Control? _side;
    private static System.Windows.Forms.Timer? _resize;
    public static bool On => _img != null;

    /// <summary>開程式時：主題有背景圖就裝上。</summary>
    public static void Attach(Form form, Control sidebar)
    {
        var t = ThemeStore.Active;
        if (t?.Wall == null) return;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(t.Wall));
            using var src = Image.FromStream(ms);
            _img = new Bitmap(src);
        }
        catch (Exception ex) { AppPaths.LogError("主題背景圖", ex); return; }
        _form = form;
        _side = sidebar;
        form.BackgroundImageLayout = ImageLayout.None;
        Compose();
        _resize = new System.Windows.Forms.Timer { Interval = 150 };
        _resize.Tick += (_, _) => { _resize.Stop(); Compose(); form.Invalidate(true); };
        form.Resize += (_, _) => { _resize.Stop(); _resize.Start(); };
        sidebar.Resize += (_, _) => { _resize.Stop(); _resize.Start(); };
        // 先記低邊啲係底色（未變透明之前），再一次過變
        var list = Walk(form).Where(Eligible).ToList();
        foreach (var c in list) MakeClear(c);
        foreach (var c in Walk(form).ToList()) { if (Native(c)) Eligible(c); Hook(c); } // 原生控件跟咗透明 parent：補返底色
    }

    /// <summary>砌好一張同視窗一樣大嘅圖：cover 縮放 + 蓋一層底色（dim），側欄再蓋深啲。</summary>
    private static void Compose()
    {
        if (_form == null || _img == null) return;
        var t = ThemeStore.Active!;
        var sz = _form.ClientSize;
        if (sz.Width < 10 || sz.Height < 10) return;
        var bmp = new Bitmap(sz.Width, sz.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)sz.Width / _img.Width, (float)sz.Height / _img.Height);
            float w = _img.Width * s, h = _img.Height * s;
            g.DrawImage(_img, (sz.Width - w) / 2, (sz.Height - h) / 2, w, h);
            using (var b = new SolidBrush(Color.FromArgb((int)(t.Dim * 255), Ui.Bg))) g.FillRectangle(b, 0, 0, sz.Width, sz.Height);
            if (_side != null)
            {
                var r = _form.RectangleToClient(_side.RectangleToScreen(_side.ClientRectangle));
                using var sb = new SolidBrush(Color.FromArgb((int)(t.SideDim * 255), Ui.Panel));
                g.FillRectangle(sb, r);
            }
        }
        var old = _form.BackgroundImage;
        _form.BackgroundImage = bmp;
        old?.Dispose();
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        var st = new Stack<Control>();
        st.Push(root);
        while (st.Count > 0)
        {
            var c = st.Pop();
            yield return c;
            foreach (Control ch in c.Controls) st.Push(ch);
        }
    }

    private static readonly HashSet<Control> Hooked = new();

    private static void Hook(Control c)
    {
        if (!Hooked.Add(c)) return;
        c.Disposed += (_, _) => Hooked.Remove(c);
        c.ControlAdded += (_, e) =>
        {
            if (e.Control == null) return;
            var news = Walk(e.Control).ToList();
            foreach (var x in news.Where(Eligible).ToList()) MakeClear(x);
            foreach (var x in news) Hook(x);
        };
        c.BackColorChanged += (_, _) => { if (Eligible(c)) MakeClear(c); };
        // 捲動：背景唔可以跟住捲，要成格重畫
        if (c is ScrollableControl sc)
        {
            sc.Scroll += (_, _) => sc.Invalidate(true);
            sc.MouseWheel += (_, _) => sc.Invalidate(true);
        }
    }

    // 呢啲 Windows 原生控件唔識透明
    private static bool Native(Control c) =>
        c is TextBoxBase or ListBox or ComboBox or ListView or TreeView or DataGridView or NumericUpDown or DateTimePicker or TabControl
            or ToolStrip or WebBrowser or ProgressBar or TrackBar or Form ||
        c.GetType().Namespace?.StartsWith("Microsoft.Web") == true;

    /// <summary>底色係 Bg / Panel（或者跟住透明 parent 而變咗灰）嘅元件。</summary>
    private static bool Eligible(Control c)
    {
        if (c is Form || c.BackColor.A < 255) return false;
        int argb = c.BackColor.ToArgb();
        bool bg = argb == Ui.Bg.ToArgb() || argb == Ui.Panel.ToArgb();
        bool greyed = c.Parent != null && c.Parent.BackColor.A < 255 && !ExplicitBack(c);
        if (Native(c))
        {
            // 原生控件跟咗透明 parent 會變灰：用返主題底色
            if (greyed) try { c.BackColor = Ui.Bg; } catch { }
            return false;
        }
        return bg || greyed;
    }

    private static bool ExplicitBack(Control c)
    {
        try
        {
            var m = typeof(Control).GetMethod("ShouldSerializeBackColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return m?.Invoke(c, null) is true;
        }
        catch { return true; }
    }

    private static readonly MethodInfo? SetStyle = typeof(Control).GetMethod("SetStyle", BindingFlags.NonPublic | BindingFlags.Instance);

    private static void MakeClear(Control c)
    {
        try
        {
            SetStyle?.Invoke(c, new object[] { ControlStyles.SupportsTransparentBackColor, true });
            c.BackColor = Color.Transparent;
        }
        catch { }
    }

    /// <summary>卡片：有背景圖就半透明。</summary>
    public static Color? CardFill => ThemeStore.Active is { Wall: not null } t ? Color.FromArgb((int)(Math.Clamp(t.CardOpacity, 0.2f, 1f) * 255), Ui.Card) : null;
}
