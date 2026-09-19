using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class DocumentGroupsTests
{
    [Fact]
    public void All_nine_groups_are_defined()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, DocumentGroups.All.Select(g => g.Number).OrderBy(n => n));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    public void Groups_one_to_five_are_fixed_and_the_rest_are_not(int number, bool willBeFixed)
    {
        Assert.Equal(willBeFixed, DocumentGroups.Get(number).WillBeFixed);
    }

    [Fact]
    public void Every_group_explains_itself_in_full()
    {
        Assert.All(DocumentGroups.All, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.ShortLabel), $"group {g.Number} ShortLabel");
            Assert.False(string.IsNullOrWhiteSpace(g.WhatIsInThePath), $"group {g.Number} WhatIsInThePath");
            Assert.False(string.IsNullOrWhiteSpace(g.WhyItIsWrong), $"group {g.Number} WhyItIsWrong");
            Assert.False(string.IsNullOrWhiteSpace(g.HowWeKnow), $"group {g.Number} HowWeKnow");
            Assert.False(string.IsNullOrWhiteSpace(g.WhatTheToolDoes), $"group {g.Number} WhatTheToolDoes");
        });
    }

    [Fact]
    public void Short_labels_are_one_line_so_they_fit_in_a_menu()
    {
        Assert.All(DocumentGroups.All, g =>
        {
            Assert.DoesNotContain('\n', g.ShortLabel);
            Assert.True(g.ShortLabel.Length <= 60, $"group {g.Number} label is {g.ShortLabel.Length} chars");
        });
    }

    [Fact]
    public void An_unknown_group_number_is_rejected_rather_than_returning_a_blank()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentGroups.Get(10));
    }

    [Fact]
    public void The_groups_that_get_fixed_are_exactly_one_to_five()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, DocumentGroups.Fixable.Select(g => g.Number).OrderBy(n => n));
    }
}
