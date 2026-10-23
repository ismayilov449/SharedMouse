using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SharedMouse
{
    /// <summary>
    /// The icon next to the clock: its colour shows the connection, hovering shows the delay, a click switches (or
    /// connects), right-click opens the menu, and important events pop up as notifications.
    /// Runs on the main (UI) thread; the keyboard/mouse work never does.
    /// </summary>
    sealed class TrayApp : ApplicationContext
    {
        const int LostNoticeAfterMs = 5000; // a Bluetooth hiccup that reconnects by itself isn't worth a pop-up

        readonly Settings settings;
        readonly bool saved; // false when started with command-line options
        readonly Control ui = new Control(); // to get back onto the UI thread
        readonly Icon searching = Icons.Make(Icons.Grey), on = Icons.Make(Icons.Green), away = Icons.Make(Icons.Blue),
                      disconnected = Icons.Make(Icons.Red);
        readonly System.Windows.Forms.Timer lostTimer = new System.Windows.Forms.Timer { Interval = LostNoticeAfterMs };
        readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        NotifyIcon tray;
        ToolStripMenuItem statusItem, switchItem, connectItem, progressItem, cancelItem, clipboardItem, filesItem, autostartItem;
        ToolStripSeparator progressLine;
        Sender sender;
        ClipboardSync clipboard;
        Action toggleConnection; // the Sender's or the Receiver's
        HotkeyWindow hotkey;     // Ctrl+` on the second laptop (the main one sees it in its keyboard hook)
        SetupForm settingsWindow;
        LogForm logWindow;
        bool wasConnected, everConnected, lostShown;
        string lostPeer, lastNotice, progress;
        Action noticeClick; // what clicking the notification shown last does
        DateTime lastNoticeAt;
        int lastClickAt = Environment.TickCount - 10000;

        public bool RestartRequested { get; private set; }

        /// <param name="greeting">Shown as a notification once the icon is up, or null.</param>
        /// <param name="showSignal">Set by a second copy of SharedMouse that was started: show the settings.</param>
        public TrayApp(Settings settings, bool saved, string greeting, WaitHandle showSignal)
        {
            this.settings = settings;
            this.saved = saved;
            ui.CreateControl();
            Flash.Init(ui);
            Status.Changed += OnStatusChanged;
            Log.Notified += OnNotified;

            StartSharing(); // throws if the keyboard/mouse can't be watched

            tray = new NotifyIcon { Icon = searching, Text = "SharedMouse", ContextMenuStrip = Menu() };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OnClick(); };
            tray.BalloonTipClicked += (s, e) => noticeClick?.Invoke();
            lostTimer.Tick += (s, e) => OnLostTimer();
            progressTimer.Tick += (s, e) => UpdateProgress();
            progressTimer.Start();
            tray.Visible = true;
            Refresh();
            if (greeting != null)
                Notice(greeting);
            if (showSignal != null)
                ThreadPool.RegisterWaitForSingleObject(showSignal, (o, timedOut) => OnUi(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        void StartSharing()
        {
            clipboard = new ClipboardSync(settings.ShareClipboard, settings.ShareFiles);
            if (settings.IsMain)
            {
                sender = new Sender(settings.Side, settings.DelayMs, settings.PeerAddress, settings.Tcp, clipboard);
                sender.Start();
                toggleConnection = sender.ToggleConnection;
            }
            else
            {
                var receiver = new Receiver(settings.Tcp, clipboard);
                receiver.Start();
                toggleConnection = receiver.ToggleConnection;
                hotkey = new HotkeyWindow(toggleConnection);
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
            connectItem = new ToolStripMenuItem("Disconnect") { ShortcutKeyDisplayString = "Ctrl+`" };
            connectItem.Click += (s, e) => toggleConnection();
            menu.Items.Add(connectItem);

            // only while a bigger clipboard/file transfer is under way
            progressLine = new ToolStripSeparator { Visible = false };
            progressItem = new ToolStripMenuItem { Enabled = false, Visible = false };
            cancelItem = new ToolStripMenuItem("Cancel transfer") { Visible = false };
            cancelItem.Click += (s, e) => clipboard.CancelTransfers();
            menu.Items.Add(progressLine);
            menu.Items.Add(progressItem);
            menu.Items.Add(cancelItem);

            menu.Items.Add(new ToolStripSeparator());
            clipboardItem = new ToolStripMenuItem("Share clipboard");
            clipboardItem.Click += (s, e) => SetSharing(!clipboard.ClipboardOn, clipboard.FilesOn);
            filesItem = new ToolStripMenuItem("Share files");
            filesItem.Click += (s, e) => SetSharing(clipboard.ClipboardOn, !clipboard.FilesOn);
            menu.Items.Add(clipboardItem);
            menu.Items.Add(filesItem);
            menu.Items.Add("Open received files", null, (s, e) => FilesIncoming.Show());

            menu.Items.Add(new ToolStripSeparator());
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
                clipboardItem.Checked = clipboard.ClipboardOn;
                filesItem.Checked = clipboard.FilesOn;
                UpdateProgress();
            };
            return menu;
        }

        void SetSharing(bool clipboardOn, bool filesOn)
        {
            clipboard.SetSharing(clipboardOn, filesOn);
            settings.ShareClipboard = clipboardOn;
            settings.ShareFiles = filesOn;
            if (saved)
                settings.Save(); // the settings window shows these too
        }

        /// <summary>
        /// A click on the icon: back to Connect when disconnected; on the main laptop, switch to the other one;
        /// otherwise the menu. A double-click counts once, so it doesn't switch there and straight back.
        /// </summary>
        void OnClick()
        {
            int now = Environment.TickCount;
            if (unchecked(now - lastClickAt) < SystemInformation.DoubleClickTime)
                return;
            lastClickAt = now;
            if (Status.Paused)
                toggleConnection();
            else if (sender != null && Status.Connected)
                sender.SwitchNow();
            else
                ShowMenu();
        }

        void ShowMenu() =>
            // NotifyIcon only opens its menu on a right-click by itself; this is the same method it uses then.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);

        // ---------------- state (UI thread) ----------------

        void OnStatusChanged() => OnUi(Refresh);

        void OnNotified(string text, Action onClick) => OnUi(() => Notice(text, onClick));

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

            tray.Icon = Status.Paused ? disconnected : !connected ? searching : Status.Remote ? away : on;
            string text = StatusText();
            statusItem.Text = text;
            tray.Text = Truncate(progress ?? "SharedMouse: " + text, 63); // Windows' limit for the hover text
            if (switchItem != null)
                switchItem.Enabled = connected;
            connectItem.Text = Status.Paused ? "Connect" : "Disconnect";
        }

        /// <summary>Shows the transfer under way in the hover text and the menu, with "Cancel transfer".</summary>
        void UpdateProgress()
        {
            string p = clipboard.Progress();
            if (p == progress)
                return;
            progress = p;
            progressLine.Visible = progressItem.Visible = cancelItem.Visible = p != null;
            progressItem.Text = p ?? "";
            Refresh();
        }

        string StatusText()
        {
            if (Status.Paused)
                return "Disconnected";
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
            if (Status.Connected || Status.Paused) // no "lost" pop-up after clicking Disconnect
                return;
            Notice($"Lost the connection to {lostPeer}. Reconnecting automatically...");
            lostShown = true;
        }

        void Notice(string text, Action onClick = null)
        {
            if (tray == null || (text == lastNotice && DateTime.UtcNow - lastNoticeAt < TimeSpan.FromMinutes(1)))
                return;
            lastNotice = text;
            lastNoticeAt = DateTime.UtcNow;
            noticeClick = onClick;
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
            hotkey?.Dispose();
            lostTimer.Dispose();
            progressTimer.Dispose();
            ui.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>Ctrl+` (the key under Esc) anywhere in Windows: disconnect / connect. For the second laptop.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int Id = 1;
        const uint ScanBacktick = 0x29;
        readonly Action onPress;
        readonly bool registered;

        public HotkeyWindow(Action onPress)
        {
            this.onPress = onPress;
            CreateHandle(new CreateParams());
            uint vk = Native.MapVirtualKey(ScanBacktick, Native.MAPVK_VSC_TO_VK);
            registered = vk != 0 && Native.RegisterHotKey(Handle, Id, Native.MOD_CONTROL | Native.MOD_NOREPEAT, vk);
            if (!registered)
                Log.Write("Ctrl+` is already used by another app here, so it doesn't disconnect on this laptop. " +
                          "Click the icon or use its menu instead.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == Id)
                ThreadPool.QueueUserWorkItem(_ => onPress()); // closing the connection may take a moment
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registered)
                Native.UnregisterHotKey(Handle, Id);
            DestroyHandle();
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
        public static readonly Color Grey = Color.FromArgb(138, 138, 138), Green = Color.FromArgb(34, 154, 72), Blue = Color.FromArgb(37, 99, 235),
                                     Red = Color.FromArgb(220, 38, 38);

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
