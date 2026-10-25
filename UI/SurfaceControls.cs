using System.Drawing.Drawing2D;

namespace MBrowser;

internal class GlassPanel : Panel
{
    internal bool UseArtwork = true;
    internal GlassPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(14);
        BackColor = Palette.Surface;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 2 || Height <= 2) return;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(18, (Math.Min(Width, Height) - 1) / 2));
        var previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
        if (UseArtwork) ThemeMedia.Draw(e.Graphics, ClientRectangle, ThemeArea.Menu, this);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 2 || Height <= 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(18, (Math.Min(Width, Height) - 1) / 2));
        using var border = new Pen(Color.FromArgb(65, Palette.Text));
        e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class RoundedButton : Button
{
    private float _hover;
    internal RoundedButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); }
    private void Hover(float target) { float start = _hover; UiMotion.Start(this,120,t => { _hover = start + (target-start)*t; Invalidate(); }); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover(0); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), 11);
        using var fill = new SolidBrush(BackColor.A == 0 ? Color.FromArgb(80 + (int)(85*_hover), Palette.Surface) : UiMotion.Blend(BackColor,Palette.Raised,_hover));
        e.Graphics.FillPath(fill, shape);
        var textArea = Width <= 50 ? ClientRectangle : new Rectangle(8, 0, Width - 16, Height);
        if (Image is not null) { e.Graphics.DrawImage(Image, 10, (Height - 18) / 2, 18, 18); textArea.X += 26; textArea.Width -= 26; }
        TextRenderer.DrawText(e.Graphics, Text, Font, textArea, Enabled ? ForeColor : Palette.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
    }
}
