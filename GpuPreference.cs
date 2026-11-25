using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Procesi
{
    // Windows Graphics Settings stores a per-user preference per executable.
    // Its SpecificAdapter identifier is distinct from the instance's DXGI LUID.
    internal static class GpuPreference
    {
        private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        private const string NamedPreference = "1073741824";
        private const int AdapterNotFound = unchecked((int)0x887A0002);
        private static readonly object RegistryAccess = new object();

        private static int Build()
        {
            const string version = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            int build;
            return Int32.TryParse(Convert.ToString(Registry.GetValue(version, "CurrentBuildNumber", null)), out build) ? build : 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
            public uint Vendor, Device, Subsystem, Revision;
            public UIntPtr DedicatedVideo, DedicatedSystem, SharedSystem;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;

            public string Key { get { return String.Format(CultureInfo.InvariantCulture, "{0:X4}&{1:X4}&{2:X}", Vendor, Device, Subsystem); } }
            public long Luid { get { return ((long)LuidHigh << 32) | LuidLow; } }
        }

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumerateAdapter(IntPtr factory, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);

        private static T ComMethod<T>(IntPtr instance, int slot) where T : class
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static List<AdapterDescription> Hardware()
        {
            Guid factoryIid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
            IntPtr factory;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryIid, out factory));
            try
            {
                var result = new List<AdapterDescription>();
                var seen = new HashSet<long>();
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1.
                var enumerate = ComMethod<EnumerateAdapter>(factory, 12);
                for (uint index = 0; ; index++)
                {
                    IntPtr adapter;
                    int hr = enumerate(factory, index, out adapter);
                    if (hr == AdapterNotFound) break;
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        AdapterDescription desc;
                        Marshal.ThrowExceptionForHR(ComMethod<GetDescription>(adapter, 10)(adapter, out desc));
                        if ((desc.Flags & 3) == 0 && desc.Vendor != 0 && desc.Device != 0 && seen.Add(desc.Luid))
                            result.Add(desc);
                    }
                    finally { Marshal.Release(adapter); }
                }
                return result;
            }
            finally { Marshal.Release(factory); }
        }

        public static Dictionary<string, string> Choices()
        {
            var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            choices.Add("default", "Let Windows decide");
            int build = Build();
            if (build < 17134) throw new NotSupportedException("This version of Windows does not support per-app GPU preferences.");
            bool useSystemChoices = build < 22000;
            if (!useSystemChoices)
            {
                List<AdapterDescription> hardware;
                try { hardware = Hardware(); }
                catch (COMException) { hardware = new List<AdapterDescription>(); }
                catch (EntryPointNotFoundException) { hardware = new List<AdapterDescription>(); }
                foreach (var group in hardware.GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // The registry cannot distinguish two physical adapters with the same PCI triplet.
                    if (group.Count() != 1) { useSystemChoices = true; continue; }
                    var card = group.First();
                    string name = card.Name.Trim();
                    if (name.Length == 0 || hardware.Count(c => String.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) > 1)
                        name = (name + " · " + card.Key).Trim();
                    choices.Add(card.Key, name);
                }
                if (choices.Count == 1) useSystemChoices = true;
            }
            if (useSystemChoices)
            {
                choices.Add("1", "Power saving GPU (Windows selection)");
                choices.Add("2", "High performance GPU (Windows selection)");
            }
            return choices;
        }

        private static string ExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !(path.StartsWith(@"\\", StringComparison.Ordinal)
                || (path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')))
                || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choosing a GPU requires the full path to the application’s .exe file.");
            return Path.GetFullPath(path);
        }

        private static string ReadRaw(RegistryKey key, string path)
        {
            if (key == null) return null;
            object value = key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return null;
            if (key.GetValueKind(path) != RegistryValueKind.String)
                throw new InvalidOperationException("The existing graphics preference has an unrecognized registry type.");
            return (string)value;
        }

        private static string GpuToken(string token)
        {
            int equals = token.IndexOf('=');
            if (equals < 0) return null;
            string name = token.Substring(0, equals).Trim();
            if (String.Equals(name, "GpuPreference", StringComparison.OrdinalIgnoreCase)) return "preference";
            return String.Equals(name, "SpecificAdapter", StringComparison.OrdinalIgnoreCase) ? "adapter" : null;
        }

        private static string Decode(string raw)
        {
            string preference = null, adapter = null;
            foreach (var token in (raw ?? "").Split(';'))
            {
                var kind = GpuToken(token);
                if (kind == null) continue;
                string value = token.Substring(token.IndexOf('=') + 1).Trim();
                if (kind == "adapter") adapter = value;
                else preference = value;
            }
            if (preference == NamedPreference && !String.IsNullOrEmpty(adapter)) return adapter.ToUpperInvariant();
            if (String.IsNullOrEmpty(preference) || preference == "0") return "default";
            return preference;
        }

        public static string Read(string path)
        {
            string target = ExecutablePath(path);
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath)) return Decode(ReadRaw(key, target));
        }

        private static string Encode(string original, string choice)
        {
            var result = new StringBuilder();
            string[] tokens = (original ?? "").Split(';');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (GpuToken(tokens[i]) != null) continue;
                result.Append(tokens[i]);
                if (i < tokens.Length - 1) result.Append(';');
            }
            if (!result.ToString().Any(c => c != ';' && !Char.IsWhiteSpace(c))) result.Clear();
            if (choice == "default") return result.ToString();
            if (result.Length > 0 && result[result.Length - 1] != ';') result.Append(';');
            if (choice == "1" || choice == "2") result.Append("GpuPreference=").Append(choice).Append(';');
            else result.Append("SpecificAdapter=").Append(choice).Append(";GpuPreference=").Append(NamedPreference).Append(';');
            return result.ToString();
        }

        public static void Set(string path, string key)
        {
            string target = ExecutablePath(path);
            if (String.IsNullOrEmpty(key)) throw new ArgumentException("Choose a graphics card.");
            if (key != "default")
            {
                if (!Choices().ContainsKey(key))
                    throw new InvalidOperationException("Windows can no longer identify the selected graphics card.");
                key = key.ToUpperInvariant();
            }
            lock (RegistryAccess)
            {
                using (var reg = key == "default" ? Registry.CurrentUser.OpenSubKey(RegistryPath, true) : Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (reg == null) return;
                    string original = ReadRaw(reg, target);
                    string updated = Encode(original, key);
                    if (updated.Length == 0)
                    {
                        if (original != null) reg.DeleteValue(target, false);
                    }
                    else if (!String.Equals(original, updated, StringComparison.Ordinal))
                        reg.SetValue(target, updated, RegistryValueKind.String);
                    if (Decode(ReadRaw(reg, target)) != key)
                        throw new InvalidOperationException("The GPU preference could not be verified after saving.");
                }
            }
        }
    }
}
