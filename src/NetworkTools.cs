using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGUI
{
    public class ServiceProbeResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public bool Ok { get; set; }
        public int LatencyMs { get; set; }
        public string Detail { get; set; }
    }

    public class DiagItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public bool Ok { get; set; }
        public string Value { get; set; }
    }

    public class AdapterInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Dns { get; set; }
        public string Status { get; set; }
    }

    public static class NetworkTools
    {
        static NetworkTools()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, err) => true;
            ServicePointManager.DefaultConnectionLimit = 32;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static async Task<List<ServiceProbeResult>> ProbeAllServicesAsync(CancellationToken ct)
        {
            var targets = new[]
            {
                new { Id = "youtube",  Name = "YouTube",        Host = "www.youtube.com",     Port = 443, Url = "https://www.youtube.com/generate_204", Endpoint = "youtube.com" },
                new { Id = "discord",  Name = "Discord",        Host = "discord.com",         Port = 443, Url = "https://discord.com/api/v10/gateway",  Endpoint = "discord.com" },
                new { Id = "voice",    Name = "Голосовой чат",  Host = "gateway.discord.gg",  Port = 443, Url = "https://gateway.discord.gg/",          Endpoint = "gateway.discord.gg" },
                new { Id = "telegram", Name = "Telegram",       Host = "api.telegram.org",    Port = 443, Url = "https://api.telegram.org/",            Endpoint = "api.telegram.org" }
            };

            var tasks = targets.Select(t => ProbeEndpointAsync(t.Id, t.Name, t.Endpoint, t.Host, t.Port, t.Url, 3800, ct));
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        public static async Task<ServiceProbeResult> ProbeEndpointAsync(
            string id, string name, string endpoint, string host, int port, string url, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "HEAD";
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.AllowAutoRedirect = false;
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                    req.KeepAlive = false;

                    using (cts.Token.Register(() => { try { req.Abort(); } catch { } }))
                    {
                        try
                        {
                            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                            {
                                sw.Stop();
                                return new ServiceProbeResult
                                {
                                    Id = id,
                                    Name = name,
                                    Endpoint = endpoint,
                                    Ok = true,
                                    LatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds),
                                    Detail = "HTTP " + (int)resp.StatusCode
                                };
                            }
                        }
                        catch (WebException wex)
                        {
                            var errResp = wex.Response as HttpWebResponse;
                            if (errResp == null) throw;
                            // Reaching the remote server and getting 3xx/4xx/405 proves TLS + DPI bypass worked
                            sw.Stop();
                            int code = (int)errResp.StatusCode;
                            errResp.Close();
                            return new ServiceProbeResult
                            {
                                Id = id,
                                Name = name,
                                Endpoint = endpoint,
                                Ok = code > 0 && code < 520,
                                LatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds),
                                Detail = "HTTP " + code
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                string msg = ex.Message;
                if (msg.Contains("aborted") || msg.Contains("timed out") || ex is TaskCanceledException)
                    msg = "timeout";
                else if (msg.Length > 42)
                    msg = msg.Substring(0, 42);

                return new ServiceProbeResult
                {
                    Id = id,
                    Name = name,
                    Endpoint = endpoint,
                    Ok = false,
                    LatencyMs = -1,
                    Detail = msg
                };
            }
        }

        public static List<DiagItem> RunDiagnostics(string baseDir)
        {
            var list = new List<DiagItem>();

            bool admin = IsAdministrator();
            list.Add(new DiagItem
            {
                Id = "admin",
                Label = "Права администратора",
                Ok = admin,
                Value = admin ? "Активны" : "Требуются для WinDivert"
            });

            string bfe = GetServiceState("BFE");
            list.Add(new DiagItem
            {
                Id = "bfe",
                Label = "Служба BFE",
                Ok = bfe == "Running",
                Value = bfe
            });

            string wd = GetServiceState("WinDivert");
            list.Add(new DiagItem
            {
                Id = "windivert",
                Label = "Драйвер WinDivert",
                Ok = wd == "Running" || wd == "Stopped" || wd == "Not installed",
                Value = wd
            });

            bool hasWinws = File.Exists(Path.Combine(baseDir, "bin", "winws.exe")) &&
                            File.Exists(Path.Combine(baseDir, "bin", "WinDivert64.sys"));
            list.Add(new DiagItem
            {
                Id = "binary",
                Label = "Файлы winws.exe / WinDivert",
                Ok = hasWinws,
                Value = hasWinws ? "Найдены" : "Отсутствуют в bin/"
            });

            bool ts = CheckTcpTimestamps();
            list.Add(new DiagItem
            {
                Id = "timestamps",
                Label = "TCP Timestamps",
                Ok = ts,
                Value = ts ? "Включены" : "Отключены"
            });

            return list;
        }

        public static string GetServiceState(string serviceName)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    return sc.Status.ToString();
                }
            }
            catch
            {
                // Fallback to sc.exe query for kernel drivers like WinDivert
                try
                {
                    var output = RunProcessCapture("sc.exe", "query \"" + serviceName + "\"", 3000);
                    if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) return "Running";
                    if (output.IndexOf("STOP_PENDING", StringComparison.OrdinalIgnoreCase) >= 0) return "StopPending";
                    if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0) return "Stopped";
                    return "Not installed";
                }
                catch { return "Unknown"; }
            }
        }

        public static bool CheckTcpTimestamps()
        {
            try
            {
                var outText = RunProcessCapture("netsh.exe", "interface tcp show global", 3000);
                foreach (var line in outText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.IndexOf("timestamps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("метки времени", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return line.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               line.IndexOf("включ", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool EnableTcpTimestamps()
        {
            try
            {
                RunProcessCapture("netsh.exe", "interface tcp set global timestamps=enabled", 4000);
                return true;
            }
            catch { return false; }
        }

        public static List<AdapterInfo> GetNetworkAdapters()
        {
            var result = new List<AdapterInfo>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    var props = nic.GetIPProperties();
                    var dnsAddrs = props.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();

                    result.Add(new AdapterInfo
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        Dns = dnsAddrs.Count > 0 ? string.Join(", ", dnsAddrs) : "DHCP / Auto",
                        Status = nic.OperationalStatus.ToString()
                    });
                }
            }
            catch { }
            return result;
        }

        public static string ApplyDns(string adapterName, string primary, string secondary)
        {
            if (string.IsNullOrWhiteSpace(adapterName))
                return "Адаптер не выбран";

            string safeName = adapterName.Replace("\"", "");
            if (string.IsNullOrWhiteSpace(primary) || primary == "dhcp")
            {
                var o1 = RunProcessCapture("netsh.exe", string.Format("interface ip set dns name=\"{0}\" source=dhcp", safeName), 5000);
                RunProcessCapture("ipconfig.exe", "/flushdns", 4000);
                return string.IsNullOrWhiteSpace(o1) ? "DNS сброшен на DHCP (авто)" : o1.Trim();
            }
            else
            {
                var o1 = RunProcessCapture("netsh.exe", string.Format("interface ip set dns name=\"{0}\" static {1} primary", safeName, primary), 5000);
                if (!string.IsNullOrWhiteSpace(secondary))
                {
                    RunProcessCapture("netsh.exe", string.Format("interface ip add dns name=\"{0}\" {1} index=2", safeName, secondary), 5000);
                }
                RunProcessCapture("ipconfig.exe", "/flushdns", 4000);
                return string.IsNullOrWhiteSpace(o1)
                    ? string.Format("DNS установлен: {0}, {1}", primary, secondary)
                    : o1.Trim();
            }
        }

        public static string FlushDns()
        {
            var output = RunProcessCapture("ipconfig.exe", "/flushdns", 4000);
            return string.IsNullOrWhiteSpace(output) ? "Кэш DNS очищен" : output.Trim();
        }

        public static string RunProcessCapture(string fileName, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
