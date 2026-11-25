using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Procesi
{
    internal static class AffinityDisplay
    {
        private static readonly ulong[] NoDies = new ulong[0];
        private static readonly Lazy<ulong[]> Dies = new Lazy<ulong[]>(ReadDies);

        internal static string Format(string hexMask)
        {
            ulong mask;
            if (!UInt64.TryParse(hexMask, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask) || mask == 0)
                return "0x" + hexMask;
            return Format(mask, Dies.Value);
        }

        internal static string Format(ulong mask, ulong[] dies)
        {
            var whole = new List<string>();
            ulong covered = 0;
            for (int i = 0; i < dies.Length; i++)
            {
                string name = "CCD" + i.ToString(CultureInfo.InvariantCulture);
                if (mask == dies[i]) return name;
                if (mask != 0 && (mask & ~dies[i]) == 0) return name + " (" + CpuRanges(mask) + ")";
                if ((mask & dies[i]) == dies[i])
                {
                    whole.Add(name);
                    covered |= dies[i];
                }
            }
            if (whole.Count > 0 && covered == mask) return String.Join(" + ", whole);
            return CpuRanges(mask) + " (0x" + mask.ToString("X", CultureInfo.InvariantCulture) + ")";
        }

        private static string CpuRanges(ulong mask)
        {
            var ranges = new List<string>();
            for (int cpu = 0; cpu < 64; cpu++)
            {
                if ((mask & (1UL << cpu)) == 0) continue;
                int start = cpu;
                while (cpu < 63 && (mask & (1UL << (cpu + 1))) != 0) cpu++;
                string range = start.ToString(CultureInfo.InvariantCulture);
                if (cpu != start) range += "–" + cpu.ToString(CultureInfo.InvariantCulture);
                ranges.Add(range);
            }
            return "CPU " + String.Join(", ", ranges);
        }

        private static ulong[] ReadDies()
        {
            // A last-level cache may be shared by only part of a CCD. Use the
            // Windows die relationships themselves and fall back if they are absent.
            try
            {
                const string cpu = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
                if (!Object.Equals(Registry.GetValue(cpu, "VendorIdentifier", null), "AuthenticAMD")) return NoDies;
            }
            catch (SecurityException) { return NoDies; }
            catch (UnauthorizedAccessException) { return NoDies; }
            catch (IOException) { return NoDies; }

            // Some Windows builds omit die records from RelationAll, even when
            // the more specific RelationProcessorDie query succeeds.
            var packages = ReadRelation(3);
            var dies = ReadRelation(5);
            if (packages == null || dies == null) return NoDies;
            byte[] topology = packages.Concat(dies).ToArray();
            return ParseDies(topology, topology.Length);
        }

        private static byte[] ReadRelation(int relation)
        {
            const int insufficientBuffer = 122;
            uint size = 0;
            if (GetLogicalProcessorInformationEx(relation, null, ref size) ||
                Marshal.GetLastWin32Error() != insufficientBuffer || size == 0 || size > Int32.MaxValue) return null;
            var data = new byte[(int)size];
            if (!GetLogicalProcessorInformationEx(relation, data, ref size) || size != data.Length) return null;
            return data;
        }

        internal static ulong[] ParseDies(byte[] data, int length)
        {
            const int package = 3, die = 5;
            ulong packageMask = 0, covered = 0;
            var masks = new List<ulong>();
            if (length < 0 || length > data.Length) return NoDies;
            for (int offset = 0; offset < length;)
            {
                if (length - offset < 8) return NoDies;
                int size = BitConverter.ToInt32(data, offset + 4);
                if (size < 8 || size > length - offset) return NoDies;
                int relation = BitConverter.ToInt32(data, offset);
                if (relation == package || relation == die)
                {
                    // The x64 PROCESSOR_RELATIONSHIP has its group count at offset
                    // 30 of the enclosing record and its first GROUP_AFFINITY at 32.
                    if (size < 48 || BitConverter.ToUInt16(data, offset + 30) != 1 ||
                        BitConverter.ToUInt16(data, offset + 40) != 0) return NoDies;
                    ulong mask = BitConverter.ToUInt64(data, offset + 32);
                    if (mask == 0) return NoDies;
                    if (relation == package)
                    {
                        if (packageMask != 0) return NoDies;
                        packageMask = mask;
                    }
                    else
                    {
                        if ((covered & mask) != 0) return NoDies;
                        covered |= mask;
                        masks.Add(mask);
                    }
                }
                offset += size;
            }
            if (masks.Count < 2 || covered != packageMask) return NoDies;
            // Number dies by their first Windows logical processor, including
            // topologies with interleaved or unequal die masks.
            return masks.OrderBy(mask => mask & (~mask + 1)).ToArray();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relation, [Out] byte[] buffer, ref uint bytes);
    }
}
