using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Core;
using Spindle.Windows.Desktop;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>
/// Where the person goes through what their agents proposed: by day, each proposal beside the entries already there,
/// with what stands in its way, to add, edit or discard one at a time or all at once. As the Mac's ProposalReview.
/// Edit closes the review with the proposal, for the window to open the editor; the review opens again after.
/// </summary>
public sealed class ProposalReview : UserControl
{
    private readonly MainWindow owner;
    private readonly ProposalInbox inbox;
    private readonly HashSet<string> busy = [];
    private bool addingAll;

    public ProposalReview(MainWindow owner)
    {
        this.owner = owner;
        inbox = owner.Proposals;
        Render();
        inbox.Changed += Changed;
        DetachedFromVisualTree += (_, _) => inbox.Changed -= Changed;
    }

    private void Changed() => Avalonia.Threading.Dispatcher.UIThread.Post(Render);

    private UserTime Time => owner.Time;
    private Catalog Catalog => owner.Catalog;

    private void Render()
    {
        var pending = inbox.Pending;
        var issues = pending.ToDictionary(p => p.Id, p => inbox.Issues(p, pending));
        var ready = pending.Where(p => !issues[p.Id].Any(i => i.Blocking)).ToArray();
        var header = Header(pending);
        header.Margin = new Thickness(24, 22, 24, 18);
        Control body;
        if (pending.Length == 0 && inbox.Saved.Count == 0)
        {
            var empty = Ui.Stack(6, Ui.Icon("IconBot", "secondary", "large").Centered(), Ui.Text("Nothing waiting", "bodyLarge").Centered(),
                Ui.Wrapped("When you ask Claude Code or Codex to add or change time, what they propose waits here.", "secondary"));
            ((TextBlock)empty.Children[2]).TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(48, 56);
            empty.Name = "ProposalsEmpty";
            body = empty;
        }
        else
        {
            var sections = Ui.Stack(22);
            foreach (var day in pending.Select(DayOf).Distinct().Order())
                sections.Children.Add(DaySection(day, pending.Where(p => DayOf(p) == day).ToArray(), issues));
            if (inbox.Saved.Count > 0) sections.Children.Add(SavedSection());
            sections.Margin = new Thickness(24, 18, 24, 20);
            body = new ScrollViewer { Content = sections, MaxHeight = 520 };
        }
        var footer = new Border { Child = Footer(pending, ready), Padding = new Thickness(24, 16) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        Content = new DockPanel { Children = { header.Dock(Dock.Top), Ui.Divider().Dock(Dock.Top), footer.Dock(Dock.Bottom), body } };
    }

    // Header and footer

    private Control Header(AgentProposal[] pending)
    {
        var mark = new Border { Child = Ui.Icon("IconBot", "brand").Centered(), Width = 36, Height = 36, CornerRadius = new CornerRadius(8) }
            .Res(Border.BackgroundProperty, "BrandTintBrush");
        var text = Ui.Stack(2,
            Ui.Text(Title(pending), "subtitle").Named("ProposalsTitle"),
            Ui.Wrapped(pending.Length == 0 ? "All dealt with. Undo takes back anything saved here." : Count(pending) + ". Nothing is saved until you add it.", "secondary"));
        return Ui.Columns("Auto,*", 12, mark.Centered(), text);
    }

    private string Title(AgentProposal[] pending)
    {
        var clients = pending.Concat(inbox.Saved.Select(d => d.Proposal)).Select(p => p.Client ?? "").Distinct().ToArray();
        return clients is [{ Length: > 0 } client] ? $"Proposed by {client}" : "Proposed by your agents";
    }

    /// <summary>"3 new entries and 1 change".</summary>
    public static string Count(IReadOnlyList<AgentProposal> proposals)
    {
        var added = proposals.Count(p => p.IsNew);
        var changes = proposals.Count - added;
        var parts = new[]
        {
            added == 0 ? null : added == 1 ? "1 new entry" : $"{added} new entries",
            changes == 0 ? null : changes == 1 ? "1 change" : $"{changes} changes",
        }.OfType<string>().ToArray();
        return parts.Length == 0 ? "Nothing waiting" : string.Join(" and ", parts);
    }

    private Control Footer(AgentProposal[] pending, AgentProposal[] ready)
    {
        var right = Ui.Row(8);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        if (pending.Length > ready.Length && ready.Length > 0)
        {
            var waiting = pending.Length - ready.Length;
            right.Children.Add(Ui.Text($"{waiting} need{(waiting == 1 ? "s" : "")} a look first", "caption", "secondary").Centered().Margin(0, 0, 8, 0));
        }
        right.Children.Add(Ui.Button(pending.Length == 0 ? "Done" : "Close", () => owner.Dialogs.Close(null)).Named("ProposalsClose"));
        if (pending.Length > 0)
        {
            var addAll = Ui.Button(addingAll ? "Adding…" : ready.Length == pending.Length ? "Add all" : $"Add {ready.Length}", AddAllAsync, owner.Notify, "primary")
                .Named("ProposalsAddAll").Tip("Save every proposal nothing stands in the way of");
            addAll.MinWidth = 100;
            addAll.IsEnabled = ready.Length > 0 && !addingAll;
            right.Children.Add(addAll);
        }
        Control? discardAll = pending.Length == 0 ? null : Ui.Button("Discard all", inbox.DiscardAllAsync, owner.Notify, "danger")
            .Named("ProposalsDiscardAll").Tip("Throw away every proposal waiting here");
        return Ui.Columns("Auto,*", 8, discardAll, right);
    }

    // Days

    private DateOnly DayOf(AgentProposal proposal) =>
        Time.Day(proposal.Fields.Start ?? inbox.Entry(proposal.EntryId)?.Start ?? proposal.Original?.Start ?? DateTimeOffset.UtcNow);

    /// <summary>When the proposal's time would be, on the entry as it is now.</summary>
    private (DateTimeOffset Start, DateTimeOffset End) Range(AgentProposal proposal)
    {
        var request = inbox.Request(proposal);
        var start = request.Start!.Value;
        var end = request.Stop ?? DateTimeOffset.UtcNow;
        return (start, end < start ? start : end);
    }

    private Control DaySection(DateOnly day, AgentProposal[] proposals, Dictionary<string, List<ProposalIssue>> issues)
    {
        var sorted = proposals.OrderBy(p => Range(p).Start).ToArray();
        var added = sorted.Where(p => p.IsNew).Sum(p => (Range(p).End - Range(p).Start).TotalSeconds);
        var heading = Ui.Columns("*,Auto", 8, Ui.Text(Time.DayHeading(day), "strong"),
            added > 0 ? Ui.Text($"+{Time.Duration(added)} proposed", "caption", "secondary", "mono").Centered() : null);
        var rows = Ui.Stack(8);
        foreach (var proposal in sorted) rows.Children.Add(Row(proposal, issues[proposal.Id]));
        return Ui.Stack(10, heading, DayStrip(day, sorted), rows);
    }

    /// <summary>
    /// A day at a glance: the entries already there as bars, the proposals as dashed outlines over them, so overlaps
    /// show before anything is added.
    /// </summary>
    private Control DayStrip(DateOnly day, AgentProposal[] proposals)
    {
        var dayStart = Time.StartOf(day);
        var dayEnd = Time.StartOf(day.AddDays(1));
        var now = DateTimeOffset.UtcNow;
        var entries = owner.Entries.Where(e => e.Start < dayEnd && (e.Stop ?? now) > dayStart).ToArray();
        var spans = entries.Select(e => (Start: e.Start, End: e.Stop ?? now)).Concat(proposals.Select(Range)).ToArray();
        var first = spans.Length > 0 ? spans.Min(s => s.Start) : dayStart.AddHours(9);
        var last = spans.Length > 0 ? spans.Max(s => s.End) : first.AddHours(8);
        // Whole hours around what is there, at least four of them.
        static DateTimeOffset Hour(DateTimeOffset value, bool up)
        {
            var ticks = value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerHour;
            if (up && ticks < value.UtcTicks) ticks += TimeSpan.TicksPerHour;
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        var from = Hour(first, false) < dayStart ? dayStart : Hour(first, false);
        var to = Hour(last, true) < from.AddHours(4) ? from.AddHours(4) : Hour(last, true);
        var hours = (int)(to - from).TotalHours;
        var step = hours > 12 ? 3 : hours > 6 ? 2 : 1;
        double X(DateTimeOffset at) => Math.Clamp((at - from).TotalSeconds / (to - from).TotalSeconds, 0, 1);
        var strip = new StripPanel { Height = 46 };
        strip.Add(new Border { CornerRadius = new CornerRadius(6) }.Res(Border.BackgroundProperty, "SubtleFillHoverBrush"), 0, 1, 0, 30);
        for (var hour = 0; hour <= hours; hour += step)
        {
            var at = from.AddHours(hour);
            strip.Add(new Border { Width = 1 }.Res(Border.BackgroundProperty, "DividerBrush"), X(at), null, 0, 30);
            var label = Ui.Text(Time.Clock(at), "caption", "tertiary", "mono");
            label.FontSize = 10;
            strip.Add(label, X(at), null, 32, 14, centered: true);
        }
        foreach (var entry in entries)
        {
            var bar = new Border { CornerRadius = new CornerRadius(4), Background = Ui.ProjectBrush(Catalog.Project(entry.ProjectId)?.Color, 0.35) }
                .Tip(entry.Description.Length == 0 ? "No description" : entry.Description);
            if (bar.Background is null) bar.Res(Border.BackgroundProperty, "ChipBackgroundBrush");
            strip.Add(bar, X(entry.Start), X(entry.Stop ?? now), 6, 18, minimum: 2);
        }
        foreach (var proposal in proposals)
        {
            var (start, end) = Range(proposal);
            var color = Catalog.Project(inbox.Request(proposal).ProjectId)?.Color;
            strip.Add(ProposalViews.Dashed(color, 4, 1.5, 0.12), X(start), X(end), 2, 26, minimum: 4);
        }
        return strip.Named("ProposalDayStrip");
    }

    // One proposal

    /// <summary>What it adds or changes, why, what stands in its way, and the buttons to answer it.</summary>
    private Control Row(AgentProposal proposal, List<ProposalIssue> issues)
    {
        var current = inbox.Entry(proposal.EntryId);
        var request = inbox.Request(proposal);
        var blocked = issues.Any(i => i.Blocking);
        var isBusy = busy.Contains(proposal.Id);
        var parts = ProposalIssues.Parts(request);
        var perProject = parts.Length > 1 && parts.Any(p => p.Description is not null);
        var description = request.Description ?? "";

        var details = Ui.Stack(4);
        var title = Ui.Row(6);
        if (!proposal.IsNew) title.Children.Add(new Border { Child = Ui.Text("Change", "caption", "medium") }.Classed("chip").Res(Border.BackgroundProperty, "BrandTintBrush"));
        // Parts described one by one show their own lines below instead.
        if (!perProject)
        {
            var text = Ui.Text(description.Length == 0 ? "No description" : description, "medium", description.Length == 0 ? "secondary" : "");
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(text.Centered());
        }
        if (title.Children.Count > 0) details.Children.Add(title);
        details.Children.Add(perProject ? PartLines(parts) : Work(parts, request));
        if (current is not null && !proposal.IsNew && Changes(proposal, current, request, parts) is { } changes) details.Children.Add(changes);
        if (proposal.Reason.Length > 0)
        {
            var reason = Ui.Wrapped("“" + proposal.Reason + "”", "caption", "secondary");
            reason.MaxLines = 2;
            details.Children.Add(reason);
        }
        foreach (var issue in issues)
        {
            var icon = Ui.Icon(issue.Blocking ? "IconErrorCircleFilled" : "IconWarningFilled", issue.Blocking ? "critical" : "caution", "small");
            details.Children.Add(Ui.Columns("Auto,*", 6, icon, Ui.Wrapped(issue.Message, "caption", issue.Blocking ? "critical" : "caution")).Named("ProposalIssue"));
        }

        var stop = request.Stop;
        var span = Ui.Text(Time.Span(request.Start!.Value, stop), "secondary", "mono");
        var length = Ui.Text(Time.Duration(Math.Max(0, ((stop ?? DateTimeOffset.UtcNow) - request.Start.Value).TotalSeconds)), "strong", "mono");
        span.HorizontalAlignment = HorizontalAlignment.Right;
        length.HorizontalAlignment = HorizontalAlignment.Right;
        var times = Ui.Stack(2, span, length);

        var accept = Ui.Button(proposal.IsNew ? "Add" : "Apply", () => AcceptAsync(proposal), owner.Notify, "primary").Named("ProposalAccept")
            .Tip(blocked ? "Edit it first: " + issues.First(i => i.Blocking).Message : "Save it as proposed");
        accept.MinWidth = 76;
        accept.HorizontalAlignment = HorizontalAlignment.Stretch;
        accept.HorizontalContentAlignment = HorizontalAlignment.Center;
        accept.IsEnabled = !blocked && !isBusy;
        var edit = Ui.Button("Edit…", () => owner.Dialogs.Close(proposal), "subtle").Named("ProposalEdit");
        edit.IsEnabled = !isBusy && (current is not null || proposal.IsNew);
        var discard = Ui.IconButton("IconDismiss", "Discard", () => inbox.DiscardAsync(proposal), owner.Notify, "small").Named("ProposalDiscard");
        discard.IsEnabled = !isBusy;
        var actions = Ui.Stack(6, accept, Ui.Row(2, edit, discard));
        actions.HorizontalAlignment = HorizontalAlignment.Right;

        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = Ui.ProjectBrush(Catalog.Project(request.ProjectId)?.Color) };
        if (bar.Background is null) bar.Res(Border.BackgroundProperty, "TextTertiaryBrush");
        var content = Ui.Columns("Auto,*,Auto,Auto", 12, bar, details, times, actions);
        content.Margin = new Thickness(12);
        var outline = new Rectangle { RadiusX = 8, RadiusY = 8, StrokeThickness = 1, StrokeDashArray = [5, 3] }
            .Res(Shape.StrokeProperty, blocked ? "CriticalBrush" : "ControlStrongStrokeBrush");
        outline.Opacity = blocked ? 0.6 : 0.45;
        var card = new Border { CornerRadius = new CornerRadius(8) }.Res(Border.BackgroundProperty, "CardBackgroundBrush");
        return new Panel { Children = { card, outline, content } }.Named("Proposal").Spoken(Spoken(proposal, request));
    }

    private string Spoken(AgentProposal proposal, TimeEntryRequest request) =>
        $"{(proposal.IsNew ? "New entry" : "Change")}: {(string.IsNullOrEmpty(request.Description) ? "No description" : request.Description)}, {Time.Span(request.Start!.Value, request.Stop)}";

    /// <summary>"● Atlas 60%  Fix the login": each part of a split described one by one, as its project's timesheet will show it.</summary>
    private Control PartLines(SplitPart[] parts)
    {
        var lines = Ui.Stack(3);
        foreach (var part in parts)
        {
            var text = part.Description ?? "";
            var words = Ui.Text(text.Length == 0 ? "No description" : text, text.Length == 0 ? "secondary" : "");
            words.TextTrimming = TextTrimming.CharacterEllipsis;
            var name = Ui.Text(Catalog.Project(part.ProjectId)?.Name ?? "No project", "medium");
            lines.Children.Add(Ui.Columns("Auto,Auto,Auto,*", 6, Ui.Dot(Catalog.ColorOf(part), 7), name, Ui.Text($"{part.Percent}%", "secondary", "mono"), words).Named("ProposalPart"));
        }
        return lines;
    }

    private Control Work(SplitPart[] parts, TimeEntryRequest request)
    {
        if (parts.Length > 1) return SplitViews.Label(Catalog, parts);
        return SplitViews.ProjectOrNone(Catalog, request.ProjectId, request.TaskId, withClient: true);
    }

    /// <summary>What the change does, line by line: "Time 14:00–16:00 → 14:00–15:30".</summary>
    private Control? Changes(AgentProposal proposal, TimeEntryResponse entry, TimeEntryRequest request, SplitPart[] parts)
    {
        var fields = proposal.Fields;
        var lines = Ui.Stack(1);
        string Span(DateTimeOffset start, DateTimeOffset? stop) => $"{Time.Clock(start)}–{(stop is { } end ? Time.Clock(end) : "now")}";
        if (fields.Start is not null || fields.Stop is not null)
            lines.Children.Add(Line("Time", $"{Span(entry.Start, entry.Stop)} → {Span(request.Start!.Value, request.Stop)}"));
        // A split whose parts only got descriptions changes no project.
        var before = WorkName(entry.Parts());
        var after = WorkName(parts);
        if (fields.Work is not null && before != after) lines.Children.Add(Line("Project", $"{before} → {after}"));
        if (fields.Description is not null || request.Description != entry.Description)
            lines.Children.Add(Line("Description", $"was “{(entry.Description.Length == 0 ? "No description" : entry.Description)}”"));
        return lines.Children.Count == 0 ? null : lines;
    }

    /// <summary>"Atlas · Login", or the parts of a split: "Atlas 60%, Beacon 40%".</summary>
    private string WorkName(IReadOnlyList<SplitPart> parts) => parts.Count > 1 ? Catalog.Describe(parts) : Catalog.Name(parts[0]);

    private static Control Line(string label, string value)
    {
        var name = Ui.Text(label, "caption", "secondary");
        name.Width = 76;
        var text = Ui.Wrapped(value, "caption", "mono");
        text.MaxLines = 2;
        return Ui.Columns("Auto,*", 6, name, text);
    }

    // What was saved here

    private Control SavedSection()
    {
        var rows = Ui.Stack(6, Ui.Text("Saved in this review", "strong"));
        foreach (var item in inbox.Saved)
        {
            var request = item.Proposal.Request(inbox.Entry(item.EntryId) ?? item.Before);
            var description = string.IsNullOrEmpty(request.Description) ? "No description" : request.Description;
            var undo = Ui.Button("Undo", () => UndoAsync(item), owner.Notify, "subtle").Named("ProposalUndo");
            undo.IsEnabled = !busy.Contains(item.Proposal.Id);
            var text = Ui.Text(description);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = Ui.Columns("Auto,*,Auto,Auto,Auto", 10,
                Ui.Icon("IconCheckmarkCircleFilled", "success", "small").Centered(), text.Centered(),
                Ui.Text(item.Before is null ? "added" : "changed", "secondary").Centered(),
                Ui.Text(Time.Span(request.Start!.Value, request.Stop), "secondary", "mono").Centered(), undo);
            rows.Children.Add(new Border { Child = row, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8) }
                .Res(Border.BackgroundProperty, "SubtleFillHoverBrush").Named("ProposalSaved"));
        }
        return rows;
    }

    // Actions

    private async Task AcceptAsync(AgentProposal proposal)
    {
        busy.Add(proposal.Id);
        try
        {
            await owner.MutateAsync(() => inbox.AcceptAsync(proposal));
        }
        catch (Exception error) when (error is ApiException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            owner.Notify(error is ApiException or InvalidOperationException ? error.Message : proposal.IsNew ? "Couldn't add the entry." : "Couldn't change the entry.");
        }
        finally
        {
            busy.Remove(proposal.Id);
            Render();
        }
    }

    private async Task AddAllAsync()
    {
        addingAll = true;
        Render();
        (int Added, int Failed) result = (0, 0);
        try
        {
            await owner.MutateAsync(async () => result = await inbox.AcceptAllAsync());
        }
        finally
        {
            addingAll = false;
            Render();
        }
        if (result.Failed > 0) owner.Notify($"{result.Failed} couldn't be saved. They are still here.");
        else if (inbox.Pending.Length == 0)
        {
            owner.Notify(result.Added == 1 ? "Saved 1 proposal" : $"Saved {result.Added} proposals", NoticeKind.Success);
            owner.Dialogs.Close(null);
        }
    }

    private async Task UndoAsync(ProposalInbox.Done item)
    {
        busy.Add(item.Proposal.Id);
        try
        {
            await owner.MutateAsync(() => inbox.UndoAsync(item));
        }
        catch (Exception error) when (error is ApiException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            owner.Notify(error is ApiException or InvalidOperationException ? error.Message : "Couldn't take it back.");
        }
        finally
        {
            busy.Remove(item.Proposal.Id);
            Render();
        }
    }
}

/// <summary>Above every section while agents' proposals wait, and beside an entry an agent proposed a change to.</summary>
internal static class ProposalViews
{
    /// <summary>A dashed outline over a faint fill, in a project's colour or the brand's: what isn't saved yet.</summary>
    public static Control Dashed(string? color, double radius, double thickness, double fill)
    {
        var outline = new Rectangle { RadiusX = radius, RadiusY = radius, StrokeThickness = thickness, StrokeDashArray = [4, 2] };
        if (Ui.ProjectBrush(color) is { } stroke)
        {
            outline.Stroke = stroke;
            outline.Fill = Ui.ProjectBrush(color, fill);
        }
        else
        {
            outline.Res(Shape.StrokeProperty, "BrandBrush");
            outline.Res(Shape.FillProperty, "BrandTintBrush");
        }
        return outline;
    }

    /// <summary>"Claude Code proposed 3 new entries. Nothing is saved until you add it.  [Review…]"</summary>
    public static Control? Banner(MainWindow owner)
    {
        var pending = owner.Proposals.Pending;
        if (pending.Length == 0) return null;
        var clients = pending.Select(p => p.Client ?? "").Distinct().ToArray();
        var who = clients.Length == 1 ? pending[0].Client ?? "An agent" : "Your agents";
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run($"{who} proposed {ProposalReview.Count(pending)}. ") { FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Run("Nothing is saved until you add it."));
        var review = Ui.Button("Review…", owner.ReviewProposalsAsync, owner.Notify, "primary").Named("ProposalsReview");
        var row = Ui.Columns("Auto,*,Auto", 10, Ui.Icon("IconBot", "brand").Centered(), text, review.Centered());
        return new Border { Child = row }.Classed("banner", "brand").Named("ProposalBanner");
    }

    /// <summary>"Change proposed", beside an entry an agent proposed a change to.</summary>
    public static Control? Chip(MainWindow owner, TimeEntryResponse entry)
    {
        if (owner.Proposals.ChangeFor(entry.Id) is null) return null;
        var label = Ui.Row(5, Ui.Icon("IconBot", "brand", "small").Centered(), Ui.Text("Change proposed", "caption", "medium").Centered());
        var chip = Ui.Button(label, owner.ReviewProposalsAsync, owner.Notify, "chip", "proposal").Named("ProposalChip")
            .Tip("An agent proposed changing this entry. Review it.").Spoken("Change proposed. Review it.");
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }
}

/// <summary>Lays out the day strip: each child across a share of the width, at a height of its own.</summary>
internal sealed class StripPanel : Panel
{
    private sealed record Slot(double From, double? To, double Top, double Height, bool Centered, double Minimum);
    private readonly Dictionary<Control, Slot> slots = [];

    /// <summary>A child from <paramref name="from"/> to <paramref name="to"/> (shares of the width), or at <paramref name="from"/> as wide as it is.</summary>
    public void Add(Control child, double from, double? to, double top, double height, bool centered = false, double minimum = 0)
    {
        slots[child] = new(from, to, top, height, centered, minimum);
        Children.Add(child);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(double.PositiveInfinity, slots.TryGetValue(child, out var slot) ? slot.Height : 0));
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        foreach (var child in Children)
        {
            if (!slots.TryGetValue(child, out var slot)) continue;
            var x = slot.From * width;
            var w = slot.To is { } to ? Math.Max(slot.Minimum, (to - slot.From) * width) : child.DesiredSize.Width;
            if (slot.Centered) x = Math.Clamp(x - w / 2, 0, Math.Max(0, width - w));
            child.Arrange(new Rect(x, slot.Top, w, slot.Height));
        }
        return finalSize;
    }
}
