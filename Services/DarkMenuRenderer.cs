using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeSessionsSidekick.Services;

public class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(System.Drawing.Point.Empty, e.Item.Size);
        var color = e.Item.Selected
            ? ColorTranslator.FromHtml("#3A3A6A")
            : ColorTranslator.FromHtml("#252535");

        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? ColorTranslator.FromHtml("#E0E0E0")
            : ColorTranslator.FromHtml("#666677");
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(ColorTranslator.FromHtml("#252535"));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(ColorTranslator.FromHtml("#40606080"));
        var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(pen, rect);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(ColorTranslator.FromHtml("#40606080"));
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No image margin background
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2,
            e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
        using var brush = new SolidBrush(ColorTranslator.FromHtml("#3A3A6A"));
        using var pen = new Pen(ColorTranslator.FromHtml("#A0B0FF"));
        e.Graphics.FillRectangle(brush, rect);
        e.Graphics.DrawRectangle(pen, rect);

        // Draw checkmark
        using var checkPen = new Pen(ColorTranslator.FromHtml("#A0B0FF"), 2);
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(checkPen, [
            new System.Drawing.Point(cx - 4, cy),
            new System.Drawing.Point(cx - 1, cy + 3),
            new System.Drawing.Point(cx + 5, cy - 4)
        ]);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = ColorTranslator.FromHtml("#888899");
        base.OnRenderArrow(e);
    }
}

public class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => ColorTranslator.FromHtml("#40606080");
    public override Color MenuItemSelected => ColorTranslator.FromHtml("#3A3A6A");
    public override Color MenuItemBorder => ColorTranslator.FromHtml("#3A3A6A");
    public override Color MenuStripGradientBegin => ColorTranslator.FromHtml("#252535");
    public override Color MenuStripGradientEnd => ColorTranslator.FromHtml("#252535");
    public override Color ToolStripDropDownBackground => ColorTranslator.FromHtml("#252535");
    public override Color ImageMarginGradientBegin => ColorTranslator.FromHtml("#252535");
    public override Color ImageMarginGradientMiddle => ColorTranslator.FromHtml("#252535");
    public override Color ImageMarginGradientEnd => ColorTranslator.FromHtml("#252535");
    public override Color SeparatorDark => ColorTranslator.FromHtml("#40606080");
    public override Color SeparatorLight => ColorTranslator.FromHtml("#40606080");
    public override Color CheckBackground => ColorTranslator.FromHtml("#3A3A6A");
    public override Color CheckSelectedBackground => ColorTranslator.FromHtml("#4A4A7A");
}
