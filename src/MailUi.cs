using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Tools;
using MultiTool.UI;

namespace MultiTool.Mail;

// =====================================================================
//  電子郵件介面（似 Gmail）：左邊帳戶 + 資料夾，中間信件清單，右邊睇信
// =====================================================================

/// <summary>每個帳戶一條 IMAP 連線（排隊用）；斷咗自動重連一次。</summary>
internal static class MailPool
{
    private sealed class Conn
    {
        public ImapClient? C;
        public readonly SemaphoreSlim L = new(1, 1);
        public List<MailFolder>? Folders;
    }

    private static readonly ConcurrentDictionary<string, Conn> Conns = new();
    private static Conn Get(MailAccount a) => Conns.GetOrAdd(a.Id, _ => new Conn());

    /// <summary>
    /// 用呢個帳戶嘅連線做一件事。folder ≠ null 就先 SELECT（reselect = 就算揀緊都再揀一次，睇到新信）。
    /// </summary>
    public static async Task<T> UseAsync<T>(MailAccount a, string? folder, Func<ImapClient, CancellationToken, Task<T>> f,
        CancellationToken ct, int timeoutSec = 60, bool reselect = false)
    {
        var conn = Get(a);
        await conn.L.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                bool fresh = false;
                try
                {
                    if (conn.C == null || !conn.C.Connected)
                    {
                        conn.C?.Dispose();
                        conn.C = new ImapClient();
                        fresh = true;
                        await conn.C.ConnectAsync(a, cts.Token);
                    }
                    if (folder != null && (reselect || conn.C.Selected != folder)) await conn.C.SelectAsync(folder, cts.Token);
                    return await f(conn.C, cts.Token);
                }
                catch (InvalidOperationException) when (!fresh)
                {
                    throw; // 伺服器答 NO / BAD：條線仲用得
                }
                catch (Exception ex)
                {
                    // 其他錯誤（斷線、逾時、登入失敗）：條線唔再信得過
                    conn.C?.Dispose();
                    conn.C = null;
                    if (ct.IsCancellationRequested) throw;
                    bool netErr = ex is IOException or SocketException or ObjectDisposedException or AuthenticationException or OperationCanceledException;
                    if (netErr && !fresh && attempt == 0) continue; // 舊線斷咗：重連再試一次
                    if (ex is OperationCanceledException) throw new TimeoutException(Lang.T("郵件伺服器冇回應，請檢查網絡。"));
                    throw;
                }
            }
        }
        finally { conn.L.Release(); }
    }

    public static async Task<List<MailFolder>> FoldersAsync(MailAccount a, CancellationToken ct, bool refresh = false)
    {
        var conn = Get(a);
        if (!refresh && conn.Folders != null) return conn.Folders;
        var list = await UseAsync(a, null, (c, t) => c.ListAsync(t), ct);
        conn.Folders = list;
        return list;
    }

    public static MailFolder? Special(MailAccount a, string kind) =>
        Get(a).Folders?.FirstOrDefault(f => f.Special == kind && !f.NoSelect);

    /// <summary>帳戶移除 / 重新登入：閂咗條線。</summary>
    public static void Drop(MailAccount a)
    {
        if (Conns.TryRemove(a.Id, out var c)) try { c.C?.Dispose(); } catch { }
    }
}

/// <summary>背景每兩分鐘睇下收件匣有冇新信：有就彈通知。</summary>
internal static class MailWatch
{
    private static System.Threading.Timer? _t;
    private static readonly ConcurrentDictionary<string, uint> Last = new();   // 每個帳戶見過最大嘅未讀 UID
    public static readonly ConcurrentDictionary<string, int> Unread = new();   // 收件匣未讀數
    private static int _busy;

    /// <summary>有新信（任何 thread）。</summary>
    public static event Action<MailAccount>? NewMail;
    /// <summary>未讀數有變（任何 thread）。</summary>
    public static event Action? UnreadChanged;

    public static void Start()
    {
        if (_t != null) return;
        _t = new System.Threading.Timer(_ => _ = TickAsync(), null, 4000, 120_000);
    }

    public static void Stop()
    {
        _t?.Dispose();
        _t = null;
    }

    public static void Kick() => _ = TickAsync();

    public static void SetUnread(MailAccount a, int n)
    {
        Unread[a.Id] = Math.Max(0, n);
        UnreadChanged?.Invoke();
    }

    private static async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var a in MailStore.All.ToList())
            {
                try
                {
                    var uids = await MailPool.UseAsync(a, "INBOX", (c, t) => c.SearchAsync("UNSEEN", t), CancellationToken.None, 60, reselect: true);
                    Unread[a.Id] = uids.Count;
                    uint max = uids.Count > 0 ? uids[^1] : 0;
                    if (Last.TryGetValue(a.Id, out var prev))
                    {
                        var fresh = uids.Where(u => u > prev).ToList();
                        if (fresh.Count > 0)
                        {
                            if (a.Notify)
                            {
                                var hs = await MailPool.UseAsync(a, "INBOX", (c, t) => c.FetchHeadersAsync(fresh.TakeLast(3), t), CancellationToken.None);
                                string body = string.Join("\n", hs.OrderByDescending(h => h.Uid).Select(h => $"{(h.From.Length > 0 ? h.From : h.FromAddr)}：{h.Subject}"));
                                Scripts.Notifier.Send(fresh.Count == 1 ? Lang.F("新郵件（{0}）", a.Email) : Lang.F("{0} 封新郵件（{1}）", fresh.Count, a.Email), body, true, false);
                            }
                            NewMail?.Invoke(a);
                        }
                        Last[a.Id] = Math.Max(prev, max);
                    }
                    else Last[a.Id] = max; // 第一次：記低，唔通知
                }
                catch (Exception ex) { AppPaths.LogError("檢查新郵件 " + a.Email, ex); }
            }
            UnreadChanged?.Invoke();
        }
        finally { _busy = 0; }
    }
}

/// <summary>叫 AI 幫手（用「AI 助手」頁揀咗嘅 Claude / ChatGPT / Gemini）。</summary>
internal static class MailAi
{
    public static string UiLang => Lang.Current switch
    {
        Language.En => "English",
        Language.ZhHans => "简体中文",
        _ => "繁體中文書面語（香港用字）"
    };

    /// <summary>AI 對話回答：用家用咩語言就用咩（廣東話就廣東話、書面語就書面語、英文就英文）。</summary>
    public const string ReplyRule = "跟用戶用嘅語言簡短回答：用戶講廣東話口語就用廣東話，用書面語就用書面語，用英文就用英文。";

    /// <summary>AI 寫出嚟、會喺介面顯示嘅文字（插件介面、通知內容等）一律用書面語。</summary>
    public static string UiTextRule => Lang.Current switch
    {
        Language.En => "Any text shown in the user interface (labels, buttons, messages, notifications) must be in English.",
        Language.ZhHans => "所有會在介面顯示的文字（標籤、按鈕、訊息、通知）一律用简体中文书面语。",
        _ => "所有會喺介面顯示嘅文字（標籤、按鈕、訊息、通知內容）一律用繁體中文書面語（香港用字），唔好用廣東話口語（例如唔好用「嘅、咗、唔、係、喺、嚟、佢、冇、撳、睇」）。"
    };

    public static async Task<string> AskAsync(string prompt)
    {
        var prov = Ai.AiProvider.LoadCurrent() ?? throw new InvalidOperationException(Lang.T("未揀 AI：請去「AI 助手」頁登入 Claude / ChatGPT / Gemini。"));
        var exe = prov.Find() ?? throw new InvalidOperationException(Lang.F("搵唔到 {0}：請去「AI 助手」頁安裝。", prov.ToolName));
        var a = await prov.AskAsync(exe, prompt, null, _ => { }, quick: true);
        if (a.IsError) throw new InvalidOperationException(a.Text);
        return a.Text.Trim();
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
}

// ---------------------------------------------------------------------
//  左邊資料夾清單
// ---------------------------------------------------------------------
internal sealed class FolderList : Control
{
    public sealed record Item(MailFolder Folder, string Glyph, string Name);
    public readonly List<Item> Items = new();
    public MailFolder? Selected;
    public readonly Dictionary<string, int> Counts = new();
    public event Action<MailFolder>? Picked;
    private int _hover = -1, _scroll;
    private int RowH => Ui.S(36);

    public FolderList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bg;
        Cursor = Cursors.Hand;
    }

    private int MaxScroll => Math.Max(0, Items.Count * RowH - Height);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - e.Delta / 120 * RowH));
        Invalidate();
    }

    private int At(Point p) { int i = (p.Y + _scroll) / RowH; return i >= 0 && i < Items.Count ? i : -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = At(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = At(e.Location);
        if (i < 0 || e.Button != MouseButtons.Left) return;
        Selected = Items[i].Folder;
        Invalidate();
        Picked?.Invoke(Items[i].Folder);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        for (int i = 0; i < Items.Count; i++)
        {
            int y = i * RowH - _scroll;
            if (y + RowH < 0 || y > Height) continue;
            var it = Items[i];
            bool sel = Selected != null && it.Folder.Name == Selected.Name;
            var r = new RectangleF(Ui.S(4), y + Ui.S(2), Width - Ui.S(8), RowH - Ui.S(4));
            if (sel || i == _hover)
            {
                using var path = Ui.Round(r, Ui.S(8f));
                using var b = new SolidBrush(sel ? Ui.Alpha(Ui.Accent, 55) : Ui.CardHi);
                g.FillPath(b, path);
            }
            Counts.TryGetValue(it.Folder.Name, out int n);
            var fc = sel ? Ui.Text : Ui.TextDim;
            var font = Ui.F(9.75f, sel || n > 0 ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, it.Glyph, Ui.F(10.5f), new Rectangle(Ui.S(12), y, Ui.S(24), RowH), sel ? Ui.Accent : Ui.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int cw = n > 0 ? Ui.S(40) : 0;
            TextRenderer.DrawText(g, it.Name, font, new Rectangle(Ui.S(42), y, Width - Ui.S(52) - cw, RowH), fc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (n > 0)
                TextRenderer.DrawText(g, n > 999 ? "999+" : n.ToString(), Ui.F(8.75f, FontStyle.Bold), new Rectangle(Width - Ui.S(12) - cw, y, cw, RowH), fc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
    }
}

// ---------------------------------------------------------------------
//  中間信件清單（自己畫，一次載入 50 封，碌到底再載）
// ---------------------------------------------------------------------
internal sealed class MailList : Control
{
    public List<MailHeader> Items = new();
    public MailHeader? Selected;
    public bool HasMore, Loading, ShowTo, ShowAcct;
    public string Empty = "";
    public event Action<MailHeader>? Picked;
    public event Action<MailHeader>? Starred;
    public event Action<MailHeader, Point>? Menu;
    public event Action? NeedMore;
    private int _scroll, _hover = -1;
    private bool _dragThumb;
    private int _dragY, _dragScroll;
    private bool Wide => Width >= Ui.S(720);          // 闊：好似 Gmail 一行過（星號 | 寄件人 | 主旨 | 時間）
    private int RowH => Wide ? Ui.S(42) : Ui.S(62);

    public MailList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = Ui.Card;
        TabStop = true;
    }

    public void Reset() { _scroll = 0; _hover = -1; Invalidate(); }

    private int ContentH => Items.Count * RowH + (HasMore || Loading ? RowH : 0);
    private int MaxScroll => Math.Max(0, ContentH - Height);

    private void ScrollTo(int v)
    {
        _scroll = Math.Max(0, Math.Min(MaxScroll, v));
        Invalidate();
        CheckMore();
    }

    public void CheckMore()
    {
        if (HasMore && !Loading && _scroll + Height >= Items.Count * RowH - RowH * 2) NeedMore?.Invoke();
    }

    public void EnsureVisible(MailHeader h)
    {
        int i = Items.IndexOf(h);
        if (i < 0) return;
        if (i * RowH < _scroll) ScrollTo(i * RowH);
        else if ((i + 1) * RowH > _scroll + Height) ScrollTo((i + 1) * RowH - Height);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Items.Count == 0) return;
        int i = Selected != null ? Items.IndexOf(Selected) : -1;
        if (e.KeyCode == Keys.Down && i + 1 < Items.Count) Pick(Items[i + 1], false);
        else if (e.KeyCode == Keys.Up && i > 0) Pick(Items[i - 1], false);
        else if (e.KeyCode == Keys.Down && i < 0) Pick(Items[0], false);
        else if (e.KeyCode == Keys.Enter && Selected != null) Pick(Selected, true);
    }

    private void Pick(MailHeader h, bool open = true)
    {
        Selected = h;
        EnsureVisible(h);
        Invalidate();
        if (open) Picked?.Invoke(h);
    }

    protected override void OnMouseWheel(MouseEventArgs e) => ScrollTo(_scroll - e.Delta / 120 * RowH);

    private int At(Point p) { int i = (p.Y + _scroll) / RowH; return p.Y >= 0 && i >= 0 && i < Items.Count ? i : -1; }
    private Rectangle StarRect(int y) => Wide ? new(Ui.S(10), y + (RowH - Ui.S(24)) / 2, Ui.S(26), Ui.S(24)) : new(Width - Ui.S(40), y + RowH - Ui.S(30), Ui.S(26), Ui.S(24));

    private Rectangle Thumb()
    {
        if (ContentH <= Height || Height <= 0) return Rectangle.Empty;
        int h = Math.Max(Ui.S(30), Height * Height / ContentH);
        int y = (int)((long)(Height - h) * _scroll / Math.Max(1, MaxScroll));
        return new Rectangle(Width - Ui.S(8), y, Ui.S(6), h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var th = Thumb();
        if (e.Button == MouseButtons.Left && th.Width > 0 && e.X >= Width - Ui.S(12))
        {
            _dragThumb = true; _dragY = e.Y; _dragScroll = _scroll;
            if (!th.Contains(e.Location)) ScrollTo((int)((long)e.Y * MaxScroll / Math.Max(1, Height)));
            _dragScroll = _scroll;
            return;
        }
        int i = At(e.Location);
        if (i < 0) return;
        var h = Items[i];
        if (e.Button == MouseButtons.Right) { Selected = h; Invalidate(); Menu?.Invoke(h, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (StarRect(i * RowH - _scroll).Contains(e.Location)) { Starred?.Invoke(h); return; }
        Pick(h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragThumb)
        {
            var th = Thumb();
            int track = Height - th.Height;
            if (track > 0) ScrollTo(_dragScroll + (int)((long)(e.Y - _dragY) * MaxScroll / track));
            return;
        }
        int i = At(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragThumb = false;
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(_scroll); }

    public static string When(DateTime d)
    {
        if (d == DateTime.MinValue) return "";
        if (d.Date == DateTime.Today) return d.ToString("HH:mm");
        if (d.Year == DateTime.Today.Year) return d.ToString("MM-dd");
        return d.ToString("yyyy-MM-dd");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        const TextFormatFlags L = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (Items.Count == 0 && !Loading)
        {
            TextRenderer.DrawText(g, Empty, Ui.F(9.75f), new Rectangle(Ui.S(20), Ui.S(20), Width - Ui.S(40), Ui.S(200)), Ui.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        using var line = new Pen(Ui.Border);
        for (int i = 0; i < Items.Count; i++)
        {
            int y = i * RowH - _scroll;
            if (y + RowH < 0) continue;
            if (y > Height) break;
            var h = Items[i];
            bool sel = h == Selected;
            if (sel || i == _hover)
                using (var b = new SolidBrush(sel ? Ui.Alpha(Ui.Accent, 45) : Ui.CardHi)) g.FillRectangle(b, 0, y, Width, RowH);
            if (!h.Seen) using (var b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, 0, y + Ui.S(8), Ui.S(3), RowH - Ui.S(16));
            g.DrawLine(line, Ui.S(12), y + RowH - 1, Width - Ui.S(12), y + RowH - 1);
            if (Wide) { PaintWide(g, h, y, L); continue; }
            var bold = Ui.F(9.75f, h.Seen ? FontStyle.Regular : FontStyle.Bold);
            string who = ShowTo ? Lang.T("致：") + FirstName(h.To) : (h.From.Length > 0 ? h.From : h.FromAddr);
            string when = When(h.Date);
            if (ShowAcct && h.Acct != null) when = AcctTag(h.Acct) + "  ·  " + when;
            int ww = TextRenderer.MeasureText(when, Ui.F(8.75f)).Width;
            int x0 = Ui.S(16), right = Width - Ui.S(14);
            TextRenderer.DrawText(g, who, bold, new Rectangle(x0, y + Ui.S(8), right - x0 - ww - Ui.S(8), Ui.S(22)), h.Seen ? Ui.TextDim : Ui.Text, L);
            TextRenderer.DrawText(g, when, Ui.F(8.75f, h.Seen ? FontStyle.Regular : FontStyle.Bold), new Rectangle(right - ww, y + Ui.S(8), ww, Ui.S(22)),
                h.Seen ? Ui.TextFaint : Ui.Accent, L | TextFormatFlags.Right);
            string subj = (h.HasAttachment ? "📎 " : "") + (h.Answered ? "↩ " : "") + (h.Subject.Length > 0 ? h.Subject : Lang.T("（冇主旨）"));
            TextRenderer.DrawText(g, subj, Ui.F(9.25f, h.Seen ? FontStyle.Regular : FontStyle.Bold), new Rectangle(x0, y + Ui.S(32), right - x0 - Ui.S(34), Ui.S(22)),
                h.Seen ? Ui.TextFaint : Ui.TextDim, L);
            var sr = StarRect(y);
            TextRenderer.DrawText(g, h.Flagged ? "★" : "☆", Ui.F(11f), sr, h.Flagged ? Ui.Hex(0xF5B301) : Ui.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
        if (HasMore || Loading)
        {
            int y = Items.Count * RowH - _scroll;
            TextRenderer.DrawText(g, Loading ? Lang.T("載入緊…") : Lang.T("載入更多…"), Ui.F(9.25f), new Rectangle(0, y, Width, RowH), Ui.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        var th = Thumb();
        if (th.Width > 0)
        {
            using var path = Ui.Round(th, Ui.S(3f));
            using var b = new SolidBrush(Ui.Alpha(Ui.TextFaint, 120));
            g.FillPath(b, path);
        }
    }

    private void PaintWide(Graphics g, MailHeader h, int y, TextFormatFlags L)
    {
        var sr = StarRect(y);
        TextRenderer.DrawText(g, h.Flagged ? "★" : "☆", Ui.F(11f), sr, h.Flagged ? Ui.Hex(0xF5B301) : Ui.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        var style = h.Seen ? FontStyle.Regular : FontStyle.Bold;
        string who = ShowTo ? Lang.T("致：") + FirstName(h.To) : (h.From.Length > 0 ? h.From : h.FromAddr);
        string when = When(h.Date);
        var wf = Ui.F(8.75f, style);
        int ww = TextRenderer.MeasureText(when, wf).Width;
        int right = Width - Ui.S(16);
        TextRenderer.DrawText(g, when, wf, new Rectangle(right - ww, y, ww, RowH), h.Seen ? Ui.TextFaint : Ui.Accent, L | TextFormatFlags.Right);
        int subjRight = right - ww - Ui.S(16);
        if (ShowAcct && h.Acct != null)
        {
            // 邊個帳戶：時間左邊一個細標籤
            string tag = AcctTag(h.Acct);
            var tf = Ui.F(8.25f);
            int tw = TextRenderer.MeasureText(tag, tf).Width + Ui.S(12);
            var tr = new Rectangle(subjRight - tw, y + (RowH - Ui.S(22)) / 2, tw, Ui.S(22));
            using (var path = Ui.Round(tr, Ui.S(6f)))
            using (var b = new SolidBrush(Ui.Alpha(TagColor(h.Acct), 60))) g.FillPath(b, path);
            TextRenderer.DrawText(g, tag, tf, tr, Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            subjRight = tr.Left - Ui.S(12);
        }
        int x = sr.Right + Ui.S(10);
        int whoW = Math.Min(Ui.S(220), Width / 4);
        TextRenderer.DrawText(g, who, Ui.F(9.75f, style), new Rectangle(x, y, whoW, RowH), h.Seen ? Ui.TextDim : Ui.Text, L);
        x += whoW + Ui.S(16);
        string subj = (h.HasAttachment ? "📎 " : "") + (h.Answered ? "↩ " : "") + (h.Subject.Length > 0 ? h.Subject : Lang.T("（冇主旨）"));
        TextRenderer.DrawText(g, subj, Ui.F(9.5f, style), new Rectangle(x, y, Math.Max(0, subjRight - x), RowH), h.Seen ? Ui.TextFaint : Ui.Text, L);
    }

    /// <summary>帳戶標籤：電郵 @ 前面嗰部分。</summary>
    private static string AcctTag(MailAccount a) => a.Email.Contains('@') ? a.Email[..a.Email.IndexOf('@')] : a.Email;

    private static readonly Color[] TagColors = { Ui.Hex(0x4F8CFF), Ui.Hex(0xEA4335), Ui.Hex(0x2BB673), Ui.Hex(0xF5B301), Ui.Hex(0x8C6CFF), Ui.Hex(0x00ACC1) };
    private static Color TagColor(MailAccount a)
    {
        int i = MailStore.All.IndexOf(a);
        return TagColors[(i < 0 ? 0 : i) % TagColors.Length];
    }

    private static string FirstName(string list)
    {
        var a = Mime.SplitAddrs(list);
        if (a.Count == 0) return "";
        var (n, _) = Mime.ParseAddress(a[0]);
        return a.Count > 1 ? n + $" +{a.Count - 1}" : n;
    }
}

// ---------------------------------------------------------------------
//  主頁
// ---------------------------------------------------------------------
internal sealed class MailPage : Panel
{
    public static Control Build() => new MailPage();

    private MailAccount? _acct;
    private MailFolder? _folder;
    private bool _all;                                   // 「全部帳戶」：所有帳戶嘅收件匣合併
    private readonly Dictionary<string, (List<uint> Uids, int Next)> _multi = new();
    private static readonly MailFolder AllInbox = new() { Name = "*all*", Display = "*all*", Special = "inbox" };
    private MailAccount? AcctOf(MailHeader? h) => h?.Acct ?? _acct;
    private static bool IsInbox(MailHeader h) => h.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
    private List<uint> _uids = new();          // 而家清單全部 UID（新嘅喺前）
    private int _next;                         // _uids 載入咗去邊
    private readonly List<MailHeader> _items = new();
    private MailHeader? _open;
    private MailMessage? _msg;
    private CancellationTokenSource? _listCts, _openCts;
    private string _search = "";
    private static readonly Dictionary<string, MailMessage> Cache = new();
    private static readonly List<string> CacheOrder = new();

    private readonly FancyButton _compose, _acctBtn;
    private readonly FolderList _folders = new();
    private readonly TextBox _searchBox = new();
    private readonly Label _status;
    private readonly MailList _list = new();
    private readonly Panel _read = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Label _subject, _meta, _blank;
    private readonly FlowLayoutPanel _bar, _attach;
    private readonly Panel _aiBox, _sortBox, _listHost;
    private readonly Label _aiTitle, _sortTitle;
    private readonly TextBox _sortText;
    private readonly TextBox _aiText;
    private readonly Panel _webHost = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly MailView _view = new() { Dock = DockStyle.Fill };
    private readonly Panel _imgBar;              // 「已封鎖遠端圖片」提示
    private bool _showImgs;                      // 呢封信撳咗「顯示圖片」
    private FancyButton? _starBtn, _archBtn;
    private readonly ToolTip _tip = new();

    private static string LastFile => Path.Combine(MailStore.Dir, "last.txt");

    private MailPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;

        // ---- 左邊：撰寫 / 帳戶 / 資料夾 ----
        var side = new Panel { Dock = DockStyle.Left, Width = Ui.S(230), BackColor = Ui.Bg, Padding = new Padding(Ui.S(14), Ui.S(14), Ui.S(6), Ui.S(10)) };
        _compose = new FancyButton { Text = Lang.T("✎  撰寫"), Kind = BtnKind.Primary, Font = Ui.F(10.5f, FontStyle.Bold), Dock = DockStyle.Top, Height = Ui.S(46) };
        _compose.Click += (_, _) => Compose(null);
        _acctBtn = new FancyButton { Text = "", Kind = BtnKind.Ghost, Font = Ui.F(9f), Dock = DockStyle.Top, Height = Ui.S(34) };
        _acctBtn.Click += (_, _) => AccountMenu();
        _tip.SetToolTip(_acctBtn, Lang.T("切換帳戶、加入帳戶、帳戶設定"));
        var gap1 = new Panel { Dock = DockStyle.Top, Height = Ui.S(10), BackColor = Ui.Bg };
        var gap2 = new Panel { Dock = DockStyle.Top, Height = Ui.S(8), BackColor = Ui.Bg };
        _folders.Dock = DockStyle.Fill;
        _folders.Picked += f => { _folder = f; _search = ""; _searchBox.Text = ""; LoadList(); };
        side.Controls.Add(_folders);
        side.Controls.Add(gap2);
        side.Controls.Add(_acctBtn);
        side.Controls.Add(gap1);
        side.Controls.Add(_compose);

        // ---- 頂：搜尋 + 重新整理 + AI 整理 ----
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(Ui.S(6), 0, Ui.S(16), Ui.S(12)) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(60), BackColor = Ui.Bg, WrapContents = false, Padding = new Padding(0, Ui.S(14), 0, 0) };
        _searchBox.PlaceholderText = Lang.T("搜尋郵件（撳 Enter）");
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _search = _searchBox.Text.Trim(); LoadList(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _searchBox.Text = ""; if (_search.Length > 0) { _search = ""; LoadList(); } }
        };
        var sh = new InputHost(_searchBox, Ui.S(360)) { Margin = new Padding(0, 0, Ui.S(8), 0) };
        var refresh = TUi.Btn(Lang.T("⟳ 重新整理"), () => { LoadList(); MailWatch.Kick(); });
        refresh.Margin = new Padding(0, 0, Ui.S(8), 0);
        var aiSort = TUi.Btn(Lang.T("✦ AI 整理"), () => _ = AiSortAsync());
        aiSort.Margin = new Padding(0, 0, Ui.S(12), 0);
        _tip.SetToolTip(aiSort, Lang.T("叫 AI 睇清單入面最近嘅信，分做「要處理」「遲啲睇」「廣告 / 通知」"));
        _status = Ui.Text_("", Ui.TextFaint, Ui.F(9f), new Padding(0, Ui.S(9), 0, 0));
        top.Controls.AddRange(new Control[] { sh, refresh, aiSort, _status });

        // ---- 中間清單 ----
        _listHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        var listHost = _listHost;
        _list.Dock = DockStyle.Fill;
        _list.Items = _items;
        _list.Picked += h => _ = OpenAsync(h);
        _list.Starred += h => _ = ToggleStarAsync(h);
        _list.NeedMore += () => _ = LoadMoreAsync(_listCts);
        _list.Menu += RowMenu;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _list.Selected != null) _ = DeleteAsync(_list.Selected);
        };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_sortBox = AiPanel(out _sortText, out _sortTitle, Ui.S(220)));

        // ---- 右邊睇信 ----
        _subject = Ui.Text_("", Ui.Text, Ui.F(13f, FontStyle.Bold), new Padding(0));
        _subject.AutoSize = false; _subject.Dock = DockStyle.Top; _subject.Height = Ui.S(40); _subject.TextAlign = ContentAlignment.BottomLeft; _subject.AutoEllipsis = true; _subject.UseMnemonic = false;
        _meta = Ui.Text_("", Ui.TextDim, Ui.F(9.25f), new Padding(0));
        _meta.AutoSize = false; _meta.Dock = DockStyle.Top; _meta.Height = Ui.S(44); _meta.UseMnemonic = false;
        _bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Bg, WrapContents = true, Padding = new Padding(0, 0, 0, Ui.S(2)) };
        _attach = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Bg, WrapContents = true };
        _aiBox = AiPanel(out _aiText, out _aiTitle, Ui.S(170));
        _blank = Ui.Text_(Lang.T("揀一封信嚟睇"), Ui.TextFaint, Ui.F(10.5f), new Padding(0));
        _blank.AutoSize = false; _blank.Dock = DockStyle.Fill; _blank.TextAlign = ContentAlignment.MiddleCenter; _blank.BackColor = Ui.Bg;

        // 由上至下：工具列、主旨、寄件人、附件、AI、信件內容（後加嘅 Dock 喺外面）
        _read.Visible = false;
        _read.Controls.Add(_webHost);
        _imgBar = new Panel { Dock = DockStyle.Top, Height = Ui.S(42), BackColor = Ui.Bg, Visible = false, Padding = new Padding(0, Ui.S(4), 0, Ui.S(6)) };
        var imgFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Alpha(Ui.Accent, 30), WrapContents = false, Padding = new Padding(Ui.S(10), 0, 0, 0) };
        var imgLbl = Ui.Text_(Lang.T("🛡 為咗私隱，已經擋咗遠端圖片（寄件人可以用圖片知道你幾時開過封信）"), Ui.TextDim, Ui.F(9f), new Padding(0, Ui.S(8), Ui.S(10), 0));
        var imgShow = TUi.Btn(Lang.T("顯示圖片"), () => { _showImgs = true; Rerender(); });
        var imgTrust = TUi.Btn(Lang.T("一律顯示呢個寄件人嘅圖片"), () =>
        {
            if (_msg != null) MailStore.TrustImages(_msg.FromAddr);
            Rerender();
        });
        foreach (var b in new[] { imgShow, imgTrust }) { b.Height = Ui.S(28); b.Margin = new Padding(0, Ui.S(2), Ui.S(6), 0); }
        imgFlow.Controls.AddRange(new Control[] { imgLbl, imgShow, imgTrust });
        _imgBar.Controls.Add(imgFlow);
        _read.Controls.Add(_imgBar);
        _read.Controls.Add(_aiBox);
        _read.Controls.Add(_attach);
        _read.Controls.Add(_meta);
        _read.Controls.Add(_subject);
        _read.Controls.Add(_bar);
        _webHost.Controls.Add(_view);
        _webHost.Controls.Add(_blank);
        _view.LinkClicked += OpenLink;
        _view.EscPressed += () => { if (_read.Visible) ShowBlank(); };
        _blank.BringToFront();
        BuildBar();

        // 清單同睇信共用同一個位：好似 Gmail，開咗信就成個畫面睇信，撳「← 返回」返清單
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        content.Controls.Add(_read);
        content.Controls.Add(listHost);
        main.Controls.Add(content);
        main.Controls.Add(top);
        Controls.Add(main);
        Controls.Add(side);

        MailStore.Changed += OnAccounts;
        MailWatch.NewMail += OnNewMail;
        MailWatch.UnreadChanged += OnUnread;
        Disposed += (_, _) => { MailStore.Changed -= OnAccounts; MailWatch.NewMail -= OnNewMail; MailWatch.UnreadChanged -= OnUnread; };
        MailWatch.Start();

        string last = "";
        try { if (File.Exists(LastFile)) last = File.ReadAllText(LastFile).Trim(); } catch { }
        var first = MailStore.All.FirstOrDefault(a => a.Id == last) ?? MailStore.All.FirstOrDefault();
        if (last == "*all*" && MailStore.All.Count > 1) SelectAll();
        else if (first != null) SelectAccount(first);
        else NoAccount();
    }

    // ================= 帳戶 =================

    private void NoAccount()
    {
        _all = false;
        _acct = null;
        _folder = null;
        _acctBtn.Text = Lang.T("+ 加入帳戶");
        _folders.Items.Clear();
        _folders.Invalidate();
        _items.Clear();
        _list.HasMore = _list.Loading = false;
        _list.Empty = Lang.T("未有郵件帳戶。\n撳左邊「+ 加入帳戶」加 Gmail 或者 Outlook / Hotmail。");
        _list.Reset();
        ShowBlank();
    }

    private void OnAccounts()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_all && MailStore.All.Count > 0) return;
            if (_acct != null && MailStore.All.Contains(_acct)) { _acctBtn.Text = AcctLabel(_acct); return; }
            var a = MailStore.All.FirstOrDefault();
            if (a != null) SelectAccount(a); else NoAccount();
        });
    }

    private static string AcctLabel(MailAccount a) => a.Email + "  ▾";

    private void AccountMenu()
    {
        var m = TUi.Menu(this);
        if (MailStore.All.Count > 1)
        {
            int total = MailStore.All.Sum(x => MailWatch.Unread.TryGetValue(x.Id, out int k) ? k : 0);
            var allIt = new ToolStripMenuItem(Lang.T("📥 全部帳戶") + (total > 0 ? $"  ({total})" : "")) { Checked = _all };
            allIt.Click += (_, _) => SelectAll();
            m.Items.Add(allIt);
            m.Items.Add(new ToolStripSeparator());
        }
        foreach (var a in MailStore.All)
        {
            var acc = a;
            MailWatch.Unread.TryGetValue(a.Id, out int n);
            var it = new ToolStripMenuItem(a.Email + (n > 0 ? $"  ({n})" : "")) { Checked = !_all && a == _acct };
            it.Click += (_, _) => SelectAccount(acc);
            m.Items.Add(it);
        }
        if (MailStore.All.Count > 0) m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Lang.T("+ 加入帳戶…"), null, (_, _) => AddAccount());
        if (_acct != null && !_all)
        {
            var cur = _acct;
            m.Items.Add(new ToolStripSeparator());
            var notify = new ToolStripMenuItem(Lang.T("新郵件通知")) { Checked = cur.Notify };
            notify.Click += (_, _) => { cur.Notify = !cur.Notify; MailStore.Save(); };
            m.Items.Add(notify);
            m.Items.Add(Lang.T("寄件人名稱…"), null, (_, _) =>
            {
                var s = AskText.Show(FindForm(), Lang.T("寄件人名稱"), Lang.T("收件人會見到嘅名（可以留空）"), cur.Name);
                if (s == null) return;
                cur.Name = s.Trim();
                MailStore.Save();
            });
            m.Items.Add(Lang.T("移除呢個帳戶"), null, (_, _) =>
            {
                if (MessageBox.Show(FindForm(), Lang.F("移除 {0}？（伺服器上面嘅信唔會刪）", cur.Email), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                MailPool.Drop(cur);
                MailStore.Remove(cur);
            });
        }
        m.Show(_acctBtn, new Point(0, _acctBtn.Height));
    }

    private void AddAccount()
    {
        using var d = new AccountForm();
        if (d.ShowDialog(FindForm()) == DialogResult.OK && d.Result != null)
        {
            MailPool.Drop(d.Result);
            MailStore.Add(d.Result);
            SelectAccount(d.Result);
            MailWatch.Kick();
        }
    }

    /// <summary>全部帳戶：所有收件匣合併做一個清單（按時間排）。</summary>
    private async void SelectAll()
    {
        _all = true;
        _acct = null;
        try { Directory.CreateDirectory(MailStore.Dir); File.WriteAllText(LastFile, "*all*"); } catch { }
        _acctBtn.Text = Lang.T("全部帳戶") + "  ▾";
        _folders.Items.Clear();
        _folders.Items.Add(new FolderList.Item(AllInbox, "📥", Lang.T("全部收件匣")));
        _folders.Selected = _folder = AllInbox;
        _folders.Invalidate();
        OnUnread();
        LoadList();
        // 預先攞每個帳戶嘅資料夾（刪除 / 封存要知垃圾桶喺邊）
        foreach (var a in MailStore.All.ToList())
            try { await MailPool.FoldersAsync(a, CancellationToken.None); } catch { }
    }

    private async void SelectAccount(MailAccount a)
    {
        _all = false;
        _acct = a;
        try { Directory.CreateDirectory(MailStore.Dir); File.WriteAllText(LastFile, a.Id); } catch { }
        _acctBtn.Text = AcctLabel(a);
        _folder = null;
        _folders.Items.Clear();
        _folders.Invalidate();
        _items.Clear();
        _list.Empty = "";
        _list.Loading = true;
        _list.Reset();
        ShowBlank();
        SetStatus(Lang.T("連線緊…"));
        try
        {
            var list = await MailPool.FoldersAsync(a, CancellationToken.None, refresh: true);
            if (_acct != a || _all) return;
            BuildFolders(list);
            _folder = list.FirstOrDefault(f => f.Special == "inbox") ?? list.FirstOrDefault(f => !f.NoSelect);
            _folders.Selected = _folder;
            _folders.Invalidate();
            LoadList();
            _ = RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            if (_acct != a) return;
            _list.Loading = false;
            _list.Empty = Lang.T("連唔到郵件伺服器：") + "\n" + MailHint.Explain(ex.Message) + "\n\n" + Lang.T("密碼改咗或者登入過期：撳左上角帳戶 → 移除，再重新加入。");
            _list.Invalidate();
            SetStatus("");
        }
    }

    private static string FolderName(MailFolder f) => f.Special switch
    {
        "inbox" => Lang.T("收件匣"),
        "flagged" => Lang.T("已加星號"),
        "sent" => Lang.T("寄件備份"),
        "drafts" => Lang.T("草稿"),
        "all" => Lang.T("所有郵件"),
        "archive" => Lang.T("封存"),
        "junk" => Lang.T("垃圾郵件"),
        "trash" => Lang.T("垃圾桶"),
        _ => f.Display.Contains(f.Delim) && f.Display.StartsWith("[Gmail]") ? f.Display[(f.Display.IndexOf(f.Delim) + 1)..] : f.Display
    };

    private static string FolderGlyph(MailFolder f) => f.Special switch
    {
        "inbox" => "📥", "flagged" => "★", "sent" => "➤", "drafts" => "✎", "all" => "✉", "archive" => "▤", "junk" => "⚠", "trash" => "🗑", _ => "▸"
    };

    private void BuildFolders(List<MailFolder> list)
    {
        string[] order = { "inbox", "flagged", "sent", "drafts", "all", "archive", "junk", "trash" };
        _folders.Items.Clear();
        foreach (var f in list.Where(f => !f.NoSelect)
                     .OrderBy(f => f.Special.Length > 0 ? Array.IndexOf(order, f.Special) : 99)
                     .ThenBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase))
            _folders.Items.Add(new FolderList.Item(f, FolderGlyph(f), FolderName(f)));
        _folders.Invalidate();
    }

    private async Task RefreshCountsAsync()
    {
        var a = _acct;
        var inbox = a != null ? MailPool.Special(a, "inbox") : null;
        if (a == null || inbox == null) return;
        try
        {
            int n = await MailPool.UseAsync(a, null, (c, t) => c.UnseenAsync(inbox.Name, t), CancellationToken.None);
            MailWatch.SetUnread(a, n);
        }
        catch { }
    }

    private void OnUnread()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_all)
            {
                _folders.Counts[AllInbox.Name] = MailStore.All.Sum(x => MailWatch.Unread.TryGetValue(x.Id, out int k) ? k : 0);
                _folders.Invalidate();
                return;
            }
            var a = _acct;
            var inbox = a != null ? MailPool.Special(a, "inbox") : null;
            if (a == null || inbox == null) return;
            MailWatch.Unread.TryGetValue(a.Id, out int n);
            _folders.Counts[inbox.Name] = n;
            _folders.Invalidate();
        });
    }

    private void OnNewMail(MailAccount a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_search.Length > 0) return;
            if (_all) { _ = CheckNewAllAsync(a); return; }
            if (a != _acct || _folder?.Special != "inbox") return;
            _ = CheckNewAsync();
        });
    }

    // ================= 清單 =================

    private void SetStatus(string s, bool err = false)
    {
        _status.Text = s;
        _status.ForeColor = err ? Ui.Danger : Ui.TextFaint;
    }

    private async void LoadList()
    {
        if (_all) { LoadAll(); return; }
        var a = _acct;
        var folder = _folder;
        if (a == null || folder == null) return;
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        _items.Clear();
        _uids = new();
        _next = 0;
        ShowBlank();
        _list.Selected = null;
        _list.HasMore = false;
        _list.Loading = true;
        _list.ShowTo = folder.Special is "sent" or "drafts";
        _list.ShowAcct = false;
        _list.Empty = _search.Length > 0 ? Lang.F("搵唔到同「{0}」有關嘅信", _search) : Lang.T("呢度冇信");
        _list.Reset();
        SetStatus(Lang.T("載入緊…"));
        try
        {
            string q = _search;
            var uids = await MailPool.UseAsync(a, folder.Name, (c, t) => q.Length > 0 ? c.SearchTextAsync(q, t) : c.SearchAsync("ALL", t), cts.Token, 60, reselect: true);
            if (cts.IsCancellationRequested) return;
            uids.Reverse();
            _uids = uids;
            await LoadMoreAsync(cts);
            if (cts.IsCancellationRequested) return;
            SetStatus(_search.Length > 0 ? Lang.F("搵到 {0} 封", _uids.Count) : "");
            if (folder.Special == "inbox") _ = RefreshCountsAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _list.Loading = false;
            _list.Empty = Lang.T("載入唔到：") + ex.Message;
            _list.Invalidate();
            SetStatus(Lang.T("出錯"), true);
        }
    }

    private bool _moreBusy;

    private async Task LoadMoreAsync(CancellationTokenSource? cts)
    {
        if (_all) { await LoadMoreAllAsync(cts); return; }
        var a = _acct;
        var folder = _folder;
        if (cts == null || a == null || folder == null || _moreBusy) return;
        var batch = _uids.Skip(_next).Take(50).ToList();
        if (batch.Count == 0) { _list.Loading = false; _list.HasMore = false; _list.Invalidate(); return; }
        _moreBusy = true;
        _list.Loading = true;
        _list.Invalidate();
        try
        {
            var hs = await MailPool.UseAsync(a, folder.Name, (c, t) => c.FetchHeadersAsync(batch, t), cts.Token, 90);
            if (cts.IsCancellationRequested) return;
            var rank = new Dictionary<uint, int>();
            for (int i = 0; i < batch.Count; i++) rank[batch[i]] = i;
            hs = hs.Where(h => rank.ContainsKey(h.Uid)).OrderBy(h => rank[h.Uid]).ToList();
            foreach (var h in hs) h.Acct = a;
            _next += batch.Count;
            _items.AddRange(hs);
            _list.HasMore = _next < _uids.Count;
            _list.Loading = false;
            _list.Invalidate();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _list.Loading = false;
            _list.HasMore = false;
            _list.Invalidate();
            SetStatus(Lang.T("載入唔到：") + ex.Message, true);
        }
        finally { _moreBusy = false; }
        if (!cts.IsCancellationRequested) BeginInvoke(_list.CheckMore);
    }

    /// <summary>有新信：淨係將新嘅加喺頂（唔使成個清單重載）。</summary>
    private async Task CheckNewAsync()
    {
        var a = _acct;
        var folder = _folder;
        var cts = _listCts;
        if (a == null || folder == null || cts == null || _list.Loading) return;
        try
        {
            uint top = _uids.Count > 0 ? _uids[0] : 0;
            var uids = await MailPool.UseAsync(a, folder.Name, (c, t) => c.SearchAsync($"UID {top + 1}:*", t), cts.Token, 60, reselect: true);
            var fresh = uids.Where(u => u > top).OrderByDescending(u => u).ToList();
            if (fresh.Count == 0 || cts.IsCancellationRequested) return;
            var hs = await MailPool.UseAsync(a, folder.Name, (c, t) => c.FetchHeadersAsync(fresh, t), cts.Token, 60);
            if (cts.IsCancellationRequested) return;
            foreach (var h in hs) h.Acct = a;
            _uids.InsertRange(0, fresh);
            _next += fresh.Count;
            _items.InsertRange(0, hs.OrderByDescending(h => h.Uid));
            _list.Invalidate();
        }
        catch { }
    }

    // ---------- 全部帳戶 ----------

    private async void LoadAll()
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        _items.Clear();
        _multi.Clear();
        ShowBlank();
        _list.Selected = null;
        _list.HasMore = false;
        _list.Loading = true;
        _list.ShowTo = false;
        _list.ShowAcct = true;
        _list.Empty = _search.Length > 0 ? Lang.F("搵唔到同「{0}」有關嘅信", _search) : Lang.T("呢度冇信");
        _list.Reset();
        SetStatus(Lang.T("載入緊…"));
        string q = _search;
        var accts = MailStore.All.ToList();
        var fails = new List<string>();
        await Task.WhenAll(accts.Select(async a =>
        {
            try
            {
                var uids = await MailPool.UseAsync(a, "INBOX", (c, t) => q.Length > 0 ? c.SearchTextAsync(q, t) : c.SearchAsync("ALL", t), cts.Token, 60, reselect: true);
                uids.Reverse();
                _multi[a.Id] = (uids, 0);
            }
            catch (Exception ex) when (!cts.IsCancellationRequested) { fails.Add(a.Email + "：" + MailHint.Explain(ex.Message).Split('\n')[0]); }
            catch { }
        }));
        if (cts.IsCancellationRequested) return;
        await LoadMoreAllAsync(cts);
        if (cts.IsCancellationRequested) return;
        int total = _multi.Values.Sum(x => x.Uids.Count);
        SetStatus(fails.Count > 0 ? Lang.T("連唔到：") + string.Join("；", fails) : _search.Length > 0 ? Lang.F("搵到 {0} 封", total) : "", fails.Count > 0);
        foreach (var a in accts) _ = MailPool.UseAsync(a, null, (c, t) => c.UnseenAsync("INBOX", t), CancellationToken.None)
            .ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) MailWatch.SetUnread(a, t.Result); });
    }

    /// <summary>每個帳戶各攞下一批，合併後按日期排。</summary>
    private async Task LoadMoreAllAsync(CancellationTokenSource? cts)
    {
        if (cts == null || _moreBusy) return;
        _moreBusy = true;
        _list.Loading = true;
        _list.Invalidate();
        try
        {
            var accts = MailStore.All.Where(a => _multi.ContainsKey(a.Id)).ToList();
            var got = new List<MailHeader>();
            await Task.WhenAll(accts.Select(async a =>
            {
                var (uids, next) = _multi[a.Id];
                var batch = uids.Skip(next).Take(30).ToList();
                if (batch.Count == 0) return;
                try
                {
                    var hs = await MailPool.UseAsync(a, "INBOX", (c, t) => c.FetchHeadersAsync(batch, t), cts.Token, 90);
                    foreach (var h in hs) h.Acct = a;
                    lock (got) got.AddRange(hs);
                    _multi[a.Id] = (uids, next + batch.Count);
                }
                catch { }
            }));
            if (cts.IsCancellationRequested) return;
            _items.AddRange(got);
            SortAll();
            _list.HasMore = _multi.Values.Any(x => x.Next < x.Uids.Count);
        }
        finally
        {
            _moreBusy = false;
            _list.Loading = false;
            _list.Invalidate();
        }
        if (!cts.IsCancellationRequested) BeginInvoke(_list.CheckMore);
    }

    private void SortAll()
    {
        var sorted = _items.OrderByDescending(h => h.Date).ToList();
        _items.Clear();
        _items.AddRange(sorted);
    }

    private async Task CheckNewAllAsync(MailAccount a)
    {
        var cts = _listCts;
        if (cts == null || _list.Loading || !_multi.TryGetValue(a.Id, out var st)) return;
        try
        {
            uint top = st.Uids.Count > 0 ? st.Uids[0] : 0;
            var uids = await MailPool.UseAsync(a, "INBOX", (c, t) => c.SearchAsync($"UID {top + 1}:*", t), cts.Token, 60, reselect: true);
            var fresh = uids.Where(u => u > top).OrderByDescending(u => u).ToList();
            if (fresh.Count == 0 || cts.IsCancellationRequested) return;
            var hs = await MailPool.UseAsync(a, "INBOX", (c, t) => c.FetchHeadersAsync(fresh, t), cts.Token, 60);
            if (cts.IsCancellationRequested || !_all) return;
            foreach (var h in hs) h.Acct = a;
            var cur = _multi[a.Id];
            cur.Uids.InsertRange(0, fresh);
            _multi[a.Id] = (cur.Uids, cur.Next + fresh.Count);
            _items.AddRange(hs);
            SortAll();
            _list.Invalidate();
        }
        catch { }
    }

    private void RowMenu(MailHeader h, Point p)
    {
        var m = TUi.Menu(this);
        m.Items.Add(h.Seen ? Lang.T("標為未讀") : Lang.T("標為已讀"), null, (_, _) => _ = SetSeenAsync(h, !h.Seen));
        m.Items.Add(h.Flagged ? Lang.T("移除星號") : Lang.T("加星號"), null, (_, _) => _ = ToggleStarAsync(h));
        var acct = AcctOf(h);
        if (ArchiveTarget(h) != null) m.Items.Add(Lang.T("封存"), null, (_, _) => _ = ArchiveAsync(h));
        var junk = acct != null ? MailPool.Special(acct, "junk") : null;
        if (junk != null && h.Folder != junk.Name) m.Items.Add(Lang.T("移去垃圾郵件"), null, (_, _) => _ = MoveAsync(h, junk.Name));
        if (junk != null && h.Folder == junk.Name)
        {
            var inbox = acct != null ? MailPool.Special(acct, "inbox") : null;
            if (inbox != null) m.Items.Add(Lang.T("唔係垃圾郵件"), null, (_, _) => _ = MoveAsync(h, inbox.Name));
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Lang.T("刪除"), null, (_, _) => _ = DeleteAsync(h));
        m.Show(_list, p);
    }

    // ================= 睇信 =================

    /// <summary>返去信件清單（收埋睇信畫面）。</summary>
    private void ShowBlank()
    {
        _openCts?.Cancel();
        _open = null;
        _msg = null;
        _read.Visible = false;
        _listHost.Visible = true;
        if (_list.Selected != null) _list.EnsureVisible(_list.Selected);
        _list.Invalidate();
        if (Visible) _list.Focus();
    }

    /// <summary>AI 結果框（標題 + 複製 + 關閉 + 文字）。</summary>
    private Panel AiPanel(out TextBox text, out Label title, int height)
    {
        var box = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Ui.Bg, Padding = new Padding(0, 0, 0, Ui.S(8)), Visible = false };
        var t = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.F(9.75f)
        };
        var head = new Panel { Dock = DockStyle.Top, Height = Ui.S(28), BackColor = Ui.Card, Padding = new Padding(Ui.S(8), 0, 0, 0) };
        var tl = Ui.Text_(Lang.T("✦ AI"), Ui.Accent2, Ui.F(9.5f, FontStyle.Bold), new Padding(0));
        tl.Dock = DockStyle.Left; tl.TextAlign = ContentAlignment.MiddleLeft; tl.AutoSize = false; tl.Width = Ui.S(260);
        var close = new FancyButton { Text = "✕", Kind = BtnKind.Ghost, Font = Ui.F(9f), Dock = DockStyle.Right, Width = Ui.S(34) };
        close.Click += (_, _) => box.Visible = false;
        var copy = new FancyButton { Text = Lang.T("複製"), Kind = BtnKind.Ghost, Font = Ui.F(9f), Dock = DockStyle.Right, Width = Ui.S(60) };
        copy.Click += (_, _) => { try { if (t.Text.Length > 0) Clipboard.SetText(t.Text); Toast.Show(Lang.T("已複製"), Ui.Success, 1200); } catch { } };
        head.Controls.Add(tl);
        head.Controls.Add(copy);
        head.Controls.Add(close);
        var pad = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding(Ui.S(10), Ui.S(4), Ui.S(4), Ui.S(6)) };
        pad.Controls.Add(t);
        box.Controls.Add(pad);
        box.Controls.Add(head);
        text = t;
        title = tl;
        return box;
    }

    private void BuildBar()
    {
        FancyButton B(string t, Action a, string? tip = null, BtnKind k = BtnKind.Ghost)
        {
            var b = TUi.Btn(t, a, 0, k);
            b.Height = Ui.S(32);
            b.Margin = new Padding(0, 0, Ui.S(6), Ui.S(6));
            if (tip != null) _tip.SetToolTip(b, tip);
            _bar.Controls.Add(b);
            return b;
        }
        B(Lang.T("← 返回"), ShowBlank, Lang.T("返去信件清單（Esc）"));
        B(Lang.T("↩ 回覆"), () => Reply(false), Lang.T("回覆寄件人（R）"));
        B(Lang.T("↩↩ 全部回覆"), () => Reply(true));
        B(Lang.T("➤ 轉寄"), Forward);
        _archBtn = B(Lang.T("▤ 封存"), () => { if (_open != null) _ = ArchiveAsync(_open); }, Lang.T("由收件匣移走（喺「所有郵件」/「封存」搵得返）"));
        B(Lang.T("🗑 刪除"), () => { if (_open != null) _ = DeleteAsync(_open); }, Lang.T("移去垃圾桶（Delete）"));
        B(Lang.T("✉ 標為未讀"), () => { if (_open != null) _ = SetSeenAsync(_open, false); });
        _starBtn = B("☆", () => { if (_open != null) _ = ToggleStarAsync(_open); }, Lang.T("加 / 移除星號"));
        B(Lang.T("✦ AI 摘要"), () => _ = AiSummaryAsync(), Lang.T("叫 AI 用幾句講晒封信講咩、要你做咩"), BtnKind.Default);
        B(Lang.T("✦ AI 草擬回覆"), () => _ = AiReplyAsync(), Lang.T("講低想點回覆，AI 幫你寫好草稿"), BtnKind.Default);
    }

    private MailFolder? ArchiveTarget(MailHeader? h)
    {
        var a = AcctOf(h);
        if (a == null || h == null) return null;
        var t = a.Kind == "gmail" ? MailPool.Special(a, "all") : MailPool.Special(a, "archive");
        if (t == null || t.Name == h.Folder) return null;
        if (a.Kind == "gmail" && !IsInbox(h)) return null; // Gmail：淨係收件匣先有「封存」
        return t;
    }

    private async Task OpenAsync(MailHeader h)
    {
        var a = AcctOf(h);
        if (a == null) return;
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        _open = h;
        _subject.Text = h.Subject.Length > 0 ? h.Subject : Lang.T("（冇主旨）");
        _meta.Text = (h.From.Length > 0 ? $"{h.From} <{h.FromAddr}>" : h.FromAddr) + "    " + MailList.When(h.Date);
        foreach (Control c in _attach.Controls.Cast<Control>().ToList()) { _attach.Controls.Remove(c); c.Dispose(); }
        _aiBox.Visible = false;
        _listHost.Visible = false;
        _read.Visible = true;
        _showImgs = false;
        _imgBar.Visible = false;
        _blank.Text = Lang.T("載入緊…");
        _view.Visible = false;
        _blank.Visible = true;
        _blank.BringToFront();
        try
        {
            string key = $"{a.Id}/{h.Folder}/{h.Uid}";
            if (!Cache.TryGetValue(key, out var m))
            {
                var raw = await MailPool.UseAsync(a, h.Folder, (c, t) => c.FetchRawAsync(h.Uid, t), cts.Token, 180)
                          ?? throw new InvalidOperationException(Lang.T("伺服器搵唔到呢封信（可能已經刪咗）"));
                m = await Task.Run(() => Mime.Parse(raw));
                Cache[key] = m;
                CacheOrder.Add(key);
                if (CacheOrder.Count > 30) { Cache.Remove(CacheOrder[0]); CacheOrder.RemoveAt(0); }
            }
            if (cts.IsCancellationRequested || _open != h) return;
            _msg = m;
            Render(h, m);
            if (!h.Seen) _ = SetSeenAsync(h, true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || _open != h) return;
            _blank.Text = Lang.T("開唔到呢封信：") + ex.Message;
        }
    }

    private void Render(MailHeader h, MailMessage m)
    {
        _subject.Text = m.Subject.Length > 0 ? m.Subject : Lang.T("（冇主旨）");
        _tip.SetToolTip(_subject, _subject.Text);
        string from = m.From.Length > 0 && m.From != m.FromAddr ? $"{m.From} <{m.FromAddr}>" : m.FromAddr;
        string meta = from + "    " + (m.Date != DateTime.MinValue ? m.Date.ToString("yyyy-MM-dd HH:mm") : "");
        meta += "\n" + Lang.T("致：") + m.To + (m.Cc.Length > 0 ? "    " + Lang.T("副本：") + m.Cc : "");
        _meta.Text = meta;
        _tip.SetToolTip(_meta, meta);
        if (_starBtn != null) { _starBtn.Text = h.Flagged ? "★" : "☆"; _starBtn.Invalidate(); }
        if (_archBtn != null) _archBtn.Visible = ArchiveTarget(h) != null;
        _aiBox.Visible = false;

        // 附件（內嵌喺 HTML 入面嘅圖唔計）
        _attach.SuspendLayout();
        foreach (Control c in _attach.Controls.Cast<Control>().ToList()) { _attach.Controls.Remove(c); c.Dispose(); }
        foreach (var p in m.Attachments.Where(p => !(p.ContentId.Length > 0 && m.Html.Contains("cid:" + p.ContentId, StringComparison.OrdinalIgnoreCase))))
        {
            var part = p;
            string name = part.FileName.Length > 0 ? part.FileName : Lang.T("附件") + Ext(part.ContentType);
            var b = TUi.Btn("📎 " + name + "  " + Size(part.Body.Length), () => { }, 0);
            b.Height = Ui.S(30);
            b.Margin = new Padding(0, 0, Ui.S(6), Ui.S(6));
            b.MaximumSize = new Size(Ui.S(320), Ui.S(30));
            b.Click += (_, _) => AttachMenu(b, part, name);
            _tip.SetToolTip(b, name);
            _attach.Controls.Add(b);
        }
        _attach.ResumeLayout(true);

        string html = BuildHtml(m);
        bool allow = _showImgs || MailStore.ImagesTrusted(m.FromAddr);
        _imgBar.Visible = !allow && MailHtml.HasRemote(html);
        _view.Show(html, allow);
        _view.Visible = true;
        _blank.Visible = false;
    }

    private static string Ext(string type) => type switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "application/pdf" => ".pdf", "text/plain" => ".txt",
        "text/calendar" => ".ics", "message/rfc822" => ".eml", _ => ""
    };

    public static string Size(long n) =>
        n >= 1 << 20 ? $"{n / 1048576.0:0.0} MB" : n >= 1024 ? $"{n / 1024.0:0} KB" : $"{n} B";

    private void AttachMenu(Control b, MailPart p, string name)
    {
        var m = TUi.Menu(this);
        m.Items.Add(Lang.T("開啟"), null, (_, _) =>
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            string[] risky = { ".exe", ".bat", ".cmd", ".com", ".scr", ".js", ".vbs", ".ps1", ".msi", ".jar", ".lnk", ".hta", ".wsf" };
            if (risky.Contains(ext) && MessageBox.Show(FindForm(), Lang.F("「{0}」係程式檔，可能有病毒。真係要開？", name), Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                string dir = Path.Combine(MailStore.Dir, "tmp", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, SafeName(name));
                File.WriteAllBytes(f, p.Body);
                TUi.Open(f);
            }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        });
        m.Items.Add(Lang.T("另存新檔…"), null, (_, _) =>
        {
            using var d = new SaveFileDialog { FileName = SafeName(name), Filter = Lang.T("所有檔案") + "|*.*" };
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { File.WriteAllBytes(d.FileName, p.Body); Toast.Show(Lang.T("已儲存"), Ui.Success, 1200); }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        });
        m.Show(b, new Point(0, b.Height));
    }

    public static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.');
        return n.Length > 0 ? n : "attachment";
    }

    /// <summary>信入面撳咗連結：網頁用外面瀏覽器開，mailto 就寫信。</summary>
    private void OpenLink(string u)
    {
        if (u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            Compose(null, to: Uri.UnescapeDataString(u[7..].Split('?')[0]));
            return;
        }
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
    }

    /// <summary>撳咗「顯示圖片」：用返同一封信重新顯示。</summary>
    private void Rerender()
    {
        if (_open != null && _msg != null) Render(_open, _msg);
    }

    private static string BuildHtml(MailMessage m)
    {
        const string head = "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\"><base target=\"_self\">" +
                            "<style>body{font-family:'Segoe UI','Microsoft JhengHei UI','Microsoft YaHei UI',sans-serif;font-size:14px;margin:14px 16px;color:#202124;background:#fff;word-wrap:break-word}" +
                            "img{max-width:100%;height:auto}pre{white-space:pre-wrap;word-wrap:break-word}blockquote{border-left:3px solid #ccc;margin-left:4px;padding-left:10px;color:#555}</style>";
        string html;
        if (m.Html.Trim().Length > 0)
        {
            html = Mime.InlineImages(m.Html, m.Attachments);
            html = Regex.Replace(html, @"<script\b[^>]*>.*?</script\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(iframe|object|embed|applet)\b[^>]*>(.*?</\1\s*>)?", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<meta\b[^>]*(charset|http-equiv)[^>]*>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase); // onclick 等
            var hm = Regex.Match(html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
            if (hm.Success) html = html.Insert(hm.Index + hm.Length, head);
            else html = "<!DOCTYPE html><html><head>" + head + "</head><body>" + html + "</body></html>";
            if (!html.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) html = "<!DOCTYPE html>" + html;
        }
        else
        {
            string text = WebUtility.HtmlEncode(m.Text);
            text = Regex.Replace(text, @"(https?://[^\s<>&""]+)", "<a href=\"$1\">$1</a>");
            html = "<!DOCTYPE html><html><head>" + head + "</head><body><pre style=\"font-family:inherit;font-size:14px\">" + text + "</pre></body></html>";
        }
        return html;
    }

    // ================= 動作 =================

    private async Task<bool> Try(MailHeader h, Func<ImapClient, CancellationToken, Task> f)
    {
        var a = AcctOf(h);
        if (a == null) return false;
        try
        {
            await MailPool.UseAsync(a, h.Folder, async (c, t) => { await f(c, t); return 0; }, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(Lang.T("做唔到：") + ex.Message, true);
            return false;
        }
    }

    private async Task SetSeenAsync(MailHeader h, bool seen)
    {
        bool was = h.Seen;
        h.Seen = seen;
        _list.Invalidate();
        if (!await Try(h, (c, t) => c.StoreAsync(h.Uid, "\\Seen", seen, t))) { h.Seen = was; _list.Invalidate(); return; }
        var acct = AcctOf(h);
        if (acct != null && IsInbox(h) && was != seen)
        {
            MailWatch.Unread.TryGetValue(acct.Id, out int n);
            MailWatch.SetUnread(acct, n + (seen ? -1 : 1));
        }
    }

    private async Task ToggleStarAsync(MailHeader h)
    {
        h.Flagged = !h.Flagged;
        _list.Invalidate();
        if (h == _open && _starBtn != null) { _starBtn.Text = h.Flagged ? "★" : "☆"; _starBtn.Invalidate(); }
        if (!await Try(h, (c, t) => c.StoreAsync(h.Uid, "\\Flagged", h.Flagged, t)))
        {
            h.Flagged = !h.Flagged;
            _list.Invalidate();
        }
    }

    private void RemoveRow(MailHeader h)
    {
        int i = _items.IndexOf(h);
        _items.Remove(h);
        int ui = _uids.IndexOf(h.Uid);
        if (ui >= 0) { _uids.RemoveAt(ui); if (ui < _next) _next--; }
        if (_list.Selected == h) _list.Selected = i >= 0 && i < _items.Count ? _items[i] : _items.LastOrDefault();
        if (_open == h) ShowBlank();
        _list.Invalidate();
        _list.CheckMore();
    }

    private async Task MoveAsync(MailHeader h, string target)
    {
        if (await Try(h, (c, t) => c.MoveAsync(h.Uid, target, t))) RemoveRow(h);
    }

    private Task ArchiveAsync(MailHeader h)
    {
        var t = ArchiveTarget(h);
        return t == null ? Task.CompletedTask : MoveAsync(h, t.Name);
    }

    private async Task DeleteAsync(MailHeader h)
    {
        var acct = AcctOf(h);
        var trash = acct != null ? MailPool.Special(acct, "trash") : null;
        if (trash != null && h.Folder != trash.Name) { await MoveAsync(h, trash.Name); return; }
        if (MessageBox.Show(FindForm(), Lang.T("永久刪除呢封信？刪咗就救唔返。"), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (await Try(h, (c, t) => c.DeleteForeverAsync(h.Uid, t))) RemoveRow(h);
    }

    // ================= 寫信 =================

    private void Compose(ComposeForm.Draft? d, string to = "")
    {
        if (MailStore.All.Count == 0) { AddAccount(); if (MailStore.All.Count == 0) return; }
        d ??= new ComposeForm.Draft { To = to };
        d.Account ??= AcctOf(_open) ?? MailStore.All[0];
        var f = new ComposeForm(d);
        f.Show(FindForm());
    }

    private string Quote(MailMessage m)
    {
        string who = m.From.Length > 0 ? $"{m.From} <{m.FromAddr}>" : m.FromAddr;
        string head = Lang.F("{0} 喺 {1} 寫道：", who, m.Date.ToString("yyyy-MM-dd HH:mm"));
        var lines = Mime.PlainText(m).Replace("\r\n", "\n").Split('\n').Select(l => "> " + l);
        return head + "\n" + string.Join("\n", lines);
    }

    private ComposeForm.Draft? ReplyDraft(bool all)
    {
        var acct = AcctOf(_open);
        if (_open == null || _msg == null || acct == null) return null;
        var m = _msg;
        string to = m.ReplyTo.Length > 0 ? m.ReplyTo : (m.From.Length > 0 && m.From != m.FromAddr ? $"\"{m.From}\" <{m.FromAddr}>" : m.FromAddr);
        string cc = "";
        if (all)
        {
            var me = acct.Email;
            var others = Mime.SplitAddrs(m.To + "," + m.Cc)
                .Where(x => !Mime.ParseAddress(x).Addr.Equals(me, StringComparison.OrdinalIgnoreCase) && !Mime.ParseAddress(x).Addr.Equals(m.FromAddr, StringComparison.OrdinalIgnoreCase));
            cc = string.Join(", ", others);
        }
        // 自己寄出嘅信（寄件備份）：回覆返原本嘅收件人
        if (m.FromAddr.Equals(acct.Email, StringComparison.OrdinalIgnoreCase)) { to = m.To; if (!all) cc = ""; }
        string subj = Regex.IsMatch(m.Subject, @"^\s*(re|回覆|答复)\s*[:：]", RegexOptions.IgnoreCase) ? m.Subject : "Re: " + m.Subject;
        return new ComposeForm.Draft
        {
            Account = acct, To = to, Cc = cc, Subject = subj, Quote = Quote(m), InReplyTo = m.MessageId,
            References = (m.References + " " + m.MessageId).Trim(), ReplyOf = _open, Context = Mime.PlainText(m)
        };
    }

    private void Reply(bool all)
    {
        var d = ReplyDraft(all);
        if (d != null) Compose(d);
    }

    private void Forward()
    {
        var acct = AcctOf(_open);
        if (_open == null || _msg == null || acct == null) return;
        var m = _msg;
        var files = new List<string>();
        try
        {
            string dir = Path.Combine(MailStore.Dir, "tmp", Guid.NewGuid().ToString("N")[..8]);
            foreach (var p in m.Attachments.Where(p => !(p.ContentId.Length > 0 && m.Html.Contains("cid:" + p.ContentId, StringComparison.OrdinalIgnoreCase))))
            {
                Directory.CreateDirectory(dir);
                string f = TUi.Unique(Path.Combine(dir, SafeName(p.FileName.Length > 0 ? p.FileName : "attachment" + Ext(p.ContentType))));
                File.WriteAllBytes(f, p.Body);
                files.Add(f);
            }
        }
        catch (Exception ex) { AppPaths.LogError("轉寄附件", ex); }
        string head = "---------- " + Lang.T("轉寄嘅郵件") + " ----------\n" +
                      Lang.T("寄件人：") + (m.From.Length > 0 ? $"{m.From} <{m.FromAddr}>" : m.FromAddr) + "\n" +
                      Lang.T("日期：") + m.Date.ToString("yyyy-MM-dd HH:mm") + "\n" +
                      Lang.T("主旨：") + m.Subject + "\n" + Lang.T("致：") + m.To + "\n\n";
        string subj = Regex.IsMatch(m.Subject, @"^\s*(fwd?|轉寄|转发)\s*[:：]", RegexOptions.IgnoreCase) ? m.Subject : "Fwd: " + m.Subject;
        Compose(new ComposeForm.Draft { Account = acct, Subject = subj, Quote = head + Mime.PlainText(m), Attachments = files, Context = Mime.PlainText(m) });
    }

    // ================= AI =================

    private void ShowAi(string title, string text)
    {
        _aiText.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _aiBox.Visible = true;
        _aiTitle.Text = "✦ " + title;
    }

    private async Task AiSummaryAsync()
    {
        if (_msg == null) return;
        if (_blank.Visible && _open == null) return;
        var m = _msg;
        ShowAi(Lang.T("AI 摘要"), Lang.T("AI 諗緊…"));
        try
        {
            string prompt = $"用{MailAi.UiLang}，簡短摘要以下電郵（3 至 5 點），最後一行講清楚收件人需唔需要做啲咩、有冇期限。唔好加開場白。\n\n" +
                            $"寄件人：{m.From} <{m.FromAddr}>\n主旨：{m.Subject}\n日期：{m.Date:yyyy-MM-dd HH:mm}\n\n內文：\n{MailAi.Clip(Mime.PlainText(m), 8000)}";
            string ans = await MailAi.AskAsync(prompt);
            if (_msg == m) ShowAi(Lang.T("AI 摘要"), ans);
        }
        catch (Exception ex) { if (_msg == m) ShowAi(Lang.T("AI 摘要"), Lang.T("AI 出錯：") + ex.Message); }
    }

    private async Task AiReplyAsync()
    {
        var d = ReplyDraft(false);
        if (d == null) return;
        var how = AskText.Show(FindForm(), Lang.T("AI 草擬回覆"), Lang.T("想點回覆？例如「多謝，星期五得閒」（可以留空，等 AI 自己諗）"), "", true);
        if (how == null) return;
        ShowAi(Lang.T("AI 草擬回覆"), Lang.T("AI 寫緊…"));
        try
        {
            d.Body = await ComposeForm.AiDraftAsync(how, d.Subject, d.Context, "", d.Account?.Name ?? "");
            _aiBox.Visible = false;
            Compose(d);
        }
        catch (Exception ex) { ShowAi(Lang.T("AI 草擬回覆"), Lang.T("AI 出錯：") + ex.Message); }
    }

    private async Task AiSortAsync()
    {
        if (_items.Count == 0) return;
        var list = _items.Take(40).ToList();
        ShowBlank();
        void Show(string t) { _sortText.Text = t.Replace("\r\n", "\n").Replace("\n", "\r\n"); _sortBox.Visible = true; }
        _sortTitle.Text = "✦ " + Lang.T("AI 整理") + " · " + Lang.F("最近 {0} 封信", list.Count);
        Show(Lang.T("AI 諗緊…"));
        try
        {
            var lines = list.Select((h, i) => $"{i + 1}. [{(h.Seen ? "已讀" : "未讀")}] {h.Date:MM-dd HH:mm} | {(h.From.Length > 0 ? h.From : h.FromAddr)} <{h.FromAddr}> | {h.Subject}");
            string prompt = $"用{MailAi.UiLang}整理以下收件匣清單。分三類：「需要處理」（要回覆 / 有期限 / 重要）、「稍後再看」、「廣告 / 通知」。" +
                            "每類列出編號、寄件人同一句講係咩；「要處理」放最頂，講埋建議做咩。唔好加開場白。\n\n" + string.Join("\n", lines);
            Show(await MailAi.AskAsync(prompt));
        }
        catch (Exception ex) { Show(Lang.T("AI 出錯：") + ex.Message); }
    }

    // Esc：由睇信返清單
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _read.Visible) { ShowBlank(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

// ---------------------------------------------------------------------
//  寫信視窗
// ---------------------------------------------------------------------
internal sealed class ComposeForm : Form
{
    internal sealed class Draft
    {
        public MailAccount? Account;
        public string To = "", Cc = "", Bcc = "", Subject = "", Body = "", Quote = "", InReplyTo = "", References = "", Context = "";
        public List<string> Attachments = new();
        public MailHeader? ReplyOf;
    }

    private readonly Draft _d;
    private MailAccount _from;
    private readonly FancyButton _fromBtn, _send;
    private readonly TextBox _to = new(), _cc = new(), _bcc = new(), _subj = new();
    private readonly TextBox _body;
    private readonly Panel _ccRow, _bccRow;
    private readonly FlowLayoutPanel _files;
    private readonly List<string> _attach;
    private readonly string _initial;
    private bool _sent;
    private CancellationTokenSource? _cts;

    public ComposeForm(Draft d)
    {
        _d = d;
        _from = d.Account ?? MailStore.All[0];
        _attach = d.Attachments.ToList();
        Text = d.Subject.Length > 0 ? d.Subject : Lang.T("新郵件");
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(Ui.S(760), Ui.S(640));
        MinimumSize = new Size(Ui.S(520), Ui.S(420));
        ShowInTaskbar = true;
        Icon = Application.OpenForms.Count > 0 ? Application.OpenForms[0]?.Icon : null;
        AllowDrop = true;
        Padding = new Padding(Ui.S(16), Ui.S(12), Ui.S(16), Ui.S(12));
        try { DarkTheme.DarkTitleBar(this); } catch { }

        Panel Row(string label, Control input, Control? extra = null)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = Ui.S(42), BackColor = Ui.Bg };
            var l = Ui.Text_(label, Ui.TextDim, Ui.F(9.5f), new Padding(0));
            l.AutoSize = false; l.Dock = DockStyle.Left; l.Width = Ui.S(80); l.TextAlign = ContentAlignment.MiddleLeft;
            var host = input is TextBox tb ? new InputHost(tb, Ui.S(200)) : input;
            host.Dock = DockStyle.Fill;
            var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(0, Ui.S(4), 0, Ui.S(4)) };
            wrap.Controls.Add(host);
            row.Controls.Add(wrap);
            if (extra != null) { extra.Dock = DockStyle.Right; row.Controls.Add(extra); }
            row.Controls.Add(l);
            return row;
        }

        _fromBtn = new FancyButton { Text = _from.Display + (MailStore.All.Count > 1 ? "  ▾" : ""), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft };
        _fromBtn.Click += (_, _) =>
        {
            if (MailStore.All.Count < 2) return;
            var m = TUi.Menu(this);
            foreach (var a in MailStore.All)
            {
                var acc = a;
                m.Items.Add(new ToolStripMenuItem(a.Display, null, (_, _) => { _from = acc; _fromBtn.Text = acc.Display + "  ▾"; _fromBtn.Invalidate(); }) { Checked = a == _from });
            }
            m.Show(_fromBtn, new Point(0, _fromBtn.Height));
        };
        _to.Text = d.To; _cc.Text = d.Cc; _bcc.Text = d.Bcc; _subj.Text = d.Subject;
        _subj.TextChanged += (_, _) => Text = _subj.Text.Length > 0 ? _subj.Text : Lang.T("新郵件");
        var ccToggle = new FancyButton { Text = Lang.T("副本 / 密件"), Kind = BtnKind.Ghost, Font = Ui.F(9f), Width = Ui.S(96) };
        var fromRow = Row(Lang.T("寄件人"), _fromBtn);
        var toRow = Row(Lang.T("收件人"), _to, ccToggle);
        _ccRow = Row(Lang.T("副本"), _cc);
        _bccRow = Row(Lang.T("密件副本"), _bcc);
        _ccRow.Visible = d.Cc.Length > 0;
        _bccRow.Visible = d.Bcc.Length > 0;
        ccToggle.Click += (_, _) => { bool v = !(_ccRow.Visible && _bccRow.Visible); _ccRow.Visible = v || _cc.Text.Length > 0; _bccRow.Visible = v || _bcc.Text.Length > 0; };
        var subjRow = Row(Lang.T("主旨"), _subj);
        _to.PlaceholderText = Lang.T("多個用逗號分隔");

        _body = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(10.5f), WordWrap = true
        };
        string body = d.Body;
        if (d.Quote.Length > 0) body = body + "\r\n\r\n" + d.Quote;
        _body.Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _initial = d.Quote.Length > 0 ? ("\r\n\r\n" + d.Quote).Replace("\r\n", "\n").Replace("\n", "\r\n") : "";
        var bodyWrap = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Input, Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(4), Ui.S(8)) };
        bodyWrap.Controls.Add(_body);
        var bodyOuter = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(0, Ui.S(8), 0, Ui.S(8)) };
        bodyOuter.Controls.Add(bodyWrap);

        _files = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Bg, WrapContents = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(44), BackColor = Ui.Bg, WrapContents = false, Padding = new Padding(0, Ui.S(6), 0, 0) };
        _send = TUi.Btn(Lang.T("➤ 傳送"), () => _ = SendAsync(), 100, BtnKind.Primary);
        var attach = TUi.Btn(Lang.T("📎 附件"), PickFiles);
        var ai = TUi.Btn(Lang.T("✦ AI 幫我寫"), () => _ = AiWriteAsync(), 0, BtnKind.Default);
        var discard = TUi.Btn(Lang.T("捨棄"), Close);
        foreach (var b in new[] { _send, attach, ai, discard }) b.Margin = new Padding(0, 0, Ui.S(8), 0);
        bottom.Controls.AddRange(new Control[] { _send, attach, ai, discard });

        Controls.Add(bodyOuter);
        Controls.Add(_files);
        Controls.Add(bottom);
        Controls.Add(subjRow);
        Controls.Add(_bccRow);
        Controls.Add(_ccRow);
        Controls.Add(toRow);
        Controls.Add(fromRow);
        RefreshFiles();

        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] fs) { _attach.AddRange(fs.Where(File.Exists)); RefreshFiles(); }
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SendAsync(); } };
        Shown += (_, _) =>
        {
            if (_to.Text.Length == 0) _to.Focus();
            else { _body.Focus(); _body.Select(0, 0); }
        };
        FormClosing += (_, e) =>
        {
            if (_sent) return;
            if (_cts != null) { e.Cancel = true; return; } // 寄緊
            bool dirty = _body.Text != _initial || (_d.ReplyOf == null && (_to.Text.Length > 0 || _subj.Text.Length > 0)) || _attach.Count != _d.Attachments.Count;
            if (dirty && MessageBox.Show(this, Lang.T("捨棄呢封信？"), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
        };
    }

    private void PickFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = Lang.T("所有檔案") + "|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _attach.AddRange(d.FileNames);
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        _files.SuspendLayout();
        foreach (Control c in _files.Controls.Cast<Control>().ToList()) { _files.Controls.Remove(c); c.Dispose(); }
        foreach (var f in _attach.ToList())
        {
            var file = f;
            long len = 0;
            try { len = new FileInfo(f).Length; } catch { }
            var b = TUi.Btn("📎 " + Path.GetFileName(f) + "  " + MailPage.Size(len) + "   ✕", () => { _attach.Remove(file); RefreshFiles(); });
            b.Height = Ui.S(30);
            b.MaximumSize = new Size(Ui.S(340), Ui.S(30));
            b.Margin = new Padding(0, 0, Ui.S(6), Ui.S(6));
            _files.Controls.Add(b);
        }
        _files.ResumeLayout(true);
    }

    private async Task SendAsync()
    {
        if (_cts != null) return;
        var rcpts = Mime.Addresses(_to.Text + "," + _cc.Text + "," + _bcc.Text);
        if (rcpts.Count == 0) { MessageBox.Show(this, Lang.T("請填收件人電郵地址。"), Program.AppName); _to.Focus(); return; }
        if (_subj.Text.Trim().Length == 0 &&
            MessageBox.Show(this, Lang.T("冇主旨，照寄？"), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        long total = 0;
        foreach (var f in _attach)
        {
            if (!File.Exists(f)) { MessageBox.Show(this, Lang.F("搵唔到附件：{0}", f), Program.AppName); return; }
            total += new FileInfo(f).Length;
        }
        if (total > 25L << 20 &&
            MessageBox.Show(this, Lang.F("附件加埋 {0}，大部分郵箱上限係 25 MB，可能寄唔到。照寄？", MailPage.Size(total)), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var a = _from;
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _send.Enabled = false;
        _send.Text = Lang.T("寄緊…");
        try
        {
            var o = new Mime.Outgoing(a.Name, a.Email, _to.Text, _cc.Text, _bcc.Text, _subj.Text.Trim(), _body.Text, _attach.ToList(), _d.InReplyTo, _d.References);
            var msg = await Task.Run(() => Mime.Build(o));
            await Smtp.SendAsync(a, rcpts, msg, cts.Token);
            // 一般 IMAP：自己放一份入寄件備份（Gmail / Outlook 會自動放）
            if (a.Kind == "imap")
            {
                try
                {
                    var folders = await MailPool.FoldersAsync(a, cts.Token);
                    var sent = folders.FirstOrDefault(f => f.Special == "sent");
                    if (sent != null) await MailPool.UseAsync(a, null, async (c, t) => { await c.AppendAsync(sent.Name, msg, t); return 0; }, cts.Token, 120);
                }
                catch (Exception ex) { AppPaths.LogError("放入寄件備份", ex); }
            }
            if (_d.ReplyOf is { } r && _d.Account != null)
            {
                r.Answered = true;
                try { await MailPool.UseAsync(_d.Account, r.Folder, async (c, t) => { await c.StoreAsync(r.Uid, "\\Answered", true, t); return 0; }, CancellationToken.None); } catch { }
            }
            _sent = true;
            Toast.Show(Lang.T("已寄出"), Ui.Success, 1500);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Lang.T("寄唔到：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts = null;
            if (!IsDisposed) { _send.Enabled = true; _send.Text = Lang.T("➤ 傳送"); }
        }
    }

    /// <summary>叫 AI 寫內文：instr = 用家想點寫；context = 原信（回覆 / 轉寄先有）。</summary>
    public static Task<string> AiDraftAsync(string instr, string subject, string context, string draft, string myName)
    {
        string prompt = "你係電郵助手。幫我寫一封電郵嘅內文：淨係輸出內文本身，唔好加主旨、唔好加解釋、唔好用 Markdown。" +
                        "語言跟我嘅指示；冇講就跟原信嘅語言；都冇就用" + MailAi.UiLang + "。中文一律用書面語，唔好用廣東話口語。語氣要自然得體。\n\n" +
                        "我嘅指示：" + (instr.Trim().Length > 0 ? instr.Trim() : "（冇特別指示，按原信內容寫一個合適嘅回覆）") + "\n" +
                        (myName.Length > 0 ? "我嘅名：" + myName + "\n" : "") +
                        "主旨：" + subject + "\n" +
                        (draft.Trim().Length > 0 ? "\n我寫咗嘅草稿（幫我改好佢）：\n" + MailAi.Clip(draft, 4000) + "\n" : "") +
                        (context.Trim().Length > 0 ? "\n原信：\n" + MailAi.Clip(context, 8000) + "\n" : "");
        return MailAi.AskAsync(prompt);
    }

    private async Task AiWriteAsync()
    {
        var how = AskText.Show(this, Lang.T("AI 幫我寫"), Lang.T("想寫咩？例如「同老闆請星期五假」；已經寫咗啲嘢就會幫你改好佢"), "", true);
        if (how == null) return;
        // 引用原信嗰段唔郁，淨係換上面自己寫嘅部分
        string quote = _d.Quote.Replace("\r\n", "\n").Replace("\n", "\r\n");
        string text = _body.Text;
        int qi = quote.Length > 0 ? text.IndexOf(quote, StringComparison.Ordinal) : -1;
        string mine = qi >= 0 ? text[..qi].TrimEnd() : text;
        string rest = qi >= 0 ? text[qi..] : "";
        var old = _body.Text;
        _body.ReadOnly = true;
        _body.Text = Lang.T("AI 寫緊…") + (rest.Length > 0 ? "\r\n\r\n" + rest : "");
        try
        {
            string ans = await AiDraftAsync(how, _subj.Text, _d.Context, mine, _from.Name);
            _body.Text = ans.Replace("\r\n", "\n").Replace("\n", "\r\n") + (rest.Length > 0 ? "\r\n\r\n" + rest : "");
        }
        catch (Exception ex)
        {
            _body.Text = old;
            MessageBox.Show(this, Lang.T("AI 出錯：") + ex.Message, Program.AppName);
        }
        finally { _body.ReadOnly = false; }
    }
}

// ---------------------------------------------------------------------
//  加帳戶
// ---------------------------------------------------------------------
internal sealed class AccountForm : Form
{
    public MailAccount? Result;
    private readonly Tools.TabStrip _tabs;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly FancyButton _ok;
    private readonly Label _msg;
    private CancellationTokenSource? _cts;
    private int _kind;

    // Gmail
    private readonly TextBox _gEmail = new(), _gPass = new() { UseSystemPasswordChar = true }, _gName = new();
    // Outlook
    private readonly TextBox _oClient = new(), _oName = new();
    private string _oRefresh = "", _oEmail = "";
    private MailAccount? _oAcct;
    private readonly CheckBox _oWork = new();
    private bool _oGuide;                         // 已經有 Client ID 都要顯示教學
    private string OTenant => _oWork.Checked ? "organizations" : "consumers";                  // 登入咗嘅 Outlook 帳戶（試 IMAP 失敗再撳都用返同一個，唔使再攞 token）
    private readonly Label _oCode;
    // 其他
    private readonly TextBox _iEmail = new(), _iPass = new() { UseSystemPasswordChar = true }, _iImap = new(), _iImapPort = new() { Text = "993" },
        _iSmtp = new(), _iSmtpPort = new() { Text = "465" }, _iName = new();

    public AccountForm()
    {
        Text = Lang.T("加入郵件帳戶");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(620), Ui.S(560));
        Padding = new Padding(Ui.S(18), Ui.S(14), Ui.S(18), Ui.S(14));
        try { DarkTheme.DarkTitleBar(this); } catch { }

        _tabs = new Tools.TabStrip("Gmail", "Outlook / Hotmail", Lang.T("其他 (IMAP)"));
        _tabs.Dock = DockStyle.Top;
        _tabs.Selected += i => { _kind = i; Build(); };
        _oCode = Ui.Text_("", Ui.Accent, Ui.F(16f, FontStyle.Bold), new Padding(0, Ui.S(6), 0, Ui.S(6)));
        _oClient.Text = MailStore.OutlookClientId;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(44), BackColor = Ui.Bg, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, Ui.S(8), 0, 0) };
        _ok = TUi.Btn(Lang.T("加入"), () => _ = OkAsync(), 90, BtnKind.Primary);
        var cancel = TUi.Btn(Lang.T("取消"), () => { _cts?.Cancel(); DialogResult = DialogResult.Cancel; Close(); }, 80);
        _ok.Margin = new Padding(Ui.S(8), 0, 0, 0);
        _msg = Ui.Text_("", Ui.TextDim, Ui.F(9f), new Padding(0));
        _msg.AutoSize = false; _msg.Dock = DockStyle.Bottom; _msg.Height = Ui.S(80); _msg.UseMnemonic = false;
        bottom.Controls.Add(_ok);
        bottom.Controls.Add(cancel);

        Controls.Add(_body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = Ui.S(10), BackColor = Ui.Bg });
        Controls.Add(_tabs);
        Controls.Add(_msg);
        Controls.Add(bottom);
        FormClosed += (_, _) => _cts?.Cancel();
        Build();
    }

    private void Build()
    {
        _body.SuspendLayout();
        foreach (Control c in _body.Controls.Cast<Control>().ToList()) _body.Controls.Remove(c);
        var col = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        int w = Ui.S(570);
        Control Field(string label, TextBox tb, string? ph = null)
        {
            var p = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Bg, WrapContents = false, Margin = new Padding(0, 0, 0, Ui.S(6)) };
            var l = Ui.Text_(label, Ui.TextDim, Ui.F(9.5f), new Padding(0, Ui.S(8), 0, 0));
            l.AutoSize = false; l.Width = Ui.S(120); l.Height = Ui.S(30);
            if (ph != null) tb.PlaceholderText = ph;
            p.Controls.Add(l);
            p.Controls.Add(tb.Parent as InputHost ?? new InputHost(tb, w - Ui.S(126)));
            return p;
        }
        Label Note(string t)
        {
            var l = Ui.Text_(t, Ui.TextFaint, Ui.F(9f), new Padding(0, 0, 0, Ui.S(8)));
            l.MaximumSize = new Size(w, 0);
            return l;
        }
        Label Step(string t) => Ui.Text_(t, Ui.Text, Ui.F(10f, FontStyle.Bold), new Padding(0, Ui.S(6), 0, Ui.S(4)));
        FancyButton Link(string t, string url)
        {
            var b = TUi.Btn(t, () =>
            {
                if (url.Length == 0) { _oGuide = true; Build(); return; } // 顯示教學
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            });
            b.Margin = new Padding(0, 0, 0, Ui.S(10));
            return b;
        }
        _ok.Text = Lang.T("加入");
        _ok.Enabled = true;
        switch (_kind)
        {
            case 0:
                col.Controls.Add(Note(Lang.T("Gmail 要用「應用程式密碼」（唔係你平時登入嘅密碼）：\n1. Google 帳戶要開咗「兩步驗證」\n2. 撳下面個掣，改個名（例如 MultiTool）→ 建立\n3. 將嗰 16 個字母貼落「應用程式密碼」")));
                col.Controls.Add(Link(Lang.T("開 Google 應用程式密碼頁"), "https://myaccount.google.com/apppasswords"));
                col.Controls.Add(Field(Lang.T("Gmail 地址"), _gEmail, "name@gmail.com"));
                col.Controls.Add(Field(Lang.T("應用程式密碼"), _gPass, "abcd efgh ijkl mnop"));
                col.Controls.Add(Field(Lang.T("寄件人名稱"), _gName, Lang.T("（可以留空）")));
                col.Controls.Add(Note(Lang.T("密碼用 Windows 加密存喺呢部電腦，唔會上載去任何地方。")));
                break;
            case 1:
                if (_oGuide || MailStore.OutlookClientId.Length == 0)
                {
                    col.Controls.Add(Note(Lang.T("Outlook / Hotmail 唔可以用密碼登入，要用微軟帳戶登入。第一次要做以下設定（只需做一次，全部免費）：")));
                    col.Controls.Add(Step(Lang.T("① 開 Azure 免費帳戶")));
                    col.Controls.Add(Note(Lang.T("撳下面個掣 →「Try Azure for free」，用你嘅 Microsoft 帳戶登記（要手機同信用卡驗證，唔會扣錢）。\n唔好撳「升級為隨用隨付」，亦唔使買 Microsoft 365。")));
                    col.Controls.Add(Link(Lang.T("開 Azure 免費帳戶"), "https://azure.microsoft.com/free/"));
                    col.Controls.Add(Step(Lang.T("② 註冊 App")));
                    col.Controls.Add(Note(Lang.T("撳下面個掣 →「+ 新增註冊」\n• 名稱：隨便，例如 MultiTool Mail\n• 支援的帳戶類型：揀「任何 Entra ID 租用戶 + 個人 Microsoft 帳戶」\n• 重新導向 URI 唔使填 → 撳「註冊」\n• 喺「概觀」複製「應用程式 (用戶端) 識別碼」，貼落下面 Client ID")));
                    col.Controls.Add(Link(Lang.T("開 Azure 應用程式註冊"), "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"));
                    col.Controls.Add(Step(Lang.T("③ 開權限")));
                    col.Controls.Add(Note(Lang.T("• 左邊「管理」→「Authentication (Preview)」→「允許公用用戶端流程」設做「是」→ 儲存\n• 「API 權限」→「+ 新增權限」→「Microsoft Graph」→「委派的權限」→ 剔 IMAP.AccessAsUser.All、SMTP.Send、offline_access → 新增權限")));
                    col.Controls.Add(Step(Lang.T("④ Outlook 開 IMAP")));
                    col.Controls.Add(Note(Lang.T("Outlook.com → ⚙ 設定 → 郵件 → 轉寄和 IMAP → 開啟 IMAP → 儲存（每個 Outlook 帳戶都要開）")));
                    col.Controls.Add(Link(Lang.T("開 Outlook 設定"), "https://outlook.live.com/mail/0/options/mail/forwarding"));
                    col.Controls.Add(Step(Lang.T("⑤ 登入")));
                    col.Controls.Add(Note(Lang.T("撳「登入微軟帳戶」→ 喺打開咗嘅網頁貼上代碼（已自動複製）→ 登入 → 接受。\n加第二個帳戶唔使再做 ①–③，直接登入；網頁揀「使用其他帳戶」。")));
                }
                else
                {
                    col.Controls.Add(Note(Lang.T("Client ID 已經設定好：直接撳「登入微軟帳戶」就得（記得個 Outlook 帳戶要開咗 IMAP）。")));
                    col.Controls.Add(Link(Lang.T("顯示第一次設定教學"), ""));
                }
                col.Controls.Add(Field("Client ID", _oClient, "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"));
                col.Controls.Add(Field(Lang.T("寄件人名稱"), _oName, Lang.T("（可以留空）")));
                _oWork.Text = Lang.T("公司 / 學校帳戶（Microsoft 365）；Hotmail / Outlook.com 個人帳戶唔好剔");
                _oWork.AutoSize = true; _oWork.ForeColor = Ui.TextDim; _oWork.BackColor = Ui.Bg; _oWork.Font = Ui.F(9.25f);
                _oWork.Margin = new Padding(0, Ui.S(2), 0, Ui.S(6));
                _oWork.Enabled = _oRefresh.Length == 0;
                col.Controls.Add(_oWork);
                col.Controls.Add(_oCode);
                if (_oRefresh.Length > 0) { _oCode.Text = Lang.F("✓ 已登入：{0}", _oEmail); _oCode.ForeColor = Ui.Success; }
                _ok.Text = _oRefresh.Length > 0 ? Lang.T("加入") : Lang.T("登入微軟帳戶");
                break;
            default:
                col.Controls.Add(Note(Lang.T("其他郵箱（Yahoo、iCloud、公司郵箱…）：填郵件服務提供嘅 IMAP / SMTP 設定。SMTP 端口 465 用 SSL，587 用 STARTTLS。")));
                col.Controls.Add(Field(Lang.T("電郵地址"), _iEmail));
                col.Controls.Add(Field(Lang.T("密碼"), _iPass));
                col.Controls.Add(Field(Lang.T("IMAP 伺服器"), _iImap, "imap.example.com"));
                col.Controls.Add(Field(Lang.T("IMAP 端口"), _iImapPort));
                col.Controls.Add(Field(Lang.T("SMTP 伺服器"), _iSmtp, "smtp.example.com"));
                col.Controls.Add(Field(Lang.T("SMTP 端口"), _iSmtpPort));
                col.Controls.Add(Field(Lang.T("寄件人名稱"), _iName, Lang.T("（可以留空）")));
                break;
        }
        _body.Controls.Add(col);
        _body.ResumeLayout(true);
        FitHeight(col);
    }

    /// <summary>視窗高度跟內容：唔使拉 scrollbar（太高就最多到螢幕高度）。</summary>
    private void FitHeight(FlowLayoutPanel col)
    {
        int need = 0;
        foreach (Control c in col.Controls) need = Math.Max(need, c.Bottom + c.Margin.Bottom);
        int other = ClientSize.Height - _body.Height;            // 分頁、按鈕、訊息嗰啲
        int want = other + need + Ui.S(8);
        var wa = Screen.FromControl(this).WorkingArea;
        int h = Math.Min(Math.Max(want, Ui.S(420)), wa.Height - (Height - ClientSize.Height) - Ui.S(20));
        if (h != ClientSize.Height)
        {
            int oldH = Height;
            ClientSize = new Size(ClientSize.Width, h);
            if (Visible) Top = Math.Max(wa.Top, Top - (Height - oldH) / 2);
        }
    }

    private void Msg(string s, bool err = false)
    {
        _msg.Text = s;
        _msg.ForeColor = err ? Ui.Danger : Ui.TextDim;
    }

    private async Task OkAsync()
    {
        if (_cts != null) return;
        MailAccount a;
        switch (_kind)
        {
            case 0:
                if (!_gEmail.Text.Contains('@') || _gPass.Text.Replace(" ", "").Length < 8) { Msg(Lang.T("請填 Gmail 地址同應用程式密碼。"), true); return; }
                a = MailAccount.Gmail(_gEmail.Text, _gPass.Text);
                a.Name = _gName.Text.Trim();
                break;
            case 1:
                if (_oRefresh.Length == 0) { await OutlookLoginAsync(); return; }
                a = _oAcct ??= MailAccount.Outlook(_oEmail, _oRefresh, OTenant);
                a.Name = _oName.Text.Trim();
                break;
            default:
                if (!_iEmail.Text.Contains('@') || _iImap.Text.Trim().Length == 0 || _iSmtp.Text.Trim().Length == 0 ||
                    !int.TryParse(_iImapPort.Text, out int ip) || !int.TryParse(_iSmtpPort.Text, out int sp)) { Msg(Lang.T("請填齊所有欄位。"), true); return; }
                a = new MailAccount
                {
                    Kind = "imap", Email = _iEmail.Text.Trim(), ImapHost = _iImap.Text.Trim(), ImapPort = ip, SmtpHost = _iSmtp.Text.Trim(), SmtpPort = sp,
                    Secret = MailStore.Protect(_iPass.Text), Name = _iName.Text.Trim()
                };
                break;
        }
        // 試登入
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        _ok.Enabled = false;
        Msg(Lang.T("試緊登入…"));
        try
        {
            using var c = new ImapClient();
            await c.ConnectAsync(a, cts.Token);
            Result = a;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            string m = ex.Message;
            if (_kind == 0 && m.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase))
                m = Lang.T("登入失敗：請確認係「應用程式密碼」，唔係平時嘅密碼。");
            if (ex is OperationCanceledException) m = Lang.T("連線逾時，請檢查網絡。");
            Msg(MailHint.Explain(m), true);
        }
        finally
        {
            _cts = null;
            if (!IsDisposed) _ok.Enabled = true;
        }
    }

    private async Task OutlookLoginAsync()
    {
        string id = _oClient.Text.Trim();
        if (!Guid.TryParse(id, out _)) { Msg(Lang.T("請先貼上 Azure App 嘅 Client ID。"), true); return; }
        MailStore.OutlookClientId = id;
        var cts = _cts = new CancellationTokenSource();
        _ok.Enabled = false;
        Msg(Lang.T("攞緊登入代碼…"));
        try
        {
            string tenant = OTenant;
            var dc = await MsOAuth.StartAsync(id, tenant, cts.Token);
            _oCode.ForeColor = Ui.Accent;
            _oCode.Text = Lang.T("登入代碼：") + dc.UserCode;
            try { Clipboard.SetText(dc.UserCode); } catch { }
            Msg(Lang.T("代碼已經複製：喺打開咗嘅網頁貼上，再登入你嘅 Outlook 帳戶。"));
            try { Process.Start(new ProcessStartInfo(dc.Url) { UseShellExecute = true }); } catch { }
            var (access, refresh, email) = await MsOAuth.PollAsync(id, tenant, dc, cts.Token);
            if (refresh.Length == 0) throw new InvalidOperationException(Lang.T("微軟冇俾 refresh token（App 權限要有 offline_access）。"));
            if (email.Length == 0) email = AskText.Show(this, "Outlook", Lang.T("你嘅 Outlook 電郵地址："), "") ?? "";
            if (!email.Contains('@')) throw new InvalidOperationException(Lang.T("唔知你嘅電郵地址。"));
            _oRefresh = refresh;
            _oEmail = email;
            _oAcct = MailAccount.Outlook(email, refresh, tenant);
            MsOAuth.Seed(_oAcct, access);
            _cts = null;
            Build();
            await OkAsync(); // 試 IMAP 登入，成功就加入
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { Msg(ex is OperationCanceledException ? "" : ex.Message, true); _oCode.Text = ""; }
        }
        finally
        {
            if (_cts == cts) _cts = null;
            if (!IsDisposed) _ok.Enabled = true;
        }
    }
}

/// <summary>常見伺服器錯誤：加一句點樣解決。</summary>
internal static class MailHint
{
    public static string Explain(string m)
    {
        if (m.Contains("authenticated but not connected", StringComparison.OrdinalIgnoreCase))
            return Lang.T("微軟接受咗登入，但係個郵箱未俾 IMAP 連線。去 outlook.live.com → 設定 → 郵件 → 轉寄和 IMAP，開啟 IMAP 再儲存；如果仲唔得，去 account.live.com/activity 喺最新嗰次登入撳「這是我」，等幾分鐘再試。") + "\n(" + m + ")";
        if (m.Contains("AADSTS50196", StringComparison.OrdinalIgnoreCase))
            return Lang.T("微軟話短時間內登入太多次，請等幾分鐘再試。") + "\n(" + m + ")";
        if (m.Contains("AADSTS7000218", StringComparison.OrdinalIgnoreCase))
            return Lang.T("Azure App 未開「允許公用用戶端流程」：去 Authentication (Preview) 開咗佢再試。") + "\n(" + m + ")";
        return m;
    }
}

/// <summary>問一句文字（單行或者多行）。</summary>
internal sealed class AskText : Form
{
    private readonly TextBox _box;

    private AskText(string title, string hint, string value, bool multi)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        int boxH = multi ? Ui.S(90) : Ui.S(30);
        ClientSize = new Size(Ui.S(440), Ui.S(110) + boxH);
        try { DarkTheme.DarkTitleBar(this); } catch { }
        var l = Ui.Text_(hint, Ui.TextDim, Ui.F(9.5f), new Padding(0));
        l.MaximumSize = new Size(Ui.S(408), 0);
        l.Location = new Point(Ui.S(16), Ui.S(14));
        _box = new TextBox
        {
            Text = value, Font = Ui.F(10.5f), BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Multiline = multi,
            ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None, Bounds = new Rectangle(Ui.S(16), Ui.S(54), Ui.S(408), boxH)
        };
        var ok = new FancyButton { Text = Lang.T("確定"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Bounds = new Rectangle(Ui.S(244), Ui.S(66) + boxH, Ui.S(84), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Bounds = new Rectangle(Ui.S(340), Ui.S(66) + boxH, Ui.S(84), Ui.S(34)) };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && (!multi || e.Control)) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
        };
        Controls.AddRange(new Control[] { l, _box, ok, cancel });
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Show(IWin32Window? owner, string title, string hint, string value, bool multi = false)
    {
        using var d = new AskText(title, hint, value, multi);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
