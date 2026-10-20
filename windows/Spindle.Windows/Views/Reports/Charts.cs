using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>Time by day, week or month: billable and non-billable stacked in each bar, hours on the side.</summary>
public sealed class TimeChart : Control
{
    private readonly ReportBucket[] buckets;
    private readonly ReportResolution resolution;

    public TimeChart(ReportBucket[] buckets, ReportResolution resolution)
    {
        this.buckets = buckets;
        this.resolution = resolution;
        Height = 220;
        ClipToBounds = false;
    }

    private IBrush Resource(string key) => this.FindResource(ActualThemeVariant, key) as IBrush ?? Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var typeface = new Typeface(this.FindResource("SpindleFontFamily") as FontFamily ?? FontFamily.Default);
        var secondary = Resource("TextSecondaryBrush");
        var primary = (Resource("TextPrimaryBrush") as ISolidColorBrush)?.Color ?? Colors.Black;
        FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush);
        var maxHours = Math.Max(0.25, buckets.Select(b => b.Seconds / 3600d).DefaultIfEmpty(0).Max());
        var ticks = ReportFormat.HourTicks(maxHours);
        var top = ticks[^1];
        var labels = ticks.Select(t => Text(ReportFormat.HourTick(t), secondary)).ToArray();
        var axis = labels.Max(l => l.Width) + 8;
        const double legend = 22;
        const double bottom = 20;
        var plot = new Rect(axis, legend, Math.Max(10, width - axis), Math.Max(10, height - legend - bottom));
        var grid = new Pen(Resource("DividerBrush"), 1);
        for (var index = 0; index < ticks.Length; index++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * ticks[index] / top) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawText(labels[index], new Point(axis - 8 - labels[index].Width, y - labels[index].Height / 2));
        }
        var billable = new SolidColorBrush(primary, 0.72);
        var other = new SolidColorBrush(primary, 0.2);
        var swatch = 10d;
        var legendBillable = Text("Billable", secondary);
        var legendOther = Text("Non-billable", secondary);
        var x = width - legendOther.Width;
        context.DrawText(legendOther, new Point(x, 0));
        context.FillRectangle(other, new Rect(x - swatch - 5, 3, swatch, swatch), 2);
        x -= swatch + 18 + legendBillable.Width;
        context.DrawText(legendBillable, new Point(x, 0));
        context.FillRectangle(billable, new Rect(x - swatch - 5, 3, swatch, swatch), 2);
        if (buckets.Length == 0) return;
        var slot = plot.Width / buckets.Length;
        var bar = Math.Min(48, slot * 0.62);
        var every = Math.Max(1, buckets.Length / 12);
        var spansYears = buckets[0].Start.Year != buckets[^1].Start.Year;
        var english = CultureInfo.GetCultureInfo("en-GB");
        for (var index = 0; index < buckets.Length; index++)
        {
            var bucket = buckets[index];
            var left = plot.Left + slot * index + (slot - bar) / 2;
            var paid = plot.Height * bucket.BillableSeconds / 3600d / top;
            var rest = plot.Height * (bucket.Seconds - bucket.BillableSeconds) / 3600d / top;
            if (rest > 0) context.FillRectangle(other, new Rect(left, plot.Bottom - paid - rest, bar, rest), 2);
            if (paid > 0) context.FillRectangle(billable, new Rect(left, plot.Bottom - paid, bar, paid), 2);
            if (index % every != 0) continue;
            var label = resolution switch
            {
                ReportResolution.Month => bucket.Start.ToString(spansYears ? "MMM yy" : "MMM", english),
                ReportResolution.Week => bucket.Start.ToString("d MMM", english),
                _ => bucket.Start.ToString(buckets.Length <= 7 ? "ddd d" : "%d", english),
            };
            var text = Text(label, secondary);
            context.DrawText(text, new Point(plot.Left + slot * index + (slot - text.Width) / 2, plot.Bottom + 4));
        }
    }
}

/// <summary>A ring with a segment per group, the total in its middle.</summary>
public sealed class Donut : Control
{
    private readonly (double Seconds, string? Color)[] slices;
    private readonly string total;

    public Donut((double Seconds, string? Color)[] slices, string total)
    {
        this.slices = slices;
        this.total = total;
        Width = 180;
        Height = 180;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = size / 2;
        var inner = outer * 0.62;
        var sum = slices.Sum(s => s.Seconds);
        var tertiary = this.FindResource(ActualThemeVariant, "TextTertiaryBrush") as IBrush ?? Brushes.Gray;
        if (sum <= 0)
        {
            context.DrawEllipse(null, new Pen(tertiary, outer - inner), center, (outer + inner) / 2, (outer + inner) / 2);
        }
        var angle = -Math.PI / 2;
        var inset = slices.Length > 1 ? 0.012 : 0;
        foreach (var (seconds, color) in slices)
        {
            if (seconds <= 0) continue;
            var sweep = seconds / sum * Math.PI * 2;
            var from = angle + inset;
            var to = angle + sweep - inset;
            if (to > from)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    Point On(double radius, double a) => new(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
                    var large = to - from > Math.PI;
                    path.BeginFigure(On(outer, from), true);
                    path.ArcTo(On(outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise);
                    path.LineTo(On(inner, to));
                    path.ArcTo(On(inner, from), new Size(inner, inner), 0, large, SweepDirection.CounterClockwise);
                    path.EndFigure(true);
                }
                var brush = Ui.ProjectBrush(color) ?? new SolidColorBrush(((tertiary as ISolidColorBrush)?.Color ?? Colors.Gray), 0.5);
                context.DrawGeometry(brush, null, geometry);
            }
            angle += sweep;
        }
        var typeface = new Typeface(this.FindResource("SpindleFontFamily") as FontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var primary = this.FindResource(ActualThemeVariant, "TextPrimaryBrush") as IBrush ?? Brushes.Black;
        var secondary = this.FindResource(ActualThemeVariant, "TextSecondaryBrush") as IBrush ?? Brushes.Gray;
        var value = new FormattedText(total, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 16, primary);
        var caption = new FormattedText("total", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(typeface.FontFamily), 12, secondary);
        context.DrawText(value, new Point(center.X - value.Width / 2, center.Y - value.Height + 2));
        context.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + 2));
    }
}
