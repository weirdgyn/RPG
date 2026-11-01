using RPG.Common;
using RPG.Common.Models;

namespace RPG.Tests;

public class DamageResolutionTests
{
    private static ArmorPiece Piece(int sp, ArmorLocation loc = ArmorLocation.Torso) =>
        new() { Id = "p", Name = "Test", Location = loc, MaxStopPower = sp, CurrentStopPower = sp };

    [Fact]
    public void Penetrating_Hit_GoesThroughArmorThenBtm()
    {
        var piece = Piece(10);

        var r = CombatRules.ResolveDamage(piece, rawDamage: 14, locationMultiplier: 1, armorPiercing: false, targetBody: 6);

        Assert.Equal(4, r.Penetrating);
        Assert.Equal(2, r.BtmReduction);
        Assert.Equal(2, r.Dealt);
        Assert.True(r.Armor.Ablated);
        Assert.Equal(9, piece.CurrentStopPower);
    }

    [Fact]
    public void HeadHit_MultipliesAfterArmorBeforeBtm()
    {
        var r = CombatRules.ResolveDamage(Piece(5, ArmorLocation.Head), rawDamage: 8, locationMultiplier: 2, armorPiercing: false, targetBody: 6);

        Assert.Equal(3, r.Penetrating);        // prima del moltiplicatore
        Assert.Equal(6, r.Armor.DamageDealt);  // (8 - 5) * 2
        Assert.Equal(4, r.Dealt);              // 6 - BTM 2
    }

    [Fact]
    public void FullyAbsorbed_DealsNothingEvenWithHighBtm()
    {
        var piece = Piece(10);

        var r = CombatRules.ResolveDamage(piece, rawDamage: 4, locationMultiplier: 1, armorPiercing: false, targetBody: 10);

        Assert.Equal(0, r.Dealt);
        Assert.Equal(4, r.BtmReduction);
        Assert.False(r.Armor.Ablated);
        Assert.Equal(10, piece.CurrentStopPower);
    }

    [Fact]
    public void PenetratingHit_AlwaysDoesAtLeastOne()
    {
        var r = CombatRules.ResolveDamage(null, rawDamage: 1, locationMultiplier: 1, armorPiercing: false, targetBody: 11);

        Assert.Equal(5, r.BtmReduction);
        Assert.Equal(1, r.Dealt);
    }

    [Fact]
    public void ArmorPiercing_HalvesSpBeforeBtm()
    {
        var r = CombatRules.ResolveDamage(Piece(5), rawDamage: 8, locationMultiplier: 1, armorPiercing: true, targetBody: 3);

        Assert.Equal(3, r.Armor.SpFaced);
        Assert.Equal(5, r.Penetrating);
        Assert.Equal(1, r.BtmReduction);
        Assert.Equal(4, r.Dealt);
    }

    [Fact]
    public void TakeDamage_SubtractsAndNeverGoesBelowZero()
    {
        var h = new HealthSystem();

        h.TakeDamage(8);
        Assert.Equal(32, h.CurrentHealth);

        h.TakeDamage(100);
        Assert.Equal(0, h.CurrentHealth);
    }

    [Fact]
    public void TakeDamage_NegativeAmountDoesNotHeal()
    {
        var h = new HealthSystem { CurrentHealth = 30 };

        h.TakeDamage(-5);

        Assert.Equal(30, h.CurrentHealth);
    }
}
