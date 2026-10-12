using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SharedMouse
{
    static class Program
    {
        const string InstanceName = @"Local\SharedMouse", ShowSettingsName = @"Local\SharedMouse.ShowSettings";
        const string RestartedFlag = "--restarted";

        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();
            Native.PreferLowLatency();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Fail(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fail(e.ExceptionObject as Exception);

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (command)
                {
                    case "":
                    case Autostart.Flag:
                    case RestartedFlag:
                        return RunWithSavedSettings(command);

                    case "main":
                    case "second":
                        // Command line: these options instead of the saved settings; the log also goes to the console.
                        Settings settings;
                        try { settings = Settings.FromArgs(args); }
                        catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException)
                        {
                            ShowUsage(ex.Message);
                            return 1;
                        }
                        Native.AttachParentConsole();
                        Application.Run(new TrayApp(settings, saved: false, greeting: null, showSignal: null));
                        return 0;

                    case "devices":
                        var text = new StringBuilder();
                        var devices = Bluetooth.PairedComputers();
                        text.AppendLine(devices.Count == 0 ? "No paired computers." : "Paired computers:");
                        foreach (var d in devices)
                            text.AppendLine($"  {Bluetooth.FormatAddress(d.Address)}  {d.Name}");
                        Show(text.ToString());
                        return 0;

                    default:
                        ShowUsage(null);
                        return 1;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Fail(ex);
                return 1;
            }
        }

        /// <summary>The normal way: double-click or Windows sign-in. One copy at a time, settings from settings.ini.</summary>
        static int RunWithSavedSettings(string command)
        {
            bool restart = false;
            using (var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsName))
            using (var instance = new Mutex(false, InstanceName))
            {
                if (!Acquire(instance, command == RestartedFlag ? 10000 : 0))
                {
                    // Already running (the icon is just hidden): open its settings instead of starting twice.
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    showSettings.Set();
                    return 0;
                }
                try
                {
                    Settings settings = Settings.Load();
                    string greeting;
                    if (settings == null)
                    {
                        settings = FirstSetup();
                        if (settings == null)
                            return 0;
                        greeting = "SharedMouse is running. Its icon is next to the clock (click ^ if you don't see it). " +
                                   "Right-click it for settings.";
                    }
                    else
                    {
                        Autostart.Refresh();
                        greeting = command == Autostart.Flag ? null :
                                   command == RestartedFlag ? "Settings saved." :
                                   "SharedMouse is running. Right-click its icon next to the clock for settings.";
                    }
                    var app = new TrayApp(settings, saved: true, greeting, showSettings);
                    Application.Run(app);
                    restart = app.RestartRequested;
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
            if (restart)
                Process.Start(Application.ExecutablePath, RestartedFlag);
            return 0;
        }

        static Settings FirstSetup()
        {
            using (var form = new SetupForm(new Settings(), firstRun: true, startWithWindows: true))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return null;
                form.Result.Save();
                Autostart.Set(form.StartWithWindows);
                return form.Result;
            }
        }

        static bool Acquire(Mutex mutex, int timeoutMs)
        {
            try { return mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { return true; } // the previous copy ended without letting go
        }

        static void Fail(Exception ex)
        {
            Log.Write("Error: " + ex);
            MessageBox.Show($"SharedMouse stopped because of an error:\n\n{ex?.Message}\n\nDetails: {Log.FilePath}",
                "SharedMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }

        /// <summary>To the console when started from one, otherwise in a message box.</summary>
        static void Show(string text)
        {
            if (Native.AttachParentConsole())
                Console.Write(text);
            else
                MessageBox.Show(text, "SharedMouse");
        }

        /// <summary>"host:port" or just "port".</summary>
        public static (string host, int port) ParseHostPort(string value, string defaultHost)
        {
            int colon = value.LastIndexOf(':');
            string host = colon >= 0 ? value.Substring(0, colon) : defaultHost;
            string port = colon >= 0 ? value.Substring(colon + 1) : value;
            return (host, int.Parse(port, CultureInfo.InvariantCulture));
        }

        static void ShowUsage(string error)
        {
            Show((error == null ? "" : error + "\n\n") +
                 "Usage:\n" +
                 "  SharedMouse.exe                  set up on the first start, then runs next to the clock\n" +
                 "  SharedMouse.exe main [--side right|left|top|bottom] [--delay 150] [--peer 00:11:22:33:44:55]\n" +
                 "  SharedMouse.exe second           let the main laptop control this one\n" +
                 "  SharedMouse.exe devices          list paired Bluetooth computers\n" +
                 "\n" +
                 "  main/second use these options instead of the saved settings:\n" +
                 "  --side            where the second laptop stands, seen from the main one (default right)\n" +
                 "  --delay ms        how long to push against the edge before switching (default 150, 0 = instantly)\n" +
                 "  --no-clipboard    don't share copied text and images\n" +
                 "  --no-files        don't share files copied in Explorer\n" +
                 "  --tcp host:port (main) / --tcp [host:]port (second) uses a network cable/LAN instead of Bluetooth\n");
        }
    }
}
