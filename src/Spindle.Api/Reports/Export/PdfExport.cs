using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Spindle.Api.Reports.Export;

/// <summary>
/// Writes a report as a printable A4 PDF in the look of Spindle's public pages: midnight ink on white, a large title,
/// the totals on a paper-coloured panel, then the table as a ledger of hairlines, repeating its header on every page.
/// A timesheet link's download and the app's report exports share it.
/// </summary>
internal static class PdfExport
{
    public const string ContentType = "application/pdf";

    private const string Font = "Geist";
    private const string Site = "https://spindletrack.com/?utm_source=pdf&utm_medium=referral";
    private static readonly Color Ink = Color.Parse("#0E1322");
    private static readonly Color Ink2 = Color.Parse("#454A59");
    private static readonly Color Muted = Color.Parse("#5F6474");
    private static readonly Color Paper = Color.Parse("#F7F6F3");
    private static readonly Color Inset = Color.Parse("#F4F3EF");
    private static readonly Color Hair = Color.Parse("#E7E8EA");
    private static readonly Color Hair2 = Color.Parse("#D2D4D9");
    private const double LedgerPadding = 6;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    static PdfExport()
    {
        // PDFsharp takes one font resolver for the life of the process, before any text is measured.
        GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
    }

    public static byte[] Write(ReportTable table)
    {
        var document = new Document();
        document.Info.Title = $"{table.Title}, {table.Period}";
        document.Info.Author = table.Workspace;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = document.AddSection();
        var setup = section.PageSetup;
        setup.PageFormat = PageFormat.A4;
        // Wide tables go on landscape pages.
        setup.Orientation = table.Columns.Count > 6 ? Orientation.Landscape : Orientation.Portrait;
        setup.TopMargin = Unit.FromCentimeter(2.2);
        setup.BottomMargin = Unit.FromCentimeter(2);
        setup.HeaderDistance = Unit.FromCentimeter(1);
        setup.FooterDistance = Unit.FromCentimeter(1);
        setup.LeftMargin = setup.RightMargin = Unit.FromCentimeter(1.8);
        setup.DifferentFirstPageHeaderFooter = true;
        var pageWidth = setup.Orientation == Orientation.Landscape ? Unit.FromCentimeter(29.7) : Unit.FromCentimeter(21);
        var contentWidth = pageWidth.Point - setup.LeftMargin.Point - setup.RightMargin.Point;

        AddPageHeaders(section, table, contentWidth);
        AddHeading(section, table);
        AddTotals(section, table, contentWidth);
        AddTable(section, table, contentWidth);
        AddPageFooters(section, contentWidth);

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>
    /// The first page carries who it is from, beside the Spindle lockup; later pages say what they continue, so a
    /// loose page still makes sense.
    /// </summary>
    private static void AddPageHeaders(Section section, ReportTable table, double contentWidth)
    {
        var first = section.Headers.FirstPage.AddParagraph();
        first.Format.AddTabStop(Unit.FromPoint(contentWidth), TabAlignment.Right);
        var sender = first.AddFormattedText(table.Workspace);
        sender.Bold = true;
        sender.Size = 9;
        if (table.Kind is { } kind)
        {
            var label = first.AddFormattedText($"  ·  {kind}");
            label.Color = Muted;
            label.Size = 9;
        }

        first.AddTab();
        AddLogo(first, 13);

        var later = section.Headers.Primary.AddParagraph();
        later.Format.AddTabStop(Unit.FromPoint(contentWidth), TabAlignment.Right);
        later.Format.Font.Size = 7.5;
        later.Format.Font.Color = Muted;
        later.AddText($"{table.Workspace}  ·  {table.Title}  ·  {table.Period}");
        later.AddTab();
        AddLogo(later, 10);
    }

    private static void AddLogo(Paragraph paragraph, double height)
    {
        var logo = paragraph.AddImage($"base64:{Logo.Value}");
        logo.Height = Unit.FromPoint(height);
        logo.LockAspectRatio = true;
    }

    /// <summary>The Spindle lockup, mark and name, drawn by branding/build.mjs.</summary>
    private static readonly Lazy<string> Logo = new(() =>
    {
        using var stream = typeof(PdfExport).Assembly.GetManifestResourceStream("Brand.spindle-logo.png")
            ?? throw new InvalidOperationException("The Spindle logo is not built into the API.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Convert.ToBase64String(bytes.ToArray());
    });

    private static void AddHeading(Section section, ReportTable table)
    {
        var title = section.AddParagraph(table.Title);
        title.Format.Font.Size = 28;
        title.Format.Font.Bold = true;
        title.Format.LineSpacingRule = LineSpacingRule.Exactly;
        title.Format.LineSpacing = Unit.FromPoint(31);
        title.Format.SpaceBefore = Unit.FromPoint(14);
        title.Format.SpaceAfter = Unit.FromPoint(6);

        var period = section.AddParagraph(table.Period);
        period.Format.Font.Size = 13;
        period.Format.Font.Color = Ink2;
        period.Format.SpaceAfter = Unit.FromPoint(20);
    }

    /// <summary>The totals as figures on a paper-coloured panel: a small label over a large number.</summary>
    private static void AddTotals(Section section, ReportTable table, double contentWidth)
    {
        if (table.Totals.Count == 0)
        {
            return;
        }

        var panel = section.AddTable();
        panel.Borders.Width = 0;
        panel.Shading.Color = Paper;
        panel.LeftPadding = Unit.FromPoint(14);
        panel.RightPadding = Unit.FromPoint(6);
        // The panel lines up with the ledger's rules under it, which reach past the text by its cell padding.
        panel.Rows.LeftIndent = Unit.FromPoint(-LedgerPadding);
        // Figures sit side by side at a fixed pace, so two figures don't spread across the page.
        var columns = Math.Max(table.Totals.Count, 3);
        for (var i = 0; i < columns; i++)
        {
            panel.AddColumn(Unit.FromPoint(contentWidth / columns));
        }

        var labels = panel.AddRow();
        labels.TopPadding = Unit.FromPoint(12);
        labels.Format.Font.Size = 8;
        labels.Format.Font.Color = Muted;
        var values = panel.AddRow();
        values.TopPadding = Unit.FromPoint(2);
        values.BottomPadding = Unit.FromPoint(13);
        values.Format.Font.Size = 17;
        values.Format.Font.Bold = true;
        for (var i = 0; i < table.Totals.Count; i++)
        {
            labels.Cells[i].AddParagraph(table.Totals[i].Label);
            values.Cells[i].AddParagraph(table.Totals[i].Value);
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void AddTable(Section section, ReportTable table, double contentWidth)
    {
        var pdfTable = section.AddTable();
        pdfTable.Borders.Width = 0;
        pdfTable.LeftPadding = Unit.FromPoint(LedgerPadding);
        pdfTable.RightPadding = Unit.FromPoint(LedgerPadding);
        var totalWidth = table.Columns.Sum(c => c.Width);
        foreach (var column in table.Columns)
        {
            var pdfColumn = pdfTable.AddColumn(Unit.FromPoint(contentWidth * column.Width / totalWidth));
            pdfColumn.Format.Alignment = IsNumber(column.Kind) ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }

        var header = pdfTable.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Size = 7.5;
        header.Format.Font.Color = Muted;
        header.TopPadding = Unit.FromPoint(4);
        header.BottomPadding = Unit.FromPoint(6);
        header.Borders.Bottom.Width = 0.75;
        header.Borders.Bottom.Color = Hair2;
        for (var c = 0; c < table.Columns.Count; c++)
        {
            header.Cells[c].AddParagraph(table.Columns[c].Header);
        }

        // Within one year the year is left out of each date; the period above already says it.
        var years = table.Rows.SelectMany(row => row.Cells).OfType<DateOnly>().Select(date => date.Year).Distinct().Count();
        var dateFormat = years <= 1 ? "ddd d MMM" : "ddd d MMM yyyy";
        object? previousDate = null;
        foreach (var row in table.Rows)
        {
            // A ledger names a day once: rows that continue the day above leave the date out and lose the rule between.
            var first = row.Cells.Count > 0 ? row.Cells[0] : null;
            var continues = first is DateOnly && Equals(first, previousDate);
            previousDate = first;
            if (continues)
            {
                pdfTable.Rows[pdfTable.Rows.Count - 1].Borders.Bottom.Width = 0;
            }

            var pdfRow = AddRow(pdfTable, table, row, dateFormat, continues);
            if (continues)
            {
                pdfRow.TopPadding = Unit.FromPoint(1);
            }
        }

        var footer = AddRow(pdfTable, table, table.Footer, dateFormat, continues: false);
        footer.Shading.Color = Inset;
        footer.Format.Font.Bold = true;
        footer.Format.Font.Size = 9;
        footer.TopPadding = footer.BottomPadding = Unit.FromPoint(7);
        footer.Borders.Top.Width = 1;
        footer.Borders.Top.Color = Hair2;
        footer.Borders.Bottom.Width = 0;
    }

    private static Row AddRow(Table pdfTable, ReportTable table, TableRow row, string dateFormat, bool continues)
    {
        // A sub-group repeats its group's labels for spreadsheets; on paper the group's row says them once, and the
        // sub-group's own name, in the last text column, is indented under it.
        var nameColumn = LastIndex(table.Columns, column => column.Kind == CellKind.Text);
        var pdfRow = pdfTable.AddRow();
        pdfRow.TopPadding = Unit.FromPoint(6);
        pdfRow.BottomPadding = Unit.FromPoint(6);
        pdfRow.Borders.Bottom.Width = 0.5;
        pdfRow.Borders.Bottom.Color = Hair;
        pdfRow.VerticalAlignment = VerticalAlignment.Top;
        for (var c = 0; c < row.Cells.Count; c++)
        {
            var kind = table.Columns[c].Kind;
            var hidden = (row.Level > 0 && c < nameColumn) || (continues && c == 0);
            var paragraph = pdfRow.Cells[c].AddParagraph(hidden ? string.Empty : Format(row.Cells[c], kind, table, dateFormat));
            if (row.Level > 0 && c == nameColumn)
            {
                paragraph.Format.LeftIndent = Unit.FromPoint(10);
            }

            // The day, and on a timesheet the time, are what a reader scans for; descriptions read in a softer ink.
            if ((kind == CellKind.Date || (kind == CellKind.Duration && table.Kind is not null)) && row.Level == 0)
            {
                paragraph.Format.Font.Bold = true;
            }
            else if (kind == CellKind.Text && c == nameColumn && !row.IsGroup)
            {
                paragraph.Format.Font.Color = Ink2;
            }
        }

        if (row.IsGroup)
        {
            pdfRow.Format.Font.Bold = true;
        }

        if (row.Level > 0)
        {
            pdfRow.Format.Font.Color = Ink2;
        }

        return pdfRow;
    }

    /// <summary>Where the document comes from, with a link, and the page count.</summary>
    private static void AddPageFooters(Section section, double contentWidth)
    {
        foreach (var footers in new[] { section.Footers.FirstPage, section.Footers.Primary })
        {
            var footer = footers.AddParagraph();
            footer.Format.AddTabStop(Unit.FromPoint(contentWidth), TabAlignment.Right);
            footer.Format.Font.Size = 7.5;
            footer.Format.Font.Color = Muted;
            footer.Format.Borders.Top.Width = 0.5;
            footer.Format.Borders.Top.Color = Hair;
            footer.Format.Borders.DistanceFromTop = Unit.FromPoint(7);
            footer.AddText("Kept with Spindle, the open-source time tracker  ·  ");
            var link = footer.AddHyperlink(Site, HyperlinkType.Web);
            link.AddFormattedText("spindletrack.com").Color = Ink;
            footer.AddTab();
            footer.AddText("Page ");
            footer.AddPageField();
            footer.AddText(" of ");
            footer.AddNumPagesField();
        }
    }

    private static int LastIndex(IReadOnlyList<TableColumn> columns, Func<TableColumn, bool> match)
    {
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            if (match(columns[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsNumber(CellKind kind) => kind is CellKind.Duration or CellKind.Hours or CellKind.Money;

    private static string Format(object? cell, CellKind kind, ReportTable table, string dateFormat) => cell switch
    {
        null => string.Empty,
        long seconds when kind == CellKind.Duration => table.Minutes ? ReportTables.FormatMinutes(seconds) : ReportTables.FormatDuration(seconds),
        decimal amount when kind == CellKind.Money => ReportTables.FormatMoney(amount, table.Currency).Replace($" {table.Currency}", string.Empty, StringComparison.Ordinal),
        decimal hours => hours.ToString("0.00", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString(dateFormat, English),
        TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        _ => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Serves the built-in Geist faces for every font PDFsharp asks for.</summary>
    private sealed class EmbeddedFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "Geist-SemiBold" : "Geist-Regular", mustSimulateBold: false, mustSimulateItalic: italic);

        public byte[] GetFont(string faceName)
        {
            using var stream = typeof(PdfExport).Assembly.GetManifestResourceStream($"Fonts.{faceName}.ttf")
                ?? throw new InvalidOperationException($"The font {faceName} is not built into the API.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
