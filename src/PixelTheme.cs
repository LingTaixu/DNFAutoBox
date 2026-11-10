// Native WinForms adaptation of NES.css 2.3.0 (MIT).
// Original: Copyright (c) 2018 B.C.Rikko. See embedded AutoBox.NES-LICENSE.txt.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoBox
{
    internal static class PixelTheme
    {
        internal static readonly Color Ink = Color.FromArgb(33, 37, 41);
        internal static readonly Color Canvas = Color.FromArgb(237, 234, 224);
        internal static readonly Color Paper = Color.FromArgb(255, 253, 247);
        internal static readonly Color Muted = Color.FromArgb(107, 112, 109);
        internal static readonly Color Blue = Color.FromArgb(32, 156, 238);
        internal static readonly Color Green = Color.FromArgb(146, 204, 65);
        internal static readonly Color Yellow = Color.FromArgb(247, 213, 29);
        internal static readonly Color Red = Color.FromArgb(231, 110, 85);
        private static readonly PrivateFontCollection PixelFonts = new PrivateFontCollection();
        private static readonly FontFamily PixelFamily;
        internal static readonly Font BodyFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        internal static readonly Font NumberFont;
        internal static readonly Font TitleFont;
        internal static readonly Icon AppIcon;

        [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr memory, uint size, IntPtr reserved, ref uint count);

        static PixelTheme()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("AutoBox.PixelFont.ttf"))
            {
                if (stream == null) throw new InvalidOperationException("缺少内嵌像素字体，请重新构建程序。");
                var bytes = new byte[checked((int)stream.Length)];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException();
                    read += count;
                }
                IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                    PixelFonts.AddMemoryFont(memory, bytes.Length);
                    uint count = 0;
                    AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref count);
                }
                finally { Marshal.FreeCoTaskMem(memory); }
            }
            PixelFamily = PixelFonts.Families[0];
            NumberFont = new Font(PixelFamily, 12F, FontStyle.Regular, GraphicsUnit.Point);
            TitleFont = new Font(PixelFamily, 18F, FontStyle.Regular, GraphicsUnit.Point);
            using (Stream stream = assembly.GetManifestResourceStream("AutoBox.App.ico"))
                AppIcon = stream == null ? SystemIcons.Application : new Icon(stream);
        }

        internal static int Px(Graphics graphics, int value)
        {
            return Math.Max(1, (int)Math.Round(value * graphics.DpiX / 96F));
        }

        internal static void Fill(Graphics graphics, Color color, Rectangle rectangle)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, rectangle);
        }

        internal static void Box(Graphics graphics, Rectangle bounds, Color face, Color outside, bool shadow)
        {
            int step = Px(graphics, 3);
            Fill(graphics, outside, bounds);
            if (shadow)
            {
                Fill(graphics, Ink, new Rectangle(bounds.X + step * 2, bounds.Y + step * 2, bounds.Width - step * 2, bounds.Height - step * 2));
                bounds.Width -= step;
                bounds.Height -= step;
            }
            Fill(graphics, face, Rectangle.Inflate(bounds, -step, -step));
            Fill(graphics, Ink, new Rectangle(bounds.X + step, bounds.Y, bounds.Width - step * 2, step));
            Fill(graphics, Ink, new Rectangle(bounds.X + step, bounds.Bottom - step, bounds.Width - step * 2, step));
            Fill(graphics, Ink, new Rectangle(bounds.X, bounds.Y + step, step, bounds.Height - step * 2));
            Fill(graphics, Ink, new Rectangle(bounds.Right - step, bounds.Y + step, step, bounds.Height - step * 2));
        }

        internal static void Text(Graphics graphics, string text, Font font, Color color, Rectangle rectangle, StringAlignment alignment)
        {
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericDefault))
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                graphics.DrawString(text, font, brush, rectangle, format);
            }
        }
    }

    internal enum PixelButtonKind { Normal, Primary, Stop, Pause }

    internal sealed class PixelButton : Button
    {
        internal PixelButtonKind Kind { get; set; }
        private bool hovered;
        private bool pressed;
        internal PixelButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = PixelTheme.BodyFont;
            Margin = new Padding(0);
        }
        protected override void OnMouseEnter(EventArgs args) { hovered = true; Invalidate(); base.OnMouseEnter(args); }
        protected override void OnMouseLeave(EventArgs args) { hovered = false; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnMouseDown(MouseEventArgs args) { if (args.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(args); }
        protected override void OnMouseUp(MouseEventArgs args) { pressed = false; Invalidate(); base.OnMouseUp(args); }
        protected override void OnMouseCaptureChanged(EventArgs args) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(args); }
        protected override void OnKeyDown(KeyEventArgs args) { if (args.KeyCode == Keys.Space) pressed = true; Invalidate(); base.OnKeyDown(args); }
        protected override void OnKeyUp(KeyEventArgs args) { pressed = false; Invalidate(); base.OnKeyUp(args); }
        protected override void OnEnabledChanged(EventArgs args) { if (!Enabled) pressed = false; Invalidate(); base.OnEnabledChanged(args); }
        protected override void OnGotFocus(EventArgs args) { Invalidate(); base.OnGotFocus(args); }
        protected override void OnLostFocus(EventArgs args) { pressed = false; Invalidate(); base.OnLostFocus(args); }

        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.None;
            Color face = Kind == PixelButtonKind.Primary ? PixelTheme.Blue : Kind == PixelButtonKind.Stop ? PixelTheme.Red : Kind == PixelButtonKind.Pause ? PixelTheme.Yellow : PixelTheme.Paper;
            Color text = Kind == PixelButtonKind.Primary || Kind == PixelButtonKind.Stop ? Color.White : PixelTheme.Ink;
            if (!Enabled) { face = Color.FromArgb(219, 220, 211); text = Color.FromArgb(131, 135, 128); }
            else if (hovered) face = ControlPaint.Light(face, 0.14F);
            int step = PixelTheme.Px(graphics, 3);
            Rectangle bounds = ClientRectangle;
            if (pressed && Enabled) bounds = new Rectangle(step, step, Width - step, Height - step);
            PixelTheme.Box(graphics, bounds, face, Parent == null ? PixelTheme.Canvas : Parent.BackColor, !pressed);
            Color shade = Enabled ? ControlPaint.Dark(face, 0.17F) : Color.FromArgb(187, 190, 180);
            PixelTheme.Fill(graphics, shade, new Rectangle(bounds.X + step, bounds.Bottom - step * 3, bounds.Width - step * 3, step));
            var textBounds = new Rectangle(bounds.X + step * 2, bounds.Y + step, bounds.Width - step * 4, bounds.Height - step * 3);
            PixelTheme.Text(graphics, Text, Font, text, textBounds, StringAlignment.Center);
            if (Focused && ShowFocusCues && Enabled)
                PixelTheme.Fill(graphics, text, new Rectangle(bounds.X + step * 3, bounds.Y + step * 3, step, step));
        }
    }

    internal sealed class PixelComboBox : ComboBox
    {
        internal bool PixelText { get; set; }
        internal PixelComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            IntegralHeight = false;
            DropDownHeight = 340;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            Font = PixelTheme.BodyFont;
            ItemHeight = 28;
            BackColor = PixelTheme.Paper;
            ForeColor = PixelTheme.Ink;
            Cursor = Cursors.Hand;
        }
        protected override void OnDrawItem(DrawItemEventArgs args)
        {
            bool selected = (args.State & DrawItemState.Selected) != 0;
            PixelTheme.Fill(args.Graphics, selected ? PixelTheme.Blue : PixelTheme.Paper, args.Bounds);
            string text = args.Index >= 0 && args.Index < Items.Count ? GetItemText(Items[args.Index]) : "";
            var bounds = args.Bounds;
            bounds.X += PixelTheme.Px(args.Graphics, 9);
            bounds.Width -= PixelTheme.Px(args.Graphics, 18);
            PixelTheme.Text(args.Graphics, text, PixelText ? PixelTheme.NumberFont : Font,
                selected ? Color.White : PixelTheme.Ink, bounds, StringAlignment.Near);
        }
        protected override void OnSelectedIndexChanged(EventArgs args) { base.OnSelectedIndexChanged(args); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs args) { base.OnEnabledChanged(args); Invalidate(); }
        protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
        protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x000f)
            {
                using (var graphics = Graphics.FromHwnd(Handle)) PaintClosed(graphics);
            }
            else if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != IntPtr.Zero)
            {
                using (var graphics = Graphics.FromHdc(message.WParam)) PaintClosed(graphics);
            }
        }
        private void PaintClosed(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            int step = PixelTheme.Px(graphics, 3);
            Color face = Enabled ? PixelTheme.Paper : Color.FromArgb(229, 230, 220);
            Color ink = Enabled ? PixelTheme.Ink : Color.FromArgb(145, 150, 140);
            PixelTheme.Box(graphics, ClientRectangle, face, Parent == null ? PixelTheme.Canvas : Parent.BackColor, false);
            Rectangle text = new Rectangle(step * 3, step, Width - step * 15, Height - step * 2);
            string selected = SelectedItem == null ? "请选择窗口" : GetItemText(SelectedItem);
            PixelTheme.Text(graphics, selected, PixelText ? PixelTheme.NumberFont : Font, ink, text, StringAlignment.Near);
            int x = Width - step * 9;
            int y = Height / 2 - step;
            for (int row = 0; row < 3; row++)
                PixelTheme.Fill(graphics, ink, new Rectangle(x + step * row, y + step * row, step * (5 - row * 2), step));
            if (Focused && Enabled)
                PixelTheme.Fill(graphics, PixelTheme.Blue, new Rectangle(step * 2, Height - step * 2, Width - step * 4, step));
        }
    }

    internal class PixelPanel : Panel
    {
        internal PixelPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = PixelTheme.Paper;
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            PixelTheme.Box(args.Graphics, ClientRectangle, BackColor, Parent == null ? PixelTheme.Canvas : Parent.BackColor, true);
            base.OnPaint(args);
        }
    }

    internal sealed class PixelStatusPanel : PixelPanel
    {
        private Color accent = PixelTheme.Blue;
        internal Color Accent { get { return accent; } set { if (accent != value) { accent = value; Invalidate(); } } }
        internal PixelStatusPanel() { Padding = new Padding(38, 7, 12, 10); }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            int step = PixelTheme.Px(args.Graphics, 3);
            var dot = new Rectangle(step * 4, Height / 2 - step * 2, step * 4, step * 4);
            PixelTheme.Fill(args.Graphics, PixelTheme.Ink, dot);
            PixelTheme.Fill(args.Graphics, accent, Rectangle.Inflate(dot, -step, -step));
        }
    }

    internal sealed class PixelHeader : Control
    {
        internal PixelHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics graphics = args.Graphics;
            int step = PixelTheme.Px(graphics, 3);
            var icon = new Rectangle(0, step * 2, step * 16, step * 14);
            PixelTheme.Box(graphics, icon, PixelTheme.Green, BackColor, true);
            for (int row = 0; row < 2; row++)
                for (int column = 0; column < 3; column++)
                    PixelTheme.Fill(graphics, PixelTheme.Ink, new Rectangle(step * (3 + column * 3), step * (5 + row * 3), step * 2, step * 2));
            PixelTheme.Fill(graphics, PixelTheme.Ink, new Rectangle(step * 3, step * 11, step * 8, step));
            PixelTheme.Text(graphics, "AUTO BOX", PixelTheme.TitleFont, PixelTheme.Ink, new Rectangle(step * 22, 0, Width - step * 22, step * 12), StringAlignment.Near);
            PixelTheme.Text(graphics, "按键循环  ·  by 陈碎碎  如果觉得好用记得把不要的+12增幅邮寄陈碎碎", PixelTheme.BodyFont, PixelTheme.Muted, new Rectangle(step * 22, step * 12, Width - step * 22, step * 7), StringAlignment.Near);
        }
    }
}
