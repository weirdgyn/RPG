using RPG.Common;
using RPG.Common.Effects;
using Xunit;

namespace RPG.Tests;

public class BonusRulesTests
{
    private static bool Skill(string n) => n is "Pistola" or "Pronto Soccorso";

    private static BonusRequest Ok(params string[] args)
    {
        Assert.True(BonusRules.TryParse(args, Skill, out var r, out var error), error);
        return r!;
    }

    private static string Fail(params string[] args)
    {
        Assert.False(BonusRules.TryParse(args, Skill, out var r, out var error));
        Assert.Null(r);
        return error;
    }

    [Fact]
    public void Apply_WithDurationAndSeveralTargets()
    {
        var r = Ok("REF", "+2", "r3", "morgan", "molly");

        Assert.Equal(BonusAction.Apply, r.Action);
        Assert.Equal(new EffectKey(EffectScope.Stat, "REF"), r.Key);
        Assert.Equal(2, r.Value);
        Assert.Equal(new EffectDuration(DurationKind.Rounds, 3), r.Duration);
        Assert.Equal(["morgan", "molly"], r.Targets);
    }

    [Fact]
    public void Apply_WithoutDurationIsManual_AndShortcutsAreTargets()
    {
        var r = Ok("INIT", "-2", "party");

        Assert.Equal(new EffectKey(EffectScope.Init), r.Key);
        Assert.Equal(-2, r.Value);
        Assert.Equal(EffectDuration.Manual, r.Duration);
        Assert.Equal(["party"], r.Targets);
    }

    [Fact]
    public void Apply_CombatDuration()
    {
        var r = Ok("INIT", "-2", "combat", "party");
        Assert.Equal(new EffectDuration(DurationKind.Combat), r.Duration);
        Assert.Equal(["party"], r.Targets);
    }

    [Fact]
    public void LastTokenIsAlwaysATarget_EvenIfItLooksLikeADuration()
    {
        var r = Ok("ALL", "+1", "r3");
        Assert.Equal(EffectDuration.Manual, r.Duration);
        Assert.Equal(["r3"], r.Targets);
    }

    [Fact]
    public void SkillNameWithSpaces()
    {
        var r = Ok("SKILL:Pronto", "Soccorso", "+3", "u1", "morgan");
        Assert.Equal(new EffectKey(EffectScope.Skill, "Pronto Soccorso"), r.Key);
        Assert.Equal(3, r.Value);
        Assert.Equal(new EffectDuration(DurationKind.Uses, 1), r.Duration);
        Assert.Equal(["morgan"], r.Targets);

        Assert.Equal(new EffectKey(EffectScope.Skill, "Pronto Soccorso"), Ok("Pronto", "Soccorso", "+3", "morgan").Key);
    }

    [Fact]
    public void TargetsCanBeCommaSeparated_AndAreDeduplicated()
    {
        Assert.Equal(["morgan", "molly"], Ok("MOVE", "+1", "morgan,", "molly", "MORGAN").Targets);
    }

    [Theory]
    [InlineData("REF|0|morgan")]
    [InlineData("REF|+11|morgan")]
    [InlineData("REF|-11|morgan")]
    [InlineData("REF|due|morgan")]
    [InlineData("REF|+2")]
    [InlineData("REF")]
    [InlineData("NONSENSE|+2|morgan")]
    public void Apply_InvalidInput(string joinedArgs) => Assert.NotEmpty(Fail(joinedArgs.Split('|')));

    [Fact]
    public void EmptyArgumentsGiveUsage() => Assert.Equal(BonusRules.Usage, Fail());

    [Fact]
    public void List_WithAndWithoutTargets()
    {
        var all = Ok("list");
        Assert.Equal(BonusAction.List, all.Action);
        Assert.Empty(all.Targets);

        Assert.Equal(["morgan"], Ok("LIST", "morgan").Targets);
    }

    [Fact]
    public void Clear_NeedsTargets()
    {
        Assert.Equal(["all"], Ok("clear", "all").Targets);
        Assert.Equal(BonusAction.Clear, Ok("clear", "morgan").Action);
        Assert.Contains("clear", Fail("clear"));
    }

    [Fact]
    public void TooManyTargetsAreRejected()
    {
        var args = new List<string> { "REF", "+1" };
        args.AddRange(Enumerable.Range(0, TeleportRules.MaxTargets + 1).Select(i => "pg" + i));
        Assert.Contains("Troppi", Fail([.. args]));
    }

    [Fact]
    public void Shortcuts()
    {
        Assert.True(BonusRules.IsShortcut("PARTY"));
        Assert.True(BonusRules.IsShortcut("npc"));
        Assert.True(BonusRules.IsShortcut("all"));
        Assert.False(BonusRules.IsShortcut("morgan"));
    }
}
