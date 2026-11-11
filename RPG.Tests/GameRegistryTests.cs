using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class GameRegistryTests
{
    private static (GameRegistry Registry, Game Game, Player Player, Character Hero, Character Npc) Setup()
    {
        var registry = new GameRegistry();
        var hero = new Character { Handle = "Hero" };
        var npc = new Character { Handle = "Molly" };
        var player = new Player { Username = "mike", Character = hero };
        var game = new Game();
        game.Players.Add(player);
        game.NPCs.Add(npc);
        registry.Games.Add(game);
        return (registry, game, player, hero, npc);
    }

    [Fact]
    public void FindGameOf_FindsPlayerCharacterAndNpc()
    {
        var (registry, game, _, hero, npc) = Setup();

        Assert.Same(game, registry.FindGameOf(hero));
        Assert.Same(game, registry.FindGameOf(npc));
    }

    [Fact]
    public void FindGameOf_ReturnsNull_ForCharacterOutsideAnyGame()
    {
        var (registry, _, _, _, _) = Setup();

        Assert.Null(registry.FindGameOf(new Character { Handle = "Stranger" }));
    }

    [Fact]
    public void FindGameOfPlayer_FindsOnlyMembers()
    {
        var (registry, game, player, _, _) = Setup();

        Assert.Same(game, registry.FindGameOfPlayer(player));
        Assert.Null(registry.FindGameOfPlayer(new Player { Username = "other" }));
    }

    [Fact]
    public void SyncFor_UsesGameSyncRoot_OrSharedLockOutsideGames()
    {
        var (registry, game, _, hero, _) = Setup();
        var stranger = new Character { Handle = "Stranger" };

        Assert.Same(game.SyncRoot, registry.SyncFor(hero));
        Assert.Same(registry.NoGameSync, registry.SyncFor(stranger));
    }

    [Fact]
    public void FindGameOf_ReleasesGamesLock_BeforeReturning()
    {
        var (registry, _, _, hero, _) = Setup();

        registry.FindGameOf(hero);

        Assert.False(Monitor.IsEntered(registry.GamesLock));
    }
}
