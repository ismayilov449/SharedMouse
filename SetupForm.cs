using System;
using System.Drawing;
using System.Windows.Forms;

namespace SharedMouse
{
    /// <summary>The setup window: shown on the first start, and later from the tray icon's "Settings...".</summary>
    sealed class SetupForm : Form
    {
        static readonly Color Frame = Color.FromArgb(160, 170, 185), ScreenFill = Color.FromArgb(232, 238, 247);

        readonly Settings original;
        readonly RadioButton mainRole, secondRole;
        readonly Label sideTitle;
        readonly TableLayoutPanel sidePicker;
        readonly CheckBox[] sideButtons = new CheckBox[4];
        readonly CheckBox clipboard, files, autostart;
        Side side;

        /// <summary>The chosen settings, after OK.</summary>
        public Settings Result { get; private set; }
        public bool StartWithWindows => autostart.Checked;

        public SetupForm(Settings settings, bool firstRun, bool startWithWindows)
        {
            original = settings;
            side = settings.Side;

            Text = firstRun ? "SharedMouse setup" : "SharedMouse settings";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = Icons.App;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 10, 18, 12),
            };
            Controls.Add(root);

            root.Controls.Add(Heading("This laptop is", top: 0));
            mainRole = new RadioButton { Text = "The main laptop", AutoSize = true, Checked = settings.IsMain, Margin = new Padding(3, 4, 3, 0) };
            root.Controls.Add(mainRole);
            root.Controls.Add(Hint("Its keyboard and mouse are shared with the other laptop."));
            secondRole = new RadioButton { Text = "The second laptop", AutoSize = true, Checked = !settings.IsMain, Margin = new Padding(3, 8, 3, 0) };
            root.Controls.Add(secondRole);
            root.Controls.Add(Hint("It is controlled with the main laptop's keyboard and mouse."));

            sideTitle = Heading("Where is the second laptop?", top: 16);
            root.Controls.Add(sideTitle);
            sidePicker = SidePicker();
            root.Controls.Add(sidePicker);

            root.Controls.Add(Heading("Options", top: 16));
            clipboard = Check("Share the clipboard (copied text and pictures)", settings.ShareClipboard);
            files = Check("Share files copied in Explorer (Ctrl+C on one laptop, Ctrl+V on the other)", settings.ShareFiles);
            autostart = Check("Start SharedMouse when I sign in to Windows", startWithWindows);
            root.Controls.Add(clipboard);
            root.Controls.Add(files);
            root.Controls.Add(autostart);

            if (firstRun)
            {
                var next = Hint("Then run the same SharedMouse.exe on the other laptop and set it up there too.");
                next.Margin = new Padding(3, 14, 3, 0);
                root.Controls.Add(next);
            }

            var ok = new Button { Text = firstRun ? "Start" : "Save", MinimumSize = new Size(88, 28), AutoSize = true };
            var cancel = new Button { Text = "Cancel", MinimumSize = new Size(88, 28), AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Result = Build();
                DialogResult = DialogResult.OK;
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 16, 0, 0),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            mainRole.CheckedChanged += (s, e) => ShowRole();
            ShowRole();
            SelectSide(side);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        Settings Build()
        {
            Settings s = original.Clone();
            s.IsMain = mainRole.Checked;
            s.Side = side;
            s.ShareClipboard = clipboard.Checked;
            s.ShareFiles = files.Checked;
            return s;
        }

        void ShowRole()
        {
            // The main laptop decides the layout; the second one just follows.
            sideTitle.Visible = sidePicker.Visible = mainRole.Checked;
        }

        TableLayoutPanel SidePicker()
        {
            var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true, Margin = new Padding(20, 4, 3, 0) };
            var self = new Label
            {
                Text = "This laptop",
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = ScreenFill,
                Size = new Size(120, 60),
                Margin = new Padding(3),
            };
            grid.Controls.Add(SideButton("Above", Side.Top, 120, 30), 1, 0);
            grid.Controls.Add(SideButton("Left", Side.Left, 80, 60), 0, 1);
            grid.Controls.Add(self, 1, 1);
            grid.Controls.Add(SideButton("Right", Side.Right, 80, 60), 2, 1);
            grid.Controls.Add(SideButton("Below", Side.Bottom, 120, 30), 1, 2);
            return grid;
        }

        CheckBox SideButton(string text, Side s, int width, int height)
        {
            var b = new CheckBox
            {
                Text = text,
                Appearance = Appearance.Button,
                AutoCheck = false,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(width, height),
                Margin = new Padding(3),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Frame;
            b.FlatAppearance.CheckedBackColor = Icons.Blue;
            b.Click += (sender, e) => SelectSide(s);
            sideButtons[(int)s] = b;
            return b;
        }

        void SelectSide(Side s)
        {
            side = s;
            for (int i = 0; i < sideButtons.Length; i++)
            {
                sideButtons[i].Checked = i == (int)s;
                sideButtons[i].ForeColor = i == (int)s ? Color.White : SystemColors.ControlText;
            }
        }

        static Label Heading(string text, int top) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(3, top, 3, 2),
        };

        static Label Hint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(21, 0, 3, 0), // lined up with the radio button's text
        };

        static CheckBox Check(string text, bool on) => new CheckBox
        {
            Text = text,
            AutoSize = true,
            Checked = on,
            Margin = new Padding(3, 4, 3, 0),
        };
    }
}
