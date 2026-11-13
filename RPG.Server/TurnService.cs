using RPG.Common.Effects;
using RPG.Common.Models;

namespace RPG.Server;

/// <summary>
/// Combattimento a turni senza rete né I/O: avvio e fine, passaggio del turno, uscita dall'ordine di iniziativa.
/// Tutto va chiamato sotto <see cref="Game.SyncRoot"/> (direttamente o tramite <see cref="GameExecutionScope"/>).
/// I personaggi morti durante un passaggio di turno sono segnalati al chiamante, che li salva fuori dal lock.
/// </summary>
public static class TurnService
{
    /// <summary>Esito di un comando di turno: con Error valorizzato non è cambiato nulla, altrimenti Announce va a tutto il game.</summary>
    public sealed record TurnOutcome(string? Error, string Announce);

    /// <summary>Esito di <see cref="EndCombat"/>: Was dice se c'era un combattimento, EndedEffects è il testo sugli effetti scaduti.</summary>
    public sealed record EndOutcome(bool Was, string EndedEffects);

    // Da chiamare sotto Game.SyncRoot. Passa al prossimo personaggio valido dell'ordine (ancora nel game e non morto),
    // incrementando il round al giro completo, e azzera movimento e azione. False se non c'è più nessuno.
    // I personaggi morti durante la scansione sono segnalati a onDead (da salvare su disco fuori dal lock).
    public static bool MoveToNextTurn(Game game, List<string> notes, Action<Character> onDead)
    {
        int n = game.TurnOrder.Count;
        int fallbackRaw = -1;

        for (int step = 1; step <= n; step++)
        {
            int raw = game.TurnIndex + step;
            int idx = raw % n;
            var character = GameExecutionScope.FindParticipant(game, game.TurnOrder[idx].Handle);
            if (character == null || character.Health.IsDead) continue;

            // Salvezza contro la morte: a inizio turno, chi è in Mortale e non stabilizzato tira 1d10 ≤ BODY.
            if (character.Health.NeedsDeathSave)
            {
                notes.Add(RollService.RollDeathSave(character));
                if (character.Health.IsDead)
                {
                    onDead(character);
                    continue;   // morto: salta il turno
                }
            }

            // Chi è stordito tenta di riprendersi all'inizio del suo turno; se non ci riesce il turno salta
            if (character.Health.IsStunned)
            {
                notes.Add(RollService.RollStunRecovery(character));
                if (character.Health.IsStunned)
                {
                    if (fallbackRaw < 0) fallbackRaw = raw;
                    continue;
                }
            }

            if (raw / n > 0) notes.AddRange(EffectService.DescribeLapsed(EffectRules.AdvanceRound(game.Effects)));
            game.Round += raw / n;
            game.TurnIndex = idx;
            game.TurnMoveUsed = 0;
            game.TurnActionUsed = false;
            game.TurnStamp++;
            return true;
        }

        // Tutti gli altri sono storditi o morti: il turno va comunque al primo stordito (che può solo passare con /next)
        if (fallbackRaw >= 0)
        {
            notes.Add("Nessuno in grado di agire: il turno passa a un personaggio stordito (può solo passare con /next).");
            if (fallbackRaw / n > 0) notes.AddRange(EffectService.DescribeLapsed(EffectRules.AdvanceRound(game.Effects)));
            game.Round += fallbackRaw / n;
            game.TurnIndex = fallbackRaw % n;
            game.TurnMoveUsed = 0;
            game.TurnActionUsed = false;
            game.TurnStamp++;
            return true;
        }

        return false;
    }

    // Da chiamare sotto Game.SyncRoot. Toglie un personaggio dall'ordine di gioco tenendo coerente il turno corrente.
    // Restituisce l'annuncio da inviare al game se il turno corrente è cambiato (o il combattimento è finito), altrimenti null.
    public static string? RemoveFromTurnOrder(Game game, string handle, Action<Character> onDead)
    {
        int i = game.TurnOrder.FindIndex(t => t.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;

        game.TurnOrder.RemoveAt(i);
        if (!game.CombatActive) return null;

        if (game.TurnOrder.Count == 0)
        {
            game.CombatActive = false;
            return $"{handle} ha lasciato il combattimento: nessuno è rimasto nell'ordine, combattimento terminato.{EffectService.EndCombatEffects(game)}";
        }
        else if (i < game.TurnIndex)
        {
            game.TurnIndex--;
        }
        else if (i == game.TurnIndex)
        {
            // Se n'è andato chi era di turno: si passa a chi lo seguiva con le stesse regole di /next
            // (salta i morti, recupero dallo stordimento, nuovo TurnStamp, movimento e azione freschi).
            // MoveToNextTurn parte dalla posizione successiva a TurnIndex: chi seguiva ora sta in TurnIndex.
            game.TurnIndex--;
            var notes = new List<string>();
            if (!MoveToNextTurn(game, notes, onDead))
            {
                game.CombatActive = false;
                game.TurnIndex = 0;
                return $"{handle} ha lasciato il combattimento: nessuno è in grado di agire, combattimento terminato.{EffectService.EndCombatEffects(game)}";
            }

            string noteText = notes.Count > 0 ? string.Join(" ", notes.Select(x => x + ".")) + " " : string.Empty;
            return $"{handle} ha lasciato il combattimento durante il suo turno. {noteText}{DescribeTurn(game)}";
        }

        return null;
    }

    public static string DescribeTurn(Game game)
    {
        string current = game.CurrentTurnHandle ?? "nessuno";
        var combatant = game.CurrentTurnHandle != null ? GameExecutionScope.FindParticipant(game, game.CurrentTurnHandle) : null;
        string budget = combatant != null
            ? $" Movimento: {Math.Max(0, MovementService.EffectiveMA(game, combatant) - game.TurnMoveUsed):F1}/{MovementService.EffectiveMA(game, combatant)}m, azione: {(game.TurnActionUsed ? "usata" : "disponibile")}."
            : string.Empty;
        return $"Round {game.Round} - tocca a {current}.{budget}";
    }

    /// <summary>Testo di /combat status.</summary>
    public static string CombatStatus(Game game)
    {
        if (!game.CombatActive)
        {
            return game.TurnOrder.Count > 0
                ? $"Nessun combattimento in corso. Ordine di iniziativa pronto: {string.Join(", ", game.TurnOrder.Select(t => $"{t.Handle} ({t.Initiative})"))}."
                : "Nessun combattimento in corso.";
        }

        var order = game.TurnOrder.Select((t, i) => i == game.TurnIndex ? $"[{t.Handle}]" : t.Handle);
        return $"{DescribeTurn(game)} Ordine: {string.Join(" > ", order)}.";
    }

    /// <summary>/combat start: richiede un ordine di iniziativa chiuso. Toglie gli storditi e dà il primo turno.</summary>
    public static TurnOutcome StartCombat(GameExecutionScope scope)
    {
        var game = scope.Game;

        if (game.CombatActive)
        {
            return new TurnOutcome("Il combattimento è già in corso: usa /combat status oppure /combat end.", string.Empty);
        }
        if (game.ActiveCheck is { Kind: Game.CheckKind.Initiative })
        {
            return new TurnOutcome("L'iniziativa è ancora aperta: attendi lo spoglio o chiudila con /check close.", string.Empty);
        }
        if (game.TurnOrder.Count == 0)
        {
            return new TurnOutcome("Nessun ordine di iniziativa: usa prima /check init.", string.Empty);
        }

        game.CombatActive = true;
        game.Round = 0;
        game.TurnIndex = game.TurnOrder.Count - 1;
        foreach (var m in game.Players.Select(pl => pl.Character).Concat(game.NPCs).OfType<Character>())
        {
            m.Health.IsStunned = false;
        }

        var startNotes = new List<string>();
        if (!MoveToNextTurn(game, startNotes, scope.MarkNewlyDead))
        {
            game.CombatActive = false;
            return new TurnOutcome("Nessun personaggio in grado di agire nell'ordine di iniziativa.", string.Empty);
        }

        game.FreeActionsCounter = 0;
        string notes = startNotes.Count > 0 ? string.Join(" ", startNotes.Select(x => x + ".")) + " " : string.Empty;
        return new TurnOutcome(null, $"COMBATTIMENTO INIZIATO! Ordine: {string.Join(" > ", game.TurnOrder.Select(t => t.Handle))}. {notes}{DescribeTurn(game)}");
    }

    /// <summary>/combat end: chiude il combattimento, toglie gli effetti a tempo e libera movimento, azioni e stordimenti.</summary>
    public static EndOutcome EndCombat(GameExecutionScope scope)
    {
        var game = scope.Game;

        bool was = game.CombatActive;
        game.CombatActive = false;
        string endedEffects = EffectService.EndCombatEffects(game);
        game.TurnMoveUsed = 0;
        game.TurnActionUsed = false;
        foreach (var m in game.Players.Select(pl => pl.Character).Concat(game.NPCs).OfType<Character>())
        {
            m.Health.IsStunned = false;
        }

        return new EndOutcome(was, endedEffects);
    }

    /// <summary>
    /// /next: il master (o chi è di turno) passa al prossimo personaggio in grado di agire. Se non ce n'è nessuno
    /// il combattimento termina.
    /// </summary>
    public static TurnOutcome NextTurn(GameExecutionScope scope, bool callerIsMaster, Character? caller)
    {
        var game = scope.Game;

        if (!game.CombatActive)
        {
            return new TurnOutcome("Nessun combattimento in corso.", string.Empty);
        }

        string? current = game.CurrentTurnHandle;
        bool isCurrent = caller != null && current != null && current.Equals(caller.Handle, StringComparison.OrdinalIgnoreCase);
        if (!callerIsMaster && !isCurrent)
        {
            return new TurnOutcome($"Non è il tuo turno: tocca a {current ?? "nessuno"}.", string.Empty);
        }

        string ended = current ?? "nessuno";
        var turnNotes = new List<string>();
        if (MoveToNextTurn(game, turnNotes, scope.MarkNewlyDead))
        {
            string notes = turnNotes.Count > 0 ? string.Join(" ", turnNotes.Select(x => x + ".")) + " " : string.Empty;
            return new TurnOutcome(null, $"Fine turno di {ended}. {notes}{DescribeTurn(game)}");
        }

        game.CombatActive = false;
        return new TurnOutcome(null, "Nessun personaggio in grado di agire: combattimento terminato." + EffectService.EndCombatEffects(game));
    }
}
