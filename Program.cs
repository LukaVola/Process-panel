using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Process Panel")]
[assembly: AssemblyProduct("Process Panel")]
[assembly: AssemblyDescription("Monitor Windows processes and manage per-application rules.")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]
[assembly: AssemblyInformationalVersion("1.03")]

namespace Procesi
{
    internal static class LoginStart
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "ProcesniPanel";
        public static bool Enabled
        {
            get
            {
                using (var key=Registry.CurrentUser.OpenSubKey(Key))
                {
                    string command = key == null ? null : key.GetValue(Name) as string;
                    return !String.IsNullOrEmpty(command) && command.EndsWith(" --tray",StringComparison.OrdinalIgnoreCase);
                }
            }
            set
            {
                using (var key=Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) key.SetValue(Name,"\"" + Application.ExecutablePath + "\" --tray",RegistryValueKind.String);
                    else key.DeleteValue(Name,false);
                }
            }
        }
    }

    internal static class Program
    {
        internal static readonly int MyPid = Process.GetCurrentProcess().Id;
        [STAThread]
        private static void Main(string[] args)
        {
            string instanceName=@"Local\ProcesniPanel-" + WindowsIdentity.GetCurrent().User.Value;
            Run(args,instanceName,delegate { return new RuleStore(); });
        }

        internal static void Run(string[] args,string instanceName,Func<RuleStore> createRules)
        {
            var english = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = english;
            Thread.CurrentThread.CurrentUICulture = english;
            if (Array.Exists(args,s => String.Equals(s,"--exit",StringComparison.OrdinalIgnoreCase)))
            {
                try { using (var existing = EventWaitHandle.OpenExisting(instanceName+"-exit")) existing.Set(); }
                catch (WaitHandleCannotBeOpenedException) { /* No resident application. */ }
                return;
            }
            bool trayOnly=Array.Exists(args,s => String.Equals(s,"--tray",StringComparison.OrdinalIgnoreCase));
            bool acquired;
            using (var activate=new EventWaitHandle(false, EventResetMode.AutoReset, instanceName+"-activate"))
            using (var quit=new EventWaitHandle(false, EventResetMode.AutoReset, instanceName+"-exit"))
            using (var unique=new Mutex(true,instanceName,out acquired))
            {
                if (!acquired)
                {
                    if (!trayOnly) activate.Set();
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var rules=createRules();
                    using (var engine=new BackgroundRuleEngine(rules))
                    using (var context=new PanelContext(rules,trayOnly))
                    {
                        var wake=ThreadPool.RegisterWaitForSingleObject(activate,
                            delegate { context.PostCommand(false); },null,Timeout.Infinite,false);
                        var stop=ThreadPool.RegisterWaitForSingleObject(quit,
                            delegate { context.PostCommand(true); },null,Timeout.Infinite,false);
                        try { Application.Run(context); }
                        finally { wake.Unregister(null); stop.Unregister(null); }
                    }
                }
                finally { unique.ReleaseMutex(); }
            }
        }
    }

    internal static class WindowsError
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint FormatMessageW(uint flags, IntPtr source, uint code, uint language,
            StringBuilder buffer, uint length, IntPtr arguments);

        internal static Win32Exception FromCode(int code)
        {
            const uint fromSystemIgnoreInserts = 0x1200, englishUS = 0x0409;
            var buffer = new StringBuilder(1024);
            string number = code.ToString(CultureInfo.InvariantCulture);
            uint length = FormatMessageW(fromSystemIgnoreInserts, IntPtr.Zero, (uint)code, englishUS,
                buffer, (uint)buffer.Capacity, IntPtr.Zero);
            string message = length == 0 ? "Windows error " + number + "." :
                buffer.ToString().TrimEnd() + " (Windows error " + number + ")";
            return new Win32Exception(code, message);
        }
    }

    internal static class ProcessActions
    {
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool IsProcessCritical(ProcHandle h,out bool critical);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateProcess(ProcHandle h,uint code);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ProcessIdToSessionId(uint pid,out uint session);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool PostMessage(IntPtr win,uint msg,IntPtr wp,IntPtr lp);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
        private static ProcHandle Select(ProcessRow row,bool terminate)
        {
            if (row.Started <= 0 || row.Pid <= 4 || row.Pid == Program.MyPid)
                throw new InvalidOperationException("Process Panel cannot close this process.");
            var h = ProcessSampler.OpenProcess((uint)(terminate ? 0x1001 : 0x1000),false,row.Pid);
            try
            {
                if (h.IsInvalid) throw WindowsError.FromCode(Marshal.GetLastWin32Error());
                long start,exit,k,u;
                if (!ProcessSampler.GetProcessTimes(h,out start,out exit,out k,out u) || start != row.Started)
                    throw new InvalidOperationException("The selected process has exited or is no longer accessible.");
                uint mySession,targetSession;
                if (!ProcessIdToSessionId((uint)row.Pid,out targetSession) || !ProcessIdToSessionId((uint)Program.MyPid,out mySession) || targetSession != mySession)
                    throw new InvalidOperationException("Choose a process in your own Windows session.");
                bool critical;
                if (!IsProcessCritical(h,out critical) || critical) throw new InvalidOperationException("Process Panel could not confirm that this process is safe to close.");
                return h;
            }
            catch { h.Dispose(); throw; }
        }
        public static void Terminate(ProcessRow row)
        {
            using (ProcHandle h=Select(row,true))
                if (!TerminateProcess(h,1)) throw WindowsError.FromCode(Marshal.GetLastWin32Error());
        }
        public static void CloseWindow(ProcessRow row)
        {
            using (ProcHandle h=Select(row,false))
            using (Process p=Process.GetProcessById(row.Pid))
            {
                IntPtr win=p.MainWindowHandle;
                uint owner;
                if (win == IntPtr.Zero || GetWindowThreadProcessId(win,out owner) == 0 || owner != row.Pid)
                    throw new InvalidOperationException("This process has no main window to close.");
                if (!PostMessage(win,0x0010,IntPtr.Zero,IntPtr.Zero)) throw WindowsError.FromCode(Marshal.GetLastWin32Error());
            }
        }
    }
}
