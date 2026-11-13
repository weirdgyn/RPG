using RPG.Common.Models;

namespace RPG.Server;

/// <summary>
/// Ambito di esecuzione per operazioni atomiche eseguite sotto il lock <see cref="Game.SyncRoot"/>.
/// Fornisce metodi standardizzati di validazione per attori e turni, e raccoglie i personaggi
/// modificati per cui è necessaria la persistenza write-through al termine della sezione critica.
/// </summary>
public sealed class GameExecutionScope(Game game)
{
    private readonly HashSet<Character> _dirtyCharacters = [];
    private readonly List<Character> _newlyDead = [];

    public Game Game { get; } = game ?? throw new ArgumentNullException(nameof(game));

    /// <summary>Personaggi modificati durante l'operazione da salvare su disco al termine del lock.</summary>
    public IReadOnlyCollection<Character> DirtyCharacters => _dirtyCharacters;

    /// <summary>Personaggi morti durante l'operazione (inclusi anche in <see cref="DirtyCharacters"/>).</summary>
    public IReadOnlyList<Character> NewlyDead => _newlyDead;

    /// <summary>Segna un personaggio come modificato da persistere al rilascio del lock.</summary>
    public void MarkDirty(Character? character)
    {
        if (character != null) _dirtyCharacters.Add(character);
    }

    /// <summary>Segna un insieme di personaggi come modificati.</summary>
    public void MarkDirty(IEnumerable<Character>? characters)
    {
        if (characters == null) return;
        foreach (var c in characters)
        {
            if (c != null) _dirtyCharacters.Add(c);
        }
    }

    /// <summary>Segna un personaggio come appena deceduto.</summary>
    public void MarkNewlyDead(Character? character)
    {
        if (character != null)
        {
            _newlyDead.Add(character);
            _dirtyCharacters.Add(character);
        }
    }

    /// <summary>Verifica se un personaggio è attualmente presente nel game (come giocatore o NPC).</summary>
    public bool IsInGame(Character? character)
    {
        if (character == null) return false;
        return Game.NPCs.Any(n => ReferenceEquals(n, character)) ||
               Game.Players.Any(p => ReferenceEquals(p.Character, character));
    }

    /// <summary>Cerca un partecipante per handle (maiuscole indifferenti): prima i giocatori, poi gli NPC.</summary>
    public Character? FindCharacter(string handle) => FindParticipant(Game, handle);

    /// <summary>Come <see cref="FindCharacter"/>, per chi ha un <see cref="Game"/> e non uno scope. Sotto Game.SyncRoot.</summary>
    public static Character? FindParticipant(Game game, string handle) =>
        game.Players.Select(p => p.Character).FirstOrDefault(c => c != null && c.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase))
        ?? game.NPCs.FirstOrDefault(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));

    /// <summary>Restituisce tutti i partecipanti attuali al game (giocatori e NPC) senza duplicati.</summary>
    public List<Character> GetParticipants()
    {
        var all = new List<Character>();
        foreach (var p in Game.Players)
        {
            if (p.Character != null && !all.Contains(p.Character)) all.Add(p.Character);
        }
        foreach (var npc in Game.NPCs)
        {
            if (!all.Contains(npc)) all.Add(npc);
        }
        return all;
    }

    /// <summary>Controlli comuni a chi agisce (attacco, lancio, ricarica, cura): presente e vivo.</summary>
    public string? CheckActor(Character actor)
    {
        if (!IsInGame(actor)) return $"{actor.Handle} non è più in questo game.";
        if (actor.Health.IsDead) return $"{actor.Handle} è morto e non può agire.";
        return null;
    }

    /// <summary>
    /// Verifica che l'attore possa agire adesso in combattimento.
    /// Se <paramref name="needsAction"/> è true, consuma l'azione del turno (<see cref="Game.TurnActionUsed"/>).
    /// </summary>
    public string? CheckTurn(Character actor, bool needsAction)
    {
        if (!Game.CombatActive) return null;

        string? current = Game.CurrentTurnHandle;
        if (current == null || !current.Equals(actor.Handle, StringComparison.OrdinalIgnoreCase))
        {
            return $"Non è il turno di {actor.Handle}: tocca a {current ?? "nessuno"} (round {Game.Round}).";
        }

        if (actor.Health.IsStunned)
        {
            return $"{actor.Handle} è stordito: non può agire né muoversi (a inizio turno tenta di riprendersi, nel frattempo può solo passare con /next).";
        }

        if (needsAction && Game.TurnActionUsed)
        {
            return $"{actor.Handle} ha già usato l'azione di questo turno: termina il turno con /next.";
        }

        if (needsAction) Game.TurnActionUsed = true;
        return null;
    }

    /// <summary>Restituisce l'azione consumata se la risoluzione fallisce dopo CheckTurn.</summary>
    public void RefundAction()
    {
        if (Game.CombatActive) Game.TurnActionUsed = false;
    }

    /// <summary>Incrementa il contatore delle azioni libere fuori dal combattimento.</summary>
    public void CountFreeAction()
    {
        if (!Game.CombatActive) Game.FreeActionsCounter++;
    }
}

/// <summary>
/// Contesto di orchestrazione per l'esecuzione atomica di operazioni su un <see cref="Game"/>.
/// 
/// Rispetta rigorosamente la gerarchia di lock:
/// 1. Acquisisce il lock esclusivo <see cref="Game.SyncRoot"/>.
/// 2. Esegue l'operazione in memoria all'interno di <see cref="GameExecutionScope"/>.
/// 3. Rilascia il lock di <see cref="Game.SyncRoot"/>.
/// 4. Solo DOPO il rilascio del lock, esegue il salvataggio write-through su disco dei personaggi contrassegnati
///    come modificati (<see cref="GameExecutionScope.DirtyCharacters"/>). Questo previene lock-inversion con
///    <c>CharacterRepository._saveLock</c>.
/// </summary>
public sealed class GameExecutionContext(Game game, Action<Character>? saveCharacter = null)
{
    public Game Game { get; } = game ?? throw new ArgumentNullException(nameof(game));
    private readonly Action<Character>? _saveCharacter = saveCharacter;

    /// <summary>
    /// Esegue un'operazione atomica sotto <see cref="Game.SyncRoot"/> e persiste i personaggi modificati al rilascio del lock.
    /// </summary>
    public TResult Execute<TResult>(Func<GameExecutionScope, TResult> operation)
    {
        return Execute(Game, operation, _saveCharacter);
    }

    /// <summary>
    /// Esegue un'azione atomica sotto <see cref="Game.SyncRoot"/> e persiste i personaggi modificati al rilascio del lock.
    /// </summary>
    public void Execute(Action<GameExecutionScope> operation)
    {
        Execute(Game, operation, _saveCharacter);
    }

    /// <summary>
    /// Metodo statico di utilità per eseguire un'operazione atomica su un game arbitrario.
    /// </summary>
    public static TResult Execute<TResult>(Game game, Func<GameExecutionScope, TResult> operation, Action<Character>? saveCharacter = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(operation);

        TResult result;
        List<Character> toSave;

        lock (game.SyncRoot)
        {
            var scope = new GameExecutionScope(game);
            result = operation(scope);
            toSave = [.. scope.DirtyCharacters];
        }

        if (saveCharacter != null)
        {
            // Un salvataggio fallito non deve impedire gli altri né far fallire un'operazione già applicata in memoria.
            foreach (var character in toSave)
            {
                try
                {
                    saveCharacter(character);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SAVE] Salvataggio di '{character.Handle}' fallito: {ex.Message}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Metodo statico di utilità per eseguire un'azione senza valore di ritorno.
    /// </summary>
    public static void Execute(Game game, Action<GameExecutionScope> operation, Action<Character>? saveCharacter = null)
    {
        Execute(game, scope => { operation(scope); return true; }, saveCharacter);
    }
}
