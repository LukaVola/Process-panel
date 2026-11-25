using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Procesi
{
    // Calls are serialized by ProcessSampler. The wildcard keeps one query open across UI samples.
    internal static class GpuSampler
    {
        private static readonly WildcardCounter Counter =
            new WildcardCounter(@"\GPU Engine(*)\Utilization Percentage");

        public static void Pause() { Counter.Pause(); }

        public static void Fill(ProcessSnapshot snapshot)
        {
            snapshot.Gpu = double.NaN;
            foreach (var row in snapshot.Rows) row.Gpu = double.NaN;
            long since;
            int count = Counter.Read(out since);
            if (count == 0) return;
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var pidEngines = new Dictionary<int, Dictionary<string, double>>();
            bool haveData = false;
            for (int i = 0; i < count; i++)
            {
                var item = Counter.ItemAt(i);
                if (!item.HasValue || item.Name == IntPtr.Zero || (item.Value == 0 && haveData)) continue;
                string name = Marshal.PtrToStringUni(item.Name);
                if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                int split = name.IndexOf("_luid_", 4, StringComparison.OrdinalIgnoreCase);
                int pid;
                if (split <= 4 || !int.TryParse(name.Substring(4, split - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
                    continue;
                haveData = true;
                if (item.Value == 0) continue;
                int duplicate = name.IndexOf('#', split);
                string engine = duplicate < 0 ? name.Substring(split) : name.Substring(split, duplicate - split);
                double total;
                engineTotals.TryGetValue(engine, out total);
                engineTotals[engine] = total + item.Value;
                Dictionary<string, double> engines;
                if (!pidEngines.TryGetValue(pid, out engines))
                    pidEngines.Add(pid, engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
                engines.TryGetValue(engine, out total);
                engines[engine] = total + item.Value;
            }
            if (!haveData) return;
            snapshot.Gpu = Busiest(engineTotals);
            foreach (var row in snapshot.Rows)
            {
                // An instance name contains only the PID. Don't attribute a previous owner's sample to a new PID owner.
                if (row.Started > since) continue;
                Dictionary<string, double> engines;
                row.Gpu = pidEngines.TryGetValue(row.Pid, out engines) ? Busiest(engines) : 0;
            }
        }

        private static double Busiest(Dictionary<string, double> engines)
        {
            double busiest = 0;
            foreach (double value in engines.Values) busiest = Math.Max(busiest, value);
            return Math.Min(100, busiest);
        }
    }
}
