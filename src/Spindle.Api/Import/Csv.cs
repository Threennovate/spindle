using System.Text;

namespace Spindle.Api.Import;

/// <summary>
/// Reads CSV as spreadsheets and time trackers write it (RFC 4180): quoted fields may hold commas, quotes and line
/// breaks. Excel in much of Europe separates with semicolons, so the header decides which separator the file uses.
/// </summary>
internal static class Csv
{
    public static List<string[]> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var separator = DetectSeparator(text);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                AddRow(rows, row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            AddRow(rows, row);
        }

        return rows;
    }

    private static void AddRow(List<string[]> rows, List<string> row)
    {
        // Blank lines, such as the one after the last row, are not rows.
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
        }
    }

    private static char DetectSeparator(string text)
    {
        int commas = 0, semicolons = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted && c == ',')
            {
                commas++;
            }
            else if (!quoted && c == ';')
            {
                semicolons++;
            }
        }

        return semicolons > commas ? ';' : ',';
    }
}
