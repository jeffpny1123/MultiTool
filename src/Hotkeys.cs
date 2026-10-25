using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool;

/// <summary>
/// 各工具嘅快捷鍵集中管理：喺工具頁或者「設定 → 快捷鍵」都改得；
/// 剔咗「頁面外使用」就喺任何地方都用得，冇剔就只喺嗰個工具頁先用得。重複咗嘅快捷鍵唔可以剔。
/// 存喺 data\hotkeys.txt（id|修飾鍵|鍵|頁面外）。
/// </summary>
internal static class HotkeyHub
{
    internal sealed class Entry
    {
        public required string Id;
        public required string Ext;          // 屬於邊個工具（要安裝咗先生效）
        public required string Page;         // 工具頁嘅 key
        public required Func<string> Name;
        public bool NoKey;                   // 冇自己嘅快捷鍵（例如各腳本自己設定），淨係有「頁面外使用」
        public string? LegacyFile;           // 舊版嘅快捷鍵檔（第一次讀返）
        public bool DefaultGlobal = true;
        public Hotkey Key;
        public bool Global;
        public Action? Action;               // 撳咗快捷鍵做乜（工具頁整好時設定）
        internal bool WasDown;
        public event Action? Changed;
        internal void Raise() => Changed?.Invoke();
    }

    public static readonly List<Entry> All = new()
    {
        new() { Id = "scripts.stopall", Ext = Extensions.Scripts, Page = "scripts", Name = () => Lang.T("我的腳本：開始已選 / 全部停止"), LegacyFile = "stopall_hotkey.txt" },
        new() { Id = "scripts.each", Ext = Extensions.Scripts, Page = "scripts", Name = () => Lang.T("我的腳本：各腳本自己嘅快捷鍵"), NoKey = true },
        new() { Id = "recorder.toggle", Ext = Extensions.Recorder, Page = "recorder", Name = () => Lang.T("螢幕錄影：開始 / 停止錄影"), LegacyFile = "recorder_hotkey.txt" },
        new() { Id = "replay.save", Ext = Extensions.Replay, Page = "replay", Name = () => Lang.T("精彩重播：儲存重播") },
        new() { Id = "replay.toggle", Ext = Extensions.Replay, Page = "replay", Name = () => Lang.T("精彩重播：開 / 關背景錄影") },
        new() { Id = "remap.toggle", Ext = Extensions.Remap, Page = "remap", Name = () => Lang.T("遊戲改鍵：開 / 關改鍵") },
        new() { Id = "window.top", Ext = Extensions.Window, Page = "window", Name = () => Lang.T("視窗管理：目前視窗置頂 / 取消") },
        new() { Id = "window.borderless", Ext = Extensions.Window, Page = "window", Name = () => Lang.T("視窗管理：目前視窗無邊框全螢幕 / 還原") },
        new() { Id = "clipboard.show", Ext = Extensions.Clipboard, Page = "clipboard", Name = () => Lang.T("剪貼簿紀錄：打開紀錄視窗") },
        new() { Id = "ocr.capture", Ext = Extensions.Ocr, Page = "ocr", Name = () => Lang.T("文字辨識：框選畫面辨識") },
        new() { Id = "perf.overlay", Ext = Extensions.Perf, Page = "perf", Name = () => Lang.T("效能監控：顯示 / 隱藏遊戲疊加") },
        new() { Id = "perf.gamemode", Ext = Extensions.Perf, Page = "perf", Name = () => Lang.T("效能監控：開 / 關遊戲模式") },
    };

    /// <summary>MainForm 設定：而家顯示緊邊頁。</summary>
    public static Func<string?>? CurrentPage;
    public static event Action? AnyChanged;

    private static string File_ => Path.Combine(AppPaths.Data, "hotkeys.txt");
    private static readonly System.Windows.Forms.Timer Poll = new() { Interval = 30 };

    static HotkeyHub()
    {
        var saved = new Dictionary<string, string[]>();
        try
        {
            if (File.Exists(File_))
                foreach (var l in File.ReadAllLines(File_))
                {
                    var p = l.Split('|');
                    if (p.Length >= 4) saved[p[0]] = p;
                }
        }
        catch { }
        foreach (var e in All)
        {
            if (saved.TryGetValue(e.Id, out var p))
            {
                e.Key = Hotkey.Parse(p[1] + "|" + p[2]);
                e.Global = p[3] == "1";
            }
            else
            {
                e.Global = e.DefaultGlobal;
                try
                {
                    if (e.LegacyFile != null)
                    {
                        var f = Path.Combine(AppPaths.Data, e.LegacyFile);
                        if (File.Exists(f)) e.Key = Hotkey.Parse(File.ReadAllText(f));
                    }
                }
                catch { }
            }
        }
        Poll.Tick += (_, _) => Tick();
    }

    public static Entry Get(string id) => All.First(e => e.Id == id);

    public static void Start() => Poll.Start();

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, All.Select(e => $"{e.Id}|{e.Key.Serialize()}|{(e.Global ? 1 : 0)}"));
        }
        catch { }
    }

    public static void SetKey(Entry e, Hotkey k)
    {
        if (e.Key == k) return;
        e.Key = k;
        Save();
        foreach (var o in All) o.Raise(); // 重複狀態可能變咗
        AnyChanged?.Invoke();
    }

    public static void SetGlobal(Entry e, bool on)
    {
        if (e.Global == on) return;
        e.Global = on;
        Save();
        e.Raise();
        AnyChanged?.Invoke();
    }

    /// <summary>同其他已安裝工具嘅快捷鍵撞咗。</summary>
    public static bool IsDuplicate(Entry e) =>
        !e.NoKey && !e.Key.IsEmpty &&
        All.Any(o => o != e && !o.NoKey && o.Key == e.Key && Extensions.IsInstalled(o.Ext));

    /// <summary>頁面外都用得（剔咗而且冇重複）。</summary>
    public static bool EffectiveGlobal(Entry e) => e.Global && !IsDuplicate(e);

    public static bool EffectiveGlobal(string id) => EffectiveGlobal(Get(id));

    /// <summary>而家呢個快捷鍵用唔用得：工具裝咗，而且（喺佢個頁 或者 可以頁面外使用）。</summary>
    public static bool Allowed(Entry e) =>
        Extensions.IsInstalled(e.Ext) && (CurrentPage?.Invoke() == e.Page || EffectiveGlobal(e));

    private static void Tick()
    {
        bool busy = HotkeyBox.Busy;
        foreach (var e in All)
        {
            if (e.NoKey || e.Action == null) continue;
            bool down = !busy && !e.Key.IsEmpty && e.Key.IsDown() && Allowed(e);
            bool fire = down && !e.WasDown && !Keyboard.RecentlyInjected;
            e.WasDown = down;
            if (!fire) continue;
            try { e.Action(); }
            catch (Exception ex) { AppPaths.LogError("快捷鍵 " + e.Id, ex); }
        }
    }

    /// <summary>將工具頁上面嘅快捷鍵格仔連去呢個設定（兩邊改都會同步）。</summary>
    public static void Bind(HotkeyBox box, string id)
    {
        var e = Get(id);
        box.Hotkey = e.Key;
        box.Changed += () => SetKey(e, box.Hotkey);
        void Sync() { if (!box.IsDisposed && box.Hotkey != e.Key) box.Hotkey = e.Key; }
        e.Changed += Sync;
        box.Disposed += (_, _) => e.Changed -= Sync;
    }

    /// <summary>新建一個已連好嘅快捷鍵格仔（連外框）。</summary>
    public static InputHost Box(string id, int width)
    {
        var box = new HotkeyBox();
        Bind(box, id);
        return new InputHost(box, width);
    }
}

/// <summary>設定頁：「快捷鍵」表（已安裝工具嘅快捷鍵 + 頁面外使用）。</summary>
internal sealed class HotkeyTable : FlowLayoutPanel
{
    private readonly List<(HotkeyHub.Entry E, CheckBox Cb, Label Warn)> _rows = new();

    public HotkeyTable()
    {
        AutoSize = true;
        BackColor = Ui.Card;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Rebuild();
        Extensions.Changed += OnExt;
        HotkeyHub.AnyChanged += Refresh2;
        Disposed += (_, _) => { Extensions.Changed -= OnExt; HotkeyHub.AnyChanged -= Refresh2; };
    }

    private void OnExt()
    {
        if (IsDisposed || !IsHandleCreated) { if (!IsDisposed) Rebuild(); return; }
        BeginInvoke(() =>
        {
            Rebuild();
            Parent?.Parent?.PerformLayout();
        });
    }

    private Label Cell(string text, int w, Color c, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Width = w, Height = Ui.S(34), ForeColor = c, BackColor = Ui.Card,
        Font = Ui.F(9.75f, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, Ui.S(10), 0), AutoEllipsis = true, UseMnemonic = false
    };

    private void Rebuild()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        _rows.Clear();
        int wName = Ui.S(300), wKey = Ui.S(160);
        var head = Row();
        head.Controls.Add(Cell(Lang.T("工具"), wName, Ui.TextDim, true));
        head.Controls.Add(Cell(Lang.T("快捷鍵"), wKey, Ui.TextDim, true));
        head.Controls.Add(Cell(Lang.T("頁面外使用"), Ui.S(120), Ui.TextDim, true));
        Controls.Add(head);
        var list = HotkeyHub.All.Where(e => Extensions.IsInstalled(e.Ext)).ToList();
        if (list.Count == 0)
            Controls.Add(Cell(Lang.T("未安裝任何有快捷鍵嘅工具（去「工具庫」安裝）"), Ui.S(520), Ui.TextDim));
        foreach (var e in list)
        {
            var r = Row();
            r.Controls.Add(Cell(e.Name(), wName, Ui.Text));
            if (e.NoKey) r.Controls.Add(Cell(Lang.T("（喺各腳本設定）"), wKey, Ui.TextDim));
            else
            {
                var host = HotkeyHub.Box(e.Id, wKey);
                host.Margin = new Padding(0, 0, Ui.S(10), 0);
                r.Controls.Add(host);
            }
            var cb = new CheckBox
            {
                AutoSize = true, BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.F(9.75f),
                Margin = new Padding(Ui.S(4), Ui.S(9), Ui.S(8), 0)
            };
            var entry = e;
            cb.CheckedChanged += (_, _) => { if (cb.Enabled) HotkeyHub.SetGlobal(entry, cb.Checked); };
            r.Controls.Add(cb);
            var warn = Cell("", Ui.S(200), Ui.Danger);
            r.Controls.Add(warn);
            Controls.Add(r);
            _rows.Add((e, cb, warn));
        }
        Refresh2();
        ResumeLayout(true);
    }

    private FlowLayoutPanel Row() => new()
    {
        AutoSize = true, BackColor = Ui.Card, WrapContents = false, Margin = new Padding(0, 0, 0, Ui.S(6))
    };

    /// <summary>更新剔格：重複咗就唔可以剔。</summary>
    private void Refresh2()
    {
        if (IsDisposed) return;
        foreach (var (e, cb, warn) in _rows)
        {
            bool dup = HotkeyHub.IsDuplicate(e);
            cb.Enabled = !dup;
            cb.Checked = e.Global && !dup;
            warn.Text = dup ? Lang.T("快捷鍵重複咗") : "";
        }
    }
}
