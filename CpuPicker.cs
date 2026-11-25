using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CpuSet = Procesi.Controls.CpuSet;
using ProcessControls = Procesi.Controls;

namespace Procesi
{
    internal sealed class CpuPicker : Form
    {
        private static readonly Color WindowColor = DarkTheme.Field;
        private static readonly Color CardColor = DarkTheme.Surface;
        private static readonly Color BorderColor = DarkTheme.Edge;
        private static readonly Color TextColor = DarkTheme.Text;
        private static readonly Color MutedColor = DarkTheme.Muted;
        private static readonly Color SelectedColor = Color.FromArgb(113, 224, 154);
        private readonly List<CpuSet> topology;
        private readonly HashSet<uint> selection;
        private readonly int availableCount;
        private readonly FlowLayoutPanel cards = new FlowLayoutPanel();
        private readonly FlowLayoutPanel shortcuts = new FlowLayoutPanel();
        private readonly Label summary = new Label();
        private readonly ComboBox groups = new ComboBox();
        private readonly ToolTip hints = new ToolTip();
        private readonly List<KeyValuePair<CheckBox, CpuSet>> visibleChecks = new List<KeyValuePair<CheckBox, CpuSet>>();
        private bool updating;
        private int group;

        internal static string Edit(IWin32Window owner, ProcessRow row, string current, string saved, bool permanent)
        {
            var cpus = ProcessControls.CpuSetTopology(row.Pid, row.Started);
            int missing = 0;
            string initial = permanent && !String.IsNullOrEmpty(saved) ? saved : current;
            if (CpuSetRule.IsLocation(initial)) initial = CpuSetRule.ForPicker(initial, cpus, out missing);
            var selected = new HashSet<uint>();
            if (!String.IsNullOrEmpty(initial) && initial != "default")
            {
                var available = new HashSet<uint>(cpus.Where(c => c.Available).Select(c => c.Id));
                foreach (string raw in initial.Split(','))
                {
                    uint id;
                    if (UInt32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id) && available.Contains(id))
                        selected.Add(id);
                    else missing++;
                }
            }
            using (var form = new CpuPicker(row, permanent, cpus, selected, missing))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                return ProcessControls.Normalize("cpusets", String.Join(",", form.selection.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture))));
            }
        }

        private CpuPicker(ProcessRow row, bool permanent, List<CpuSet> cpus, HashSet<uint> initial, int missing)
        {
            topology = cpus;
            selection = initial;
            availableCount = topology.Count(c => c.Available);
            Text = "CPU Sets · " + (permanent ? "Always" : "Current");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = ShowIcon = MinimizeBox = MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96,96);
            Font = new Font("Segoe UI", 9.5f);
            BackColor = WindowColor; ForeColor = TextColor;
            ClientSize = new Size(720, 614);
            MinimumSize = new Size(500, 410);
            DarkTheme.ApplyWindow(this);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(16, 13, 16, 10) };
            for (int i = 0; i < 8; i++)
                layout.RowStyles.Add(i == 5 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var title = new Label { AutoSize = true, Text = row.Name, UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(2, 0, 0, 4), ForeColor = TextColor };
            layout.Controls.Add(title, 0, 0);
            string caption = permanent ? "Applies each time this application starts." : "Applies to this process (PID " + row.Pid + ").";
            layout.Controls.Add(Information(caption + " Windows prefers the selected logical processors."), 0, 1);

            var toolbar = Strip();
            var groupNumbers = topology.Select(c => c.Group).Distinct().OrderBy(g => g).ToList();
            if (groupNumbers.Count > 0) group = groupNumbers[0];
            if (groupNumbers.Count > 1)
            {
                groups.DropDownStyle = ComboBoxStyle.DropDownList;
                groups.FlatStyle = FlatStyle.Flat; groups.BackColor = CardColor; groups.ForeColor = TextColor;
                groups.AccessibleName = "Processor group"; groups.Width = 116;
                foreach (int number in groupNumbers) groups.Items.Add("Group " + number);
                groups.SelectedIndex = 0;
                groups.SelectedIndexChanged += delegate { group = groupNumbers[groups.SelectedIndex]; ShowGroup(); };
                toolbar.Controls.Add(groups);
            }
            toolbar.Controls.Add(Command(groupNumbers.Count > 1 ? "Select group" : "Select all", delegate { SetVisible(c => true); }));
            toolbar.Controls.Add(Command("Invert selection", delegate { SetVisible(c => !selection.Contains(c.Id)); }));
            if (topology.GroupBy(c => new {c.Group,c.Core}).Any(core => core.Count() > 1))
                toolbar.Controls.Add(Command("One thread per core", delegate {
                    var first = new HashSet<uint>(topology.Where(c => c.Group == group && c.Available)
                        .GroupBy(c => c.Core).Select(core => core.OrderBy(c => c.Index).First().Id));
                    SetVisible(c => first.Contains(c.Id));
                }));
            toolbar.Controls.Add(Command("Let Windows decide", delegate { selection.Clear(); ReflectSelection(); }));
            layout.Controls.Add(toolbar, 0, 2);

            shortcuts.AutoSize = true; shortcuts.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            shortcuts.Dock = DockStyle.Fill; shortcuts.Margin = new Padding(0); shortcuts.WrapContents = true;
            layout.Controls.Add(shortcuts, 0, 3);
            string note = missing == 0 ? "" : missing + (missing == 1 ? " previously selected CPU set is unavailable. " : " previously selected CPU sets are unavailable. ") + "Only the selection shown will be applied.";
            var warning = Information(note); warning.ForeColor = Color.FromArgb(242, 190, 117); warning.Visible = missing > 0;
            layout.Controls.Add(warning, 0, 4);
            cards.Dock = DockStyle.Fill; cards.AutoScroll = true; cards.WrapContents = true;
            cards.Margin = new Padding(0, 4, 0, 7); cards.BackColor = WindowColor;
            cards.SizeChanged += delegate { FitCards(); };
            DarkTheme.Scrollbars(cards);
            layout.Controls.Add(cards, 0, 5);

            summary.AutoSize = true; summary.Margin = new Padding(2, 2, 0, 7); summary.ForeColor = MutedColor;
            layout.Controls.Add(summary, 0, 6);
            var footer = Strip(); footer.FlowDirection = FlowDirection.RightToLeft;
            var ok = Command(permanent ? "Save rule" : "Apply", delegate { DialogResult = DialogResult.OK; });
            ok.BackColor = Color.FromArgb(47, 78, 59); ok.FlatAppearance.BorderColor = Color.FromArgb(105, 156, 119);
            var cancel = Command("Cancel", delegate { DialogResult = DialogResult.Cancel; });
            footer.Controls.Add(ok); footer.Controls.Add(cancel);
            layout.Controls.Add(footer, 0, 7); AcceptButton = ok; CancelButton = cancel;
            ShowGroup();
        }

        private static Label Information(string text)
        {
            return new Label { AutoSize = true, Text = text, UseMnemonic = false, ForeColor = MutedColor, Margin = new Padding(2, 0, 0, 7) };
        }

        private static FlowLayoutPanel Strip()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        }

        private static Button Command(string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(2, 2, 4, 3),
                ForeColor = TextColor, BackColor = CardColor, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = BorderColor;
            button.Click += delegate { action(); };
            return button;
        }

        private static void ClearAndDispose(Control host)
        {
            while (host.Controls.Count != 0) host.Controls[host.Controls.Count - 1].Dispose();
        }

        private void ShowGroup()
        {
            cards.SuspendLayout(); shortcuts.SuspendLayout();
            try
            {
                visibleChecks.Clear(); ClearAndDispose(cards); ClearAndDispose(shortcuts);
                var visible = topology.Where(c => c.Group == group).OrderBy(c => c.Index).ToList();
                var caches = visible.GroupBy(c => c.CacheIndex).OrderBy(set => set.Key).ToList();
                var presets = new Dictionary<string, Predicate<CpuSet>>();
                if (caches.Count > 1)
                    foreach (var cache in caches)
                    {
                        int index = cache.Key;
                        uint bytes = cache.First().L3Bytes;
                        int ordinal = caches.IndexOf(cache);
                        bool uniqueSize = bytes > 0 && caches.Count(g => g.First().L3Bytes == bytes) == 1;
                        string label = uniqueSize ? "L3" : (bytes > 0 ? "L3 #" : "LLC #") + ordinal;
                        if (bytes > 0) label += " · " + (bytes / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " MiB";
                        presets.Add(label, c => c.CacheIndex == index);
                    }
                var classes = visible.Select(c => c.Efficiency).Distinct().OrderBy(n => n).ToList();
                if (classes.Count > 1)
                    foreach (int number in classes)
                    {
                        int level = number;
                        string label = level == classes.Last() ? "Performance cores" : level == classes.First() ? "Efficiency cores" : "Efficiency class " + level;
                        presets.Add(label, c => c.Efficiency == level);
                    }
                if (presets.Count <= 4)
                    foreach (var preset in presets)
                    {
                        var match = preset.Value;
                        shortcuts.Controls.Add(Command(preset.Key, delegate { SetVisible(match); }));
                    }
                else
                {
                    var combo = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, FlatStyle=FlatStyle.Flat,
                        Width=210, BackColor=CardColor, ForeColor=TextColor, AccessibleName="Processor presets" };
                    combo.Items.Add("Processor presets…");
                    combo.Items.AddRange(presets.Keys.Cast<object>().ToArray()); combo.SelectedIndex = 0;
                    combo.SelectionChangeCommitted += delegate {
                        if (combo.SelectedIndex > 0) { SetVisible(presets[(string)combo.SelectedItem]); combo.SelectedIndex = 0; }
                    };
                    shortcuts.Controls.Add(combo);
                }
                int coreNumber = 0;
                foreach (var core in visible.GroupBy(c => c.Core))
                    cards.Controls.Add(CoreCard(coreNumber++, core.Key, core.ToList()));
                shortcuts.Visible = presets.Count > 0;
                FitCards();
                ReflectSelection();
            }
            finally { shortcuts.ResumeLayout(true); cards.ResumeLayout(true); }
        }

        private Control CoreCard(int number, int coreId, List<CpuSet> cpus)
        {
            var tile = new FlowLayoutPanel { FlowDirection=FlowDirection.TopDown, WrapContents=false, BackColor=CardColor,
                Font=Font, Padding=new Padding(6, 3, 5, 4), Margin=new Padding(2,2,4,4), AutoSize=false };
            var name = Command("Core " + number, delegate {
                bool select = cpus.Any(c => c.Available && !selection.Contains(c.Id));
                foreach (var cpu in cpus.Where(c => c.Available))
                    if (select) selection.Add(cpu.Id); else selection.Remove(cpu.Id);
                ReflectSelection();
            });
            name.FlatAppearance.BorderSize = 0; name.Margin = Padding.Empty; name.Padding = Padding.Empty;
            name.TextAlign = ContentAlignment.MiddleLeft; name.ForeColor = MutedColor;
            name.AccessibleName = "Select or clear all logical processors in core " + number;
            hints.SetToolTip(name, name.AccessibleName + " · Windows core index " + coreId);
            tile.Controls.Add(name);
            foreach (var cpu in cpus)
            {
                var item = cpu;
                var check = new CheckBox { Text="CPU " + cpu.Index + (cpu.Available ? "" : " · reserved"), AutoSize=true, Enabled=cpu.Available,
                    Margin=new Padding(4,0,1,1), ForeColor=TextColor, UseVisualStyleBackColor=false, BackColor=CardColor };
                check.AccessibleName = "Group " + cpu.Group + ", core " + number + ", CPU " + cpu.Index;
                hints.SetToolTip(check, "Windows CPU set ID: " + cpu.Id + (cpu.Available ? "" : " · reserved for another process"));
                check.CheckedChanged += delegate {
                    if (updating) return;
                    if (check.Checked) selection.Add(item.Id); else selection.Remove(item.Id);
                    check.ForeColor = check.Checked ? SelectedColor : TextColor;
                    RefreshSummary();
                };
                tile.Controls.Add(check);
                visibleChecks.Add(new KeyValuePair<CheckBox, CpuSet>(check, cpu));
            }
            int preferredHeight = name.GetPreferredSize(Size.Empty).Height + tile.Padding.Vertical;
            foreach (CheckBox check in tile.Controls.OfType<CheckBox>())
                preferredHeight += check.GetPreferredSize(Size.Empty).Height + check.Margin.Vertical;
            tile.Height = preferredHeight + 2;
            return tile;
        }

        private void FitCards()
        {
            if (cards.ClientSize.Width < 50) return;
            int width = cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            int desired = TextRenderer.MeasureText("Core 000        ", Font).Width + 36;
            int columns = Math.Max(1, Math.Min(4, width / Math.Max(1, desired)));
            foreach (Control tile in cards.Controls) tile.Width = Math.Max(45, width / columns - tile.Margin.Horizontal);
        }

        private void SetVisible(Predicate<CpuSet> select)
        {
            foreach (var cpu in topology.Where(c => c.Group == group && c.Available))
                if (select(cpu)) selection.Add(cpu.Id); else selection.Remove(cpu.Id);
            ReflectSelection();
        }

        private void ReflectSelection()
        {
            updating = true;
            try
            {
                foreach (var item in visibleChecks)
                {
                    item.Key.Checked = selection.Contains(item.Value.Id);
                    item.Key.ForeColor = item.Key.Checked ? SelectedColor : TextColor;
                }
            }
            finally { updating = false; }
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            summary.Text = selection.Count == 0 ? "Windows selects the logical processors." :
                "Logical processors selected: " + selection.Count + " of " + availableCount;
        }

        protected override void Dispose(bool disposing) { if (disposing) hints.Dispose(); base.Dispose(disposing); }
    }
}
