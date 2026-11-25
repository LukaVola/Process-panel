using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Procesi
{
    // ProcessSampler serializes reads and closes the queries when the window is hidden.
    internal sealed class WildcardCounter
    {
        private const uint MoreData = 0x800007D2;
        private const uint FormatDouble = 0x200;

        [StructLayout(LayoutKind.Sequential)]
        internal struct CounterItem
        {
            internal IntPtr Name;
            internal uint Status;
            internal double Value;
            internal bool HasValue
            {
                get { return Status <= 1 && !double.IsNaN(Value) && !double.IsInfinity(Value) && Value >= 0; }
            }
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string source, UIntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes,
            out uint count, IntPtr items);
        [StructLayout(LayoutKind.Sequential)]
        private struct SingleValue { internal uint Status; internal double Value; }
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out SingleValue value);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCloseQuery(IntPtr query);

        private static readonly int ItemBytes = Marshal.SizeOf(typeof(CounterItem));
        private readonly string counterPath;
        private IntPtr query, counter, buffer;
        private uint capacity;
        private long lastSample;
        private DateTime retryAt;

        internal WildcardCounter(string path) { counterPath = path; }

        internal void Pause()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            query = counter = buffer = IntPtr.Zero;
            capacity = 0;
            lastSample = 0;
        }

        private bool Collect(out long since)
        {
            since = lastSample;
            DateTime now = DateTime.UtcNow;
            if (now < retryAt) return false;
            bool openFailed = query == IntPtr.Zero && (PdhOpenQueryW(null, UIntPtr.Zero, out query) != 0 ||
                PdhAddEnglishCounterW(query, counterPath, UIntPtr.Zero, out counter) != 0);
            if (openFailed || PdhCollectQueryData(query) != 0)
            {
                Pause();
                retryAt = now.AddMinutes(1);
                return false;
            }
            lastSample = now.ToFileTimeUtc();
            return true;
        }

        internal double ReadValue(bool needsPrevious, bool uncapped)
        {
            long since;
            if (!Collect(out since) || (needsPrevious && since == 0)) return double.NaN;
            SingleValue value;
            const uint noCap100 = 0x8000;
            uint format = uncapped ? FormatDouble | noCap100 : FormatDouble;
            if (PdhGetFormattedCounterValue(counter, format, IntPtr.Zero, out value) != 0 || value.Status > 1 ||
                double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0) return double.NaN;
            return value.Value;
        }

        internal int Read(out long since)
        {
            if (!Collect(out since) || since == 0) return 0;
            uint count, bytes = capacity;
            uint status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out count, buffer);
            if (status == MoreData && bytes > capacity && bytes <= int.MaxValue)
            {
                buffer = buffer == IntPtr.Zero
                    ? Marshal.AllocHGlobal((int)bytes) : Marshal.ReAllocHGlobal(buffer, new IntPtr((int)bytes));
                capacity = bytes;
                status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out count, buffer);
            }
            return status == 0 && (ulong)count * (uint)ItemBytes <= capacity ? (int)count : 0;
        }

        internal CounterItem ItemAt(int index)
        {
            return Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * ItemBytes));
        }
    }

    internal static class CoreSampler
    {
        private static readonly WildcardCounter Counter =
            new WildcardCounter(@"\Processor Information(*)\% Idle Time");
        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort group);

        public static void Pause() { Counter.Pause(); }

        public static void Fill(ProcessSnapshot snapshot)
        {
            const ushort allGroups = 0xffff;
            uint logicalCount = GetActiveProcessorCount(allGroups);
            if (logicalCount == 0 || logicalCount > int.MaxValue) logicalCount = (uint)Environment.ProcessorCount;
            snapshot.CoreCpu = new double[logicalCount];
            for (int i = 0; i < snapshot.CoreCpu.Length; i++) snapshot.CoreCpu[i] = double.NaN;
            long since;
            int count = Counter.Read(out since);
            if (count == 0) return;

            // Processor Information uses NUMA-node,logical-index instance names, including on multi-group hosts.
            // Sort their numeric components (the provider orders "0,10" before "0,2").
            var cpus = new SortedDictionary<ulong, double>();
            double total = double.NaN;
            for (int i = 0; i < count; i++)
            {
                var item = Counter.ItemAt(i);
                if (item.Name == IntPtr.Zero) continue;
                string name = Marshal.PtrToStringUni(item.Name);
                double value = item.HasValue ? 100 - Math.Min(100, item.Value) : double.NaN;
                if (string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase))
                {
                    total = value;
                    continue;
                }
                int comma = name.IndexOf(',');
                uint node, index;
                if (comma <= 0 || !uint.TryParse(name.Substring(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out node) ||
                    !uint.TryParse(name.Substring(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index)) continue;
                cpus[((ulong)node << 32) | index] = value;
            }
            if (!double.IsNaN(total) && (logicalCount > 64 || double.IsNaN(snapshot.Cpu))) snapshot.Cpu = total;
            // With an incomplete counter set the array positions could otherwise identify the wrong CPU.
            if (cpus.Count == snapshot.CoreCpu.Length) cpus.Values.CopyTo(snapshot.CoreCpu, 0);
        }
    }
}
