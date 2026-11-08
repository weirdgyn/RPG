using RPG.Common.Effects;
using RPG.Common.Models;
using Xunit;

namespace RPG.Tests;

public class EffectMovementTests
{
    private static Character Pg(int ma) => new() { Handle = "morgan", Stats = new Stats { MA = ma } };

    private static ActiveEffect Fx(string key, int value, string handle = "morgan")
    {
        Assert.True(EffectKey.TryParse(key, null, out var k, out _));
        return ActiveEffect.Create(handle, "master", "bonus", k, value, EffectDuration.Manual);
    }

    private static Character.CyberWare Cyber(string applyTo, int value) =>
        new() { ID = "gambe", Bonus = new Character.BonusType { ApplyTo = applyTo, Value = value } };

    [Fact]
    public void NoEffects_UsesTheSheetValue() =>
        Assert.Equal(5, EffectRules.MovementAllowance([], Pg(5)));

    [Fact]
    public void MoveAndStatMaEffects_AddUp()
    {
        Assert.Equal(8, EffectRules.MovementAllowance([Fx("MOVE", 2), Fx("STAT:MA", 1)], Pg(5)));
    }

    [Fact]
    public void NegativeEffects_NeverGoBelowZero()
    {
        Assert.Equal(0, EffectRules.MovementAllowance([Fx("MOVE", -9)], Pg(5)));
    }

    [Fact]
    public void OtherKeysAndOtherCharacters_DoNotChangeMovement()
    {
        var effects = new[] { Fx("ALL", 5), Fx("INIT", 5), Fx("HIT", 5), Fx("DAMAGE", 5), Fx("STAT:REF", 5), Fx("MOVE", 4, "molly") };
        Assert.Equal(5, EffectRules.MovementAllowance(effects, Pg(5)));
    }

    [Fact]
    public void CyberwareBonus_CountsToo()
    {
        var c = Pg(5);
        c.Cyberware.Add(Cyber("MOVE", 2));
        Assert.Equal(9, EffectRules.MovementAllowance([Fx("MOVE", 2)], c));
    }

    [Fact]
    public void StatMa_DoesNotAffectOtherRolls()
    {
        Assert.False(new EffectKey(EffectScope.Stat, "MA").Matches(new EffectContext(RollKind.Skill, "REF", "Pistola")));
        Assert.True(new EffectKey(EffectScope.Stat, "MA").Matches(EffectRules.MoveContext));
        Assert.False(new EffectKey(EffectScope.Stat, "REF").Matches(EffectRules.MoveContext));
    }
}
