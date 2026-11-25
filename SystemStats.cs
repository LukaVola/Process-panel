using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Procesi
{
    internal static class SystemStats
    {
        private static readonly WildcardCounter Utility =
            new WildcardCounter(@"\Processor Information(_Total)\% Processor Utility");
        private static readonly WildcardCounter Ready =
            new WildcardCounter(@"\System\Processor Queue Length");

        // Each window contains only observations from the last 30 seconds of visible sampling.
        private const int Seconds = 30;
        private struct ReadySample { internal long At; internal double Value; }
        private sealed class Acknowledgment { internal long At; }
        private struct UiSample { internal long Sent; internal Acknowledgment Reply; }
        private static readonly Queue<ReadySample> readyWindow = new Queue<ReadySample>();
        private static readonly Queue<UiSample> uiWindow = new Queue<UiSample>();
        private static SynchronizationContext ui;
        private static Acknowledgment pending;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relation, [Out] byte[] buffer, ref uint bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation([Out] byte[] buffer, uint bytes, out uint needed,
            IntPtr process, uint flags);
        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();
        private static byte[] setBuffer, topologyBuffer;
        private static int sockets = -1, numa = -1, cores = -1;
        private static long nextTopology;

        // Call from a WinForms UI thread after WindowsFormsSynchronizationContext has been installed.
        public static void AttachUi(SynchronizationContext context)
        {
            if (context != null && context.GetType() != typeof(SynchronizationContext))
                Interlocked.Exchange(ref ui, context);
        }

        internal static void Pause()
        {
            Utility.Pause();
            Ready.Pause();
            readyWindow.Clear();
            uiWindow.Clear();
            pending = null;
        }

        internal static void Fill(ProcessSnapshot snapshot)
        {
            long now = Stopwatch.GetTimestamp();
            long cutoff = now - Seconds * Stopwatch.Frequency;
            snapshot.CpuUtility = Utility.ReadValue(true, true);
            snapshot.QueueLength = Ready.ReadValue(false, true);
            while (readyWindow.Count > 0 && readyWindow.Peek().At < cutoff) readyWindow.Dequeue();
            if (!double.IsNaN(snapshot.QueueLength))
                readyWindow.Enqueue(new ReadySample { At = now, Value = snapshot.QueueLength });
            if (readyWindow.Count != 0)
            {
                double sum = 0;
                foreach (ReadySample sample in readyWindow) sum += sample.Value;
                snapshot.QueueAverage = sum / readyWindow.Count;
            }
            if (now >= nextTopology)
            {
                ReadTopology();
                nextTopology = now + 60 * Stopwatch.Frequency;
            }
            snapshot.SocketCount = sockets;
            snapshot.NumaCount = numa;
            snapshot.CoreCount = cores;
            ushort groups = GetActiveProcessorGroupCount();
            if (groups > 0) snapshot.GroupCount = groups;
            snapshot.ParkedCount = ReadParked(snapshot.CoreCpu.Length);
            snapshot.Responsiveness = ProbeUi();
        }

        private static double ProbeUi()
        {
            SynchronizationContext target = Volatile.Read(ref ui);
            if (target == null) return double.NaN;
            long sent = Stopwatch.GetTimestamp();
            if (pending == null || Interlocked.Read(ref pending.At) != 0)
            {
                var reply = new Acknowledgment();
                try
                {
                    target.Post(delegate(object unused) { Interlocked.Exchange(ref reply.At, Stopwatch.GetTimestamp()); }, null);
                }
                catch (InvalidAsynchronousStateException) { return double.NaN; }
                catch (InvalidOperationException) { return double.NaN; }
                pending = reply;
            }
            // If the UI is stalled, later probes share its outstanding acknowledgment instead of flooding the queue.
            uiWindow.Enqueue(new UiSample { Sent = sent, Reply = pending });
            long now = Stopwatch.GetTimestamp();
            long cutoff = now - Seconds * Stopwatch.Frequency;
            while (uiWindow.Count > 0 && uiWindow.Peek().Sent < cutoff) uiWindow.Dequeue();
            int observed = 0, onTime = 0;
            long deadline = Stopwatch.Frequency / 20; // 50 milliseconds.
            foreach (UiSample probe in uiWindow)
            {
                long completed = Interlocked.Read(ref probe.Reply.At);
                if (completed == 0 && now - probe.Sent < deadline) continue;
                observed++;
                if (completed != 0 && completed - probe.Sent <= deadline) onTime++;
            }
            return observed == 0 ? double.NaN : 100.0 * onTime / observed;
        }

        private static void ReadTopology()
        {
            cores = sockets = numa = -1;
            const int allRelations = 0xffff, insufficientBuffer = 122;
            uint length = topologyBuffer == null ? 0 : (uint)topologyBuffer.Length;
            if (!GetLogicalProcessorInformationEx(allRelations, topologyBuffer, ref length))
            {
                if (Marshal.GetLastWin32Error() != insufficientBuffer || length == 0 || length > int.MaxValue) return;
                topologyBuffer = new byte[(int)length];
                if (!GetLogicalProcessorInformationEx(allRelations, topologyBuffer, ref length)) return;
            }
            int coreCount = 0, packageCount = 0;
            var nodes = new HashSet<uint>();
            for (int pos = 0; pos < length;)
            {
                if (length - pos < 8) return;
                int type = BitConverter.ToInt32(topologyBuffer, pos);
                int size = BitConverter.ToInt32(topologyBuffer, pos + 4);
                if (size < 8 || size > length - pos) return;
                if (type == 0) coreCount++;
                if (type == 3) packageCount++;
                if ((type == 1 || type == 6) && size >= 12)
                    nodes.Add(BitConverter.ToUInt32(topologyBuffer, pos + 8));
                pos += size;
            }
            if (coreCount > 0) cores = coreCount;
            if (packageCount > 0) sockets = packageCount;
            if (nodes.Count > 0) numa = nodes.Count;
        }

        private static int ReadParked(int expectedCount)
        {
            const int insufficientBuffer = 122;
            uint needed;
            if (!GetSystemCpuSetInformation(setBuffer, setBuffer == null ? 0u : (uint)setBuffer.Length,
                out needed, IntPtr.Zero, 0))
            {
                if (Marshal.GetLastWin32Error() != insufficientBuffer || needed == 0 || needed > int.MaxValue) return -1;
                setBuffer = new byte[(int)needed];
                if (!GetSystemCpuSetInformation(setBuffer, (uint)setBuffer.Length, out needed, IntPtr.Zero, 0)) return -1;
            }
            int seen = 0, parked = 0;
            for (int pos = 0; pos < needed;)
            {
                if (needed - pos < 8) return -1;
                int size = BitConverter.ToInt32(setBuffer, pos);
                if (size < 8 || size > needed - pos) return -1;
                if (BitConverter.ToInt32(setBuffer, pos + 4) == 0 && size >= 20)
                {
                    seen++;
                    if ((setBuffer[pos + 19] & 1) != 0) parked++;
                }
                pos += size;
            }
            return seen > 0 && seen == expectedCount ? parked : -1;
        }
    }
}
