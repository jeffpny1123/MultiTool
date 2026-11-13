using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 本地 AI 製影片：同 AI 製圖共用 stable-diffusion.cpp（sd-cli -M vid_gen）+ Wan 影片模型（全部 Apache 2.0）。
/// 模型由用家自己喺 Hugging Face 下載（唔會包喺拉比工具箱入面）。每個模型寫明係「文字轉影片」定「圖轉影片」。
/// </summary>
internal static class LocalVideo
{
    [Flags]
    public enum Kind { Text = 1, Image = 2 }

    /// <summary>一個要下載嘅檔。Name = 存落碟嘅檔名：唔同模型用同一個 Name 就共用，唔會重複下載。</summary>
    public sealed record VFile(string Name, string Repo, Func<string, bool> Match, string[] Prefer);

    /// <summary>Kinds = 做到邊種；Fps = 模型本身嘅格數；HighNoise = Wan2.2 14B 要兩個模型（高噪 + 低噪）；Extra = 取樣參數。</summary>
    public sealed record Model(string Key, string Label, string Size, string Note, Kind Kinds, int NeedGb, int MinGb, int Fps, VFile Diffusion, VFile? HighNoise, VFile Vae, string Extra)
    {
        /// <summary>顯示卡記憶體（GB）：少過 MinVram = 未達最低要求；NeedVram = 建議。</summary>
        public int MinVram { get; init; } = 4;
        public int NeedVram { get; init; } = 8;
        public VFile[] Files => HighNoise != null ? new[] { Diffusion, HighNoise, Vae, Umt5 } : new[] { Diffusion, Vae, Umt5 };
    }

    private static readonly string[] Q4 = { "Q4_K_M", "Q4_K_S", "Q4_0" };
    private static bool Ends(string f, string tail) => f.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    private static Func<string, bool> GgufIn(string folder) => f => Ends(f, ".gguf") && (folder.Length == 0 ? !f.Contains('/') : f.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

    // 文字理解（所有 Wan 模型共用）同兩款 VAE
    private static readonly VFile Umt5 = new("umt5-xxl.gguf", "city96/umt5-xxl-encoder-gguf", GgufIn(""), Q4);
    private static readonly VFile Vae21 = new("wan21-vae.safetensors", "Comfy-Org/Wan_2.1_ComfyUI_repackaged", f => Ends(f, "vae/wan_2.1_vae.safetensors"), Array.Empty<string>());
    private static readonly VFile Vae22 = new("wan22-vae.safetensors", "Comfy-Org/Wan_2.2_ComfyUI_Repackaged", f => Ends(f, "vae/wan2.2_vae.safetensors"), Array.Empty<string>());

    public static readonly Model[] Models =
    {
        new("wan21-t2v-1.3b", "Wan2.1 T2V 1.3B", "≈ 6.3 GB", "最輕量，8 GB 顯示卡都行到；畫質一般，啱試玩", Kind.Text, 16, 8, 16,
            new("wan21-t2v-1.3b.safetensors", "Comfy-Org/Wan_2.1_ComfyUI_repackaged", f => Ends(f, "wan2.1_t2v_1.3B_fp16.safetensors"), Array.Empty<string>()), null, Vae21,
            "--cfg-scale 6.0 --steps 20"),
        new("wan22-ti2v-5b", "Wan2.2 TI2V 5B", "≈ 7.9 GB", "一個模型兩樣都做到；畫質同速度平衡", Kind.Text | Kind.Image, 16, 12, 24,
            new("wan22-ti2v-5b.gguf", "QuantStack/Wan2.2-TI2V-5B-GGUF", GgufIn(""), Q4), null, Vae22,
            "--cfg-scale 6.0 --steps 20") { MinVram = 6, NeedVram = 12 },
        new("wan22-i2v-a14b", "Wan2.2 I2V A14B", "≈ 21.6 GB", "將一張圖變成影片，畫質最好；好食資源，建議 16 GB 以上顯示卡", Kind.Image, 32, 24, 16,
            new("wan22-i2v-a14b-low.gguf", "QuantStack/Wan2.2-I2V-A14B-GGUF", GgufIn("LowNoise"), Q4),
            new("wan22-i2v-a14b-high.gguf", "QuantStack/Wan2.2-I2V-A14B-GGUF", GgufIn("HighNoise"), Q4), Vae21,
            "--cfg-scale 3.5 --steps 10 --high-noise-cfg-scale 3.5 --high-noise-sampling-method euler --high-noise-steps 8") { MinVram = 8, NeedVram = 16 },
        new("wan22-t2v-a14b", "Wan2.2 T2V A14B", "≈ 21.6 GB", "用文字出影片，畫質最好；好食資源，建議 16 GB 以上顯示卡", Kind.Text, 32, 24, 16,
            new("wan22-t2v-a14b-low.gguf", "QuantStack/Wan2.2-T2V-A14B-GGUF", GgufIn("LowNoise"), Q4),
            new("wan22-t2v-a14b-high.gguf", "QuantStack/Wan2.2-T2V-A14B-GGUF", GgufIn("HighNoise"), Q4), Vae21,
            "--cfg-scale 3.5 --steps 10 --high-noise-cfg-scale 3.5 --high-noise-sampling-method euler --high-noise-steps 8") { MinVram = 8, NeedVram = 16 },
    };

    // Wan 官方建議嘅負面提示（模型參數，唔係介面文字）
    private const string Negative = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    public static string Dir => Path.Combine(AiProvider.WorkDir, "video");
    private static string FilePath(VFile f) => Path.Combine(Dir, f.Name);
    /// <summary>整好嘅影片：影片\RabiToolkit\AI</summary>
    public static string OutDir => Path.Combine(Program.BrandFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)), "AI");

    public static bool ModelReady(Model m) => m.Files.All(f => File.Exists(FilePath(f)));
    public static Model[] Ready => LocalImage.BinReady ? Models.Where(ModelReady).ToArray() : Array.Empty<Model>();

    public static string KindText(Kind k) => k == (Kind.Text | Kind.Image) ? Lang.T("文字轉影片") + " ＋ " + Lang.T("圖轉影片") : k == Kind.Image ? Lang.T("圖轉影片") : Lang.T("文字轉影片");

    /// <summary>部機夠唔夠（記憶體 + 顯示卡）：Level 0 = 夠、1 = 可能唔夠、2 = 未達最低要求（紅字，下載前再確認）。</summary>
    public static (int Level, string Text) Fit(Model m) => LocalAi.Check(m.MinGb, m.NeedGb, m.MinVram, m.NeedVram);

    /// <summary>一 click 安裝：未有程式就先下載 stable-diffusion.cpp，再逐個下載模型檔（同其他影片模型共用嘅檔唔會再下載）。</summary>
    public static async Task InstallAsync(Model m, Action<string, double> progress, CancellationToken ct)
    {
        await LocalImage.EnsureBinAsync(progress, ct);
        Directory.CreateDirectory(Dir);
        var files = m.Files;
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            if (File.Exists(FilePath(f))) continue;
            progress(Lang.T("搵緊模型…"), -1);
            string file = await LocalImage.ResolveAsync(f.Repo, f.Match, f.Prefer, ct);
            int n = i + 1;
            await LocalImage.DownloadAsync($"https://huggingface.co/{f.Repo}/resolve/main/{file}", FilePath(f),
                (d, t) => progress(Lang.F("下載模型 {0}（{1}/{2}）…", m.Label, n, files.Length), t > 0 ? (double)d / t : -1), ct);
        }
        progress(Lang.T("完成"), 1);
    }

    /// <summary>刪除模型：其他已下載嘅影片模型仲用緊嘅共用檔會留低。</summary>
    public static void Delete(Model m)
    {
        var keep = Models.Where(x => x != m && ModelReady(x)).SelectMany(x => x.Files).Select(f => f.Name).ToHashSet();
        foreach (var f in m.Files.Where(f => !keep.Contains(f.Name)))
            try { File.Delete(FilePath(f)); } catch (Exception ex) { AppPaths.LogError("刪除影片模型", ex); }
    }

    public static void Stop() => LocalImage.Stop();

    /// <summary>將用家張圖中間裁成同影片一樣嘅比例再縮放（唔係就會俾人拉扁）。</summary>
    private static string PrepareImage(string src, int w, int h)
    {
        string dst = Path.Combine(Dir, "init.png");
        using var img = Image.FromFile(src);
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double scale = Math.Max((double)w / img.Width, (double)h / img.Height);
            double sw = w / scale, sh = h / scale;
            g.DrawImage(img, new Rectangle(0, 0, w, h), new RectangleF((float)((img.Width - sw) / 2), (float)((img.Height - sh) / 2), (float)sw, (float)sh), GraphicsUnit.Pixel);
        }
        bmp.Save(dst, ImageFormat.Png);
        return dst;
    }

    /// <summary>
    /// 整一段影片，回傳 .webm 路徑。image = null 就係文字轉影片，有就係圖轉影片。
    /// progress(第幾步, 總步數, 狀態字)。
    /// </summary>
    public static async Task<string> GenerateAsync(Model m, string prompt, string? image, int w, int h, double seconds, Action<int, int, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(Dir);
        string outFile = Path.Combine(OutDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".webm");
        // Wan 要 4n + 1 格
        int frames = (int)Math.Round(seconds * m.Fps / 4.0) * 4 + 1;
        string Q(string s) => LocalImage.Q(s);
        string args = $"-M vid_gen --diffusion-model {Q(FilePath(m.Diffusion))}" +
                      (m.HighNoise != null ? $" --high-noise-diffusion-model {Q(FilePath(m.HighNoise))}" : "") +
                      $" --vae {Q(FilePath(m.Vae))} --t5xxl {Q(FilePath(Umt5))}" +
                      $" -p {Q(prompt.Replace("\r", " ").Replace("\n", " "))} -n {Q(Negative)}" +
                      $" -W {w} -H {h} --video-frames {frames} --fps {m.Fps} --flow-shift 3.0 --sampling-method euler {m.Extra}" +
                      // --offload-to-cpu / --vae-tiling：顯示卡記憶體唔夠都盡量行到（慢啲）
                      " --seed -1 --offload-to-cpu --diffusion-fa --vae-tiling" +
                      (image != null ? $" --init-img {Q(PrepareImage(image, w, h))}" : "") +
                      $" -o {Q(outFile)}";
        await LocalImage.RunAsync(args, outFile, "AI 製影片", progress, ct, Lang.T("整唔到影片："));
        return outFile;
    }
}

/// <summary>「影片模型」小視窗：每個模型寫明「文字轉影片 / 圖轉影片」，一 click 下載。</summary>
internal sealed class VideoModelDialog : Form
{
    public event Action? Changed;
    private CancellationTokenSource? _cts;

    public VideoModelDialog()
    {
        Text = Lang.T("影片模型");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        int rowH = Ui.S(104), W = Ui.S(540), btnW = Ui.S(112), textW = Ui.S(540 - 24 - 24 - 112 - 12);
        double ram = LocalAi.RamGb;
        ClientSize = new Size(W, Ui.S(108) + rowH * LocalVideo.Models.Length + Ui.S(8));
        FormClosing += (_, _) => _cts?.Cancel();
        Controls.Add(new Label
        {
            Text = Lang.T("免費模型，喺你部機自己整影片：唔使帳號、唔使錢，下載完唔使上網。全部係 Apache 2.0 授權，整出嚟嘅影片可以商用。") + "\n" +
                   Lang.T("整影片好食資源：冇獨立顯示卡會非常慢，一段幾秒嘅影片都可能要幾十分鐘。幾個模型共用嘅檔案唔會重複下載。") +
                   "\n" + LocalAi.SpecText(),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(10), W - Ui.S(32), Ui.S(92))
        });
        int y = Ui.S(104);
        var rows = new List<Action>();
        foreach (var m in LocalVideo.Models)
        {
            var (fit, fitText) = LocalVideo.Fit(m);
            var panel = new Panel { BackColor = Ui.CardHi, Bounds = new Rectangle(Ui.S(12), y, W - Ui.S(24), rowH - Ui.S(8)) };
            int bx = panel.Width - btnW - Ui.S(12);
            var name = new Label { Text = $"{m.Label}　{m.Size}", Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(8), textW, Ui.S(22)) };
            // 寫明做到邊種
            var kind = new Label { Text = "🎬 " + LocalVideo.KindText(m.Kinds), Font = Ui.F(8.75f, FontStyle.Bold), ForeColor = Ui.Accent, AutoSize = true, Location = new Point(Ui.S(12), Ui.S(32)) };
            var need = new Label { Text = Lang.F("建議 {0} GB RAM", m.NeedGb) + " · " + Lang.F("顯示卡 {0} GB", m.NeedVram), Font = Ui.F(8.75f), AutoSize = true,
                ForeColor = fit == 2 ? Ui.Danger : fit == 1 ? Ui.Hex(0xE0A030) : Ui.TextDim };
            void PlaceTags() => need.Location = new Point(kind.Right + Ui.S(12), Ui.S(32));
            kind.SizeChanged += (_, _) => PlaceTags();
            var note = new Label { Text = Lang.T(m.Note), Font = Ui.F(8.75f), ForeColor = Ui.TextFaint, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(52), textW, Ui.S(18)) };
            new ToolTip().SetToolTip(note, Lang.T(m.Note));
            var state = new Label { Font = Ui.F(8.75f), AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(72), textW, Ui.S(18)) };
            var bar = new ProgressBar { Bounds = new Rectangle(Ui.S(12), Ui.S(90), panel.Width - Ui.S(24), Ui.S(4)), Visible = false };
            var main = new FancyButton { Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(bx, Ui.S(10), btnW, Ui.S(30)) };
            var del = new FancyButton { Text = Lang.T("刪除"), Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Bounds = new Rectangle(bx, Ui.S(44), btnW, Ui.S(22)) };
            panel.Controls.AddRange(new Control[] { name, kind, need, note, state, bar, main, del });
            PlaceTags();
            Controls.Add(panel);

            void Refresh()
            {
                bool ready = LocalImage.BinReady && LocalVideo.ModelReady(m);
                state.Text = ready ? Lang.T("✓ 已下載") : Lang.T("未下載");
                state.ForeColor = ready ? Ui.Success : Ui.TextFaint;
                if (!ready && fit == 1) { state.Text = fitText; state.ForeColor = Ui.Hex(0xE0A030); }
                if (!ready && fit == 2) { state.Text = "⚠ " + fitText; state.ForeColor = Ui.Danger; }
                // 未達最低要求：成行紅字，仍然可以撳下載（會再警告）
                if (fit == 2) { name.ForeColor = Ui.Danger; main.Kind = ready ? BtnKind.Primary : BtnKind.Danger; }
                main.Text = ready ? Lang.T("✓ 已下載") : Lang.T("下載");
                main.Enabled = !ready && _cts == null;
                del.Visible = ready && _cts == null;
                main.Invalidate();
            }
            rows.Add(Refresh);

            main.Click += async (_, _) =>
            {
                // 未達最低要求：再提醒一次（警告 + 免責聲明），用家堅持先下載
                if (fit == 2 && !LocalAi.ConfirmWeak(this, m.Label, fitText)) return;
                _cts = new CancellationTokenSource();
                rows.ForEach(r => r());
                bar.Visible = true;
                bool ok = false;
                try
                {
                    await LocalVideo.InstallAsync(m, (text, frac) => BeginInvoke(() =>
                    {
                        if (IsDisposed) return;
                        state.ForeColor = Ui.TextDim;
                        state.Text = text + (frac >= 0 ? $" {frac * 100:0}%" : "");
                        bar.Style = frac >= 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                        if (frac >= 0) bar.Value = (int)Math.Clamp(frac * 100, 0, 100);
                    }), _cts.Token);
                    ok = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppPaths.LogError("下載影片模型", ex);
                    if (!IsDisposed) MessageBox.Show(this, Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _cts = null;
                if (IsDisposed) return;
                bar.Visible = false;
                if (ok) Changed?.Invoke();
                rows.ForEach(r => r());
            };
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Lang.F("刪除 {0}？", m.Label), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LocalVideo.Delete(m);
                Changed?.Invoke();
                rows.ForEach(r => r());
            };
            Refresh();
            y += rowH;
        }
    }
}

/// <summary>「🎬 AI 製影片」視窗：揀「文字轉影片」或者「圖轉影片」→ 喺你部機整一段短片。</summary>
internal sealed class VideoGenDialog : Form
{
    private readonly TextBox _prompt = new();
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _len = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly FancyButton _go, _models, _pick, _play, _save, _folder;
    private readonly Label _status, _picName, _hint;
    private readonly ProgressBar _bar = new() { Visible = false };
    private readonly PictureBox _pic = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private CancellationTokenSource? _cts;
    private string? _last, _image;
    private static readonly (string Label, int W, int H)[] Sizes =
    {
        ("832 × 480（橫）", 832, 480), ("480 × 832（直）", 480, 832), ("640 × 640（正方）", 640, 640), ("1280 × 704（高清，慢好多）", 1280, 704)
    };
    private static readonly double[] Lengths = { 2, 3, 5 };

    private bool ImageMode => _mode.SelectedIndex == 1;

    public VideoGenDialog(string prompt)
    {
        Text = Lang.T("🎬 AI 製影片");
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(780), Ui.S(640));
        MinimumSize = new Size(Ui.S(700), Ui.S(520));

        _prompt.Multiline = true; _prompt.Text = prompt; _prompt.BorderStyle = BorderStyle.FixedSingle;
        _prompt.BackColor = Ui.Input; _prompt.ForeColor = Ui.Text; _prompt.Font = Ui.F(10.5f); _prompt.ScrollBars = ScrollBars.Vertical;
        foreach (var c in new[] { _mode, _model, _size, _len }) { c.BackColor = Ui.Input; c.ForeColor = Ui.Text; c.FlatStyle = FlatStyle.Flat; c.Font = Ui.F(9.75f); }
        _mode.Items.Add(Lang.T("文字轉影片")); _mode.Items.Add(Lang.T("圖轉影片"));
        foreach (var s in Sizes) _size.Items.Add(Lang.T(s.Label));
        foreach (var l in Lengths) _len.Items.Add(Lang.F("{0} 秒", l));
        _size.SelectedIndex = 0; _len.SelectedIndex = 0;
        _go = new FancyButton { Text = Lang.T("🎬 整影片"), Kind = BtnKind.Primary, Font = Ui.F(10f, FontStyle.Bold) };
        _models = new FancyButton { Text = Lang.T("影片模型…"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        _pick = new FancyButton { Text = Lang.T("揀圖片…"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        _play = new FancyButton { Text = Lang.T("▶ 播放"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Enabled = false };
        _save = new FancyButton { Text = Lang.T("另存新檔…"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Enabled = false };
        _folder = new FancyButton { Text = Lang.T("開啟資料夾"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        _status = new Label { ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, AutoEllipsis = true };
        _picName = new Label { ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        _hint = new Label { ForeColor = Ui.TextFaint, Font = Ui.F(10f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Bg };
        _pic.BackColor = Ui.Bg;
        Controls.AddRange(new Control[] { _prompt, _mode, _model, _size, _len, _go, _models, _pick, _picName, _status, _bar, _hint, _pic, _play, _save, _folder });
        _hint.BringToFront();

        Resize += (_, _) => Place();
        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => { FillModels(); Place(); };
        Place();
        FillModels();
        _go.Click += async (_, _) => await GoAsync();
        _models.Click += (_, _) => { using var d = new VideoModelDialog(); d.Changed += FillModels; d.ShowDialog(this); FillModels(); };
        _pick.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = Lang.T("圖片") + "|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var fs = File.OpenRead(d.FileName);
                var old = _pic.Image; _pic.Image = Image.FromStream(fs); old?.Dispose();
                _image = d.FileName;
                _picName.Text = Path.GetFileName(d.FileName);
                // 跟張圖嘅比例揀橫 / 直 / 正方
                double r = (double)_pic.Image.Width / _pic.Image.Height;
                _size.SelectedIndex = r > 1.2 ? 0 : r < 0.83 ? 1 : 2;
                Place();
            }
            catch { MessageBox.Show(this, Lang.T("開唔到呢張圖片。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        _play.Click += (_, _) => { if (_last != null) try { Process.Start(new ProcessStartInfo(_last) { UseShellExecute = true }); } catch { } };
        _folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(LocalVideo.OutDir);
            try { Process.Start(new ProcessStartInfo("explorer.exe", _last != null ? "/select,\"" + _last + "\"" : "\"" + LocalVideo.OutDir + "\"") { UseShellExecute = true }); } catch { }
        };
        _save.Click += (_, _) =>
        {
            if (_last == null) return;
            using var d = new SaveFileDialog { Filter = "WebM|*.webm", FileName = Path.GetFileName(_last) };
            if (d.ShowDialog(this) == DialogResult.OK) try { File.Copy(_last, d.FileName, true); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Program.AppName); }
        };
        FormClosing += (_, _) => { _cts?.Cancel(); LocalVideo.Stop(); };
    }

    private void Place()
    {
        int p = Ui.S(14), w = ClientSize.Width - p * 2, g = Ui.S(8);
        // 第一行：做邊種 + 模型
        _mode.SetBounds(p, p + Ui.S(3), Ui.S(170), Ui.S(28));
        _model.SetBounds(_mode.Right + g, p + Ui.S(3), Ui.S(220), Ui.S(28));
        _models.SetBounds(_model.Right + g, p, Ui.S(120), Ui.S(34));
        int y = p + Ui.S(44);
        _prompt.SetBounds(p, y, w, Ui.S(70));
        _prompt.PlaceholderText = ImageMode ? Lang.T("描述張圖要點樣郁，例如：隻白兔慢慢擰轉頭望住鏡頭，耳仔隨風擺動") : Lang.T("描述你想要嘅影片，例如：一隻白兔喺雪地跳過嚟，鏡頭慢慢拉近，電影感");
        y = _prompt.Bottom + Ui.S(10);
        // 第二行：圖片（圖轉影片先有）、大細、長度、開始
        _pick.Visible = _picName.Visible = ImageMode;
        int x = p;
        if (ImageMode)
        {
            _pick.SetBounds(x, y, Ui.S(100), Ui.S(34));
            _picName.SetBounds(_pick.Right + g, y, Ui.S(130), Ui.S(34));
            x = _picName.Right + g;
        }
        _size.SetBounds(x, y + Ui.S(3), Ui.S(200), Ui.S(28));
        _len.SetBounds(_size.Right + g, y + Ui.S(3), Ui.S(80), Ui.S(28));
        _go.SetBounds(ClientSize.Width - p - Ui.S(130), y, Ui.S(130), Ui.S(34));
        y += Ui.S(42);
        _status.SetBounds(p, y, w, Ui.S(20));
        _bar.SetBounds(p, y + Ui.S(22), w, Ui.S(6));
        y += Ui.S(34);
        int by = ClientSize.Height - p - Ui.S(32);
        _pic.SetBounds(p, y, w, by - y - Ui.S(10));
        _hint.Bounds = _pic.Bounds;
        _hint.Visible = _pic.Image == null || !ImageMode;
        _hint.Text = _last != null ? Lang.T("影片整好咗：撳「▶ 播放」睇。")
            : ImageMode ? Lang.T("撳「揀圖片…」揀一張圖做影片嘅第一格。") : Lang.T("輸入描述，再撳「🎬 整影片」。");
        _play.SetBounds(p, by, Ui.S(100), Ui.S(32));
        _save.SetBounds(_play.Right + g, by, Ui.S(120), Ui.S(32));
        _folder.SetBounds(_save.Right + g, by, Ui.S(120), Ui.S(32));
    }

    /// <summary>只列出做到而家揀咗嗰種（文字 / 圖）嘅已下載模型。</summary>
    private void FillModels()
    {
        var want = ImageMode ? LocalVideo.Kind.Image : LocalVideo.Kind.Text;
        var all = LocalVideo.Ready;
        var ready = all.Where(m => m.Kinds.HasFlag(want)).ToArray();
        string? cur = (_model.SelectedItem as ModelItem)?.M.Key;
        _model.Items.Clear();
        foreach (var m in ready) _model.Items.Add(new ModelItem(m));
        if (_model.Items.Count > 0)
            _model.SelectedIndex = Math.Max(0, ready.ToList().FindIndex(m => m.Key == cur));
        if (_cts == null)
        {
            _status.ForeColor = Ui.TextDim;
            _status.Text = ready.Length > 0 ? Lang.T("整影片要好耐（幾分鐘到幾十分鐘，視乎顯示卡）；期間部機會好忙。")
                : all.Length == 0 ? Lang.T("未有影片模型：撳「影片模型…」免費下載一個。")
                : ImageMode ? Lang.T("已下載嘅模型做唔到圖轉影片：撳「影片模型…」下載一個標明「圖轉影片」嘅。")
                : Lang.T("已下載嘅模型做唔到文字轉影片：撳「影片模型…」下載一個標明「文字轉影片」嘅。");
        }
        _go.Enabled = ready.Length > 0 || _cts != null;
        _go.Invalidate();
    }

    private sealed record ModelItem(LocalVideo.Model M) { public override string ToString() => M.Label; }

    private async Task GoAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; } // 整緊：撳多次 = 停
        if (_model.SelectedItem is not ModelItem mi) return;
        string prompt = _prompt.Text.Trim();
        bool img = ImageMode;
        if (img && _image == null) { _status.ForeColor = Ui.Danger; _status.Text = Lang.T("請先揀一張圖片。"); return; }
        if (prompt.Length == 0 && !img) { _status.ForeColor = Ui.Danger; _status.Text = Lang.T("請先輸入描述。"); return; }
        if (prompt.Length == 0) prompt = "the scene comes alive with natural, smooth motion";
        var (_, w, h) = Sizes[Math.Max(0, _size.SelectedIndex)];
        double sec = Lengths[Math.Max(0, _len.SelectedIndex)];
        _cts = new CancellationTokenSource();
        _go.Text = Lang.T("停止"); _go.Kind = BtnKind.Danger; _go.Invalidate();
        _bar.Visible = true; _bar.Style = ProgressBarStyle.Marquee;
        _status.ForeColor = Ui.TextDim; _status.Text = Lang.T("準備緊…（第一次要載入模型，會慢啲）");
        var sw = Stopwatch.StartNew();
        try
        {
            string file = await LocalVideo.GenerateAsync(mi.M, prompt, img ? _image : null, w, h, sec, (step, total, text) => BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (total > 0)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = Math.Clamp(step * 100 / total, 0, 100);
                    _status.Text = Lang.F("整緊… 第 {0} / {1} 步（已用 {2:0} 分鐘）", step, total, sw.Elapsed.TotalMinutes);
                }
                else if (text.Length > 0) _status.Text = text;
            }), _cts.Token);
            _last = file;
            _status.ForeColor = Ui.Success;
            _status.Text = Lang.F("✓ 整好咗（{0:0.0} 分鐘）：{1}", sw.Elapsed.TotalMinutes, file);
            _play.Enabled = _save.Enabled = true; _play.Invalidate(); _save.Invalidate();
        }
        catch (OperationCanceledException) { _status.Text = Lang.T("已停止。"); }
        catch (Exception ex) { _status.ForeColor = Ui.Danger; _status.Text = ex.Message.Replace("\n", " "); }
        finally
        {
            _cts = null;
            if (!IsDisposed)
            {
                _bar.Visible = false;
                _go.Text = Lang.T("🎬 整影片"); _go.Kind = BtnKind.Primary; _go.Invalidate();
                Place();
            }
        }
    }
}
