using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MultiTool.Tools;

/// <summary>
/// 二維碼（QR Code，ISO/IEC 18004）嘅產生同讀取，自己寫嘅純 C#，冇用第三方程式庫。
/// 產生：數字 / 英數 / UTF-8 位元組模式，版本 1–40，四級容錯。
/// 讀取：喺一幅灰階圖入面搵三個定位角 → 取樣 → 糾錯（Reed-Solomon）→ 解讀文字。
/// 啱螢幕截圖同正面影嘅相；斜得好犀利或者好矇嘅相未必讀到。
/// </summary>
internal static class QrCode
{
    public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

    // 每個版本、每級容錯：每塊嘅糾錯碼字數、塊數（[容錯級][版本]，版本由 1 開始）
    private static readonly sbyte[][] EccPerBlock =
    {
        new sbyte[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new sbyte[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new sbyte[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new sbyte[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };
    private static readonly sbyte[][] NumBlocks =
    {
        new sbyte[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new sbyte[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new sbyte[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new sbyte[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };
    // 格式資訊入面嘅容錯級編碼：L=1、M=0、Q=3、H=2
    private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };
    private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // ---------------- GF(256) ----------------
    private static readonly byte[] Exp = new byte[512], Log = new byte[256];
    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = (byte)x; Log[x] = (byte)i; x <<= 1; if (x >= 256) x ^= 0x11D; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }
    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[(Log[a] + 255 - Log[b]) % 255];

    // ---------------- 版本資料 ----------------
    private static int RawModules(int ver)
    {
        int r = (16 * ver + 128) * ver + 64;
        if (ver >= 2) { int n = ver / 7 + 2; r -= (25 * n - 10) * n - 55; if (ver >= 7) r -= 36; }
        return r;
    }
    private static int DataCodewords(int ver, Ecc e) => RawModules(ver) / 8 - EccPerBlock[(int)e][ver] * NumBlocks[(int)e][ver];

    private static int[] AlignPositions(int ver)
    {
        if (ver == 1) return Array.Empty<int>();
        int n = ver / 7 + 2, size = ver * 4 + 17;
        int step = ver == 32 ? 26 : (ver * 4 + n * 2 + 1) / (n * 2 - 2) * 2;
        var r = new int[n];
        r[0] = 6;
        for (int i = n - 1, pos = size - 7; i >= 1; i--, pos -= step) r[i] = pos;
        return r;
    }

    /// <summary>畫功能圖案（定位角、對齊、計時、版本），回傳「邊格係功能格」。格式資訊嘅位置只係留空。</summary>
    private static bool[,] DrawFunction(int ver, bool[,] m)
    {
        int size = ver * 4 + 17;
        var f = new bool[size, size];
        void Set(int x, int y, bool dark) { m[y, x] = dark; f[y, x] = true; }
        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy)), x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4);
                }
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        var al = AlignPositions(ver);
        for (int i = 0; i < al.Length; i++)
            for (int j = 0; j < al.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        // 格式資訊位置（之後先填）
        for (int i = 0; i <= 8; i++) { if (!f[8, i]) Set(i, 8, false); if (!f[i, 8]) Set(8, i, false); }
        for (int i = 0; i < 8; i++) { Set(size - 1 - i, 8, false); Set(8, size - 1 - i, false); }
        Set(8, size - 8, true); // 固定黑格
        if (ver >= 7)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool b = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, c = i / 3;
                Set(a, c, b); Set(c, a, b);
            }
        }
        return f;
    }

    private static int FormatBits(Ecc e, int mask)
    {
        int data = EccFormatBits[(int)e] << 3 | mask, rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return (data << 10 | rem) ^ 0x5412;
    }

    private static void DrawFormat(bool[,] m, Ecc e, int mask)
    {
        int size = m.GetLength(0), bits = FormatBits(e, mask);
        bool B(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) m[i, 8] = B(i);
        m[7, 8] = B(6); m[8, 8] = B(7); m[8, 7] = B(8);
        for (int i = 9; i < 15; i++) m[8, 14 - i] = B(i);
        for (int i = 0; i < 8; i++) m[8, size - 1 - i] = B(i);
        for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = B(i);
        m[size - 8, 8] = true;
    }

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    /// <summary>資料格嘅次序（由右下角開始之字形向上），產生同讀取共用。</summary>
    private static IEnumerable<(int X, int Y)> DataOrder(bool[,] func)
    {
        int size = func.GetLength(0);
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool up = ((right + 1) & 2) == 0;
                    int y = up ? size - 1 - vert : vert;
                    if (!func[y, x]) yield return (x, y);
                }
        }
    }

    // ---------------- Reed-Solomon ----------------
    private static byte[] RsGenerator(int degree)
    {
        var g = new byte[degree];
        g[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                g[j] = Mul(g[j], root);
                if (j + 1 < degree) g[j] ^= g[j + 1];
            }
            root = Mul(root, 2);
        }
        return g;
    }

    private static byte[] RsRemainder(byte[] data, int len, byte[] gen)
    {
        var r = new byte[gen.Length];
        for (int k = 0; k < len; k++)
        {
            byte factor = (byte)(data[k] ^ r[0]);
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[^1] = 0;
            for (int i = 0; i < r.Length; i++) r[i] ^= Mul(gen[i], factor);
        }
        return r;
    }

    /// <summary>糾正一塊（資料 + 糾錯碼）入面嘅錯誤；糾唔到回傳 false。</summary>
    private static bool RsCorrect(byte[] block, int eccLen)
    {
        int n = block.Length;
        var syn = new byte[eccLen];
        bool clean = true;
        for (int i = 0; i < eccLen; i++)
        {
            byte s = 0, a = Exp[i];
            for (int j = 0; j < n; j++) s = (byte)(Mul(s, a) ^ block[j]);
            syn[i] = s;
            if (s != 0) clean = false;
        }
        if (clean) return true;
        // Berlekamp–Massey：搵錯誤位置多項式 Λ（低次項喺前）
        var lam = new byte[eccLen + 1]; lam[0] = 1;
        var prev = new byte[eccLen + 1]; prev[0] = 1;
        int L = 0, mm = 1; byte bb = 1;
        for (int r = 0; r < eccLen; r++)
        {
            byte d = syn[r];
            for (int i = 1; i <= L; i++) d ^= Mul(lam[i], syn[r - i]);
            if (d == 0) { mm++; continue; }
            var t = (byte[])lam.Clone();
            byte coef = Div(d, bb);
            for (int i = 0; i + mm <= eccLen; i++) lam[i + mm] ^= Mul(coef, prev[i]);
            if (2 * L <= r) { L = r + 1 - L; prev = t; bb = d; mm = 1; }
            else mm++;
        }
        if (L == 0 || L > eccLen / 2) return false;
        // Chien search：錯誤位置
        var pos = new List<int>();
        for (int i = 0; i < n; i++)
        {
            // 位置 i（由尾數起計 p = n-1-i）：X = α^p，要 Λ(X^-1) = 0
            int p = n - 1 - i;
            byte xinv = Exp[(255 - p % 255) % 255], v = 0;
            for (int k = L; k >= 0; k--) v = (byte)(Mul(v, xinv) ^ lam[k]);
            if (v == 0) pos.Add(i);
        }
        if (pos.Count != L) return false;
        // Ω = S·Λ mod x^eccLen
        var omega = new byte[eccLen];
        for (int i = 0; i < eccLen; i++)
        {
            byte v = 0;
            for (int k = 0; k <= Math.Min(i, L); k++) v ^= Mul(lam[k], syn[i - k]);
            omega[i] = v;
        }
        foreach (int i in pos)
        {
            int p = n - 1 - i;
            byte x = Exp[p % 255], xinv = Exp[(255 - p % 255) % 255];
            byte num = 0;
            for (int k = eccLen - 1; k >= 0; k--) num = (byte)(Mul(num, xinv) ^ omega[k]);
            byte den = 0;   // Λ'(X^-1)：只計奇數次項
            for (int k = 1; k <= L; k += 2)
            {
                byte term = lam[k];
                for (int q = 0; q < k - 1; q++) term = Mul(term, xinv);
                den ^= term;
            }
            if (den == 0) return false;
            block[i] ^= Mul(x, Div(num, den));
        }
        // 再驗一次
        for (int i = 0; i < eccLen; i++)
        {
            byte s = 0, a = Exp[i];
            for (int j = 0; j < n; j++) s = (byte)(Mul(s, a) ^ block[j]);
            if (s != 0) return false;
        }
        return true;
    }

    // ---------------- 產生 ----------------
    private sealed class Bits
    {
        public readonly List<bool> B = new();
        public void Add(int val, int len) { for (int i = len - 1; i >= 0; i--) B.Add(((val >> i) & 1) != 0); }
    }

    private static int CountBits(int mode, int ver)
    {
        int g = ver <= 9 ? 0 : ver <= 26 ? 1 : 2;
        return mode switch { 1 => new[] { 10, 12, 14 }[g], 2 => new[] { 9, 11, 13 }[g], 4 => new[] { 8, 16, 16 }[g], _ => new[] { 8, 10, 12 }[g] };
    }

    /// <summary>
    /// 將文字變成二維碼。回傳方陣（true = 黑格，[y, x]），唔連白邊。文字太長放唔落會掟 ArgumentException。
    /// </summary>
    public static bool[,] Encode(string text, Ecc ecc = Ecc.M)
    {
        int mode; byte[] bytes = Array.Empty<byte>();
        if (text.Length > 0 && text.All(c => c >= '0' && c <= '9')) mode = 1;
        else if (text.Length > 0 && text.All(c => Alnum.IndexOf(c) >= 0)) mode = 2;
        else { mode = 4; bytes = Encoding.UTF8.GetBytes(text); }
        int count = mode == 4 ? bytes.Length : text.Length;

        int ver = 0; Bits bits = new();
        for (int v = 1; v <= 40; v++)
        {
            int cb = CountBits(mode, v);
            if (count >= 1 << cb) continue;
            var b = new Bits();
            b.Add(mode, 4); b.Add(count, cb);
            if (mode == 1)
                for (int i = 0; i < text.Length; i += 3)
                {
                    int n = Math.Min(3, text.Length - i);
                    b.Add(int.Parse(text.Substring(i, n)), n * 3 + 1);
                }
            else if (mode == 2)
                for (int i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length) b.Add(Alnum.IndexOf(text[i]) * 45 + Alnum.IndexOf(text[i + 1]), 11);
                    else b.Add(Alnum.IndexOf(text[i]), 6);
                }
            else foreach (byte x in bytes) b.Add(x, 8);
            if (b.B.Count <= DataCodewords(v, ecc) * 8) { ver = v; bits = b; break; }
        }
        if (ver == 0) throw new ArgumentException("too long");

        int cap = DataCodewords(ver, ecc) * 8;
        bits.Add(0, Math.Min(4, cap - bits.B.Count));
        bits.Add(0, (8 - bits.B.Count % 8) % 8);
        for (int pad = 0xEC; bits.B.Count < cap; pad ^= 0xEC ^ 0x11) bits.Add(pad, 8);
        var data = new byte[cap / 8];
        for (int i = 0; i < bits.B.Count; i++) if (bits.B[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));

        // 分塊 + 糾錯碼 + 交錯
        int nb = NumBlocks[(int)ecc][ver], el = EccPerBlock[(int)ecc][ver], raw = RawModules(ver) / 8;
        int numShort = nb - raw % nb, shortLen = raw / nb;
        var gen = RsGenerator(el);
        var blocks = new List<(byte[] Data, byte[] Ecc)>();
        for (int i = 0, k = 0; i < nb; i++)
        {
            int dl = shortLen - el + (i < numShort ? 0 : 1);
            var d = new byte[dl];
            Array.Copy(data, k, d, 0, dl);
            k += dl;
            blocks.Add((d, RsRemainder(d, dl, gen)));
        }
        var all = new List<byte>(raw);
        for (int i = 0; i <= shortLen - el; i++)
            foreach (var b in blocks) if (i < b.Data.Length) all.Add(b.Data[i]);
        for (int i = 0; i < el; i++)
            foreach (var b in blocks) all.Add(b.Ecc[i]);

        int size = ver * 4 + 17;
        var m = new bool[size, size];
        var func = DrawFunction(ver, m);
        int idx = 0;
        foreach (var (x, y) in DataOrder(func))
        {
            if (idx < all.Count * 8) m[y, x] = ((all[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
            idx++;
        }
        // 揀扣分最少嘅遮罩
        int best = 0, bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(m, func, mask);
            DrawFormat(m, ecc, mask);
            int s = Penalty(m);
            if (s < bestScore) { bestScore = s; best = mask; }
            ApplyMask(m, func, mask); // 還原
        }
        ApplyMask(m, func, best);
        DrawFormat(m, ecc, best);
        return m;
    }

    private static void ApplyMask(bool[,] m, bool[,] func, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (!func[y, x] && MaskBit(mask, x, y)) m[y, x] = !m[y, x];
    }

    private static int Penalty(bool[,] m)
    {
        int size = m.GetLength(0), score = 0, dark = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 0; bool cur = false; int hist = 0;
                for (int b = 0; b < size; b++)
                {
                    bool v = pass == 0 ? m[a, b] : m[b, a];
                    if (b > 0 && v == cur) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                    else { cur = v; run = 1; }
                    // 似定位角嘅 1:1:3:1:1 圖案（黑白黑黑黑白黑 + 四格白）
                    hist = ((hist << 1) | (v ? 1 : 0)) & 0x7FF;
                    if (b >= 10 && (hist == 0x05D || hist == 0x5D0)) score += 40;
                }
            }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (m[y, x]) dark++;
                if (x + 1 < size && y + 1 < size && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) score += 3;
            }
        int total = size * size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;   // 黑格比例離 50% 每 5% 扣 10 分
        if (k > 0) score += k * 10;
        return score;
    }

    // ---------------- 讀取 ----------------

    /// <summary>
    /// 喺灰階圖（每像素一個 byte，0 = 黑、255 = 白，由上至下逐行）入面搵二維碼。搵唔到回傳 null。
    /// </summary>
    public static string? Decode(byte[] gray, int w, int h)
    {
        // 幾種二值化方法逐個試：全圖門檻（乾淨嘅截圖）→ 區域門檻（光暗唔平均嘅相）；再試反色（白碼黑底）
        foreach (bool invert in new[] { false, true })
            foreach (int method in new[] { 0, 1, 2 })
            {
                var bin = Binarize(gray, w, h, method, invert);
                var r = DecodeBinary(bin, w, h);
                if (r != null) return r;
            }
        return null;
    }

    /// <summary>true = 黑。method 0 = Otsu 全圖門檻；1 / 2 = 細 / 大窗口嘅區域平均門檻。</summary>
    private static bool[] Binarize(byte[] g, int w, int h, int method, bool invert)
    {
        var bin = new bool[w * h];
        if (method == 0)
        {
            var hist = new int[256];
            foreach (byte v in g) hist[v]++;
            long total = g.Length, sum = 0;
            for (int i = 0; i < 256; i++) sum += (long)i * hist[i];
            long sumB = 0, wB = 0; double max = -1; int th = 128;
            for (int i = 0; i < 256; i++)
            {
                wB += hist[i];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += (long)i * hist[i];
                double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > max) { max = between; th = i; }
            }
            for (int i = 0; i < g.Length; i++) bin[i] = (g[i] <= th) != invert;
            return bin;
        }
        // 積分圖 → 每點同周圍平均值比
        var integ = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long row = 0;
            for (int x = 0; x < w; x++)
            {
                row += g[y * w + x];
                integ[(y + 1) * (w + 1) + x + 1] = integ[y * (w + 1) + x + 1] + row;
            }
        }
        int r = Math.Max(8, Math.Min(w, h) / (method == 1 ? 16 : 6));
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long s = integ[y1 * (w + 1) + x1] - integ[y0 * (w + 1) + x1] - integ[y1 * (w + 1) + x0] + integ[y0 * (w + 1) + x0];
                int mean = (int)(s / ((x1 - x0) * (y1 - y0)));
                bin[y * w + x] = (g[y * w + x] < mean - 8) != invert;
            }
        }
        return bin;
    }

    private readonly record struct Pt(double X, double Y);
    private readonly record struct Finder(double X, double Y, double Module, int Hits);

    private static bool Ratio(int[] s, out double module)
    {
        int total = s[0] + s[1] + s[2] + s[3] + s[4];
        module = total / 7.0;
        if (total < 7) return false;
        double tol = module * 0.7;
        return Math.Abs(s[0] - module) < tol && Math.Abs(s[1] - module) < tol && Math.Abs(s[2] - 3 * module) < 3 * tol * 0.7 &&
               Math.Abs(s[3] - module) < tol && Math.Abs(s[4] - module) < tol;
    }

    /// <summary>由 (cx, cy) 沿 (dx, dy) 方向兩邊數「黑 白 黑(中) 白 黑」，核對 1:1:3:1:1；啱就回傳中心位置同格仔大細。</summary>
    private static bool CrossCheck(bool[] bin, int w, int h, int cx, int cy, int dx, int dy, out double center, out double module)
    {
        center = 0; module = 0;
        var s = new int[5];
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        int x = cx, y = cy;
        if (!In(x, y) || !bin[y * w + x]) return false;
        while (In(x, y) && bin[y * w + x]) { s[2]++; x -= dx; y -= dy; }
        while (In(x, y) && !bin[y * w + x]) { s[1]++; x -= dx; y -= dy; }
        while (In(x, y) && bin[y * w + x]) { s[0]++; x -= dx; y -= dy; }
        if (s[1] == 0 || s[0] == 0) return false;
        x = cx + dx; y = cy + dy;
        int after = 0;
        while (In(x, y) && bin[y * w + x]) { s[2]++; after++; x += dx; y += dy; }
        while (In(x, y) && !bin[y * w + x]) { s[3]++; x += dx; y += dy; }
        while (In(x, y) && bin[y * w + x]) { s[4]++; x += dx; y += dy; }
        if (s[3] == 0 || s[4] == 0) return false;
        if (!Ratio(s, out module)) return false;
        // 中間黑塊嘅中心
        int start = (dx != 0 ? cx : cy) - (s[2] - after - 1);
        center = start + s[2] / 2.0;   // 幾何中心（像素 k 佔 [k, k+1)）
        return true;
    }

    /// <summary>由 (sx, sy) 開始搵相連嘅黑色像素（四方向），回傳重心同面積；大過 maxArea 就當唔係（回傳 null）。</summary>
    private static (double X, double Y, int Area)? Blob(bool[] bin, int w, int h, int sx, int sy, int maxArea)
    {
        if (sx < 0 || sy < 0 || sx >= w || sy >= h || !bin[sy * w + sx]) return null;
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(sy * w + sx); seen.Add(sy * w + sx);
        long sumX = 0, sumY = 0;
        while (stack.Count > 0)
        {
            int p = stack.Pop(), x = p % w, y = p / w;
            sumX += x; sumY += y;
            if (seen.Count > maxArea) return null;
            if (x > 0 && bin[p - 1] && seen.Add(p - 1)) stack.Push(p - 1);
            if (x < w - 1 && bin[p + 1] && seen.Add(p + 1)) stack.Push(p + 1);
            if (y > 0 && bin[p - w] && seen.Add(p - w)) stack.Push(p - w);
            if (y < h - 1 && bin[p + w] && seen.Add(p + w)) stack.Push(p + w);
        }
        return (sumX / (double)seen.Count + 0.5, sumY / (double)seen.Count + 0.5, seen.Count);
    }

    private static List<Finder> FindFinders(bool[] bin, int w, int h)
    {
        var found = new List<Finder>();
        var starts = new List<int>(); var lens = new List<int>();
        var s = new int[5];
        for (int y = 0; y < h; y++)
        {
            // 呢一行嘅黑白段：starts / lens，第 0 段一定當白（可以係 0 長度），所以單數段係黑
            starts.Clear(); lens.Clear();
            bool cur = false; int st = 0;
            for (int x = 0; x <= w; x++)
            {
                bool dark = x < w && bin[y * w + x];
                if (x == w || dark != cur) { starts.Add(st); lens.Add(x - st); st = x; cur = dark; }
            }
            for (int i = 1; i + 4 < lens.Count; i += 2)
            {
                for (int k = 0; k < 5; k++) s[k] = lens[i + k];
                if (!Ratio(s, out _)) continue;
                int cx = starts[i + 2] + lens[i + 2] / 2;
                if (!CrossCheck(bin, w, h, cx, y, 0, 1, out double cy, out double mv)) continue;
                if (!CrossCheck(bin, w, h, cx, (int)cy, 1, 0, out double cx2, out double mh)) continue;
                if (Math.Abs(mv - mh) > Math.Max(mv, mh) * 0.5) continue;
                double mod = (mv + mh) / 2;
                int f = found.FindIndex(q => Math.Abs(q.X - cx2) < mod * 3 && Math.Abs(q.Y - cy) < mod * 3);
                if (f >= 0)
                {
                    var q = found[f];
                    found[f] = new Finder((q.X * q.Hits + cx2) / (q.Hits + 1), (q.Y * q.Hits + cy) / (q.Hits + 1), (q.Module * q.Hits + mod) / (q.Hits + 1), q.Hits + 1);
                }
                else found.Add(new Finder(cx2, cy, mod, 1));
            }
        }
        // 用中間嗰個 3×3 黑塊嘅重心同面積再執準（轉咗角度都準：面積 = 9 格）
        var list = new List<Finder>();
        foreach (var f in found.Where(f => f.Hits >= 2).OrderByDescending(f => f.Hits).Take(12))
        {
            int max = (int)(f.Module * f.Module * 9 * 3) + 16;
            if (Blob(bin, w, h, (int)f.X, (int)f.Y, max) is { } b && b.Area >= f.Module * f.Module * 9 * 0.4)
                list.Add(new Finder(b.X, b.Y, Math.Sqrt(b.Area / 9.0), f.Hits));
            else list.Add(f);
        }
        return list;
    }

    private static string? DecodeBinary(bool[] bin, int w, int h)
    {
        var fs = FindFinders(bin, w, h);
        if (fs.Count < 3) return null;
        for (int i = 0; i < fs.Count; i++)
            for (int j = i + 1; j < fs.Count; j++)
                for (int k = j + 1; k < fs.Count; k++)
                {
                    var t = new[] { fs[i], fs[j], fs[k] };
                    double mMin = t.Min(f => f.Module), mMax = t.Max(f => f.Module);
                    if (mMax > mMin * 1.8) continue;
                    // 左上角 = 對住最長邊嗰個
                    double D(Finder a, Finder b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                    double d01 = D(t[0], t[1]), d02 = D(t[0], t[2]), d12 = D(t[1], t[2]);
                    Finder tl, a1, a2;
                    if (d12 >= d01 && d12 >= d02) { tl = t[0]; a1 = t[1]; a2 = t[2]; }
                    else if (d02 >= d01 && d02 >= d12) { tl = t[1]; a1 = t[0]; a2 = t[2]; }
                    else { tl = t[2]; a1 = t[0]; a2 = t[1]; }
                    double s1 = D(tl, a1), s2 = D(tl, a2);
                    if (Math.Max(s1, s2) > Math.Min(s1, s2) * 1.4) continue;
                    // 畫面 y 向下：外積 > 0 即係 a1 喺右上、a2 喺左下
                    double cross = (a1.X - tl.X) * (a2.Y - tl.Y) - (a1.Y - tl.Y) * (a2.X - tl.X);
                    var (tr, bl) = cross > 0 ? (a1, a2) : (a2, a1);
                    double mod = (tl.Module + tr.Module + bl.Module) / 3;
                    int est = (int)Math.Round((D(tl, tr) + D(tl, bl)) / 2 / mod) + 7;
                    int ver0 = (int)Math.Round((est - 17) / 4.0);
                    foreach (int dv in ver0 >= 20 ? new[] { 0, -1, 1, -2, 2 } : new[] { 0, -1, 1 })
                    {
                        int ver = ver0 + dv;
                        if (ver < 1 || ver > 40) continue;
                        var r = TrySample(bin, w, h, new Pt(tl.X, tl.Y), new Pt(tr.X, tr.Y), new Pt(bl.X, bl.Y), ver);
                        if (r != null) return r;
                    }
                }
        return null;
    }

    /// <summary>解 8 元一次方程（高斯消去），求透視變換 8 個系數。</summary>
    private static double[]? Homography(Pt[] src, Pt[] dst)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[i].X, y = src[i].Y, u = dst[i].X, v = dst[i].Y;
            double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y, u }, r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y, v };
            for (int c = 0; c < 9; c++) { a[i * 2, c] = r1[c]; a[i * 2 + 1, c] = r2[c]; }
        }
        for (int c = 0; c < 8; c++)
        {
            int p = c;
            for (int r = c + 1; r < 8; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
            if (Math.Abs(a[p, c]) < 1e-9) return null;
            if (p != c) for (int k = 0; k < 9; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
            for (int r = 0; r < 8; r++)
            {
                if (r == c) continue;
                double f = a[r, c] / a[c, c];
                for (int k = c; k < 9; k++) a[r, k] -= f * a[c, k];
            }
        }
        var hmat = new double[8];
        for (int i = 0; i < 8; i++) hmat[i] = a[i, 8] / a[i, i];
        return hmat;
    }

    private static string? TrySample(bool[] bin, int w, int h, Pt tl, Pt tr, Pt bl, int ver)
    {
        int size = ver * 4 + 17;
        // 仿射：格仔座標 (3.5, 3.5) → 左上定位角中心，如此類推
        double span = size - 7;
        double ax = (tr.X - tl.X) / span, ay = (tr.Y - tl.Y) / span, bx = (bl.X - tl.X) / span, by = (bl.Y - tl.Y) / span;
        Pt Affine(double mx, double my) => new(tl.X + ax * (mx - 3.5) + bx * (my - 3.5), tl.Y + ay * (mx - 3.5) + by * (my - 3.5));
        Func<double, double, Pt> map = Affine;
        bool persp = false;
        // 版本 2 以上：搵右下角嗰個對齊圖案，用四點做透視變換（相影斜咗都啱）
        if (ver >= 2)
        {
            double am = size - 6.5;
            var guess = Affine(am, am);
            if (FindAlignment(bin, w, h, guess, new Pt(ax, ay), new Pt(bx, by)) is { } al)
            {
                var hm = Homography(new[] { new Pt(3.5, 3.5), new Pt(size - 3.5, 3.5), new Pt(3.5, size - 3.5), new Pt(am, am) }, new[] { tl, tr, bl, al });
                if (hm != null) persp = true;
                if (hm != null)
                    map = (mx, my) =>
                    {
                        double d = hm[6] * mx + hm[7] * my + 1;
                        return new Pt((hm[0] * mx + hm[1] * my + hm[2]) / d, (hm[3] * mx + hm[4] * my + hm[5]) / d);
                    };
            }
        }
        var r = SampleAndDecode(bin, w, h, map, ver);
        if (r == null && persp) r = SampleAndDecode(bin, w, h, Affine, ver);
        return r;
    }

    /// <summary>
    /// 喺估計位置附近搵對齊圖案（黑框、白圈、中間一粒黑）嘅中心。
    /// u / v = 一格嘅橫、直方向（像素），所以二維碼轉咗角度都搵到。
    /// </summary>
    private static Pt? FindAlignment(bool[] bin, int w, int h, Pt guess, Pt u, Pt v)
    {
        double mod = Math.Sqrt(Math.Abs(u.X * v.Y - u.Y * v.X));
        int rad = (int)Math.Ceiling(mod * 11), max = (int)(mod * mod * 3) + 8;
        bool Dark(double x, double y)
        {
            int px = (int)Math.Floor(x), py = (int)Math.Floor(y);
            return px >= 0 && py >= 0 && px < w && py < h && bin[py * w + px];
        }
        double best = double.MaxValue; Pt? hit = null;
        var done = new HashSet<long>();
        int step = Math.Max(1, (int)(mod / 3));
        for (int y = (int)guess.Y - rad; y <= (int)guess.Y + rad; y += step)
            for (int x = (int)guess.X - rad; x <= (int)guess.X + rad; x += step)
            {
                if (Blob(bin, w, h, x, y, max) is not { } b) continue;
                if (b.Area < mod * mod * 0.3) continue;
                long key = (long)Math.Round(b.X * 4) << 32 | (uint)Math.Round(b.Y * 4);
                if (!done.Add(key)) continue;
                bool ok = true;
                // 一格外面係白圈，兩格外面係黑框（八個方向）
                for (int dy = -1; dy <= 1 && ok; dy++)
                    for (int dx = -1; dx <= 1 && ok; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        if (Dark(b.X + dx * u.X + dy * v.X, b.Y + dx * u.Y + dy * v.Y)) ok = false;
                        else if (!Dark(b.X + 2 * (dx * u.X + dy * v.X), b.Y + 2 * (dx * u.Y + dy * v.Y))) ok = false;
                    }
                if (!ok) continue;
                double dist = (b.X - guess.X) * (b.X - guess.X) + (b.Y - guess.Y) * (b.Y - guess.Y);
                if (dist < best) { best = dist; hit = new Pt(b.X, b.Y); }
            }
        return hit;
    }

    private static string? SampleAndDecode(bool[] bin, int w, int h, Func<double, double, Pt> map, int ver)
    {
        int size = ver * 4 + 17;
        var m = new bool[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = map(x + 0.5, y + 0.5);
                int px = (int)Math.Floor(p.X), py = (int)Math.Floor(p.Y);
                if (px < 0 || py < 0 || px >= w || py >= h) return null;
                m[y, x] = bin[py * w + px];
            }
        return DecodeMatrix(m) ?? DecodeMatrix(Transpose(m));
    }

    private static bool[,] Transpose(bool[,] m)
    {
        int n = m.GetLength(0);
        var t = new bool[n, n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) t[x, y] = m[y, x];
        return t;
    }

    /// <summary>由一個取樣好嘅方陣（[y, x]，true = 黑）解讀文字；格式資訊讀唔到或者糾錯失敗回傳 null。</summary>
    public static string? DecodeMatrix(bool[,] m)
    {
        int size = m.GetLength(0);
        if ((size - 17) % 4 != 0) return null;
        int ver = (size - 17) / 4;
        if (ver < 1 || ver > 40) return null;
        // 格式資訊：兩份，揀同合法碼最近嗰個（最多錯 3 位）
        int f1 = 0, f2 = 0;
        bool G(int x, int y) => m[y, x];
        for (int i = 0; i <= 5; i++) if (G(8, i)) f1 |= 1 << i;
        if (G(8, 7)) f1 |= 1 << 6; if (G(8, 8)) f1 |= 1 << 7; if (G(7, 8)) f1 |= 1 << 8;
        for (int i = 9; i < 15; i++) if (G(14 - i, 8)) f1 |= 1 << i;
        for (int i = 0; i < 8; i++) if (G(size - 1 - i, 8)) f2 |= 1 << i;
        for (int i = 8; i < 15; i++) if (G(8, size - 15 + i)) f2 |= 1 << i;
        int bestDist = 99, eccIdx = 0, mask = 0;
        for (int e = 0; e < 4; e++)
            for (int k = 0; k < 8; k++)
            {
                int code = FormatBits((Ecc)e, k);
                foreach (int f in new[] { f1, f2 })
                {
                    int d = System.Numerics.BitOperations.PopCount((uint)(code ^ f));
                    if (d < bestDist) { bestDist = d; eccIdx = e; mask = k; }
                }
            }
        if (bestDist > 3) return null;
        var ecc = (Ecc)eccIdx;
        var func = DrawFunction(ver, new bool[size, size]);
        int raw = RawModules(ver) / 8;
        var all = new byte[raw];
        int idx = 0;
        foreach (var (x, y) in DataOrder(func))
        {
            if (idx >= raw * 8) break;
            bool bit = m[y, x] ^ MaskBit(mask, x, y);
            if (bit) all[idx >> 3] |= (byte)(0x80 >> (idx & 7));
            idx++;
        }
        // 拆返做一塊塊再糾錯
        int nb = NumBlocks[eccIdx][ver], el = EccPerBlock[eccIdx][ver];
        int numShort = nb - raw % nb, shortLen = raw / nb;
        var blocks = new byte[nb][];
        for (int i = 0; i < nb; i++) blocks[i] = new byte[shortLen + (i < numShort ? 0 : 1)];
        int p = 0;
        for (int i = 0; i <= shortLen - el; i++)
            for (int b = 0; b < nb; b++)
                if (i < blocks[b].Length - el) blocks[b][i] = all[p++];
        for (int i = 0; i < el; i++)
            for (int b = 0; b < nb; b++) blocks[b][blocks[b].Length - el + i] = all[p++];
        var data = new List<byte>();
        foreach (var b in blocks)
        {
            if (!RsCorrect(b, el)) return null;
            data.AddRange(b.Take(b.Length - el));
        }
        return ParseData(data.ToArray(), ver);
    }

    private static string? ParseData(byte[] data, int ver)
    {
        int pos = 0, total = data.Length * 8;
        int Read(int n)
        {
            if (pos + n > total) { pos = total + 1; return 0; }
            int v = 0;
            for (int i = 0; i < n; i++, pos++) v = v << 1 | ((data[pos >> 3] >> (7 - (pos & 7))) & 1);
            return v;
        }
        var sb = new StringBuilder();
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count == 0) return;
            string s;
            try { s = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
            catch { s = Encoding.Latin1.GetString(bytes.ToArray()); }
            sb.Append(s);
            bytes.Clear();
        }
        while (pos + 4 <= total)
        {
            int mode = Read(4);
            if (mode == 0) break;
            if (mode == 7) { int e = Read(8); if ((e & 0x80) != 0) { Read(8); if ((e & 0x40) != 0) Read(8); } continue; } // ECI：略過編號
            if (mode == 3) { Read(16); continue; }       // 結構化附加：略過
            if (mode == 5 || mode == 9) { if (mode == 9) Read(8); continue; } // FNC1
            if (mode != 1 && mode != 2 && mode != 4 && mode != 8) return null;
            int count = Read(CountBits(mode, ver));
            if (pos > total) return null;
            if (mode != 4) Flush();
            if (mode == 1)
            {
                for (; count >= 3; count -= 3) sb.Append(Read(10).ToString("000"));
                if (count == 2) sb.Append(Read(7).ToString("00"));
                else if (count == 1) sb.Append(Read(4).ToString("0"));
            }
            else if (mode == 2)
            {
                for (; count >= 2; count -= 2) { int v = Read(11); if (v >= 45 * 45) return null; sb.Append(Alnum[v / 45]).Append(Alnum[v % 45]); }
                if (count == 1) { int v = Read(6); if (v >= 45) return null; sb.Append(Alnum[v]); }
            }
            else if (mode == 4) { for (int i = 0; i < count; i++) bytes.Add((byte)Read(8)); }
            else
            {
                // 漢字模式（Shift-JIS 雙位元組）：冇 Shift-JIS 編碼表就用問號代替
                for (int i = 0; i < count; i++)
                {
                    int v = Read(13);
                    int hi = v / 0xC0, lo = v % 0xC0, sj = (hi << 8 | lo) + (((hi << 8 | lo) + 0x8140) <= 0x9FFC ? 0x8140 : 0xC140);
                    try { sb.Append(Encoding.GetEncoding(932).GetString(new[] { (byte)(sj >> 8), (byte)sj })); }
                    catch { sb.Append('?'); }
                }
            }
            if (pos > total) return null;
        }
        Flush();
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
