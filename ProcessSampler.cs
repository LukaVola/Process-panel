using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Procesi
{
    public sealed class ProcessRow
    {
        public string Name, Path, Priority;
        public int Pid;
        public long Started, MemoryBytes;
        public double Cpu, Gpu = double.NaN;
    }

    public sealed class ProcessSnapshot
    {
        public List<ProcessRow> Rows = new List<ProcessRow>();
        public double Cpu = double.NaN, Gpu = double.NaN, MemoryPercent = double.NaN;
        public double CpuUtility = double.NaN, QueueLength = double.NaN, QueueAverage = double.NaN, Responsiveness = double.NaN;
        public int ThreadCount = -1, SocketCount = -1, GroupCount = -1, NumaCount = -1, CoreCount = -1, ParkedCount = -1;
        public double[] CoreCpu = new double[0];
        public double MemoryUsedGb = double.NaN, MemoryTotalGb = double.NaN;
    }

    internal sealed class ProcHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProcHandle() : base(true) {}
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        protected override bool ReleaseHandle() { return CloseHandle(handle); }
    }

    public static class ProcessSampler
    {
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, Pid;
            public UIntPtr Heap;
            public uint Module, Threads, Parent;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Exe;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Size, PageFaults;
            public UIntPtr PeakSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, PageFile, PeakPageFile;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Size, Load;
            public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended;
        }
        [DllImport("kernel32.dll", SetLastError=true)] private static extern ProcHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool Process32FirstW(ProcHandle snapshot, ref ProcessEntry info);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool Process32NextW(ProcHandle snapshot, ref ProcessEntry info);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern ProcHandle OpenProcess(uint rights, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetProcessTimes(ProcHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(ProcHandle process, uint flags, [Out] char[] path, ref int size);
        [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(ProcHandle process);
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(ProcHandle process, ref Counters counters, uint size);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

        private struct Sample { public long Started, Ticks, Timestamp; public string Path; }
        private static readonly object Gate = new object();
        private static Dictionary<int, Sample> previous = new Dictionary<int, Sample>();
        private static long lastTotal, lastIdle;

        public static ProcessSnapshot Capture()
        {
            lock (Gate) return CaptureLocked(null);
        }

        public static ProcessSnapshot CaptureForRules(IList<string> paths)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths) names.Add(System.IO.Path.GetFileName(path));
            lock (Gate)
            {
                Pause();
                return names.Count == 0 ? new ProcessSnapshot() : CaptureLocked(names);
            }
        }

        public static void Pause()
        {
            lock (Gate)
            {
                previous.Clear();
                lastTotal = lastIdle = 0;
                GpuSampler.Pause();
                CoreSampler.Pause();
                SystemStats.Pause();
            }
        }

        private static ProcessSnapshot CaptureLocked(HashSet<string> names)
        {
            ProcessSnapshot result = new ProcessSnapshot();
            uint logical = GetActiveProcessorCount(0xffff);
            if (logical == 0 || logical == 0xffffffff) logical = (uint)Environment.ProcessorCount;
            long idle, kernel, user;
            if (names == null && GetSystemTimes(out idle, out kernel, out user))
            {
                long total = kernel + user;
                if (logical <= 64 && lastTotal > 0 && total > lastTotal)
                    result.Cpu = Percent(100.0 * (1 - (double)(idle-lastIdle) / (total-lastTotal)));
                lastTotal = total; lastIdle = idle;
            }
            MemoryStatus memory = new MemoryStatus();
            memory.Size = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (names == null && GlobalMemoryStatusEx(ref memory) && memory.Total != 0)
            {
                result.MemoryTotalGb = memory.Total / 1073741824.0;
                result.MemoryUsedGb = (memory.Total - memory.Available) / 1073741824.0;
                result.MemoryPercent = 100 * result.MemoryUsedGb / result.MemoryTotalGb;
            }
            Dictionary<int,Sample> current = new Dictionary<int,Sample>();
            char[] buffer = new char[32768];
            uint counterSize = (uint)Marshal.SizeOf(typeof(Counters));
            long totalThreads = 0;
            long snapshotStart = DateTime.UtcNow.ToFileTimeUtc();
            using (ProcHandle list = CreateToolhelp32Snapshot(2, 0))
            {
                if (list.IsInvalid) throw new InvalidOperationException("Windows could not retrieve the process list.");
                ProcessEntry entry = new ProcessEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                if (!Process32FirstW(list, ref entry)) throw new InvalidOperationException("The process list is currently unavailable.");
                do
                {
                    if (entry.Pid == 0 || (names != null && !names.Contains(entry.Exe))) continue;
                    totalThreads += entry.Threads;
                    ProcessRow row = new ProcessRow { Name=entry.Exe, Pid=(int)entry.Pid, Path="", Priority="—", Cpu=double.NaN, MemoryBytes=-1 };
                    using (ProcHandle handle = OpenProcess(0x1000, false, row.Pid))
                    {
                        if (!handle.IsInvalid)
                        {
                            long exit;
                            Sample old = default(Sample);
                            Sample now = default(Sample);
                            if (GetProcessTimes(handle, out row.Started, out exit, out kernel, out user))
                            {
                                now = new Sample { Started=row.Started, Ticks=kernel+user, Timestamp=Stopwatch.GetTimestamp() };
                                if (previous.TryGetValue(row.Pid, out old) && old.Started == now.Started && now.Timestamp > old.Timestamp)
                                {
                                    row.Cpu = Percent(100.0 * (now.Ticks-old.Ticks) * Stopwatch.Frequency / (now.Timestamp-old.Timestamp) / (10000000.0 * logical));
                                    row.Path = old.Path;
                                }
                            }
                            if (String.IsNullOrEmpty(row.Path))
                            {
                                int length = buffer.Length;
                                if (QueryFullProcessImageNameW(handle, 0, buffer, ref length)) row.Path = new string(buffer,0,length);
                            }
                            if (!String.IsNullOrEmpty(row.Path))
                            {
                                row.Name = System.IO.Path.GetFileName(row.Path);
                                if (names != null && !names.Contains(row.Name)) continue;
                            }
                            else if (row.Started > snapshotStart)
                            {
                                // A PID may be reused after Toolhelp captures its name. Only use a
                                // snapshot-only name for a process that already existed at capture.
                                row.Started = 0;
                            }
                            if (row.Started > 0) { now.Path=row.Path; current[row.Pid]=now; }
                            if (names == null)
                            {
                                uint priority = GetPriorityClass(handle);
                                if (priority != 0) row.Priority = Controls.Display("cpu", priority.ToString(CultureInfo.InvariantCulture));
                                Counters counters = new Counters(); counters.Size = counterSize;
                                if (GetProcessMemoryInfo(handle, ref counters, counterSize)) row.MemoryBytes = (long)counters.WorkingSet.ToUInt64();
                            }
                        }
                    }
                    result.Rows.Add(row);
                } while (Process32NextW(list, ref entry));
                if (Marshal.GetLastWin32Error() != 18) throw new InvalidOperationException("Windows could not finish reading the process list.");
            }
            if (names == null)
            {
                previous = current;
                if (totalThreads <= int.MaxValue) result.ThreadCount = (int)totalThreads;
                CoreSampler.Fill(result);
                GpuSampler.Fill(result);
                SystemStats.Fill(result);
            }
            return result;
        }
        private static double Percent(double value) { return Math.Max(0, Math.Min(100, value)); }
    }
}
