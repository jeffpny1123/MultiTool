using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

// =====================================================================
//  資料
// =====================================================================

public enum StepKind { Key, Click, Wait, Image, If, Loop, Break }

/// <summary>按鍵 / 滑鼠嘅動作：撳住（按下 → 等 → 彈起）、淨係按下、淨係彈起。</summary>
public enum PressMode { Hold, Down, Up }

/// <summary>腳本入面一個步驟。</summary>
public sealed class Step
{
    public StepKind Kind;
    public int Vk;                 // 按鍵
    public Keys Mods;              // 按鍵：組合鍵（Ctrl / Shift / Alt）
    public PressMode Mode = PressMode.Hold; // 按鍵 / 點擊
    public decimal Hold = 0.1m;    // 按鍵 / 點擊：撳住幾多秒
    public bool Right;             // 點擊：右鍵
    public int X, Y;               // 點擊：座標
    public decimal Seconds = 1m;   // 等待
    public string Image = "";      // 搵圖：腳本資料夾入面嘅檔名
    public int Similarity = 80;    // 搵圖：相似度 %
    public IfBlock? Sub;           // IF 步驟（IF 入面嘅 IF）：條件 + 入面嘅步驟
    public int Count = 0;          // 循環：重複幾多次（0 = 一直重複，預設）
    public List<Step> Body = new(); // 循環：入面嘅步驟

    public Step Clone()
    {
        var c = (Step)MemberwiseClone();
        c.Sub = Sub?.Clone();
        c.Body = Body.Select(x => x.Clone()).ToList();
        return c;
    }

    public static string KindName(StepKind k) => k switch
    {
        StepKind.Key => Lang.T("按鍵"),
        StepKind.Click => Lang.T("滑鼠點擊"),
        StepKind.Wait => Lang.T("等待"),
        StepKind.If => Lang.T("IF 條件"),
        StepKind.Loop => Lang.T("循環"),
        StepKind.Break => Lang.T("中斷循環"),
        _ => Lang.T("搵圖點擊")
    };

    public static string ModeName(PressMode m) => m switch { PressMode.Down => Lang.T("按下"), PressMode.Up => Lang.T("彈起"), _ => Lang.T("撳住") };

    private static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public string Serialize() => Kind switch
    {
        StepKind.Key => $"key|{Vk}|{D(Hold)}|{(int)Mods}|{(int)Mode}",
        StepKind.Click => $"click|{(Right ? "R" : "L")}|{X}|{Y}|{(int)Mode}|{D(Hold)}",
        StepKind.Wait => $"wait|{D(Seconds)}",
        StepKind.If => "if|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((Sub ?? new IfBlock()).SerializeFull())),
        StepKind.Break => "break",
        StepKind.Loop => $"loop|{Count}|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", Body.Select(b => b.Serialize())))),
        _ => $"image|{Image}|{Similarity}"
    };

    public static Step? Parse(string line)
    {
        var p = line.Split('|');
        static decimal Dec(string s, decimal def) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : def;
        static int Int(string s, int def) => int.TryParse(s, out var v) ? v : def;
        string At(int i) => i < p.Length ? p[i] : "";
        PressMode Mode(int i) => (PressMode)Math.Clamp(Int(At(i), 0), 0, 2);
        try
        {
            return p[0] switch
            {
                "key" => new Step
                {
                    Kind = StepKind.Key, Vk = Int(p[1], 0), Hold = Dec(At(2), 0.1m),
                    Mods = (Keys)Int(At(3), 0) & (Keys.Control | Keys.Shift | Keys.Alt), Mode = Mode(4)
                },
                "click" => new Step
                {
                    Kind = StepKind.Click, Right = p[1] == "R", X = Int(p[2], 0), Y = Int(p[3], 0),
                    Mode = Mode(4), Hold = Dec(At(5), 0.1m)
                },
                "wait" => new Step { Kind = StepKind.Wait, Seconds = Dec(p[1], 1m) },
                "image" => new Step { Kind = StepKind.Image, Image = p[1], Similarity = Int(p[2], 80) },
                "break" => new Step { Kind = StepKind.Break },
                "loop" => new Step
                {
                    Kind = StepKind.Loop, Count = Math.Max(0, Int(p[1], 2)),
                    Body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(At(2)))
                        .Split('\n').Where(l => l.Length > 0).Select(Parse).Where(x => x != null).Select(x => x!).ToList()
                },
                "if" => new Step
                {
                    Kind = StepKind.If,
                    Sub = IfBlock.ParseFull(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p[1]))) ?? new IfBlock()
                },
                _ => null
            };
        }
        catch { return null; }
    }
}

/// <summary>一個步驟 / 條件可以有最多 6 張圖，用「,」分隔存喺同一個欄位。</summary>
public static class ImageList
{
    public const int Max = 6;

    public static List<string> Split(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string Join(IEnumerable<string> names) => string.Join(",", names.Take(Max));

    public static bool Has(string field, string name) => Split(field).Contains(name);
}

/// <summary>IF 條件嘅種類。</summary>
public enum CondKind { ImageFound, ImageNotFound, PixelColor, Time, Countdown }

/// <summary>
/// IF 框：自己一條線一直檢查條件，成立就行入面嘅步驟（同主步驟同時行，互不干擾）。
/// </summary>
public sealed class IfBlock
{
    public string Id = Guid.NewGuid().ToString("N")[..6];
    public CondKind Kind = CondKind.ImageFound;
    public string Image = "";          // 搵圖 / 搵唔到圖
    public int Similarity = 80;
    public int X, Y;                   // 顏色
    public int Color = 0xFF0000;       // RGB
    public int Tolerance = 10;         // 每個色板最多差幾多
    public string Time = "12:00:00";   // 現實時間 HH:mm:ss
    public decimal Seconds = 60m;      // 倒數
    public decimal Interval = 1m;      // 幾耐檢查一次
    public bool Once;                  // 觸發一次就停
    public string Name = "";           // 標題（空就用「IF 條件 n」）
    public bool Collapsed;             // 編輯介面摺埋
    public bool ClickFound;            // 搵到圖：順手喺嗰個位置左鍵點一下
    public List<Step> Steps = new();

    public IfBlock Clone()
    {
        var c = (IfBlock)MemberwiseClone();
        c.Steps = Steps.Select(x => x.Clone()).ToList();
        return c;
    }

    public static string KindName(CondKind k) => k switch
    {
        CondKind.ImageFound => Lang.T("畫面搵到圖"),
        CondKind.ImageNotFound => Lang.T("畫面搵唔到圖"),
        CondKind.PixelColor => Lang.T("某位置係某顏色"),
        CondKind.Time => Lang.T("現實時間到咗"),
        _ => Lang.T("倒數完")
    };

    /// <summary>呢種條件要唔要設「每幾多秒檢查」。</summary>
    public bool UsesInterval => Kind is CondKind.ImageFound or CondKind.ImageNotFound or CondKind.PixelColor;

    private static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public string Serialize() =>
        $"{Id};kind={(int)Kind};img={Image};sim={Similarity};x={X};y={Y};color={Color};tol={Tolerance};" +
        $"time={Time};sec={D(Seconds)};interval={D(Interval)};once={(Once ? 1 : 0)};" +
        $"fold={(Collapsed ? 1 : 0)};click={(ClickFound ? 1 : 0)};name={Name.Replace(';', '；').Replace('\n', ' ').Replace('\r', ' ')}";

    /// <summary>連埋入面啲步驟一齊（IF 步驟用，可以一層層 nest）。</summary>
    public string SerializeFull() =>
        string.Join("\n", new[] { "if=" + Serialize() }.Concat(Steps.Select(s => "s=" + s.Serialize())));

    public static IfBlock? ParseFull(string text)
    {
        IfBlock? b = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("if=")) b = Parse(line[3..]);
            else if (line.StartsWith("s=") && b != null)
            {
                var st = Step.Parse(line[2..]);
                if (st != null) b.Steps.Add(st);
            }
        }
        return b;
    }

    public static IfBlock? Parse(string text)
    {
        var parts = text.Split(';');
        if (parts.Length == 0 || parts[0].Length == 0) return null;
        var b = new IfBlock { Id = parts[0] };
        foreach (var kv in parts.Skip(1))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0) continue;
            string k = kv[..eq], v = kv[(eq + 1)..];
            int I(int def) => int.TryParse(v, out var x) ? x : def;
            decimal Dm(decimal def) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : def;
            switch (k)
            {
                case "kind": b.Kind = (CondKind)Math.Clamp(I(0), 0, 4); break;
                case "img": b.Image = v; break;
                case "sim": b.Similarity = I(80); break;
                case "x": b.X = I(0); break;
                case "y": b.Y = I(0); break;
                case "color": b.Color = I(0xFF0000); break;
                case "tol": b.Tolerance = I(10); break;
                case "time": b.Time = v; break;
                case "sec": b.Seconds = Dm(60m); break;
                case "interval": b.Interval = Dm(1m); break;
                case "once": b.Once = v == "1"; break;
                case "fold": b.Collapsed = v == "1"; break;
                case "click": b.ClickFound = v == "1"; break;
                case "name": b.Name = v; break;
            }
        }
        return b;
    }
}

/// <summary>一個腳本：名、運行方式、快捷鍵、步驟。存喺 data\scripts\&lt;id&gt;\script.txt。</summary>
public sealed class Script
{
    /// <summary>程式運行時用嘅識別（唔會儲存）。</summary>
    public string Id = Guid.NewGuid().ToString("N")[..8];
    /// <summary>資料夾名：跟腳本名（data\scripts\&lt;腳本名&gt;）。</summary>
    public string Folder = "";
    public string Name = Lang.T("新腳本");
    public bool Loop;
    public Hotkey Hotkey;
    public List<Step> Steps = new();
    public List<IfBlock> Ifs = new();
    public bool Background;            // 背景執行：淨係喺揀咗嘅程式視窗入面截圖 / 點擊 / 按鍵
    public string TargetProcess = "";  // 背景程式（process 名）
    public string TargetTitle = "";    // 背景程式視窗標題
    public double Speed = 1.0;         // 運行速度倍率（0.5x – 3.0x）：等待 / 撳住 / 檢查間隔都除以佢

    public string Dir
    {
        get
        {
            if (Folder.Length == 0) Folder = UniqueFolder(Name);
            return Path.Combine(AppPaths.Scripts, Folder);
        }
    }

    /// <summary>腳本名 → 合法資料夾名（拎走 \ / : * ? " &lt; &gt; | 等）。</summary>
    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? Lang.T("腳本") : cleaned;
    }

    /// <summary>搵一個未被其他腳本用緊嘅資料夾名（撞名就加 (2)、(3)…）。</summary>
    private string UniqueFolder(string name)
    {
        string baseName = Sanitize(name), cand = baseName;
        for (int i = 2; Directory.Exists(Path.Combine(AppPaths.Scripts, cand)) &&
                        !string.Equals(cand, Folder, StringComparison.OrdinalIgnoreCase); i++)
            cand = $"{baseName} ({i})";
        return cand;
    }

    /// <summary>資料夾名同腳本名唔一致就改名（例如改咗腳本名之後）。</summary>
    public void SyncFolder()
    {
        try
        {
            string want = Sanitize(Name);
            if (Folder.Length == 0) { Folder = UniqueFolder(Name); return; }
            // 已經係「名」或者「名 (n)」就唔使郁
            if (string.Equals(Folder, want, StringComparison.OrdinalIgnoreCase) ||
                (Folder.StartsWith(want + " (", StringComparison.OrdinalIgnoreCase) && Folder.EndsWith(")"))) return;
            string target = UniqueFolder(Name);
            string from = Path.Combine(AppPaths.Scripts, Folder), to = Path.Combine(AppPaths.Scripts, target);
            if (Directory.Exists(from)) Directory.Move(from, to);
            Folder = target;
        }
        catch (Exception ex) { AppPaths.LogError("改腳本資料夾名", ex); }
    }

    /// <summary>呢張圖仲有冇地方用緊（刪步驟時用嚟決定刪唔刪圖檔）。</summary>
    public bool UsesImage(string name) =>
        name.Length > 0 && (StepsUse(Steps, name) || Ifs.Any(b => ImageList.Has(b.Image, name) || StepsUse(b.Steps, name)));

    private static bool StepsUse(IEnumerable<Step> steps, string name) =>
        steps.Any(s => ImageList.Has(s.Image, name)
                       || (s.Sub != null && (ImageList.Has(s.Sub.Image, name) || StepsUse(s.Sub.Steps, name)))
                       || StepsUse(s.Body, name));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>
            {
                "name=" + Name.Replace('\n', ' ').Replace('\r', ' '),
                "mode=" + (Loop ? "loop" : "once"),
                "hotkey=" + Hotkey.Serialize(),
                "bg=" + (Background ? "1" : "0"),
                "bgproc=" + TargetProcess,
                "bgtitle=" + TargetTitle.Replace('\n', ' ').Replace('\r', ' '),
                "speed=" + Speed.ToString("0.0", CultureInfo.InvariantCulture)
            };
            lines.AddRange(Steps.Select(s => "step=" + s.Serialize()));
            foreach (var b in Ifs)
            {
                lines.Add("if=" + b.Serialize());
                lines.AddRange(b.Steps.Select(s => $"ifstep={b.Id}|{s.Serialize()}"));
            }
            File.WriteAllLines(Path.Combine(Dir, "script.txt"), lines);
        }
        catch (Exception ex) { AppPaths.LogError("儲存腳本", ex); }
    }

    public static Script? Load(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "script.txt");
            if (!File.Exists(file)) return null;
            var sc = new Script { Folder = Path.GetFileName(dir) };
            foreach (var line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                switch (k)
                {
                    case "name": sc.Name = v; break;
                    case "mode": sc.Loop = v == "loop"; break;
                    case "hotkey": sc.Hotkey = Hotkey.Parse(v); break;
                    case "bg": sc.Background = v == "1"; break;
                    case "bgproc": sc.TargetProcess = v; break;
                    case "bgtitle": sc.TargetTitle = v; break;
                    case "speed":
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) sc.Speed = Math.Clamp(sp, 0.5, 3.0);
                        break;
                    case "step":
                        var st = Step.Parse(v);
                        if (st != null) sc.Steps.Add(st);
                        break;
                    case "if":
                        var b = IfBlock.Parse(v);
                        if (b != null) sc.Ifs.Add(b);
                        break;
                    case "ifstep":
                        int bar = v.IndexOf('|');
                        if (bar < 0) break;
                        var owner = sc.Ifs.FirstOrDefault(x => x.Id == v[..bar]);
                        var ist = Step.Parse(v[(bar + 1)..]);
                        if (owner != null && ist != null) owner.Steps.Add(ist);
                        break;
                }
            }
            return sc;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("讀腳本", ex);
            return null;
        }
    }

    public static List<Script> LoadAll()
    {
        var list = new List<Script>();
        try
        {
            if (!Directory.Exists(AppPaths.Scripts)) return list;
            foreach (var d in Directory.GetDirectories(AppPaths.Scripts).OrderBy(d => Directory.GetCreationTimeUtc(d)))
            {
                var sc = Load(d);
                if (sc == null) continue;
                sc.SyncFolder(); // 舊腳本（亂碼資料夾名）自動改返做腳本名
                list.Add(sc);
            }
        }
        catch (Exception ex) { AppPaths.LogError("讀腳本清單", ex); }
        return list;
    }
}

// =====================================================================
//  運行
// =====================================================================

/// <summary>負責行腳本（每個腳本一條 thread，可以同時行幾個）。</summary>
public static class ScriptRunner
{
    private static readonly Dictionary<string, CancellationTokenSource> Running = new();

    /// <summary>有腳本開始 / 停止（喺 UI thread 觸發）。</summary>
    public static event Action? Changed;

    /// <summary>腳本嘅運行狀態字（id, 文字）。</summary>
    public static event Action<string, string>? Status;

    private static Control? _ui;
    private static bool _dialogOpen;

    public static void Init(Control ui) => _ui = ui;

    public static bool IsRunning(Script s) => Running.ContainsKey(s.Id);

    private static readonly Dictionary<string, DateTime> LastToggle = new();

    /// <summary>開始 / 停止；同一個腳本 0.5 秒內只認一次（防連撳 / 快捷鍵彈兩次）。</summary>
    public static void Toggle(Script s)
    {
        var now = DateTime.UtcNow;
        if (LastToggle.TryGetValue(s.Id, out var last) && (now - last).TotalMilliseconds < 500) return;
        LastToggle[s.Id] = now;
        if (IsRunning(s)) Stop(s); else Start(s);
    }

    public static void Start(Script s)
    {
        if (IsRunning(s) || _ui == null) return;
        if (s.Steps.Count == 0 && s.Ifs.Count == 0)
        {
            Status?.Invoke(s.Id, Lang.T("冇步驟，行唔到"));
            return;
        }
        // 開始前先檢查有冇未設定好嘅步驟
        var problems = Validate(s);
        if (problems.Count > 0)
        {
            Status?.Invoke(s.Id, Lang.F("有 {0} 個步驟未設定好，未開始", problems.Count));
            string list = string.Join("\n", problems.Take(12).Select(x => "• " + x));
            if (problems.Count > 12) list += Lang.F("\n…仲有 {0} 個", problems.Count - 12);
            if (_dialogOpen) return; // 已經有個提示開緊
            _dialogOpen = true;
            try
            {
                MessageBox.Show(_ui.FindForm(), Lang.F("「{0}」有啲步驟未設定好，請先改好：\n\n{1}", s.Name, list), Lang.T("未能開始"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _dialogOpen = false; }
            return;
        }
        var cts = new CancellationTokenSource();
        Running[s.Id] = cts;
        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(s, cts.Token));
    }

    /// <summary>搵出所有未設定好嘅地方（冇圖、圖檔唔見咗、按鍵未設定、空嘅循環）。</summary>
    public static List<string> Validate(Script s)
    {
        var errs = new List<string>();
        if (s.Background)
        {
            if (s.TargetProcess.Length == 0) errs.Add(Lang.T("背景執行：未揀程式"));
            else if (Win.Find(s.TargetProcess, s.TargetTitle) == 0) errs.Add(Lang.F("背景執行：搵唔到「{0} - {1}」，請先開咗個程式或者重新揀", s.TargetProcess, s.TargetTitle));
        }
        CheckSteps(s, s.Steps, Lang.T("主線程"), errs);
        foreach (var b in s.Ifs)
        {
            string where = Lang.F("IF 框「{0}」", b.Name);
            CheckCond(s, b, where, errs);
            CheckSteps(s, b.Steps, where, errs);
        }
        return errs;
    }

    private static void CheckImages(Script s, string field, string where, List<string> errs)
    {
        var names = ImageList.Split(field);
        if (names.Count == 0) errs.Add(Lang.F("{0}：未截圖 / 揀圖", where));
        else if (names.Any(n => !File.Exists(Path.Combine(s.Dir, n)))) errs.Add(Lang.F("{0}：有圖檔唔見咗，請重新截圖", where));
    }

    private static void CheckCond(Script s, IfBlock b, string where, List<string> errs)
    {
        if (b.Kind is CondKind.ImageFound or CondKind.ImageNotFound) CheckImages(s, b.Image, where + Lang.T(" 條件"), errs);
        if (b.Kind == CondKind.Time && !TimeSpan.TryParse(b.Time, CultureInfo.InvariantCulture, out _))
            errs.Add(Lang.F("{0} 條件：時間格式唔啱", where));
    }

    private static void CheckSteps(Script s, List<Step> steps, string path, List<string> errs)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            string where = Lang.F("{0} › 第 {1} 步（{2}）", path, i + 1, Step.KindName(st.Kind));
            switch (st.Kind)
            {
                case StepKind.Key:
                    if (st.Vk == 0) errs.Add(Lang.F("{0}：未設定按鍵", where));
                    break;
                case StepKind.Image:
                    CheckImages(s, st.Image, where, errs);
                    break;
                case StepKind.Loop:
                    if (st.Body.Count == 0) errs.Add(Lang.F("{0}：循環入面冇步驟", where));
                    CheckSteps(s, st.Body, where, errs);
                    break;
                case StepKind.If:
                    if (st.Sub == null) { errs.Add(Lang.F("{0}：條件唔見咗", where)); break; }
                    CheckCond(s, st.Sub, $"{where}「{st.Sub.Name}」", errs);
                    CheckSteps(s, st.Sub.Steps, where, errs);
                    break;
            }
        }
    }

    public static void Stop(Script s, string? text = null)
    {
        if (!Running.Remove(s.Id, out var cts)) return;
        try { cts.Cancel(); } catch { }
        Status?.Invoke(s.Id, text ?? Lang.T("已停止"));
        Changed?.Invoke();
    }

    public static void StopAll()
    {
        foreach (var cts in Running.Values) try { cts.Cancel(); } catch { }
        Running.Clear();
    }

    private static T OnUi<T>(Func<T> f) => _ui!.InvokeRequired ? (T)_ui.Invoke(f) : f();

    private static void Post(Action a)
    {
        try { if (_ui != null && !_ui.IsDisposed) _ui.BeginInvoke(a); } catch { }
    }

    private static void Report(Script s, string text) => Post(() => Status?.Invoke(s.Id, text));
    private static void Report(string key, string text) => Post(() => Status?.Invoke(key, text));

    /// <summary>今次運行按咗落但未彈起嘅鍵 / 滑鼠掣（停止時會自動彈返起）。</summary>
    private sealed class Held
    {
        public readonly HashSet<int> Keys = new();
        public bool Left, Right;
        public Point LastMouse;
        public RunCtx? Ctx;

        public void ReleaseAll()
        {
            var c = Ctx ?? RunCtx.Foreground;
            foreach (var vk in Keys) c.KeyUp(vk, false);
            Keys.Clear();
            if (Left) c.MouseUp(LastMouse.X, LastMouse.Y, false, move: false);
            if (Right) c.MouseUp(LastMouse.X, LastMouse.Y, true, move: false);
            Left = Right = false;
        }
    }

    private static int[] ModVks(Keys mods)
    {
        var l = new List<int>();
        if (mods.HasFlag(System.Windows.Forms.Keys.Control)) l.Add(0x11);
        if (mods.HasFlag(System.Windows.Forms.Keys.Shift)) l.Add(0x10);
        if (mods.HasFlag(System.Windows.Forms.Keys.Alt)) l.Add(0x12);
        return l.ToArray();
    }

    /// <summary>每輪 / 每次檢查前整一個運行環境：前景（成個螢幕 + 真滑鼠鍵盤）或者背景（指定視窗）。</summary>
    private static RunCtx MakeCtx(Script s)
    {
        var (bg, proc, title) = OnUi(() => (s.Background, s.TargetProcess, s.TargetTitle));
        if (!bg) return new RunCtx(OwnWindow(), 0);
        var h = Win.Find(proc, title);
        if (h == 0) throw new InvalidOperationException(Lang.F("搵唔到背景程式視窗（{0} - {1}），佢係咪閂咗？", proc, title));
        return new RunCtx(null, h);
    }

    private static Rectangle? OwnWindow() => OnUi(() =>
    {
        var f = _ui!.FindForm();
        return f != null && f.Visible && f.WindowState != FormWindowState.Minimized ? f.Bounds : (Rectangle?)null;
    });

    // 而家行緊邊個腳本（跟住 async 流程走，IF 框嗰啲 Task 都會帶埋），用嚟攞運行速度
    private static readonly AsyncLocal<Script?> Current = new();

    /// <summary>按腳本嘅運行速度調整時間：2.0x = 時間減半；0.5x = 時間加倍。運行中改速度即時生效。</summary>
    private static TimeSpan Scaled(double seconds)
    {
        double speed = Math.Clamp(Current.Value?.Speed ?? 1.0, 0.5, 3.0);
        return TimeSpan.FromSeconds(Math.Max(0, seconds / speed));
    }

    private static double SpeedNow => Math.Clamp(Current.Value?.Speed ?? 1.0, 0.5, 3.0);

    private static async Task RunAsync(Script s, CancellationToken ct)
    {
        Current.Value = s;
        int round = 0;
        var held = new Held();
        // 每個 IF 框自己一條線
        var ifIds = OnUi(() => s.Ifs.Select(b => b.Id).ToList());
        var ifTasks = ifIds.Select(id => Task.Run(() => RunIfAsync(s, id, ct))).ToList();
        try
        {
            if (OnUi(() => s.Steps.Count) == 0)
            {
                // 冇主步驟：淨係行 IF 框，直到撳停止
                Report(s, Lang.T("監察緊 IF 條件…"));
                await Task.Delay(Timeout.Infinite, ct);
            }
            do
            {
                round++;
                // 每輪開頭喺 UI thread 抄一份步驟，運行中改都唔會撞
                var (steps, dir, loop) = OnUi(() => (s.Steps.Select(x => x.Clone()).ToList(), s.Dir, s.Loop));
                var exclude = MakeCtx(s);
                held.Ctx = exclude;
                for (int i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(s, Lang.F("{0}步驟 {1}/{2}：{3}", (loop ? Lang.F("第 {0} 輪 · ", round) : ""), i + 1, steps.Count, Step.KindName(steps[i].Kind)));
                    try { await RunStep(steps[i], dir, exclude, held, ct); }
                    catch (LoopBreak) { } // 唔喺循環入面嘅中斷：當冇事
                }
                if (!loop) break;
                await Task.Delay(50, ct); // 一直行時唔好食晒 CPU
            } while (!ct.IsCancellationRequested);
            Post(() => { if (IsRunning(s)) Stop(s, Lang.F("{0:HH:mm:ss} 完成", DateTime.Now)); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppPaths.LogError("運行腳本 " + s.Name, ex);
            Post(() => Stop(s, Lang.T("出錯：") + ex.Message));
        }
        finally
        {
            // 停咗就將仲撳住嘅鍵 / 掣彈返起，費事卡住
            try { held.ReleaseAll(); } catch { }
            try { await Task.WhenAll(ifTasks); } catch { }
        }
    }

    /// <summary>即刻檢查一次條件（IF 入面嘅 IF 用；倒數喺度永遠唔成立）。</summary>
    private static bool CheckNow(IfBlock b, string dir, RunCtx exclude, CancellationToken ct)
    {
        switch (b.Kind)
        {
            case CondKind.ImageFound:
            case CondKind.ImageNotFound:
            {
                var paths = ImageList.Split(b.Image).Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
                if (paths.Count == 0) return false;
                var hit = exclude.FindAny(paths, b.Similarity / 100.0, ct);
                if (b.Kind == CondKind.ImageFound && hit != null && b.ClickFound)
                    exclude.Click(hit.Value.X, hit.Value.Y, false);
                return b.Kind == CondKind.ImageFound ? hit != null : hit == null;
            }
            case CondKind.PixelColor:
            {
                var c = exclude.PixelAt(b.X, b.Y);
                int r = (b.Color >> 16) & 255, g = (b.Color >> 8) & 255, bl = b.Color & 255;
                return Math.Abs(c.R - r) <= b.Tolerance && Math.Abs(c.G - g) <= b.Tolerance && Math.Abs(c.B - bl) <= b.Tolerance;
            }
            case CondKind.Time:
                return TimeSpan.TryParse(b.Time, CultureInfo.InvariantCulture, out var ts) && DateTime.Now.TimeOfDay >= ts;
            default:
                return false;
        }
    }

    /// <summary>一個 IF 框：一直檢查條件，成立就行佢入面嘅步驟。</summary>
    private static async Task RunIfAsync(Script s, string ifId, CancellationToken ct)
    {
        Current.Value = s;
        var held = new Held();
        string key = s.Id + "/" + ifId;
        int fired = 0;
        DateTime countdownStart = DateTime.Now;
        DateTime? nextTime = null;
        string lastTimeText = "";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 每次都攞最新設定（運行中改都得）；個 IF 被刪咗就收工
                var b = OnUi(() => s.Ifs.FirstOrDefault(x => x.Id == ifId)?.Clone());
                if (b == null) return;
                var dir = OnUi(() => s.Dir);
                bool ok = false;
                switch (b.Kind)
                {
                    case CondKind.ImageFound:
                    case CondKind.ImageNotFound:
                    case CondKind.PixelColor:
                        ok = CheckNow(b, dir, MakeCtx(s), ct);
                        break;
                    case CondKind.Time:
                    {
                        if (b.Time != lastTimeText || nextTime == null)
                        {
                            lastTimeText = b.Time;
                            nextTime = TimeSpan.TryParse(b.Time, CultureInfo.InvariantCulture, out var ts) ? DateTime.Today + ts : null;
                            if (nextTime != null && nextTime <= DateTime.Now) nextTime = nextTime.Value.AddDays(1);
                        }
                        if (nextTime != null && DateTime.Now >= nextTime)
                        {
                            ok = true;
                            nextTime = nextTime.Value.AddDays(1);
                        }
                        else if (nextTime != null && fired == 0)
                            Report(key, Lang.F("等緊 {0:MM-dd HH:mm:ss}", nextTime));
                        break;
                    }
                    case CondKind.Countdown:
                    {
                        double left = ((double)b.Seconds - (DateTime.Now - countdownStart).TotalSeconds * SpeedNow) / SpeedNow; // 倒數都跟速度
                        if (left <= 0) ok = true;
                        else Report(key, Lang.F("倒數 {0} 秒", Math.Ceiling(left)) + (fired > 0 ? Lang.F("（已觸發 {0} 次）", fired) : ""));
                        break;
                    }
                }

                if (ok)
                {
                    fired++;
                    Report(key, Lang.F("{0:HH:mm:ss} 觸發第 {1} 次", DateTime.Now, fired));
                    var excl = MakeCtx(s);
                    held.Ctx = excl;
                    for (int i = 0; i < b.Steps.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { await RunStep(b.Steps[i], dir, excl, held, ct); }
                        catch (LoopBreak) { }
                    }
                    countdownStart = DateTime.Now;
                }
                double wait = b.UsesInterval ? Math.Max(0.1, (double)b.Interval) : 0.2;
                await Task.Delay(b.UsesInterval ? Scaled(wait) : TimeSpan.FromSeconds(wait), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppPaths.LogError("IF 條件", ex);
            Report(key, Lang.T("出錯：") + ex.Message);
        }
        finally
        {
            try { held.ReleaseAll(); } catch { }
        }
    }

    /// <summary>「中斷循環」：跳出最近嗰個循環。</summary>
    private sealed class LoopBreak : Exception { }

    private static async Task RunStep(Step st, string dir, RunCtx exclude, Held held, CancellationToken ct)
    {
        switch (st.Kind)
        {
            case StepKind.Key:
            {
                if (st.Vk == 0) break;
                var all = ModVks(st.Mods).Where(v => v != st.Vk).Append(st.Vk).ToArray();
                if (st.Mode != PressMode.Up)
                {
                    foreach (var vk in all) { exclude.KeyDown(vk, (st.Mods & Keys.Alt) != 0); held.Keys.Add(vk); Thread.Sleep(15); }
                }
                if (st.Mode == PressMode.Hold) await Task.Delay(Scaled((double)st.Hold), ct);
                if (st.Mode != PressMode.Down)
                {
                    foreach (var vk in all.Reverse()) { exclude.KeyUp(vk, (st.Mods & Keys.Alt) != 0); held.Keys.Remove(vk); Thread.Sleep(15); }
                }
                break;
            }
            case StepKind.Click:
            {
                var pt = exclude.FromScreen(st.X, st.Y);
                await Press(exclude, pt.X, pt.Y, st.Right, st.Mode, st.Hold, held, ct);
            }
                break;
            case StepKind.Wait:
                await Task.Delay(Scaled((double)st.Seconds), ct);
                break;
            case StepKind.If:
                // IF 入面嘅 IF：行到呢步就檢查一次，成立先行入面嘅步驟
                if (st.Sub != null && CheckNow(st.Sub, dir, exclude, ct))
                    foreach (var sub in st.Sub.Steps)
                    {
                        ct.ThrowIfCancellationRequested();
                        await RunStep(sub, dir, exclude, held, ct);
                    }
                break;
            case StepKind.Loop:
                // 循環：入面嘅步驟重複 Count 次（0 = 一直重複到停止）
                try
                {
                    for (int n = 0; st.Count == 0 || n < st.Count; n++)
                    {
                        foreach (var sub in st.Body)
                        {
                            ct.ThrowIfCancellationRequested();
                            await RunStep(sub, dir, exclude, held, ct);
                        }
                        if (st.Body.Count == 0) await Task.Delay(50, ct);
                    }
                }
                catch (LoopBreak) { } // 中斷循環：直接做循環之後嘅步驟
                break;
            case StepKind.Break:
                throw new LoopBreak();
            case StepKind.Image:
                var paths = ImageList.Split(st.Image).Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
                if (paths.Count == 0) break;
                var hit = exclude.FindAny(paths, st.Similarity / 100.0, ct);
                if (hit != null) exclude.Click(hit.Value.X, hit.Value.Y, false);
                break;
        }
    }

    private static async Task Press(RunCtx ctx, int x, int y, bool right, PressMode mode, decimal hold, Held held, CancellationToken ct)
    {
        held.LastMouse = new Point(x, y);
        if (mode != PressMode.Up)
        {
            ctx.MouseDown(x, y, right);
            if (right) held.Right = true; else held.Left = true;
        }
        if (mode == PressMode.Hold) await Task.Delay(Scaled((double)hold), ct);
        if (mode != PressMode.Down)
        {
            ctx.MouseUp(x, y, right, move: mode == PressMode.Up);
            if (right) held.Right = false; else held.Left = false;
        }
    }
}

/// <summary>截成個畫面搵一張圖，搵到就回傳中心點（螢幕座標）。</summary>
internal static class ScreenSearch
{
    public static Point? Find(string imagePath, double threshold, Rectangle? exclude, CancellationToken ct)
    {
        Gray tpl;
        using (var ms = new MemoryStream(File.ReadAllBytes(imagePath)))
        using (var img = Image.FromStream(ms))
        using (var b = new Bitmap(img))
            tpl = Gray.From(b);

        var vs = SystemInformation.VirtualScreen;
        Gray screen;
        using (var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            screen = Gray.From(bmp);
        }
        Rectangle? ex = exclude.HasValue
            ? new Rectangle(exclude.Value.X - vs.X, exclude.Value.Y - vs.Y, exclude.Value.Width, exclude.Value.Height)
            : null;
        var r = Matcher.Find(screen, tpl, ex, ct);
        if (r.Score < threshold) return null;
        return new Point(vs.X + r.X + r.W / 2, vs.Y + r.Y + r.H / 2);
    }

    /// <summary>截一次畫面，同幾張圖逐張配對，回傳最似嗰張（夠相似度先算）嘅中心點。</summary>
    public static Point? FindAny(List<string> imagePaths, double threshold, Rectangle? exclude, CancellationToken ct)
    {
        var vs = SystemInformation.VirtualScreen;
        Gray screen;
        using (var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            screen = Gray.From(bmp);
        }
        Rectangle? ex = exclude.HasValue
            ? new Rectangle(exclude.Value.X - vs.X, exclude.Value.Y - vs.Y, exclude.Value.Width, exclude.Value.Height)
            : null;
        Matcher.Result best = default;
        foreach (var path in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            Gray tpl;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms);
                using var b = new Bitmap(img);
                tpl = Gray.From(b);
            }
            catch { continue; }
            var r = Matcher.Find(screen, tpl, ex, ct);
            if (r.Score > best.Score) best = r;
        }
        if (best.Score < threshold || best.W == 0) return null;
        return new Point(vs.X + best.X + best.W / 2, vs.Y + best.Y + best.H / 2);
    }

    /// <summary>讀螢幕上某一點嘅顏色。</summary>
    public static Color PixelAt(int x, int y)
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
        return bmp.GetPixel(0, 0);
    }
}

// =====================================================================
//  介面
// =====================================================================

/// <summary>「我的腳本」頁：清單（一格格）↔ 編輯介面。</summary>
public sealed class ScriptsPage : Panel
{
    private readonly List<Script> _scripts = Script.LoadAll();
    private readonly CardStack _listView = new();
    private readonly ScriptGrid _grid;
    private Control? _editor;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 30 };
    private readonly Dictionary<string, bool> _wasDown = new();
    private readonly Dictionary<string, DateTime> _lastFire = new();
    private bool _handling;

    public static Control Build() => new ScriptsPage();

    private ScriptsPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        ScriptRunner.Init(this);

        _grid = new ScriptGrid(_scripts);
        _grid.OpenRequested += Open;
        _grid.PreviewRequested += Open;
        _grid.EditRequested += OpenEditor;
        _grid.NewRequested += () =>
        {
            var sc = new Script { Name = NextName() };
            sc.Save();
            _scripts.Add(sc);
            _grid.Refresh2();
            OpenEditor(sc);
        };
        _grid.DeleteRequested += sc =>
        {
            if (MessageBox.Show(FindForm(), Lang.F("刪除腳本「{0}」？", sc.Name), Lang.T("我的腳本"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;
            ScriptRunner.Stop(sc);
            try { Directory.Delete(sc.Dir, true); } catch (Exception ex) { AppPaths.LogError("刪除腳本", ex); }
            _scripts.Remove(sc);
            _grid.Refresh2();
        };
        _grid.CopyRequested += sc =>
        {
            var copy = new Script
            {
                Name = sc.Name + Lang.T(" (副本)"), Loop = sc.Loop, Steps = sc.Steps.Select(x => x.Clone()).ToList(),
                Background = sc.Background, TargetProcess = sc.TargetProcess, TargetTitle = sc.TargetTitle, Speed = sc.Speed,
                Ifs = sc.Ifs.Select(x => x.Clone()).ToList()
            };
            try
            {
                Directory.CreateDirectory(copy.Dir);
                foreach (var f in Directory.GetFiles(sc.Dir).Where(f => Path.GetFileName(f) != "script.txt"))
                    File.Copy(f, Path.Combine(copy.Dir, Path.GetFileName(f)), true);
            }
            catch (Exception ex) { AppPaths.LogError("複製腳本", ex); }
            copy.Save();
            _scripts.Add(copy);
            _grid.Refresh2();
        };

        var card = new CardPanel { Title = Lang.T("我的腳本"), Hint = Lang.T("撳「+」新增腳本；撳腳本睇預覽，再撳「修改」入去改；右鍵可以複製 / 刪除。入咗預覽頁先可以用快捷鍵開始 / 停止。") };
        card.SetContent(_grid);
        _listView.AddRow(card);
        _listView.Dock = DockStyle.Fill;
        Controls.Add(_listView);

        ScriptRunner.Changed += () => _grid.Invalidate();

        // 全部腳本嘅快捷鍵（頁未打開都有效）
        _poll.Tick += (_, _) =>
        {
            var form = FindForm();
            bool typing = form != null && Form.ActiveForm == form && form.ActiveControl is HotkeyBox;
            if (_handling) return; // 彈緊提示視窗（modal 期間 timer 照跳）：唔好再觸發
            foreach (var sc in _scripts.ToList())
            {
                // 快捷鍵只喺「入咗呢個腳本嘅預覽頁」先可以開始；行緊嘅腳本喺邊度都可以用快捷鍵停
                bool viewing = Visible && _editor is ScriptViewer v && v.Script == sc;
                bool usable = viewing || ScriptRunner.IsRunning(sc);
                bool down = usable && !typing && !sc.Hotkey.IsEmpty && sc.Hotkey.IsDown();
                _wasDown.TryGetValue(sc.Id, out bool was);
                _wasDown[sc.Id] = down; // 先記低，就算下面彈視窗都唔會當再撳一次
                if (!down || was || Keyboard.RecentlyInjected) continue;
                // 防連撳：同一個腳本 0.5 秒內只認一次
                var now = DateTime.UtcNow;
                if (_lastFire.TryGetValue(sc.Id, out var last) && (now - last).TotalMilliseconds < 500) continue;
                _lastFire[sc.Id] = now;
                _handling = true;
                try { ScriptRunner.Toggle(sc); }
                finally { _handling = false; }
            }
        };
        _poll.Start();
        HandleDestroyed += (_, _) => { _poll.Stop(); ScriptRunner.StopAll(); };
    }

    private string NextName()
    {
        for (int i = 1; ; i++)
        {
            string n = Lang.T("腳本 ") + i;
            if (_scripts.All(s => s.Name != n)) return n;
        }
    }

    /// <summary>換走目前顯示緊嘅（預覽 / 編輯）；null = 返去清單。</summary>
    private void ShowView(Control? view)
    {
        if (_editor != null)
        {
            Controls.Remove(_editor);
            var old = _editor;
            BeginInvoke(() => old.Dispose());
            _editor = null;
        }
        if (view == null)
        {
            _listView.Visible = true;
            _grid.Refresh2();
            return;
        }
        view.Dock = DockStyle.Fill;
        _editor = view;
        _listView.Visible = false;
        Controls.Add(view);
        view.BringToFront();
    }

    /// <summary>撳腳本：先睇預覽（唔可以改），上面有「修改」同「開始」。</summary>
    private void Open(Script sc)
    {
        var v = new ScriptViewer(sc);
        v.Back += () => ShowView(null);
        v.Edit += () => OpenEditor(sc);
        ShowView(v);
    }

    private void OpenEditor(Script sc)
    {
        var ed = new ScriptEditor(sc);
        ed.Back += () => Open(sc); // 改完返去預覽
        ed.Reload += () => OpenEditor(sc);
        ShowView(ed);
    }
}

/// <summary>腳本清單：一格一個腳本，最後一格「+」。</summary>
internal sealed class ScriptGrid : Control
{
    private readonly List<Script> _scripts;
    private readonly ContextMenuStrip _menu = new();
    private int _hover = -1, _menuIndex = -1;

    public event Action<Script>? OpenRequested, DeleteRequested, CopyRequested, PreviewRequested, EditRequested;
    public event Action? NewRequested;

    public ScriptGrid(List<Script> scripts)
    {
        _scripts = scripts;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        try { _menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _menu.BackColor = Ui.CardHi;
        _menu.ForeColor = Ui.Text;
        _menu.Items.Add(Lang.T("預覽"), null, (_, _) => { if (Valid(_menuIndex)) PreviewRequested?.Invoke(_scripts[_menuIndex]); });
        _menu.Items.Add(Lang.T("修改"), null, (_, _) => { if (Valid(_menuIndex)) EditRequested?.Invoke(_scripts[_menuIndex]); });
        _menu.Items.Add(Lang.T("複製"), null, (_, _) => { if (Valid(_menuIndex)) CopyRequested?.Invoke(_scripts[_menuIndex]); });
        _menu.Items.Add(Lang.T("刪除"), null, (_, _) => { if (Valid(_menuIndex)) DeleteRequested?.Invoke(_scripts[_menuIndex]); });
        ScriptRunner.Status += (_, _) => Invalidate();
    }

    private bool Valid(int i) => i >= 0 && i < _scripts.Count;

    private int TileW => Ui.S(168);
    private int TileH => Ui.S(84);
    private int Gap => Ui.S(12);
    private int Tiles => _scripts.Count + 1;
    private int Cols(int w) => Math.Max(1, (w + Gap) / (TileW + Gap));
    private int Rows(int w) => (Tiles + Cols(w) - 1) / Cols(w);

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        return new Size(w, Rows(w) * TileH + (Rows(w) - 1) * Gap);
    }

    private Rectangle TileRect(int i)
    {
        int c = Cols(Width);
        return new Rectangle(i % c * (TileW + Gap), i / c * (TileH + Gap), TileW - 1, TileH - 1);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < Tiles; i++) if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>數量變咗：重畫 + 叫成頁重新計高度。</summary>
    public void Refresh2()
    {
        Invalidate();
        for (Control? p = Parent; p != null; p = p.Parent)
            if (p is CardStack) { p.PerformLayout(); p.Invalidate(true); break; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        for (int i = 0; i < Tiles; i++)
        {
            var r = TileRect(i);
            bool hover = i == _hover;
            using var path = Ui.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), Ui.S(8f));
            using (var b = new SolidBrush(hover ? Ui.CardHi : Ui.Input)) g.FillPath(b, path);
            if (i == _scripts.Count)
            {
                using var pen = new Pen(hover ? Ui.Accent : Ui.BorderHi, Ui.S(1.2f)) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, path);
                var pf = Ui.F(18f);
                var lf = Ui.F(9f);
                int ph = TextRenderer.MeasureText("+", pf).Height, lh = lf.Height;
                int top = r.Y + (r.Height - ph - lh - Ui.S(2)) / 2;
                TextRenderer.DrawText(g, "+", pf, new Rectangle(r.X, top, r.Width, ph),
                    hover ? Ui.Accent : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Lang.T("新增腳本"), lf, new Rectangle(r.X, top + ph + Ui.S(2), r.Width, lh),
                    hover ? Ui.Accent : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                continue;
            }
            var sc = _scripts[i];
            bool run = ScriptRunner.IsRunning(sc);
            using (var pen = new Pen(run ? Ui.Success : hover ? Ui.Accent : Ui.Border, run ? Ui.S(1.5f) : 1f)) g.DrawPath(pen, path);
            int pad = Ui.S(12);
            TextRenderer.DrawText(g, sc.Name, Ui.F(10.5f, FontStyle.Bold), new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2 - Ui.S(14), Ui.S(22)),
                Ui.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string info = Lang.F("{0} 個步驟", sc.Steps.Count) + (sc.Ifs.Count > 0 ? Lang.F(" · {0} 個 IF", sc.Ifs.Count) : "") + " · " + (sc.Loop ? Lang.T("一直行") : Lang.T("行一次"));
            TextRenderer.DrawText(g, info, Ui.F(8.75f), new Point(r.X + pad, r.Y + pad + Ui.S(26)), Ui.TextDim, TextFormatFlags.NoPadding);
            string hk = sc.Hotkey.IsEmpty ? Lang.T("冇快捷鍵") : Lang.T("快捷鍵 ") + sc.Hotkey;
            TextRenderer.DrawText(g, run ? Lang.T("● 運行中") : hk, Ui.F(8.75f), new Point(r.X + pad, r.Bottom - pad - Ui.S(16)),
                run ? Ui.Success : Ui.TextFaint, TextFormatFlags.NoPadding);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = HitTest(e.Location);
        if (i < 0) return;
        if (i == _scripts.Count)
        {
            if (e.Button == MouseButtons.Left) NewRequested?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(_scripts[i]);
        else if (e.Button == MouseButtons.Right)
        {
            _menuIndex = i;
            _menu.Show(this, e.Location);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>編輯一個腳本：名、運行方式、快捷鍵、開始，同埋步驟清單。改咗即刻自動儲存。</summary>
internal sealed class ScriptEditor : Panel
{
    private readonly Script _sc;
    private FancyButton? _back;
    private readonly CardStack _stack = new();
    private readonly FancyButton _start;
    private readonly FancyButton _mode;
    private readonly StepList _steps;
    private readonly CardPanel _stepsCard;
    private readonly Action<string, string> _onStatus;
    private readonly Action _onChanged;

    public event Action? Back;
    /// <summary>AI 套用咗新腳本：要重新整個編輯器。</summary>
    public event Action? Reload;

    public ScriptEditor(Script sc)
    {
        _sc = sc;
        BackColor = Ui.Bg;
        _stack.Dock = DockStyle.Fill;

        // ---- 頂行：返回、名、運行方式、快捷鍵、開始 ----
        var back = new FancyButton { Text = Lang.T("← 返回"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
        back.Size = new Size(Ui.S(84), Ui.S(34));
        back.Click += (_, _) => { if (ScriptRunner.IsRunning(_sc)) return; Commit(); Back?.Invoke(); };
        _back = back;

        var nameBox = new TextBox { Text = sc.Name, MaxLength = 40 };
        var nameHost = new InputHost(nameBox, Ui.S(200));
        nameBox.Leave += (_, _) => Commit(); // 改完名離開格仔就順手改資料夾名
        nameBox.TextChanged += (_, _) =>
        {
            _sc.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Lang.T("未命名") : nameBox.Text.Trim();
            _sc.Save();
        };

        _mode = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(96), Ui.S(34)) };
        _mode.Click += (_, _) => { _sc.Loop = !_sc.Loop; _sc.Save(); UpdateButtons(); };

        var hkLabel = Ui.Text_(Lang.T("快捷鍵"), Ui.TextDim, Ui.F(9.75f), new Padding(0));
        var hkBox = new HotkeyBox { Hotkey = sc.Hotkey };
        hkBox.Changed += () => { _sc.Hotkey = hkBox.Hotkey; _sc.Save(); };
        var hkHost = new InputHost(hkBox, Ui.S(130));

        _start = new FancyButton { Kind = BtnKind.Success, Size = new Size(Ui.S(96), Ui.S(34)) };
        _start.Click += (_, _) => { Commit(); ScriptRunner.Toggle(_sc); };

        var top = new BareRow();
        top.SetContent(new FlowRow(new Control[] { back, nameHost, _mode }, new Control[] { _start, hkHost, hkLabel }));
        top.Padding = new Padding(0);

        // ---- 步驟 ----
        _steps = new StepList(sc, sc.Steps, AddIf, nestedIf: true);
        _stepsCard = new CardPanel { Title = Lang.T("主線程"), Hint = Lang.T("由上至下逐步做；「循環」可以將幾步重複 N 次；IF 框同主線程同時行。「一直行」全部一直 loop；「行一次」主線程完咗 IF 都停。") };
        _stepsCard.SetContent(_steps);

        _stack.AddRow(top);
        _stack.AddRow(_stepsCard);
        foreach (var b in sc.Ifs) AddIfCard(b);
        Controls.Add(_stack);

        _onStatus = (id, text) =>
        {
            if (id == _sc.Id) _steps.SetStatus(text);
            else if (id.StartsWith(_sc.Id + "/") && _ifPanels.TryGetValue(id[(_sc.Id.Length + 1)..], out var ip)) ip.SetStatus(text);
        };
        _onChanged = UpdateButtons;
        ScriptRunner.Status += _onStatus;
        ScriptRunner.Changed += _onChanged;
        UpdateButtons();

        // 右下角「雲」：揀咗 AI 而且登入咗先會出現
        _ = new MultiTool.Ai.ScriptAssist(this, sc, () => BeginInvoke(() => Reload?.Invoke()));
    }

    private void Commit()
    {
        if (!ScriptRunner.IsRunning(_sc)) _sc.SyncFolder();
        _sc.Save();
    }

    private readonly Dictionary<string, IfPanel> _ifPanels = new();

    private void AddIf()
    {
        var b = new IfBlock { Name = NextIfName() };
        _sc.Ifs.Add(b);
        _sc.Save();
        AddIfCard(b);
        if (ScriptRunner.IsRunning(_sc)) _steps.SetStatus(Lang.T("新加嘅 IF 框要停咗再開始先會行"));
    }

    private string NextIfName()
    {
        for (int i = 1; ; i++)
        {
            string n = Lang.T("IF 條件 ") + i;
            if (_sc.Ifs.All(x => x.Name != n)) return n;
        }
    }

    private void AddIfCard(IfBlock b)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) b.Name = NextIfName();
        var card = new CardPanel();
        var panel = new IfPanel(_sc, b);
        card.SetContent(panel);
        _ifPanels[b.Id] = panel;
        panel.DeleteRequested += () =>
        {
            if (MessageBox.Show(FindForm(), Lang.T("刪除呢個 IF 框同入面嘅步驟？"), Lang.T("我的腳本"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;
            var images = new[] { b.Image }.Concat(b.Steps.Select(x => x.Image)).SelectMany(ImageList.Split).Distinct().ToList();
            _sc.Ifs.Remove(b);
            _ifPanels.Remove(b.Id);
            foreach (var img in images)
                if (!_sc.UsesImage(img)) try { File.Delete(Path.Combine(_sc.Dir, img)); } catch { }
            _sc.Save();
            BeginInvoke(() => _stack.RemoveRow(card));
        };
        _stack.AddRow(card);
    }

    private void UpdateButtons()
    {
        bool run = ScriptRunner.IsRunning(_sc);
        _start.Text = run ? Lang.T("停止") : Lang.T("開始");
        _start.Kind = run ? BtnKind.Danger : BtnKind.Success;
        _mode.Text = _sc.Loop ? Lang.T("一直行") : Lang.T("行一次");
        if (_back != null) _back.Enabled = !run;
        _start.Invalidate();
        _mode.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ScriptRunner.Status -= _onStatus;
            ScriptRunner.Changed -= _onChanged;
        }
        base.Dispose(disposing);
    }
}

/// <summary>一行：左邊一組靠左，右邊一組靠右（右邊組由右至左排）。</summary>
internal sealed class FlowRow : Panel
{
    private readonly Control[] _left, _right;

    public FlowRow(Control[] left, Control[] rightToLeft)
    {
        _left = left;
        _right = rightToLeft;
        BackColor = Ui.Bg;
        foreach (var c in _left.Concat(_right)) Controls.Add(c);
    }

    private int H => _left.Concat(_right).Max(c => c.Height);

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width, H);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_left == null || _right == null) return;
        int h = H, x = 0;
        foreach (var c in _left)
        {
            var sz = c is Label ? c.PreferredSize : c.Size;
            c.Location = new Point(x, (h - sz.Height) / 2);
            x += sz.Width + Ui.S(10);
        }
        x = ClientSize.Width;
        bool first = true;
        for (int i = 0; i < _right.Length; i++)
        {
            var c = _right[i];
            if (c.Tag as string == "hidden") continue;
            var sz = c is Label ? c.PreferredSize : c.Size;
            x -= sz.Width;
            c.Location = new Point(x, (h - sz.Height) / 2);
            x -= first ? Ui.S(16) : Ui.S(10);
            first = false;
        }
    }
}

/// <summary>步驟清單：每行一個步驟（類型、參數、↑ ↓ ✕），下面一排「+ 新增」掣。</summary>
internal sealed class StepList : Panel
{
    private readonly Script _sc;
    private readonly List<Step> _steps;
    private readonly bool _inLoop;
    private readonly List<Control[]> _rowControls = new();
    private readonly List<FancyButton> _addButtons = new();
    private readonly Label _status;
    private readonly Label _empty;
    private readonly ToolTip _tip = new();
    private bool _ready;

    private static readonly List<KeyList.Item> KeyItems = KeyList.Build();

    /// <param name="steps">要編輯嘅步驟清單（主步驟或者某個 IF 框嘅步驟）。</param>
    /// <param name="addIf">有就喺新增掣度加多個「+ IF 條件」（主線程用：開一個同時行嘅 IF 框）。</param>
    /// <param name="nestedIf">true = 「+ IF 條件」會喺呢個清單入面加一個 IF 步驟（IF 入面嘅 IF）。</param>
    /// <param name="inLoop">呢個清單喺循環入面（包括循環入面嘅 IF）：多個「+ 中斷循環」。</param>
    public StepList(Script sc, List<Step> steps, Action? addIf = null, bool nestedIf = false, bool inLoop = false)
    {
        _inLoop = inLoop;
        Action? parallelIf = addIf;
        _sc = sc;
        _steps = steps;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        foreach (var kind in new[] { StepKind.Key, StepKind.Click, StepKind.Wait, StepKind.Image, StepKind.Loop })
        {
            var b = new FancyButton { Text = "+ " + Step.KindName(kind), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
            b.Size = new Size(Ui.S(116), Ui.S(34));
            b.Click += (_, _) => AddStep(kind);
            _addButtons.Add(b);
            Controls.Add(b);
        }
        if (inLoop)
        {
            var b = new FancyButton { Text = Lang.T("+ 中斷循環"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
            b.Size = new Size(Ui.S(116), Ui.S(34));
            b.Click += (_, _) => AddStep(StepKind.Break);
            _addButtons.Add(b);
            Controls.Add(b);
        }
        if (nestedIf)
        {
            // IF 判斷：行到呢步檢查一次
            var b = new FancyButton { Text = Lang.T("+ IF 條件"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f, FontStyle.Bold) };
            b.Size = new Size(Ui.S(116), Ui.S(34));
            _tip.SetToolTip(b, Lang.T("行到呢步就檢查一次，成立先做入面嘅步驟"));
            b.Click += (_, _) => AddStep(StepKind.If);
            _addButtons.Add(b);
            Controls.Add(b);
        }
        if (parallelIf != null)
        {
            // IF 框：同主線程同時行，一直監察
            var b = new FancyButton { Text = Lang.T("+ IF 框（同時行）"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f, FontStyle.Bold) };
            b.Size = new Size(Ui.S(150), Ui.S(34));
            _tip.SetToolTip(b, Lang.T("開一個同主線程同時行嘅 IF 框，一直監察條件"));
            b.Click += (_, _) => parallelIf();
            _addButtons.Add(b);
            Controls.Add(b);
        }
        _status = Ui.Text_("", Ui.Text, Ui.F(9f), new Padding(0));
        _empty = Ui.Text_(Lang.T("未有步驟，撳下面嘅掣加。"), Ui.TextFaint, Ui.F(9.5f), new Padding(0));
        Controls.Add(_status);
        Controls.Add(_empty);
        Rebuild();
        _ready = true;
        Relayout();
    }

    public void SetStatus(string text) => _status.Text = text;

    private int RowH => Ui.S(34);
    private int Gap => Ui.S(8);

    private int BlockIndent => Ui.S(28);

    /// <summary>每行高度：IF 步驟 = 標題行 + 下面成個 IF 框。</summary>
    private int RowHeight(int i, int width)
    {
        if (i >= _rowControls.Count) return RowH;
        var block = _rowControls[i].FirstOrDefault(c => c is IfPanel or LoopBlock);
        if (block == null) return RowH;
        return RowH + Ui.S(6) + block.GetPreferredSize(new Size(Math.Max(50, width - BlockIndent), 0)).Height;
    }

    private int RowsH(int width)
    {
        if (_steps.Count == 0) return Ui.S(24);
        int h = 0;
        for (int i = 0; i < _steps.Count; i++) h += RowHeight(i, width) + (i > 0 ? Gap : 0);
        return h;
    }

    /// <summary>新增掣：太窄就自動換行。</summary>
    private int PlaceButtons(int width, int y, bool apply)
    {
        int bx = 0, by = y;
        foreach (var b in _addButtons)
        {
            if (bx > 0 && bx + b.Width > width) { bx = 0; by += Ui.S(34) + Ui.S(8); }
            if (apply) b.Location = new Point(bx, by);
            bx += b.Width + Ui.S(10);
        }
        return by + Ui.S(34);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        int y = RowsH(w) + Gap + Ui.S(8);
        int end = PlaceButtons(w, y, false);
        return new Size(w, end + Ui.S(12) + Ui.S(20));
    }

    private void Changed(bool structure)
    {
        _sc.Save();
        if (structure)
        {
            Rebuild();
            Relayout();
        }
    }

    private void AddStep(StepKind kind)
    {
        var st = new Step { Kind = kind };
        if (kind == StepKind.If) st.Sub = new IfBlock { Name = Lang.T("IF 條件") };
        if (kind == StepKind.Click)
        {
            var p = Cursor.Position;
            st.X = p.X;
            st.Y = p.Y;
        }
        _steps.Add(st);
        Changed(true);
    }

    private static IEnumerable<string> AllImages(Step st)
    {
        foreach (var n in ImageList.Split(st.Image)) yield return n;
        if (st.Sub != null)
        {
            foreach (var n in ImageList.Split(st.Sub.Image)) yield return n;
            foreach (var n in st.Sub.Steps.SelectMany(AllImages)) yield return n;
        }
        foreach (var n in st.Body.SelectMany(AllImages)) yield return n;
    }

    private void MoveStep(int i, int d)
    {
        int j = i + d;
        if (j < 0 || j >= _steps.Count) return;
        (_steps[i], _steps[j]) = (_steps[j], _steps[i]);
        Changed(true);
    }

    private void Delete(int i)
    {
        var st = _steps[i];
        _steps.RemoveAt(i);
        // 刪埋冇其他地方用緊嘅圖（包括 IF / 循環入面嘅）
        foreach (var name in AllImages(st).Distinct())
            if (!_sc.UsesImage(name)) try { File.Delete(Path.Combine(_sc.Dir, name)); } catch { }
        Changed(true);
    }

    /// <summary>重新整晒每行嘅控件。</summary>
    private void Rebuild()
    {
        SuspendLayout();
        foreach (var row in _rowControls)
            foreach (var c in row) { Controls.Remove(c); c.Dispose(); }
        _rowControls.Clear();
        for (int i = 0; i < _steps.Count; i++)
        {
            var row = BuildRow(i, _steps[i]);
            _rowControls.Add(row);
            foreach (var c in row) Controls.Add(c);
        }
        _empty.Visible = _steps.Count == 0;
        ResumeLayout(false);
    }

    private Label L(string text, Color? c = null) => Ui.Text_(text, c ?? Ui.TextDim, Ui.F(9.75f), new Padding(0));

    private Control[] BuildRow(int i, Step st)
    {
        var list = new List<Control>
        {
            Ui.Text_((i + 1) + ".", Ui.TextFaint, Ui.F(9.75f, FontStyle.Bold), new Padding(0)),
            new KindBadge(Step.KindName(st.Kind))
        };
        switch (st.Kind)
        {
            case StepKind.Key:
            {
                // 一格搞掂：撳一下，再撳鍵（可以組合鍵，例如 Shift + Alt + T）
                var keyBox = new KeyCaptureBox(Ui.S(190)) { Vk = st.Vk, Mods = st.Mods };
                _tip.SetToolTip(keyBox, Lang.T("撳一下，再撳要按嘅鍵或者組合鍵（例如 Shift + Alt + T）。右鍵可以清除。"));
                keyBox.Changed += () => { st.Vk = keyBox.Vk; st.Mods = keyBox.Mods; Changed(false); };
                list.Add(keyBox);
                AddModeControls(list, st);
                break;
            }
            case StepKind.Click:
            {
                var btn = new FancyButton { Text = st.Right ? Lang.T("右鍵") : Lang.T("左鍵"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(58), Ui.S(34)) };
                btn.Click += (_, _) => { st.Right = !st.Right; btn.Text = st.Right ? Lang.T("右鍵") : Lang.T("左鍵"); Changed(false); };
                var x = new NumberBox(Ui.S(82)) { Minimum = -10000, Maximum = 20000 };
                x.Value = st.X;
                x.ValueChanged += (_, _) => { st.X = (int)x.Value; Changed(false); };
                var y = new NumberBox(Ui.S(82)) { Minimum = -10000, Maximum = 20000 };
                y.Value = st.Y;
                y.ValueChanged += (_, _) => { st.Y = (int)y.Value; Changed(false); };
                var pickPos = new FancyButton { Text = Lang.T("座標"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(58), Ui.S(34)) };
                _tip.SetToolTip(pickPos, Lang.T("撳完 3 秒內將滑鼠移去目標位置"));
                pickPos.Click += (_, _) => PickPosition(pickPos, x, y);
                list.AddRange(new Control[] { btn, L("X"), x, L("Y"), y, pickPos });
                AddModeControls(list, st);
                break;
            }
            case StepKind.Loop:
            {
                var count = new NumberBox(Ui.S(90)) { Minimum = 0, Maximum = 99999, Increment = 1 };
                count.Value = st.Count;
                count.ValueChanged += (_, _) => { st.Count = (int)count.Value; Changed(false); };
                _tip.SetToolTip(count, Lang.T("0 = 一直重複，直到撳停止"));
                list.AddRange(new Control[] { L(Lang.T("重複")), count, L(Lang.T("次（0 = 一直重複）"), Ui.TextFaint), new LoopBlock(_sc, st.Body) });
                break;
            }
            case StepKind.Break:
            {
                list.Add(L(Lang.T("跳出最近嗰個循環，做循環之後嘅步驟"), Ui.TextFaint));
                break;
            }
            case StepKind.If:
            {
                st.Sub ??= new IfBlock { Name = Lang.T("IF 條件") };
                var panel = new IfPanel(_sc, st.Sub, nested: true, inLoop: _inLoop);
                list.Add(panel);
                break;
            }
            case StepKind.Wait:
            {
                // 介面用 ms，內部照存秒（舊腳本唔使轉換）
                var ms = new NumberBox(Ui.S(110)) { Minimum = 1m, Maximum = 3_600_000m, DecimalPlaces = 0, Increment = 100m };
                ms.Value = Math.Round(st.Seconds * 1000m);
                ms.ValueChanged += (_, _) => { st.Seconds = ms.Value / 1000m; Changed(false); };
                list.AddRange(new Control[] { ms, L("ms") });
                break;
            }
            case StepKind.Image:
            {
                var tile = new ImageStrip(_sc, () => st.Image, v => { st.Image = v; Changed(false); });
                var sim = new NumberBox(Ui.S(90)) { Minimum = 10, Maximum = 100, Increment = 1 };
                sim.Value = st.Similarity;
                sim.ValueChanged += (_, _) => { st.Similarity = (int)sim.Value; Changed(false); };
                list.AddRange(new Control[] { tile, L(Lang.T("相似度")), sim, L("%"), L(Lang.T("搵唔到就跳過"), Ui.TextFaint) });
                break;
            }
        }
        var up = SmallButton("↑", Lang.T("向上移"), () => MoveStep(i, -1));
        var down = SmallButton("↓", Lang.T("向下移"), () => MoveStep(i, 1));
        var del = SmallButton("✕", Lang.T("刪除呢個步驟"), () => Delete(i));
        up.Enabled = i > 0;
        down.Enabled = i < _steps.Count - 1;
        list.Add(del);
        list.Add(down);
        list.Add(up);
        return list.ToArray();
    }

    /// <summary>「撳住 / 按下 / 彈起」掣；撳住先會出秒數。</summary>
    private void AddModeControls(List<Control> list, Step st)
    {
        var mode = new FancyButton { Text = Step.ModeName(st.Mode), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(58), Ui.S(34)) };
        _tip.SetToolTip(mode, Lang.T("撳住：按下 → 等 → 彈起\n按下：淨係按落去（之後用「彈起」放開）\n彈起：淨係放開"));
        mode.Click += (_, _) => BeginInvoke(() =>
        {
            st.Mode = st.Mode switch { PressMode.Hold => PressMode.Down, PressMode.Down => PressMode.Up, _ => PressMode.Hold };
            Changed(true);
        });
        list.Add(mode);
        if (st.Mode == PressMode.Hold)
        {
            var hold = new NumberBox(Ui.S(90)) { Minimum = 10m, Maximum = 60_000m, DecimalPlaces = 0, Increment = 50m };
            hold.Value = Math.Round(st.Hold * 1000m);
            hold.ValueChanged += (_, _) => { st.Hold = hold.Value / 1000m; Changed(false); };
            list.Add(hold);
            list.Add(L("ms"));
        }
    }

    private FancyButton SmallButton(string text, string tip, Action click)
    {
        var b = new FancyButton { Text = text, Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(30), Ui.S(34)) };
        _tip.SetToolTip(b, tip);
        b.Click += (_, _) => BeginInvoke(click); // 等個 click 完先重建，唔好喺自己嘅事件入面 Dispose 自己
        return b;
    }

    private void PickPosition(FancyButton btn, NumberBox x, NumberBox y)
    {
        int left = 3;
        btn.Enabled = false;
        btn.Text = left + "…";
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            left--;
            if (left > 0)
            {
                btn.Text = left + "…";
                return;
            }
            t.Stop();
            t.Dispose();
            var p = Cursor.Position;
            x.Value = p.X;
            y.Value = p.Y;
            btn.Text = Lang.T("座標");
            btn.Enabled = true;
        };
        t.Start();
    }

    private void Relayout() => RelayoutFrom(this);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_ready) return;
        int y = 0, w = ClientSize.Width;
        if (_steps.Count == 0) _empty.Location = new Point(0, Ui.S(2));
        for (int i = 0; i < _rowControls.Count; i++)
        {
            var row = _rowControls[i];
            // 左邊：編號、類型、參數；最後三個（✕ ↓ ↑）靠右；IF 步驟嘅 IF 框喺下面成行
            int x = 0;
            int nRight = 3;
            for (int k = 0; k < row.Length - nRight; k++)
            {
                var c = row[k];
                if (c is IfPanel or LoopBlock)
                {
                    var block = c;
                    int bw = Math.Max(50, w - BlockIndent);
                    var bs = block.GetPreferredSize(new Size(bw, 0));
                    block.SetBounds(BlockIndent, y + RowH + Ui.S(6), bw, bs.Height);
                    continue;
                }
                var sz = c is Label ? c.PreferredSize : c.Size;
                if (k == 0) sz = new Size(Ui.S(28), sz.Height);
                c.Location = new Point(x, y + (RowH - sz.Height) / 2);
                x += sz.Width + (k == 0 ? Ui.S(2) : c is ToggleChip ? Ui.S(4) : Ui.S(8));
            }
            int rx = w;
            for (int k = row.Length - nRight; k < row.Length; k++)
            {
                var c = row[k];
                rx -= c.Width;
                c.Location = new Point(rx, y);
                rx -= Ui.S(4);
            }
            y += RowHeight(i, w) + Gap;
        }
        if (_steps.Count == 0) y = Ui.S(24) + Gap;
        int end = PlaceButtons(w, y + Ui.S(8), true);
        _status.Location = new Point(0, end + Ui.S(12));
    }

    /// <summary>由某個控件開始，叫成頁（連入面一層層嘅 IF）重新排位。</summary>
    internal static void RelayoutFrom(Control from)
    {
        from.PerformLayout();
        for (Control? p = from.Parent; p != null; p = p.Parent)
        {
            if (p is CardStack)
            {
                p.PerformLayout();
                Deep(p);
                p.Invalidate(true);
                return;
            }
        }

        static void Deep(Control c)
        {
            foreach (Control ch in c.Controls)
            {
                if (ch is StepList or IfPanel or LoopBlock or CardPanel)
                {
                    ch.PerformLayout();
                    Deep(ch);
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>步驟類型嘅小標籤。</summary>
internal sealed class KindBadge : Control
{
    public KindBadge(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = Ui.F(9f, FontStyle.Bold);
        Size = new Size(Ui.S(70), Ui.S(26));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.Round(r, Height / 2f);
        using (var b = new SolidBrush(Ui.Alpha(Ui.Accent, 40))) g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Ui.Hex(0x9DBBFF),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>搵圖步驟嘅圖片格：撳一下揀圖，或者拖圖入嚟。</summary>
internal sealed class ImageTile : Control
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private Bitmap? _thumb;
    private bool _hover;

    public event Action<string>? Picked;

    public ImageTile(string path, bool has)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(Ui.S(64), Ui.S(34));
        Cursor = Cursors.Hand;
        AllowDrop = true;
        if (has) SetImage(path);
    }

    public void SetImage(string path)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var img = Image.FromStream(ms);
            var old = _thumb;
            _thumb = new Bitmap(img, Math.Max(1, img.Width * Ui.S(30) / Math.Max(1, img.Height)), Ui.S(30));
            old?.Dispose();
        }
        catch { _thumb = null; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.Round(r, Ui.S(6f));
        using (var b = new SolidBrush(_hover ? Ui.CardHi : Ui.Input)) g.FillPath(b, path);
        if (_thumb != null)
        {
            var st = g.Save();
            g.SetClip(path);
            float s = Math.Min((Width - 4f) / _thumb.Width, (Height - 4f) / _thumb.Height);
            float w = _thumb.Width * s, h = _thumb.Height * s;
            g.DrawImage(_thumb, (Width - w) / 2, (Height - h) / 2, w, h);
            g.Restore(st);
            using var pen = new Pen(_hover ? Ui.Accent : Ui.Border, 1f);
            g.DrawPath(pen, path);
        }
        else
        {
            using var pen = new Pen(_hover ? Ui.Accent : Ui.BorderHi, Ui.S(1.2f)) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, Lang.T("截圖"), Ui.F(9f), Rectangle.Round(r), _hover ? Ui.Accent : Ui.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    private ContextMenuStrip? _menu;

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) Snip();
        else if (e.Button == MouseButtons.Right)
        {
            if (_menu == null)
            {
                _menu = new ContextMenuStrip();
                try { _menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
                _menu.BackColor = Ui.CardHi;
                _menu.ForeColor = Ui.Text;
                _menu.Items.Add(Lang.T("截圖框圖"), null, (_, _) => Snip());
                _menu.Items.Add(Lang.T("從檔案揀…"), null, (_, _) => PickFile());
            }
            _menu.Show(this, e.Location);
        }
    }

    /// <summary>入截圖模式，畫個框框住想要嘅圖。</summary>
    private void Snip()
    {
        using var bmp = SnipOverlay.Grab(FindForm());
        if (bmp == null) return;
        string tmp = Path.Combine(Path.GetTempPath(), "multitool_snip_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            bmp.Save(tmp, ImageFormat.Png);
            Picked?.Invoke(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog { Title = Lang.T("揀圖片"), Filter = Lang.T("圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*") };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Picked?.Invoke(dlg.FileName);
    }

    private static string? Dropped(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = Dropped(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var f = Dropped(e);
        if (f != null) Picked?.Invoke(f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumb?.Dispose();
            _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>細細粒嘅開關（組合鍵 Ctrl / Shift / Alt 用）。</summary>
internal sealed class ToggleChip : Control
{
    private bool _checked, _hover;

    public event Action? CheckedChanged;

    public ToggleChip(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = Ui.F(8.75f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + Ui.S(14), Ui.S(28));
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.Round(r, Ui.S(6f));
        using (var b = new SolidBrush(_checked ? Ui.Alpha(Ui.Accent, 70) : _hover ? Ui.CardHi : Ui.Input)) g.FillPath(b, path);
        using (var pen = new Pen(_checked ? Ui.Accent : _hover ? Ui.BorderHi : Ui.Border, 1f)) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), _checked ? Color.White : Ui.TextFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 按鍵格：撳一下，然後撳鍵。Shift + Alt + T 會記低成個組合；淨係撳 T 就淨係 T；
/// 淨係撳一個修飾鍵（例如 Shift）再放手，就當係按 Shift。右鍵清除。
/// </summary>
internal sealed class KeyCaptureBox : Control
{
    private int _vk;
    private Keys _mods;
    private bool _hover, _mainPressed;
    private Keys _pendingMods;
    private string? _preview;
    private readonly ContextMenuStrip _menu = new();

    public event Action? Changed;

    public KeyCaptureBox(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(width, Ui.S(34));
        Font = Ui.F(9.75f);
        Cursor = Cursors.Hand;
        try { _menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _menu.BackColor = Ui.CardHi;
        _menu.ForeColor = Ui.Text;
        _menu.Items.Add(Lang.T("清除"), null, (_, _) => Set(0, Keys.None));
    }

    public int Vk { get => _vk; set { _vk = value; Invalidate(); } }
    public Keys Mods { get => _mods; set { _mods = value & (Keys.Control | Keys.Shift | Keys.Alt); Invalidate(); } }

    public static string Format(Keys mods, int vk)
    {
        if (vk == 0) return Lang.T("（無）");
        var parts = new List<string>();
        if (mods.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Keys.Alt)) parts.Add("Alt");
        parts.Add(Hotkey.KeyName(vk));
        return string.Join(" + ", parts);
    }

    private static string ModsText(Keys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Keys.Alt)) parts.Add("Alt");
        return string.Join(" + ", parts);
    }

    private void Set(int vk, Keys mods)
    {
        _vk = vk;
        _mods = mods & (Keys.Control | Keys.Shift | Keys.Alt);
        _preview = null;
        Invalidate();
        Changed?.Invoke();
        // 設定完離開格仔，之後再撳鍵唔會改到
        var f = FindForm();
        if (f != null && Focused) f.ActiveControl = null;
    }

    private static bool IsModifier(Keys code) =>
        code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu;

    private static (int Vk, Keys Flag) ModifierOf(Keys code) => code switch
    {
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => (0x11, Keys.Control),
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => (0x10, Keys.Shift),
        _ => (0x12, Keys.Alt)
    };

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        if (!Focused || (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN)) return base.ProcessCmdKey(ref msg, keyData);
        var code = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (IsModifier(code))
        {
            // 撳緊修飾鍵：顯示「Shift + Alt + …」，等主鍵
            _pendingMods = mods | ModifierOf(code).Flag;
            _preview = ModsText(_pendingMods) + " + …";
            Invalidate();
            return true;
        }
        _mainPressed = true;
        Set((int)code, mods);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_CHAR = 0x0102;
        if (Focused && (m.Msg == WM_SYSCHAR || m.Msg == WM_CHAR)) return; // 食咗，唔好觸發選單 / 響聲
        if (Focused && (m.Msg == WM_KEYUP || m.Msg == WM_SYSKEYUP))
        {
            var code = (Keys)(int)m.WParam;
            if (IsModifier(code) && !_mainPressed && _pendingMods != Keys.None)
            {
                // 淨係撳修飾鍵再放手：主鍵就係呢個修飾鍵，其他仲撳住嘅當組合
                var (vk, flag) = ModifierOf(code);
                Set(vk, _pendingMods & ~flag);
            }
            return; // 唔交俾系統（Alt 彈起會開視窗選單）
        }
        base.WndProc(ref m);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _mainPressed = false;
        _pendingMods = Keys.None;
        _preview = Lang.T("請撳鍵…");
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _preview = null;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Focus();
        else if (e.Button == MouseButtons.Right) _menu.Show(this, e.Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Field.Paint(this, g, Focused, _hover);
        string text = _preview ?? Format(_mods, _vk);
        var color = _preview != null ? Ui.Accent : _vk == 0 ? Ui.TextFaint : Ui.Text;
        TextRenderer.DrawText(g, text, Font, new Rectangle(Ui.S(10), 0, Width - Ui.S(20), Height), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>一個 IF 框嘅內容：條件設定 + 觸發後要行嘅步驟。</summary>
internal sealed class IfPanel : TintPanel
{
    private readonly Script _sc;
    private readonly IfBlock _b;
    private readonly List<Control> _cond = new();
    private readonly List<Control> _opts = new();
    private readonly StepList _steps;
    private readonly FancyButton _delete;
    private readonly FancyButton _fold;
    private readonly InputHost _nameHost;
    private readonly Label _summary;
    private string _lastStatus = "";
    private readonly ToolTip _tip = new();
    private readonly ContextMenuStrip _kindMenu = new();
    private bool _ready;

    public event Action? DeleteRequested;

    private readonly bool _nested;

    /// <param name="nested">true = IF 入面嘅 IF（行到嗰步先檢查一次，冇「每幾秒檢查」同倒數）。</param>
    public IfPanel(Script sc, IfBlock b, bool nested = false, bool inLoop = false) : base(Ui.Accent, 0.11f)
    {
        _sc = sc;
        _b = b;
        _nested = nested;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;

        try { _kindMenu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _kindMenu.BackColor = Ui.CardHi;
        _kindMenu.ForeColor = Ui.Text;
        foreach (CondKind k in Enum.GetValues(typeof(CondKind)))
        {
            if (nested && k == CondKind.Countdown) continue;
            var kind = k;
            _kindMenu.Items.Add(IfBlock.KindName(k), null, (_, _) =>
            {
                if (_b.Kind == kind) return;
                _b.Kind = kind;
                Save();
                BeginInvoke(RebuildCond);
            });
        }

        _delete = new FancyButton { Text = Lang.T("刪除 IF"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(78), Ui.S(34)) };
        _delete.Click += (_, _) => DeleteRequested?.Invoke();
        Controls.Add(_delete);
        _delete.Visible = !nested; // IF 步驟用嗰行嘅 ✕ 刪

        // 標題列：摺埋掣 + 可改嘅名 + 摘要
        _fold = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(34), Ui.S(34)) };
        _tip.SetToolTip(_fold, Lang.T("摺埋 / 打開"));
        _fold.Click += (_, _) => { _b.Collapsed = !_b.Collapsed; Save(); ApplyFold(); };
        var nameBox = new TextBox { Text = b.Name, MaxLength = 40 };
        _nameHost = new InputHost(nameBox, Ui.S(220));
        nameBox.Font = Ui.F(10.5f, FontStyle.Bold);
        _tip.SetToolTip(nameBox, Lang.T("撳入去改名"));
        nameBox.TextChanged += (_, _) =>
        {
            _b.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Lang.T("IF 條件") : nameBox.Text.Trim();
            Save();
        };
        _summary = Ui.Text_("", Ui.TextFaint, Ui.F(9f), new Padding(0));
        _summary.AutoSize = false;
        _summary.AutoEllipsis = true;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_fold);
        Controls.Add(_nameHost);
        Controls.Add(_summary);

        _steps = new StepList(sc, b.Steps, nestedIf: true, inLoop: inLoop);
        _steps.Dock = DockStyle.None;
        Controls.Add(_steps);

        RebuildCond();
        _ready = true;
        ApplyFold();
    }

    public void SetStatus(string text)
    {
        _lastStatus = text;
        _steps.SetStatus(text);
        if (_b.Collapsed) UpdateSummary();
    }

    /// <summary>摺埋時喺標題旁邊顯示：條件 · 步驟數 · 最新狀態。</summary>
    private void UpdateSummary()
    {
        string t = Lang.F("如果{0} · {1} 個步驟", IfBlock.KindName(_b.Kind), _b.Steps.Count);
        if (_lastStatus.Length > 0) t += " · " + _lastStatus;
        _summary.Text = _b.Collapsed ? t : "";
    }

    private void ApplyFold()
    {
        _fold.Text = _b.Collapsed ? "▸" : "▾";
        foreach (var c in _cond.Concat(_opts)) c.Visible = !_b.Collapsed;
        _steps.Visible = !_b.Collapsed;
        UpdateSummary();
        Invalidate();
        Relayout();
    }

    private void Save() => _sc.Save();

    private Label L(string text, Color? c = null) => Ui.Text_(text, c ?? Ui.TextDim, Ui.F(9.75f), new Padding(0));

    private NumberBox Num(int width, decimal min, decimal max, decimal value, int decimals, decimal step, Action<decimal> set)
    {
        var n = new NumberBox(width) { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step };
        n.Value = value;
        n.ValueChanged += (_, _) => { set(n.Value); Save(); };
        return n;
    }

    /// <summary>按條件種類重新整第一行（條件）同第二行（選項）。</summary>
    private void RebuildCond()
    {
        SuspendLayout();
        foreach (var c in _cond.Concat(_opts)) { Controls.Remove(c); c.Dispose(); }
        _cond.Clear();
        _opts.Clear();

        if (_nested && _b.Kind == CondKind.Countdown) _b.Kind = CondKind.ImageFound;
        _cond.Add(L(Lang.T("如果"), Ui.Text));
        var kindBtn = new FancyButton { Text = IfBlock.KindName(_b.Kind) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
        kindBtn.Size = new Size(Ui.S(150), Ui.S(34));
        kindBtn.Click += (_, _) => _kindMenu.Show(kindBtn, new Point(0, kindBtn.Height + Ui.S(2)));
        _cond.Add(kindBtn);

        switch (_b.Kind)
        {
            case CondKind.ImageFound:
            case CondKind.ImageNotFound:
            {
                var tile = new ImageStrip(_sc, () => _b.Image, v => { _b.Image = v; Save(); });
                _cond.Add(tile);
                _cond.Add(L(Lang.T("相似度")));
                _cond.Add(Num(Ui.S(84), 10, 100, _b.Similarity, 0, 1, v => _b.Similarity = (int)v));
                _cond.Add(L("%"));
                if (_b.Kind == CondKind.ImageFound)
                {
                    var chip = new ToggleChip(Lang.T("搵到就點擊")) { Checked = _b.ClickFound };
                    chip.Size = new Size(chip.Width, Ui.S(30));
                    _tip.SetToolTip(chip, Lang.T("搵到嗰陣順手喺圖片中心左鍵點一下，再做「然後做」嘅步驟"));
                    chip.CheckedChanged += () => { _b.ClickFound = chip.Checked; Save(); };
                    _cond.Add(chip);
                }
                break;
            }
            case CondKind.PixelColor:
            {
                var x = Num(Ui.S(82), -10000, 20000, _b.X, 0, 1, v => _b.X = (int)v);
                var y = Num(Ui.S(82), -10000, 20000, _b.Y, 0, 1, v => _b.Y = (int)v);
                var swatch = new ColorSwatch(_b.Color);
                _tip.SetToolTip(swatch, Lang.T("撳一下自己揀顏色"));
                swatch.ColorChanged += c => { _b.Color = c; Save(); };
                var pick = new FancyButton { Text = Lang.T("座標"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(58), Ui.S(34)) };
                _tip.SetToolTip(pick, Lang.T("撳完 3 秒內將滑鼠移去目標位置，會一齊記低嗰點嘅顏色"));
                pick.Click += (_, _) => Countdown(pick, Lang.T("座標"), () =>
                {
                    var p = Cursor.Position;
                    x.Value = p.X;
                    y.Value = p.Y;
                    var c = ScreenSearch.PixelAt(p.X, p.Y);
                    swatch.Value = (c.R << 16) | (c.G << 8) | c.B;
                });
                _cond.AddRange(new Control[] { L("X"), x, L("Y"), y, pick, L(Lang.T("顏色")), swatch, L(Lang.T("容差")),
                    Num(Ui.S(78), 0, 255, _b.Tolerance, 0, 1, v => _b.Tolerance = (int)v) });
                break;
            }
            case CondKind.Time:
            {
                TimeSpan.TryParse(_b.Time, CultureInfo.InvariantCulture, out var ts);
                int hh = ts.Hours, mm = ts.Minutes, ss = ts.Seconds;
                void SetTime() { _b.Time = $"{hh:00}:{mm:00}:{ss:00}"; }
                _cond.AddRange(new Control[]
                {
                    Num(Ui.S(70), 0, 23, hh, 0, 1, v => { hh = (int)v; SetTime(); }), L(Lang.T("時")),
                    Num(Ui.S(70), 0, 59, mm, 0, 1, v => { mm = (int)v; SetTime(); }), L(Lang.T("分")),
                    Num(Ui.S(70), 0, 59, ss, 0, 1, v => { ss = (int)v; SetTime(); }), L(Lang.T("秒")),
                    L(_nested ? Lang.T("（行到呢步時已經過咗呢個時間）") : Lang.T("（每日嗰個時間觸發）"), Ui.TextFaint)
                });
                break;
            }
            case CondKind.Countdown:
                _cond.AddRange(new Control[]
                {
                    Num(Ui.S(100), 1, 86400, _b.Seconds, 1, 1, v => _b.Seconds = v), L(Lang.T("秒")),
                    L(Lang.T("（由開始 / 上次觸發計起）"), Ui.TextFaint)
                });
                break;
        }

        // 第二行：檢查間隔、觸發後
        if (_b.UsesInterval && !_nested)
        {
            _opts.Add(L(Lang.T("每")));
            _opts.Add(Num(Ui.S(84), 0.1m, 60m, _b.Interval, 1, 0.1m, v => _b.Interval = v));
            _opts.Add(L(Lang.T("秒檢查一次")));
        }
        _opts.Add(L(Lang.T("然後做："), Ui.Text));

        foreach (var c in _cond.Concat(_opts))
        {
            c.Visible = !_b.Collapsed;
            Controls.Add(c);
        }
        ResumeLayout(false);
        if (_ready) Relayout();
    }

    private static void Countdown(FancyButton btn, string label, Action done)
    {
        int left = 3;
        btn.Enabled = false;
        btn.Text = left + "…";
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            if (--left > 0) { btn.Text = left + "…"; return; }
            t.Stop();
            t.Dispose();
            done();
            btn.Text = label;
            btn.Enabled = true;
        };
        t.Start();
    }

    private void Relayout() => StepList.RelayoutFrom(this);

    private int RowH => Ui.S(34);
    private int Gap => Ui.S(10);

    private int Top0 => Pad + RowH + Ui.S(14); // 標題列下面
    private int StepsY => Top0 + RowH * 2 + Gap + Ui.S(14);
    private int StepsX => Pad + Ui.S(20);

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        if (_b.Collapsed) return new Size(w, RowH + Pad * 2);
        int stepsH = _steps.GetPreferredSize(new Size(w - StepsX - Pad, 0)).Height;
        return new Size(w, StepsY + stepsH + Pad);
    }

    private void Flow(IEnumerable<Control> items, int y)
    {
        int x = Pad;
        foreach (var c in items)
        {
            var sz = c is Label ? c.PreferredSize : c.Size;
            c.Location = new Point(x, y + (RowH - sz.Height) / 2);
            x += sz.Width + Ui.S(8);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_ready) return;
        Flow(new Control[] { _fold, _nameHost }, Pad);
        _delete.Location = new Point(ClientSize.Width - Pad - _delete.Width, Pad);
        int sx = _nameHost.Right + Ui.S(12);
        int right = _nested ? ClientSize.Width - Pad : _delete.Left;
        _summary.SetBounds(sx, Pad, Math.Max(10, right - sx - Ui.S(10)), RowH);
        if (_b.Collapsed) return;
        Flow(_cond, Top0);
        Flow(_opts, Top0 + RowH + Gap);
        // 步驟縮入少少，睇得出係屬於呢個 IF
        int w = Math.Max(10, ClientSize.Width - StepsX - Pad);
        var sz = _steps.GetPreferredSize(new Size(w, 0));
        _steps.SetBounds(StepsX, StepsY, w, sz.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_b.Collapsed) return;
        // 標題列下面一條分隔線
        using (var pen = new Pen(Edge, 1f))
            e.Graphics.DrawLine(pen, Pad, Pad + RowH + Ui.S(7), Width - Pad, Pad + RowH + Ui.S(7));
        // 左邊一條線表示「然後做」嘅範圍
        int sy = StepsY, lx = Pad + Ui.S(4);
        int h = Math.Max(1, Height - sy - Pad);
        using var b = Ui.AccentBrush(new RectangleF(lx, sy, Ui.S(3), h));
        e.Graphics.FillRectangle(b, lx, sy, Ui.S(3), h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _kindMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>顏色格：顯示顏色同 #RRGGBB，撳一下可以自己揀。</summary>
internal sealed class ColorSwatch : Control
{
    private int _rgb;
    private bool _hover;

    public event Action<int>? ColorChanged;

    public ColorSwatch(int rgb)
    {
        _rgb = rgb;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(Ui.S(104), Ui.S(34));
        Font = Ui.Mono(9.5f);
        Cursor = Cursors.Hand;
    }

    public int Value
    {
        get => _rgb;
        set { _rgb = value & 0xFFFFFF; Invalidate(); ColorChanged?.Invoke(_rgb); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        using var dlg = new ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(_rgb) };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Value = dlg.Color.ToArgb() & 0xFFFFFF;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Field.Paint(this, g, false, _hover);
        Ui.Smooth(g);
        var box = new RectangleF(Ui.S(8), Ui.S(8), Height - Ui.S(16), Height - Ui.S(16));
        using (var path = Ui.Round(box, Ui.S(4f)))
        using (var b = new SolidBrush(System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(_rgb))))
        {
            g.FillPath(b, path);
            using var pen = new Pen(Ui.BorderHi, 1f);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, $"#{_rgb:X6}", Font, new Rectangle((int)box.Right + Ui.S(6), 0, Width, Height), Ui.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 截圖模式：成個畫面變暗，拖一個框揀範圍，放手就攞嗰部分。Esc / 右鍵取消。
/// </summary>
internal sealed class SnipOverlay : Form
{
    private readonly Bitmap _shot;
    private readonly Rectangle _vs;
    private Point _start, _cur;
    private bool _drag;
    private Rectangle Sel => Rectangle.FromLTRB(Math.Min(_start.X, _cur.X), Math.Min(_start.Y, _cur.Y),
        Math.Max(_start.X, _cur.X), Math.Max(_start.Y, _cur.Y));

    public Bitmap? Result { get; private set; }

    /// <summary>收埋自己個視窗 → 截成個畫面 → 俾用家框 → 回傳框住嗰部分（取消就 null）。</summary>
    public static Bitmap? Grab(Form? owner)
    {
        double oldOpacity = owner?.Opacity ?? 1;
        try
        {
            if (owner != null)
            {
                owner.Opacity = 0;
                owner.Update();
                Application.DoEvents();
                System.Threading.Thread.Sleep(180); // 等個視窗真係消失
            }
            var vs = SystemInformation.VirtualScreen;
            var shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            using var ov = new SnipOverlay(shot, vs);
            ov.ShowDialog();
            return ov.Result;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("截圖", ex);
            return null;
        }
        finally
        {
            if (owner != null)
            {
                owner.Opacity = oldOpacity;
                owner.Activate();
            }
        }
    }

    private SnipOverlay(Bitmap shot, Rectangle vs)
    {
        _shot = shot;
        _vs = vs;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = vs;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _vs; // 某啲多螢幕設定要再設一次
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        _drag = true;
        _start = _cur = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var old = _cur;
        _cur = e.Location;
        if (_drag || old != _cur) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag || e.Button != MouseButtons.Left) return;
        _drag = false;
        _cur = e.Location;
        var r = Rectangle.Intersect(Sel, new Rectangle(0, 0, _shot.Width, _shot.Height));
        if (r.Width < 4 || r.Height < 4)
        {
            Invalidate(); // 框太細，當冇揀，可以再拖過
            return;
        }
        Result = _shot.Clone(r, PixelFormat.Format32bppArgb);
        Close();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_shot, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
        {
            if (_drag && Sel.Width > 0 && Sel.Height > 0)
            {
                var sel = Sel;
                using var region = new Region(ClientRectangle);
                region.Exclude(sel);
                g.FillRegion(dim, region);
                using var pen = new Pen(Ui.Accent, 2f);
                g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
                string size = $"{sel.Width} × {sel.Height}";
                var f = Ui.F(9f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(size, f);
                int ty = sel.Y - sz.Height - 6 >= 0 ? sel.Y - sz.Height - 6 : sel.Bottom + 6;
                var tr = new Rectangle(sel.X, ty, sz.Width + 12, sz.Height + 4);
                using (var bg = new SolidBrush(Color.FromArgb(220, 20, 22, 28))) g.FillRectangle(bg, tr);
                TextRenderer.DrawText(g, size, f, tr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                g.FillRectangle(dim, ClientRectangle);
                // 十字線幫手對位
                using var pen = new Pen(Color.FromArgb(120, Ui.Accent), 1f);
                g.DrawLine(pen, 0, _cur.Y, Width, _cur.Y);
                g.DrawLine(pen, _cur.X, 0, _cur.X, Height);
            }
        }
        // 提示（喺滑鼠所在嗰個螢幕頂中間）
        var screen = Screen.FromPoint(PointToScreen(_cur)).Bounds;
        screen.Offset(-_vs.X, -_vs.Y);
        string hint = Lang.T("拖一個框，框住要搵嘅圖片　·　Esc / 右鍵取消");
        var hf = Ui.F(10.5f, FontStyle.Bold);
        var hs = TextRenderer.MeasureText(hint, hf);
        var hr = new Rectangle(screen.X + (screen.Width - hs.Width) / 2 - 16, screen.Y + 24, hs.Width + 32, hs.Height + 16);
        using (var bg = new SolidBrush(Color.FromArgb(225, 20, 22, 28))) g.FillRectangle(bg, hr);
        TextRenderer.DrawText(g, hint, hf, hr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shot.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>循環步驟入面嘅步驟清單（左邊一條色線）。</summary>
internal sealed class LoopBlock : TintPanel
{
    private readonly StepList _steps;

    public LoopBlock(Script sc, List<Step> body) : base(Ui.Success, 0.10f)
    {
        _steps = new StepList(sc, body, nestedIf: true, inLoop: true) { Dock = DockStyle.None };
        Controls.Add(_steps);
    }

    private int Indent => Pad + Ui.S(14);

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        return new Size(w, Pad * 2 + _steps.GetPreferredSize(new Size(Math.Max(50, w - Indent - Pad), 0)).Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_steps == null) return;
        int w = Math.Max(50, ClientSize.Width - Indent - Pad);
        _steps.SetBounds(Indent, Pad, w, _steps.GetPreferredSize(new Size(w, 0)).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var b = new SolidBrush(Ui.Success);
        e.Graphics.FillRectangle(b, Pad, Pad, Ui.S(3), Math.Max(1, Height - Pad * 2));
    }
}

// =====================================================================
//  預覽（唔可以改）
// =====================================================================

/// <summary>將步驟 / 條件變做一句人睇得明嘅說明。</summary>
internal static class Describe
{
    private static string D(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Mode(PressMode m, decimal hold) => m switch
    {
        PressMode.Down => Lang.T("（按下）"),
        PressMode.Up => Lang.T("（彈起）"),
        _ => Lang.F("（撳住 {0} ms）", Math.Round(hold * 1000m))
    };

    public static string Step(Step st) => st.Kind switch
    {
        StepKind.Key => (st.Vk == 0 ? Lang.T("（未設定）") : KeyCaptureBox.Format(st.Mods, st.Vk)) + " " + Mode(st.Mode, st.Hold),
        StepKind.Click => $"{(st.Right ? Lang.T("右鍵") : Lang.T("左鍵"))} ({st.X}, {st.Y}) {Mode(st.Mode, st.Hold)}",
        StepKind.Wait => $"{Math.Round(st.Seconds * 1000m)} ms",
        StepKind.Image => st.Image.Length == 0 ? Lang.T("（未揀圖）") : Lang.F("{0}相似度 {1}%，搵到最似嗰張就點", Count(st.Image), st.Similarity),
        StepKind.Loop => st.Count == 0 ? Lang.T("一直重複") : Lang.F("重複 {0} 次", st.Count),
        StepKind.Break => Lang.T("跳出最近嗰個循環"),
        StepKind.If => st.Sub == null ? "" : Lang.F("{0}：如果{1}", st.Sub.Name, Cond(st.Sub, true)),
        _ => ""
    };

    private static string Count(string images)
    {
        int n = ImageList.Split(images).Count;
        return n > 1 ? Lang.F("{0} 張圖 · ", n) : "";
    }

    public static string Cond(IfBlock b, bool nested) => b.Kind switch
    {
        CondKind.ImageFound => b.Image.Length == 0 ? Lang.T("畫面搵到圖（未揀圖）") : Lang.F("畫面搵到圖（{0}相似度 {1}%{2}）", Count(b.Image), b.Similarity, (b.ClickFound ? Lang.T("，搵到就點擊") : "")),
        CondKind.ImageNotFound => b.Image.Length == 0 ? Lang.T("畫面搵唔到圖（未揀圖）") : Lang.F("畫面搵唔到圖（{0}全部都搵唔到，相似度 {1}%）", Count(b.Image), b.Similarity),
        CondKind.PixelColor => Lang.F("({0}, {1}) 係 #{2:X6}（容差 {3}）", b.X, b.Y, b.Color, b.Tolerance),
        CondKind.Time => nested ? Lang.F("已經過咗 {0}", b.Time) : Lang.F("時間到 {0}（每日）", b.Time),
        _ => Lang.F("倒數 {0} 秒", D(b.Seconds))
    };

    /// <summary>步驟有冇圖（預覽顯示縮圖用）。</summary>
    public static string? Image(Step st) =>
        st.Kind == StepKind.Image ? ImageList.Split(st.Image).FirstOrDefault() :
        st.Kind == StepKind.If && st.Sub != null && st.Sub.Kind is CondKind.ImageFound or CondKind.ImageNotFound
            ? ImageList.Split(st.Sub.Image).FirstOrDefault() : null;
}

/// <summary>唯讀步驟清單：一行一步，循環 / IF 入面嘅步驟會縮入，左邊有色線。</summary>
internal sealed class PreviewList : Control
{
    private sealed record Line(int Depth, StepKind Kind, int Number, string Text, string? Image, Color[] Bars);

    private readonly List<Line> _lines = new();
    private readonly string _dir;
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    public PreviewList(Script sc, List<Step> steps)
    {
        _dir = sc.Dir;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Add(steps, 0, Array.Empty<Color>());
    }

    private void Add(List<Step> steps, int depth, Color[] bars)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            _lines.Add(new Line(depth, st.Kind, i + 1, Describe.Step(st), Describe.Image(st), bars));
            if (st.Kind == StepKind.Loop)
                Add(st.Body, depth + 1, bars.Append(Ui.Success).ToArray());
            else if (st.Kind == StepKind.If && st.Sub != null)
                Add(st.Sub.Steps, depth + 1, bars.Append(Ui.Accent).ToArray());
        }
    }

    private int LineH => Ui.S(30);
    private int IndentW => Ui.S(26);

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        return new Size(w, Math.Max(1, _lines.Count) * LineH);
    }

    private Bitmap? Thumb(string name)
    {
        if (_thumbs.TryGetValue(name, out var b)) return b;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(_dir, name)));
            using var img = Image.FromStream(ms);
            int h = Ui.S(22);
            b = new Bitmap(img, Math.Max(1, Math.Min(Ui.S(80), img.Width * h / Math.Max(1, img.Height))), h);
        }
        catch { b = null; }
        _thumbs[name] = b;
        return b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        if (_lines.Count == 0)
        {
            TextRenderer.DrawText(g, Lang.T("未有步驟"), Ui.F(9.5f), new Point(0, Ui.S(6)), Ui.TextFaint, TextFormatFlags.NoPadding);
            return;
        }
        var nf = Ui.F(9.5f, FontStyle.Bold);
        var tf = Ui.F(9.75f);
        var bf = Ui.F(8.75f, FontStyle.Bold);
        for (int i = 0; i < _lines.Count; i++)
        {
            var ln = _lines[i];
            int y = i * LineH;
            // 外層循環 / IF 嘅色線
            for (int d = 0; d < ln.Bars.Length; d++)
            {
                using var bar = new SolidBrush(ln.Bars[d]);
                g.FillRectangle(bar, d * IndentW + Ui.S(6), y, Ui.S(3), LineH);
            }
            int x = ln.Depth * IndentW;
            TextRenderer.DrawText(g, ln.Number + ".", nf, new Rectangle(x, y, Ui.S(26), LineH), Ui.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += Ui.S(26);
            // 類型標籤
            string kind = global::MultiTool.Scripts.Step.KindName(ln.Kind);
            var hue = ln.Kind switch { StepKind.Loop => Ui.Success, StepKind.If => Ui.Accent, StepKind.Break => Ui.Danger, _ => Ui.Accent2 };
            var ks = TextRenderer.MeasureText(kind, bf);
            var kr = new RectangleF(x, y + (LineH - Ui.S(22)) / 2f, ks.Width + Ui.S(16), Ui.S(22));
            using (var path = Ui.Round(kr, Ui.S(11f)))
            using (var b = new SolidBrush(Ui.Alpha(hue, 45)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, kind, bf, Rectangle.Round(kr), Ui.Mix(hue, Color.White, 0.45f),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += (int)kr.Width + Ui.S(10);
            if (ln.Image != null && Thumb(ln.Image) is { } th)
            {
                g.DrawImage(th, x, y + (LineH - th.Height) / 2);
                using var pen = new Pen(Ui.Border, 1f);
                g.DrawRectangle(pen, x, y + (LineH - th.Height) / 2, th.Width, th.Height);
                x += th.Width + Ui.S(10);
            }
            TextRenderer.DrawText(g, ln.Text, tf, new Rectangle(x, y, Math.Max(10, Width - x), LineH), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var b in _thumbs.Values) b?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>腳本預覽：睇晒所有步驟（唔可以改）；上面有「修改」同「開始 / 停止」。</summary>
internal sealed class ScriptViewer : Panel
{
    private readonly Script _sc;
    private ToggleChip _bgToggle = null!;
    private FancyButton _bgPick = null!;
    private FlowRow _flow = null!;
    private readonly ToolTip _tip = new();
    private readonly ContextMenuStrip _winMenu = new();

    private void UpdateBg()
    {
        _bgPick.Tag = _sc.Background ? null : "hidden";
        _bgPick.Visible = _sc.Background;
        string label = _sc.TargetProcess.Length == 0 ? Lang.T("揀程式…") : $"{_sc.TargetProcess} - {_sc.TargetTitle}";
        _bgPick.Text = label + "  ▾";
        _tip.SetToolTip(_bgPick, _sc.TargetProcess.Length == 0 ? Lang.T("揀要喺邊個程式入面運行") : label);
        _flow?.PerformLayout();
    }

    /// <summary>彈出而家開緊嘅視窗清單俾用家揀。</summary>
    private void ShowWindowMenu()
    {
        _winMenu.Items.Clear();
        try { _winMenu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _winMenu.BackColor = Ui.CardHi;
        _winMenu.ForeColor = Ui.Text;
        var wins = Win.List();
        if (wins.Count == 0) _winMenu.Items.Add(Lang.T("（冇搵到視窗）")).Enabled = false;
        foreach (var w in wins)
        {
            var item = new ToolStripMenuItem(w.ToString().Length > 70 ? w.ToString()[..70] + "…" : w.ToString())
            {
                Checked = string.Equals(w.Process, _sc.TargetProcess, StringComparison.OrdinalIgnoreCase) && w.Title == _sc.TargetTitle
            };
            var info = w;
            item.Click += (_, _) =>
            {
                _sc.TargetProcess = info.Process;
                _sc.TargetTitle = info.Title;
                _sc.Save();
                UpdateBg();
            };
            _winMenu.Items.Add(item);
        }
        _winMenu.Show(_bgPick, new Point(0, _bgPick.Height + Ui.S(2)));
    }
    public Script Script => _sc;
    private FancyButton? _back;
    private readonly CardStack _stack = new();
    private readonly FancyButton _start;
    private readonly Label _status;
    private readonly Action<string, string> _onStatus;
    private readonly Action _onChanged;

    public event Action? Back, Edit;

    public ScriptViewer(Script sc)
    {
        _sc = sc;
        BackColor = Ui.Bg;
        _stack.Dock = DockStyle.Fill;

        var back = new FancyButton { Text = Lang.T("← 返回"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        back.Click += (_, _) => { if (!ScriptRunner.IsRunning(_sc)) BeginInvoke(() => Back?.Invoke()); };
        _back = back;
        var title = Ui.Text_(sc.Name, Ui.Text, Ui.F(12f, FontStyle.Bold), new Padding(0));
        string info = (sc.Loop ? Lang.T("一直行") : Lang.T("行一次")) + (sc.Hotkey.IsEmpty ? "" : Lang.T(" · 快捷鍵 ") + sc.Hotkey);
        _sc = sc;
        var infoLabel = Ui.Text_(info, Ui.TextDim, Ui.F(9.5f), new Padding(0));
        var edit = new FancyButton { Text = Lang.T("修改"), Kind = BtnKind.Primary, Size = new Size(Ui.S(96), Ui.S(34)) };
        edit.Click += (_, _) => BeginInvoke(() => Edit?.Invoke());
        _start = new FancyButton { Kind = BtnKind.Success, Size = new Size(Ui.S(96), Ui.S(34)) };
        _start.Click += (_, _) => ScriptRunner.Toggle(_sc);

        var top = new BareRow();
        // 背景執行：開咗就要揀程式，腳本淨係喺嗰個視窗入面截圖 / 點擊 / 按鍵
        _bgToggle = new ToggleChip(Lang.T("背景執行")) { Checked = sc.Background };
        _bgToggle.Size = new Size(_bgToggle.Width + Ui.S(8), Ui.S(34));
        _tip.SetToolTip(_bgToggle, Lang.T("開咗之後，腳本淨係喺揀咗嘅程式視窗入面運行，你切去其他視窗都照行。\n") +
                                   Lang.T("注意：部分遊戲唔收背景點擊 / 按鍵；程式視窗唔可以縮細。"));
        _bgPick = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(190), Ui.S(34)) };
        _bgPick.Click += (_, _) => ShowWindowMenu();
        _bgToggle.CheckedChanged += () =>
        {
            _sc.Background = _bgToggle.Checked;
            _sc.Save();
            UpdateBg();
        };
        // 運行速度：0.5x（慢一倍）– 3.0x（快三倍），運行中改都即時生效。
        // 一粒掣：撳開揀常用倍率；滑鼠滾輪每格 ±0.1x
        var speed = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        void ShowSpeed()
        {
            speed.Text = Lang.T("運行速度") + "  " + _sc.Speed.ToString("0.0#", CultureInfo.InvariantCulture) + "x  ▾";
            speed.Size = new Size(Math.Max(Ui.S(130), speed.GetPreferredSize(Size.Empty).Width), Ui.S(34));
            speed.Invalidate();
            _flow?.PerformLayout();
        }
        void SetSpeed(double v)
        {
            _sc.Speed = Math.Round(Math.Clamp(v, 0.5, 3.0), 2);
            _sc.Save();
            ShowSpeed();
        }
        var speedMenu = new ContextMenuStrip();
        try { speedMenu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        speedMenu.BackColor = Ui.CardHi;
        speedMenu.ForeColor = Ui.Text;
        foreach (double v in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0 })
        {
            string label = v.ToString("0.0#", CultureInfo.InvariantCulture) + "x" +
                           (v == 1.0 ? "  " + Lang.T("（正常）") : v < 1 ? "  " + Lang.T("（慢）") : "");
            var item = new ToolStripMenuItem(label) { ForeColor = Ui.Text, Tag = v };
            item.Click += (_, _) => SetSpeed((double)item.Tag!);
            speedMenu.Items.Add(item);
        }
        speedMenu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in speedMenu.Items) it.Checked = Math.Abs((double)it.Tag! - _sc.Speed) < 0.001;
        };
        speed.Click += (_, _) => speedMenu.Show(speed, new Point(0, speed.Height));
        speed.MouseWheel += (_, e) => SetSpeed(_sc.Speed + (e.Delta > 0 ? 0.1 : -0.1));
        _tip.SetToolTip(speed, Lang.T("腳本運行速度倍率：1.0x 正常、2.0x 快一倍、0.5x 慢一倍（等待、撳住、檢查間隔都會跟住變）") + "\n" + Lang.T("（滑鼠滾輪可以微調 ±0.1x）"));
        _flow = new FlowRow(new Control[] { back, title, speed }, new Control[] { _start, edit, _bgToggle, _bgPick });
        ShowSpeed();
        top.SetContent(_flow);
        UpdateBg();
        top.Padding = new Padding(0);
        _stack.AddRow(top);

        // 主線程
        var main = new CardPanel { Title = Lang.T("主線程"), Hint = Lang.F("{0} 個步驟 · {1}", sc.Steps.Count, info) };
        var mainPanel = new StatusWrap(new PreviewList(sc, sc.Steps), out _status);
        main.SetContent(mainPanel);
        _stack.AddRow(main);

        // IF 框
        foreach (var b in sc.Ifs)
        {
            string every = b.UsesInterval ? Lang.F(" · 每 {0} 秒檢查", b.Interval.ToString("0.##", CultureInfo.InvariantCulture)) : "";
            var card = new CardPanel { Title = "IF：" + b.Name, Hint = Lang.F("如果{0}{1}（同主線程同時行）", Describe.Cond(b, false), every) };
            var content = new PreviewList(sc, b.Steps);
            card.SetContent(content);
            _stack.AddRow(card);
        }
        Controls.Add(_stack);

        _onStatus = (id, text) => { if (id == _sc.Id) _status.Text = text; };
        _onChanged = UpdateButtons;
        ScriptRunner.Status += _onStatus;
        ScriptRunner.Changed += _onChanged;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool run = ScriptRunner.IsRunning(_sc);
        _start.Text = run ? Lang.T("停止") : Lang.T("開始");
        _start.Kind = run ? BtnKind.Danger : BtnKind.Success;
        // 運行緊唔可以返上一頁（要先停止），亦唔可以轉背景設定
        if (_back != null) _back.Enabled = !run;
        if (_bgToggle != null) _bgToggle.Enabled = !run;
        if (_bgPick != null) _bgPick.Enabled = !run;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ScriptRunner.Status -= _onStatus;
            ScriptRunner.Changed -= _onChanged;
        }
        base.Dispose(disposing);
    }
}

/// <summary>一個控件 + 下面一行狀態字。</summary>
internal sealed class StatusWrap : Panel
{
    private readonly Control _inner;
    private readonly Label _status;

    public StatusWrap(Control inner, out Label status)
    {
        BackColor = Color.Transparent;
        _inner = inner;
        _status = Ui.Text_("", Ui.Text, Ui.F(9f), new Padding(0));
        status = _status;
        Controls.Add(_inner);
        Controls.Add(_status);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        return new Size(w, _inner.GetPreferredSize(new Size(w, 0)).Height + Ui.S(30));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_inner == null || _status == null) return;
        int h = _inner.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        _inner.SetBounds(0, 0, ClientSize.Width, h);
        _status.Location = new Point(0, h + Ui.S(10));
    }
}

/// <summary>
/// 一排圖片格（最多 6 張）：撳「+」截圖加一張；撳已有嘅圖重新截圖覆蓋；右鍵可以從檔案揀 / 移除；可以拖圖入嚟。
/// </summary>
internal sealed class ImageStrip : Control
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private readonly Script _sc;
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Dictionary<string, Bitmap?> _thumbs = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tip = new();
    private int _hover = -1, _menuIndex = -1;

    public ImageStrip(Script sc, Func<string> get, Action<string> set)
    {
        _sc = sc;
        _get = get;
        _set = set;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AllowDrop = true;
        try { _menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _menu.BackColor = Ui.CardHi;
        _menu.ForeColor = Ui.Text;
        _menu.Items.Add(Lang.T("重新截圖"), null, (_, _) => Snip(_menuIndex));
        _menu.Items.Add(Lang.T("從檔案揀…"), null, (_, _) => PickFile(_menuIndex));
        _menu.Items.Add(Lang.T("移除"), null, (_, _) => Remove(_menuIndex));
        UpdateSize();
    }

    private List<string> Names => ImageList.Split(_get());
    private int Tiles => Math.Min(ImageList.Max, Names.Count + 1);
    private bool IsAdd(int i) => i == Names.Count && Names.Count < ImageList.Max;
    private int TileW => Ui.S(44);
    private int Gap => Ui.S(4);

    private void UpdateSize()
    {
        Size = new Size(Tiles * (TileW + Gap) - Gap, Ui.S(34));
        _tip.SetToolTip(this, Lang.F("最多 {0} 張，搵到最似嗰張。撳「+」截圖加；撳圖重新截；右鍵從檔案揀 / 移除", ImageList.Max));
    }

    /// <summary>張數變咗：格仔數目變，叫成行重新排位。</summary>
    private void Changed(List<string> names)
    {
        _set(ImageList.Join(names));
        UpdateSize();
        Invalidate();
        StepList.RelayoutFrom(Parent ?? this);
    }

    private Rectangle TileRect(int i) => new(i * (TileW + Gap), 0, TileW - 1, Height - 1);

    private int HitTest(Point p)
    {
        for (int i = 0; i < Tiles; i++) if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    private Bitmap? Thumb(string name)
    {
        if (_thumbs.TryGetValue(name, out var b)) return b;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(_sc.Dir, name)));
            using var img = Image.FromStream(ms);
            int h = Ui.S(30);
            b = new Bitmap(img, Math.Max(1, img.Width * h / Math.Max(1, img.Height)), h);
        }
        catch { b = null; }
        _thumbs[name] = b;
        return b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var names = Names;
        for (int i = 0; i < Tiles; i++)
        {
            var r = TileRect(i);
            bool hover = i == _hover;
            using var path = Ui.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width, r.Height), Ui.S(6f));
            using (var b = new SolidBrush(hover ? Ui.CardHi : Ui.Input)) g.FillPath(b, path);
            if (IsAdd(i))
            {
                using var pen = new Pen(hover ? Ui.Accent : Ui.BorderHi, Ui.S(1.2f)) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, path);
                TextRenderer.DrawText(g, names.Count == 0 ? Lang.T("截圖") : "+", Ui.F(names.Count == 0 ? 8.5f : 12f), r,
                    hover ? Ui.Accent : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                continue;
            }
            var th = Thumb(names[i]);
            if (th != null)
            {
                var st = g.Save();
                g.SetClip(path);
                float s = Math.Min((r.Width - 4f) / th.Width, (r.Height - 4f) / th.Height);
                float w = th.Width * s, h = th.Height * s;
                g.DrawImage(th, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                g.Restore(st);
            }
            using (var pen = new Pen(hover ? Ui.Accent : Ui.Border, 1f)) g.DrawPath(pen, path);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = HitTest(e.Location);
        if (i < 0) return;
        if (e.Button == MouseButtons.Left) Snip(i);
        else if (e.Button == MouseButtons.Right)
        {
            if (IsAdd(i)) { PickFile(i); return; }
            _menuIndex = i;
            _menu.Show(this, e.Location);
        }
    }

    private void Snip(int i)
    {
        using var bmp = SnipOverlay.Grab(FindForm());
        if (bmp == null) return;
        string tmp = Path.Combine(Path.GetTempPath(), "multitool_snip_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            bmp.Save(tmp, ImageFormat.Png);
            Put(i, tmp);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private void PickFile(int i)
    {
        using var dlg = new OpenFileDialog { Title = Lang.T("揀圖片"), Filter = Lang.T("圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*") };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Put(i, dlg.FileName);
    }

    /// <summary>將圖複製入腳本資料夾，放喺第 i 格（i = 張數 就係新加）。</summary>
    private void Put(int i, string src)
    {
        try
        {
            Directory.CreateDirectory(_sc.Dir);
            string ext = Path.GetExtension(src).ToLowerInvariant();
            if (!Exts.Contains(ext)) ext = ".png";
            string name = "img_" + Guid.NewGuid().ToString("N")[..8] + ext;
            File.Copy(src, Path.Combine(_sc.Dir, name), true);
            var names = Names;
            string? old = null;
            if (i >= 0 && i < names.Count) { old = names[i]; names[i] = name; }
            else if (names.Count < ImageList.Max) names.Add(name);
            else return;
            Changed(names);
            if (old != null && !_sc.UsesImage(old)) try { File.Delete(Path.Combine(_sc.Dir, old)); } catch { }
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), Lang.T("揀圖失敗：") + ex.Message, Lang.T("我的腳本")); }
    }

    private void Remove(int i)
    {
        var names = Names;
        if (i < 0 || i >= names.Count) return;
        string old = names[i];
        names.RemoveAt(i);
        Changed(names);
        if (!_sc.UsesImage(old)) try { File.Delete(Path.Combine(_sc.Dir, old)); } catch { }
    }

    private static string? Dropped(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = Dropped(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var f = Dropped(e);
        if (f == null) return;
        int i = HitTest(PointToClient(new Point(e.X, e.Y)));
        Put(i < 0 ? Names.Count : i, f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in _thumbs.Values) b?.Dispose();
            _menu.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 腳本運行環境。前景：截成個螢幕、用真滑鼠鍵盤（座標 = 螢幕座標）。
/// 背景：淨係截指定視窗、將點擊 / 按鍵訊息直接送去嗰個視窗（座標 = 視窗內容區座標），切咗去其他視窗都照行。
/// </summary>
internal sealed class RunCtx
{
    public static readonly RunCtx Foreground = new(null, 0);

    private readonly Rectangle? _exclude; // 前景：自己個視窗唔計
    public readonly nint Hwnd;
    public bool Bg => Hwnd != 0;

    public RunCtx(Rectangle? exclude, nint hwnd)
    {
        _exclude = exclude;
        Hwnd = hwnd;
    }

    /// <summary>螢幕座標 → 呢個環境嘅座標（背景就換做視窗內容區座標）。</summary>
    public Point FromScreen(int x, int y) => Bg ? Win.ScreenToClientPt(Hwnd, new Point(x, y)) : new Point(x, y);

    public void KeyDown(int vk, bool alt)
    {
        if (Bg) Win.PostKey(Hwnd, vk, false, alt); else Keyboard.Down(vk);
    }

    public void KeyUp(int vk, bool alt)
    {
        if (Bg) Win.PostKey(Hwnd, vk, true, alt); else Keyboard.Up(vk);
    }

    public void MouseDown(int x, int y, bool right)
    {
        if (Bg) Win.PostMouse(Hwnd, x, y, right, down: true);
        else Mouse.Down(x, y, right);
    }

    public void MouseUp(int x, int y, bool right, bool move)
    {
        if (Bg) Win.PostMouse(Hwnd, x, y, right, down: false);
        else Mouse.Up(x, y, right, move);
    }

    public void Click(int x, int y, bool right)
    {
        if (Bg)
        {
            Win.PostMouse(Hwnd, x, y, right, down: true);
            Thread.Sleep(40);
            Win.PostMouse(Hwnd, x, y, right, down: false);
        }
        else Mouse.Click(x, y, right, restore: false);
    }

    /// <summary>截圖（前景成個螢幕；背景淨係個視窗）。回傳嘅點已經係呢個環境嘅座標。</summary>
    private (Gray? Img, Point Origin, Rectangle? Exclude) Capture()
    {
        if (Bg)
        {
            using var bmp = Win.CaptureClient(Hwnd);
            return bmp == null ? (null, Point.Empty, null) : (Gray.From(bmp), Point.Empty, null);
        }
        var vs = SystemInformation.VirtualScreen;
        using var shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
        Rectangle? ex = _exclude.HasValue
            ? new Rectangle(_exclude.Value.X - vs.X, _exclude.Value.Y - vs.Y, _exclude.Value.Width, _exclude.Value.Height)
            : null;
        return (Gray.From(shot), vs.Location, ex);
    }

    /// <summary>同幾張圖逐張配對，回傳最似嗰張（夠相似度先算）嘅中心點。</summary>
    public Point? FindAny(List<string> imagePaths, double threshold, CancellationToken ct)
    {
        var (screen, origin, ex) = Capture();
        if (screen == null) return null; // 背景視窗縮細咗等情況
        Matcher.Result best = default;
        foreach (var path in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            Gray tpl;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms);
                using var b = new Bitmap(img);
                tpl = Gray.From(b);
            }
            catch { continue; }
            var r = Matcher.Find(screen, tpl, ex, ct);
            if (r.Score > best.Score) best = r;
        }
        if (best.Score < threshold || best.W == 0) return null;
        return new Point(origin.X + best.X + best.W / 2, origin.Y + best.Y + best.H / 2);
    }

    /// <summary>某點嘅顏色（座標係螢幕座標，背景會自動換算）。</summary>
    public Color PixelAt(int screenX, int screenY)
    {
        if (!Bg) return ScreenSearch.PixelAt(screenX, screenY);
        var p = FromScreen(screenX, screenY);
        using var bmp = Win.CaptureClient(Hwnd);
        if (bmp == null || p.X < 0 || p.Y < 0 || p.X >= bmp.Width || p.Y >= bmp.Height) return Color.Black;
        return bmp.GetPixel(p.X, p.Y);
    }
}

/// <summary>Windows 視窗相關：列出視窗、背景截圖、送滑鼠 / 鍵盤訊息。</summary>
internal static class Win
{
    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool EnumProc(nint h, nint l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint h, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint h, POINT p, uint flags);

    public sealed record WinInfo(nint Handle, string Process, string Title)
    {
        public override string ToString() => $"{Process} - {Title}";
    }

    /// <summary>列出所有見到嘅主視窗（有標題、唔係自己）。</summary>
    public static List<WinInfo> List()
    {
        var list = new List<WinInfo>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != 0) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == self) return true;
                string proc;
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
                list.Add(new WinInfo(h, proc, sb.ToString()));
            }
            catch { }
            return true;
        }, 0);
        return list.OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title).ToList();
    }

    /// <summary>用 process 名 + 標題搵返個視窗；標題變咗就用同 process 第一個視窗。</summary>
    public static nint Find(string process, string title)
    {
        if (string.IsNullOrEmpty(process)) return 0;
        var all = List().Where(w => string.Equals(w.Process, process, StringComparison.OrdinalIgnoreCase)).ToList();
        return (all.FirstOrDefault(w => w.Title == title) ?? all.FirstOrDefault())?.Handle ?? 0;
    }

    public static Point ScreenToClientPt(nint h, Point p)
    {
        var pt = new POINT { X = p.X, Y = p.Y };
        ScreenToClient(h, ref pt);
        return new Point(pt.X, pt.Y);
    }

    /// <summary>截視窗內容區（被遮住都得；縮細咗就截唔到，回傳 null）。</summary>
    public static Bitmap? CaptureClient(nint h)
    {
        if (!IsWindow(h) || IsIconic(h)) return null;
        if (!GetClientRect(h, out var r) || r.R <= 0 || r.B <= 0) return null;
        var bmp = new Bitmap(r.R, r.B, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            nint hdc = g.GetHdc();
            try { PrintWindow(h, hdc, 1 /*PW_CLIENTONLY*/ | 2 /*PW_RENDERFULLCONTENT*/); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    private static nint MakeLParam(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    /// <summary>喺視窗內容區 (x, y) 送滑鼠訊息（會送去嗰點最入面嘅子視窗）。</summary>
    public static void PostMouse(nint h, int x, int y, bool right, bool down)
    {
        nint target = h;
        var pt = new POINT { X = x, Y = y };
        // 搵最入面嘅子視窗，座標換算返佢自己嘅
        for (int i = 0; i < 8; i++)
        {
            var child = ChildWindowFromPointEx(target, pt, 1 | 4 /*SKIPINVISIBLE|SKIPTRANSPARENT*/);
            if (child == 0 || child == target) break;
            var scr = pt;
            ClientToScreen(target, ref scr);
            ScreenToClient(child, ref scr);
            target = child;
            pt = scr;
        }
        nint lp = MakeLParam(pt.X, pt.Y);
        PostMessage(target, WM_MOUSEMOVE, 0, lp);
        uint msg = right ? (down ? WM_RBUTTONDOWN : WM_RBUTTONUP) : (down ? WM_LBUTTONDOWN : WM_LBUTTONUP);
        PostMessage(target, msg, down ? (right ? 2 : 1) : 0, lp);
    }

    /// <summary>送按鍵訊息去視窗（有 Alt 就用 SYSKEY）。</summary>
    public static void PostKey(nint h, int vk, bool up, bool alt)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        bool ext = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
        long lp = 1 | (scan << 16) | (ext ? 1L << 24 : 0) | (alt ? 1L << 29 : 0) | (up ? (1L << 30) | (1L << 31) : 0);
        uint msg = alt || vk == 0x12 ? (up ? WM_SYSKEYUP : WM_SYSKEYDOWN) : (up ? WM_KEYUP : WM_KEYDOWN);
        PostMessage(h, msg, vk, (nint)lp);
    }
}
