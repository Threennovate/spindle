using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// A calendar block's split suggestion drawn on the block, only while its ✦ is hovered or focused, as a proposal and not a
/// split: the block stays as it is, the parts are hatched over it in their projects' colours with dashed cuts, each is
/// labelled with its project and time, and one line says what a click does. A weak suggestion is fainter, as it is
/// quieter everywhere else. It only draws what the suggestion holds.
/// </summary>
internal static class SuggestionPreview
{
    /// <summary>A stretch of the block's time and where it would go; <see cref="Cut"/> when the work moved at its start.</summary>
    internal sealed record Row(SplitTarget Target, long From, long To, bool Cut);

    /// <summary>
    /// Where the work moved, within the part of the entry a block shows (<paramref name="from"/> to <paramref name="to"/>,
    /// e.g. one day of an entry over midnight). Empty for shares by percent.
    /// </summary>
    internal static IReadOnlyList<Row> Rows(SplitSuggestion suggestion, long from, long to)
    {
        if (suggestion.Stretches is not { Count: > 0 } stretches || to <= from) return [];
        var rows = new List<Row>();
        for (var index = 0; index < stretches.Count; index++)
        {
            var start = index == 0 ? long.MinValue : stretches[index].Start;
            var end = index == stretches.Count - 1 ? long.MaxValue : stretches[index + 1].Start;
            var (rowFrom, rowTo) = (Math.Max(start, from), Math.Min(end, to));
            if (rowTo <= rowFrom) continue;
            rows.Add(new Row(stretches[index].Target, rowFrom, rowTo, index > 0 && start > from));
        }
        return rows;
    }

    /// <summary>The ✦ on a block, which <paramref name="take"/>s the suggestion when clicked; the calendar draws it on hover and focus.</summary>
    internal static Button Spark(MainWindow owner, TimeEntryResponse entry, SplitSuggestion suggestion, Func<Task> take)
    {
        var weak = suggestion.Strength == SplitStrength.Weak;
        var icon = Ui.Icon(weak ? "IconSparkle" : "IconSparkleFilled", weak ? "secondary" : "brand").Classed("small");
        var spark = new Button
        {
            Content = icon, Padding = new Thickness(2), MinWidth = 0, MinHeight = 0, Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        }.Classed("subtle").Named("CalendarSuggestion").Spoken(Spoken(owner, entry, suggestion));
        AutomationProperties.SetHelpText(spark, EvidenceStrip.Spoken(owner, suggestion));
        ToolTip.SetTip(spark, Tip(owner, suggestion));
        // Above the ✦, so the evidence doesn't cover the suggestion drawn on the block below it.
        ToolTip.SetPlacement(spark, PlacementMode.Top);
        spark.Click += async (_, _) =>
        {
            try
            {
                await take();
            }
            catch (Exception exception)
            {
                owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't split the entry.");
            }
        };
        return spark;
    }

    /// <summary>"Split at 10:42: Atlas until 10:42, then Beacon. Activate to take it."</summary>
    internal static string Spoken(MainWindow owner, TimeEntryResponse entry, SplitSuggestion suggestion)
    {
        var splits = owner.Splits;
        var what = suggestion.Stretches is null ? "Split suggested: " + string.Join(", ", splits.Summary(suggestion, entry).Split(" · ")) : $"{splits.Title(suggestion)}: {splits.Summary(suggestion, entry)}";
        var weak = suggestion.Strength == SplitStrength.Weak ? " The logs back this only lightly." : "";
        return $"{what}.{weak} {(Takeable(suggestion) ? "Activate to take it; Undo puts it back." : "Activate to choose a project for each repository.")}";
    }

    internal static bool Takeable(SplitSuggestion suggestion) => suggestion.Parts.All(p => p.Target.ProjectId is not null);

    private static Control Tip(MainWindow owner, SplitSuggestion suggestion)
    {
        var stack = Ui.Stack(8, Ui.Text(owner.Splits.Title(suggestion) + (suggestion.Stretches is null ? " as agents worked" : ""), "medium"), EvidenceStrip.Popover(owner, suggestion));
        if (suggestion.Strength == SplitStrength.Weak)
            stack.Children.Add(Ui.Wrapped($"The logs back this only lightly: {(int)Math.Round(suggestion.Coverage * 100)}% of the time, few prompts.", "caption", "secondary"));
        stack.Children.Add(Ui.Wrapped(Takeable(suggestion) ? "Click to take it. Undo puts it back." : "Click to choose a project for each repository.", "caption", "secondary"));
        stack.MaxWidth = 280;
        return stack;
    }

    /// <summary>A part of the block the suggestion would give a project, in the block's own coordinates.</summary>
    internal sealed record Part(SplitTarget Target, Rect Area, long Seconds);

    /// <summary>
    /// The suggestion laid out on a block of <paramref name="size"/> showing <paramref name="from"/> to
    /// <paramref name="to"/> of the entry: columns as wide as the shares, or stretches as tall as their time, and the cut
    /// lines between them (vertical for shares, at each move for stretches).
    /// </summary>
    internal static (IReadOnlyList<Part> Parts, IReadOnlyList<(Point From, Point To, long? At)> Dividers) Layout(
        SplitSuggestion suggestion, TimeEntryResponse entry, long from, long to, Size size)
    {
        var parts = new List<Part>();
        var dividers = new List<(Point, Point, long?)>();
        var rows = Rows(suggestion, from, to);
        if (rows.Count == 0)
        {
            var hours = SplitAdvisor.Hours(suggestion, entry);
            var total = Math.Max(1, suggestion.Parts.Sum(p => p.Percent));
            var x = 0d;
            for (var index = 0; index < suggestion.Parts.Count; index++)
            {
                var width = size.Width * suggestion.Parts[index].Percent / total;
                parts.Add(new Part(suggestion.Parts[index].Target, new Rect(x, 0, width, size.Height), hours[index]));
                if (index > 0) dividers.Add((new Point(x, 0), new Point(x, size.Height), null));
                x += width;
            }
            return (parts, dividers);
        }
        var span = (double)(to - from);
        foreach (var row in rows)
        {
            var top = size.Height * (row.From - from) / span;
            parts.Add(new Part(row.Target, new Rect(0, top, size.Width, size.Height * (row.To - row.From) / span), (row.To - row.From) / 1000));
            if (row.Cut) dividers.Add((new Point(0, top), new Point(size.Width, top), row.From));
        }
        return (parts, dividers);
    }

    /// <summary>
    /// "Split Atlas 0:50 · Beacon 1:10 · click ✦ to apply" or "Split at 10:50 · click ✦ to apply": what a click on the ✦
    /// does, in the words the chips use. It names the projects, as narrow parts are labelled with their time alone;
    /// without <paramref name="named"/>, "Split 0:50 · 1:10 · …" for where the names don't fit.
    /// </summary>
    internal static string Hint(SplitSuggestion suggestion, TimeEntryResponse entry, MainWindow owner, bool named = true)
    {
        var hours = SplitAdvisor.Hours(suggestion, entry);
        var what = suggestion.Stretches is null
            ? "Split " + string.Join(" · ", suggestion.Parts.Select((p, i) => (named ? EvidenceStrip.NameOf(owner, p.Target) + " " : "") + Durations.ShortClock(hours[i])))
            : owner.Splits.Title(suggestion);
        return $"{what} · {Action(suggestion)}";
    }

    private static string Action(SplitSuggestion suggestion) => Takeable(suggestion) ? "click ✦ to apply" : "click ✦ to choose projects";

    /// <summary>
    /// The proposal itself, drawn behind the block's text so its title, project and time stay readable: each part
    /// hatched in its project's colour over the block's own fill, with dashed lines where it would be cut. It never looks
    /// like a saved split, which has solid colours and no hatching. Weak suggestions are fainter, with finer dashes.
    /// </summary>
    internal sealed class Fill : Control
    {
        private readonly IReadOnlyList<(Part Part, IBrush? Brush)> parts;
        private readonly IReadOnlyList<(Point From, Point To, long? At)> dividers;
        private readonly bool weak;
        private readonly Rect words;

        /// <param name="text">The block's own words, which the hatching leaves clear.</param>
        public Fill(MainWindow owner, IReadOnlyList<Part> parts, IReadOnlyList<(Point, Point, long?)> dividers, bool weak, Rect text)
        {
            words = text.Inflate(2);
            this.parts = parts.Select(p => (p, Ui.ProjectBrush(SplitChips.ColorOf(owner, p.Target)))).ToList();
            this.dividers = dividers;
            this.weak = weak;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            var text = this.FindResource(ActualThemeVariant, "TextPrimaryBrush") as IBrush ?? Brushes.Gray;
            foreach (var (part, brush) in parts)
            {
                var color = (brush as ISolidColorBrush)?.Color ?? Colors.Gray;
                var area = part.Area;
                if (area.Width <= 0 || area.Height <= 0) continue;
                using (context.PushClip(area))
                {
                    context.FillRectangle(new SolidColorBrush(color, weak ? 0.05 : 0.1), area);
                    using var clear = context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), new RectangleGeometry(words, 3, 3)));
                    var hatch = new Pen(new SolidColorBrush(color, weak ? 0.3 : 0.55), 1);
                    for (var x = area.Left - area.Height; x < area.Right; x += weak ? 8 : 6)
                        context.DrawLine(hatch, new Point(x, area.Bottom), new Point(x + area.Height, area.Top));
                }
            }
            var pen = new Pen(text, weak ? 1 : 1.5) { DashStyle = new DashStyle(weak ? [2, 3] : [4, 3], 0) };
            foreach (var (from, to, _) in dividers) context.DrawLine(pen, from, to);
            // A soft backing under the block's own words, so a cut never runs through its title, project or time.
            if (words.Width > 4 && words.Height > 4 && this.FindResource(ActualThemeVariant, "SolidCardBackgroundBrush") is ISolidColorBrush card)
                context.DrawRectangle(new SolidColorBrush(card.Color, 0.72), null, new RoundedRect(words, 3));
        }
    }

    /// <summary>
    /// The words on the proposal: the hint, each cut's time on its line, and each part's project and time ("Atlas 0:50",
    /// or "0:50" where the name doesn't fit). Each is placed where it covers neither the block's own text
    /// (<paramref name="text"/>) nor another label, nor the <paramref name="hidden"/> right edge under the scroll bar, or
    /// left out: the ✦'s tooltip and name say all of it.
    /// </summary>
    internal static Control Labels(MainWindow owner, SplitSuggestion suggestion, TimeEntryResponse entry, IReadOnlyList<Part> parts,
        IReadOnlyList<(Point From, Point To, long? At)> dividers, Size size, Rect text, double hidden)
    {
        var weak = suggestion.Strength == SplitStrength.Weak;
        var width = Math.Max(0, size.Width - hidden);
        var layer = new LabelLayer(width, [text, new(width - 22, 0, 22, 20)]) { Width = size.Width, Height = size.Height, ClipToBounds = true, IsHitTestVisible = false };
        Border Pill(string words, string? color, params string[] classes)
        {
            var pill = new Border
            {
                Child = Ui.Text(words, ["caption", "mono", .. classes]), CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 0, 5, 1),
                BorderThickness = new Thickness(1), BorderBrush = Ui.ProjectBrush(color, weak ? 0.5 : 0.9),
            }.Res(Border.BackgroundProperty, "SolidCardBackgroundBrush");
            if (pill.BorderBrush is null) pill.Res(Border.BorderBrushProperty, "TextSecondaryBrush");
            return pill;
        }

        // The hint, top right beside the ✦, else bottom right.
        var hint = Hint(suggestion, entry, owner);
        var shorter = Hint(suggestion, entry, owner, named: false);
        Border[] hints = hint == shorter
            ? [Pill(hint, null, "secondary"), Pill(Action(suggestion), null, "secondary")]
            : [Pill(hint, null, "secondary"), Pill(shorter, null, "secondary"), Pill(Action(suggestion), null, "secondary")];
        layer.Add(hints, pill =>
        [
            new(width - 24 - pill.Width, 2),
            new(width - 4 - pill.Width, size.Height - 2 - pill.Height),
        ]);
        // Each cut's time on its line.
        foreach (var (from, _, at) in dividers)
        {
            if (at is not { } instant) continue;
            layer.Add([Pill(owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(instant)), null, "strong")], pill =>
            {
                var y = Math.Clamp(from.Y - pill.Height / 2, 0, Math.Max(0, size.Height - pill.Height));
                return [new((width - pill.Width) / 2, y), new(width - 24 - pill.Width, y)];
            });
        }
        // Each part at its right edge: at the bottom of a column or the first stretch, under the cut of the others.
        foreach (var part in parts)
        {
            var color = SplitChips.ColorOf(owner, part.Target);
            var time = Durations.ShortClock(part.Seconds);
            var area = part.Area;
            layer.Add([Pill($"{EvidenceStrip.NameOf(owner, part.Target)} {time}", color), Pill(time, color)], pill =>
            {
                var right = Math.Min(area.Right, width) - 4 - pill.Width;
                var middle = new Point(area.Left + (Math.Min(area.Right, width) - area.Left - pill.Width) / 2, area.Top + (area.Height - pill.Height) / 2);
                Point top = new(right, area.Top + 3), bottom = new(right, area.Bottom - 3 - pill.Height);
                // Left of the middle, where the cut's time isn't.
                var aside = middle.WithX(area.Left + (Math.Min(area.Right, width) - area.Left) / 4);
                return area.Top > 0 ? [top, bottom, middle, aside] : [bottom, top, middle, aside];
            });
        }
        return layer;
    }

    /// <summary>
    /// Labels placed once their size is known: each takes the first of its spots where it fits, in its long or its short
    /// form, without covering what is already there; the others are left out of sight.
    /// </summary>
    private sealed class LabelLayer(double width, IReadOnlyList<Rect> reserved) : Panel
    {
        private readonly List<(Control[] Forms, Func<Size, IEnumerable<Point>> Spots)> labels = [];

        public LabelLayer Add(Control[] forms, Func<Size, IEnumerable<Point>> spots)
        {
            labels.Add((forms, spots));
            foreach (var form in forms) Children.Add(form);
            return this;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children) child.Measure(Size.Infinity);
            return new Size(Width, Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var taken = reserved.ToList();
            var away = new Rect(-10_000, -10_000, 0, 0);
            foreach (var (forms, spots) in labels)
            {
                var placed = false;
                foreach (var form in forms)
                {
                    var spot = placed ? null : spots(form.DesiredSize).Select(p => new Rect(p, form.DesiredSize)).Cast<Rect?>().FirstOrDefault(r =>
                        r!.Value.Left >= 0 && r.Value.Top >= 0 && r.Value.Right <= width && r.Value.Bottom <= finalSize.Height && !taken.Any(t => t.Intersects(r.Value)));
                    if (spot is { } rect)
                    {
                        taken.Add(rect.Inflate(1));
                        form.Arrange(rect);
                        placed = true;
                    }
                    else form.Arrange(away.WithWidth(form.DesiredSize.Width).WithHeight(form.DesiredSize.Height));
                }
            }
            return finalSize;
        }
    }
}
