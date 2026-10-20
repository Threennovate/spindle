using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Spindle.Activity;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// What a split suggestion is built from, at a glance: the entry's time as a thin line, each prompt as a tick in its
/// project's colour (or its repository's, while it has no project), the agents' attention as thin bars under them, and
/// the time the logs don't account for hatched in the colour of the project it stays on. As the Mac's EvidenceStrip,
/// it draws only what the suggestion already holds.
/// </summary>
public sealed class EvidenceStrip : Control
{
    private const double TickHeight = 9;
    private const double BarHeight = 4;
    private readonly MainWindow owner;
    private readonly SplitSuggestion suggestion;

    public EvidenceStrip(MainWindow owner, SplitSuggestion suggestion)
    {
        this.owner = owner;
        this.suggestion = suggestion;
        Height = TickHeight + 2 + BarHeight + 2;
        MinWidth = 120;
        this.Spoken(Spoken(owner, suggestion));
    }

    /// <summary>The strip with the entry's start and end under it.</summary>
    public static Control WithTimes(MainWindow owner, SplitSuggestion suggestion) =>
        Ui.Stack(2, new EvidenceStrip(owner, suggestion), Ui.Columns("Auto,*,Auto", 0,
            Ui.Text(owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(suggestion.Start)), "caption", "tertiary", "mono"),
            new Panel(),
            Ui.Text(owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(suggestion.End)), "caption", "tertiary", "mono")));

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var span = suggestion.End - suggestion.Start;
        if (span <= 0 || width <= 0) return;
        double X(long at) => (double)(at - suggestion.Start) / span * width;
        var barY = TickHeight + 2;
        var line = this.FindResource(ActualThemeVariant, "TextTertiaryBrush") as IBrush ?? Brushes.Gray;
        // The entry's time, behind everything else.
        context.DrawRectangle(line, null, new Rect(0, barY + (BarHeight - 1) / 2, width, 1));

        // Time the logs don't account for, hatched in the colour of the project that keeps it.
        var openColor = suggestion.Fallback is { } fallback ? SplitChips.ColorOf(owner, fallback) : null;
        var hatch = new Pen(Ui.ProjectBrush(openColor) ?? line, 1.25);
        foreach (var (start, end) in suggestion.Open)
        {
            var rect = new Rect(X(start), barY - 1.5, Math.Max(1, X(end) - X(start)), BarHeight + 3);
            using (context.PushClip(rect))
            {
                context.DrawRectangle(Ui.ProjectBrush(openColor, 0.18) ?? line, null, rect);
                for (var offset = rect.Left - rect.Height; offset < rect.Right; offset += 3.5)
                {
                    context.DrawLine(hatch, new Point(offset, rect.Bottom), new Point(offset + rect.Height, rect.Top));
                }
            }
        }

        // Attention, as thin bars where the logs place it.
        foreach (var piece in suggestion.Pieces)
        {
            var rect = new Rect(X(piece.Start), barY, Math.Max(1, X(piece.End) - X(piece.Start)), BarHeight);
            context.DrawRectangle(Ui.ProjectBrush(SplitChips.ColorOf(owner, piece.Target)), null, rect, BarHeight / 2, BarHeight / 2);
        }

        // Each prompt, as a tick above its attention.
        foreach (var prompt in suggestion.Prompts)
        {
            var x = Math.Min(width - 1.5, Math.Max(0, X(prompt.At) - 0.75));
            context.DrawRectangle(Ui.ProjectBrush(SplitChips.ColorOf(owner, prompt.Target)), null, new Rect(x, 0, 1.5, TickHeight), 0.75, 0.75);
        }
    }

    public static string NameOf(MainWindow owner, SplitTarget target) =>
        target.ProjectId is { } id ? owner.Catalog.Project(id)?.Name ?? "a project" : target.Repository?.Name ?? "a repository";

    /// <summary>
    /// The strip in words, for screen readers: each part's time and prompts, what the logs leave open, and where the work
    /// moved. "Atlas 50m, 4 prompts; Beacon 1h 10m, 6 prompts. The work moved to Beacon at 09:50."
    /// </summary>
    public static string Spoken(MainWindow owner, SplitSuggestion suggestion)
    {
        var touches = suggestion.Touches;
        var parts = suggestion.Parts.Select(part =>
        {
            var prompts = touches.GetValueOrDefault(part.Target);
            var text = $"{NameOf(owner, part.Target)} {Durations.HoursMinutes((part.Attention + part.Unattributed) / 1000d)}, {prompts} {(prompts == 1 ? "prompt" : "prompts")}";
            return part.Unattributed > 0 ? text + $", with {Durations.HoursMinutes(part.Unattributed / 1000d)} the agent logs don't account for" : text;
        });
        var sentences = new List<string>
        {
            $"Agent activity from {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(suggestion.Start))} to {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(suggestion.End))}: {string.Join("; ", parts)}.",
        };
        if (suggestion.Stretches is { } stretches)
        {
            sentences.AddRange(stretches.Skip(1).Select(s => $"The work moved to {NameOf(owner, s.Target)} at {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(s.Start))}."));
        }
        return string.Join(" ", sentences);
    }

    /// <summary>The strip with its parts named, for resting on a chip: enough to judge the suggestion without opening the entry.</summary>
    public static Control Popover(MainWindow owner, SplitSuggestion suggestion)
    {
        var touches = suggestion.Touches;
        var stack = Ui.Stack(8, WithTimes(owner, suggestion));
        foreach (var part in suggestion.Parts)
        {
            var prompts = touches.GetValueOrDefault(part.Target);
            stack.Children.Add(Ui.Columns("Auto,*,Auto", 6, Ui.Dot(SplitChips.ColorOf(owner, part.Target), 8).Centered(), Ui.Text(NameOf(owner, part.Target), "medium"),
                Ui.Text($"{prompts} {(prompts == 1 ? "prompt" : "prompts")} · {Durations.HoursMinutes((part.Attention + part.Unattributed) / 1000d)}", "secondary", "mono")));
        }
        if (suggestion.Parts.FirstOrDefault(p => p.Unattributed > 0) is { } open)
        {
            stack.Children.Add(Ui.Wrapped($"Hatched: {Durations.HoursMinutes(open.Unattributed / 1000d)} the logs don't account for, which stays on {NameOf(owner, open.Target)}.", "caption", "secondary"));
        }
        return new Border { Child = stack, Width = 280 };
    }
}
