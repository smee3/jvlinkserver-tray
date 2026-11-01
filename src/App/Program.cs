using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("JVLinkServer Tray")]
[assembly: AssemblyProduct("JVLinkServer Tray")]
[assembly: AssemblyCopyright("Copyright (c) 2026 smee3. MIT License.")]
[assembly: AssemblyVersion(JVLinkServerTray.TrayVersion.Value)]
[assembly: AssemblyFileVersion(JVLinkServerTray.TrayVersion.Value)]

namespace JVLinkServerTray
{
    public static class Program
    {
        const string IniName = "JVLinkServerTray.ini";

        // DPI awareness (per monitor) is declared in src\App\app.manifest, not set here.
        [STAThread]
        public static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string exeDir = ExeDir();
            string path, error = null;
            TrayConfig config = null;
            string arg = TrayText.ConfigArg(args);
            try
            {
                if (arg == null)
                {
                    path = Path.Combine(exeDir, IniName);
                    config = TrayConfig.LoadFile(path);           // a missing ini next to the EXE means the defaults
                }
                else
                {
                    path = arg.Length == 0 ? "(--config の値がありません)" : Path.GetFullPath(arg);
                    // An ini named on the command line must exist: never fall back to the defaults (the real server).
                    if (arg.Length == 0 || !File.Exists(path)) error = "設定ファイルが見つかりません（" + path + "）";
                    else config = TrayConfig.LoadFile(path);
                }
            }
            catch (Exception e)
            {
                path = arg ?? Path.Combine(exeDir, IniName);
                error = "設定ファイルを読めません（" + e.Message + "）";
                config = null;
            }

            string logDir = null;
            if (config != null)
            {
                try { logDir = config.LogDir; }
                catch (Exception) { }
                if (string.IsNullOrEmpty(logDir))
                {
                    error = "記録の場所を決められません（ini の ServerExe と ServerArgs の --log-dir）";
                    config = null;
                }
            }
            if (config == null) logDir = Path.Combine(exeDir, "logs");
            var log = new TrayLog(logDir);

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                log.Write("思わぬエラーで終わります: " + e.ExceptionObject);
            };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                log.Write("思わぬエラー: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            };

            // Without a readable ini the tray controls no server; it only shows red with the reason.
            int port = config != null ? config.Port : ServerArgs.DefaultPort;
            Mutex mutex;
            try
            {
                mutex = SingleInstance.TryAcquire(port);
            }
            catch (Exception e)
            {
                log.Write("起動: 二重起動を確かめられないので終わります（" + e.Message + "）");
                return 1;
            }
            if (mutex == null)
            {
                log.Write("起動: すでに動いています（ポート " + port + "）。このトレイは終わります");
                return 0;
            }

            TrayApp app = null;
            try
            {
                app = new TrayApp(config, path, error, log);
                Application.Run(app);
                return 0;
            }
            catch (Exception e)
            {
                log.Write("思わぬエラーで終わります: " + e.GetType().Name + ": " + e.Message);
                return 1;
            }
            finally
            {
                if (app != null)
                {
                    try { app.Dispose(); } catch (Exception) { }   // takes the icon away even if Run threw
                }
                TrayIcons.DisposeAll();
                try { mutex.ReleaseMutex(); } catch (Exception) { }
                mutex.Dispose();   // the mutex stays referenced (held) until here
            }
        }

        static string ExeDir()
        {
            try
            {
                return Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location));
            }
            catch (Exception)
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }
}
