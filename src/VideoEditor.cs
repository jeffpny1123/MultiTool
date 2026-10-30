using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Video;

// =====================================================================
//  影片剪輯：時間軸（文字 / 影片 / 音樂三條軌）、即時預覽（ffmpeg 解碼 + waveOut 出聲）、匯出 MP4。
//  所有效果都係砌一條 ffmpeg filter graph：預覽同匯出用同一條，所以預覽見到乜，匯出就係乜。
// =====================================================================

/// <summary>主軌上面一段片 / 一張相。</summary>
internal sealed class EdClip
{
    public string Path = "";
    public bool IsImage;
    public double In, Out = 4;          // 原片嘅開始 / 結束（秒）；相片：In = 0，Out = 顯示幾耐
    public double SrcDur;               // 原片長度（相片 = 0）
    public bool HasAudio;
    /// <summary>聲音已經分離咗去音樂軌（段片本身唔再出聲）。</summary>
    public bool Detached;
    public double Speed = 1;            // 0.25x – 4x
    public double Volume = 1;           // 0 – 2
    public bool Mute;
    public double Bright, Contrast = 1, Sat = 1; // 亮度 -1..1、對比 0..2、飽和度 0..3
    public bool Gray;
    public int Rotate;                  // 0 / 90 / 180 / 270
    public bool Flip;
    public bool Fill;                   // true = 填滿（裁走多出嘅）；false = 完整顯示（加黑邊）
    public string Trans = "";           // 去下一段嘅轉場（空 = 冇）
    public double TransDur = 0.5;
    public double FadeIn, FadeOut;      // 開頭 / 結尾淡入淡出（秒，0 = 冇）
    public string FadeColor = "#000000"; // 淡入淡出嘅顏色（黑、白或者自己揀）
    // 喺畫面嘅位置同大細（可以喺預覽度拖）：X / Y = 中心（畫面比例 0–1），Zoom = 相對「完整顯示 / 填滿」嘅倍數
    public double X = 0.5, Y = 0.5, Zoom = 1;
    public int SrcW, SrcH;              // 原片闊高（預覽計框用；0 = 未知）

    public bool Moved => Math.Abs(X - 0.5) > 0.0005 || Math.Abs(Y - 0.5) > 0.0005 || Math.Abs(Zoom - 1) > 0.0005;

    public double Len => Math.Max(0.05, (Out - In) / (IsImage ? 1 : Speed));
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>文字軌：標題 / 字幕。</summary>
internal sealed class EdText
{
    public string Text = "";
    public double Start, Dur = 3;
    public int Pos = 2;                 // 舊版：0 上、1 中、2 下（X / Y 未設定時用）
    public double X = -1, Y = -1;       // 位置：文字中心喺畫面嘅比例（0–1）；-1 = 跟 Pos
    public int Size = 64;               // 以 1080p 計嘅字體大小
    public string Color = "#FFFFFF";
    public bool Box = true;             // 半透明黑底
    public bool Fade = true;
    public int Row;                     // 第幾條文字軌（數字大 = 喺上面）
}

internal enum EdTrack { Text, Img, Vid, Aud }

/// <summary>圖片軌：疊喺畫面上面嘅相（貼圖 / logo），可以喺預覽度拖位置。</summary>
internal sealed class EdOverlay
{
    public string Path = "";
    public double Start, Dur = 3;
    public double X = 0.5, Y = 0.5;     // 中心位置（畫面比例 0–1）
    public double Scale = 0.3;          // 闊度佔畫面幾多（0.02–2）
    public double Opacity = 1;
    public bool Fade = true;
    public int ImgW, ImgH;              // 原圖大細（預覽計框用）
    public int Row;                     // 圖片：第幾條圖片軌；影片：第幾條影片軌（1 起，0 係主影片軌）
    // ---- 影片疊加（影片 2、3…軌，畫中畫）----
    public bool IsVideo;
    public double In, SrcDur;           // 由原片第幾秒開始；原片長度
    public bool HasAudio, Mute;
    public double Volume = 1;

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>音樂軌：背景音樂 / 音效。</summary>
internal sealed class EdMusic
{
    public string Path = "";
    public double Start, In, Out;
    public double SrcDur;
    public double Volume = 0.6;
    public double FadeIn = 1, FadeOut = 1;
    /// <summary>播放速度（由影片分離出嚟嘅音軌會跟返段片嘅速度）。</summary>
    public double Speed = 1;
    /// <summary>喺第幾條音軌（0 起）；唔同音軌可以重疊，一齊混音。</summary>
    public int Row;

    public double Sp => Speed > 0.01 ? Speed : 1;
    public double Len => Math.Max(0.05, (Out - In) / Sp);
    public string Name => System.IO.Path.GetFileName(Path);
}

internal sealed class EdProject
{
    public string Name = "";
    public int W = 1920, H = 1080, Fps = 30;
    public List<EdClip> Clips = new();
    public List<EdText> Texts = new();
    public List<EdOverlay> Overlays = new();
    public List<EdMusic> Music = new();

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static EdProject? FromJson(string s)
    {
        try
        {
            var p = JsonSerializer.Deserialize<EdProject>(s, Json);
            if (p != null && !s.Contains("\"Ver\"")) { p.Ver = 1; p.Migrate(); }
            return p;
        }
        catch { return null; }
    }
    public const int MaxRows = 8, MaxAudioRows = MaxRows;
    /// <summary>每種軌嘅數目（撳軌名旁邊「+」加）。影片軌包埋主影片軌（第 1 條）。</summary>
    public int TextRows = 1, ImgRows = 1, VidRows = 1, AudioRows = 1;

    private static int Cnt(int set, IEnumerable<int> used) => Math.Clamp(Math.Max(set, used.DefaultIfEmpty(0).Max() + 1), 1, MaxRows);
    public int TextRowCount() => Cnt(TextRows, Texts.Select(t => t.Row));
    public int ImgRowCount() => Cnt(ImgRows, Overlays.Where(o => !o.IsVideo).Select(o => o.Row));
    public int VidRowCount() => Cnt(VidRows, Overlays.Select(o => o.Row));

    /// <summary>格式版本：2 = 圖片放喺影片軌（冇咗獨立圖片軌）。</summary>
    public int Ver = 2;
    /// <summary>舊專案：圖片軌嘅圖搬去影片軌（放喺影片疊加軌上面）。</summary>
    public void Migrate()
    {
        if (Ver >= 2) return;
        int vmax = Overlays.Where(o => o.IsVideo).Select(o => o.Row).DefaultIfEmpty(0).Max();
        foreach (var o in Overlays.Where(o => !o.IsVideo)) o.Row = vmax + 1 + Math.Max(0, o.Row);
        VidRows = Math.Max(VidRows, Overlays.Select(o => o.Row).DefaultIfEmpty(0).Max() + 1);
        Ver = 2;
    }
    public int AudioRowCount() => Cnt(AudioRows, Music.Select(m => m.Row));

    public int RowCount(EdTrack k) => k switch { EdTrack.Text => TextRowCount(), EdTrack.Img => ImgRowCount(), EdTrack.Vid => VidRowCount(), _ => AudioRowCount() };
    public void SetRows(EdTrack k, int n)
    {
        n = Math.Clamp(n, 1, MaxRows);
        switch (k) { case EdTrack.Text: TextRows = n; break; case EdTrack.Img: ImgRows = n; break; case EdTrack.Vid: VidRows = n; break; default: AudioRows = n; break; }
    }

    /// <summary>某條軌上面嘅嘢（時間 + 長度）。</summary>
    public IEnumerable<(object Obj, double Start, double Len)> OnRow(EdTrack k, int row)
    {
        switch (k)
        {
            case EdTrack.Text: foreach (var t in Texts.Where(t => t.Row == row)) yield return (t, t.Start, t.Dur); break;
            case EdTrack.Img: break;
            case EdTrack.Vid:
                if (row == 0) { var s = Starts(); for (int i = 0; i < Clips.Count; i++) yield return (Clips[i], s[i], Clips[i].Len); }
                else foreach (var o in Overlays.Where(o => o.Row == row)) yield return (o, o.Start, o.Dur);
                break;
            default: foreach (var m in Music.Where(m => m.Row == row)) yield return (m, m.Start, m.Len); break;
        }
    }

    /// <summary>搵一條喺 [start, start+len) 冇嘢嘅軌；全部都有嘢就自動加一條。影片由第 2 條開始搵。</summary>
    public int FreeRow(EdTrack k, double start, double len, object? except = null)
    {
        int rows = RowCount(k);
        for (int r = k == EdTrack.Vid ? 1 : 0; r < rows; r++)
            if (!OnRow(k, r).Any(x => x.Obj != except && x.Start < start + len - 0.01 && start < x.Start + x.Len - 0.01)) return r;
        if (rows < MaxRows) { SetRows(k, rows + 1); return rows; }
        return k == EdTrack.Vid ? Math.Max(1, rows - 1) : 0;
    }

    public int FreeMusicRow(double start, double len, EdMusic? except = null) => FreeRow(EdTrack.Aud, start, len, except);

    /// <summary>疊加次序（先畫嘅喺底）：影片 2、3…（圖片同影片一齊排）。</summary>
    public IEnumerable<EdOverlay> OverlayOrder() => Overlays.OrderBy(o => o.Row);
    public IEnumerable<EdText> TextOrder() => Texts.OrderBy(t => t.Row);

    public EdProject Clone() => FromJson(ToJson()) ?? new EdProject();

    /// <summary>每段片喺時間軸嘅開始時間（有轉場就重疊）。</summary>
    public double[] Starts()
    {
        var s = new double[Clips.Count];
        double t = 0;
        for (int i = 0; i < Clips.Count; i++)
        {
            s[i] = t;
            t += Clips[i].Len - (i + 1 < Clips.Count ? TransLen(i) : 0);
        }
        return s;
    }

    /// <summary>第 i 段去下一段嘅轉場長度（夾喺兩段片長度之內）。</summary>
    public double TransLen(int i)
    {
        if (i < 0 || i + 1 >= Clips.Count || Clips[i].Trans.Length == 0) return 0;
        return Math.Clamp(Clips[i].TransDur, 0.1, Math.Min(Clips[i].Len, Clips[i + 1].Len) * 0.9);
    }

    public double VideoEnd
    {
        get
        {
            if (Clips.Count == 0) return 0;
            var s = Starts();
            return s[^1] + Clips[^1].Len;
        }
    }

    public double Duration =>
        new[]
        {
            VideoEnd, Texts.Select(t => t.Start + t.Dur).DefaultIfEmpty(0).Max(), Music.Select(m => m.Start + m.Len).DefaultIfEmpty(0).Max(),
            Overlays.Select(o => o.Start + o.Dur).DefaultIfEmpty(0).Max()
        }.Max();
}

/// <summary>專案檔：data\editor\projects\*.json。</summary>
internal static class EdStore
{
    public static string Dir => Path.Combine(AppPaths.Data, "editor");
    public static string ProjDir => Path.Combine(Dir, "projects");
    public static string Tmp => Path.Combine(Dir, "tmp");
    private static string LastFile => Path.Combine(Dir, "last.txt");

    public static string FileOf(string name) => Path.Combine(ProjDir, Scripts.Script.Sanitize(name) + ".json");

    public static List<string> List()
    {
        try
        {
            if (!Directory.Exists(ProjDir)) return new();
            return Directory.GetFiles(ProjDir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Select(Path.GetFileNameWithoutExtension).Select(x => x!).ToList();
        }
        catch { return new(); }
    }

    public static void Save(EdProject p)
    {
        try
        {
            Directory.CreateDirectory(ProjDir);
            File.WriteAllText(FileOf(p.Name), p.ToJson(), Encoding.UTF8);
            File.WriteAllText(LastFile, p.Name, Encoding.UTF8);
        }
        catch (Exception ex) { AppPaths.LogError("儲存剪片專案", ex); }
    }

    public static EdProject? Load(string name)
    {
        try
        {
            var f = FileOf(name);
            if (!File.Exists(f)) return null;
            var p = EdProject.FromJson(File.ReadAllText(f, Encoding.UTF8));
            if (p != null) { p.Name = name; p.Migrate(); }
            return p;
        }
        catch { return null; }
    }

    public static string? Last()
    {
        try { return File.Exists(LastFile) ? File.ReadAllText(LastFile, Encoding.UTF8).Trim() : null; } catch { return null; }
    }

    public static void Delete(string name)
    {
        try { File.Delete(FileOf(name)); } catch { }
    }

    public static string NewName()
    {
        var have = new HashSet<string>(List(), StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
        {
            string n = Lang.T("我的影片") + " " + i;
            if (!have.Contains(n)) return n;
        }
    }
}

/// <summary>媒體資料：長度、有冇聲、解像度（用 ffmpeg -i 讀）。</summary>
internal static class MediaInfo
{
    public static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    public static readonly string[] AudioExt = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma" };
    public static readonly string[] VideoExt = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v", ".ts", ".mpg", ".mpeg" };

    public static bool IsImage(string p) => ImageExt.Contains(Path.GetExtension(p).ToLowerInvariant());
    public static bool IsAudio(string p) => AudioExt.Contains(Path.GetExtension(p).ToLowerInvariant());

    private static readonly ConcurrentDictionary<string, (double Dur, bool Audio, bool Video, int W, int H)> Cache = new();

    public static (double Dur, bool Audio, bool Video, int W, int H) Probe(string path)
    {
        if (Cache.TryGetValue(path, out var c)) return c;
        var psi = new ProcessStartInfo(Ffmpeg.Exe, $"-hide_banner -i \"{path}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        double dur = 0;
        var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (m.Success) dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        bool video = Regex.IsMatch(err, @"Stream #\d+:\d+.*?: Video:(?!.*attached pic)");
        bool audio = Regex.IsMatch(err, @"Stream #\d+:\d+.*?: Audio:");
        int w = 0, h = 0;
        var vm = Regex.Match(err, @"Video:.*?,\s*(\d{2,5})x(\d{2,5})");
        if (vm.Success) { w = int.Parse(vm.Groups[1].Value); h = int.Parse(vm.Groups[2].Value); }
        if (!video && !audio) throw new InvalidOperationException(Lang.F("讀唔到「{0}」，可能唔係影片 / 相片 / 聲音檔。", Path.GetFileName(path)));
        var r = (dur, audio, video, w, h);
        Cache[path] = r;
        return r;
    }

    /// <summary>某一格嘅細圖（時間軸縮圖用）。</summary>
    public static Bitmap? Thumb(string path, double at, int height)
    {
        try
        {
            string ss = IsImage(path) ? "" : $"-ss {F(Math.Max(0, at))} ";
            var psi = new ProcessStartInfo(Ffmpeg.Exe, $"-hide_banner -loglevel error {ss}-i \"{path}\" -frames:v 1 -vf scale=-2:{height} -f image2pipe -vcodec bmp -")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(10000);
            if (ms.Length == 0) return null;
            ms.Position = 0;
            using var b = new Bitmap(ms);
            return new Bitmap(b);
        }
        catch { return null; }
    }

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>砌 ffmpeg filter graph（預覽同匯出共用）。</summary>
internal static class EdGraph
{
    public static readonly string[] Transitions =
        { "fade", "dissolve", "fadeblack", "fadewhite", "slideleft", "slideright", "slideup", "slidedown", "wipeleft", "wiperight", "circleopen", "circleclose", "radial", "pixelize" };

    public static string TransName(string t) => t switch
    {
        "" => Lang.T("冇"),
        "fade" => Lang.T("淡入淡出"),
        "dissolve" => Lang.T("溶解"),
        "fadeblack" => Lang.T("經黑色"),
        "fadewhite" => Lang.T("經白色"),
        "slideleft" => Lang.T("向左滑"),
        "slideright" => Lang.T("向右滑"),
        "slideup" => Lang.T("向上滑"),
        "slidedown" => Lang.T("向下滑"),
        "wipeleft" => Lang.T("向左抹"),
        "wiperight" => Lang.T("向右抹"),
        "circleopen" => Lang.T("圓形打開"),
        "circleclose" => Lang.T("圓形收埋"),
        "radial" => Lang.T("時鐘掃"),
        "pixelize" => Lang.T("馬賽克"),
        _ => t
    };

    private static string F(double v) => MediaInfo.F(v);

    /// <summary>ffmpeg filter 入面嘅路徑：/ 分隔、: 要 escape。</summary>
    private static string Esc(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string? _font;
    public static string Font
    {
        get
        {
            if (_font != null) return _font;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var f in new[] { "msjhbd.ttc", "msjh.ttc", "msyhbd.ttc", "msyh.ttc", "arialbd.ttf", "arial.ttf", "segoeui.ttf" })
                if (File.Exists(Path.Combine(dir, f))) return _font = Path.Combine(dir, f);
            return _font = "";
        }
    }

    /// <summary>文字中心位置（舊專案冇 X / Y 就用上 / 中 / 下）。</summary>
    public static (double X, double Y) TextPos(EdText t) =>
        t.X >= 0 && t.Y >= 0 ? (t.X, t.Y) : (0.5, t.Pos switch { 0 => 0.12, 1 => 0.5, _ => 0.86 });

    private static string AFmt => "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";

    /// <summary>
    /// 由 from 秒開始砌。W×H = 輸出大細（預覽用細啲）。回傳 ffmpeg 輸入參數同 graph 文字；輸出 label：[vout] / [aout]。
    /// </summary>
    public static (string Inputs, string Graph, double Length) Build(EdProject p, double from, int W, int H, bool video, bool audio, string tmpDir)
    {
        var inputs = new StringBuilder();
        var g = new StringBuilder();
        int idx = 0;
        double total = p.Duration;
        from = Math.Clamp(from, 0, Math.Max(0, total - 0.05));
        double length = Math.Max(0.1, total - from);
        int fps = Math.Clamp(p.Fps, 10, 60);
        var starts = p.Starts();

        // ---- 主軌 ----
        var parts = new List<(int Clip, double Len, double SrcStart)>();
        for (int i = 0; i < p.Clips.Count; i++)
        {
            var c = p.Clips[i];
            double s = starts[i], e = s + c.Len;
            if (e <= from + 0.02) continue;
            double off = Math.Max(0, from - s);
            double len = c.Len - off;
            if (len < 0.05) continue;
            parts.Add((i, len, c.IsImage ? 0 : c.In + off * c.Speed));
        }
        var vLabels = new List<string>();
        var aLabels = new List<string>();
        for (int k = 0; k < parts.Count; k++)
        {
            var (ci, len, srcStart) = parts[k];
            var c = p.Clips[ci];
            int inp = idx++;
            if (c.IsImage) inputs.Append($"-loop 1 -framerate {fps} -t {F(len + 0.5)} -i \"{c.Path}\" ");
            else inputs.Append($"-ss {F(srcStart)} -t {F(len * c.Speed + 0.3)} -i \"{c.Path}\" ");
            if (video)
            {
                var f = new List<string> { c.IsImage || c.Speed == 1 ? "setpts=PTS-STARTPTS" : $"setpts=(PTS-STARTPTS)/{F(c.Speed)}" };
                if (c.Rotate == 90) f.Add("transpose=1");
                else if (c.Rotate == 180) f.Add("transpose=1,transpose=1");
                else if (c.Rotate == 270) f.Add("transpose=2");
                if (c.Flip) f.Add("hflip");
                if (c.Moved)
                {
                    // 搬咗位 / 改咗大細：先縮放，四邊加一個畫面咁闊嘅黑邊，再由中心位置裁返 W×H（超出畫面嘅部分會被裁走）
                    double z = Math.Clamp(c.Zoom, 0.05, 5);
                    int zw = Math.Max(2, (int)Math.Round(W * z / 2) * 2), zh = Math.Max(2, (int)Math.Round(H * z / 2) * 2);
                    string mode = c.Fill ? "increase" : "decrease";
                    f.Add($"scale={zw}:{zh}:force_original_aspect_ratio={mode}:force_divisible_by=2");
                    f.Add($"pad=iw+{2 * W}:ih+{2 * H}:{W}:{H}:color=black");
                    f.Add($"crop={W}:{H}:iw/2-{F(W * Math.Clamp(c.X, 0, 1))}:ih/2-{F(H * Math.Clamp(c.Y, 0, 1))}");
                }
                else if (c.Fill) f.Add($"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H}");
                else f.Add($"scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black");
                double sat = c.Gray ? 0 : c.Sat;
                if (Math.Abs(c.Bright) > 0.001 || Math.Abs(c.Contrast - 1) > 0.001 || Math.Abs(sat - 1) > 0.001)
                    f.Add($"eq=brightness={F(c.Bright)}:contrast={F(c.Contrast)}:saturation={F(sat)}");
                f.Add($"fps={fps},format=yuv420p,setsar=1,trim=duration={F(len)},setpts=PTS-STARTPTS");
                // 開頭 / 結尾淡入淡出（由 / 去某個顏色）
                double off0 = c.Len - len; // 預覽由片段中間開始：已經過咗幾多
                string fc = Regex.IsMatch(c.FadeColor ?? "", "^#[0-9A-Fa-f]{6}$") ? "0x" + c.FadeColor![1..] : "black";
                if (c.FadeIn > 0.01 && off0 < c.FadeIn) f.Add($"fade=t=in:st=0:d={F(Math.Max(0.05, Math.Min(c.FadeIn - off0, len)))}:color={fc}");
                if (c.FadeOut > 0.01)
                {
                    double d = Math.Min(c.FadeOut, len);
                    f.Add($"fade=t=out:st={F(Math.Max(0, len - d))}:d={F(d)}:color={fc}");
                }
                g.Append($"[{inp}:v]{string.Join(",", f)}[v{k}];\n");
                vLabels.Add($"v{k}");
            }
            if (audio)
            {
                if (!c.IsImage && c.HasAudio && !c.Mute)
                {
                    var f = new List<string> { "asetpts=PTS-STARTPTS" };
                    double sp = c.Speed;
                    while (sp > 2.0001) { f.Add("atempo=2"); sp /= 2; }
                    while (sp < 0.4999) { f.Add("atempo=0.5"); sp /= 0.5; }
                    if (Math.Abs(sp - 1) > 0.001) f.Add($"atempo={F(sp)}");
                    if (Math.Abs(c.Volume - 1) > 0.001) f.Add($"volume={F(c.Volume)}");
                    f.Add(AFmt);
                    f.Add($"apad,atrim=duration={F(len)},asetpts=PTS-STARTPTS");
                    // 聲音跟埋畫面淡入淡出
                    double aoff = c.Len - len;
                    if (c.FadeIn > 0.01 && aoff < c.FadeIn) f.Add($"afade=t=in:st=0:d={F(Math.Max(0.05, Math.Min(c.FadeIn - aoff, len)))}");
                    if (c.FadeOut > 0.01) { double d = Math.Min(c.FadeOut, len); f.Add($"afade=t=out:st={F(Math.Max(0, len - d))}:d={F(d)}"); }
                    g.Append($"[{inp}:a]{string.Join(",", f)}[a{k}];\n");
                }
                else g.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={F(len)},{AFmt}[a{k}];\n");
                aLabels.Add($"a{k}");
            }
        }

        // ---- 接駁（有轉場就 xfade / acrossfade）----
        string cv = "", ca = "";
        double curLen = 0;
        if (parts.Count == 0)
        {
            if (video) { g.Append($"color=c=black:s={W}x{H}:r={fps},format=yuv420p,trim=duration={F(length)}[base];\n"); cv = "base"; }
            if (audio) { g.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={F(length)},{AFmt}[abase];\n"); ca = "abase"; }
            curLen = length;
        }
        else
        {
            if (video) cv = vLabels[0];
            if (audio) ca = aLabels[0];
            curLen = parts[0].Len;
            for (int k = 1; k < parts.Count; k++)
            {
                int prevClip = parts[k - 1].Clip;
                double d = parts[k].Clip == prevClip + 1 ? p.TransLen(prevClip) : 0;
                d = Math.Min(d, Math.Min(curLen, parts[k].Len) - 0.05);
                string tr = p.Clips[prevClip].Trans;
                if (d >= 0.05 && tr.Length > 0)
                {
                    if (video) { g.Append($"[{cv}][{vLabels[k]}]xfade=transition={tr}:duration={F(d)}:offset={F(curLen - d)}[cv{k}];\n"); cv = $"cv{k}"; }
                    if (audio) { g.Append($"[{ca}][{aLabels[k]}]acrossfade=d={F(d)}[ca{k}];\n"); ca = $"ca{k}"; }
                    curLen += parts[k].Len - d;
                }
                else
                {
                    if (video) { g.Append($"[{cv}][{vLabels[k]}]concat=n=2:v=1:a=0[cv{k}];\n"); cv = $"cv{k}"; }
                    if (audio) { g.Append($"[{ca}][{aLabels[k]}]concat=n=2:v=0:a=1[ca{k}];\n"); ca = $"ca{k}"; }
                    curLen += parts[k].Len;
                }
            }
            // 文字 / 音樂長過影片：補黑畫面 / 靜音
            if (length > curLen + 0.05)
            {
                double pad = length - curLen;
                if (video) { g.Append($"[{cv}]tpad=stop_mode=add:stop_duration={F(pad)}:color=black[cvp];\n"); cv = "cvp"; }
                if (audio) { g.Append($"[{ca}]apad=pad_dur={F(pad)}[cap];\n"); ca = "cap"; }
            }
        }

        // ---- 影片 2、3…軌 + 圖片（疊喺主影片上面、文字下面）----
        var ovAudio = new List<string>();
        {
            int n = 0;
            foreach (var o in p.OverlayOrder())
            {
                double a = o.Start - from, b = o.Start + o.Dur - from;
                if (b <= 0 || !File.Exists(o.Path)) continue;
                bool ovSound = audio && o.IsVideo && o.HasAudio && !o.Mute;
                if (!video && !ovSound) continue;
                int inp = idx++;
                var f = new List<string>();
                if (o.IsVideo)
                {
                    // 由 max(a,0) 開始出畫面：-ss 跳到原片相應位置，setpts 推後
                    double skip = Math.Max(0, -a), vlen = b - Math.Max(0, a);
                    inputs.Append($"-ss {F(o.In + skip)} -t {F(vlen + 0.2)} -i \"{o.Path}\" ");
                    if (ovSound)
                    {
                        var af = new List<string> { "asetpts=PTS-STARTPTS", AFmt, $"atrim=duration={F(vlen)}" };
                        if (Math.Abs(o.Volume - 1) > 0.001) af.Add($"volume={F(o.Volume)}");
                        int ms = (int)Math.Round(Math.Max(0, a) * 1000);
                        if (ms > 0) af.Add($"adelay={ms}|{ms}");
                        g.Append($"[{inp}:a]{string.Join(",", af)}[ova{n}];\n");
                        ovAudio.Add($"ova{n}");
                    }
                    if (!video) { n++; continue; }
                    f.Add($"fps={fps},trim=duration={F(vlen)},setpts=PTS-STARTPTS+{F(Math.Max(0, a))}/TB");
                }
                else
                    // 張相由時間軸 0 秒（預覽 = 開始位置）開始一直循環，到 b 為止；用 enable 控制幾時出現
                    inputs.Append($"-loop 1 -framerate {fps} -t {F(b + 0.2)} -i \"{o.Path}\" ");
                int ow = Math.Max(2, (int)Math.Round(W * Math.Clamp(o.Scale, 0.02, 2) / 2) * 2);
                f.AddRange(new[] { $"scale={ow}:-2", "format=rgba" });
                if (o.Opacity < 0.999) f.Add($"colorchannelmixer=aa={F(Math.Clamp(o.Opacity, 0, 1))}");
                if (o.Fade)
                {
                    double fd = Math.Min(0.4, o.Dur / 3);
                    if (a >= 0) f.Add($"fade=t=in:st={F(a)}:d={F(fd)}:alpha=1");
                    f.Add($"fade=t=out:st={F(Math.Max(0, b - fd))}:d={F(fd)}:alpha=1");
                }
                g.Append($"[{inp}:v]{string.Join(",", f)}[ov{n}];\n");
                g.Append($"[{cv}][ov{n}]overlay=x='{F(W * o.X)}-overlay_w/2':y='{F(H * o.Y)}-overlay_h/2':enable='between(t,{F(Math.Max(0, a))},{F(b)})':eof_action=pass[ovo{n}];\n");
                cv = $"ovo{n}";
                n++;
            }
        }

        // ---- 文字 ----
        if (video)
        {
            int n = 0;
            foreach (var t in p.TextOrder())
            {
                double a = t.Start - from, b = t.Start + t.Dur - from;
                if (b <= 0 || t.Text.Trim().Length == 0) continue;
                Directory.CreateDirectory(tmpDir);
                string tf = Path.Combine(tmpDir, $"text{n}.txt");
                File.WriteAllText(tf, t.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
                int size = Math.Max(8, (int)Math.Round(t.Size * H / 1080.0));
                var (tx, ty) = TextPos(t);
                string x = $"{F(W * tx)}-text_w/2";
                string y = $"{F(H * ty)}-text_h/2";
                string col = Regex.IsMatch(t.Color, "^#[0-9A-Fa-f]{6}$") ? "0x" + t.Color[1..] : "white";
                var opts = new List<string>
                {
                    Font.Length > 0 ? $"fontfile='{Esc(Font)}'" : "",
                    $"textfile='{Esc(tf)}'", "expansion=none", $"fontsize={size}", $"fontcolor={col}",
                    $"x='{x}'", $"y='{y}'", $"line_spacing={Math.Max(2, size / 6)}",
                    $"enable='between(t,{F(a)},{F(b)})'"
                };
                if (t.Box) opts.Add($"box=1:boxcolor=black@0.45:boxborderw={Math.Max(4, size / 3)}");
                else opts.Add($"borderw={Math.Max(1, size / 20)}:bordercolor=black@0.8");
                if (t.Fade)
                {
                    double fd = Math.Min(0.4, t.Dur / 3);
                    opts.Add($"alpha='if(lt(t,{F(a + fd)}),max(0,(t-{F(a)})/{F(fd)}),if(gt(t,{F(b - fd)}),max(0,({F(b)}-t)/{F(fd)}),1))'");
                }
                g.Append($"[{cv}]drawtext={string.Join(":", opts.Where(o => o.Length > 0))}[tx{n}];\n");
                cv = $"tx{n}";
                n++;
            }
            g.Append($"[{cv}]format=yuv420p[vout];\n");
        }

        // ---- 音樂 ----
        if (audio)
        {
            var mix = new List<string> { ca };
            mix.AddRange(ovAudio);
            int n = 0;
            foreach (var m in p.Music)
            {
                double at = m.Start - from, srcIn = m.In, len = m.Len;
                if (at + len <= 0) continue;
                bool cutStart = at < 0;
                if (cutStart) { srcIn -= at * m.Sp; len += at; at = 0; }
                if (len < 0.05) continue;
                int inp = idx++;
                inputs.Append($"-ss {F(srcIn)} -t {F(len * m.Sp + 0.2)} -i \"{m.Path}\" ");
                var f = new List<string> { "asetpts=PTS-STARTPTS" };
                double msp = m.Sp;
                while (msp > 2.0001) { f.Add("atempo=2"); msp /= 2; }
                while (msp < 0.4999) { f.Add("atempo=0.5"); msp /= 0.5; }
                if (Math.Abs(msp - 1) > 0.001) f.Add($"atempo={F(msp)}");
                f.AddRange(new[] { AFmt, $"atrim=duration={F(len)}", $"volume={F(m.Volume)}" });
                if (m.FadeIn > 0 && !cutStart) f.Add($"afade=t=in:d={F(Math.Min(m.FadeIn, len / 2))}");
                if (m.FadeOut > 0) f.Add($"afade=t=out:st={F(Math.Max(0, len - Math.Min(m.FadeOut, len / 2)))}:d={F(Math.Min(m.FadeOut, len / 2))}");
                int ms = (int)Math.Round(at * 1000);
                if (ms > 0) f.Add($"adelay={ms}|{ms}");
                g.Append($"[{inp}:a]{string.Join(",", f)}[m{n}];\n");
                mix.Add($"m{n}");
                n++;
            }
            if (mix.Count > 1)
                g.Append($"{string.Concat(mix.Select(x => $"[{x}]"))}amix=inputs={mix.Count}:duration=first:normalize=0:dropout_transition=0,atrim=duration={F(length)}[aout];\n");
            else g.Append($"[{ca}]atrim=duration={F(length)}[aout];\n");
        }
        return (inputs.ToString(), g.ToString().TrimEnd(';', '\n'), length);
    }

    private static int _ver = -1;

    /// <summary>ffmpeg 7 之後用 -/filter_complex 讀檔；之前用 -filter_complex_script。</summary>
    public static string GraphArg(string file)
    {
        if (_ver < 0)
        {
            _ver = 7;
            try
            {
                var psi = new ProcessStartInfo(Ffmpeg.Exe, "-hide_banner -version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var pr = Process.Start(psi)!;
                var o = pr.StandardOutput.ReadToEnd();
                pr.WaitForExit(5000);
                var m = Regex.Match(o, @"ffmpeg version n?(\d+)\.");
                if (m.Success) _ver = int.Parse(m.Groups[1].Value);
            }
            catch { }
        }
        return _ver >= 7 ? $"-/filter_complex \"{file}\"" : $"-filter_complex_script \"{file}\"";
    }

    /// <summary>寫 graph 檔，回傳完整 ffmpeg 參數（未加輸出）。</summary>
    public static string Args(EdProject p, double from, int W, int H, bool video, bool audio, string tag, out double length)
    {
        string tmp = Path.Combine(EdStore.Tmp, tag);
        Directory.CreateDirectory(tmp);
        var (inputs, graph, len) = Build(p, from, W, H, video, audio, tmp);
        length = len;
        string gf = Path.Combine(tmp, "graph.txt");
        File.WriteAllText(gf, graph, new UTF8Encoding(false));
        return $"-hide_banner -loglevel error -nostdin {inputs}{GraphArg(gf)} ";
    }
}

// ---------------------------------------------------------------------
//  waveOut：播預覽聲（48 kHz 立體聲 16-bit）
// ---------------------------------------------------------------------
internal sealed class WaveOut : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat { public short Tag, Channels; public int Rate, AvgBytes; public short Align, Bits, Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr { public nint Data; public uint Length, Recorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime { [FieldOffset(0)] public uint Type; [FieldOffset(4)] public uint Cb; }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out nint h, int dev, ref WaveFormat f, nint cb, nint inst, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(nint h);
    [DllImport("winmm.dll")] private static extern int waveOutClose(nint h);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(nint h, ref MmTime t, int size);

    public const int Rate = 48000, BytesPerSec = Rate * 4;
    private const uint Done = 1, Prepared = 2;
    private readonly nint _h;
    private readonly nint[] _hdrs, _bufs;
    private readonly bool[] _used;
    private readonly int _bufSize, _hdrSize = Marshal.SizeOf<WaveHdr>();
    private readonly int _flagsOff = (int)Marshal.OffsetOf<WaveHdr>("Flags");
    private long _written;
    private bool _closed;

    public WaveOut(int buffers = 8, int bufBytes = BytesPerSec / 20)
    {
        var f = new WaveFormat { Tag = 1, Channels = 2, Rate = Rate, Bits = 16, Align = 4, AvgBytes = BytesPerSec, Size = 0 };
        int r = waveOutOpen(out _h, -1, ref f, 0, 0, 0);
        if (r != 0) throw new InvalidOperationException(Lang.F("開唔到聲音輸出（錯誤 {0}）", r));
        _bufSize = bufBytes;
        _hdrs = new nint[buffers];
        _bufs = new nint[buffers];
        _used = new bool[buffers];
        for (int i = 0; i < buffers; i++)
        {
            _hdrs[i] = Marshal.AllocHGlobal(_hdrSize);
            _bufs[i] = Marshal.AllocHGlobal(bufBytes);
            Marshal.StructureToPtr(new WaveHdr(), _hdrs[i], false);
        }
    }

    public int BufferBytes => _bufSize;

    private bool IsFree(int i) => !_used[i] || (Marshal.ReadInt32(_hdrs[i], _flagsOff) & Done) != 0;

    /// <summary>預覽音量（0–1，淨係影響預覽播出嚟嘅聲，唔改影片）。</summary>
    public static volatile float Volume = LoadVolume();
    public static volatile bool Muted;

    private static string VolFile => Path.Combine(EdStore.Dir, "preview_volume.txt");

    private static float LoadVolume()
    {
        try { if (File.Exists(VolFile) && float.TryParse(File.ReadAllText(VolFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Math.Clamp(v, 0, 1); }
        catch { }
        return 1;
    }

    public static void SaveVolume()
    {
        try { Directory.CreateDirectory(EdStore.Dir); File.WriteAllText(VolFile, Volume.ToString("0.###", CultureInfo.InvariantCulture)); } catch { }
    }

    /// <summary>16-bit PCM 乘音量（就地改）。</summary>
    private static void ApplyVolume(byte[] data, int count)
    {
        float v = Muted ? 0 : Volume;
        if (v >= 0.999f) return;
        if (v <= 0.001f) { Array.Clear(data, 0, count); return; }
        for (int i = 0; i + 1 < count; i += 2)
        {
            int x = (int)((short)(data[i] | (data[i + 1] << 8)) * v);
            data[i] = (byte)x;
            data[i + 1] = (byte)(x >> 8);
        }
    }

    /// <summary>寫一段聲（最多 BufferBytes）；冇空位就等。</summary>
    public void Write(byte[] data, int count, CancellationToken ct)
    {
        ApplyVolume(data, count);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < _hdrs.Length; i++)
            {
                if (!IsFree(i)) continue;
                if (_used[i]) waveOutUnprepareHeader(_h, _hdrs[i], _hdrSize);
                Marshal.Copy(data, 0, _bufs[i], count);
                var hdr = new WaveHdr { Data = _bufs[i], Length = (uint)count };
                Marshal.StructureToPtr(hdr, _hdrs[i], false);
                waveOutPrepareHeader(_h, _hdrs[i], _hdrSize);
                waveOutWrite(_h, _hdrs[i], _hdrSize);
                _used[i] = true;
                _written += count;
                return;
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>已經播咗幾多秒。</summary>
    public double Position
    {
        get
        {
            if (_closed) return 0;
            var t = new MmTime { Type = 4 }; // TIME_BYTES
            waveOutGetPosition(_h, ref t, 12);
            return t.Cb / (double)BytesPerSec;
        }
    }

    public double Written => _written / (double)BytesPerSec;

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { waveOutReset(_h); } catch { }
        for (int i = 0; i < _hdrs.Length; i++)
        {
            try { if (_used[i]) waveOutUnprepareHeader(_h, _hdrs[i], _hdrSize); } catch { }
        }
        try { waveOutClose(_h); } catch { }
        foreach (var p in _hdrs) Marshal.FreeHGlobal(p);
        foreach (var p in _bufs) Marshal.FreeHGlobal(p);
    }
}

/// <summary>即時預覽：兩個 ffmpeg（畫面 / 聲）由某個時間開始解碼；聲音做時鐘，畫面跟住佢出。</summary>
internal sealed class EdPlayer : IDisposable
{
    private Process? _vp, _ap;
    private WaveOut? _out;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<(double T, Bitmap Img)> _frames = new();
    private readonly SemaphoreSlim _room = new(4, 4);
    private double _from, _len;
    private readonly Stopwatch _sw = new();
    public bool Playing { get; private set; }
    public event Action<string>? Failed;

    /// <summary>預覽畫面喺螢幕上實際有幾大（最長邊 px），由 EditorPage 按預覽框大細設定。</summary>
    public static int ViewLong = 960;

    /// <summary>
    /// 預覽解像度：停低（單格）跟預覽框實際大細出，唔會糊；播放緊就最多 1280 免得太食 CPU。
    /// 唔會大過專案本身；闊要 4 嘅倍數（24bpp 每行唔使補位）。
    /// </summary>
    public static Size PreviewSize(EdProject p, bool playing = false)
    {
        int want = Math.Clamp(ViewLong, 480, playing ? 1280 : 2560);
        double k = Math.Min(1.0, want / (double)Math.Max(p.W, p.H));
        int w = Math.Max(16, (int)Math.Round(p.W * k / 4) * 4), h = Math.Max(16, (int)Math.Round(p.H * k / 2) * 2);
        return new Size(w, h);
    }

    public void Start(EdProject p, double from)
    {
        Stop();
        var size = PreviewSize(p, true);
        _from = from;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string vArgs = EdGraph.Args(p, from, size.Width, size.Height, true, false, "pv", out _len);
        string aArgs = EdGraph.Args(p, from, size.Width, size.Height, false, true, "pa", out _);
        _vp = Run(vArgs + "-map [vout] -f rawvideo -pix_fmt bgr24 -");
        _ap = Run(aArgs + $"-map [aout] -f s16le -ar {WaveOut.Rate} -ac 2 -");
        try { _out = new WaveOut(); } catch { _out = null; }
        Playing = true;
        _sw.Restart();
        int fps = Math.Clamp(p.Fps, 10, 60);
        var vp = _vp; var ap = _ap; var outp = _out;
        // 畫面
        Task.Run(() =>
        {
            int frameBytes = size.Width * size.Height * 3;
            var buf = new byte[frameBytes];
            var s = vp.StandardOutput.BaseStream;
            long n = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int got = 0;
                    while (got < frameBytes)
                    {
                        int r = s.Read(buf, got, frameBytes - got);
                        if (r <= 0) return;
                        got += r;
                    }
                    _room.Wait(ct);
                    var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, size.Width, size.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    for (int y = 0; y < size.Height; y++) Marshal.Copy(buf, y * size.Width * 3, bd.Scan0 + y * bd.Stride, size.Width * 3);
                    bmp.UnlockBits(bd);
                    _frames.Enqueue((_from + n / (double)fps, bmp));
                    n++;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!ct.IsCancellationRequested) Failed?.Invoke(ex.Message); }
        });
        // 聲
        Task.Run(() =>
        {
            if (outp == null) { try { ap.StandardOutput.BaseStream.CopyTo(Stream.Null); } catch { } return; }
            var buf = new byte[outp.BufferBytes];
            var s = ap.StandardOutput.BaseStream;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int r = s.Read(buf, got, buf.Length - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got <= 0) return;
                    got -= got % 4;
                    if (got > 0) outp.Write(buf, got, ct);
                    if (got < buf.Length) return;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private Process Run(string args)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Exe, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        var p = Process.Start(psi)!;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            string msg;
            lock (err) msg = err.ToString().Trim();
            if (p.ExitCode != 0 && msg.Length > 0 && Playing)
            {
                AppPaths.LogError("剪片預覽", new Exception(msg));
                Failed?.Invoke(msg.Split('\n').Last().Trim());
            }
        };
        return p;
    }

    /// <summary>而家播到時間軸邊度（聲音做時鐘；冇聲就用計時器）。</summary>
    public double Clock
    {
        get
        {
            if (!Playing) return _from;
            double t = _out != null && _out.Written > 0 ? _out.Position : _sw.Elapsed.TotalSeconds;
            return _from + t;
        }
    }

    public bool Finished => Playing && Clock >= _from + _len - 0.03;

    /// <summary>攞最新一格夠鐘出嘅畫面（舊嘅會 Dispose）。</summary>
    public Bitmap? TakeFrame()
    {
        Bitmap? best = null;
        double now = Clock;
        while (_frames.TryPeek(out var f) && f.T <= now + 0.01)
        {
            if (!_frames.TryDequeue(out f)) break;
            best?.Dispose();
            best = f.Img;
            try { _room.Release(); } catch { }
        }
        return best;
    }

    public void Stop()
    {
        Playing = false;
        try { _cts?.Cancel(); } catch { }
        foreach (var p in new[] { _vp, _ap })
        {
            try { if (p is { HasExited: false }) p.Kill(); } catch { }
            try { p?.Dispose(); } catch { }
        }
        _vp = _ap = null;
        try { _out?.Dispose(); } catch { }
        _out = null;
        while (_frames.TryDequeue(out var f)) { f.Img.Dispose(); try { _room.Release(); } catch { } }
    }

    /// <summary>暫停時：淨係出一格畫面（拖時間軸用）。</summary>
    public static Bitmap? Still(EdProject p, double at, CancellationToken ct)
    {
        var size = PreviewSize(p);
        string args = EdGraph.Args(p, at, size.Width, size.Height, true, false, "still", out _) + "-map [vout] -frames:v 1 -f rawvideo -pix_fmt bgr24 -";
        var psi = new ProcessStartInfo(Ffmpeg.Exe, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var pr = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { pr.Kill(); } catch { } });
        var errTask = pr.StandardError.ReadToEndAsync();
        int frameBytes = size.Width * size.Height * 3;
        var buf = new byte[frameBytes];
        int got = 0;
        var s = pr.StandardOutput.BaseStream;
        while (got < frameBytes)
        {
            int r = s.Read(buf, got, frameBytes - got);
            if (r <= 0) break;
            got += r;
        }
        pr.WaitForExit(5000);
        if (got < frameBytes)
        {
            var err = errTask.Wait(1000) ? errTask.Result.Trim() : "";
            if (err.Length > 0 && !ct.IsCancellationRequested) throw new InvalidOperationException(err.Split('\n').Last().Trim());
            return null;
        }
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, size.Width, size.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        for (int y = 0; y < size.Height; y++) Marshal.Copy(buf, y * size.Width * 3, bd.Scan0 + y * bd.Stride, size.Width * 3);
        bmp.UnlockBits(bd);
        return bmp;
    }

    public void Dispose() { Stop(); _room.Dispose(); }
}
