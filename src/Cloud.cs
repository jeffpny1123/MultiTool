using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Tools;

// =====================================================================
//  雲端伺服器：用家自己租一部 Vultr 雲端主機（Linux），拉比工具箱幫佢裝好 Docker，
//  之後喺程式入面一鍵開遊戲伺服器、Discord bot 等等，每樣用唔同 port，可以同時開幾樣。
//  • 用家經推薦連結開 Vultr 帳戶，貼 API key 入嚟；API key 用 DPAPI 加密存喺呢部電腦
//  • 程式自己整一條 SSH 鑰匙（data\cloud\id_ed25519），用 Windows 內置 OpenSSH 連去伺服器
//  • 費用由 Vultr 直接收；揀邊個 plan、幾時升級由用家決定
// =====================================================================

/// <summary>租咗嘅伺服器。</summary>
internal sealed class CloudServer
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Region { get; set; } = "";
    public string Plan { get; set; } = "";
    public string RootPass { get; set; } = "";   // Vultr 網頁控制台用（DPAPI）
    public bool Ready { get; set; }              // 已經裝好 Docker
}

/// <summary>伺服器上面行緊嘅一樣嘢（一個 Docker container）。</summary>
internal sealed class CloudService
{
    public string Id { get; set; } = "";         // container 名 = rt-<Id>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Ports { get; set; } = new(); // 例如 "25565/tcp"
    public Dictionary<string, string> Opt { get; set; } = new();
}

internal sealed class CloudData
{
    public string ApiKey { get; set; } = "";     // DPAPI
    public string SshKeyId { get; set; } = "";
    public CloudServer? Server { get; set; }
    public List<CloudService> Services { get; set; } = new();
}

internal static class CloudStore
{
    // GitHub 版：TODO 換做你自己嘅 Vultr 推薦連結（https://www.vultr.com/?ref=XXXXXXX）
    // Steam 版：一律用普通連結（Steam 唔准喺 App 入面引導去其他收費服務賺佣金）
    private const string RefUrl = "https://www.vultr.com/";
    public const string SignupUrl = Program.IsSteam ? "https://www.vultr.com/" : RefUrl;
    public const string ApiPageUrl = "https://my.vultr.com/settings/#settingsapi";

    public static string Dir => Path.Combine(AppPaths.Data, "cloud");
    private static string FilePath => Path.Combine(Dir, "cloud.json");
    public static string KeyFile => Path.Combine(Dir, "id_ed25519");
    public static string KnownHosts => Path.Combine(Dir, "known_hosts");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static CloudData Data { get; } = Load();

    private static CloudData Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<CloudData>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { AppPaths.LogError("讀雲端伺服器設定", ex); }
        return new CloudData();
    }

    public static void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, Opt)); }
        catch (Exception ex) { AppPaths.LogError("存雲端伺服器設定", ex); }
    }

    public static string ApiKey
    {
        get => Mail.MailStore.Unprotect(Data.ApiKey);
        set { Data.ApiKey = Mail.MailStore.Protect(value.Trim()); Save(); }
    }
}

// ---------------------------------------------------------------------
//  Vultr API v2
// ---------------------------------------------------------------------
internal sealed record VultrRegion(string Id, string City, string Country, string Continent)
{
    public override string ToString() => $"{City}（{Country}）";
}

internal sealed record VultrPlan(string Id, int Vcpu, int RamMb, int DiskGb, double Monthly, string Type)
{
    public override string ToString() =>
        Lang.F("{0} vCPU · {1} GB RAM · {2} GB 硬碟 · 每月 US${3}", Vcpu, (RamMb / 1024.0).ToString("0.#"), DiskGb, Monthly.ToString("0.##"));
}

internal static class Vultr
{
    private const string Api = "https://api.vultr.com/v2";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<JsonNode?> Call(HttpMethod method, string path, object? body = null, string? key = null)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (key ?? CloudStore.ApiKey));
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text; } catch { }
            throw new InvalidOperationException((int)resp.StatusCode switch
            {
                401 => Lang.T("API key 唔啱，或者未喺 Vultr 開啟 API。"),
                403 => Lang.T("Vultr 拒絕：請喺 API 設定「Access Control」加入你嘅 IP，或者揀「Allow All IPv4」。"),
                _ => "Vultr: " + (msg.Length > 300 ? msg[..300] : msg)
            });
        }
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    private static string S(JsonNode? n) => n?.ToString() ?? "";
    private static int I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : int.TryParse(S(n), out var j) ? j : 0;
    private static double D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.TryParse(S(n), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 0;

    /// <summary>試 API key：回傳帳戶餘額（負數 = 有錢喺入面）。</summary>
    public static async Task<(double Balance, double Pending)> AccountAsync(string? key = null)
    {
        var a = (await Call(HttpMethod.Get, "/account", null, key))?["account"];
        return (D(a?["balance"]), D(a?["pending_charges"]));
    }

    public static async Task<List<VultrRegion>> RegionsAsync()
    {
        var arr = (await Call(HttpMethod.Get, "/regions?per_page=500"))?["regions"]?.AsArray() ?? new JsonArray();
        return arr.Select(r => new VultrRegion(S(r?["id"]), S(r?["city"]), S(r?["country"]), S(r?["continent"])))
                  .Where(r => r.Id.Length > 0).OrderBy(r => r.Continent == "Asia" ? 0 : r.Continent == "North America" ? 1 : 2).ThenBy(r => r.City).ToList();
    }

    /// <summary>某個地區有得揀嘅 plan（一般雲端主機，至少 1 GB RAM），平到貴。</summary>
    public static async Task<List<VultrPlan>> PlansAsync(string region)
    {
        var arr = (await Call(HttpMethod.Get, "/plans?type=all&per_page=500"))?["plans"]?.AsArray() ?? new JsonArray();
        return arr.Where(p => p?["locations"]?.AsArray().Any(l => S(l) == region) == true)
                  .Select(p => new VultrPlan(S(p?["id"]), I(p?["vcpu_count"]), I(p?["ram"]), I(p?["disk"]), D(p?["monthly_cost"]), S(p?["type"])))
                  .Where(p => p.RamMb >= 1024 && (p.Type is "vc2" or "vhf" or "vhp"))
                  .OrderBy(p => p.Monthly).ThenBy(p => p.RamMb).ToList();
    }

    private static async Task<int> UbuntuAsync()
    {
        var arr = (await Call(HttpMethod.Get, "/os?per_page=500"))?["os"]?.AsArray() ?? new JsonArray();
        foreach (var name in new[] { "Ubuntu 24.04", "Ubuntu 22.04" })
        {
            var o = arr.FirstOrDefault(x => S(x?["name"]).StartsWith(name) && S(x?["arch"]) == "x64");
            if (o != null) return I(o["id"]);
        }
        throw new InvalidOperationException(Lang.T("Vultr 搵唔到 Ubuntu 系統。"));
    }

    private static async Task<string> SshKeyAsync()
    {
        if (CloudStore.Data.SshKeyId.Length > 0) return CloudStore.Data.SshKeyId;
        string pub = (await Ssh.EnsureKeyAsync()).Trim();
        var k = await Call(HttpMethod.Post, "/ssh-keys", new { name = Program.FileName + "-" + Environment.MachineName, ssh_key = pub });
        CloudStore.Data.SshKeyId = S(k?["ssh_key"]?["id"]);
        CloudStore.Save();
        return CloudStore.Data.SshKeyId;
    }

    // 第一次開機：裝 Docker、開防火牆（淨係開 SSH；之後每加一樣服務先開佢嘅 port）
    private const string Setup = """
#!/bin/bash
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y curl unzip ufw
curl -fsSL https://get.docker.com | sh
systemctl enable --now docker
ufw allow 22/tcp
ufw --force enable
mkdir -p /opt/rabi
touch /opt/rabi/ready
""";

    public static async Task<CloudServer> CreateAsync(string region, string plan, string label)
    {
        string keyId = await SshKeyAsync();
        int os = await UbuntuAsync();
        var r = await Call(HttpMethod.Post, "/instances", new
        {
            region, plan, os_id = os, label, hostname = "rabi-server", sshkey_id = new[] { keyId },
            user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(Setup.Replace("\r\n", "\n"))), backups = "disabled"
        });
        var i = r?["instance"];
        return new CloudServer
        {
            Id = S(i?["id"]), Label = label, Region = region, Plan = plan, Ip = S(i?["main_ip"]),
            RootPass = Mail.MailStore.Protect(S(i?["default_password"]))
        };
    }

    /// <summary>(IP, 狀態, plan)：status = pending / active；server_status = installingbooting / ok。</summary>
    public static async Task<(string Ip, string Status, string ServerStatus, string Power, string Plan)> GetAsync(string id)
    {
        var i = (await Call(HttpMethod.Get, "/instances/" + id))?["instance"];
        return (S(i?["main_ip"]), S(i?["status"]), S(i?["server_status"]), S(i?["power_status"]), S(i?["plan"]));
    }

    /// <summary>可以升級去邊啲 plan。</summary>
    public static async Task<List<string>> UpgradesAsync(string id)
    {
        var arr = (await Call(HttpMethod.Get, $"/instances/{id}/upgrades?type=plans"))?["upgrades"]?["plans"]?.AsArray() ?? new JsonArray();
        return arr.Select(S).ToList();
    }

    public static Task ChangePlanAsync(string id, string plan) => Call(HttpMethod.Patch, "/instances/" + id, new { plan });
    public static Task RebootAsync(string id) => Call(HttpMethod.Post, $"/instances/{id}/reboot");
    public static Task DeleteAsync(string id) => Call(HttpMethod.Delete, "/instances/" + id);
}

// ---------------------------------------------------------------------
//  SSH：用 Windows 內置 OpenSSH（ssh.exe / scp.exe / ssh-keygen.exe）
// ---------------------------------------------------------------------
internal static class Ssh
{
    /// <summary>OpenSSH 資料夾（程式係 32-bit，要經 Sysnative 先攞到 64-bit System32）。</summary>
    private static string? Dir
    {
        get
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var d in new[] { Path.Combine(win, "Sysnative", "OpenSSH"), Path.Combine(win, "System32", "OpenSSH") })
                if (File.Exists(Path.Combine(d, "ssh.exe"))) return d;
            return null;
        }
    }
    public static bool Available => Dir != null;
    private static string Tool(string name) => Path.Combine(Dir ?? throw new InvalidOperationException(
        Lang.T("呢部電腦冇 OpenSSH：請去 Windows「設定 → 系統 → 選用功能」加入「OpenSSH 用戶端」。")), name);

    private static async Task<(int Code, string Out)> Run(string exe, IEnumerable<string> args, int timeoutSec)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Lang.T("開唔到 SSH"));
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException(Lang.T("伺服器冇回應（逾時）。")); }
        return (p.ExitCode, (await o) + (await e));
    }

    /// <summary>未有鑰匙就整一條（冇密碼、淨係呢個 Windows 用戶讀得），回傳 public key。</summary>
    public static async Task<string> EnsureKeyAsync()
    {
        Directory.CreateDirectory(CloudStore.Dir);
        if (!File.Exists(CloudStore.KeyFile))
        {
            var (code, outp) = await Run(Tool("ssh-keygen.exe"), new[] { "-t", "ed25519", "-N", "", "-C", Program.FileName, "-f", CloudStore.KeyFile, "-q" }, 30);
            if (code != 0 || !File.Exists(CloudStore.KeyFile)) throw new InvalidOperationException(Lang.T("整唔到 SSH 鑰匙：") + outp);
            // OpenSSH 要求私鑰淨係自己讀得
            try { await Run("icacls.exe", new[] { CloudStore.KeyFile, "/inheritance:r", "/grant:r", Environment.UserName + ":F" }, 20); } catch { }
        }
        return File.ReadAllText(CloudStore.KeyFile + ".pub");
    }

    private static List<string> Common(string ip) => new()
    {
        "-i", CloudStore.KeyFile, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=" + CloudStore.KnownHosts, "-o", "ConnectTimeout=12", "-o", "ServerAliveInterval=15"
    };

    /// <summary>喺伺服器行一句指令。</summary>
    public static Task<(int Code, string Out)> ExecAsync(string ip, string command, int timeoutSec = 120)
    {
        var a = Common(ip); a.Add("root@" + ip); a.Add(command);
        return Run(Tool("ssh.exe"), a, timeoutSec);
    }

    /// <summary>上載檔案去伺服器某個資料夾。</summary>
    public static Task<(int Code, string Out)> UploadAsync(string ip, IEnumerable<string> files, string remoteDir, int timeoutSec = 1800)
    {
        var a = Common(ip); a.Add("-r"); a.AddRange(files); a.Add($"root@{ip}:{remoteDir}/");
        return Run(Tool("scp.exe"), a, timeoutSec);
    }

    /// <summary>開一個終端機視窗連去伺服器（遠端操作）。</summary>
    public static void OpenTerminal(string ip)
    {
        var psi = new ProcessStartInfo(Tool("ssh.exe")) { UseShellExecute = false, CreateNoWindow = false };
        var a = Common(ip);
        int bi = a.IndexOf("BatchMode=yes"); if (bi > 0) { a.RemoveAt(bi); a.RemoveAt(bi - 1); }
        a.Add("root@" + ip);
        foreach (var x in a) psi.ArgumentList.Add(x);
        Process.Start(psi);
    }

    /// <summary>shell 單引號包住（防止特殊字元）。</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";
}

// ---------------------------------------------------------------------
//  可以開嘅服務（全部用 Docker，一樣一個 container，自動重啟）
// ---------------------------------------------------------------------
internal sealed record CloudField(string Key, string Label, string Default, bool Secret = false);

internal sealed record CloudKind(string Key, string Name, string Icon, string Desc, string[] Ports, CloudField[] Fields, int RamMb,
    Func<CloudService, string> Run)
{
    public override string ToString() => Icon + " " + Name;
}

internal static class CloudKinds
{
    private static string Env(CloudService s, string name, string key) => $" -e {name}={Ssh.Q(s.Opt.GetValueOrDefault(key, ""))}";
    private static string Data(CloudService s, string inside) => $" -v /opt/rabi/{s.Id}:{inside}";
    /// <summary>host port 對 container 原本嘅 port（有機會搬咗去第二個 port 避開撞號）。</summary>
    private static string Map(CloudService s, CloudKind k) =>
        string.Concat(s.Ports.Select((p, i) => $" -p {p.Split('/')[0]}:{k.Ports[i]}"));
    private static string Base(CloudService s) => $"docker run -d --name rt-{s.Id} --restart unless-stopped";

    public static readonly CloudKind[] All =
    {
        new("minecraft", Lang.T("Minecraft（Java 版）"), "⛏", Lang.T("最受歡迎嘅方塊遊戲；自動下載最新版本伺服器。"),
            new[] { "25565/tcp" },
            new[] { new CloudField("motd", Lang.T("伺服器名稱"), "Rabi Server"), new CloudField("mem", Lang.T("記憶體（GB）"), "2"), new CloudField("version", Lang.T("版本（LATEST = 最新）"), "LATEST") },
            2048, s => Base(s) + Map(s, Find("minecraft")) + " -e EULA=TRUE" + Env(s, "MOTD", "motd") + $" -e MEMORY={Ssh.Q(s.Opt.GetValueOrDefault("mem", "2") + "G")}" + Env(s, "VERSION", "version") + Data(s, "/data") + " itzg/minecraft-server"),
        new("palworld", Lang.T("Palworld 幻獸帕魯"), "🐾", Lang.T("最多 32 人；建議 8 GB 記憶體或以上。"),
            new[] { "8211/udp", "27015/udp" },
            new[] { new CloudField("name", Lang.T("伺服器名稱"), "Rabi Palworld"), new CloudField("pass", Lang.T("伺服器密碼（可以留空）"), "", true), new CloudField("admin", Lang.T("管理員密碼"), "", true), new CloudField("players", Lang.T("最多幾多人"), "16") },
            8192, s => Base(s) + Map(s, Find("palworld")) + " -e PORT=8211" + Env(s, "SERVER_NAME", "name") + Env(s, "SERVER_PASSWORD", "pass") + Env(s, "ADMIN_PASSWORD", "admin") + Env(s, "PLAYERS", "players") + " -e MULTITHREADING=true -e COMMUNITY=false" + Data(s, "/palworld") + " thijsvanloef/palworld-server-docker:latest"),
        new("valheim", Lang.T("Valheim 英靈神殿"), "⚔", Lang.T("最多 10 人；密碼最少 5 個字。"),
            new[] { "2456/udp", "2457/udp" },
            new[] { new CloudField("name", Lang.T("伺服器名稱"), "Rabi Valheim"), new CloudField("world", Lang.T("世界名稱"), "Dedicated"), new CloudField("pass", Lang.T("密碼（最少 5 個字）"), "", true) },
            4096, s => Base(s) + Map(s, Find("valheim")) + Env(s, "SERVER_NAME", "name") + Env(s, "WORLD_NAME", "world") + Env(s, "SERVER_PASS", "pass") + $" -v /opt/rabi/{s.Id}/config:/config -v /opt/rabi/{s.Id}/data:/opt/valheim" + " --cap-add=sys_nice --stop-timeout 120 lloesche/valheim-server"),
        new("terraria", Lang.T("Terraria 泰拉瑞亞"), "🌳", Lang.T("第一次開會自動建立中型世界。"),
            new[] { "7777/tcp" },
            new[] { new CloudField("world", Lang.T("世界名稱"), "RabiWorld") },
            1024, s => Base(s) + Map(s, Find("terraria")) + Data(s, "/root/.local/share/Terraria/Worlds") + " -it ryshe/terraria:latest" + $" -world /root/.local/share/Terraria/Worlds/{Ssh.Q(s.Opt.GetValueOrDefault("world", "RabiWorld"))}.wld -autocreate 2"),
        new("botnode", Lang.T("Discord bot（Node.js）"), "🤖", Lang.T("上載你個 bot 資料夾（要有 package.json）；Token 會放入環境變數 DISCORD_TOKEN。"),
            Array.Empty<string>(),
            new[] { new CloudField("token", "Discord Bot Token", "", true), new CloudField("start", Lang.T("啟動指令"), "npm start") },
            512, s => Base(s) + Env(s, "DISCORD_TOKEN", "token") + Data(s, "/app") + " -w /app node:22-slim sh -c " + Ssh.Q("npm install --omit=dev && " + s.Opt.GetValueOrDefault("start", "npm start"))),
        new("botpy", Lang.T("Discord bot（Python）"), "🐍", Lang.T("上載你個 bot 資料夾（有 requirements.txt 會自動安裝）；Token 會放入環境變數 DISCORD_TOKEN。"),
            Array.Empty<string>(),
            new[] { new CloudField("token", "Discord Bot Token", "", true), new CloudField("start", Lang.T("啟動指令"), "python main.py") },
            512, s => Base(s) + Env(s, "DISCORD_TOKEN", "token") + Data(s, "/app") + " -w /app python:3.12-slim sh -c " + Ssh.Q("(test -f requirements.txt && pip install -r requirements.txt || true) && " + s.Opt.GetValueOrDefault("start", "python main.py"))),
        new("custom", Lang.T("自訂 Docker 映像"), "🧩", Lang.T("進階：任何 Docker Hub 映像，自己填 port。"),
            Array.Empty<string>(),
            new[] { new CloudField("image", Lang.T("映像（例如 nginx:latest）"), ""), new CloudField("ports", Lang.T("Port（例如 8080/tcp,9000/udp）"), "") },
            256, s => Base(s) + string.Concat(s.Ports.Select(p => $" -p {p.Split('/')[0]}:{p}")) + Data(s, "/data") + " " + Ssh.Q(s.Opt.GetValueOrDefault("image", ""))),
    };

    public static CloudKind Find(string key) => All.First(k => k.Key == key);

    /// <summary>分配 host port：同其他服務撞就逐個加一（幾個 port 一齊搬，保持次序）。</summary>
    public static List<string> AllocPorts(IEnumerable<string> wanted, IEnumerable<CloudService> others)
    {
        var used = new HashSet<string>(others.SelectMany(o => o.Ports), StringComparer.OrdinalIgnoreCase);
        var list = wanted.ToList();
        for (int shift = 0; shift < 500; shift++)
        {
            var cand = list.Select(p => { var a = p.Split('/'); return (int.Parse(a[0]) + shift * (list.Count > 1 ? 10 : 1)) + "/" + a[1]; }).ToList();
            if (!cand.Any(used.Contains)) return cand;
        }
        throw new InvalidOperationException(Lang.T("搵唔到空閒嘅 port。"));
    }

    /// <summary>喺伺服器建立並開始服務：開資料夾、上載檔案（bot）、docker run、防火牆開 port。</summary>
    public static async Task CreateAsync(string ip, CloudService s, IEnumerable<string>? upload, bool unzip = false)
    {
        var k = Find(s.Kind);
        var (c1, o1) = await Ssh.ExecAsync(ip, $"mkdir -p /opt/rabi/{s.Id}");
        if (c1 != 0) throw new InvalidOperationException(o1);
        if (upload != null && upload.Any())
        {
            var (c2, o2) = await Ssh.UploadAsync(ip, upload, $"/opt/rabi/{s.Id}");
            if (c2 != 0) throw new InvalidOperationException(Lang.T("上載失敗：") + o2);
            if (unzip)
                foreach (var f in upload)
                    await Ssh.ExecAsync(ip, $"cd /opt/rabi/{s.Id} && unzip -o -q {Ssh.Q(Path.GetFileName(f))} && rm -f {Ssh.Q(Path.GetFileName(f))}", 300);
        }
        string fw = string.Concat(s.Ports.Select(p => $" && ufw allow {p}"));
        var (c3, o3) = await Ssh.ExecAsync(ip, $"docker rm -f rt-{s.Id} >/dev/null 2>&1; {k.Run(s)}{fw}", 900);
        if (c3 != 0) throw new InvalidOperationException(o3.Trim());
    }

    public static async Task RemoveAsync(string ip, CloudService s)
    {
        string fw = string.Concat(s.Ports.Select(p => $"; ufw delete allow {p}"));
        await Ssh.ExecAsync(ip, $"docker rm -f rt-{s.Id}{fw}; rm -rf /opt/rabi/{s.Id}");
    }

    /// <summary>每個服務嘅狀態（Up 3 hours / Exited (1) ...）。</summary>
    public static async Task<Dictionary<string, string>> StatusAsync(string ip)
    {
        var (code, outp) = await Ssh.ExecAsync(ip, "docker ps -a --filter name=rt- --format '{{.Names}}|{{.Status}}'", 30);
        var d = new Dictionary<string, string>();
        if (code != 0) return d;
        foreach (var line in outp.Split('\n'))
        {
            var p = line.Trim().Split('|', 2);
            if (p.Length == 2 && p[0].StartsWith("rt-")) d[p[0][3..]] = p[1];
        }
        return d;
    }

    /// <summary>伺服器用量：(CPU 負載, RAM 已用 / 總共 MB, 硬碟已用 / 總共 GB)。</summary>
    public static async Task<string> UsageAsync(string ip)
    {
        var (code, o) = await Ssh.ExecAsync(ip, "cut -d' ' -f1 /proc/loadavg; nproc; free -m | awk 'NR==2{print $3\" \"$2}'; df -BG / | awk 'NR==2{print $3\" \"$2}'", 20);
        if (code != 0) return "";
        var l = o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (l.Length < 4) return "";
        var ram = l[2].Split(' '); var disk = l[3].Split(' ');
        return Lang.F("CPU 負載 {0}（{1} 核）　RAM {2} / {3} MB　硬碟 {4} / {5}", l[0], l[1], ram[0], ram.ElementAtOrDefault(1) ?? "?", disk[0], disk.ElementAtOrDefault(1) ?? "?");
    }

    /// <summary>將資料夾打包做 zip（略過 node_modules、.git、venv），上載後喺伺服器解壓。</summary>
    public static string ZipFolder(string folder)
    {
        string zip = Path.Combine(Path.GetTempPath(), Program.FileName + "-bot-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        string[] skip = { "node_modules", ".git", "venv", ".venv", "__pycache__" };
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(folder, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => skip.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            z.CreateEntryFromFile(f, rel.Replace('\\', '/'));
        }
        return zip;
    }
}
