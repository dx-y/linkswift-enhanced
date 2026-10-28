// LinkSwift / IDMPush 增强 —— 本地接收端 v3.0
//
// 设计目标（对普通用户零门槛）：
//   • 下载后双击一次 → 自动注册 linkswift://、自动找 IDMan.exe、写配置，提示后退出；
//   • 之后由脚本在被点击时用协议拉起；空闲 120 秒自动退出 —— 不常驻、不开机自启、无控制台窗口；
//   • 单文件、自带 .NET 运行时，不需要装 Node / .NET；裁剪后约 11 MB。
//
// 协议（与 userscript 的 LOCAL_BRIDGE 对齐，v2 通道）：
//   GET  /v1/ping           → { ok, app, version, idm, idmPath, dryRun, tokenRequired }
//   POST /v1/push           → 请求体 { v, url, filename, filesize, referer, ua, cookie, confirm }
//   GET  /v1/123pan/tasks   → { ok, available, reason, tasks:[{id,fileId,filename,bytesDone,bytesTotal,percent,status,statusText,speed,errMsg,updatedAt}] }
//   必须带 X-LS-Client: linkswift-userscript/2；OPTIONS 预检一律拒绝（脚本用 GM_xmlhttpRequest，不需要 CORS）。
//
// 注意：本项目开启 PublishTrimmed，所以不能用反射序列化，JSON 一律手写。
//
// 命令行：
//   IDMPush.exe                 首次：自动配置并提示；之后：静默启动服务（空闲自动退出）
//   IDMPush.exe --setup         强制重新配置
//   IDMPush.exe --idm "路径"     指定 IDMan.exe
//   IDMPush.exe --port 16888
//   IDMPush.exe --idle-exit 0   常驻（默认 120 秒）
//   IDMPush.exe --silent        静默，不弹任何对话框（自动化测试用）

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace IDMPush
{
    internal static class Program
    {
        public const string Version = "3.0.0";
        public const string ClientId = "linkswift-userscript/2";
        public const int DefaultPort = 16888;
        public const int MaxBody = 64 * 1024;
        public const int DefaultIdleExitSec = 120;
        public const string AppDirName = "LinkswiftBridge";

        static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);
        static string ConfigPath => Path.Combine(ConfigDir, "config.json");
        static string LogPath => Path.Combine(ConfigDir, "bridge.log");

        static string _idmPath = "";
        static string _exePath = "";
        static int _idleExitSec = DefaultIdleExitSec;
        static int _port = DefaultPort;
        static bool _silent;
        static System.Threading.Timer _idleTimer;
        static TcpListener _listener;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                if (!_silent) ShowMessage("接收端启动失败：" + ex.Message, true);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            _exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
            bool forceSetup = false;
            string argIdm = null;
            bool hasIdleArg = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("linkswift:", StringComparison.OrdinalIgnoreCase)) continue; // 协议拉起时浏览器会带上 URL
                switch (a)
                {
                    case "--setup": forceSetup = true; break;
                    case "--silent": _silent = true; break;
                    case "--idm": if (i + 1 < args.Length) argIdm = args[++i]; break;
                    case "--port": if (i + 1 < args.Length && int.TryParse(args[++i], out var p)) _port = p; break;
                    case "--idle-exit":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var s)) { _idleExitSec = s; hasIdleArg = true; }
                        break;
                }
            }

            var cfg = LoadConfig();
            _idmPath = argIdm ?? cfg?.IdmPath ?? "";
            if (!hasIdleArg && cfg != null) _idleExitSec = cfg.IdleExitSec;

            bool firstRun = forceSetup || cfg == null;
            if (firstRun)
            {
                _idmPath = argIdm ?? DetectIdm();
                RegisterProtocol();
                SaveConfig();
                Log($"setup done: idm={(_idmPath.Length > 0 ? _idmPath : "(未找到)")} exe={_exePath}");
                if (!_silent)
                {
                    ShowSetupResult();
                    return 0; // 首次配置完就退出，不留后台进程
                }
            }
            else if (!string.IsNullOrEmpty(_idmPath) && !File.Exists(_idmPath))
            {
                Log("configured idm missing: " + _idmPath);
                _idmPath = DetectIdm();
                SaveConfig();
            }

            if (!TryBind(_port)) { Log("port busy, exit"); return 0; } // 已有实例在跑
            Serve();
            return 0;
        }

        // ---------------- 配置 / 注册 ----------------

        sealed class Config
        {
            public string IdmPath = "";
            public int IdleExitSec = DefaultIdleExitSec;
            public int Port = DefaultPort;
        }

        static Config LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8));
                var root = doc.RootElement;
                var c = new Config();
                if (root.TryGetProperty("IdmPath", out var v1) && v1.ValueKind == JsonValueKind.String) c.IdmPath = v1.GetString() ?? "";
                if (root.TryGetProperty("IdleExitSec", out var v2) && v2.TryGetInt32(out var n2)) c.IdleExitSec = n2;
                if (root.TryGetProperty("Port", out var v3) && v3.TryGetInt32(out var n3)) c.Port = n3;
                return c;
            }
            catch { return null; }
        }

        static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = "{\n  \"IdmPath\": " + Js(_idmPath ?? "") + ",\n  \"IdleExitSec\": " + _idleExitSec + ",\n  \"Port\": " + _port + "\n}\n";
                File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            }
            catch (Exception e) { Log("save config failed: " + e.Message); }
        }

        static bool RegisterProtocol()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\linkswift");
                if (k == null) return false;
                k.SetValue("", "URL:Linkswift Bridge");
                k.SetValue("URL Protocol", "");
                using var cmd = k.CreateSubKey(@"shell\open\command");
                cmd?.SetValue("", "\"" + _exePath + "\" \"%1\"");
                return true;
            }
            catch (Exception e) { Log("register protocol failed: " + e.Message); return false; }
        }

        static string DetectIdm()
        {
            var candidates = new List<string>();
            try
            {
                using var appPath = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\IDMan.exe");
                if (appPath?.GetValue("") is string v && !string.IsNullOrWhiteSpace(v)) candidates.Add(v.Trim('"'));
            }
            catch { }
            try
            {
                foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Internet Download Manager", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Internet Download Manager" })
                {
                    using var un = Registry.LocalMachine.OpenSubKey(root);
                    if (un?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                        candidates.Add(Path.Combine(loc.Trim('"'), "IDMan.exe"));
                }
            }
            catch { }
            candidates.Add(@"C:\Program Files (x86)\Internet Download Manager\IDMan.exe");
            candidates.Add(@"C:\Program Files\Internet Download Manager\IDMan.exe");
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return "";
        }

        // ---------------- 极简 JSON 输出（裁剪后不能用反射序列化） ----------------

        static string Js(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Js(bool b) => b ? "true" : "false";
        static string Js(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string Js(double n) => n.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // ---------------- HTTP 服务 ----------------

        static bool TryBind(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                return true;
            }
            catch (SocketException e)
            {
                Log($"bind {port} failed: {e.SocketErrorCode}");
                return false;
            }
        }

        static void Serve()
        {
            Log($"listen http://127.0.0.1:{_port}/  idle-exit={(_idleExitSec > 0 ? _idleExitSec + "s" : "off")}  idm={_idmPath}");
            Touch();
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception e) { Log("accept failed: " + e.Message); break; }
                _ = Task.Run(() => HandleClient(client));
            }
        }

        static void Touch()
        {
            if (_idleExitSec <= 0) return;
            _idleTimer?.Dispose();
            _idleTimer = new System.Threading.Timer(_ =>
            {
                Log("idle exit");
                try { _listener?.Stop(); } catch { }
                Environment.Exit(0);
            }, null, _idleExitSec * 1000, Timeout.Infinite);
        }

        static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
        {
            "https://pan.baidu.com", "https://yun.baidu.com",
            "https://www.aliyundrive.com", "https://www.alipan.com",
            "https://yun.139.com", "https://caiyun.139.com",
            "https://cloud.189.cn", "https://pan.xunlei.com",
            "https://pan.quark.cn", "https://drive.uc.cn",
            "https://www.123pan.com", "https://www.123684.com",
            "https://www.guangyapan.com"
        };

        sealed class RequestHead
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers;
        }

        static void HandleClient(TcpClient client)
        {
            Touch();
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var stream = client.GetStream();
                    var head = ReadHead(stream);
                    if (head == null) return;

                    if (string.Equals(head.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteRaw(stream, 403, "Forbidden", "", "text/plain");
                        return;
                    }
                    if (!head.Headers.TryGetValue("x-ls-client", out var cid) || cid != ClientId)
                    {
                        WriteJson(stream, 403, "{\"ok\":false,\"error\":\"bad-client\",\"detail\":" + Js("缺少或错误的 X-LS-Client") + "}");
                        return;
                    }
                    if (head.Headers.TryGetValue("origin", out var origin) && !string.IsNullOrEmpty(origin) && !AllowedOrigins.Contains(origin))
                    {
                        WriteJson(stream, 403, "{\"ok\":false,\"error\":\"bad-origin\",\"detail\":" + Js(origin) + "}");
                        return;
                    }

                    int contentLength = 0;
                    if (head.Headers.TryGetValue("content-length", out var cl)) int.TryParse(cl, out contentLength);

                    if (head.Method == "GET" && head.Path.StartsWith("/v1/ping", StringComparison.Ordinal))
                    {
                        bool idmOk = !string.IsNullOrEmpty(_idmPath) && File.Exists(_idmPath);
                        WriteJson(stream, 200,
                            "{\"ok\":true,\"app\":\"linkswift-bridge\",\"version\":" + Js(Version) +
                            ",\"idm\":" + Js(idmOk) + ",\"idmPath\":" + Js(_idmPath) +
                            ",\"dryRun\":false,\"tokenRequired\":false}");
                        return;
                    }

                    if (head.Method == "POST" && head.Path.StartsWith("/v1/push", StringComparison.Ordinal))
                    {
                        if (contentLength > MaxBody)
                        {
                            WriteJson(stream, 413, "{\"ok\":false,\"error\":\"too-large\"}");
                            return;
                        }
                        var task = ValidateTask(ReadBody(stream, contentLength), out var err, out var detail);
                        if (task == null)
                        {
                            WriteJson(stream, err == "too-large" ? 413 : 400,
                                "{\"ok\":false,\"error\":" + Js(err) + ",\"detail\":" + Js(detail) + "}");
                            return;
                        }
                        Log($"push url={Trunc(task.Url, 300)} file={task.Filename} confirm={task.ConfirmDialog}");
                        var pushed = PushToIdm(task);
                        if (!pushed.Ok)
                        {
                            WriteJson(stream, 503,
                                "{\"ok\":false,\"error\":" + Js(pushed.Error ?? "idm-unavailable") + ",\"detail\":" + Js(pushed.Detail ?? "") + "}");
                            return;
                        }
                        WriteJson(stream, 200, "{\"ok\":true}");
                        return;
                    }

                    if (head.Method == "GET" && head.Path.StartsWith("/v1/123pan/tasks", StringComparison.Ordinal))
                    {
                        WriteJson(stream, 200, Read123PanTasks());
                        return;
                    }

                    WriteJson(stream, 404, "{\"ok\":false,\"error\":\"not-found\",\"detail\":" + Js(head.Path) + "}");
                }
                catch (Exception e)
                {
                    Log("handle failed: " + e.Message);
                }
            }
        }

        static RequestHead ReadHead(NetworkStream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 16 * 1024)
            {
                int n = s.Read(one, 0, 1);
                if (n <= 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
            }
            var lines = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var parts = lines[0].Split(' ');
            if (parts.Length < 3) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf(':');
                if (idx <= 0) continue;
                headers[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
            }
            return new RequestHead { Method = parts[0].ToUpperInvariant(), Path = parts[1], Headers = headers };
        }

        static string ReadBody(NetworkStream s, int len)
        {
            if (len <= 0) return "";
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = s.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(buf, 0, read);
        }

        static void WriteRaw(NetworkStream s, int status, string reason, string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            if (bytes.Length > 0) s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        static void WriteJson(NetworkStream s, int status, string json)
        {
            var reason = status switch
            {
                200 => "OK",
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                413 => "Payload Too Large",
                503 => "Service Unavailable",
                _ => "OK"
            };
            WriteRaw(s, status, reason, json, "application/json");
        }

        // ---------------- 123 云盘客户端任务 ----------------

        static readonly Dictionary<long, (long Bytes, long At)> Pan123Samples = new();
        static readonly object Pan123Lock = new();

        static long Num(object v)
        {
            if (v == null || v == DBNull.Value) return 0;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is double d) return (long)d;
            return long.TryParse(Convert.ToString(v), out var n) ? n : 0;
        }

        static string Read123PanTasks()
        {
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "123pan", "123pan-download.db");
            if (!File.Exists(dbPath))
                return "{\"ok\":false,\"available\":false,\"reason\":\"db-missing\",\"tasks\":[]}";

            var tmp = Path.Combine(Path.GetTempPath(), $"linkswift-123pan-{Environment.ProcessId}-{DateTime.Now.Ticks}.db");
            try
            {
                File.Copy(dbPath, tmp, true);
                var cs = new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, file_id, filename, bytes_total, bytes_done, status, err_msg, updated_at FROM download_task_v2 ORDER BY id DESC LIMIT 20";
                using var r = cmd.ExecuteReader();

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder(512);
                sb.Append("{\"ok\":true,\"available\":true,\"reason\":\"\",\"tasks\":[");
                bool firstTask = true;
                var seen = new HashSet<long>();

                while (r.Read())
                {
                    long id = Num(r.GetValue(0));
                    seen.Add(id);
                    long bytesTotal = Num(r.GetValue(3));
                    long bytesDone = Num(r.GetValue(4));
                    int statusNum = (int)Num(r.GetValue(5));
                    string statusText = statusNum == 2 ? "已完成" : statusNum == 3 ? "失败" : statusNum == 1 ? "下载中" : "排队中";

                    long speed = 0;
                    lock (Pan123Lock)
                    {
                        if (Pan123Samples.TryGetValue(id, out var prev) && now > prev.At && bytesDone >= prev.Bytes)
                            speed = (long)Math.Round((bytesDone - prev.Bytes) / ((now - prev.At) / 1000.0));
                        Pan123Samples[id] = (bytesDone, now);
                    }
                    double percent = bytesTotal > 0 ? Math.Min(100.0, bytesDone * 100.0 / bytesTotal) : 0.0;

                    if (!firstTask) sb.Append(',');
                    firstTask = false;
                    sb.Append("{\"id\":").Append(Js(id))
                      .Append(",\"fileId\":").Append(Js(Convert.ToString(r.GetValue(1)) ?? ""))
                      .Append(",\"filename\":").Append(Js(Convert.ToString(r.GetValue(2)) ?? ""))
                      .Append(",\"bytesDone\":").Append(Js(bytesDone))
                      .Append(",\"bytesTotal\":").Append(Js(bytesTotal))
                      .Append(",\"percent\":").Append(Js(percent))
                      .Append(",\"status\":").Append(Js((long)statusNum))
                      .Append(",\"statusText\":").Append(Js(statusText))
                      .Append(",\"speed\":").Append(Js(speed))
                      .Append(",\"errMsg\":").Append(Js(Convert.ToString(r.GetValue(6)) ?? ""))
                      .Append(",\"updatedAt\":").Append(Js(Convert.ToString(r.GetValue(7)) ?? ""))
                      .Append('}');
                }
                sb.Append("]}");

                lock (Pan123Lock)
                {
                    foreach (var k in Pan123Samples.Keys.Where(k => !seen.Contains(k)).ToList()) Pan123Samples.Remove(k);
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Log("read 123pan db failed: " + e.Message);
                return "{\"ok\":false,\"available\":false,\"reason\":\"db-error\",\"detail\":" + Js(Trunc(e.Message, 200)) + ",\"tasks\":[]}";
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---------------- IDM 推送 ----------------

        sealed class TaskInfo
        {
            public string Url, Filename, Referer, Cookie;
            public bool ConfirmDialog;
        }

        static TaskInfo ValidateTask(string body, out string error, out string detail)
        {
            error = null; detail = null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
            catch { error = "bad-request"; detail = "不是合法 JSON"; return null; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "bad-request"; detail = "载荷不是对象"; return null; }
                string Get(string name)
                {
                    if (!root.TryGetProperty(name, out var el)) return "";
                    return el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString() ?? "",
                        JsonValueKind.Number => el.ToString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => ""
                    };
                }
                var url = Get("url");
                if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                { error = "bad-request"; detail = "url 不是 http(s)"; return null; }
                if (url.Length > 8192) { error = "too-large"; detail = "url 过长"; return null; }
                var filename = Get("filename").Replace("\r", "").Replace("\n", "");
                if (filename.Length > 512) filename = filename.Substring(0, 512);
                var referer = Get("referer");
                if (referer.Length > 2048) referer = referer.Substring(0, 2048);
                bool confirm = root.TryGetProperty("confirm", out var ce) && ce.ValueKind == JsonValueKind.True;
                return new TaskInfo { Url = url, Filename = filename, Referer = referer, Cookie = Get("cookie"), ConfirmDialog = confirm };
            }
        }

        sealed class PushResult
        {
            public bool Ok;
            public string Error;
            public string Detail;
        }

        [ComImport, Guid("4BD46AAE-C51F-4BF7-8BC0-2E86E33D1873"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICIDMLinkTransmitter
        {
            void SendLinkToIDM(
                [MarshalAs(UnmanagedType.BStr)] string bstrUrl,
                [MarshalAs(UnmanagedType.BStr)] string bstrReferer,
                [MarshalAs(UnmanagedType.BStr)] string bstrCookies,
                [MarshalAs(UnmanagedType.BStr)] string bstrData,
                [MarshalAs(UnmanagedType.BStr)] string bstrUser,
                [MarshalAs(UnmanagedType.BStr)] string bstrPassword,
                [MarshalAs(UnmanagedType.BStr)] string bstrLocalPath,
                [MarshalAs(UnmanagedType.BStr)] string bstrLocalFileName,
                int lFlags);
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "COM 激活在运行时才用到")]
        static PushResult PushToIdm(TaskInfo task)
        {
            if (string.IsNullOrEmpty(_idmPath) || !File.Exists(_idmPath))
                return new PushResult { Ok = false, Error = "idm-unavailable", Detail = "未找到 IDMan.exe" };

            try
            {
                var clsid = new Guid("AC746233-E9D3-49CD-862F-068F7B7CCCA4");
                var t = Type.GetTypeFromCLSID(clsid, true);
                object com = Activator.CreateInstance(t);
                try
                {
                    var tx = (ICIDMLinkTransmitter)com;
                    // flags: 1 = 静默开始；0 = 弹「下载文件信息」框（可改保存路径）
                    tx.SendLinkToIDM(task.Url, task.Referer ?? "", task.Cookie ?? "", "", "", "", "", task.Filename ?? "", task.ConfirmDialog ? 0 : 1);
                    return new PushResult { Ok = true };
                }
                finally
                {
                    try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
                }
            }
            catch (Exception e)
            {
                Log("COM push failed: " + e.Message);
                try
                {
                    var psi = new ProcessStartInfo(_idmPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add(task.Url);
                    if (!string.IsNullOrEmpty(task.Filename)) { psi.ArgumentList.Add("/f"); psi.ArgumentList.Add(task.Filename); }
                    psi.ArgumentList.Add("/a");
                    Process.Start(psi);
                    return new PushResult { Ok = true };
                }
                catch (Exception e2)
                {
                    Log("cli push failed: " + e2.Message);
                    return new PushResult { Ok = false, Error = "idm-rejected", Detail = e.Message };
                }
            }
        }

        // ---------------- 日志 / 提示 ----------------

        static readonly object LogLock = new object();

        static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(ConfigDir);
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { }
        }

        static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);

        static void ShowMessage(string msg, bool error)
        {
            try { MessageBoxW(IntPtr.Zero, msg, "IDM 接收端", error ? 0x10u : 0x40u); } catch { }
        }

        static void ShowSetupResult()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(_idmPath)) sb.AppendLine("已找到 IDM：" + _idmPath);
            else sb.AppendLine("没找到 IDMan.exe。请先安装 IDM，或用命令指定路径：\r\nIDMPush.exe --idm \"IDMan.exe 的完整路径\"");
            sb.AppendLine();
            sb.AppendLine("配置已完成：已注册 linkswift:// 协议（只影响当前用户，无需管理员）。");
            sb.AppendLine();
            sb.AppendLine("接下来回到网盘页面点「推送至 IDM」即可，脚本会自动拉起本程序。");
            sb.AppendLine("本程序不常驻、不开机自启，空闲 120 秒自动退出。");
            sb.AppendLine();
            sb.AppendLine("点「确定」关闭本窗口即可，不需要保持运行。");
            try
            {
                MessageBoxW(IntPtr.Zero, sb.ToString(), "IDM 接收端 —— 配置完成", string.IsNullOrEmpty(_idmPath) ? 0x30u : 0x40u);
            }
            catch { }
        }
    }
}
