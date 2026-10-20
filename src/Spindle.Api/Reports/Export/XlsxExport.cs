using ClosedXML.Excel;

namespace Spindle.Api.Reports.Export;

/// <summary>Writes a report as an Excel workbook, with real durations, numbers and dates that add up in formulas.</summary>
internal static class XlsxExport
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int HeaderRow = 5;

    public static byte[] Write(ReportTable table)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(table.Title.Replace(" report", string.Empty, StringComparison.Ordinal));

        sheet.Cell(1, 1).Value = table.Title;
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell(2, 1).Value = $"{table.Workspace} · {table.Period}";
        sheet.Cell(3, 1).Value = string.Join(" · ", table.Totals.Select(t => $"{t.Label} {t.Value}"));
        sheet.Range(2, 1, 3, 1).Style.Font.SetFontColor(XLColor.FromHtml("#6B7280"));

        for (var c = 0; c < table.Columns.Count; c++)
        {
            var column = table.Columns[c];
            var header = sheet.Cell(HeaderRow, c + 1);
            header.Value = column.Header;
            header.Style.Font.SetBold();
            header.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F3F4F6"));
            header.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin);
            if (column.Kind != CellKind.Text)
            {
                header.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            }

            sheet.Column(c + 1).Width = Math.Max(column.Width, column.Header.Length + 2);
        }

        var row = HeaderRow + 1;
        foreach (var tableRow in table.Rows)
        {
            WriteRow(sheet, table, tableRow, row++);
        }

        var lastDataRow = row - 1;
        WriteRow(sheet, table, table.Footer, row);
        sheet.Row(row).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);

        if (lastDataRow > HeaderRow)
        {
            sheet.Range(HeaderRow, 1, lastDataRow, table.Columns.Count).SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(HeaderRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteRow(IXLWorksheet sheet, ReportTable table, TableRow tableRow, int row)
    {
        for (var c = 0; c < tableRow.Cells.Count; c++)
        {
            var cell = sheet.Cell(row, c + 1);
            var kind = table.Columns[c].Kind;
            switch (tableRow.Cells[c])
            {
                case long seconds when kind == CellKind.Duration:
                    cell.Value = TimeSpan.FromSeconds(seconds);
                    cell.Style.NumberFormat.Format = "[h]:mm:ss";
                    break;
                case decimal number:
                    cell.Value = number;
                    cell.Style.NumberFormat.Format = kind == CellKind.Money ? "#,##0.00" : "0.00";
                    break;
                case DateOnly date:
                    cell.Value = date.ToDateTime(TimeOnly.MinValue);
                    cell.Style.NumberFormat.Format = "yyyy-mm-dd";
                    break;
                case TimeOnly time:
                    cell.Value = time.ToTimeSpan();
                    cell.Style.NumberFormat.Format = "hh:mm";
                    break;
                case string text:
                    // Set as a value, so text that looks like a formula stays text.
                    cell.Value = text;
                    break;
            }
        }

        if (tableRow.IsGroup)
        {
            sheet.Row(row).Style.Font.SetBold();
        }
    }
}
