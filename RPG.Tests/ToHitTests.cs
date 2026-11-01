using RPG.Common;
using RPG.Common.Utils;
using Xunit;

namespace RPG.Tests;

public class ToHitTests
{
    // Un solo dado, senza esplosione
    private static GameDice.D10Roll Single(int die) => new(die, die, 1, [die]);

    [Fact]
    public void Hit_WhenTotalAboveTn()
    {
        // 7 + REF 8 + Pistola 4 = 19 contro TN 15
        var r = CombatRules.ResolveToHit(Single(7), stat: 8, skillLevel: 4, woundPenalty: 0, weaponModifier: 0, cyberBonus: 0, targetNumber: 15);

        Assert.Equal(19, r.Total);
        Assert.True(r.IsHit);
        Assert.False(r.IsFumble);
    }

    [Fact]
    public void Hit_WhenTotalEqualsTn_Miss_WhenOneBelow()
    {
        // 3 + 8 + 4 = 15
        Assert.True(CombatRules.ResolveToHit(Single(3), 8, 4, 0, 0, 0, targetNumber: 15).IsHit);
        Assert.False(CombatRules.ResolveToHit(Single(2), 8, 4, 0, 0, 0, targetNumber: 15).IsHit);
    }

    [Fact]
    public void ExplodedDie_AddsAllDice()
    {
        var exploded = new GameDice.D10Roll(16, 10, 2, [10, 6]);

        var r = CombatRules.ResolveToHit(exploded, stat: 8, skillLevel: 4, woundPenalty: 0, weaponModifier: 0, cyberBonus: 0, targetNumber: 28);

        Assert.Equal(28, r.Total);
        Assert.True(r.IsHit);
    }

    [Fact]
    public void NaturalOne_IsFumble_AndAlwaysMisses()
    {
        // Totale 1 + 20 + 10 = 31, molto sopra il TN, ma l'1 naturale manca comunque
        var r = CombatRules.ResolveToHit(Single(1), stat: 20, skillLevel: 10, woundPenalty: 0, weaponModifier: 0, cyberBonus: 0, targetNumber: 10);

        Assert.Equal(31, r.Total);
        Assert.True(r.IsFumble);
        Assert.False(r.IsHit);
    }

    [Fact]
    public void WoundPenaltyAndWeaponModifier_AreSummedWithTheirSign()
    {
        // 10 + 5 + 3 - 2 (ferite Seria) - 1 (arma imprecisa) + 1 (cyber) = 16
        var r = CombatRules.ResolveToHit(Single(10), stat: 5, skillLevel: 3, woundPenalty: -2, weaponModifier: -1, cyberBonus: 1, targetNumber: 16);

        Assert.Equal(16, r.Total);
        Assert.True(r.IsHit);
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]
    [InlineData(6, 4, 0, 20)]
    [InlineData(6, 4, -2, 18)]
    public void MeleeTargetNumber_IsTenPlusDefence(int targetRef, int dodge, int penalty, int expected)
    {
        Assert.Equal(expected, CombatRules.GetMeleeTargetNumber(targetRef, dodge, penalty));
    }

    [Fact]
    public void MeleeRange_IsTwoAndAHalfMetres()
    {
        Assert.Equal(2.5, CombatRules.MeleeMaxDistanceMeters);
    }
}
