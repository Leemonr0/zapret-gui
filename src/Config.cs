using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ZapretGUI
{
    public class AppConfig
    {
        public string Strategy { get; set; }
        public string Scope { get; set; }
        public string ViewMode { get; set; }
        public string RunMode { get; set; }
        public string Language { get; set; }
        public string GameFilter { get; set; }
        public string GameFilterTcpRange { get; set; }
        public string GameFilterUdpRange { get; set; }
        public bool WatchdogEnabled { get; set; }
        public int WatchdogIntervalMin { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool AutoStartBypass { get; set; }
        public string CustomArgs { get; set; }

        public AppConfig()
        {
            Strategy = "general (ALT)";
            Scope = "all";
            ViewMode = "simple";
            RunMode = "process";
            Language = "ru";
            GameFilter = "disabled";
            GameFilterTcpRange = "1024-65535";
            GameFilterUdpRange = "1024-65535";
            WatchdogEnabled = false;
            WatchdogIntervalMin = 15;
            MinimizeToTray = true;
            AutoStartBypass = false;
            CustomArgs = "";
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static AppConfig Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    var cfg = Json.Deserialize<AppConfig>(text);
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            return new AppConfig();
        }

        public void Save(string path)
        {
            try
            {
                var json = Json.Serialize(this);
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
