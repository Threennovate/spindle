using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Spindle.Windows.Views;

/// <summary>
/// The edge of untracked time: diagonal hatching in a band inside a rounded outline, with nothing filled in, unlike the
/// dashed outline of what agents proposed. As the Mac's HatchedEdge.
/// </summary>
public sealed class HatchedEdge : Control
{
    private readonly IBrush? brush;

    /// <param name="hex">The project's colour; the secondary text colour without one.</param>
    public HatchedEdge(string? hex)
    {
        brush = Ui.ProjectBrush(hex, 0.7);
        IsHitTestVisible = false;
    }

    public double Band { get; init; } = 4;
    public double Radius { get; init; } = 5;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var color = brush ?? this.FindResource(ActualThemeVariant, "TextSecondaryBrush") as IBrush ?? Brushes.Gray;
        var outer = new RectangleGeometry(bounds, Radius, Radius);
        var inner = new RectangleGeometry(bounds.Deflate(Band), Math.Max(0, Radius - Band), Math.Max(0, Radius - Band));
        var pen = new Pen(color, 1);
        using (context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner)))
        {
            for (var x = -bounds.Height; x < bounds.Width; x += 5)
            {
                context.DrawLine(pen, new Point(x, bounds.Height), new Point(x + bounds.Height, 0));
            }
        }
    }
}
