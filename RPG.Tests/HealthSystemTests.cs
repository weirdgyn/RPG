using System.Text.Json;
using RPG.Common.Models;
using Xunit;

namespace RPG.Tests;

public class HealthSystemTests
{
    private static HealthSystem WithDamage(int damage) =>
        new() { MaxHealth = HealthSystem.TrackBoxes, CurrentHealth = HealthSystem.TrackBoxes - damage };

    [Fact]
    public void NewCharacter_IsUnharmedWith40Boxes()
    {
        var h = new HealthSystem();

        Assert.Equal(40, h.MaxHealth);
        Assert.Equal(40, h.CurrentHealth);
        Assert.Equal(0, h.DamageTaken);
        Assert.Equal(0, h.GetWoundPenalty());
        Assert.Equal("Illeso", h.GetWoundStatusDescription());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, -2)]
    [InlineData(8, -2)]
    [InlineData(9, -4)]
    [InlineData(12, -4)]
    [InlineData(13, -6)]
    [InlineData(36, -6)]
    public void WoundPenalty_ByDamageTaken(int damage, int expected)
    {
        Assert.Equal(expected, WithDamage(damage).GetWoundPenalty());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(12, 2)]
    [InlineData(13, 3)]   // Mortale 0
    [InlineData(16, 3)]
    [InlineData(17, 4)]   // Mortale 1
    [InlineData(21, 5)]   // Mortale 2
    public void StunSaveModifier_ByDamageTaken(int damage, int expected)
    {
        Assert.Equal(expected, WithDamage(damage).GetStunSaveModifier());
    }

    [Theory]
    [InlineData(1, "Leggera (Nessuna penalità)")]
    [InlineData(5, "Seria (-2 ai tiri)")]
    [InlineData(9, "Critica (-4 ai tiri)")]
    [InlineData(13, "Mortale 0 (-6 ai tiri)")]
    [InlineData(17, "Mortale 1 (-6 ai tiri)")]
    public void WoundStatusDescription_ByDamageTaken(int damage, string expected)
    {
        Assert.Equal(expected, WithDamage(damage).GetWoundStatusDescription());
    }

    [Fact]
    public void LastBox_IsMortale6_NotDeadYet()
    {
        // 40 danni = ultima casella (Mortale 6): si è ancora in gioco, con -6.
        var h = WithDamage(40);

        Assert.False(h.IsDead);
        Assert.Equal(-6, h.GetWoundPenalty());
        Assert.Equal("Mortale 6 (-6 ai tiri)", h.GetWoundStatusDescription());
    }

    [Fact]
    public void DamageOverTrack_IsDeath()
    {
        // 41 danni = una casella oltre la pista: morto, fuori gioco.
        var h = WithDamage(41);

        Assert.True(h.IsDead);
        Assert.Equal(-99, h.GetWoundPenalty());
        Assert.Equal("Morto", h.GetWoundStatusDescription());
    }

    [Fact]
    public void StunnedFlag_IsSessionOnly_NotSerialized()
    {
        string json = JsonSerializer.Serialize(new HealthSystem { IsStunned = true });

        Assert.DoesNotContain("IsStunned", json);
    }
}
