using RPG.Common;
using RPG.Common.Models;

namespace RPG.Tests;

public class JoinRulesTests
{
    [Fact]
    public void Master_WithoutCharacter_CanJoin()
    {
        Assert.Null(JoinRules.GetRoleDenial(Player.RoleType.master, hasLinkedCharacter: false));
    }

    [Fact]
    public void Master_WithCharacter_IsDenied()
    {
        var denial = JoinRules.GetRoleDenial(Player.RoleType.master, hasLinkedCharacter: true);

        Assert.NotNull(denial);
        Assert.Contains("master", denial, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Player_WithCharacter_CanJoin()
    {
        Assert.Null(JoinRules.GetRoleDenial(Player.RoleType.player, hasLinkedCharacter: true));
    }

    [Fact]
    public void Player_WithoutCharacter_IsDenied_AndToldToLinkFirst()
    {
        var denial = JoinRules.GetRoleDenial(Player.RoleType.player, hasLinkedCharacter: false);

        Assert.NotNull(denial);
        Assert.Contains("/link", denial);
    }
}
