using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Procesi
{
    internal static class Controls
    {
        private const uint QueryLimited = 0x1000;
        private const uint SetInformation = 0x0200;
        private const uint SetLimited = 0x2000;
        private const int InsufficientBuffer = 122;
        private const int ProcessMemoryPriority = 0;
        private const int ProcessIoPriority = 33;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] CpuValues = { "64", "16384", "32", "32768", "128", "256" };
        private static readonly string[] GpuValues = { "0", "1", "2", "3", "4", "5" };
        // The CPU and GPU scheduling APIs use the same order, with Idle as the lowest class.
        private static readonly string[] PriorityCodes = { "VL", "BN", "N", "AN", "H", "RT" };
        private static readonly string[] PriorityNames = { "Very low", "Below normal", "Normal", "Above normal", "High", "Realtime" };
        private static readonly string[] PriorityLabels = PriorityNames.Select((name,i) => name + " (" + PriorityCodes[i] + ")").ToArray();
        private static readonly string[] IoValues = { "0", "1", "2" };
        private static readonly string[] IoLabels = { "Very low", "Low", "Normal" };
        private static readonly string[] MemoryValues = { "1", "2", "3", "4", "5" };
        private static readonly string[] MemoryLabels = { "Very low", "Low", "Medium", "Below normal", "Normal" };

        public static string Label(string key)
        {
            switch (key)
            {
                case "cpu": return "CPU Priority";
                case "io": return "I/O Priority";
                case "memory": return "Memory Priority";
                case "gpupriority": return "GPU Priority";
                case "affinity": return "CPU Affinity";
                case "cpusets": return "CPU Sets";
                default: throw new ArgumentException("Unknown process setting.");
            }
        }

        private static readonly string[] NoValues = new string[0];

        private static string[] ValueTable(string key)
        {
            switch (key)
            {
                case "cpu": return CpuValues;
                case "gpupriority": return GpuValues;
                case "io": return IoValues;
                case "memory": return MemoryValues;
                default: return NoValues;
            }
        }

        public static string[] Choices(string key) { return (string[])ValueTable(key).Clone(); }

        public static int PriorityRank(string label)
        {
            int index = Array.IndexOf(PriorityCodes, label);
            if (index < 0) index = Array.IndexOf(PriorityLabels, label);
            if (index < 0) index = Array.IndexOf(PriorityNames, label);
            if (index < 0 && label == "Idle") return 1;
            return index + 1;
        }

        public static string PriorityDescription(string label)
        {
            int rank = PriorityRank(label);
            if (rank == 0) return label;
            return rank == 1 ? PriorityLabels[0] + " · Windows Idle" : PriorityLabels[rank - 1];
        }

        public static string Display(string key, string value)
        {
            return FormatValue(key,value,false);
        }

        public static string DisplayCompact(string key, string value)
        {
            return FormatValue(key,value,true);
        }

        private static string FormatValue(string key, string value,bool compact)
        {
            if (value == null) return "—";
            if (key == "cpusets")
                return CpuSetRule.IsLocation(value) ? CpuSetRule.Display(value) :
                    value == "default" || value.Length == 0 ? "Let Windows decide" : value.Replace(",", ", ");
            if (key == "affinity") return AffinityDisplay.Format(value);
            int index = Array.IndexOf(ValueTable(key), value);
            if (key == "io" && value == "3") return "High (system)";
            if (key == "io" && value == "4") return "Critical (system)";
            if (index < 0) return value;
            if (key == "memory") return MemoryLabels[index];
            if (key == "io") return IoLabels[index];
            return compact ? PriorityCodes[index] : PriorityLabels[index];
        }

        public static string Normalize(string key, string value)
        {
            string raw = (value ?? "").Trim();
            if (key == "affinity")
            {
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(2);
                ulong mask;
                if (!UInt64.TryParse(raw, NumberStyles.AllowHexSpecifier, Invariant, out mask) || mask == 0)
                    throw new ArgumentException("Enter a hexadecimal mask that selects at least one logical processor.");
                return mask.ToString("X", Invariant);
            }
            if (key == "cpusets")
            {
                if (raw.Length == 0 || String.Equals(raw, "default", StringComparison.OrdinalIgnoreCase)) return "default";
                var ids = new SortedSet<uint>();
                foreach (string part in raw.Split(','))
                {
                    uint id;
                    if (!UInt32.TryParse(part.Trim(), NumberStyles.None, Invariant, out id))
                        throw new ArgumentException("Enter CPU set IDs separated by commas.");
                    ids.Add(id);
                }
                return String.Join(",", ids.Select(id => id.ToString(Invariant)));
            }
            if (Array.IndexOf(ValueTable(key), raw) < 0) throw new ArgumentException("Choose a valid value for this setting.");
            return raw;
        }

        // Query and change the same kernel object. A reused PID must never inherit an old selection.
        private static SafeProcessHandle OpenChecked(int pid, long started, uint rights)
        {
            if (started <= 0 || pid <= 0) throw new InvalidOperationException("The process identity could not be verified.");
            SafeProcessHandle process = OpenProcess(rights | QueryLimited, false, pid);
            if (process.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                process.Dispose();
                throw Error(code);
            }
            try
            {
                long created, exited, kernel, user;
                Check(GetProcessTimes(process, out created, out exited, out kernel, out user));
                uint exitCode;
                Check(GetExitCodeProcess(process, out exitCode));
                if (created != started || exitCode != 259) // STILL_ACTIVE
                    throw new InvalidOperationException("This process is no longer running.");
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        private static Exception Error(int code)
        {
            if (code == 5 || code == 1314)
                return new InvalidOperationException("Windows denied access. This setting may require administrator privileges. Windows can also restrict changes to protected processes.");
            if (code == 50 || code == 120)
                return new NotSupportedException("This system does not support the selected setting.");
            return WindowsError.FromCode(code);
        }

        private static void Check(bool ok)
        {
            if (!ok) throw Error(Marshal.GetLastWin32Error());
        }

        private static void CheckStatus(int status)
        {
            if (status < 0) throw Error(unchecked((int)RtlNtStatusToDosError(status)));
        }

        private static uint ReadAccess(string key)
        {
            // The graphics scheduler requires PROCESS_SET_INFORMATION even for its query.
            return key == "gpupriority" ? SetInformation : QueryLimited;
        }

        public static string Read(int pid, long started, string key)
        {
            try
            {
                using (var process = OpenChecked(pid, started, ReadAccess(key))) return ReadValue(process, key);
            }
            catch (EntryPointNotFoundException) { throw new NotSupportedException("This version of Windows does not support the selected setting."); }
        }

        private static string ReadValue(SafeProcessHandle process, string key)
        {
            uint value;
            switch (key)
            {
                case "cpu":
                    value = GetPriorityClass(process);
                    if (value == 0) throw Error(Marshal.GetLastWin32Error());
                    break;
                case "memory":
                    Check(GetProcessInformation(process, ProcessMemoryPriority, out value, sizeof(uint)));
                    break;
                case "io":
                    uint size;
                    CheckStatus(NtQueryInformationProcess(process, ProcessIoPriority, out value, sizeof(uint), out size));
                    break;
                case "gpupriority":
                    CheckStatus(D3DKMTGetProcessSchedulingPriorityClass(process, out value));
                    break;
                case "affinity":
                    ulong system;
                    return ReadMasks(process, out system).ToString("X", Invariant);
                case "cpusets":
                    uint[] selected = SelectedCpuSets(process);
                    return selected.Length == 0 ? "default" : String.Join(",", selected.OrderBy(x => x).Select(x => x.ToString(Invariant)));
                default: throw new ArgumentException("Unknown process setting.");
            }
            return value.ToString(Invariant);
        }

        public static void Apply(int pid, long started, string key, string value)
        {
            string canonical = Normalize(key, value);
            uint rights = key == "cpusets" ? SetLimited : SetInformation;
            try
            {
                using (var process = OpenChecked(pid, started, rights | ReadAccess(key)))
                {
                    uint number;
                    switch (key)
                    {
                        case "cpu":
                            Check(SetPriorityClass(process, UInt32.Parse(canonical, Invariant)));
                            break;
                        case "memory":
                            number = UInt32.Parse(canonical, Invariant);
                            Check(SetProcessInformation(process, ProcessMemoryPriority, ref number, sizeof(uint)));
                            break;
                        case "io":
                            number = UInt32.Parse(canonical, Invariant);
                            CheckStatus(NtSetInformationProcess(process, ProcessIoPriority, ref number, sizeof(uint)));
                            break;
                        case "gpupriority":
                            CheckStatus(D3DKMTSetProcessSchedulingPriorityClass(process, UInt32.Parse(canonical, Invariant)));
                            break;
                        case "affinity":
                            ulong system;
                            ReadMasks(process, out system);
                            ulong mask = UInt64.Parse(canonical, NumberStyles.HexNumber, Invariant);
                            if ((mask & ~system) != 0) throw new ArgumentException("The mask includes an unavailable logical processor.");
                            Check(SetProcessAffinityMask(process, new UIntPtr(mask)));
                            break;
                        case "cpusets":
                            uint[] ids = canonical == "default" ? null : canonical.Split(',').Select(s => UInt32.Parse(s, Invariant)).ToArray();
                            if (ids != null)
                            {
                                var available = new HashSet<uint>(Topology(process).Where(item => item.Available).Select(item => item.Id));
                                if (ids.Any(id => !available.Contains(id)))
                                    throw new ArgumentException("A selected CPU set is missing or reserved for another process.");
                            }
                            Check(SetProcessDefaultCpuSets(process, ids, ids == null ? 0u : (uint)ids.Length));
                            break;
                    }
                    string actual = ReadValue(process, key);
                    if (actual != canonical)
                        throw new InvalidOperationException("Windows kept the setting at " + Display(key, actual) + ".");
                }
            }
            catch (EntryPointNotFoundException) { throw new NotSupportedException("This version of Windows does not support the selected setting."); }
        }

        private static ulong ReadMasks(SafeProcessHandle process, out ulong system)
        {
            if (GetActiveProcessorGroupCount() > 1)
                throw new NotSupportedException("This PC has multiple processor groups. Use CPU Sets to select processors across groups.");
            UIntPtr processMask, systemMask;
            Check(GetProcessAffinityMask(process, out processMask, out systemMask));
            system = systemMask.ToUInt64();
            if (system == 0 || processMask == UIntPtr.Zero) throw new InvalidOperationException("Windows could not read the CPU affinity mask.");
            return processMask.ToUInt64();
        }

        public static ulong AffinityLimit(int pid, long started)
        {
            using (var process = OpenChecked(pid, started, QueryLimited))
            {
                ulong allowed;
                ReadMasks(process, out allowed);
                return allowed;
            }
        }

        public static string AffinityInfo(int pid, long started)
        {
            ulong mask = AffinityLimit(pid, started);
            var indices = Enumerable.Range(0, 64).Where(index => (mask & (1UL << index)) != 0);
            return "Available logical processors: " + String.Join(", ", indices) + ". Full mask: 0x" + mask.ToString("X", Invariant) + ".";
        }

        private static uint[] SelectedCpuSets(SafeProcessHandle process)
        {
            uint[] ids = null;
            for (int pass = 0; pass < 5; pass++)
            {
                uint needed;
                if (GetProcessDefaultCpuSets(process, ids, ids == null ? 0u : (uint)ids.Length, out needed))
                    return needed == 0 ? new uint[0] : ids.Take((int)needed).ToArray();
                int code = Marshal.GetLastWin32Error();
                if (code != InsufficientBuffer) throw Error(code);
                ids = new uint[checked((int)needed)];
            }
            throw new InvalidOperationException("The CPU set selection changed while it was being read. Try again.");
        }

        internal sealed class CpuSet
        {
            public uint Id;
            public int Group, Index, Core, CacheIndex, Efficiency;
            public uint L3Bytes;
            public bool Available;
        }

        private static List<CpuSet> Topology(SafeProcessHandle process)
        {
            byte[] data = null;
            for (int pass = 0; pass < 5; pass++)
            {
                uint needed;
                if (GetSystemCpuSetInformation(data, data == null ? 0u : (uint)data.Length, out needed, process, 0))
                {
                    var sets = new List<CpuSet>();
                    int limit = checked((int)needed);
                    for (int pos = 0; pos < limit;)
                    {
                        if (limit - pos < 8) throw new InvalidOperationException("Windows returned incomplete processor information.");
                        int size = BitConverter.ToInt32(data, pos);
                        if (size < 8 || size > limit - pos) throw new InvalidOperationException("Windows returned invalid processor information.");
                        if (BitConverter.ToInt32(data, pos + 4) == 0 && size >= 20)
                        {
                            byte flags = data[pos + 19];
                            sets.Add(new CpuSet { Id = BitConverter.ToUInt32(data, pos + 8), Group = BitConverter.ToUInt16(data, pos + 12),
                                Index = data[pos + 14], Core = data[pos + 15], CacheIndex = data[pos + 16],
                                Efficiency = data[pos + 18], Available = (flags & 2) == 0 || (flags & 4) != 0 });
                        }
                        pos += size;
                    }
                    return sets;
                }
                int code = Marshal.GetLastWin32Error();
                if (code != InsufficientBuffer) throw Error(code);
                data = new byte[checked((int)needed)];
            }
            throw new InvalidOperationException("The processor list changed while it was being read. Try again.");
        }

        public static Dictionary<uint,string> AvailableCpuSets(int pid, long started)
        {
            using (var process = OpenChecked(pid, started, QueryLimited))
                return Topology(process).Where(s => s.Available).ToDictionary(s => s.Id,
                    s => "G" + s.Group + " · CPU " + s.Index + "  (core " + s.Core + ")");
        }

        public static string CpuSetInfo(int pid, long started)
        {
            return String.Join(Environment.NewLine, AvailableCpuSets(pid, started).Select(s => s.Key + ": " + s.Value));
        }

        internal static List<CpuSet> CpuSetTopology(int pid, long started)
        {
            if (pid == 0 && started == 0)
            {
                // A saved rule can be edited while its executable is closed.
                using (var self = System.Diagnostics.Process.GetCurrentProcess())
                {
                    pid = self.Id;
                    started = self.StartTime.ToFileTimeUtc();
                }
            }
            using (var process = OpenChecked(pid, started, QueryLimited))
            {
                var sets = Topology(process);
                AddL3Sizes(sets);
                return sets;
            }
        }

        // The CPU-set API provides the shared-cache index. A cache relationship supplies its capacity.
        private static void AddL3Sizes(List<CpuSet> sets)
        {
            const int relationCache = 2;
            uint length = 0;
            if (GetLogicalProcessorInformationEx(relationCache, null, ref length) ||
                Marshal.GetLastWin32Error() != InsufficientBuffer || length < 8 || length > Int32.MaxValue) return;
            var data = new byte[(int)length];
            if (!GetLogicalProcessorInformationEx(relationCache, data, ref length)) return;
            int stride = IntPtr.Size + 8;
            for (int at = 0; at <= (int)length - 8;)
            {
                int size = BitConverter.ToInt32(data, at + 4);
                if (size < 8 || size > (int)length - at) return;
                if (size >= 40 + stride && BitConverter.ToInt32(data, at) == relationCache &&
                    data[at + 8] == 3 && BitConverter.ToInt32(data, at + 16) == 0) // CacheUnified.
                {
                    uint bytes = BitConverter.ToUInt32(data, at + 12);
                    // Prior to the group-array version, this field was reserved and a single mask followed.
                    int groups = Math.Max(1, (int)BitConverter.ToUInt16(data, at + 38));
                    if (groups <= (size - 40) / stride)
                    {
                        for (int n = 0; n < groups; n++)
                        {
                            int pos = at + 40 + n * stride;
                            ulong mask = IntPtr.Size == 8 ? BitConverter.ToUInt64(data, pos) : BitConverter.ToUInt32(data, pos);
                            int group = BitConverter.ToUInt16(data, pos + IntPtr.Size);
                            foreach (var cpu in sets)
                                if (cpu.Group == group && cpu.Index < 64 && (mask & (1UL << cpu.Index)) != 0)
                                    cpu.L3Bytes = bytes;
                        }
                    }
                }
                at += size;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetPriorityClass(SafeProcessHandle process, uint value);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(SafeProcessHandle process, int kind, out uint value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(SafeProcessHandle process, int kind, ref uint value, uint size);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessAffinityMask(SafeProcessHandle process, out UIntPtr selected, out UIntPtr system);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessAffinityMask(SafeProcessHandle process, UIntPtr selected);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDefaultCpuSets(SafeProcessHandle process, [Out] uint[] ids, uint length, out uint needed);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[] ids, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation([Out] byte[] data, uint length, out uint needed, SafeProcessHandle process, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, [Out] byte[] buffer, ref uint length);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, out uint value, uint size, out uint returned);
        [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(SafeProcessHandle process, int kind, ref uint value, uint size);
        [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
        [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(SafeProcessHandle process, out uint value);
        [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(SafeProcessHandle process, uint value);
    }
}
