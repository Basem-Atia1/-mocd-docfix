using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The same file is written down three ways — CRM's relative path, the vendor's UNC-rooted one,
/// and whatever an operator pastes off a screen. Comparing those as strings answers the wrong
/// question, and answering the wrong question here means saying a file is gone when it is not.
/// </summary>
public class FilePathsTests
{
    private const string Path = @"DigitalServices\cd97bf8d\20260914\78efd6b2.png";

    [Fact]
    public void A_plain_relative_path_is_left_as_it_is()
        => Assert.Equal(Path, FilePaths.Normalise(Path));

    [Fact]
    public void A_unc_rooted_path_is_reduced_to_the_part_that_identifies_the_file()
        => Assert.Equal(Path, FilePaths.Normalise(@"\\mocdstgdpfs01\dms\" + Path));

    [Fact]
    public void Forward_slashes_quotes_and_spaces_are_all_forgiven()
    {
        Assert.Equal(Path, FilePaths.Normalise(Path.Replace('\\', '/')));
        Assert.Equal(Path, FilePaths.Normalise("\"" + Path + "\""));
        Assert.Equal(Path, FilePaths.Normalise("   " + Path + "  "));
    }

    [Fact]
    public void Two_ways_of_writing_the_same_file_compare_equal()
    {
        Assert.True(FilePaths.Same(Path, @"\\server\share\" + Path));
        Assert.True(FilePaths.Same(Path, Path.ToUpperInvariant()));
        Assert.False(FilePaths.Same(Path, Path.Replace("78efd6b2", "00000000")));
    }

    [Fact]
    public void Nothing_is_never_the_same_as_something()
    {
        Assert.False(FilePaths.Same(null, Path));
        Assert.False(FilePaths.Same(Path, null));
        Assert.False(FilePaths.Same("", ""));
    }

    [Fact]
    public void A_path_is_told_from_an_id_or_a_name_by_its_separators()
    {
        Assert.True(FilePaths.LooksLikeAPath(Path));
        Assert.True(FilePaths.LooksLikeAPath(Path.Replace('\\', '/')));
        Assert.False(FilePaths.LooksLikeAPath("2a1c51a3-e330-f111-b119-005056010908"));
        Assert.False(FilePaths.LooksLikeAPath("cert.jpg"));
    }
}
