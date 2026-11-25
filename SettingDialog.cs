using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Procesi
{
    internal sealed class SettingDialog : Form
    {
        private readonly TableLayoutPanel grid = new TableLayoutPanel();
        private readonly ComboBox combo = new ComboBox();
        private readonly CheckedListBox list = new CheckedListBox();
        private readonly CheckBox remember = new CheckBox();
        private readonly Label note = new Label();
        private readonly Panel selector = new Panel();
        private readonly List<KeyValuePair<string,string>> entries = new List<KeyValuePair<string,string>>();
        public Func<bool> Commit;
        public bool Remember { get { return remember.Checked; } }
        public string Selected { get { if (combo.SelectedIndex < 0) throw new ArgumentException("Select a value from the list."); return entries[combo.SelectedIndex].Key; } }
        public IEnumerable<string> SelectedMany { get { return list.CheckedIndices.Cast<int>().Select(x=>entries[x].Key); } }

        public SettingDialog(string title, ProcessRow row, bool multi)
        {
            Text = title; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ShowIcon = false;
            Font = new Font("Segoe UI",10f); AutoScaleMode=AutoScaleMode.Dpi; BackColor=DarkTheme.Surface; ForeColor=DarkTheme.Text;
            ClientSize = new Size(464, multi ? 498 : 279);
            grid.Padding = new Padding(22,15,22,14); grid.Dock = DockStyle.Fill; grid.ColumnCount = 1; grid.RowCount = 6;
            foreach (float height in new float[] {38,31,multi ? 246 : 42,36,48,49})
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            var heading = new Label { Dock=DockStyle.Fill, Text=title, Font=new Font("Segoe UI",17f,FontStyle.Bold), ForeColor=DarkTheme.Text, AutoEllipsis=true };
            var process = new Label {Dock=DockStyle.Fill, Text=row.Name + "   ·   PID " + row.Pid, AutoEllipsis=true, ForeColor=DarkTheme.Muted};
            grid.Controls.Add(heading,0,0); grid.Controls.Add(process,0,1);
            selector.Dock=DockStyle.Fill; grid.Controls.Add(selector,0,2);
            list.IntegralHeight=false; list.Dock=DockStyle.Fill; list.CheckOnClick=true; list.BorderStyle=BorderStyle.FixedSingle;
            list.BackColor=DarkTheme.Field; list.ForeColor=DarkTheme.Text; DarkTheme.Scrollbars(list);
            combo.DropDownStyle=ComboBoxStyle.DropDownList; combo.Dock=DockStyle.Top;
            combo.BackColor=DarkTheme.Field; combo.ForeColor=DarkTheme.Text; combo.FlatStyle=FlatStyle.Flat;
            if (multi) selector.Controls.Add(list); else selector.Controls.Add(combo);
            remember.Dock=DockStyle.Fill; remember.Visible=false; remember.Text="Always apply this setting when the process starts";
            remember.FlatStyle=FlatStyle.Flat; remember.FlatAppearance.CheckedBackColor=DarkTheme.Edge;
            grid.Controls.Add(remember,0,3);
            note.Dock=DockStyle.Fill; note.ForeColor=DarkTheme.Muted;
            grid.Controls.Add(note,0,4);
            var actions = new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,3,0,0)};
            var ok = new Button {Text="Apply",Width=100,Height=33};
            DarkTheme.StyleButton(ok,true);
            var cancel = new Button {Text="Cancel",Width=95,Height=33,DialogResult=DialogResult.Cancel};
            DarkTheme.StyleButton(cancel,false);
            ok.Click += delegate {
                try { if (Commit != null && Commit()) DialogResult = DialogResult.OK; }
                catch (Exception e) { DarkTheme.Message(this,e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information); }
            };
            actions.Controls.Add(ok); actions.Controls.Add(cancel); grid.Controls.Add(actions,0,5);
            AcceptButton=ok; CancelButton=cancel;
            Controls.Add(grid);
            DarkTheme.ApplyWindow(this);
        }

        public void SetChoices(IEnumerable<KeyValuePair<string,string>> choices, string current)
        {
            foreach (var choice in choices)
            {
                int i = combo.Items.Add(choice.Value);
                entries.Add(choice);
                if (choice.Key == current) combo.SelectedIndex=i;
            }
        }
        public void SetMultiple(IEnumerable<KeyValuePair<string,string>> choices, IEnumerable<string> selected, string help)
        {
            var checkedIds=new HashSet<string>(selected);
            foreach (var choice in choices) { entries.Add(choice); list.Items.Add(choice.Value,checkedIds.Contains(choice.Key)); }
            SetNote(help);
        }
        public void SetRuleOption(bool allowed,string saved)
        {
            remember.Visible = true; remember.Enabled = allowed;
            if (saved != null) note.Text="Saved rule: " + saved + ". " + note.Text;
        }
        public void SetNote(string text) { note.Text=text; }
    }
}
