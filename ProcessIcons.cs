using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace Procesi
{
    // Get and Dispose run on the UI thread. An image returned to a paint handler
    // stays valid for that paint. The worker never disposes a published image.
    internal sealed class ProcessIcons : IDisposable
    {
        private const int Capacity = 320;
        private readonly object gate = new object();
        private readonly Dictionary<Key, LinkedListNode<Entry>> entries = new Dictionary<Key, LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> recency = new LinkedList<Entry>();
        private readonly LinkedList<Entry> requests = new LinkedList<Entry>();
        private readonly Bitmap placeholder = SystemIcons.Application.ToBitmap();
        private readonly Action ready;
        private bool running, disposed;

        private struct Key : IEquatable<Key>
        {
            internal readonly string Path;
            internal readonly int Size;
            internal Key(string path, int size) { Path = path; Size = size; }
            public bool Equals(Key other) { return Size == other.Size && StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path); }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode() { return StringComparer.OrdinalIgnoreCase.GetHashCode(Path) ^ Size; }
        }

        private sealed class Entry
        {
            internal Key Key;
            internal LinkedListNode<Entry> Request;
            internal Bitmap Pixels;
        }

        public ProcessIcons(Action ready) { this.ready = ready; }

        public Image Get(string path, int pixelSize)
        {
            int size = Math.Max(12, Math.Min(64, pixelSize));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ProcessIcons");
                Image fallback = FindOrRequest(new Key("", size)).Pixels ?? placeholder;
                if (String.IsNullOrEmpty(path)) return fallback;
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
                // Reject UNC, device, root-relative and drive-relative paths before any lookup.
                if (path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
                    !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
                    !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fallback;
                return FindOrRequest(new Key(path, size)).Pixels ?? fallback;
            }
        }

        private Entry FindOrRequest(Key key)
        {
            LinkedListNode<Entry> node;
            if (entries.TryGetValue(key, out node))
            {
                if (node != recency.First) { recency.Remove(node); recency.AddFirst(node); }
                return node.Value;
            }
            if (entries.Count == Capacity)
            {
                Entry oldest = recency.Last.Value;
                recency.RemoveLast();
                entries.Remove(oldest.Key);
                if (oldest.Request != null) requests.Remove(oldest.Request);
                if (oldest.Pixels != null) oldest.Pixels.Dispose();
            }
            var entry = new Entry { Key = key };
            entries.Add(key, recency.AddFirst(entry));
            entry.Request = requests.AddLast(entry);
            if (!running)
            {
                var thread = new Thread(LoadRequests) { Name = "Process icons", IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                running = true;
                thread.Start();
            }
            return entry;
        }

        private void LoadRequests()
        {
            int batch = 0;
            var since = Stopwatch.StartNew();
            while (true)
            {
                Entry job;
                lock (gate)
                {
                    if (disposed || requests.Count == 0) { running = false; break; }
                    job = requests.First.Value;
                    requests.RemoveFirst();
                    job.Request = null;
                }
                Bitmap result = Load(job.Key);
                lock (gate)
                {
                    LinkedListNode<Entry> live;
                    if (!disposed && entries.TryGetValue(job.Key, out live) && live.Value == job)
                    {
                        job.Pixels = result;
                        if (result != null) batch++;
                    }
                    else if (result != null) result.Dispose();
                }
                if (batch > 0 && (batch >= 16 || since.ElapsedMilliseconds >= 100))
                {
                    Notify(); batch = 0; since.Restart();
                }
            }
            if (batch > 0) Notify();
        }

        private void Notify()
        {
            lock (gate) { if (disposed || ready == null) return; }
            // A form can close between this check and the caller's BeginInvoke.
            try { ready(); } catch (InvalidOperationException) { }
        }

        private static Bitmap Load(Key key)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                const uint iconFlag = 0x100, smallIcon = 1;
                // Request the larger system image when the small one would need upscaling.
                uint flags = iconFlag | (key.Size <= Math.Max(16, GetSystemMetrics(49)) ? smallIcon : 0u);
                if (key.Path.Length == 0)
                {
                    var stock = new StockInfo { Size = (uint)Marshal.SizeOf(typeof(StockInfo)) };
                    int hr = SHGetStockIconInfo(2, flags, ref stock); // SIID_APPLICATION.
                    handle = stock.Icon;
                    if (hr < 0 || handle == IntPtr.Zero) return null;
                }
                else
                {
                    if (!IsLocalFile(key.Path)) return null;
                    ShellFileInfo file;
                    IntPtr status = SHGetFileInfoW(key.Path, 0, out file, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), flags);
                    handle = file.Icon;
                    if (status == IntPtr.Zero || handle == IntPtr.Zero) return null;
                }
                Bitmap pixels;
                using (Icon icon = Icon.FromHandle(handle)) pixels = icon.ToBitmap();
                if (pixels.Width == key.Size && pixels.Height == key.Size) return pixels;
                using (pixels) return Scale(pixels, key.Size);
            }
            catch (ExternalException) { return null; }
            catch (ArgumentException) { return null; }
            // GDI+ can report an invalid or undecodable icon as OutOfMemory.
            catch (OutOfMemoryException) { return null; }
            finally { if (handle != IntPtr.Zero) DestroyIcon(handle); }
        }

        private static bool IsLocalFile(string path)
        {
            // Drive-letter mappings can be remote. A local junction, symlink or cloud
            // placeholder can also redirect Shell to a network or offline location.
            uint type = GetDriveTypeW(path.Substring(0, 3));
            if (type != 2 && type != 3 && type != 6) return false;
            const uint reparseOrOffline = 0x400 | 0x1000 | 0x40000 | 0x400000;
            for (int i = 3; i <= path.Length; i++)
            {
                if (i != path.Length && path[i] != '\\') continue;
                uint attr = GetFileAttributesW(i == path.Length ? path : path.Substring(0, i));
                if (attr == UInt32.MaxValue || (attr & reparseOrOffline) != 0) return false;
            }
            return true;
        }

        private static Bitmap Scale(Bitmap input, int size)
        {
            var scaled = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(scaled))
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(input, new Rectangle(0, 0, size, size), 0, 0, input.Width, input.Height, GraphicsUnit.Pixel, attributes);
                }
                return scaled;
            }
            catch { scaled.Dispose(); throw; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                requests.Clear();
                foreach (Entry entry in recency) if (entry.Pixels != null) entry.Pixels.Dispose();
                recency.Clear(); entries.Clear(); placeholder.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Display;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string Type;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StockInfo
        {
            public uint Size;
            public IntPtr Icon;
            public int SystemIndex, IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Path;
        }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
        [DllImport("shell32.dll", ExactSpelling=true)] private static extern int SHGetStockIconInfo(int id, uint flags, ref StockInfo info);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint GetFileAttributesW(string path);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
