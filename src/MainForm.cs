using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ZapretGUI
{
    public class MainForm : Form
    {
        private readonly ZapretEngine _engine;
        private WebView2 _webView;
        private NotifyIcon _trayIcon;
        private System.Windows.Forms.Timer _statusTimer;
        private bool _reallyExiting;
        private bool _webReady;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // Native DWM & window drag/resize interop
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;
        private const int WM_NCHITTEST = 0x84;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;

        public MainForm(ZapretEngine engine)
        {
            _engine = engine;

            Text = "zapret";
            Size = new Size(1100, 748);
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 15, 16); // #0f0f10
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(1);
            Icon = CreateAppIcon();

            SetupTray();
            SetupWebView();

            _engine.OnLog += line => PushEvent("log", new { line });
            _engine.OnStateChanged += () => PushStatus();
            _engine.OnAutoPickProgress += (name, idx, total, status) =>
                PushEvent("autopick", new { name, idx, total, status });

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += (s, e) => PushStatus();
            _statusTimer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                int borderColor = 0x00262628; // subtle hairline border (COLORREF BGR)
                DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x20000; // WS_MINIMIZEBOX
                cp.Style |= 0x10000; // WS_MAXIMIZEBOX
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                const int grip = 6;
                var pt = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
                bool left = pt.X <= grip;
                bool right = pt.X >= ClientSize.Width - grip;
                bool top = pt.Y <= grip;
                bool bottom = pt.Y >= ClientSize.Height - grip;

                if (top && left) { m.Result = (IntPtr)13; return; }
                if (top && right) { m.Result = (IntPtr)14; return; }
                if (bottom && left) { m.Result = (IntPtr)16; return; }
                if (bottom && right) { m.Result = (IntPtr)17; return; }
                if (left) { m.Result = (IntPtr)10; return; }
                if (right) { m.Result = (IntPtr)11; return; }
                if (top) { m.Result = (IntPtr)12; return; }
                if (bottom) { m.Result = (IntPtr)15; return; }
            }
            base.WndProc(ref m);
        }

        private void SetupTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть zapret", null, (s, e) => RestoreWindow());
            menu.Items.Add("Запустить обход", null, (s, e) => Task.Run(() => _engine.StartBypass()));
            menu.Items.Add("Остановить обход", null, (s, e) => Task.Run(() => _engine.StopBypass()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Icon = CreateAppIcon(),
                Text = "zapret",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreWindow();
        }

        private async void SetupWebView()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(20, 20, 21) // #141415
            };
            Controls.Add(_webView);

            try
            {
                string userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZapretGUI", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string html = LoadUiHtml();
                _webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    _webReady = true;
                    PushInitialState();
                };
                _webView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось инициализировать WebView2 Runtime:\n" + ex.Message,
                    "zapret", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string LoadUiHtml()
        {
            // Prefer external ui/index.html if present next to exe (for live iteration), otherwise embedded resource
            string externalPath = Path.Combine(_engine.BaseDir, "ui", "index.html");
            if (File.Exists(externalPath))
                return File.ReadAllText(externalPath, Encoding.UTF8);

            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("index.html"))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            return "<html><body style='background:#141415;color:#ededed;font-family:sans-serif;padding:24px'>UI resource not found.</body></html>";
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                var msg = _json.Deserialize<Dictionary<string, object>>(raw);
                if (msg == null || !msg.ContainsKey("cmd")) return;

                string cmd = Convert.ToString(msg["cmd"]);
                string reqId = msg.ContainsKey("id") ? Convert.ToString(msg["id"]) : null;

                switch (cmd)
                {
                    case "window.drag":
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                        break;

                    case "window.minimize":
                        WindowState = FormWindowState.Minimized;
                        break;

                    case "window.maximize":
                        WindowState = WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal
                            : FormWindowState.Maximized;
                        PushEvent("windowState", new { maximized = WindowState == FormWindowState.Maximized });
                        break;

                    case "window.close":
                        if (_engine.Config.MinimizeToTray)
                        {
                            Hide();
                        }
                        else
                        {
                            ExitApp();
                        }
                        break;

                    case "window.exit":
                        ExitApp();
                        break;

                    case "app.init":
                        PushInitialState();
                        break;

                    case "app.elevate":
                        RelaunchAsAdmin();
                        break;

                    case "app.openFolder":
                        try { Process.Start("explorer.exe", _engine.BaseDir); } catch { }
                        break;

                    case "bypass.start":
                        string stName = msg.ContainsKey("strategy") ? Convert.ToString(msg["strategy"]) : null;
                        Task.Run(() =>
                        {
                            bool ok = _engine.StartBypass(string.IsNullOrEmpty(stName) ? null : stName);
                            Reply(reqId, new { ok });
                        });
                        break;

                    case "bypass.stop":
                        Task.Run(() =>
                        {
                            _engine.StopBypass();
                            Reply(reqId, new { ok = true });
                        });
                        break;

                    case "bypass.cleanup":
                        Task.Run(() =>
                        {
                            string res = _engine.CleanupStuck();
                            Reply(reqId, new { ok = true, message = res });
                        });
                        break;

                    case "bypass.autopick":
                        Task.Run(async () =>
                        {
                            string picked = await _engine.AutoPickStrategyAsync();
                            Reply(reqId, new { ok = !string.IsNullOrEmpty(picked), strategy = picked });
                        });
                        break;

                    case "bypass.cancelAutopick":
                        _engine.CancelAutoPick();
                        Reply(reqId, new { ok = true });
                        break;

                    case "probe.run":
                        Task.Run(async () =>
                        {
                            var probes = await NetworkTools.ProbeAllServicesAsync(CancellationToken.None);
                            Reply(reqId, new { probes });
                        });
                        break;

                    case "diag.run":
                        Task.Run(() =>
                        {
                            var diag = NetworkTools.RunDiagnostics(_engine.BaseDir);
                            Reply(reqId, new { diag });
                        });
                        break;

                    case "config.update":
                        UpdateConfigFromMessage(msg);
                        Reply(reqId, new { ok = true, config = _engine.Config });
                        PushStatus();
                        break;

                    case "autostart.set":
                        bool en = msg.ContainsKey("enabled") && Convert.ToBoolean(msg["enabled"]);
                        Task.Run(() =>
                        {
                            _engine.SetAutostartService(en);
                            Reply(reqId, new { ok = true, config = _engine.Config });
                        });
                        break;

                    case "lists.read":
                        string fn = Convert.ToString(msg["file"]);
                        Reply(reqId, new { file = fn, content = _engine.ReadHostFile(fn) });
                        break;

                    case "lists.save":
                        string sfn = Convert.ToString(msg["file"]);
                        string content = Convert.ToString(msg["content"]);
                        _engine.SaveHostFile(sfn, content);
                        Reply(reqId, new { ok = true });
                        break;

                    case "dns.list":
                        Task.Run(() =>
                        {
                            var adapters = NetworkTools.GetNetworkAdapters();
                            Reply(reqId, new { adapters });
                        });
                        break;

                    case "dns.apply":
                        string adapter = Convert.ToString(msg["adapter"]);
                        string primary = Convert.ToString(msg["primary"]);
                        string secondary = msg.ContainsKey("secondary") ? Convert.ToString(msg["secondary"]) : "";
                        Task.Run(() =>
                        {
                            string outMsg = NetworkTools.ApplyDns(adapter, primary, secondary);
                            _engine.Log("[DNS] " + outMsg);
                            var adapters = NetworkTools.GetNetworkAdapters();
                            Reply(reqId, new { ok = true, message = outMsg, adapters });
                        });
                        break;

                    case "dns.flush":
                        Task.Run(() =>
                        {
                            string outMsg = NetworkTools.FlushDns();
                            _engine.Log("[DNS] " + outMsg);
                            Reply(reqId, new { ok = true, message = outMsg });
                        });
                        break;

                    case "tcp.timestamps":
                        Task.Run(() =>
                        {
                            bool ok = NetworkTools.EnableTcpTimestamps();
                            _engine.Log("[TCP] Timestamps включены");
                            Reply(reqId, new { ok });
                        });
                        break;

                    case "logs.clear":
                        _engine.ClearLogs();
                        Reply(reqId, new { ok = true });
                        break;
                }
            }
            catch (Exception ex)
            {
                _engine.Log("[UI bridge error] " + ex.Message);
            }
        }

        private void UpdateConfigFromMessage(Dictionary<string, object> msg)
        {
            var c = _engine.Config;
            if (msg.ContainsKey("strategy")) c.Strategy = Convert.ToString(msg["strategy"]);
            if (msg.ContainsKey("scope")) c.Scope = Convert.ToString(msg["scope"]);
            if (msg.ContainsKey("viewMode")) c.ViewMode = Convert.ToString(msg["viewMode"]);
            if (msg.ContainsKey("runMode")) c.RunMode = Convert.ToString(msg["runMode"]);
            if (msg.ContainsKey("language")) c.Language = Convert.ToString(msg["language"]);
            if (msg.ContainsKey("gameFilter")) c.GameFilter = Convert.ToString(msg["gameFilter"]);
            if (msg.ContainsKey("gameFilterTcpRange")) c.GameFilterTcpRange = Convert.ToString(msg["gameFilterTcpRange"]);
            if (msg.ContainsKey("gameFilterUdpRange")) c.GameFilterUdpRange = Convert.ToString(msg["gameFilterUdpRange"]);
            if (msg.ContainsKey("watchdogEnabled")) c.WatchdogEnabled = Convert.ToBoolean(msg["watchdogEnabled"]);
            if (msg.ContainsKey("watchdogIntervalMin")) c.WatchdogIntervalMin = Convert.ToInt32(msg["watchdogIntervalMin"]);
            if (msg.ContainsKey("minimizeToTray")) c.MinimizeToTray = Convert.ToBoolean(msg["minimizeToTray"]);
            if (msg.ContainsKey("customArgs")) c.CustomArgs = Convert.ToString(msg["customArgs"]);
            _engine.SaveConfig();

            // If strategy or scope changed while bypass is active and restart flag passed
            if (msg.ContainsKey("restartIfRunning") && Convert.ToBoolean(msg["restartIfRunning"]) && _engine.GetStatus().Running)
            {
                Task.Run(() => _engine.StartBypass(c.Strategy));
            }
        }

        private void PushInitialState()
        {
            var payload = new
            {
                status = _engine.GetStatus(),
                config = _engine.Config,
                strategies = _engine.ListStrategies(),
                hostFiles = _engine.ListHostFiles(),
                logs = _engine.GetLogs(),
                version = "1.10.4"
            };
            PushEvent("init", payload);

            // Kick off background diagnostics & adapter list
            Task.Run(() =>
            {
                PushEvent("diag", new { diag = NetworkTools.RunDiagnostics(_engine.BaseDir) });
                PushEvent("adapters", new { adapters = NetworkTools.GetNetworkAdapters() });
            });
        }

        private void PushStatus()
        {
            if (!_webReady) return;
            var st = _engine.GetStatus();
            PushEvent("status", st);
            if (_trayIcon != null)
            {
                string tip = st.Running ? ("zapret — активен (" + st.ActiveStrategy + ")") : "zapret — остановлен";
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                try { _trayIcon.Text = tip; } catch { }
            }
        }

        private void Reply(string reqId, object data)
        {
            if (string.IsNullOrEmpty(reqId)) return;
            PushEvent("reply", new { id = reqId, data });
        }

        private void PushEvent(string type, object payload)
        {
            if (!_webReady || _webView == null || _webView.IsDisposed) return;
            try
            {
                string serialized = _json.Serialize(new { type, payload });
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() =>
                    {
                        try { if (_webView != null && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(serialized); } catch { }
                    }));
                }
                else
                {
                    if (_webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(serialized);
                }
            }
            catch { }
        }

        private void RelaunchAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                ExitApp();
            }
            catch { }
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _reallyExiting = true;
            if (_statusTimer != null) _statusTimer.Stop();
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            _engine.Dispose();
            Close();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExiting && e.CloseReason == CloseReason.UserClosing && _engine.Config.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        public static Icon CreateAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var bg = new SolidBrush(Color.FromArgb(25, 25, 27)))
                using (var border = new Pen(Color.FromArgb(70, 237, 237, 237), 1.5f))
                {
                    var rect = new Rectangle(2, 2, 28, 28);
                    using (var path = RoundedRect(rect, 7))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                }

                // Minimal crisp geometric "Z" shield mark
                using (var pen = new Pen(Color.FromArgb(237, 237, 237), 2.2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(10, 11),
                        new PointF(22, 11),
                        new PointF(10, 21),
                        new PointF(22, 21)
                    });
                }

                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
