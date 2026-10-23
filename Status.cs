using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SharedMouse
{
    /// <summary>The connection state shown by the tray icon. Set from network/input threads; Changed fires on those threads.</summary>
    static class Status
    {
        static readonly object gate = new object();
        static object link; // the connection that is up, or null

        public static string Peer { get; private set; }
        public static bool Connected => Volatile.Read(ref link) != null;
        /// <summary>The shared keyboard and mouse are on the second laptop right now.</summary>
        public static bool Remote { get; private set; }
        /// <summary>Average round trip in ms, or -1 while unknown.</summary>
        public static double Delay { get; private set; } = -1;
        /// <summary>The user clicked "Disconnect": not connecting until "Connect".</summary>
        public static bool Paused { get; private set; }

        public static event Action Changed;

        public static void SetPaused(bool paused)
        {
            if (Paused == paused)
                return;
            Paused = paused;
            Changed?.Invoke();
        }

        public static void SetConnected(object l, string peer)
        {
            lock (gate)
            {
                link = l;
                Peer = peer;
                Delay = -1;
                Remote = false;
            }
            Changed?.Invoke();
        }

        public static void SetDisconnected(object l)
        {
            lock (gate)
            {
                if (link != l)
                    return;
                link = null;
                Remote = false;
                Delay = -1;
            }
            Changed?.Invoke();
        }

        public static void SetRemote(bool remote)
        {
            if (Remote == remote)
                return;
            Remote = remote;
            Changed?.Invoke();
        }

        public static void SetDelay(object l, double ms)
        {
            if (Volatile.Read(ref link) != l)
                return;
            Delay = ms;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Log lines go to the log window, a log file and stdout (when redirected), all from a background thread so
    /// logging never blocks the input hooks. Lines that need the user's attention also pop up as a notification.
    /// </summary>
    static class Log
    {
        const int Keep = 1000;
        const long MaxFileBytes = 1 << 20;
        static readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        static readonly Queue<string> recent = new Queue<string>();
        static readonly List<Action<string>> listeners = new List<Action<string>>();

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharedMouse", "SharedMouse.log");

        /// <summary>Raised (on the calling thread) for lines that should pop up next to the clock, with what clicking does (or null).</summary>
        public static event Action<string, Action> Notified;

        static Log()
        {
            new Thread(() =>
            {
                StreamWriter file = OpenFile();
                foreach (var line in lines.GetConsumingEnumerable())
                {
                    try { Console.WriteLine(line); } catch (IOException) { }
                    try { file?.WriteLine(line); } catch (IOException) { file = null; }
                    Action<string>[] copy;
                    lock (recent)
                    {
                        recent.Enqueue(line);
                        if (recent.Count > Keep)
                            recent.Dequeue();
                        copy = listeners.ToArray();
                    }
                    foreach (var listener in copy)
                        listener(line);
                }
            }) { IsBackground = true, Name = "log" }.Start();
        }

        static StreamWriter OpenFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var info = new FileInfo(FilePath);
                var mode = info.Exists && info.Length > MaxFileBytes ? FileMode.Create : FileMode.Append;
                var stream = new FileStream(FilePath, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine();
                return writer;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void Write(string line) => lines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");

        /// <summary>Logs the line and shows it as a notification; clicking it runs <paramref name="onClick"/> (on the UI thread).</summary>
        public static void Notify(string line, Action onClick = null)
        {
            Write(line);
            Notified?.Invoke(line, onClick);
        }

        /// <summary>Starts sending new lines to <paramref name="listener"/> (on the log thread) and returns the lines so far.</summary>
        public static string[] Attach(Action<string> listener)
        {
            lock (recent)
            {
                listeners.Add(listener);
                return recent.ToArray();
            }
        }

        public static void Detach(Action<string> listener)
        {
            lock (recent)
                listeners.Remove(listener);
        }

        public static string Size(long bytes) =>
            bytes < 1024 ? $"{bytes} bytes" :
            bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" :
            bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" :
            $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
