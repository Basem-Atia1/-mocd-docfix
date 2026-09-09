using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class FilePathParserTests
{
    [Fact]
    public void Parses_the_correct_four_segment_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg");

        Assert.Equal("DigitalServices", p.Root);
        Assert.Equal("cd97bf8d-bea8-f011-b116-005056010908", p.CategorySegment);
        Assert.Equal("20260330", p.DateSegment);
        Assert.Equal("5b05398a-b0bc-4c26-a6a6-40b7e0ece187", p.FileStem);
        Assert.Equal(".jpg", p.Extension);
        Assert.Equal(4, p.SegmentCount);
        Assert.False(p.HasDoubledSeparators);
    }

    [Fact]
    public void Parses_the_document_type_name_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg");

        Assert.Equal("goodConductCertificate", p.CategorySegment);
        Assert.Equal("20260330", p.DateSegment);
    }

    [Fact]
    public void Parses_the_docType_prefixed_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\docType2746f51e7e3ef111b119005056010908\20260518\028b1307-7972-43e0-8566-d1687f2e4d63.pdf");

        Assert.Equal("docType2746f51e7e3ef111b119005056010908", p.CategorySegment);
        Assert.Equal(".pdf", p.Extension);
    }

    [Fact]
    public void Three_segment_shape_has_no_category_and_still_finds_the_date()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\20260707\090d2179-20be-4ec3-a558-a182adc5f38d.doc");

        Assert.Null(p.CategorySegment);
        Assert.Equal("20260707", p.DateSegment);
        Assert.Equal(3, p.SegmentCount);
    }

    [Fact]
    public void Numeric_category_is_kept_verbatim()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\0\20260423\c91918d7-5698-4d08-b227-0005d35e76db.png");

        Assert.Equal("0", p.CategorySegment);
    }

    [Fact]
    public void Doubled_separators_are_collapsed_and_flagged()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\\POD\\20250911\\e9733bfd-07e9-4060-a7bf-31e162412ce5.png");

        Assert.True(p.HasDoubledSeparators);
        Assert.Equal("POD", p.CategorySegment);
        Assert.Equal("20250911", p.DateSegment);
        Assert.Equal(4, p.SegmentCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_an_empty_parse(string? raw)
    {
        var p = FilePathParser.Parse(raw);

        Assert.Equal(0, p.SegmentCount);
        Assert.Null(p.CategorySegment);
        Assert.Equal(string.Empty, p.FileStem);
    }
}
