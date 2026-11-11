using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class EffectServiceTests
{
    private static (Game Game, Character Hero, Character Npc) Setup()
    {
        var hero = new Character { Handle = "Hero" };
        var npc = new Character { Handle = "Molly" };
        var game = new Game();
        game.Players.Add(new Player { Username = "mike", Character = hero });
        game.NPCs.Add(npc);
        return (game, hero, npc);
    }

    private static EffectKey Key(string text)
    {
        Assert.True(EffectKey.TryParse(text, EffectService.SkillExists, out var key, out _));
        return key;
    }

    private static ActiveEffect Fx(string handle, string key, int value, string source = "master", EffectDuration? duration = null) =>
        ActiveEffect.Create(handle, source, source, Key(key), value, duration ?? EffectDuration.Manual);

    private static BonusRequest Request(BonusAction action, string key, int value, params string[] targets) =>
        new(action, Key(key), value, EffectDuration.Manual, [.. targets]);

    private static BonusRequest ClearRequest(params string[] targets) =>
        new(BonusAction.Clear, Key("ALL"), 0, EffectDuration.Manual, [.. targets]);

    private static BonusRequest ListRequest(params string[] targets) =>
        new(BonusAction.List, Key("ALL"), 0, EffectDuration.Manual, [.. targets]);

    [Fact]
    public void TryAddAll_AddsEverything()
    {
        var (game, _, _) = Setup();

        var res = EffectService.TryAddAll(game, [Fx("Hero", "MOVE", 2), Fx("Molly", "INIT", 3)]);

        Assert.Null(res.Error);
        Assert.Equal(2, game.Effects.Count);
    }

    [Fact]
    public void TryAddAll_IsAllOrNothing()
    {
        var (game, _, _) = Setup();
        game.Effects.Add(Fx("Hero", "MOVE", 1, "vecchio"));

        var res = EffectService.TryAddAll(game, [Fx("Hero", "INIT", 2), Fx("Hero", "HIT", 99)]);

        Assert.NotNull(res.Error);
        Assert.Equal("Hero", res.Failed!.TargetHandle);
        Assert.Single(game.Effects);
        Assert.Equal("vecchio", game.Effects[0].Source);
    }

    [Fact]
    public void ApplyBonus_Apply_AnnouncesAndStoresTheEffects()
    {
        var (game, _, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, Request(BonusAction.Apply, "MOVE", 3, "party", "molly")));

        Assert.Null(res.Error);
        Assert.Contains("Hero", res.Summary);
        Assert.Contains("Molly", res.Summary);
        Assert.Equal(2, game.Effects.Count);
    }

    [Fact]
    public void ApplyBonus_UnknownTarget_ChangesNothing()
    {
        var (game, _, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, Request(BonusAction.Apply, "MOVE", 3, "Hero", "Ghost")));

        Assert.Contains("Ghost", res.Error);
        Assert.Empty(game.Effects);
    }

    [Fact]
    public void ApplyBonus_InvalidValue_ChangesNothing()
    {
        var (game, _, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, Request(BonusAction.Apply, "MOVE", 99, "Hero")));

        Assert.NotNull(res.Error);
        Assert.Contains("Nessun effetto modificato", res.Error);
        Assert.Empty(game.Effects);
    }

    [Fact]
    public void ApplyBonus_Clear_RemovesOnlyMasterBonuses()
    {
        var (game, _, _) = Setup();
        game.Effects.Add(Fx("Hero", "MOVE", 2));
        game.Effects.Add(Fx("Hero", "INIT", 3, "Sandevistan"));

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, ClearRequest("Hero")));

        Assert.Null(res.Error);
        Assert.Contains("toglie 1", res.Summary);
        Assert.Equal("Sandevistan", Assert.Single(game.Effects).Source);
    }

    [Fact]
    public void ApplyBonus_ClearWithNothingToRemove_RepliesPrivately()
    {
        var (game, _, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, ClearRequest("Hero")));

        Assert.Equal(string.Empty, res.Summary);
        Assert.Contains("Nessun bonus del master", res.Reply);
    }

    [Fact]
    public void ApplyBonus_List_DefaultsToEveryone()
    {
        var (game, _, _) = Setup();
        game.Effects.Add(Fx("Molly", "MOVE", 3));

        var res = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, ListRequest()));
        var empty = GameExecutionContext.Execute(game, s => EffectService.ApplyBonus(s, ListRequest("Hero")));

        Assert.StartsWith("Effetti attivi - Molly:", res.Reply);
        Assert.Equal("Nessun effetto attivo.", empty.Reply);
    }

    [Fact]
    public void EndCombatEffects_RemovesCombatAndRoundEffects_AndAnnouncesThem()
    {
        var (game, _, _) = Setup();
        Assert.True(EffectDuration.TryParse("r2", out var rounds, out _));
        game.Effects.Add(Fx("Hero", "INIT", 3, "Sandevistan", rounds));
        game.Effects.Add(Fx("Hero", "MOVE", 2));

        string text = EffectService.EndCombatEffects(game);

        Assert.Contains("Effetto scaduto", text);
        Assert.Single(game.Effects);
        Assert.Equal(string.Empty, EffectService.EndCombatEffects(game));
    }

    [Fact]
    public void DescribeEffectsOf_ListsOnlyTheCharactersEffects()
    {
        var (game, _, _) = Setup();
        game.Effects.Add(Fx("Hero", "MOVE", 2));
        game.Effects.Add(Fx("Molly", "INIT", 3));

        Assert.Single(EffectService.DescribeEffectsOf(game, "hero"));
        Assert.Empty(EffectService.DescribeEffectsOf(game, "nessuno"));
    }

    [Fact]
    public void Scope_FindCharacter_FindsPlayersAndNpcsIgnoringCase()
    {
        var (game, hero, npc) = Setup();
        var scope = new GameExecutionScope(game);

        Assert.Same(hero, scope.FindCharacter("HERO"));
        Assert.Same(npc, scope.FindCharacter("molly"));
        Assert.Null(scope.FindCharacter("ghost"));
    }
}
