using RPG.Common.Models;
using RPG.Common.World;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class MovementServiceTests
{
    private static (Game Game, Character Hero) Setup(int ma = 5)
    {
        var hero = new Character { Handle = "Hero", Stats = new Stats { MA = ma }, Position = new Position3D { X = 0, Y = 0, Z = 0 } };
        var game = new Game();
        game.Players.Add(new Player { Username = "mike", Character = hero });
        return (game, hero);
    }

    private static Position3D At(int x, int y, int z = 0) => new() { X = x, Y = y, Z = z };

    [Fact]
    public void TryMove_OutOfCombat_MovesAndCountsFreeAction()
    {
        var (game, hero) = Setup();
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(3, 0)), dirty.Add);

        Assert.Null(res.Error);
        Assert.Equal(GameMap.MovementResult.Success, res.Result);
        Assert.Equal(3, hero.Position.X);
        Assert.Equal(1, game.FreeActionsCounter);
        Assert.Equal([hero], dirty);
    }

    [Fact]
    public void TryMove_TooFar_ChangesNothing()
    {
        var (game, hero) = Setup(ma: 5);
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(20, 0)), dirty.Add);

        Assert.NotNull(res.Error);
        Assert.Equal(0, hero.Position.X);
        Assert.Equal(0, game.FreeActionsCounter);
        Assert.Empty(dirty);
    }

    [Fact]
    public void TryMove_Obstacle_IsBlockedAndNotSaved()
    {
        var (game, hero) = Setup();
        hero.Position = At(4, 5);
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(5, 5)), dirty.Add);

        Assert.Null(res.Error);
        Assert.Equal(GameMap.MovementResult.ObstacleInTheWay, res.Result);
        Assert.Equal(4, hero.Position.X);
        Assert.Empty(dirty);
    }

    [Fact]
    public void TryMove_InCombat_UsesTheRemainingBudget()
    {
        var (game, hero) = Setup(ma: 5);
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero", Initiative = 10 });
        game.TurnIndex = 0;

        var first = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(3, 0)));
        var second = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(6, 0)));

        Assert.Null(first.Error);
        Assert.NotNull(second.Error);          // restano 2m, la destinazione dista 3m
        Assert.Equal(3, hero.Position.X);
        Assert.Equal(3.0, game.TurnMoveUsed, 3);
        Assert.Equal(0, game.FreeActionsCounter);
    }

    [Fact]
    public void TryMove_InCombat_WrongTurn_IsRefused()
    {
        var (game, hero) = Setup();
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Molly", Initiative = 12 });
        game.TurnIndex = 0;

        var res = GameExecutionContext.Execute(game, s => MovementService.TryMove(s, game.Map, hero, At(1, 0)));

        Assert.NotNull(res.Error);
        Assert.Equal(0, hero.Position.X);
    }

    [Fact]
    public void MaText_ShowsEffectsOnlyWhenTheyChangeIt()
    {
        Assert.Equal("MA 7", MovementService.MaText(7, 7));
        Assert.Equal("MA 7, con effetti 10", MovementService.MaText(7, 10));
    }

    [Fact]
    public void TryTeleport_MovesEveryoneAndMarksThemDirty()
    {
        var (game, hero) = Setup();
        var npc = new Character { Handle = "Molly" };
        game.NPCs.Add(npc);
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryTeleport(s, ["hero", "MOLLY"], At(10, 10, 1)), dirty.Add);

        Assert.Null(res.Error);
        Assert.Equal(2, res.Moved.Count);
        Assert.Equal(10, hero.Position.X);
        Assert.Equal(1, npc.Position.Z);
        Assert.Equal(2, dirty.Count);
    }

    [Fact]
    public void TryTeleport_UnknownTarget_MovesNobody()
    {
        var (game, hero) = Setup();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryTeleport(s, ["Hero", "Ghost"], At(10, 10)));

        Assert.Contains("Ghost", res.Error);
        Assert.Empty(res.Moved);
        Assert.Equal(0, hero.Position.X);
    }

    [Theory]
    [InlineData(500, 0, 0)]    // fuori mappa
    [InlineData(5, 5, 0)]      // ostacolo
    public void TryTeleport_InvalidDestination_MovesNobody(int x, int y, int z)
    {
        var (game, hero) = Setup();

        var res = GameExecutionContext.Execute(game, s => MovementService.TryTeleport(s, ["Hero"], At(x, y, z)));

        Assert.NotNull(res.Error);
        Assert.Equal(0, hero.Position.X);
    }
}
