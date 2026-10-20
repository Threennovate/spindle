using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Spindle.Windows.Views;

/// <summary>
/// The building blocks of every view. Visuals come from style classes and theme resources (Theme/*.axaml), never from
/// colours in code, so the views follow the Windows light and dark themes. Project colours are data, not design.
/// </summary>
internal static class Ui
{
    // Resources

    /// <summary>Binds a property to a theme resource, so it follows the theme.</summary>
    public static T Res<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static T Classed<T>(this T control, params string[] classes) where T : StyledElement
    {
        foreach (var name in classes)
        {
            if (name.Length > 0) control.Classes.Add(name);
        }
        return control;
    }

    public static T Named<T>(this T control, string name) where T : Control
    {
        control.Name = name;
        return control;
    }

    public static T Tip<T>(this T control, string? tip) where T : Control
    {
        ToolTip.SetTip(control, tip);
        return control;
    }

    public static T Spoken<T>(this T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    public static T Margin<T>(this T control, double left, double top, double right, double bottom) where T : Control
    {
        control.Margin = new Thickness(left, top, right, bottom);
        return control;
    }

    public static T Column<T>(this T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    public static T Row<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    public static T Dock<T>(this T control, Avalonia.Controls.Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    public static T Centered<T>(this T control) where T : Control
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    // Text and icons

    public static TextBlock Text(string text, params string[] classes) => new TextBlock { Text = text }.Classed(classes);

    public static TextBlock Wrapped(string text, params string[] classes) => Text(text, ["wrap", .. classes]);

    public static PathIcon Icon(string key, params string[] classes) => new PathIcon().Res(PathIcon.DataProperty, key).Classed(classes);

    // Layout

    public static StackPanel Stack(double spacing, params Control?[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children)
        {
            if (child is not null) panel.Children.Add(child);
        }
        return panel;
    }

    public static StackPanel Row(double spacing, params Control?[] children)
    {
        var panel = Stack(spacing, children);
        panel.Orientation = Orientation.Horizontal;
        return panel;
    }

    /// <summary>A one-row grid; children fill the columns in order.</summary>
    public static Grid Columns(string definitions, double spacing, params Control?[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(definitions), ColumnSpacing = spacing };
        for (var index = 0; index < children.Length; index++)
        {
            if (children[index] is not { } child) continue;
            Grid.SetColumn(child, index);
            grid.Children.Add(child);
        }
        return grid;
    }

    public static Border Card(Control child, params string[] classes) => new Border { Child = child }.Classed(["card", .. classes]);

    public static Border Divider() => new Border().Classed("divider");

    public static Control Spacer(double height) => new Border { Height = height };

    // Buttons

    /// <summary>A button whose action runs once at a time; failures go to <paramref name="error"/>.</summary>
    public static Button Button(object content, Func<Task> action, Action<string> error, params string[] classes)
    {
        var button = new Button { Content = content }.Classed(classes);
        if (content is string text) AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                error(exception.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    public static Button Button(object content, Action action, params string[] classes) =>
        Button(content, () =>
        {
            action();
            return Task.CompletedTask;
        }, _ => { }, classes);

    /// <summary>An icon-only button with a tooltip that also names it for screen readers.</summary>
    public static Button IconButton(string icon, string label, Func<Task> action, Action<string> error, params string[] classes) =>
        Button(Icon(icon), action, error, ["icon", .. classes]).Tip(label).Spoken(label);

    public static Button IconButton(string icon, string label, Action action, params string[] classes) =>
        Button(Icon(icon), action, ["icon", .. classes]).Tip(label).Spoken(label);

    /// <summary>An icon and a label, side by side, as button content.</summary>
    public static StackPanel Labeled(string icon, string text, params string[] classes) =>
        Row(8, Icon(icon, classes).Centered(), Text(text).Centered());

    public static Button Link(string text, Func<Task> action, Action<string> error) => Button(text, action, error, "link");

    // Data colours

    /// <summary>A project's colour, from its <c>#RRGGBB</c>; null when there is none.</summary>
    public static Color? ProjectColor(string? hex) => hex is not null && Color.TryParse(hex, out var color) ? color : null;

    public static IBrush? ProjectBrush(string? hex, double opacity = 1) => ProjectColor(hex) is { } color ? new SolidColorBrush(color, opacity) : null;

    /// <summary>A project's colour for text on its own tint: a quarter of the way to the text colour, so it reads in both themes.</summary>
    public static IBrush? ReadableBrush(string? hex, bool dark)
    {
        if (ProjectColor(hex) is not { } color) return null;
        var target = dark ? 0.95 : 0.08;
        byte Channel(byte value) => (byte)Math.Round((value / 255d * 0.75 + target * 0.25) * 255);
        return new SolidColorBrush(Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B)));
    }

    /// <summary>A project's dot; grey (secondary text) without a project.</summary>
    public static Ellipse Dot(string? hex, double size = 9)
    {
        var dot = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (ProjectBrush(hex) is { } brush) dot.Fill = brush;
        else dot.Res(Shape.FillProperty, "TextTertiaryBrush");
        return dot;
    }

    /// <summary>A tag's name in a small capsule.</summary>
    public static Border Chip(string name) => new Border { Child = Text(name, "caption") }.Classed("chip");

    /// <summary>A key as it is printed on the keyboard, e.g. Esc or Enter.</summary>
    public static Border Keycap(string key) => new Border { Child = Text(key, "caption", "secondary") }.Classed("keycap");

    /// <summary>A person's initials in a circle, e.g. "AK" for Ana Kovač.</summary>
    public static Border Avatar(string name, double size = 28)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = new[] { words.FirstOrDefault(), words.Length > 1 ? words[^1] : null }.Where(w => w is not null).Select(w => char.ToUpperInvariant(w![0]));
        var initials = string.Concat(letters);
        var label = Text(initials.Length == 0 ? "?" : initials, "strong", "secondary");
        label.FontSize = size * 0.4;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = label }.Res(Border.BackgroundProperty, "ChipBackgroundBrush");
    }

    // Brand

    /// <summary>The Spindle logo, the mark and the name, drawn in the theme's ink and marigold.</summary>
    public static Control Logo(double height = 28)
    {
        var canvas = new Canvas { Width = 169, Height = 46 };
        var mark = Mark();
        mark.RenderTransform = new MatrixTransform(new Matrix(0.7407, 0, 0, 0.7407, -8.5185, -1.7037));
        mark.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        canvas.Children.Add(mark);
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(Wordmark) }.Res(Shape.FillProperty, "LogoInkBrush"));
        return new Viewbox { Height = height, Child = canvas, Stretch = Stretch.Uniform }.Spoken("Spindle");
    }

    /// <summary>The mark alone, for small spaces.</summary>
    public static Control LogoMark(double size = 20) => new Viewbox { Width = size, Height = size, Child = Mark(), Stretch = Stretch.Uniform };

    private static Canvas Mark()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        (double X, double Y, double W, double H)[] bars = [(29.7, 2.3, 4.6, 8.6), (21.5, 14.1, 21, 4.6), (15.5, 21.9, 33, 4.6), (11.5, 29.7, 41, 4.6), (15.5, 37.5, 33, 4.6), (21.5, 45.3, 21, 4.6), (29.7, 53.1, 4.6, 8.6)];
        for (var index = 0; index < bars.Length; index++)
        {
            var (x, y, width, height) = bars[index];
            var bar = new Rectangle { Width = width, Height = height, RadiusX = 2.3, RadiusY = 2.3 };
            bar.Res(Shape.FillProperty, index == 3 ? "LogoAccentBrush" : "LogoInkBrush");
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y);
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    /// <summary>
    /// A sign-in provider's own mark, as its guidelines ask and as the web shows it: Google's four colours, which are
    /// the brand's and not Spindle's, and Apple's in the text colour. Null for a provider without one.
    /// </summary>
    public static Control? ProviderMark(string provider, double size = 18)
    {
        var canvas = provider switch
        {
            "google" => new Canvas { Width = 48, Height = 48 },
            "apple" => new Canvas { Width = 384, Height = 512 },
            _ => null,
        };
        if (canvas is null) return null;
        if (provider == "google")
            foreach (var (color, data) in GoogleMark) canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Fill = new SolidColorBrush(Color.Parse(color)) });
        else canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(AppleMark) }.Res(Shape.FillProperty, "TextPrimaryBrush"));
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    }

    private static readonly (string Color, string Data)[] GoogleMark =
    [
        ("#FFC107", "M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.3 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z"),
        ("#FF3D00", "M6.3 14.7l6.6 4.8C14.7 15.1 19 12 24 12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z"),
        ("#4CAF50", "M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.3 0-9.7-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z"),
        ("#1976D2", "M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z"),
    ];

    private const string AppleMark = "M318.7 268.7c-.2-36.7 16.4-64.4 50-84.8-18.8-26.9-47.2-41.7-84.7-44.6-35.5-2.8-74.3 20.7-88.5 20.7-15 0-49.4-19.7-76.4-19.7C63.3 141.2 4 184.8 4 273.5c0 26.2 4.8 53.3 14.4 81.2 12.8 36.7 59 126.7 107.2 125.2 25.2-.6 43-17.9 75.8-17.9 31.8 0 48.3 17.9 76.4 17.9 48.6-.7 90.4-82.5 102.6-119.3-65.2-30.7-61.7-90-61.7-91.9zM262.1 104.5c27.3-32.4 24.8-61.9 24-72.5-24.1 1.4-52 16.4-67.9 34.9-17.5 19.8-27.8 44.3-25.6 71.9 26.1 2 49.9-11.4 69.5-34.3z";

    private const string Wordmark ="M49.49 35.46L49.49 35.46Q47.83 35.46 46.66 35.10Q45.49 34.74 44.77 34.39Q44.05 34.04 43.73 34.04L43.73 34.04Q43.41 34.04 43.23 34.25Q43.05 34.46 42.91 34.73Q42.77 35.00 42.58 35.21Q42.39 35.42 42.05 35.42L42.05 35.42Q41.53 35.42 41.23 35.11Q40.93 34.80 40.79 34.12L40.79 34.12L39.47 28.64Q39.35 28.12 39.56 27.74Q39.77 27.36 40.25 27.24L40.25 27.24Q40.75 27.10 41.13 27.30Q41.51 27.50 41.85 28.12L41.85 28.12Q42.67 29.76 43.78 30.84Q44.89 31.92 46.27 32.46Q47.65 33.00 49.23 33.00L49.23 33.00Q51.35 33.00 52.60 31.89Q53.85 30.78 53.85 29.04L53.85 29.04Q53.87 27.90 53.38 26.96Q52.89 26.02 51.52 25.14Q50.15 24.26 47.55 23.34L47.55 23.34Q44.39 22.28 42.54 20.93Q40.69 19.58 39.88 17.90Q39.07 16.22 39.07 14.20L39.07 14.20Q39.07 11.90 40.14 10.14Q41.21 8.38 43.14 7.37Q45.07 6.36 47.61 6.36L47.61 6.36Q49.31 6.34 50.47 6.71Q51.63 7.08 52.37 7.44Q53.11 7.80 53.55 7.80L53.55 7.80Q53.95 7.80 54.12 7.44Q54.29 7.08 54.50 6.72Q54.71 6.36 55.23 6.36L55.23 6.36Q55.75 6.36 56.05 6.70Q56.35 7.04 56.61 7.94L56.61 7.94L58.33 13.60Q58.53 14.24 58.33 14.66Q58.13 15.08 57.59 15.26L57.59 15.26Q57.09 15.40 56.69 15.19Q56.29 14.98 56.01 14.42L56.01 14.42Q55.09 12.50 53.85 11.26Q52.61 10.02 51.14 9.43Q49.67 8.84 48.05 8.84L48.05 8.84Q46.01 8.84 44.75 10.04Q43.49 11.24 43.49 13.06L43.49 13.06Q43.49 14.26 44.03 15.27Q44.57 16.28 45.92 17.19Q47.27 18.10 49.69 19.02L49.69 19.02Q52.93 20.18 54.79 21.50Q56.65 22.82 57.42 24.41Q58.19 26.00 58.17 28.00L58.17 28.00Q58.17 30.08 57.13 31.77Q56.09 33.46 54.15 34.46Q52.21 35.46 49.49 35.46ZM66.91 17.60L66.91 17.60L66.91 19.86L67.09 20.32L67.09 40.48Q67.09 41.20 67.29 41.52Q67.49 41.84 67.91 41.98L67.91 41.98L69.01 42.16Q69.39 42.32 69.59 42.58Q69.79 42.84 69.79 43.22L69.79 43.22Q69.79 43.68 69.49 43.94Q69.19 44.20 68.65 44.20L68.65 44.20L61.61 44.20Q61.07 44.20 60.77 43.94Q60.47 43.68 60.47 43.22L60.47 43.22Q60.47 42.84 60.67 42.59Q60.87 42.34 61.25 42.16L61.25 42.16L61.93 41.98Q62.37 41.82 62.57 41.51Q62.77 41.20 62.77 40.48L62.77 40.48L62.77 21.28Q62.77 20.66 62.58 20.39Q62.39 20.12 62.01 20.02L62.01 20.02L60.91 19.96Q60.53 19.82 60.34 19.60Q60.15 19.38 60.15 19.04L60.15 19.04Q60.15 18.68 60.39 18.41Q60.63 18.14 61.19 17.94L61.19 17.94L63.89 16.92Q64.53 16.66 64.98 16.53Q65.43 16.40 65.81 16.40L65.81 16.40Q66.33 16.40 66.62 16.73Q66.91 17.06 66.91 17.60ZM66.05 22.60L66.05 22.60L65.31 21.28Q66.73 18.92 68.72 17.60Q70.71 16.28 73.07 16.28L73.07 16.28Q75.35 16.28 77.09 17.42Q78.83 18.56 79.82 20.61Q80.81 22.66 80.81 25.40L80.81 25.40Q80.81 28.52 79.64 30.74Q78.47 32.96 76.47 34.14Q74.47 35.32 71.97 35.32L71.97 35.32Q69.69 35.32 67.88 34.16Q66.07 33.00 65.01 31.00L65.01 31.00L66.13 29.24Q67.07 31.08 68.42 31.98Q69.77 32.88 71.29 32.88L71.29 32.88Q72.71 32.88 73.85 32.05Q74.99 31.22 75.65 29.65Q76.31 28.08 76.31 25.86L76.31 25.86Q76.31 23.66 75.65 22.13Q74.99 20.60 73.86 19.80Q72.73 19.00 71.27 19.00L71.27 19.00Q69.63 19.00 68.31 19.94Q66.99 20.88 66.05 22.60ZM89.71 17.60L89.71 17.60L89.71 31.12Q89.71 31.84 89.91 32.16Q90.11 32.48 90.55 32.62L90.55 32.62L91.21 32.82Q91.59 33.00 91.80 33.25Q92.01 33.50 92.01 33.88L92.01 33.88Q92.01 34.34 91.71 34.60Q91.41 34.86 90.85 34.86L90.85 34.86L84.23 34.86Q83.69 34.86 83.39 34.60Q83.09 34.34 83.09 33.88L83.09 33.88Q83.09 33.50 83.29 33.25Q83.49 33.00 83.87 32.82L83.87 32.82L84.55 32.62Q84.99 32.48 85.19 32.16Q85.39 31.84 85.39 31.12L85.39 31.12L85.39 21.28Q85.39 20.66 85.20 20.39Q85.01 20.12 84.63 20.02L84.63 20.02L83.53 19.96Q83.15 19.82 82.96 19.60Q82.77 19.38 82.77 19.04L82.77 19.04Q82.77 18.68 83.01 18.40Q83.25 18.12 83.81 17.94L83.81 17.94L86.71 16.90Q87.37 16.66 87.80 16.53Q88.23 16.40 88.61 16.40L88.61 16.40Q89.13 16.40 89.42 16.73Q89.71 17.06 89.71 17.60ZM87.13 13.04L87.13 13.04Q85.81 13.04 84.99 12.30Q84.17 11.56 84.17 10.36L84.17 10.36Q84.17 9.18 84.99 8.44Q85.81 7.70 87.13 7.70L87.13 7.70Q88.49 7.70 89.31 8.44Q90.13 9.18 90.13 10.36L90.13 10.36Q90.13 11.56 89.31 12.30Q88.49 13.04 87.13 13.04ZM100.43 17.60L100.43 17.60L100.43 31.12Q100.43 31.88 100.62 32.22Q100.81 32.56 101.25 32.72L101.25 32.72L101.93 32.92Q102.61 33.24 102.61 33.88L102.61 33.88Q102.61 34.86 101.57 34.86L101.57 34.86L94.95 34.86Q94.41 34.86 94.11 34.60Q93.81 34.34 93.81 33.88L93.81 33.88Q93.81 33.50 94.01 33.25Q94.21 33.00 94.59 32.82L94.59 32.82L95.27 32.62Q95.71 32.48 95.91 32.16Q96.11 31.84 96.11 31.12L96.11 31.12L96.11 21.28Q96.11 20.66 95.92 20.39Q95.73 20.12 95.35 20.02L95.35 20.02L94.25 19.96Q93.87 19.82 93.68 19.60Q93.49 19.38 93.49 19.04L93.49 19.04Q93.49 18.68 93.73 18.40Q93.97 18.12 94.53 17.94L94.53 17.94L97.43 16.90Q98.09 16.66 98.52 16.53Q98.95 16.40 99.33 16.40L99.33 16.40Q99.85 16.40 100.14 16.73Q100.43 17.06 100.43 17.60ZM100.63 21.42L99.89 22.10L98.89 20.96L99.81 20.10Q101.85 18.12 103.56 17.19Q105.27 16.26 106.89 16.26L106.89 16.26Q109.11 16.26 110.32 17.63Q111.53 19.00 111.81 21.40L111.81 21.40L113.03 31.12Q113.11 31.84 113.27 32.16Q113.43 32.48 113.85 32.62L113.85 32.62L114.53 32.82Q114.91 33.02 115.12 33.26Q115.33 33.50 115.33 33.88L115.33 33.88Q115.33 34.34 115.03 34.60Q114.73 34.86 114.17 34.86L114.17 34.86L107.53 34.86Q106.47 34.86 106.47 33.88L106.47 33.88Q106.47 33.24 107.15 32.92L107.15 32.92L107.85 32.72Q108.29 32.56 108.52 32.23Q108.75 31.90 108.67 31.18L108.67 31.18L107.57 22.70Q107.35 21.04 106.68 20.22Q106.01 19.40 104.69 19.40L104.69 19.40Q103.79 19.40 102.79 19.86Q101.79 20.32 100.63 21.42L100.63 21.42ZM130.35 32.70L130.35 31.34L130.17 31.12L130.17 10.20Q130.17 9.56 129.98 9.30Q129.79 9.04 129.41 8.94L129.41 8.94L128.31 8.88Q127.93 8.76 127.74 8.53Q127.55 8.30 127.55 7.96L127.55 7.96Q127.55 7.60 127.79 7.32Q128.03 7.04 128.59 6.86L128.59 6.86L131.49 5.82Q132.15 5.58 132.58 5.45Q133.01 5.32 133.39 5.32L133.39 5.32Q133.91 5.32 134.20 5.65Q134.49 5.98 134.49 6.52L134.49 6.52L134.49 31.12Q134.49 31.84 134.71 32.16Q134.93 32.48 135.33 32.62L135.33 32.62L135.99 32.82Q136.37 33.00 136.58 33.25Q136.79 33.50 136.79 33.88L136.79 33.88Q136.79 34.34 136.49 34.60Q136.19 34.86 135.65 34.86L135.65 34.86L132.55 34.86Q131.55 34.86 130.95 34.29Q130.35 33.72 130.35 32.70L130.35 32.70ZM116.45 26.20L116.45 26.20Q116.45 23.08 117.62 20.86Q118.79 18.64 120.80 17.46Q122.81 16.28 125.29 16.28L125.29 16.28Q127.57 16.28 129.38 17.44Q131.19 18.60 132.25 20.62L132.25 20.62L131.13 22.38Q130.19 20.54 128.85 19.64Q127.51 18.74 125.97 18.74L125.97 18.74Q124.55 18.74 123.41 19.56Q122.27 20.38 121.61 21.95Q120.95 23.52 120.95 25.74L120.95 25.74Q120.95 27.92 121.61 29.44Q122.27 30.96 123.41 31.76Q124.55 32.56 125.99 32.56L125.99 32.56Q127.63 32.56 128.95 31.63Q130.27 30.70 131.21 29.00L131.21 29.00L131.95 30.30Q130.53 32.64 128.54 33.98Q126.55 35.32 124.19 35.32L124.19 35.32Q121.91 35.32 120.17 34.17Q118.43 33.02 117.44 30.97Q116.45 28.92 116.45 26.20ZM145.21 6.52L145.21 6.52L145.21 31.12Q145.21 31.84 145.41 32.16Q145.61 32.48 146.03 32.62L146.03 32.62L146.71 32.82Q147.09 33.00 147.29 33.25Q147.49 33.50 147.49 33.88L147.49 33.88Q147.49 34.34 147.20 34.60Q146.91 34.86 146.35 34.86L146.35 34.86L139.73 34.86Q139.19 34.86 138.88 34.60Q138.57 34.34 138.57 33.88L138.57 33.88Q138.57 33.50 138.78 33.25Q138.99 33.00 139.37 32.82L139.37 32.82L140.05 32.62Q140.47 32.48 140.68 32.16Q140.89 31.84 140.89 31.12L140.89 31.12L140.89 10.20Q140.89 9.56 140.70 9.30Q140.51 9.04 140.11 8.94L140.11 8.94L139.03 8.88Q138.65 8.76 138.46 8.53Q138.27 8.30 138.27 7.96L138.27 7.96Q138.27 7.60 138.51 7.32Q138.75 7.04 139.31 6.86L139.31 6.86L142.21 5.82Q142.87 5.58 143.30 5.45Q143.73 5.32 144.11 5.32L144.11 5.32Q144.63 5.32 144.92 5.65Q145.21 5.98 145.21 6.52ZM166.07 23.22L166.07 23.22Q166.07 24.44 165.39 25.07Q164.71 25.70 163.43 25.70L163.43 25.70L151.89 25.70L151.89 23.96L160.43 23.96Q161.63 23.96 161.63 22.84L161.63 22.84Q161.63 20.90 160.58 19.73Q159.53 18.56 157.85 18.56L157.85 18.56Q156.49 18.56 155.43 19.30Q154.37 20.04 153.77 21.41Q153.17 22.78 153.17 24.68L153.17 24.68Q153.17 28.28 154.91 30.18Q156.65 32.08 159.43 32.08L159.43 32.08Q161.31 32.08 162.63 31.22Q163.95 30.36 164.57 29.20L164.57 29.20Q164.85 28.86 165.06 28.72Q165.27 28.58 165.49 28.58L165.49 28.58Q165.77 28.60 165.95 28.82Q166.13 29.04 166.11 29.46L166.11 29.46Q166.03 30.96 165.04 32.30Q164.05 33.64 162.33 34.47Q160.61 35.30 158.29 35.30L158.29 35.30Q155.51 35.30 153.46 34.12Q151.41 32.94 150.30 30.83Q149.19 28.72 149.19 25.98L149.19 25.98Q149.19 23.20 150.30 21.01Q151.41 18.82 153.49 17.55Q155.57 16.28 158.45 16.28L158.45 16.28Q160.73 16.28 162.44 17.18Q164.15 18.08 165.11 19.65Q166.07 21.22 166.07 23.22Z";
}
