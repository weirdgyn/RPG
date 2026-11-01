using RPG.Common;
using RPG.Common.Models;
using RPG.Common.World;
using Xunit;

namespace RPG.Tests;

public class CombatRulesTests
{
    [Theory]
    [InlineData(0.5, 10)]
    [InlineData(1.0, 10)]
    [InlineData(1.1, 15)]
    [InlineData(12.5, 15)]
    [InlineData(12.6, 20)]
    [InlineData(25.0, 20)]
    [InlineData(25.1, 25)]
    [InlineData(50.0, 25)]
    [InlineData(50.1, 30)]
    [InlineData(100.0, 30)]
    public void RangedTargetNumber_BandsScaleOnWeaponRange50(double distance, int expected)
    {
        Assert.Equal(expected, CombatRules.GetRangedTargetNumber(distance, 50));
    }

    [Fact]
    public void RangedTargetNumber_BeyondDoubleRange_IsOutOfRange()
    {
        Assert.Null(CombatRules.GetRangedTargetNumber(100.1, 50));
    }

    [Theory]
    [InlineData(5.0, 15)]
    [InlineData(5.1, 20)]
    [InlineData(10.0, 20)]
    [InlineData(20.0, 25)]
    [InlineData(40.0, 30)]
    public void RangedTargetNumber_BandsScaleOnShortRange(double distance, int expected)
    {
        Assert.Equal(expected, CombatRules.GetRangedTargetNumber(distance, 20));
    }

    [Fact]
    public void RangedTargetNumber_BeyondShortRangeLimit_IsOutOfRange()
    {
        Assert.Null(CombatRules.GetRangedTargetNumber(40.1, 20));
    }

    [Fact]
    public void RangedTargetNumber_RangeZero_FallsBackTo50()
    {
        Assert.Equal(25, CombatRules.GetRangedTargetNumber(30, 0));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(7, 2)]
    [InlineData(8, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(11, 5)]
    [InlineData(15, 5)]
    public void BodyTypeReduction_FollowsCp2020Table(int body, int expected)
    {
        Assert.Equal(expected, CombatRules.GetBodyTypeReduction(body));
    }

    [Theory]
    [InlineData(0, 2, 0)]   // colpo tutto assorbito dall'armatura: resta 0
    [InlineData(1, 2, 1)]   // penetrato: almeno 1
    [InlineData(2, 2, 1)]
    [InlineData(5, 2, 3)]
    [InlineData(10, 0, 10)]
    public void BodyTypeModifier_ReducesButPenetratingHitDoesAtLeastOne(int damage, int reduction, int expected)
    {
        Assert.Equal(expected, CombatRules.ApplyBodyTypeModifier(damage, reduction));
    }

    private static ArmorPiece Piece(int sp) => new() { Id = "p", Name = "Test", Location = ArmorLocation.Torso, MaxStopPower = sp, CurrentStopPower = sp };

    [Fact]
    public void ApplyArmor_FullyAbsorbed_NoDamageNoAblation()
    {
        var piece = Piece(10);
        var r = CombatRules.ApplyArmor(piece, rawDamage: 4, locationMultiplier: 1);

        Assert.Equal(0, r.DamageDealt);
        Assert.Equal(4, r.Absorbed);
        Assert.False(r.Ablated);
        Assert.Equal(10, piece.CurrentStopPower);
    }

    [Fact]
    public void ApplyArmor_Penetrating_ReducesByAndAblatesOnePoint()
    {
        var piece = Piece(10);
        var r = CombatRules.ApplyArmor(piece, rawDamage: 14, locationMultiplier: 1);

        Assert.Equal(4, r.DamageDealt);
        Assert.Equal(10, r.SpFaced);
        Assert.Equal(10, r.Absorbed);
        Assert.True(r.Ablated);
        Assert.Equal(9, piece.CurrentStopPower);
    }

    [Fact]
    public void ApplyArmor_HeadMultiplierAppliesAfterArmor()
    {
        var piece = Piece(5);
        var r = CombatRules.ApplyArmor(piece, rawDamage: 8, locationMultiplier: 2);

        Assert.Equal(6, r.DamageDealt);   // (8 - 5) * 2
    }

    [Fact]
    public void ApplyArmor_ArmorPiercingHalvesSpRoundingUp()
    {
        var piece = Piece(5);
        var r = CombatRules.ApplyArmor(piece, rawDamage: 8, locationMultiplier: 1, armorPiercing: true);

        Assert.Equal(3, r.SpFaced);       // (5 + 1) / 2
        Assert.Equal(5, r.DamageDealt);
    }

    [Fact]
    public void ApplyArmor_NoPiece_AllDamagePasses()
    {
        var r = CombatRules.ApplyArmor(null, rawDamage: 6, locationMultiplier: 1);

        Assert.Equal(6, r.DamageDealt);
        Assert.False(r.Ablated);
    }

    [Theory]
    [InlineData(1, "Testa", 2, ArmorLocation.Head)]
    [InlineData(2, "Torso", 1, ArmorLocation.Torso)]
    [InlineData(4, "Torso", 1, ArmorLocation.Torso)]
    [InlineData(5, "Braccio Destro", 1, ArmorLocation.Arms)]
    [InlineData(6, "Braccio Sinistro", 1, ArmorLocation.Arms)]
    [InlineData(7, "Gamba Destra", 1, ArmorLocation.Legs)]
    [InlineData(8, "Gamba Destra", 1, ArmorLocation.Legs)]
    [InlineData(9, "Gamba Sinistra", 1, ArmorLocation.Legs)]
    [InlineData(10, "Gamba Sinistra", 1, ArmorLocation.Legs)]
    public void HitLocation_MapsRollToZone(int roll, string name, int multiplier, ArmorLocation armor)
    {
        var loc = CombatRules.ResolveHitLocation(roll);

        Assert.Equal(name, loc.Name);
        Assert.Equal(multiplier, loc.DamageMultiplier);
        Assert.Equal(armor, loc.ArmorLocation);
    }

    [Theory]
    [InlineData(1, 6, 0, true)]     // soglia 6: si sopravvive con 1-5
    [InlineData(5, 6, 0, true)]
    [InlineData(6, 6, 0, false)]    // il valore uguale alla soglia non basta
    [InlineData(1, 6, 1, true)]     // Mortale 1: soglia 5
    [InlineData(4, 6, 1, true)]
    [InlineData(5, 6, 1, false)]
    [InlineData(1, 6, 6, false)]    // soglia 0: non ci si salva mai
    [InlineData(1, 1, 0, false)]    // BODY 1: soglia 1, nemmeno l'1 basta
    [InlineData(9, 10, 0, true)]
    [InlineData(10, 10, 0, false)]  // un 10 naturale non salva neanche con BODY 10
    [InlineData(1, 3, 6, false)]    // BODY sotto il livello: soglia 0, mai negativa
    public void DeathSave_SurvivesOnD10BelowBodyMinusLevel(int die, int body, int level, bool expected)
    {
        Assert.Equal(expected, CombatRules.DeathSaveSurvives(die, body, level));
    }

    [Theory]
    [InlineData(8, 0, 8)]
    [InlineData(8, 3, 5)]
    [InlineData(8, 6, 2)]
    [InlineData(6, 6, 0)]
    [InlineData(3, 6, 0)]
    [InlineData(6, -1, 6)]   // livello non valido: nessuna penalità
    public void DeathSaveThreshold_IsBodyMinusLevel_NeverNegative(int body, int level, int expected)
    {
        Assert.Equal(expected, CombatRules.GetDeathSaveThreshold(body, level));
    }

    [Fact]
    public void Distance_IsEuclidean3D()
    {
        Assert.Equal(5.0, CombatRules.CalculateDistance(new Position3D(), new Position3D { X = 3, Y = 4 }), 6);
        Assert.Equal(3.0, CombatRules.CalculateDistance(new Position3D(), new Position3D { X = 1, Y = 2, Z = 2 }), 6);
    }
}
