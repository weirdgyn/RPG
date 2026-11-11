using RPG.Common.Models;
using RPG.Common.Utils;
using RPG.Common.World;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class MedicalServiceTests
{
    private static (Game Game, Character Healer, Character Wounded) Setup(int woundedDamage = 20)
    {
        var healer = new Character { Handle = "Doc", Position = new Position3D() };
        var wounded = new Character { Handle = "Hero", Position = new Position3D { X = 1 } };
        wounded.Health.TakeDamage(woundedDamage);
        var game = new Game();
        game.Players.Add(new Player { Username = "doc", Character = healer });
        game.Players.Add(new Player { Username = "mike", Character = wounded });
        return (game, healer, wounded);
    }

    // ---- cura del master ----

    [Fact]
    public void MasterHeal_Full_RestoresHealthAndMarksDirty()
    {
        var (game, _, wounded) = Setup();
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MedicalService.MasterHeal(s, "hero", null), dirty.Add);

        Assert.Null(res.Error);
        Assert.Contains("cura completamente", res.Message);
        Assert.Equal(0, wounded.Health.DamageTaken);
        Assert.Equal([wounded], dirty);
    }

    [Fact]
    public void MasterHeal_Partial_ReturnsOnlyThoseBoxes()
    {
        var (game, _, wounded) = Setup();

        var res = GameExecutionContext.Execute(game, s => MedicalService.MasterHeal(s, "Hero", 5));

        Assert.Null(res.Error);
        Assert.Equal(15, wounded.Health.DamageTaken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(MedicalService.MaxHealAmount + 1)]
    public void MasterHeal_InvalidAmount_IsRefused(int amount)
    {
        var (game, _, wounded) = Setup();
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MedicalService.MasterHeal(s, "Hero", amount), dirty.Add);

        Assert.NotNull(res.Error);
        Assert.Equal(20, wounded.Health.DamageTaken);
        Assert.Empty(dirty);
    }

    [Fact]
    public void MasterHeal_UnknownTarget_IsRefused()
    {
        var (game, _, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => MedicalService.MasterHeal(s, "Ghost", null));

        Assert.Contains("Ghost", res.Error);
    }

    // ---- stabilizzazione: i casi in cui il tentativo non avviene ----

    [Fact]
    public void Stabilize_UnknownTarget_IsRefused()
    {
        var (game, healer, _) = Setup();

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Ghost"));

        Assert.Contains("Ghost", res.Error);
    }

    [Fact]
    public void Stabilize_NotMortal_IsRefusedWithoutSpendingTheAction()
    {
        var (game, healer, _) = Setup(woundedDamage: 5);
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Doc" });

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Hero"));

        Assert.Contains("non è in Mortale", res.Error);
        Assert.False(game.TurnActionUsed);
    }

    [Fact]
    public void Stabilize_DeadTarget_OnlyTheMasterCanRevive()
    {
        var (game, healer, wounded) = Setup();
        wounded.Health.Kill();

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Hero"));

        Assert.Contains("morto", res.Error);
    }

    [Fact]
    public void Stabilize_AlreadyStable_IsRefused()
    {
        var (game, healer, wounded) = Setup();
        wounded.Health.IsStabilized = true;

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Hero"));

        Assert.Contains("già stabilizzato", res.Error);
    }

    [Fact]
    public void Stabilize_TooFar_IsRefused()
    {
        var (game, healer, wounded) = Setup();
        wounded.Position = new Position3D { X = 10 };

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Hero"));

        Assert.Contains("troppo lontano", res.Error);
        Assert.False(wounded.Health.IsStabilized);
    }

    [Fact]
    public void Stabilize_WrongTurn_IsRefused()
    {
        var (game, healer, _) = Setup();
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "Hero"));

        Assert.Contains("Non è il turno", res.Error);
    }

    [Fact]
    public void Stabilize_HealerNoLongerInGame_IsRefused()
    {
        var (game, _, _) = Setup();
        var outsider = new Character { Handle = "Stranger" };

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, outsider, "Hero"));

        Assert.Contains("non è più in questo game", res.Error);
    }

    [Fact]
    public void Stabilize_ValidAttempt_SpendsTheActionOutsideCombatCountingFreeAction()
    {
        var (game, healer, wounded) = Setup();
        var dirty = new List<Character>();

        var res = GameExecutionContext.Execute(game, s => MedicalService.Stabilize(s, healer, "hero"), dirty.Add);

        // L'esito dipende dal dado: qui conta che il tentativo sia avvenuto e coerente con lo stato.
        Assert.Null(res.Error);
        Assert.Same(wounded, res.Target);
        Assert.Equal(1, game.FreeActionsCounter);
        Assert.Equal(res.Success, wounded.Health.IsStabilized);
        Assert.Equal(res.Success, dirty.Contains(wounded));
        Assert.NotEmpty(res.Calc);
    }

    // ---- formattazione dei calcoli ----

    [Fact]
    public void FormatSum_ShowsSignsAndTotal()
    {
        string text = RollFormatter.FormatSum([("d10", 6, null), ("REF", 7, null), ("ferite", -2, null)]);

        Assert.Equal("d10 6 + REF 7 - ferite 2 = 11", text);
    }

    [Fact]
    public void DiceExtra_OnlyWhenTheDieExploded()
    {
        var plain = new GameDice.D10Roll(4, 4, 1, [4]);

        Assert.Equal(string.Empty, RollFormatter.DiceExtra(plain));
    }
}
