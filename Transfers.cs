using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SharedMouse
{
    /// <summary>A clipboard/file transfer being sent. Only the link's writer thread calls WriteNext/Dispose.</summary>
    abstract class Outgoing : IDisposable
    {
        static int lastId;

        public readonly int Id = Interlocked.Increment(ref lastId);
        public readonly Kind Kind;
        public readonly long Total; // data bytes in all
        public long Sent;  // data bytes written
        public long Acked; // data bytes the other side confirmed
        public volatile bool Finished;
        public Action Done; // called on the writer thread after the last byte went out

        protected Outgoing(Kind kind, long total)
        {
            Kind = kind;
            Total = total;
        }

        /// <summary>Writes the next message(s), with at most one data chunk.</summary>
        public abstract void WriteNext(BinaryWriter w, int chunkSize);

        protected void WriteData(BinaryWriter w, byte[] buffer, int offset, int count)
        {
            w.Write((byte)Msg.XferData);
            w.Write(Id);
            w.Write((ushort)count);
            w.Write(buffer, offset, count);
            Sent += count;
        }

        protected void WriteEnd(BinaryWriter w)
        {
            w.Write((byte)Msg.XferEnd);
            w.Write(Id);
            Finished = true;
            Done?.Invoke();
        }

        public virtual void Dispose() { }
    }

    /// <summary>Clipboard text (UTF-8) or image (PNG).</summary>
    sealed class BytesOutgoing : Outgoing
    {
        readonly byte[] data;
        bool begun;
        int offset;

        public BytesOutgoing(Kind kind, byte[] data) : base(kind, data.Length)
        {
            this.data = data;
        }

        public override void WriteNext(BinaryWriter w, int chunkSize)
        {
            if (!begun)
            {
                begun = true;
                w.Write((byte)Msg.XferBegin);
                w.Write(Id);
                w.Write((byte)Kind);
                w.Write((long)data.Length);
                w.Write(1);
            }
            int n = Math.Min(chunkSize, data.Length - offset);
            if (n > 0)
            {
                WriteData(w, data, offset, n);
                offset += n;
            }
            if (offset == data.Length)
                WriteEnd(w);
        }
    }

    sealed class FileItem
    {
        public string FullPath, RelativePath;
        public long Size; // -1 = folder
    }

    /// <summary>Files and folders copied in Explorer, read from disk as they are sent.</summary>
    sealed class FilesOutgoing : Outgoing
    {
        readonly List<FileItem> items;
        readonly byte[] buffer = new byte[65536];
        bool begun;
        int next;
        FileStream file;
        long left;

        public FilesOutgoing(List<FileItem> items, long total) : base(Kind.Files, total)
        {
            this.items = items;
        }

        public override void WriteNext(BinaryWriter w, int chunkSize)
        {
            try
            {
                if (!begun)
                {
                    begun = true;
                    w.Write((byte)Msg.XferBegin);
                    w.Write(Id);
                    w.Write((byte)Kind.Files);
                    w.Write(Total);
                    w.Write(items.Count);
                }

                // Start the next file/folder, skipping over empty ones.
                while (file == null)
                {
                    if (next == items.Count)
                    {
                        WriteEnd(w);
                        return;
                    }
                    FileItem item = items[next++];
                    w.Write((byte)Msg.XferItem);
                    w.Write(Id);
                    w.Write(item.RelativePath);
                    w.Write(item.Size);
                    if (item.Size > 0)
                    {
                        file = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        left = item.Size;
                    }
                }

                int n = file.Read(buffer, 0, (int)Math.Min(Math.Min(chunkSize, buffer.Length), left));
                if (n == 0)
                    throw new IOException(Path.GetFileName(file.Name) + " got shorter while it was being sent.");
                WriteData(w, buffer, 0, n);
                left -= n;
                if (left == 0)
                {
                    file.Dispose();
                    file = null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notify("Stopped sending files: " + ex.Message);
                w.Write((byte)Msg.XferCancel);
                w.Write(Id);
                Finished = true;
            }
        }

        public override void Dispose()
        {
            file?.Dispose();
            file = null;
        }
    }

    /// <summary>A transfer being received. Only the link's reader thread uses it.</summary>
    abstract class Incoming
    {
        public readonly int Id;
        public readonly Kind Kind;
        public readonly long Total;
        public long Received;

        protected Incoming(int id, Kind kind, long total)
        {
            Id = id;
            Kind = kind;
            Total = total;
        }

        public virtual void Item(string relativePath, long size) => throw new InvalidDataException("Unexpected file in a clipboard transfer.");
        public abstract void Data(byte[] bytes);
        public abstract void Complete();
        public virtual void Abort() { }
    }

    sealed class BytesIncoming : Incoming
    {
        readonly MemoryStream buffer;
        readonly Action<byte[]> onComplete;

        public BytesIncoming(int id, Kind kind, long total, Action<byte[]> onComplete) : base(id, kind, total)
        {
            buffer = new MemoryStream((int)total);
            this.onComplete = onComplete;
        }

        public override void Data(byte[] bytes)
        {
            if (buffer.Length + bytes.Length > Total)
                throw new InvalidDataException("Clipboard transfer is bigger than announced.");
            buffer.Write(bytes, 0, bytes.Length);
            Received += bytes.Length;
        }

        public override void Complete() => onComplete(buffer.ToArray());
    }

    /// <summary>Writes received files into a fresh folder under %TEMP%\SharedMouse\incoming.</summary>
    sealed class FilesIncoming : Incoming
    {
        readonly string folder;
        readonly int count;
        readonly string from;
        readonly Action<List<string>> onComplete;
        readonly List<string> topLevel = new List<string>();
        FileStream file;
        int files, lastPercent;

        public static string Root => Path.Combine(Path.GetTempPath(), "SharedMouse", "incoming");

        /// <summary>The folder of the files received last, or null.</summary>
        public static string LastFolder { get; private set; }

        /// <summary>Opens the files received last in Explorer, or the folder they all go to.</summary>
        public static void Show()
        {
            string folder = LastFolder != null && Directory.Exists(LastFolder) ? LastFolder : Root;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }

        public FilesIncoming(int id, long total, int count, string from, Action<List<string>> onComplete) : base(id, Kind.Files, total)
        {
            this.count = count;
            this.from = from;
            this.onComplete = onComplete;
            folder = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}");
            Directory.CreateDirectory(folder);
            string line = $"Receiving {count} item(s), {Log.Size(total)}, from {from}...";
            if (total > 2 * 1024 * 1024) // takes a while over Bluetooth
                Log.Notify(line);
            else
                Log.Write(line);
        }

        public override void Item(string relativePath, long size)
        {
            CloseFile();
            string path = SafePath(relativePath);
            string top = Path.Combine(folder, relativePath.Split('\\')[0]);
            if (!topLevel.Contains(top))
                topLevel.Add(top);
            if (size < 0)
            {
                Directory.CreateDirectory(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            files++;
        }

        public override void Data(byte[] bytes)
        {
            if (file == null)
                throw new InvalidDataException("File data without a file.");
            file.Write(bytes, 0, bytes.Length);
            Received += bytes.Length;
            int percent = Total > 0 ? (int)(Received * 100 / Total) : 100;
            if (Total > 2 * 1024 * 1024 && percent / 25 > lastPercent / 25 && percent < 100)
                Log.Write($"  {percent}%");
            lastPercent = percent;
        }

        public override void Complete()
        {
            CloseFile();
            LastFolder = folder;
            Log.Notify($"Received {files} file(s), {Log.Size(Received)}, from {from}. Paste them with Ctrl+V in any folder, " +
                       "or click here to see them.", Show);
            onComplete(topLevel);
        }

        public override void Abort()
        {
            CloseFile();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        void CloseFile()
        {
            file?.Dispose();
            file = null;
        }

        /// <summary>Only plain names below our folder: never "..", drive letters or absolute paths from the other side.</summary>
        string SafePath(string relativePath)
        {
            string[] parts = relativePath.Split('\\');
            foreach (string part in parts)
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Refused a strange file name from the other laptop: " + relativePath);
            string full = Path.GetFullPath(Path.Combine(folder, relativePath));
            if (!full.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refused a strange file name from the other laptop: " + relativePath);
            return full;
        }

        /// <summary>Removes received files older than a day.</summary>
        public static void CleanUp()
        {
            try
            {
                if (!Directory.Exists(Root))
                    return;
                foreach (string dir in Directory.GetDirectories(Root))
                    if (Directory.GetCreationTime(dir) < DateTime.Now.AddDays(-1))
                        Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
