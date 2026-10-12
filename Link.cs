using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace SharedMouse
{
    enum Msg : byte
    {
        Hello = 1,  // magic(uint), version(byte), machine name(string), capabilities(byte: 1 clipboard, 2 files)
        Ping,       // both directions, keep-alive and latency probe: sent-at(long)
        Enter,      // main -> second: you are now controlled.  edge(byte Side), placeCursor(bool), along(float 0..1), switchDelayMs(ushort)
        Leave,      // both directions: control goes back to the main laptop.  along(float 0..1, NaN = unknown)
        Move,       // main -> second: dx(int), dy(int)
        Button,     // main -> second: button(byte 0 L, 1 R, 2 M, 3 X1, 4 X2), down(bool)
        Wheel,      // main -> second: horizontal(bool), delta(short)
        Key,        // main -> second: vk(ushort), scan(ushort), flags(byte: 1 up, 2 extended)
        Pong,       // answer to Ping: the same sent-at(long)

        // Clipboard/file transfers, both directions. Sent in small chunks behind keyboard/mouse traffic.
        XferBegin = 20, // id(int), kind(byte Kind), total bytes(long), item count(int)
        XferItem,       // id(int), relative path(string), size(long, -1 = folder)   [files only]
        XferData,       // id(int), length(ushort), bytes
        XferEnd,        // id(int)
        XferCancel,     // id(int)                     sender gave up
        XferAck,        // id(int), bytes received(long)   receiver -> sender, for flow control
    }

    enum Kind : byte { Text = 1, Image = 2, Files = 3 }

    /// <summary>One small message waiting to be sent: input events, pings and transfer control.</summary>
    struct Ev
    {
        public Msg Type;
        public int A, B, C;
        public float F;
        public long L;

        public static Ev Ping(long sentAt) => new Ev { Type = Msg.Ping, L = sentAt };
        public static Ev Pong(long sentAt) => new Ev { Type = Msg.Pong, L = sentAt };
        public static Ev Enter(Side edge, bool place, float along, int delayMs) =>
            new Ev { Type = Msg.Enter, A = (int)edge, B = place ? 1 : 0, F = along, C = delayMs };
        public static Ev Leave(float along) => new Ev { Type = Msg.Leave, F = along };
        public static Ev Move(int dx, int dy) => new Ev { Type = Msg.Move, A = dx, B = dy };
        public static Ev Button(int button, bool down) => new Ev { Type = Msg.Button, A = button, B = down ? 1 : 0 };
        public static Ev Wheel(bool horizontal, int delta) => new Ev { Type = Msg.Wheel, A = horizontal ? 1 : 0, B = delta };
        public static Ev Key(uint vk, uint scan, bool up, bool extended) =>
            new Ev { Type = Msg.Key, A = (int)vk, B = (int)scan, C = (up ? 1 : 0) | (extended ? 2 : 0) };
        public static Ev Cancel(int id) => new Ev { Type = Msg.XferCancel, A = id };
        public static Ev Ack(int id, long received) => new Ev { Type = Msg.XferAck, A = id, L = received };

        /// <summary>Keyboard/mouse events whose order matters (a click must happen where the mouse already is).</summary>
        public bool IsOrderedInput => Type == Msg.Enter || Type == Msg.Leave || Type == Msg.Button || Type == Msg.Wheel || Type == Msg.Key;

        public void Write(BinaryWriter w)
        {
            w.Write((byte)Type);
            switch (Type)
            {
                case Msg.Ping: case Msg.Pong: w.Write(L); break;
                case Msg.Enter: w.Write((byte)A); w.Write(B != 0); w.Write(F); w.Write((ushort)Math.Min(C, ushort.MaxValue)); break;
                case Msg.Leave: w.Write(F); break;
                case Msg.Move: w.Write(A); w.Write(B); break;
                case Msg.Button: w.Write((byte)A); w.Write(B != 0); break;
                case Msg.Wheel: w.Write(A != 0); w.Write((short)B); break;
                case Msg.Key: w.Write((ushort)A); w.Write((ushort)B); w.Write((byte)C); break;
                case Msg.XferCancel: w.Write(A); break;
                case Msg.XferAck: w.Write(A); w.Write(L); break;
            }
        }
    }

    /// <summary>
    /// A connection to the other laptop (Bluetooth or TCP): handshake, keep-alive, latency measurement, and one writer
    /// thread that always sends keyboard/mouse first and fills the gaps with a little clipboard/file data.
    /// </summary>
    sealed class Link : IDisposable
    {
        public const byte CapClipboard = 1, CapFiles = 2;
        const uint Magic = 0x45534D53; // "SMSE"
        const byte Version = 3;
        const int PingEveryMs = 500, DeadAfterMs = 6000;

        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long Now => clock.ElapsedTicks;
        static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        readonly Socket socket;
        readonly BinaryWriter writer;
        readonly ConcurrentQueue<Ev> events = new ConcurrentQueue<Ev>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ManualResetEvent closed = new ManualResetEvent(false);
        readonly int chunkSize, window;
        readonly long moveInterval, activeInput;
        Outgoing transfer;
        Timer keepAlive;
        int lastReceived;
        long lastInput = long.MinValue / 2;
        volatile bool isClosed;

        // round-trip times of the last pings, for the window title
        readonly double[] rtts = new double[10];
        int rttCount;
        bool rttReported;

        public BinaryReader Reader { get; }
        public string PeerName { get; private set; }
        public byte PeerCaps { get; private set; }
        public bool IsBluetooth { get; }
        public bool Closed => isClosed;

        public Link(Socket socket)
        {
            this.socket = socket;
            IsBluetooth = socket.AddressFamily != AddressFamily.InterNetwork && socket.AddressFamily != AddressFamily.InterNetworkV6;
            // Bluetooth is slow (~100-200 KB/s, a few hundred packets/s). Automated tests run over TCP but use the
            // Bluetooth settings, so the flow control gets exercised.
            bool slow = IsBluetooth || Environment.GetEnvironmentVariable("SHAREDMOUSE_TEST") == "1";
            chunkSize = slow ? 2048 : 32768;
            window = slow ? 8192 : 1 << 20;
            // A 1000 Hz mouse would send more packets than Bluetooth can carry, and the queue that builds up is lag.
            // So mouse moves go out at most every 8 ms (125 per second, still 2 per screen refresh), summed up.
            moveInterval = slow ? Ticks(8) : 0;
            activeInput = Ticks(300);
            try { socket.SendBufferSize = slow ? 8192 : 1 << 20; } catch (SocketException) { }

            var stream = new NetworkStream(socket, ownsSocket: true);
            writer = new BinaryWriter(new BufferedStream(stream, 65536));
            Reader = new BinaryReader(new BufferedStream(stream, 16384)); // fewer system calls; returns as soon as data arrives
        }

        /// <summary>Exchanges Hello messages, then starts sending. Throws if the other side is not SharedMouse.</summary>
        public void Handshake(byte myCaps)
        {
            writer.Write((byte)Msg.Hello);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Environment.MachineName);
            writer.Write(myCaps);
            writer.Flush();

            socket.ReceiveTimeout = 10000;
            if ((Msg)Reader.ReadByte() != Msg.Hello || Reader.ReadUInt32() != Magic)
                throw new InvalidDataException("The other side is not SharedMouse.");
            byte version = Reader.ReadByte();
            PeerName = Reader.ReadString();
            if (version != Version)
                throw new InvalidDataException($"Version mismatch: {PeerName} runs an older/newer SharedMouse. Use the same SharedMouse.exe on both laptops.");
            PeerCaps = Reader.ReadByte();
            socket.ReceiveTimeout = 0;

            lastReceived = Environment.TickCount;
            Program.SetStatus($"connected to {PeerName}");
            new Thread(WriteLoop) { IsBackground = true, Name = "writer", Priority = ThreadPriority.Highest }.Start();
            keepAlive = new Timer(_ => Tick(), null, PingEveryMs, PingEveryMs);
        }

        /// <summary>Reads the next message type. Pings and pongs are answered/measured here and never returned.</summary>
        public Msg ReadType()
        {
            while (true)
            {
                var type = (Msg)Reader.ReadByte();
                lastReceived = Environment.TickCount;
                if (type == Msg.Ping)
                    Post(Ev.Pong(Reader.ReadInt64()));
                else if (type == Msg.Pong)
                    OnPong(Reader.ReadInt64());
                else
                    return type;
            }
        }

        public void Post(Ev e)
        {
            if (isClosed)
                return;
            if (e.Type == Msg.Move || e.IsOrderedInput)
                Interlocked.Exchange(ref lastInput, Now);
            events.Enqueue(e);
            wake.Set();
        }

        /// <summary>Starts sending a clipboard/file transfer, cancelling the one in progress (the clipboard only holds the latest copy).</summary>
        public void StartTransfer(Outgoing t)
        {
            Outgoing old = Interlocked.Exchange(ref transfer, t);
            if (old != null && !old.Finished)
            {
                old.Finished = true;
                Post(Ev.Cancel(old.Id));
            }
            wake.Set();
        }

        public void OnAck(int id, long received)
        {
            Outgoing t = Volatile.Read(ref transfer);
            if (t != null && t.Id == id)
            {
                Interlocked.Exchange(ref t.Acked, received);
                wake.Set();
            }
        }

        public void WaitClosed() => closed.WaitOne();

        void WriteLoop()
        {
            var batch = new List<Ev>();
            Outgoing current = null;
            int moveX = 0, moveY = 0;
            bool movePending = false, dataHeldBack = false;
            long lastMoveSent = long.MinValue / 2;
            try
            {
                while (!isClosed)
                {
                    // Sleep until something is posted, a held-back mouse move is due, or file data may flow at full speed again.
                    long due = long.MaxValue;
                    if (movePending)
                        due = lastMoveSent + moveInterval;
                    if (dataHeldBack)
                        due = Math.Min(due, Interlocked.Read(ref lastInput) + activeInput);
                    int waitMs = due == long.MaxValue ? 500 : (int)Math.Ceiling(Math.Max(0, Ms(due - Now)));
                    if (waitMs > 0)
                        wake.WaitOne(waitMs);

                    while (!isClosed)
                    {
                        batch.Clear();
                        while (batch.Count < 256 && events.TryDequeue(out Ev e))
                        {
                            if (e.Type == Msg.Move)
                            {
                                moveX += e.A;
                                moveY += e.B;
                                movePending = true;
                                continue;
                            }
                            if (movePending && e.IsOrderedInput)
                            {
                                batch.Add(Ev.Move(moveX, moveY));
                                moveX = moveY = 0;
                                movePending = false;
                                lastMoveSent = Now;
                            }
                            batch.Add(e);
                        }
                        if (movePending && Now - lastMoveSent >= moveInterval)
                        {
                            batch.Add(Ev.Move(moveX, moveY));
                            moveX = moveY = 0;
                            movePending = false;
                            lastMoveSent = Now;
                        }

                        Outgoing t = Volatile.Read(ref transfer);
                        if (t != current)
                        {
                            current?.Dispose(); // replaced or finished: release its open file, on this thread
                            current = t;
                        }
                        // While you're using the keyboard/mouse, keep only one small chunk of file data in flight,
                        // so input never queues behind it. When idle, transfers get the full speed again.
                        bool typing = Now - Interlocked.Read(ref lastInput) < activeInput;
                        long inFlight = t == null ? 0 : t.Sent - Interlocked.Read(ref t.Acked);
                        bool sendData = t != null && !t.Finished && inFlight < (typing ? chunkSize : window);
                        dataHeldBack = t != null && !t.Finished && !sendData && inFlight < window;
                        if (batch.Count == 0 && !sendData)
                            break;

                        foreach (var e in batch)
                            e.Write(writer);
                        if (sendData)
                            t.WriteNext(writer, chunkSize);
                        writer.Flush();

                        if (t != null && t.Finished)
                            Interlocked.CompareExchange(ref transfer, null, t);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException) { }
            finally
            {
                current?.Dispose();
                Dispose();
            }
        }

        void OnPong(long sentAt)
        {
            double rtt = Ms(Now - sentAt);
            if (rtt < 0 || rtt > 60000)
                return;
            lock (rtts)
                rtts[rttCount++ % rtts.Length] = rtt;
        }

        void Tick()
        {
            if (isClosed)
                return;
            if (unchecked(Environment.TickCount - lastReceived) > DeadAfterMs)
            {
                Log.Write("No answer from the other laptop for a few seconds.");
                Dispose();
                return;
            }
            Post(Ev.Ping(Now));
            ShowLatency();
        }

        /// <summary>Round-trip time (there and back) of recent pings, in the window title; logged once after connecting.</summary>
        void ShowLatency()
        {
            double avg = 0, worst = 0;
            int n;
            lock (rtts)
            {
                n = Math.Min(rttCount, rtts.Length);
                for (int i = 0; i < n; i++)
                {
                    avg += rtts[i] / n;
                    worst = Math.Max(worst, rtts[i]);
                }
            }
            if (n == 0)
                return;
            Program.SetStatus($"{PeerName} - delay {avg:0} ms (worst {worst:0} ms)");
            if (!rttReported && n == rtts.Length)
            {
                rttReported = true;
                Log.Write($"Connection delay: {avg:0} ms there and back (worst {worst:0} ms). Live value in the window title.");
            }
        }

        public void Dispose()
        {
            if (isClosed)
                return;
            isClosed = true;
            keepAlive?.Dispose();
            try { socket.Close(); } catch { }
            wake.Set();
            closed.Set();
            Program.SetStatus("not connected");
        }
    }

    /// <summary>Console output from a background thread, so a paused console (text selected) never blocks the input hooks.</summary>
    static class Log
    {
        static readonly BlockingCollection<string> lines = new BlockingCollection<string>();

        static Log()
        {
            new Thread(() =>
            {
                foreach (var line in lines.GetConsumingEnumerable())
                    Console.WriteLine(line);
            }) { IsBackground = true }.Start();
        }

        public static void Write(string line) => lines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");

        public static string Size(long bytes) =>
            bytes < 1024 ? $"{bytes} bytes" :
            bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" :
            bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" :
            $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
