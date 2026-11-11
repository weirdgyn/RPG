using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Common.World;

namespace RPG.Server;

/// <summary>
/// Regole di movimento e teletrasporto, senza rete né I/O: lavorano su un <see cref="GameExecutionScope"/>
/// (cioè sotto <see cref="Game.SyncRoot"/>) e segnano come modificati i personaggi spostati, che il
/// <see cref="GameExecutionContext"/> salva dopo il rilascio del lock.
/// </summary>
public static class MovementService
{
    /// <summary>Esito di un movimento: se Error è valorizzato Result va ignorato.</summary>
    public readonly record struct MoveOutcome(string? Error, GameMap.MovementResult Result);

    /// <summary>Esito di un teletrasporto: se Error è null, Moved contiene i personaggi spostati.</summary>
    public sealed record TeleportOutcome(string? Error, IReadOnlyList<Character> Moved);

    /// <summary>Movimento per turno con gli effetti (MOVE, STAT:MA). Da chiamare sotto Game.SyncRoot.</summary>
    public static int EffectiveMA(Game game, Character character) =>
        EffectRules.MovementAllowance(game.Effects, character, EffectService.SkillExists);

    /// <summary>"MA 7" oppure "MA 7, con effetti 10".</summary>
    public static string MaText(int baseMa, int effective) =>
        effective == baseMa ? $"MA {baseMa}" : $"MA {baseMa}, con effetti {effective}";

    /// <summary>
    /// Movimento atomico: turno, budget di MA rimasto e aggiornamento della posizione nella stessa sezione critica
    /// (con TurnMoveUsed in combattimento, oppure il contatore delle azioni libere fuori).
    /// </summary>
    public static MoveOutcome TryMove(GameExecutionScope scope, GameMap map, Character actor, Position3D requested)
    {
        var game = scope.Game;

        string? error = scope.CheckTurn(actor, needsAction: false);
        if (error != null) return new MoveOutcome(error, GameMap.MovementResult.Success);

        double distance = CombatRules.CalculateDistance(actor.Position, requested);
        int allowance = EffectiveMA(game, actor);
        double available = game.CombatActive ? Math.Max(0, allowance - game.TurnMoveUsed) : allowance;

        if (distance > available + 1e-9)
        {
            return new MoveOutcome(game.CombatActive
                ? $"Troppo lontano: a {actor.Handle} restano {available:F1}m di movimento in questo turno ({MaText(actor.Stats.MA, allowance)}), la destinazione dista {distance:F1}m."
                : $"Troppo lontano: {actor.Handle} può muoversi al massimo di {allowance}m per volta ({MaText(actor.Stats.MA, allowance)}), la destinazione dista {distance:F1}m.",
                GameMap.MovementResult.Success);
        }

        var result = map.GetMovementResult(requested);
        if (result == GameMap.MovementResult.Success)
        {
            actor.Position = requested;
            if (game.CombatActive) game.TurnMoveUsed += distance;
            else scope.CountFreeAction();
            scope.MarkDirty(actor);
        }

        return new MoveOutcome(null, result);
    }

    /// <summary>
    /// Sposta personaggi e NPC del game nel punto indicato: non è un'azione e non è soggetto al limite di
    /// movimento, ma la destinazione deve essere nella mappa e libera. O si spostano tutti o nessuno.
    /// </summary>
    public static TeleportOutcome TryTeleport(GameExecutionScope scope, IReadOnlyList<string> handles, Position3D destination)
    {
        var game = scope.Game;
        if (game.Map == null) return new TeleportOutcome("Devi essere in un game per usare /teleport: usa /join <game>.", []);

        var found = new List<Character>();
        foreach (var handle in handles)
        {
            var character = scope.FindCharacter(handle);
            if (character == null) return new TeleportOutcome($"Personaggio '{handle}' non trovato in questo game: nessuno spostato.", []);
            found.Add(character);
        }

        var result = game.Map.GetMovementResult(destination);
        if (result != GameMap.MovementResult.Success)
        {
            return new TeleportOutcome(result == GameMap.MovementResult.OutOfBounds
                ? $"({destination.X}, {destination.Y}, {destination.Z}) è fuori dai confini della mappa: nessuno spostato."
                : $"C'è un ostacolo in ({destination.X}, {destination.Y}, {destination.Z}): nessuno spostato.", []);
        }

        foreach (var character in found)
        {
            character.Position = new Position3D { X = destination.X, Y = destination.Y, Z = destination.Z };
            scope.MarkDirty(character);
        }

        return new TeleportOutcome(null, found);
    }
}
