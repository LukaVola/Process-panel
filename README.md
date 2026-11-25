# Process Panel

Process Panel is a small Windows process monitor with a live view of CPU, GPU and memory usage. The **Active Processes** tab filters processes by a selectable CPU threshold. The CPU meter shows current activity on each logical processor.

## Run

On 64-bit Windows 10 or 11 with .NET Framework 4.8 or later, open `ProcessPanel.exe` from the release. Right-click a process to change its CPU affinity, CPU sets, CPU priority, I/O priority, memory priority or GPU priority. GPU preferences depend on Windows and the installed graphics drivers. Windows permissions determine which processes you can change.

## Persistent rules

Use a process's settings to save a rule for later launches. **Persistent Rules** lists saved rules. They also apply while Process Panel is running in the system tray. Sign-in startup is optional. Windows continues to manage the current power plan.

On a new profile the rules list starts empty. Process Panel stores scheduling rules in `%LOCALAPPDATA%\ProcesniPanel\rules.xml`. Windows stores per-application GPU selection in the current user's graphics preferences.

## Build

Run the following in PowerShell from the repository folder. The build uses the 64-bit .NET Framework C# compiler installed with Windows.

```powershell
.\Build.ps1
.\ProcessPanel.exe
```
