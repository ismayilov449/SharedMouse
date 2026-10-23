using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using static SharedMouse.Native;

namespace SharedMouse
{
    /// <summary>
    /// Runs on the SECOND laptop: waits for the main laptop and replays its keyboard and mouse here.
    /// </summary>
    sealed class Receiver
    {
        static readonly int InputSize = Marshal.SizeOf<INPUT>();
        static readonly uint[] ButtonDown = { MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_XDOWN, MOUSEEVENTF_XDOWN };
        static readonly uint[] ButtonUp = { MOUSEEVENTF_LEFTUP, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_XUP, MOUSEEVENTF_XUP };

        readonly string tcpListen;
        readonly ClipboardSync clipboard;
        readonly ManualResetEvent enabled = new ManualResetEvent(true); // reset by "Disconnect"

        volatile Socket listener;
        volatile Link link;
        bool active;
        Side doorway; // the edge of this laptop's screen that leads back to the main laptop
        int delayMs;
        readonly EdgePush push = new EdgePush();
        POINT pos;
        int lastMoveTick;
        int buttons;
        readonly Dictionary<ushort, (ushort scan, bool extended)> keysDown = new Dictionary<ushort, (ushort, bool)>();
        int lastErrorTick = Environment.TickCount - 10000;

        public Receiver(string tcpListen, ClipboardSync clipboard)
        {
            this.tcpListen = tcpListen;
            this.clipboard = clipboard;
        }

        /// <summary>Starts waiting for the main laptop, on a thread of its own (input is replayed there).</summary>
        public void Start()
        {
            clipboard.Start();
            new Thread(Run) { IsBackground = true, Name = "input", Priority = ThreadPriority.Highest }.Start();
        }

        /// <summary>"Disconnect" (false) closes the connection and stops listening; "Connect" (true) starts again. Any thread.</summary>
        public void SetConnectionEnabled(bool on)
        {
            if (on)
            {
                enabled.Set();
            }
            else
            {
                enabled.Reset();
                try { listener?.Close(); } catch (ObjectDisposedException) { }
                link?.Dispose();
                Log.Notify("Disconnected. Press Ctrl+` or click the icon to connect again.");
            }
            Status.SetPaused(!on);
        }

        public void ToggleConnection() => SetConnectionEnabled(!enabled.WaitOne(0));

        void Run()
        {
            while (true)
            {
                enabled.WaitOne(); // until "Connect"
                Socket l = OpenListener();
                listener = l;
                try
                {
                    if (enabled.WaitOne(0)) // "Disconnect" may have been clicked meanwhile
                        AcceptLoop(l);
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                {
                    // e.g. Bluetooth was switched off, or the laptop went to sleep; or "Disconnect" closed the listener
                    if (enabled.WaitOne(0))
                        Log.Write($"Stopped listening ({ex.Message}). Restarting...");
                }
                finally
                {
                    listener = null;
                    l.Close();
                }
                if (enabled.WaitOne(0))
                    Thread.Sleep(3000);
            }
        }

        Socket OpenListener()
        {
            if (tcpListen != null)
            {
                var (host, port) = Program.ParseHostPort(tcpListen, "0.0.0.0");
                var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Parse(host), port));
                tcp.Listen(1);
                Log.Write($"Waiting for the main laptop on TCP {tcp.LocalEndPoint}...");
                return tcp;
            }

            bool reported = false;
            while (true)
            {
                enabled.WaitOne(); // no "Bluetooth is off" retries while disconnected
                try
                {
                    Socket bt = Bluetooth.Listen();
                    Log.Write("Waiting for the main laptop over Bluetooth...");
                    return bt;
                }
                catch (SocketException ex)
                {
                    if (!reported)
                        Log.Notify($"Bluetooth is not available ({ex.Message}). Turn Bluetooth on; retrying...");
                    reported = true;
                    Thread.Sleep(5000);
                }
            }
        }

        void AcceptLoop(Socket listener)
        {
            while (true)
            {
                Socket s = listener.Accept();
                if (s.AddressFamily == AddressFamily.InterNetwork)
                    s.NoDelay = true;
                using (link = new Link(s))
                {
                    try
                    {
                        if (!enabled.WaitOne(0)) // "Disconnect" was clicked just as the main laptop connected
                            return;
                        link.Handshake(clipboard.Caps);
                        Log.Write($"Connected to {link.PeerName}. Its keyboard and mouse can now control this laptop.");
                        clipboard.Attach(link);
                        Serve();
                    }
                    catch (InvalidDataException ex)
                    {
                        Log.Notify(ex.Message);
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException) { }
                    finally
                    {
                        clipboard.Detach(link);
                    }
                }
                Deactivate();
                if (enabled.WaitOne(0))
                    Log.Write("Disconnected. Waiting for the main laptop...");
            }
        }

        void Serve()
        {
            var r = link.Reader;
            while (true)
            {
                Msg type = link.ReadType();
                switch (type)
                {
                    case Msg.Enter:
                        var edge = (Side)r.ReadByte();
                        bool place = r.ReadBoolean();
                        float along = r.ReadSingle();
                        int delay = r.ReadUInt16();
                        Enter(edge, place, along, delay);
                        break;
                    case Msg.Leave:
                        r.ReadSingle();
                        Deactivate();
                        break;
                    case Msg.Move:
                        int dx = r.ReadInt32(), dy = r.ReadInt32();
                        if (active) Move(dx, dy);
                        break;
                    case Msg.Button:
                        int button = r.ReadByte();
                        bool down = r.ReadBoolean();
                        if (active && button < ButtonDown.Length) Button(button, down);
                        break;
                    case Msg.Wheel:
                        bool horizontal = r.ReadBoolean();
                        short delta = r.ReadInt16();
                        if (active) Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, delta);
                        break;
                    case Msg.Key:
                        ushort vk = r.ReadUInt16(), scan = r.ReadUInt16();
                        byte flags = r.ReadByte();
                        if (active) Key(vk, scan, up: (flags & 1) != 0, extended: (flags & 2) != 0);
                        break;
                    default:
                        if (!clipboard.Handle(type, link))
                            throw new InvalidDataException("Unexpected message from the main laptop.");
                        break;
                }
            }
        }

        void Enter(Side edge, bool place, float along, int delay)
        {
            if (edge > Side.Bottom)
                throw new InvalidDataException("Unknown screen edge.");
            active = true;
            Status.SetRemote(true);
            doorway = edge;
            delayMs = delay;
            lastMoveTick = Environment.TickCount - 1000; // re-read the real cursor position on the first move
            if (place)
            {
                // Appear at the shared edge, at the same spot along it where the cursor left the main screen.
                RECT m = Edges.OuterMonitor(edge);
                pos = Edges.Inside(m, edge, along, 1);
                MoveTo(pos);
                lastMoveTick = Environment.TickCount;
                Flash.Edge(m, edge);
            }
            else
            {
                GetCursorPos(out POINT p);
                Flash.At(p);
            }
        }

        void Move(int dx, int dy)
        {
            // Track the position ourselves while moving (GetCursorPos lags behind SendInput),
            // but pick up the real one after a pause, e.g. if this laptop's own touchpad was used.
            int now = Environment.TickCount;
            if (unchecked(now - lastMoveTick) > 100)
                GetCursorPos(out pos);
            lastMoveTick = now;

            var target = new POINT(pos.X + dx, pos.Y + dy);
            if (buttons != 0)
                push.Reset();
            else if (push.Update(pos, target, doorway, delayMs))
            {
                float along = Edges.Along(MonitorRect(pos), pos, doorway);
                Deactivate();
                link.Post(Ev.Leave(along));
                return;
            }

            if (!IsOnAnyMonitor(target))
            {
                RECT t = MonitorRect(target);
                target.X = Math.Max(t.Left, Math.Min(t.Right - 1, target.X));
                target.Y = Math.Max(t.Top, Math.Min(t.Bottom - 1, target.Y));
            }
            pos = target;
            MoveTo(pos);
        }

        void MoveTo(POINT p)
        {
            // Absolute coordinates, so this laptop's mouse acceleration isn't applied a second time.
            RECT vs = VirtualScreen();
            int nx = (int)(((2L * (p.X - vs.Left) + 1) * 32768) / vs.Width);
            int ny = (int)(((2L * (p.Y - vs.Top) + 1) * 32768) / vs.Height);
            Inject(MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0,
                Math.Max(0, Math.Min(65535, nx)), Math.Max(0, Math.Min(65535, ny))));
        }

        void Button(int button, bool down)
        {
            if (down) buttons |= 1 << button;
            else buttons &= ~(1 << button);
            int xButton = button == 3 ? 1 : button == 4 ? 2 : 0;
            Mouse(down ? ButtonDown[button] : ButtonUp[button], xButton);
        }

        void Mouse(uint flags, int data) => Inject(MouseInput(flags, data, 0, 0));

        void Key(ushort vk, ushort scan, bool up, bool extended)
        {
            if (up) keysDown.Remove(vk);
            else keysDown[vk] = (scan, extended);

            // Scan codes = physical key position, so this laptop's own keyboard layout decides the character.
            // A few keys have no usable scan code and go by virtual-key code instead.
            bool byVk = scan == 0 || vk == 0x13 /* Pause */ || vk == 0x90 /* NumLock */ || vk == 0x03 /* Break */ ||
                        (vk >= 0xA6 && vk <= 0xB7) /* browser, volume, media, launch keys */;
            var ki = new KEYBDINPUT
            {
                wVk = byVk ? vk : (ushort)0,
                wScan = scan,
                dwFlags = (byVk ? 0 : KEYEVENTF_SCANCODE) | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
            };
            Inject(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = ki } });
        }

        /// <summary>Releases everything still held, so no key or button gets stuck when control goes back.</summary>
        void Deactivate()
        {
            active = false;
            Status.SetRemote(false);
            push.Reset();
            foreach (var kv in new List<KeyValuePair<ushort, (ushort scan, bool extended)>>(keysDown))
                Key(kv.Key, kv.Value.scan, up: true, extended: kv.Value.extended);
            for (int b = 0; b < ButtonUp.Length; b++)
                if ((buttons & (1 << b)) != 0)
                    Button(b, down: false);
        }

        static INPUT MouseInput(uint flags, int data, int x, int y) =>
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags } } };

        void Inject(INPUT input)
        {
            if (SendInput(1, new[] { input }, InputSize) == 1)
                return;
            int error = Marshal.GetLastWin32Error();
            if (active)
            {
                // Nothing would arrive here, so don't leave the cursor lost on this screen: give control back.
                Deactivate();
                link?.Post(Ev.Leave(float.NaN));
                Log.Notify($"Windows refused the input here (error {error}): is this laptop locked, or an admin window in front? " +
                           "The keyboard and mouse went back to the main laptop.");
                return;
            }
            int now = Environment.TickCount;
            if (unchecked(now - lastErrorTick) > 5000)
            {
                lastErrorTick = now;
                Log.Write($"Windows refused the input (error {error}). Is this laptop locked, or an admin window in front?");
            }
        }
    }
}
