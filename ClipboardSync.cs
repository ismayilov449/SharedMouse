using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharedMouse
{
    /// <summary>
    /// Keeps both clipboards in sync: text, images, and files copied in Explorer (pasted from a temp folder on the other side).
    /// Has its own hidden window on its own thread, so slow clipboard work never delays the keyboard/mouse hooks.
    /// </summary>
    sealed class ClipboardSync
    {
        const int MaxTextBytes = 16 << 20, MaxImageBytes = 32 << 20, MaxFiles = 10000;
        const long MaxFilesBluetooth = 100L << 20, MaxFilesCable = 4L << 30;

        readonly bool clipboardOn, filesOn;
        volatile Link link;
        Form window;
        System.Windows.Forms.Timer settle;
        uint ownSequence; // clipboard sequence number right after we put the other laptop's data on it

        // reader-thread state
        Incoming incoming;
        int ackId;
        long ackBytes;

        public ClipboardSync(bool clipboardOn, bool filesOn)
        {
            this.clipboardOn = clipboardOn;
            this.filesOn = filesOn;
        }

        public byte Caps => (byte)((clipboardOn ? Link.CapClipboard : 0) | (filesOn ? Link.CapFiles : 0));

        public void Start()
        {
            FilesIncoming.CleanUp();
            if (Caps == 0)
                return;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                window = new ClipboardWindow(() => { settle.Stop(); settle.Start(); });
                _ = window.Handle;
                // Apps often change the clipboard several times in a row; wait until it settles.
                settle = new System.Windows.Forms.Timer { Interval = 200 };
                settle.Tick += (s, e) => { settle.Stop(); OnLocalCopy(); };
                Native.AddClipboardFormatListener(window.Handle);
                ready.Set();
                Application.Run();
            }) { IsBackground = true, Name = "clipboard" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }

        public void Attach(Link l)
        {
            link = l;
            bool clip = clipboardOn && (l.PeerCaps & Link.CapClipboard) != 0;
            bool files = filesOn && (l.PeerCaps & Link.CapFiles) != 0;
            Log.Write($"Clipboard sharing {(clip ? "on" : "off")}, file copy/paste {(files ? "on" : "off")}.");
        }

        /// <summary>Called on the reader thread once the connection has ended.</summary>
        public void Detach(Link l)
        {
            if (link == l)
                link = null;
            incoming?.Abort();
            incoming = null;
        }

        // ---------------- this laptop copied something (clipboard thread) ----------------

        void OnLocalCopy()
        {
            Link l = link;
            uint sequence = Native.GetClipboardSequenceNumber();
            if (l == null || l.Closed || sequence == ownSequence)
                return;
            bool sendClipboard = clipboardOn && (l.PeerCaps & Link.CapClipboard) != 0;
            bool sendFiles = filesOn && (l.PeerCaps & Link.CapFiles) != 0;
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    if (sendFiles)
                    {
                        var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
                        Task.Run(() => SendFiles(l, paths, sequence));
                    }
                }
                else if (sendClipboard && Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    if (bytes.Length > MaxTextBytes)
                        Log.Notify($"Copied text not shared: {Log.Size(bytes.Length)} is too big.");
                    else if (bytes.Length > 0)
                        l.StartTransfer(new BytesOutgoing(Kind.Text, bytes));
                }
                else if (sendClipboard && Clipboard.ContainsImage())
                {
                    Image image = Clipboard.GetImage();
                    if (image != null)
                        Task.Run(() => SendImage(l, image, sequence));
                }
            }
            catch (ExternalException)
            {
                // Another app is holding the clipboard open; its next change will come through.
            }
        }

        bool StillCurrent(Link l, uint sequence) => !l.Closed && link == l && Native.GetClipboardSequenceNumber() == sequence;

        void SendImage(Link l, Image image, uint sequence)
        {
            byte[] png;
            using (image)
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            if (png.Length > MaxImageBytes)
                Log.Notify($"Copied image not shared: {Log.Size(png.Length)} is too big.");
            else if (StillCurrent(l, sequence))
                l.StartTransfer(new BytesOutgoing(Kind.Image, png));
        }

        void SendFiles(Link l, List<string> paths, uint sequence)
        {
            var items = new List<FileItem>();
            long total = 0;
            try
            {
                foreach (string path in paths)
                {
                    string name = Path.GetFileName(path.TrimEnd('\\'));
                    if (name.Length == 0)
                        throw new IOException("Whole drives can't be sent.");
                    if (Directory.Exists(path))
                        AddFolder(items, path, name, ref total);
                    else
                        AddFile(items, path, name, ref total);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notify("Copied files not sent: " + ex.Message);
                return;
            }

            long max = l.IsBluetooth ? MaxFilesBluetooth : MaxFilesCable;
            if (total > max)
            {
                Log.Notify($"Copied files not sent: {Log.Size(total)} is more than the {Log.Size(max)} limit{(l.IsBluetooth ? " over Bluetooth" : "")}.");
                return;
            }
            if (!StillCurrent(l, sequence))
                return;
            Log.Write($"Sending {items.Count} item(s), {Log.Size(total)}, to {l.PeerName}...");
            l.StartTransfer(new FilesOutgoing(items, total) { Done = () => Log.Write("Files sent.") });
        }

        static void AddFile(List<FileItem> items, string path, string relative, ref long total)
        {
            long size = new FileInfo(path).Length;
            items.Add(new FileItem { FullPath = path, RelativePath = relative, Size = size });
            total += size;
            if (items.Count > MaxFiles)
                throw new IOException($"more than {MaxFiles} files.");
        }

        static void AddFolder(List<FileItem> items, string path, string relative, ref long total)
        {
            items.Add(new FileItem { FullPath = path, RelativePath = relative, Size = -1 });
            foreach (string file in Directory.GetFiles(path))
                AddFile(items, file, relative + "\\" + Path.GetFileName(file), ref total);
            foreach (string dir in Directory.GetDirectories(path))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) // skip links, they can loop
                    AddFolder(items, dir, relative + "\\" + Path.GetFileName(dir), ref total);
        }

        // ---------------- the other laptop copied something (reader thread) ----------------

        /// <summary>Handles transfer messages; returns false for anything else.</summary>
        public bool Handle(Msg type, Link l)
        {
            BinaryReader r = l.Reader;
            switch (type)
            {
                case Msg.XferBegin:
                {
                    int id = r.ReadInt32();
                    var kind = (Kind)r.ReadByte();
                    long total = r.ReadInt64();
                    int count = r.ReadInt32();
                    incoming?.Abort();
                    incoming = null;
                    Try(() => incoming = Begin(l, id, kind, total, count));
                    return true;
                }
                case Msg.XferItem:
                {
                    int id = r.ReadInt32();
                    string path = r.ReadString();
                    long size = r.ReadInt64();
                    if (incoming?.Id == id)
                        Try(() => incoming.Item(path, size));
                    return true;
                }
                case Msg.XferData:
                {
                    int id = r.ReadInt32();
                    int n = r.ReadUInt16();
                    byte[] data = r.ReadBytes(n);
                    if (data.Length != n)
                        throw new EndOfStreamException();
                    if (incoming?.Id == id)
                        Try(() => incoming.Data(data));
                    // Always confirm, even data we skip, so the sender keeps going.
                    if (ackId != id)
                    {
                        ackId = id;
                        ackBytes = 0;
                    }
                    ackBytes += n;
                    l.Post(Ev.Ack(id, ackBytes));
                    return true;
                }
                case Msg.XferEnd:
                {
                    int id = r.ReadInt32();
                    if (incoming?.Id == id)
                    {
                        Incoming done = incoming;
                        incoming = null;
                        Try(done.Complete);
                    }
                    return true;
                }
                case Msg.XferCancel:
                {
                    int id = r.ReadInt32();
                    if (incoming?.Id == id)
                    {
                        incoming.Abort();
                        incoming = null;
                    }
                    return true;
                }
                case Msg.XferAck:
                    l.OnAck(r.ReadInt32(), r.ReadInt64());
                    return true;
                default:
                    return false;
            }
        }

        Incoming Begin(Link l, int id, Kind kind, long total, int count)
        {
            string from = l.PeerName;
            switch (kind)
            {
                case Kind.Text when clipboardOn && total > 0 && total <= MaxTextBytes:
                    return new BytesIncoming(id, total, bytes => SetClipboard(() =>
                    {
                        var data = new DataObject();
                        data.SetText(Encoding.UTF8.GetString(bytes), TextDataFormat.UnicodeText);
                        return data;
                    }, $"Clipboard: text from {from}"));

                case Kind.Image when clipboardOn && total > 0 && total <= MaxImageBytes:
                    return new BytesIncoming(id, total, bytes => SetClipboard(() =>
                    {
                        using (var ms = new MemoryStream(bytes))
                        using (var image = Image.FromStream(ms))
                        {
                            var data = new DataObject();
                            data.SetImage(new Bitmap(image));
                            return data;
                        }
                    }, $"Clipboard: image from {from}"));

                case Kind.Files when filesOn && total <= MaxFilesCable && count <= MaxFiles + 1000:
                    return new FilesIncoming(id, total, count, from, paths => SetClipboard(() =>
                    {
                        var list = new StringCollection();
                        list.AddRange(paths.ToArray());
                        var data = new DataObject();
                        data.SetFileDropList(list);
                        return data;
                    }, null));

                default:
                    return null; // not wanted here: the data is skipped
            }
        }

        void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
            {
                Log.Notify("Couldn't receive the copied data: " + ex.Message);
                incoming?.Abort();
                incoming = null;
            }
        }

        void SetClipboard(Func<DataObject> make, string message)
        {
            window.BeginInvoke((Action)(() =>
            {
                try
                {
                    Clipboard.SetDataObject(make(), copy: true, retryTimes: 10, retryDelay: 100);
                    ownSequence = Native.GetClipboardSequenceNumber();
                    if (message != null)
                        Log.Write(message);
                }
                catch (Exception ex) when (ex is ExternalException || ex is ArgumentException)
                {
                    Log.Notify("Couldn't update the clipboard: " + ex.Message);
                }
            }));
        }

        sealed class ClipboardWindow : Form
        {
            readonly Action onUpdate;

            public ClipboardWindow(Action onUpdate)
            {
                this.onUpdate = onUpdate;
                ShowInTaskbar = false;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_CLIPBOARDUPDATE)
                    onUpdate();
                base.WndProc(ref m);
            }
        }
    }
}
