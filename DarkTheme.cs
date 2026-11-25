using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Procesi
{
    internal static class DarkTheme
    {
        internal static readonly Color Surface = Color.FromArgb(44,44,44);
        internal static readonly Color Field = Color.FromArgb(27,27,27);
        internal static readonly Color Raised = Color.FromArgb(56,56,56);
        internal static readonly Color Edge = Color.FromArgb(85,85,85);
        internal static readonly Color Text = Color.FromArgb(237,237,237);
        internal static readonly Color Muted = Color.FromArgb(195,195,195);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int length);
        [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd,string theme,string ids);

        public static void ApplyWindow(Form form)
        {
            Action update = delegate {
                int on=1;
                if (DwmSetWindowAttribute(form.Handle,20,ref on,4) != 0)
                    DwmSetWindowAttribute(form.Handle,19,ref on,4);
                int caption=ColorTranslator.ToWin32(Surface), captionText=ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(form.Handle,35,ref caption,4);
                DwmSetWindowAttribute(form.Handle,36,ref captionText,4);
            };
            form.HandleCreated += delegate { update(); };
            if (form.IsHandleCreated) update();
        }

        public static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle=FlatStyle.Flat; button.UseVisualStyleBackColor=false;
            button.BackColor=primary ? Color.FromArgb(66,66,66) : Raised; button.ForeColor=Text;
            button.FlatAppearance.BorderColor=primary ? Color.FromArgb(140,140,140) : Edge;
            button.FlatAppearance.BorderSize=1;
            button.FlatAppearance.MouseOverBackColor=Color.FromArgb(80,80,80);
            button.FlatAppearance.MouseDownBackColor=Color.FromArgb(90,90,90);
        }

        public static void Scrollbars(Control control)
        {
            Action style = delegate { SetWindowTheme(control.Handle,"DarkMode_Explorer",null); };
            control.HandleCreated += delegate { style(); };
            if (control.IsHandleCreated) style();
        }

        public static void StyleMenu(ToolStrip menu)
        {
            menu.RenderMode=ToolStripRenderMode.Professional; menu.Renderer=new MenuRenderer();
            menu.BackColor=Surface; menu.ForeColor=Text;
            StyleItems(menu.Items);
        }
        private static void StyleItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor=Text; item.BackColor=Surface;
                var m=item as ToolStripMenuItem;
                if (m != null && m.HasDropDownItems) StyleMenu(m.DropDown);
            }
        }
        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new MenuColors()) { RoundedEdges=false; }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor=e.Item.Enabled ? Text : Color.Gray; base.OnRenderArrow(e);
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor=e.Item.Enabled ? Text : Color.FromArgb(148,148,148); base.OnRenderItemText(e);
            }
        }
        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color MenuBorder { get { return Edge; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(68,68,68); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(82,82,82); } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Edge; } }
            public override Color SeparatorLight { get { return Surface; } }
            public override Color CheckBackground { get { return Edge; } }
            public override Color CheckSelectedBackground { get { return Edge; } }
        }

        public static DialogResult Message(IWin32Window owner,string text,string title,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton=MessageBoxDefaultButton.Button1)
        {
            using (var form=new Form())
            {
                form.Text=title; form.Font=SystemFonts.MessageBoxFont; form.AutoScaleMode=AutoScaleMode.Dpi;
                form.ShowInTaskbar=false; form.ShowIcon=false; form.MinimizeBox=false; form.MaximizeBox=false;
                form.FormBorderStyle=FormBorderStyle.FixedDialog; form.StartPosition=FormStartPosition.CenterParent;
                form.BackColor=Surface; form.ForeColor=Text;
                int messageHeight=TextRenderer.MeasureText(text,form.Font,new Size(472,1600),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
                form.ClientSize=new Size(520,Math.Min(380,Math.Max(62,messageHeight+18))+82);
                var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(17,16,17,12) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,41));
                var content=new TextBox { Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Surface,ForeColor=Text,
                    Dock=DockStyle.Fill,ScrollBars=messageHeight>370 ? ScrollBars.Vertical : ScrollBars.None,Text=text,TabStop=false };
                Scrollbars(content);
                var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false };
                bool confirm=buttons==MessageBoxButtons.YesNo;
                var accept=new Button {Text=confirm ? "Yes" : "OK",Size=new Size(94,31), DialogResult=confirm ? DialogResult.Yes : DialogResult.OK};
                if (confirm)
                {
                    var decline=new Button {Text="No",Size=new Size(94,31),DialogResult=DialogResult.No};
                    StyleButton(decline,defaultButton==MessageBoxDefaultButton.Button2); actions.Controls.Add(decline);
                    form.CancelButton=decline;
                    if (defaultButton==MessageBoxDefaultButton.Button2) form.AcceptButton=decline;
                }
                else form.CancelButton=accept;
                StyleButton(accept,defaultButton==MessageBoxDefaultButton.Button1);
                actions.Controls.Add(accept); if (form.AcceptButton==null) form.AcceptButton=accept;
                layout.Controls.Add(content,0,0);layout.Controls.Add(actions,0,1);form.Controls.Add(layout);
                ApplyWindow(form);
                form.Shown+=delegate { ((Control)form.AcceptButton).Focus(); };
                return form.ShowDialog(owner);
            }
        }
    }
}
