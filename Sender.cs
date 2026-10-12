using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static SharedMouse.Native;

namespace SharedMouse
{
    /// <summary>
    /// Runs on the MAIN laptop: watches its keyboard and mouse, and while the cursor is "on" the second laptop,
    /// blocks them locally and forwards them over the link.
    /// All mode state lives on the input thread (the hooks run there, never delayed by the tray UI); other threads only post to it.
    /// </summary>
    sealed class Sender
    {
        const uint VK_SPACE = 0x20, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        const int SwitchBackCooldownMs = 300;

        // For automated tests only: treat injected input carrying this marker as if it came from the real keyboard/mouse.
        static readonly bool testMode = Environment.GetEnvironmentVariable("SHAREDMOUSE_TEST") == "1";
        static readonly IntPtr TestMarker = new IntPtr(0x534D5445);

        readonly Side side; // where the second laptop is
        readonly int delayMs;
        readonly ulong? peerAddress;
        readonly string tcpTarget;
        readonly ClipboardSync clipboard;
        volatile Link link;

        // input-thread state
        CursorHider hider;
        LowLevelProc keyboardProc, mouseProc; // kept in fields so the GC can't collect the hook callbacks
        bool remote;
        POINT park, returnPos;
        RECT exitMonitor;
        int cooldownUntil;
        readonly EdgePush push = new EdgePush();
        readonly HashSet<uint> localKeys = new HashSet<uint>(), remoteKeys = new HashSet<uint>(), swallowedKeys = new HashSet<uint>();
        int localButtons, remoteButtons;
        bool ctrl, alt;

        public Sender(Side side, int delayMs, ulong? peerAddress, string tcpTarget, ClipboardSync clipboard)
        {
            this.side = side;
            this.delayMs = delayMs;
            this.peerAddress = peerAddress;
            this.tcpTarget = tcpTarget;
            this.clipboard = clipboard;
        }

        /// <summary>Starts watching the keyboard and mouse on a thread of their own, and connecting. Throws if the hooks fail.</summary>
        public void Start()
        {
            Exception error = null;
            var ready = new ManualResetEventSlim();
            var input = new Thread(() =>
            {
                try
                {
                    InstallHooks();
                }
                catch (Exception ex)
                {
                    error = ex;
                    return;
                }
                finally
                {
                    ready.Set();
                }
                Application.Run(); // the hooks are called from this thread's message loop
            }) { IsBackground = true, Name = "input", Priority = ThreadPriority.Highest };
            input.SetApartmentState(ApartmentState.STA);
            input.Start();
            ready.Wait();
            if (error != null)
                throw error;

            clipboard.Start();
            new Thread(NetworkLoop) { IsBackground = true, Name = "network" }.Start();
            Log.Write($"Main laptop ready. The second laptop is at the {side.ToString().ToUpperInvariant()}.");
            Log.Write("Ctrl+Alt+Space switches between the laptops at any time.");
        }

        void InstallHooks()
        {
            hider = new CursorHider();
            _ = hider.Handle; // create the window now so BeginInvoke works from other threads
            cooldownUntil = Environment.TickCount;

            keyboardProc = KeyboardHook;
            mouseProc = MouseHook;
            IntPtr module = GetModuleHandle(null);
            if (SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0) == IntPtr.Zero ||
                SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0) == IntPtr.Zero)
                throw new InvalidOperationException("Could not watch the keyboard/mouse (error " + Marshal.GetLastWin32Error() + ").");
        }

        /// <summary>Switches between the laptops, like Ctrl+Alt+Space. Callable from any thread.</summary>
        public void SwitchNow() => Post(Toggle);

        // ---------------- network ----------------

        void NetworkLoop()
        {
            while (true)
            {
                Link l = ConnectWithRetry();
                link = l;
                Log.Write($"Connected to {l.PeerName}. Push the mouse against the {side.ToString().ToLowerInvariant()} edge of the screen to control it.");
                clipboard.Attach(l);
                var reader = new Thread(() => ReadLoop(l)) { IsBackground = true, Name = "reader", Priority = ThreadPriority.Highest };
                reader.Start();
                l.WaitClosed();
                reader.Join();

                link = null;
                Post(() => GoLocal(null, notifyPeer: false));
                Log.Write("Disconnected. Reconnecting...");
                Thread.Sleep(1000);
            }
        }

        Link ConnectWithRetry()
        {
            bool reported = false;
            while (true)
            {
                foreach (var (name, open) in Candidates(reported))
                {
                    Link l = null;
                    try
                    {
                        l = new Link(open());
                        l.Handshake(clipboard.Caps);
                        return l;
                    }
                    catch (Exception ex) when (ex is SocketException || ex is System.IO.IOException || ex is System.IO.InvalidDataException)
                    {
                        l?.Dispose();
                        if (ex is System.IO.InvalidDataException)
                            Log.Notify($"{name}: {ex.Message}");
                        else if (!reported)
                            Log.Write($"  {name}: {ex.Message}");
                    }
                }
                reported = true;
                Thread.Sleep(3000);
            }
        }

        List<(string name, Func<Socket> open)> Candidates(bool reported)
        {
            var list = new List<(string, Func<Socket>)>();
            if (tcpTarget != null)
            {
                var (host, port) = Program.ParseHostPort(tcpTarget, "127.0.0.1");
                list.Add((tcpTarget, () => new TcpClient(host, port) { NoDelay = true }.Client));
            }
            else if (peerAddress != null)
            {
                ulong a = peerAddress.Value;
                list.Add((Bluetooth.FormatAddress(a), () => Bluetooth.Connect(a)));
            }
            else
            {
                var paired = Bluetooth.PairedComputers();
                if (!reported && paired.Count == 0)
                    Log.Notify("No paired computers found. Is Bluetooth on? The two laptops must be paired in Windows Bluetooth settings.");
                else if (!reported)
                    Log.Write("Looking for SharedMouse on: " + string.Join(", ", paired.ConvertAll(d => d.Name)));
                foreach (var d in paired)
                {
                    ulong a = d.Address;
                    list.Add((d.Name, () => Bluetooth.Connect(a)));
                }
            }
            return list;
        }

        void ReadLoop(Link l)
        {
            try
            {
                while (true)
                {
                    Msg type = l.ReadType();
                    if (type == Msg.Leave)
                    {
                        float along = l.Reader.ReadSingle();
                        Post(() => OnPeerLeave(along));
                    }
                    else if (!clipboard.Handle(type, l))
                        throw new System.IO.InvalidDataException("Unexpected message.");
                }
            }
            catch { }
            finally
            {
                clipboard.Detach(l);
                l.Dispose();
            }
        }

        void Post(Action a)
        {
            try { hider.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void Send(Ev e) => link?.Post(e);

        // ---------------- switching (input thread) ----------------

        void GoRemote(POINT from, bool viaEdge)
        {
            if (remote || link == null)
                return;
            remote = true;
            exitMonitor = MonitorRect(from);
            float along = Edges.Along(exitMonitor, from, side);
            returnPos = viaEdge ? Edges.Inside(exitMonitor, side, along, 2) : from;
            Send(Ev.Enter(Edges.Opposite(side), viaEdge, along, delayMs));

            // Park the cursor in the middle of the main screen and hide it; moves are measured from there.
            RECT primary = MonitorRect(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
            park = new POINT(primary.Left + primary.Width / 2, primary.Top + primary.Height / 2);
            hider.ShowAt(park);
            SetCursorPos(park.X, park.Y);
            Status.SetRemote(true);
            Log.Write($">> Controlling {link?.PeerName}");
        }

        void GoLocal(POINT? at, bool notifyPeer)
        {
            if (!remote)
                return;
            remote = false;
            if (notifyPeer)
                Send(Ev.Leave(float.NaN));
            hider.Hide();
            POINT p = at ?? returnPos;
            SetCursorPos(p.X, p.Y);
            cooldownUntil = Environment.TickCount + SwitchBackCooldownMs;
            Status.SetRemote(false);
            Log.Write("<< Back on this laptop");
        }

        void OnPeerLeave(float along)
        {
            if (remote)
                GoLocal(float.IsNaN(along) ? (POINT?)null : Edges.Inside(exitMonitor, side, along, 2), notifyPeer: false);
        }

        void Toggle()
        {
            if (remote)
                GoLocal(null, notifyPeer: true);
            else if (link == null)
                Log.Write("Not connected to the second laptop yet.");
            else
            {
                GetCursorPos(out POINT p);
                GoRemote(p, viaEdge: false);
            }
        }

        // ---------------- hooks (input thread) ----------------

        static readonly IntPtr Block = new IntPtr(1);

        IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (IsInjected(k.flags & LLKHF_INJECTED, k.dwExtraInfo))
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            // AltGr sends a fake Left-Ctrl (scan code 0x21D). The second laptop makes its own if its layout needs one.
            if ((k.scanCode & 0x200) != 0)
                return remote ? Block : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            uint vk = k.vkCode;
            bool up = (k.flags & LLKHF_UP) != 0;
            bool extended = (k.flags & LLKHF_EXTENDED) != 0;
            if (vk == VK_LCONTROL || vk == VK_RCONTROL) ctrl = !up;
            if (vk == VK_LMENU || vk == VK_RMENU) alt = !up;

            if (up)
            {
                // Each key is released on the laptop that saw it go down.
                if (swallowedKeys.Remove(vk))
                    return Block;
                if (localKeys.Remove(vk))
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                if (remoteKeys.Remove(vk) || remote)
                {
                    if (remote)
                        Send(Ev.Key(vk, k.scanCode, up: true, extended));
                    return Block;
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (vk == VK_SPACE && ctrl && alt)
            {
                swallowedKeys.Add(vk);
                Toggle();
                return Block;
            }
            if (remote)
            {
                remoteKeys.Add(vk);
                Send(Ev.Key(vk, k.scanCode, up: false, extended));
                return Block;
            }
            localKeys.Add(vk);
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsInjected(m.flags & LLMHF_INJECTED, m.dwExtraInfo))
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            int msg = (int)wParam;
            int button = ButtonOf(msg, m.mouseData, out bool down);

            if (!remote)
            {
                if (button >= 0)
                {
                    if (down) localButtons |= 1 << button;
                    else if ((remoteButtons & (1 << button)) != 0) { remoteButtons &= ~(1 << button); return Block; }
                    else localButtons &= ~(1 << button);
                }
                else if (msg == WM_MOUSEMOVE)
                {
                    // The hook sees where the mouse is heading, even past the screen edge.
                    GetCursorPos(out POINT cur);
                    if (link == null || localButtons != 0 || unchecked(Environment.TickCount - cooldownUntil) < 0)
                        push.Reset();
                    else if (push.Update(cur, m.pt, side, delayMs))
                    {
                        GoRemote(cur, viaEdge: true);
                        return Block;
                    }
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (button >= 0)
            {
                // A button held down before switching is released here, where it was pressed.
                if (!down && (localButtons & (1 << button)) != 0)
                {
                    localButtons &= ~(1 << button);
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }
                if (down) remoteButtons |= 1 << button;
                else remoteButtons &= ~(1 << button);
                Send(Ev.Button(button, down));
            }
            else if (msg == WM_MOUSEMOVE)
            {
                int dx = m.pt.X - park.X, dy = m.pt.Y - park.Y;
                if (dx != 0 || dy != 0)
                    Send(Ev.Move(dx, dy));
            }
            else if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                Send(Ev.Wheel(msg == WM_MOUSEHWHEEL, (short)(m.mouseData >> 16)));
            }
            return Block;
        }

        /// <summary>Input made by software (including SharedMouse itself) is never forwarded.</summary>
        static bool IsInjected(uint injectedFlag, IntPtr extraInfo) => injectedFlag != 0 && !(testMode && extraInfo == TestMarker);

        static int ButtonOf(int msg, uint mouseData, out bool down)
        {
            down = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN;
            switch (msg)
            {
                case WM_LBUTTONDOWN: case WM_LBUTTONUP: return 0;
                case WM_RBUTTONDOWN: case WM_RBUTTONUP: return 1;
                case WM_MBUTTONDOWN: case WM_MBUTTONUP: return 2;
                case WM_XBUTTONDOWN: case WM_XBUTTONUP: return (mouseData >> 16) == 1 ? 3 : 4;
                default: return -1;
            }
        }
    }

    /// <summary>A tiny, practically invisible topmost window with no cursor, placed under the parked cursor to hide it.</summary>
    sealed class CursorHider : Form
    {
        public CursorHider()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(16, 16);
            BackColor = Color.Black;
            Opacity = 0.01; // still receives the mouse (fully transparent windows don't), but can't be seen
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x8 /* WS_EX_TOPMOST */;
                return cp;
            }
        }

        public void ShowAt(POINT p)
        {
            Location = new Point(p.X - Width / 2, p.Y - Height / 2);
            if (!Visible)
                Show();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETCURSOR)
            {
                SetCursor(IntPtr.Zero);
                m.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref m);
        }
    }
}
