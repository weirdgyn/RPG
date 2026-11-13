using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class TurnServiceTests
{
    // Tre NPC A, B, C con ordine di iniziativa A > B > C; il combattimento non è ancora avviato
    private static (Game Game, Character A, Character B, Character C) Setup()
    {
        var a = new Character { Handle = "A" };
        var b = new Character { Handle = "B" };
        var c = new Character { Handle = "C" };
        var game = new Game();
        game.NPCs.AddRange([a, b, c]);
        game.TurnOrder.AddRange([
            new Game.TurnEntry { Handle = "A", Initiative = 15 },
            new Game.TurnEntry { Handle = "B", Initiative = 12 },
            new Game.TurnEntry { Handle = "C", Initiative = 9 }]);
        return (game, a, b, c);
    }

    private static TurnService.TurnOutcome Start(Game game, List<Character>? saved = null) =>
        GameExecutionContext.Execute(game, TurnService.StartCombat, (saved ?? []).Add);

    private static TurnService.TurnOutcome Next(Game game, bool master, Character? caller) =>
        GameExecutionContext.Execute(game, s => TurnService.NextTurn(s, master, caller));

    // ---- avvio ----

    [Fact]
    public void StartCombat_GivesTheFirstTurnToTheHighestInitiative()
    {
        var (game, a, _, _) = Setup();
        a.Health.IsStunned = true;
        game.FreeActionsCounter = 4;

        // lo stordimento si azzera all'avvio, quindi A può agire
        var res = Start(game);

        Assert.Null(res.Error);
        Assert.True(game.CombatActive);
        Assert.Equal(1, game.Round);
        Assert.Equal("A", game.CurrentTurnHandle);
        Assert.False(a.Health.IsStunned);
        Assert.Equal(0, game.FreeActionsCounter);
        Assert.Contains("COMBATTIMENTO INIZIATO! Ordine: A > B > C.", res.Announce);
        Assert.Contains("tocca a A", res.Announce);
    }

    [Fact]
    public void StartCombat_AlreadyActive_IsRefused()
    {
        var (game, _, _, _) = Setup();
        Start(game);

        var res = Start(game);

        Assert.Contains("già in corso", res.Error);
    }

    [Fact]
    public void StartCombat_WithoutInitiativeOrder_IsRefused()
    {
        var game = new Game();

        var res = Start(game);

        Assert.Contains("Nessun ordine di iniziativa", res.Error);
        Assert.False(game.CombatActive);
    }

    [Fact]
    public void StartCombat_WhileInitiativeIsOpen_IsRefused()
    {
        var (game, _, _, _) = Setup();
        game.ActiveCheck = new Game.Check { Kind = Game.CheckKind.Initiative };

        var res = Start(game);

        Assert.Contains("iniziativa è ancora aperta", res.Error);
    }

    [Fact]
    public void StartCombat_EveryoneDead_NoCombat()
    {
        var (game, a, b, c) = Setup();
        a.Health.Kill();
        b.Health.Kill();
        c.Health.Kill();

        var res = Start(game);

        Assert.Contains("Nessun personaggio in grado di agire", res.Error);
        Assert.False(game.CombatActive);
    }

    // ---- passaggio del turno ----

    [Fact]
    public void NextTurn_Master_PassesToTheNextAndResetsTheBudget()
    {
        var (game, _, _, _) = Setup();
        Start(game);
        game.TurnMoveUsed = 3;
        game.TurnActionUsed = true;

        var res = Next(game, master: true, caller: null);

        Assert.Null(res.Error);
        Assert.Equal("B", game.CurrentTurnHandle);
        Assert.Equal(0, game.TurnMoveUsed);
        Assert.False(game.TurnActionUsed);
        Assert.StartsWith("Fine turno di A.", res.Announce);
    }

    [Fact]
    public void NextTurn_PlayerNotInTurn_IsRefused()
    {
        var (game, _, b, _) = Setup();
        Start(game);

        var res = Next(game, master: false, caller: b);

        Assert.Contains("Non è il tuo turno: tocca a A", res.Error);
        Assert.Equal("A", game.CurrentTurnHandle);
    }

    [Fact]
    public void NextTurn_PlayerInTurn_CanPass()
    {
        var (game, a, _, _) = Setup();
        Start(game);

        var res = Next(game, master: false, caller: a);

        Assert.Null(res.Error);
        Assert.Equal("B", game.CurrentTurnHandle);
    }

    [Fact]
    public void NextTurn_WithoutCombat_IsRefused()
    {
        var (game, _, _, _) = Setup();

        var res = Next(game, master: true, caller: null);

        Assert.Contains("Nessun combattimento in corso", res.Error);
    }

    [Fact]
    public void NextTurn_FullLoop_AdvancesTheRound_AndSkipsTheDead()
    {
        var (game, _, b, _) = Setup();
        Start(game);
        b.Health.Kill();

        Next(game, true, null);                 // A -> C (B è morto)
        Assert.Equal("C", game.CurrentTurnHandle);
        Assert.Equal(1, game.Round);

        Next(game, true, null);                 // C -> A: nuovo round
        Assert.Equal("A", game.CurrentTurnHandle);
        Assert.Equal(2, game.Round);
    }

    [Fact]
    public void NextTurn_RoundChange_ExpiresRoundEffects()
    {
        var (game, _, _, _) = Setup();
        Assert.True(EffectKey.TryParse("INIT", EffectService.SkillExists, out var key, out _));
        Assert.True(EffectDuration.TryParse("r1", out var duration, out _));
        game.Effects.Add(ActiveEffect.Create("A", "master", "master", key, 2, duration));
        Start(game);

        Next(game, true, null);   // A -> B
        Next(game, true, null);   // B -> C
        var res = Next(game, true, null);   // C -> A: nuovo round

        Assert.Empty(game.Effects);
        Assert.Contains("Effetto scaduto", res.Announce);
    }

    [Fact]
    public void NextTurn_NobodyAbleToAct_EndsTheCombat()
    {
        var (game, a, b, c) = Setup();
        Start(game);
        a.Health.Kill();
        b.Health.Kill();
        c.Health.Kill();

        var res = Next(game, true, null);

        Assert.False(game.CombatActive);
        Assert.Contains("combattimento terminato", res.Announce);
    }

    // ---- fine del combattimento ----

    [Fact]
    public void EndCombat_ClearsStunAndBudget_AndReportsWhetherItWasActive()
    {
        var (game, a, _, _) = Setup();
        Start(game);
        a.Health.IsStunned = true;
        game.TurnActionUsed = true;
        game.TurnMoveUsed = 2;

        var first = GameExecutionContext.Execute(game, TurnService.EndCombat);
        var second = GameExecutionContext.Execute(game, TurnService.EndCombat);

        Assert.True(first.Was);
        Assert.False(second.Was);
        Assert.False(game.CombatActive);
        Assert.False(a.Health.IsStunned);
        Assert.False(game.TurnActionUsed);
        Assert.Equal(0, game.TurnMoveUsed);
    }

    // ---- uscita dall'ordine di iniziativa ----

    [Fact]
    public void RemoveFromTurnOrder_UnknownHandle_ChangesNothing()
    {
        var (game, _, _, _) = Setup();
        Start(game);

        string? notice = TurnService.RemoveFromTurnOrder(game, "Ghost", _ => { });

        Assert.Null(notice);
        Assert.Equal(3, game.TurnOrder.Count);
    }

    [Fact]
    public void RemoveFromTurnOrder_SomeoneBeforeTheCurrent_KeepsTheTurnOnTheSamePerson()
    {
        var (game, _, _, _) = Setup();
        Start(game);
        Next(game, true, null);                  // tocca a B

        string? notice = TurnService.RemoveFromTurnOrder(game, "A", _ => { });

        Assert.Null(notice);
        Assert.Equal("B", game.CurrentTurnHandle);
    }

    [Fact]
    public void RemoveFromTurnOrder_TheCurrent_PassesTheTurnToTheNext()
    {
        var (game, _, _, _) = Setup();
        Start(game);
        Next(game, true, null);                  // tocca a B

        string? notice = TurnService.RemoveFromTurnOrder(game, "B", _ => { });

        Assert.Equal("C", game.CurrentTurnHandle);
        Assert.Contains("B ha lasciato il combattimento durante il suo turno", notice);
        Assert.Contains("tocca a C", notice);
    }

    [Fact]
    public void RemoveFromTurnOrder_TheLastOne_EndsTheCombat()
    {
        var (game, _, _, _) = Setup();
        Start(game);

        TurnService.RemoveFromTurnOrder(game, "B", _ => { });
        TurnService.RemoveFromTurnOrder(game, "C", _ => { });
        string? notice = TurnService.RemoveFromTurnOrder(game, "A", _ => { });

        Assert.False(game.CombatActive);
        Assert.Contains("combattimento terminato", notice);
    }

    [Fact]
    public void RemoveFromTurnOrder_OutsideCombat_JustRemoves()
    {
        var (game, _, _, _) = Setup();

        string? notice = TurnService.RemoveFromTurnOrder(game, "B", _ => { });

        Assert.Null(notice);
        Assert.Equal(["A", "C"], game.TurnOrder.Select(t => t.Handle));
    }

    // ---- testi ----

    [Fact]
    public void CombatStatus_CoversIdleReadyAndActive()
    {
        var empty = new Game();
        var (game, _, _, _) = Setup();

        Assert.Equal("Nessun combattimento in corso.", TurnService.CombatStatus(empty));
        Assert.Contains("Ordine di iniziativa pronto: A (15), B (12), C (9)", TurnService.CombatStatus(game));

        Start(game);
        string active = TurnService.CombatStatus(game);
        Assert.Contains("Round 1 - tocca a A.", active);
        Assert.Contains("Ordine: [A] > B > C.", active);
    }

    [Fact]
    public void DescribeTurn_ShowsRemainingMovementAndAction()
    {
        var (game, a, _, _) = Setup();
        a.Stats.MA = 7;
        Start(game);
        game.TurnMoveUsed = 2.5;
        game.TurnActionUsed = true;

        string text = TurnService.DescribeTurn(game);

        // il separatore decimale dipende dalla cultura: si controllano le parti, non il numero formattato
        Assert.StartsWith("Round 1 - tocca a A. Movimento: ", text);
        Assert.Contains("/7m, azione: usata.", text);
    }
}
