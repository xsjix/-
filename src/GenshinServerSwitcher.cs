// ============================================================
//  原神转服器 (官服 <-> B服 一键切换 + 启动)  C# / WinForms 版
//  ------------------------------------------------------------
//  原理(均通过米哈游官方接口 hyp-api.mihoyo.com 与
//  launcher-webstatic.mihoyo.com 官方CDN实测验证):
//    1. 国服官服与B服共用同一套游戏文件
//    2. 渠道由游戏目录 config.ini 决定:
//         官服: channel=1  sub_channel=1  cps=hyp_mihoyo(米哈游启动器)
//         B服 : channel=14 sub_channel=0  cps=bilibili
//       切B服必须写"纯净B站客户端配置" —— 若残留米哈游启动器的
//       uapc(含"hyp"项)/wpf_version/downloading_mode 字段, B服登录
//       会报"二级地址解析失败"
//    3. B服登录还需官方渠道SDK(PCGameSDK.dll + BLPlatform64),
//       官服安装的客户端没有, 本工具从官方CDN下载(MD5校验,本地缓存)
//    4. 切回官服: 完整还原启动器原始配置快照并清理B服组件
//  编译: csc(/target:winexe, 仅使用系统自带组件, 无第三方依赖)
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GenshinServerSwitcher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 命令行模式(自检/切换), 结果写入 exe 目录 cli_output.txt
            if (args.Length > 0)
            {
                Core.RunCli(args);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(Core.ToolDir, "crash.txt"),
                    ex.ToString(), System.Text.Encoding.UTF8); } catch { }
            }
        }
    }

    // ======================= 核心逻辑 =======================
    static class Core
    {
        public static readonly string ToolDir = AppDomain.CurrentDomain.BaseDirectory;
        // 自检时会重定向到临时目录, 故不能为 readonly
        public static string BackupDir = Path.Combine(ToolDir, "backups");
        public static string DataDir = Path.Combine(ToolDir, "channeldata");
        public static string SdkCacheDir = Path.Combine(ToolDir, "ChannelSDK");
        public const string SettingsPath = "settings.txt";
        public const int MaxBackups = 20;

        public const int CnChannel = 1, CnSub = 1;
        public const int BiliChannel = 14, BiliSub = 0;
        public const string BiliSdkApiUrl =
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameChannelSDKs?launcher_id=umfgRO5gh5&game_id=T2S0Gz4Dr2&channel=14&language=zh-cn";

        public class GameInfo
        {
            public bool Ok; public string Error = "";
            public string Kind = "missing";   // cn / bilibili / other / missing
            public string KindText = "配置缺失";
            public string Channel; public string SubChannel; public string Version = "";
        }

        public static bool GameRunning()
        {
            string[] names = { "YuanShen", "GenshinImpact", "YuanShenCloudGame", "PCGamePlatform", "game_security_protection" };
            foreach (string n in names)
                if (Process.GetProcessesByName(n).Length > 0) return true;
            return false;
        }

        static string ReadKeyFromLines(string[] lines, string key)
        {
            Regex re = new Regex("^\\s*" + Regex.Escape(key) + "\\s*=(.*)$", RegexOptions.IgnoreCase);
            foreach (string l in lines)
            {
                Match m = re.Match(l);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        public static GameInfo GetGameConfig(string gameDir)
        {
            GameInfo r = new GameInfo();
            string cfg = Path.Combine(gameDir, "config.ini");
            try
            {
                if (!File.Exists(Path.Combine(gameDir, "YuanShen.exe")))
                { r.Error = "目录中找不到 YuanShen.exe, 不是国服游戏目录"; return r; }
                if (!File.Exists(cfg)) return r;
                string[] lines = File.ReadAllLines(cfg);
                r.Channel = ReadKeyFromLines(lines, "channel");
                r.SubChannel = ReadKeyFromLines(lines, "sub_channel");
                r.Version = ReadKeyFromLines(lines, "game_version") ?? "";
                if (r.Version.Length == 0)
                {
                    string sv = Path.Combine(gameDir, "YuanShen_Data\\Persistent\\ScriptVersion");
                    if (File.Exists(sv)) r.Version = File.ReadAllText(sv).Trim();
                }
                if (r.Channel == CnChannel.ToString()) { r.Kind = "cn"; r.KindText = "官服"; r.Ok = true; }
                else if (r.Channel == BiliChannel.ToString()) { r.Kind = "bilibili"; r.KindText = "B服"; r.Ok = true; }
                else { r.Kind = "other"; r.KindText = "未知渠道 (channel=" + r.Channel + ")"; r.Ok = true; }
                return r;
            }
            catch (Exception ex) { r.Error = ex.Message; return r; }
        }

        public static string BackupGameConfig(string gameDir)
        {
            string cfg = Path.Combine(gameDir, "config.ini");
            if (!File.Exists(cfg)) return null;
            Directory.CreateDirectory(BackupDir);
            string kind = "unknown";
            GameInfo info = GetGameConfig(gameDir);
            if (info.Kind == "cn") kind = "cn"; else if (info.Kind == "bilibili") kind = "bili";
            string dest = Path.Combine(BackupDir, "config_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + kind + ".ini");
            File.Copy(cfg, dest, true);
            FileInfo[] all = new DirectoryInfo(BackupDir).GetFiles("config_*.ini")
                .OrderByDescending(f => f.LastWriteTime).ToArray();
            for (int i = MaxBackups; i < all.Length; i++)
                try { all[i].Delete(); } catch { }
            return dest;
        }

        public static void SaveChannelSnapshot(string gameDir, string kind)
        {
            string cfg = Path.Combine(gameDir, "config.ini");
            if (!File.Exists(cfg)) return;
            Directory.CreateDirectory(DataDir);
            File.Copy(cfg, Path.Combine(DataDir, kind + ".ini"), true);
        }

        // ---------- 内置资源 (编译时打包进 exe: B服登录组件 + 官服原始配置模板) ----------
        public static System.Reflection.Assembly ExeAssembly()
        {
            return System.Reflection.Assembly.GetExecutingAssembly();
        }

        static Stream EmbeddedStream(string name)
        {
            try { return ExeAssembly().GetManifestResourceStream(name); }
            catch { return null; }
        }

        static string ExtractEmbeddedToTemp(string name, string tempName)
        {
            using (Stream rs = EmbeddedStream(name))
            {
                if (rs == null) return null;
                string tmp = Path.Combine(Path.GetTempPath(), tempName);
                using (FileStream fs = File.Create(tmp)) rs.CopyTo(fs);
                return tmp;
            }
        }

        // ---------- B服渠道SDK ----------
        public static bool BiliSdkInstalled(string gameDir)
        {
            return File.Exists(Path.Combine(gameDir, "YuanShen_Data\\Plugins\\PCGameSDK.dll"));
        }

        public static bool BiliSdkArtifacts(string gameDir)
        {
            if (BiliSdkInstalled(gameDir)) return true;
            if (File.Exists(Path.Combine(gameDir, "sdk_pkg_version"))) return true;
            if (Directory.Exists(Path.Combine(gameDir, "YuanShen_Data\\Plugins\\BLPlatform64"))) return true;
            return false;
        }

        class SdkInfo { public string Url; public string Md5; public long Size; }

        static SdkInfo GetBiliSdkInfo()
        {
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
                string resp;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    wc.Encoding = Encoding.UTF8;
                    resp = wc.DownloadString(BiliSdkApiUrl);
                }
                SdkInfo s = new SdkInfo();
                Match mu = Regex.Match(resp, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                Match mm = Regex.Match(resp, "\"md5\"\\s*:\\s*\"([^\"]+)\"");
                Match ms = Regex.Match(resp, "\"size\"\\s*:\\s*\"?(\\d+)\"?");
                if (!mu.Success || !mm.Success) return null;
                s.Url = mu.Groups[1].Value; s.Md5 = mm.Groups[1].Value.ToLower();
                long sz; long.TryParse(ms.Success ? ms.Groups[1].Value : "0", out sz); s.Size = sz;
                return s;
            }
            catch { return null; }
        }

        static void ExtractZipOverwrite(string zipPath, string destDir)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string target = Path.Combine(destDir, e.FullName.Replace('/', '\\'));
                    string dir = Path.GetDirectoryName(target);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    e.ExtractToFile(target, true);
                }
            }
        }

        public class Res { public bool Ok; public string Message = ""; }

        public static Res EnsureBiliSdk(string gameDir, string zipPath, Action<string> log, Action pump)
        {
            if (BiliSdkInstalled(gameDir)) return new Res { Ok = true, Message = "B服登录组件已就绪" };
            Action<string> say = log;
            if (say == null) say = delegate(string m) { };
            try
            {
                if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath))
                {
                    ExtractZipOverwrite(zipPath, gameDir);
                    if (!BiliSdkInstalled(gameDir)) return new Res { Ok = false, Message = "B服组件解压后校验失败" };
                    return new Res { Ok = true, Message = "B服登录组件安装完成" };
                }
                Directory.CreateDirectory(SdkCacheDir);
                string cacheZip = Path.Combine(SdkCacheDir, "bili_sdk.zip");
                // 逃生阀: 若 ChannelSDK\使用在线组件.txt 存在, 跳过本地来源强制在线更新(应对未来大版本更新)
                bool forceOnline = File.Exists(Path.Combine(SdkCacheDir, "使用在线组件.txt"));
                if (!forceOnline && File.Exists(cacheZip))
                {
                    say("使用本地缓存的B服组件...");
                    ExtractZipOverwrite(cacheZip, gameDir);
                    if (BiliSdkInstalled(gameDir)) return new Res { Ok = true, Message = "B服登录组件安装完成(本地缓存)" };
                    say("本地缓存无效...");
                    try { File.Delete(cacheZip); } catch { }
                }
                if (!forceOnline)
                {
                    // 程序内置组件 (编译时打包, 完全离线可用)
                    say("使用程序内置的B服登录组件...");
                    string embTmp = ExtractEmbeddedToTemp("bili_sdk.zip", "zsf_embedded_sdk.zip");
                    if (embTmp != null)
                    {
                        try
                        {
                            ExtractZipOverwrite(embTmp, gameDir);
                            if (BiliSdkInstalled(gameDir))
                                return new Res { Ok = true, Message = "B服登录组件安装完成(内置组件)" };
                        }
                        finally { try { File.Delete(embTmp); } catch { } }
                    }
                    say("内置组件不可用, 转为在线下载...");
                }
                say("首次切换到B服: 正在从官方接口获取组件信息...");
                SdkInfo info = GetBiliSdkInfo();
                if (info == null) return new Res { Ok = false, Message = "访问米哈游官方接口失败, 请检查网络后重试" };
                say("正在下载B服登录组件 (" + (info.Size / 1024 / 1024) + " MB), 视网速可能需要几分钟...");
                string tmp = cacheZip + ".downloading";
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    var t = wc.DownloadFileTaskAsync(info.Url, tmp);
                    while (!t.IsCompleted)
                    {
                        if (pump != null) pump();
                        Thread.Sleep(300);
                    }
                    if (t.IsFaulted)
                        throw t.Exception != null ? t.Exception.GetBaseException() : new Exception("下载失败");
                }
                say("下载完成, 正在校验 MD5 ...");
                string md5;
                using (MD5 h = MD5.Create())
                    using (FileStream fs = File.OpenRead(tmp))
                        md5 = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLower();
                if (md5 != info.Md5)
                {
                    try { File.Delete(tmp); } catch { }
                    return new Res { Ok = false, Message = "MD5 校验失败, 已放弃(文件不完整)" };
                }
                if (File.Exists(cacheZip)) File.Delete(cacheZip);
                File.Move(tmp, cacheZip);
                ExtractZipOverwrite(cacheZip, gameDir);
                if (!BiliSdkInstalled(gameDir)) return new Res { Ok = false, Message = "B服组件解压后校验失败" };
                return new Res { Ok = true, Message = "B服登录组件下载并安装完成" };
            }
            catch (Exception ex)
            {
                return new Res { Ok = false, Message = "B服组件下载/安装失败: " + ex.Message };
            }
        }

        public static Res RemoveBiliSdk(string gameDir)
        {
            List<string> errs = new List<string>();
            string[] files = { Path.Combine(gameDir, "YuanShen_Data\\Plugins\\PCGameSDK.dll"),
                               Path.Combine(gameDir, "sdk_pkg_version") };
            foreach (string f in files)
            {
                if (File.Exists(f))
                    try { File.Delete(f); } catch (Exception ex) { errs.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
            string bl = Path.Combine(gameDir, "YuanShen_Data\\Plugins\\BLPlatform64");
            if (Directory.Exists(bl))
                try { Directory.Delete(bl, true); } catch (Exception ex) { errs.Add("BLPlatform64: " + ex.Message); }
            if (errs.Count > 0) return new Res { Ok = false, Message = "清理B服组件失败(文件被占用?): " + string.Join("; ", errs.ToArray()) };
            return new Res { Ok = true, Message = "" };
        }

        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".zsftmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            try { File.Replace(tmp, path, null); }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }

        public static Res SwitchChannel(string gameDir, string target, Action<string> log, Action pump, string forceZip)
        {
            if (GameRunning())
                return new Res { Ok = false, Message = "游戏正在运行, 请先完全退出游戏(包括后台的B服登录组件进程)再切换渠道" };
            if (!File.Exists(Path.Combine(gameDir, "YuanShen.exe")))
                return new Res { Ok = false, Message = "找不到 YuanShen.exe, 请确认选择的是国服游戏目录" };
            string toText = target == "cn" ? "官服" : "B服";
            Action<string> say = log;
            if (say == null) say = delegate(string m) { };
            string cfg = Path.Combine(gameDir, "config.ini");

            GameInfo cur = GetGameConfig(gameDir);
            string fromText = cur.KindText;
            int ch = target == "cn" ? CnChannel : BiliChannel;
            int sub = target == "cn" ? CnSub : BiliSub;

            // 配置缺失 -> 创建纯净模板 (B服需先确保组件)
            if (cur.Kind == "missing")
            {
                if (target == "bilibili")
                {
                    Res sdk = EnsureBiliSdk(gameDir, forceZip, say, pump);
                    if (!sdk.Ok) return new Res { Ok = false, Message = "未完成切换: " + sdk.Message };
                }
                string v = cur.Version.Length == 0 ? "0.0.0" : cur.Version;
                string cpsNew = target == "bilibili" ? "bilibili" : "gw_pc";
                AtomicWrite(cfg, "[General]\r\nchannel=" + ch + "\r\nsub_channel=" + sub +
                    "\r\ncps=" + cpsNew + "\r\ngame_version=" + v + "\r\n");
                GameInfo after = GetGameConfig(gameDir);
                if (after.Kind != target) return new Res { Ok = false, Message = "写入后校验失败" };
                return new Res { Ok = true, Message = "已创建 config.ini 并切换到" + toText + " (游戏版本 " + v + ")" };
            }

            // 已在目标渠道 -> 组件与配置状态自愈
            if (cur.Kind == target)
            {
                if (target == "cn" && BiliSdkArtifacts(gameDir))
                {
                    Res clean = RemoveBiliSdk(gameDir);
                    if (!clean.Ok) return new Res { Ok = false, Message = "当前已是官服, 但清理残留B服组件失败: " + clean.Message };
                    return new Res { Ok = true, Message = "当前已是官服, 已清理残留的B服登录组件" };
                }
                if (target == "bilibili")
                {
                    bool needCfgFix = false;
                    if (File.Exists(cfg))
                    {
                        string raw = File.ReadAllText(cfg);
                        if (Regex.IsMatch(raw, "(?m)^\\s*wpf_version\\s*=") || raw.Contains("\"hyp\"") ||
                            Regex.IsMatch(raw, "(?m)^\\s*downloading_mode\\s*=")) needCfgFix = true;
                    }
                    bool sdkMissing = !BiliSdkInstalled(gameDir);
                    if (sdkMissing || needCfgFix)
                    {
                        if (sdkMissing)
                        {
                            Res sdk = EnsureBiliSdk(gameDir, forceZip, say, pump);
                            if (!sdk.Ok) return new Res { Ok = false, Message = "当前已是B服, 但登录组件缺失: " + sdk.Message };
                        }
                        if (needCfgFix)
                        {
                            string v = cur.Version.Length == 0 ? "0.0.0" : cur.Version;
                            AtomicWrite(cfg, "[General]\r\nchannel=" + BiliChannel + "\r\nsub_channel=" + BiliSub +
                                "\r\ncps=bilibili\r\ngame_version=" + v + "\r\n");
                        }
                        return new Res { Ok = true, Message = "当前已是B服, 已自动修复登录组件/配置状态" };
                    }
                }
                return new Res { Ok = true, Message = "当前已是" + toText + ", 无需切换" };
            }

            // 离开当前渠道: 备份 + 快照
            string backupPath = BackupGameConfig(gameDir);
            SaveChannelSnapshot(gameDir, cur.Kind);

            // 切B服: 先装组件
            if (target == "bilibili")
            {
                Res sdk = EnsureBiliSdk(gameDir, forceZip, say, pump);
                if (!sdk.Ok) return new Res { Ok = false, Message = "未完成切换: " + sdk.Message };
            }

            try
            {
                string version = cur.Version.Length == 0 ? "0.0.0" : cur.Version;
                string newText;
                if (target == "bilibili")
                {
                    // 纯净B站客户端配置(不含启动器残留字段, 否则登录报"二级地址解析失败")
                    newText = "[General]\r\nchannel=" + ch + "\r\nsub_channel=" + sub +
                        "\r\ncps=bilibili\r\ngame_version=" + version + "\r\n";
                }
                else
                {
                    // 切回官服: 完整还原官服配置(优先本地快照, 其次程序内置模板)并刷新版本号
                    string[] cfgLines = null;
                    string snapPath = Path.Combine(DataDir, "cn.ini");
                    if (File.Exists(snapPath))
                        cfgLines = File.ReadAllLines(snapPath);
                    else
                    {
                        using (Stream rs = EmbeddedStream("cn_config.ini"))
                        {
                            if (rs != null)
                                using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                                {
                                    List<string> tl = new List<string>(sr.ReadToEnd().Replace("\r\n", "\n").Split('\n'));
                                    if (tl.Count > 0 && tl[tl.Count - 1] == "") tl.RemoveAt(tl.Count - 1);
                                    cfgLines = tl.ToArray();
                                }
                        }
                    }
                    if (cfgLines != null)
                    {
                        List<string> ls = new List<string>(cfgLines);
                        bool hadVersion = false;
                        for (int i = 0; i < ls.Count; i++)
                            if (Regex.IsMatch(ls[i], "^\\s*game_version\\s*=")) { ls[i] = "game_version=" + version; hadVersion = true; }
                        if (!hadVersion) ls.Add("game_version=" + version);
                        newText = string.Join("\r\n", ls.ToArray()) + "\r\n";
                    }
                    else
                    {
                        // 最后兜底: 原地改写三个键
                        string cps = ReadKeyFromLines(File.ReadAllLines(cfg), "cps");
                        List<string> ls = new List<string>(File.ReadAllLines(cfg));
                        for (int i = 0; i < ls.Count; i++)
                        {
                            if (Regex.IsMatch(ls[i], "^\\s*channel\\s*=")) ls[i] = "channel=" + ch;
                            else if (Regex.IsMatch(ls[i], "^\\s*sub_channel\\s*=")) ls[i] = "sub_channel=" + sub;
                            else if (!string.IsNullOrEmpty(cps) && Regex.IsMatch(ls[i], "^\\s*cps\\s*=")) ls[i] = "cps=" + cps;
                        }
                        newText = string.Join("\r\n", ls.ToArray()) + "\r\n";
                    }
                }
                AtomicWrite(cfg, newText);
                GameInfo after = GetGameConfig(gameDir);
                if (after.Kind != target)
                {
                    if (backupPath != null && File.Exists(backupPath)) File.Copy(backupPath, cfg, true);
                    return new Res { Ok = false, Message = "切换后校验失败, 已自动还原备份" };
                }
                if (target == "cn" && BiliSdkArtifacts(gameDir))
                {
                    Res clean = RemoveBiliSdk(gameDir);
                    if (!clean.Ok)
                        return new Res { Ok = false, Message = "渠道已切回官服, 但B服组件清理失败: " + clean.Message };
                }
                return new Res { Ok = true, Message = "已切换: " + fromText + " -> " + toText };
            }
            catch (Exception ex)
            {
                return new Res { Ok = false, Message = "切换失败: " + ex.Message };
            }
        }

        public static Res StartGame(string gameDir)
        {
            string exe = Path.Combine(gameDir, "YuanShen.exe");
            if (!File.Exists(exe)) return new Res { Ok = false, Message = "找不到 YuanShen.exe" };
            GameInfo info = GetGameConfig(gameDir);
            if (info.Kind == "bilibili" && !BiliSdkInstalled(gameDir))
                return new Res
                {
                    Ok = false,
                    Message = "B服登录组件 PCGameSDK.dll 不存在, 很可能被安全软件隔离删除\r\n请在杀毒软件隔离区恢复它并将整个游戏目录加入信任区(白名单), 再点击「切换到B服」重装组件"
                };
            Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = gameDir, UseShellExecute = true });
            return new Res { Ok = true, Message = "已启动游戏" };
        }

        // ---------- 路径发现 ----------
        public static bool ValidGameDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(dir, "YuanShen.exe")) || File.Exists(Path.Combine(dir, "GenshinImpact.exe"));
        }

        public static string LoadSettings()
        {
            try
            {
                string p = Path.Combine(ToolDir, SettingsPath);
                if (!File.Exists(p)) return null;
                foreach (string l in File.ReadAllLines(p))
                    if (l.StartsWith("gameDir=", StringComparison.OrdinalIgnoreCase))
                        return l.Substring(8).Trim();
            }
            catch { }
            return null;
        }

        public static void SaveSettings(string gameDir)
        {
            try { File.WriteAllText(Path.Combine(ToolDir, SettingsPath), "gameDir=" + gameDir, new UTF8Encoding(false)); }
            catch { }
        }

        public static string FindFast()
        {
            List<string> cands = new List<string>();
            string[] rks = { @"SOFTWARE\WOW6432Node\miHoYo\原神", @"SOFTWARE\WOW6432Node\miHoYo\Genshin Impact" };
            foreach (string rk in rks)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(rk))
                    {
                        if (k == null) continue;
                        object v = k.GetValue("GameInstallPath");
                        if (v != null)
                        {
                            cands.Add(v.ToString());
                            cands.Add(Path.Combine(v.ToString(), "Genshin Impact Game"));
                        }
                    }
                }
                catch { }
            }
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string root = d.RootDirectory.FullName;
                    cands.Add(Path.Combine(root, "miHoYo Launcher\\games\\Genshin Impact Game"));
                    cands.Add(Path.Combine(root, "Program Files\\miHoYo Launcher\\games\\Genshin Impact Game"));
                    cands.Add(Path.Combine(root, "Program Files\\Genshin Impact\\Genshin Impact Game"));
                    cands.Add(Path.Combine(root, "Genshin Impact\\Genshin Impact Game"));
                }
            }
            catch { }
            foreach (string c in cands)
                if (File.Exists(Path.Combine(c, "YuanShen.exe"))) return c;
            return null;
        }

        public static List<string> FindDeep()
        {
            List<string> found = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    SearchDir(d.RootDirectory.FullName, 0, found);
                }
            }
            catch { }
            return found;
        }

        static void SearchDir(string dir, int depth, List<string> found)
        {
            if (depth > 4 || found.Count > 8) return;
            string[] sub = new string[0];
            try { sub = Directory.GetDirectories(dir); } catch { return; }
            foreach (string s in sub)
            {
                if (string.Equals(Path.GetFileName(s), "Genshin Impact Game", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(s, "YuanShen.exe")))
                {
                    found.Add(s);
                    continue;
                }
                if (depth < 4) SearchDir(s, depth + 1, found);
            }
        }

        // ---------- 命令行模式(供自检) ----------
        public static void RunCli(string[] args)
        {
            StringBuilder o = new StringBuilder();
            try
            {
                if (args[0] == "--selftest") SelfTest(o);
                else if (args[0] == "--status")
                {
                    string dir = LoadSettings();
                    o.AppendLine("savedDir=" + dir);
                    GameInfo i = GetGameConfig(dir);
                    o.AppendLine("kind=" + i.Kind + " text=" + i.KindText + " ver=" + i.Version);
                    o.AppendLine("sdkInstalled=" + BiliSdkInstalled(dir));
                }
                else if (args[0] == "--switch")
                {
                    Res r = SwitchChannel(LoadSettings(), args[1], delegate(string m) { o.AppendLine("[log] " + m); }, null, null);
                    o.AppendLine("Ok=" + r.Ok + " " + r.Message);
                }
            }
            catch (Exception ex) { o.AppendLine("EXCEPTION: " + ex); }
            File.WriteAllText(Path.Combine(ToolDir, "cli_output.txt"), o.ToString(), new UTF8Encoding(false));
        }

        static int fails = 0;
        static void Assert(StringBuilder o, string name, bool cond)
        {
            o.AppendLine((cond ? "[PASS] " : "[FAIL] ") + name);
            if (!cond) fails++;
        }

        static void SelfTest(StringBuilder o)
        {
            // 0. 内置资源检查 (B服登录组件 + 官服配置模板)
            bool embSdkOk = false, embCfgOk = false;
            try
            {
                string t = ExtractEmbeddedToTemp("bili_sdk.zip", "zsf_selftest_sdk.zip");
                if (t != null)
                {
                    try
                    {
                        using (ZipArchive z = ZipFile.OpenRead(t))
                            embSdkOk = z.GetEntry("YuanShen_Data/Plugins/PCGameSDK.dll") != null;
                    }
                    finally { try { File.Delete(t); } catch { } }
                }
                using (Stream rs = EmbeddedStream("cn_config.ini"))
                    if (rs != null)
                        using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                        {
                            string c = sr.ReadToEnd();
                            embCfgOk = c.Contains("channel=1") && c.Contains("cps=");
                        }
            }
            catch { }
            Assert(o, "内置B服登录组件(bili_sdk.zip)", embSdkOk);
            Assert(o, "内置官服配置模板(cn_config.ini)", embCfgOk);

            string mock = Path.Combine(Path.GetTempPath(), "zsf_cstest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(mock);
            Directory.CreateDirectory(Path.Combine(mock, "YuanShen_Data\\Persistent"));
            File.Create(Path.Combine(mock, "YuanShen.exe")).Dispose();
            File.WriteAllText(Path.Combine(mock, "YuanShen_Data\\Persistent\\ScriptVersion"), "7.1.0");
            string orig = "[General]\r\ngame_version=7.1.0\r\nchannel=1\r\nsub_channel=1\r\ncps=hyp_mihoyo\r\n" +
                "uapc={\"hk4e_cn\":{\"uapc\":\"\"},\"hyp\":{\"uapc\":\"\"}}\r\ndownloading_mode=\r\nwpf_version=7.1.0.48145775\r\n";
            File.WriteAllText(Path.Combine(mock, "config.ini"), orig, new UTF8Encoding(false));

            string stage = Path.Combine(mock, "_s");
            Directory.CreateDirectory(Path.Combine(stage, "YuanShen_Data\\Plugins"));
            File.WriteAllText(Path.Combine(stage, "YuanShen_Data\\Plugins\\PCGameSDK.dll"), "fake");
            File.WriteAllText(Path.Combine(stage, "sdk_pkg_version"), "fake");
            string fakeZip = Path.Combine(mock, "_z.zip");
            ZipFile.CreateFromDirectory(stage, fakeZip);

            string bkDir = Path.Combine(mock, "backups");
            string dataDir = Path.Combine(mock, "channeldata");
            string sdkDir = Path.Combine(mock, "ChannelSDK");
            string oldBk = BackupDir, oldData = DataDir, oldSdk = SdkCacheDir;
            BackupDir = bkDir; DataDir = dataDir; SdkCacheDir = sdkDir;
            try
            {
                GameInfo i = GetGameConfig(mock);
                Assert(o, "识别官服", i.Kind == "cn" && i.Version == "7.1.0");

                Res r = SwitchChannel(mock, "bilibili", null, null, fakeZip);
                string txt = File.ReadAllText(Path.Combine(mock, "config.ini"));
                string tmpl = "[General]\r\nchannel=14\r\nsub_channel=0\r\ncps=bilibili\r\ngame_version=7.1.0\r\n";
                Assert(o, "切B服+纯净模板", r.Ok && txt == tmpl);
                Assert(o, "组件已安装", BiliSdkInstalled(mock) && File.Exists(Path.Combine(mock, "sdk_pkg_version")));
                Assert(o, "快照已保存", File.Exists(Path.Combine(dataDir, "cn.ini")));
                Assert(o, "备份已生成", new DirectoryInfo(bkDir).GetFiles("config_*.ini").Length >= 1);

                Res r2 = SwitchChannel(mock, "cn", null, null, null);
                string txt2 = File.ReadAllText(Path.Combine(mock, "config.ini"));
                Assert(o, "切回官服=原始配置", r2.Ok && txt2 == orig);
                Assert(o, "组件已清理", !BiliSdkInstalled(mock) && !File.Exists(Path.Combine(mock, "sdk_pkg_version")) &&
                    !Directory.Exists(Path.Combine(mock, "YuanShen_Data\\Plugins\\BLPlatform64")));

                Res r3 = SwitchChannel(mock, "cn", null, null, null);
                Assert(o, "重复切换无改动", r3.Ok && File.ReadAllText(Path.Combine(mock, "config.ini")) == orig);

                File.Delete(Path.Combine(mock, "config.ini"));
                Assert(o, "缺失识别", GetGameConfig(mock).Kind == "missing");
                Res r4 = SwitchChannel(mock, "bilibili", null, null, fakeZip);
                Assert(o, "缺失创建并切B服", r4.Ok && GetGameConfig(mock).Kind == "bilibili");

                File.Delete(Path.Combine(mock, "YuanShen_Data\\Plugins\\PCGameSDK.dll"));
                string before = File.ReadAllText(Path.Combine(mock, "config.ini"));
                Res r5 = SwitchChannel(mock, "bilibili", null, null, fakeZip);
                Assert(o, "组件自愈重装", r5.Ok && BiliSdkInstalled(mock) &&
                    File.ReadAllText(Path.Combine(mock, "config.ini")) == before);

                string polluted = tmpl.Replace("cps=bilibili\r\n",
                    "cps=bilibili\r\nuapc={\"hk4e_cn\":{\"uapc\":\"\"},\"hyp\":{\"uapc\":\"\"}}\r\ndownloading_mode=\r\nwpf_version=7.1.0.1\r\n");
                File.WriteAllText(Path.Combine(mock, "config.ini"), polluted, new UTF8Encoding(false));
                Res r6 = SwitchChannel(mock, "bilibili", null, null, fakeZip);
                Assert(o, "污染配置自愈重写", r6.Ok && GetGameConfig(mock).Kind == "bilibili" &&
                    File.ReadAllText(Path.Combine(mock, "config.ini")) == tmpl);
            }
            finally
            {
                BackupDir = oldBk; DataDir = oldData; SdkCacheDir = oldSdk;
                try { Directory.Delete(mock, true); } catch { }
            }
            o.AppendLine(fails == 0 ? "===== 自检全部通过 =====" : "===== " + fails + " 项失败 =====");
        }
    }

    // ======================= 图形界面 =======================
    class MainForm : Form
    {
        string gameDir = null;
        Label lblChannel, lblVersion, lblPath;
        Button btnLaunchCn, btnLaunchBili, btnSwitchCn, btnSwitchBili, btnRestore, btnBackupDir, btnGameDir, btnBrowse, btnAutoFind;
        TextBox txtPath, txtLog;

        public MainForm()
        {
            Text = "原神转服器 · 官服 ⇄ B服";
            ClientSize = new System.Drawing.Size(500, 646);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            BackColor = System.Drawing.Color.FromArgb(245, 246, 248);

            Label lblTitle = new Label { Text = "原神转服器", Location = new System.Drawing.Point(20, 14), Size = new System.Drawing.Size(300, 34), Font = new System.Drawing.Font("Microsoft YaHei UI", 16F, System.Drawing.FontStyle.Bold), ForeColor = System.Drawing.Color.FromArgb(40, 44, 52) };
            Label lblSub = new Label { Text = "官服 ⇄ B服 一键切换 · 官方组件 · 不动游戏文件", Location = new System.Drawing.Point(22, 48), Size = new System.Drawing.Size(460, 20), ForeColor = System.Drawing.Color.Gray };

            System.Windows.Forms.Panel pnl = new System.Windows.Forms.Panel { Location = new System.Drawing.Point(20, 76), Size = new System.Drawing.Size(460, 86), BackColor = System.Drawing.Color.White };
            lblChannel = new Label { Text = "未检测", Location = new System.Drawing.Point(18, 10), Size = new System.Drawing.Size(200, 40), Font = new System.Drawing.Font("Microsoft YaHei UI", 20F, System.Drawing.FontStyle.Bold), ForeColor = System.Drawing.Color.Gray };
            lblVersion = new Label { Text = "游戏版本: -", Location = new System.Drawing.Point(18, 54), Size = new System.Drawing.Size(300, 20), ForeColor = System.Drawing.Color.FromArgb(90, 96, 104) };
            lblPath = new Label { Text = "路径: 未设置", Location = new System.Drawing.Point(230, 14), Size = new System.Drawing.Size(220, 60), ForeColor = System.Drawing.Color.FromArgb(120, 126, 134) };
            pnl.Controls.AddRange(new Control[] { lblChannel, lblVersion, lblPath });

            btnLaunchCn = MkBtn("▶  启动官服", 20, 176, 220, 50, System.Drawing.Color.FromArgb(47, 158, 99), System.Drawing.Color.White, 12F);
            btnLaunchBili = MkBtn("▶  启动B服", 260, 176, 220, 50, System.Drawing.Color.FromArgb(59, 130, 246), System.Drawing.Color.White, 12F);
            btnSwitchCn = MkBtn("切换到官服(不启动)", 20, 238, 220, 34, System.Drawing.Color.FromArgb(223, 240, 232), System.Drawing.Color.FromArgb(31, 122, 74), 9F);
            btnSwitchBili = MkBtn("切换到B服(不启动)", 260, 238, 220, 34, System.Drawing.Color.FromArgb(222, 234, 254), System.Drawing.Color.FromArgb(37, 99, 235), 9F);
            btnRestore = MkBtn("恢复备份", 20, 282, 140, 30, System.Drawing.Color.White, System.Drawing.Color.FromArgb(70, 76, 84), 9F);
            btnBackupDir = MkBtn("备份文件夹", 170, 282, 140, 30, System.Drawing.Color.White, System.Drawing.Color.FromArgb(70, 76, 84), 9F);
            btnGameDir = MkBtn("游戏目录", 320, 282, 140, 30, System.Drawing.Color.White, System.Drawing.Color.FromArgb(70, 76, 84), 9F);

            Label lblPathTitle = new Label { Text = "游戏路径:", Location = new System.Drawing.Point(20, 326), Size = new System.Drawing.Size(64, 22), ForeColor = System.Drawing.Color.FromArgb(70, 76, 84) };
            txtPath = new TextBox { Location = new System.Drawing.Point(88, 323), Size = new System.Drawing.Size(280, 24), ReadOnly = true, BackColor = System.Drawing.Color.White };
            btnBrowse = MkBtn("浏览…", 372, 321, 52, 28, System.Drawing.Color.White, System.Drawing.Color.FromArgb(70, 76, 84), 9F);
            btnAutoFind = MkBtn("自动查找", 428, 321, 52, 28, System.Drawing.Color.White, System.Drawing.Color.FromArgb(70, 76, 84), 9F);

            Label lblLogTitle = new Label { Text = "运行日志", Location = new System.Drawing.Point(20, 358), Size = new System.Drawing.Size(100, 18), ForeColor = System.Drawing.Color.Gray };
            txtLog = new TextBox { Location = new System.Drawing.Point(20, 378), Size = new System.Drawing.Size(460, 196), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = System.Drawing.Color.White, Font = new System.Drawing.Font("Consolas", 8.5F) };
            Label lblTip = new Label { Text = "提示: 切换前请先完全退出游戏 · 每次切换自动备份 config.ini\r\n首次切B服需从官方服务器下载登录组件(约85MB, 之后走本地缓存)\r\n首次进B服请在弹出的B站登录窗口中登录B站账号", Location = new System.Drawing.Point(20, 580), Size = new System.Drawing.Size(460, 56), ForeColor = System.Drawing.Color.FromArgb(150, 154, 160), Font = new System.Drawing.Font("Microsoft YaHei UI", 8F) };

            Controls.AddRange(new Control[] { lblTitle, lblSub, pnl, btnLaunchCn, btnLaunchBili, btnSwitchCn, btnSwitchBili, btnRestore, btnBackupDir, btnGameDir, lblPathTitle, txtPath, btnBrowse, btnAutoFind, lblLogTitle, txtLog, lblTip });

            btnLaunchCn.Click += delegate { DoSwitch("cn", true); };
            btnLaunchBili.Click += delegate { DoSwitch("bilibili", true); };
            btnSwitchCn.Click += delegate { DoSwitch("cn", false); };
            btnSwitchBili.Click += delegate { DoSwitch("bilibili", false); };
            btnRestore.Click += delegate { DoRestore(); };
            btnBackupDir.Click += delegate
            {
                Directory.CreateDirectory(Core.BackupDir);
                Process.Start("explorer.exe", Core.BackupDir);
            };
            btnGameDir.Click += delegate { if (gameDir != null) Process.Start("explorer.exe", gameDir); };
            btnBrowse.Click += delegate { DoBrowse(); };
            btnAutoFind.Click += delegate { DoFind(); };

            Load += delegate { InitOnLoad(); };
        }

        Button MkBtn(string text, int x, int y, int w, int h, System.Drawing.Color bg, System.Drawing.Color fg, float fs)
        {
            Button b = new Button();
            b.Text = text; b.Location = new System.Drawing.Point(x, y); b.Size = new System.Drawing.Size(w, h);
            b.BackColor = bg; b.ForeColor = fg; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.Font = new System.Drawing.Font("Microsoft YaHei UI", fs, System.Drawing.FontStyle.Bold);
            b.Cursor = System.Windows.Forms.Cursors.Hand;
            return b;
        }

        void Log(string msg)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }

        void InitOnLoad()
        {
            string saved = Core.LoadSettings();
            if (Core.ValidGameDir(saved)) { gameDir = saved; Log("已加载游戏路径: " + saved); UpdateUi(); return; }
            Log("未找到已保存的路径, 正在自动查找...");
            string fast = Core.FindFast();
            if (fast != null) { gameDir = fast; Core.SaveSettings(fast); Log("已自动找到游戏: " + fast); UpdateUi(); }
            else { Log("常见位置未找到, 请点击「自动查找」或「浏览…」"); UpdateUi(); }
        }

        void UpdateUi()
        {
            if (!Core.ValidGameDir(gameDir))
            {
                lblChannel.Text = "未检测"; lblChannel.ForeColor = System.Drawing.Color.Gray;
                lblVersion.Text = "游戏版本: -"; lblPath.Text = "路径: 未设置"; txtPath.Text = "";
                SetEnabled(false);
                return;
            }
            Core.GameInfo info = Core.GetGameConfig(gameDir);
            if (!info.Ok) { lblChannel.Text = "无法读取"; lblChannel.ForeColor = System.Drawing.Color.Firebrick; lblVersion.Text = "错误: " + info.Error; return; }
            switch (info.Kind)
            {
                case "cn": lblChannel.Text = "官服"; lblChannel.ForeColor = System.Drawing.Color.FromArgb(47, 158, 99); break;
                case "bilibili": lblChannel.Text = "B服"; lblChannel.ForeColor = System.Drawing.Color.FromArgb(59, 130, 246); break;
                case "missing": lblChannel.Text = "配置缺失"; lblChannel.ForeColor = System.Drawing.Color.Chocolate; break;
                default: lblChannel.Text = "未知渠道"; lblChannel.ForeColor = System.Drawing.Color.Chocolate; break;
            }
            lblVersion.Text = "游戏版本: " + info.Version + "  (channel=" + info.Channel + " sub_channel=" + info.SubChannel + ")";
            string shortDir = gameDir.Length > 38 ? gameDir.Substring(0, 36) + "…" : gameDir;
            lblPath.Text = "路径: " + shortDir;
            txtPath.Text = gameDir;
            btnLaunchCn.Enabled = true; btnLaunchBili.Enabled = true;
            btnSwitchCn.Enabled = info.Kind != "cn";
            btnSwitchBili.Enabled = info.Kind != "bilibili";
            btnRestore.Enabled = true; btnGameDir.Enabled = true;
        }

        void SetEnabled(bool on)
        {
            btnLaunchCn.Enabled = btnLaunchBili.Enabled = btnSwitchCn.Enabled = btnSwitchBili.Enabled = on;
            btnRestore.Enabled = btnGameDir.Enabled = on;
        }

        void DoSwitch(string target, bool launch)
        {
            if (!Core.ValidGameDir(gameDir)) { Log("未设置有效的游戏路径"); return; }
            Log("正在切换到 " + (target == "cn" ? "官服" : "B服") + " ...");
            Cursor = System.Windows.Forms.Cursors.WaitCursor;
            Core.Res r = Core.SwitchChannel(gameDir, target,
                delegate(string m) { Log(m); Application.DoEvents(); },
                delegate { Application.DoEvents(); }, null);
            Cursor = System.Windows.Forms.Cursors.Default;
            Log(r.Message);
            if (!r.Ok) { MessageBox.Show(this, r.Message, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); UpdateUi(); return; }
            UpdateUi();
            if (launch)
            {
                Core.Res s = Core.StartGame(gameDir);
                Log(s.Message);
                if (!s.Ok) MessageBox.Show(this, s.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void DoRestore()
        {
            if (!Core.ValidGameDir(gameDir)) return;
            try
            {
                FileInfo[] bks = new DirectoryInfo(Core.BackupDir).GetFiles("config_*.ini")
                    .OrderByDescending(f => f.LastWriteTime).ToArray();
                if (bks.Length == 0) { Log("备份文件夹中没有备份"); return; }
                Core.BackupGameConfig(gameDir);
                File.Copy(bks[0].FullName, Path.Combine(gameDir, "config.ini"), true);
                Log("已恢复备份 " + bks[0].Name + ", 当前渠道: " + Core.GetGameConfig(gameDir).KindText);
            }
            catch (Exception ex) { Log("恢复失败: " + ex.Message); }
            UpdateUi();
        }

        void DoBrowse()
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择游戏目录(包含 YuanShen.exe 的目录, 通常名为 \"Genshin Impact Game\")";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                if (Core.ValidGameDir(dlg.SelectedPath))
                {
                    gameDir = dlg.SelectedPath; Core.SaveSettings(gameDir);
                    Log("已设置游戏路径: " + gameDir); UpdateUi();
                }
                else if (MessageBox.Show(this, "所选目录中未找到 YuanShen.exe, 仍然使用该目录吗?", "提示",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    gameDir = dlg.SelectedPath; Core.SaveSettings(gameDir);
                    Log("已手动指定目录: " + gameDir); UpdateUi();
                }
            }
        }

        void DoFind()
        {
            Log("正在自动查找游戏目录(快速)...");
            Cursor = System.Windows.Forms.Cursors.WaitCursor; Application.DoEvents();
            string fast = Core.FindFast();
            if (fast != null)
            {
                gameDir = fast; Core.SaveSettings(fast); Cursor = System.Windows.Forms.Cursors.Default;
                Log("已找到: " + fast); UpdateUi(); return;
            }
            Log("常见位置未找到, 开始全盘搜索(仅固定磁盘, 可能需要十几秒)...");
            Application.DoEvents();
            List<string> deep = Core.FindDeep();
            Cursor = System.Windows.Forms.Cursors.Default;
            if (deep.Count > 0)
            {
                gameDir = deep[0]; Core.SaveSettings(deep[0]);
                Log("已找到: " + deep[0]); UpdateUi();
            }
            else Log("未找到游戏, 请手动点击「浏览…」选择游戏目录");
        }
    }
}
