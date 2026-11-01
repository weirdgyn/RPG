using RPG.Common;
using RPG.Common.Models;
using RPG.Common.Utils;

namespace RPG.Tests;

public class MedicalRulesTests
{
    private static GameDice.D10Roll Single(int die) => new(die, die, 1, [die]);

    private static HealthSystem WithDamage(int damage) =>
        new() { MaxHealth = HealthSystem.TrackBoxes, CurrentHealth = HealthSystem.TrackBoxes - damage };

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    public void Block_NotMortal_BelowThirteenDamage(int damage)
    {
        Assert.Equal(MedicalRules.StabilizeBlock.NotMortal, MedicalRules.GetStabilizeBlock(WithDamage(damage)));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(25)]
    [InlineData(40)]
    public void NoBlock_WhenMortalAndNotStabilized(int damage)
    {
        Assert.Equal(MedicalRules.StabilizeBlock.None, MedicalRules.GetStabilizeBlock(WithDamage(damage)));
    }

    [Fact]
    public void Block_AlreadyStabilized()
    {
        var h = WithDamage(20);
        h.IsStabilized = true;

        Assert.Equal(MedicalRules.StabilizeBlock.AlreadyStable, MedicalRules.GetStabilizeBlock(h));
    }

    [Fact]
    public void Block_Dead_TakesPrecedenceOverStabilized()
    {
        var h = WithDamage(41);
        h.IsStabilized = true;

        Assert.Equal(MedicalRules.StabilizeBlock.Dead, MedicalRules.GetStabilizeBlock(h));
    }

    [Fact]
    public void Stabilization_SucceedsAboveTn()
    {
        // 7 + TECH 6 + Pronto Soccorso 5 = 18 contro TN 13
        var r = MedicalRules.ResolveStabilization(Single(7), tech: 6, skillLevel: 5, healerWoundPenalty: 0, targetDamageTaken: 13);

        Assert.Equal(18, r.Total);
        Assert.Equal(13, r.TargetNumber);
        Assert.True(r.Success);
    }

    [Fact]
    public void Stabilization_EqualToTn_Succeeds_OneBelow_Fails()
    {
        // 4 + 6 + 5 = 15
        Assert.True(MedicalRules.ResolveStabilization(Single(4), 6, 5, 0, 15).Success);
        Assert.False(MedicalRules.ResolveStabilization(Single(4), 6, 5, 0, 16).Success);
    }

    [Fact]
    public void Stabilization_HealerWoundsLowerTheTotal()
    {
        // 7 + 6 + 5 - 4 (Critica) = 14 contro TN 17
        var r = MedicalRules.ResolveStabilization(Single(7), 6, 5, -4, 17);

        Assert.Equal(14, r.Total);
        Assert.False(r.Success);
    }

    [Fact]
    public void Stabilization_ExplodedDieCountsAllDice()
    {
        var exploded = new GameDice.D10Roll(15, 10, 2, [10, 5]);

        var r = MedicalRules.ResolveStabilization(exploded, 6, 5, 0, 26);

        Assert.Equal(26, r.Total);
        Assert.True(r.Success);
    }

    [Fact]
    public void Stabilization_NaturalOneAlwaysFails()
    {
        // Totale alto (1 + 10 + 10 = 21) contro TN 13, ma l'1 naturale è un fallimento critico
        var r = MedicalRules.ResolveStabilization(Single(1), 10, 10, 0, 13);

        Assert.True(r.IsFumble);
        Assert.False(r.Success);
    }

    [Fact]
    public void BestSkillLevel_PicksTheHighest()
    {
        var skills = new Dictionary<string, int> { ["Pronto Soccorso"] = 4, ["Tecnomedicina"] = 5, ["MedicalTech"] = 3 };

        Assert.Equal(5, MedicalRules.BestSkillLevel(skills, ["Pronto Soccorso", "Tecnomedicina", "MedicalTech"]));
    }

    [Fact]
    public void BestSkillLevel_NoSkills_IsZero()
    {
        Assert.Equal(0, MedicalRules.BestSkillLevel(new Dictionary<string, int>(), ["Pronto Soccorso", "Tecnomedicina"]));
    }

    [Fact]
    public void BestSkillLevel_IsCaseInsensitiveWithCaseInsensitiveDictionary()
    {
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["pronto soccorso"] = 4 };

        Assert.Equal(4, MedicalRules.BestSkillLevel(skills, ["Pronto Soccorso"]));
    }

    [Fact]
    public void HealRange_IsTwoAndAHalfMetres()
    {
        Assert.Equal(2.5, MedicalRules.HealRangeMeters);
    }
}
