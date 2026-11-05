using Nexus.Hub.Core.Search;

namespace Nexus.Tests;

public sealed class FuzzyMatchTests
{
    [Fact]
    public void Exact_beats_prefix_beats_contains_beats_scattered()
    {
        int exact = FuzzyMatch.Score("A101", "A101")!.Value;
        int prefix = FuzzyMatch.Score("A10", "A101 - Floor Plan")!.Value;
        int contains = FuzzyMatch.Score("floor", "A101 - First Floor Plan")!.Value;
        int scattered = FuzzyMatch.Score("rvap", "Review and apply")!.Value;
        Assert.True(exact > prefix && prefix > contains && contains > scattered);
    }

    [Fact]
    public void Every_word_must_match_and_order_matters()
    {
        Assert.NotNull(FuzzyMatch.Score("floor a1", "A101 - Floor Plan"));
        Assert.Null(FuzzyMatch.Score("floor z9", "A101 - Floor Plan"));
        Assert.Null(FuzzyMatch.Score("ylppa", "Review and apply"));
    }
}
