using RPG.Common.Effects;
using Xunit;

namespace RPG.Tests;

public class EffectKeyTests
{
    private static bool KnownSkill(string name) => name.Equals("Pistola", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("ALL", "ALL")]
    [InlineData("init", "INIT")]
    [InlineData("Hit", "HIT")]
    [InlineData("damage", "DAMAGE")]
    [InlineData("MOVE", "MOVE")]
    [InlineData("stat:ref", "STAT:REF")]
    [InlineData("REF", "STAT:REF")]
    [InlineData("skill:pistola", "SKILL:pistola")]
    [InlineData("Pistola", "SKILL:Pistola")]
    public void TryParse_AcceptsValidKeys(string text, string canonical)
    {
        Assert.True(EffectKey.TryParse(text, KnownSkill, out var key, out _));
        Assert.Equal(canonical, key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("STAT:LUCK")]
    [InlineData("SKILL:Volare")]
    [InlineData("SKILL:")]
    [InlineData("Volare")]
    [InlineData("ARMOR")]
    public void TryParse_RejectsInvalidKeys(string text)
    {
        Assert.False(EffectKey.TryParse(text, KnownSkill, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_WithoutSkillValidator_AcceptsAnySkillWithPrefix()
    {
        Assert.True(EffectKey.TryParse("SKILL:Qualsiasi", null, out var key, out _));
        Assert.Equal(EffectScope.Skill, key.Scope);
    }

    [Fact]
    public void CanonicalForm_RoundTrips()
    {
        Assert.True(EffectKey.TryParse("STAT:BODY", KnownSkill, out var key, out _));
        Assert.True(EffectKey.TryParse(key.ToString(), KnownSkill, out var again, out _));
        Assert.Equal(key, again);
    }

    [Fact]
    public void All_AppliesToChecksButNotToDamageOrMove()
    {
        var all = new EffectKey(EffectScope.All);

        Assert.True(all.Matches(new EffectContext(RollKind.Skill)));
        Assert.True(all.Matches(new EffectContext(RollKind.Hit)));
        Assert.True(all.Matches(new EffectContext(RollKind.Init)));
        Assert.False(all.Matches(new EffectContext(RollKind.Damage)));
        Assert.False(all.Matches(new EffectContext(RollKind.Move)));
    }

    [Fact]
    public void KindScopes_MatchOnlyTheirRoll()
    {
        Assert.True(new EffectKey(EffectScope.Init).Matches(new EffectContext(RollKind.Init)));
        Assert.False(new EffectKey(EffectScope.Init).Matches(new EffectContext(RollKind.Hit)));
        Assert.True(new EffectKey(EffectScope.Hit).Matches(new EffectContext(RollKind.Hit)));
        Assert.True(new EffectKey(EffectScope.Damage).Matches(new EffectContext(RollKind.Damage)));
        Assert.True(new EffectKey(EffectScope.Move).Matches(new EffectContext(RollKind.Move)));
        Assert.False(new EffectKey(EffectScope.Move).Matches(new EffectContext(RollKind.Skill)));
    }

    [Fact]
    public void Stat_MatchesChecksThatUseThatStat()
    {
        var refKey = new EffectKey(EffectScope.Stat, "REF");

        Assert.True(refKey.Matches(new EffectContext(RollKind.Skill, Stat: "ref")));
        Assert.True(refKey.Matches(new EffectContext(RollKind.Init, Stat: "REF")));
        Assert.False(refKey.Matches(new EffectContext(RollKind.Skill, Stat: "INT")));
        Assert.False(refKey.Matches(new EffectContext(RollKind.Damage, Stat: "REF")));
    }

    [Fact]
    public void Skill_MatchesThatSkillInSkillAndHitRolls()
    {
        var key = new EffectKey(EffectScope.Skill, "Pistola");

        Assert.True(key.Matches(new EffectContext(RollKind.Skill, Skill: "pistola")));
        Assert.True(key.Matches(new EffectContext(RollKind.Hit, Skill: "Pistola")));
        Assert.False(key.Matches(new EffectContext(RollKind.Skill, Skill: "Fucile")));
        Assert.False(key.Matches(new EffectContext(RollKind.Init, Skill: "Pistola")));
    }
}
