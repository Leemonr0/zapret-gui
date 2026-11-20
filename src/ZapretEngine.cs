using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGUI
{
    public class StrategyInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string PreviewArgs { get; set; }
    }

    public class EngineStatus
    {
        public bool Running { get; set; }
        public string Mode { get; set; } // process | service | stopped
        public int Pid { get; set; }
        public long UptimeSeconds { get; set; }
        public string ActiveStrategy { get; set; }
        public string Scope { get; set; }
        public string ServiceState { get; set; }
        public bool IsAdmin { get; set; }
        public bool AutoPickRunning { get; set; }
        public string AutoPickStatus { get; set; }
    }

    public class ZapretEngine : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _binDir;
        private readonly string _listsDir;
        private readonly string _strategiesDir;
        private readonly string _configPath;

        private Process _winwsProc;
        private DateTime? _startedAtUtc;
        private string _activeStrategy;
        private readonly object _lock = new object();
        private readonly List<string> _logs = new List<string>();
        private CancellationTokenSource _autoPickCts;
        private bool _autoPickRunning;
        private string _autoPickStatus = "";
        private System.Threading.Timer _watchdogTimer;
        private bool _watchdogBusy;

        public AppConfig Config { get; private set; }
        public event Action<string> OnLog;
        public event Action OnStateChanged;
        public event Action<string, int, int, string> OnAutoPickProgress;

        public ZapretEngine(string baseDir)
        {
            _baseDir = baseDir;
            _binDir = Path.Combine(baseDir, "bin");
            _listsDir = Path.Combine(baseDir, "lists");
            _strategiesDir = Path.Combine(baseDir, "strategies");
            _configPath = Path.Combine(baseDir, "config.json");

            Config = AppConfig.Load(_configPath);
            EnsureUserLists();

            // Detect if already running (service or stray winws.exe)
            DetectExistingInstance();
            UpdateWatchdogTimer();
        }

        public string BaseDir { get { return _baseDir; } }

        private void NotifyStateChanged()
        {
            var h = OnStateChanged;
            if (h != null) h();
        }

        public void SaveConfig()
        {
            Config.Save(_configPath);
            UpdateWatchdogTimer();
        }

        public void EnsureUserLists()
        {
            try
            {
                Directory.CreateDirectory(_listsDir);
                EnsureNonEmptyFile(Path.Combine(_listsDir, "list-general-user.txt"), "# Пользовательские домены\ndomain.example.abc");
                EnsureNonEmptyFile(Path.Combine(_listsDir, "list-exclude-user.txt"), "domain.example.abc");
                EnsureNonEmptyFile(Path.Combine(_listsDir, "ipset-exclude-user.txt"), "203.0.113.113/32");
            }
            catch { }
        }

        private void EnsureNonEmptyFile(string path, string defaultContent)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                File.WriteAllText(path, defaultContent + Environment.NewLine, new UTF8Encoding(false));
        }

        private void DetectExistingInstance()
        {
            string svc = NetworkTools.GetServiceState("zapret");
            if (svc == "Running")
            {
                _startedAtUtc = DateTime.UtcNow;
                _activeStrategy = ReadInstalledServiceStrategy() ?? Config.Strategy;
                Log("Обнаружена активная служба Windows: zapret (" + _activeStrategy + ")");
                return;
            }

            try
            {
                var procs = Process.GetProcessesByName("winws");
                if (procs.Length > 0)
                {
                    _winwsProc = procs[0];
                    try { _startedAtUtc = _winwsProc.StartTime.ToUniversalTime(); }
                    catch { _startedAtUtc = DateTime.UtcNow; }
                    _activeStrategy = Config.Strategy;
                    Log("Обнаружен запущенный процесс winws.exe (PID " + _winwsProc.Id + ")");
                }
            }
            catch { }
        }

        private string ReadInstalledServiceStrategy()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret"))
                {
                    if (key != null)
                        return key.GetValue("zapret-gui-strategy") as string ?? key.GetValue("zapret-discord-youtube") as string;
                }
            }
            catch { }
            return null;
        }

        public void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line;
            lock (_lock)
            {
                _logs.Add(stamped);
                if (_logs.Count > 500) _logs.RemoveRange(0, _logs.Count - 500);
            }
            var handler = OnLog;
            if (handler != null) handler(stamped);
        }

        public List<string> GetLogs()
        {
            lock (_lock) return new List<string>(_logs);
        }

        public void ClearLogs()
        {
            lock (_lock) _logs.Clear();
        }

        public List<StrategyInfo> ListStrategies()
        {
            var result = new List<StrategyInfo>();
            if (!Directory.Exists(_strategiesDir)) return result;

            var files = Directory.GetFiles(_strategiesDir, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => NaturalSortKey(Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                string args = ParseBatStrategyArgs(f, "all");
                result.Add(new StrategyInfo
                {
                    Name = name,
                    FilePath = f,
                    PreviewArgs = args
                });
            }
            return result;
        }

        private static string NaturalSortKey(string s)
        {
            return Regex.Replace(s, @"\d+", m => m.Value.PadLeft(6, '0'));
        }

        public string BuildWinwsArgs(string strategyName, string scope)
        {
            if (scope == "custom" && !string.IsNullOrWhiteSpace(Config.CustomArgs))
            {
                return ExpandVariables(Config.CustomArgs.Trim());
            }

            string batPath = Path.Combine(_strategiesDir, strategyName + ".bat");
            if (!File.Exists(batPath))
            {
                var first = ListStrategies().FirstOrDefault();
                if (first == null) throw new FileNotFoundException("Стратегии .bat не найдены в папке strategies/");
                batPath = first.FilePath;
            }

            return ParseBatStrategyArgs(batPath, scope);
        }

        private string ParseBatStrategyArgs(string batPath, string scope)
        {
            var lines = File.ReadAllLines(batPath, Encoding.UTF8);
            var sb = new StringBuilder();
            bool capturing = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!capturing)
                {
                    int idx = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        capturing = true;
                        int after = idx + "winws.exe".Length;
                        if (after < line.Length && line[after] == '"') after++;
                        line = line.Substring(after).Trim();
                    }
                    else continue;
                }

                if (string.IsNullOrWhiteSpace(line)) break;

                bool cont = line.EndsWith("^");
                if (cont) line = line.Substring(0, line.Length - 1).TrimEnd();

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);

                if (!cont) break;
            }

            string args = sb.ToString();
            // Batch escaping: ^! -> !
            args = args.Replace("^!", "!");
            args = ExpandVariables(args);

            // Scope filtering: tailor lists to YouTube-only or Discord-only when requested
            if (scope == "youtube")
            {
                string ytList = Path.Combine(_listsDir, "list-youtube.txt");
                string genList = Path.Combine(_listsDir, "list-general.txt");
                string ipsetAll = Path.Combine(_listsDir, "ipset-all.txt");
                args = args.Replace(genList, ytList);
                // Restrict ipset-all blocks by replacing with youtube hostlist for clean YouTube-only scope
                args = Regex.Replace(args, "--ipset=\"" + Regex.Escape(ipsetAll) + "\"", "--hostlist=\"" + ytList + "\"", RegexOptions.IgnoreCase);
            }
            else if (scope == "discord")
            {
                string dcList = Path.Combine(_listsDir, "list-discord.txt");
                string genList = Path.Combine(_listsDir, "list-general.txt");
                string googleList = Path.Combine(_listsDir, "list-google.txt");
                args = args.Replace(genList, dcList);
                args = args.Replace(googleList, dcList);
            }

            return args.Trim();
        }

        private string ExpandVariables(string input)
        {
            string binWithSlash = _binDir.EndsWith("\\") ? _binDir : _binDir + "\\";
            string listsWithSlash = _listsDir.EndsWith("\\") ? _listsDir : _listsDir + "\\";

            string gfTcp = "12";
            string gfUdp = "12";
            string gf = "12";
            string tcpRange = string.IsNullOrWhiteSpace(Config.GameFilterTcpRange) ? "1024-65535" : Config.GameFilterTcpRange.Trim();
            string udpRange = string.IsNullOrWhiteSpace(Config.GameFilterUdpRange) ? "1024-65535" : Config.GameFilterUdpRange.Trim();

            if (Config.GameFilter == "all") { gf = tcpRange; gfTcp = tcpRange; gfUdp = udpRange; }
            else if (Config.GameFilter == "tcp") { gf = tcpRange; gfTcp = tcpRange; }
            else if (Config.GameFilter == "udp") { gf = udpRange; gfUdp = udpRange; }

            return input
                .Replace("%BIN%", binWithSlash)
                .Replace("%LISTS%", listsWithSlash)
                .Replace("%GameFilterTCP%", gfTcp)
                .Replace("%GameFilterUDP%", gfUdp)
                .Replace("%GameFilter%", gf);
        }

        public EngineStatus GetStatus()
        {
            string svcState = NetworkTools.GetServiceState("zapret");
            bool procAlive = false;
            int pid = 0;

            lock (_lock)
            {
                if (_winwsProc != null)
                {
                    try
                    {
                        if (!_winwsProc.HasExited)
                        {
                            procAlive = true;
                            pid = _winwsProc.Id;
                        }
                        else
                        {
                            _winwsProc = null;
                        }
                    }
                    catch { _winwsProc = null; }
                }
            }

            if (!procAlive && svcState != "Running")
            {
                // Double check if an external winws.exe exists
                try
                {
                    var externals = Process.GetProcessesByName("winws");
                    if (externals.Length > 0)
                    {
                        procAlive = true;
                        pid = externals[0].Id;
                    }
                }
                catch { }
            }

            bool running = procAlive || svcState == "Running";
            if (!running) _startedAtUtc = null;
            else if (_startedAtUtc == null) _startedAtUtc = DateTime.UtcNow;

            long uptime = _startedAtUtc.HasValue ? (long)Math.Max(0, (DateTime.UtcNow - _startedAtUtc.Value).TotalSeconds) : 0;

            return new EngineStatus
            {
                Running = running,
                Mode = svcState == "Running" ? "service" : (procAlive ? "process" : "stopped"),
                Pid = pid,
                UptimeSeconds = uptime,
                ActiveStrategy = _activeStrategy ?? Config.Strategy,
                Scope = Config.Scope,
                ServiceState = svcState,
                IsAdmin = NetworkTools.IsAdministrator(),
                AutoPickRunning = _autoPickRunning,
                AutoPickStatus = _autoPickStatus
            };
        }

        public bool StartBypass(string strategyOverride = null)
        {
            EnsureUserLists();

            string winwsExe = Path.Combine(_binDir, "winws.exe");
            if (!File.Exists(winwsExe))
            {
                Log("Ошибка: bin\\winws.exe не найден");
                return false;
            }

            if (!NetworkTools.IsAdministrator())
            {
                Log("Ошибка: для запуска WinDivert требуются права администратора");
                return false;
            }

            StopBypass(quiet: true);

            // Enable TCP timestamps when needed by strategies
            NetworkTools.EnableTcpTimestamps();

            string strategy = strategyOverride ?? Config.Strategy;
            string args;
            try
            {
                args = BuildWinwsArgs(strategy, Config.Scope);
            }
            catch (Exception ex)
            {
                Log("Ошибка разбора стратегии: " + ex.Message);
                return false;
            }

            if (Config.RunMode == "service")
            {
                return InstallAndStartService(strategy, winwsExe, args);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = winwsExe,
                    Arguments = args,
                    WorkingDirectory = _binDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("[winws] " + e.Data); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("[winws:err] " + e.Data); };
                p.Exited += (s, e) =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    Log("Процесс winws.exe завершился (код " + code + ")");
                    lock (_lock)
                    {
                        if (_winwsProc == p) { _winwsProc = null; _startedAtUtc = null; }
                    }
                    NotifyStateChanged();
                };

                Log("Запуск winws.exe (" + strategy + ", режим: " + Config.Scope + ")");
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                lock (_lock)
                {
                    _winwsProc = p;
                    _startedAtUtc = DateTime.UtcNow;
                    _activeStrategy = strategy;
                }

                // Wait briefly to catch immediate argument/driver errors
                Thread.Sleep(350);
                if (p.HasExited)
                {
                    Log("winws.exe остановился сразу после запуска — проверьте права админа и драйвер WinDivert");
                    NotifyStateChanged();
                    return false;
                }

                Config.Strategy = strategy;
                SaveConfig();
                NotifyStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                Log("Ошибка запуска winws.exe: " + ex.Message);
                NotifyStateChanged();
                return false;
            }
        }

        private bool InstallAndStartService(string strategy, string winwsExe, string args)
        {
            Log("Установка службы Windows: zapret (" + strategy + ")");
            NetworkTools.RunProcessCapture("net.exe", "stop zapret", 4000);
            NetworkTools.RunProcessCapture("sc.exe", "delete zapret", 4000);
            Thread.Sleep(300);

            // Escape internal quotes for sc create binPath=
            string escapedArgs = args.Replace("\"", "\\\"");
            string binPath = "\\\"" + winwsExe + "\\\" " + escapedArgs;
            string createOut = NetworkTools.RunProcessCapture("sc.exe", "create zapret binPath= \"" + binPath + "\" DisplayName= \"zapret\" start= auto", 5000);
            Log(createOut.Trim());
            NetworkTools.RunProcessCapture("sc.exe", "description zapret \"Zapret DPI bypass service\"", 3000);

            string startOut = NetworkTools.RunProcessCapture("sc.exe", "start zapret", 6000);
            Log(startOut.Trim());

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret", true))
                {
                    if (key != null) key.SetValue("zapret-gui-strategy", strategy);
                }
            }
            catch { }

            Thread.Sleep(500);
            bool ok = NetworkTools.GetServiceState("zapret") == "Running";
            if (ok)
            {
                _startedAtUtc = DateTime.UtcNow;
                _activeStrategy = strategy;
                Config.Strategy = strategy;
                SaveConfig();
            }
            NotifyStateChanged();
            return ok;
        }

        public void StopBypass(bool quiet = false)
        {
            if (!quiet) Log("Остановка обхода...");

            lock (_lock)
            {
                if (_winwsProc != null)
                {
                    try
                    {
                        if (!_winwsProc.HasExited)
                        {
                            _winwsProc.Kill();
                            _winwsProc.WaitForExit(2000);
                        }
                    }
                    catch { }
                    _winwsProc = null;
                }
            }

            // Stop zapret service if running
            if (NetworkTools.GetServiceState("zapret") == "Running")
            {
                NetworkTools.RunProcessCapture("net.exe", "stop zapret", 5000);
            }

            // Kill any stray winws.exe
            try
            {
                foreach (var p in Process.GetProcessesByName("winws"))
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }
            }
            catch { }

            _startedAtUtc = null;
            if (!quiet) Log("Обход остановлен");
            NotifyStateChanged();
        }

        public string CleanupStuck()
        {
            Log("Очистка зависших процессов и драйверов WinDivert...");
            StopBypass(quiet: true);

            var sb = new StringBuilder();
            string[] services = { "zapret", "WinDivert", "WinDivert14" };
            foreach (var s in services)
            {
                string st = NetworkTools.GetServiceState(s);
                if (st != "Not installed")
                {
                    NetworkTools.RunProcessCapture("net.exe", "stop " + s, 4000);
                    NetworkTools.RunProcessCapture("sc.exe", "delete " + s, 4000);
                    sb.AppendLine("Служба " + s + " остановлена и удалена");
                    Log("Удалена служба/драйвер: " + s);
                }
            }

            NetworkTools.RunProcessCapture("taskkill.exe", "/IM winws.exe /F", 3000);
            if (sb.Length == 0) sb.Append("Зависших процессов и служб не обнаружено");
            Log("Очистка завершена");
            NotifyStateChanged();
            return sb.ToString().Trim();
        }

        public void SetAutostartService(bool enable)
        {
            Config.RunMode = enable ? "service" : "process";
            Config.AutoStartBypass = enable;
            SaveConfig();

            if (enable)
            {
                StartBypass(Config.Strategy);
            }
            else
            {
                NetworkTools.RunProcessCapture("net.exe", "stop zapret", 4000);
                NetworkTools.RunProcessCapture("sc.exe", "delete zapret", 4000);
                Log("Автозапуск службы zapret отключён");
                NotifyStateChanged();
            }
        }

        public async Task<string> AutoPickStrategyAsync()
        {
            if (_autoPickRunning) return null;
            if (!NetworkTools.IsAdministrator())
            {
                Log("Автоподбор требует прав администратора");
                return null;
            }

            _autoPickCts = new CancellationTokenSource();
            var ct = _autoPickCts.Token;
            _autoPickRunning = true;
            _autoPickStatus = "Подготовка списка стратегий...";
            NotifyStateChanged();

            string prevMode = Config.RunMode;
            Config.RunMode = "process"; // Fast switching in process mode

            try
            {
                // Order popular strategies first for faster convergence
                var all = ListStrategies();
                string[] priority = {
                    "general (ALT)", "general (FAKE TLS AUTO)", "general (ALT2)", "general (ALT3)",
                    "general (ALT4)", "general (ALT5)", "general (SIMPLE FAKE)", "general",
                    "general (FAKE TLS AUTO ALT)", "general (ALT6)", "general (ALT7)", "general (ALT8)"
                };

                var ordered = all
                    .OrderBy(s =>
                    {
                        int idx = Array.IndexOf(priority, s.Name);
                        return idx >= 0 ? idx : 100;
                    })
                    .Take(10)
                    .ToList();

                string bestName = null;
                int bestScore = -1;
                int bestLatency = int.MaxValue;

                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var st = ordered[i];
                    _autoPickStatus = string.Format("Проверка {0}/{1}: {2}", i + 1, ordered.Count, st.Name);
                    var hProg = OnAutoPickProgress;
                    if (hProg != null) hProg(st.Name, i + 1, ordered.Count, _autoPickStatus);
                    Log(_autoPickStatus);

                    if (!StartBypass(st.Name)) continue;

                    await Task.Delay(1100, ct);
                    var probes = await NetworkTools.ProbeAllServicesAsync(ct);
                    int okCount = probes.Count(p => p.Ok);
                    int avgMs = okCount > 0 ? (int)probes.Where(p => p.Ok).Average(p => p.LatencyMs) : 9999;

                    Log(string.Format("Результат {0}: {1}/{2} сервисов, средний RTT {3} ms", st.Name, okCount, probes.Count, avgMs));

                    if (okCount > bestScore || (okCount == bestScore && avgMs < bestLatency))
                    {
                        bestScore = okCount;
                        bestLatency = avgMs;
                        bestName = st.Name;
                    }

                    if (okCount == probes.Count)
                    {
                        Log("Найдена рабочая стратегия: " + st.Name + " (" + avgMs + " ms)");
                        break;
                    }
                }

                Config.RunMode = prevMode;
                if (!string.IsNullOrEmpty(bestName))
                {
                    Config.Strategy = bestName;
                    SaveConfig();
                    StartBypass(bestName);
                    _autoPickStatus = "Выбрана: " + bestName;
                    return bestName;
                }
                else
                {
                    StopBypass(quiet: true);
                    _autoPickStatus = "Подходящая стратегия не найдена";
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                Config.RunMode = prevMode;
                _autoPickStatus = "Автоподбор отменён";
                Log("Автоподбор отменён пользователем");
                return null;
            }
            finally
            {
                _autoPickRunning = false;
                NotifyStateChanged();
            }
        }

        public void CancelAutoPick()
        {
            try { if (_autoPickCts != null) _autoPickCts.Cancel(); } catch { }
        }

        private void UpdateWatchdogTimer()
        {
            if (_watchdogTimer != null) _watchdogTimer.Dispose();
            _watchdogTimer = null;

            if (!Config.WatchdogEnabled) return;
            int mins = Math.Max(1, Math.Min(120, Config.WatchdogIntervalMin));
            int ms = mins * 60 * 1000;
            _watchdogTimer = new System.Threading.Timer(async _ => await WatchdogTickAsync(), null, ms, ms);
        }

        private async Task WatchdogTickAsync()
        {
            if (_watchdogBusy || _autoPickRunning) return;
            if (!GetStatus().Running) return;

            _watchdogBusy = true;
            try
            {
                var results = await NetworkTools.ProbeAllServicesAsync(CancellationToken.None);
                int ok = results.Count(r => r.Ok);
                if (ok <= 1)
                {
                    Log("[Сторож] Потеряна доступность (" + ok + "/4) — перезапуск обхода");
                    StartBypass(Config.Strategy);
                }
            }
            catch { }
            finally { _watchdogBusy = false; }
        }

        public List<string> ListHostFiles()
        {
            if (!Directory.Exists(_listsDir)) return new List<string>();
            return Directory.GetFiles(_listsDir, "*.txt")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string ReadHostFile(string fileName)
        {
            string safe = Path.GetFileName(fileName);
            string full = Path.Combine(_listsDir, safe);
            if (!File.Exists(full)) return "";
            return File.ReadAllText(full, Encoding.UTF8);
        }

        public void SaveHostFile(string fileName, string content)
        {
            string safe = Path.GetFileName(fileName);
            string full = Path.Combine(_listsDir, safe);
            if (string.IsNullOrWhiteSpace(content)) content = "domain.example.abc";
            File.WriteAllText(full, content, new UTF8Encoding(false));
            Log("Сохранён список: lists\\" + safe);
        }

        public void Dispose()
        {
            if (_watchdogTimer != null) _watchdogTimer.Dispose();
            if (_autoPickCts != null) _autoPickCts.Cancel();
            // Do not kill Windows service on GUI exit; only stop child process if process mode
            if (Config.RunMode == "process")
            {
                StopBypass(quiet: true);
            }
        }
    }
}
