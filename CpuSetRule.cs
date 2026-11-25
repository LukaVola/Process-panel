using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Procesi
{
    // Store the same group and logical CPU numbers that the user selected in the editor.
    // Translate them back to Windows CPU Set IDs against the target process at apply time.
    internal static class CpuSetRule
    {
        private const string Prefix="loc:";
        private static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        public static bool IsLocation(string value) { return value!=null && value.StartsWith(Prefix,StringComparison.Ordinal); }
        private static int Location(Controls.CpuSet cpu) { return cpu.Group*64+cpu.Index; }
        private static string Encode(int n) { return (n/64).ToString(Invariant)+"/"+(n%64).ToString(Invariant); }
        private static IEnumerable<int> Locations(string stored)
        {
            var result=new SortedSet<int>();
            foreach (string part in stored.Substring(Prefix.Length).Split(','))
            {
                var fields=part.Split('/'); ushort group; int index;
                if (fields.Length!=2 || !ushort.TryParse(fields[0],NumberStyles.None,Invariant,out group)
                    || !int.TryParse(fields[1],NumberStyles.None,Invariant,out index) || index<0 || index>=64)
                    throw new FormatException("Invalid processor group or logical processor number.");
                result.Add(group*64+index);
            }
            return result;
        }
        public static string NormalizeStored(string key,string value)
        {
            return key=="cpusets" && IsLocation(value) ? Prefix+String.Join(",",Locations(value).Select(Encode)) : Controls.Normalize(key,value);
        }
        public static string Store(string key,string value)
        {
            var normalized=NormalizeStored(key,value);
            if (key!="cpusets" || normalized=="default" || IsLocation(normalized)) return normalized;
            var topology=Controls.CpuSetTopology(0,0);
            var selected=new SortedSet<int>();
            foreach (string raw in normalized.Split(','))
            {
                uint id=uint.Parse(raw,Invariant);
                var cpu=topology.FirstOrDefault(c=>c.Id==id);
                if (cpu==null || topology.Count(c=>Location(c)==Location(cpu))!=1)
                    throw new InvalidOperationException("Windows could not match a selected CPU set to a unique processor. Reopen the CPU Sets dialog.");
                selected.Add(Location(cpu));
            }
            return Prefix+String.Join(",",selected.Select(Encode));
        }
        public static string ForPicker(string value,IList<Controls.CpuSet> topology,out int missing)
        {
            missing=0;
            if (!IsLocation(value)) return value;
            var selected=new SortedSet<uint>();
            var map=topology.ToLookup(Location);
            foreach(int location in Locations(value))
            {
                var matches=map[location].ToArray();
                if (matches.Length==1 && matches[0].Available) selected.Add(matches[0].Id);
                else missing++;
            }
            return selected.Count==0 ? "default" : String.Join(",",selected.Select(id=>id.ToString(Invariant)));
        }
        public static string Resolve(int pid,long started,string key,string value)
        {
            if (key!="cpusets" || !IsLocation(value)) return value;
            int missing;
            string resolved=ForPicker(value,Controls.CpuSetTopology(pid,started),out missing);
            if (missing>0) throw new InvalidOperationException("Some processors in the saved CPU set are unavailable. Review the CPU Sets rule.");
            return resolved;
        }
        public static string Display(string value)
        {
            return String.Join("  ·  ",Locations(value).GroupBy(n=>n/64).Select(g=>
                "Group "+g.Key+(g.Count()==1 ? " / CPU " : " / CPUs ")+String.Join(", ",g.Select(n=>(n%64).ToString(Invariant)))));
        }
    }
}
