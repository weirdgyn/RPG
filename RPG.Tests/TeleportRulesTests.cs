using RPG.Common;
using Xunit;

namespace RPG.Tests;

public class TeleportRulesTests
{
    [Fact]
    public void ParseTargets_SplitsOnSpacesAndCommas()
    {
        var list = TeleportRules.ParseTargets(["morgan,", "molly", "case,wintermute"]);

        Assert.Equal(["morgan", "molly", "case", "wintermute"], list);
    }

    [Fact]
    public void ParseTargets_RemovesDuplicatesIgnoringCase()
    {
        var list = TeleportRules.ParseTargets(["Morgan", "morgan,", "MORGAN"]);

        Assert.Single(list);
        Assert.Equal("Morgan", list[0]);
    }

    [Fact]
    public void ParseTargets_IgnoresEmptyParts()
    {
        Assert.Empty(TeleportRules.ParseTargets([",", " ", ",,"]));
        Assert.Empty(TeleportRules.ParseTargets([]));
    }
}
