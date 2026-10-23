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

public enum StepKind { Key, Click, Wait, Image, If, Loop, Break, Drag, StopScript, Thread, Parallel, System, Text, Scroll }

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
    public int X, Y;               // 點擊：座標；拖拉：起點
    public int X2, Y2;             // 拖拉：終點（拖拉用 Hold 做用時）
    public decimal Seconds = 1m;   // 等待
    public string Image = "";      // 搵圖：腳本資料夾入面嘅檔名
    public int Similarity = 80;    // 搵圖：相似度 %
    public IfBlock? Sub;           // IF 步驟（IF 入面嘅 IF）：條件 + 入面嘅步驟
    public int Count = 0;          // 循環：重複幾多次（0 = 一直重複，預設）
    public List<Step> Body = new(); // 循環：入面嘅步驟
    public List<List<Step>> Branches = new(); // 同時運行：每條支線嘅步驟
    public bool Collapsed;                    // 循環 / 同時運行：編輯介面摺埋
    public SysAction Sys;                     // 系統：做咩
    public string Target = "";                // 系統：程式路徑 / process 名
    public string TargetName = "";            // 系統：顯示用名（程式名 / 視窗標題）
    public bool Repeat;                       // 系統 → 播放音效：一直重複
    public bool Random;                       // 延遲：隨機
    public decimal Seconds2 = 2m;             // 延遲：隨機嘅最長時間（秒）
    public decimal LimitSec = 0m;   // 循環：時限（秒，0 = 唔限）
    public string Until = "";       // 循環：到呢個現實時間就停（HH:mm:ss，空 = 唔限）

    public Step Clone()
    {
        var c = (Step)MemberwiseClone();
        c.Sub = Sub?.Clone();
        c.Body = Body.Select(x => x.Clone()).ToList();
        c.Branches = Branches.Select(b => b.Select(x => x.Clone()).ToList()).ToList();
        return c;
    }

    /// <summary>入面所有子步驟清單（循環 / 新線程 / IF / 同時運行嘅支線）。</summary>
    public IEnumerable<List<Step>> Kids()
    {
        yield return Body;
        if (Sub != null) yield return Sub.Steps;
        foreach (var b in Branches) yield return b;
    }

    public static string KindName(StepKind k) => k switch
    {
        StepKind.Key => Lang.T("按鍵"),
        StepKind.Click => Lang.T("滑鼠"),
        StepKind.Wait => Lang.T("等待"),
        StepKind.If => Lang.T("IF 條件"),
        StepKind.Loop => Lang.T("循環"),
        StepKind.Break => Lang.T("中斷循環"),
        StepKind.Drag => Lang.T("拖拉"),
        StepKind.StopScript => Lang.T("停止腳本"),
        StepKind.Thread => Lang.T("新線程"),
        StepKind.Parallel => Lang.T("同時運行"),
        StepKind.System => Lang.T("系統"),
        StepKind.Text => Lang.T("輸入文字"),
        StepKind.Scroll => Lang.T("滑鼠"),
        _ => Lang.T("搵圖點擊")
    };

    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
    private static string UnB64(string s) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }

    public static string SysName(SysAction a) => a switch
    {
        SysAction.OpenApp => Lang.T("打開應用程式"),
        SysAction.CloseApp => Lang.T("關閉程式"),
        SysAction.SwitchWindow => Lang.T("切換視窗至"),
        SysAction.PlaySound => Lang.T("播放音效"),
        _ => Lang.T("關閉電腦")
    };

    public static string ModeName(PressMode m) => m switch { PressMode.Down => Lang.T("按下"), PressMode.Up => Lang.T("彈起"), _ => Lang.T("撳住") };

    private static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public string Serialize() => Kind switch
    {
        StepKind.Key => $"key|{Vk}|{D(Hold)}|{(int)Mods}|{(int)Mode}",
        StepKind.Click => $"click|{(Right ? "R" : "L")}|{X}|{Y}|{(int)Mode}|{D(Hold)}",
        StepKind.Wait => $"wait|{D(Seconds)}|{(Random ? 1 : 0)}|{D(Seconds2)}",
        StepKind.Text => "text|" + B64(Target),
        StepKind.Scroll => $"scroll|{X}|{Y}|{Count}",
        StepKind.If => "if|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((Sub ?? new IfBlock()).SerializeFull())),
        StepKind.Break => "break",
        StepKind.Drag => $"drag|{(Right ? "R" : "L")}|{X}|{Y}|{X2}|{Y2}|{D(Hold)}",
        StepKind.Loop => $"loop|{Count}|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", Body.Select(b => b.Serialize())))) + $"|0|{Until.Replace('|', ' ')}|{D(LimitSec)}|{(Collapsed ? 1 : 0)}",
        StepKind.StopScript => "stop",
        StepKind.System => $"sys|{(int)Sys}|{B64(Target)}|{B64(TargetName)}|{D(Seconds)}|{(Repeat ? 1 : 0)}",
        StepKind.Parallel => "par|~" + (Collapsed ? 1 : 0) + "|" + string.Join("|", Branches.Select(b =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", b.Select(x => x.Serialize())))))),
        StepKind.Thread => "thread|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Body.Select(b => b.Serialize())))),
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
                "wait" => new Step { Kind = StepKind.Wait, Seconds = Dec(p[1], 1m), Random = At(2) == "1", Seconds2 = Dec(At(3), 2m) },
                "text" => new Step { Kind = StepKind.Text, Target = UnB64(At(1)) },
                "scroll" => new Step { Kind = StepKind.Scroll, X = Int(At(1), 0), Y = Int(At(2), 0), Count = Int(At(3), -3) },
                "image" => new Step { Kind = StepKind.Image, Image = p[1], Similarity = Int(p[2], 80) },
                "break" => new Step { Kind = StepKind.Break },
                "stop" => new Step { Kind = StepKind.StopScript },
                "sys" => new Step
                {
                    Kind = StepKind.System, Sys = (SysAction)Math.Clamp(Int(At(1), 0), 0, 4),
                    Target = UnB64(At(2)), TargetName = UnB64(At(3)), Seconds = Dec(At(4), 60m), Repeat = At(5) == "1"
                },
                "par" => new Step
                {
                    Kind = StepKind.Parallel,
                    Collapsed = At(1) == "~1",
                    Branches = p.Skip(1).Where(x => !x.StartsWith("~")).Select(b64 => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64))
                        .Split('\n').Where(l => l.Length > 0).Select(Parse).Where(x => x != null).Select(x => x!).ToList()).ToList()
                },
                "thread" => new Step
                {
                    Kind = StepKind.Thread,
                    Body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(At(1)))
                        .Split('\n').Where(l => l.Length > 0).Select(Parse).Where(x => x != null).Select(x => x!).ToList()
                },
                "drag" => new Step
                {
                    Kind = StepKind.Drag, Right = p[1] == "R", X = Int(At(2), 0), Y = Int(At(3), 0),
                    X2 = Int(At(4), 0), Y2 = Int(At(5), 0), Hold = Dec(At(6), 0.5m)
                },
                "loop" => new Step
                {
                    Kind = StepKind.Loop, Count = Math.Max(0, Int(p[1], 2)),
                    Body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(At(2)))
                        .Split('\n').Where(l => l.Length > 0).Select(Parse).Where(x => x != null).Select(x => x!).ToList(),
                    // 舊版第 3 格係分鐘；新版用第 5 格（秒）
                    LimitSec = At(5).Length > 0 ? Math.Max(0m, Dec(At(5), 0m)) : Math.Max(0m, Dec(At(3), 0m)) * 60m,
                    Until = At(4).Trim(),
                    Collapsed = At(6) == "1"
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
/// <summary>新線程：一條同主線程同時行嘅步驟清單。</summary>
public sealed class ThreadBlock
{
    public string Id = Guid.NewGuid().ToString("N")[..6];
    public string Name = "";
    public List<Step> Steps = new();

    public ThreadBlock Clone() => new() { Id = Guid.NewGuid().ToString("N")[..6], Name = Name, Steps = Steps.Select(x => x.Clone()).ToList() };
}

/// <summary>「系統」步驟做嘅嘢。</summary>
public enum SysAction { OpenApp, CloseApp, SwitchWindow, Shutdown, PlaySound }

public enum CondKind { ImageFound, ImageNotFound, PixelColor, Time, Countdown, Elapsed }

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
    public decimal ElapsedSec = 1800m; // 時限：腳本運行咗幾多秒
    public decimal Interval = 1m;      // 幾耐檢查一次
    public bool Once;                  // 觸發一次就停
    public string Name = "";           // 標題（空就用「IF 條件 n」）
    public bool Collapsed;             // 編輯介面摺埋
    public bool ClickFound;            // 搵到圖：順手喺嗰個位置左鍵點一下
    public List<IfBlock> More = new(); // 額外條件（淨係用條件部分）
    public bool Or;                    // 幾個條件：true = 或者（任何一個成立）；false = 而且（全部成立）
    public List<Step> Steps = new();

    /// <summary>所有條件（第一個 + 額外）。</summary>
    public IEnumerable<IfBlock> AllConds => new[] { this }.Concat(More);

    public IfBlock Clone()
    {
        var c = (IfBlock)MemberwiseClone();
        c.Steps = Steps.Select(x => x.Clone()).ToList();
        c.More = More.Select(x => x.Clone()).ToList();
        return c;
    }

    public static string KindName(CondKind k) => k switch
    {
        CondKind.ImageFound => Lang.T("畫面搵到圖"),
        CondKind.ImageNotFound => Lang.T("畫面搵唔到圖"),
        CondKind.PixelColor => Lang.T("某位置係某顏色"),
        CondKind.Time => Lang.T("現實時間到咗"),
        CondKind.Elapsed => Lang.T("時限到（運行咗幾耐）"),
        _ => Lang.T("倒數完")
    };

    /// <summary>呢種條件要唔要設「每幾多秒檢查」。</summary>
    public bool UsesInterval => AllConds.Any(c => c.Kind is CondKind.ImageFound or CondKind.ImageNotFound or CondKind.PixelColor);

    private static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public string Serialize() =>
        $"{Id};kind={(int)Kind};img={Image};sim={Similarity};x={X};y={Y};color={Color};tol={Tolerance};" +
        $"time={Time};sec={D(Seconds)};elapsed={D(ElapsedSec)};interval={D(Interval)};once={(Once ? 1 : 0)};" +
        $"fold={(Collapsed ? 1 : 0)};click={(ClickFound ? 1 : 0)};or={(Or ? 1 : 0)};" +
        (More.Count > 0 ? "more=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", More.Select(m => m.Serialize())))) + ";" : "") +
        $"name={Name.Replace(';', '；').Replace('\n', ' ').Replace('\r', ' ')}";

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
                case "kind": b.Kind = (CondKind)Math.Clamp(I(0), 0, 5); break;
                case "img": b.Image = v; break;
                case "sim": b.Similarity = I(80); break;
                case "x": b.X = I(0); break;
                case "y": b.Y = I(0); break;
                case "color": b.Color = I(0xFF0000); break;
                case "tol": b.Tolerance = I(10); break;
                case "time": b.Time = v; break;
                case "sec": b.Seconds = Dm(60m); break;
                case "min": b.ElapsedSec = Dm(30m) * 60m; break; // 舊版：分鐘
                case "elapsed": b.ElapsedSec = Dm(1800m); break;
                case "interval": b.Interval = Dm(1m); break;
                case "once": b.Once = v == "1"; break;
                case "fold": b.Collapsed = v == "1"; break;
                case "click": b.ClickFound = v == "1"; break;
                case "name": b.Name = v; break;
                case "or": b.Or = v == "1"; break;
                case "more":
                    try
                    {
                        foreach (var ln in System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v)).Split('\n'))
                            if (ln.Length > 0 && Parse(ln) is { } m) b.More.Add(m);
                    }
                    catch { }
                    break;
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
    /// <summary>運行次數：1 = 一次，N = N 次，0 = 一直運行。</summary>
    public int Runs = 1;
    public bool Loop { get => Runs != 1; set => Runs = value ? 0 : 1; }

    public static string RunsName(int runs) => runs switch { 0 => Lang.T("一直運行"), 1 => Lang.T("運行一次"), _ => Lang.F("運行 {0} 次", runs) };
    public Hotkey Hotkey;
    public List<Step> Steps = new();
    public List<IfBlock> Ifs = new();
    public List<ThreadBlock> Threads = new(); // 新線程：同主線程同時行
    public bool Background;            // 背景執行：淨係喺揀咗嘅程式視窗入面截圖 / 點擊 / 按鍵
    public string TargetProcess = "";  // 背景程式（process 名）
    public string TargetTitle = "";    // 背景程式視窗標題
    public double Speed = 1.0;         // 運行速度倍率（0.5x – 3.0x）：等待 / 撳住 / 檢查間隔都除以佢
    public bool Relative;              // 座標係「相對程式視窗」（背景執行時用），唔係成個螢幕
    public long TargetHwnd;            // 揀咗嘅視窗（同一個程式開幾個時分得清；重開程式會變，變咗就用 TargetIndex）
    public int TargetIndex;            // 同名視窗入面第幾個
    public int RefW, RefH;             // 記錄座標時個視窗內容區嘅大小：運行時按而家嘅大小比例換算（0 = 未記錄）
    public bool FakeFocus;             // 背景執行：送輸入前先扮個視窗有焦點（部分遊戲要咁先收背景輸入）
    public bool Public;                // 公開到腳本庫（P2P）
    public string Desc = "";           // 腳本庫顯示嘅簡介

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
        name.Length > 0 && (StepsUse(Steps, name) || Threads.Any(t => StepsUse(t.Steps, name)) || Ifs.Any(b => b.AllConds.Any(c => ImageList.Has(c.Image, name)) || StepsUse(b.Steps, name)));

    private static bool StepsUse(IEnumerable<Step> steps, string name) =>
        steps.Any(s => ImageList.Has(s.Image, name)
                       || (s.Sub != null && (s.Sub.AllConds.Any(c => ImageList.Has(c.Image, name)) || StepsUse(s.Sub.Steps, name)))
                       || StepsUse(s.Body, name) || s.Branches.Any(b => StepsUse(b, name)));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(Path.Combine(Dir, "script.txt"), Lines());
        }
        catch (Exception ex) { AppPaths.LogError("儲存腳本", ex); }
    }

    /// <summary>用返之前嘅內容（復原用）：淨係換步驟 / IF / 線程，其他設定唔郁。</summary>
    public void RestoreSteps(List<string> lines)
    {
        var t = FromLines(lines, Folder);
        Steps = t.Steps;
        Ifs = t.Ifs;
        Threads = t.Threads;
    }

    /// <summary>刪走資料夾入面冇再用到嘅圖（離開修改頁時做，費事復原時圖已經冇咗）。</summary>
    public void CleanupImages()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")) continue;
                if (!UsesImage(Path.GetFileName(f))) try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public List<string> Lines()
    {
        {
            var lines = new List<string>
            {
                "name=" + Name.Replace('\n', ' ').Replace('\r', ' '),
                "mode=" + (Loop ? "loop" : "once"),
                "runs=" + Runs,
                "hotkey=" + Hotkey.Serialize(),
                "bg=" + (Background ? "1" : "0"),
                "bgproc=" + TargetProcess,
                "bgtitle=" + TargetTitle.Replace('\n', ' ').Replace('\r', ' '),
                "speed=" + Speed.ToString("0.0#", CultureInfo.InvariantCulture),
                "rel=" + (Relative ? "1" : "0"),
                "bghwnd=" + TargetHwnd,
                "bgidx=" + TargetIndex,
                "refw=" + RefW,
                "refh=" + RefH,
                "win=2",
                "pub=" + (Public ? "1" : "0"),
                "fakefocus=" + (FakeFocus ? "1" : "0"),
                "desc=" + Desc.Replace('\n', ' ').Replace('\r', ' ')
            };
            lines.AddRange(Steps.Select(s => "step=" + s.Serialize()));
            foreach (var t in Threads)
            {
                lines.Add($"thread={t.Id}|{t.Name.Replace('\n', ' ').Replace('\r', ' ').Replace('|', ' ')}");
                lines.AddRange(t.Steps.Select(s => $"tstep={t.Id}|{s.Serialize()}"));
            }
            foreach (var b in Ifs)
            {
                lines.Add("if=" + b.Serialize());
                lines.AddRange(b.Steps.Select(s => $"ifstep={b.Id}|{s.Serialize()}"));
            }
            return lines;
        }
    }

    public static Script? Load(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "script.txt");
            if (!File.Exists(file)) return null;
            return FromLines(File.ReadAllLines(file), Path.GetFileName(dir));
        }
        catch (Exception ex) { AppPaths.LogError("讀取腳本", ex); return null; }
    }

    internal static Script FromLines(IEnumerable<string> allLines, string folder)
    {
        {
            var sc = new Script { Folder = folder };
            bool win2 = false;
            foreach (var line in allLines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                switch (k)
                {
                    case "name": sc.Name = v; break;
                    case "mode": sc.Loop = v == "loop"; break;
                    case "runs": if (int.TryParse(v, out var rn) && rn >= 0) sc.Runs = rn; break;
                    case "hotkey": sc.Hotkey = Hotkey.Parse(v); break;
                    case "bg": sc.Background = v == "1"; break;
                    case "bgproc": sc.TargetProcess = v; break;
                    case "bgtitle": sc.TargetTitle = v; break;
                    case "rel": sc.Relative = v == "1"; break;
                    case "bghwnd": long.TryParse(v, out sc.TargetHwnd); break;
                    case "bgidx": int.TryParse(v, out sc.TargetIndex); break;
                    case "win": win2 = true; break;
                    case "pub": sc.Public = v == "1"; break;
                    case "fakefocus": sc.FakeFocus = v == "1"; break;
                    case "desc": sc.Desc = v; break;
                    case "refw": int.TryParse(v, out sc.RefW); break;
                    case "refh": int.TryParse(v, out sc.RefH); break;
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
                    case "thread":
                    {
                        int tb = v.IndexOf('|');
                        sc.Threads.Add(tb < 0 ? new ThreadBlock { Id = v } : new ThreadBlock { Id = v[..tb], Name = v[(tb + 1)..] });
                        break;
                    }
                    case "tstep":
                    {
                        int tb = v.IndexOf('|');
                        if (tb < 0) break;
                        var towner = sc.Threads.FirstOrDefault(x => x.Id == v[..tb]);
                        var tst = Step.Parse(v[(tb + 1)..]);
                        if (towner != null && tst != null) towner.Steps.Add(tst);
                        break;
                    }
                    case "ifstep":
                        int bar = v.IndexOf('|');
                        if (bar < 0) break;
                        var owner = sc.Ifs.FirstOrDefault(x => x.Id == v[..bar]);
                        var ist = Step.Parse(v[(bar + 1)..]);
                        if (owner != null && ist != null) owner.Steps.Add(ist);
                        break;
                }
            }
            // 舊版：閂咗背景執行之後仲留住個程式名（當時前景唔理視窗）。新版前景都會用揀咗嘅視窗，所以舊嘢清走
            if (!win2 && !sc.Background && !sc.Relative)
            {
                sc.TargetProcess = ""; sc.TargetTitle = ""; sc.TargetHwnd = 0; sc.TargetIndex = 0;
            }
            return sc;
        }
    }

    /// <summary>淨係讀公開咗嘅腳本（分享伺服器用；唔會改資料夾名，背景 thread 用都安全）。</summary>
    public static List<Script> LoadPublic()
    {
        var list = new List<Script>();
        try
        {
            if (!Directory.Exists(AppPaths.Scripts)) return list;
            foreach (var d in Directory.GetDirectories(AppPaths.Scripts))
                if (Load(d) is { Public: true } sc) list.Add(sc);
        }
        catch { }
        return list;
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
    private static readonly Dictionary<string, Script> RunningScripts = new();

    /// <summary>而家行緊嘅前景腳本（同一時間只可以有一個）。</summary>
    public static Script? RunningForeground => RunningScripts.Values.FirstOrDefault(x => !x.Background);

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

    /// <summary>目標程式用管理員權限開：停腳本，問用戶要唔要用管理員身份重開 MultiTool。</summary>
    private static void NeedAdmin(Script s, string proc) => Post(() =>
    {
        if (IsRunning(s)) Stop(s, Lang.F("「{0}」用咗管理員權限運行，點擊同按鍵被 Windows 擋住", proc));
        if (_dialogOpen || _ui == null) return;
        _dialogOpen = true;
        try
        {
            if (MessageBox.Show(_ui.FindForm(), Lang.F("「{0}」用咗管理員權限運行，Windows 唔准普通權限嘅程式控制佢，所以點擊同按鍵冇效。\n\n要唔要用管理員身份重新開 MultiTool？（運行中嘅腳本會停止）", proc),
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                StopAll();
                if (Elevation.RestartAsAdmin()) Application.Exit();
            }
        }
        finally { _dialogOpen = false; }
    });

    public static void Start(Script s)
    {
        if (IsRunning(s) || _ui == null) return;
        if (s.Steps.Count == 0 && s.Ifs.Count == 0 && s.Threads.Count == 0)
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
        // 前景腳本會用真滑鼠鍵盤：同一時間只可以行一個
        if (!s.Background && RunningForeground is { } fg && fg != s)
        {
            Status?.Invoke(s.Id, Lang.T("已經有前景腳本運行緊，未開始"));
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                MessageBox.Show(_ui.FindForm(), Lang.F("「{0}」運行緊。前景執行嘅腳本會用真嘅滑鼠同鍵盤，同一時間只可以行一個。\n\n請先停咗佢，或者將其中一個改做背景執行。", fg.Name),
                    Lang.T("未能開始"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _dialogOpen = false; }
            return;
        }
        var cts = new CancellationTokenSource();
        Running[s.Id] = cts;
        RunningScripts[s.Id] = s;
        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(s, cts.Token));
    }

    /// <summary>搵出所有未設定好嘅地方（冇圖、圖檔唔見咗、按鍵未設定、空嘅循環）。</summary>
    public static List<string> Validate(Script s)
    {
        var errs = new List<string>();
        if (s.Background && s.TargetProcess.Length == 0) errs.Add(Lang.T("背景執行：未揀程式"));
        else if (s.TargetProcess.Length > 0 && Win.Find(s) == 0)
            errs.Add(Lang.F("搵唔到視窗「{0} - {1}」，請先開咗個程式或者重新揀", s.TargetProcess, s.TargetTitle));
        CheckSteps(s, s.Steps, Lang.T("主線程"), errs);
        foreach (var t in s.Threads)
        {
            if (t.Steps.Count == 0) errs.Add(Lang.F("{0}：冇步驟", t.Name));
            CheckSteps(s, t.Steps, t.Name, errs);
        }
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
        foreach (var m in b.More) CheckOne(s, m, where, errs);
        CheckOne(s, b, where, errs);
    }

    private static void CheckOne(Script s, IfBlock b, string where, List<string> errs)
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
                case StepKind.System:
                    if (st.Sys != SysAction.Shutdown && st.Target.Length == 0) errs.Add(Lang.F("{0}：未揀程式 / 視窗", where));
                    else if (st.Sys == SysAction.PlaySound && !File.Exists(st.Target)) errs.Add(Lang.F("{0}：搵唔到音效檔", where));
                    break;
                case StepKind.Text:
                    if (st.Target.Length == 0) errs.Add(Lang.F("{0}：未輸入文字", where));
                    break;
                case StepKind.Thread:
                    if (st.Body.Count == 0) errs.Add(Lang.F("{0}：新線程入面冇步驟", where));
                    CheckSteps(s, st.Body, where, errs);
                    break;
                case StepKind.Parallel:
                    for (int b = 0; b < st.Branches.Count; b++)
                    {
                        string bw = Lang.F("{0} › 支線 {1}", where, b + 1);
                        if (st.Branches[b].Count == 0) errs.Add(Lang.F("{0}：支線入面冇步驟", bw));
                        CheckSteps(s, st.Branches[b], bw, errs);
                    }
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
        RunningScripts.Remove(s.Id);
        SoundFx.StopAll(s.Id);
        if (!Running.Remove(s.Id, out var cts)) return;
        try { cts.Cancel(); } catch { }
        Status?.Invoke(s.Id, text ?? Lang.T("已停止"));
        Changed?.Invoke();
    }

    public static bool AnyRunning => Running.Count > 0;

    /// <summary>緊急停止：停晒所有運行緊嘅腳本（會更新狀態同介面）。</summary>
    public static void StopEverything(string reason)
    {
        foreach (var sc in RunningScripts.Values.ToList()) Stop(sc, reason);
        StopAll();
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
        var (bg, proc, title, rel) = OnUi(() => (s.Background, s.TargetProcess, s.TargetTitle, s.Relative));
        if (rel && proc.Length > 0) OnUi(() => { ScriptCoords.EnsureRef(s); return 0; });
        var refSize = OnUi(() => new Size(s.RefW, s.RefH));
        if (!bg)
        {
            // 前景：有揀視窗就跟住個視窗計座標（視窗移咗位 / 改咗大細都啱）
            if (proc.Length == 0) return new RunCtx(OwnWindow(), 0, false);
            var fh = OnUi(() => Win.Find(s));
            if (fh == 0) throw new InvalidOperationException(Lang.F("搵唔到視窗（{0} - {1}），佢係咪閂咗？", proc, title));
            return new RunCtx(OwnWindow(), 0, rel, fh, refSize);
        }
        var h = OnUi(() => Win.Find(s));
        if (h == 0) throw new InvalidOperationException(Lang.F("搵唔到背景程式視窗（{0} - {1}），佢係咪閂咗？", proc, title));
        // 背景執行一律自動用假焦點：唔理呢啲訊息嘅程式冇影響，要佢先收背景輸入嘅遊戲就會得
        return new RunCtx(null, h, rel, 0, refSize) { Fake = true };
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

    // 腳本幾時開始行（時限條件用；跟住 async 流程走）
    private static readonly AsyncLocal<DateTime> StartedAt = new();
    // 「新線程」開出嚟嘅支線（行一次嘅腳本要等埋佢哋行完先算完成）
    private static readonly AsyncLocal<List<Task>?> Branches = new();
    private static string Hms(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    private static TimeSpan RunTime => StartedAt.Value == default ? TimeSpan.Zero : DateTime.Now - StartedAt.Value;

    /// <summary>「停止腳本」步驟：停成個腳本（主線程 + 所有 IF 框）。</summary>
    private sealed class ScriptStop : Exception { }

    private static async Task RunAsync(Script s, CancellationToken ct)
    {
        Current.Value = s;
        StartedAt.Value = DateTime.Now;
        var branches = new List<Task>();
        Branches.Value = branches;
        // 新線程：一開始就同主線程一齊行
        foreach (var tid in OnUi(() => s.Threads.Select(t => t.Id).ToList()))
            branches.Add(Task.Run(() => RunThreadAsync(s, tid, ct)));
        int round = 0;
        // 前景 + 揀咗視窗：先切去嗰個視窗先開始
        var (isBg, hasWin) = OnUi(() => (s.Background, s.TargetProcess.Length > 0));
        if (!isBg && hasWin)
        {
            var wh = OnUi(() => Win.Find(s));
            if (wh != 0)
            {
                Report(s, Lang.T("切去目標視窗…"));
                Win.Activate(wh);
                try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }
            }
        }
        var held = new Held();
        // 每個 IF 框自己一條線
        var ifIds = OnUi(() => s.Ifs.Select(b => b.Id).ToList());
        var ifTasks = ifIds.Select(id => Task.Run(() => RunIfAsync(s, id, ct))).ToList();
        try
        {
            if (OnUi(() => s.Steps.Count) == 0 && branches.Count == 0)
            {
                // 冇主步驟：淨係行 IF 框，直到撳停止
                Report(s, Lang.T("監察緊 IF 條件…"));
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (OnUi(() => s.Steps.Count) > 0)
            do
            {
                round++;
                // 每輪開頭喺 UI thread 抄一份步驟，運行中改都唔會撞
                var (steps, dir, runs) = OnUi(() => (s.Steps.Select(x => x.Clone()).ToList(), s.Dir, s.Runs));
                var exclude = MakeCtx(s);
                held.Ctx = exclude;
                for (int i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(s, Lang.F("{0}步驟 {1}/{2}：{3}", RoundText(round, runs), i + 1, steps.Count, Step.KindName(steps[i].Kind)));
                    try { await RunStep(steps[i], dir, exclude, held, ct); }
                    catch (LoopBreak) { } // 唔喺循環入面嘅中斷：當冇事
                }
                if (runs > 0 && round >= runs) break;
                await Task.Delay(50, ct); // 一直行時唔好食晒 CPU
            } while (!ct.IsCancellationRequested);
            // 等埋新線程行完
            while (true)
            {
                Task[] pending;
                lock (branches) pending = branches.Where(t => !t.IsCompleted).ToArray();
                if (pending.Length == 0) break;
                Report(s, Lang.F("主線程完成，等緊 {0} 條新線程…", pending.Length));
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(Timeout.Infinite, ct));
                ct.ThrowIfCancellationRequested();
            }
            Post(() => { if (IsRunning(s)) Stop(s, Lang.F("{0:HH:mm:ss} 完成", DateTime.Now)); });
        }
        catch (OperationCanceledException) { }
        catch (ScriptStop) { }
        catch (NeedAdminException na) { NeedAdmin(s, na.Process); }
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

    /// <summary>「系統」步驟：開 / 閂程式、切換視窗、關機。</summary>
    private static async Task RunSystem(Step st, CancellationToken ct)
    {
        switch (st.Sys)
        {
            case SysAction.OpenApp:
                MultiTool.Ai.PcActions.OpenPath(st.Target);
                await Task.Delay(500, ct);
                break;
            case SysAction.CloseApp:
            {
                string name = Path.GetFileNameWithoutExtension(st.Target);
                var procs = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
                // 等佢自己閂（最多 3 秒），閂唔到就強制結束
                var until = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < until && procs.Any(p => { try { return !p.HasExited; } catch { return false; } }))
                    await Task.Delay(200, ct);
                foreach (var p in procs) { try { if (!p.HasExited) p.Kill(true); } catch { } finally { p.Dispose(); } }
                break;
            }
            case SysAction.SwitchWindow:
            {
                var wins = OnUi(() => Win.List());
                var w = wins.FirstOrDefault(x => string.Equals(x.Process, st.Target, StringComparison.OrdinalIgnoreCase) && x.Title == st.TargetName)
                        ?? wins.FirstOrDefault(x => string.Equals(x.Process, st.Target, StringComparison.OrdinalIgnoreCase));
                if (w == null) throw new InvalidOperationException(Lang.F("搵唔到視窗（{0}），佢係咪閂咗？", st.Target));
                Win.Activate(w.Handle);
                await Task.Delay(300, ct);
                break;
            }
            case SysAction.PlaySound:
            {
                var me = Current.Value;
                OnUi(() => { SoundFx.Play(me?.Id ?? "", st.Target, st.Repeat); return 0; });
                break;
            }
            case SysAction.Shutdown:
            {
                int sec = (int)Math.Clamp(st.Seconds, 0m, 3600m);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", $"/s /t {sec}") { UseShellExecute = false, CreateNoWindow = true });
                break;
            }
        }
    }

    private static string RoundText(int round, int runs) =>
        runs == 1 ? "" : runs == 0 ? Lang.F("第 {0} 輪 · ", round) : Lang.F("第 {0}/{1} 輪 · ", round, runs);

    /// <summary>一條新線程：同主線程同時行；腳本「一直行」就一直重複，「行一次」就行一次。</summary>
    private static async Task RunThreadAsync(Script s, string tid, CancellationToken ct)
    {
        var held = new Held();
        string key = s.Id + "/t:" + tid;
        int round = 0;
        try
        {
            do
            {
                round++;
                var (steps, dir, runs) = OnUi(() => (s.Threads.FirstOrDefault(x => x.Id == tid)?.Steps.Select(x => x.Clone()).ToList(), s.Dir, s.Runs));
                if (steps == null || steps.Count == 0) return;
                var ctx = MakeCtx(s);
                held.Ctx = ctx;
                for (int i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(key, Lang.F("{0}步驟 {1}/{2}：{3}", RoundText(round, runs), i + 1, steps.Count, Step.KindName(steps[i].Kind)));
                    try { await RunStep(steps[i], dir, ctx, held, ct); }
                    catch (LoopBreak) { }
                }
                if (runs > 0 && round >= runs) break;
                await Task.Delay(50, ct);
            } while (!ct.IsCancellationRequested);
            Report(key, Lang.F("{0:HH:mm:ss} 完成", DateTime.Now));
        }
        catch (OperationCanceledException) { }
        catch (ScriptStop) { }
        catch (NeedAdminException na) { NeedAdmin(s, na.Process); }
        catch (Exception ex)
        {
            AppPaths.LogError("新線程", ex);
            Post(() => { if (IsRunning(s)) Stop(s, Lang.T("新線程出錯：") + ex.Message); });
        }
        finally { try { held.ReleaseAll(); } catch { } }
    }

    /// <summary>即刻檢查（IF 入面嘅 IF 用）：全部條件按「而且 / 或者」計；成立先做「搵到就點擊」。</summary>
    private static bool CheckNow(IfBlock b, string dir, RunCtx exclude, CancellationToken ct)
    {
        var clicks = new List<Point>();
        bool ok = b.Or ? false : true;
        foreach (var c in b.AllConds)
        {
            bool r = Eval(c, dir, exclude, ct, out var hit);
            if (r && hit != null && c.Kind == CondKind.ImageFound && c.ClickFound) clicks.Add(hit.Value);
            if (b.Or && r) { ok = true; break; }
            if (!b.Or && !r) { ok = false; break; }
        }
        if (ok && clicks.Count > 0) exclude.Click(clicks[0].X, clicks[0].Y, false);
        return ok;
    }

    /// <summary>檢查一個條件（唔點擊；搵到圖就回傳位置）。倒數喺度永遠唔成立。</summary>
    private static bool Eval(IfBlock b, string dir, RunCtx exclude, CancellationToken ct, out Point? hitAt)
    {
        hitAt = null;
        switch (b.Kind)
        {
            case CondKind.ImageFound:
            case CondKind.ImageNotFound:
            {
                var paths = ImageList.Split(b.Image).Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
                if (paths.Count == 0) return false;
                var hit = exclude.FindAny(paths, b.Similarity / 100.0, ct);
                hitAt = hit;
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
            case CondKind.Elapsed:
                return RunTime.TotalSeconds >= (double)b.ElapsedSec;
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
        DateTime lastFire = default;
        var timeState = new Dictionary<int, (string Text, DateTime? Next)>();
        var elapsedDone = new HashSet<int>(); // 幾個條件時：每個「現實時間」條件自己記
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 每次都攞最新設定（運行中改都得）；個 IF 被刪咗就收工
                var b = OnUi(() => s.Ifs.FirstOrDefault(x => x.Id == ifId)?.Clone());
                if (b == null) return;
                var dir = OnUi(() => s.Dir);
                bool ok = false;
                if (b.More.Count > 0)
                {
                    // 幾個條件：逐個計，按「而且 / 或者」合埋
                    var ctx = MakeCtx(s);
                    var conds = b.AllConds.ToList();
                    var results = new bool[conds.Count];
                    Point? clickAt = null;
                    for (int i = 0; i < conds.Count; i++)
                    {
                        var c = conds[i];
                        bool r;
                        switch (c.Kind)
                        {
                            case CondKind.Time:
                            {
                                var (txt, nxt) = timeState.TryGetValue(i, out var st0) ? st0 : ("", (DateTime?)null);
                                if (c.Time != txt || nxt == null)
                                {
                                    nxt = TimeSpan.TryParse(c.Time, CultureInfo.InvariantCulture, out var ts2) ? DateTime.Today + ts2 : null;
                                    if (nxt != null && nxt <= DateTime.Now) nxt = nxt.Value.AddDays(1);
                                    timeState[i] = (c.Time, nxt);
                                }
                                r = nxt != null && DateTime.Now >= nxt;
                                break;
                            }
                            case CondKind.Countdown:
                                r = ((DateTime.Now - countdownStart).TotalSeconds * SpeedNow) >= (double)c.Seconds;
                                break;
                            case CondKind.Elapsed:
                                r = !elapsedDone.Contains(i) && RunTime.TotalSeconds >= (double)c.ElapsedSec; // 時限：只用一次
                                break;
                            default:
                                r = Eval(c, dir, ctx, ct, out var hit);
                                if (r && hit != null && c.Kind == CondKind.ImageFound && c.ClickFound && clickAt == null) clickAt = hit;
                                break;
                        }
                        results[i] = r;
                        if (b.Or && r) break;
                        if (!b.Or && !r) break;
                    }
                    ok = b.Or ? results.Any(x => x) : results.All(x => x);
                    if (ok)
                    {
                        if (clickAt != null) ctx.Click(clickAt.Value.X, clickAt.Value.Y, false);
                        // 現實時間：觸發咗就等聽日
                        for (int i = 0; i < conds.Count; i++)
                            if (results[i] && conds[i].Kind == CondKind.Elapsed) elapsedDone.Add(i);
                        for (int i = 0; i < conds.Count; i++)
                            if (results[i] && conds[i].Kind == CondKind.Time && timeState.TryGetValue(i, out var ts3) && ts3.Next != null)
                                timeState[i] = (ts3.Text, ts3.Next.Value.AddDays(1));
                    }
                    else if (fired == 0)
                        Report(key, Lang.F("檢查緊 {0} 個條件（{1}）", conds.Count, b.Or ? Lang.T("或者") : Lang.T("而且")));
                }
                else switch (b.Kind)
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
                    case CondKind.Elapsed:
                    {
                        // 時限：腳本運行咗 N 分鐘就觸發（只觸發一次）
                        if (fired > 0) { Report(key, Lang.F("時限已到（{0:HH:mm:ss} 觸發咗）", lastFire)); break; }
                        var left = TimeSpan.FromSeconds((double)b.ElapsedSec) - RunTime;
                        if (left <= TimeSpan.Zero) ok = true;
                        else Report(key, Lang.F("時限仲有 {0}", Hms(left)));
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
                    lastFire = DateTime.Now;
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
        catch (ScriptStop) { }
        catch (NeedAdminException na) { NeedAdmin(s, na.Process); }
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
                var pt = exclude.Pt(st.X, st.Y);
                await Press(exclude, pt.X, pt.Y, st.Right, st.Mode, st.Hold, held, ct);
            }
                break;
            case StepKind.Wait:
            {
                double sec = (double)st.Seconds;
                if (st.Random)
                {
                    double a = Math.Min((double)st.Seconds, (double)st.Seconds2), b = Math.Max((double)st.Seconds, (double)st.Seconds2);
                    sec = a + System.Random.Shared.NextDouble() * (b - a);
                }
                await Task.Delay(Scaled(sec), ct);
                break;
            }
            case StepKind.Text:
                exclude.TypeText(st.Target, ct);
                break;
            case StepKind.Scroll:
            {
                var pt = exclude.Pt(st.X, st.Y);
                exclude.Wheel(pt.X, pt.Y, st.Count);
                break;
            }
            case StepKind.Drag:
                await exclude.DragAsync(exclude.Pt(st.X, st.Y), exclude.Pt(st.X2, st.Y2), st.Right, Scaled((double)st.Hold), ct);
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
            {
                // 循環：入面嘅步驟重複 Count 次（0 = 一直重複到停止）
                // 時限 / 指定時間：到鐘即刻停（連入面嘅循環、延遲都即刻停）
                DateTime? deadline = null;
                if (st.LimitSec > 0) deadline = DateTime.Now.AddSeconds((double)st.LimitSec);
                if (st.Until.Length > 0 && TimeSpan.TryParse(st.Until, CultureInfo.InvariantCulture, out var ut))
                {
                    var at = DateTime.Today + ut;
                    if (at <= DateTime.Now) at = at.AddDays(1);
                    if (deadline == null || at < deadline) deadline = at;
                }
                using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (deadline != null) loopCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (deadline.Value - DateTime.Now).TotalMilliseconds)));
                var lct = loopCts.Token;
                // 記低循環開始前已經撳住嘅鍵 / 掣：循環完結時淨係彈起循環入面撳落嘅
                var keysBefore = new HashSet<int>(held.Keys);
                bool leftBefore = held.Left, rightBefore = held.Right;
                try
                {
                    for (int n = 0; st.Count == 0 || n < st.Count; n++)
                    {
                        foreach (var sub in st.Body)
                        {
                            lct.ThrowIfCancellationRequested();
                            await RunStep(sub, dir, exclude, held, lct);
                        }
                        if (st.Body.Count == 0) await Task.Delay(50, lct);
                    }
                }
                catch (LoopBreak) { } // 中斷循環：直接做循環之後嘅步驟
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // 時限到：做循環之後嘅步驟
                finally
                {
                    // 循環完結：入面撳落咗未彈起嘅鍵 / 滑鼠掣全部彈返起
                    foreach (var vk in held.Keys.Where(k => !keysBefore.Contains(k)).ToList())
                    {
                        try { exclude.KeyUp(vk, false); } catch { }
                        held.Keys.Remove(vk);
                    }
                    if (held.Left && !leftBefore) { try { exclude.MouseUp(held.LastMouse.X, held.LastMouse.Y, false, move: false); } catch { } held.Left = false; }
                    if (held.Right && !rightBefore) { try { exclude.MouseUp(held.LastMouse.X, held.LastMouse.Y, true, move: false); } catch { } held.Right = false; }
                }
                break;
            }
            case StepKind.Break:
                throw new LoopBreak();
            case StepKind.System:
                await RunSystem(st, ct);
                break;
            case StepKind.Parallel:
            {
                // 同時運行：每條支線各自一條線一齊行，全部行完先做下一步；其中一條出錯就停晒
                using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pct = pcts.Token;
                var tasks = st.Branches.Select(br => br.Select(x => x.Clone()).ToList()).Select(br => Task.Run(async () =>
                {
                    var h = new Held { Ctx = exclude };
                    try
                    {
                        foreach (var sub in br)
                        {
                            pct.ThrowIfCancellationRequested();
                            await RunStep(sub, dir, exclude, h, pct);
                        }
                    }
                    catch (LoopBreak) { } // 支線入面嘅中斷：淨係結束呢條支線
                    catch (OperationCanceledException) { throw; }
                    catch { pcts.Cancel(); throw; }
                    finally { try { h.ReleaseAll(); } catch { } }
                })).ToList();
                try { await Task.WhenAll(tasks); } catch { }
                ct.ThrowIfCancellationRequested();
                var err = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException).FirstOrDefault(x => x is not OperationCanceledException);
                if (err != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(err).Throw();
                break;
            }
            case StepKind.Thread:
            {
                // 新線程：開一條支線同時行入面嘅步驟，呢度唔使等
                var me = Current.Value;
                var body = st.Body.Select(x => x.Clone()).ToList();
                var t = Task.Run(async () =>
                {
                    var h = new Held { Ctx = exclude };
                    try
                    {
                        foreach (var sub in body)
                        {
                            ct.ThrowIfCancellationRequested();
                            await RunStep(sub, dir, exclude, h, ct);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (LoopBreak) { }
                    catch (ScriptStop) { }
                    catch (NeedAdminException na) { if (me != null) NeedAdmin(me, na.Process); }
                    catch (Exception ex)
                    {
                        AppPaths.LogError("新線程", ex);
                        if (me != null) Post(() => { if (IsRunning(me)) Stop(me, Lang.T("新線程出錯：") + ex.Message); });
                    }
                    finally { try { h.ReleaseAll(); } catch { } }
                });
                var list = Branches.Value;
                if (list != null) lock (list) { list.RemoveAll(x => x.IsCompleted); list.Add(t); }
                break;
            }
            case StepKind.StopScript:
            {
                var me = Current.Value;
                if (me != null) Post(() => { if (IsRunning(me)) Stop(me, Lang.F("{0:HH:mm:ss} 停止腳本", DateTime.Now)); });
                throw new ScriptStop();
            }
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
        var from = held.LastMouse;
        bool holding = right ? held.Right : held.Left;
        held.LastMouse = new Point(x, y);
        // 拖拉：之前喺 A「按下」咗，而家喺 B「彈起」→ 撳住慢慢移去 B 先放（背景都得）
        if (mode == PressMode.Up && holding && from != new Point(x, y))
        {
            await ctx.DragAsync(from, new Point(x, y), right, Scaled(0.15), ct, pressAtStart: false);
            if (right) held.Right = false; else held.Left = false;
            return;
        }
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

    /// <summary>而家嘅「我的腳本」頁（首頁「腳本庫」前往用）。</summary>
    public static ScriptsPage? Instance { get; private set; }

    private FancyButton? _libBtn;
    private Panel? _libView;

    /// <summary>打開腳本庫（喺「我的腳本」頁入面，上面有返回掣）。</summary>
    public void OpenLibrary()
    {
        if (_libView == null || _libView.IsDisposed)
        {
            var wrap = new Panel { BackColor = Ui.Bg };
            var lib = LibraryPage.Build();
            lib.Dock = DockStyle.Fill;
            var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(56), BackColor = Ui.Bg };
            var back = new FancyButton { Text = Lang.T("← 返回我的腳本"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Location = new Point(Ui.S(24), Ui.S(16)) };
            back.Size = new Size(back.GetPreferredSize(Size.Empty).Width, Ui.S(34));
            back.Click += (_, _) => ShowView(null);
            var title = Ui.Text_(Lang.T("腳本庫"), Ui.Text, Ui.F(13f, FontStyle.Bold), new Padding(0));
            title.Location = new Point(back.Right + Ui.S(16), Ui.S(20));
            top.Controls.Add(back);
            top.Controls.Add(title);
            wrap.Controls.Add(lib);
            wrap.Controls.Add(top);
            _libView = wrap;
        }
        ShowView(_libView);
    }

    private void PlaceLibButton()
    {
        if (_libBtn == null) return;
        _libBtn.Location = new Point(ClientSize.Width - Ui.S(28) - _libBtn.Width, ClientSize.Height - Ui.S(24) - _libBtn.Height);
        _libBtn.Visible = _editor == null;
        if (_libBtn.Visible) _libBtn.BringToFront();
    }

    private ScriptsPage()
    {
        Instance = this;
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        ScriptRunner.Init(this);
        EmergencyStop.Init();

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
            _grid.Selected.Remove(sc);
            UpdateSelButton();
            _grid.Refresh2();
        };
        _grid.CopyRequested += sc =>
        {
            var copy = new Script
            {
                Name = sc.Name + Lang.T(" (副本)"), Runs = sc.Runs, Steps = sc.Steps.Select(x => x.Clone()).ToList(),
                Background = sc.Background, TargetProcess = sc.TargetProcess, TargetTitle = sc.TargetTitle, Speed = sc.Speed,
                Relative = sc.Relative, TargetHwnd = sc.TargetHwnd, TargetIndex = sc.TargetIndex, RefW = sc.RefW, RefH = sc.RefH,
                Ifs = sc.Ifs.Select(x => x.Clone()).ToList(),
                Threads = sc.Threads.Select(x => x.Clone()).ToList()
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

        // ---- 上面一行：開始剔咗嘅腳本 / 全部停止（+ 快捷鍵）----
        _startSel = new FancyButton { Kind = BtnKind.Success, Font = Ui.F(9.75f) };
        _startSel.Click += (_, _) => StartSelected();
        var stopAll = new FancyButton { Text = Lang.T("⏹ 全部停止"), Kind = BtnKind.Danger, Font = Ui.F(9.75f) };
        _stopAll = stopAll;
        stopAll.Size = new Size(Math.Max(Ui.S(110), stopAll.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        stopAll.Click += (_, _) => StopAllScripts();
        _stopAllKey = new HotkeyBox();
        HotkeyHub.Bind(_stopAllKey, "scripts.stopall");
        HotkeyHub.Get("scripts.stopall").Action = () =>
        {
            // 同一粒快捷鍵：有嘢行緊就全部停；冇就開始剔咗嘅腳本
            if (_handling) return;
            _handling = true;
            try
            {
                if (_scripts.Any(ScriptRunner.IsRunning)) StopAllScripts();
                else StartSelected();
            }
            finally { _handling = false; }
        };
        var stopKeyHost = new InputHost(_stopAllKey, Ui.S(130));
        var stopKeyLabel = Ui.Text_(Lang.T("開始已選 / 全部停止 快捷鍵"), Ui.TextDim, Ui.F(9.5f), new Padding(0));
        _grid.SelectionChanged += UpdateSelButton;
        UpdateSelButton();
        var monitor = new FancyButton { Text = Lang.T("📺 監視模式"), Kind = BtnKind.Primary, Font = Ui.F(9.75f) };
        monitor.Size = new Size(Math.Max(Ui.S(110), monitor.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        monitor.Click += (_, _) => OpenMonitor();
        var bar = new BareRow { Padding = new Padding(0) };
        bar.SetContent(new FlowRow(new Control[] { _startSel, stopAll, monitor }, new Control[] { stopKeyHost, stopKeyLabel }));
        _listView.AddRow(bar);

        var card = new CardPanel { Title = Lang.T("我的腳本"), Hint = Lang.T("撳「+」新增腳本；撳腳本睇預覽，再撳「修改」入去改；右鍵可以複製 / 刪除。入咗預覽頁先可以用快捷鍵開始 / 停止。") + Lang.T("剔右上角個格可以揀幾個腳本一齊開始。") };
        card.SetContent(_grid);
        _listView.AddRow(card);
        _listView.Dock = DockStyle.Fill;
        Controls.Add(_listView);

        // 右下角：腳本庫（搜尋 / 下載其他人公開嘅腳本）
        _libBtn = new FancyButton { Text = Lang.T("腳本庫 →"), Kind = BtnKind.Primary, Font = Ui.F(10f, FontStyle.Bold) };
        _libBtn.Size = new Size(Math.Max(Ui.S(120), _libBtn.GetPreferredSize(Size.Empty).Width), Ui.S(40));
        _libBtn.Click += (_, _) => OpenLibrary();
        Controls.Add(_libBtn);
        Resize += (_, _) => PlaceLibButton();
        PlaceLibButton();

        ScriptRunner.Changed += () => { _grid.Invalidate(); UpdateStopAll(); };
        UpdateStopAll();

        // 全部腳本嘅快捷鍵（頁未打開都有效）
        _poll.Tick += (_, _) =>
        {
            var form = FindForm();
            bool typing = HotkeyBox.Busy;
            if (_handling) return; // 彈緊提示視窗（modal 期間 timer 照跳）：唔好再觸發
            if (!MultiTool.Extensions.IsInstalled(MultiTool.Extensions.Scripts)) return; // 「我的腳本」已移除：快捷鍵唔生效
            // 全部停止快捷鍵：喺邊度都用得
            // 「開始已選 / 全部停止」快捷鍵由 HotkeyHub 處理
            foreach (var sc in _scripts.ToList())
            {
                // 喺其他頁（AI 助手、錄影工具…）或者最少化：所有腳本快捷鍵都用得；
                // 喺「我的腳本」頁入面就淨係預覽緊嗰個可以開始（費事改緊其他腳本時誤觸）；行緊嘅腳本喺邊度都可以停
                bool viewing = Visible && (_editor is ScriptViewer v && v.Script == sc || _editor is MonitorView);
                bool usable = viewing || (!Visible && HotkeyHub.EffectiveGlobal("scripts.each")) || ScriptRunner.IsRunning(sc);
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
        // 由腳本庫下載咗新腳本：返嚟呢頁時加入清單
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            try
            {
                var known = new HashSet<string>(_scripts.Select(x => x.Folder), StringComparer.OrdinalIgnoreCase);
                bool added = false;
                foreach (var d in Directory.Exists(AppPaths.Scripts) ? Directory.GetDirectories(AppPaths.Scripts) : Array.Empty<string>())
                {
                    if (known.Contains(Path.GetFileName(d))) continue;
                    if (Script.Load(d) is { } sc) { _scripts.Add(sc); added = true; }
                }
                if (added) _grid.Refresh2();
            }
            catch { }
        };
    }

    private FancyButton _startSel = null!;
    private FancyButton _stopAll = null!;

    /// <summary>冇腳本行緊就灰咗「全部停止」。</summary>
    private void UpdateStopAll()
    {
        _stopAll.Enabled = _scripts.Any(ScriptRunner.IsRunning);
        _stopAll.Invalidate();
    }
    private HotkeyBox _stopAllKey = null!;
    private bool _stopWasDown;

    private void UpdateSelButton()
    {
        _grid.Selected.RemoveWhere(sc => !_scripts.Contains(sc));
        int n = _grid.Selected.Count;
        _startSel.Text = Lang.F("▶ 開始已選（{0}）", n);
        _startSel.Enabled = n > 0;
        _startSel.Size = new Size(Math.Max(Ui.S(130), _startSel.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        _startSel.Invalidate();
        _startSel.Parent?.PerformLayout();
    }

    /// <summary>一次過開始剔咗嘅腳本（各自跟自己「行一次 / 一直行」設定）。</summary>
    private void StartSelected()
    {
        var sel = _scripts.Where(_grid.Selected.Contains).Where(sc => !ScriptRunner.IsRunning(sc)).ToList();
        if (sel.Count == 0) return;
        // 前景腳本用真滑鼠鍵盤：同一時間只可以有一個
        var fgs = sel.Where(sc => !sc.Background).Select(sc => sc.Name).ToList();
        if (ScriptRunner.RunningForeground is { } running) fgs.Insert(0, running.Name);
        if (fgs.Count >= 2)
        {
            MessageBox.Show(FindForm(), Lang.F("前景執行嘅腳本同一時間只可以行一個，而家有 {0} 個：\n\n{1}\n\n請只留一個前景腳本，其他改做背景執行。", fgs.Count,
                    string.Join("\n", fgs.Select(n => "• " + n))), Lang.T("未能開始"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        foreach (var sc in sel) ScriptRunner.Start(sc);
        _grid.Invalidate();
    }

    private void StopAllScripts()
    {
        foreach (var sc in _scripts.Where(ScriptRunner.IsRunning).ToList()) ScriptRunner.Stop(sc);
        _grid.Invalidate();
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
            if (old != _libView) BeginInvoke(() => old.Dispose()); // 腳本庫留住（搜尋結果唔使重新搵）
            _editor = null;
        }
        if (view == null)
        {
            _listView.Visible = true;
            _grid.Refresh2();
            PlaceLibButton();
            return;
        }
        view.Dock = DockStyle.Fill;
        _editor = view;
        _listView.Visible = false;
        Controls.Add(view);
        view.BringToFront();
        PlaceLibButton();
    }

    /// <summary>監視模式：即時睇住各腳本嘅程式畫面，剔咗可以一齊開始。</summary>
    private void OpenMonitor()
    {
        var m = new MonitorView(_scripts, _grid.Selected, () => { UpdateSelButton(); _grid.Invalidate(); }, StartSelected, StopAllScripts);
        m.Back += () => ShowView(null);
        ShowView(m);
    }

    /// <summary>撳腳本：先睇預覽（唔可以改），上面有「修改」同「開始」。</summary>
    private void Open(Script sc)
    {
        var v = new ScriptViewer(sc);
        v.Back += () => ShowView(null);
        v.Edit += () => OpenEditor(sc);
        v.Reload += () => Open(sc);
        ShowView(v);
    }

    private void OpenEditor(Script sc)
    {
        var ed = new ScriptEditor(sc);
        ed.Back += () => { ScriptUndo.Clear(sc); sc.CleanupImages(); Open(sc); }; // 改完返去預覽（冇用嘅圖呢個時候先刪）
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
    public event Action? NewRequested, SelectionChanged;
    public event Action<Script>? BgChanged;
    /// <summary>剔咗嘅腳本（可以一次過開始）。</summary>
    public readonly HashSet<Script> Selected = new();

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
        _menu.Items.Add(Lang.T("公開到腳本庫…"), null, (_, _) =>
        {
            if (!Valid(_menuIndex)) return;
            using var d = new PublishDialog(_scripts[_menuIndex]);
            d.ShowDialog(FindForm());
            Invalidate();
        });
        _menu.Items.Add(Lang.T("刪除"), null, (_, _) => { if (Valid(_menuIndex)) DeleteRequested?.Invoke(_scripts[_menuIndex]); });
        ScriptRunner.Status += (id, text) => { _status[id] = text; Invalidate(); };
    }

    private readonly Dictionary<string, string> _status = new(); // 每個腳本最新嘅運行狀態（第幾輪 / 第幾步）

    private bool Valid(int i) => i >= 0 && i < _scripts.Count;

    // 一行一個腳本，向下排
    private int TileH => Ui.S(64);
    private int AddH => Ui.S(48);
    private int Gap => Ui.S(8);
    private int Tiles => _scripts.Count + 1;

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        return new Size(w, _scripts.Count * (TileH + Gap) + AddH);
    }

    private Rectangle TileRect(int i)
    {
        int h = i == _scripts.Count ? AddH : TileH;
        return new Rectangle(0, i * (TileH + Gap), Math.Max(Ui.S(300), Width) - 1, h - 1);
    }

    private int _hoverBg = -1, _hoverRun = -1, _hoverRuns = -1;

    /// <summary>「開始」掣左邊：運行次數選單。</summary>
    private Rectangle RunsRect(Rectangle tile)
    {
        var rr = RunRect(tile);
        int w = Ui.S(110), h = Ui.S(30);
        return new Rectangle(rr.X - Ui.S(10) - w, tile.Y + (tile.Height - h) / 2, w, h);
    }

    /// <summary>格仔最右邊「開始 / 停止」掣。</summary>
    private Rectangle RunRect(Rectangle tile)
    {
        int w = Ui.S(96), h = Ui.S(34);
        return new Rectangle(tile.Right - Ui.S(14) - w, tile.Y + (tile.Height - h) / 2, w, h);
    }

    /// <summary>格仔中間「背景執行：揀程式」嗰粒。</summary>
    private Rectangle BgRect(Rectangle tile)
    {
        int w = Math.Clamp(tile.Width * 38 / 100, Ui.S(180), Ui.S(380));
        int h = Ui.S(30);
        return new Rectangle(tile.Right - Ui.S(270) - w, tile.Y + (tile.Height - h) / 2, w, h);
    }

    /// <summary>格仔右上角個剔格。</summary>
    private Rectangle CheckRect(Rectangle tile)
    {
        int sz = Ui.S(20);
        return new Rectangle(tile.X + Ui.S(14), tile.Y + (tile.Height - sz) / 2, sz, sz);
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
                TextRenderer.DrawText(g, "+  " + Lang.T("新增腳本"), Ui.F(10f), r,
                    hover ? Ui.Accent : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                continue;
            }
            var sc = _scripts[i];
            bool run = ScriptRunner.IsRunning(sc);
            // 運行中：綠色粗框 + 淡綠底，一眼睇到
            if (run) using (var rb = new SolidBrush(Ui.Alpha(Ui.Success, 28))) g.FillPath(rb, path);
            using (var pen = new Pen(run ? Ui.Success : hover ? Ui.Accent : Ui.Border, run ? Ui.S(2.5f) : 1f)) g.DrawPath(pen, path);
            int pad = Ui.S(12);
            int tx = r.X + Ui.S(48);                         // 剔格右邊
            int textW = BgRect(r).X - tx - Ui.S(12);
            TextRenderer.DrawText(g, sc.Name, Ui.F(10.5f, FontStyle.Bold), new Rectangle(tx, r.Y + Ui.S(11), textW, Ui.S(22)),
                Ui.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string info = Lang.F("{0} 個步驟", sc.Steps.Count) + (sc.Ifs.Count > 0 ? Lang.F(" · {0} 個 IF", sc.Ifs.Count) : "")
                          + (sc.Public ? " · " + Lang.T("🌐 已公開") : "");
            // 運行中：顯示第幾輪 / 第幾步
            bool showRun = run && _status.TryGetValue(sc.Id, out var stText) && stText.Length > 0;
            TextRenderer.DrawText(g, showRun ? "● " + _status[sc.Id] : info, Ui.F(8.75f), new Rectangle(tx, r.Y + Ui.S(36), textW, Ui.S(18)),
                showRun ? Ui.Success : Ui.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            // 右上角剔格（多選）
            var cb = CheckRect(r);
            bool sel = Selected.Contains(sc);
            using (var cp = Ui.Round(new RectangleF(cb.X, cb.Y, cb.Width, cb.Height), Ui.S(4f)))
            {
                using (var fb = new SolidBrush(sel ? Ui.Accent : Ui.Card)) g.FillPath(fb, cp);
                using var pen2 = new Pen(sel ? Ui.Accent : Ui.BorderHi, Ui.S(1.2f));
                g.DrawPath(pen2, cp);
            }
            if (sel)
            {
                using var tick = new Pen(Color.White, Ui.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(cb.X + cb.Width * 0.22f, cb.Y + cb.Height * 0.52f),
                    new PointF(cb.X + cb.Width * 0.42f, cb.Y + cb.Height * 0.72f),
                    new PointF(cb.X + cb.Width * 0.78f, cb.Y + cb.Height * 0.30f)
                });
            }
            // 背景執行：撳呢粒直接揀程式（唔使入修改）
            var bp = BgRect(r);
            bool bgHover = _hoverBg == i;
            using (var bpath = Ui.Round(new RectangleF(bp.X, bp.Y, bp.Width, bp.Height), Ui.S(6f)))
            {
                using (var bb = new SolidBrush(bgHover ? Ui.CardHi : Ui.Card)) g.FillPath(bb, bpath);
                using var bpen = new Pen(bgHover ? Ui.Accent : sc.Background ? Ui.Accent2 : Ui.Border, 1f);
                g.DrawPath(bpen, bpath);
            }
            TextRenderer.DrawText(g, BgControls.ShortTarget(sc) + "  ▾", Ui.F(8.75f), new Rectangle(bp.X + Ui.S(8), bp.Y, bp.Width - Ui.S(12), bp.Height),
                sc.Background ? Ui.Text : Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            // 運行次數
            var rs = RunsRect(r);
            using (var rsp = Ui.Round(new RectangleF(rs.X, rs.Y, rs.Width, rs.Height), Ui.S(6f)))
            {
                using (var rsb = new SolidBrush(_hoverRuns == i ? Ui.CardHi : Ui.Card)) g.FillPath(rsb, rsp);
                using var rspen = new Pen(_hoverRuns == i ? Ui.Accent : Ui.Border, 1f);
                g.DrawPath(rspen, rsp);
            }
            TextRenderer.DrawText(g, Script.RunsName(sc.Runs) + "  ▾", Ui.F(8.75f), new Rectangle(rs.X + Ui.S(8), rs.Y, rs.Width - Ui.S(10), rs.Height),
                Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            // 開始 / 停止掣
            var rr = RunRect(r);
            bool rh = _hoverRun == i;
            var runCol = run ? Ui.Danger : Ui.Success;
            if (rh) runCol = ControlPaint.Light(runCol, 0.25f);
            using (var rp = Ui.Round(new RectangleF(rr.X, rr.Y, rr.Width, rr.Height), Ui.S(8f)))
            using (var rbr = new SolidBrush(runCol))
                g.FillPath(rbr, rp);
            TextRenderer.DrawText(g, run ? Lang.T("■ 停止") : Lang.T("▶ 開始"), Ui.F(9.75f, FontStyle.Bold), rr, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        int hb = h >= 0 && h < _scripts.Count && BgRect(TileRect(h)).Contains(e.Location) ? h : -1;
        int hr = h >= 0 && h < _scripts.Count && RunRect(TileRect(h)).Contains(e.Location) ? h : -1;
        int hn = h >= 0 && h < _scripts.Count && RunsRect(TileRect(h)).Contains(e.Location) ? h : -1;
        if (h != _hover || hb != _hoverBg || hr != _hoverRun || hn != _hoverRuns) { _hover = h; _hoverBg = hb; _hoverRun = hr; _hoverRuns = hn; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverBg = -1;
        _hoverRun = -1;
        _hoverRuns = -1;
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
        if (e.Button == MouseButtons.Left)
        {
            if (RunRect(TileRect(i)).Contains(e.Location))
            {
                ScriptRunner.Toggle(_scripts[i]);
                Invalidate();
                return;
            }
            if (RunsRect(TileRect(i)).Contains(e.Location))
            {
                var target = _scripts[i];
                var rs = RunsRect(TileRect(i));
                RunsMenu.Show(this, new Point(rs.X, rs.Bottom + Ui.S(2)), target, Invalidate);
                return;
            }
            if (BgRect(TileRect(i)).Contains(e.Location))
            {
                var target = _scripts[i];
                if (ScriptRunner.IsRunning(target)) return; // 行緊唔可以轉
                var br = BgRect(TileRect(i));
                BgControls.ShowQuickMenu(this, new Point(br.X, br.Bottom + Ui.S(2)), target, () => { Invalidate(); BgChanged?.Invoke(target); });
                return;
            }
            var cb = CheckRect(TileRect(i));
            cb.Inflate(Ui.S(6), Ui.S(6)); // 撳近少少都算
            if (cb.Contains(e.Location))
            {
                var sc = _scripts[i];
                if (!Selected.Remove(sc)) Selected.Add(sc);
                Invalidate();
                SelectionChanged?.Invoke();
                return;
            }
            OpenRequested?.Invoke(_scripts[i]);
        }
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
    private readonly CardStack _head = StickyHead.Make();
    private readonly FancyButton _start;
    private readonly FancyButton _mode;
    private readonly StepList _steps;
    private readonly CardPanel _stepsCard;
    private readonly Action<string, string> _onStatus;
    private readonly Action _onChanged;
    private readonly ToolTip _tip = new();

    public event Action? Back;
    /// <summary>AI 套用咗新腳本：要重新整個編輯器。</summary>
    public event Action? Reload;

    public ScriptEditor(Script sc)
    {
        _sc = sc;
        // 背景執行嘅腳本：座標統一用「相對程式視窗」（舊腳本喺度自動換算）
        if (sc.TargetProcess.Length > 0) ScriptCoords.MakeRelative(sc); // 已經相對就淨係補記視窗大細
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
        _mode.Size = new Size(Ui.S(120), Ui.S(34));
        _mode.Click += (_, _) => RunsMenu.Show(_mode, new Point(0, _mode.Height + Ui.S(2)), _sc, UpdateButtons);

        var hkLabel = Ui.Text_(Lang.T("快捷鍵"), Ui.TextDim, Ui.F(9.75f), new Padding(0));
        var hkBox = new HotkeyBox { Hotkey = sc.Hotkey };
        hkBox.Changed += () => { _sc.Hotkey = hkBox.Hotkey; _sc.Save(); };
        var hkHost = new InputHost(hkBox, Ui.S(130));

        _start = new FancyButton { Kind = BtnKind.Success, Size = new Size(Ui.S(96), Ui.S(34)) };
        _start.Click += (_, _) => { Commit(); ScriptRunner.Toggle(_sc); };
        var rec = new FancyButton { Text = Lang.T("● 錄製"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        _tip.SetToolTip(rec, Lang.T("錄低你撳嘅鍵、滑鼠掣同滾輪（唔記滑鼠移動），停咗之後加喺主線程最尾。撳 F10 停止。"));
        rec.Click += (_, _) => StartRecording();

        var top = new BareRow();
        top.SetContent(new FlowRow(new Control[] { back, nameHost, _mode, rec }, new Control[] { _start, hkHost, hkLabel }));
        top.Padding = new Padding(0);

        // ---- 步驟 ----
        _steps = new StepList(sc, sc.Steps, null, nestedIf: true);
        _stepsCard = new CardPanel { Title = Lang.T("主線程"), Hint = Lang.T("由上至下逐步做；「新線程」同主線程同時行。「一直行」全部一直重複；「行一次」全部行完一次就停。") };
        _stepsCard.SetContent(_steps);

        _head.AddRow(top);

        // ---- 第二行：背景執行 + 揀程式視窗（可以預先設定好）----
        var bg = new BgControls(sc, _tip);
        var bgRow = new BareRow { Padding = new Padding(0) };
        var bgHint = Ui.Text_(Lang.T("揀咗視窗之後，座標係相對程式視窗嘅位置"), Ui.TextFaint, Ui.F(9f), new Padding(0));
        var bgFlow = new FlowRow(new Control[] { bg.Toggle, bg.Pick, bgHint }, Array.Empty<Control>());
        bg.Relayout = () => bgFlow.PerformLayout();
        bg.Changed += () => { Commit(); BeginInvoke(() => Reload?.Invoke()); }; // 座標換算咗：重新整
        bgRow.SetContent(bgFlow);
        _head.AddRow(bgRow);
        _stack.AddRow(_stepsCard);
        foreach (var t in sc.Threads) AddThreadCard(t);
        foreach (var b in sc.Ifs) AddIfCard(b); // 舊版 IF 框照顯示
        // 「+ 新線程」掣（永遠喺最底）
        var addThread = new FancyButton { Text = Lang.T("+ 新線程"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(10f, FontStyle.Bold) };
        addThread.Size = new Size(Ui.S(150), Ui.S(38));
        _tip.SetToolTip(addThread, Lang.T("開一條同主線程同時行嘅線程"));
        addThread.Click += (_, _) => AddThread();
        _addThreadRow = new BareRow { Padding = new Padding(0) };
        _addThreadRow.SetContent(new FlowRow(new Control[] { addThread }, Array.Empty<Control>()));
        _stack.AddRow(_addThreadRow);
        Controls.Add(_stack); Controls.Add(_head); // 頂部固定，下面捲動

        _onStatus = (id, text) =>
        {
            if (id == _sc.Id) _steps.SetStatus(text);
            else if (id.StartsWith(_sc.Id + "/t:") && _threadLists.TryGetValue(id[(_sc.Id.Length + 3)..], out var tl)) tl.SetStatus(text);
            else if (id.StartsWith(_sc.Id + "/") && _ifPanels.TryGetValue(id[(_sc.Id.Length + 1)..], out var ip)) ip.SetStatus(text);
        };
        _onChanged = UpdateButtons;
        ScriptRunner.Status += _onStatus;
        ScriptRunner.Changed += _onChanged;
        UpdateButtons();

        // 右下角「雲」：揀咗 AI 而且登入咗先會出現
        _ = new MultiTool.Ai.ScriptAssist(this, sc, () => BeginInvoke(() => Reload?.Invoke()));

        // 右下角「復原」：返返上一次改動之前（例如撳錯刪除）
        _undo = new FancyButton { Text = Lang.T("↶ 復原"), Kind = BtnKind.Ghost, Font = Ui.F(10f, FontStyle.Bold), Size = new Size(Ui.S(104), Ui.S(40)) };
        _tip.SetToolTip(_undo, Lang.T("復原上一次改動（加 / 刪 / 移步驟等），只記一次"));
        _undo.Click += (_, _) =>
        {
            if (ScriptRunner.IsRunning(_sc)) { _steps.SetStatus(Lang.T("腳本行緊，停咗先再復原")); return; }
            if (ScriptUndo.Undo(_sc)) BeginInvoke(() => Reload?.Invoke());
        };
        Controls.Add(_undo);
        _undo.BringToFront();
        _onUndo = () => { if (!_undo.IsDisposed) _undo.Enabled = ScriptUndo.Has(_sc); };
        ScriptUndo.Changed += _onUndo;
        _onUndo();
        Resize += (_, _) => PlaceUndo();
        PlaceUndo();
    }

    private readonly FancyButton _undo;
    private readonly Action _onUndo;

    private void PlaceUndo()
    {
        // 喺「雲」掣左邊
        int m = Ui.S(28), cloud = Ui.S(52);
        _undo.Location = new Point(ClientSize.Width - m - cloud - Ui.S(14) - _undo.Width, ClientSize.Height - Ui.S(20) - cloud + (cloud - _undo.Height) / 2);
        _undo.BringToFront();
    }

    /// <summary>錄製：細窗喺右上角，撳 F10 或者「停止錄製」就停，錄到嘅步驟加喺主線程最尾。</summary>
    private void StartRecording()
    {
        if (ScriptRunner.IsRunning(_sc)) { _steps.SetStatus(Lang.T("腳本行緊，停咗先再錄製")); return; }
        Commit();
        var form = FindForm();
        var recorder = new ScriptRecorder(Keys.F10);
        var bar = new RecordingBar("F10");
        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            recorder.Stop();
            try { bar.Close(); bar.Dispose(); } catch { }
            var steps = recorder.ToSteps(p => ScriptCoords.FromScreen(_sc, p) ?? p);
            if (form is { IsDisposed: true }) form = null;
            if (form != null)
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate();
            }
            if (steps.Count == 0)
            {
                MessageBox.Show(form, Lang.T("冇錄到任何動作。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ScriptUndo.Push(_sc);
            _sc.Steps.AddRange(steps);
            _sc.Save();
            MessageBox.Show(form, Lang.F("錄到 {0} 個步驟，已經加咗喺主線程最尾。", steps.Count), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Reload?.Invoke();
        }
        // 唔好靠修改頁自己嘅視窗控制代碼（縮細 / 重建頁面時可能冇咗），用錄製細窗或者主視窗轉返 UI 執行緒
        void OnUi()
        {
            try
            {
                if (bar.IsHandleCreated && !bar.IsDisposed) bar.BeginInvoke(Finish);
                else if (form is { IsHandleCreated: true, IsDisposed: false }) form.BeginInvoke(Finish);
                else if (IsHandleCreated) BeginInvoke(Finish);
            }
            catch (Exception ex) { AppPaths.LogError("停止錄製", ex); }
        }
        recorder.StopRequested += OnUi;
        bar.StopClicked += Finish; // 撳掣本身已經喺 UI 執行緒
        bar.Show();
        // 揀咗視窗就切去嗰個視窗；冇就縮細 MultiTool
        var h = _sc.TargetProcess.Length > 0 ? Win.Find(_sc) : 0;
        if (h != 0) Win.Activate(h);
        else if (form != null) form.WindowState = FormWindowState.Minimized;
        recorder.Start();
    }

    private void Commit()
    {
        if (!ScriptRunner.IsRunning(_sc)) _sc.SyncFolder();
        _sc.Save();
    }

    private readonly Dictionary<string, IfPanel> _ifPanels = new();
    private readonly Dictionary<string, StepList> _threadLists = new();
    private BareRow? _addThreadRow;

    private void AddThread()
    {
        string name = "";
        for (int i = 1; ; i++) { name = Lang.T("新線程 ") + i; if (_sc.Threads.All(x => x.Name != name)) break; }
        var t = new ThreadBlock { Name = name };
        ScriptUndo.Push(_sc);
        _sc.Threads.Add(t);
        _sc.Save();
        AddThreadCard(t);
        if (ScriptRunner.IsRunning(_sc)) _steps.SetStatus(Lang.T("新加嘅線程要停咗再開始先會行"));
    }

    private void AddThreadCard(ThreadBlock t)
    {
        var list = new StepList(_sc, t.Steps, null, nestedIf: true);
        var card = new CardPanel { Title = t.Name, Hint = Lang.T("同主線程同時行；右上角可以刪除呢條線程") };
        var del = new FancyButton { Text = Lang.T("刪除線程"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(96), Ui.S(32)) };
        var wrap = new ThreadWrap(list, del);
        card.SetContent(wrap);
        _threadLists[t.Id] = list;
        del.Click += (_, _) =>
        {
            if (t.Steps.Count > 0 && MessageBox.Show(FindForm(), Lang.F("刪除「{0}」同入面嘅步驟？", t.Name), Lang.T("我的腳本"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;
            ScriptUndo.Push(_sc);
            _sc.Threads.Remove(t);
            _threadLists.Remove(t.Id);
            _sc.Save();
            BeginInvoke(() => _stack.RemoveRow(card));
        };
        // 加喺「+ 新線程」掣之前
        if (_addThreadRow != null) _stack.RemoveRow(_addThreadRow);
        _stack.AddRow(card);
        if (_addThreadRow != null) _stack.AddRow(_addThreadRow);
    }

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
            ScriptUndo.Push(_sc);
            _sc.Ifs.Remove(b);
            _ifPanels.Remove(b.Id);
            _sc.Save();
            BeginInvoke(() => _stack.RemoveRow(card));
        };
        _stack.AddRow(card);
    }

    private void UpdateButtons()
    {
        bool run = ScriptRunner.IsRunning(_sc);
        _start.Text = run ? Lang.T("停止") : Lang.T("測試");
        _start.Kind = run ? BtnKind.Danger : BtnKind.Success;
        _mode.Text = Script.RunsName(_sc.Runs) + "  ▾";
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
            ScriptUndo.Changed -= _onUndo;
        }
        base.Dispose(disposing);
    }
}

/// <summary>復原：每個腳本只記上一次改動之前嘅樣（加 / 刪 / 移步驟等）。</summary>
internal static class ScriptUndo
{
    private static readonly Dictionary<Script, List<string>> Last = new(ReferenceEqualityComparer.Instance);
    public static event Action? Changed;

    /// <summary>改之前叫：記低而家嘅樣。</summary>
    public static void Push(Script sc)
    {
        Last[sc] = sc.Lines();
        Changed?.Invoke();
    }

    public static bool Has(Script sc) => Last.ContainsKey(sc);

    public static bool Undo(Script sc)
    {
        if (!Last.Remove(sc, out var lines)) return false;
        sc.RestoreSteps(lines);
        sc.Save();
        Changed?.Invoke();
        return true;
    }

    public static void Clear(Script sc)
    {
        if (Last.Remove(sc)) Changed?.Invoke();
    }
}

/// <summary>排位版本：結構改咗就加一，啲量度暫存就會作廢。</summary>
internal static class LayoutVer
{
    public static int V;
    public static void Bump() => V++;

    /// <summary>淨係令呢個控件同佢上面嗰幾層嘅量度作廢（隔籬嘅唔使重計）。</summary>
    public static void MarkDirty(Control c)
    {
        for (Control? p = c; p != null; p = p.Parent)
            if (p is ISizeCache sc) sc.Dirty();
    }
}

/// <summary>有量度暫存嘅控件。</summary>
internal interface ISizeCache { void Dirty(); }

/// <summary>頂部固定一行（捲落去都睇到）：高度跟內容。</summary>
internal static class StickyHead
{
    public static CardStack Make()
    {
        var h = new CardStack { Dock = DockStyle.Top, Height = Ui.S(120) };
        h.AutoScroll = false;
        h.Padding = new Padding(Ui.S(24), Ui.S(16), Ui.S(24), Ui.S(6));
        h.Layout += (_, _) =>
        {
            h.AutoScroll = false;
            int bottom = 0;
            foreach (Control c in h.Controls) bottom = Math.Max(bottom, c.Bottom);
            int ht = bottom + h.Padding.Bottom;
            if (bottom > 0 && h.Height != ht) h.Height = ht;
        };
        return h;
    }
}

/// <summary>新線程卡：右上角「刪除線程」+ 步驟清單。</summary>
internal sealed class ThreadWrap : Panel
{
    private readonly StepList _list;
    private readonly FancyButton _del;

    public ThreadWrap(StepList list, FancyButton del)
    {
        _list = list;
        _del = del;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        _list.Dock = DockStyle.None;
        Controls.Add(_del);
        Controls.Add(_list);
    }

    private int Head => Ui.S(40);

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        return new Size(w, Head + _list.GetPreferredSize(new Size(w, 0)).Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_list == null) return;
        _del.Location = new Point(ClientSize.Width - _del.Width, 0);
        _list.SetBounds(0, Head, ClientSize.Width, _list.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
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
    private int RowGap => Ui.S(10);

    private static bool Shown(Control c) => c.Tag as string != "hidden";
    private static int W(Control c) => (c is Label ? c.PreferredSize : c.Size).Width;
    private int LeftW => _left.Where(Shown).Sum(c => W(c) + Ui.S(10));
    private int RightW => _right.Where(Shown).Sum(c => W(c) + Ui.S(10)) + Ui.S(6);

    /// <summary>左右兩組放唔落一行：右邊組落第二行（唔會疊埋）。</summary>
    private bool Wrapped(int width) => width > 0 && LeftW + RightW > width;

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Width;
        return new(proposed.Width, Wrapped(w) ? H * 2 + RowGap : H);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_left == null || _right == null) return;
        int h = H, x = 0;
        bool wrap = Wrapped(ClientSize.Width);
        foreach (var c in _left)
        {
            var sz = c is Label ? c.PreferredSize : c.Size;
            c.Location = new Point(x, (h - sz.Height) / 2);
            x += sz.Width + Ui.S(10);
        }
        int y0 = wrap ? h + RowGap : 0;
        x = ClientSize.Width;
        bool first = true;
        for (int i = 0; i < _right.Length; i++)
        {
            var c = _right[i];
            if (c.Tag as string == "hidden") continue;
            var sz = c is Label ? c.PreferredSize : c.Size;
            x -= sz.Width;
            c.Location = new Point(x, y0 + (h - sz.Height) / 2);
            x -= first ? Ui.S(16) : Ui.S(10);
            first = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 換行狀態變咗：叫上面重新計高度
        bool wrap = Wrapped(ClientSize.Width);
        if (wrap != _lastWrap)
        {
            _lastWrap = wrap;
            for (Control? p = Parent; p != null; p = p.Parent)
                if (p is CardStack cs) { if (cs.IsHandleCreated) cs.BeginInvoke(() => cs.PerformLayout()); break; }
        }
    }

    private bool _lastWrap;
}

/// <summary>步驟清單：每行一個步驟（類型、參數、↑ ↓ ✕），下面一排「+ 新增」掣。</summary>
internal sealed class StepList : Panel, ISizeCache
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
    private readonly List<StepKind> _kinds = new();

    public StepList(Script sc, List<Step> steps, Action? addIf = null, bool nestedIf = false, bool inLoop = false)
    {
        _inLoop = inLoop;
        _kinds.AddRange(new[] { StepKind.Key, StepKind.Click, StepKind.Wait, StepKind.Image, StepKind.Loop });
        if (nestedIf) _kinds.Add(StepKind.If);
        _kinds.Add(StepKind.Text);
        _kinds.Add(StepKind.Parallel);
        _kinds.Add(StepKind.System);
        if (inLoop) _kinds.Add(StepKind.Break);
        _kinds.Add(StepKind.StopScript);
        _sc = sc;
        _steps = steps;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        // 常用嘅直接一粒掣；其他放入「更多」
        foreach (var kind in new[] { StepKind.Key, StepKind.Click, StepKind.Wait, StepKind.Image, StepKind.Loop }) // 拖拉 = 點擊「按下」+ 延遲 + 點擊「彈起」
        {
            var b = new FancyButton { Text = "+ " + Step.KindName(kind), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
            b.Size = new Size(Ui.S(116), Ui.S(34));
            b.Click += (_, _) => AddStep(kind);
            _addButtons.Add(b);
            Controls.Add(b);
        }
        {
            var more = new FancyButton { Text = Lang.T("+ 更多 ▾"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
            more.Size = new Size(Ui.S(116), Ui.S(34));
            _tip.SetToolTip(more, Lang.T("輸入文字、IF 條件、同時運行、系統、中斷循環、停止腳本"));
            more.Click += (_, _) => ShowMoreMenu(more);
            _addButtons.Add(more);
            Controls.Add(more);
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

    private ContextMenuStrip DarkMenu()
    {
        var m = new ContextMenuStrip();
        try { m.Renderer = DarkTheme.MenuRenderer(); } catch { }
        m.BackColor = Ui.CardHi;
        m.ForeColor = Ui.Text;
        return m;
    }

    private static void ShowMenu(ContextMenuStrip menu, Control anchor)
    {
        menu.Closed += (_, _) => { try { if (!anchor.IsDisposed) anchor.BeginInvoke(() => menu.Dispose()); } catch { } };
        menu.Show(anchor, new Point(0, anchor.Height + Ui.S(2)));
    }

    /// <summary>滑鼠：左鍵 / 右鍵 / 滾輪 下拉選單（揀滾輪就變做滾輪步驟，座標保留）。</summary>
    private FancyButton MouseButtonPicker(Step st)
    {
        string Cur() => st.Kind == StepKind.Scroll ? Lang.T("滾輪") : st.Right ? Lang.T("右鍵") : Lang.T("左鍵");
        var b = new FancyButton { Text = Cur() + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        b.Click += (_, _) =>
        {
            var m = DarkMenu();
            void Item(string text, bool check, Action apply)
            {
                var it = new ToolStripMenuItem(text) { Checked = check };
                it.Click += (_, _) => BeginInvoke(() => { apply(); RebuildRow(st); });
                m.Items.Add(it);
            }
            Item(Lang.T("左鍵"), st.Kind == StepKind.Click && !st.Right, () => { st.Kind = StepKind.Click; st.Right = false; });
            Item(Lang.T("右鍵"), st.Kind == StepKind.Click && st.Right, () => { st.Kind = StepKind.Click; st.Right = true; });
            Item(Lang.T("滾輪"), st.Kind == StepKind.Scroll, () => { if (st.Kind != StepKind.Scroll && st.Count == 0) st.Count = -3; st.Kind = StepKind.Scroll; });
            ShowMenu(m, b);
        };
        return b;
    }

    /// <summary>「系統」步驟嗰行：做咩 ▾ + 揀程式 / 視窗 / 秒數。</summary>
    private void BuildSystemRow(Step st, List<Control> list)
    {
        var act = new FancyButton { Text = Step.SysName(st.Sys) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
        act.Size = new Size(Ui.S(150), Ui.S(34));
        act.Click += (_, _) =>
        {
            var m = DarkMenu();
            foreach (SysAction a in Enum.GetValues(typeof(SysAction)))
            {
                var chosen = a;
                var item = new ToolStripMenuItem(Step.SysName(a)) { Checked = st.Sys == a };
                item.Click += (_, _) =>
                {
                    if (st.Sys == chosen) return;
                    // 打開 = 程式路徑；關閉 / 切換 = process 名：種類變咗就清空
                    bool samePick = (st.Sys is SysAction.CloseApp or SysAction.SwitchWindow) && (chosen is SysAction.CloseApp or SysAction.SwitchWindow);
                    if (!samePick) { st.Target = ""; st.TargetName = ""; }
                    st.Sys = chosen;
                    BeginInvoke(() => RebuildRow(st));
                };
                m.Items.Add(item);
            }
            ShowMenu(m, act);
        };
        list.Add(act);

        switch (st.Sys)
        {
            case SysAction.OpenApp:
            {
                var pick = new FancyButton { Text = (st.TargetName.Length > 0 ? st.TargetName : Lang.T("揀程式…")) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
                pick.Size = new Size(Ui.S(240), Ui.S(34));
                _tip.SetToolTip(pick, st.Target.Length > 0 ? st.Target : Lang.T("揀已安裝嘅程式，或者揀一個檔案"));
                pick.Click += (_, _) =>
                {
                    var m = DarkMenu();
                    var browse = new ToolStripMenuItem(Lang.T("瀏覽檔案…"));
                    browse.Click += (_, _) => BeginInvoke(() =>
                    {
                        using var dlg = new OpenFileDialog { Filter = Lang.T("程式 / 捷徑") + "|*.exe;*.lnk;*.bat;*.cmd|" + Lang.T("所有檔案") + "|*.*" };
                        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                        st.Target = dlg.FileName;
                        st.TargetName = Path.GetFileNameWithoutExtension(dlg.FileName);
                        RebuildRow(st);
                    });
                    m.Items.Add(browse);
                    m.Items.Add(new ToolStripSeparator());
                    var apps = new ToolStripMenuItem(Lang.T("已安裝程式"));
                    try
                    {
                        foreach (var (name, launch) in MultiTool.Ai.PcActions.InstalledApps())
                        {
                            var n = name; var l = launch;
                            var it = new ToolStripMenuItem(n) { Checked = st.Target == l, BackColor = Ui.CardHi, ForeColor = Ui.Text };
                            it.Click += (_, _) => { st.Target = l; st.TargetName = n; BeginInvoke(() => RebuildRow(st)); };
                            apps.DropDownItems.Add(it);
                        }
                    }
                    catch { }
                    if (apps.DropDownItems.Count == 0) apps.Enabled = false;
                    try { apps.DropDown.Renderer = DarkTheme.MenuRenderer(); } catch { }
                    apps.DropDown.BackColor = Ui.CardHi;
                    if (apps.DropDown is ToolStripDropDownMenu dm) dm.MaximumSize = new Size(Ui.S(420), Ui.S(520));
                    m.Items.Add(apps);
                    ShowMenu(m, pick);
                };
                list.Add(pick);
                break;
            }
            case SysAction.CloseApp:
            case SysAction.SwitchWindow:
            {
                string label = st.Target.Length == 0 ? Lang.T("揀視窗…")
                    : st.Sys == SysAction.CloseApp ? st.Target : (st.TargetName.Length > 0 ? $"{st.Target} - {st.TargetName}" : st.Target);
                var pick = new FancyButton { Text = label + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
                pick.Size = new Size(Ui.S(260), Ui.S(34));
                _tip.SetToolTip(pick, st.Sys == SysAction.CloseApp ? Lang.T("關閉呢個程式所有視窗（3 秒內閂唔到就強制結束）") : Lang.T("將呢個視窗切去最前"));
                pick.Click += (_, _) =>
                {
                    var m = DarkMenu();
                    var wins = Win.List();
                    if (wins.Count == 0) m.Items.Add(Lang.T("（冇搵到視窗）")).Enabled = false;
                    foreach (var w in wins)
                    {
                        var info = w;
                        var t = $"{w.Process} - {w.Title}";
                        if (t.Length > 70) t = t[..70] + "…";
                        var it = new ToolStripMenuItem(t) { Checked = string.Equals(st.Target, w.Process, StringComparison.OrdinalIgnoreCase) && (st.Sys == SysAction.CloseApp || st.TargetName == w.Title) };
                        it.Click += (_, _) => { st.Target = info.Process; st.TargetName = info.Title; BeginInvoke(() => RebuildRow(st)); };
                        m.Items.Add(it);
                    }
                    ShowMenu(m, pick);
                };
                list.Add(pick);
                break;
            }
            case SysAction.PlaySound:
            {
                var pick = new FancyButton { Text = (st.TargetName.Length > 0 ? st.TargetName : Lang.T("揀音效…")) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
                pick.Size = new Size(Ui.S(220), Ui.S(34));
                _tip.SetToolTip(pick, st.Target.Length > 0 ? st.Target : Lang.T("揀 Windows 內置音效，或者自己嘅 wav / mp3"));
                pick.Click += (_, _) =>
                {
                    var m = DarkMenu();
                    var browse = new ToolStripMenuItem(Lang.T("瀏覽檔案…"));
                    browse.Click += (_, _) => BeginInvoke(() =>
                    {
                        using var dlg = new OpenFileDialog { Filter = Lang.T("音效") + "|*.wav;*.mp3;*.wma;*.mid|" + Lang.T("所有檔案") + "|*.*" };
                        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                        st.Target = dlg.FileName;
                        st.TargetName = Path.GetFileNameWithoutExtension(dlg.FileName);
                        RebuildRow(st);
                    });
                    m.Items.Add(browse);
                    m.Items.Add(new ToolStripSeparator());
                    try
                    {
                        string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                        foreach (var f in Directory.GetFiles(media, "*.wav").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(60))
                        {
                            var file = f;
                            var it = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(f)) { Checked = st.Target == f };
                            it.Click += (_, _) => { st.Target = file; st.TargetName = Path.GetFileNameWithoutExtension(file); BeginInvoke(() => RebuildRow(st)); };
                            m.Items.Add(it);
                        }
                    }
                    catch { }
                    if (m.Items.Count > 2 && m is ToolStripDropDownMenu dm) dm.MaximumSize = new Size(Ui.S(360), Ui.S(520));
                    ShowMenu(m, pick);
                };
                var rep = new FancyButton { Text = st.Repeat ? Lang.T("一直重複") : Lang.T("播一次"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(90), Ui.S(34)) };
                _tip.SetToolTip(rep, Lang.T("一直重複：直到腳本停止先停"));
                rep.Click += (_, _) => { st.Repeat = !st.Repeat; rep.Text = st.Repeat ? Lang.T("一直重複") : Lang.T("播一次"); Changed(false); };
                var test = new FancyButton { Text = Lang.T("試聽"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(66), Ui.S(34)) };
                test.Click += (_, _) => { if (File.Exists(st.Target)) SoundFx.Play("preview", st.Target, false); };
                list.AddRange(new Control[] { pick, rep, test });
                break;
            }
            case SysAction.Shutdown:
            {
                var sec = new NumberBox(Ui.S(90)) { Minimum = 0, Maximum = 3600, Increment = 10 };
                sec.Value = Math.Clamp(Math.Round(st.Seconds), 0, 3600);
                sec.ValueChanged += (_, _) => { st.Seconds = sec.Value; Changed(false); };
                list.AddRange(new Control[] { sec, L(Lang.T("秒後關機")), L(Lang.T("（期間可以喺命令提示字元打 shutdown /a 取消）"), Ui.TextFaint) });
                break;
            }
        }
    }

    /// <summary>「+ 更多」：唔常用嘅步驟。</summary>
    private void ShowMoreMenu(Control anchor)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        foreach (var k in _kinds.Where(k => k is StepKind.Text or StepKind.If or StepKind.Parallel or StepKind.System or StepKind.Break or StepKind.StopScript))
        {
            var kind = k;
            menu.Items.Add(Step.KindName(kind), null, (_, _) => BeginInvoke(() => AddStep(kind)));
        }
        menu.Closed += (_, _) => { try { if (!anchor.IsDisposed) anchor.BeginInvoke(() => menu.Dispose()); } catch { } };
        menu.Show(anchor, new Point(0, anchor.Height + Ui.S(2)));
    }

    private int RowH => Ui.S(34);
    private int Gap => Ui.S(8);

    private int BlockIndent => Ui.S(28);

    /// <summary>循環 / 同時運行摺埋咗。</summary>
    private bool Folded(int i) => i < _steps.Count && _steps[i].Collapsed && _steps[i].Kind is StepKind.Loop or StepKind.Parallel or StepKind.Thread;

    /// <summary>摺埋 / 打開掣（循環、同時運行用）。</summary>
    private FancyButton FoldButton(Step st, Func<string> summary)
    {
        var b = new FancyButton { Text = st.Collapsed ? "▸" : "▾", Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(30), Ui.S(30)) };
        _tip.SetToolTip(b, Lang.T("摺埋 / 打開") + (st.Collapsed ? "\n" + summary() : ""));
        b.Click += (_, _) => { st.Collapsed = !st.Collapsed; BeginInvoke(() => RebuildRow(st)); };
        return b;
    }

    /// <summary>每行高度：IF 步驟 = 標題行 + 下面成個 IF 框。</summary>
    private int RowHeight(int i, int width)
    {
        if (i >= _rowControls.Count) return RowH;
        int head = HeadH(i, width);
        var block = _rowControls[i].FirstOrDefault(c => c is IfPanel or LoopBlock or ParallelBlock);
        if (block == null || Folded(i)) return head;
        return head + Ui.S(6) + block.GetPreferredSize(new Size(Math.Max(50, width - BlockIndent), 0)).Height;
    }

    private int LineGap => Ui.S(6);
    private int HeadH(int i, int width) { int n = PlaceHead(i, width, 0, false); return n * RowH + (n - 1) * LineGap; }

    /// <summary>排一行嘅標題部分：右邊（↑ ↓ ✕）優先；左邊放唔落就換行（唔會疊埋）。回傳用咗幾多行。</summary>
    private int PlaceHead(int i, int w, int y, bool apply)
    {
        var row = _rowControls[i];
        const int nRight = 3;
        int rightW = 0;
        for (int k = row.Length - nRight; k < row.Length; k++) rightW += row[k].Width + Ui.S(4);
        int limit = Math.Max(Ui.S(120), w - rightW - Ui.S(8));
        int x = 0, line = 0, indent = Ui.S(30);
        for (int k = 0; k < row.Length - nRight; k++)
        {
            var c = row[k];
            if (c is IfPanel or LoopBlock or ParallelBlock) continue;
            var sz = c is Label ? c.PreferredSize : c.Size;
            if (k == 0) sz = new Size(Ui.S(28), sz.Height);
            if (k >= 2 && x > indent && x + sz.Width > (line == 0 ? limit : w)) { line++; x = indent; }
            if (apply) c.Location = new Point(x, y + line * (RowH + LineGap) + (RowH - sz.Height) / 2);
            x += sz.Width + (k == 0 ? Ui.S(2) : c is ToggleChip ? Ui.S(4) : Ui.S(8));
        }
        if (apply)
        {
            int rx = w;
            for (int k = row.Length - nRight; k < row.Length; k++)
            {
                var c = row[k];
                rx -= c.Width;
                c.Location = new Point(rx, y);
                rx -= Ui.S(4);
                c.BringToFront(); // 排序 / 刪除掣永遠喺最上面
            }
        }
        return line + 1;
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

    // 量度結果暫存：版本冇變（冇改過結構）同闊度一樣就直接用，費事一層層重新計
    private int _pv = -1, _pk = int.MinValue;
    private Size _ps;
    public void Dirty() => _pv = -1;

    public override Size GetPreferredSize(Size proposed)
    {
        int key = proposed.Width > 0 ? proposed.Width : -Width - 1;
        if (_pv == LayoutVer.V && _pk == key) return _ps;
        _ps = Measure(proposed);
        _pv = LayoutVer.V;
        _pk = key;
        return _ps;
    }

    private Size Measure(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(360));
        int y = RowsH(w) + Gap + Ui.S(8);
        int end = PlaceButtons(w, y, false);
        return new Size(w, end + Ui.S(12) + Ui.S(20));
    }

    private void Changed(bool structure)
    {
        LayoutVer.MarkDirty(this);
        _sc.Save();
        if (structure)
        {
            Rebuild();
            Relayout();
        }
    }

    private void AddStep(StepKind kind) => InsertStep(kind, _steps.Count);

    private void InsertStep(StepKind kind, int at)
    {
        ScriptUndo.Push(_sc);
        var st = new Step { Kind = kind };
        if (kind == StepKind.If) st.Sub = new IfBlock { Name = Lang.T("IF 條件") };
        if (kind == StepKind.Parallel) { st.Branches.Add(new List<Step>()); st.Branches.Add(new List<Step>()); }
        if (kind == StepKind.System) st.Seconds = 60m;
        if (kind == StepKind.Scroll) st.Count = -3;
        if (kind is StepKind.Click or StepKind.Drag or StepKind.Scroll)
        {
            var p = ScriptCoords.FromScreen(_sc, Cursor.Position) ?? Point.Empty;
            st.X = p.X;
            st.Y = p.Y;
            if (kind == StepKind.Drag) { st.X2 = p.X + 100; st.Y2 = p.Y; st.Hold = 0.5m; }
        }
        _steps.Insert(Math.Clamp(at, 0, _steps.Count), st);
        Changed(true);
    }

    private static IEnumerable<string> AllImages(Step st)
    {
        foreach (var n in ImageList.Split(st.Image)) yield return n;
        if (st.Sub != null)
        {
            foreach (var n in st.Sub.AllConds.SelectMany(c => ImageList.Split(c.Image))) yield return n;
            foreach (var n in st.Sub.Steps.SelectMany(AllImages)) yield return n;
        }
        foreach (var n in st.Body.SelectMany(AllImages)) yield return n;
        foreach (var n in st.Branches.SelectMany(b => b).SelectMany(AllImages)) yield return n;
    }

    private void MoveStep(int i, int d)
    {
        int j = i + d;
        if (i >= 0 && j >= 0 && j < _steps.Count) ScriptUndo.Push(_sc);
        if (j < 0 || j >= _steps.Count) return;
        (_steps[i], _steps[j]) = (_steps[j], _steps[i]);
        Changed(true);
    }

    private void Delete(int i)
    {
        if (i < 0 || i >= _steps.Count) return;
        ScriptUndo.Push(_sc);
        _steps.RemoveAt(i);
        // 冇再用嘅圖：離開修改頁先刪（費事復原時圖冇咗）
        Changed(true);
    }

    /// <summary>重新整晒每行嘅控件。</summary>
    // 每個步驟嘅控件（重用：加 / 刪 / 移一步唔使成個清單重新整，步驟多都唔會慢）
    private readonly Dictionary<Step, Control[]> _rowCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>某一步自己嘅控件要重新整（例如換咗模式、摺埋）。</summary>
    private void RebuildRow(Step st)
    {
        int w = Width > 0 ? Width : ClientSize.Width;
        int before = GetPreferredSize(new Size(w, 0)).Height;
        SuspendLayout();
        if (_rowCache.Remove(st, out var old))
            foreach (var c in old) { Controls.Remove(c); c.Dispose(); }
        _sc.Save();
        Rebuild();
        ResumeLayout(false);
        Dirty();
        int after = GetPreferredSize(new Size(w, 0)).Height;
        if (after == before)
        {
            // 高度冇變：淨係排返呢個清單，唔使成頁重新排
            PerformLayout();
            Invalidate(true);
        }
        else Relayout();
    }

    private void Rebuild()
    {
        SuspendLayout();
        // 唔再喺清單嘅步驟：清走佢哋嘅控件
        var keep = new HashSet<Step>(_steps, ReferenceEqualityComparer.Instance);
        foreach (var kv in _rowCache.Where(kv => !keep.Contains(kv.Key)).ToList())
        {
            foreach (var c in kv.Value) { Controls.Remove(c); c.Dispose(); }
            _rowCache.Remove(kv.Key);
        }
        _rowControls.Clear();
        for (int i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            if (!_rowCache.TryGetValue(st, out var row))
            {
                row = BuildRow(st);
                _rowCache[st] = row;
                foreach (var c in row) Controls.Add(c);
                // 右鍵行號 / 種類：複製 / 貼上 / 加插動作
                foreach (var c in row.Take(2)) c.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowInsertMenu(_steps.IndexOf(st), (Control)c, e.Location); };
            }
            // 行號同上下掣跟位置更新
            row[0].Text = (i + 1) + ".";
            row[^1].Enabled = i > 0;                 // ↑
            row[^2].Enabled = i < _steps.Count - 1;  // ↓
            _rowControls.Add(row);
        }
        _empty.Visible = _steps.Count == 0;
        ResumeLayout(false);
    }

    private Label L(string text, Color? c = null) => Ui.Text_(text, c ?? Ui.TextDim, Ui.F(9.75f), new Padding(0));

    // 複製咗嘅步驟（連入面嘅循環 / IF / 支線一齊），同埋佢個腳本資料夾（貼去第二個腳本時抄埋啲圖）
    private static Step? _clip;
    private static string _clipDir = "";

    private void Paste(int at)
    {
        if (_clip == null) return;
        ScriptUndo.Push(_sc);
        var st = _clip.Clone();
        if (!string.Equals(_clipDir, _sc.Dir, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in AllImages(st).Distinct())
            {
                try
                {
                    string src = Path.Combine(_clipDir, name), dst = Path.Combine(_sc.Dir, name);
                    if (File.Exists(src) && !File.Exists(dst)) { Directory.CreateDirectory(_sc.Dir); File.Copy(src, dst); }
                }
                catch { }
            }
        }
        _steps.Insert(Math.Clamp(at, 0, _steps.Count), st);
        Changed(true);
    }

    /// <summary>右鍵某一行：複製 / 貼上 / 加插動作至上一行 / 下一行。</summary>
    private void ShowInsertMenu(int i, Control anchor, Point at)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        var copy = new ToolStripMenuItem(Lang.T("複製"));
        copy.Click += (_, _) => { _clip = _steps[i].Clone(); _clipDir = _sc.Dir; };
        menu.Items.Add(copy);
        var cut = new ToolStripMenuItem(Lang.T("剪下"));
        cut.Click += (_, _) => { _clip = _steps[i].Clone(); _clipDir = _sc.Dir; BeginInvoke(() => Delete(i)); };
        menu.Items.Add(cut);
        bool canPaste = _clip != null && _kinds.Contains(_clip.Kind);
        string clipName = _clip != null ? "（" + Step.KindName(_clip.Kind) + "）" : "";
        var pasteUp = new ToolStripMenuItem(Lang.T("貼上至上一行") + clipName) { Enabled = canPaste };
        pasteUp.Click += (_, _) => BeginInvoke(() => Paste(i));
        var pasteDown = new ToolStripMenuItem(Lang.T("貼上至下一行") + clipName) { Enabled = canPaste };
        pasteDown.Click += (_, _) => BeginInvoke(() => Paste(i + 1));
        menu.Items.Add(pasteUp);
        menu.Items.Add(pasteDown);
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem Sub(string text, int pos)
        {
            var item = new ToolStripMenuItem(text);
            foreach (var k in _kinds)
            {
                var kind = k;
                var sub = new ToolStripMenuItem(Step.KindName(kind)) { BackColor = Ui.CardHi, ForeColor = Ui.Text };
                sub.Click += (_, _) => BeginInvoke(() => InsertStep(kind, pos));
                item.DropDownItems.Add(sub);
            }
            try { item.DropDown.Renderer = DarkTheme.MenuRenderer(); } catch { }
            item.DropDown.BackColor = Ui.CardHi;
            return item;
        }
        menu.Items.Add(Sub(Lang.T("加插動作至上一行"), i));
        menu.Items.Add(Sub(Lang.T("加插動作至下一行"), i + 1));
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(anchor, at);
    }

    /// <summary>喺行嘅空位撳右鍵都得。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < _rowControls.Count; i++)
        {
            var first = _rowControls[i].FirstOrDefault();
            if (first == null) continue;
            int top = first.Top - Ui.S(4), bottom = first.Top + RowH + Ui.S(4);
            if (e.Y >= top && e.Y <= bottom) { ShowInsertMenu(i, this, e.Location); return; }
        }
    }

    private Control[] BuildRow(Step st)
    {
        var list = new List<Control>
        {
            Ui.Text_("", Ui.TextFaint, Ui.F(9.75f, FontStyle.Bold), new Padding(0)),
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
                var btn = MouseButtonPicker(st);
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
            case StepKind.Drag:
            {
                var btn = new FancyButton { Text = st.Right ? Lang.T("右鍵") : Lang.T("左鍵"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(58), Ui.S(34)) };
                btn.Click += (_, _) => { st.Right = !st.Right; btn.Text = st.Right ? Lang.T("右鍵") : Lang.T("左鍵"); Changed(false); };
                var pos = L($"({st.X}, {st.Y}) → ({st.X2}, {st.Y2})", Ui.Text);
                var pick = new FancyButton { Text = Lang.T("揀位置"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(78), Ui.S(34)) };
                _tip.SetToolTip(pick, Lang.T("撳完 3 秒內將滑鼠移去起點，之後再 3 秒內移去終點"));
                pick.Click += (_, _) => PickDrag(pick, st, pos);
                var ms = new NumberBox(Ui.S(90)) { Minimum = 10m, Maximum = 60_000m, DecimalPlaces = 0, Increment = 50m };
                ms.Value = Math.Round(st.Hold * 1000m);
                ms.ValueChanged += (_, _) => { st.Hold = ms.Value / 1000m; Changed(false); };
                _tip.SetToolTip(ms, Lang.T("由起點拖去終點用幾耐"));
                list.AddRange(new Control[] { btn, pos, pick, L(Lang.T("用時")), ms, L("ms") });
                break;
            }
            case StepKind.Loop:
            {
                var count = new NumberBox(Ui.S(90)) { Minimum = 0, Maximum = 99999, Increment = 1 };
                count.Value = st.Count;
                count.ValueChanged += (_, _) => { st.Count = (int)count.Value; Changed(false); };
                _tip.SetToolTip(count, Lang.T("0 = 一直重複，直到撳停止"));
                var block = new LoopBlock(_sc, st.Body, loopStep: st);
                var addCond = new FancyButton { Text = Lang.T("+ 停止條件 ▾"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
                addCond.Size = new Size(Ui.S(120), Ui.S(32));
                _tip.SetToolTip(addCond, Lang.T("加時限或者指定時間，到咗就停呢個循環"));
                addCond.Click += (_, _) => block.ShowAddMenu(addCond);
                list.Insert(0 + 2, FoldButton(st, () => Lang.F("{0} 個步驟", st.Body.Count)));
                list.AddRange(new Control[] { L(Lang.T("重複")), count, L(Lang.T("次（0 = 一直重複）"), Ui.TextFaint), addCond });
                if (st.Collapsed) list.Add(L(Lang.F("（已摺埋 · {0} 個步驟）", st.Body.Count), Ui.TextFaint));
                list.Add(block);
                break;
            }
            case StepKind.StopScript:
            {
                list.Add(L(Lang.T("停晒成個腳本（包括 IF 框）"), Ui.TextFaint));
                break;
            }
            case StepKind.Thread:
            {
                list.AddRange(new Control[] { L(Lang.T("同時行下面嘅步驟，主線程唔使等，即刻做下一步"), Ui.TextFaint),
                    new LoopBlock(_sc, st.Body, thread: true) });
                break;
            }
            case StepKind.System:
                BuildSystemRow(st, list);
                break;
            case StepKind.Text:
            {
                var tb = new TextBox { Text = st.Target, MaxLength = 2000 };
                var host = new InputHost(tb, Ui.S(320));
                _tip.SetToolTip(tb, Lang.T("要輸入嘅文字（中文都得）"));
                tb.TextChanged += (_, _) => { st.Target = tb.Text; Changed(false); };
                list.Add(host);
                break;
            }
            case StepKind.Scroll:
            {
                var mb = MouseButtonPicker(st);
                var dir = new FancyButton { Text = st.Count >= 0 ? Lang.T("向上") : Lang.T("向下"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(66), Ui.S(34)) };
                var n = new NumberBox(Ui.S(80)) { Minimum = 1, Maximum = 100, Increment = 1 };
                n.Value = Math.Max(1, Math.Abs(st.Count));
                void Apply() { st.Count = (int)n.Value * (dir.Text == Lang.T("向上") ? 1 : -1); Changed(false); }
                dir.Click += (_, _) => { dir.Text = dir.Text == Lang.T("向上") ? Lang.T("向下") : Lang.T("向上"); Apply(); };
                n.ValueChanged += (_, _) => Apply();
                var x = new NumberBox(Ui.S(82)) { Minimum = -10000, Maximum = 20000 };
                x.Value = st.X;
                x.ValueChanged += (_, _) => { st.X = (int)x.Value; Changed(false); };
                var y = new NumberBox(Ui.S(82)) { Minimum = -10000, Maximum = 20000 };
                y.Value = st.Y;
                y.ValueChanged += (_, _) => { st.Y = (int)y.Value; Changed(false); };
                var pickPos = new FancyButton { Text = Lang.T("座標"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(58), Ui.S(34)) };
                _tip.SetToolTip(pickPos, Lang.T("撳完 3 秒內將滑鼠移去目標位置"));
                pickPos.Click += (_, _) => PickPosition(pickPos, x, y);
                list.AddRange(new Control[] { mb, dir, n, L(Lang.T("格")), L("X"), x, L("Y"), y, pickPos });
                break;
            }
            case StepKind.Parallel:
            {
                if (st.Branches.Count < 2) { while (st.Branches.Count < 2) st.Branches.Add(new List<Step>()); }
                list.Insert(2, FoldButton(st, () => Lang.F("{0} 條支線", st.Branches.Count)));
                list.Add(L(st.Collapsed ? Lang.F("（已摺埋 · {0} 條支線，{1} 個步驟）", st.Branches.Count, st.Branches.Sum(x => x.Count))
                                        : Lang.T("下面幾條支線一齊行，全部行完先做下一步"), Ui.TextFaint));
                list.Add(new ParallelBlock(_sc, st));
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
                var rnd = new ToggleChip(Lang.T("隨機")) { Checked = st.Random };
                rnd.Size = new Size(rnd.Width, Ui.S(30));
                _tip.SetToolTip(rnd, Lang.T("每次喺兩個時間之間隨機揀一個（似真人啲）"));
                rnd.CheckedChanged += () => { st.Random = rnd.Checked; if (st.Random && st.Seconds2 <= st.Seconds) st.Seconds2 = st.Seconds * 2; BeginInvoke(() => RebuildRow(st)); };
                list.AddRange(new Control[] { ms, L("ms"), rnd });
                if (st.Random)
                {
                    var ms2 = new NumberBox(Ui.S(110)) { Minimum = 1m, Maximum = 3_600_000m, DecimalPlaces = 0, Increment = 100m };
                    ms2.Value = Math.Round(st.Seconds2 * 1000m);
                    ms2.ValueChanged += (_, _) => { st.Seconds2 = ms2.Value / 1000m; Changed(false); };
                    list.AddRange(new Control[] { L(Lang.T("至")), ms2, L("ms") });
                }
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
        var up = SmallButton("↑", Lang.T("向上移"), () => MoveStep(_steps.IndexOf(st), -1));
        var down = SmallButton("↓", Lang.T("向下移"), () => MoveStep(_steps.IndexOf(st), 1));
        var del = SmallButton("✕", Lang.T("刪除呢個步驟"), () => Delete(_steps.IndexOf(st)));
        list.Add(del);
        list.Add(down);
        list.Add(up);
        return list.ToArray();
    }

    /// <summary>「撳住 / 按下 / 彈起」掣；撳住先會出秒數。</summary>
    /// <summary>呢步之前（同一清單前面，或者成個腳本任何地方）有冇「按下」同一個鍵 / 滑鼠鍵。</summary>
    private bool HasDownBefore(Step st)
    {
        bool Match(Step x) => x != st && x.Mode == PressMode.Down && x.Kind == st.Kind &&
                              (st.Kind == StepKind.Key ? x.Vk == st.Vk : x.Right == st.Right);
        int i = _steps.IndexOf(st);
        if (i > 0 && _steps.Take(i).Any(Match)) return true;
        IEnumerable<Step> All(IEnumerable<Step> steps) =>
            steps.SelectMany(x => new[] { x }.Concat(x.Kids().SelectMany(All)));
        return All(_sc.Steps).Concat(_sc.Ifs.SelectMany(b => All(b.Steps))).Any(Match);
    }

    private void AddModeControls(List<Control> list, Step st)
    {
        // 下拉式選單：撳住 / 按下 / 彈起
        var mode = new FancyButton { Text = Step.ModeName(st.Mode) + " ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
        mode.Size = new Size(Math.Max(Ui.S(78), mode.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        _tip.SetToolTip(mode, Lang.T("撳住：按下 → 等 → 彈起\n按下：淨係按落去（之後用「彈起」放開）\n彈起：淨係放開"));
        mode.Click += (_, _) =>
        {
            var menu = new ContextMenuStrip();
            try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
            menu.BackColor = Ui.CardHi;
            menu.ForeColor = Ui.Text;
            foreach (var m in new[] { PressMode.Hold, PressMode.Down, PressMode.Up })
            {
                var item = new ToolStripMenuItem(Step.ModeName(m)) { Checked = st.Mode == m, ForeColor = Ui.Text };
                var chosen = m;
                item.Click += (_, _) => BeginInvoke(() =>
                {
                    if (st.Mode == chosen) return;
                    if (chosen == PressMode.Up && !HasDownBefore(st))
                        MessageBox.Show(FindForm(),
                            Lang.T("提醒：前面冇「按下」同一個鍵 / 滑鼠鍵嘅步驟，「彈起」可能冇效果。\n通常做法：先加一步「按下」，之後先「彈起」（例如拖拉）。"),
                            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    st.Mode = chosen;
                    RebuildRow(st);
                });
                menu.Items.Add(item);
            }
            menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
            menu.Show(mode, new Point(0, mode.Height + Ui.S(2)));
        };
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

    /// <summary>背景執行但搵唔到程式視窗：揀唔到相對座標。</summary>
    public static void NoWindow(Control c) =>
        MessageBox.Show(c.FindForm(), Lang.T("背景執行：搵唔到揀咗嘅程式視窗，請先開咗個程式，或者喺上面重新揀視窗。"), Program.AppName,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>拖拉：3 秒後記起點，再 3 秒後記終點。</summary>
    private void PickDrag(FancyButton btn, Step st, Label pos)
    {
        int left = 3, stage = 0;
        Point a = Point.Empty;
        btn.Enabled = false;
        btn.Text = Lang.T("起點") + " " + left;
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            if (--left > 0) { btn.Text = (stage == 0 ? Lang.T("起點") : Lang.T("終點")) + " " + left; return; }
            var p = ScriptCoords.FromScreen(_sc, Cursor.Position);
            if (p == null)
            {
                t.Stop(); t.Dispose();
                btn.Text = Lang.T("揀位置"); btn.Enabled = true;
                NoWindow(btn);
                return;
            }
            if (stage == 0)
            {
                a = p.Value;
                stage = 1;
                left = 3;
                btn.Text = Lang.T("終點") + " " + left;
                return;
            }
            t.Stop(); t.Dispose();
            st.X = a.X; st.Y = a.Y; st.X2 = p.Value.X; st.Y2 = p.Value.Y;
            pos.Text = $"({st.X}, {st.Y}) → ({st.X2}, {st.Y2})";
            btn.Text = Lang.T("揀位置");
            btn.Enabled = true;
            Changed(false);
            Relayout();
        };
        t.Start();
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
            var p = ScriptCoords.FromScreen(_sc, Cursor.Position);
            if (p == null) NoWindow(btn);
            else { x.Value = p.Value.X; y.Value = p.Value.Y; }
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
            // 左邊：編號、類型、參數（太長就換行）；最後三個（↑ ↓ ✕）靠右優先；IF / 循環框喺下面成行
            int lines = PlaceHead(i, w, y, true);
            int headH = lines * RowH + (lines - 1) * LineGap;
            foreach (var c in row)
            {
                if (c is not (IfPanel or LoopBlock or ParallelBlock)) continue;
                c.Visible = !Folded(i);
                if (!c.Visible) continue;
                int bw = Math.Max(50, w - BlockIndent);
                var bs = c.GetPreferredSize(new Size(bw, 0));
                c.SetBounds(BlockIndent, y + headH + Ui.S(6), bw, bs.Height);
            }
            y += RowHeight(i, w) + Gap;
        }
        if (_steps.Count == 0) y = Ui.S(24) + Gap;
        int end = PlaceButtons(w, y + Ui.S(8), true);
        _status.Location = new Point(0, end + Ui.S(12));
    }

    /// <summary>由某個控件開始，叫成頁（連入面一層層嘅 IF）重新排位。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint h, int msg, nint w, nint l);

    internal static void RelayoutFrom(Control from)
    {
        LayoutVer.MarkDirty(from);
        for (Control? p = from.Parent; p != null; p = p.Parent)
        {
            if (p is CardStack)
            {
                // 排位期間唔好畫（排完先一次過畫），步驟多都唔會一格格咁閃
                bool redrawOff = p.IsHandleCreated;
                if (redrawOff) SendMessage(p.Handle, 0x000B /*WM_SETREDRAW*/, 0, 0);
                try
                {
                    from.PerformLayout();
                    p.PerformLayout();
                    Deep(p);
                }
                finally
                {
                    if (redrawOff) SendMessage(p.Handle, 0x000B, 1, 0);
                    p.Invalidate(true);
                }
                return;
            }
        }
        from.PerformLayout();

        static void Deep(Control c)
        {
            foreach (Control ch in c.Controls)
            {
                if (ch is StepList or IfPanel or LoopBlock or ParallelBlock or CardPanel)
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
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Ui.OnTint(Ui.Accent),
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
internal sealed class IfPanel : TintPanel, ISizeCache
{
    private readonly Script _sc;
    private readonly IfBlock _b;
    private readonly List<List<Control>> _condRows = new(); // 每個條件一行
    private readonly List<Control> _addRow = new();          // 「+ 條件」
    private readonly List<Control> _opts = new();
    private IEnumerable<Control> CondControls => _condRows.SelectMany(r => r).Concat(_addRow).Concat(_opts);
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
        string t = Lang.F("如果{0} · {1} 個步驟", string.Join(_b.Or ? Lang.T(" 或者 ") : Lang.T(" 而且 "), _b.AllConds.Select(c => IfBlock.KindName(c.Kind))), _b.Steps.Count);
        if (_lastStatus.Length > 0) t += " · " + _lastStatus;
        _summary.Text = _b.Collapsed ? t : "";
    }

    private void ApplyFold()
    {
        _fold.Text = _b.Collapsed ? "▸" : "▾";
        foreach (var c in CondControls) c.Visible = !_b.Collapsed;
        _steps.Visible = !_b.Collapsed;
        UpdateSummary();
        Invalidate();
        Relayout();
    }

    private void Save() { LayoutVer.MarkDirty(this); _sc.Save(); }

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
        foreach (var c in CondControls.ToList()) { Controls.Remove(c); c.Dispose(); }
        _condRows.Clear();
        _addRow.Clear();
        _opts.Clear();

        var conds = _b.AllConds.ToList();
        for (int i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            if (_nested && c.Kind == CondKind.Countdown) c.Kind = CondKind.ImageFound;
            var row = new List<Control>();
            if (i == 0) row.Add(L(Lang.T("如果"), Ui.Text));
            else
            {
                // 而且 / 或者：撳一下切換（全部條件共用）
                var logic = new FancyButton { Text = (_b.Or ? Lang.T("或者") : Lang.T("而且")) + " ⇄", Kind = BtnKind.Ghost, Font = Ui.F(9.5f, FontStyle.Bold) };
                logic.Size = new Size(Math.Max(Ui.S(78), TextRenderer.MeasureText(logic.Text, logic.Font).Width + Ui.S(28)), Ui.S(34));
                _tip.SetToolTip(logic, Lang.T("而且 = 全部條件都成立先做；或者 = 任何一個成立就做。撳一下切換。"));
                logic.Click += (_, _) => { _b.Or = !_b.Or; Save(); BeginInvoke(RebuildCond); };
                row.Add(logic);
            }
            var kindBtn = new FancyButton { Text = IfBlock.KindName(c.Kind) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
            kindBtn.Size = new Size(Ui.S(150), Ui.S(34));
            var cc = c;
            kindBtn.Click += (_, _) => ShowKindMenu(kindBtn, cc);
            row.Add(kindBtn);
            AddParams(c, row);
            if (i > 0)
            {
                var del = new FancyButton { Text = "✕", Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(34), Ui.S(34)) };
                _tip.SetToolTip(del, Lang.T("刪除呢個條件"));
                del.Click += (_, _) => { ScriptUndo.Push(_sc); _b.More.Remove(cc); Save(); BeginInvoke(RebuildCond); };
                row.Add(del);
            }
            _condRows.Add(row);
        }
        var add = new FancyButton { Text = Lang.T("+ 條件"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
        add.Size = new Size(Ui.S(96), Ui.S(32));
        _tip.SetToolTip(add, Lang.T("加多個條件，可以揀「而且」或者「或者」"));
        add.Click += (_, _) => { ScriptUndo.Push(_sc); _b.More.Add(new IfBlock { Kind = CondKind.ImageFound }); Save(); BeginInvoke(RebuildCond); };
        _addRow.Add(add);

        // 第二行：檢查間隔、觸發後
        if (_b.UsesInterval && !_nested)
        {
            _opts.Add(L(Lang.T("每")));
            _opts.Add(Num(Ui.S(84), 0.1m, 60m, _b.Interval, 1, 0.1m, v => _b.Interval = v));
            _opts.Add(L(Lang.T("秒檢查一次")));
        }
        _opts.Add(L(Lang.T("然後做："), Ui.Text));

        foreach (var c in CondControls)
        {
            c.Visible = !_b.Collapsed;
            Controls.Add(c);
        }
        ResumeLayout(false);
        if (_ready) Relayout();
    }

    /// <summary>揀條件種類（每個條件自己一個選單）。</summary>
    private void ShowKindMenu(FancyButton btn, IfBlock c)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        foreach (CondKind k in Enum.GetValues(typeof(CondKind)))
        {
            if (_nested && k == CondKind.Countdown) continue;
            var kind = k;
            var item = new ToolStripMenuItem(IfBlock.KindName(k)) { Checked = c.Kind == k };
            item.Click += (_, _) =>
            {
                if (c.Kind == kind) return;
                c.Kind = kind;
                Save();
                BeginInvoke(RebuildCond);
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { try { if (!btn.IsDisposed) btn.BeginInvoke(() => menu.Dispose()); } catch { } };
        menu.Show(btn, new Point(0, btn.Height + Ui.S(2)));
    }

    /// <summary>一個條件嘅設定控件。</summary>
    private void AddParams(IfBlock c, List<Control> row)
    {
        switch (c.Kind)
        {
            case CondKind.ImageFound:
            case CondKind.ImageNotFound:
            {
                var tile = new ImageStrip(_sc, () => c.Image, v => { c.Image = v; Save(); });
                row.Add(tile);
                row.Add(L(Lang.T("相似度")));
                row.Add(Num(Ui.S(84), 10, 100, c.Similarity, 0, 1, v => c.Similarity = (int)v));
                row.Add(L("%"));
                if (c.Kind == CondKind.ImageFound)
                {
                    var chip = new ToggleChip(Lang.T("搵到就點擊")) { Checked = c.ClickFound };
                    chip.Size = new Size(chip.Width, Ui.S(30));
                    _tip.SetToolTip(chip, Lang.T("搵到嗰陣順手喺圖片中心左鍵點一下，再做「然後做」嘅步驟"));
                    chip.CheckedChanged += () => { c.ClickFound = chip.Checked; Save(); };
                    row.Add(chip);
                }
                break;
            }
            case CondKind.PixelColor:
            {
                var x = Num(Ui.S(82), -10000, 20000, c.X, 0, 1, v => c.X = (int)v);
                var y = Num(Ui.S(82), -10000, 20000, c.Y, 0, 1, v => c.Y = (int)v);
                var swatch = new ColorSwatch(c.Color);
                _tip.SetToolTip(swatch, Lang.T("撳一下自己揀顏色"));
                swatch.ColorChanged += col => { c.Color = col; Save(); };
                var pick = new FancyButton { Text = Lang.T("座標"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(58), Ui.S(34)) };
                _tip.SetToolTip(pick, Lang.T("撳完 3 秒內將滑鼠移去目標位置，會一齊記低嗰點嘅顏色"));
                pick.Click += (_, _) => Countdown(pick, Lang.T("座標"), () =>
                {
                    var scr = Cursor.Position;
                    var p = ScriptCoords.FromScreen(_sc, scr);
                    if (p == null) { StepList.NoWindow(pick); return; }
                    x.Value = p.Value.X;
                    y.Value = p.Value.Y;
                    var px = ScreenSearch.PixelAt(scr.X, scr.Y);
                    swatch.Value = (px.R << 16) | (px.G << 8) | px.B;
                });
                row.AddRange(new Control[] { L("X"), x, L("Y"), y, pick, L(Lang.T("顏色")), swatch, L(Lang.T("容差")),
                    Num(Ui.S(78), 0, 255, c.Tolerance, 0, 1, v => c.Tolerance = (int)v) });
                break;
            }
            case CondKind.Time:
            {
                TimeSpan.TryParse(c.Time, CultureInfo.InvariantCulture, out var ts);
                int hh = ts.Hours, mm = ts.Minutes, ss = ts.Seconds;
                void SetTime() { c.Time = $"{hh:00}:{mm:00}:{ss:00}"; }
                row.AddRange(new Control[]
                {
                    Num(Ui.S(70), 0, 23, hh, 0, 1, v => { hh = (int)v; SetTime(); }), L(Lang.T("時")),
                    Num(Ui.S(70), 0, 59, mm, 0, 1, v => { mm = (int)v; SetTime(); }), L(Lang.T("分")),
                    Num(Ui.S(70), 0, 59, ss, 0, 1, v => { ss = (int)v; SetTime(); }), L(Lang.T("秒")),
                    L(_nested ? Lang.T("（行到呢步時已經過咗呢個時間）") : Lang.T("（每日嗰個時間觸發）"), Ui.TextFaint)
                });
                break;
            }
            case CondKind.Countdown:
                row.AddRange(new Control[]
                {
                    Num(Ui.S(100), 1, 86400, c.Seconds, 1, 1, v => c.Seconds = v), L(Lang.T("秒")),
                    L(Lang.T("（由開始 / 上次觸發計起）"), Ui.TextFaint)
                });
                break;
            case CondKind.Elapsed:
                row.AddRange(new Control[]
                {
                    L(Lang.T("腳本運行咗")), Num(Ui.S(110), 1, 8_640_000, c.ElapsedSec, 0, 1, v => c.ElapsedSec = v), L(Lang.T("秒")),
                    L(_nested ? Lang.T("（行到呢步時已經夠鐘）") : Lang.T("（到咗觸發一次；想停腳本就加「停止腳本」）"), Ui.TextFaint)
                });
                break;
        }
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
    private int CondRowCount => Math.Max(1, _condRows.Count) + 2; // 條件行 + 「+ 條件」 + 選項
    private int StepsY => Top0 + RowH * CondRowCount + Gap * (CondRowCount - 1) + Ui.S(14);
    private int StepsX => Pad + Ui.S(20);

    // 量度結果暫存：版本冇變（冇改過結構）同闊度一樣就直接用，費事一層層重新計
    private int _pv = -1, _pk = int.MinValue;
    private Size _ps;
    public void Dirty() => _pv = -1;

    public override Size GetPreferredSize(Size proposed)
    {
        int key = proposed.Width > 0 ? proposed.Width : -Width - 1;
        if (_pv == LayoutVer.V && _pk == key) return _ps;
        _ps = Measure(proposed);
        _pv = LayoutVer.V;
        _pk = key;
        return _ps;
    }

    private Size Measure(Size proposed)
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
        int yy = Top0;
        foreach (var r in _condRows) { Flow(r, yy); yy += RowH + Gap; }
        Flow(_addRow, yy); yy += RowH + Gap;
        Flow(_opts, yy);
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

/// <summary>同時運行：幾條支線，每條一個步驟清單；可以加 / 刪支線（最少 2 條）。</summary>
internal sealed class ParallelBlock : TintPanel, ISizeCache
{
    public static readonly Color Hue = Color.FromArgb(56, 182, 220);
    private readonly Script _sc;
    private readonly Step _st;
    private readonly List<(Label Title, FancyButton? Del, StepList List)> _rows = new();
    private readonly FancyButton _add;
    private const int MaxBranches = 8;

    public ParallelBlock(Script sc, Step st) : base(Hue, 0.10f)
    {
        _sc = sc;
        _st = st;
        _add = new FancyButton { Text = Lang.T("+ 新增支線"), Kind = BtnKind.Ghost, Dashed = true, Font = Ui.F(9.5f) };
        _add.Size = new Size(Ui.S(130), Ui.S(32));
        _add.Click += (_, _) =>
        {
            if (_st.Branches.Count >= MaxBranches) return;
            ScriptUndo.Push(_sc);
            _st.Branches.Add(new List<Step>());
            _sc.Save();
            Build();
        };
        Controls.Add(_add);
        Build();
    }

    private void Build()
    {
        SuspendLayout();
        foreach (var r in _rows)
        {
            Controls.Remove(r.Title); r.Title.Dispose();
            if (r.Del != null) { Controls.Remove(r.Del); r.Del.Dispose(); }
            Controls.Remove(r.List); r.List.Dispose();
        }
        _rows.Clear();
        for (int i = 0; i < _st.Branches.Count; i++)
        {
            var title = Ui.Text_(Lang.F("支線 {0}", i + 1), Hue, Ui.F(9.75f, FontStyle.Bold), new Padding(0));
            FancyButton? del = null;
            if (_st.Branches.Count > 2)
            {
                int idx = i;
                del = new FancyButton { Text = Lang.T("刪除支線"), Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(84), Ui.S(28)) };
                del.Click += (_, _) =>
                {
                    if (_st.Branches[idx].Count > 0 && MessageBox.Show(FindForm(), Lang.F("刪除支線 {0} 同入面嘅步驟？", idx + 1), Program.AppName,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    ScriptUndo.Push(_sc);
                    _st.Branches.RemoveAt(idx);
                    _sc.Save();
                    BeginInvoke(Build);
                };
                Controls.Add(del);
            }
            var list = new StepList(_sc, _st.Branches[i], nestedIf: true) { Dock = DockStyle.None };
            Controls.Add(title);
            Controls.Add(list);
            _rows.Add((title, del, list));
        }
        _add.Enabled = _st.Branches.Count < MaxBranches;
        ResumeLayout(false);
        if (IsHandleCreated) StepList.RelayoutFrom(this);
    }

    private int Indent => Pad + Ui.S(14);
    private int HeadH => Ui.S(30);

    // 量度結果暫存：版本冇變（冇改過結構）同闊度一樣就直接用，費事一層層重新計
    private int _pv = -1, _pk = int.MinValue;
    private Size _ps;
    public void Dirty() => _pv = -1;

    public override Size GetPreferredSize(Size proposed)
    {
        int key = proposed.Width > 0 ? proposed.Width : -Width - 1;
        if (_pv == LayoutVer.V && _pk == key) return _ps;
        _ps = Measure(proposed);
        _pv = LayoutVer.V;
        _pk = key;
        return _ps;
    }

    private Size Measure(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        int lw = Math.Max(50, w - Indent - Pad);
        int h = Pad;
        foreach (var r in _rows) h += HeadH + r.List.GetPreferredSize(new Size(lw, 0)).Height + Ui.S(6);
        return new Size(w, h + Ui.S(32) + Pad);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_add == null) return;
        int lw = Math.Max(50, ClientSize.Width - Indent - Pad);
        int y = Pad;
        foreach (var r in _rows)
        {
            r.Title.Location = new Point(Indent, y + (HeadH - r.Title.PreferredSize.Height) / 2);
            if (r.Del != null) r.Del.Location = new Point(Indent + r.Title.PreferredSize.Width + Ui.S(12), y + (HeadH - r.Del.Height) / 2);
            y += HeadH;
            int lh = r.List.GetPreferredSize(new Size(lw, 0)).Height;
            r.List.SetBounds(Indent, y, lw, lh);
            y += lh + Ui.S(6);
        }
        _add.Location = new Point(Indent, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var b = new SolidBrush(Hue);
        e.Graphics.FillRectangle(b, Pad, Pad, Ui.S(3), Math.Max(1, Height - Pad * 2));
    }
}

/// <summary>循環步驟入面嘅步驟清單（左邊一條色線）。</summary>
internal sealed class LoopBlock : TintPanel, ISizeCache
{
    private readonly StepList _steps;

    public static readonly Color ThreadHue = Color.FromArgb(240, 160, 48);
    private readonly Color _hue;

    private readonly Script _sc;
    private readonly Step? _loop;                 // 循環步驟（有就顯示停止條件）
    private readonly List<Control> _head = new(); // 停止條件一行
    private readonly ToolTip _tip = new();

    /// <param name="thread">true = 新線程嘅步驟框（唔係循環，冇「中斷循環」）。</param>
    public LoopBlock(Script sc, List<Step> body, bool thread = false, Step? loopStep = null) : base(thread ? ThreadHue : Ui.Success, 0.10f)
    {
        _sc = sc;
        _loop = loopStep;
        _hue = thread ? ThreadHue : Ui.Success;
        _steps = new StepList(sc, body, nestedIf: true, inLoop: !thread) { Dock = DockStyle.None };
        Controls.Add(_steps);
        BuildHead();
    }

    /// <summary>「+ 停止條件」選單：時限 / 指定時間（已經加咗嘅會灰咗）。</summary>
    public void ShowAddMenu(Control anchor)
    {
        if (_loop == null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        var lim = new ToolStripMenuItem(Lang.T("時限（秒）")) { Enabled = _loop.LimitSec <= 0 };
        lim.Click += (_, _) => { ScriptUndo.Push(_sc); _loop.LimitSec = 60m; _sc.Save(); BeginInvoke(BuildHead); };
        var until = new ToolStripMenuItem(Lang.T("到指定時間停")) { Enabled = _loop.Until.Length == 0 };
        until.Click += (_, _) => { ScriptUndo.Push(_sc); _loop.Until = DateTime.Now.AddHours(1).ToString("HH:00:00"); _sc.Save(); BeginInvoke(BuildHead); };
        menu.Items.Add(lim);
        menu.Items.Add(until);
        menu.Closed += (_, _) => { try { if (!anchor.IsDisposed) anchor.BeginInvoke(() => menu.Dispose()); } catch { } };
        menu.Show(anchor, new Point(0, anchor.Height + Ui.S(2)));
    }

    private Label L(string t, Color? c = null) => Ui.Text_(t, c ?? Ui.TextDim, Ui.F(9.75f), new Padding(0));

    private FancyButton X(Action remove)
    {
        var b = new FancyButton { Text = "✕", Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(30), Ui.S(30)) };
        _tip.SetToolTip(b, Lang.T("刪除呢個停止條件"));
        b.Click += (_, _) => { ScriptUndo.Push(_sc); remove(); _sc.Save(); BeginInvoke(BuildHead); };
        return b;
    }

    /// <summary>循環框頂嗰行：已經加咗嘅停止條件。</summary>
    private void BuildHead()
    {
        SuspendLayout();
        foreach (var c in _head) { Controls.Remove(c); c.Dispose(); }
        _head.Clear();
        var st = _loop;
        if (st != null && (st.LimitSec > 0 || st.Until.Length > 0))
        {
            _head.Add(L(Lang.T("停止條件："), Ui.Text));
            if (st.LimitSec > 0)
            {
                var limit = new NumberBox(Ui.S(100)) { Minimum = 1, Maximum = 8_640_000, DecimalPlaces = 0, Increment = 1 };
                limit.Value = Math.Max(1, Math.Round(st.LimitSec));
                limit.ValueChanged += (_, _) => { st.LimitSec = limit.Value; _sc.Save(); };
                _tip.SetToolTip(limit, Lang.T("循環行咗幾多秒就停（0 = 唔限）"));
                _head.AddRange(new Control[] { L(Lang.T("時限")), limit, L(Lang.T("秒")), X(() => st.LimitSec = 0) });
            }
            if (st.Until.Length > 0)
            {
                TimeSpan.TryParse(st.Until, CultureInfo.InvariantCulture, out var uts);
                var hh = new NumberBox(Ui.S(64)) { Minimum = 0, Maximum = 23, Increment = 1 };
                var mm = new NumberBox(Ui.S(64)) { Minimum = 0, Maximum = 59, Increment = 1 };
                hh.Value = uts.Hours; mm.Value = uts.Minutes;
                void Set() { st.Until = $"{(int)hh.Value:00}:{(int)mm.Value:00}:00"; _sc.Save(); }
                hh.ValueChanged += (_, _) => Set();
                mm.ValueChanged += (_, _) => Set();
                _tip.SetToolTip(hh, Lang.T("到咗呢個現實時間就停循環（如果已經過咗，就係聽日嗰個時間）"));
                _head.AddRange(new Control[] { L(Lang.T("到")), hh, L(Lang.T("時")), mm, L(Lang.T("分")), L(Lang.T("就停")), X(() => st.Until = "") });
            }
            if (_head.Count > 1) _head.Add(L(Lang.T("（任何一個先到就停）"), Ui.TextFaint));
        }
        foreach (var c in _head) Controls.Add(c);
        ResumeLayout(false);
        if (IsHandleCreated) StepList.RelayoutFrom(this);
    }

    private int Indent => Pad + Ui.S(14);
    private int HeadH => _head.Count > 0 ? Ui.S(34) + Ui.S(8) : 0;

    // 量度結果暫存：版本冇變（冇改過結構）同闊度一樣就直接用，費事一層層重新計
    private int _pv = -1, _pk = int.MinValue;
    private Size _ps;
    public void Dirty() => _pv = -1;

    public override Size GetPreferredSize(Size proposed)
    {
        int key = proposed.Width > 0 ? proposed.Width : -Width - 1;
        if (_pv == LayoutVer.V && _pk == key) return _ps;
        _ps = Measure(proposed);
        _pv = LayoutVer.V;
        _pk = key;
        return _ps;
    }

    private Size Measure(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        return new Size(w, Pad * 2 + HeadH + _steps.GetPreferredSize(new Size(Math.Max(50, w - Indent - Pad), 0)).Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_steps == null) return;
        int x = Indent, rh = Ui.S(34);
        foreach (var c in _head)
        {
            var sz = c is Label ? c.PreferredSize : c.Size;
            c.Location = new Point(x, Pad + (rh - sz.Height) / 2);
            x += sz.Width + Ui.S(8);
        }
        int w = Math.Max(50, ClientSize.Width - Indent - Pad);
        _steps.SetBounds(Indent, Pad + HeadH, w, _steps.GetPreferredSize(new Size(w, 0)).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var b = new SolidBrush(_hue);
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
        StepKind.Wait => st.Random ? $"{Math.Round(st.Seconds * 1000m)} – {Math.Round(st.Seconds2 * 1000m)} ms" + Lang.T("（隨機）") : $"{Math.Round(st.Seconds * 1000m)} ms",
        StepKind.Text => "「" + (st.Target.Length > 40 ? st.Target[..40] + "…" : st.Target).Replace("\n", " ") + "」",
        StepKind.Scroll => Lang.F("{0} {1} 格 ({2}, {3})", st.Count >= 0 ? Lang.T("向上") : Lang.T("向下"), Math.Abs(st.Count), st.X, st.Y),
        StepKind.Drag => $"{(st.Right ? Lang.T("右鍵") : Lang.T("左鍵"))} ({st.X}, {st.Y}) → ({st.X2}, {st.Y2}) · {Math.Round(st.Hold * 1000m)} ms",
        StepKind.Image => st.Image.Length == 0 ? Lang.T("（未揀圖）") : Lang.F("{0}相似度 {1}%，搵到最似嗰張就點", Count(st.Image), st.Similarity),
        StepKind.Loop => (st.Count == 0 ? Lang.T("一直重複") : Lang.F("重複 {0} 次", st.Count))
            + (st.LimitSec > 0 ? Lang.F("，最多 {0} 秒", D(st.LimitSec)) : "")
            + (st.Until.Length > 0 ? Lang.F("，到 {0} 停", st.Until.Length >= 5 ? st.Until[..5] : st.Until) : ""),
        StepKind.StopScript => Lang.T("停晒成個腳本"),
        StepKind.System => st.Sys switch
        {
            SysAction.OpenApp => Lang.F("打開 {0}", st.TargetName.Length > 0 ? st.TargetName : st.Target),
            SysAction.CloseApp => Lang.F("關閉 {0}", st.Target),
            SysAction.SwitchWindow => Lang.F("切換至 {0}", st.TargetName.Length > 0 ? $"{st.Target} - {st.TargetName}" : st.Target),
            SysAction.PlaySound => Lang.F("播放音效 {0}", st.TargetName) + (st.Repeat ? Lang.T("（一直重複）") : ""),
            _ => Lang.F("{0} 秒後關閉電腦", D(st.Seconds))
        },
        StepKind.Thread => Lang.F("同時行 {0} 個步驟（主線程唔使等）", st.Body.Count),
        StepKind.Parallel => Lang.F("{0} 條支線同時運行，全部完成先繼續", st.Branches.Count),
        StepKind.Break => Lang.T("跳出最近嗰個循環"),
        StepKind.If => st.Sub == null ? "" : Lang.F("{0}：如果{1}", st.Sub.Name, Cond(st.Sub, true)),
        _ => ""
    };

    private static string Count(string images)
    {
        int n = ImageList.Split(images).Count;
        return n > 1 ? Lang.F("{0} 張圖 · ", n) : "";
    }

    public static string Cond(IfBlock b, bool nested) =>
        b.More.Count == 0 ? One(b, nested)
        : string.Join(b.Or ? Lang.T(" 或者 ") : Lang.T(" 而且 "), b.AllConds.Select(c => One(c, nested)));

    private static string One(IfBlock b, bool nested) => b.Kind switch
    {
        CondKind.ImageFound => b.Image.Length == 0 ? Lang.T("畫面搵到圖（未揀圖）") : Lang.F("畫面搵到圖（{0}相似度 {1}%{2}）", Count(b.Image), b.Similarity, (b.ClickFound ? Lang.T("，搵到就點擊") : "")),
        CondKind.ImageNotFound => b.Image.Length == 0 ? Lang.T("畫面搵唔到圖（未揀圖）") : Lang.F("畫面搵唔到圖（{0}全部都搵唔到，相似度 {1}%）", Count(b.Image), b.Similarity),
        CondKind.PixelColor => Lang.F("({0}, {1}) 係 #{2:X6}（容差 {3}）", b.X, b.Y, b.Color, b.Tolerance),
        CondKind.Time => nested ? Lang.F("已經過咗 {0}", b.Time) : Lang.F("時間到 {0}（每日）", b.Time),
        CondKind.Elapsed => Lang.F("腳本運行咗 {0} 秒", D(b.ElapsedSec)),
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
            else if (st.Kind == StepKind.Thread)
                Add(st.Body, depth + 1, bars.Append(LoopBlock.ThreadHue).ToArray());
            else if (st.Kind == StepKind.Parallel)
                for (int b = 0; b < st.Branches.Count; b++)
                {
                    var bb = bars.Append(ParallelBlock.Hue).ToArray();
                    _lines.Add(new Line(depth + 1, StepKind.Parallel, b + 1, Lang.F("支線 {0}", b + 1), null, bb));
                    Add(st.Branches[b], depth + 2, bb);
                }
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
            var hue = ln.Kind switch { StepKind.Loop => Ui.Success, StepKind.Thread => LoopBlock.ThreadHue, StepKind.Parallel => ParallelBlock.Hue, StepKind.If => Ui.Accent, StepKind.Break => Ui.Danger, _ => Ui.Accent2 };
            var ks = TextRenderer.MeasureText(kind, bf);
            var kr = new RectangleF(x, y + (LineH - Ui.S(22)) / 2f, ks.Width + Ui.S(16), Ui.S(22));
            using (var path = Ui.Round(kr, Ui.S(11f)))
            using (var b = new SolidBrush(Ui.Alpha(hue, 45)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, kind, bf, Rectangle.Round(kr), Ui.OnTint(hue),
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


/// <summary>
/// 腳本座標：背景執行時用「相對程式視窗」座標（視窗移位都照用得），前景就用螢幕座標。
/// 開 / 閂背景執行時自動將所有座標換算。
/// </summary>
internal static class ScriptCoords
{
    private static void Each(List<Step> steps, Func<Point, Point> f)
    {
        foreach (var st in steps)
        {
            if (st.Kind is StepKind.Click or StepKind.Drag or StepKind.Scroll)
            {
                var a = f(new Point(st.X, st.Y)); st.X = a.X; st.Y = a.Y;
                if (st.Kind == StepKind.Drag) { var b = f(new Point(st.X2, st.Y2)); st.X2 = b.X; st.Y2 = b.Y; }
            }
            if (st.Sub != null) { Cond(st.Sub, f); Each(st.Sub.Steps, f); }
            Each(st.Body, f);
            foreach (var b in st.Branches) Each(b, f);
        }
    }

    private static void Cond(IfBlock b, Func<Point, Point> f)
    {
        foreach (var m in b.More) Cond(m, f);
        if (b.Kind != CondKind.PixelColor) return;
        var p = f(new Point(b.X, b.Y)); b.X = p.X; b.Y = p.Y;
    }

    private static void All(Script sc, Func<Point, Point> f)
    {
        Each(sc.Steps, f);
        foreach (var t in sc.Threads) Each(t.Steps, f);
        foreach (var b in sc.Ifs) { Cond(b, f); Each(b.Steps, f); }
    }

    /// <summary>記錄時視窗大細 → 而家視窗大細（按比例）。</summary>
    public static Point RefToClient(nint h, Point p, Size refSize)
    {
        if (refSize.Width <= 0 || refSize.Height <= 0) return p;
        var cs = Win.ClientSize(h);
        if (cs.Width <= 0 || cs.Height <= 0 || cs == refSize) return p;
        return new Point((int)Math.Round(p.X * (double)cs.Width / refSize.Width), (int)Math.Round(p.Y * (double)cs.Height / refSize.Height));
    }

    /// <summary>而家視窗大細 → 記錄時視窗大細（揀位置時用）。</summary>
    public static Point ClientToRef(nint h, Point p, Size refSize)
    {
        if (refSize.Width <= 0 || refSize.Height <= 0) return p;
        var cs = Win.ClientSize(h);
        if (cs.Width <= 0 || cs.Height <= 0 || cs == refSize) return p;
        return new Point((int)Math.Round(p.X * (double)refSize.Width / cs.Width), (int)Math.Round(p.Y * (double)refSize.Height / cs.Height));
    }

    /// <summary>未記錄視窗大細（舊腳本）：用而家個視窗大細做基準。</summary>
    public static void EnsureRef(Script sc)
    {
        if (!sc.Relative || (sc.RefW > 0 && sc.RefH > 0)) return;
        var h = Win.Find(sc);
        if (h == 0) return;
        var cs = Win.ClientSize(h);
        if (cs.Width <= 0 || cs.Height <= 0) return;
        sc.RefW = cs.Width;
        sc.RefH = cs.Height;
        sc.Save();
    }

    /// <summary>螢幕座標 → 相對視窗（要搵到個視窗先得）。記低而家視窗大細做比例基準。</summary>
    public static bool MakeRelative(Script sc)
    {
        if (sc.Relative) { EnsureRef(sc); return true; }
        var h = Win.Find(sc);
        if (h == 0) return false;
        All(sc, p => Win.ScreenToClientPt(h, p));
        var cs = Win.ClientSize(h);
        sc.RefW = Math.Max(0, cs.Width);
        sc.RefH = Math.Max(0, cs.Height);
        sc.Relative = true;
        sc.Save();
        return true;
    }

    /// <summary>相對視窗 → 螢幕座標。</summary>
    public static bool MakeAbsolute(Script sc)
    {
        if (!sc.Relative) return true;
        var h = Win.Find(sc);
        if (h == 0) return false;
        var refSize = new Size(sc.RefW, sc.RefH);
        All(sc, p => Win.ClientToScreenPt(h, RefToClient(h, p, refSize)));
        sc.Relative = false;
        sc.RefW = sc.RefH = 0;
        sc.Save();
        return true;
    }

    /// <summary>撳「座標」揀到嘅螢幕位置 → 腳本座標；背景執行但搵唔到視窗就返 null。</summary>
    public static Point? FromScreen(Script sc, Point screen)
    {
        if (sc.TargetProcess.Length == 0) return sc.Relative ? null : screen;
        var h = Win.Find(sc);
        if (h == 0 || !MakeRelative(sc)) return null;
        return ClientToRef(h, Win.ScreenToClientPt(h, screen), new Size(sc.RefW, sc.RefH));
    }
}

/// <summary>運行次數選單（腳本清單、預覽頁、修改頁共用）。</summary>
internal static class RunsMenu
{
    public static void Show(Control anchor, Point at, Script sc, Action changed)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        void Item(int n)
        {
            var it = new ToolStripMenuItem(Script.RunsName(n)) { Checked = sc.Runs == n };
            it.Click += (_, _) => { sc.Runs = n; sc.Save(); changed(); };
            menu.Items.Add(it);
        }
        foreach (var n in new[] { 1, 3, 5, 10, 0 }) Item(n);
        menu.Items.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem(Lang.T("自訂次數…"));
        custom.Click += (_, _) => anchor.BeginInvoke(() =>
        {
            int? n = AskCount(anchor.FindForm(), sc.Runs <= 1 ? 2 : sc.Runs);
            if (n == null) return;
            sc.Runs = n.Value;
            sc.Save();
            changed();
        });
        menu.Items.Add(custom);
        menu.Closed += (_, _) => { try { if (!anchor.IsDisposed) anchor.BeginInvoke(() => menu.Dispose()); } catch { } };
        menu.Show(anchor, at);
    }

    private static int? AskCount(Form? owner, int value)
    {
        using var f = new Form
        {
            Text = Lang.T("運行次數"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Ui.Bg, ForeColor = Ui.Text, Font = Ui.F(9.75f),
            ClientSize = new Size(Ui.S(300), Ui.S(130))
        };
        var lbl = new Label { Text = Lang.T("運行幾多次？（0 = 一直運行）"), AutoSize = true, ForeColor = Ui.TextDim, Location = new Point(Ui.S(20), Ui.S(18)) };
        var num = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = Math.Clamp(value, 0, 99999), Location = new Point(Ui.S(20), Ui.S(46)), Width = Ui.S(120), BackColor = Ui.CardHi, ForeColor = Ui.Text };
        var ok = new FancyButton { Text = Lang.T("確定"), Kind = BtnKind.Success, Size = new Size(Ui.S(90), Ui.S(34)), Location = new Point(Ui.S(190), Ui.S(84)) };
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.AcceptButton = ok;
        f.Controls.AddRange(new Control[] { lbl, num, ok });
        return f.ShowDialog(owner) == DialogResult.OK ? (int)num.Value : null;
    }
}

/// <summary>「背景執行」開關 + 揀視窗掣（預覽頁同修改頁共用）。揀視窗前景 / 背景都用得。</summary>
internal sealed class BgControls
{
    public readonly ToggleChip Toggle;
    public readonly FancyButton Pick;
    public readonly ToggleChip Fake;
    private readonly Script _sc;
    private readonly ToolTip _tip;
    /// <summary>設定或者座標改咗：外面要重新整個畫面。</summary>
    public event Action? Changed;
    public Action? Relayout;

    public BgControls(Script sc, ToolTip tip)
    {
        _sc = sc;
        _tip = tip;
        Toggle = new ToggleChip(Lang.T("背景執行")) { Checked = sc.Background };
        Toggle.Size = new Size(Toggle.Width + Ui.S(8), Ui.S(34));
        _tip.SetToolTip(Toggle, Lang.T("開咗之後，腳本淨係喺揀咗嘅程式視窗入面運行，你切去其他視窗都照行。\n") +
                                Lang.T("注意：部分遊戲唔收背景點擊 / 按鍵；程式視窗唔可以縮細。") + "\n" +
                                Lang.T("閂咗（前景執行）就會先切去揀咗嘅視窗先開始。"));
        Pick = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(230), Ui.S(34)) };
        Pick.Click += (_, _) => ShowMenu(Pick, new Point(0, Pick.Height + Ui.S(2)), _sc, AfterChange, withBgToggle: false);
        Fake = new ToggleChip(Lang.T("假焦點")) { Checked = sc.FakeFocus };
        Fake.Size = new Size(Fake.Width + Ui.S(8), Ui.S(34));
        _tip.SetToolTip(Fake, Lang.T("背景執行時，送按鍵 / 點擊之前先話俾遊戲知「你而家喺最前面」（唔會真係切視窗）。\n部分唔收背景輸入嘅遊戲開咗會得；讀原始輸入（DirectInput / Raw Input）嘅遊戲就冇效。"));
        Fake.CheckedChanged += () => { _sc.FakeFocus = Fake.Checked; _sc.Save(); };
        Toggle.CheckedChanged += () =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _sc.Background = Toggle.Checked;
                _sc.Save();
                AfterChange();
            }
            finally { _busy = false; }
        };
        Update();
    }

    private bool _busy;

    private void AfterChange()
    {
        _busy = true;
        try { Toggle.Checked = _sc.Background; } finally { _busy = false; }
        Update();
        Changed?.Invoke();
    }

    public static string Label(Win.WinInfo w, List<Win.WinInfo> all)
    {
        var same = all.Where(x => x.Process == w.Process && x.Title == w.Title).OrderBy(x => (long)x.Handle).ToList();
        string t = $"{w.Process} - {w.Title}";
        if (t.Length > 60) t = t[..60] + "…";
        if (same.Count > 1)
        {
            var pos = Win.Position(w.Handle);
            t += $"  #{same.IndexOf(w) + 1}  ({pos.X}, {pos.Y})";
        }
        return t;
    }

    private static string TargetLabel(Script sc)
    {
        if (sc.TargetProcess.Length == 0) return Lang.T("不指定視窗（成個螢幕）");
        var h = Win.Find(sc);
        var all = Win.List();
        var w = all.FirstOrDefault(x => x.Handle == h);
        return w != null ? Label(w, all) : $"{sc.TargetProcess} - {sc.TargetTitle}" + Lang.T("（未開）");
    }

    public void Update()
    {
        Pick.Tag = null;
        Pick.Visible = true;
        Fake.Tag = _sc.Background ? null : "hidden";
        Fake.Visible = _sc.Background;
        string label = _sc.Background && _sc.TargetProcess.Length == 0 ? Lang.T("揀程式…") : TargetLabel(_sc);
        Pick.Text = label + "  ▾";
        _tip.SetToolTip(Pick, Lang.T("揀腳本喺邊個程式視窗運行（前景 / 背景都用得）") + "\n" + label);
        Relayout?.Invoke();
    }

    /// <summary>腳本清單用：而家揀咗邊個程式（短名）。</summary>
    public static string ShortTarget(Script sc)
    {
        if (sc.Background)
            return sc.TargetProcess.Length == 0 ? Lang.T("背景：未揀程式") : Lang.T("背景：") + TargetLabel(sc);
        return sc.TargetProcess.Length == 0 ? Lang.T("前景執行") : Lang.T("前景：") + TargetLabel(sc);
    }

    /// <summary>腳本清單用：彈出選單（背景執行開關 + 揀視窗），揀咗即刻設定（座標自動換算）。</summary>
    public static void ShowQuickMenu(Control anchor, Point at, Script sc, Action changed) =>
        ShowMenu(anchor, at, sc, changed, withBgToggle: true);

    private static void ShowMenu(Control anchor, Point at, Script sc, Action changed, bool withBgToggle)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        void Warn(string t) => MessageBox.Show(anchor.FindForm(), t, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (withBgToggle)
        {
            var bg = new ToolStripMenuItem(Lang.T("背景執行")) { Checked = sc.Background };
            bg.Click += (_, _) =>
            {
                sc.Background = !sc.Background;
                sc.Save();
                changed();
                if (sc.Background && sc.TargetProcess.Length == 0 && !anchor.IsDisposed)
                    anchor.BeginInvoke(() => ShowMenu(anchor, at, sc, changed, withBgToggle: false));
            };
            menu.Items.Add(bg);
            menu.Items.Add(new ToolStripSeparator());
        }
        var none = new ToolStripMenuItem(Lang.T("不指定視窗（成個螢幕）")) { Checked = sc.TargetProcess.Length == 0 };
        none.Click += (_, _) =>
        {
            if (sc.TargetProcess.Length == 0) return;
            if (!ScriptCoords.MakeAbsolute(sc))
            {
                Warn(Lang.T("搵唔到程式視窗，座標未能換返螢幕座標。請先開返個程式再揀。"));
                return;
            }
            sc.TargetProcess = ""; sc.TargetTitle = ""; sc.TargetHwnd = 0; sc.TargetIndex = 0;
            sc.Background = false; // 背景一定要有視窗
            sc.Save();
            changed();
        };
        menu.Items.Add(none);
        menu.Items.Add(new ToolStripSeparator());
        var wins = Win.List();
        var cur = sc.TargetProcess.Length > 0 ? Win.Find(sc) : 0;
        if (wins.Count == 0) menu.Items.Add(Lang.T("（冇搵到視窗）")).Enabled = false;
        foreach (var w in wins)
        {
            var item = new ToolStripMenuItem(Label(w, wins)) { Checked = w.Handle == cur };
            var info = w;
            item.Click += (_, _) =>
            {
                // 由「成個螢幕」轉去視窗：座標換做相對視窗；本身已經相對就照用
                var same = Win.List().Where(x => x.Process == info.Process && x.Title == info.Title).OrderBy(x => (long)x.Handle).ToList();
                sc.TargetProcess = info.Process;
                sc.TargetTitle = info.Title;
                sc.TargetHwnd = (long)info.Handle;
                sc.TargetIndex = Math.Max(0, same.FindIndex(x => x.Handle == info.Handle));
                ScriptCoords.MakeRelative(sc);
                sc.Save();
                changed();
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            try { if (!anchor.IsDisposed && anchor.IsHandleCreated) anchor.BeginInvoke(() => menu.Dispose()); } catch { }
        };
        menu.Show(anchor, at);
    }
}

/// <summary>腳本預覽：睇晒所有步驟（唔可以改）；上面有「修改」同「開始 / 停止」。</summary>
internal sealed class ScriptViewer : Panel
{
    private readonly Script _sc;
    private BgControls _bg = null!;
    private FlowRow _flow = null!;
    private readonly ToolTip _tip = new();

    public Script Script => _sc;
    private FancyButton? _back;
    private readonly CardStack _head = StickyHead.Make();
    private readonly CardStack _stack = new();
    private readonly FancyButton _start;
    private readonly Label _status;
    private readonly Action<string, string> _onStatus;
    private readonly Action _onChanged;

    public event Action? Back, Edit, Reload;

    public ScriptViewer(Script sc)
    {
        _sc = sc;
        BackColor = Ui.Bg;
        _stack.Dock = DockStyle.Fill;

        var back = new FancyButton { Text = Lang.T("← 返回"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        back.Click += (_, _) => { if (!ScriptRunner.IsRunning(_sc)) BeginInvoke(() => Back?.Invoke()); };
        _back = back;
        var title = Ui.Text_(sc.Name, Ui.Text, Ui.F(12f, FontStyle.Bold), new Padding(0));
        string info = Script.RunsName(sc.Runs) + (sc.Hotkey.IsEmpty ? "" : Lang.T(" · 快捷鍵 ") + sc.Hotkey);
        _sc = sc;
        var infoLabel = Ui.Text_(info, Ui.TextDim, Ui.F(9.5f), new Padding(0));
        var edit = new FancyButton { Text = Lang.T("修改"), Kind = BtnKind.Primary, Size = new Size(Ui.S(96), Ui.S(34)) };
        edit.Click += (_, _) => BeginInvoke(() => Edit?.Invoke());
        _start = new FancyButton { Kind = BtnKind.Success, Size = new Size(Ui.S(96), Ui.S(34)) };
        _start.Click += (_, _) => ScriptRunner.Toggle(_sc);

        var top = new BareRow();
        // 背景執行：開咗就要揀程式，腳本淨係喺嗰個視窗入面截圖 / 點擊 / 按鍵
        _bg = new BgControls(sc, _tip);
        _bg.Relayout = () => _flow?.PerformLayout();
        _bg.Changed += () => BeginInvoke(() => Reload?.Invoke()); // 座標換算咗：重新整預覽
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
        var runsBtn = new FancyButton { Text = Script.RunsName(sc.Runs) + "  ▾", Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(120), Ui.S(34)) };
        _tip.SetToolTip(runsBtn, Lang.T("運行次數：一次、幾多次，或者一直運行"));
        runsBtn.Click += (_, _) => RunsMenu.Show(runsBtn, new Point(0, runsBtn.Height + Ui.S(2)), _sc, () => { runsBtn.Text = Script.RunsName(_sc.Runs) + "  ▾"; runsBtn.Invalidate(); });
        _flow = new FlowRow(new Control[] { back, title, speed, runsBtn }, new Control[] { _start, edit, _bg.Toggle, _bg.Pick });
        ShowSpeed();
        top.SetContent(_flow);
        _bg.Update();
        top.Padding = new Padding(0);
        _head.AddRow(top);

        // 主線程
        var main = new CardPanel { Title = Lang.T("主線程"), Hint = Lang.F("{0} 個步驟 · {1}", sc.Steps.Count, info) };
        var mainPanel = new StatusWrap(new PreviewList(sc, sc.Steps), out _status);
        main.SetContent(mainPanel);
        _stack.AddRow(main);

        // 新線程
        foreach (var t in sc.Threads)
        {
            var tcard = new CardPanel { Title = t.Name, Hint = Lang.F("{0} 個步驟 · 同主線程同時行", t.Steps.Count) };
            tcard.SetContent(new PreviewList(sc, t.Steps));
            _stack.AddRow(tcard);
        }

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
        Controls.Add(_head);

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
        if (_bg != null) { _bg.Toggle.Enabled = !run; _bg.Pick.Enabled = !run; }
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
    public static readonly RunCtx Foreground = new(null, 0, false);

    private readonly Rectangle? _exclude; // 前景：自己個視窗唔計
    public readonly nint Hwnd;
    private readonly bool _rel;           // 腳本座標已經係「相對視窗」
    private readonly nint _win;           // 前景：揀咗嘅視窗（座標由佢換算返螢幕）
    public bool Bg => Hwnd != 0;

    private readonly Size _ref;           // 記錄座標時個視窗內容區大小（按比例換算用）
    public bool Fake;                     // 背景：送輸入前扮有焦點

    /// <summary>背景 + 假焦點：送輸入前話俾個視窗知「你而家喺最前面」。</summary>
    private void Focus() { if (Bg && Fake) Win.FakeFocus(Hwnd); }

    public RunCtx(Rectangle? exclude, nint hwnd, bool relative, nint fgWindow = 0, Size refSize = default)
    {
        _exclude = exclude;
        Hwnd = hwnd;
        _rel = relative;
        _win = fgWindow;
        _ref = refSize;
    }

    /// <summary>記錄時嘅視窗座標 → 而家個視窗大細嘅座標（按比例）。</summary>
    private Point Scale(nint h, Point p) => ScriptCoords.RefToClient(h, p, _ref);

    /// <summary>腳本座標 → 呢個環境嘅座標。背景：相對視窗就直接用（視窗移位都啱）；舊腳本嘅螢幕座標就換算。
    /// 前景：相對視窗就換返螢幕座標。</summary>
    public Point Pt(int x, int y)
    {
        var p = new Point(x, y);
        if (Bg) return _rel ? Scale(Hwnd, p) : Win.ScreenToClientPt(Hwnd, p);
        return _rel && _win != 0 ? Win.ClientToScreenPt(_win, Scale(_win, p)) : p;
    }

    /// <summary>拖拉：喺 a 撳落 → 慢慢移去 b → 放開。背景用視窗訊息，被擋住都得。</summary>
    /// <param name="pressAtStart">false = 已經撳住咗（之前一步「按下」），淨係移動同放開。</param>
    public async Task DragAsync(Point a, Point b, bool right, TimeSpan duration, CancellationToken ct, bool pressAtStart = true)
    {
        if (pressAtStart) Guard();
        Focus();
        int steps = Math.Clamp((int)(duration.TotalMilliseconds / 15), 5, 200);
        var stepDelay = TimeSpan.FromMilliseconds(Math.Max(1, duration.TotalMilliseconds / steps));
        if (Bg)
        {
            var (target, pa) = Win.ChildAt(Hwnd, a);
            int dx = pa.X - a.X, dy = pa.Y - a.Y; // 子視窗座標偏移
            nint btn = right ? 2 : 1;
            if (pressAtStart)
            {
                Win.Send(target, 0x200, 0, pa);
                Win.Send(target, right ? 0x204u : 0x201u, btn, pa);
            }
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(stepDelay, ct);
                var p = new Point(a.X + (b.X - a.X) * i / steps + dx, a.Y + (b.Y - a.Y) * i / steps + dy);
                Win.Send(target, 0x200, btn, p);
            }
            Win.Send(target, right ? 0x205u : 0x202u, 0, new Point(b.X + dx, b.Y + dy));
            return;
        }
        if (pressAtStart) Mouse.Down(a.X, a.Y, right);
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(stepDelay, ct);
                Mouse.Move(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
            }
        }
        finally { Mouse.Up(b.X, b.Y, right); }
    }

    /// <summary>目標程式用咗管理員權限而我哋冇：Windows 會擋晒點擊 / 按鍵，即刻講。</summary>
    private void Guard()
    {
        nint h = Bg ? Hwnd : Win.Foreground();
        if (Elevation.IsHigherThanUs(h)) throw new NeedAdminException(Elevation.ProcessName(h));
    }

    public void KeyDown(int vk, bool alt)
    {
        Guard();
        Focus();
        if (Bg) Win.PostKey(Hwnd, vk, false, alt); else Keyboard.Down(vk);
    }

    public void KeyUp(int vk, bool alt)
    {
        Focus();
        if (Bg) Win.PostKey(Hwnd, vk, true, alt); else Keyboard.Up(vk);
    }

    public void MouseDown(int x, int y, bool right)
    {
        Guard();
        Focus();
        if (Bg) Win.PostMouse(Hwnd, x, y, right, down: true);
        else Mouse.Down(x, y, right);
    }

    public void MouseUp(int x, int y, bool right, bool move)
    {
        Focus();
        if (Bg) Win.PostMouse(Hwnd, x, y, right, down: false);
        else Mouse.Up(x, y, right, move);
    }

    /// <summary>打字：前景用 Unicode 輸入；背景送 WM_CHAR 去個視窗。</summary>
    public void TypeText(string text, CancellationToken ct)
    {
        Guard();
        if (!Bg) { Keyboard.TypeText(text, ct); return; }
        Focus();
        foreach (char ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') { Win.PostKey(Hwnd, 0x0D, false, false); Win.PostKey(Hwnd, 0x0D, true, false); }
            else Win.PostChar(Hwnd, ch);
            Thread.Sleep(15);
        }
    }

    /// <summary>滾輪：notches &gt; 0 向上。</summary>
    public void Wheel(int x, int y, int notches)
    {
        Guard();
        if (!Bg) { Mouse.Wheel(x, y, notches); return; }
        Focus();
        var scr = Win.ClientToScreenPt(Hwnd, new Point(x, y));
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            Win.PostWheel(Hwnd, scr, 120 * Math.Sign(notches));
            Thread.Sleep(40);
        }
    }

    public void Click(int x, int y, bool right)
    {
        Guard();
        Focus();
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
        if (!Bg) { var sp = Pt(screenX, screenY); return ScreenSearch.PixelAt(sp.X, sp.Y); }
        var p = Pt(screenX, screenY);
        using var bmp = Win.CaptureClient(Hwnd);
        if (bmp == null || p.X < 0 || p.Y < 0 || p.X >= bmp.Width || p.Y >= bmp.Height) return Color.Black;
        return bmp.GetPixel(p.X, p.Y);
    }
}

/// <summary>Windows 視窗相關：列出視窗、背景截圖、送滑鼠 / 鍵盤訊息。</summary>
internal static class Win
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public static nint Foreground() => GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>將視窗切去最前（縮細咗就還原）。</summary>
    public static void Activate(nint h)
    {
        try
        {
            if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
            var fg = GetForegroundWindow();
            uint me = GetCurrentThreadId(), other = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally { if (attached) AttachThreadInput(me, other, false); }
        }
        catch { }
    }

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

    /// <summary>
    /// 搵返腳本揀咗嘅視窗：同一個程式開咗幾個都分得清。
    /// 先用記低嘅視窗（仲開緊就用佢）；重開咗就用「同名視窗入面第幾個」；再唔得就用同 process 第一個。
    /// </summary>
    public static nint Find(Script s)
    {
        if (string.IsNullOrEmpty(s.TargetProcess)) return 0;
        var same = List().Where(w => string.Equals(w.Process, s.TargetProcess, StringComparison.OrdinalIgnoreCase)).ToList();
        if (s.TargetHwnd != 0 && same.Any(w => (long)w.Handle == s.TargetHwnd)) return (nint)s.TargetHwnd;
        var exact = same.Where(w => w.Title == s.TargetTitle).OrderBy(w => (long)w.Handle).ToList();
        if (exact.Count > 0) return exact[Math.Clamp(s.TargetIndex, 0, exact.Count - 1)].Handle;
        return same.FirstOrDefault()?.Handle ?? 0;
    }

    /// <summary>視窗喺螢幕嘅位置（顯示用，分辨同名視窗）。</summary>
    public static Point Position(nint h)
    {
        var p = new POINT();
        ClientToScreen(h, ref p);
        return new Point(p.X, p.Y);
    }

    public static Point ClientToScreenPt(nint h, Point p)
    {
        var pt = new POINT { X = p.X, Y = p.Y };
        ClientToScreen(h, ref pt);
        return new Point(pt.X, pt.Y);
    }

    /// <summary>(x, y) 嗰點最入面嘅子視窗同埋喺佢入面嘅座標。</summary>
    public static (nint Target, Point Pt) ChildAt(nint h, Point p)
    {
        nint target = h;
        var pt = new POINT { X = p.X, Y = p.Y };
        for (int i = 0; i < 8; i++)
        {
            var child = ChildWindowFromPointEx(target, pt, 1 | 4);
            if (child == 0 || child == target) break;
            var scr = pt;
            ClientToScreen(target, ref scr);
            ScreenToClient(child, ref scr);
            target = child;
            pt = scr;
        }
        return (target, new Point(pt.X, pt.Y));
    }

    public static void Send(nint target, uint msg, nint w, Point p) => PostMessage(target, msg, w, MakeLParam(p.X, p.Y));

    /// <summary>用 process 名 + 標題搵返個視窗；標題變咗就用同 process 第一個視窗。</summary>
    public static nint Find(string process, string title)
    {
        if (string.IsNullOrEmpty(process)) return 0;
        var all = List().Where(w => string.Equals(w.Process, process, StringComparison.OrdinalIgnoreCase)).ToList();
        return (all.FirstOrDefault(w => w.Title == title) ?? all.FirstOrDefault())?.Handle ?? 0;
    }

    /// <summary>視窗內容區大細。</summary>
    public static Size ClientSize(nint h) => GetClientRect(h, out var r) ? new Size(r.R - r.L, r.B - r.T) : Size.Empty;

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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, long> _lastFake = new();

    /// <summary>假焦點：送「啟動 / 攞焦點」訊息（唔會真係切視窗）。每 300ms 最多一次。</summary>
    public static void FakeFocus(nint h)
    {
        long now = Environment.TickCount64;
        if (_lastFake.TryGetValue(h, out var last) && now - last < 300) return;
        _lastFake[h] = now;
        PostMessage(h, 0x001C /*WM_ACTIVATEAPP*/, 1, 0);
        PostMessage(h, 0x0086 /*WM_NCACTIVATE*/, 1, 0);
        PostMessage(h, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);
        PostMessage(h, 0x0007 /*WM_SETFOCUS*/, 0, 0);
        Thread.Sleep(15);
    }

    public static void PostChar(nint h, char ch) => PostMessage(h, 0x0102 /*WM_CHAR*/, ch, 1);

    public static void PostWheel(nint h, Point screen, int delta) =>
        PostMessage(h, 0x020A /*WM_MOUSEWHEEL*/, (nint)(delta << 16), MakeLParam(screen.X, screen.Y));

    public static void PostKey(nint h, int vk, bool up, bool alt)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        bool ext = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
        long lp = 1 | (scan << 16) | (ext ? 1L << 24 : 0) | (alt ? 1L << 29 : 0) | (up ? (1L << 30) | (1L << 31) : 0);
        uint msg = alt || vk == 0x12 ? (up ? WM_SYSKEYUP : WM_SYSKEYDOWN) : (up ? WM_KEYUP : WM_KEYDOWN);
        PostMessage(h, msg, vk, (nint)lp);
    }
}
