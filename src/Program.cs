using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ZapretGUI
{
    static class Program
    {
        private const string RuntimeVersion = "v1.10.4";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [STAThread]
        static void Main(string[] args)
        {
            bool previewMode = args != null && args.Any(a =>
                a.Equals("--preview", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

            if (!previewMode && !NetworkTools.IsAdministrator())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = string.Join(" ", args ?? new string[0]),
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // Continue in limited mode if user declined UAC
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\ZapretGUI_SingleInstance_v1", out createdNew))
            {
                if (!createdNew && !previewMode)
                {
                    MessageBox.Show("zapret уже запущен (проверьте системный трей).", "zapret",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string runtimeDir = PrepareRuntimeDirectory();
                ConfigureNativeAndManagedResolvers(runtimeDir);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                RunApp(runtimeDir);
            }
        }

        private static string PrepareRuntimeDirectory()
        {
            // If portable bin/ + WebView2 DLLs already sit next to the exe, use exe directory directly
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "bin", "winws.exe")) &&
                File.Exists(Path.Combine(exeDir, "Microsoft.Web.WebView2.Core.dll")))
            {
                return exeDir;
            }

            // Standalone single-exe mode: extract embedded payload.zip into LocalAppData
            string runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZapretGUI", "runtime", RuntimeVersion);

            Directory.CreateDirectory(runtimeDir);

            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("payload.zip"))
            {
                if (stream == null) return exeDir;

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                        string relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(runtimeDir, relPath);
                        string destFolder = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destFolder))
                            Directory.CreateDirectory(destFolder);

                        // Preserve user-edited hostlists and config across launches
                        bool isUserEditable = relPath.StartsWith("lists" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                           || relPath.Equals("config.json", StringComparison.OrdinalIgnoreCase);

                        if (File.Exists(destPath))
                        {
                            if (isUserEditable) continue;
                            // Skip overwriting identical-size binaries if already extracted
                            try
                            {
                                if (new FileInfo(destPath).Length == entry.Length) continue;
                            }
                            catch { }
                        }

                        try
                        {
                            using (var inStream = entry.Open())
                            using (var outStream = File.Create(destPath))
                            {
                                inStream.CopyTo(outStream);
                            }
                        }
                        catch
                        {
                            // Ignore if file is currently locked by a running winws/service instance
                        }
                    }
                }
            }

            return runtimeDir;
        }

        private static void ConfigureNativeAndManagedResolvers(string runtimeDir)
        {
            SetDllDirectory(runtimeDir);
            string loaderPath = Path.Combine(runtimeDir, "WebView2Loader.dll");
            if (File.Exists(loaderPath))
            {
                LoadLibrary(loaderPath);
            }

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    string asmName = new AssemblyName(eventArgs.Name).Name + ".dll";
                    string candidate = Path.Combine(runtimeDir, asmName);
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }
                catch { }
                return null;
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunApp(string runtimeDir)
        {
            using (var engine = new ZapretEngine(runtimeDir))
            {
                Application.Run(new MainForm(engine));
            }
        }
    }
}
