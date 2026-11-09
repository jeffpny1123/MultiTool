using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Tools;

// =====================================================================
//  Yahoo Finance 即時推送（WebSocket）
//  • 連 wss://streamer.finance.yahoo.com，送 {"subscribe":[...]}，之後有成交就推一條訊息過嚟
//  • 訊息係 Base64 包住嘅 Protobuf（PricingData），自己解碼，唔使額外函式庫
//  • 斷線自動重連；連唔到就照用每 30 秒輪詢（StockWatch.RefreshAsync）
// =====================================================================

/// <summary>一條推送：代號、價錢、時間（UTC）、升跌、昨收等等（冇嘅欄位係 NaN）。</summary>
internal readonly record struct StockTick(string Symbol, double Price, DateTime TimeUtc, double Change, double ChangePct,
    double PrevClose, double DayVolume, string Currency, string ShortName);

internal static class YahooStream
{
    private const string Url = "wss://streamer.finance.yahoo.com/?version=2";

    /// <summary>收到推送（背景 thread）。</summary>
    public static event Action<StockTick>? Tick;

    /// <summary>而家連住冇。</summary>
    public static bool Connected { get; private set; }
    public static DateTime LastTick { get; private set; }

    private static readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _sent = new(StringComparer.OrdinalIgnoreCase);
    private static ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;
    private static readonly SemaphoreSlim _sendLock = new(1, 1);

    public static void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>設定要訂閱嘅代號（自選股改咗就叫）。</summary>
    public static void SetSymbols(IEnumerable<string> symbols)
    {
        lock (_wanted)
        {
            _wanted.Clear();
            foreach (var s in symbols) _wanted.Add(s.ToUpperInvariant());
        }
        _ = SyncAsync();
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        int fails = 0;
        while (!ct.IsCancellationRequested)
        {
            bool hasAny;
            lock (_wanted) hasAny = _wanted.Count > 0;
            if (!hasAny) { await Task.Delay(2000, ct).ContinueWith(_ => { }); continue; }
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "https://finance.yahoo.com");
            ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(Url), connectCts.Token);
                _ws = ws;
                lock (_sent) _sent.Clear();
                Connected = true;
                fails = 0;
                await SyncAsync();
                await ReceiveAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                fails++;
                if (fails == 1 || fails % 20 == 0) AppPaths.LogError("股票即時推送", ex);
            }
            finally
            {
                Connected = false;
                _ws = null;
                try { ws.Dispose(); } catch { }
            }
            // 重連：越失敗等越耐（最多 1 分鐘）
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(fails, 5))), ct); } catch { break; }
        }
    }

    /// <summary>補訂 / 退訂，令伺服器嗰邊同 _wanted 一樣。</summary>
    private static async Task SyncAsync()
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        string[] add, remove;
        lock (_wanted) lock (_sent)
        {
            add = _wanted.Where(s => !_sent.Contains(s)).ToArray();
            remove = _sent.Where(s => !_wanted.Contains(s)).ToArray();
        }
        try
        {
            if (add.Length > 0) await SendAsync(ws, new JsonObject { ["subscribe"] = new JsonArray(add.Select(s => (JsonNode)s).ToArray()) });
            if (remove.Length > 0) await SendAsync(ws, new JsonObject { ["unsubscribe"] = new JsonArray(remove.Select(s => (JsonNode)s).ToArray()) });
            lock (_sent)
            {
                foreach (var s in add) _sent.Add(s);
                foreach (var s in remove) _sent.Remove(s);
            }
        }
        catch { }
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonNode msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    private static async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).Trim();
            if (text.Length == 0) continue;
            try
            {
                // version=2：{"type":"pricing","message":"<base64>"}；舊版：直接係 base64
                string b64 = text;
                if (text.StartsWith('{'))
                {
                    var j = JsonNode.Parse(text);
                    if ((string?)j?["type"] is { } type && type != "pricing") continue;
                    b64 = (string?)j?["message"] ?? "";
                }
                if (b64.Length == 0) continue;
                var tick = Decode(Convert.FromBase64String(b64));
                if (tick.Symbol.Length == 0 || double.IsNaN(tick.Price)) continue;
                LastTick = DateTime.Now;
                Tick?.Invoke(tick);
            }
            catch { } // 一條壞訊息唔影響其他
        }
    }

    // ================= Protobuf 解碼（PricingData） =================
    // 1 id(string) 2 price(float) 3 time(sint64 毫秒) 4 currency 5 exchange 8 changePercent(float)
    // 9 dayVolume(sint64) 12 change(float) 13 shortName 16 previousClose(float)

    private static StockTick Decode(byte[] b)
    {
        string id = "", cur = "", name = "";
        double price = double.NaN, chg = double.NaN, pct = double.NaN, prev = double.NaN, vol = double.NaN;
        long time = 0;
        int i = 0;
        while (i < b.Length)
        {
            ulong key = Varint(b, ref i);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0: // varint
                {
                    ulong v = Varint(b, ref i);
                    long z = (long)(v >> 1) ^ -(long)(v & 1); // zigzag（sint64）
                    if (field == 3) time = z;
                    else if (field == 9) vol = z;
                    break;
                }
                case 1: i += 8; break; // fixed64（double，例如市值，用唔着）
                case 2: // 長度 + 內容（字串）
                {
                    int len = (int)Varint(b, ref i);
                    if (i + len > b.Length) return default;
                    string s = Encoding.UTF8.GetString(b, i, len);
                    i += len;
                    if (field == 1) id = s;
                    else if (field == 4) cur = s;
                    else if (field == 13) name = s;
                    break;
                }
                case 5: // fixed32（float）
                {
                    if (i + 4 > b.Length) return default;
                    float f = BitConverter.ToSingle(b, i);
                    i += 4;
                    if (field == 2) price = f;
                    else if (field == 8) pct = f;
                    else if (field == 12) chg = f;
                    else if (field == 16) prev = f;
                    break;
                }
                default: return new StockTick(id, price, Time(time), chg, pct, prev, vol, cur, name); // 唔識嘅格式：停
            }
        }
        return new StockTick(id, price, Time(time), chg, pct, prev, vol, cur, name);
    }

    private static DateTime Time(long ms) =>
        ms <= 0 ? DateTime.UtcNow : ms < 100_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(ms).UtcDateTime : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static ulong Varint(byte[] b, ref int i)
    {
        ulong v = 0;
        int shift = 0;
        while (i < b.Length)
        {
            byte x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) break;
        }
        return v;
    }
}
