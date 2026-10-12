using System;
using System.Globalization;
using System.IO;

namespace SharedMouse
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();
            Native.PreferLowLatency();
            Console.Title = "SharedMouse";

            bool interactive = args.Length == 0;
            try
            {
                string mode = interactive ? AskMode() : args[0].ToLowerInvariant();
                var clipboard = new ClipboardSync(clipboardOn: !HasFlag(args, "--no-clipboard"), filesOn: !HasFlag(args, "--no-files"));
                string tcp = Option(args, "--tcp");

                switch (mode)
                {
                    case "main":
                        Side side = Edges.Parse(Option(args, "--side") ?? (interactive ? AskSide() : "right"));
                        int delay = int.Parse(Option(args, "--delay") ?? "150", CultureInfo.InvariantCulture);
                        string peer = Option(args, "--peer");
                        titleBase = "SharedMouse - main laptop";
                        SetStatus("not connected");
                        new Sender(side, delay, peer == null ? (ulong?)null : Bluetooth.ParseAddress(peer), tcp, clipboard).Run();
                        return 0;

                    case "second":
                        titleBase = "SharedMouse - second laptop";
                        SetStatus("not connected");
                        new Receiver(tcp, clipboard).Run();
                        return 0;

                    case "devices":
                        var devices = Bluetooth.PairedComputers();
                        Console.WriteLine(devices.Count == 0 ? "No paired computers." : "Paired computers:");
                        foreach (var d in devices)
                            Console.WriteLine($"  {Bluetooth.FormatAddress(d.Address)}  {d.Name}");
                        return 0;

                    default:
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Console.WriteLine("Error: " + ex.Message);
                if (interactive)
                {
                    Console.WriteLine("Press Enter to close.");
                    Console.ReadLine();
                }
                return 1;
            }
        }

        static string titleBase = "SharedMouse";

        /// <summary>Shows the connection state in the window title, e.g. "SharedMouse - main laptop - MSI - delay 12 ms".</summary>
        public static void SetStatus(string status)
        {
            try { Console.Title = titleBase + " - " + status; }
            catch (IOException) { }
        }

        static string AskMode()
        {
            Console.WriteLine("SharedMouse - one keyboard and mouse for two laptops");
            Console.WriteLine();
            Console.WriteLine("  1  MAIN laptop    (its keyboard and mouse will be shared)");
            Console.WriteLine("  2  SECOND laptop  (it will be controlled)");
            Console.WriteLine();
            while (true)
            {
                Console.Write("Which one is this laptop? Type 1 or 2: ");
                string answer = Console.ReadLine()?.Trim();
                if (answer == "1") return "main";
                if (answer == "2") return "second";
                if (answer == null) return "";
            }
        }

        static string AskSide()
        {
            while (true)
            {
                Console.Write("Where is the second laptop? L = left, R = right, T = top (above), B = bottom (below) [R]: ");
                string answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(answer))
                    return "right";
                if (answer == "l" || answer == "r" || answer == "t" || answer == "b")
                {
                    Console.WriteLine();
                    return answer;
                }
            }
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

        /// <summary>"host:port" or just "port".</summary>
        public static (string host, int port) ParseHostPort(string value, string defaultHost)
        {
            int colon = value.LastIndexOf(':');
            string host = colon >= 0 ? value.Substring(0, colon) : defaultHost;
            string port = colon >= 0 ? value.Substring(colon + 1) : value;
            return (host, int.Parse(port, CultureInfo.InvariantCulture));
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  SharedMouse.exe                     ask which laptop this is");
            Console.WriteLine("  SharedMouse.exe main [--side right|left|top|bottom] [--delay 150] [--peer 00:11:22:33:44:55]");
            Console.WriteLine("                                      share this laptop's keyboard and mouse");
            Console.WriteLine("  SharedMouse.exe second              let the main laptop control this one");
            Console.WriteLine("  SharedMouse.exe devices             list paired Bluetooth computers");
            Console.WriteLine();
            Console.WriteLine("  --side            where the second laptop stands, seen from the main one (default right)");
            Console.WriteLine("  --delay ms        how long to push against the edge before switching (default 150, 0 = instantly)");
            Console.WriteLine("  --no-clipboard    don't share copied text and images");
            Console.WriteLine("  --no-files        don't share files copied in Explorer");
            Console.WriteLine("  --tcp host:port (main) / --tcp [host:]port (second) uses a network cable/LAN instead of Bluetooth.");
        }
    }
}
