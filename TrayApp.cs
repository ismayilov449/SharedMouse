using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace SharedMouse
{
    /// <summary>
    /// The icon next to the clock: its colour shows the connection, hovering shows the delay, right-click opens the menu,
    /// and important events pop up as notifications. Runs on the main (UI) thread; the keyboard/mouse work never does.
    /// </summary>
    sealed class TrayApp : ApplicationContext
    {
        const int LostNoticeAfterMs = 5000; // a Bluetooth hiccup that reconnects by itself isn't worth a pop-up

        readonly Settings settings;
        readonly bool saved; // false when started with command-line options
        readonly Control ui = new Control(); // to get back onto the UI thread
        readonly Icon off = Icons.Make(Icons.Grey), on = Icons.Make(Icons.Green), away = Icons.Make(Icons.Blue);
        readonly System.Windows.Forms.Timer lostTimer = new System.Windows.Forms.Timer { Interval = LostNoticeAfterMs };
        NotifyIcon tray;
        ToolStripMenuItem statusItem, switchItem, autostartItem;
        Sender sender;
        SetupForm settingsWindow;
        LogForm logWindow;
        bool wasConnected, everConnected, lostShown;
        string lostPeer, lastNotice;
        DateTime lastNoticeAt;

        public bool RestartRequested { get; private set; }

        /// <param name="greeting">Shown as a notification once the icon is up, or null.</param>
        /// <param name="showSignal">Set by a second copy of SharedMouse that was started: show the settings.</param>
        public TrayApp(Settings settings, bool saved, string greeting, WaitHandle showSignal)
        {
            this.settings = settings;
            this.saved = saved;
            ui.CreateControl();
            Status.Changed += OnStatusChanged;
            Log.Notified += OnNotified;

            StartSharing(); // throws if the keyboard/mouse can't be watched

            tray = new NotifyIcon { Icon = off, Text = "SharedMouse", ContextMenuStrip = Menu() };
            tray.DoubleClick += (s, e) => { if (saved) ShowSettings(); else ShowLog(); };
            lostTimer.Tick += (s, e) => OnLostTimer();
            tray.Visible = true;
            Refresh();
            if (greeting != null)
                Notice(greeting);
            if (showSignal != null)
                ThreadPool.RegisterWaitForSingleObject(showSignal, (o, timedOut) => OnUi(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        void StartSharing()
        {
            var clipboard = new ClipboardSync(settings.ShareClipboard, settings.ShareFiles);
            if (settings.IsMain)
            {
                sender = new Sender(settings.Side, settings.DelayMs, settings.PeerAddress, settings.Tcp, clipboard);
                sender.Start();
            }
            else
            {
                new Receiver(settings.Tcp, clipboard).Start();
            }
        }

        ContextMenuStrip Menu()
        {
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem { Font = new Font(menu.Font, FontStyle.Bold) };
            statusItem.Click += (s, e) => ShowLog();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            if (settings.IsMain)
            {
                switchItem = new ToolStripMenuItem("Switch to the other laptop") { ShortcutKeyDisplayString = "Ctrl+Alt+Space" };
                switchItem.Click += (s, e) => sender.SwitchNow();
                menu.Items.Add(switchItem);
            }
            if (saved)
                menu.Items.Add("Settings...", null, (s, e) => ShowSettings());
            menu.Items.Add("Show log", null, (s, e) => ShowLog());
            if (saved)
            {
                autostartItem = new ToolStripMenuItem("Start with Windows");
                autostartItem.Click += (s, e) => Autostart.Set(!autostartItem.Checked);
                menu.Items.Add(autostartItem);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            menu.Opening += (s, e) =>
            {
                if (autostartItem != null)
                    autostartItem.Checked = Autostart.Enabled;
            };
            return menu;
        }

        // ---------------- state (UI thread) ----------------

        void OnStatusChanged() => OnUi(Refresh);

        void OnNotified(string text) => OnUi(() => Notice(text));

        void OnUi(Action action)
        {
            try { ui.BeginInvoke(action); }
            catch (InvalidOperationException) { } // closing
        }

        void Refresh()
        {
            if (tray == null)
                return;
            bool connected = Status.Connected;
            if (connected && !wasConnected)
            {
                lostTimer.Stop();
                if (!everConnected || lostShown)
                    Notice(settings.IsMain
                        ? $"Connected to {Status.Peer}. Push the mouse against the {settings.Side.ToString().ToLowerInvariant()} edge of the screen to move to it."
                        : $"Connected to {Status.Peer}. Its keyboard and mouse can now control this laptop.");
                everConnected = true;
                lostShown = false;
            }
            else if (!connected && wasConnected)
            {
                lostTimer.Start();
            }
            if (connected)
                lostPeer = Status.Peer;
            wasConnected = connected;

            tray.Icon = !connected ? off : Status.Remote ? away : on;
            string text = StatusText();
            statusItem.Text = text;
            tray.Text = Truncate("SharedMouse: " + text, 63); // Windows' limit for the hover text
            if (switchItem != null)
                switchItem.Enabled = connected;
        }

        string StatusText()
        {
            if (!Status.Connected)
                return settings.IsMain ? "Looking for the second laptop..." : "Waiting for the main laptop...";
            string peer = Status.Peer;
            string delay = Status.Delay >= 0 ? $" · {Status.Delay:0} ms" : "";
            if (Status.Remote)
                return (settings.IsMain ? $"Controlling {peer}" : $"Controlled by {peer}") + delay;
            return $"Connected to {peer}" + delay;
        }

        void OnLostTimer()
        {
            lostTimer.Stop();
            if (Status.Connected)
                return;
            Notice($"Lost the connection to {lostPeer}. Reconnecting automatically...");
            lostShown = true;
        }

        void Notice(string text)
        {
            if (tray == null || (text == lastNotice && DateTime.UtcNow - lastNoticeAt < TimeSpan.FromMinutes(1)))
                return;
            lastNotice = text;
            lastNoticeAt = DateTime.UtcNow;
            tray.ShowBalloonTip(5000, "SharedMouse", Truncate(text, 250), ToolTipIcon.None);
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

        // ---------------- windows ----------------

        void ShowLog()
        {
            if (logWindow == null || logWindow.IsDisposed)
            {
                logWindow = new LogForm();
                logWindow.Show();
                return;
            }
            if (logWindow.WindowState == FormWindowState.Minimized)
                logWindow.WindowState = FormWindowState.Normal;
            logWindow.Activate();
        }

        void ShowSettings()
        {
            if (!saved)
                return;
            if (settingsWindow != null)
            {
                settingsWindow.Activate();
                return;
            }
            using (var form = new SetupForm(settings, firstRun: false, startWithWindows: Autostart.Enabled))
            {
                settingsWindow = form;
                DialogResult result = form.ShowDialog();
                settingsWindow = null;
                if (result != DialogResult.OK)
                    return;
                if (form.StartWithWindows != Autostart.Enabled)
                    Autostart.Set(form.StartWithWindows);
                if (form.Result.SameAs(settings))
                    return;
                form.Result.Save();
                // Simplest and safest way to switch roles or edges: start again with the new settings.
                RestartRequested = true;
                ExitThread();
            }
        }

        protected override void ExitThreadCore()
        {
            Status.Changed -= OnStatusChanged;
            Log.Notified -= OnNotified;
            if (tray != null)
            {
                tray.Visible = false; // otherwise the icon lingers until the mouse passes over it
                tray.Dispose();
            }
            logWindow?.Close();
            lostTimer.Dispose();
            ui.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>Shows the log, live.</summary>
    sealed class LogForm : Form
    {
        readonly TextBox box;
        readonly Action<string> onLine;

        public LogForm()
        {
            Text = "SharedMouse log";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icons.App;

            box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = SystemColors.Window,
            };
            var openFile = new Button { Text = "Open log file", AutoSize = true, MinimumSize = new Size(110, 28) };
            openFile.Click += (s, e) => Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\"");
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            bottom.Controls.Add(openFile);
            Controls.Add(box);
            Controls.Add(bottom);
            box.BringToFront(); // docked last, so it fills what the button row leaves

            onLine = line =>
            {
                try { BeginInvoke((Action)(() => box.AppendText(line + Environment.NewLine))); }
                catch (InvalidOperationException) { } // closed meanwhile
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            string[] lines = Log.Attach(onLine);
            box.AppendText(lines.Length == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Log.Detach(onLine);
            base.OnFormClosed(e);
        }
    }

    static class Icons
    {
        public static readonly Color Grey = Color.FromArgb(138, 138, 138), Green = Color.FromArgb(34, 154, 72), Blue = Color.FromArgb(37, 99, 235);

        /// <summary>SharedMouse.ico, all sizes, for windows.</summary>
        public static Icon App { get; } = LoadApp();

        static Icon LoadApp()
        {
            using (var stream = typeof(Icons).Assembly.GetManifestResourceStream("SharedMouse.ico"))
                return stream != null ? new Icon(stream) : Make(Blue);
        }

        /// <summary>A tray-sized icon in the given colour.</summary>
        public static Icon Make(Color color)
        {
            using (var bitmap = Draw(color, SystemInformation.SmallIconSize.Width))
                return Icon.FromHandle(bitmap.GetHicon());
        }

        /// <summary>A computer mouse, at any size. Also used to make SharedMouse.ico.</summary>
        public static Bitmap Draw(Color color, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            float s = size / 16f;
            using (var g = Graphics.FromImage(bitmap))
            using (var body = new GraphicsPath())
            using (var fill = new SolidBrush(color))
            using (var line = new Pen(Color.White, Math.Max(1f, s)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // a capsule: half circles at the top and bottom
                float x = 3.5f * s, y = 0.5f * s, w = 9f * s, h = 15f * s;
                body.AddArc(x, y, w, w, 180, 180);
                body.AddArc(x, y + h - w, w, w, 0, 180);
                body.CloseFigure();
                g.FillPath(fill, body);
                // the two buttons
                g.SetClip(body);
                g.DrawLine(line, 8f * s, 0, 8f * s, 6f * s);
                g.DrawLine(line, 0, 6f * s, size, 6f * s);
            }
            return bitmap;
        }
    }
}
