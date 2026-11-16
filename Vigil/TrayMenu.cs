using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Vigil;

/// <summary>
/// The tray icon's menu, drawn in the colours of the window (see Ui/styles.css) instead of the system's.
/// </summary>
public sealed class TrayMenu : ContextMenuStrip
{
    // The same values as the variables of the same names in Ui/styles.css.
    private static readonly Color Surface = Color.FromArgb(0x16, 0x16, 0x16);      // --raised
    private static readonly Color Hover = Color.FromArgb(0x26, 0x26, 0x26);        // a step above --hover, to read on --raised
    private static readonly Color Border = Color.FromArgb(0x2f, 0x2f, 0x2f);       // --border-strong
    private static readonly Color TextColor = Color.FromArgb(0xed, 0xed, 0xed);    // --text
    private static readonly Color Subtle = Color.FromArgb(0x6e, 0x6e, 0x6e);       // --subtle

    /// <summary>True when Windows rounds the menu's corners and draws its border (Windows 11).</summary>
    private bool _rounded;

    public TrayMenu()
    {
        Renderer = new MenuRenderer(this);
        BackColor = Surface;
        ForeColor = TextColor;
        Padding = new Padding(4);
        DropShadowEnabled = true;
    }

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        // Room to breathe, as the rows of the window have.
        if (e.Item is null) return;
        e.Item.Padding = e.Item is ToolStripSeparator ? new Padding(0, 2, 0, 2) : new Padding(0, 5, 8, 5);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        _rounded = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)) == 0;
        if (_rounded)
        {
            int border = Border.R | Border.G << 8 | Border.B << 16; // a COLORREF
            _ = DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class MenuRenderer(TrayMenu menu) : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (menu._rounded) return; // Windows draws it, following the rounded corners
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Graphics g = e.Graphics;
            float radius = 6 * g.DpiX / 96;
            var bounds = new RectangleF(0, 0, e.Item.Width - 1, e.Item.Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = RoundedRectangle(bounds, radius);
            using var brush = new SolidBrush(Hover);
            g.FillPath(brush, path);
            g.SmoothingMode = SmoothingMode.Default;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color color = e.Item.Enabled ? TextColor : Subtle;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            float scale = g.DpiX / 96;
            float x = e.ImageRectangle.Left + e.ImageRectangle.Width / 2f;
            float y = e.ImageRectangle.Top + e.ImageRectangle.Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item.Enabled ? TextColor : Subtle, 1.5f * scale)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            g.DrawLines(pen,
            [
                new PointF(x - 4.5f * scale, y + 0.5f * scale),
                new PointF(x - 1.5f * scale, y + 3.5f * scale),
                new PointF(x + 4.5f * scale, y - 3f * scale)
            ]);
            g.SmoothingMode = SmoothingMode.Default;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
