using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Procesi
{
    internal sealed class PanelContext : ApplicationContext
    {
        private readonly RuleStore rules;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Icon icon;
        private readonly TrayWindow host;
        private MainForm dashboard;
        private MainForm.DashboardState lastState;
        private bool stopping, disposed;

        internal PanelContext(RuleStore rules,bool trayOnly)
        {
            this.rules = rules;
            host = new TrayWindow(this);
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
            menu.Items.Add("Open Process Panel",null,delegate { OpenDashboard(); });
            var login = new ToolStripMenuItem("Start at sign-in");
            login.Click += delegate {
                try { LoginStart.Enabled = !LoginStart.Enabled; }
                catch (Exception ex) {
                    DarkTheme.Message(dashboard,ex.Message,"Process Panel",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }
            };
            menu.Opening += delegate {
                try { login.Checked = LoginStart.Enabled; login.Enabled = true; }
                catch { login.Enabled = false; }
            };
            menu.Items.Add(login);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Process Panel",null,delegate { ExitThread(); });
            DarkTheme.StyleMenu(menu);
            tray.Icon = icon;
            tray.Text = "Process Panel";
            tray.ContextMenuStrip = menu; // Windows opens this menu on right-click.
            tray.MouseClick += TrayMouseClick;
            tray.Visible = true;
            if (!trayOnly) OpenDashboard();
        }

        private void TrayMouseClick(object sender,MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || stopping || menu.Visible) return;
            SetForegroundWindow(host.Handle);
            Point cursor = Cursor.Position;
            Rectangle area = Screen.FromPoint(cursor).WorkingArea;
            var direction = cursor.Y > area.Top + area.Height / 2
                ? ToolStripDropDownDirection.AboveRight : ToolStripDropDownDirection.BelowRight;
            menu.Show(cursor,direction);
        }

        private void OpenDashboard()
        {
            if (stopping) return;
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new MainForm(rules);
                dashboard.RestoreDashboardState(lastState);
                dashboard.FormClosing += delegate(object sender,FormClosingEventArgs e) {
                    if (!stopping && e.CloseReason == CloseReason.UserClosing)
                        lastState = dashboard.CaptureDashboardState();
                };
                dashboard.FormClosed += delegate { dashboard = null; };
            }
            if (dashboard.IsHandleCreated && !IsWindowEnabled(dashboard.Handle))
            {
                IntPtr dialog = GetLastActivePopup(dashboard.Handle);
                if (dialog != dashboard.Handle && IsWindowVisible(dialog))
                {
                    SetForegroundWindow(dialog);
                    return;
                }
            }
            if (dashboard.WindowState == FormWindowState.Minimized)
                dashboard.WindowState = dashboard.RestoreState;
            if (!dashboard.Visible) dashboard.Show();
            dashboard.Activate();
            SetForegroundWindow(dashboard.Handle);
        }

        internal void PostCommand(bool quit) { host.PostCommand(quit); }

        protected override void ExitThreadCore()
        {
            if (stopping) return;
            stopping = true;
            tray.Visible = false;
            if (dashboard != null) dashboard.Close();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                stopping = true;
                if (dashboard != null) dashboard.Dispose();
                tray.Dispose();
                menu.Dispose();
                icon.Dispose();
                host.Dispose();
            }
            base.Dispose(disposing);
        }

        // A hidden top-level window can receive the Windows session-ending broadcast
        // and wake requests even when the dashboard has no controls or window handles.
        private sealed class TrayWindow : NativeWindow, IDisposable
        {
            private const int Open = 0x8001, Quit = 0x8002;
            private readonly PanelContext owner;
            private readonly object gate = new object();

            internal TrayWindow(PanelContext owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams { Caption = "Process Panel Tray", ExStyle = 0x80 });
            }

            internal void PostCommand(bool quit)
            {
                lock (gate)
                {
                    if (Handle != IntPtr.Zero)
                        PostMessage(Handle,quit ? Quit : Open,IntPtr.Zero,IntPtr.Zero);
                }
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Open: owner.OpenDashboard(); return;
                    case Quit: owner.ExitThread(); return;
                    case 0x0011: m.Result = new IntPtr(1); return; // WM_QUERYENDSESSION
                    case 0x0016: // WM_ENDSESSION
                        if (m.WParam != IntPtr.Zero) owner.ExitThread();
                        return;
                    default: base.WndProc(ref m); return;
                }
            }

            public void Dispose() { lock (gate) { DestroyHandle(); } }
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,int msg,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    }
}
