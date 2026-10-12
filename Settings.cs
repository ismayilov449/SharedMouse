using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SharedMouse
{
    /// <summary>What this laptop does. Saved in %APPDATA%\SharedMouse\settings.ini, or given on the command line.</summary>
    sealed class Settings
    {
        public bool IsMain = true;
        public Side Side = Side.Right;   // main only: where the second laptop is
        public int DelayMs = 150;        // main only: how long to push against the edge before switching
        public bool ShareClipboard = true, ShareFiles = true;
        public string Peer;              // main only: Bluetooth address to connect to instead of searching
        public string Tcp;               // use a cable/LAN instead of Bluetooth

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharedMouse", "settings.ini");

        public ulong? PeerAddress => Peer == null ? (ulong?)null : Bluetooth.ParseAddress(Peer);

        public Settings Clone() => (Settings)MemberwiseClone();

        public bool SameAs(Settings o) =>
            IsMain == o.IsMain && Side == o.Side && DelayMs == o.DelayMs && ShareClipboard == o.ShareClipboard &&
            ShareFiles == o.ShareFiles && Peer == o.Peer && Tcp == o.Tcp;

        /// <returns>The saved settings, or null before the first setup.</returns>
        public static Settings Load()
        {
            if (!File.Exists(FilePath))
                return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith("#"))
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            var s = new Settings();
            string Get(string key) => values.TryGetValue(key, out string v) && v.Length > 0 ? v : null;
            bool Flag(string key, bool fallback) => Get(key) == null ? fallback : Get(key).Equals("true", StringComparison.OrdinalIgnoreCase);
            s.IsMain = !"second".Equals(Get("role"), StringComparison.OrdinalIgnoreCase);
            try { s.Side = Edges.Parse(Get("side")?.ToLowerInvariant() ?? "right"); } catch (ArgumentException) { }
            if (int.TryParse(Get("delay"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay))
                s.DelayMs = Math.Max(0, Math.Min(5000, delay));
            s.ShareClipboard = Flag("clipboard", true);
            s.ShareFiles = Flag("files", true);
            s.Peer = Get("peer");
            s.Tcp = Get("tcp");
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, new[]
            {
                "# SharedMouse settings. Easiest to change from the tray icon: right-click > Settings.",
                "role=" + (IsMain ? "main" : "second"),
                "side=" + Side.ToString().ToLowerInvariant(),
                "delay=" + DelayMs.ToString(CultureInfo.InvariantCulture),
                "clipboard=" + (ShareClipboard ? "true" : "false"),
                "files=" + (ShareFiles ? "true" : "false"),
                "# peer=00:11:22:33:44:55   connect to this Bluetooth address instead of searching (main laptop)",
                "peer=" + Peer,
                "# tcp=host:port (main) / tcp=[host:]port (second)   use a network cable instead of Bluetooth",
                "tcp=" + Tcp,
            });
        }

        /// <summary>From "main|second [options]". Throws ArgumentException/FormatException for bad options.</summary>
        public static Settings FromArgs(string[] args)
        {
            var s = new Settings
            {
                IsMain = args[0].Equals("main", StringComparison.OrdinalIgnoreCase),
                Side = Edges.Parse(Option(args, "--side") ?? "right"),
                DelayMs = int.Parse(Option(args, "--delay") ?? "150", CultureInfo.InvariantCulture),
                ShareClipboard = !HasFlag(args, "--no-clipboard"),
                ShareFiles = !HasFlag(args, "--no-files"),
                Peer = Option(args, "--peer"),
                Tcp = Option(args, "--tcp"),
            };
            _ = s.PeerAddress; // validates it
            return s;
        }

        static string Option(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].ToLowerInvariant();
            return null;
        }

        static bool HasFlag(string[] args, string name) =>
            Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Start with Windows": the per-user Run list, like other tray apps. No admin rights needed.</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "SharedMouse";
        public const string Flag = "--autostart";

        static string Command => $"\"{Application.ExecutablePath}\" {Flag}";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return key?.GetValue(Name) != null;
                }
                catch (SecurityException) { return false; }
            }
        }

        /// <returns>False if Windows didn't allow it.</returns>
        public static bool Set(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on)
                        key.SetValue(Name, Command);
                    else
                        key.DeleteValue(Name, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                Log.Write("Couldn't change \"Start with Windows\": " + ex.Message);
                return false;
            }
        }

        /// <summary>If the exe was moved since "Start with Windows" was turned on, point the entry at the new place.</summary>
        public static void Refresh()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (key?.GetValue(Name) is string current && current != Command)
                        Set(true);
            }
            catch (SecurityException) { }
        }
    }
}
