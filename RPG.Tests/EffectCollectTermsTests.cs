using RPG.Common.Effects;
using RPG.Common.Models;
using Xunit;

namespace RPG.Tests;

public class EffectCollectTermsTests
{
    private static Character.CyberWare Cyber(string id, string applyTo, int value) =>
        new() { ID = id, Bonus = new Character.BonusType { ApplyTo = applyTo, Value = value } };

    private static ActiveEffect Master(string handle, string key, int value) =>
        ActiveEffect.Create(handle, "master", "master", ParseKey(key), value, EffectDuration.Manual);

    private static EffectKey ParseKey(string text)
    {
        Assert.True(EffectKey.TryParse(text, null, out var key, out _));
        return key;
    }

    private static readonly EffectContext Init = new(RollKind.Init, "REF", "CombatSense");
    private static readonly EffectContext Hit = new(RollKind.Hit, "REF", "Pistola");

    [Fact]
    public void CyberwareBonus_AppliesWhenItsKeyMatchesTheRoll()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(Cyber("Sandevistan", "INIT", 3));
        c.Cyberware.Add(Cyber("Smartgun", "HIT", 2));

        Assert.Equal([new EffectTerm("Sandevistan", 3)], EffectRules.CollectTerms([], c, Init, null));
        Assert.Equal([new EffectTerm("Smartgun", 2)], EffectRules.CollectTerms([], c, Hit, null));
    }

    [Fact]
    public void CyberwareBonus_CanUseStatAndSkillKeysToo()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(Cyber("Riflessi cablati", "STAT:REF", 1));
        c.Cyberware.Add(Cyber("Chip pistola", "SKILL:Pistola", 2));

        var terms = EffectRules.CollectTerms([], c, Hit, null);

        Assert.Equal(3, terms.Sum(t => t.Value));
    }

    [Fact]
    public void CyberwareBonus_WithUnknownKeyOrZeroValue_IsIgnored()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(Cyber("Placca", "ARMOR", 5));
        c.Cyberware.Add(Cyber("Niente", "NONE", 5));
        c.Cyberware.Add(Cyber("Zero", "INIT", 0));

        Assert.Empty(EffectRules.CollectTerms([], c, Init, null));
    }

    [Fact]
    public void GameEffects_FollowTheCyberware_InOrder()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(Cyber("Sandevistan", "INIT", 3));
        var effects = new List<ActiveEffect> { Master("morgan", "REF", 2), Master("molly", "REF", 9) };

        var terms = EffectRules.CollectTerms(effects, c, Init, null);

        Assert.Equal([new EffectTerm("Sandevistan", 3), new EffectTerm("master", 2)], terms);
    }

    [Fact]
    public void Damage_IsNotAffectedByAllOrStatKeys()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(Cyber("Tutto", "ALL", 1));
        var effects = new List<ActiveEffect> { Master("morgan", "STAT:REF", 2) };

        Assert.Empty(EffectRules.CollectTerms(effects, c, new EffectContext(RollKind.Damage), null));
    }
}
