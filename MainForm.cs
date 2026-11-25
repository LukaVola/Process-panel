using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Procesi
{
    public partial class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(238, 238, 238);
        private static readonly Color Muted = Color.FromArgb(188, 188, 188);
        private static readonly Color Accent = Color.FromArgb(172, 194, 212);
        private static readonly Color Hairline = Color.FromArgb(81, 81, 81);
        private static readonly Color Paper = Color.FromArgb(43, 43, 43);
        private static readonly Color Surface = Color.FromArgb(25, 25, 25);
        private static readonly Color Raised = Color.FromArgb(55, 55, 55);
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
        private readonly RuleStore rules = new RuleStore();
        private readonly BackgroundWorker sampler = new BackgroundWorker();
        private readonly Timer clock = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolTip tips = new ToolTip();
        private readonly SummaryBand summary = new SummaryBand();
        private readonly PageButton processesTab = new PageButton("All Processes", 114);
        private readonly PageButton activeTab = new PageButton("Active Processes", 137);
        private readonly PageButton rulesTab = new PageButton("Persistent Rules", 132);
        private readonly Panel thresholdPanel = new Panel();
        private readonly Label thresholdLabel = new Label();
        private readonly TrackBar activeThreshold = new TrackBar();
        private readonly TextBox search = new TextBox();
        private readonly Label searchHint = new Label();
        private readonly Button action = MakeButton("Process Settings", true);
        private readonly Button refresh = MakeButton("Refresh", false);
        private readonly Button ruleCpu = MakeButton("CPU Sets…", false);
        private readonly Button browseRule = MakeButton("Choose another app…", false);
        private readonly ProcessGrid grid = new ProcessGrid();
        private ProcessIcons processIcons;
        private int iconRepaintQueued;
        private readonly Label empty = new Label();
        private readonly Label message = new Label();
        private readonly MetricsFooter metrics = new MetricsFooter();
        private readonly List<ProcessRow> shown = new List<ProcessRow>();
        private readonly List<SavedRule> shownRules = new List<SavedRule>();
        private readonly Dictionary<string,SavedRule> byPath = new Dictionary<string,SavedRule>(StringComparer.OrdinalIgnoreCase);
        private ProcessSnapshot latest;
        private bool queuedRefresh, exiting, updating, isMaximized, samplerPaused;
        private enum Page { All, Active, Rules }
        private sealed class ViewState
        {
            public string SortKey;
            public bool Ascending;
            public string Query = "";
            public ViewState(string key, bool ascending) { SortKey=key; Ascending=ascending; }
        }
        private Page page;
        private readonly ViewState[] views = {new ViewState("name",true), new ViewState("cpu",false), new ViewState("app",true)};
        private ViewState CurrentView { get { return views[(int)page]; } }
        private bool showingRules { get { return page == Page.Rules; } }
        private static readonly double[] CpuThresholds = {0.01, 0.05, 0.1, 0.2};
        private double MinimumCpu { get { return CpuThresholds[activeThreshold.Value]; } }
        private DateTime showMessageUntil;

        private sealed class SavedRule
        {
            public string Path;
            public string Name;
            public string Settings;
            public string Details;
            public string[] Keys;
            public string Scope { get { return System.IO.Path.IsPathRooted(Path) ? Path : "File name (any folder)"; } }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr window, int messageId, IntPtr wParam, string text);

        public MainForm()
        {
            Text = "Process Panel";
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Paper;
            ForeColor = Ink;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle desktopArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            using (Graphics desktop = Graphics.FromHwnd(IntPtr.Zero))
            {
                int availableWidth = (int)(desktopArea.Width*96f/desktop.DpiX)-42;
                int availableHeight = (int)(desktopArea.Height*96f/desktop.DpiY)-70;
                ClientSize = new Size(Math.Max(674,Math.Min(1366,availableWidth)),Math.Max(420,Math.Min(680,availableHeight)));
            }
            MinimumSize = new Size(690, 460);
            Icon = MakeIcon();
            DarkTheme.ApplyWindow(this);
            KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1,
                Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] {142, 46, 61})
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            base.Controls.Add(layout);
            summary.Dock = DockStyle.Fill;
            summary.Margin = Padding.Empty;
            layout.Controls.Add(summary, 0, 0);

            var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = Raised, Padding = new Padding(16, 0, 0, 0), WrapContents = false };
            tabs.Controls.Add(processesTab);
            tabs.Controls.Add(activeTab);
            tabs.Controls.Add(rulesTab);
            foreach (Button button in new [] {ruleCpu, browseRule})
            {
                button.Dock = DockStyle.None;
                button.Size = new Size(button == ruleCpu ? 102 : 149, 32);
                button.Margin = new Padding(button == ruleCpu ? 12 : 5, 7, 0, 0);
                button.Visible = false;
                tabs.Controls.Add(button);
            }
            ruleCpu.Click += delegate { var r=SelectedRule(); EditSavedCpuSets(r == null ? null : r.Path); };
            browseRule.Click += delegate { EditSavedCpuSets(null); };
            thresholdPanel.Size = new Size(238,46);
            thresholdPanel.BackColor = Raised;
            thresholdPanel.Margin = new Padding(12,0,0,0);
            thresholdLabel.TextAlign = ContentAlignment.MiddleLeft;
            thresholdLabel.ForeColor = Ink;
            thresholdLabel.SetBounds(0,0,109,46);
            thresholdLabel.Text = "Min CPU: 0.01%";
            activeThreshold.SetBounds(109,10,128,26);
            activeThreshold.AutoSize = false;
            activeThreshold.Height = 26;
            activeThreshold.BackColor = Raised;
            activeThreshold.Minimum = 0;
            activeThreshold.Maximum = CpuThresholds.Length-1;
            activeThreshold.SmallChange = 1;
            activeThreshold.LargeChange = 1;
            activeThreshold.TickFrequency = 1;
            activeThreshold.TickStyle = TickStyle.BottomRight;
            activeThreshold.AccessibleName = "Minimum CPU usage";
            activeThreshold.AccessibleDescription = "CPU thresholds: 0.01%, 0.05%, 0.1% and 0.2%. Use the arrow keys to select the next threshold.";
            activeThreshold.ValueChanged += delegate
            {
                thresholdLabel.Text = "Min CPU: " + MinimumCpu.ToString("0.##",English) + "%";
                activeThreshold.AccessibleName = "Minimum CPU usage: " + MinimumCpu.ToString("0.##",English) + "%";
                if (page == Page.Active) RebuildRows();
            };
            activeThreshold.AccessibleName = "Minimum CPU usage: 0.01%";
            tips.SetToolTip(activeThreshold, "Minimum total CPU usage: 0.01%, 0.05%, 0.1% or 0.2%.");
            thresholdPanel.Controls.Add(thresholdLabel);
            thresholdPanel.Controls.Add(activeThreshold);
            thresholdPanel.Visible = false;
            tabs.Controls.Add(thresholdPanel);
            processesTab.Click += delegate { ChangePage(Page.All); };
            activeTab.Click += delegate { ChangePage(Page.Active); };
            rulesTab.Click += delegate { ChangePage(Page.Rules); };
            processesTab.Selected = true;
            layout.Controls.Add(tabs, 0, 1);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                Padding = new Padding(18, 11, 18, 11), ColumnCount = 4, RowCount = 1 };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 161));
            var field = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(35, 8, 6, 4), Margin = new Padding(0, 1, 2, 1) };
            field.Paint += delegate(object sender, PaintEventArgs args)
            {
                var g = args.Graphics;
                float scale = g.DpiX / 96f;
                using (var border = new Pen(search.Focused ? Accent : Hairline))
                    g.DrawRectangle(border, 0, 0, field.Width - 1, field.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Muted, 1.4f * scale))
                {
                    float y = field.Height / 2f - 6 * scale;
                    g.DrawEllipse(pen, 11 * scale, y, 9 * scale, 9 * scale);
                    g.DrawLine(pen, 19 * scale, y + 8 * scale, 23 * scale, y + 12 * scale);
                }
            };
            field.Click += delegate { search.Focus(); };
            search.Dock = DockStyle.Fill;
            search.BorderStyle = BorderStyle.None;
            search.AccessibleName = "Search processes or rules";
            search.BackColor = Surface;
            search.ForeColor = Ink;
            search.GotFocus += delegate { field.Invalidate(); SearchPrompt(); };
            search.LostFocus += delegate { field.Invalidate(); SearchPrompt(); };
            search.HandleCreated += delegate { SearchPrompt(); };
            search.TextChanged += delegate { SearchPrompt(); if (!updating) RebuildRows(); };
            field.Controls.Add(search);
            searchHint.Dock = DockStyle.Fill;
            searchHint.BackColor = Surface;
            searchHint.ForeColor = Muted;
            searchHint.TextAlign = ContentAlignment.TopLeft;
            searchHint.Cursor = Cursors.IBeam;
            searchHint.Click += delegate { search.Focus(); };
            field.Controls.Add(searchHint);
            searchHint.BringToFront();
            SearchPrompt();
            toolbar.Controls.Add(field, 0, 0);
            refresh.Margin = new Padding(0, 0, 7, 0);
            refresh.Click += delegate { RefreshNow(); };
            tips.SetToolTip(refresh, "Refresh the process list (F5)");
            toolbar.Controls.Add(refresh, 2, 0);
            action.Click += delegate { PrimaryAction(); };
            toolbar.Controls.Add(action, 3, 0);
            layout.Controls.Add(toolbar, 0, 2);

            var listArea = new Panel { Dock = DockStyle.Fill, Margin = new Padding(18, 0, 18, 0), BackColor = Surface };
            PrepareGrid();
            listArea.Controls.Add(grid);
            empty.AutoSize = false;
            empty.Dock = DockStyle.Fill;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = Muted;
            empty.BackColor = Surface;
            empty.Text = "Loading processes…";
            listArea.Controls.Add(empty);
            empty.BringToFront();
            layout.Controls.Add(listArea, 0, 3);

            message.Dock = DockStyle.Fill;
            message.Margin = new Padding(8, 0, 8, 0);
            message.AutoEllipsis = true;
            message.ForeColor = Muted;
            message.TextAlign = ContentAlignment.MiddleLeft;
            message.Font = new Font(Font.FontFamily, 8.5f);
            message.AccessibleName = "Notification";
            toolbar.Controls.Add(message, 1, 0);
            metrics.Dock = DockStyle.Fill;
            metrics.Margin = Padding.Empty;
            bool reflowQueued = false;
            metrics.HeightNeeded += delegate(int h)
            {
                if (layout.RowStyles[4].Height == h && metrics.Height == h) return;
                layout.RowStyles[4].Height = h;
                if (!layout.IsHandleCreated || reflowQueued) return;
                reflowQueued = true;
                layout.BeginInvoke((Action)delegate
                {
                    reflowQueued=false;
                    if (!layout.IsDisposed) layout.PerformLayout();
                });
            };
            layout.Controls.Add(metrics, 0, 4);
            metrics.UpdateSnapshot(null);
            SetColumns();
            UpdateAction();
            ConfigureTray();
            sampler.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                if ((bool)e.Argument)
                {
                    var targets = rules.Paths();
                    if (targets.Count != 0) rules.Reconcile(ProcessSampler.CaptureForRules(targets).Rows);
                    else if (!samplerPaused) ProcessSampler.Pause();
                    samplerPaused = targets.Count == 0;
                    return;
                }
                samplerPaused = false;
                var snapshot = ProcessSampler.Capture();
                rules.Reconcile(snapshot.Rows);
                e.Result = snapshot;
            };
            sampler.RunWorkerCompleted += Sampled;
            clock.Interval = 2000;
            clock.Tick += delegate { if (!sampler.IsBusy) RefreshNow(); };
            Shown += delegate { SystemStats.AttachUi(System.Threading.SynchronizationContext.Current); RefreshNow(); clock.Start(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) { Hide(); clock.Interval = 6000; RefreshNow(); }
                else if (Visible) { isMaximized = WindowState == FormWindowState.Maximized; }
            };
            KeyDown += HandleKeys;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                exiting = true;
                clock.Stop();
                tray.Visible = false;
            };
            FormClosed += delegate { clock.Dispose(); tray.Dispose(); trayMenu.Dispose(); tips.Dispose(); };
            VisibleChanged += delegate { if (!Visible) ReleaseProcessIcons(); };
            Disposed += delegate { ReleaseProcessIcons(); };
        }

        private void ConfigureTray()
        {
            trayMenu.Items.Add("Open Process Panel", null, delegate { RestoreFromTray(); });
            var login = new ToolStripMenuItem("Start at sign-in");
            login.Click += delegate
            {
                try { LoginStart.Enabled = !LoginStart.Enabled; login.Checked = LoginStart.Enabled; }
                catch (Exception ex) { DarkTheme.Message(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            trayMenu.Opening += delegate
            {
                try { login.Checked = LoginStart.Enabled; login.Enabled = true; }
                catch { login.Enabled = false; }
            };
            trayMenu.Items.Add(login);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { Close(); });
            DarkTheme.StyleMenu(trayMenu);
            tray.Icon = Icon;
            tray.ContextMenuStrip = trayMenu;
            tray.Text = "Process Panel";
            tray.MouseClick += delegate(object sender, MouseEventArgs args) { if (args.Button == MouseButtons.Left) RestoreFromTray(); };
            tray.Visible = true;
        }

        internal void RestoreFromTray()
        {
            startupHidden = true;
            clock.Interval = 2000;
            clock.Start();
            Show();
            SystemStats.AttachUi(System.Threading.SynchronizationContext.Current);
            if (WindowState == FormWindowState.Minimized) WindowState = isMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
            ShowWindow(Handle, WindowState == FormWindowState.Maximized ? 3 : 5);
            Activate();
            SetForegroundWindow(Handle);
            RefreshNow();
        }

        private static Icon MakeIcon()
        {
            using (var b = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(b))
            using (var fill = new SolidBrush(Color.FromArgb(45, 91, 85)))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(fill, 1, 1, 30, 30);
                g.FillRectangle(Brushes.White, 7, 17, 4, 7);
                g.FillRectangle(Brushes.White, 14, 12, 4, 12);
                g.FillRectangle(Brushes.White, 21, 7, 4, 17);
                var h = b.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(h)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        private static Button MakeButton(string text, bool filled)
        {
            var b = new PanelButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = filled ? Color.FromArgb(66, 84, 97) : Raised,
                ForeColor = filled ? Color.White : Ink, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = filled ? Color.FromArgb(102, 123, 135) : Hairline;
            b.FlatAppearance.MouseOverBackColor = filled ? Color.FromArgb(75, 96, 111) : Color.FromArgb(66, 66, 66);
            b.FlatAppearance.MouseDownBackColor = filled ? Color.FromArgb(51, 67, 77) : Color.FromArgb(47, 47, 47);
            return b;
        }

        private void PrepareGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Surface;
            DarkTheme.Scrollbars(grid);
            foreach (Control c in grid.Controls) if (c is ScrollBar) DarkTheme.Scrollbars(c);
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(55, 55, 55);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.RowHeadersVisible = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ReadOnly = true;
            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ScrollBars = ScrollBars.Vertical;
            grid.SizeChanged += delegate { FitHeadings(); };
            grid.RowTemplate.Height = 31;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 33;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI Semibold", 9f), BackColor = Raised,
                ForeColor = Ink, SelectionBackColor = Raised,
                SelectionForeColor = Ink, Padding = new Padding(5, 0, 5, 0), WrapMode = DataGridViewTriState.False
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI", 9.5f), BackColor = Surface, ForeColor = Ink,
                SelectionBackColor = Color.FromArgb(59, 81, 98), SelectionForeColor = Color.White,
                Padding = new Padding(5, 0, 5, 0), NullValue = ""
            };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 28, 28);
            grid.CellValueNeeded += GiveValue;
            grid.CellPainting += PaintProcessName;
            grid.CellPainting += PaintActiveCpu;
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (showingRules && e.RowIndex < shownRules.Count)
                    e.ToolTipText = e.ColumnIndex == 2 ? shownRules[e.RowIndex].Details : shownRules[e.RowIndex].Path;
                else if (!showingRules && e.RowIndex < shown.Count) e.ToolTipText = shown[e.RowIndex].Path;
            };
            grid.CurrentCellChanged += delegate { if (!updating) { UpdateAction(); UpdateNotice(); } };
            grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                grid.Rows[e.RowIndex].Selected = true;
            };
            grid.CellMouseUp += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0) OpenRowMenu(Cursor.Position);
            };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (page == Page.Active && e.RowIndex < shown.Count) RevealActiveProcess(shown[e.RowIndex]);
                else if (showingRules) RuleDetails(); else PrimaryAction();
            };
            grid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
                string key = grid.Columns[e.ColumnIndex].Name;
                bool numericDescending = key == "cpu" || key == "gpu" || key == "ram" || key == "rules" || key == "priority";
                CurrentView.Ascending = key == CurrentView.SortKey ? !CurrentView.Ascending : !numericDescending;
                CurrentView.SortKey = key;
                RebuildRows();
            };
        }

        private void PaintProcessName(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (showingRules || e.RowIndex < 0 || e.RowIndex >= shown.Count || e.ColumnIndex < 0 ||
                grid.Columns[e.ColumnIndex].Name != "name" || !Visible || WindowState == FormWindowState.Minimized || exiting) return;

            ProcessRow process = shown[e.RowIndex];
            float scale = e.Graphics.DpiX / 96f;
            int inset = Math.Max(1,(int)Math.Round(2*scale));
            int gap = Math.Max(1,(int)Math.Round(4*scale));
            int iconSize = Math.Min((int)Math.Round(16*scale),e.CellBounds.Height-2*inset);
            int left = e.CellBounds.Left + e.CellStyle.Padding.Left + inset;
            int right = e.CellBounds.Right - e.CellStyle.Padding.Right - inset;
            iconSize = Math.Min(iconSize,right-left);
            if (iconSize < 1) return;

            e.PaintBackground(e.ClipBounds,true);
            if (processIcons == null) processIcons = new ProcessIcons(QueueIconRepaint);
            Image icon = processIcons.Get(process.Path,iconSize);
            if (icon != null)
            {
                var bounds = new Rectangle(left,e.CellBounds.Top+(e.CellBounds.Height-iconSize)/2,iconSize,iconSize);
                e.Graphics.DrawImage(icon,bounds,0,0,icon.Width,icon.Height,GraphicsUnit.Pixel);
            }
            int textLeft = left+iconSize+gap;
            if (textLeft < right)
            {
                var textBounds = new Rectangle(textLeft,e.CellBounds.Top,right-textLeft,e.CellBounds.Height);
                bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
                TextRenderer.DrawText(e.Graphics,process.Name,e.CellStyle.Font,textBounds,
                    selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
            }
            e.Paint(e.ClipBounds,DataGridViewPaintParts.Focus | DataGridViewPaintParts.Border | DataGridViewPaintParts.ErrorIcon);
            e.Handled = true;
        }

        private void QueueIconRepaint()
        {
            if (System.Threading.Interlocked.Exchange(ref iconRepaintQueued,1) != 0) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    System.Threading.Interlocked.Exchange(ref iconRepaintQueued,0);
                    if (exiting || IsDisposed || !Visible || showingRules || !grid.Visible) return;
                    var column = grid.Columns["name"];
                    if (column == null) return;
                    Rectangle visible = grid.GetColumnDisplayRectangle(column.Index,true);
                    if (visible.Width > 0 && visible.Height > 0) grid.Invalidate(visible);
                });
            }
            catch (InvalidOperationException)
            {
                System.Threading.Interlocked.Exchange(ref iconRepaintQueued,0);
            }
        }

        private void ReleaseProcessIcons()
        {
            if (processIcons == null) return;
            processIcons.Dispose();
            processIcons = null;
        }

        private void PaintActiveCpu(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (page != Page.Active || e.RowIndex < 0 || e.RowIndex >= shown.Count ||
                e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "cpu") return;
            e.PaintBackground(e.ClipBounds,true);
            var inner = Rectangle.Inflate(e.CellBounds,-3,-4);
            double utilization = Math.Min(100, Math.Max(0,shown[e.RowIndex].Cpu));
            if (!Double.IsNaN(utilization) && utilization > 0 && inner.Width > 0 && inner.Height > 0)
            {
                int width = Math.Max(1, (int)Math.Round(inner.Width * utilization / 100));
                bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
                using (var fill = new SolidBrush(selected ? Color.FromArgb(56,105,83) : Color.FromArgb(36,90,59)))
                    e.Graphics.FillRectangle(fill,inner.X,inner.Y,width,inner.Height);
            }
            e.PaintContent(e.ClipBounds);
            e.Paint(e.ClipBounds,DataGridViewPaintParts.Focus | DataGridViewPaintParts.Border);
            e.Handled = true;
        }
        private void AddColumn(string name, string title, int weight, bool numeric)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight,
                MinimumWidth = numeric ? 55 : 66, SortMode = DataGridViewColumnSortMode.Programmatic };
            if (numeric)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            grid.Columns.Add(column);
        }

        private void SetColumns()
        {
            grid.RowCount = 0;
            grid.Columns.Clear();
            if (showingRules)
            {
                AddColumn("app", "Application", 22, false);
                AddColumn("path", "Match scope", 35, false);
                AddColumn("settings", "Settings", 36, false);
            }
            else
            {
                AddColumn("name", "Process name", page == Page.Active ? 25 : 30, false);
                AddColumn("pid", "PID", 9, true);
                AddColumn("cpu", "CPU (%)", page == Page.Active ? 15 : 10, true);
                AddColumn("ram", "RAM (MiB)", 13, true);
                AddColumn("gpu", "GPU (%)", 10, true);
                AddColumn("priority", "CPU priority", 17, false);
                AddColumn("rules", "Rules", 9, true);
                FitHeadings();
            }
        }

        private void FitHeadings()
        {
            if (showingRules || !grid.Columns.Contains("rules")) return;
            bool compact = grid.Width < 840 * Font.Height / 17;
            grid.Columns["cpu"].HeaderText = compact ? "CPU %" : "CPU (%)";
            grid.Columns["gpu"].HeaderText = compact ? "GPU %" : "GPU (%)";
            grid.Columns["ram"].HeaderText = compact ? "MiB" : "RAM (MiB)";
            grid.Columns["priority"].HeaderText = compact ? "Priority" : "CPU priority";
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderCell.Style.Padding = compact ? Padding.Empty : new Padding(5,0,5,0);
            grid.Columns["rules"].MinimumWidth = compact ? Math.Max(62, 62*Font.Height/17) : 55;
        }

        private void GiveValue(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string key = grid.Columns[e.ColumnIndex].Name;
            if (showingRules)
            {
                if (e.RowIndex >= shownRules.Count) return;
                var row = shownRules[e.RowIndex];
                e.Value = key == "app" ? row.Name : key == "path" ? row.Scope : row.Settings;
                return;
            }
            if (e.RowIndex >= shown.Count) return;
            var p = shown[e.RowIndex];
            switch (key)
            {
                case "name": e.Value = p.Name; break;
                case "pid": e.Value = p.Pid; break;
                case "cpu": e.Value = CpuText(p.Cpu); break;
                case "gpu": e.Value = double.IsNaN(p.Gpu) ? "—" : p.Gpu.ToString("N1", English); break;
                case "ram": e.Value = p.MemoryBytes < 0 ? "—" : (p.MemoryBytes / 1048576d).ToString("N1", English); break;
                case "priority": e.Value = String.IsNullOrEmpty(p.Priority) ? "—" : p.Priority; break;
                case "rules": int count = RuleCount(p); e.Value = count == 0 ? "—" : count.ToString(); break;
            }
        }

        private static string CpuText(double cpu)
        {
            if (Double.IsNaN(cpu)) return "—";
            if (cpu > 0 && cpu < 0.01) return "<0.01";
            return cpu.ToString(cpu >= 0.01 && cpu < 0.1 ? "0.00#" : "N1",English);
        }

        private int RuleCount(ProcessRow process)
        {
            SavedRule byFileName = null, byFullPath = null;
            if (!String.IsNullOrEmpty(process.Name)) byPath.TryGetValue(process.Name, out byFileName);
            if (!String.IsNullOrEmpty(process.Path)) byPath.TryGetValue(process.Path, out byFullPath);
            if (byFullPath == null) return byFileName == null ? 0 : byFileName.Keys.Length;
            if (byFileName == null) return byFullPath.Keys.Length;
            return byFullPath.Keys.Length + byFileName.Keys.Count(k => !byFullPath.Keys.Contains(k));
        }

        private void ReadRules()
        {
            byPath.Clear();
            foreach (string path in rules.Paths())
            {
                var values = rules.Values(path);
                var parts = values.OrderBy(pair => pair.Key).Select(pair =>
                    Procesi.Controls.Label(pair.Key) + " · " + Procesi.Controls.Display(pair.Key, pair.Value)).ToArray();
                string name = Path.GetFileName(path);
                byPath[path] = new SavedRule { Name = name, Path = path, Keys = values.Keys.ToArray(),
                    Settings = String.Join("   /   ", parts), Details = String.Join(Environment.NewLine, parts) };
            }
        }

        private void ChangePage(Page next)
        {
            if (page == next) return;
            updating = true;
            try
            {
                CurrentView.Query = search.Text;
                page = next;
                processesTab.Selected = page == Page.All;
                activeTab.Selected = page == Page.Active;
                rulesTab.Selected = showingRules;
                ruleCpu.Visible = showingRules;
                browseRule.Visible = showingRules;
                thresholdPanel.Visible = page == Page.Active;
                action.Text = showingRules ? "Remove rules…" : "Process Settings";
                search.Text = CurrentView.Query;
                SearchPrompt();
                SetColumns();
            }
            finally { updating = false; }
            ReadRules();
            RebuildRows();
        }

        private void RevealActiveProcess(ProcessRow row)
        {
            if (row == null) return;
            var all = views[(int)Page.All];
            if (!MatchesSearch(row,all.Query.Trim())) all.Query = "";
            ChangePage(Page.All);
            int at = shown.FindIndex(p => p.Pid == row.Pid && p.Started == row.Started);
            if (at < 0)
            {
                SetStatus("This process has exited.");
                return;
            }
            grid.CurrentCell = grid[0,at];
            grid.Rows[at].Selected = true;
            if (!grid.Rows[at].Displayed) grid.FirstDisplayedScrollingRowIndex = at;
            grid.Focus();
        }

        private void SearchPrompt()
        {
            searchHint.Text = showingRules ? "Search by application or path" : "Search by process name or PID";
            searchHint.Visible = !search.Focused && search.TextLength==0;
            if (search.IsHandleCreated) SendMessageW(search.Handle,0x1501,new IntPtr(1),searchHint.Text);
        }

        internal void RefreshNow()
        {
            if (exiting || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)RefreshNow); return; }
            if (sampler.IsBusy) { queuedRefresh = true; return; }
            bool hidden = !Visible || WindowState == FormWindowState.Minimized;
            if (hidden && samplerPaused && rules.Paths().Count == 0) return;
            sampler.RunWorkerAsync(hidden);
        }

        private void Sampled(object sender, RunWorkerCompletedEventArgs e)
        {
            if (exiting || IsDisposed) return;
            if (e.Error != null)
            {
                SetStatus("Could not refresh the process list. " + e.Error.Message);
                if (latest == null) empty.Text = "The process list is unavailable.";
            }
            else if (e.Result != null && Visible && WindowState != FormWindowState.Minimized)
            {
                latest = (ProcessSnapshot)e.Result;
                summary.UpdateSnapshot(latest);
                ReadRules();
                if (Visible) RebuildRows();
                metrics.UpdateSnapshot(latest);
                UpdateNotice();
            }
            else if (!Visible && samplerPaused && rules.Paths().Count == 0)
            {
                clock.Stop();
            }
            if (queuedRefresh) { queuedRefresh = false; RefreshNow(); }
        }

        private static bool ContainsText(string value, string term)
        {
            return term.Length == 0 || (value != null && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MatchesSearch(ProcessRow row, string term)
        {
            return ContainsText(row.Name,term) || ContainsText(row.Pid.ToString(English),term);
        }

        private void RebuildRows()
        {
            if (updating || grid.Columns.Count == 0) return;
            var oldProcess = SelectedProcess();
            var oldRule = SelectedRule();
            int first = grid.FirstDisplayedScrollingRowIndex;
            int oldOffset = grid.CurrentRow == null ? -1 : grid.CurrentRow.Index - first;
            int column = grid.CurrentCell == null ? 0 : grid.CurrentCell.ColumnIndex;
            string term = search.Text.Trim();
            bool ascending = CurrentView.Ascending;
            string sort = CurrentView.SortKey;
            updating = true;
            try
            {
                int selectedIndex, size;
                if (showingRules)
                {
                    shownRules.Clear();
                    var source = byPath.Values.Where(r => ContainsText(r.Name, term) || ContainsText(r.Path, term));
                    Func<SavedRule,string> key = r => sort == "settings" ? r.Settings : sort == "path" ? r.Scope : r.Name;
                    var ordered = ascending ? source.OrderBy(key, StringComparer.CurrentCultureIgnoreCase) :
                        source.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase);
                    shownRules.AddRange(ordered.ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase));
                    selectedIndex = oldRule == null ? -1 : shownRules.FindIndex(r => r.Path == oldRule.Path);
                    size = shownRules.Count;
                }
                else
                {
                    shown.Clear();
                    if (latest != null)
                    {
                        double threshold = MinimumCpu;
                        shown.AddRange(latest.Rows.Where(r => (page != Page.Active || r.Cpu >= threshold) && MatchesSearch(r,term)));
                    }
                    shown.Sort(delegate(ProcessRow a, ProcessRow b)
                    {
                        int cmp;
                        switch (sort)
                        {
                            case "pid": cmp = a.Pid.CompareTo(b.Pid); break;
                            case "cpu": cmp = a.Cpu.CompareTo(b.Cpu); break;
                            case "gpu": cmp = a.Gpu.CompareTo(b.Gpu); break;
                            case "ram": cmp = a.MemoryBytes.CompareTo(b.MemoryBytes); break;
                            case "priority": cmp = PriorityRank(a.Priority).CompareTo(PriorityRank(b.Priority)); break;
                            case "rules": cmp = RuleCount(a).CompareTo(RuleCount(b)); break;
                            default: cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); break;
                        }
                        if (cmp != 0) return ascending ? cmp : -cmp;
                        return a.Pid.CompareTo(b.Pid);
                    });
                    selectedIndex = oldProcess == null ? -1 : shown.FindIndex(r => r.Pid == oldProcess.Pid && r.Started == oldProcess.Started);
                    size = shown.Count;
                }
                grid.RowCount = size;
                foreach (DataGridViewColumn c in grid.Columns)
                    c.HeaderCell.SortGlyphDirection = c.Name != sort ? SortOrder.None : ascending ? SortOrder.Ascending : SortOrder.Descending;
                if (selectedIndex >= 0 && column < grid.Columns.Count)
                {
                    if (grid.CurrentCell == null || grid.CurrentCell.RowIndex != selectedIndex || grid.CurrentCell.ColumnIndex != column)
                        grid.CurrentCell = grid[column, selectedIndex];
                    int visible = Math.Max(1, grid.DisplayedRowCount(false));
                    if (oldOffset >= 0 && oldOffset < visible)
                        first = Math.Max(0, Math.Min(first, selectedIndex));
                }
                else { grid.ClearSelection(); grid.CurrentCell = null; }
                if (size > 0 && first >= 0 && grid.Visible)
                {
                    int nextFirst = Math.Min(first, size - 1);
                    if (grid.FirstDisplayedScrollingRowIndex != nextFirst) grid.FirstDisplayedScrollingRowIndex = nextFirst;
                }
                grid.Invalidate();
                grid.Visible = size > 0;
                empty.Visible = size == 0;
                empty.Text = term.Length != 0 ? "No results match your search." : showingRules ?
                    "No persistent rules yet." : latest == null ? "Loading processes…" :
                    page == Page.Active ? "No processes meet the current CPU threshold." : "No processes available.";
            }
            finally { updating = false; }
            UpdateAction();
            UpdateNotice();
        }

        private static int PriorityRank(string priority)
        {
            string p = (priority ?? "").ToLowerInvariant().Replace(" ", "");
            if (p.Contains("real")) return 6;
            if (p.Contains("high")) return 5;
            if (p.Contains("above")) return 4;
            if (p.Contains("below")) return 2;
            if (p.Contains("normal")) return 3;
            if (p.Contains("idle") || p.Contains("low")) return 1;
            return 0;
        }

        private ProcessRow SelectedProcess()
        {
            int i = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
            return !showingRules && i >= 0 && i < shown.Count ? shown[i] : null;
        }

        private SavedRule SelectedRule()
        {
            int i = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
            return showingRules && i >= 0 && i < shownRules.Count ? shownRules[i] : null;
        }

        private void UpdateAction()
        {
            bool available = showingRules ? SelectedRule() != null : SelectedProcess() != null;
            action.Enabled = available;
            action.BackColor = available ? Color.FromArgb(66, 84, 97) : Raised;
            action.FlatAppearance.BorderColor = available ? Color.FromArgb(102, 123, 135) : Hairline;
        }

        private void PrimaryAction()
        {
            if (showingRules) { DeleteRule(); return; }
            var row = SelectedProcess();
            if (row != null) ShowProcessSettings(row, action);
        }

        private void OpenRowMenu(Point screenPoint)
        {
            ContextMenuStrip menu;
            if (showingRules)
            {
                if (SelectedRule() == null) return;
                menu = new ContextMenuStrip();
                menu.Items.Add("View rules", null, delegate { RuleDetails(); });
                menu.Items.Add("Remove rules…", null, delegate { DeleteRule(); });
            }
            else
            {
                var row = SelectedProcess();
                if (row == null) return;
                menu = BuildProcessMenu(row);
            }
            if (menu == null) return;
            DarkTheme.StyleMenu(menu);
            menu.Closed += delegate { BeginInvoke((Action)delegate { menu.Dispose(); }); };
            menu.Show(screenPoint);
        }

        private void RuleDetails()
        {
            var rule = SelectedRule();
            if (rule != null) DarkTheme.Message(this, rule.Path + Environment.NewLine + Environment.NewLine + rule.Details,
                rule.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeleteRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            if (DarkTheme.Message(this, "Remove all persistent rules for this application?\n\n" + rule.Path,
                "Remove rules", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { rules.RemoveAll(rule.Path); ReadRules(); RebuildRows(); SetStatus("Removed rules for " + rule.Name + "."); }
            catch (Exception ex) { DarkTheme.Message(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        internal void SetStatus(string text)
        {
            if (IsDisposed || exiting) return;
            if (InvokeRequired) { BeginInvoke((Action)delegate { SetStatus(text); }); return; }
            showMessageUntil = DateTime.UtcNow.AddSeconds(8);
            message.Text = text;
            message.ForeColor = Ink;
            tips.SetToolTip(message, text);
        }

        private void UpdateNotice()
        {
            if (DateTime.UtcNow < showMessageUntil) return;
            string error = rules.LastError;
            string text = error ?? "";
            if (message.Text != text)
            {
                message.Text = text;
                message.ForeColor = String.IsNullOrEmpty(text) ? Muted : Color.FromArgb(244, 198, 130);
                tips.SetToolTip(message, text);
            }
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == System.Windows.Forms.Keys.F) { search.Focus(); search.SelectAll(); }
            else if (e.KeyCode == System.Windows.Forms.Keys.F5) RefreshNow();
            else if (e.KeyCode == System.Windows.Forms.Keys.Escape && search.Focused) search.Clear();
            else if (e.KeyCode == System.Windows.Forms.Keys.Delete && showingRules && grid.Focused) DeleteRule();
            else if (e.KeyCode == System.Windows.Forms.Keys.Enter && grid.Focused)
            {
                if (page == Page.Active) RevealActiveProcess(SelectedProcess());
                else if (showingRules) RuleDetails(); else PrimaryAction();
            }
            else if ((e.KeyCode == System.Windows.Forms.Keys.Apps || (e.Shift && e.KeyCode == System.Windows.Forms.Keys.F10)) && grid.Focused && grid.CurrentCell != null)
            {
                var cell = grid.GetCellDisplayRectangle(grid.CurrentCell.ColumnIndex, grid.CurrentCell.RowIndex, true);
                OpenRowMenu(grid.PointToScreen(new Point(cell.Left + 12, cell.Bottom)));
            }
            else return;
            e.Handled = e.SuppressKeyPress = true;
        }

        private sealed class PanelButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled) { base.OnPaint(e); return; }
                e.Graphics.Clear(BackColor);
                using (var pen = new Pen(FlatAppearance.BorderColor)) e.Graphics.DrawRectangle(pen, 0, 0, Width-1, Height-1);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(167,167,167),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private sealed class ProcessGrid : DataGridView
        {
            public ProcessGrid() { DoubleBuffered = true; }
        }

        private sealed class MetricsFooter : Panel
        {
            private static readonly string[] Help = {
                "CPU time is the percentage of time the processor was busy. CPU utility also accounts for the processor's speed relative to its nominal speed.",
                "Threads waiting to run. The value in parentheses is the average queue length over the last 30 seconds.",
                "Percentage of test messages that the Process Panel window answered within 50 ms over the last 30 seconds.",
                "Current utilization of the busiest GPU engine.",
                "Percentage of physical memory in use and total physical memory.",
                "Total number of threads and processes at the last sample.",
                "CPU sockets, processor groups and NUMA nodes, followed by physical cores/logical processors and the number of parked logical processors."
            };
            private readonly Label[] fields = new Label[Help.Length];
            private readonly ToolTip fieldTips = new ToolTip();
            private bool arranging;
            public event Action<int> HeightNeeded;

            public MetricsFooter()
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(56, 56, 56);
                ForeColor = Ink;
                Font = new Font("Segoe UI", 9.25f);
                AccessibleName = "System statistics";
                for (int i=0; i<fields.Length; i++)
                {
                    var label = new Label { ForeColor = Ink, BackColor = BackColor, UseMnemonic = false,
                        TextAlign = ContentAlignment.MiddleLeft, AccessibleDescription = Help[i], Margin = Padding.Empty };
                    label.Paint += delegate(object sender, PaintEventArgs e)
                    {
                        var field = (Label)sender;
                        using (var pen = new Pen(Hairline))
                            e.Graphics.DrawLine(pen,field.Width-1,4,field.Width-1,field.Height-4);
                    };
                    fieldTips.SetToolTip(label, Help[i]);
                    fields[i] = label;
                    base.Controls.Add(label);
                }
            }
            private static string Num(double n, string format)
            {
                return Double.IsNaN(n) || n<0 ? "—" : n.ToString(format,English);
            }
            private static string Pct(double n) { return Double.IsNaN(n) || n<0 ? "—" : Num(n,"0.#") + "%"; }
            private static string Int(int n) { return n < 0 ? "—" : n.ToString("N0",English); }
            private static string Count(int n, string singular, string plural)
            {
                return Int(n) + " " + (n == 1 ? singular : plural);
            }
            public void UpdateSnapshot(ProcessSnapshot snapshot)
            {
                var s = snapshot;
                int logical = s == null || s.CoreCpu == null || s.CoreCpu.Length == 0 ? -1 : s.CoreCpu.Length;
                string[] contents = {
                    "CPU: " + Pct(s == null ? double.NaN : s.Cpu) + " time, " + Pct(s == null ? double.NaN : s.CpuUtility) + " utility",
                    "Queue length: " + Num(s == null ? double.NaN : s.QueueLength, "0") + " (" + Num(s == null ? double.NaN : s.QueueAverage, "0.00") + ")",
                    "Responsiveness: " + Pct(s == null ? double.NaN : s.Responsiveness),
                    "GPU: " + Pct(s == null ? double.NaN : s.Gpu),
                    "RAM: " + Pct(s == null ? double.NaN : s.MemoryPercent) + " of " + Num(s == null ? double.NaN : s.MemoryTotalGb, "0.#") + " GiB",
                    Count(s == null ? -1 : s.ThreadCount, "thread", "threads") + " in " + Count(s == null ? -1 : s.Rows.Count, "process", "processes"),
                    Count(s == null ? -1 : s.SocketCount, "socket", "sockets") + ", " + Count(s == null ? -1 : s.GroupCount, "group", "groups") +
                    ", " + Count(s == null ? -1 : s.NumaCount, "NUMA node", "NUMA nodes") + ", " + Int(s == null ? -1 : s.CoreCount) + "/" + Int(logical) +
                    (logical == 1 ? " core, " : " cores, ") + Int(s == null ? -1 : s.ParkedCount) + " parked"
                };
                for (int i=0; i<fields.Length; i++)
                {
                    if (fields[i].Text != contents[i]) fields[i].Text = contents[i];
                }
                ArrangeFields();
            }
            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                ArrangeFields();
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ArrangeFields();
            }
            private void ArrangeFields()
            {
                if (arranging || !IsHandleCreated || ClientSize.Width < 2 || fields[fields.Length-1] == null) return;
                arranging = true;
                try
                {
                    using (var g = CreateGraphics())
                    {
                        int pad = Math.Max(4,(int)Math.Round(6*g.DpiX/96));
                        int rowHeight = TextRenderer.MeasureText(g,"Agj",Font).Height + pad*2;
                        int x=0, y=1;
                        foreach (var field in fields)
                        {
                            int w = TextRenderer.MeasureText(g,field.Text,Font,Size.Empty,TextFormatFlags.SingleLine).Width + 2*pad+1;
                            if (x > 0 && x+w > ClientSize.Width) { x=0; y+=rowHeight; }
                            var bounds = new Rectangle(x,y,Math.Min(w, ClientSize.Width),rowHeight);
                            if (field.Bounds != bounds) field.Bounds = bounds;
                            if (field.Padding.Left != pad) field.Padding = new Padding(pad,0,pad,0);
                            x+=w;
                        }
                        if (HeightNeeded != null) HeightNeeded(y+rowHeight+1);
                    }
                }
                finally { arranging = false; }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(Hairline)) e.Graphics.DrawLine(pen,0,0,Width,0);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) fieldTips.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class PageButton : Button
        {
            private bool selected;
            public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
            public PageButton(string label, int width)
            {
                Width = width; Height = 46; Text = label; Cursor = Cursors.Hand; Margin = Padding.Empty;
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                Font = new Font("Segoe UI Semibold", 10f); BackColor = Raised;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(ClientRectangle.Contains(PointToClient(Cursor.Position)) ? Color.FromArgb(69, 69, 69) : Raised);
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, selected ? Ink : Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (selected) using (var brush = new SolidBrush(Accent)) g.FillRectangle(brush, 9, Height - 3, Width - 18, 3);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -6));
            }
        }

        private sealed class SummaryBand : Control
        {
            private static readonly Color Background = Color.FromArgb(48, 50, 51);
            private static readonly Color Frame = Color.FromArgb(115, 119, 119);
            private static readonly Color Track = Color.FromArgb(39, 41, 42);
            private static readonly Color GridLine = Color.FromArgb(63, 66, 66);
            private static readonly Color CpuColor = Color.FromArgb(51, 201, 51);
            private static readonly Color GpuColor = Color.FromArgb(250, 166, 47);
            private static readonly Color RamColor = Color.FromArgb(105, 95, 255);
            private readonly Font caption = new Font("Segoe UI Semibold", 9.6f);
            private readonly Font metric = new Font("Segoe UI Semibold", 10f);
            private readonly ToolTip meterTip = new ToolTip();
            private ProcessSnapshot stats;
            private Rectangle[] coreCells = new Rectangle[0];
            private Rectangle gpuCell, ramCell;
            private string lastTip = "";
            public SummaryBand() { DoubleBuffered = true; BackColor = Background; }
            public void UpdateSnapshot(ProcessSnapshot value)
            {
                stats = value;
                AccessibleName = "CPU: " + Percent(value.Cpu) + ", GPU: " + Percent(value.Gpu) + ", RAM: " + Percent(value.MemoryPercent);
                Invalidate();
            }
            private static string Percent(double n)
            {
                return double.IsNaN(n) ? "—" : n.ToString("0.#", English) + "%";
            }
            private static Rectangle Box(int x, int y, int width, int height)
            {
                return new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                float d = g.DpiX / 96f;
                g.Clear(Background);
                int margin = (int)(15*d), gap = (int)(8*d), top = (int)(12*d), y = (int)(19*d);
                int net = Width - 2*margin - 2*gap;
                int cpuWidth = (int)(net * 0.71), gpuWidth = (net-cpuWidth)/2;
                var groups = new [] {
                    Box(margin, y, cpuWidth, Height-y-top),
                    Box(margin+cpuWidth+gap,y,gpuWidth,Height-y-top),
                    Box(margin+cpuWidth+gpuWidth+2*gap,y,net-cpuWidth-gpuWidth,Height-y-top)
                };
                string[] names = {"CPU","GPU","RAM"};
                double[] values = stats == null ? new [] {double.NaN,double.NaN,double.NaN} :
                    new [] {stats.Cpu,stats.Gpu,stats.MemoryPercent};
                int inset = (int)(10*d);
                using (var framePen = new Pen(Frame))
                using (var dark = new SolidBrush(Background))
                {
                    for (int i=0; i<3; i++)
                    {
                        var group = groups[i];
                        g.DrawRectangle(framePen, group.X, group.Y, group.Width-1, group.Height-1);
                        string v = Percent(values[i]);
                        var style = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                        var labelSize = TextRenderer.MeasureText(g, names[i], caption, Size.Empty, style);
                        var metricSize = TextRenderer.MeasureText(g, v, metric, Size.Empty, style);
                        int lx = group.X+inset, rx = group.Right-inset-metricSize.Width;
                        g.FillRectangle(dark, lx-3, group.Y-2, labelSize.Width+6, 5);
                        g.FillRectangle(dark, rx-3, group.Y-2, metricSize.Width+6, 5);
                        TextRenderer.DrawText(g,names[i],caption,new Point(lx,group.Y-labelSize.Height/2),Color.White,style);
                        TextRenderer.DrawText(g,v,metric,new Point(rx,group.Y-metricSize.Height/2),Color.White,style);
                        var interior = Box(group.Left+inset,group.Top+(int)(15*d),group.Width-2*inset,group.Height-(int)(23*d));
                        if (i == 0) DrawCores(g, interior, d);
                        else
                        {
                            if (i == 1) gpuCell=group; else ramCell=group;
                            DrawMeter(g, interior, values[i], i==1 ? GpuColor : RamColor);
                        }
                    }
                }
            }
            private void DrawCores(Graphics g, Rectangle bounds, float d)
            {
                var samples = stats == null ? null : stats.CoreCpu;
                if (samples == null || samples.Length == 0)
                {
                    coreCells = new Rectangle[0];
                    DrawMeter(g, bounds, stats == null ? double.NaN : stats.Cpu, CpuColor);
                    return;
                }
                int count = samples.Length;
                int rows = count > 1 ? 2 : 1;
                int maxColumns = Math.Max(1, bounds.Width/(int)Math.Max(3,7*d));
                rows = Math.Max(rows, (count+maxColumns-1)/maxColumns);
                int cols = (count+rows-1)/rows;
                int gutter = Math.Max(1,(int)(2*d));
                if (coreCells.Length != count) coreCells = new Rectangle[count];
                for (int i=0; i<count; i++)
                {
                    int col = i%cols, row = i/cols;
                    int l = bounds.Left + col*bounds.Width/cols;
                    int t = bounds.Top + row*bounds.Height/rows;
                    int r = bounds.Left + (col+1)*bounds.Width/cols;
                    int b = bounds.Top + (row+1)*bounds.Height/rows;
                    coreCells[i] = Box(l, t, r-l-(col+1==cols ? 0 : gutter), b-t-(row+1==rows ? 0 : gutter));
                    DrawMeter(g,coreCells[i],samples[i],CpuColor);
                }
            }
            private static void DrawMeter(Graphics g, Rectangle cell, double value, Color color)
            {
                using (var background = new SolidBrush(Track)) g.FillRectangle(background, cell);
                using (var border = new Pen(GridLine)) g.DrawRectangle(border,cell.X,cell.Y,cell.Width-1,cell.Height-1);
                if (double.IsNaN(value) || value <= 0 || cell.Width <= 2 || cell.Height <= 2) return;
                var inner = Rectangle.Inflate(cell,-1,-1);
                int h = Math.Max(1,(int)Math.Round(inner.Height*Math.Min(1,value/100)));
                using (var brush = new SolidBrush(color)) g.FillRectangle(brush,inner.X,inner.Bottom-h,inner.Width,h);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (stats == null) return;
                string text = "";
                if (gpuCell.Contains(e.Location)) text = "GPU · " + Percent(stats.Gpu);
                else if (ramCell.Contains(e.Location))
                {
                    text = "RAM · " + Percent(stats.MemoryPercent);
                    if (stats.MemoryTotalGb > 0) text += " · " + stats.MemoryUsedGb.ToString("N1",English) + " / " + stats.MemoryTotalGb.ToString("N1",English) + " GiB";
                }
                else if (stats.CoreCpu != null)
                {
                    for (int i=0; i<Math.Min(coreCells.Length,stats.CoreCpu.Length); i++)
                        if (coreCells[i].Contains(e.Location)) { text = "Logical processor " + i + " · " + Percent(stats.CoreCpu[i]); break; }
                }
                if (lastTip != text) { lastTip=text; meterTip.SetToolTip(this,text); }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { caption.Dispose(); metric.Dispose(); meterTip.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
