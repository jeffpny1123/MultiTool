using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  遊戲伺服器：用 SteamCMD 一撳安裝 / 更新專用伺服器，開關、設定、備份、俾朋友連入嚟。
// =====================================================================

/// <summary>SteamCMD：下載、執行。</summary>
internal static class SteamCmd
{
    private const string Url = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public static string Dir => Path.Combine(AppPaths.Data, "tools", "steamcmd");
    public static string Exe => Path.Combine(Dir, "steamcmd.exe");
    public static bool Ready => File.Exists(Exe);

    public static async Task InstallAsync(Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string zip = Path.Combine(Dir, "steamcmd.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
            using var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(Lang.T("下載 SteamCMD…"), total > 0 ? (double)done / total : -1);
            }
        }
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, Dir, true), ct);
        try { File.Delete(zip); } catch { }
        progress(Lang.T("完成"), 1);
    }

    public static void Delete()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }

    private static readonly Regex Progress = new(@"progress:\s*([\d.]+)", RegexOptions.Compiled);

    /// <summary>安裝 / 更新一個專用伺服器（匿名登入）。log 逐行回報；progress 0-1。</summary>
    public static async Task UpdateAppAsync(int appId, string dir, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        if (!Ready) throw new InvalidOperationException(Lang.T("未有 SteamCMD：請去「工具庫」重新安裝遊戲伺服器。"));
        Directory.CreateDirectory(dir);
        // SteamCMD 第一次開會自己更新，有時要行多次先成功
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            bool ok = false;
            var psi = new ProcessStartInfo(Exe, $"+force_install_dir \"{dir}\" +login anonymous +app_update {appId} validate +quit")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, WorkingDirectory = Dir, StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi)!;
            void Line(string? l)
            {
                if (string.IsNullOrWhiteSpace(l)) return;
                log(l);
                if (l.Contains($"Success! App '{appId}'")) ok = true;
                var m = Progress.Match(l);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v)) progress(Math.Clamp(v / 100, 0, 1));
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                await p.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            if (ok) return;
            log(Lang.F("SteamCMD 未完成（代碼 {0}），再試…", p.ExitCode));
        }
        throw new InvalidOperationException(Lang.T("SteamCMD 安裝失敗，睇下控制台訊息。"));
    }
}

internal sealed record PortSpec(string Proto, int Offset);

/// <summary>一隻遊戲嘅專用伺服器資料。</summary>
internal sealed class GameDef
{
    public required string Id, Name;
    public int AppId;
    public string Exe = "";
    public int DefPort, DefPlayers = 16;
    public PortSpec[] Ports = Array.Empty<PortSpec>();
    public bool HasName = true, HasPassword = true, HasAdmin, HasPlayers = true;
    public int MinPassword;
    public Func<GameSrv, string> Args = _ => "";
    public Action<GameSrv>? Configure;
    public Func<GameSrv, string?> SaveDir = _ => null;
    public Func<GameSrv, string?>? LogFile;
    public string? StopCommand;
    public string Note = "", Size = "";
}

internal static class GameDefs
{
    private static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";
    private static string Env(Environment.SpecialFolder f, params string[] p) => Path.Combine(new[] { Environment.GetFolderPath(f) }.Concat(p).ToArray());

    public static readonly List<GameDef> All = new()
    {
        new()
        {
            Id = "palworld", Name = "Palworld", AppId = 2394010, Exe = "PalServer.exe", DefPort = 8211, DefPlayers = 32,
            Ports = new[] { new PortSpec("UDP", 0) }, HasAdmin = true, Size = "~6 GB",
            Args = s => $"-port={s.Port} -players={s.Players} -useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS",
            Configure = s =>
            {
                var ini = Path.Combine(s.GameDir, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
                string text = File.Exists(ini) ? File.ReadAllText(ini) : "";
                if (!text.Contains("OptionSettings="))
                {
                    var def = Path.Combine(s.GameDir, "DefaultPalWorldSettings.ini");
                    if (!File.Exists(def)) return;
                    text = File.ReadAllText(def);
                }
                text = PalSet(text, "ServerName", "\"" + s.Name.Replace("\"", "") + "\"");
                text = PalSet(text, "ServerPassword", "\"" + s.Password.Replace("\"", "") + "\"");
                text = PalSet(text, "AdminPassword", "\"" + s.Admin.Replace("\"", "") + "\"");
                text = PalSet(text, "ServerPlayerMaxNum", s.Players.ToString());
                text = PalSet(text, "PublicPort", s.Port.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
                File.WriteAllText(ini, text);
            },
            SaveDir = s => Path.Combine(s.GameDir, "Pal", "Saved", "SaveGames"),
            Note = Lang.T("管理員密碼最少 8 個英文字母或數字。")
        },
        new()
        {
            Id = "valheim", Name = "Valheim", AppId = 896660, Exe = "valheim_server.exe", DefPort = 2456, DefPlayers = 10,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("UDP", 1) }, HasPlayers = false, MinPassword = 5, Size = "~1 GB",
            Args = s => $"-nographics -batchmode -name {Q(s.Name)} -port {s.Port} -world \"Dedicated\" -password {Q(s.Password)} -savedir {Q(s.SaveRoot)} -public 1",
            SaveDir = s => s.SaveRoot,
            Note = Lang.T("Valheim 一定要密碼（最少 5 個字），而且伺服器名唔可以包含密碼。")
        },
        new()
        {
            Id = "enshrouded", Name = "Enshrouded", AppId = 2278520, Exe = "enshrouded_server.exe", DefPort = 15636, DefPlayers = 16,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("UDP", 1) }, HasAdmin = true, Size = "~3 GB",
            Configure = s =>
            {
                var f = Path.Combine(s.GameDir, "enshrouded_server.json");
                JsonObject o;
                try { o = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? new JsonObject() : new JsonObject(); }
                catch { o = new JsonObject(); }
                o["name"] = s.Name;
                o["saveDirectory"] ??= "./savegame";
                o["logDirectory"] ??= "./logs";
                o["ip"] ??= "0.0.0.0";
                o["gamePort"] = s.Port;
                o["queryPort"] = s.Port + 1;
                o["slotCount"] = s.Players;
                if (o["userGroups"] is JsonArray groups && groups.Count > 0)
                {
                    foreach (var g in groups.OfType<JsonObject>())
                    {
                        bool admin = string.Equals(g["name"]?.ToString(), "Admin", StringComparison.OrdinalIgnoreCase);
                        g["password"] = admin && s.Admin.Length > 0 ? s.Admin : s.Password;
                    }
                }
                else o["password"] = s.Password;
                File.WriteAllText(f, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            },
            SaveDir = s => Path.Combine(s.GameDir, "savegame"),
            LogFile = s => Path.Combine(s.GameDir, "logs", "enshrouded_server.log"),
            Note = Lang.T("管理員密碼 = Admin 群組密碼；其他群組用返伺服器密碼。")
        },
        new()
        {
            Id = "7dtd", Name = "7 Days to Die", AppId = 294420, Exe = "7DaysToDieServer.exe", DefPort = 26900, DefPlayers = 8,
            Ports = new[] { new PortSpec("TCP", 0), new PortSpec("UDP", 0), new PortSpec("UDP", 1), new PortSpec("UDP", 2) }, Size = "~15 GB",
            Args = s => $"-quit -batchmode -nographics -configfile=serverconfig.xml -dedicated -logfile {Q(Path.Combine(s.SaveRoot, "output_log.txt"))}",
            Configure = s =>
            {
                var f = Path.Combine(s.GameDir, "serverconfig.xml");
                if (!File.Exists(f)) return;
                string x = File.ReadAllText(f);
                string Esc(string v) => System.Security.SecurityElement.Escape(v) ?? "";
                string Set(string t, string k, string v) =>
                    Regex.Replace(t, $@"(<property\s+name=""{k}""\s+value="")[^""]*("")", m => m.Groups[1].Value + Esc(v) + m.Groups[2].Value);
                x = Set(x, "ServerName", s.Name);
                x = Set(x, "ServerPassword", s.Password);
                x = Set(x, "ServerMaxPlayerCount", s.Players.ToString());
                x = Set(x, "ServerPort", s.Port.ToString());
                File.WriteAllText(f, x);
            },
            SaveDir = _ => Env(Environment.SpecialFolder.ApplicationData, "7DaysToDie", "Saves"),
            LogFile = s => Path.Combine(s.SaveRoot, "output_log.txt")
        },
        new()
        {
            Id = "satisfactory", Name = "Satisfactory", AppId = 1690800, Exe = "FactoryServer.exe", DefPort = 7777,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("TCP", 0), new PortSpec("TCP", 1111) },
            HasName = false, HasPassword = false, HasPlayers = false, Size = "~8 GB",
            Args = s => $"-log -unattended -Port={s.Port}",
            SaveDir = _ => Env(Environment.SpecialFolder.LocalApplicationData, "FactoryGame", "Saved", "SaveGames"),
            LogFile = s => Path.Combine(s.GameDir, "FactoryGame", "Saved", "Logs", "FactoryGame.log"),
            Note = Lang.T("伺服器名同密碼喺遊戲入面第一次連線時設定（Server Manager）。")
        },
        new()
        {
            Id = "vrising", Name = "V Rising", AppId = 1829350, Exe = "VRisingServer.exe", DefPort = 9876, DefPlayers = 40,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("UDP", 1) }, Size = "~2 GB",
            Args = s => $"-persistentDataPath {Q(s.SaveRoot)} -serverName {Q(s.Name)} -saveName \"world1\" -logFile {Q(Path.Combine(s.SaveRoot, "VRisingServer.log"))}",
            Configure = s =>
            {
                var dst = Path.Combine(s.SaveRoot, "Settings", "ServerHostSettings.json");
                var src = Path.Combine(s.GameDir, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json");
                if (!File.Exists(dst))
                {
                    if (!File.Exists(src)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst);
                }
                if (JsonNode.Parse(File.ReadAllText(dst)) is not JsonObject o) return;
                o["Name"] = s.Name;
                o["Password"] = s.Password;
                o["MaxConnectedUsers"] = s.Players;
                o["Port"] = s.Port;
                o["QueryPort"] = s.Port + 1;
                File.WriteAllText(dst, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            },
            SaveDir = s => Path.Combine(s.SaveRoot, "Saves"),
            LogFile = s => Path.Combine(s.SaveRoot, "VRisingServer.log")
        },
        new()
        {
            Id = "arksa", Name = "ARK: Survival Ascended", AppId = 2430930, Exe = @"ShooterGame\Binaries\Win64\ArkAscendedServer.exe", DefPort = 7777, DefPlayers = 20,
            Ports = new[] { new PortSpec("UDP", 0) }, HasAdmin = true, Size = "~15 GB",
            Args = s => "TheIsland_WP?listen?SessionName=" + s.Name.Replace("?", "").Replace("\"", "") +
                        (s.Password.Length > 0 ? "?ServerPassword=" + s.Password.Replace("?", "") : "") +
                        (s.Admin.Length > 0 ? "?ServerAdminPassword=" + s.Admin.Replace("?", "") : "") +
                        $"?Port={s.Port} -WinLiveMaxPlayers={s.Players} -log",
            SaveDir = s => Path.Combine(s.GameDir, "ShooterGame", "Saved", "SavedArks"),
            LogFile = s => Path.Combine(s.GameDir, "ShooterGame", "Saved", "Logs", "ShooterGame.log")
        },
        new()
        {
            Id = "rust", Name = "Rust", AppId = 258550, Exe = "RustDedicated.exe", DefPort = 28015, DefPlayers = 50,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("UDP", 1), new PortSpec("TCP", 2) }, HasPassword = false, HasAdmin = true, Size = "~10 GB",
            Args = s => $"-batchmode -nographics +server.port {s.Port} +server.queryport {s.Port + 1} +server.hostname {Q(s.Name)} " +
                        $"+server.maxplayers {s.Players} +server.identity \"multitool\" +server.worldsize 3500 +server.seed 12345 " +
                        $"+rcon.port {s.Port + 2} +rcon.web 1" + (s.Admin.Length > 0 ? $" +rcon.password {Q(s.Admin)}" : "") +
                        $" -logfile {Q(Path.Combine(s.SaveRoot, "rust.log"))}",
            SaveDir = s => Path.Combine(s.GameDir, "server", "multitool"),
            LogFile = s => Path.Combine(s.SaveRoot, "rust.log"),
            StopCommand = "quit",
            Note = Lang.T("Rust 冇伺服器密碼；管理員密碼 = RCON 密碼。")
        },
        new()
        {
            Id = "zomboid", Name = "Project Zomboid", AppId = 380870, Exe = "StartServer64.bat", DefPort = 16261,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("UDP", 1) }, HasName = false, HasPassword = false, HasPlayers = false, HasAdmin = true, Size = "~3 GB",
            Args = s => "-servername multitool" + (s.Admin.Length > 0 ? " -adminpassword " + Q(s.Admin) : ""),
            SaveDir = _ => Env(Environment.SpecialFolder.UserProfile, "Zomboid", "Saves", "Multiplayer"),
            StopCommand = "quit",
            Note = Lang.T("其他設定（密碼、人數等）喺 %UserProfile%\\Zomboid\\Server\\multitool.ini。")
        },
        new()
        {
            Id = "cs2", Name = "Counter-Strike 2", AppId = 730, Exe = @"game\bin\win64\cs2.exe", DefPort = 27015, DefPlayers = 10,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("TCP", 0) }, HasAdmin = true, Size = "~35 GB",
            Args = s => $"-dedicated -console -usercon -port {s.Port} -maxplayers {s.Players} +map de_dust2 +game_type 0 +game_mode 1 " +
                        $"+hostname {Q(s.Name)}" + (s.Password.Length > 0 ? $" +sv_password {Q(s.Password)}" : "") +
                        (s.Admin.Length > 0 ? $" +rcon_password {Q(s.Admin)}" : ""),
            StopCommand = "quit",
            Note = Lang.T("想喺伺服器列表出現要 GSLT token：喺「額外參數」加 +sv_setsteamaccount 你嘅token。")
        },
        new()
        {
            Id = "custom", Name = Lang.T("其他 Steam 遊戲（自訂）"), DefPort = 27015,
            Ports = new[] { new PortSpec("UDP", 0), new PortSpec("TCP", 0) }, HasAdmin = true,
            Args = s => s.CustomArgs.Replace("{port}", s.Port.ToString()).Replace("{players}", s.Players.ToString())
                .Replace("{name}", s.Name).Replace("{password}", s.Password).Replace("{admin}", s.Admin).Replace("{save}", s.SaveRoot),
            SaveDir = s => s.SaveRoot,
            Note = Lang.T("填專用伺服器嘅 Steam App ID（要支援匿名下載）、執行檔路徑同參數；參數可以用 {port} {players} {name} {password} {admin} {save}。")
        },
    };

    private static string PalSet(string text, string key, string value) =>
        Regex.Replace(text, $@"(?<=[(,]){key}=(""[^""]*""|[^,)]*)", key + "=" + value.Replace("$", "$$"));

    public static GameDef Get(string id) => All.First(d => d.Id == id);
}

/// <summary>一個伺服器（設定 + 執行狀態）。存喺 &lt;根目錄&gt;\&lt;id&gt;\server.txt。</summary>
internal sealed class GameSrv
{
    public enum State { Stopped, Installing, Starting, Running, Stopping }

    public readonly GameDef Def;
    public string Name = "MultiTool Server", Password = "", Admin = "", ExtraArgs = "";
    public int Port, Players;
    public bool AutoStart, AutoRestart = true, Upnp;
    public int BackupMinutes = 30, BackupKeep = 10;
    public int CustomAppId;
    public string CustomExe = "", CustomArgs = "";
    public bool Installed;

    public State Status { get; private set; } = State.Stopped;
    public string StatusText = "";
    public double Progress = -1;
    public DateTime StartedAt;
    public DateTime LastBackup = DateTime.MinValue;
    public string? Problem; // 最近出嘅問題（可以問 AI）
    private Process? _proc;
    private bool _stopping;
    private CancellationTokenSource? _installCts;
    private readonly List<string> _log = new();
    public event Action? Changed;
    public event Action<string>? LogLine;

    /// <summary>資料夾名（內置遊戲 = 遊戲 id；自訂可以有好多個：custom-1、custom-2…）。</summary>
    public readonly string Key;

    public GameSrv(GameDef def, string? key = null)
    {
        Def = def;
        Key = key ?? def.Id;
        Port = def.DefPort;
        Players = def.DefPlayers;
    }

    public string Dir => Path.Combine(GameServers.Root, Key);
    public bool IsCustom => Def.Id == "custom";

    /// <summary>顯示用名稱（自訂伺服器用佢自己個名）。</summary>
    public string DisplayName => !IsCustom ? Def.Name
        : (Name.Trim().Length > 0 && Name != "MultiTool Server" ? Name.Trim() : Lang.T("自訂伺服器")) + (CustomAppId > 0 ? $" (App {CustomAppId})" : "");
    public string GameDir => Path.Combine(Dir, "game");
    public string SaveRoot => Path.Combine(Dir, "save");
    public string BackupDir => Path.Combine(Dir, "backups");
    private string ConfigFile => Path.Combine(Dir, "server.txt");
    public int AppId => Def.Id == "custom" ? CustomAppId : Def.AppId;
    public string ExePath => Path.Combine(GameDir, Def.Id == "custom" ? CustomExe : Def.Exe);
    public string? SavePath => Def.SaveDir(this);
    public bool Busy => Status is State.Installing or State.Starting or State.Stopping;
    public bool Running => Status is State.Running or State.Starting;

    public string[] LogSnapshot() { lock (_log) return _log.ToArray(); }

    public void Log(string line)
    {
        line = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > 600) _log.RemoveRange(0, 100);
        }
        LogLine?.Invoke(line);
    }

    private void Set(State s, string text, double progress = -1)
    {
        Status = s;
        StatusText = text;
        Progress = progress;
        Changed?.Invoke();
        GameServers.RaiseChanged();
    }

    // ---------- 設定檔 ----------
    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            foreach (var line in File.ReadAllLines(ConfigFile))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                int I(int d) => int.TryParse(v, out var x) ? x : d;
                string S() { try { return Encoding.UTF8.GetString(Convert.FromBase64String(v)); } catch { return ""; } }
                switch (k)
                {
                    case "name": Name = S(); break;
                    case "password": Password = S(); break;
                    case "admin": Admin = S(); break;
                    case "extra": ExtraArgs = S(); break;
                    case "port": Port = Math.Clamp(I(Def.DefPort), 1, 65535); break;
                    case "players": Players = Math.Clamp(I(Def.DefPlayers), 1, 500); break;
                    case "autostart": AutoStart = v == "1"; break;
                    case "autorestart": AutoRestart = v != "0"; break;
                    case "upnp": Upnp = v == "1"; break;
                    case "backupmin": BackupMinutes = Math.Clamp(I(30), 0, 1440); break;
                    case "backupkeep": BackupKeep = Math.Clamp(I(10), 1, 100); break;
                    case "appid": CustomAppId = I(0); break;
                    case "exe": CustomExe = S(); break;
                    case "args": CustomArgs = S(); break;
                    case "installed": Installed = v == "1"; break;
                }
            }
        }
        catch (Exception ex) { AppPaths.LogError("讀伺服器設定", ex); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string B(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            File.WriteAllLines(ConfigFile, new[]
            {
                "name=" + B(Name), "password=" + B(Password), "admin=" + B(Admin), "extra=" + B(ExtraArgs),
                "port=" + Port, "players=" + Players, "autostart=" + (AutoStart ? 1 : 0), "autorestart=" + (AutoRestart ? 1 : 0),
                "upnp=" + (Upnp ? 1 : 0), "backupmin=" + BackupMinutes, "backupkeep=" + BackupKeep,
                "appid=" + CustomAppId, "exe=" + B(CustomExe), "args=" + B(CustomArgs), "installed=" + (Installed ? 1 : 0)
            });
        }
        catch (Exception ex) { AppPaths.LogError("儲存伺服器設定", ex); }
    }

    /// <summary>設定有冇問題（開始前檢查）。</summary>
    public string? Validate()
    {
        if (Def.Id == "custom" && (CustomAppId <= 0 || CustomExe.Trim().Length == 0)) return Lang.T("請填 App ID 同執行檔。");
        if (Def.MinPassword > 0 && Password.Length < Def.MinPassword) return Lang.F("密碼最少要 {0} 個字。", Def.MinPassword);
        if (Def.Id == "valheim" && Password.Length > 0 && Name.Contains(Password, StringComparison.OrdinalIgnoreCase))
            return Lang.T("伺服器名唔可以包含密碼。");
        if (Def.Id == "palworld" && Admin.Length > 0 && Admin.Length < 8) return Lang.T("管理員密碼最少 8 個字。");
        return null;
    }

    // ---------- 安裝 / 更新 ----------
    private static readonly Regex AcfNum = new(@"""(BytesToDownload|BytesDownloaded|BytesToStage|BytesStaged)""\s+""(\d+)""", RegexOptions.Compiled);

    private static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" : $"{b / (double)(1L << 20):0} MB";

    /// <summary>睇 SteamCMD 寫嘅 appmanifest 計進度（佢嘅輸出唔一定即時出）。</summary>
    private void PollManifest()
    {
        try
        {
            var f = Path.Combine(GameDir, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(f)) return;
            string text;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) text = r.ReadToEnd();
            var v = AcfNum.Matches(text).ToDictionary(m => m.Groups[1].Value, m => long.Parse(m.Groups[2].Value));
            long toDl = v.GetValueOrDefault("BytesToDownload"), dl = v.GetValueOrDefault("BytesDownloaded");
            long toSt = v.GetValueOrDefault("BytesToStage"), st = v.GetValueOrDefault("BytesStaged");
            if (toDl > 0 && dl < toDl)
            {
                Progress = (double)dl / toDl;
                StatusText = Lang.F("下載緊 {0} / {1}（{2:P0}）", Gb(dl), Gb(toDl), Progress);
            }
            else if (toSt > 0 && st < toSt)
            {
                Progress = (double)st / toSt;
                StatusText = Lang.F("安裝緊 {0} / {1}（{2:P0}）", Gb(st), Gb(toSt), Progress);
            }
            else return;
            Changed?.Invoke();
            GameServers.RaiseChanged();
        }
        catch { }
    }

    public async Task InstallAsync()
    {
        if (Busy) return;
        if (Running) { Log(Lang.T("請先停止伺服器再更新。")); return; }
        if (AppId <= 0) { Log(Lang.T("請先填 App ID。")); return; }
        _installCts = new CancellationTokenSource();
        Set(State.Installing, Lang.T("準備緊 SteamCMD（第一次會自我更新，要等一陣）…"), 0);
        Log(Lang.F("用 SteamCMD 安裝 / 更新 {0}（App {1}）…", DisplayName, AppId));
        using var poll = new System.Threading.Timer(_ => PollManifest(), null, 1000, 1000);
        try
        {
            await Task.Run(() => SteamCmd.UpdateAppAsync(AppId, GameDir, line =>
                {
                    Log(line);
                    if (line.Contains("Checking for available update", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Downloading update", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = Lang.T("SteamCMD 自我更新緊…");
                        Changed?.Invoke();
                    }
                    else if (line.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = Lang.T("已連線 Steam，準備下載…");
                        Changed?.Invoke();
                    }
                },
                p => { Progress = p; StatusText = Lang.F("下載 / 更新緊… {0:P0}", p); Changed?.Invoke(); GameServers.RaiseChanged(); }, _installCts.Token));
            Installed = true;
            Save();
            Log(Lang.T("✓ 安裝 / 更新完成"));
            Problem = null;
            Set(State.Stopped, Lang.T("已安裝，可以開始"));
        }
        catch (OperationCanceledException) { Log(Lang.T("已取消")); Set(State.Stopped, Lang.T("已取消")); }
        catch (Exception ex)
        {
            Log(Lang.T("安裝失敗：") + ex.Message);
            Problem = Lang.T("安裝失敗：") + ex.Message;
            AppPaths.LogError("遊戲伺服器安裝 " + Def.Id, ex);
            Set(State.Stopped, Lang.T("安裝失敗：") + ex.Message);
        }
        finally { _installCts = null; }
    }

    /// <summary>呢隻遊戲常用嘅設定檔（檔案分頁用）。</summary>
    public IEnumerable<string> ConfigFiles()
    {
        string G(params string[] p) => Path.Combine(new[] { GameDir }.Concat(p).ToArray());
        string S(params string[] p) => Path.Combine(new[] { SaveRoot }.Concat(p).ToArray());
        string U(params string[] p) => Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Concat(p).ToArray());
        return (Def.Id switch
        {
            "palworld" => new[] { G("Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini"), G("Pal", "Saved", "Config", "WindowsServer", "Engine.ini") },
            "valheim" => new[] { S("adminlist.txt"), S("bannedlist.txt"), S("permittedlist.txt") },
            "enshrouded" => new[] { G("enshrouded_server.json") },
            "7dtd" => new[] { G("serverconfig.xml"), G("serveradmin.xml") },
            "vrising" => new[] { S("Settings", "ServerHostSettings.json"), S("Settings", "ServerGameSettings.json"), S("Settings", "adminlist.txt") },
            "arksa" => new[] { G("ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"), G("ShooterGame", "Saved", "Config", "WindowsServer", "Game.ini") },
            "rust" => new[] { G("server", "multitool", "cfg", "server.cfg"), G("server", "multitool", "cfg", "users.cfg") },
            "zomboid" => new[] { U("Zomboid", "Server", "multitool.ini"), U("Zomboid", "Server", "multitool_SandboxVars.lua") },
            "cs2" => new[] { G("game", "csgo", "cfg", "server.cfg") },
            "satisfactory" => new[] { G("FactoryGame", "Saved", "Config", "WindowsServer", "Game.ini"), G("FactoryGame", "Saved", "Config", "WindowsServer", "ServerSettings.ini") },
            _ => Array.Empty<string>()
        }).Where(File.Exists);
    }

    public string? LogPath => Def.LogFile?.Invoke(this);

    public void CancelInstall() => _installCts?.Cancel();

    // ---------- 開 / 停 ----------
    public async Task StartAsync()
    {
        if (Busy || Running) return;
        if (!Installed || !File.Exists(ExePath)) { Set(State.Stopped, Lang.T("未安裝：撳「安裝 / 更新」先。")); return; }
        if (Validate() is { } err) { Set(State.Stopped, err); Log(err); return; }
        Set(State.Starting, Lang.T("開緊…"));
        try
        {
            Directory.CreateDirectory(SaveRoot);
            try { Def.Configure?.Invoke(this); }
            catch (Exception ex) { Log(Lang.T("寫設定檔失敗：") + ex.Message); }
            string args = Def.Args(this) + (ExtraArgs.Trim().Length > 0 ? " " + ExtraArgs.Trim() : "");
            var psi = new ProcessStartInfo(ExePath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            Log(Lang.T("啟動：") + Path.GetFileName(ExePath) + " " + MaskArgs(args));
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
            p.Exited += (_, _) => OnExited(p);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            _stopping = false;
            StartedAt = DateTime.Now;
            StartTail();
            await Task.Delay(1500);
            if (p.HasExited) return; // OnExited 處理咗
            Problem = null;
            Set(State.Running, Lang.T("運行中"));
            if (Upnp) _ = MapPortsAsync();
        }
        catch (Exception ex)
        {
            Log(Lang.T("開唔到：") + ex.Message);
            Problem = Lang.T("開唔到：") + ex.Message;
            Set(State.Stopped, Lang.T("開唔到：") + ex.Message);
        }
    }

    private string MaskArgs(string a)
    {
        foreach (var secret in new[] { Password, Admin }.Where(x => x.Length > 0)) a = a.Replace(secret, "****");
        return a;
    }

    private void OnExited(Process p)
    {
        if (p != _proc) return;
        _proc = null;
        StopTail();
        int code = 0;
        try { code = p.ExitCode; } catch { }
        if (Upnp) UnmapPorts(); // 伺服器閂咗（點閂都好）：端口即刻閂返；自動重開會再開
        if (_stopping)
        {
            Log(Lang.T("伺服器已停止"));
            Set(State.Stopped, Lang.T("已停止"));
            return;
        }
        Log(Lang.F("伺服器意外結束（代碼 {0}）", code));
        Problem = Lang.F("伺服器意外結束（代碼 {0}）", code);
        bool restart = AutoRestart && (DateTime.Now - StartedAt).TotalSeconds > 30; // 一開就死嘅唔好無限重開
        Set(State.Stopped, Lang.F("意外結束（代碼 {0}）", code) + (restart ? Lang.T("，10 秒後自動重開") : ""));
        if (restart)
            Task.Delay(10000).ContinueWith(_ => GameServers.Ui(() => { if (Status == State.Stopped) _ = StartAsync(); }));
    }

    /// <summary>停：先用遊戲指令 / Ctrl+C 叫佢儲存再閂，等唔到就強制結束。</summary>
    public async Task StopAsync()
    {
        var p = _proc;
        if (p == null || p.HasExited) { Set(State.Stopped, Lang.T("已停止")); return; }
        _stopping = true;
        Set(State.Stopping, Lang.T("停緊（等佢儲存）…"));
        try
        {
            if (Def.StopCommand != null)
            {
                try { await p.StandardInput.WriteLineAsync(Def.StopCommand); await p.StandardInput.FlushAsync(); } catch { }
                if (await WaitExit(p, 30000)) return;
            }
            ConsoleCtrl.SendCtrlC(p.Id);
            if (await WaitExit(p, 45000)) return;
            Log(Lang.T("等唔到，強制結束"));
            try { p.Kill(true); } catch { }
        }
        finally { if (Upnp) UnmapPorts(); }
    }

    private static async Task<bool> WaitExit(Process p, int ms)
    {
        var t = p.WaitForExitAsync();
        return await Task.WhenAny(t, Task.Delay(ms)) == t;
    }

    /// <summary>程式閂嗰陣：快快手停晒。</summary>
    public void StopNow()
    {
        var p = _proc;
        if (p == null || p.HasExited) return;
        _stopping = true;
        try
        {
            if (Def.StopCommand != null) { p.StandardInput.WriteLine(Def.StopCommand); p.StandardInput.Flush(); }
            else ConsoleCtrl.SendCtrlC(p.Id);
            if (!p.WaitForExit(15000)) p.Kill(true);
        }
        catch { }
        if (Upnp) UnmapPorts();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(1000);
        await StartAsync();
    }

    public void SendCommand(string cmd)
    {
        var p = _proc;
        if (p == null || p.HasExited) { Log(Lang.T("伺服器未開")); return; }
        try { p.StandardInput.WriteLine(cmd); p.StandardInput.Flush(); Log("> " + cmd); }
        catch (Exception ex) { Log(ex.Message); }
    }

    // ---------- log 檔（有啲遊戲唔寫 stdout）----------
    private System.Threading.Timer? _tail;
    private long _tailPos;

    private void StartTail()
    {
        var f = Def.LogFile?.Invoke(this);
        if (f == null) return;
        _tailPos = File.Exists(f) ? new FileInfo(f).Length : 0;
        if (Def.Id is "7dtd" or "vrising" or "rust") _tailPos = 0; // 每次開都係新檔
        _tail = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!File.Exists(f)) return;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _tailPos) _tailPos = 0;
                if (fs.Length == _tailPos) return;
                fs.Position = _tailPos;
                using var r = new StreamReader(fs, Encoding.UTF8);
                string rest = r.ReadToEnd();
                _tailPos = fs.Length;
                foreach (var l in rest.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0).TakeLast(80)) Log(l);
            }
            catch { }
        }, null, 1000, 1000);
    }

    private void StopTail() { _tail?.Dispose(); _tail = null; }

    // ---------- 連線 ----------
    public IEnumerable<(string Proto, int Port)> PortList() => Def.Ports.Select(p => (p.Proto, Port + p.Offset)).Distinct();

    public async Task<bool> MapPortsAsync()
    {
        bool all = true;
        foreach (var (proto, port) in PortList())
        {
            bool ok = await Scripts.Upnp.MapAsync(port, proto, "MultiTool " + DisplayName, CancellationToken.None, LeaseSec);
            Log(ok ? Lang.F("UPnP：已開 {0} {1}", proto, port) : Lang.F("UPnP：開唔到 {0} {1}", proto, port));
            all &= ok;
        }
        if (all && Running) KeepMapped();
        return all;
    }

    // 端口限時 2 個鐘（MultiTool 當機都唔會永遠開住），運行中每 50 分鐘續期
    private const int LeaseSec = 7200;
    private CancellationTokenSource? _leaseCts;

    private void KeepMapped()
    {
        _leaseCts?.Cancel();
        var cts = _leaseCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(50), cts.Token); } catch { return; }
                if (!Running || !Upnp) return;
                foreach (var (proto, port) in PortList())
                    await Scripts.Upnp.MapAsync(port, proto, "MultiTool " + DisplayName, CancellationToken.None, LeaseSec);
            }
        });
    }

    public void UnmapPorts()
    {
        _leaseCts?.Cancel();
        _leaseCts = null;
        foreach (var (proto, port) in PortList()) Scripts.Upnp.Unmap(port, proto);
    }

    /// <summary>開之前檢查：冇密碼就提醒（UPnP 開咗嘅話任何人都入得）。用戶取消就回傳 false。</summary>
    public bool ConfirmStart(IWin32Window? owner)
    {
        if (!Def.HasPassword || Password.Length > 0 || Running) return true;
        string msg = Upnp
            ? Lang.F("「{0}」冇設密碼，而且開咗自動開路由器端口（UPnP）：知道你 IP 嘅任何人都入得嚟。\n建議喺「管理 → 設定」加密碼。\n\n照開？", DisplayName)
            : Lang.F("「{0}」冇設密碼：連到你網絡（或者你轉發咗端口）嘅人都入得嚟。\n建議喺「管理 → 設定」加密碼。\n\n照開？", DisplayName);
        return MessageBox.Show(owner, msg, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    // ---------- 備份 ----------
    public string? Backup()
    {
        var src = SavePath;
        if (src == null || !Directory.Exists(src)) { Log(Lang.T("搵唔到存檔資料夾，未有嘢備份（開過伺服器先會有）")); return null; }
        Directory.CreateDirectory(BackupDir);
        var zip = Path.Combine(BackupDir, $"{Key} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        try
        {
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, f);
                    try
                    {
                        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var e = z.CreateEntry(rel, CompressionLevel.Fastest);
                        using var es = e.Open();
                        fs.CopyTo(es);
                    }
                    catch { }
                }
            }
            LastBackup = DateTime.Now;
            foreach (var old in new DirectoryInfo(BackupDir).GetFiles("*.zip").OrderByDescending(x => x.CreationTimeUtc).Skip(BackupKeep))
                try { old.Delete(); } catch { }
            Log(Lang.T("✓ 已備份：") + Path.GetFileName(zip));
            return zip;
        }
        catch (Exception ex)
        {
            Log(Lang.T("備份失敗：") + ex.Message);
            try { File.Delete(zip); } catch { }
            return null;
        }
    }

    public List<FileInfo> Backups()
    {
        try { return Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles("*.zip").OrderByDescending(x => x.CreationTimeUtc).ToList() : new(); }
        catch { return new(); }
    }

    /// <summary>還原備份：舊存檔先搬去隔籬（唔會刪），再解壓。</summary>
    public bool Restore(string zip)
    {
        if (Running || Busy) { Log(Lang.T("請先停止伺服器再還原。")); return false; }
        var dst = SavePath;
        if (dst == null) return false;
        try
        {
            if (Directory.Exists(dst))
            {
                var keep = dst.TrimEnd('\\') + $" (還原前 {DateTime.Now:yyyyMMdd-HHmmss})";
                Directory.Move(dst, keep);
                Log(Lang.T("舊存檔搬咗去：") + keep);
            }
            Directory.CreateDirectory(dst);
            ZipFile.ExtractToDirectory(zip, dst, true);
            Log(Lang.T("✓ 已還原：") + Path.GetFileName(zip));
            return true;
        }
        catch (Exception ex) { Log(Lang.T("還原失敗：") + ex.Message); return false; }
    }
}

/// <summary>對另一個程式嘅主控台送 Ctrl+C（等伺服器正常儲存再閂）。</summary>
internal static class ConsoleCtrl
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    private static readonly object Gate = new();

    public static bool SendCtrlC(int pid)
    {
        lock (Gate)
        {
            try
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) return false;
                SetConsoleCtrlHandler(0, true); // 自己唔好被 Ctrl+C 閂埋
                bool ok = GenerateConsoleCtrlEvent(0, 0);
                Thread.Sleep(500);
                FreeConsole();
                Thread.Sleep(200);
                SetConsoleCtrlHandler(0, false); // 還原，否則之後開嘅伺服器會繼承「唔理 Ctrl+C」
                return ok;
            }
            catch { return false; }
        }
    }
}

/// <summary>所有伺服器。</summary>
internal static class GameServers
{
    public static readonly List<GameSrv> List = new();
    public static event Action? Changed;
    private static Control? _ui;
    private static System.Windows.Forms.Timer? _timer;
    private static string RootFile => Path.Combine(AppPaths.Data, "servers_root.txt");
    public static string Root = Path.Combine(AppPaths.Data, "servers");

    public static void RaiseChanged() => Changed?.Invoke();

    public static void Ui(Action a)
    {
        var c = _ui;
        if (c == null || c.IsDisposed) return;
        try { c.BeginInvoke(a); } catch { }
    }

    /// <summary>頁面整好時叫：讀返已加嘅伺服器、自動開始、自動備份。</summary>
    public static void Init(Control ui)
    {
        if (_ui != null) return;
        _ui = ui;
        try { if (File.Exists(RootFile)) { var r = File.ReadAllText(RootFile).Trim(); if (r.Length > 0) Root = r; } } catch { }
        try
        {
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root).OrderBy(x => x))
                {
                    if (!File.Exists(Path.Combine(dir, "server.txt"))) continue;
                    string key = Path.GetFileName(dir);
                    var d = key.StartsWith("custom", StringComparison.OrdinalIgnoreCase) ? GameDefs.Get("custom")
                        : GameDefs.All.FirstOrDefault(x => x.Id == key);
                    if (d == null) continue;
                    var s = new GameSrv(d, key);
                    s.Load();
                    List.Add(s);
                }
        }
        catch (Exception ex) { AppPaths.LogError("讀伺服器清單", ex); }
        // 上次 MultiTool 冇正常閂（例如當機）可能留低開咗嘅端口：冇開緊嘅伺服器一律閂返
        foreach (var s in List.Where(s => s.Upnp && !(s.AutoStart && s.Installed)))
            foreach (var (proto, port) in s.PortList()) _ = Scripts.Upnp.UnmapAsync(port, proto, CancellationToken.None);
        foreach (var s in List.Where(s => s.AutoStart && s.Installed)) _ = s.StartAsync();
        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += (_, _) =>
        {
            foreach (var s in List.Where(s => s.Status == GameSrv.State.Running && s.BackupMinutes > 0))
                if ((DateTime.Now - (s.LastBackup == DateTime.MinValue ? s.StartedAt : s.LastBackup)).TotalMinutes >= s.BackupMinutes)
                {
                    var srv = s;
                    srv.LastBackup = DateTime.Now;
                    Task.Run(() => srv.Backup());
                }
        };
        _timer.Start();
        Application.ApplicationExit += (_, _) => { foreach (var s in List) s.StopNow(); };
        Extensions.Changed += () =>
        {
            if (Extensions.IsInstalled(Extensions.GameServer)) return;
            foreach (var s in List.Where(s => s.Running)) _ = s.StopAsync();
        };
    }

    public static void SetRoot(string root)
    {
        Root = root;
        try { File.WriteAllText(RootFile, root); } catch { }
    }

    public static GameSrv Add(GameDef d)
    {
        string key = d.Id;
        if (d.Id == "custom")
        {
            // 自訂伺服器冇上限：custom-1、custom-2…
            int n = 1;
            while (List.Any(x => string.Equals(x.Key, $"custom-{n}", StringComparison.OrdinalIgnoreCase)) ||
                   Directory.Exists(Path.Combine(Root, $"custom-{n}"))) n++;
            key = $"custom-{n}";
        }
        var s = new GameSrv(d, key) { Name = d.Id == "custom" ? Lang.F("自訂伺服器 {0}", key[7..]) : "MultiTool Server" };
        s.Save();
        List.Add(s);
        RaiseChanged();
        return s;
    }

    public static void Remove(GameSrv s, bool deleteFiles)
    {
        List.Remove(s);
        try
        {
            if (deleteFiles) Directory.Delete(s.Dir, true);
            else File.Delete(Path.Combine(s.Dir, "server.txt"));
        }
        catch (Exception ex) { AppPaths.LogError("移除伺服器", ex); }
        RaiseChanged();
    }

    public static string LocalIp()
    {
        if (Scripts.Upnp.LocalAddress is { } a) return a.ToString();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                         n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ip != null) return ip.Address.ToString();
            }
        }
        catch { }
        return "?";
    }

    private static string? _publicIp;

    public static async Task<string> PublicIpAsync()
    {
        if (_publicIp != null) return _publicIp;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(ip, out _)) return _publicIp = ip;
        }
        catch { }
        return "?";
    }

    /// <summary>Windows 防火牆加例外（會彈 UAC）。</summary>
    public static bool AddFirewall(GameSrv s)
    {
        var cmds = s.PortList().Select(p =>
            $"netsh advfirewall firewall add rule name=\"MultiTool {s.DisplayName.Replace("\"", "")} {p.Proto} {p.Port}\" dir=in action=allow protocol={p.Proto} localport={p.Port}");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", cmds))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}

// =====================================================================
//  頁面
// =====================================================================
internal sealed class GameServerPage : Panel
{
    private readonly CardStack _listView = new();
    private readonly FlowLayoutPanel _rows;
    private readonly Label _rootLabel;
    private Control? _detail;
    private bool _dirty;

    public static Control Build() => new GameServerPage();

    private GameServerPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        GameServers.Init(this);

        _listView.Dock = DockStyle.Fill;
        FancyButton? add = null;
        add = TUi.Btn(Lang.T("+ 新增伺服器 ▾"), () =>
        {
            var menu = TUi.Menu(add!);
            foreach (var d in GameDefs.All)
            {
                var def = d;
                bool have = d.Id != "custom" && GameServers.List.Any(s => s.Def == d);
                var it = new ToolStripMenuItem(d.Name + (d.Size.Length > 0 ? "   " + d.Size : "")) { Enabled = !have, ForeColor = Ui.Text };
                it.Click += (_, _) => { var s = GameServers.Add(def); OpenDetail(s); };
                menu.Items.Add(it);
            }
            menu.Show(add!, new Point(0, add!.Height));
        }, 0, BtnKind.Primary);
        _rootLabel = TUi.L("", Ui.Text);
        var change = TUi.Btn(Lang.T("更改…"), () =>
        {
            if (GameServers.List.Any(s => s.Running || s.Busy)) { MessageBox.Show(FindForm(), Lang.T("請先停止所有伺服器。"), Program.AppName); return; }
            using var dlg = new FolderBrowserDialog { SelectedPath = GameServers.Root };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            GameServers.SetRoot(dlg.SelectedPath);
            UpdateRoot();
        }, 80);
        var head = new CardPanel
        {
            Title = Lang.T("遊戲伺服器"),
            Hint = Lang.T("一撳安裝 Steam 遊戲嘅專用伺服器（用 Valve 官方 SteamCMD），開關、設定、自動備份，俾朋友連入嚟一齊玩。")
        };
        head.SetContent(TUi.Col(
            TUi.Row(add),
            TUi.Row(TUi.L(Lang.T("新伺服器裝喺")), _rootLabel, change),
            new WrapText(Lang.T("伺服器會用你部機嘅 CPU、記憶體同網絡；大型遊戲（ARK、CS2）要預留幾十 GB 硬碟。"))));
        _listView.AddRow(head);

        _rows = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var mine = new CardPanel { Title = Lang.T("我的伺服器") };
        mine.SetContent(_rows);
        _listView.AddRow(mine);
        Controls.Add(_listView);
        UpdateRoot();
        BuildRows();

        GameServers.Changed += OnChanged;
        Disposed += (_, _) => GameServers.Changed -= OnChanged;
        VisibleChanged += (_, _) => { if (Visible && _dirty) BuildRows(); };
    }

    private void UpdateRoot() => _rootLabel.Text = GameServers.Root;

    private void OnChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_detail == null && Visible) BuildRows(); else _dirty = true;
        });
    }

    private void BuildRows()
    {
        _dirty = false;
        _rows.SuspendLayout();
        foreach (Control c in _rows.Controls.Cast<Control>().ToList()) { _rows.Controls.Remove(c); c.Dispose(); }
        if (GameServers.List.Count == 0) _rows.Controls.Add(TUi.L(Lang.T("未有伺服器，撳上面「+ 新增伺服器」揀遊戲。")));
        foreach (var s0 in GameServers.List)
        {
            var s = s0;
            var name = new Label
            {
                Text = s.DisplayName, AutoSize = false, Width = Ui.S(260), Height = Ui.S(34), ForeColor = Ui.Text, BackColor = Ui.Card,
                Font = Ui.F(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
                Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var st = new Label
            {
                Text = StateText(s), AutoSize = false, Width = Ui.S(200), Height = Ui.S(34), BackColor = Ui.Card, Font = Ui.F(9.5f),
                ForeColor = s.Status == GameSrv.State.Running ? Ui.Success : Ui.TextDim, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var toggle = TUi.Btn(s.Running ? Lang.T("■ 停止") : Lang.T("▶ 開始"),
                () => { if (s.Running) _ = s.StopAsync(); else if (s.ConfirmStart(FindForm())) _ = s.StartAsync(); }, 90, s.Running ? BtnKind.Danger : BtnKind.Success);
            toggle.Enabled = !s.Busy && (s.Installed || s.Running);
            var manage = TUi.Btn(Lang.T("管理 →"), () => OpenDetail(s), 90);
            var row = TUi.Row(name, st, toggle, manage);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(6));
            _rows.Controls.Add(row);
        }
        _rows.ResumeLayout(true);
        _listView.PerformLayout();
        _listView.Invalidate(true);
    }

    public static string StateText(GameSrv s) => s.Status switch
    {
        GameSrv.State.Running => Lang.T("● 運行中") + "  " + Uptime(s),
        GameSrv.State.Installing => s.StatusText,
        GameSrv.State.Starting => Lang.T("開緊…"),
        GameSrv.State.Stopping => Lang.T("停緊…"),
        _ => s.Installed ? (s.StatusText.Length > 0 ? s.StatusText : Lang.T("已停止")) : Lang.T("未安裝")
    };

    private static string Uptime(GameSrv s)
    {
        var t = DateTime.Now - s.StartedAt;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    private void OpenDetail(GameSrv s)
    {
        CloseDetail();
        var d = new ServerDetail(s, CloseDetail, (srv, files) =>
        {
            CloseDetail();
            GameServers.Remove(srv, files);
        });
        d.Dock = DockStyle.Fill;
        _detail = d;
        _listView.Visible = false;
        Controls.Add(d);
        d.BringToFront();
    }

    private void CloseDetail()
    {
        if (_detail != null)
        {
            Controls.Remove(_detail);
            var old = _detail;
            BeginInvoke(() => old.Dispose());
            _detail = null;
        }
        _listView.Visible = true;
        BuildRows();
    }
}

/// <summary>分頁列（文字 + 底線）。</summary>
internal sealed class TabStrip : Control
{
    private readonly string[] _tabs;
    private int _sel, _hover = -1;
    public event Action<int>? Selected;

    public TabStrip(params string[] tabs)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _tabs = tabs;
        Height = Ui.S(42);
        Cursor = Cursors.Hand;
    }

    public int Index { get => _sel; set { _sel = value; Invalidate(); Selected?.Invoke(value); } }

    private Rectangle TabRect(int i)
    {
        int x = 0;
        for (int k = 0; k < _tabs.Length; k++)
        {
            int w = TextRenderer.MeasureText(_tabs[k], Ui.F(10.5f, FontStyle.Bold)).Width + Ui.S(28);
            if (k == i) return new Rectangle(x, 0, w, Height);
            x += w + Ui.S(4);
        }
        return Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Enumerable.Range(0, _tabs.Length).FirstOrDefault(i => TabRect(i).Contains(e.Location), -1);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (int i = 0; i < _tabs.Length; i++) if (TabRect(i).Contains(e.Location)) { Index = i; return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        using (var line = new Pen(Ui.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        for (int i = 0; i < _tabs.Length; i++)
        {
            var r = TabRect(i);
            bool sel = i == _sel;
            TextRenderer.DrawText(g, _tabs[i], Ui.F(10.5f, sel ? FontStyle.Bold : FontStyle.Regular), r,
                sel ? Ui.Text : i == _hover ? Ui.Text : Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (sel)
            {
                using var b = Ui.AccentBrush(new RectangleF(r.X + Ui.S(10), Height - Ui.S(3), r.Width - Ui.S(20), Ui.S(3)));
                g.FillRectangle(b, r.X + Ui.S(10), Height - Ui.S(3), r.Width - Ui.S(20), Ui.S(3));
            }
        }
    }
}

/// <summary>細進度條。</summary>
internal sealed class ThinBar : Control
{
    private double _v = -1;

    public ThinBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Ui.S(6);
    }

    public double Value { get => _v; set { _v = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        if (_v < 0) return;
        Ui.Smooth(g);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var p = Ui.Round(r, Height / 2f)) using (var b = new SolidBrush(Ui.Input)) g.FillPath(b, p);
        float w = (float)(Math.Clamp(_v, 0, 1) * (Width - 1));
        if (w < 2) return;
        var fr = new RectangleF(0, 0, w, Height - 1);
        using (var p = Ui.Round(fr, Height / 2f)) using (var b = Ui.AccentBrush(fr)) g.FillPath(b, p);
    }
}

/// <summary>簡單文字編輯器（改設定檔）。</summary>
internal sealed class TextEditorForm : Form
{
    public TextEditorForm(string path, bool running)
    {
        Text = Path.GetFileName(path) + " - " + Program.AppName;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(Ui.S(900), Ui.S(640));
        var box = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Ui.Input, ForeColor = Ui.Text, Font = new Font(Ui.MonoFamily, 10f), AcceptsTab = true, MaxLength = 0
        };
        string original;
        try { original = File.ReadAllText(path); }
        catch (Exception ex) { original = ""; box.ReadOnly = true; box.Text = ex.Message; }
        if (!box.ReadOnly) box.Text = original.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(54), BackColor = Ui.Panel, Padding = new Padding(Ui.S(12), Ui.S(10), Ui.S(12), 0) };
        var save = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Primary, Size = new Size(Ui.S(100), Ui.S(34)) };
        var close = new FancyButton { Text = Lang.T("關閉"), Kind = BtnKind.Ghost, Size = new Size(Ui.S(90), Ui.S(34)) };
        var info = Ui.Text_(running ? Lang.T("伺服器運行緊：儲存後要重啟先生效，有啲遊戲停機時會覆蓋設定檔。") : path,
            running ? Ui.Danger : Ui.TextFaint, Ui.F(9f), new Padding(Ui.S(10), Ui.S(9), 0, 0));
        save.Click += (_, _) =>
        {
            try
            {
                File.WriteAllText(path, original.Contains("\r\n") ? box.Text : box.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
                original = box.Text;
                Toast.Show(Lang.T("已儲存"), Ui.Success, 1200);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Program.AppName); }
        };
        close.Click += (_, _) => Close();
        save.Enabled = !box.ReadOnly;
        bar.Controls.Add(save);
        bar.Controls.Add(close);
        bar.Controls.Add(info);
        Controls.Add(box);
        Controls.Add(bar);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.S) { save.PerformClick(); e.SuppressKeyPress = true; } };
    }
}

/// <summary>一個伺服器嘅管理頁：上面控制列，下面分頁（伺服器資訊 / 檔案 / 控制台）。</summary>
internal sealed class ServerDetail : Panel
{
    private readonly GameSrv _s;
    private readonly FancyButton _start, _stop, _restart, _install;
    private readonly Label _status, _pub, _lan;
    private readonly ThinBar _bar;
    private readonly TextBox _console;
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 500 };
    private readonly Queue<string> _pending = new();
    private readonly Control[] _pages;
    private readonly FlowLayoutPanel _files, _configs;
    private readonly Label _pathLabel;
    private readonly CardStack _filesStack;
    private string _browse;
    private string _publicIp = "…";

    public ServerDetail(GameSrv s, Action back, Action<GameSrv, bool> remove)
    {
        _s = s;
        _browse = s.Dir;
        BackColor = Ui.Bg;

        // ================= 頂部（永遠睇到）=================
        var top = new Panel { Dock = DockStyle.Top, BackColor = Ui.Bg, Padding = new Padding(Ui.S(24), Ui.S(14), Ui.S(24), 0) };
        var backBtn = TUi.Btn(Lang.T("← 返回"), back, 90);
        var title = Ui.Text_(s.DisplayName, Ui.Text, Ui.F(14f, FontStyle.Bold), new Padding(Ui.S(6), Ui.S(4), Ui.S(10), 0));
        _title = title;
        var sub = Ui.Text_(s.AppId > 0 ? $"Steam App {s.AppId}" + (s.Def.Size.Length > 0 ? " · " + s.Def.Size : "") : "", Ui.TextFaint, Ui.F(9f),
            new Padding(0, Ui.S(11), 0, 0));
        _start = TUi.Btn(Lang.T("▶ 開始"), () => { if (s.ConfirmStart(FindForm())) _ = s.StartAsync(); }, 90, BtnKind.Success);
        _stop = TUi.Btn(Lang.T("■ 停止"), () => _ = s.StopAsync(), 90, BtnKind.Danger);
        _restart = TUi.Btn(Lang.T("↻ 重啟"), () => _ = s.RestartAsync(), 100);
        _install = TUi.Btn("", () => { if (s.Status == GameSrv.State.Installing) s.CancelInstall(); else _ = s.InstallAsync(); }, 170, BtnKind.Primary);
        _status = Ui.Text_("", Ui.TextDim, Ui.F(9.75f), new Padding(0, Ui.S(9), 0, 0));
        _askAi = TUi.Btn(Lang.T("✦ 問 AI 點解"), () =>
        {
            var problem = s.Problem ?? s.StatusText;
            _ = _assist.AskAsync(Lang.F("伺服器出咗問題：「{0}」。請睇下控制台同設定檔，話我知可能嘅原因同點樣解決；如果係設定問題可以直接幫我改。", problem));
        }, 0, BtnKind.Primary);
        _askAi.Visible = false;
        var row1 = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Bg, WrapContents = false, Margin = new Padding(0) };
        row1.Controls.Add(backBtn); row1.Controls.Add(title); row1.Controls.Add(sub);
        var row2 = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Bg, WrapContents = true, Margin = new Padding(0, Ui.S(10), 0, 0) };
        foreach (var b in new Control[] { _start, _stop, _restart, _install }) { b.Margin = new Padding(0, 0, Ui.S(10), 0); row2.Controls.Add(b); }
        row2.Controls.Add(_status);
        _askAi.Margin = new Padding(Ui.S(10), 0, 0, 0);
        row2.Controls.Add(_askAi);
        _bar = new ThinBar { Width = Ui.S(400), Margin = new Padding(0, Ui.S(8), 0, 0) };
        var tabs = new TabStrip(Lang.T("伺服器資訊"), Lang.T("檔案"), Lang.T("控制台")) { Margin = new Padding(0, Ui.S(12), 0, 0) };
        var col = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Bg, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        col.Controls.Add(row1); col.Controls.Add(row2); col.Controls.Add(_bar); col.Controls.Add(tabs);
        top.Controls.Add(col);
        void SizeTop()
        {
            int w = Math.Max(Ui.S(300), top.ClientSize.Width - top.Padding.Horizontal);
            tabs.Width = w;
            _bar.Width = Math.Min(w, Ui.S(560));
            top.Height = top.Padding.Top + col.GetPreferredSize(new Size(w, 0)).Height + Ui.S(4);
        }
        top.Resize += (_, _) => SizeTop();

        // ================= 分頁 1：伺服器資訊 =================
        var info = new CardStack { Dock = DockStyle.Fill };
        info.AddRow(BuildSettings(s));
        info.AddRow(BuildConnection(s, out _pub, out _lan));
        if (s.Def.Id != "cs2") info.AddRow(BuildBackup(s));
        var rm = TUi.Btn(Lang.T("移除伺服器…"), () =>
        {
            if (s.Running || s.Busy) { MessageBox.Show(FindForm(), Lang.T("請先停止伺服器。"), Program.AppName); return; }
            var r = MessageBox.Show(FindForm(), Lang.F("移除「{0}」？\n\n「是」= 連安裝檔、存檔同備份一齊刪走\n「否」= 只係喺清單移除，檔案保留", s.DisplayName),
                Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;
            remove(s, r == DialogResult.Yes);
        }, 0, BtnKind.Danger);
        var danger = new CardPanel { Title = Lang.T("移除") };
        danger.SetContent(TUi.Row(rm));
        info.AddRow(danger);

        // ================= 分頁 2：檔案 =================
        _filesStack = new CardStack { Dock = DockStyle.Fill };
        var quick = TUi.Row(
            TUi.Btn(Lang.T("安裝資料夾"), () => TUi.Open(s.GameDir, folder: true)),
            TUi.Btn(Lang.T("存檔資料夾"), () => { if (s.SavePath != null) TUi.Open(s.SavePath, folder: true); }),
            TUi.Btn(Lang.T("備份資料夾"), () => TUi.Open(s.BackupDir, folder: true)),
            TUi.Btn(Lang.T("Log 檔"), () => { if (s.LogPath != null && File.Exists(s.LogPath)) TUi.Open(s.LogPath); else Toast.Show(Lang.T("未有 log 檔"), Ui.TextDim); }));
        var quickCard = new CardPanel { Title = Lang.T("常用資料夾"), Hint = Lang.T("喺檔案總管打開。") };
        quickCard.SetContent(quick);
        _filesStack.AddRow(quickCard);
        _configs = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var cfgCard = new CardPanel { Title = Lang.T("設定檔"), Hint = Lang.T("直接改遊戲嘅設定檔（開過一次伺服器先會有）。") };
        cfgCard.SetContent(_configs);
        _filesStack.AddRow(cfgCard);
        _pathLabel = TUi.L("", Ui.Text);
        _files = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var up = TUi.Btn(Lang.T("↑ 上一層"), () =>
        {
            if (string.Equals(_browse.TrimEnd('\\'), s.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            _browse = Path.GetDirectoryName(_browse) ?? s.Dir;
            BuildFiles();
        }, 100);
        var refresh = TUi.Btn(Lang.T("↻ 重新整理"), () => { BuildConfigs(); BuildFiles(); }, 0);
        var openHere = TUi.Btn(Lang.T("喺檔案總管打開"), () => TUi.Open(_browse, folder: true));
        var browser = new CardPanel { Title = Lang.T("瀏覽檔案"), Hint = Lang.T("撳資料夾入去，撳文字檔用內置編輯器改，其他檔案會喺檔案總管顯示。") };
        browser.SetContent(TUi.Col(TUi.Row(up, refresh, openHere), TUi.Row(_pathLabel), _files));
        _filesStack.AddRow(browser);

        // ================= 分頁 3：控制台 =================
        var consolePage = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(Ui.S(24), Ui.S(12), Ui.S(24), Ui.S(20)) };
        _console = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Ui.Input,
            ForeColor = Ui.Text, Font = new Font(Ui.MonoFamily, 9.5f), WordWrap = true, Dock = DockStyle.Fill, MaxLength = 0
        };
        _console.Lines = s.LogSnapshot();
        var cmd = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = Lang.T("輸入伺服器指令，Enter 傳送（唔係每隻遊戲都支援）") };
        cmd.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (cmd.Text.Trim().Length > 0) s.SendCommand(cmd.Text.Trim());
            cmd.Clear();
        };
        var cmdHost = new InputHost(cmd, Ui.S(400)) { Dock = DockStyle.Bottom };
        var gap = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(10), BackColor = Ui.Bg };
        var consoleBorder = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Input, Padding = new Padding(Ui.S(10)) };
        consoleBorder.Controls.Add(_console);
        consolePage.Controls.Add(consoleBorder);
        consolePage.Controls.Add(gap);
        consolePage.Controls.Add(cmdHost);

        _pages = new Control[] { info, _filesStack, consolePage };
        foreach (var pg in _pages) { pg.Visible = false; Controls.Add(pg); }
        Controls.Add(top);
        tabs.Selected += i =>
        {
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
            _pages[i].BringToFront();
            if (i == 1) { BuildConfigs(); BuildFiles(); }
            if (i == 2) { _console.SelectionStart = _console.TextLength; _console.ScrollToCaret(); }
            _assist?.Layout(); // 雲朵掣要喺最上面
        };
        tabs.Index = 0;
        SizeTop();

        s.Changed += OnChanged;
        s.LogLine += OnLog;
        Disposed += (_, _) => { s.Changed -= OnChanged; s.LogLine -= OnLog; _ui.Stop(); };
        _ui.Tick += (_, _) => { FlushLog(); UpdateState(); };
        _ui.Start();
        UpdateState();
        UpdateConn();
        _ = LoadIpAsync();

        // 右下角 AI 助手（揀咗 AI 而且登入咗先出現）
        _assist = new ServerAssist(this, s, () => { if (_pages[1].Visible) { BuildConfigs(); BuildFiles(); } }, () => ReloadSettings?.Invoke());
        _assist.AvailableChanged += UpdateState;
    }

    private readonly ServerAssist _assist;
    private readonly FancyButton _askAi;
    private readonly Label _title;
    private Action? ReloadSettings;

    // ---------- 伺服器資訊分頁嘅卡 ----------
    private CardPanel BuildSettings(GameSrv s)
    {
        var fields = TUi.Col();
        TextBox Text(string v, bool secret = false) => new() { Text = v, UseSystemPasswordChar = secret, BorderStyle = BorderStyle.None };
        Control Field(string label, Control input, int w = 260)
        {
            var l = TUi.L(label);
            l.AutoSize = false;
            l.Width = Ui.S(150);
            l.Height = Ui.S(34);
            l.TextAlign = ContentAlignment.MiddleLeft;
            var host = input is NumberBox ? input : new InputHost(input, Ui.S(w));
            var r = TUi.Row(l, host);
            r.WrapContents = false;
            r.Margin = new Padding(0, 0, 0, Ui.S(6));
            return r;
        }
        TextBox? name = null, pw = null, admin = null, extra = null, exe = null, cargs = null;
        NumberBox? players = null, appid = null;
        var port = new NumberBox(Ui.S(120)) { Minimum = 1, Maximum = 65535, Value = s.Port };
        if (s.Def.Id == "custom")
        {
            appid = new NumberBox(Ui.S(140)) { Minimum = 0, Maximum = 99999999, Value = s.CustomAppId };
            exe = Text(s.CustomExe);
            exe.PlaceholderText = @"Server\MyServer.exe";
            cargs = Text(s.CustomArgs);
            cargs.PlaceholderText = "-port {port} -name \"{name}\"";
            fields.Controls.Add(Field("Steam App ID", appid));
            fields.Controls.Add(Field(Lang.T("執行檔（喺安裝資料夾入面）"), exe, 360));
            fields.Controls.Add(Field(Lang.T("啟動參數"), cargs, 460));
        }
        if (s.Def.HasName) { name = Text(s.Name); fields.Controls.Add(Field(Lang.T("伺服器名"), name)); }
        if (s.Def.HasPassword) { pw = Text(s.Password, true); fields.Controls.Add(Field(Lang.T("密碼（空白 = 冇）"), pw)); }
        if (s.Def.HasAdmin) { admin = Text(s.Admin, true); fields.Controls.Add(Field(Lang.T("管理員密碼"), admin)); }
        if (s.Def.HasPlayers) { players = new NumberBox(Ui.S(120)) { Minimum = 1, Maximum = 500, Value = s.Players }; fields.Controls.Add(Field(Lang.T("最多人數"), players)); }
        fields.Controls.Add(Field(Lang.T("端口"), port));
        if (s.Def.Id != "custom") { extra = Text(s.ExtraArgs); fields.Controls.Add(Field(Lang.T("額外參數（進階）"), extra, 460)); }
        var showPw = TUi.Check(Lang.T("顯示密碼"), false, v => { if (pw != null) pw.UseSystemPasswordChar = !v; if (admin != null) admin.UseSystemPasswordChar = !v; });
        var saveLbl = TUi.L("");
        var save = TUi.Btn(Lang.T("儲存設定"), () =>
        {
            if (name != null) s.Name = name.Text.Trim();
            if (pw != null) s.Password = pw.Text;
            if (admin != null) s.Admin = admin.Text;
            if (players != null) s.Players = (int)players.Value;
            s.Port = (int)port.Value;
            if (extra != null) s.ExtraArgs = extra.Text;
            if (appid != null) s.CustomAppId = (int)appid.Value;
            if (exe != null) s.CustomExe = exe.Text.Trim();
            if (cargs != null) s.CustomArgs = cargs.Text;
            s.Save();
            var err = s.Validate();
            saveLbl.Text = err ?? (s.Running ? Lang.T("已儲存，重啟伺服器先生效") : Lang.T("已儲存"));
            saveLbl.ForeColor = err != null ? Ui.Danger : Ui.Success;
            UpdateConn();
            _title.Text = s.DisplayName;
        }, 110, BtnKind.Primary);
        // AI 套用咗設定：將新值放返入格仔
        ReloadSettings = () =>
        {
            if (name != null) name.Text = s.Name;
            if (pw != null) pw.Text = s.Password;
            if (admin != null) admin.Text = s.Admin;
            if (players != null) players.Value = s.Players;
            port.Value = s.Port;
            if (extra != null) extra.Text = s.ExtraArgs;
            if (appid != null) appid.Value = s.CustomAppId;
            if (exe != null) exe.Text = s.CustomExe;
            if (cargs != null) cargs.Text = s.CustomArgs;
            var err = s.Validate();
            saveLbl.Text = err ?? Lang.T("AI 已填好設定") + (s.Running ? Lang.T("，重啟伺服器先生效") : "");
            saveLbl.ForeColor = err != null ? Ui.Danger : Ui.Success;
            _title.Text = s.DisplayName;
            UpdateConn();
        };
        fields.Controls.Add(TUi.Row(save, showPw, saveLbl));
        fields.Controls.Add(TUi.Row(
            TUi.Check(Lang.T("開 MultiTool 時自動開始"), s.AutoStart, v => { s.AutoStart = v; s.Save(); }),
            TUi.Check(Lang.T("當機自動重開"), s.AutoRestart, v => { s.AutoRestart = v; s.Save(); })));
        if (s.Def.Note.Length > 0) fields.Controls.Add(new WrapText(s.Def.Note));
        var set = new CardPanel { Title = Lang.T("設定") };
        set.SetContent(fields);
        return set;
    }

    private CardPanel BuildConnection(GameSrv s, out Label pub, out Label lan)
    {
        pub = TUi.L("", Ui.Text);
        lan = TUi.L("", Ui.Text);
        var copy = TUi.Btn(Lang.T("複製"), () => { try { Clipboard.SetText($"{_publicIp}:{s.Port}"); Toast.Show(Lang.T("已複製"), Ui.Success, 1200); } catch { } }, 70);
        var upnpLbl = TUi.L("");
        var upnp = TUi.Check(Lang.T("開伺服器時自動開路由器端口（UPnP）"), s.Upnp, v =>
        {
            s.Upnp = v;
            s.Save();
            // 運行中改：即刻開 / 閂
            if (s.Running && v) { _ = s.MapPortsAsync(); }
            else if (!v) Task.Run(s.UnmapPorts);
        });
        var mapNow = TUi.Btn(Lang.T("而家試開端口"), async () =>
        {
            upnpLbl.Text = Lang.T("試緊…");
            bool ok = await s.MapPortsAsync();
            if (ok && !s.Running) await Task.Run(s.UnmapPorts); // 淨係試：伺服器未開就即刻閂返
            upnpLbl.Text = ok ? (s.Running ? Lang.T("✓ 成功") : Lang.T("✓ 成功（伺服器未開，已經閂返；開伺服器時會自動再開）"))
                              : Lang.T("✗ 失敗：路由器可能關咗 UPnP，要手動轉發端口");
            upnpLbl.ForeColor = ok ? Ui.Success : Ui.Danger;
        });
        var fw = TUi.Btn(Lang.T("加入防火牆例外"), () =>
        {
            bool ok = GameServers.AddFirewall(s);
            Toast.Show(ok ? Lang.T("已加入防火牆例外") : Lang.T("加唔到防火牆例外（要允許管理員權限）"), ok ? Ui.Success : Ui.Danger);
        });
        var conn = new CardPanel { Title = Lang.T("俾朋友連入嚟"), Hint = Lang.T("同一個網絡用區網 IP；外面嘅朋友用對外 IP，路由器要開端口。") };
        conn.SetContent(TUi.Col(
            TUi.Row(TUi.L(Lang.T("對外 IP")), pub, copy),
            TUi.Row(TUi.L(Lang.T("區網 IP")), lan),
            TUi.Row(upnp, mapNow, upnpLbl),
            TUi.Row(fw),
            new WrapText(Lang.T("如果用流動網絡或者 ISP 用 CGNAT，外面嘅朋友會連唔到；可以試 Tailscale、ZeroTier 或者 playit.gg。"))));
        return conn;
    }

    private CardPanel BuildBackup(GameSrv s)
    {
        FancyButton? every = null, keep = null, restore = null;
        string Every(int m) => m == 0 ? Lang.T("關") : m < 60 ? Lang.F("每 {0} 分鐘", m) : Lang.F("每 {0} 小時", m / 60);
        every = TUi.Btn(Lang.T("自動備份：") + Every(s.BackupMinutes) + " ▾", () => TUi.Pick(every!, new[] { 0, 15, 30, 60, 120, 360 }.Select(m => (Every(m), m)), s.BackupMinutes, m =>
        {
            s.BackupMinutes = m; s.Save();
            every!.Text = Lang.T("自動備份：") + Every(m) + " ▾"; every.Invalidate();
        }), 200);
        keep = TUi.Btn(Lang.F("保留 {0} 份", s.BackupKeep) + " ▾", () => TUi.Pick(keep!, new[] { 3, 5, 10, 20, 50 }.Select(n => (Lang.F("保留 {0} 份", n), n)), s.BackupKeep, n =>
        {
            s.BackupKeep = n; s.Save();
            keep!.Text = Lang.F("保留 {0} 份", n) + " ▾"; keep.Invalidate();
        }), 130);
        var now = TUi.Btn(Lang.T("立即備份"), () => Task.Run(() => s.Backup()), 100);
        restore = TUi.Btn(Lang.T("還原… ▾"), () =>
        {
            var menu = TUi.Menu(restore!);
            var list = s.Backups();
            if (list.Count == 0) menu.Items.Add(new ToolStripMenuItem(Lang.T("未有備份")) { Enabled = false });
            foreach (var f in list.Take(25))
            {
                var file = f;
                var it = new ToolStripMenuItem(f.Name + $"  ({f.Length / 1024 / 1024.0:0.#} MB)") { ForeColor = Ui.Text };
                it.Click += (_, _) =>
                {
                    if (MessageBox.Show(FindForm(), Lang.F("還原「{0}」？\n\n而家嘅存檔會搬去隔籬資料夾保留（唔會刪）。", file.Name), Program.AppName,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) s.Restore(file.FullName);
                };
                menu.Items.Add(it);
            }
            menu.Show(restore!, new Point(0, restore!.Height));
        }, 100);
        var bk = new CardPanel { Title = Lang.T("備份"), Hint = Lang.T("運行緊先會自動備份；還原要先停止伺服器。") };
        bk.SetContent(TUi.Row(every, keep, now, restore));
        return bk;
    }

    // ---------- 檔案分頁 ----------
    private static readonly string[] TextExt = { ".ini", ".json", ".txt", ".cfg", ".xml", ".log", ".lua", ".yml", ".yaml", ".properties", ".conf", ".csv", ".md", ".bat" };

    private static bool IsText(string f) => TextExt.Contains(Path.GetExtension(f).ToLowerInvariant());

    private void Edit(string f)
    {
        try
        {
            if (new FileInfo(f).Length > 5 * 1024 * 1024) { TUi.Open(f); return; }
            using var ed = new TextEditorForm(f, _s.Running);
            ed.ShowDialog(FindForm());
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
    }

    private void BuildConfigs()
    {
        _configs.SuspendLayout();
        foreach (Control c in _configs.Controls.Cast<Control>().ToList()) { _configs.Controls.Remove(c); c.Dispose(); }
        var list = _s.ConfigFiles().ToList();
        if (list.Count == 0) _configs.Controls.Add(TUi.L(Lang.T("未搵到設定檔（開過一次伺服器先會產生）")));
        foreach (var f0 in list)
        {
            var f = f0;
            var name = new Label
            {
                Text = Path.GetRelativePath(_s.Dir, f).StartsWith("..") ? f : Path.GetRelativePath(_s.Dir, f), AutoSize = false, Width = Ui.S(460), Height = Ui.S(34),
                ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var row = TUi.Row(name, TUi.Btn(Lang.T("編輯"), () => Edit(f), 70, BtnKind.Primary), TUi.Btn(Lang.T("用記事本開"), () =>
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + f + "\"") { UseShellExecute = true }); } catch { }
            }));
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _configs.Controls.Add(row);
        }
        _configs.ResumeLayout(true);
        _filesStack.PerformLayout();
    }

    private void BuildFiles()
    {
        _files.SuspendLayout();
        foreach (Control c in _files.Controls.Cast<Control>().ToList()) { _files.Controls.Remove(c); c.Dispose(); }
        _pathLabel.Text = Path.GetRelativePath(Path.GetDirectoryName(_s.Dir) ?? _s.Dir, _browse);
        try
        {
            Directory.CreateDirectory(_s.Dir);
            var di = new DirectoryInfo(_browse);
            var entries = di.GetDirectories().OrderBy(d => d.Name).Cast<FileSystemInfo>()
                .Concat(di.GetFiles().OrderBy(f => f.Name)).Take(300).ToList();
            if (entries.Count == 0) _files.Controls.Add(TUi.L(Lang.T("（空資料夾）")));
            foreach (var e0 in entries)
            {
                var e = e0;
                bool dir = e is DirectoryInfo;
                var lbl = new Label
                {
                    Text = (dir ? "▸  " : "    ") + e.Name, AutoSize = false, Width = Ui.S(420), Height = Ui.S(30),
                    ForeColor = dir ? Ui.Text : Ui.TextDim, BackColor = Ui.Card, Font = Ui.F(9.5f, dir ? FontStyle.Bold : FontStyle.Regular),
                    TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, Ui.S(10), 0)
                };
                lbl.MouseEnter += (_, _) => lbl.BackColor = Ui.CardHi;
                lbl.MouseLeave += (_, _) => lbl.BackColor = Ui.Card;
                lbl.Click += (_, _) =>
                {
                    if (dir) { _browse = e.FullName; BeginInvoke(BuildFiles); }
                    else if (IsText(e.FullName)) Edit(e.FullName);
                    else TUi.Reveal(e.FullName);
                };
                string meta = dir ? "" : Size(((FileInfo)e).Length);
                var info = Ui.Text_(meta + "   " + e.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Ui.TextFaint, Ui.F(9f), new Padding(0, Ui.S(7), 0, 0));
                var row = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = false, Margin = new Padding(0, 0, 0, Ui.S(2)) };
                row.Controls.Add(lbl);
                row.Controls.Add(info);
                _files.Controls.Add(row);
            }
        }
        catch (Exception ex) { _files.Controls.Add(TUi.L(ex.Message, Ui.Danger)); }
        _files.ResumeLayout(true);
        _filesStack.PerformLayout();
        _filesStack.Invalidate(true);
    }

    private static string Size(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";

    // ---------- 共用 ----------
    private async Task LoadIpAsync()
    {
        _publicIp = await GameServers.PublicIpAsync();
        if (!IsDisposed) UpdateConn();
    }

    private void UpdateConn()
    {
        var ports = string.Join("、", _s.PortList().Select(p => $"{p.Proto} {p.Port}"));
        _pub.Text = $"{_publicIp}:{_s.Port}";
        _lan.Text = $"{GameServers.LocalIp()}:{_s.Port}    " + Lang.T("（要開嘅端口：") + ports + Lang.T("）");
    }

    private void OnChanged() { if (!IsDisposed) BeginInvoke(UpdateState); }

    private void OnLog(string line) { lock (_pending) _pending.Enqueue(line); }

    private void FlushLog()
    {
        string[] lines;
        lock (_pending) { lines = _pending.ToArray(); _pending.Clear(); }
        if (lines.Length == 0) return;
        if (_console.Lines.Length > 800) _console.Lines = _s.LogSnapshot();
        else _console.AppendText((_console.TextLength > 0 ? "\r\n" : "") + string.Join("\r\n", lines));
        _console.SelectionStart = _console.TextLength;
        _console.ScrollToCaret();
    }

    private void UpdateState()
    {
        if (IsDisposed) return;
        bool installing = _s.Status == GameSrv.State.Installing;
        _start.Enabled = !_s.Busy && !_s.Running && _s.Installed;
        _stop.Enabled = _s.Running && !_s.Busy;
        _restart.Enabled = _s.Status == GameSrv.State.Running;
        _install.Enabled = installing || (!_s.Busy && !_s.Running);
        _install.Text = installing ? (_s.Progress > 0 ? Lang.F("取消下載（{0:P0}）", _s.Progress) : Lang.T("取消下載"))
            : _s.Installed ? Lang.T("⬇ 更新") : Lang.T("⬇ 安裝");
        _install.Kind = installing ? BtnKind.Default : _s.Installed ? BtnKind.Ghost : BtnKind.Primary;
        _status.Text = GameServerPage.StateText(_s);
        _status.ForeColor = _s.Status == GameSrv.State.Running ? Ui.Success : Ui.TextDim;
        _bar.Value = installing ? Math.Max(0, _s.Progress) : -1;
        // 出咗問題 + 有登入 AI：顯示「問 AI 點解」
        bool ask = _assist != null && _assist.Available && _s.Problem != null && !_s.Busy;
        if (_askAi.Visible != ask) _askAi.Visible = ask;
        foreach (var b in new[] { _start, _stop, _restart, _install }) b.Invalidate();
    }
}

/// <summary>伺服器頁右下角嘅 AI 助手：揀咗 AI 而且登入咗先出現；AI 可以喺伺服器資料夾入面讀 / 改設定檔。</summary>
internal sealed class ServerAssist
{
    private static readonly Dictionary<string, Ai.AssistState> States = new();
    private readonly Control _host;
    private readonly GameSrv _s;
    private readonly Ai.AssistState _st;
    private readonly Ai.CloudButton _cloud;
    private readonly Ai.AssistPanel _panel;
    private readonly Action _changed, _settingsApplied;
    private Ai.AiProvider? _prov;
    private Process? _running;
    private bool _stopped;

    public ServerAssist(Control host, GameSrv s, Action changed, Action settingsApplied)
    {
        _host = host;
        _s = s;
        _changed = changed;
        _settingsApplied = settingsApplied;
        _st = States.TryGetValue(s.Key, out var st) ? st : States[s.Key] = new Ai.AssistState();
        _cloud = new Ai.CloudButton { Visible = false };
        _panel = new Ai.AssistPanel
        {
            Visible = false,
            ApplyText = Lang.T("套用設定"),
            Intro = Lang.T("我可以幫你填伺服器設定、改遊戲設定檔、睇控制台搵出錯原因。例如：「8 個人玩，密碼 abc123」、「經驗值加倍，關咗 PvP」、「點解伺服器開唔到？」\n") +
                    Lang.T("我只會喺呢個伺服器嘅資料夾入面讀寫檔案；改完通常要重啟伺服器先生效。")
        };
        _cloud.Click += (_, _) => Toggle();
        _panel.CloseRequested += () => { _st.Open = false; Layout(); };
        _panel.SendRequested += async () => await SendAsync();
        _panel.ClearRequested += Clear;
        _panel.ApplyRequested += Apply;
        host.Controls.Add(_panel);
        host.Controls.Add(_cloud);
        host.Resize += (_, _) => Layout();
        // 個頁可能已經整好 handle：即刻檢查；未有就等 handle 整好
        if (host.IsHandleCreated) host.BeginInvoke(() => _ = CheckAsync());
        else host.HandleCreated += (_, _) => host.BeginInvoke(() => _ = CheckAsync());
        // 之後先登入 AI：返嚟呢頁再檢查
        host.VisibleChanged += (_, _) => { if (host.Visible && _prov == null) _ = CheckAsync(); };
        host.Disposed += (_, _) => { if (_running != null) { _stopped = true; try { _running.Kill(true); } catch { } } };
    }

    private bool _checking;

    private async Task CheckAsync()
    {
        if (_checking || _prov != null || _host.IsDisposed) return;
        _checking = true;
        try { await CheckCoreAsync(); }
        catch (Exception ex) { AppPaths.LogError("伺服器 AI 助手", ex); }
        finally { _checking = false; }
    }

    private async Task CheckCoreAsync()
    {
        var p = Ai.AiProvider.LoadCurrent();
        if (p == null) return;
        string? exe = await Task.Run(p.Find);
        if (exe == null) { AppPaths.LogError("伺服器 AI 助手", new Exception("搵唔到 " + p.ToolName)); return; }
        var a = await p.AuthAsync(exe);
        if (a.LoggedIn != true) { AppPaths.LogError("伺服器 AI 助手", new Exception(p.Name + " 未登入（" + a.LoggedIn + "）")); return; }
        if (_host.IsDisposed) return;
        _prov = p;
        if (_st.ProviderKey != p.Key) { _st.Session = null; _st.ProviderKey = p.Key; }
        _panel.SetTitle(Lang.F("AI 助手 · {0}", p.Name));
        _panel.Render(_st, p.Name);
        _cloud.Visible = true;
        Layout();
        AvailableChanged?.Invoke();
    }

    public event Action? AvailableChanged;

    private void Toggle()
    {
        _st.Open = !_st.Open;
        Layout();
        if (_st.Open) _panel.FocusInput();
    }

    public void Layout()
    {
        int m = Ui.S(28), size = Ui.S(52);
        var cs = _host.ClientSize;
        _cloud.SetBounds(cs.Width - size - m, cs.Height - size - Ui.S(20), size, size);
        _cloud.BringToFront();
        bool open = _st.Open && _cloud.Visible;
        if (open)
        {
            int w = Math.Min(Ui.S(420), cs.Width - Ui.S(40));
            int h = Math.Min(Ui.S(520), _cloud.Top - Ui.S(30));
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
        _panel.Render(_st, _prov?.Name ?? "AI");
    }

    private string BuildPrompt(string q, bool first)
    {
        var sb = new StringBuilder();
        if (first)
        {
            sb.AppendLine($"你係「多功能工具」入面嘅遊戲伺服器助手，幫用戶管理佢部機上面嘅「{_s.Def.Name}」專用伺服器。");
            sb.AppendLine("工作資料夾就係呢個伺服器嘅資料夾：game\\ = 伺服器安裝檔，save\\ = 存檔 / 資料，backups\\ = 備份，server.txt = MultiTool 自己嘅設定（唔好改）。");
            sb.AppendLine("你可以讀同改呢個資料夾入面嘅設定檔幫用戶調設定（例如經驗倍率、難度、PvP），改之前簡單講你會改咩，改完講清楚改咗邊個檔、邊幾項。");
            sb.AppendLine("規則：");
            sb.AppendLine("- 唔好刪除或者改動存檔同備份，除非用戶明確要求。唔好喺呢個資料夾以外做嘢。唔好執行伺服器程式或者 SteamCMD（開關伺服器由用戶喺 MultiTool 撳掣）。");
            sb.AppendLine("- 伺服器名、密碼、管理員密碼、人數、端口、額外啟動參數係 MultiTool 自己管理嘅設定（每次開伺服器時寫入遊戲設定檔），唔好直接改設定檔入面呢幾項。");
            sb.AppendLine("  要改呢啲（或者用戶叫你幫手填設定），就喺回答最後俾一個 ```json code block，只放要改嘅欄位：");
            sb.AppendLine("  {\"name\":\"伺服器名\",\"password\":\"密碼\",\"admin\":\"管理員密碼\",\"players\":8,\"port\":2456,\"extraArgs\":\"額外啟動參數\"}");
            if (_s.IsCustom)
                sb.AppendLine("  呢個係自訂 Steam 遊戲伺服器，仲可以填：\"appid\"（專用伺服器嘅 Steam App ID，要支援匿名下載）、\"exe\"（安裝資料夾 game\\ 入面嘅執行檔相對路徑）、\"args\"（啟動參數，可以用 {port} {players} {name} {password} {admin} {save}）。唔肯定 App ID 就講清楚。");
            sb.AppendLine("  用戶撳「套用設定」先會生效；講清楚你填咗乜。");
            sb.AppendLine("- 伺服器運行緊時改設定要提用戶重啟；有啲遊戲停機時會覆蓋設定檔，最好叫用戶先停伺服器再改。");
            sb.AppendLine("- " + Mail.MailAi.ReplyRule);
            sb.AppendLine();
        }
        sb.AppendLine($"伺服器狀態：{GameServerPage.StateText(_s)}；" + (_s.Installed ? "已安裝" : "未安裝"));
        var cur = new List<string>();
        if (_s.Def.HasName || _s.IsCustom) cur.Add($"name=\"{_s.Name}\"");
        if (_s.Def.HasPassword) cur.Add("password=" + (_s.Password.Length > 0 ? "（已設定）" : "（冇）"));
        if (_s.Def.HasAdmin) cur.Add("admin=" + (_s.Admin.Length > 0 ? "（已設定）" : "（冇）"));
        if (_s.Def.HasPlayers) cur.Add("players=" + _s.Players);
        cur.Add("port=" + _s.Port);
        cur.Add($"extraArgs=\"{_s.ExtraArgs}\"");
        if (_s.IsCustom) { cur.Add("appid=" + _s.CustomAppId); cur.Add($"exe=\"{_s.CustomExe}\""); cur.Add($"args=\"{_s.CustomArgs}\""); }
        sb.AppendLine("MultiTool 而家嘅設定：" + string.Join("，", cur));
        if (_s.Def.Note.Length > 0) sb.AppendLine("注意：" + _s.Def.Note);
        if (_s.IsCustom && Directory.Exists(_s.GameDir))
        {
            var exes = Directory.EnumerateFiles(_s.GameDir, "*.exe", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_s.GameDir, f)).Where(f => !f.Contains("redist", StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
            if (exes.Count > 0) sb.AppendLine("安裝資料夾入面嘅 exe：" + string.Join("、", exes));
        }
        var cfg = _s.ConfigFiles().Select(f => Path.GetRelativePath(_s.Dir, f)).ToList();
        sb.AppendLine("常用設定檔：" + (cfg.Count > 0 ? string.Join("、", cfg) : "（未有，可能要開過一次伺服器先會產生）"));
        // 控制台只俾最近 25 行（每行最多 200 字），唔好俾太多
        var log = _s.LogSnapshot().TakeLast(25).ToList();
        if (log.Count > 0)
        {
            sb.AppendLine("最近嘅控制台輸出：");
            sb.AppendLine("```");
            foreach (var l in log) sb.AppendLine(l.Length > 200 ? l[..200] + "…" : l);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("用戶：" + q);
        return sb.ToString();
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
        await AskAsync(q);
    }

    public bool Available => _prov != null;

    /// <summary>打開對話框並問一條問題（例如「伺服器出咗問題」掣）。</summary>
    public async Task AskAsync(string q)
    {
        if (_prov == null || _running != null) return;
        _st.Open = true;
        Layout();
        string? exe = _prov.Find();
        if (exe == null) { _panel.System(Lang.F("搵唔到 {0}。", _prov.ToolName)); return; }
        Directory.CreateDirectory(_s.Dir);

        _stopped = false;
        _st.Msgs.Add(("user", q));
        _panel.Render(_st, _prov.Name);
        _panel.Thinking(Lang.F("{0} 諗緊…", _prov.Name));
        _panel.SetBusy(true);

        Ai.AiProvider.Answer ans;
        try
        {
            ans = await _prov.AskAsync(exe, BuildPrompt(q, _st.Session == null), _st.Session, p => _running = p, folder: _s.Dir);
            if (ans.IsError && !_stopped && _st.Session != null && Ai.AiProvider.IsSessionGone(ans.Text))
            {
                _st.Session = null;
                ans = await _prov.AskAsync(exe, BuildPrompt(q, true), null, p => _running = p, folder: _s.Dir);
            }
        }
        catch (Exception ex) { ans = new Ai.AiProvider.Answer(Lang.T("出錯：") + ex.Message, null, true); }
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
                text = (text.Length > 0 ? text + "\n\n" : "") + Lang.T("（已經填好設定，撳下面「套用設定」就會用）");
            }
            _st.Msgs.Add(("ai", text));
        }
        _panel.Render(_st, _prov.Name);
        _panel.FocusInput();
        _changed(); // AI 可能改咗檔案：更新檔案分頁
    }

    private static (string Text, string? Json) Extract(string answer)
    {
        var matches = Regex.Matches(answer, "```(?:json)?\\s*\\n(.*?)```", RegexOptions.Singleline);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            string body = matches[i].Groups[1].Value.Trim();
            if (!body.StartsWith('{')) continue;
            string text = (answer[..matches[i].Index] + answer[(matches[i].Index + matches[i].Length)..]).Trim();
            return (text, body);
        }
        return (answer.Trim(), null);
    }

    /// <summary>將 AI 填嘅設定放入 MultiTool 設定。</summary>
    private void Apply()
    {
        if (_st.Pending == null) return;
        var changed = new List<string>();
        try
        {
            if (JsonNode.Parse(_st.Pending) is not JsonObject o) throw new FormatException("JSON");
            string? Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : o[k]?.ToString();
            int? Num(string k) => o[k] is JsonValue v && (v.TryGetValue<int>(out var n) || (v.TryGetValue<string>(out var t) && int.TryParse(t, out n))) ? n : null;
            if (Str("name") is { } name && (_s.Def.HasName || _s.IsCustom)) { _s.Name = name.Trim(); changed.Add(Lang.T("伺服器名")); }
            if (Str("password") is { } pw && _s.Def.HasPassword) { _s.Password = pw; changed.Add(Lang.T("密碼")); }
            if (Str("admin") is { } ad && _s.Def.HasAdmin) { _s.Admin = ad; changed.Add(Lang.T("管理員密碼")); }
            if (Num("players") is { } pl && _s.Def.HasPlayers) { _s.Players = Math.Clamp(pl, 1, 500); changed.Add(Lang.T("最多人數")); }
            if (Num("port") is { } po) { _s.Port = Math.Clamp(po, 1, 65535); changed.Add(Lang.T("端口")); }
            if (Str("extraArgs") is { } ea) { _s.ExtraArgs = ea; changed.Add(Lang.T("額外參數（進階）")); }
            if (_s.IsCustom)
            {
                if (Num("appid") is { } id) { _s.CustomAppId = Math.Max(0, id); changed.Add("App ID"); }
                if (Str("exe") is { } ex) { _s.CustomExe = ex.Trim(); changed.Add(Lang.T("執行檔")); }
                if (Str("args") is { } ar) { _s.CustomArgs = ar; changed.Add(Lang.T("啟動參數")); }
            }
            _s.Save();
        }
        catch (Exception ex)
        {
            _st.Msgs.Add(("error", Lang.T("AI 俾嘅設定格式唔啱：") + ex.Message));
            _st.Pending = null;
            _panel.Render(_st, _prov?.Name ?? "AI");
            return;
        }
        _st.Pending = null;
        _st.Msgs.Add(("system", changed.Count > 0 ? Lang.T("已套用：") + string.Join("、", changed) : Lang.T("冇可以套用嘅設定。")));
        _panel.Render(_st, _prov?.Name ?? "AI");
        _settingsApplied();
        GameServers.RaiseChanged();
    }
}
