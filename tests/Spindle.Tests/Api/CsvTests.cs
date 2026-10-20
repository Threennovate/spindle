using Spindle.Api.Import;

namespace Spindle.Tests.Api;

public sealed class CsvTests
{
    [Fact]
    public void Reads_quoted_fields_with_separators_quotes_and_line_breaks()
    {
        var rows = Csv.Read("﻿Name,Tags,Note\r\n\"Anić, Ana\",\"a, b\",\"She said \"\"hi\"\"\nand left\"\r\nIvo,,\r\n\r\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Name", "Tags", "Note"], rows[0]);
        Assert.Equal(["Anić, Ana", "a, b", "She said \"hi\"\nand left"], rows[1]);
        Assert.Equal(["Ivo", "", ""], rows[2]);
    }

    [Fact]
    public void Reads_files_that_Excel_saved_with_semicolons()
    {
        var rows = Csv.Read("Project;Duration\nWebsite, redesign;1:30:00\n\"Mobile; app\";0:30:00");

        Assert.Equal(["Website, redesign", "1:30:00"], rows[1]);
        Assert.Equal(["Mobile; app", "0:30:00"], rows[2]);
    }

    [Fact]
    public void Reads_a_last_line_without_a_line_break()
    {
        Assert.Equal(["a", "b"], Csv.Read("x,y\na,b").Last());
        Assert.Empty(Csv.Read(""));
    }
}
