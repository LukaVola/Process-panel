using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Settings = Procesi.Controls;

namespace Procesi
{
    public partial class MainForm
    {
        private static readonly string[] Keys = { "cpu", "affinity", "cpusets", "io", "memory", "gpupriority" };
        private const string RuleHeader = "ProcesniPanel-Rules/1";

        private void MenuAction(Action action)
        {
            try { action(); }
            catch (Exception e) { DarkTheme.Message(this, e.Message, "Process Panel", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void AddAction(ToolStripItemCollection items, string text, bool enabled, Action action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Enabled = enabled;
            item.Click += delegate { MenuAction(action); };
            items.Add(item);
        }

        internal ContextMenuStrip BuildProcessMenu(ProcessRow row)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Font = SystemFonts.MenuFont;
            menu.Items.Add(new ToolStripMenuItem(row.Name.Replace("&","&&") + "   ·   " + row.Pid) { Enabled=false });
            menu.Items.Add(new ToolStripSeparator());
            bool known = row.Started > 0;
            bool pathKnown = !String.IsNullOrEmpty(row.Path);
            Dictionary<string,string> saved = rules.EffectiveValues(row.Path, row.Name);
            foreach (string key in Keys)
            {
                string local = key;
                if (key=="cpusets")
                {
                    var sets=new ToolStripMenuItem("CPU Sets");
                    AddAction(sets.DropDownItems,"Current…",known,delegate { EditCpuSets(row,false); });
                    AddAction(sets.DropDownItems,"Always…",pathKnown || saved.ContainsKey(key),delegate { EditCpuSets(row,true); });
                    if (saved.ContainsKey(key))
                        AddAction(sets.DropDownItems,"Remove Persistent Rule",true,delegate { RemoveSetting(row,"cpusets"); });
                    menu.Items.Add(sets);
                }
                else AddAction(menu.Items, Settings.Label(key) + "…", known, delegate { EditSetting(row, local); });
            }
            AddAction(menu.Items, "GPU Preference…", pathKnown, delegate { EditGpu(row); });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem ruleMenu = new ToolStripMenuItem("Persistent Rules");
            AddAction(ruleMenu.DropDownItems, "Copy Rules", saved.Count > 0, delegate {
                var output = new List<string> { RuleHeader };
                foreach (var kv in saved) output.Add(kv.Key + "=" + kv.Value);
                Clipboard.SetText(String.Join(Environment.NewLine, output));
                SetStatus("Rules copied.");
            });
            AddAction(ruleMenu.DropDownItems, "Paste Rules…", pathKnown, delegate {
                string[] lines = Clipboard.GetText().Trim().Split(new [] {"\r\n","\n"}, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2 || lines[0] != RuleHeader || lines.Length > Keys.Length + 1)
                    throw new ArgumentException("The clipboard does not contain Process Panel rules.");
                var changes = new Dictionary<string,string>();
                foreach (string line in lines.Skip(1))
                {
                    int pos = line.IndexOf('=');
                    if (pos <= 0 || !Keys.Contains(line.Substring(0,pos))) throw new ArgumentException("The copied rule is invalid.");
                    string key = line.Substring(0,pos);
                    changes.Add(key, CpuSetRule.NormalizeStored(key, line.Substring(pos+1)));
                }
                string summary = String.Join(Environment.NewLine, changes.Select(x => Settings.Label(x.Key) + "  ·  " + Settings.Display(x.Key,x.Value)));
                if (DarkTheme.Message(this, "Save these rules for this application path?\n" + row.Path + "\n\n" + summary,
                    "Paste Rules", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                rules.Merge(row.Path, changes); SetStatus("Rules saved for " + row.Name + "."); RefreshNow();
            });
            if (saved.Count > 0)
            {
                ruleMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (string key in saved.Keys)
                {
                    string local = key;
                    AddAction(ruleMenu.DropDownItems, "Remove · " + Settings.Label(key), true, delegate {
                        RemoveSetting(row,local);
                    });
                }
            }
            menu.Items.Add(ruleMenu);
            AddAction(menu.Items, "Open File Location", pathKnown, delegate {
                if (!File.Exists(row.Path)) throw new FileNotFoundException("Windows could not find the selected file.");
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + row.Path + "\"");
            });
            AddAction(menu.Items, "Copy File Path", pathKnown, delegate { Clipboard.SetText(row.Path); SetStatus("File path copied."); });
            menu.Items.Add(new ToolStripSeparator());
            bool closable = known && row.Pid != Program.MyPid && row.Pid > 4;
            AddAction(menu.Items, "Close", closable, delegate { ProcessActions.CloseWindow(row); SetStatus("The application was asked to close."); });
            AddAction(menu.Items, "Terminate…", closable, delegate {
                if (DarkTheme.Message(this, "Terminate " + row.Name + " (PID " + row.Pid + ")? Unsaved work may be lost.",
                    "Terminate Process", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                ProcessActions.Terminate(row); SetStatus("Windows received the request to terminate the process."); RefreshNow();
            });
            DarkTheme.StyleMenu(menu);
            return menu;
        }

        internal void ShowProcessSettings(ProcessRow row, Control anchor)
        {
            ContextMenuStrip menu = BuildProcessMenu(row);
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        internal void EditSavedCpuSets(string path)
        {
            MenuAction(delegate {
                if (String.IsNullOrEmpty(path))
                {
                    using (var chooser=new OpenFileDialog { Title="Select an Application for a Persistent CPU Sets Rule", Filter="Windows applications (*.exe)|*.exe", CheckFileExists=true })
                    {
                        if (chooser.ShowDialog(this)!=DialogResult.OK) return;
                        path=chooser.FileName;
                    }
                }
                EditCpuSets(new ProcessRow {Path=path,Name=Path.GetFileName(path)},true);
            });
        }

        private void EditCpuSets(ProcessRow row,bool permanent)
        {
            string current="default", saved;
            var values = row.Started > 0 ? rules.EffectiveValues(row.Path,row.Name) : rules.Values(row.Path);
            values.TryGetValue("cpusets",out saved);
            ProcessRow target=row;
            if (row.Started>0)
            {
                try { current=Settings.Read(row.Pid,row.Started,"cpusets"); }
                catch (Exception)
                {
                    if (!permanent) throw;
                    target=new ProcessRow {Path=row.Path,Name=row.Name};
                }
            }
            string choice=CpuPicker.Edit(this,target,current,saved,permanent);
            if (choice==null) return;
            if (permanent) SaveAndApply(row,"cpusets",choice);
            else
            {
                rules.ApplyOnce(row.Pid,row.Started,row.Path,row.Name,"cpusets",choice);
                SetStatus("CPU Sets applied to " + row.Name + "."); RefreshNow();
            }
        }

        private void RemoveSetting(ProcessRow row,string key)
        {
            string target=rules.TargetForSetting(row.Path,row.Name,key);
            rules.Remove(target,key);
            SetStatus(Settings.Label(key) + " rule removed for " + target + ".");
            RefreshNow();
        }

        private void SaveAndApply(ProcessRow row,string key,string value)
        {
            string target=row.Started > 0 ? rules.TargetForSetting(row.Path,row.Name,key) : row.Path;
            rules.Set(target,key,value);
            if (row.Started>0)
            {
                try { rules.ApplyOnce(row.Pid,row.Started,row.Path,row.Name,key,value); }
                catch (Exception e)
                {
                    RefreshNow();
                    DarkTheme.Message(this,"The persistent rule was saved. It could not be applied to the running process.\n\n"+e.Message,
                        "Persistent Rule",MessageBoxButtons.OK,MessageBoxIcon.Information);
                    return;
                }
            }
            SetStatus("Persistent rule saved for "+row.Name+"."); RefreshNow();
        }

        private void EditSetting(ProcessRow row, string key)
        {
            string current = Settings.Read(row.Pid,row.Started,key);
            string saved;
            rules.EffectiveValues(row.Path,row.Name).TryGetValue(key,out saved);
            bool multi = key == "affinity";
            using (var editor = new SettingDialog(Settings.Label(key), row, multi))
            {
                if (multi)
                {
                    List<KeyValuePair<string,string>> choices = new List<KeyValuePair<string,string>>();
                    List<string> selected = new List<string>();
                    ulong mask = UInt64.Parse(current, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    ulong available = Settings.AffinityLimit(row.Pid,row.Started);
                    for (int i=0; i<64; i++)
                    {
                        ulong bit = 1UL << i;
                        if ((available & bit) == 0) continue;
                        string value = bit.ToString("X",CultureInfo.InvariantCulture);
                        choices.Add(new KeyValuePair<string,string>(value,"CPU " + i));
                        if ((mask & bit) != 0) selected.Add(value);
                    }
                    editor.SetMultiple(choices,selected,"Select the logical processors this process can run on.");
                }
                else
                {
                    var choices = Settings.Choices(key);
                    editor.SetChoices(choices.Select(x => new KeyValuePair<string,string>(x,Settings.Display(key,x))),current);
                    if (!choices.Contains(current)) editor.SetNote("Current setting: " + Settings.Display(key,current));
                }
                string target = rules.TargetForSetting(row.Path,row.Name,key);
                string savedLabel = saved == null ? null : Settings.Display(key,saved);
                if (savedLabel != null && !Path.IsPathRooted(target)) savedLabel += " for " + target + " in any folder";
                editor.SetRuleOption(!String.IsNullOrEmpty(row.Path) || saved != null, savedLabel);
                editor.Commit = delegate {
                    string value;
                    if (key == "affinity")
                    {
                        ulong mask = 0;
                        foreach (string part in editor.SelectedMany) mask |= UInt64.Parse(part,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                        if (mask == 0) throw new ArgumentException("Select at least one logical processor.");
                        value = mask.ToString("X",CultureInfo.InvariantCulture);
                    }
                    else value = editor.Selected;
                    if ((key == "cpu" && value == "256") || (key == "gpupriority" && value == "5"))
                        if (DarkTheme.Message(editor,"Real-time priority may make the system unresponsive. Apply it to " + row.Name + "?",
                            "Real-time Priority",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                    if (editor.Remember) SaveAndApply(row,key,value);
                    else
                    {
                        rules.ApplyOnce(row.Pid,row.Started,row.Path,row.Name,key,value);
                        SetStatus(Settings.Label(key) + " · " + Settings.Display(key,value) + " · " + row.Name);
                        RefreshNow();
                    }
                    return true;
                };
                editor.ShowDialog(this);
            }
        }

        private void EditGpu(ProcessRow row)
        {
            var choices = GpuPreference.Choices();
            string current = GpuPreference.Read(row.Path);
            if (!choices.ContainsKey(current)) choices.Add(current,"Current Windows preference");
            using (var editor = new SettingDialog("GPU Preference",row,false))
            {
                editor.SetChoices(choices,current);
                editor.SetNote("Windows saves this preference for the application. Restart the application to use the new setting.");
                editor.Commit = delegate {
                    if (editor.Selected != current) GpuPreference.Set(row.Path,editor.Selected);
                    SetStatus("GPU preference saved for " + row.Name + "."); return true;
                };
                editor.ShowDialog(this);
            }
        }
    }
}
