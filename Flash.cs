using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using static SharedMouse.Native;

namespace SharedMouse
{
    /// <summary>
    /// A short, fading highlight where the cursor arrives after a switch, so you always see which screen has it:
    /// a bar along the edge it came through, or a ring around it (Ctrl+Alt+Space, the tray icon, a lost connection).
    /// Callable from any thread; the windows live on the UI thread.
    /// </summary>
    static class Flash
    {
        const int RingSize = 96;
        static Control ui;

        public static void Init(Control uiThread) => ui = uiThread;

        public static void Edge(RECT m, Side side)
        {
            int t = Math.Max(6, Math.Min(m.Width, m.Height) / 120); // thicker on high-resolution screens
            Rectangle bar =
                side == Side.Left ? new Rectangle(m.Left, m.Top, t, m.Height) :
                side == Side.Right ? new Rectangle(m.Right - t, m.Top, t, m.Height) :
                side == Side.Top ? new Rectangle(m.Left, m.Top, m.Width, t) :
                new Rectangle(m.Left, m.Bottom - t, m.Width, t);
            Show(bar, ring: false);
        }

        public static void At(POINT p) => Show(new Rectangle(p.X - RingSize / 2, p.Y - RingSize / 2, RingSize, RingSize), ring: true);

        static void Show(Rectangle bounds, bool ring)
        {
            try
            {
                ui?.BeginInvoke((Action)(() =>
                {
                    try { new FlashWindow(bounds, ring); }
                    catch (Exception ex) { Log.Write("Couldn't show the switch highlight: " + ex.Message); } // only a highlight
                }));
            }
            catch (InvalidOperationException) { } // closing
        }

        /// <summary>
        /// A plain Win32 window, not a WinForms Form: showing a topmost Form gives it the keyboard focus, and on the
        /// second laptop (where SharedMouse makes all the input, so Windows lets it take the foreground) the keys you
        /// typed then went to the highlight instead of your app. This one is shown without activation, never gets
        /// the focus, and the mouse goes through it.
        /// </summary>
        sealed class FlashWindow : NativeWindow
        {
            const byte StartAlpha = 204; // 80%
            const int HoldMs = 150, FadeMs = 450;
            readonly Stopwatch age = Stopwatch.StartNew();
            readonly Timer fade = new Timer { Interval = 15 }; // keeps this window alive while it runs

            public FlashWindow(Rectangle b, bool ring)
            {
                CreateHandle(new CreateParams
                {
                    X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                    Style = unchecked((int)WS_POPUP),
                    ExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED,
                });
                if (ring)
                {
                    int w = Math.Max(6, b.Width / 12);
                    IntPtr outer = CreateEllipticRgn(0, 0, b.Width + 1, b.Height + 1), inner = CreateEllipticRgn(w, w, b.Width - w + 1, b.Height - w + 1);
                    CombineRgn(outer, outer, inner, RGN_DIFF);
                    DeleteObject(inner);
                    SetWindowRgn(Handle, outer, false); // the window owns the region now
                }
                SetLayeredWindowAttributes(Handle, 0, StartAlpha, LWA_ALPHA);
                SetWindowPos(Handle, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                fade.Tick += (s, e) => Step();
                fade.Start();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ERASEBKGND)
                {
                    using (var g = Graphics.FromHdc(m.WParam))
                        g.Clear(Icons.Blue);
                    m.Result = new IntPtr(1);
                    return;
                }
                base.WndProc(ref m);
            }

            void Step()
            {
                double t = (age.ElapsedMilliseconds - HoldMs) / (double)FadeMs;
                if (t >= 1)
                {
                    fade.Dispose();
                    DestroyHandle();
                    return;
                }
                if (t > 0)
                    SetLayeredWindowAttributes(Handle, 0, (byte)(StartAlpha * (1 - t)), LWA_ALPHA);
            }
        }
    }
}
