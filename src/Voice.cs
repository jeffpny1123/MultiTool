using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>語音對話設定：快捷鍵、中文 / 英文語音。存喺 data\ai\voice.txt。</summary>
internal static class VoiceSettings
{
    private static string FilePath => Path.Combine(AiProvider.WorkDir, "voice.txt");

    public static bool Enabled;
    public static Hotkey Hotkey;
    public static string ZhVoice = "";   // 空 = 自動（優先廣東話）
    public static string EnVoice = "";
    public static string Model = "turbo"; // Whisper 模型：base / small / turbo
    public static SpeechLang Speech = Lang.Current switch
    {
        Language.ZhHans => SpeechLang.Mandarin,
        Language.En => SpeechLang.Auto,
        _ => SpeechLang.Cantonese
    };

    static VoiceSettings() => Load();

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                switch (k)
                {
                    case "enabled": Enabled = v == "1"; break;
                    case "hotkey": Hotkey = Hotkey.Parse(v); break;
                    case "zh": ZhVoice = v; break;
                    case "en": EnVoice = v; break;
                    case "model": Model = v; break;
                    case "speech": if (Enum.TryParse<SpeechLang>(v, out var sl)) Speech = sl; break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllLines(FilePath, new[] { "enabled=" + (Enabled ? "1" : "0"), "hotkey=" + Hotkey.Serialize(), "zh=" + ZhVoice, "en=" + EnVoice, "model=" + Model, "speech=" + Speech });
        }
        catch (Exception ex) { AppPaths.LogError("儲存語音設定", ex); }
    }
}

/// <summary>Windows 內置語音（SAPI）朗讀。包括 OneCore 語音（例如廣東話 HiuGaai）。</summary>
internal static class Tts
{
    public sealed record Voice(string Id, string Name, int[] Lcids)
    {
        public bool IsChinese => Lcids.Any(l => (l & 0x3FF) == 0x04);
        public bool IsCantonese => Lcids.Contains(0x0C04);
        public bool IsEnglish => Lcids.Any(l => (l & 0x3FF) == 0x09);
    }

    private static dynamic? _sp;
    private static List<Voice>? _voices;
    private static readonly Dictionary<string, object> Tokens = new();

    private static dynamic? Sp
    {
        get
        {
            if (_sp != null) return _sp;
            try
            {
                var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t != null) _sp = Activator.CreateInstance(t);
            }
            catch (Exception ex) { AppPaths.LogError("開語音", ex); }
            return _sp;
        }
    }

    public static List<Voice> Voices
    {
        get
        {
            if (_voices != null) return _voices;
            _voices = new List<Voice>();
            void Add(dynamic tokens)
            {
                try
                {
                    int n = tokens.Count;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            dynamic tok = tokens.Item(i);
                            string id = tok.Id;
                            if (Tokens.ContainsKey(id)) continue;
                            string name = tok.GetDescription(0);
                            string lang = "";
                            try { lang = tok.GetAttribute("Language"); } catch { }
                            var lcids = lang.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => int.TryParse(x.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0)
                                .Where(v => v != 0).ToArray();
                            Tokens[id] = tok;
                            _voices.Add(new Voice(id, name, lcids));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            try { if (Sp != null) Add(Sp.GetVoices("", "")); } catch { }
            try
            {
                var t = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
                if (t != null)
                {
                    dynamic cat = Activator.CreateInstance(t)!;
                    cat.SetId(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices", false);
                    Add(cat.EnumerateTokens("", ""));
                }
            }
            catch { }
            return _voices;
        }
    }

    public static bool HasChinese(string text) => Regex.IsMatch(text, @"[一-鿿]");

    /// <summary>按文字語言揀語音：中文用設定（冇就廣東話 → 其他中文），英文同理。</summary>
    public static Voice? Pick(string text)
    {
        var vs = Voices;
        if (HasChinese(text))
            return vs.FirstOrDefault(v => v.Id == VoiceSettings.ZhVoice)
                   ?? vs.FirstOrDefault(v => v.IsCantonese) ?? vs.FirstOrDefault(v => v.IsChinese);
        return vs.FirstOrDefault(v => v.Id == VoiceSettings.EnVoice) ?? vs.FirstOrDefault(v => v.IsEnglish);
    }

    /// <summary>朗讀（非同步）；有語音就返 true。</summary>
    public static bool Speak(string text, Voice? voice = null)
    {
        var sp = Sp;
        if (sp == null) return false;
        try
        {
            voice ??= Pick(text);
            if (voice != null && Tokens.TryGetValue(voice.Id, out var tok)) sp.Voice = (dynamic)tok;
            sp.Speak(text, 1 | 2); // SVSFlagsAsync | SVSFPurgeBeforeSpeak
            return true;
        }
        catch (Exception ex) { AppPaths.LogError("朗讀", ex); return false; }
    }

    public static bool Done
    {
        get
        {
            try { return _sp == null || (bool)_sp.WaitUntilDone(0); } catch { return true; }
        }
    }

    public static void Stop()
    {
        try { _sp?.Speak("", 2); } catch { }
    }
}

/// <summary>
/// 語音對話：撳一下快捷鍵開始錄音 → 自動偵測講完（靜咗 1 秒）→ Whisper 本機轉文字 → 問 AI → 讀出 50 字內嘅回答。
/// 一開始 1 秒都冇講嘢就自動取消；錄音中再撳快捷鍵 = 即刻傳送。
/// 唔會彈 Windows 介面、唔會搶視窗、唔留紀錄。
/// </summary>
internal sealed class VoiceChat : Form
{
    private static VoiceChat? _open;
    private readonly Label _status, _question, _answer;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private WaveRecorder? _rec;
    private bool _recording, _speaking, _closing;
    // 自動偵測講嘢（音量）
    private const int FrameMs = 20, StartWaitMs = 1000, EndSilenceMs = 1000;
    private int _frames, _voiceRun, _lastVoice = -1;
    private bool _heard;
    private double _noise;
    private float _level;
    private DateTime _closeAt = DateTime.MaxValue;
    private Process? _proc;
    private readonly CancellationTokenSource _cts = new();

    protected override bool ShowWithoutActivation => true; // 唔好搶遊戲嘅焦點

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/;
            return cp;
        }
    }

    /// <summary>撳落快捷鍵：如果開緊就先取消，再開新一輪錄音。</summary>
    public static void Begin()
    {
        if (_open != null && !_open.IsDisposed)
        {
            if (_open._recording) { _open.SendNow(); return; } // 錄緊：即刻傳送
            _open.Finish();                                     // 答緊 / 讀緊：取消，開新一句
        }
        _open = new VoiceChat();
        _open.Show();
    }

    private VoiceChat()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // 唔好設 TopMost（佢會順手 activate 視窗）；置頂靠 CreateParams 嘅 WS_EX_TOPMOST
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Border;
        AutoScaleMode = AutoScaleMode.None;
        Padding = new Padding(1);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Size = new Size(Ui.S(420), Ui.S(220));
        Location = new Point(wa.Right - Width - Ui.S(20), wa.Top + Ui.S(20)); // 右上角

        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding(Ui.S(16), Ui.S(12), Ui.S(16), Ui.S(12)) };
        Controls.Add(inner);
        _status = new Label { Dock = DockStyle.Top, Height = Ui.S(24), ForeColor = Ui.TextDim, Font = Ui.F(9.5f) };
        _question = new Label { Dock = DockStyle.Top, Height = Ui.S(28), ForeColor = Ui.Text, Font = Ui.F(11f), AutoEllipsis = true };
        _answer = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Success, Font = Ui.F(11.5f, FontStyle.Bold), TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, Ui.S(6), 0, 0) };
        inner.Controls.Add(_answer);
        inner.Controls.Add(_question);
        inner.Controls.Add(_status);
        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => StartRecording();
    }

    private void StartRecording()
    {
        if (!Whisper.Ready)
        {
            Status(Lang.T("未下載語音辨識：去「AI 助手 → 語音設定」撳「下載」"), Ui.Danger);
            _closeAt = DateTime.Now.AddSeconds(5);
            _timer.Start();
            return;
        }
        // 一邊錄音一邊預熱：辨識程式同 Claude
        _ = WhisperServer.EnsureAsync();
        var prov0 = AiProvider.LoadCurrent();
        if (prov0 is ClaudeProvider && prov0.Find() is { } cexe) _ = Task.Run(() => ClaudeLive.Warm(cexe)); // 第一次要讀程式清單，唔好卡住介面
        try
        {
            _rec = new WaveRecorder();
            _rec.Start();
            _recording = true;
            Status(Lang.T("🎤 請講嘢…（講完停一秒就會自動傳送）"), Ui.Accent);
        }
        catch (Exception ex)
        {
            _rec?.Dispose();
            _rec = null;
            Status(ex.Message, Ui.Danger);
            _closeAt = DateTime.Now.AddSeconds(6);
        }
        _timer.Start();
    }

    /// <summary>
    /// 用音量判斷有冇講嘢：0 = 繼續聽、1 = 講完（靜咗 1 秒）、2 = 開頭 1 秒都冇講嘢。
    /// 背景噪音會自動適應（攞最靜嗰啲做底，聲音要明顯大過佢先算講嘢）。
    /// </summary>
    private int Listen()
    {
        foreach (float rms in _rec!.LevelsFrom(_frames))
        {
            _frames++;
            _level = rms;
            if (_noise == 0 || rms < _noise) _noise = rms;          // 底噪：跟住最靜嗰格
            else if (!_heard) _noise = _noise * 0.98 + rms * 0.02;   // 未講嘢之前慢慢適應
            double threshold = Math.Max(_noise * 3.0, 250);
            if (rms > threshold)
            {
                if (++_voiceRun >= 3) { _heard = true; _lastVoice = _frames; } // 連續 60 ms 有聲先當講嘢
            }
            else _voiceRun = 0;
        }
        int nowMs = _frames * FrameMs;
        _question.Text = new string('●', Math.Clamp((int)(_level / 250), 0, 20));
        if (nowMs > 30_000) return _heard ? 1 : 2;
        if (!_heard) return nowMs >= StartWaitMs ? 2 : 0;
        return (_frames - _lastVoice) * FrameMs >= EndSilenceMs ? 1 : 0;
    }

    /// <summary>停止錄音並傳送。</summary>
    private void SendNow()
    {
        if (!_recording) return;
        _recording = false;
        _question.Text = "";
        _ = ProcessAsync();
    }

    private void Status(string text, Color c)
    {
        _status.ForeColor = c;
        _status.Text = text;
    }

    private void Tick()
    {
        if (_closing) return;
        if (DateTime.Now >= _closeAt) { Finish(); return; }
        if (_recording)
        {
            switch (Listen())
            {
                case 0: return;                 // 繼續聽
                case 1: SendNow(); return;      // 講完
                default:                        // 一直冇講嘢
                    _recording = false;
                    Finish();
                    return;
            }
        }
        if (_speaking && Tts.Done)
        {
            _speaking = false;
            _closeAt = DateTime.Now.AddSeconds(3);
        }
    }

    private static string Prompt(string q) => ClaudeLive.SystemPrompt + "\nDo not use any tools.\n\n" + PcActions.NowTag + "\nQuestion: " + q;

    public const int MaxUnits = 50;

    /// <summary>硬性限制 50「字」：每個中文字算 1，每個英文 / 數字詞算 1（唔會將英文字切開一半）。</summary>
    private static string Limit(string a)
    {
        a = Regex.Replace(a, @"[*_`#>]", "").Replace("\r", " ").Replace("\n", " ").Trim();
        var sb = new StringBuilder();
        int count = 0;
        foreach (Match m in Regex.Matches(a, @"[\u4e00-\u9fff]|[A-Za-z0-9]+(?:['.\-][A-Za-z0-9]+)*|[^\u4e00-\u9fffA-Za-z0-9]"))
        {
            bool unit = m.Value.Length > 1 || char.IsLetterOrDigit(m.Value[0]);
            if (unit && count == MaxUnits) break;
            sb.Append(m.Value);
            if (unit) count++;
        }
        return sb.ToString().TrimEnd(' ', '，', ',', '、');
    }

    private async Task ProcessAsync()
    {
        string wav = Path.Combine(Whisper.Dir, "last.wav");
        try
        {
            var len = _rec!.Length;
            _rec.Stop(wav);
            _rec = null;
            if (len.TotalSeconds < 0.4) { Finish(); return; }

            Status(Lang.T("辨識中…"), Ui.TextDim);
            var sw = Stopwatch.StartNew();
            string q = await WhisperServer.TranscribeAsync(wav, len, _cts.Token);
            double tAsr = sw.Elapsed.TotalSeconds;
            if (_closing) return;
            q = Regex.Replace(q, @"\[[^\]]*\]|\([^)]*\)", "").Trim(); // [音樂]、(笑聲) 之類
            q = Regex.Replace(q, @"\s+", " ").Trim(); // 幾段字合返做一行
            if (q.Length == 0)
            {
                Status(Lang.T("聽唔到你講咩，再試一次。"), Ui.Danger);
                _closeAt = DateTime.Now.AddSeconds(3);
                return;
            }
            _question.Text = q;

            // 開網頁 / 開程式 / 問時間：程式自己處理，唔使等 AI
            // 開網頁 / 程式唔喺度判斷（語音有錯字），等 AI 回覆再判斷；呢度淨係處理問時間
            var quick = (PcActions.TryTime(q, out var timeReply), timeReply, new List<string>());
            if (quick.Item1)
            {
                if (_closing || IsDisposed) return;
                Status(Lang.F("⚡ 快速指令 · 辨識 {0:0.0} 秒", tAsr), Ui.TextDim);
                if (quick.Item3.Count > 0) _question.Text = q + "  →  " + string.Join("、", quick.Item3);
                _answer.Text = quick.Item2;
                _speaking = Tts.Speak(quick.Item2);
                if (!_speaking) _closeAt = DateTime.Now.AddSeconds(4);
                return;
            }

            var prov = AiProvider.LoadCurrent();
            string? exe = prov?.Find();
            if (prov == null || exe == null)
            {
                Status(Lang.T("未揀 AI：喺「AI 助手」撳右上角「帳號」揀"), Ui.Danger);
                _closeAt = DateTime.Now.AddSeconds(5);
                return;
            }

            Status(Lang.F("{0} 諗緊…", prov.Name), Ui.TextDim);
            sw.Restart();
            var ans = prov is ClaudeProvider
                ? await Task.Run(() => ClaudeLive.AskAsync(exe, q, _cts.Token))
                : await prov.AskAsync(exe, await Task.Run(() => Prompt(q)), null, p => _proc = p, quick: true);
            _proc = null;
            double tAi = sw.Elapsed.TotalSeconds;
            if (_closing || IsDisposed) return;
            if (ans.IsError)
            {
                Status(Lang.T("出錯：") + (ans.Text.Length > 60 ? ans.Text[..60] + "…" : ans.Text), Ui.Danger);
                _closeAt = DateTime.Now.AddSeconds(5);
                return;
            }
            var (clean, done) = PcActions.Run(ans.Text);
            // 冇用標籤（本地細模型成日咁）：睇 AI 回覆講「幫你開咗 X」就開 X（AI 已經改正咗語音辨識嘅錯字）；
            // 回覆都睇唔出先用返原句試
            if (done.Count == 0 && (PcActions.ReplyTarget(clean) ?? PcActions.OpenTarget(q)) is { } target)
            {
                var r = await PcActions.OpenAsync(target, async p =>
                {
                    var a2 = await prov.AskAsync(exe, p, null, x => _proc = x, quick: true);
                    return a2.IsError ? null : a2.Text;
                }, PcActions.WantsSite(clean + q));
                done = r.Done;
            }
            // 讀出嚟嘅：拎走連結、來源清單、網址
            clean = Regex.Replace(clean, @"\[([^\]]+)\]\([^)]*\)", "$1");
            int src = clean.IndexOf("Sources:", StringComparison.OrdinalIgnoreCase);
            if (src > 0) clean = clean[..src];
            clean = Regex.Replace(clean, @"https?://\S+", "").Trim();
            string a = Limit(clean);
            if (done.Count > 0) _question.Text = q + "  →  " + string.Join("、", done);
            Status(Lang.F("{0} · 辨識 {1:0.0} 秒 · AI {2:0.0} 秒", prov.Name, tAsr, tAi), Ui.TextDim);
            _answer.Text = a;
            _speaking = Tts.Speak(a);
            if (!_speaking) _closeAt = DateTime.Now.AddSeconds(5);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closing || IsDisposed) return;
            AppPaths.LogError("語音對話", ex);
            Status(ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message, Ui.Danger);
            _closeAt = DateTime.Now.AddSeconds(6);
        }
        finally { try { File.Delete(wav); } catch { } }
    }

    private void Finish()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        _cts.Cancel();
        Tts.Stop();
        try { _proc?.Kill(true); } catch { }
        _rec?.Dispose();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _timer.Dispose();
        _rec?.Dispose();
        if (_open == this) _open = null;
        base.OnFormClosed(e);
    }
}

/// <summary>AI 助手「設定」：語音對話（啟用、快捷鍵、辨識模型下載、語音）。</summary>
internal sealed class AiSettingsDialog : Form
{
    private CancellationTokenSource? _cts;

    public AiSettingsDialog()
    {
        Text = Lang.T("語音設定");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(500), Ui.S(460));
        FormClosing += (_, _) => _cts?.Cancel();

        int x0 = Ui.S(18), x1 = Ui.S(150), w = Ui.S(330), y = Ui.S(16);
        Label Lbl(string t, int yy) => new() { Text = t, AutoSize = false, Bounds = new Rectangle(x0, yy + Ui.S(4), Ui.S(130), Ui.S(24)), ForeColor = Ui.TextDim };

        Controls.Add(new Label
        {
            Text = Lang.T("語音對話"), Font = Ui.F(11f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = false,
            Bounds = new Rectangle(x0, y, Ui.S(130), Ui.S(26))
        });
        var enable = new CheckBox
        {
            Text = Lang.T("啟用"), Checked = VoiceSettings.Enabled, ForeColor = Ui.Text, AutoSize = true,
            Location = new Point(x1, y + Ui.S(2)), Cursor = Cursors.Hand
        };
        Controls.Add(enable);
        y += Ui.S(36);
        var rest = new List<Control>();

        Controls.Add(Lbl(Lang.T("快捷鍵"), y));
        var hk = new HotkeyBox { Hotkey = VoiceSettings.Hotkey, Bounds = new Rectangle(x1, y, Ui.S(160), Ui.S(28)), BackColor = Ui.Input, ForeColor = Ui.Text };
        hk.Changed += () => { VoiceSettings.Hotkey = hk.Hotkey; VoiceSettings.Save(); };
        Controls.Add(hk);
        rest.Add(hk);
        Controls.Add(new Label { Text = Lang.T("（撳一下開始講，停一秒自動傳送）"), ForeColor = Ui.TextFaint, Font = Ui.F(8.75f), AutoSize = true, Location = new Point(x1 + Ui.S(170), y + Ui.S(6)) });
        y += Ui.S(40);

        // ---- 語音辨識模型 ----
        Controls.Add(Lbl(Lang.T("辨識模型"), y));
        var model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(x1, y, Ui.S(220), Ui.S(28)), BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat };
        foreach (var m in Whisper.Models) model.Items.Add(m.Label);
        model.SelectedIndex = Array.IndexOf(Whisper.Models, Whisper.Current);
        Controls.Add(model);
        rest.Add(model);
        var dl = new FancyButton { Text = Lang.T("下載"), Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(x1 + Ui.S(230), y - Ui.S(2), Ui.S(100), Ui.S(32)) };
        var del = new FancyButton { Text = Lang.T("刪除"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Bounds = new Rectangle(x1 + Ui.S(230), y - Ui.S(2), Ui.S(100), Ui.S(32)), Visible = false };
        Controls.Add(del);
        rest.Add(del);
        Controls.Add(dl);
        rest.Add(dl);
        y += Ui.S(36);
        var mstate = new Label { AutoSize = false, Bounds = new Rectangle(x1, y, w, Ui.S(22)), Font = Ui.F(8.75f) };
        Controls.Add(mstate);
        var bar = new ProgressBar { Bounds = new Rectangle(x1, y + Ui.S(22), w, Ui.S(8)), Visible = false };
        Controls.Add(bar);
        y += Ui.S(40);

        void ModelState()
        {
            var m = Whisper.Models[Math.Max(0, model.SelectedIndex)];
            bool ok = Whisper.BinReady && Whisper.ModelReady(m);
            mstate.Text = ok ? Lang.T("✓ 已下載，可以用") : Lang.T("未下載（只需下載一次）");
            mstate.ForeColor = ok ? Ui.Success : Ui.TextFaint;
            dl.Visible = !ok;
            del.Visible = ok;
        }
        model.SelectedIndexChanged += (_, _) =>
        {
            VoiceSettings.Model = Whisper.Models[model.SelectedIndex].Key;
            VoiceSettings.Save();
            WhisperServer.Stop();
            ModelState();
        };
        dl.Click += async (_, _) =>
        {
            var m = Whisper.Models[Math.Max(0, model.SelectedIndex)];
            _cts = new CancellationTokenSource();
            dl.Enabled = model.Enabled = false;
            bar.Visible = true;
            try
            {
                await Whisper.InstallAsync(m, (text, frac) => BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    mstate.ForeColor = Ui.TextDim;
                    mstate.Text = text + (frac >= 0 ? $" {frac * 100:0}%" : "");
                    bar.Style = frac >= 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    if (frac >= 0) bar.Value = (int)Math.Clamp(frac * 100, 0, 100);
                }), _cts.Token);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                AppPaths.LogError("下載語音辨識", ex);
                MessageBox.Show(this, Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (IsDisposed) return;
            bar.Visible = false;
            dl.Enabled = model.Enabled = enable.Checked;
            ModelState();
        };
        del.Click += (_, _) =>
        {
            var m = Whisper.Models[Math.Max(0, model.SelectedIndex)];
            if (MessageBox.Show(this, Lang.F("刪除語音辨識模型 {0}？\n（之後要用就要重新下載）", m.Label), Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!Whisper.DeleteModel(m))
                MessageBox.Show(this, Lang.T("刪除失敗，請閂咗 MultiTool 再手動刪除 data\\ai\\whisper 入面嘅模型檔。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ModelState();
        };
        ModelState();

        Controls.Add(Lbl(Lang.T("辨識語言"), y));
        var speech = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(x1, y, Ui.S(220), Ui.S(28)), BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat };
        speech.Items.AddRange(new object[] { Lang.T("廣東話"), Lang.T("普通話"), "English", Lang.T("自動判斷") });
        speech.SelectedIndex = (int)VoiceSettings.Speech;
        speech.SelectedIndexChanged += (_, _) => { VoiceSettings.Speech = (SpeechLang)speech.SelectedIndex; VoiceSettings.Save(); };
        Controls.Add(speech);
        rest.Add(speech);
        y += Ui.S(40);

        // ---- 朗讀語音 ----
        var voices = Tts.Voices;
        ComboBox Combo(Func<Tts.Voice, bool> filter, string current, Action<string> set, int yy)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(x1, yy, w, Ui.S(28)), BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat };
            cb.Items.Add(Lang.T("自動"));
            var list = voices.Where(filter).ToList();
            foreach (var v in list) cb.Items.Add(v.Name);
            int idx = list.FindIndex(v => v.Id == current);
            cb.SelectedIndex = idx >= 0 ? idx + 1 : 0;
            cb.SelectedIndexChanged += (_, _) => { set(cb.SelectedIndex <= 0 ? "" : list[cb.SelectedIndex - 1].Id); VoiceSettings.Save(); };
            return cb;
        }

        Controls.Add(Lbl(Lang.T("中文語音"), y));
        var zh = Combo(v => v.IsChinese, VoiceSettings.ZhVoice, id => VoiceSettings.ZhVoice = id, y);
        Controls.Add(zh);
        rest.Add(zh);
        y += Ui.S(40);
        Controls.Add(Lbl(Lang.T("英文語音"), y));
        var en = Combo(v => v.IsEnglish, VoiceSettings.EnVoice, id => VoiceSettings.EnVoice = id, y);
        Controls.Add(en);
        rest.Add(en);
        y += Ui.S(40);

        var test = new FancyButton { Text = Lang.T("試聽"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Bounds = new Rectangle(x1, y, Ui.S(90), Ui.S(32)) };
        test.Click += (_, _) =>
        {
            if (!Tts.Speak(Lang.T("你好，我係你嘅 AI 助手。"))) MessageBox.Show(this, Lang.T("搵唔到可用嘅語音。"), Program.AppName);
        };
        Controls.Add(test);
        rest.Add(test);

        y += Ui.S(44);

        void Apply() { foreach (var c in rest) c.Enabled = enable.Checked; }
        enable.CheckedChanged += (_, _) => { VoiceSettings.Enabled = enable.Checked; VoiceSettings.Save(); Apply(); };
        Apply();

        Controls.Add(new Label
        {
            Text = Lang.T("撳一下快捷鍵就開始聽，講完停一秒會自動傳送（1 秒內冇講嘢就自動取消；錄音中再撳一下即刻傳送）。喺本機辨識（唔使上網、唔會彈出其他視窗），再問 AI，回答會讀出嚟（50 字內），對話唔會儲存。辨識模型越大越準，但越慢；廣東話建議用 large-v3-turbo。"),
            ForeColor = Ui.TextFaint, Font = Ui.F(8.75f), AutoSize = false, Bounds = new Rectangle(x0, y, Ui.S(464), Ui.S(60))
        });
        if (voices.Count == 0)
            Controls.Add(new Label
            {
                Text = Lang.T("（搵唔到 Windows 語音，請喺「設定 → 時間與語言 → 語音」加入。）"), ForeColor = Ui.Danger, Font = Ui.F(8.75f),
                AutoSize = false, Bounds = new Rectangle(x0, y + Ui.S(60), Ui.S(464), Ui.S(22))
            });
    }
}
