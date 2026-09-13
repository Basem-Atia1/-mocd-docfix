using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class SelectionTests
{
    private static IReadOnlyList<int> Ok(string input, int count)
    {
        var result = Selection.Parse(input, count);
        Assert.Null(result.Error);
        return result.Indexes;
    }

    private static string Bad(string input, int count)
    {
        var result = Selection.Parse(input, count);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Indexes);
        return result.Error!;
    }

    [Fact]
    public void A_single_number_selects_one_row()
    {
        Assert.Equal(new[] { 0 }, Ok("1", 10));
    }

    [Fact]
    public void A_comma_list_selects_each_one()
    {
        Assert.Equal(new[] { 0, 2, 4 }, Ok("1,3,5", 10));
    }

    [Fact]
    public void Spaces_around_the_commas_do_not_matter()
    {
        Assert.Equal(new[] { 0, 2 }, Ok(" 1 , 3 ", 10));
    }

    [Fact]
    public void Spaces_alone_work_as_separators_too()
    {
        Assert.Equal(new[] { 0, 2 }, Ok("1 3", 10));
    }

    [Fact]
    public void A_range_selects_everything_between_the_ends()
    {
        Assert.Equal(new[] { 2, 3, 4, 5 }, Ok("3-6", 10));
    }

    [Fact]
    public void A_range_and_singles_can_be_mixed()
    {
        Assert.Equal(new[] { 0, 2, 3, 4, 8 }, Ok("1,3-5,9", 10));
    }

    [Fact]
    public void All_selects_every_row()
    {
        Assert.Equal(new[] { 0, 1, 2 }, Ok("all", 3));
        Assert.Equal(new[] { 0, 1, 2 }, Ok("ALL", 3));
        Assert.Equal(new[] { 0, 1, 2 }, Ok("*", 3));
    }

    [Fact]
    public void Duplicates_are_collapsed_and_the_result_is_ordered()
    {
        Assert.Equal(new[] { 0, 2, 4 }, Ok("5,1,3,3,1", 10));
    }

    [Fact]
    public void A_backwards_range_is_read_the_way_it_was_obviously_meant()
    {
        Assert.Equal(new[] { 2, 3, 4 }, Ok("5-3", 10));
    }

    [Fact]
    public void Nothing_typed_is_an_error_rather_than_an_empty_run()
    {
        Assert.Contains("nothing", Bad("", 10), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing", Bad("   ", 10), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("1,11")]
    [InlineData("9-12")]
    public void A_number_outside_the_list_is_named_in_the_error(string input)
    {
        var error = Bad(input, 10);

        Assert.Contains("1 to 10", error);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("1,banana")]
    [InlineData("1-")]
    [InlineData("-3")]
    [InlineData("1-2-3")]
    public void Anything_that_is_not_a_number_or_a_range_is_rejected(string input)
    {
        Assert.False(string.IsNullOrWhiteSpace(Bad(input, 10)));
    }

    [Fact]
    public void An_empty_list_can_never_be_selected_from()
    {
        Assert.Contains("nothing", Bad("all", 0), StringComparison.OrdinalIgnoreCase);
    }
}
