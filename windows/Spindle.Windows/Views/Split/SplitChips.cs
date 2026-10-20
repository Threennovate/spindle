using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// "✦ 1:20 · 0:40", each with its project's colour, or "✦ Split at 10:42" when the work moved: the split agent activity
/// suggests, offered beside an entry. Hours here; percentages are for the editor.
/// </summary>
internal static class SplitChips
{
    public static Control? ForEntry(MainWindow owner, TimeEntryResponse entry)
    {
        if (owner.Splits.Suggestion(entry) is not { } suggestion) return null;
        return Chip(owner, suggestion, entry, () => owner.Splits.ReviewAsync(entry));
    }

    /// <summary>
    /// For the running entry in the timer bar: its suggestion, or "✦ End split at 11:05" when its split's work changed;
    /// an empty placeholder when there is neither.
    /// </summary>
    public static Control ForRunning(MainWindow owner, TimeEntryResponse running) =>
        ForEntry(owner, running) ?? (owner.Splits.Drift(running) is { } drift ? DriftChip(owner, drift) : new Panel { IsVisible = false });

    /// <summary>
    /// "✦ Start at 9:12?": for the first minutes of a running timer, when agents were at work right before it with
    /// nothing tracked. One click moves the start back; the project stays as the person chose it.
    /// </summary>
    public static Control? EarlierStartChip(MainWindow owner, TimeEntryResponse running)
    {
        if (owner.Splits.StartEarlier(running) is not { } offer) return null;
        var clock = owner.Time.Clock(offer.Start);
        var other = offer.OtherProjectId is { } id ? owner.Catalog.Project(id)?.Name : null;
        var label = Ui.Row(5, Ui.Icon("IconSparkle", "brand", "small").Centered());
        if (other is not null)
        {
            var since = Ui.Text($"Agents were in {other} since {clock}", "caption", "secondary");
            since.MaxWidth = 220;
            since.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            label.Children.Add(since.Centered());
            label.Children.Add(Ui.Text("·", "caption", "secondary").Centered());
        }
        // A timer without a project gets the agents' one along with the start.
        var fills = offer.FillsProjectId is { } fillsId ? owner.Catalog.Project(fillsId)?.Name : null;
        var start = fills is null ? $"Start at {clock}?" : $"Start {fills} at {clock}?";
        var startText = Ui.Text(start, "caption", "medium");
        startText.MaxWidth = 280;
        startText.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        label.Children.Add(startText.Centered());
        var chip = Ui.Button(label, () => owner.Desktop.TakeAsync(offer), owner.Notify, "chip").Named("EarlierStart")
            .Tip(fills is not null
                ? $"Agents were working in {fills} from {clock} with nothing tracked. Click to start the timer at {clock} on {fills}."
                : $"Agents were working {(other is null ? "" : $"in {other} ")}from {clock} with nothing tracked. Click to start the timer at {clock}; its project stays as it is.")
            .Spoken(start.TrimEnd('?'));
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    /// <summary>The running split's work changed after it was set: a menu takes it, opens the entry, or puts it off.</summary>
    public static Button DriftChip(MainWindow owner, DriftOffer offer)
    {
        var title = owner.Splits.Title(offer.Drift);
        var label = Ui.Row(5, Ui.Icon("IconSparkle", "brand", "small").Centered(), Ui.Text(title, "caption", "medium", "mono").Centered());
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = owner.Splits.Headline(offer.Drift), IsEnabled = false });
        void Add(string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception exception) { owner.Notify(exception.Message); }
            };
            menu.Items.Add(item);
        }
        Add(title, () => owner.Desktop.TakeAsync(offer));
        Add("Review…", () => owner.Desktop.ReviewAsync(offer));
        Add("Not now", () => owner.Desktop.NotNowAsync(offer));
        var chip = new Button { Content = label, Flyout = menu }.Classed("chip").Named("SplitDrift")
            .Tip($"{owner.Splits.Headline(offer.Drift)}. {owner.Splits.Question(offer)}")
            .Spoken($"{title}: {owner.Splits.Headline(offer.Drift)}");
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    public static Button Chip(MainWindow owner, SplitSuggestion suggestion, TimeEntryResponse entry, Func<Task> action)
    {
        var label = Ui.Row(5, Ui.Icon("IconSparkle", "brand", "small").Centered());
        if (suggestion.Stretches is null)
        {
            var hours = SplitAdvisor.Hours(suggestion, entry);
            for (var index = 0; index < suggestion.Parts.Count; index++)
            {
                if (index > 0) label.Children.Add(Ui.Text("·", "caption", "secondary").Centered());
                label.Children.Add(Ui.Dot(ColorOf(owner, suggestion.Parts[index].Target), 7));
                label.Children.Add(Ui.Text(Durations.ShortClock(hours[index]), "caption", "medium", "mono").Centered());
            }
        }
        else
        {
            label.Children.Add(Ui.Text(owner.Splits.Title(suggestion), "caption", "medium", "mono").Centered());
        }
        var names = string.Join(", ", owner.Splits.Summary(suggestion, entry).Split(" · "));
        var chip = Ui.Button(label, action, owner.Notify, "chip").Named("SplitSuggestion")
            .Spoken(suggestion.Stretches is null ? "Split as agents worked: " + names : $"{owner.Splits.Title(suggestion)}, as agents worked: {names}");
        // Resting on the chip shows what the suggestion is built from.
        ToolTip.SetTip(chip, EvidenceStrip.Popover(owner, suggestion));
        AutomationProperties.SetHelpText(chip, EvidenceStrip.Spoken(owner, suggestion));
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    /// <summary>The colour of a suggested part: its project's, or a stable colour for a repository not linked yet.</summary>
    public static string ColorOf(MainWindow owner, SplitTarget target) =>
        target.ProjectId is { } id ? owner.Catalog.Project(id)?.Color ?? "#8E8E93" : ActivityColors.Of(target.Repository?.Identity ?? "");
}

/// <summary>Colours for repositories in activity lanes, the same palette and hash as the Mac's.</summary>
public static class ActivityColors
{
    private static readonly string[] Palette = ["#497CC4", "#9470C1", "#BE698B", "#C48245", "#799643", "#3B9A83", "#399CB2", "#BA6858"];

    public static string Of(string key)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(key))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return Palette[(int)(hash % (ulong)Palette.Length)];
    }
}
