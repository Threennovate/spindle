using System.Globalization;
using System.Text;

namespace Spindle.Api.Reports.Export;

/// <summary>
/// Writes a report as CSV (RFC 4180), in UTF-8 with a byte order mark so that Excel reads names such as "Anić" right.
/// Numbers use a decimal point, whatever the reader's locale.
/// </summary>
internal static class CsvExport
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static byte[] Write(ReportTable table)
    {
        var csv = new StringBuilder();
        AppendLine(csv, table.Columns.Select(c => c.Header));
        foreach (var row in table.Rows)
        {
            AppendLine(csv, row.Cells.Select((cell, i) => Format(cell, table.Columns[i].Kind)));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static string Format(object? cell, CellKind kind) => cell switch
    {
        null => string.Empty,
        long seconds when kind == CellKind.Duration => ReportTables.FormatDuration(seconds),
        decimal number => number.ToString("0.00", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        string text => DefuseFormula(text),
        _ => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Spreadsheets run a cell that starts with = + - @ as a formula. Descriptions come from people, so such text is
    /// kept as text with a leading apostrophe, as OWASP recommends against CSV injection.
    /// </summary>
    private static string DefuseFormula(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static void AppendLine(StringBuilder csv, IEnumerable<string> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first)
            {
                csv.Append(',');
            }

            first = false;
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                csv.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                csv.Append(cell);
            }
        }

        csv.Append("\r\n");
    }
}
