using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Tools;

// =====================================================================
//  檔案修復（核心，唔用 WinForms）：影片 / 聲音（ffmpeg、untrunc）、相片（JPEG / PNG 自己修）、
//  ZIP / Office 文件（自己由殘骸重砌）、PDF（qpdf）。修好嘅檔另存一份，原檔唔郁。
// =====================================================================

internal enum RepairType { Video, Audio, Image, Archive, Pdf, Unknown }

internal sealed class RepairOptions
{
    public string? Ffmpeg, Qpdf, Untrunc;   // 第三方工具路徑（冇就 null）
    public bool ToH264;                     // 影片一律轉做 H.264 / AAC（最兼容）
    public string? Reference;               // 參考影片（MP4 冇 moov 時用）
    /// <summary>相片：用系統（GDI+）讀入再儲存一次（讀到 = true）。UI 嗰邊提供；冇就跳過。</summary>
    public Func<string, string, bool>? ImageResave;
}

internal sealed class RepairOutcome
{
    public string? Output;                  // 修好嘅檔案
    public string? NeedTool;                // "ffmpeg" / "qpdf" / "untrunc"：要先下載
    public bool NeedReference;              // MP4 冇 moov：要揀一條參考影片
    public string? Error;
    public readonly List<string> Notes = new();
    public bool Ok => Output != null;
}

internal static class FileRepair
{
    public static readonly string[] VideoExt = { ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm", ".avi", ".flv", ".wmv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob" };
    public static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma" };
    public static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif" };
    public static readonly string[] ZipExt = { ".zip", ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".vsdx", ".3mf" };

    // ------------------------------------------------------------------
    //  認檔案：睇檔頭，唔信副檔名（副檔名錯都係常見「開唔到」原因）
    // ------------------------------------------------------------------
    public static RepairType Detect(string path, out string realExt)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        realExt = ext;
        byte[] h = new byte[64];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(h, 0, h.Length);
        bool At(int off, string ascii) => off + ascii.Length <= n && Encoding.ASCII.GetString(h, off, ascii.Length) == ascii;
        bool Bytes(params byte[] b) => n >= b.Length && b.Select((x, i) => h[i] == x).All(x => x);

        if (Bytes(0xFF, 0xD8, 0xFF)) { realExt = ext is ".jpg" or ".jpeg" ? ext : ".jpg"; return RepairType.Image; }
        if (Bytes(0x89, 0x50, 0x4E, 0x47)) { realExt = ".png"; return RepairType.Image; }
        if (At(0, "GIF8")) { realExt = ".gif"; return RepairType.Image; }
        if (At(0, "BM") && ext == ".bmp") return RepairType.Image;
        if (At(0, "RIFF") && At(8, "WEBP")) { realExt = ".webp"; return RepairType.Image; }
        if (Bytes(0x49, 0x49, 0x2A, 0x00) || Bytes(0x4D, 0x4D, 0x00, 0x2A)) { realExt = ext is ".tif" or ".tiff" ? ext : ".tif"; return RepairType.Image; }
        if (At(4, "ftyp"))
        {
            string brand = n >= 12 ? Encoding.ASCII.GetString(h, 8, 4) : "";
            if (brand is "heic" or "heix" or "mif1" or "msf1" or "hevc") { realExt = ".heic"; return RepairType.Image; }
            if (brand is "avif" or "avis") { realExt = ".avif"; return RepairType.Image; }
            if (brand.StartsWith("M4A") || brand.StartsWith("M4B")) { realExt = ".m4a"; return RepairType.Audio; }
            if (brand.StartsWith("qt")) { realExt = ".mov"; return RepairType.Video; }
            if (brand.StartsWith("3g")) { realExt = ext == ".3gp" ? ext : ".mp4"; return RepairType.Video; }
            realExt = ext is ".mov" or ".m4v" or ".mp4" ? ext : AudioExt.Contains(ext) ? ".m4a" : ".mp4";
            return AudioExt.Contains(ext) ? RepairType.Audio : RepairType.Video;
        }
        if (At(4, "mdat") || At(4, "moov") || At(4, "wide") || At(4, "free") || At(4, "skip"))
        { realExt = AudioExt.Contains(ext) ? ".m4a" : ext is ".mov" or ".m4v" ? ext : ".mp4"; return AudioExt.Contains(ext) ? RepairType.Audio : RepairType.Video; }
        if (Bytes(0x1A, 0x45, 0xDF, 0xA3)) { realExt = ext is ".webm" or ".mka" ? ext : ".mkv"; return ext == ".mka" ? RepairType.Audio : RepairType.Video; }
        if (At(0, "RIFF") && At(8, "AVI ")) { realExt = ".avi"; return RepairType.Video; }
        if (At(0, "RIFF") && At(8, "WAVE")) { realExt = ".wav"; return RepairType.Audio; }
        if (At(0, "FLV")) { realExt = ".flv"; return RepairType.Video; }
        if (Bytes(0x30, 0x26, 0xB2, 0x75)) { realExt = ext is ".wma" ? ext : ".wmv"; return ext == ".wma" ? RepairType.Audio : RepairType.Video; } // ASF
        if (At(0, "ID3") || (n >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && ext is ".mp3" or ".aac")) { realExt = ext == ".aac" ? ".aac" : ".mp3"; return RepairType.Audio; }
        if (At(0, "fLaC")) { realExt = ".flac"; return RepairType.Audio; }
        if (At(0, "OggS")) { realExt = ext is ".opus" ? ext : ".ogg"; return RepairType.Audio; }
        if (At(0, "PK\u0003\u0004") || At(0, "PK\u0005\u0006") || At(0, "PK\u0007\u0008")) { realExt = ZipExt.Contains(ext) ? ext : ".zip"; return RepairType.Archive; }
        if (Encoding.ASCII.GetString(h, 0, n).Contains("%PDF")) { realExt = ".pdf"; return RepairType.Pdf; }
        if (n >= 189 && h[0] == 0x47 && h[188] == 0x47) { realExt = ext is ".mts" or ".m2ts" ? ext : ".ts"; return RepairType.Video; }

        // 檔頭壞咗：靠副檔名
        if (VideoExt.Contains(ext)) return RepairType.Video;
        if (AudioExt.Contains(ext)) return RepairType.Audio;
        if (ImageExt.Contains(ext)) return RepairType.Image;
        if (ZipExt.Contains(ext)) return RepairType.Archive;
        if (ext == ".pdf") return RepairType.Pdf;
        return RepairType.Unknown;
    }

    /// <summary>成個檔案係咪全部 0（例如當機時未寫入，資料已經冇咗）。抽樣檢查。</summary>
    public static bool AllZero(string path)
    {
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        if (len == 0) return true;
        var buf = new byte[1 << 16];
        foreach (long pos in new[] { 0L, len / 4, len / 2, len * 3 / 4, Math.Max(0, len - buf.Length) })
        {
            fs.Position = pos;
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] != 0) return false;
        }
        return true;
    }

    /// <summary>修好嘅檔名：「名 (已修復).副檔名」，撞名就加數字。</summary>
    public static string OutPath(string src, string outDir, string ext)
    {
        string dir = outDir.Length > 0 ? outDir : Path.GetDirectoryName(src)!;
        string name = Path.GetFileNameWithoutExtension(src) + " (" + Lang.T("已修復") + ")";
        string p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name} {i}{ext}");
        return p;
    }

    public static async Task<RepairOutcome> RepairAsync(string path, string outDir, RepairOptions o, Action<string, double> progress, CancellationToken ct)
    {
        var r = new RepairOutcome();
        try
        {
            path = Path.GetFullPath(path);
            if (outDir.Length > 0) outDir = Path.GetFullPath(outDir);
            if (!File.Exists(path)) { r.Error = Lang.T("搵唔到檔案"); return r; }
            if (AllZero(path)) { r.Error = Lang.T("成個檔案入面全部係 0（資料已經冇咗，通常係當機或者儲存中途斷電），救唔返。"); return r; }
            var type = Detect(path, out string realExt);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (realExt != ext && type != RepairType.Unknown && !(realExt == ".jpg" && ext == ".jpeg"))
                r.Notes.Add(Lang.F("副檔名唔啱：其實係 {0} 檔（會用返啱嘅副檔名儲存）", realExt.TrimStart('.').ToUpperInvariant()));
            switch (type)
            {
                case RepairType.Video:
                case RepairType.Audio:
                    await MediaAsync(path, outDir, type, realExt, o, r, progress, ct);
                    break;
                case RepairType.Image:
                    await ImageAsync(path, outDir, realExt, o, r, progress, ct);
                    break;
                case RepairType.Archive:
                    await Task.Run(() => Zip(path, outDir, realExt, r, progress, ct), ct);
                    break;
                case RepairType.Pdf:
                    await PdfAsync(path, outDir, o, r, progress, ct);
                    break;
                default:
                    r.Error = Lang.T("認唔到呢種檔案。支援：影片、聲音、相片、ZIP / Word / Excel / PowerPoint、PDF。");
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }

    // ------------------------------------------------------------------
    //  行外部程式
    // ------------------------------------------------------------------
    private sealed record RunResult(int Code, string Out, string Err);

    private static async Task<RunResult> RunAsync(string exe, string args, CancellationToken ct, Action<string>? line = null, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = cwd ?? Path.GetDirectoryName(exe) ?? ""
        };
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (so) so.AppendLine(e.Data); line?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (se) { if (se.Length < 400_000) se.AppendLine(e.Data); } line?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        p.WaitForExit();
        lock (so) lock (se) return new RunResult(p.ExitCode, so.ToString(), se.ToString());
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";

    // ------------------------------------------------------------------
    //  影片 / 聲音
    // ------------------------------------------------------------------
    private sealed class MediaInfo2
    {
        public bool NoMoov, Unreadable;
        public double Duration;
        public string VCodec = "", ACodec = "";
        public bool HasVideo => VCodec.Length > 0;
        public bool HasAudio => ACodec.Length > 0;
    }

    private static readonly Regex DurRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static double Secs(Match m) => int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                                           double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    private static async Task<MediaInfo2> ProbeAsync(string ff, string path, CancellationToken ct)
    {
        var res = await RunAsync(ff, $"-hide_banner -i {Q(path)}", ct);
        var e = res.Err;
        var mi = new MediaInfo2
        {
            NoMoov = e.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase),
            Unreadable = e.Contains("Invalid data found when processing input") || e.Contains("could not find codec parameters", StringComparison.OrdinalIgnoreCase) && !e.Contains("Stream #")
        };
        var d = DurRx.Match(e);
        if (d.Success) mi.Duration = Secs(d);
        var v = Regex.Match(e, @"Stream #\d+:\d+.*?: Video: (\w+)(?!.*attached pic)");
        if (v.Success) mi.VCodec = v.Groups[1].Value;
        var a = Regex.Match(e, @"Stream #\d+:\d+.*?: Audio: (\w+)");
        if (a.Success) mi.ACodec = a.Groups[1].Value;
        if (!mi.HasVideo && !mi.HasAudio) mi.Unreadable = true;
        return mi;
    }

    /// <summary>解碼一次檢查有幾多錯誤（順便報進度）。回傳錯誤行數。</summary>
    private static async Task<int> DecodeCheckAsync(string ff, string path, double dur, Action<string, double> progress, string label, CancellationToken ct)
    {
        int errors = 0;
        var res = await RunAsync(ff, $"-hide_banner -v error -stats -err_detect ignore_err -i {Q(path)} -map 0:v:0? -map 0:a:0? -f null -", ct, l =>
        {
            var m = TimeRx.Match(l);
            if (m.Success) { progress(label, dur > 0 ? Math.Clamp(Secs(m) / dur, 0, 1) : -1); return; }
            if (l.Length > 0 && !l.StartsWith("frame=") && !l.StartsWith("size=")) Interlocked.Increment(ref errors);
        });
        return errors;
    }

    private static async Task<bool> FfmpegToAsync(string ff, string args, string outFile, double dur, Action<string, double> progress, string label, CancellationToken ct)
    {
        try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        var res = await RunAsync(ff, "-hide_banner -y -stats " + args + " " + Q(outFile), ct, l =>
        {
            var m = TimeRx.Match(l);
            if (m.Success) progress(label, dur > 0 ? Math.Clamp(Secs(m) / dur, 0, 1) : -1);
        });
        bool ok = res.Code == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0;
        if (!ok) { try { if (File.Exists(outFile)) File.Delete(outFile); } catch { } }
        return ok;
    }

    private static readonly string[] HardCodecs = { "hevc", "av1", "vp9", "vp8", "mpeg2video", "mpeg4", "msmpeg4v3", "wmv3", "vc1", "prores" };

    private static async Task MediaAsync(string path, string outDir, RepairType type, string ext, RepairOptions o, RepairOutcome r,
        Action<string, double> progress, CancellationToken ct)
    {
        if (o.Ffmpeg == null || !File.Exists(o.Ffmpeg)) { r.NeedTool = "ffmpeg"; return; }
        string ff = o.Ffmpeg;
        progress(Lang.T("檢查緊…"), -1);
        var mi = await ProbeAsync(ff, path, ct);
        bool mp4Like = ext is ".mp4" or ".mov" or ".m4v" or ".3gp" or ".m4a";

        // ---- MP4 / MOV 冇 moov（錄影中途斷電 / 當機）：要參考影片 + untrunc ----
        if (mi.NoMoov || (mp4Like && mi.Unreadable))
        {
            r.Notes.Add(Lang.T("影片嘅索引（moov）唔見咗：通常係錄影中途斷電、當機或者手機冇電。"));
            if (o.Reference == null) { r.NeedReference = true; return; }
            if (o.Untrunc == null || !File.Exists(o.Untrunc)) { r.NeedTool = "untrunc"; return; }
            await UntruncAsync(path, outDir, ext, o, r, progress, ct);
            return;
        }
        if (mi.Unreadable)
        {
            // MKV / AVI 等：試吓忽略錯誤硬讀
            r.Notes.Add(Lang.T("檔頭損壞，嘗試忽略錯誤讀取。"));
        }

        string label = Lang.T("檢查損壞位置…");
        int errors = mi.Unreadable ? 1 : await DecodeCheckAsync(ff, path, mi.Duration, progress, label, ct);
        if (errors > 0) r.Notes.Add(Lang.F("發現約 {0} 處讀取錯誤（損壞嘅片段）。", errors));
        else if (!mi.Unreadable) r.Notes.Add(Lang.T("讀取時冇發現明顯損壞。"));

        string inArgs = $"-err_detect ignore_err -fflags +genpts+discardcorrupt+igndts -i {Q(path)}";
        bool video = type == RepairType.Video && mi.HasVideo;

        // ---- 1. 唔使重新壓縮：重新封裝（重建索引、時間戳）----
        if (errors == 0 && !(video && o.ToH264))
        {
            string outExt = video ? (ext is ".mkv" or ".webm" or ".mov" or ".mp4" or ".m4v" ? ext : ".mp4") : ext;
            string outFile = OutPath(path, outDir, outExt);
            string maps = video ? (outExt is ".mkv" or ".webm" ? "-map 0" : "-map 0:v -map 0:a? -sn -dn") : "-map 0:a";
            string faststart = outExt is ".mp4" or ".mov" or ".m4v" or ".m4a" ? "-movflags +faststart " : "";
            if (await FfmpegToAsync(ff, $"{inArgs} {maps} -c copy -avoid_negative_ts make_zero {faststart}-ignore_unknown", outFile, mi.Duration, progress, Lang.T("重建索引…"), ct)
                || (video && outExt != ".mkv" && await FfmpegToAsync(ff, $"{inArgs} -map 0 -c copy -avoid_negative_ts make_zero", outFile = Path.ChangeExtension(outFile, ".mkv"), mi.Duration, progress, Lang.T("重建索引…"), ct)))
            {
                r.Output = outFile;
                r.Notes.Add(Lang.T("已重建檔案索引同時間戳（冇重新壓縮，畫質不變）。"));
                if (video && HardCodecs.Contains(mi.VCodec))
                    r.Notes.Add(Lang.F("影片係 {0} 格式，部分播放器（例如未裝 HEVC 擴充功能嘅 Windows 播放器）播唔到：可以剔「轉做 H.264」再修一次。", mi.VCodec.ToUpperInvariant()));
                return;
            }
        }

        // ---- 2. 重新壓縮：損壞位置用前後畫面補返，出一個乾淨嘅檔 ----
        if (video)
        {
            string outFile = OutPath(path, outDir, ".mp4");
            string a = mi.HasAudio ? "-c:a aac -b:a 192k" : "-an";
            if (await FfmpegToAsync(ff, $"{inArgs} -map 0:v:0 -map 0:a:0? -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p {a} -movflags +faststart -max_muxing_queue_size 4096",
                    outFile, mi.Duration, progress, Lang.T("重新壓縮…"), ct))
            {
                r.Output = outFile;
                r.Notes.Add(errors > 0
                    ? Lang.T("已重新壓縮成 H.264 / AAC：損壞嘅位置可能會有花屏或者跳格。")
                    : Lang.T("已轉做 H.264 / AAC（最兼容）。"));
                return;
            }
        }
        else if (mi.HasAudio)
        {
            string outExt = ext is ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".opus" ? ext : ".mp3";
            string outFile = OutPath(path, outDir, outExt);
            string q = outExt == ".mp3" ? "-q:a 2" : outExt == ".m4a" ? "-c:a aac -b:a 256k" : "";
            if (await FfmpegToAsync(ff, $"{inArgs} -map 0:a:0 -vn {q}", outFile, mi.Duration, progress, Lang.T("重新壓縮…"), ct))
            {
                r.Output = outFile;
                r.Notes.Add(Lang.T("已重新壓縮聲音：損壞嘅位置可能會有雜音或者跳咗。"));
                return;
            }
        }
        r.Error = Lang.T("ffmpeg 讀唔到入面嘅影音資料，呢個檔案損壞得太嚴重。");
    }

    private static async Task UntruncAsync(string path, string outDir, string ext, RepairOptions o, RepairOutcome r, Action<string, double> progress, CancellationToken ct)
    {
        // 複製一份去暫存資料夾先做（untrunc 會喺原檔隔籬寫新檔）
        string work = Path.Combine(Path.GetTempPath(), "MultiTool-repair-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string broken = Path.Combine(work, "broken" + ext);
            progress(Lang.T("準備緊…"), -1);
            await Task.Run(() => File.Copy(path, broken), ct);
            foreach (string extra in new[] { "", "-s " })
            {
                progress(Lang.T("用參考影片重建索引（可能要幾分鐘）…"), -1);
                var res = await RunAsync(o.Untrunc!, $"-n {extra}{Q(o.Reference!)} {Q(broken)}", ct, null, work);
                var fixedFile = Directory.GetFiles(work).FirstOrDefault(f => Path.GetFileName(f).Contains("_fixed", StringComparison.OrdinalIgnoreCase));
                if (fixedFile != null && new FileInfo(fixedFile).Length > 1024)
                {
                    string outFile = OutPath(path, outDir, ext);
                    File.Move(fixedFile, outFile);
                    r.Output = outFile;
                    r.Notes.Add(Lang.T("已用參考影片重建索引。最尾幾秒（未寫完嘅部分）可能會冇咗。"));
                    return;
                }
                foreach (var f in Directory.GetFiles(work)) if (!f.Equals(broken, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
                if (extra.Length == 0 && res.Err.Length + res.Out.Length > 0) AppPaths.LogError("untrunc", new Exception((res.Out + res.Err).Trim()));
            }
            r.Error = Lang.T("重建唔到：參考影片要係同一部機、同一個設定（解像度、FPS、格式）錄嘅正常影片，揀過另一條再試。");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    // ------------------------------------------------------------------
    //  相片
    // ------------------------------------------------------------------
    private static async Task ImageAsync(string path, string outDir, string ext, RepairOptions o, RepairOutcome r, Action<string, double> progress, CancellationToken ct)
    {
        progress(Lang.T("檢查緊…"), -1);
        byte[] data = await File.ReadAllBytesAsync(path, ct);
        byte[] fixedBytes = data;
        string tmp = Path.Combine(Path.GetTempPath(), "MultiTool-repair-" + Guid.NewGuid().ToString("N")[..8] + ext);
        try
        {
            if (ext is ".jpg" or ".jpeg") fixedBytes = FixJpeg(data, r.Notes);
            else if (ext == ".png")
            {
                var png = FixPng(data, r.Notes, out bool decoded);
                if (png != null)
                {
                    fixedBytes = png;
                    if (decoded)
                    {
                        // 自己解碼 + 重新編碼過：已經係乾淨嘅 PNG
                        string outPng = OutPath(path, outDir, ".png");
                        await File.WriteAllBytesAsync(outPng, png, ct);
                        r.Output = outPng;
                        return;
                    }
                }
            }
            await File.WriteAllBytesAsync(tmp, fixedBytes, ct);

            // 系統讀到就再儲存一次（出一個乾淨嘅檔）
            string outExt = ext is ".heic" or ".heif" or ".avif" or ".webp" ? ".png" : ext;
            string outFile = OutPath(path, outDir, outExt);
            if (o.ImageResave != null && outExt is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff")
            {
                if (o.ImageResave(tmp, outFile))
                {
                    r.Output = outFile;
                    r.Notes.Add(fixedBytes == data && r.Notes.Count == 0 ? Lang.T("相片冇發現明顯損壞，已重新儲存一份（如果仍然開唔到，可能係睇相程式唔支援呢個格式）。") : Lang.T("已重新儲存相片。"));
                    return;
                }
            }
            // 用 ffmpeg（容錯解碼）
            if (o.Ffmpeg != null && File.Exists(o.Ffmpeg))
            {
                string q = outExt is ".jpg" or ".jpeg" ? "-q:v 2" : "";
                var res = await RunAsync(o.Ffmpeg, $"-hide_banner -y -err_detect ignore_err -i {Q(tmp)} -frames:v 1 {q} -update 1 {Q(outFile)}", ct);
                if (res.Code == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0)
                {
                    r.Output = outFile;
                    r.Notes.Add(ext is ".heic" or ".heif" or ".avif" or ".webp"
                        ? Lang.F("已轉做 PNG（Windows 預設未必開到 {0}）。", ext.TrimStart('.').ToUpperInvariant())
                        : fixedBytes == data && r.Notes.Count == 0
                            ? Lang.T("相片冇發現明顯損壞，已重新儲存一份（如果仍然開唔到，可能係睇相程式唔支援呢個格式）。")
                            : Lang.T("已重新解碼相片：損壞嘅部分可能會變灰色、綠色或者花咗。"));
                    return;
                }
                try { File.Delete(outFile); } catch { }
            }
            else if (ext is ".heic" or ".heif" or ".avif" or ".webp" || fixedBytes == data)
            {
                r.NeedTool = "ffmpeg";
                return;
            }
            if (fixedBytes != data)
            {
                // 淨係修咗檔案結構（冇得驗證）
                string raw = OutPath(path, outDir, ext);
                await File.WriteAllBytesAsync(raw, fixedBytes, ct);
                r.Output = raw;
                return;
            }
            r.Error = Lang.T("讀唔到相片內容，呢個檔案損壞得太嚴重。");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>JPEG：剪走開頭雜資料、補返結尾標記（FFD9）。</summary>
    public static byte[] FixJpeg(byte[] d, List<string> notes)
    {
        int start = -1;
        for (int i = 0; i + 2 < d.Length; i++) if (d[i] == 0xFF && d[i + 1] == 0xD8 && d[i + 2] == 0xFF) { start = i; break; }
        if (start < 0) return d;
        var outp = new List<byte>(d.Length + 2);
        if (start > 0) notes.Add(Lang.T("剪走咗檔頭前面嘅多餘資料。"));
        int end = d.Length;
        while (end > start + 2 && d[end - 1] == 0) end--; // 結尾嘅 0（未寫完）
        bool hasEoi = end - start >= 4 && d[end - 2] == 0xFF && d[end - 1] == 0xD9;
        outp.AddRange(new ArraySegment<byte>(d, start, end - start));
        if (!hasEoi)
        {
            outp.Add(0xFF); outp.Add(0xD9);
            notes.Add(Lang.T("相片唔完整（結尾唔見咗），已補返結尾。"));
        }
        return outp.ToArray();
    }

    // ---------- PNG ----------
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> b, uint crc = 0)
    {
        crc ^= 0xFFFFFFFF;
        foreach (byte x in b) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint BE(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static void PutBE(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }

    private static void Chunk(List<byte> outp, string type, ReadOnlySpan<byte> data)
    {
        PutBE(outp, (uint)data.Length);
        var t = Encoding.ASCII.GetBytes(type);
        outp.AddRange(t);
        outp.AddRange(data.ToArray());
        PutBE(outp, Crc32(data, Crc32(t)));
    }

    /// <summary>
    /// PNG：逐個 chunk 檢查、修正 CRC、剪走 IEND 後面嘅嘢、補 IEND；
    /// 圖片資料（IDAT）唔完整就自己解碼，冇咗嘅行用灰色補，再輸出一個新 PNG（decoded = true）。
    /// </summary>
    public static byte[]? FixPng(byte[] d, List<string> notes, out bool decoded)
    {
        decoded = false;
        byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (d.Length < 8 + 25) return null;
        int pos = 8;
        bool sigBad = !d.AsSpan(0, 8).SequenceEqual(sig);
        if (sigBad) notes.Add(Lang.T("修正咗 PNG 檔頭。"));
        var chunks = new List<(string Type, byte[] Data)>();
        int crcFixed = 0;
        bool truncated = false, sawEnd = false;
        while (pos + 8 <= d.Length)
        {
            uint len = BE(d, pos);
            string type = Encoding.ASCII.GetString(d, pos + 4, 4);
            if (!Regex.IsMatch(type, "^[A-Za-z]{4}$")) { truncated = true; break; }
            if (len > int.MaxValue || pos + 8 + len > d.Length)
            {
                // 最後一段唔完整：IDAT 就保留有嘅部分
                if (type == "IDAT") chunks.Add((type, d.AsSpan(pos + 8, d.Length - pos - 8).ToArray()));
                truncated = true;
                break;
            }
            var data = d.AsSpan(pos + 8, (int)len).ToArray();
            if (pos + 12 + len <= d.Length)
            {
                uint crc = BE(d, pos + 8 + (int)len);
                if (crc != Crc32(data, Crc32(Encoding.ASCII.GetBytes(type)))) crcFixed++;
            }
            else truncated = true;
            if (type == "IEND") { sawEnd = true; break; }
            chunks.Add((type, data));
            pos += 12 + (int)len;
        }
        if (chunks.Count == 0 || chunks[0].Type != "IHDR" || chunks[0].Data.Length != 13) return null;
        if (crcFixed > 0) notes.Add(Lang.F("修正咗 {0} 個校驗碼（CRC）錯誤。", crcFixed));
        if (!sawEnd) truncated = true;
        if (truncated) notes.Add(Lang.T("相片唔完整（後半部分唔見咗）。"));

        // 試吓解碼圖片資料：完整就淨係重砌 chunk；唔完整就解碼 + 補灰色 + 重新編碼
        var ihdr = chunks[0].Data;
        int w = (int)BE(ihdr, 0), h = (int)BE(ihdr, 4);
        byte depth = ihdr[8], color = ihdr[9], interlace = ihdr[12];
        var idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        int ch = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        long rowBytes = depth == 8 && ch > 0 ? (long)w * ch : 0;
        byte[]? raw = null;
        int rowsGot = h;
        if (rowBytes > 0 && interlace == 0 && w > 0 && h > 0 && (rowBytes + 1) * h < 600_000_000)
        {
            raw = Inflate(idat, (int)((rowBytes + 1) * h), out int got);
            rowsGot = (int)(got / (rowBytes + 1));
        }
        if (raw != null && rowsGot < h)
        {
            // 自己解碼：每行先還原 filter，冇咗嘅行用灰色
            int bpp = ch;
            var img = new byte[rowBytes * h];
            var prev = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                var cur = img.AsSpan((int)(y * rowBytes), (int)rowBytes);
                if (y < rowsGot)
                {
                    int f = raw[y * (rowBytes + 1)];
                    var src = raw.AsSpan((int)(y * (rowBytes + 1) + 1), (int)rowBytes);
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int x = src[i];
                        cur[i] = (byte)(f switch
                        {
                            1 => x + a,
                            2 => x + b,
                            3 => x + ((a + b) >> 1),
                            4 => x + Paeth(a, b, c),
                            _ => x
                        });
                    }
                }
                else if (color == 3) cur.Clear();
                else
                {
                    for (int i = 0; i < rowBytes; i++) cur[i] = 128;
                    if (ch is 2 or 4) for (int i = ch - 1; i < rowBytes; i += ch) cur[i] = 255; // alpha 唔透明
                }
                cur.CopyTo(prev);
            }
            // 重新編碼（filter 0）
            var filt = new byte[(rowBytes + 1) * h];
            for (int y = 0; y < h; y++) img.AsSpan((int)(y * rowBytes), (int)rowBytes).CopyTo(filt.AsSpan((int)(y * (rowBytes + 1) + 1)));
            byte[] z;
            using (var ms = new MemoryStream())
            {
                using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(filt);
                z = ms.ToArray();
            }
            var outp = new List<byte>(sig);
            foreach (var c in chunks.Where(c => c.Type != "IDAT" && (c.Type == "IHDR" || c.Type == "PLTE" || c.Type == "tRNS" || c.Type == "gAMA" || c.Type == "sRGB" || c.Type == "pHYs" || c.Type == "iCCP" || c.Type == "cHRM")))
                Chunk(outp, c.Type, c.Data);
            Chunk(outp, "IDAT", z);
            Chunk(outp, "IEND", ReadOnlySpan<byte>.Empty);
            decoded = true;
            notes.Add(Lang.F("救返 {0} / {1} 行，冇咗嘅部分用灰色補。", rowsGot, h));
            return outp.ToArray();
        }
        // 圖片資料完整（或者唔支援自己解碼）：重砌 chunk（CRC 全部重新計）
        if (crcFixed == 0 && !truncated && !sigBad) return d;
        var o2 = new List<byte>(sig);
        foreach (var c in chunks) Chunk(o2, c.Type, c.Data);
        Chunk(o2, "IEND", ReadOnlySpan<byte>.Empty);
        return o2.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>zlib 解壓：壞咗 / 唔完整就盡量解，回傳解到嘅長度。</summary>
    private static byte[] Inflate(byte[] z, int max, out int got)
    {
        var outBuf = new byte[max];
        got = 0;
        try
        {
            using var zs = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
            int n;
            while (got < max && (n = zs.Read(outBuf, got, Math.Min(65536, max - got))) > 0) got += n;
        }
        catch { /* 唔完整：保留已經解到嘅 */ }
        return outBuf;
    }

    // ------------------------------------------------------------------
    //  ZIP / Office / EPUB
    // ------------------------------------------------------------------
    private sealed class ZEntry { public string Name = ""; public byte[] Data = Array.Empty<byte>(); public bool Damaged; public DateTime Time; }

    private static void Zip(string path, string outDir, string ext, RepairOutcome r, Action<string, double> progress, CancellationToken ct)
    {
        progress(Lang.T("檢查緊…"), -1);
        var fi = new FileInfo(path);
        if (fi.Length > 1_500_000_000) { r.Error = Lang.T("檔案太大（超過 1.5 GB），暫時唔支援。"); return; }
        // 1. 原本個 ZIP 讀唔讀到（讀晒每個檔案，CRC 會檢查）
        bool okStructure = true;
        var broken = new List<string>();
        try
        {
            using var z = ZipFile.OpenRead(path);
            foreach (var e in z.Entries)
            {
                ct.ThrowIfCancellationRequested();
                try { using var s = e.Open(); s.CopyTo(Stream.Null); }
                catch { broken.Add(e.FullName); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { okStructure = false; }

        if (okStructure && broken.Count == 0)
        {
            string? missing = OfficeMissing(path, ext);
            if (missing == null)
            {
                r.Error = Lang.T("ZIP 結構完整，冇發現損壞。如果開唔到，可能係程式版本太舊或者檔案有密碼。");
                return;
            }
        }

        // 2. 由殘骸重砌：逐個搵「PK 03 04」本地檔頭
        byte[] d = File.ReadAllBytes(path);
        var entries = new Dictionary<string, ZEntry>(StringComparer.Ordinal);
        int found = 0, damaged = 0;
        for (int p = 0; p + 30 <= d.Length; p++)
        {
            if (d[p] != 0x50 || d[p + 1] != 0x4B || d[p + 2] != 3 || d[p + 3] != 4) continue;
            if ((p & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int flags = d[p + 6] | d[p + 7] << 8;
            int method = d[p + 8] | d[p + 9] << 8;
            uint crc = (uint)(d[p + 14] | d[p + 15] << 8 | d[p + 16] << 16 | d[p + 17] << 24);
            long csize = (uint)(d[p + 18] | d[p + 19] << 8 | d[p + 20] << 16 | d[p + 21] << 24);
            long usize = (uint)(d[p + 22] | d[p + 23] << 8 | d[p + 24] << 16 | d[p + 25] << 24);
            int nlen = d[p + 26] | d[p + 27] << 8, xlen = d[p + 28] | d[p + 29] << 8;
            if (p + 30 + nlen + xlen > d.Length || nlen == 0 || nlen > 1024) continue;
            if (method != 0 && method != 8) continue;
            string name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(d, p + 30, nlen) : Encoding.Latin1.GetString(d, p + 30, nlen);
            if (name.IndexOfAny(new[] { '\0', ':' }) >= 0 || name.StartsWith("/") || name.Contains("..")) continue;
            int dataStart = p + 30 + nlen + xlen;
            int dosTime = d[p + 10] | d[p + 11] << 8, dosDate = d[p + 12] | d[p + 13] << 8;
            found++;
            if (name.EndsWith("/")) continue; // 資料夾
            long avail;
            if ((flags & 8) != 0 || csize == 0 && usize == 0 || csize == 0xFFFFFFFF)
            {
                // 冇記低大細：一直去到下一個 PK 標記
                int q = NextSig(d, dataStart);
                avail = (q < 0 ? d.Length : q) - dataStart;
                csize = -1;
            }
            else avail = Math.Min(csize, d.Length - dataStart);
            byte[] data;
            bool bad = false;
            if (method == 0)
            {
                long take = csize >= 0 ? avail : avail;
                data = d.AsSpan(dataStart, (int)Math.Max(0, take)).ToArray();
                if (csize >= 0 && take < csize) bad = true;
                if (csize < 0 && data.Length >= 16 && (flags & 8) != 0) { /* 可能包括 data descriptor：用 CRC 判斷 */ }
            }
            else
            {
                data = InflateRaw(d, dataStart, (int)avail, usize > 0 && usize < 1_000_000_000 ? (int)usize : -1, out bool complete);
                if (!complete) bad = true;
            }
            if (crc != 0 && (flags & 8) == 0 && Crc32(data) != crc) bad = true;
            if (data.Length == 0 && usize > 0) continue;
            var ent = new ZEntry { Name = name, Data = data, Damaged = bad, Time = DosTime(dosDate, dosTime) };
            // 同名：保留冇損壞 / 大啲嗰個
            if (!entries.TryGetValue(name, out var old) || (old.Damaged && !bad) || (old.Damaged == bad && data.Length > old.Data.Length))
                entries[name] = ent;
            progress(Lang.T("搵緊入面嘅檔案…"), (double)p / d.Length);
        }
        if (entries.Count == 0) { r.Error = Lang.T("搵唔到任何可以救返嘅內容。"); return; }
        damaged = entries.Values.Count(e => e.Damaged);

        // 3. 寫新 ZIP（Office / EPUB 要嘅檔案擺最前）
        string outFile = OutPath(path, outDir, ext);
        var order = entries.Values.OrderBy(e => e.Name == "mimetype" ? 0 : e.Name == "[Content_Types].xml" ? 1 : 2).ToList();
        using (var fs = File.Create(outFile))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var e in order)
            {
                var ze = z.CreateEntry(e.Name, e.Name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                try { ze.LastWriteTime = e.Time; } catch { }
                using var s = ze.Open();
                s.Write(e.Data);
            }
        }
        r.Output = outFile;
        r.Notes.Add(Lang.F("重新砌過 ZIP：救返 {0} 個檔案。", entries.Count));
        if (damaged > 0)
            r.Notes.Add(Lang.F("其中 {0} 個唔完整：{1}", damaged, string.Join("、", entries.Values.Where(e => e.Damaged).Select(e => e.Name).Take(6)) + (damaged > 6 ? "…" : "")));
        string? miss = OfficeMissing(outFile, ext);
        if (miss != null) r.Notes.Add(Lang.F("缺少 {0}：Office 可能仍然開唔到，可以試吓用 Office 嘅「開啟並修復」。", miss));
    }

    /// <summary>Office / EPUB 必要嘅檔案冇咗（回傳缺咗邊個；冇問題 = null）。</summary>
    private static string? OfficeMissing(string zipPath, string ext)
    {
        string? need = ext switch
        {
            ".docx" or ".docm" => "word/document.xml",
            ".xlsx" or ".xlsm" => "xl/workbook.xml",
            ".pptx" or ".pptm" => "ppt/presentation.xml",
            ".epub" => "META-INF/container.xml",
            _ => null
        };
        if (need == null) return null;
        try
        {
            using var z = ZipFile.OpenRead(zipPath);
            if (ext is not ".epub" && z.GetEntry("[Content_Types].xml") == null) return "[Content_Types].xml";
            return z.GetEntry(need) == null ? need : null;
        }
        catch { return need; }
    }

    private static int NextSig(byte[] d, int from)
    {
        for (int i = from; i + 4 <= d.Length; i++)
            if (d[i] == 0x50 && d[i + 1] == 0x4B && ((d[i + 2] == 3 && d[i + 3] == 4) || (d[i + 2] == 1 && d[i + 3] == 2) || (d[i + 2] == 7 && d[i + 3] == 8) || (d[i + 2] == 5 && d[i + 3] == 6)))
                return i;
        return -1;
    }

    private static byte[] InflateRaw(byte[] d, int off, int len, int expect, out bool complete)
    {
        complete = false;
        using var outMs = new MemoryStream(expect > 0 ? expect : Math.Max(1024, len * 3));
        try
        {
            using var ds = new DeflateStream(new MemoryStream(d, off, Math.Max(0, len)), CompressionMode.Decompress);
            var buf = new byte[65536];
            int n;
            while ((n = ds.Read(buf, 0, buf.Length)) > 0)
            {
                outMs.Write(buf, 0, n);
                if (expect > 0 && outMs.Length > expect * 2L) break;
            }
            complete = expect < 0 || outMs.Length == expect;
        }
        catch { }
        return outMs.ToArray();
    }

    private static DateTime DosTime(int date, int time)
    {
        try { return new DateTime(1980 + (date >> 9), Math.Clamp((date >> 5) & 15, 1, 12), Math.Clamp(date & 31, 1, 28), (time >> 11) % 24, ((time >> 5) & 63) % 60, (time & 31) * 2 % 60); }
        catch { return DateTime.Now; }
    }

    // ------------------------------------------------------------------
    //  PDF（qpdf 會自動重建損壞嘅索引表）
    // ------------------------------------------------------------------
    private static async Task PdfAsync(string path, string outDir, RepairOptions o, RepairOutcome r, Action<string, double> progress, CancellationToken ct)
    {
        if (o.Qpdf == null || !File.Exists(o.Qpdf)) { r.NeedTool = "qpdf"; return; }
        progress(Lang.T("檢查緊…"), -1);
        byte[] d = await File.ReadAllBytesAsync(path, ct);
        string src = path;
        string? tmp = null;
        int start = IndexOf(d, Encoding.ASCII.GetBytes("%PDF"), 0);
        bool hasEof = IndexOf(d, Encoding.ASCII.GetBytes("%%EOF"), Math.Max(0, d.Length - 2048)) >= 0;
        try
        {
            if (start > 0 || !hasEof)
            {
                if (start > 0) r.Notes.Add(Lang.T("剪走咗檔頭前面嘅多餘資料。"));
                if (!hasEof) r.Notes.Add(Lang.T("PDF 唔完整（結尾唔見咗）。"));
                tmp = Path.Combine(Path.GetTempPath(), "MultiTool-repair-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
                using (var fs = File.Create(tmp))
                {
                    fs.Write(d, Math.Max(0, start), d.Length - Math.Max(0, start));
                    if (!hasEof) fs.Write(Encoding.ASCII.GetBytes("\n%%EOF\n"));
                }
                src = tmp;
            }
            string outFile = OutPath(path, outDir, ".pdf");
            progress(Lang.T("重建 PDF…"), -1);
            var res = await RunAsync(o.Qpdf, $"--warning-exit-0 --object-streams=preserve {Q(src)} {Q(outFile)}", ct);
            if (res.Code == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0)
            {
                r.Output = outFile;
                bool repaired = res.Err.Contains("damaged", StringComparison.OrdinalIgnoreCase) || res.Err.Contains("reconstruct", StringComparison.OrdinalIgnoreCase) || tmp != null;
                r.Notes.Add(repaired ? Lang.T("已重建 PDF 嘅索引表。") : Lang.T("PDF 結構冇明顯損壞，已重新整理一次。"));
                // 「已經自動修好」嗰類警告唔計
                string[] fixedWords = { "damaged", "xref", "cross-reference", "reconstruct", "recover", "repairing", "expected endstream" };
                int warn = res.Err.Split('\n').Count(l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase) &&
                                                         !fixedWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));
                if (warn > 0) r.Notes.Add(Lang.F("有 {0} 個警告：部分頁面或者圖片可能救唔返。", warn));
                return;
            }
            try { File.Delete(outFile); } catch { }
            string msg = res.Err.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
            r.Error = res.Err.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? Lang.T("PDF 有密碼保護，要先用密碼開啟。")
                : Lang.T("修唔到：") + msg;
        }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    private static int IndexOf(byte[] d, byte[] pat, int from)
    {
        for (int i = Math.Max(0, from); i + pat.Length <= d.Length; i++)
        {
            int k = 0;
            while (k < pat.Length && d[i + k] == pat[k]) k++;
            if (k == pat.Length) return i;
        }
        return -1;
    }
}

/// <summary>檔案修復用嘅第三方工具（第一次用先下載）：qpdf（PDF）、untrunc（MP4 冇 moov）。</summary>
internal static class RepairTools
{
    public static string QpdfDir => Path.Combine(AppPaths.Data, "tools", "qpdf");
    public static string QpdfExe => Path.Combine(QpdfDir, "qpdf.exe");
    public static string UntruncDir => Path.Combine(AppPaths.Data, "tools", "untrunc");
    public static string UntruncExe => Path.Combine(UntruncDir, "untrunc.exe");

    private static bool X64 => Environment.Is64BitOperatingSystem;

    /// <summary>（名稱, 大約大細）</summary>
    public static (string Name, string Size) Info(string tool) => tool switch
    {
        "qpdf" => ("qpdf", "~28 MB"),
        "untrunc" => ("untrunc", "~13 MB"),
        _ => ("ffmpeg", "~100 MB")
    };

    public static async Task InstallAsync(string tool, Action<string, double> progress, CancellationToken ct)
    {
        if (tool == "ffmpeg") { await Video.Ffmpeg.InstallAsync(progress, ct); return; }
        string url, dir;
        Func<string, string?> map; // zip 入面嘅路徑 → 解壓去邊（null = 唔要）
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
        if (tool == "qpdf")
        {
            dir = QpdfDir;
            string want = X64 ? "-msvc64.zip" : "-msvc32.zip";
            url = "";
            try
            {
                var json = await http.GetStringAsync("https://api.github.com/repos/qpdf/qpdf/releases/latest", ct);
                var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+" + Regex.Escape(want) + ")\"");
                if (m.Success) url = m.Groups[1].Value;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (url.Length == 0) url = "https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2" + want;
            map = e => e.Contains("/bin/") && !e.EndsWith("/") ? Path.Combine(dir, Path.GetFileName(e)) : null;
        }
        else
        {
            dir = UntruncDir;
            url = "https://github.com/anthwlock/untrunc/releases/download/latest/" + (X64 ? "untrunc_x64.zip" : "untrunc_x32.zip");
            map = e => !e.EndsWith("/") && !e.EndsWith("untrunc-gui.exe", StringComparison.OrdinalIgnoreCase) ? Path.Combine(dir, Path.GetFileName(e)) : null;
        }
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "download.zip"), part = zip + ".part";
        string label = Lang.F("下載 {0}…", Info(tool).Name);
        using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(label, total > 0 ? (double)done / total : -1);
            }
        }
        File.Move(part, zip, true);
        progress(Lang.T("解壓縮…"), -1);
        await Task.Run(() =>
        {
            using var z = ZipFile.OpenRead(zip);
            foreach (var e in z.Entries)
                if (map(e.FullName) is { } to) e.ExtractToFile(to, true);
        }, ct);
        try { File.Delete(zip); } catch { }
        if (!File.Exists(tool == "qpdf" ? QpdfExe : UntruncExe)) throw new InvalidOperationException(Lang.T("下載咗但搵唔到程式檔"));
        progress(Lang.T("完成"), 1);
    }

    public static void Delete()
    {
        foreach (var d in new[] { QpdfDir, UntruncDir }) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }
}
