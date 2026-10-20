using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>How an entry's project, or the parts of a split entry, are shown, as on the Mac.</summary>
internal static class SplitViews
{
    /// <summary>"● Website · Design · Acme": the project, in its colour, with its task and client.</summary>
    public static Control ProjectLabel(ProjectResponse project, TaskResponse? task = null, ClientResponse? client = null)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines!.Add(new Run(project.Name) { FontWeight = FontWeight.Medium, Foreground = Ui.ProjectBrush(project.Color) });
        if (task is not null) text.Inlines.Add(Secondary(text, " · " + task.Name));
        if (client is not null) text.Inlines.Add(Secondary(text, " · " + client.Name));
        return Ui.Columns("Auto,*", 6, Ui.Dot(project.Color), text);
    }

    /// <summary>The project of an entry or a choice, or "No project".</summary>
    public static Control ProjectOrNone(Catalog catalog, Guid? projectId, Guid? taskId, bool withClient = false)
    {
        if (catalog.Project(projectId) is not { } project) return Ui.Text("No project", "secondary").Centered();
        return ProjectLabel(project, catalog.Task(taskId), withClient ? catalog.Client(project.ClientId) : null);
    }

    /// <summary>Overlapping dots, one for each part's project.</summary>
    public static Control Dots(Catalog catalog, IReadOnlyList<SplitPart> parts, double size = 9)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -size * 0.35, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts)
        {
            var dot = Ui.Dot(catalog.ColorOf(part), size);
            dot.StrokeThickness = 1.5;
            dot.Res(Shape.StrokeProperty, "SolidCardBackgroundBrush");
            panel.Children.Add(dot);
        }
        return panel;
    }

    /// <summary>"●● Website 60% · Research 40%": where a split entry's time went.</summary>
    public static Control Label(Catalog catalog, IReadOnlyList<SplitPart> parts)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (index > 0) text.Inlines!.Add(Secondary(text, " · "));
            var name = new Run(catalog.Project(part.ProjectId)?.Name ?? "No project") { FontWeight = FontWeight.Medium };
            if (Ui.ProjectBrush(catalog.ColorOf(part)) is { } brush) name.Foreground = brush;
            text.Inlines!.Add(name);
            text.Inlines.Add(Secondary(text, $" {part.Percent}%"));
        }
        text.FontFeatures = FontFeatureCollection.Parse("+tnum");
        return Ui.Columns("Auto,*", 6, Dots(catalog, parts), text).Tip("Split: " + catalog.Describe(parts));
    }

    /// <summary>A thin bar with a segment per part, as wide as its share of the time.</summary>
    public static Control Meter(Catalog catalog, IReadOnlyList<SplitPart> parts, double height = 4)
    {
        var grid = new Grid { Height = height, ColumnSpacing = 1, VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < parts.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(parts[index].Percent, GridUnitType.Star));
            grid.Children.Add(Segment(catalog.ColorOf(parts[index]), height).Column(index));
        }
        return new Border { Child = grid, CornerRadius = new CornerRadius(height / 2), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The same bar, thinner, along the bottom of a calendar block.</summary>
    public static Control Thread(Catalog catalog, IReadOnlyList<SplitPart> parts)
    {
        var grid = new Grid { Height = 3, ColumnSpacing = 2 };
        for (var index = 0; index < parts.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(parts[index].Percent, GridUnitType.Star));
            var segment = Segment(catalog.ColorOf(parts[index]), 3);
            segment.CornerRadius = new CornerRadius(1.5);
            grid.Children.Add(segment.Column(index));
        }
        return grid;
    }

    private static Border Segment(string? color, double height)
    {
        var segment = new Border { Height = height };
        if (Ui.ProjectBrush(color) is { } brush) segment.Background = brush;
        else segment.Res(Border.BackgroundProperty, "TextTertiaryBrush");
        return segment;
    }

    /// <summary>A run in the secondary text colour, following the theme of the text it sits in.</summary>
    private static Run Secondary(TextBlock owner, string text)
    {
        var run = new Run(text);
        owner.GetResourceObservable("TextSecondaryBrush").Subscribe(new Observer(value => run.Foreground = value as IBrush));
        return run;
    }

    private sealed class Observer(Action<object?> next) : IObserver<object?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(object? value) => next(value);
    }
}
