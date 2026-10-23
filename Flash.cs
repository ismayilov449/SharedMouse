using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            try { ui?.BeginInvoke((Action)(() => new FlashWindow(bounds, ring).Show())); }
            catch (InvalidOperationException) { } // closing
        }

        sealed class FlashWindow : Form
        {
            const double StartOpacity = 0.8;
            const int HoldMs = 150, FadeMs = 450;
            readonly Rectangle bounds;
            readonly Stopwatch age = Stopwatch.StartNew();
            readonly Timer fade = new Timer { Interval = 15 };

            public FlashWindow(Rectangle bounds, bool ring)
            {
                this.bounds = bounds;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Icons.Blue;
                Opacity = StartOpacity;
                TopMost = true;
                Bounds = bounds;
                if (ring)
                {
                    int w = Math.Max(6, bounds.Width / 12);
                    using (var path = new GraphicsPath(FillMode.Alternate))
                    {
                        path.AddEllipse(0, 0, bounds.Width, bounds.Height);
                        path.AddEllipse(w, w, bounds.Width - 2 * w, bounds.Height - 2 * w);
                        Region = new Region(path);
                    }
                }
                fade.Tick += (s, e) => Step();
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    // tool window, never activated, topmost, and clicks go through it
                    cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x8 /* WS_EX_TOPMOST */ |
                                  0x20 /* WS_EX_TRANSPARENT */ | 0x80000 /* WS_EX_LAYERED */;
                    return cp;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Bounds = bounds; // in case Windows adjusted it for another screen's scaling
                fade.Start();
            }

            void Step()
            {
                double t = (age.ElapsedMilliseconds - HoldMs) / (double)FadeMs;
                if (t >= 1)
                {
                    fade.Stop();
                    Close();
                    return;
                }
                if (t > 0)
                    Opacity = StartOpacity * (1 - t);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                fade.Dispose();
                base.OnFormClosed(e); // a closed modeless form disposes itself
            }
        }
    }
}
