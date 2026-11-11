using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class GameExecutionContextTests
{
    [Fact]
    public void Execute_LocksSyncRootDuringOperation()
    {
        var game = new Game();
        bool wasLocked = false;

        GameExecutionContext.Execute(game, scope =>
        {
            wasLocked = Monitor.IsEntered(game.SyncRoot);
        });

        Assert.True(wasLocked);
        Assert.False(Monitor.IsEntered(game.SyncRoot));
    }

    [Fact]
    public void Execute_SavesDirtyCharacters_OutsideLock()
    {
        var game = new Game();
        var hero = new Character { Handle = "Hero" };
        var saved = new List<(Character Char, bool HeldLock)>();

        var ctx = new GameExecutionContext(game, c =>
        {
            saved.Add((c, Monitor.IsEntered(game.SyncRoot)));
        });

        ctx.Execute(scope =>
        {
            scope.MarkDirty(hero);
        });

        Assert.Single(saved);
        Assert.Same(hero, saved[0].Char);
        // Fondamentale: al momento del salvataggio, Game.SyncRoot DEVE essere già stato rilasciato
        Assert.False(saved[0].HeldLock);
    }

    [Fact]
    public void Execute_FailingSave_DoesNotBlockOthersNorThrow()
    {
        var game = new Game();
        var a = new Character { Handle = "A" };
        var b = new Character { Handle = "B" };
        var saved = new List<string>();

        var result = GameExecutionContext.Execute(game, scope =>
        {
            scope.MarkDirty(a);
            scope.MarkDirty(b);
            return 42;
        }, c =>
        {
            if (c.Handle == "A") throw new IOException("disco pieno");
            saved.Add(c.Handle);
        });

        Assert.Equal(42, result);
        Assert.Equal(["B"], saved);
    }

    [Fact]
    public void Execute_DeduplicatesDirtyCharacters()
    {
        var game = new Game();
        var hero = new Character { Handle = "Hero" };
        int saveCount = 0;

        GameExecutionContext.Execute(game, scope =>
        {
            scope.MarkDirty(hero);
            scope.MarkDirty(hero);
            scope.MarkDirty(new[] { hero, hero });
        }, _ => saveCount++);

        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void Execute_PropagatesException_WithoutSaving()
    {
        var game = new Game();
        var hero = new Character { Handle = "Hero" };
        bool saveCalled = false;

        Assert.Throws<InvalidOperationException>(() =>
        {
            GameExecutionContext.Execute(game, scope =>
            {
                scope.MarkDirty(hero);
                throw new InvalidOperationException("Boom");
            }, _ => saveCalled = true);
        });

        Assert.False(saveCalled);
        Assert.False(Monitor.IsEntered(game.SyncRoot));
    }

    [Fact]
    public void Scope_IsInGame_DetectsPlayersAndNpcs()
    {
        var game = new Game();
        var pChar = new Character { Handle = "Player1" };
        var npcChar = new Character { Handle = "Goon1" };
        var outsider = new Character { Handle = "Outsider" };

        game.Players.Add(new Player { Character = pChar });
        game.NPCs.Add(npcChar);

        var scope = new GameExecutionScope(game);

        Assert.True(scope.IsInGame(pChar));
        Assert.True(scope.IsInGame(npcChar));
        Assert.False(scope.IsInGame(outsider));
        Assert.False(scope.IsInGame(null));
    }

    [Fact]
    public void Scope_GetParticipants_ReturnsDistinctCharacters()
    {
        var game = new Game();
        var c1 = new Character { Handle = "C1" };
        var c2 = new Character { Handle = "C2" };

        game.Players.Add(new Player { Character = c1 });
        game.Players.Add(new Player { Character = c1 }); // duplicato intenzionale per verifica
        game.NPCs.Add(c2);

        var scope = new GameExecutionScope(game);
        var participants = scope.GetParticipants();

        Assert.Equal(2, participants.Count);
        Assert.Contains(c1, participants);
        Assert.Contains(c2, participants);
    }

    [Fact]
    public void Scope_CheckActor_ValidatesCorrectly()
    {
        var game = new Game();
        var alive = new Character { Handle = "Alive" };
        var dead = new Character { Handle = "Dead" };
        dead.Health.TakeDamage(dead.Health.MaxHealth + 1);
        var outsider = new Character { Handle = "Outsider" };

        game.NPCs.Add(alive);
        game.NPCs.Add(dead);

        var scope = new GameExecutionScope(game);

        Assert.Null(scope.CheckActor(alive));
        Assert.Contains("morto", scope.CheckActor(dead));
        Assert.Contains("non è più in questo game", scope.CheckActor(outsider));
    }

    [Fact]
    public void Scope_CheckTurn_OutOfCombat_AlwaysAllowed()
    {
        var game = new Game { CombatActive = false };
        var actor = new Character { Handle = "Actor" };
        var scope = new GameExecutionScope(game);

        var result = scope.CheckTurn(actor, needsAction: true);

        Assert.Null(result);
        Assert.False(game.TurnActionUsed);
    }

    [Fact]
    public void Scope_CheckTurn_InCombat_ChecksTurnAndAction()
    {
        var game = new Game
        {
            CombatActive = true,
            TurnOrder = [new Game.TurnEntry { Handle = "TurnActor", Initiative = 10 }, new Game.TurnEntry { Handle = "OtherActor", Initiative = 8 }],
            TurnIndex = 0
        };

        var turnActor = new Character { Handle = "TurnActor" };
        var otherActor = new Character { Handle = "OtherActor" };
        var scope = new GameExecutionScope(game);

        // Non tocca a OtherActor
        var wrongTurn = scope.CheckTurn(otherActor, needsAction: false);
        Assert.Contains("Non è il turno di OtherActor", wrongTurn);

        // Tocca a TurnActor senza azione
        var okMove = scope.CheckTurn(turnActor, needsAction: false);
        Assert.Null(okMove);
        Assert.False(game.TurnActionUsed);

        // Tocca a TurnActor con azione: consuma l'azione
        var okAction = scope.CheckTurn(turnActor, needsAction: true);
        Assert.Null(okAction);
        Assert.True(game.TurnActionUsed);

        // Azione già usata
        var alreadyUsed = scope.CheckTurn(turnActor, needsAction: true);
        Assert.Contains("ha già usato l'azione", alreadyUsed);

        // Rimborso azione
        scope.RefundAction();
        Assert.False(game.TurnActionUsed);

        // Attore stordito
        turnActor.Health.IsStunned = true;
        var stunned = scope.CheckTurn(turnActor, needsAction: false);
        Assert.Contains("stordito", stunned);
    }

    [Fact]
    public void Scope_CountFreeAction_IncrementsOnlyOutOfCombat()
    {
        var game = new Game { CombatActive = false, FreeActionsCounter = 0 };
        var scope = new GameExecutionScope(game);

        scope.CountFreeAction();
        Assert.Equal(1, game.FreeActionsCounter);

        game.CombatActive = true;
        scope.CountFreeAction();
        Assert.Equal(1, game.FreeActionsCounter);
    }

    [Fact]
    public void Scope_MarkNewlyDead_AddsToDeadAndDirty()
    {
        var game = new Game();
        var victim = new Character { Handle = "Victim" };
        var scope = new GameExecutionScope(game);

        scope.MarkNewlyDead(victim);

        Assert.Contains(victim, scope.NewlyDead);
        Assert.Contains(victim, scope.DirtyCharacters);
    }
}
