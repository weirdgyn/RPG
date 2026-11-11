using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class RollServiceTests
{
    private static Character Pg() => new()
    {
        Handle = "Hero",
        Role = Character.RoleType.Solo,
        Stats = new Stats { REF = 7, BODY = 6 },
        Skills = new Dictionary<string, int> { ["Pistola"] = 4, ["CombatSense"] = 3 }
    };

    private static ActiveEffect Fx(string key, int value, string duration = "manual")
    {
        Assert.True(EffectKey.TryParse(key, EffectService.SkillExists, out var k, out _));
        Assert.True(EffectDuration.TryParse(duration, out var d, out _));
        return ActiveEffect.Create("Hero", "master", "master", k, value, d);
    }

    [Fact]
    public void RollSkill_UnknownSkill_ReturnsNull()
    {
        Assert.Null(RollService.RollSkill(null, Pg(), "NonEsiste"));
    }

    [Fact]
    public void RollSkill_TotalIsTheSumOfEveryTerm()
    {
        var hero = Pg();
        var game = new Game();
        game.Effects.Add(Fx("SKILL:Pistola", 2));

        var r = RollService.RollSkill(game, hero, "Pistola")!;

        Assert.Equal(hero.Stats.GetStatValue(r.StatName), r.StatValue);
        Assert.Equal(4, r.SkillLevel);
        Assert.Equal(r.StatValue + r.SkillLevel + r.Penalty + 2 + r.Dice.Total, r.Total);
        Assert.Single(r.Terms);
    }

    [Fact]
    public void RollSkill_WithoutGame_UsesNoGameEffects()
    {
        var r = RollService.RollSkill(null, Pg(), "Pistola")!;

        Assert.Empty(r.Terms);
        Assert.Empty(r.Expired);
    }

    [Fact]
    public void RollSkill_ConsumesSingleUseEffects()
    {
        var game = new Game();
        game.Effects.Add(Fx("SKILL:Pistola", 3, "u1"));

        var first = RollService.RollSkill(game, Pg(), "Pistola")!;
        var second = RollService.RollSkill(game, Pg(), "Pistola")!;

        Assert.Single(first.Terms);
        Assert.Single(first.Expired);
        Assert.Empty(second.Terms);
        Assert.Empty(game.Effects);
    }

    [Fact]
    public void RollInitiative_SoloAddsCombatSense_AndTheTotalAddsUp()
    {
        var game = new Game();
        game.Effects.Add(Fx("INIT", 2));

        var r = RollService.RollInitiative(game, Pg());

        Assert.Equal(7, r.Ref);
        Assert.Equal(3, r.CombatSense);
        Assert.Equal(2, r.Effects);
        Assert.Equal(7 + 3 + 2 + r.Dice, r.Total);
    }

    [Fact]
    public void RollInitiative_NonSolo_HasNoCombatSense()
    {
        var hero = Pg();
        hero.Role = Character.RoleType.NetRunner;

        var r = RollService.RollInitiative(new Game(), hero);

        Assert.Equal(0, r.CombatSense);
    }

    [Fact]
    public void DescribeInitiative_ListsEveryPartAndTheTotal()
    {
        var game = new Game();
        game.Effects.Add(Fx("INIT", 2));
        var r = RollService.RollInitiative(game, Pg());

        string text = RollService.DescribeInitiative(r);

        Assert.StartsWith("REF 7 + CombatSense 3 + master 2 + d10 ", text);
        Assert.EndsWith($"= {r.Total}", text);
    }

    [Fact]
    public void RollStunSave_NotApplicable_ToDeadOrAlreadyStunned()
    {
        var dead = Pg();
        dead.Health.Kill();
        var stunned = Pg();
        stunned.Health.IsStunned = true;

        Assert.Null(RollService.RollStunSave(dead));
        Assert.Null(RollService.RollStunSave(stunned));
    }

    [Fact]
    public void RollStunSave_ReportsItsOutcomeConsistentlyWithTheState()
    {
        var hero = Pg();

        string? line = RollService.RollStunSave(hero);

        Assert.NotNull(line);
        Assert.Equal(line!.Contains("STORDITO"), hero.Health.IsStunned);
    }

    [Fact]
    public void RollDeathSave_ReportsItsOutcomeConsistentlyWithTheState()
    {
        var hero = Pg();
        hero.Health.TakeDamage(20);

        string line = RollService.RollDeathSave(hero);

        Assert.Equal(line.EndsWith("MUORE"), hero.Health.IsDead);
    }

    [Fact]
    public void LogTerms_FormatsSignedValuesWithLabels()
    {
        string text = RollService.LogTerms([new EffectTerm("master", 2), new EffectTerm("stordito", -1)]);

        Assert.Equal(" +2 master -1 stordito", text);
    }
}
