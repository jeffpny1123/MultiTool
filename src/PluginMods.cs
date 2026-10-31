using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Plugins;

// =====================================================================
//  插件（type: mod）：冇自己介面，喺背景行 mod.js，用 mt.ui.* 以 override 方式改 MultiTool 原有介面
//  • 用 selector 揀元件（好似 CSS）：@editor FancyButton[key="⬆ 匯出"]
//  • override：記住原本值；插件停咗 / 重新載入就還原；之後先整嘅元件都會自動套用
//  • 幾個插件改同一樣嘢：按工作坊入面嘅次序，後面蓋前面
//  • 只可以改程式本身（主視窗框、側欄、標題列、首頁、設定、主題）；現有工具嘅頁同視窗唔准改（想要新功能就寫新工具）
// =====================================================================

/// <summary>插件次序同開關：data\plugins_mods.txt（每行「id|1」或者「id|0」；越後越優先）。</summary>
internal static class ModStore
{
    private static string FilePath => Path.Combine(AppPaths.Data, "plugins_mods.txt");
    private static List<(string Id, bool On)>? _list;
    private static bool _synced;

    static ModStore() { PluginStore.Changed += () => _synced = false; }

    /// <summary>全部插件（mod）：已排好次序；新嘅加喺最後（未啟用）。</summary>
    public static List<(string Id, bool On)> List
    {
        get
        {
            if (_list != null && _synced) return _list;
            _synced = true;
            if (_list == null)
            {
                _list = new();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            var p = line.Trim().Split('|');
                            if (p[0].Length > 0 && _list.All(x => x.Id != p[0])) _list.Add((p[0], p.Length > 1 && p[1] == "1"));
                        }
                }
                catch { }
            }
            // 對返資料夾：刪咗嘅拎走，新嘅加落尾
            var mods = PluginStore.List().Where(m => m.IsMod).Select(m => m.Id).ToHashSet();
            bool changed = _list.RemoveAll(x => !mods.Contains(x.Id)) > 0;
            foreach (var id in mods.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (_list.All(x => x.Id != id)) { _list.Add((id, false)); changed = true; }
            if (changed) Save();
            return _list;
        }
    }

    private static void Save()
    {
        try { Directory.CreateDirectory(AppPaths.Data); File.WriteAllLines(FilePath, (_list ?? new()).Select(x => x.Id + "|" + (x.On ? "1" : "0"))); } catch { }
    }

    public static bool IsOn(string id) => List.Any(x => x.Id == id && x.On);
    public static int Order(string id) { var l = _list ?? List; int i = l.FindIndex(x => x.Id == id); return i < 0 ? int.MaxValue : i; }

    public static void SetOn(string id, bool on)
    {
        var l = List;
        int i = l.FindIndex(x => x.Id == id);
        if (i < 0) l.Add((id, on)); else l[i] = (id, on);
        Save();
    }

    /// <summary>移前（-1）/ 移後（+1）。</summary>
    public static void Move(string id, int delta)
    {
        var l = List;
        int i = l.FindIndex(x => x.Id == id), j = i + delta;
        if (i < 0 || j < 0 || j >= l.Count) return;
        (l[i], l[j]) = (l[j], l[i]);
        Save();
        ModEngine.ReapplyAll();
    }

    public static void Remove(string id)
    {
        _list?.RemoveAll(x => x.Id == id);
        Save();
    }
}

/// <summary>喺背景行插件嘅 mod.js（隱藏視窗入面嘅 WebView2）。</summary>
internal static class ModHost
{
    private static Form? _host;
    private static readonly Dictionary<string, PluginView> Views = new();
    /// <summary>(插件 id, level, 文字)</summary>
    public static event Action<string, string, string>? Log;
    public static event Action? Changed;

    public static bool IsRunning(string id) => Views.ContainsKey(id);
    internal static Form? HostForm => _host;

    private static Form Host()
    {
        if (_host != null && !_host.IsDisposed) return _host;
        _host = new Form
        {
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000), Size = new Size(8, 8)
        };
        _ = _host.Handle; // 唔使顯示，有 handle 就得
        return _host;
    }

    public static void StartEnabled()
    {
        foreach (var (id, on) in ModStore.List.ToList()) if (on) Start(id);
    }

    public static bool Start(string id, bool dev = false)
    {
        Stop(id);
        PluginManifest m;
        try { m = PluginStore.Load(id); }
        catch (Exception ex) { Log?.Invoke(id, "error", "manifest.json：" + ex.Message); return false; }
        if (!m.IsMod) return false;
        if (!PluginStore.Approved(m)) { Log?.Invoke(id, "warn", Lang.T("未同意權限，冇運行。")); return false; }
        var v = new PluginView(m, dev) { Dock = DockStyle.None, Size = new Size(8, 8) };
        v.Log += (l, t) => Log?.Invoke(id, l, t);
        v.Navigating += () => ModEngine.ClearMod(id);
        Host().Controls.Add(v);
        Views[id] = v;
        _ = v.StartAsync();
        Changed?.Invoke();
        return true;
    }

    public static void Stop(string id)
    {
        ModEngine.ClearMod(id);
        if (Views.Remove(id, out var v)) { v.Close(); Changed?.Invoke(); }
    }

    public static void StopAll() { foreach (var id in Views.Keys.ToList()) Stop(id); }

    public static void DevTools(string id) { if (Views.TryGetValue(id, out var v)) v.DevTools(); }

    internal static void Warn(string id, string text) => Log?.Invoke(id, "warn", text);
}

/// <summary>mt.ui.*：揀元件、override、加元件、聽事件。全部喺 UI thread 行。</summary>
internal static class ModEngine
{
    private enum Kind { Override, Add, Listen }

    private sealed class Rule
    {
        public int Id;
        public long Seq;
        public PluginManifest M = null!;
        public Kind Kind;
        public Sel? Sel;            // Listen "page" 冇 selector
        public JsonObject Props = new();
        public string Event = "", Mode = "parent";
        public int Cb;
        public Action<string>? Post;
        public readonly HashSet<string> Warned = new();
    }

    /// <summary>每個被插件掂過嘅控件：原本值、加咗嘅元件、事件。</summary>
    private sealed class State
    {
        public readonly Dictionary<string, object?> Orig = new();     // property 名 → 原本值
        public readonly HashSet<string> Ambient = new();              // 原本係跟 parent 嘅（還原用 ResetXxx）
        public readonly Dictionary<string, object?> Applied = new();  // 插件設咗乜值（分辨係咪程式自己改）
        public readonly Dictionary<int, Control> Added = new();       // rule id → 加咗嘅元件（呢個係錨點）
        public readonly Dictionary<int, Action> Listen = new();       // rule id → 解除事件
        public bool Hooked, Tracked;
        public bool Any => Orig.Count > 0 || Added.Count > 0 || Listen.Count > 0;
    }

    private static readonly List<Rule> Rules = new();
    private static readonly ConditionalWeakTable<Control, State> States = new();
    private static readonly List<WeakReference<Control>> Touched = new();
    private static readonly HashSet<Control> Created = new();         // 插件加嘅元件
    private static int _ruleId, _applying;
    private static long _seq;
    private static bool _active;
    private static readonly ToolTip Tip = new();

    // ================= API =================

    public static JsonNode? Call(PluginManifest m, string method, JsonArray args, Action<string>? post)
    {
        string S(int i) => i < args.Count && args[i] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
        int I(int i, int d) => i < args.Count && args[i] is JsonValue v && v.TryGetValue<double>(out var n) ? (int)n : d;
        JsonObject O(int i) => i < args.Count && args[i] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
        switch (method)
        {
            case "ui.override":
            {
                var r = NewRule(m, Kind.Override, Sel.Parse(S(0)), post);
                r.Props = O(1);
                return AddRule(r);
            }
            case "ui.add":
            {
                var spec = O(0);
                string mode = spec["after"] != null ? "after" : spec["before"] != null ? "before" : "parent";
                string sel = spec[mode]?.ToString() ?? throw new ArgumentException(Lang.T("mt.ui.add 要有 parent、after 或者 before（selector）"));
                var r = NewRule(m, Kind.Add, Sel.Parse(sel), post);
                r.Mode = mode;
                r.Props = spec;
                return AddRule(r);
            }
            case "ui.on":
            {
                string ev = S(0).Trim().ToLowerInvariant();
                if (ev is not ("click" or "change" or "show" or "page")) throw new ArgumentException(Lang.F("冇呢個事件：{0}（可以用 click、change、show、page）", ev));
                var r = NewRule(m, Kind.Listen, ev == "page" ? null : Sel.Parse(S(1)), post);
                r.Event = ev;
                r.Cb = I(2, 0);
                return AddRule(r);
            }
            case "ui.remove":
            {
                int id = I(0, -1);
                var r = Rules.FirstOrDefault(x => x.Id == id && x.M.Id == m.Id);
                if (r == null) return false;
                RemoveRules(new[] { r });
                return true;
            }
            case "ui.reset":
                ClearMod(m.Id);
                return true;
            case "ui.query":
            {
                var sel = Sel.Parse(S(0));
                var arr = new JsonArray();
                foreach (var c in Find(sel).Take(200)) arr.Add(Info(c));
                return arr;
            }
            case "ui.tree":
            {
                var sel = Sel.Parse(S(0));
                var c = Find(sel).FirstOrDefault();
                if (c == null) return null;
                int budget = 1500;
                return Tree(c, Math.Clamp(I(1, 6), 1, 12), ref budget);
            }
            case "ui.click":
            {
                var c = Find(Sel.Parse(S(0))).FirstOrDefault(x => x.Visible && x.Enabled) ?? throw new ArgumentException(Lang.T("搵唔到可以撳嘅元件"));
                Click(c);
                return true;
            }
            case "ui.setValue":
            {
                var c = Find(Sel.Parse(S(0))).FirstOrDefault() ?? throw new ArgumentException(Lang.T("搵唔到元件"));
                SetValue(c, args.Count > 1 ? args[1] : null);
                return true;
            }
            case "app.page":
                return MainForm.Instance?.CurrentKey;
            case "app.pages":
            {
                var arr = new JsonArray();
                foreach (var (key, name, shown) in MainForm.Instance?.PageList() ?? new())
                    arr.Add(new JsonObject { ["key"] = key, ["name"] = name, ["installed"] = shown, ["built"] = MainForm.Instance?.PageRoot(key) != null });
                return arr;
            }
            case "app.open":
                MainForm.Instance?.ShowKey(S(0));
                return true;
            case "app.addTheme":
            {
                string id = ThemeStore.Save(m.Id, PluginStore.PathOf(m.Id), O(0));
                return id;
            }
            case "app.removeTheme":
            {
                string id = S(0);
                if (!id.StartsWith("mod:" + m.Id + "/")) throw new UnauthorizedAccessException(Lang.T("只可以刪自己插件嘅主題"));
                try { Directory.Delete(Path.Combine(ThemeStore.Dir, m.Id, id[(m.Id.Length + 5)..]), true); } catch { }
                return true;
            }
            case "app.themes":
            {
                var arr = new JsonArray();
                string cur = ThemeStore.Active?.Id ?? (Ui.Light ? "light" : "dark");
                foreach (var (id, name) in ThemeStore.All()) arr.Add(new JsonObject { ["id"] = id, ["name"] = name, ["current"] = id == cur });
                return arr;
            }
            case "app.useTheme":
            {
                string id = S(0);
                if (!ThemeStore.All().Any(x => x.Id == id)) throw new ArgumentException(Lang.F("冇呢個主題：{0}", id));
                // 遲啲先問（唔好喺 API 入面彈對話框）
                MainForm.Instance?.BeginInvoke(() => MainForm.Instance?.SwitchTheme(id));
                return true;
            }
        }
        throw new NotSupportedException(Lang.F("冇呢個 API：{0}", method));
    }

    private static Rule NewRule(PluginManifest m, Kind k, Sel? sel, Action<string>? post) =>
        new() { Id = ++_ruleId, Seq = ++_seq, M = m, Kind = k, Sel = sel, Post = post };

    private static int AddRule(Rule r)
    {
        Rules.Add(r);
        Activate();
        if (r.Sel != null)
            foreach (var c in Find(r.Sel).ToList()) Reapply(c);
        if (r.Kind == Kind.Listen && r.Event == "page") MainForm.PageShown += r.PageHandler();
        return r.Id;
    }

    /// <summary>插件停咗 / 重新載入：清走佢所有 override，還原原本嘅樣。</summary>
    public static void ClearMod(string id)
    {
        var rs = Rules.Where(r => r.M.Id == id).ToList();
        if (rs.Count > 0) RemoveRules(rs);
    }

    private static void RemoveRules(IEnumerable<Rule> rs)
    {
        foreach (var r in rs.ToList())
        {
            Rules.Remove(r);
            if (r.Kind == Kind.Listen && r.Event == "page") MainForm.PageShown -= r.PageHandler();
        }
        ReapplyAll();
    }

    /// <summary>次序改咗 / rule 少咗：所有掂過嘅元件重新計。</summary>
    public static void ReapplyAll()
    {
        foreach (var w in Touched.ToList())
        {
            if (!w.TryGetTarget(out var c) || c.IsDisposed) { Touched.Remove(w); continue; }
            Reapply(c);
            if (States.TryGetValue(c, out var st) && !st.Any) { st.Tracked = false; Touched.Remove(w); }
        }
    }

    // ================= 搵元件 =================

    /// <summary>全部打開咗嘅視窗（主視窗、對話框…）入面嘅所有元件。</summary>
    private static IEnumerable<Control> AllControls()
    {
        if (MainForm.Instance is not { IsDisposed: false } mf) yield break;
        foreach (var c in Walk(mf)) if (Allowed(c)) yield return c;
    }

    internal static IEnumerable<Control> Walk(Control root)
    {
        var st = new Stack<Control>();
        st.Push(root);
        while (st.Count > 0)
        {
            var c = st.Pop();
            yield return c;
            for (int i = c.Controls.Count - 1; i >= 0; i--) st.Push(c.Controls[i]);
        }
    }

    /// <summary>插件改得嘅範圍：主視窗、側欄、標題列、首頁、設定；工具頁同其他視窗唔得。</summary>
    internal static bool Allowed(Control c)
    {
        var mf = MainForm.Instance;
        if (mf == null || c.IsDisposed) return false;
        if (Prot.TryGetValue(c, out var pr) && pr.All) return false; // 程式名 / 版本號
        if (!ReferenceEquals(c, mf) && !ReferenceEquals(c.FindForm(), mf)) return false;
        for (var x = c; x != null; x = x.Parent)
            if (mf.PageKeyOf(x) is { } key) return key is "home" or "settings"; // 首頁、設定唔算工具
        return true;
    }

    // ---- 唔准插件改嘅元件：程式名同版本號 ----
    private sealed class Protection { public bool All; public HashSet<string> Props = new(StringComparer.OrdinalIgnoreCase); }
    private static readonly ConditionalWeakTable<Control, Protection> Prot = new();
    private static readonly List<WeakReference<Control>> ProtList = new();

    /// <summary>保護元件：冇 props = 成個唔准改（連隱藏、遮住）；有 = 淨係嗰啲屬性唔准改。</summary>
    public static void Protect(Control c, params string[] props)
    {
        var p = Prot.GetOrCreateValue(c);
        if (props.Length == 0) { p.All = true; ProtList.Add(new WeakReference<Control>(c)); }
        foreach (var x in props) p.Props.Add(x);
    }

    /// <summary>c 入面有冇成個保護咗嘅元件（唔准隱藏佢嘅 parent）。</summary>
    private static bool HoldsProtected(Control c)
    {
        foreach (var w in ProtList)
            if (w.TryGetTarget(out var p))
                for (var x = p.Parent; x != null; x = x.Parent) if (ReferenceEquals(x, c)) return true;
        return false;
    }

    private static bool IsOurs(Control c) => c is ModInspector.Overlay || c.FindForm() is ModInspector.Overlay || ModHost.HostForm != null && ReferenceEquals(c.FindForm() ?? c as Form, ModHost.HostForm);

    private static IEnumerable<Control> Find(Sel sel)
    {
        var roots = sel.RootCandidates();
        var src = roots ?? AllControls();
        foreach (var c in roots == null ? src : roots.SelectMany(Walk))
            if (!c.IsDisposed && Allowed(c) && sel.Match(c)) yield return c;
    }

    // ================= 套用 =================

    /// <summary>開始跟住所有視窗：新整嘅元件自動套用。</summary>
    private static void Activate()
    {
        if (_active) return;
        _active = true;
        if (MainForm.Instance is { } mf) Hook(mf); // 淨係主視窗（其他視窗屬於工具，唔准改）
    }

    private static void Hook(Control c)
    {
        foreach (var x in Walk(c))
        {
            var st = States.GetOrCreateValue(x);
            if (st.Hooked) continue;
            st.Hooked = true;
            x.ControlAdded += OnAdded;
            x.TextChanged += OnText;
            x.VisibleChanged += OnVisible;
            x.EnabledChanged += OnEnabled;
            x.BackColorChanged += OnBack;
            x.ForeColorChanged += OnFore;
            x.FontChanged += OnFont;
        }
    }

    private static void OnAdded(object? s, ControlEventArgs e)
    {
        if (e.Control == null || Rules.Count == 0) { if (e.Control != null && _active) Hook(e.Control); return; }
        Hook(e.Control);
        ApplyTree(e.Control);
    }

    private static readonly EventHandler OnText = (s, _) => Changed(s, "Text");
    private static readonly EventHandler OnVisible = (s, _) => Changed(s, "Visible");
    private static readonly EventHandler OnEnabled = (s, _) => Changed(s, "Enabled");
    private static readonly EventHandler OnBack = (s, _) => Changed(s, "BackColor");
    private static readonly EventHandler OnFore = (s, _) => Changed(s, "ForeColor");
    private static readonly EventHandler OnFont = (s, _) => Changed(s, "Font");

    /// <summary>程式自己改咗某個值：當佢係新嘅原本值，再蓋返插件嘅值。</summary>
    private static void Changed(object? s, string prop)
    {
        if (_applying > 0 || s is not Control c || !States.TryGetValue(c, out var st) || !Allowed(c)) return;
        if (prop == "Text" && !st.Orig.ContainsKey("Text"))
        {
            // 文字變咗：[key=…] / [text=…] 啱唔啱可能唔同咗，重新計
            if (Rules.Count > 0 && (st.Any || Rules.Any(r => r.Sel != null && r.Sel.Match(c)))) Reapply(c);
            return;
        }
        if (!st.Orig.ContainsKey(prop)) return;
        object? now = prop == "Visible" ? OwnVisible(c) : Get(c, prop);
        if (st.Applied.TryGetValue(prop, out var mine) && Equals(now, mine)) return; // 我哋自己設嘅 / parent 隱藏咗
        st.Orig[prop] = now;
        st.Ambient.Remove(prop);
        Reapply(c);
    }

    private static void ApplyTree(Control root)
    {
        if (Rules.Count == 0) return;
        foreach (var c in Walk(root).ToList())
        {
            if (c.IsDisposed || !Allowed(c)) continue;
            bool had = States.TryGetValue(c, out var st) && st.Any;
            if (had || Rules.Any(r => r.Sel != null && r.Sel.Match(c))) Reapply(c);
        }
    }

    private static IEnumerable<Rule> Sorted() => Rules.OrderBy(r => ModStore.Order(r.M.Id)).ThenBy(r => r.Seq);

    /// <summary>還原原本值，再按次序套用所有啱嘅 rule。</summary>
    private static void Reapply(Control c)
    {
        if (c.IsDisposed) return;
        var st = States.GetOrCreateValue(c);
        bool allowed = Allowed(c);
        _applying++;
        try
        {
            // 1. 還原
            foreach (var (prop, val) in st.Orig.ToList()) SetRaw(c, prop, val, st.Ambient.Contains(prop));
            st.Orig.Clear(); st.Ambient.Clear(); st.Applied.Clear();
            var live = new HashSet<int>();
            // 2. 套用（後面蓋前面）
            foreach (var r in Sorted().ToList())
            {
                if (!allowed || r.Sel == null || !r.Sel.Match(c)) continue;
                live.Add(r.Id);
                switch (r.Kind)
                {
                    case Kind.Override:
                        foreach (var kv in r.Props.ToList()) ApplyProp(c, st, r, kv.Key, kv.Value);
                        break;
                    case Kind.Add:
                        if (Created.Contains(c)) { live.Remove(r.Id); break; } // 插件自己加嘅元件唔再做錨點（費事無限咁加）
                        if (!st.Added.ContainsKey(r.Id)) { var x = Create(r, c); if (x != null) st.Added[r.Id] = x; }
                        break;
                    case Kind.Listen:
                        if (!st.Listen.ContainsKey(r.Id)) st.Listen[r.Id] = Listen(r, c);
                        break;
                }
            }
            // 3. 已經冇咗嘅 rule：拆走加咗嘅元件同事件
            foreach (var id in st.Added.Keys.Where(k => !live.Contains(k)).ToList())
            {
                var x = st.Added[id];
                st.Added.Remove(id);
                Created.Remove(x);
                try { x.Parent?.Controls.Remove(x); x.Dispose(); } catch { }
            }
            foreach (var id in st.Listen.Keys.Where(k => !live.Contains(k)).ToList())
            {
                try { st.Listen[id](); } catch { }
                st.Listen.Remove(id);
            }
        }
        finally { _applying--; }
        if (st.Any && !st.Tracked) { st.Tracked = true; Touched.Add(new WeakReference<Control>(c)); }
        try { c.Invalidate(); } catch { }
    }

    // 唔准改嘅 property
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Parent", "Controls", "Handle", "Site", "Tag", "WindowTarget", "BindingContext", "DataBindings", "AccessibilityObject",
        "ContextMenuStrip", "TopLevelControl", "Capture", "ImeMode", "PasswordChar", "UseSystemPasswordChar"
    };

    private static void ApplyProp(Control c, State st, Rule r, string key, JsonNode? value)
    {
        string prop = Alias(c, key);
        try
        {
            if (prop == "tooltip") { if (!st.Orig.ContainsKey("tooltip")) st.Orig["tooltip"] = Tip.GetToolTip(c); Tip.SetToolTip(c, value?.ToString() ?? ""); st.Applied["tooltip"] = value?.ToString(); return; }
            if (IsSecret(c) && prop.Equals("Text", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(Lang.T("唔可以改密碼格"));
            var pi = Prop(c, prop) ?? throw new ArgumentException(Lang.F("{0} 冇「{1}」呢個屬性", c.GetType().Name, key));
            if (Prot.TryGetValue(c, out var pr) && (pr.All || pr.Props.Contains(pi.Name))) throw new UnauthorizedAccessException(Lang.T("程式名同版本號唔可以改"));
            if (pi.Name is "Visible" or "Width" or "Height" or "Size" or "Bounds" or "Region" && HoldsProtected(c))
                throw new UnauthorizedAccessException(Lang.T("呢度有程式名同版本號，唔可以隱藏或者改大細"));
            object? v = Convert(value, pi.PropertyType, r.M, Get(c, pi.Name), pi.Name);
            if (!st.Orig.ContainsKey(pi.Name))
            {
                st.Orig[pi.Name] = pi.Name == "Visible" ? OwnVisible(c) : pi.GetValue(c);
                if (IsAmbient(c, pi.Name)) st.Ambient.Add(pi.Name);
            }
            if (pi.Name == "BackColor" && v is Color { A: < 255 }) AllowTransparent(c);
            pi.SetValue(c, v);
            st.Applied[pi.Name] = pi.Name == "Visible" ? v : pi.GetValue(c);
        }
        catch (Exception ex)
        {
            if (r.Warned.Add(key)) ModHost.Warn(r.M.Id, Lang.F("override「{0}」：{1}", key, (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>常用名 → 真正 property（自己畫嘅掣 / 卡用 Fill、TextColor）。</summary>
    private static string Alias(Control c, string key)
    {
        string k = key.Trim();
        bool custom = Prop(c, "Fill") != null;
        switch (k.ToLowerInvariant())
        {
            case "back": case "background": case "backcolor": return custom ? "Fill" : "BackColor";
            case "fore": case "color": case "forecolor": case "textcolor": return custom || Prop(c, "TextColor") != null ? "TextColor" : "ForeColor";
            case "image": case "backgroundimage": return c is PictureBox || c is ButtonBase && k.ToLowerInvariant() == "image" ? "Image" : "BackgroundImage";
            case "imagelayout": return "BackgroundImageLayout";
            case "tooltip": return "tooltip";
            case "x": return "Left";
            case "y": return "Top";
        }
        return k;
    }

    private static PropertyInfo? Prop(Control c, string name)
    {
        if (Blocked.Contains(name)) return null;
        var pi = c.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return pi != null && pi.CanWrite && pi.GetIndexParameters().Length == 0 && pi.SetMethod?.IsPublic == true ? pi : null;
    }

    private static object? Get(Control c, string prop)
    {
        try { return c.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(c); } catch { return null; }
    }

    private static void SetRaw(Control c, string prop, object? val, bool ambient)
    {
        try
        {
            if (prop == "tooltip") { Tip.SetToolTip(c, val as string ?? ""); return; }
            if (ambient)
            {
                var reset = c.GetType().GetMethod("Reset" + prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
                if (reset != null) { reset.Invoke(c, null); return; }
            }
            c.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.SetValue(c, val);
        }
        catch { }
    }

    /// <summary>BackColor / ForeColor / Font / Cursor 原本冇自己設（跟 parent）。</summary>
    private static bool IsAmbient(Control c, string prop)
    {
        if (prop is not ("BackColor" or "ForeColor" or "Font" or "Cursor")) return false;
        try
        {
            var m = typeof(Control).GetMethod("ShouldSerialize" + prop, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return m != null && m.Invoke(c, null) is false;
        }
        catch { return false; }
    }

    private static void AllowTransparent(Control c)
    {
        try
        {
            typeof(Control).GetMethod("SetStyle", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(c, new object[] { ControlStyles.SupportsTransparentBackColor, true });
        }
        catch { }
    }

    private static MethodInfo? _getState;
    /// <summary>元件自己嘅 Visible（唔理 parent 有冇隱藏）。</summary>
    private static bool OwnVisible(Control c)
    {
        try
        {
            _getState ??= typeof(Control).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetState" && m.GetParameters().Length == 1);
            if (_getState != null)
            {
                var pt = _getState.GetParameters()[0].ParameterType;
                object flag = pt.IsEnum ? Enum.ToObject(pt, 0x00000002) : 0x00000002;
                if (_getState.Invoke(c, new[] { flag }) is bool b) return b;
            }
        }
        catch { }
        return c.Visible;
    }

    private static bool IsSecret(Control c) => c is TextBox t && (t.UseSystemPasswordChar || t.PasswordChar != '\0');
    private static string TextOf(Control c)
    {
        if (IsSecret(c)) return "";
        return States.TryGetValue(c, out var st) && st.Orig.TryGetValue("Text", out var o) && o is string s ? s : c.Text ?? "";
    }

    // ================= 轉換 JSON → C# 值 =================

    private static readonly Dictionary<string, Image> Images = new();

    private static readonly HashSet<string> Pixels = new(StringComparer.OrdinalIgnoreCase) { "Width", "Height", "Left", "Top" };

    private static object? Convert(JsonNode? v, Type t, PluginManifest m, object? current, string prop = "")
    {
        var u = Nullable.GetUnderlyingType(t);
        if (v == null) return u != null || !t.IsValueType ? null : throw new ArgumentException(Lang.T("唔可以係 null"));
        if (u != null) t = u;
        string str = v is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : v.ToJsonString();
        if (t == typeof(string)) return str;
        if (t == typeof(bool)) return v is JsonValue b && b.TryGetValue<bool>(out var bb) ? bb : bool.Parse(str);
        if (t == typeof(int)) return Pixels.Contains(prop) ? Ui.S(Num(v, str)) : (int)Num(v, str); // 大細 / 位置用 100% 縮放嘅 px，自動跟 DPI
        if (t == typeof(float)) return (float)Num(v, str);
        if (t == typeof(double)) return Num(v, str);
        if (t == typeof(decimal)) return (decimal)Num(v, str);
        if (t == typeof(Color)) return ParseColor(str);
        if (t.IsEnum) return Enum.Parse(t, str.Replace("|", ","), true);
        if (t == typeof(Font)) return ParseFont(v, current as Font ?? Ui.F(9.75f));
        if (t == typeof(Image)) return str.Length == 0 ? null : LoadImage(m, str);
        if (t == typeof(Size)) { var a = Pair(v, "width", "height"); return new Size(Ui.S(a.Item1), Ui.S(a.Item2)); }
        if (t == typeof(Point)) { var a = Pair(v, "x", "y"); return new Point(Ui.S(a.Item1), Ui.S(a.Item2)); }
        if (t == typeof(Padding))
        {
            if (v is JsonArray pa && pa.Count == 4) return new Padding(Ui.S(Num(pa[0]!, "")), Ui.S(Num(pa[1]!, "")), Ui.S(Num(pa[2]!, "")), Ui.S(Num(pa[3]!, "")));
            return new Padding(Ui.S(Num(v, str)));
        }
        if (t == typeof(Cursor))
            return typeof(Cursors).GetProperty(str, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null) as Cursor ?? Cursors.Default;
        throw new NotSupportedException(Lang.F("唔支援呢種值：{0}", t.Name));
    }

    private static double Num(JsonNode v, string str) =>
        v is JsonValue n && n.TryGetValue<double>(out var d) ? d : double.Parse(str, System.Globalization.CultureInfo.InvariantCulture);

    private static (double, double) Pair(JsonNode v, string a, string b)
    {
        if (v is JsonArray arr && arr.Count >= 2) return (Num(arr[0]!, ""), Num(arr[1]!, ""));
        if (v is JsonObject o) return (Num(o[a]!, ""), Num(o[b]!, ""));
        throw new ArgumentException(Lang.F("要用 [{0}, {1}]", a, b));
    }

    /// <summary>"#rgb" / "#rrggbb" / "#aarrggbb" / "transparent" / "red" / "theme:accent"</summary>
    internal static Color ParseColor(string s)
    {
        s = s.Trim();
        if (s.StartsWith("theme:", StringComparison.OrdinalIgnoreCase))
            return s[6..].ToLowerInvariant() switch
            {
                "bg" => Ui.Bg, "panel" => Ui.Panel, "card" => Ui.Card, "cardhi" => Ui.CardHi, "input" => Ui.Input, "border" => Ui.Border,
                "text" => Ui.Text, "dim" => Ui.TextDim, "faint" => Ui.TextFaint, "accent" => Ui.Accent, "accent2" => Ui.Accent2,
                "success" => Ui.Success, "danger" => Ui.Danger, _ => throw new ArgumentException(Lang.F("冇呢個主題色：{0}", s))
            };
        if (s.StartsWith("#"))
        {
            string h = s[1..];
            if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
            uint n = System.Convert.ToUInt32(h, 16);
            if (h.Length == 6) return Color.FromArgb(255, (int)(n >> 16) & 255, (int)(n >> 8) & 255, (int)n & 255);
            if (h.Length == 8) return Color.FromArgb((int)(n >> 24) & 255, (int)(n >> 16) & 255, (int)(n >> 8) & 255, (int)n & 255);
            throw new ArgumentException(Lang.F("顏色格式唔啱：{0}", s));
        }
        var c = Color.FromName(s);
        if (!c.IsKnownColor && !s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Lang.F("顏色格式唔啱：{0}", s));
        return c;
    }

    /// <summary>字型：數字 = 大細；字串 = 字型名；{ family, size, bold, italic, underline }。</summary>
    private static Font ParseFont(JsonNode v, Font cur)
    {
        string family = cur.FontFamily.Name;
        float size = cur.SizeInPoints;
        var style = cur.Style;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) size = (float)d;
            else if (jv.TryGetValue<string>(out var s)) family = s;
        }
        else if (v is JsonObject o)
        {
            if (o["family"] is JsonValue f && f.TryGetValue<string>(out var fs)) family = fs;
            if (o["size"] is JsonValue z && z.TryGetValue<double>(out var zs)) size = (float)zs;
            void Flag(string k, FontStyle fl) { if (o[k] is JsonValue b && b.TryGetValue<bool>(out var on)) style = on ? style | fl : style & ~fl; }
            Flag("bold", FontStyle.Bold); Flag("italic", FontStyle.Italic); Flag("underline", FontStyle.Underline); Flag("strikeout", FontStyle.Strikeout);
        }
        return new Font(family, Math.Clamp(size, 5f, 72f), style, GraphicsUnit.Point);
    }

    /// <summary>插件資料夾入面嘅圖（讀入記憶體，唔鎖住個檔）。</summary>
    private static Image LoadImage(PluginManifest m, string file)
    {
        string root = Path.GetFullPath(PluginStore.PathOf(m.Id)) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(Path.Combine(root, file));
        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(p)) throw new FileNotFoundException(Lang.F("插件資料夾入面冇「{0}」", file));
        string key = p + "|" + File.GetLastWriteTimeUtc(p).Ticks;
        if (Images.TryGetValue(key, out var img)) return img;
        using var ms = new MemoryStream(File.ReadAllBytes(p));
        using var src = Image.FromStream(ms);
        return Images[key] = new Bitmap(src);
    }

    // ================= 加元件 =================

    private static readonly HashSet<string> SpecKeys = new(StringComparer.OrdinalIgnoreCase) { "type", "parent", "after", "before", "file" };

    private static Control? Create(Rule r, Control anchor)
    {
        var spec = r.Props;
        string type = spec["type"]?.ToString().ToLowerInvariant() ?? "button";
        string file = spec["file"]?.ToString() ?? "";
        Control x;
        try
        {
            x = type switch
            {
                "button" => new FancyButton { Kind = BtnKind.Default },
                "label" => new Label { AutoSize = true, ForeColor = Ui.Text, BackColor = Color.Transparent, Font = Ui.F(9.75f), UseMnemonic = false },
                "checkbox" => new CheckBox { AutoSize = true, ForeColor = Ui.Text, BackColor = Color.Transparent },
                "textbox" => new TextBox { BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Width = Ui.S(180) },
                "panel" => new Panel { BackColor = Color.Transparent, Size = new Size(Ui.S(200), Ui.S(100)) },
                "image" => new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Size = new Size(Ui.S(64), Ui.S(64)) },
                "web" => new PluginView(r.M.WithEntry(file.Length > 0 ? file : "panel.html"), false) { Dock = DockStyle.None, Size = new Size(Ui.S(320), Ui.S(200)) },
                _ => throw new ArgumentException(Lang.F("mt.ui.add 唔識「{0}」（可以用 button、label、checkbox、textbox、panel、image、web）", type))
            };
        }
        catch (Exception ex) { if (r.Warned.Add("type")) ModHost.Warn(r.M.Id, ex.Message); return null; }
        if (x is PictureBox pb && file.Length > 0) try { pb.Image = LoadImage(r.M, file); } catch (Exception ex) { if (r.Warned.Add("file")) ModHost.Warn(r.M.Id, ex.Message); }
        var st = States.GetOrCreateValue(x);
        st.Hooked = true; // 插件自己嘅元件唔使跟
        foreach (var kv in spec.ToList())
        {
            if (SpecKeys.Contains(kv.Key) || kv.Key.StartsWith("__cb_")) continue;
            var pi = Prop(x, Alias(x, kv.Key));
            if (Alias(x, kv.Key) == "tooltip") { Tip.SetToolTip(x, kv.Value?.ToString() ?? ""); continue; }
            if (pi == null) { if (r.Warned.Add(kv.Key)) ModHost.Warn(r.M.Id, Lang.F("{0} 冇「{1}」呢個屬性", x.GetType().Name, kv.Key)); continue; }
            try
            {
                var v = Convert(kv.Value, pi.PropertyType, r.M, pi.GetValue(x), pi.Name);
                if (pi.Name == "BackColor" && v is Color { A: < 255 }) AllowTransparent(x);
                pi.SetValue(x, v);
            }
            catch (Exception ex) { if (r.Warned.Add(kv.Key)) ModHost.Warn(r.M.Id, kv.Key + "：" + ex.Message); }
        }
        if (x is FancyButton fb && spec["width"] == null) fb.Width = fb.GetPreferredSize(Size.Empty).Width;
        // 事件
        int click = (int)(spec["__cb_click"]?.GetValue<double>() ?? 0), change = (int)(spec["__cb_change"]?.GetValue<double>() ?? 0);
        if (click > 0) x.Click += (_, _) => Send(r, click, new JsonObject());
        if (change > 0)
        {
            if (x is CheckBox cb) cb.CheckedChanged += (_, _) => Send(r, change, new JsonObject { ["value"] = cb.Checked });
            else x.TextChanged += (_, _) => Send(r, change, new JsonObject { ["value"] = x.Text });
        }
        // 放位置
        var parent = r.Mode == "parent" ? anchor : anchor.Parent;
        if (parent == null) { x.Dispose(); return null; }
        Created.Add(x);
        parent.Controls.Add(x);
        if (r.Mode != "parent")
        {
            int idx = parent.Controls.GetChildIndex(anchor);
            parent.Controls.SetChildIndex(x, r.Mode == "before" ? idx : idx + 1);
            if (parent is not FlowLayoutPanel and not TableLayoutPanel && x.Dock == DockStyle.None && spec["left"] == null && spec["x"] == null)
                x.Location = r.Mode == "after" ? new Point(anchor.Right + Ui.S(8), anchor.Top) : new Point(anchor.Left - x.Width - Ui.S(8), anchor.Top);
            anchor.Disposed += (_, _) => { try { x.Dispose(); } catch { } };
        }
        if (parent is not FlowLayoutPanel) x.BringToFront();
        // 唔准遮住程式名 / 版本號：佢哋擺返最前
        if (parent is not FlowLayoutPanel and not TableLayoutPanel)
            foreach (Control sib in parent.Controls.Cast<Control>().ToList())
                if (Prot.TryGetValue(sib, out var sp) && sp.All) sib.BringToFront();
        if (x is PluginView pv) _ = pv.StartAsync();
        return x;
    }

    // ================= 事件 =================

    private static readonly ConditionalWeakTable<Rule, Action<string>> PageHandlers = new();
    private static Action<string> PageHandler(this Rule r) =>
        PageHandlers.GetValue(r, rr => key => Send(rr, rr.Cb, new JsonObject { ["key"] = key }));

    private static Action Listen(Rule r, Control c)
    {
        switch (r.Event)
        {
            case "click":
            {
                EventHandler h = (_, _) => Send(r, r.Cb, Info(c));
                c.Click += h;
                return () => c.Click -= h;
            }
            case "change":
            {
                EventHandler h = (_, _) => { var o = Info(c); o["value"] = Value(c); Send(r, r.Cb, o); };
                switch (c)
                {
                    case CheckBox cb: cb.CheckedChanged += h; return () => cb.CheckedChanged -= h;
                    case RadioButton rb: rb.CheckedChanged += h; return () => rb.CheckedChanged -= h;
                    case ComboBox co: co.SelectedIndexChanged += h; return () => co.SelectedIndexChanged -= h;
                    case NumericUpDown nu: nu.ValueChanged += h; return () => nu.ValueChanged -= h;
                    default: c.TextChanged += h; return () => c.TextChanged -= h;
                }
            }
            default: // show：第一次出現
                Send(r, r.Cb, Info(c));
                return () => { };
        }
    }

    private static void Send(Rule r, int cb, JsonNode data)
    {
        if (cb <= 0 || r.Post == null || !Rules.Contains(r)) return;
        r.Post(new JsonObject { ["__mtev"] = 1, ["cb"] = cb, ["data"] = data }.ToJsonString());
    }

    private static JsonNode? Value(Control c) => c switch
    {
        CheckBox cb => cb.Checked,
        RadioButton rb => rb.Checked,
        NumericUpDown nu => (double)nu.Value,
        _ => IsSecret(c) ? "" : c.Text
    };

    private static void Click(Control c)
    {
        if (c is Button b) { b.PerformClick(); return; }
        if (c is CheckBox cb) { cb.Checked = !cb.Checked; return; }
        if (c is RadioButton rb) { rb.Checked = true; return; }
        typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(c, new object[] { EventArgs.Empty });
        typeof(Control).GetMethod("OnMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(c,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, c.Width / 2, c.Height / 2, 0) });
    }

    private static void SetValue(Control c, JsonNode? v)
    {
        if (IsSecret(c)) throw new UnauthorizedAccessException(Lang.T("唔可以改密碼格"));
        string s = v is JsonValue jv && jv.TryGetValue<string>(out var x) ? x : v?.ToJsonString() ?? "";
        switch (c)
        {
            case CheckBox cb: cb.Checked = v is JsonValue b && b.TryGetValue<bool>(out var bb) ? bb : bool.Parse(s); break;
            case RadioButton rb: rb.Checked = v is JsonValue b2 && b2.TryGetValue<bool>(out var bb2) ? bb2 : bool.Parse(s); break;
            case NumericUpDown nu: nu.Value = Math.Clamp((decimal)Num(v!, s), nu.Minimum, nu.Maximum); break;
            case ComboBox co:
                int i = co.Items.Cast<object>().ToList().FindIndex(o => o?.ToString() == s);
                if (i >= 0) co.SelectedIndex = i; else co.Text = s;
                break;
            default: c.Text = s; break;
        }
    }

    // ================= 元件資料（query / tree / 揀元件）=================

    internal static JsonObject Info(Control c)
    {
        var o = new JsonObject
        {
            ["selector"] = Best(c),
            ["type"] = c.GetType().Name
        };
        if (!string.IsNullOrEmpty(c.Name)) o["name"] = c.Name;
        string t = TextOf(c);
        if (t.Length > 0)
        {
            o["text"] = t.Length > 200 ? t[..200] + "…" : t;
            if (Lang.KeyOf(t) is { } k && k != t) o["key"] = k;
        }
        o["visible"] = c.Visible;
        o["enabled"] = c.Enabled;
        o["x"] = Ui.Unscale(c.Left); o["y"] = Ui.Unscale(c.Top); o["width"] = Ui.Unscale(c.Width); o["height"] = Ui.Unscale(c.Height);
        return o;
    }

    private static JsonObject Tree(Control c, int depth, ref int budget)
    {
        budget--;
        var o = new JsonObject { ["type"] = c.GetType().Name };
        if (!string.IsNullOrEmpty(c.Name)) o["name"] = c.Name;
        string t = TextOf(c);
        if (t.Length > 0) o[Lang.KeyOf(t) is { } k ? "key" : "text"] = Lang.KeyOf(t) ?? (t.Length > 80 ? t[..80] + "…" : t);
        if (!c.Visible) o["visible"] = false;
        if (depth > 0 && c.Controls.Count > 0 && budget > 0)
        {
            var arr = new JsonArray();
            foreach (Control ch in c.Controls)
            {
                if (budget <= 0) { arr.Add("…"); break; }
                if (Created.Contains(ch)) continue;
                // 工具頁：唔准改，淨係話俾佢知有呢頁
                if (MainForm.Instance?.PageKeyOf(ch) is { } key && key is not ("home" or "settings")) { arr.Add(new JsonObject { ["page"] = key, ["locked"] = true }); continue; }
                arr.Add(Tree(ch, depth - 1, ref budget));
            }
            o["children"] = arr;
        }
        return o;
    }

    /// <summary>俾 AI 睇：某頁嘅元件樹（JSON，太長就截短）。</summary>
    public static string TreeText(string key, int maxChars)
    {
        var mf = MainForm.Instance;
        if (mf == null) return "";
        Control? root = key is "" or "main" ? mf : key is "home" or "settings" ? mf.PageRoot(key) : null;
        if (root == null) return "";
        int budget = 900;
        string json = Tree(root, key.Length == 0 ? 4 : 9, ref budget).ToJsonString();
        return json.Length > maxChars ? json[..maxChars] + "…" : json;
    }

    /// <summary>元件嘅根：頁 / 側欄 / 視窗。</summary>
    private static (Control Root, string Name) RootOf(Control c)
    {
        var mf = MainForm.Instance;
        for (var x = c; x != null; x = x.Parent)
        {
            if (mf != null)
            {
                if (mf.PageKeyOf(x) is { } key) return (x, "@" + key);
                if (ReferenceEquals(x, mf.Sidebar)) return (x, "@sidebar");
                if (ReferenceEquals(x, mf.TitleBarCtl)) return (x, "@titlebar");
                if (ReferenceEquals(x, mf)) return (x, "@main");
            }
            if (x is Form f) return (f, "@" + f.GetType().Name);
        }
        return (c, "@main");
    }

    /// <summary>最好用嘅 selector：[key=…] / [text=…] / #name，唔係唯一就用位置路徑。</summary>
    public static string Best(Control c)
    {
        var (root, rn) = RootOf(c);
        if (ReferenceEquals(root, c)) return rn;
        string type = c.GetType().Name;
        bool Unique(string s)
        {
            try { var sel = Sel.Parse(s); return Walk(root).Count(x => sel.Match(x)) == 1; } catch { return false; }
        }
        if (!string.IsNullOrEmpty(c.Name) && Unique($"{rn} {type}#{c.Name}")) return $"{rn} {type}#{c.Name}";
        string t = TextOf(c);
        if (t.Length > 0 && t.Length <= 60 && !t.Contains('\n'))
        {
            if (Lang.KeyOf(t) is { } k) { string s = $"{rn} {type}[key=\"{Esc(k)}\"]"; if (Unique(s)) return s; }
            string s2 = $"{rn} {type}[text=\"{Esc(t)}\"]";
            if (Unique(s2)) return s2;
        }
        var parts = new List<string>();
        for (var x = c; x != null && !ReferenceEquals(x, root); x = x.Parent)
        {
            string tn = x.GetType().Name;
            int i = 0;
            if (x.Parent != null)
                foreach (Control sib in x.Parent.Controls)
                {
                    if (ReferenceEquals(sib, x)) break;
                    if (Sel.IsType(sib, tn)) i++; // 同 Match 一樣計（子類都算）
                }
            parts.Insert(0, $"{tn}:nth({i})");
        }
        return rn + " > " + string.Join(" > ", parts);
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ================= Selector =================

    /// <summary>
    /// 好似 CSS：@根 類型#名[屬性="值"]:nth(n) … 用空格（任何一層入面）或者 &gt;（直屬）連住。
    /// 根：@main 主視窗、@sidebar 側欄、@頁 key（editor…）、@視窗類型（EdExportForm…）。
    /// 屬性：key（原文，唔理語言）、text、name，或者任何 property（例如 [Kind="Primary"]）；= 等於、*= 包含、^= 開頭、$= 結尾。
    /// </summary>
    internal sealed class Sel
    {
        private sealed class Part
        {
            public bool Child;
            public string? Root, Type, Name;
            public readonly List<(string Attr, string Op, string Val)> Attrs = new();
            public int Nth = -1;
            public bool Visible;
        }

        private readonly List<Part> _parts = new();

        public static Sel Parse(string s)
        {
            var sel = new Sel();
            s = (s ?? "").Trim();
            if (s.Length == 0) throw new ArgumentException(Lang.T("selector 唔可以空白"));
            int i = 0;
            bool child = false;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (s[i] == '>') { child = true; i++; continue; }
                var p = new Part { Child = child };
                child = false;
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '>')
                {
                    char ch = s[i];
                    if (ch == '@') { i++; p.Root = ReadIdent(s, ref i, true); }
                    else if (ch == '#') { i++; p.Name = ReadIdent(s, ref i, false); }
                    else if (ch == '[') { i++; ReadAttr(s, ref i, p); }
                    else if (ch == ':')
                    {
                        i++;
                        string ps = ReadIdent(s, ref i, false).ToLowerInvariant();
                        if (ps == "nth")
                        {
                            if (i >= s.Length || s[i] != '(') throw Bad(s);
                            int close = s.IndexOf(')', i);
                            if (close < 0 || !int.TryParse(s[(i + 1)..close].Trim(), out var n)) throw Bad(s);
                            p.Nth = n;
                            i = close + 1;
                        }
                        else if (ps == "visible") p.Visible = true;
                        else throw new ArgumentException(Lang.F("selector 唔識「:{0}」", ps));
                    }
                    else if (ch == '*') { i++; p.Type = "*"; }
                    else if (char.IsLetter(ch) || ch == '_') p.Type = ReadIdent(s, ref i, false);
                    else throw Bad(s);
                }
                if (i == start) throw Bad(s);
                if (p.Root != null && sel._parts.Count > 0) throw new ArgumentException(Lang.T("selector 嘅 @根 要放喺最前"));
                if (p.Root != null && p.Root.ToLowerInvariant() is not ("main" or "sidebar" or "titlebar" or "content" or "home" or "settings"))
                    throw new ArgumentException(Lang.F("插件唔可以改「@{0}」：現有工具唔准改，可以用 @main、@sidebar、@titlebar、@content、@home、@settings。想要新功能可以寫一個新工具。", p.Root));
                sel._parts.Add(p);
            }
            if (sel._parts.Count == 0) throw Bad(s);
            return sel;
        }

        private static ArgumentException Bad(string s) => new(Lang.F("selector 格式唔啱：{0}", s));

        private static string ReadIdent(string s, ref int i, bool root)
        {
            if (i < s.Length && s[i] == '"')
            {
                int close = s.IndexOf('"', i + 1);
                if (close < 0) throw Bad(s);
                string q = s[(i + 1)..close];
                i = close + 1;
                return q;
            }
            int st = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.' || root && s[i] == ':' && !IsPseudoAt(s, i))) i++;
            if (i == st) throw Bad(s);
            return s[st..i];
        }

        private static bool IsPseudoAt(string s, int i) =>
            string.Compare(s, i + 1, "nth(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(s, i + 1, "visible", 0, 7, StringComparison.OrdinalIgnoreCase) == 0;

        private static void ReadAttr(string s, ref int i, Part p)
        {
            int st = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            string attr = s[st..i];
            if (attr.Length == 0) throw Bad(s);
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            string op = "=";
            if (i < s.Length && s[i] is '*' or '^' or '$') { op = s[i] + "="; i++; }
            if (i >= s.Length || s[i] != '=') throw Bad(s);
            i++;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            string val;
            if (i < s.Length && s[i] is '"' or '\'')
            {
                char q = s[i++];
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw Bad(s);
                i++;
                val = sb.ToString();
            }
            else
            {
                int vs = i;
                while (i < s.Length && s[i] != ']') i++;
                val = s[vs..i].Trim();
            }
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != ']') throw Bad(s);
            i++;
            p.Attrs.Add((attr, op, val));
        }

        /// <summary>有 @根 就淨係喺嗰度搵（快好多）；冇就 null（全部視窗）。</summary>
        public List<Control>? RootCandidates() => _parts[0].Root is { } r ? Roots(r).ToList() : null;

        private static IEnumerable<Control> Roots(string r)
        {
            var mf = MainForm.Instance;
            if (mf != null)
            {
                switch (r.ToLowerInvariant())
                {
                    case "main": yield return mf; yield break;
                    case "sidebar": yield return mf.Sidebar; yield break;
                    case "content": yield return mf.Content; yield break;
                    case "titlebar": if (mf.TitleBarCtl != null) yield return mf.TitleBarCtl; yield break;
                }
                if (mf.PageRoot(r) is { } page) { yield return page; yield break; }
            }
            // 其他視窗屬於工具：唔准揀
        }

        public bool Match(Control c) => MatchAt(_parts.Count - 1, c);

        private bool MatchAt(int i, Control c)
        {
            if (!MatchPart(_parts[i], c, true)) return false;
            if (i == 0) return true;
            if (_parts[i].Child) return c.Parent != null && MatchAt(i - 1, c.Parent);
            for (var a = c.Parent; a != null; a = a.Parent) if (MatchAt(i - 1, a)) return true;
            return false;
        }

        private static bool MatchPart(Part p, Control c, bool nth)
        {
            if (p.Root != null && !Roots(p.Root).Any(r => ReferenceEquals(r, c))) return false;
            if (p.Type != null && p.Type != "*" && !IsType(c, p.Type)) return false;
            if (p.Name != null && c.Name != p.Name) return false;
            foreach (var (attr, op, val) in p.Attrs)
            {
                string la = attr.ToLowerInvariant();
                string? have = la switch
                {
                    "text" or "key" => TextOf(c),
                    "name" => c.Name,
                    "type" => c.GetType().Name,
                    _ => AttrValue(c, attr)
                };
                if (have == null) return false;
                bool exact = la is "text" or "key" or "name";
                bool Test(string want) => op switch
                {
                    "*=" => have.Contains(want, StringComparison.Ordinal),
                    "^=" => have.StartsWith(want, StringComparison.Ordinal),
                    "$=" => have.EndsWith(want, StringComparison.Ordinal),
                    _ => have == want || !exact && have.Equals(want, StringComparison.OrdinalIgnoreCase)
                };
                // key = 原文：翻譯做而家嘅介面語言再比（唔理用緊咩語言都揀到）
                if (!(Test(val) || la == "key" && Test(Lang.T(val)))) return false;
            }
            if (p.Visible && !c.Visible) return false;
            if (nth && p.Nth >= 0)
            {
                if (c.Parent == null) return p.Nth == 0;
                int idx = 0;
                foreach (Control sib in c.Parent.Controls)
                {
                    if (ReferenceEquals(sib, c)) return idx == p.Nth;
                    if (MatchPart(p, sib, false)) idx++;
                }
                return false;
            }
            return true;
        }

        private static string? AttrValue(Control c, string attr)
        {
            if (Blocked.Contains(attr)) return null;
            try { return c.GetType().GetProperty(attr, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(c)?.ToString(); }
            catch { return null; }
        }

        internal static bool IsType(Control c, string t)
        {
            for (var x = c.GetType(); x != null && x != typeof(object); x = x.BaseType)
            {
                if (x.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
                if (x == typeof(Control)) break;
            }
            return false;
        }
    }
}

/// <summary>揀元件：滑鼠移過就框住，Ctrl + 撳 = 揀；Esc 取消。</summary>
internal sealed class ModInspector : IMessageFilter
{
    private static ModInspector? _cur;
    private readonly Action<Control?> _done;
    private readonly Overlay _box = new(true), _tip = new(false);
    private Control? _hot;

    private ModInspector(Action<Control?> done) => _done = done;

    public static bool Active => _cur != null;

    public static void Start(Action<Control?> done)
    {
        _cur?.Finish(null);
        _cur = new ModInspector(done);
        Application.AddMessageFilter(_cur);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_KEYDOWN = 0x100;
        if (m.Msg == WM_KEYDOWN && (int)m.WParam == 0x1B) { Finish(null); return true; }
        if (m.Msg is not (WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP)) return false;
        var c = Control.FromChildHandle(m.HWnd);
        if (c == null || c is Overlay || c.FindForm() is Overlay) return false;
        // 滑鼠位置下面最深嗰個（自己畫嘅冇 handle 子元件都計）
        var pt = Cursor.Position;
        for (var inner = c.GetChildAtPoint(c.PointToClient(pt), GetChildAtPointSkip.Invisible);
             inner != null; inner = inner.GetChildAtPoint(inner.PointToClient(pt), GetChildAtPointSkip.Invisible))
            c = inner;
        if (m.Msg == WM_MOUSEMOVE)
        {
            if (!ReferenceEquals(c, _hot)) Highlight(c);
            return false;
        }
        if ((Control.ModifierKeys & Keys.Control) == 0) return false;
        if (m.Msg == WM_LBUTTONDOWN) Finish(c);
        return true; // Ctrl + 撳：唔好傳落去
    }

    private void Highlight(Control c)
    {
        _hot = c;
        try
        {
            var r = c.RectangleToScreen(c.ClientRectangle);
            r.Inflate(2, 2);
            _box.ShowRect(r);
            string sel = ModEngine.Allowed(c) ? ModEngine.Best(c) : Lang.T("🔒 工具頁唔可以用插件改");
            _tip.ShowText(sel + "   " + Lang.T("（Ctrl + 撳 = 揀；Esc 取消）"), new Point(r.Left, r.Bottom + 4));
        }
        catch { }
    }

    private void Finish(Control? c)
    {
        Application.RemoveMessageFilter(this);
        if (_cur == this) _cur = null;
        try { _box.Close(); _tip.Close(); } catch { }
        try { _done(c); } catch (Exception ex) { AppPaths.LogError("揀元件", ex); }
    }

    /// <summary>浮喺最頂、撳唔到（滑鼠穿透）嘅框 / 提示。</summary>
    internal sealed class Overlay : Form
    {
        private readonly bool _frame;
        private string _text = "";

        public Overlay(bool frame)
        {
            _frame = frame;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = frame ? Ui.Accent : Ui.CardHi;
            ForeColor = Ui.Text;
            Font = Ui.F(9f);
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x20 | 0x80000 | 0x08000000; // TOOLWINDOW | TRANSPARENT | LAYERED | NOACTIVATE
                return cp;
            }
        }

        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, _frame ? (byte)255 : (byte)235, 2);
        }

        public void ShowRect(Rectangle r)
        {
            Bounds = r;
            int t = Math.Max(2, Ui.S(2));
            var outer = new Region(new Rectangle(0, 0, r.Width, r.Height));
            outer.Exclude(new Rectangle(t, t, Math.Max(0, r.Width - 2 * t), Math.Max(0, r.Height - 2 * t)));
            var old = Region;
            Region = outer;
            old?.Dispose();
            if (!Visible) Show();
        }

        public void ShowText(string text, Point at)
        {
            _text = text;
            var sz = TextRenderer.MeasureText(text, Font);
            var scr = Screen.FromPoint(at).WorkingArea;
            var r = new Rectangle(at, new Size(Math.Min(sz.Width + Ui.S(16), scr.Width), sz.Height + Ui.S(10)));
            if (r.Bottom > scr.Bottom) r.Y = Math.Max(scr.Top, at.Y - r.Height - Ui.S(40));
            if (r.Right > scr.Right) r.X = Math.Max(scr.Left, scr.Right - r.Width);
            Bounds = r;
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_frame) return;
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, _text, Font, new Rectangle(Ui.S(8), 0, Width - Ui.S(8), Height), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>新插件（改介面）嘅範本 + API 說明。</summary>
internal static partial class PluginTemplates
{
    public static string ModManifest(string name, string target) => "{\n" +
        "  \"name\": " + JsonSerializer.Serialize(name) + ",\n" +
        "  \"version\": \"1.0.0\",\n" +
        "  \"author\": \"\",\n" +
        "  \"description\": \"\",\n" +
        "  \"icon\": \"🎨\",\n" +
        "  \"type\": \"mod\",\n" +
        "  \"target\": " + JsonSerializer.Serialize(target) + ",\n" +
        "  \"entry\": \"mod.js\",\n" +
        "  \"permissions\": {\n" +
        "    \"ui\": true\n" +
        "  }\n" +
        "}\n";

    private const string ModJsHant = """
// 插件：修改 MultiTool 本身的外觀（主題、背景圖、字型、側欄、標題列、首頁、設定頁）。
// 現有工具的頁面不能修改；如需新功能，請另外編寫一個新工具。
// 詳細用法見「📖 API 說明」

// 例子 1：加入一個主題（會出現在左下角「主題」選單，選擇後重新啟動即套用到整個程式）
// 背景圖 / 字型檔放在插件資料夾（按「📂 資料夾」）
// mt.app.addTheme({
//   id: 'sakura', name: '櫻花', base: 'light',
//   colors: { bg: '#fff0f6', panel: '#ffe1ee', card: '#ffffff', text: '#5c2a42', dim: '#a05a7c', accent: '#ec5f9c' },
//   wallpaper: 'bg.jpg', dim: 0.4, cardOpacity: 0.85
// });

// 例子 2：將左上角的程式名稱改為強調色（停用插件會自動還原）
mt.ui.override('@sidebar Label[key="多功能工具"]', { foreColor: 'theme:accent' });

console.log('插件已載入');
""";

    private const string ModJsHans = """
// 插件：修改 MultiTool 本身的外观（主题、背景图、字体、侧栏、标题栏、首页、设定页）。
// 现有工具的页面不能修改；如需新功能，请另外编写一个新工具。
// 详细用法见「📖 API 说明」

// 例子 1：加入一个主题（会出现在左下角「主题」选单，选择后重新启动即套用到整个程序）
// 背景图 / 字体档放在插件资料夹（按「📂 资料夹」）
// mt.app.addTheme({
//   id: 'sakura', name: '樱花', base: 'light',
//   colors: { bg: '#fff0f6', panel: '#ffe1ee', card: '#ffffff', text: '#5c2a42', dim: '#a05a7c', accent: '#ec5f9c' },
//   wallpaper: 'bg.jpg', dim: 0.4, cardOpacity: 0.85
// });

// 例子 2：将左上角的程序名称改为强调色（停用插件会自动还原）
mt.ui.override('@sidebar Label[key="多功能工具"]', { foreColor: 'theme:accent' });

console.log('插件已载入');
""";

    private const string ModJsEn = """
// Plugin: changes how MultiTool itself looks (themes, background image, font, sidebar, title bar, home and settings pages).
// Existing tools' pages can't be changed; for new features, write a new tool instead.
// See "📖 API reference" for details

// Example 1: add a theme (it appears in the "Theme" menu at the bottom left; choosing it restarts MultiTool and applies it everywhere)
// Put the background image / font file in the plugin folder ("📂 Folder")
// mt.app.addTheme({
//   id: 'sakura', name: 'Sakura', base: 'light',
//   colors: { bg: '#fff0f6', panel: '#ffe1ee', card: '#ffffff', text: '#5c2a42', dim: '#a05a7c', accent: '#ec5f9c' },
//   wallpaper: 'bg.jpg', dim: 0.4, cardOpacity: 0.85
// });

// Example 2: show the app name at the top left in the accent colour (restored when the plugin is turned off)
mt.ui.override('@sidebar Label[key="多功能工具"]', { foreColor: 'theme:accent' });

console.log('Plugin loaded');
""";

    public static string ModJs => Lang.Base switch { Language.En => ModJsEn, Language.ZhHans => ModJsHans, _ => ModJsHant };

    private const string ModDocHant = """

================ 插件（type: "mod"）：修改 MultiTool 本身的外觀 ================

插件沒有自己的介面，MultiTool 開啟時在背景執行 mod.js。插件可以修改「程式本身」：主題（顏色、背景圖、字型、卡片透明度）、
左側欄、標題列、主視窗、首頁及設定頁。現有工具的頁面及其視窗不能修改——如需新功能，請另外編寫一個新工具。
插件停用、刪除或重新載入時，mt.ui 的修改會自動還原。幾個插件修改同一樣東西時，按工作坊的次序，排後面的會蓋過前面的。

manifest.json 需要："type": "mod", "entry": "mod.js", "permissions": { "ui": true }；其他權限（net、notify、ai…）與工具相同。

━━ 主題（換整個程式的顏色、背景圖、字型，請一律用這個）━━
  mt.app.addTheme({
    id: "sakura",              // 英文字母、數字、- 或 _
    name: "櫻花",               // 顯示在左下角「主題」選單
    base: "light",             // "light" 或 "dark"：沒有指定的顏色用哪一套
    colors: {                  // 全部可選，"#rrggbb"
      bg, panel, card, cardHi, input, border, borderHi, text, dim, faint, accent, accent2, success, danger
      // bg 內容背景、panel 左側欄、card 卡片、cardHi 卡片（滑鼠經過）、input 輸入框、border 邊框、
      // text 文字、dim 次要文字、faint 提示文字、accent 強調色、accent2 第二強調色、success 成功、danger 錯誤
    },
    wallpaper: "bg.jpg",       // 插件資料夾內的背景圖（png / jpg / gif / webp / bmp，≤ 30 MB），會鋪滿整個視窗
    dim: 0.35,                 // 背景圖上蓋一層 bg 色（0 = 完全看到圖，1 = 看不到），令文字看得清
    sideDim: 0.55,             // 左側欄上再蓋一層 panel 色
    cardOpacity: 0.86,         // 有背景圖時卡片的不透明度（0.2–1）
    font: "Microsoft JhengHei UI"  // 或 { file: "myfont.ttf" }（插件資料夾內的字型檔，要有中文字）
  })                           → 主題 id（"mod:<插件>/<id>"）。同一個 id 再叫一次 = 更新。
  mt.app.themes()              → [{ id, name, current }]
  mt.app.useTheme(id)          問使用者是否重新啟動並換成該主題
  mt.app.removeTheme(id)       刪除自己插件的主題
  注意：主題在開啟程式時套用，更改後要重新啟動；插件停用後其主題不會顯示，使用中的話下次開啟會變回預設。

━━ mt.ui：用 selector 修改側欄、標題列、首頁、設定頁的元件 ━━
Selector（類似 CSS）：
  @根 類型#名稱[屬性="值"]:nth(n)   以空格連接 = 在裏面任何一層；以 > 連接 = 直屬下一層
  根：@main 主視窗、@sidebar 左側欄、@titlebar 標題列、@content 右邊內容區（只限外框）、@home 首頁、@settings 設定頁。不寫根 = 主視窗內可改的元件。
  類型：Label、FancyButton、CardPanel、Panel、NavItem…（父類亦可，例如 Button、Control；* = 任何）
  屬性：[key="原文"] 介面原文（程式碼內的廣東話原文，不受介面語言影響，建議使用）、[text="顯示的文字"]、[name="…"]，
        或任何屬性，例如 [Kind="Primary"]。= 等於、*= 包含、^= 開頭、$= 結尾
  :nth(n) 同一層中符合條件的第 n 個（由 0 開始）；:visible 只選顯示中的
  不確定時，用編寫器的「🔍 選擇元件」或 mt.ui.query / mt.ui.tree 查看。工具頁會顯示為 locked。

mt.ui（全部回傳 Promise）：
  mt.ui.override(sel, props)     → rule id。修改符合的元件，之後新建立的元件亦會自動套用
  mt.ui.hide(sel)                = override(sel, { visible: false })
  mt.ui.add({ type, parent | after | before: sel, ...props, onClick, onChange })  → rule id
        type：button、label、checkbox、textbox、panel、image（file: "deco.png"）、web（file: "panel.html"，插件自己的 HTML 頁，可用 mt.*）
        parent：放入該元件裏面（用 x、y 或 dock 定位置）；after / before：放在該元件旁邊
  mt.ui.on(event, sel, fn)       → rule id。event：click、change（fn 收到 { value }）、show（元件出現）；
        mt.ui.on('page', fn) 切換工具頁時 fn({ key })。不能阻止原本的動作。
  mt.ui.remove(id) / mt.ui.reset()          移除一條 / 全部 rule（會還原）
  mt.ui.query(sel) / mt.ui.tree(sel, depth) 查看元件
  mt.ui.click(sel) / mt.ui.setValue(sel, value)
  mt.app.page() / mt.app.pages() / mt.app.open(key)   目前工具頁 / 全部工具頁 / 跳到某頁

props（不分大小寫）：
  text、visible、enabled、tooltip
  foreColor、backColor："#rrggbb"、"#aarrggbb"（半透明）、"transparent"、"red"、"theme:accent"
        （theme: bg panel card cardhi input border text dim faint accent accent2 success danger）
        FancyButton、CardPanel 是自己畫的：backColor / foreColor 會自動改為 Fill / TextColor
  font：數字（大小）、"字型名" 或 { family, size, bold, italic, underline }
  backgroundImage（或 image）："插件資料夾內的圖片"；backgroundImageLayout："Stretch" | "Zoom" | "Tile" | "Center"
  width、height、left (x)、top (y)、padding、margin、dock、anchor：以 100% 縮放的像素計
  其他公開屬性亦可，例如 Kind（"Primary" | "Success" | "Danger" | "Ghost"）、TextAlign、Cursor

例子（動漫主題）：
  mt.app.addTheme({ id: 'anime', name: '星空少女', base: 'dark',
    colors: { bg: '#1a1033', panel: '#22153f', card: '#2b1d4d', text: '#f3e9ff', dim: '#c3a9e8', accent: '#ff7ac6', accent2: '#8fd3ff' },
    wallpaper: 'wallpaper.jpg', dim: 0.45, cardOpacity: 0.8 });
  // 側欄底部加一張角色圖
  mt.ui.add({ type: 'image', parent: '@sidebar', file: 'chibi.png', x: 40, y: 330, width: 110, height: 110 });

程式名稱及版本號不可修改、隱藏或遮蓋。
安全：密碼輸入框不能讀取或修改。插件可以讀取側欄、首頁及設定頁的文字及按掣，只安裝你信任的插件。
""";

    private const string ModDocHans = """

================ 插件（type: "mod"）：修改 MultiTool 本身的外观 ================

插件没有自己的界面，MultiTool 开启时在后台执行 mod.js。插件可以修改「程序本身」：主题（颜色、背景图、字体、卡片透明度）、
左侧栏、标题栏、主窗口、首页及设定页。现有工具的页面及其窗口不能修改——如需新功能，请另外编写一个新工具。
插件停用、删除或重新载入时，mt.ui 的修改会自动还原。几个插件修改同一样东西时，按工作坊的次序，排后面的会盖过前面的。

manifest.json 需要："type": "mod", "entry": "mod.js", "permissions": { "ui": true }；其他权限（net、notify、ai…）与工具相同。

━━ 主题（换整个程序的颜色、背景图、字体，请一律用这个）━━
  mt.app.addTheme({
    id: "sakura",              // 英文字母、数字、- 或 _
    name: "樱花",               // 显示在左下角「主题」选单
    base: "light",             // "light" 或 "dark"：没有指定的颜色用哪一套
    colors: {                  // 全部可选，"#rrggbb"
      bg, panel, card, cardHi, input, border, borderHi, text, dim, faint, accent, accent2, success, danger
      // bg 内容背景、panel 左侧栏、card 卡片、cardHi 卡片（鼠标经过）、input 输入框、border 边框、
      // text 文字、dim 次要文字、faint 提示文字、accent 强调色、accent2 第二强调色、success 成功、danger 错误
    },
    wallpaper: "bg.jpg",       // 插件资料夹内的背景图（png / jpg / gif / webp / bmp，≤ 30 MB），会铺满整个窗口
    dim: 0.35,                 // 背景图上盖一层 bg 色（0 = 完全看到图，1 = 看不到），令文字看得清
    sideDim: 0.55,             // 左侧栏上再盖一层 panel 色
    cardOpacity: 0.86,         // 有背景图时卡片的不透明度（0.2–1）
    font: "Microsoft YaHei UI" // 或 { file: "myfont.ttf" }（插件资料夹内的字体档，要有中文字）
  })                           → 主题 id（"mod:<插件>/<id>"）。同一个 id 再叫一次 = 更新。
  mt.app.themes()              → [{ id, name, current }]
  mt.app.useTheme(id)          问用户是否重新启动并换成该主题
  mt.app.removeTheme(id)       删除自己插件的主题
  注意：主题在开启程序时套用，更改后要重新启动；插件停用后其主题不会显示，使用中的话下次开启会变回默认。

━━ mt.ui：用 selector 修改侧栏、标题栏、首页、设定页的元件 ━━
Selector（类似 CSS）：
  @根 类型#名称[属性="值"]:nth(n)   以空格连接 = 在里面任何一层；以 > 连接 = 直属下一层
  根：@main 主窗口、@sidebar 左侧栏、@titlebar 标题栏、@content 右边内容区（只限外框）、@home 首页、@settings 设定页。不写根 = 主窗口内可改的元件。
  类型：Label、FancyButton、CardPanel、Panel、NavItem…（父类亦可，例如 Button、Control；* = 任何）
  属性：[key="原文"] 界面原文（程序代码内的粤语原文，不受界面语言影响，建议使用）、[text="显示的文字"]、[name="…"]，
        或任何属性，例如 [Kind="Primary"]。= 等于、*= 包含、^= 开头、$= 结尾
  :nth(n) 同一层中符合条件的第 n 个（由 0 开始）；:visible 只选显示中的
  不确定时，用编写器的「🔍 选择元件」或 mt.ui.query / mt.ui.tree 查看。工具页会显示为 locked。

mt.ui（全部回传 Promise）：
  mt.ui.override(sel, props)     → rule id。修改符合的元件，之后新建立的元件亦会自动套用
  mt.ui.hide(sel)                = override(sel, { visible: false })
  mt.ui.add({ type, parent | after | before: sel, ...props, onClick, onChange })  → rule id
        type：button、label、checkbox、textbox、panel、image（file: "deco.png"）、web（file: "panel.html"，插件自己的 HTML 页，可用 mt.*）
        parent：放入该元件里面（用 x、y 或 dock 定位置）；after / before：放在该元件旁边
  mt.ui.on(event, sel, fn)       → rule id。event：click、change（fn 收到 { value }）、show（元件出现）；
        mt.ui.on('page', fn) 切换工具页时 fn({ key })。不能阻止原本的动作。
  mt.ui.remove(id) / mt.ui.reset()          移除一条 / 全部 rule（会还原）
  mt.ui.query(sel) / mt.ui.tree(sel, depth) 查看元件
  mt.ui.click(sel) / mt.ui.setValue(sel, value)
  mt.app.page() / mt.app.pages() / mt.app.open(key)   目前工具页 / 全部工具页 / 跳到某页

props（不分大小写）：
  text、visible、enabled、tooltip
  foreColor、backColor："#rrggbb"、"#aarrggbb"（半透明）、"transparent"、"red"、"theme:accent"
        （theme: bg panel card cardhi input border text dim faint accent accent2 success danger）
        FancyButton、CardPanel 是自己画的：backColor / foreColor 会自动改为 Fill / TextColor
  font：数字（大小）、"字体名" 或 { family, size, bold, italic, underline }
  backgroundImage（或 image）："插件资料夹内的图片"；backgroundImageLayout："Stretch" | "Zoom" | "Tile" | "Center"
  width、height、left (x)、top (y)、padding、margin、dock、anchor：以 100% 缩放的像素计
  其他公开属性亦可，例如 Kind（"Primary" | "Success" | "Danger" | "Ghost"）、TextAlign、Cursor

例子（动漫主题）：
  mt.app.addTheme({ id: 'anime', name: '星空少女', base: 'dark',
    colors: { bg: '#1a1033', panel: '#22153f', card: '#2b1d4d', text: '#f3e9ff', dim: '#c3a9e8', accent: '#ff7ac6', accent2: '#8fd3ff' },
    wallpaper: 'wallpaper.jpg', dim: 0.45, cardOpacity: 0.8 });
  // 侧栏底部加一张角色图
  mt.ui.add({ type: 'image', parent: '@sidebar', file: 'chibi.png', x: 40, y: 330, width: 110, height: 110 });

程序名称及版本号不可修改、隐藏或遮盖。
安全：密码输入框不能读取或修改。插件可以读取侧栏、首页及设定页的文字及按钮，只安装你信任的插件。
""";

    private const string ModDocEn = """

================ Plugins (type: "mod"): change how MultiTool itself looks ================

A plugin has no page of its own; mod.js runs in the background while MultiTool is open. Plugins can change the app itself:
themes (colours, background image, font, card transparency), the sidebar, the title bar, the main window, the home page and the settings page.
Existing tools' pages and windows can't be changed — for new features, write a new tool instead.
mt.ui changes are restored when the plugin is turned off, deleted or reloaded. When several plugins change the same thing, the later one in the Workshop order wins.

manifest.json needs: "type": "mod", "entry": "mod.js", "permissions": { "ui": true }; other permissions (net, notify, ai…) work as for tools.

━━ Themes (always use this to recolour the whole app, add a background image or change the font) ━━
  mt.app.addTheme({
    id: "sakura",              // letters, digits, - or _
    name: "Sakura",            // shown in the "Theme" menu at the bottom left
    base: "light",             // "light" or "dark": used for colours you don't set
    colors: {                  // all optional, "#rrggbb"
      bg, panel, card, cardHi, input, border, borderHi, text, dim, faint, accent, accent2, success, danger
      // bg content background, panel sidebar, card cards, cardHi card on hover, input input boxes, border borders,
      // text text, dim secondary text, faint hint text, accent accent, accent2 second accent, success, danger
    },
    wallpaper: "bg.jpg",       // background image in the plugin folder (png / jpg / gif / webp / bmp, ≤ 30 MB), fills the whole window
    dim: 0.35,                 // a layer of the bg colour over the image (0 = full image, 1 = hidden) so text stays readable
    sideDim: 0.55,             // an extra layer of the panel colour over the sidebar
    cardOpacity: 0.86,         // card opacity when there's a background image (0.2–1)
    font: "Segoe UI"           // or { file: "myfont.ttf" } (a font file in the plugin folder)
  })                           → theme id ("mod:<plugin>/<id>"). Calling again with the same id updates it.
  mt.app.themes()              → [{ id, name, current }]
  mt.app.useTheme(id)          asks the user to restart with that theme
  mt.app.removeTheme(id)       removes one of this plugin's themes
  Note: themes are applied when MultiTool starts, so changes need a restart. A turned-off plugin's themes are hidden; if one is in use, the default comes back next time.

━━ mt.ui: change elements of the sidebar, title bar, home and settings pages with selectors ━━
Selectors (like CSS):
  @root Type#name[attr="value"]:nth(n)   joined by a space = anywhere inside; joined by > = direct child
  Roots: @main main window, @sidebar left sidebar, @titlebar title bar, @content the right-hand content area (frame only), @home home page, @settings settings page.
         No root = every element in the main window that can be changed.
  Types: Label, FancyButton, CardPanel, Panel, NavItem… (base types work too, e.g. Button, Control; * = any)
  Attributes: [key="source text"] the original text in the source code (Cantonese; not affected by the interface language — recommended),
        [text="displayed text"], [name="…"], or any property, e.g. [Kind="Primary"]. = equals, *= contains, ^= starts with, $= ends with
  :nth(n) the n-th matching element at that level (from 0); :visible only shown elements
  When unsure, use the editor's "🔍 Pick element" or mt.ui.query / mt.ui.tree. Tool pages show as locked.

mt.ui (all return Promises):
  mt.ui.override(sel, props)     → rule id. Changes matching elements; elements created later get it too
  mt.ui.hide(sel)                = override(sel, { visible: false })
  mt.ui.add({ type, parent | after | before: sel, ...props, onClick, onChange })  → rule id
        type: button, label, checkbox, textbox, panel, image (file: "deco.png"), web (file: "panel.html", the plugin's own HTML page with mt.*)
        parent: put it inside that element (position with x, y or dock); after / before: next to that element
  mt.ui.on(event, sel, fn)       → rule id. event: click, change (fn gets { value }), show (element appears);
        mt.ui.on('page', fn) calls fn({ key }) when the tool page changes. The original action can't be cancelled.
  mt.ui.remove(id) / mt.ui.reset()          remove one / all rules (restores them)
  mt.ui.query(sel) / mt.ui.tree(sel, depth) inspect elements
  mt.ui.click(sel) / mt.ui.setValue(sel, value)
  mt.app.page() / mt.app.pages() / mt.app.open(key)   current tool page / all tool pages / go to a page

props (case-insensitive):
  text, visible, enabled, tooltip
  foreColor, backColor: "#rrggbb", "#aarrggbb" (semi-transparent), "transparent", "red", "theme:accent"
        (theme: bg panel card cardhi input border text dim faint accent accent2 success danger)
        FancyButton and CardPanel draw themselves: backColor / foreColor become Fill / TextColor automatically
  font: a number (size), "font name" or { family, size, bold, italic, underline }
  backgroundImage (or image): "an image in the plugin folder"; backgroundImageLayout: "Stretch" | "Zoom" | "Tile" | "Center"
  width, height, left (x), top (y), padding, margin, dock, anchor: pixels at 100% scaling
  Other public properties work too, e.g. Kind ("Primary" | "Success" | "Danger" | "Ghost"), TextAlign, Cursor

Example (anime theme):
  mt.app.addTheme({ id: 'anime', name: 'Starry Night', base: 'dark',
    colors: { bg: '#1a1033', panel: '#22153f', card: '#2b1d4d', text: '#f3e9ff', dim: '#c3a9e8', accent: '#ff7ac6', accent2: '#8fd3ff' },
    wallpaper: 'wallpaper.jpg', dim: 0.45, cardOpacity: 0.8 });
  // a character picture at the bottom of the sidebar
  mt.ui.add({ type: 'image', parent: '@sidebar', file: 'chibi.png', x: 40, y: 330, width: 110, height: 110 });

The app name and version number can't be changed, hidden or covered.
Security: password boxes can't be read or changed. Plugins can read text in the sidebar, home and settings pages and click buttons — only install plugins you trust.
""";

    public static string ModDoc => Lang.Base switch { Language.En => ModDocEn, Language.ZhHans => ModDocHans, _ => ModDocHant };
}
