using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Scripts;
using MultiTool.UI;
using ImageList = MultiTool.Scripts.ImageList;

namespace MultiTool.Ai;

/// <summary>
/// 腳本 ⇄ 俾 AI 睇得明嘅 JSON。腳本本身嘅存檔格式有 base64 nest，AI 好難直接改，所以轉一轉。
/// </summary>
internal static class ScriptJson
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string Spec = """
        腳本 JSON 格式：
        {
          "mode": "once" | "loop",
          "runs": 1,                           // 運行次數：1 = 一次，N = N 次，0 = 一直運行（有就以呢個為準）            // once = 主線程行一次；loop = 一直重複
          "steps": [ 步驟... ],                // 主線程：由上至下逐步做
          "threads": [                         // 新線程：同主線程同時行（腳本一直行就一直重複）
            { "name": "新線程 1", "steps": [ 步驟... ] }
          ],
          "ifs": [                             // IF 框：同主線程「同時」行，每 interval 秒檢查一次條件，成立就行入面啲步驟
            { "name": "IF 條件 1", "condition": 條件, "steps": [ 步驟... ] }
          ]
        }
        步驟（type）：
          {"type":"key","key":"A","ctrl":false,"shift":false,"alt":false,"action":"hold","hold":0.1}
              key 用 .NET Keys 名：A-Z、D0-D9（數字鍵）、F1-F12、Space、Enter、Escape、Tab、Back、Left、Up、Right、Down、
              ShiftKey、ControlKey、Menu(Alt)、NumPad0-9 等。action：hold = 撳住 hold 秒再放；down = 淨係按下；up = 淨係彈起
          {"type":"click","button":"left"|"right","x":100,"y":200,"action":"hold","hold":0.1}   // 螢幕座標滑鼠點擊
          {"type":"wait","seconds":1,"random_max":0}                 // 延遲（介面用 ms 顯示）；random_max > seconds 就喺 seconds 至 random_max 之間隨機
          {"type":"text","text":"你好"}                              // 輸入文字（中文都得）
          {"type":"scroll","x":100,"y":200,"notches":-3}             // 滑鼠滾輪：喺 (x,y) 碌 notches 格；正數向上，負數向下
          {"type":"drag","button":"left","x":100,"y":200,"x2":300,"y2":200,"seconds":0.5}   // 由 (x,y) 撳住拖去 (x2,y2)，用時 seconds
          {"type":"move","x":100,"y":200,"x2":300,"y2":200,"seconds":0.5}   // 淨係移動滑鼠（唔撳）：由 (x,y) 移去 (x2,y2)，用時 seconds
          {"type":"image","images":["怪物.png"],"similarity":80}   // 喺畫面搵其中一張圖（最多 6 張），搵到就點擊佢，搵唔到就跳過繼續
          {"type":"loop","count":0,"limit_seconds":0,"until":"","steps":[...]}   // 重複 count 次；0 = 一直重複；limit_seconds = 行咗幾多秒就停（0 = 唔限）；until = 到呢個現實時間 "HH:mm" 就停（空 = 唔限）
          {"type":"system","action":"open_app","target":"C:\\...\\app.exe","name":"App"}   // 系統：play_sound 播音效（target = wav/mp3 路徑，repeat = true 一直重複到腳本停）；open_app 開程式（target = 路徑）；close_app 閂程式（target = process 名，例如 notepad）；switch_window 切換視窗（target = process 名，name = 視窗標題，可空）；shutdown 關機（seconds = 幾多秒後）
          {"type":"stop"}                                          // 停止成個腳本（包括 IF 框）
          {"type":"parallel","branches":[[...],[...]]}             // 同時運行：幾條支線（最少 2 條）一齊行，全部行完先做下一步
          {"type":"thread","steps":[...]}                          // 新線程：開一條支線同時行入面啲步驟，主線程唔使等即刻做下一步
          {"type":"break"}                                         // 中斷最近一層循環；只可以放喺 loop 入面（包括 loop 入面嘅 if）
          {"type":"if","name":"","condition":條件,"steps":[...],"else":[...]}   // 行到呢步先檢查一次，成立就行 steps；唔成立而有 else 就行 else（可以一層層 nest）
          {"type":"var","name":"次數","op":"add","value":1}         // 變數：op = set / add / sub；read = 讀畫面數字（x,y,x2,y2 範圍）
              op = find_images：搵晒一樣嘅圖（images, similarity, sort = reading / nearest / right_to_left / bottom_to_top），位置存入同名清單，變數 = 數量
              op = list_remove_first / list_remove_last / list_remove_at（value = 第幾個，由 1 起）：清單操作，數量跟住更新
          {"type":"notify","text":"刷咗 {次數} 次","windows":true,"discord":false}   // 通知；{變數名} 會換做數值
          {"type":"call","script":"資料夾名","name":"顯示名"}       // 行其他腳本嘅主步驟一次
        滑鼠點擊 click 可以加 "target"：
          "position"（預設，用 x,y）；"image"（images, similarity：搵到就撳佢中間）；
          "if"（撳 IF 搵到嘅位置，只可以放喺 if / ifs 嘅 steps 入面）；"list"（"list":清單名, "index":0 = 循環目前位置 / -1 = 最後 / N = 第 N 個）
        拖拉 drag 可以加 "target":"image"（images, similarity, "order": group = 全部拖埋一齊 / right_to_left / left_to_right / bottom_to_top / top_to_bottom）
        循環 loop 加 "list":"清單名" 就變成逐個清單位置行（count 唔用）。
        搵圖嘅步驟 / 條件（image、click/drag 搵圖、var find_images、image_found / image_not_found）可以加 "region":[x1,y1,x2,y2] 限定搵圖範圍。
        條件（kind）：
          {"kind":"image_found","images":["a.png"],"similarity":80,"click_found":false,"interval":1}   // click_found = 搵到順手左鍵點一下
          {"kind":"image_not_found","images":["a.png"],"similarity":80,"interval":1}
          {"kind":"color","x":100,"y":200,"color":"#FF0000","tolerance":10,"interval":1}             // 某個螢幕位置係某顏色
          {"kind":"time","time":"12:00:00"}                                                          // 現實時間到咗
          {"kind":"countdown","seconds":60}                                                          // 倒數完；只可以用喺頂層 ifs
          {"kind":"elapsed","seconds":1800}                                                          // 腳本運行咗 N 秒（時限）；頂層 ifs 觸發一次
          {"kind":"text","x":0,"y":0,"x2":100,"y2":30,"text":"完成"}                                  // 範圍入面出現文字
          {"kind":"number","x":0,"y":0,"x2":100,"y2":30,"op":"<","value":300}                         // 範圍入面嘅數字比較（op：> >= < <= = !=）
          {"kind":"variable","var":"次數","op":">=","value":50}                                       // 變數比較
        頂層 ifs 都可以加 "else":[...]：每次檢查唔成立就行。
          interval（每幾秒檢查一次）只對頂層 ifs 有用。
        幾個條件：IF（ifs 或者 type=if）可以加 "more_conditions":[條件, ...] 同 "logic":"and"|"or"
          and = 全部條件成立先做；or = 任何一個成立就做。
        """;

    // ---------------- 腳本 → JSON ----------------

    public static string ToJson(Script sc)
    {
        var root = new JsonObject
        {
            ["mode"] = sc.Loop ? "loop" : "once",
            ["runs"] = sc.Runs,
            ["steps"] = StepsArr(sc.Steps),
            ["threads"] = new JsonArray(sc.Threads.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["steps"] = StepsArr(t.Steps) }).ToArray()),
            ["ifs"] = new JsonArray(sc.Ifs.Select(b =>
            {
                var o = new JsonObject { ["name"] = b.Name, ["condition"] = Cond(b, true) };
                AddMore(o, b, true);
                o["steps"] = StepsArr(b.Steps);
                if (b.HasElse) o["else"] = StepsArr(b.Else);
                return (JsonNode)o;
            }).ToArray())
        };
        return root.ToJsonString(Pretty);
    }

    private static JsonArray StepsArr(IEnumerable<Step> steps) => new(steps.Select(s => (JsonNode)StepObj(s)).ToArray());

    private static string Act(PressMode m) => m switch { PressMode.Down => "down", PressMode.Up => "up", _ => "hold" };

    private static JsonArray Imgs(string field) => new(ImageList.Split(field).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());

    private static JsonArray Rgn(int x1, int y1, int x2, int y2) => new(JsonValue.Create(x1), JsonValue.Create(y1), JsonValue.Create(x2), JsonValue.Create(y2));

    private static string[] SortNames = { "reading", "nearest", "right_to_left", "bottom_to_top" };
    private static string[] OrderNames = { "group", "right_to_left", "left_to_right", "bottom_to_top", "top_to_bottom" };
    private static string[] VarOps = { "set", "add", "sub", "read", "find_images", "list_remove_first", "list_remove_last", "list_remove_at" };
    private static readonly string[] CmpNames = { ">", ">=", "<", "<=", "=", "!=" };

    private static JsonObject StepObj(Step s)
    {
        var o = StepObjCore(s);
        if (s.Rgn) o["region"] = Rgn(s.RX1, s.RY1, s.RX2, s.RY2);
        return o;
    }

    private static JsonObject StepObjCore(Step s)
    {
        switch (s.Kind)
        {
            case StepKind.Key:
            {
                var o = new JsonObject { ["type"] = "key", ["key"] = ((Keys)s.Vk).ToString() };
                if (s.Mods.HasFlag(Keys.Control)) o["ctrl"] = true;
                if (s.Mods.HasFlag(Keys.Shift)) o["shift"] = true;
                if (s.Mods.HasFlag(Keys.Alt)) o["alt"] = true;
                o["action"] = Act(s.Mode);
                if (s.Mode == PressMode.Hold) o["hold"] = s.Hold;
                return o;
            }
            case StepKind.Click:
            {
                var o = new JsonObject { ["type"] = "click", ["button"] = s.Right ? "right" : "left", ["action"] = Act(s.Mode) };
                if (s.ByList) { o["target"] = "list"; o["list"] = s.Var; o["index"] = s.Index; }
                else if (s.ByIf) o["target"] = "if";
                else if (s.ByImage) { o["target"] = "image"; o["images"] = Imgs(s.Image); o["similarity"] = s.Similarity; }
                else { o["x"] = s.X; o["y"] = s.Y; }
                if (s.Mode == PressMode.Hold) o["hold"] = s.Hold;
                return o;
            }
            case StepKind.Wait:
            {
                var w = new JsonObject { ["type"] = "wait", ["seconds"] = s.Seconds };
                if (s.Random) w["random_max"] = s.Seconds2;
                return w;
            }
            case StepKind.Text: return new JsonObject { ["type"] = "text", ["text"] = s.Target };
            case StepKind.Scroll: return new JsonObject { ["type"] = "scroll", ["x"] = s.X, ["y"] = s.Y, ["notches"] = s.Count };
            case StepKind.Drag when s.ByImage:
                return new JsonObject
                {
                    ["type"] = "drag", ["target"] = "image", ["button"] = s.Right ? "right" : "left", ["images"] = Imgs(s.Image),
                    ["similarity"] = s.Similarity, ["order"] = OrderNames[Math.Clamp(s.Order, 0, 4)], ["seconds"] = s.Hold
                };
            case StepKind.Drag:
                return new JsonObject
                {
                    ["type"] = "drag", ["button"] = s.Right ? "right" : "left", ["x"] = s.X, ["y"] = s.Y,
                    ["x2"] = s.X2, ["y2"] = s.Y2, ["seconds"] = s.Hold
                };
            case StepKind.Var:
            {
                var o = new JsonObject { ["type"] = "var", ["name"] = s.Var, ["op"] = VarOps[Math.Clamp((int)s.Op, 0, VarOps.Length - 1)] };
                switch (s.Op)
                {
                    case VarOp.Read: o["x"] = s.X; o["y"] = s.Y; o["x2"] = s.X2; o["y2"] = s.Y2; break;
                    case VarOp.FindImg: o["images"] = Imgs(s.Image); o["similarity"] = s.Similarity; o["sort"] = SortNames[Math.Clamp(s.Order, 0, 3)]; break;
                    case VarOp.ListRemoveFirst or VarOp.ListRemoveLast: break;
                    default: o["value"] = s.Value; break;
                }
                return o;
            }
            case StepKind.Notify: return new JsonObject { ["type"] = "notify", ["text"] = s.Target, ["windows"] = s.Toast, ["discord"] = s.Discord };
            case StepKind.Call: return new JsonObject { ["type"] = "call", ["script"] = s.Target, ["name"] = s.TargetName };
            case StepKind.Move:
                return new JsonObject { ["type"] = "move", ["x"] = s.X, ["y"] = s.Y, ["x2"] = s.X2, ["y2"] = s.Y2, ["seconds"] = s.Hold };
            case StepKind.Loop:
            {
                var lo = new JsonObject { ["type"] = "loop", ["count"] = s.Count, ["limit_seconds"] = s.LimitSec, ["until"] = s.Until };
                if (s.Var.Length > 0) lo["list"] = s.Var;
                lo["steps"] = StepsArr(s.Body);
                return lo;
            }
            case StepKind.StopScript: return new JsonObject { ["type"] = "stop" };
            case StepKind.System:
                return new JsonObject
                {
                    ["type"] = "system",
                    ["action"] = s.Sys switch { SysAction.OpenApp => "open_app", SysAction.CloseApp => "close_app", SysAction.SwitchWindow => "switch_window", SysAction.PlaySound => "play_sound", _ => "shutdown" },
                    ["target"] = s.Target, ["name"] = s.TargetName, ["seconds"] = s.Seconds, ["repeat"] = s.Repeat
                };
            case StepKind.Thread: return new JsonObject { ["type"] = "thread", ["steps"] = StepsArr(s.Body) };
            case StepKind.Parallel:
            {
                var arr = new JsonArray();
                foreach (var b in s.Branches) arr.Add(StepsArr(b));
                return new JsonObject { ["type"] = "parallel", ["branches"] = arr };
            }
            case StepKind.Break: return new JsonObject { ["type"] = "break" };
            case StepKind.If:
            {
                var b = s.Sub ?? new IfBlock();
                var io = new JsonObject { ["type"] = "if", ["name"] = b.Name, ["condition"] = Cond(b, false) };
                AddMore(io, b, false);
                io["steps"] = StepsArr(b.Steps);
                if (b.HasElse) io["else"] = StepsArr(b.Else);
                return io;
            }
            default: return new JsonObject { ["type"] = "image", ["images"] = Imgs(s.Image), ["similarity"] = s.Similarity };
        }
    }

    private static void AddMore(JsonObject o, IfBlock b, bool top)
    {
        if (b.More.Count == 0) return;
        o["logic"] = b.Or ? "or" : "and";
        o["more_conditions"] = new JsonArray(b.More.Select(m => (JsonNode)Cond(m, false)).ToArray());
    }

    private static void ParseMore(JsonObject o, IfBlock b, bool top, string where, HashSet<string> have, List<string> warn)
    {
        b.Or = Str(o, "logic") == "or";
        if (o["more_conditions"] is not JsonArray more) return;
        foreach (var n in more)
            if (n is JsonObject mo) b.More.Add(ParseCond(mo, top, where, have, warn));
    }

    private static JsonObject Cond(IfBlock b, bool top)
    {
        JsonObject o = b.Kind switch
        {
            CondKind.ImageFound => new JsonObject { ["kind"] = "image_found", ["images"] = Imgs(b.Image), ["similarity"] = b.Similarity, ["click_found"] = b.ClickFound },
            CondKind.ImageNotFound => new JsonObject { ["kind"] = "image_not_found", ["images"] = Imgs(b.Image), ["similarity"] = b.Similarity },
            CondKind.PixelColor => new JsonObject { ["kind"] = "color", ["x"] = b.X, ["y"] = b.Y, ["color"] = $"#{b.Color & 0xFFFFFF:X6}", ["tolerance"] = b.Tolerance },
            CondKind.Time => new JsonObject { ["kind"] = "time", ["time"] = b.Time },
            CondKind.Elapsed => new JsonObject { ["kind"] = "elapsed", ["seconds"] = b.ElapsedSec },
            CondKind.Text => new JsonObject { ["kind"] = "text", ["x"] = b.X, ["y"] = b.Y, ["x2"] = b.X2, ["y2"] = b.Y2, ["text"] = b.Text },
            CondKind.Number => new JsonObject { ["kind"] = "number", ["x"] = b.X, ["y"] = b.Y, ["x2"] = b.X2, ["y2"] = b.Y2, ["op"] = CmpNames[(int)b.Op], ["value"] = b.Value },
            CondKind.Variable => new JsonObject { ["kind"] = "variable", ["var"] = b.Var, ["op"] = CmpNames[(int)b.Op], ["value"] = b.Value },
            _ => new JsonObject { ["kind"] = "countdown", ["seconds"] = b.Seconds }
        };
        if (b.Rgn && b.Kind is CondKind.ImageFound or CondKind.ImageNotFound) o["region"] = Rgn(b.RX1, b.RY1, b.RX2, b.RY2);
        if (top && b.UsesInterval) o["interval"] = b.Interval;
        return o;
    }

    // ---------------- JSON → 腳本 ----------------

    public sealed record Parsed(bool Loop, List<Step> Steps, List<IfBlock> Ifs, List<string> Warnings, List<ThreadBlock>? Threads = null, int? Runs = null);

    /// <summary>讀 AI 俾嘅 JSON。images 只保留腳本資料夾入面真係有嘅檔。出錯會 throw。</summary>
    public static Parsed FromJson(string json, Script sc)
    {
        var warn = new List<string>();
        var have = new HashSet<string>(ExistingImages(sc), StringComparer.OrdinalIgnoreCase);
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   as JsonObject ?? throw new FormatException(Lang.T("唔係 JSON object"));
        bool loop = Str(root, "mode") == "loop";
        var steps = ParseSteps(root["steps"] as JsonArray, Lang.T("主線程"), false, false, have, warn);
        var ifs = new List<IfBlock>();
        if (root["ifs"] is JsonArray arr)
        {
            int n = 0;
            foreach (var node in arr)
            {
                n++;
                if (node is not JsonObject o) continue;
                var b = ParseCond(o["condition"] as JsonObject, true, Lang.F("IF 框 {0}", n), have, warn);
                ParseMore(o, b, true, Lang.F("IF 框 {0}", n), have, warn);
                b.Name = Str(o, "name") is { Length: > 0 } nm ? nm : Lang.F("IF 條件 {0}", n);
                b.Steps = ParseSteps(o["steps"] as JsonArray, Lang.F("IF 框「{0}」", b.Name), false, true, have, warn);
                if (o["else"] is JsonArray ea) { b.HasElse = true; b.Else = ParseSteps(ea, Lang.F("IF 框「{0}」否則", b.Name), false, true, have, warn); }
                ifs.Add(b);
            }
        }
        var threads = new List<ThreadBlock>();
        if (root["threads"] is JsonArray tarr)
        {
            int n = 0;
            foreach (var node in tarr)
            {
                n++;
                if (node is not JsonObject o) continue;
                var t = new ThreadBlock { Name = Str(o, "name") is { Length: > 0 } nm ? nm : Lang.T("新線程 ") + n };
                t.Steps = ParseSteps(o["steps"] as JsonArray, t.Name, false, true, have, warn);
                threads.Add(t);
            }
        }
        int? runs = root["runs"] is JsonValue rv && rv.TryGetValue<int>(out var rn) && rn >= 0 ? rn : null;
        return new Parsed(loop, steps, ifs, warn, threads, runs);
    }

    public static List<string> ExistingImages(Script sc)
    {
        try
        {
            if (!Directory.Exists(sc.Dir)) return new();
            return Directory.GetFiles(sc.Dir)
                .Where(f => new[] { ".png", ".bmp", ".jpg", ".jpeg" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => Path.GetFileName(f)!).ToList();
        }
        catch { return new(); }
    }

    private static string Str(JsonObject o, string k)
    {
        try { return o[k]?.GetValue<string>()?.Trim() ?? ""; } catch { return o[k]?.ToString().Trim() ?? ""; }
    }

    private static decimal Dec(JsonObject o, string k, decimal def)
    {
        var n = o[k];
        if (n == null) return def;
        try { return n.GetValue<decimal>(); } catch { }
        return decimal.TryParse(n.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    private static int Int(JsonObject o, string k, int def) => (int)Math.Round(Dec(o, k, def));

    private static bool Bool(JsonObject o, string k)
    {
        var n = o[k];
        if (n == null) return false;
        try { return n.GetValue<bool>(); } catch { return n.ToString() is "1" or "true"; }
    }

    private static PressMode Mode(JsonObject o) => Str(o, "action").ToLowerInvariant() switch
    {
        "down" => PressMode.Down,
        "up" => PressMode.Up,
        _ => PressMode.Hold
    };

    private static string Images(JsonObject o, string where, HashSet<string> have, List<string> warn)
    {
        var names = new List<string>();
        if (o["images"] is JsonArray a)
        {
            foreach (var n in a)
                if (n != null) names.Add(n.ToString().Trim());
        }
        else if (Str(o, "image") is { Length: > 0 } one) names.Add(one);
        var ok = names.Where(have.Contains).Distinct().Take(ImageList.Max).ToList();
        var missing = names.Where(n => n.Length > 0 && !have.Contains(n)).ToList();
        if (missing.Count > 0) warn.Add(Lang.F("{0}：冇呢啲圖（{1}），要自己截圖", where, string.Join("、", missing)));
        else if (ok.Count == 0) warn.Add(Lang.F("{0}：未有圖，要自己截圖", where));
        return ImageList.Join(ok);
    }

    private static int ParseKey(string name)
    {
        name = name.Trim();
        if (name.Length == 1 && char.IsDigit(name[0])) return (int)Keys.D0 + (name[0] - '0');
        var alias = name.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "ControlKey",
            "shift" => "ShiftKey",
            "alt" => "Menu",
            "esc" => "Escape",
            "backspace" => "Back",
            "del" => "Delete",
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            _ => name
        };
        return Enum.TryParse<Keys>(alias, true, out var k) && k != Keys.None ? (int)(k & Keys.KeyCode) : 0;
    }

    private static List<Step> ParseSteps(JsonArray? arr, string path, bool inLoop, bool _top, HashSet<string> have, List<string> warn)
    {
        var list = new List<Step>();
        if (arr == null) return list;
        int i = 0;
        foreach (var node in arr)
        {
            i++;
            if (node is not JsonObject o) continue;
            string where = Lang.F("{0} 第 {1} 步", path, i);
            string type = Str(o, "type").ToLowerInvariant();
            int before = list.Count;
            ParseOne(o, type, where, list, inLoop, have, warn);
            if (list.Count > before && ReadRgn(o) is { } rg)
            {
                var st0 = list[^1];
                st0.Rgn = true; (st0.RX1, st0.RY1, st0.RX2, st0.RY2) = rg;
            }
        }
        return list;
    }

    private static (int, int, int, int)? ReadRgn(JsonObject o)
    {
        if (o["region"] is not JsonArray r || r.Count != 4) return null;
        try { return ((int)Math.Round(r[0]!.GetValue<double>()), (int)Math.Round(r[1]!.GetValue<double>()), (int)Math.Round(r[2]!.GetValue<double>()), (int)Math.Round(r[3]!.GetValue<double>())); }
        catch { return null; }
    }

    private static CmpOp ParseCmp(string s) => s.Trim() switch
    {
        ">" => CmpOp.Gt, ">=" or "≥" => CmpOp.Ge, "<" => CmpOp.Lt, "<=" or "≤" => CmpOp.Le, "=" or "==" => CmpOp.Eq, "!=" or "≠" or "<>" => CmpOp.Ne, _ => CmpOp.Ge
    };

    private static void ParseOne(JsonObject o, string type, string where, List<Step> list, bool inLoop, HashSet<string> have, List<string> warn)
    {
        {
            switch (type)
            {
                case "key":
                {
                    int vk = ParseKey(Str(o, "key"));
                    if (vk == 0) warn.Add(Lang.F("{0}：唔識呢個鍵「{1}」，要自己揀", where, Str(o, "key")));
                    var mods = Keys.None;
                    if (Bool(o, "ctrl")) mods |= Keys.Control;
                    if (Bool(o, "shift")) mods |= Keys.Shift;
                    if (Bool(o, "alt")) mods |= Keys.Alt;
                    list.Add(new Step { Kind = StepKind.Key, Vk = vk, Mods = mods, Mode = Mode(o), Hold = Math.Max(0, Dec(o, "hold", 0.1m)) });
                    break;
                }
                case "click":
                {
                    var st = new Step
                    {
                        Kind = StepKind.Click, Right = Str(o, "button").ToLowerInvariant() == "right",
                        X = Int(o, "x", 0), Y = Int(o, "y", 0), Mode = Mode(o), Hold = Math.Max(0, Dec(o, "hold", 0.1m))
                    };
                    switch (Str(o, "target").ToLowerInvariant())
                    {
                        case "image":
                            st.ByImage = true;
                            st.Image = Images(o, where, have, warn);
                            st.Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100);
                            break;
                        case "if":
                            st.ByIf = true;
                            break;
                        case "list":
                            st.ByList = true;
                            st.Var = Str(o, "list");
                            st.Index = Int(o, "index", 0);
                            if (st.Var.Length == 0) warn.Add(Lang.F("{0}：清單名未填", where));
                            break;
                        default:
                            if (st.X == 0 && st.Y == 0) warn.Add(Lang.F("{0}：座標未揀", where));
                            break;
                    }
                    list.Add(st);
                    break;
                }
                case "move":
                    list.Add(new Step
                    {
                        Kind = StepKind.Move, X = Int(o, "x", 0), Y = Int(o, "y", 0), X2 = Int(o, "x2", 0), Y2 = Int(o, "y2", 0),
                        Hold = Math.Max(0m, Dec(o, "seconds", 0.5m))
                    });
                    break;
                case "drag":
                {
                    var ds = new Step
                    {
                        Kind = StepKind.Drag, Right = Str(o, "button").ToLowerInvariant() == "right",
                        X = Int(o, "x", 0), Y = Int(o, "y", 0), X2 = Int(o, "x2", 0), Y2 = Int(o, "y2", 0),
                        Hold = Math.Max(0.01m, Dec(o, "seconds", 0.5m))
                    };
                    if (Str(o, "target").ToLowerInvariant() == "image")
                    {
                        ds.ByImage = true;
                        ds.Image = Images(o, where, have, warn);
                        ds.Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100);
                        ds.Order = Math.Max(0, Array.IndexOf(OrderNames, Str(o, "order").ToLowerInvariant()));
                    }
                    list.Add(ds);
                    break;
                }
                case "var":
                {
                    var vs = new Step { Kind = StepKind.Var, Var = Str(o, "name"), Value = Dec(o, "value", 1m) };
                    int op = Array.IndexOf(VarOps, Str(o, "op").ToLowerInvariant());
                    vs.Op = (VarOp)Math.Max(0, op);
                    if (vs.Op == VarOp.Read) { vs.X = Int(o, "x", 0); vs.Y = Int(o, "y", 0); vs.X2 = Int(o, "x2", 0); vs.Y2 = Int(o, "y2", 0); }
                    if (vs.Op == VarOp.FindImg)
                    {
                        vs.Image = Images(o, where, have, warn);
                        vs.Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100);
                        vs.Order = Math.Max(0, Array.IndexOf(SortNames, Str(o, "sort").ToLowerInvariant()));
                    }
                    if (vs.Var.Length == 0) { vs.Var = Lang.T("變數名稱"); warn.Add(Lang.F("{0}：變數名未填", where)); }
                    list.Add(vs);
                    break;
                }
                case "notify":
                    list.Add(new Step { Kind = StepKind.Notify, Target = Str(o, "text"), Toast = o["windows"] == null || Bool(o, "windows"), Discord = Bool(o, "discord") });
                    break;
                case "call":
                    list.Add(new Step { Kind = StepKind.Call, Target = Str(o, "script"), TargetName = Str(o, "name") is { Length: > 0 } cn ? cn : Str(o, "script") });
                    break;
                case "wait":
                {
                    var ws = new Step { Kind = StepKind.Wait, Seconds = Math.Max(0, Dec(o, "seconds", 1m)) };
                    decimal mx = Dec(o, "random_max", 0m);
                    if (mx > ws.Seconds) { ws.Random = true; ws.Seconds2 = mx; }
                    list.Add(ws);
                    break;
                }
                case "text":
                    list.Add(new Step { Kind = StepKind.Text, Target = Str(o, "text") });
                    break;
                case "scroll":
                    list.Add(new Step { Kind = StepKind.Scroll, X = Int(o, "x", 0), Y = Int(o, "y", 0), Count = Int(o, "notches", -3) is var nn && nn != 0 ? nn : -3 });
                    break;
                case "image":
                    list.Add(new Step { Kind = StepKind.Image, Image = Images(o, where, have, warn), Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100) });
                    break;
                case "loop":
                    list.Add(new Step
                    {
                        Kind = StepKind.Loop, Count = Math.Max(0, Int(o, "count", 0)),
                        LimitSec = Math.Max(0m, o["limit_seconds"] != null ? Dec(o, "limit_seconds", 0m) : Dec(o, "limit_minutes", 0m) * 60m),
                        Until = TimeSpan.TryParse(Str(o, "until"), CultureInfo.InvariantCulture, out var ut) ? $"{ut.Hours:00}:{ut.Minutes:00}:{ut.Seconds:00}" : "",
                        Var = Str(o, "list"),
                        Body = ParseSteps(o["steps"] as JsonArray, where + Lang.T(" 循環"), true, false, have, warn)
                    });
                    break;
                case "stop":
                    list.Add(new Step { Kind = StepKind.StopScript });
                    break;
                case "system":
                {
                    var sys = Str(o, "action") switch { "close_app" => SysAction.CloseApp, "switch_window" => SysAction.SwitchWindow, "shutdown" => SysAction.Shutdown, "play_sound" => SysAction.PlaySound, _ => SysAction.OpenApp };
                    var ss = new Step { Kind = StepKind.System, Sys = sys, Target = Str(o, "target"), TargetName = Str(o, "name"), Seconds = Math.Max(0, Dec(o, "seconds", 60m)),
                        Repeat = o["repeat"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb };
                    if (sys == SysAction.PlaySound && ss.TargetName.Length == 0) ss.TargetName = Path.GetFileNameWithoutExtension(ss.Target);
                    if (sys != SysAction.Shutdown && ss.Target.Length == 0) warn.Add(Lang.F("{0}：系統步驟未揀程式 / 視窗", where));
                    list.Add(ss);
                    break;
                }
                case "parallel":
                {
                    var ps = new Step { Kind = StepKind.Parallel };
                    if (o["branches"] is JsonArray brs)
                        for (int bi = 0; bi < brs.Count; bi++)
                            ps.Branches.Add(ParseSteps(brs[bi] as JsonArray, where + Lang.F(" 支線 {0}", bi + 1), false, true, have, warn));
                    while (ps.Branches.Count < 2) ps.Branches.Add(new List<Step>());
                    list.Add(ps);
                    break;
                }
                case "thread":
                    list.Add(new Step
                    {
                        Kind = StepKind.Thread,
                        Body = ParseSteps(o["steps"] as JsonArray, where + Lang.T(" 新線程"), false, true, have, warn)
                    });
                    break;
                case "break":
                    if (!inLoop) { warn.Add(Lang.F("{0}：中斷循環唔喺循環入面，已略過", where)); break; }
                    list.Add(new Step { Kind = StepKind.Break });
                    break;
                case "if":
                {
                    var b = ParseCond(o["condition"] as JsonObject, false, where, have, warn);
                    ParseMore(o, b, false, where, have, warn);
                    b.Name = Str(o, "name");
                    b.Steps = ParseSteps(o["steps"] as JsonArray, where + " IF", inLoop, false, have, warn);
                    if (o["else"] is JsonArray ea) { b.HasElse = true; b.Else = ParseSteps(ea, where + Lang.T(" 否則"), inLoop, false, have, warn); }
                    list.Add(new Step { Kind = StepKind.If, Sub = b });
                    break;
                }
                default:
                    warn.Add(Lang.F("{0}：唔識「{1}」，已略過", where, type));
                    break;
            }
        }
    }

    private static IfBlock ParseCond(JsonObject? o, bool top, string where, HashSet<string> have, List<string> warn)
    {
        var b = new IfBlock();
        if (o == null) { warn.Add(Lang.F("{0}：冇條件，用咗「搵到圖」", where)); b.Image = ""; return b; }
        switch (Str(o, "kind").ToLowerInvariant())
        {
            case "image_not_found":
                b.Kind = CondKind.ImageNotFound;
                b.Image = Images(o, where + Lang.T(" 條件"), have, warn);
                b.Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100);
                break;
            case "color":
                b.Kind = CondKind.PixelColor;
                b.X = Int(o, "x", 0);
                b.Y = Int(o, "y", 0);
                string c = Str(o, "color").TrimStart('#');
                b.Color = int.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var col) ? col & 0xFFFFFF : 0xFF0000;
                b.Tolerance = Math.Clamp(Int(o, "tolerance", 10), 0, 255);
                if (b.X == 0 && b.Y == 0) warn.Add(Lang.F("{0} 條件：顏色位置未揀", where));
                break;
            case "time":
                b.Kind = CondKind.Time;
                b.Time = Str(o, "time") is { Length: > 0 } t ? t : "12:00:00";
                break;
            case "elapsed":
                b.Kind = CondKind.Elapsed;
                b.ElapsedSec = Math.Max(1m, o["seconds"] != null ? Dec(o, "seconds", 1800m) : Dec(o, "minutes", 30m) * 60m);
                break;
            case "text":
                b.Kind = CondKind.Text;
                b.X = Int(o, "x", 0); b.Y = Int(o, "y", 0); b.X2 = Int(o, "x2", 0); b.Y2 = Int(o, "y2", 0);
                b.Text = Str(o, "text");
                break;
            case "number":
                b.Kind = CondKind.Number;
                b.X = Int(o, "x", 0); b.Y = Int(o, "y", 0); b.X2 = Int(o, "x2", 0); b.Y2 = Int(o, "y2", 0);
                b.Op = ParseCmp(Str(o, "op"));
                b.Value = Dec(o, "value", 0m);
                break;
            case "variable":
                b.Kind = CondKind.Variable;
                b.Var = Str(o, "var");
                b.Op = ParseCmp(Str(o, "op"));
                b.Value = Dec(o, "value", 0m);
                break;
            case "countdown":
                if (!top) { warn.Add(Lang.F("{0}：倒數只可以用喺 IF 框，改咗做「搵到圖」", where)); goto default; }
                b.Kind = CondKind.Countdown;
                b.Seconds = Math.Max(0, Dec(o, "seconds", 60m));
                break;
            default:
                b.Kind = CondKind.ImageFound;
                b.Image = Images(o, where + Lang.T(" 條件"), have, warn);
                b.Similarity = Math.Clamp(Int(o, "similarity", 80), 1, 100);
                b.ClickFound = Bool(o, "click_found");
                break;
        }
        if (top) b.Interval = Math.Max(0.05m, Dec(o, "interval", 1m));
        if (b.Kind is CondKind.ImageFound or CondKind.ImageNotFound && ReadRgn(o) is { } rg)
        {
            b.Rgn = true;
            (b.RX1, b.RY1, b.RX2, b.RY2) = rg;
        }
        return b;
    }
}

/// <summary>每個腳本嘅 AI 對話（轉頁 / 重開編輯器都仲喺度，程式閂咗就冇）。</summary>
internal sealed class AssistState
{
    public readonly List<(string Role, string Text)> Msgs = new();
    public string? Session;
    public string? ProviderKey;
    public bool Open;
    public string? Pending;          // AI 最新建議、未套用嘅 JSON
    public (bool Loop, List<Step> Steps, List<IfBlock> Ifs)? Undo;
    public List<ThreadBlock>? UndoThreads;

    private static readonly Dictionary<Script, AssistState> All = new();
    public static AssistState Of(Script sc) => All.TryGetValue(sc, out var s) ? s : All[sc] = new AssistState();
}

/// <summary>編輯腳本頁右下角嘅「雲」掣 + 展開嘅 AI 對話欄。</summary>
internal sealed class ScriptAssist
{
    private readonly Control _host;
    private readonly Script _sc;
    private readonly Action _reload;
    private readonly AssistState _st;
    private readonly CloudButton _cloud;
    private readonly AssistPanel _panel;
    private AiProvider? _prov;
    private Process? _running;
    private bool _stopped;

    /// <param name="reload">套用咗新腳本之後，重新整個編輯器。</param>
    public ScriptAssist(Control host, Script sc, Action reload)
    {
        _host = host;
        _sc = sc;
        _reload = reload;
        _st = AssistState.Of(sc);
        _cloud = new CloudButton { Visible = false };
        _panel = new AssistPanel { Visible = false };
        _cloud.Click += (_, _) => Toggle();
        _panel.CloseRequested += () => { _st.Open = false; Layout(); };
        _panel.SendRequested += async () => await SendAsync();
        _panel.ApplyRequested += Apply;
        _panel.UndoRequested += Undo;
        _panel.ClearRequested += Clear;
        host.Controls.Add(_panel);
        host.Controls.Add(_cloud);
        host.Resize += (_, _) => Layout();
        host.HandleCreated += async (_, _) => await CheckAsync();
        host.Disposed += (_, _) => { if (_running != null) { _stopped = true; try { _running.Kill(true); } catch { } } };
    }

    /// <summary>揀咗 AI 而且登入咗先出個雲。</summary>
    private async Task CheckAsync()
    {
        var p = AiProvider.LoadCurrent();
        if (p == null) return;
        string? exe = await Task.Run(p.Find);
        if (exe == null) return;
        var a = await p.AuthAsync(exe);
        if (a.LoggedIn != true || _host.IsDisposed) return;
        _prov = p;
        if (_st.ProviderKey != p.Key) { _st.Session = null; _st.ProviderKey = p.Key; }
        _panel.SetTitle(Lang.F("AI 助手 · {0}", p.Name));
        _panel.Render(_st, p.Name);
        _cloud.Visible = true;
        Layout();
    }

    private void Toggle()
    {
        _st.Open = !_st.Open;
        Layout();
        if (_st.Open) _panel.FocusInput();
    }

    private void Layout()
    {
        int m = Ui.S(28), size = Ui.S(52);
        var cs = _host.ClientSize;
        _cloud.SetBounds(cs.Width - size - m, cs.Height - size - Ui.S(20), size, size);
        _cloud.BringToFront();
        bool open = _st.Open && _cloud.Visible;
        if (open)
        {
            int w = Math.Min(Ui.S(400), cs.Width - Ui.S(40));
            int h = Math.Min(Ui.S(500), _cloud.Top - Ui.S(30));
            _panel.SetBounds(cs.Width - w - m, _cloud.Top - h - Ui.S(10), w, Math.Max(Ui.S(200), h));
            _panel.BringToFront();
        }
        _panel.Visible = open;
        _cloud.Active = open;
    }

    private void Clear()
    {
        if (_running != null) return;
        _st.Msgs.Clear();
        _st.Session = null;
        _st.Pending = null;
        _panel.Render(_st, _prov?.Name ?? "AI");
    }

    private string BuildPrompt(string q, bool first)
    {
        var imgs = ScriptJson.ExistingImages(_sc);
        var sb = new StringBuilder();
        if (first)
        {
            sb.AppendLine("你係「多功能工具」入面嘅腳本助手，幫用戶寫 / 改佢而家編輯緊嘅自動化腳本（類似按鍵精靈）。");
            sb.AppendLine(Mail.MailAi.ReplyRule + "唔好用任何工具，唔好讀寫檔案，直接答。");
            sb.AppendLine();
            sb.AppendLine(ScriptJson.Spec);
            sb.AppendLine("規則：");
            sb.AppendLine("- 要改腳本嘅話，喺回答最後用一個 ```json code block 俾出「完整」嘅新腳本 JSON（唔係淨係改咗嘅部分）。淨係解答問題、唔使改就唔好俾 JSON。");
            sb.AppendLine("- 你冇辦法整新圖片：images 只可以用下面列出嘅檔名；需要新圖就留空 []，並提用戶自己喺嗰步撳截圖。");
            sb.AppendLine("- 唔知座標就用 0,0，並提用戶之後喺嗰步撳「座標」揀位置。");
            sb.AppendLine("- 盡量保留用戶原有嘅步驟同設定，只改佢要求嘅部分。");
            sb.AppendLine("- " + Mail.MailAi.UiTextRule);
            sb.AppendLine();
        }
        sb.AppendLine("腳本資料夾而家有嘅圖片：" + (imgs.Count > 0 ? string.Join("、", imgs) : "（冇）"));
        if (_sc.Relative && _sc.TargetProcess.Length > 0) sb.AppendLine("注意：呢個腳本揀咗程式視窗，所有 x / y 座標都係相對程式視窗內容區左上角，唔係螢幕座標。");
        sb.AppendLine("而家嘅腳本（用戶可能自己改過，以呢個為準）：");
        sb.AppendLine("```json");
        sb.AppendLine(ScriptJson.ToJson(_sc));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("用戶：" + q);
        return sb.ToString();
    }

    /// <summary>拎回答入面最後一個 JSON code block（要似腳本）。</summary>
    private static (string Text, string? Json) Extract(string answer)
    {
        var matches = Regex.Matches(answer, "```(?:json)?\\s*\\n(.*?)```", RegexOptions.Singleline);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            string body = matches[i].Groups[1].Value.Trim();
            if (!body.StartsWith('{') || !body.Contains("\"steps\"")) continue;
            string text = (answer[..matches[i].Index] + answer[(matches[i].Index + matches[i].Length)..]).Trim();
            return (text, body);
        }
        return (answer.Trim(), null);
    }

    private async Task SendAsync()
    {
        if (_running != null)
        {
            _stopped = true;
            try { _running.Kill(true); } catch { }
            return;
        }
        if (_prov == null) return;
        string q = _panel.TakeInput();
        if (q.Length == 0) return;
        string? exe = _prov.Find();
        if (exe == null) { _panel.System(Lang.F("搵唔到 {0}。", _prov.ToolName)); return; }
        var files = _panel.TakeFiles();

        _stopped = false;
        _st.Msgs.Add(("user", q + (files.Count > 0 ? "\n" + AttachBar.Label(files) : "")));
        _panel.Render(_st, _prov.Name);
        _panel.Thinking(Lang.F("{0} 諗緊…", _prov.Name));
        _panel.SetBusy(true);

        AiProvider.Answer ans;
        try
        {
            ans = await _prov.AskAsync(exe, BuildPrompt(q, _st.Session == null), _st.Session, p => _running = p, files: files);
            if (ans.IsError && !_stopped && _st.Session != null && AiProvider.IsSessionGone(ans.Text))
            {
                _st.Session = null;
                ans = await _prov.AskAsync(exe, BuildPrompt(q, true), null, p => _running = p, files: files);
            }
        }
        catch (Exception ex) { ans = new AiProvider.Answer(Lang.T("出錯：") + ex.Message, null, true); }
        _running = null;
        if (_host.IsDisposed) return;
        _panel.SetBusy(false);

        if (ans.SessionId != null) _st.Session = ans.SessionId;
        if (_stopped) _st.Msgs.Add(("system", Lang.T("已停止。")));
        else if (ans.IsError) _st.Msgs.Add(("error", ans.Text));
        else
        {
            var (text, json) = Extract(ans.Text);
            if (json != null)
            {
                _st.Pending = json;
                text = (text.Length > 0 ? text + "\n\n" : "") + Lang.T("（已經寫好新腳本，撳下面「套用到腳本」就會換上）");
            }
            _st.Msgs.Add(("ai", text));
        }
        _panel.Render(_st, _prov.Name);
        _panel.FocusInput();
    }

    private void Apply()
    {
        if (_st.Pending == null) return;
        if (ScriptRunner.IsRunning(_sc)) { _panel.System(Lang.T("腳本行緊，停咗先再套用。")); return; }
        ScriptJson.Parsed p;
        try { p = ScriptJson.FromJson(_st.Pending, _sc); }
        catch (Exception ex)
        {
            _st.Msgs.Add(("error", Lang.T("AI 俾嘅腳本格式唔啱，套用唔到：") + ex.Message));
            _st.Pending = null;
            _panel.Render(_st, _prov?.Name ?? "AI");
            return;
        }
        // 留底，可以復原
        _st.Undo = (_sc.Loop, _sc.Steps.Select(x => x.Clone()).ToList(), _sc.Ifs.Select(x => x.Clone()).ToList());
        _st.UndoThreads = _sc.Threads.ToList();
        _sc.Threads.Clear();
        if (p.Threads != null) _sc.Threads.AddRange(p.Threads);
        _sc.Loop = p.Loop;
        if (p.Runs is { } pr) _sc.Runs = pr;
        _sc.Steps.Clear();
        _sc.Steps.AddRange(p.Steps);
        _sc.Ifs.Clear();
        _sc.Ifs.AddRange(p.Ifs);
        _sc.Save();
        _st.Pending = null;
        string msg = Lang.T("已套用新腳本。");
        if (p.Warnings.Count > 0) msg += Lang.T("\n要你自己補嘅地方：\n• ") + string.Join("\n• ", p.Warnings.Take(12));
        _st.Msgs.Add(("system", msg));
        _st.Open = true;
        _reload();
    }

    private void Undo()
    {
        if (_st.Undo is not { } u) return;
        if (ScriptRunner.IsRunning(_sc)) { _panel.System(Lang.T("腳本行緊，停咗先再復原。")); return; }
        _sc.Loop = u.Loop;
        _sc.Steps.Clear();
        _sc.Steps.AddRange(u.Steps);
        _sc.Ifs.Clear();
        _sc.Ifs.AddRange(u.Ifs);
        if (_st.UndoThreads != null) { _sc.Threads.Clear(); _sc.Threads.AddRange(_st.UndoThreads); }
        _sc.Save();
        _st.Undo = null;
        _st.UndoThreads = null;
        _st.Msgs.Add(("system", Lang.T("已復原返套用之前嘅腳本。")));
        _reload();
    }
}

/// <summary>圓形「雲」掣。</summary>
internal sealed class CloudButton : Control
{
    private bool _hover;
    public bool Active { get => _active; set { _active = value; Invalidate(); } }
    private bool _active;

    public CloudButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Ui.Accent;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width, Height);
        Region = new Region(path);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = _active ? Ui.Accent2 : _hover ? Ui.Mix(Ui.Accent, Color.White, 0.15f) : Ui.Accent;
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, -1, -1, Width + 1, Height + 1);
        // 雲：三個圓 + 底部長方形
        float w = Width, h = Height, cx = w / 2f, cy = h / 2f + h * 0.04f, u = w / 52f;
        using var cloud = new GraphicsPath();
        cloud.AddEllipse(cx - 15 * u, cy - 3 * u, 14 * u, 14 * u);
        cloud.AddEllipse(cx - 8 * u, cy - 12 * u, 18 * u, 18 * u);
        cloud.AddEllipse(cx + 3 * u, cy - 4 * u, 13 * u, 13 * u);
        cloud.AddRectangle(new RectangleF(cx - 8 * u, cy + 2 * u, 17 * u, 9 * u));
        using var white = new SolidBrush(Color.White);
        using (var region = new Region(cloud)) g.FillRegion(white, region);
    }
}

/// <summary>展開嘅細 AI 對話欄。</summary>
internal sealed class AssistPanel : Panel
{
    public event Action? CloseRequested, SendRequested, ApplyRequested, UndoRequested, ClearRequested;
    private readonly Label _title;
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    private readonly FancyButton _send, _apply, _undo, _clear, _clip;
    private readonly Panel _actions;
    private readonly AttachBar _attach = new() { Dock = DockStyle.Top };

    public AssistPanel()
    {
        BackColor = Ui.CardHi;
        Padding = new Padding(1);
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding(Ui.S(12)) };
        Controls.Add(inner);

        var head = new Panel { Dock = DockStyle.Top, Height = Ui.S(34), BackColor = Ui.Card };
        _title = new Label { Text = Lang.T("AI 助手"), Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        var close = new FancyButton { Text = "✕", Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(32), Ui.S(30)), Dock = DockStyle.Right };
        close.Click += (_, _) => CloseRequested?.Invoke();
        _clear = new FancyButton { Text = Lang.T("清除"), Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(56), Ui.S(30)), Dock = DockStyle.Right };
        _clear.Click += (_, _) => ClearRequested?.Invoke();
        head.Controls.Add(_title);
        head.Controls.Add(_clear);
        head.Controls.Add(close);

        _log = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text,
            Font = Ui.F(9.75f), Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false
        };

        _actions = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(42), BackColor = Ui.Card, Visible = false, Padding = new Padding(0, Ui.S(6), 0, 0) };
        _apply = new FancyButton { Text = Lang.T("套用到腳本"), Kind = BtnKind.Success, Font = Ui.F(9.5f), Size = new Size(Ui.S(110), Ui.S(34)), Dock = DockStyle.Left };
        _apply.Click += (_, _) => ApplyRequested?.Invoke();
        _undo = new FancyButton { Text = Lang.T("復原"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(72), Ui.S(34)), Dock = DockStyle.Right };
        _undo.Click += (_, _) => UndoRequested?.Invoke();
        _actions.Controls.Add(_apply);
        _actions.Controls.Add(_undo);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(74), BackColor = Ui.Card, Padding = new Padding(0, Ui.S(8), 0, 0) };
        var inputHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Input, Padding = new Padding(Ui.S(8)) };
        _input = new TextBox { Multiline = true, BorderStyle = BorderStyle.None, BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(9.75f), Dock = DockStyle.Fill };
        inputHost.Controls.Add(_input);
        _send = new FancyButton { Text = Lang.T("傳送"), Kind = BtnKind.Primary, Font = Ui.F(9.5f), Size = new Size(Ui.S(64), Ui.S(66)), Dock = DockStyle.Right };
        _send.Click += (_, _) => SendRequested?.Invoke();
        var gap = new Panel { Dock = DockStyle.Right, Width = Ui.S(8), BackColor = Ui.Card };
        // 附件：📎 揀圖片 / 文件；亦可以拖入嚟或者 Ctrl+V 貼截圖
        _clip = new FancyButton { Text = "📎", Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(36), Ui.S(66)), Dock = DockStyle.Right };
        _clip.Click += (_, _) => _attach.Pick(FindForm());
        new ToolTip().SetToolTip(_clip, Lang.T("附加圖片或文件（亦可以拖入嚟，或者 Ctrl+V 貼上截圖）"));
        var gap2 = new Panel { Dock = DockStyle.Right, Width = Ui.S(6), BackColor = Ui.Card };
        bottom.Controls.Add(inputHost);
        bottom.Controls.Add(gap2);
        bottom.Controls.Add(_clip);
        bottom.Controls.Add(gap);
        bottom.Controls.Add(_send);
        bottom.Controls.Add(_attach);
        _attach.Changed += () =>
        {
            _attach.Width = Math.Max(Ui.S(100), bottom.ClientSize.Width);
            bottom.Height = Ui.S(74) + (_attach.Visible ? _attach.Height : 0);
        };
        _attach.Wire(_input, _log);
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendRequested?.Invoke(); }
        };

        inner.Controls.Add(_log);
        inner.Controls.Add(_actions);
        inner.Controls.Add(bottom);
        inner.Controls.Add(head);
    }

    public void SetTitle(string t) => _title.Text = t;
    public void FocusInput() => _input.Focus();

    /// <summary>未有對話時顯示嘅說明（唔設就用腳本助手嘅）。</summary>
    public string? Intro;

    /// <summary>「套用」掣嘅字（預設「套用到腳本」）。</summary>
    public string ApplyText
    {
        set
        {
            _apply.Text = value;
            _apply.Width = Math.Max(Ui.S(110), TextRenderer.MeasureText(value, _apply.Font).Width + Ui.S(30));
        }
    }

    public string TakeInput()
    {
        string t = _input.Text.Trim();
        if (t.Length > 0) _input.Clear();
        if (t.Length == 0 && _attach.Files.Count > 0) t = Lang.T("請睇吓附件。"); // 淨係傳附件
        return t;
    }

    /// <summary>傳送時攞走附件（已複製去 AI 資料夾）；冇就空。</summary>
    public List<string> TakeFiles() => _attach.Take();

    public void SetBusy(bool busy)
    {
        _send.Text = busy ? Lang.T("停止") : Lang.T("傳送");
        _send.Kind = busy ? BtnKind.Danger : BtnKind.Primary;
        _input.ReadOnly = busy;
        _clip.Enabled = !busy;
        _apply.Enabled = _undo.Enabled = _clear.Enabled = !busy;
        _send.Invalidate();
    }

    public void Render(AssistState st, string aiName)
    {
        _log.Clear();
        if (st.Msgs.Count == 0 && Intro != null) Line(null, Intro, Ui.TextFaint, 9f);
        else if (st.Msgs.Count == 0)
            Line(null, Lang.T("話我知你想個腳本做啲咩，例如：「每 5 秒撳一下 F1，搵到怪物圖就點佢」。\n") +
                       Lang.T("我會睇住你而家個腳本幫你改，寫好之後撳「套用到腳本」就會換上（可以復原）。"), Ui.TextFaint, 9f);
        foreach (var (role, text) in st.Msgs)
        {
            switch (role)
            {
                case "user": Line(Lang.T("你"), text, Ui.Accent); break;
                case "ai": Line(aiName, text, Ui.Success); break;
                case "error": Line(Lang.T("錯誤"), text, Ui.Danger); break;
                default: Line(null, text, Ui.TextFaint, 9f); break;
            }
        }
        _apply.Visible = st.Pending != null;
        _undo.Visible = st.Undo != null;
        _actions.Visible = st.Pending != null || st.Undo != null;
    }

    public void System(string text) => Line(null, text, Ui.TextFaint, 9f);

    public void Thinking(string text) => Line(null, text, Ui.TextFaint, 9f);

    private void Line(string? who, string text, Color color, float size = 9.75f)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        string sep = _log.TextLength > 0 ? "\n" : "";
        if (who != null)
        {
            _log.SelectionColor = color;
            _log.SelectionFont = Ui.F(9.25f, FontStyle.Bold);
            _log.AppendText(sep + who + "\n");
            sep = "";
            _log.SelectionColor = Ui.Text;
        }
        else _log.SelectionColor = color;
        _log.SelectionFont = Ui.F(size);
        _log.AppendText(sep + text.Replace("\r\n", "\n").TrimEnd() + "\n");
        _log.ScrollToCaret();
    }
}
