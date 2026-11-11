using RPG.Common.Models;

namespace RPG.Server;

/// <summary>
/// Elenco dei game attivi e relativo lock: lo stato condiviso da cui dipendono tutti i servizi del server.
///
/// Gerarchia dei lock (dall'esterno verso l'interno): <c>_linkLock</c> → <see cref="GamesLock"/> → <see cref="Game.SyncRoot"/>.
/// Chi tiene un <see cref="Game.SyncRoot"/> non deve prendere <see cref="GamesLock"/> né il SyncRoot di un altro game;
/// i salvataggi su disco si fanno senza alcun lock di gioco in mano.
/// </summary>
public sealed class GameRegistry
{
    private readonly List<Game> _games = [];
    private readonly object _noGameSync = new();

    /// <summary>
    /// Protegge <see cref="Games"/> e la composizione di ogni game (Players, NPCs): vengono modificati da più task
    /// (join, uscite, disconnessioni). Le modifiche alla composizione richiedono anche il SyncRoot del game.
    /// </summary>
    public object GamesLock { get; } = new();

    /// <summary>I game attivi. Da leggere e modificare solo sotto <see cref="GamesLock"/>.</summary>
    public List<Game> Games => _games;

    /// <summary>
    /// Il game che contiene il personaggio (come giocatore o NPC), o null se è fuori da ogni game.
    /// Prende <see cref="GamesLock"/> e lo rilascia prima di restituire: non va chiamato da chi tiene un Game.SyncRoot.
    /// </summary>
    public Game? FindGameOf(Character character)
    {
        lock (GamesLock)
        {
            return _games.FirstOrDefault(g =>
                g.NPCs.Any(n => ReferenceEquals(n, character)) || g.Players.Any(p => ReferenceEquals(p.Character, character)));
        }
    }

    /// <summary>Il game in cui il giocatore è presente, o null. Stesse regole di lock di <see cref="FindGameOf"/>.</summary>
    public Game? FindGameOfPlayer(Player player)
    {
        lock (GamesLock)
        {
            return _games.FirstOrDefault(g => g.Players.Contains(player));
        }
    }

    /// <summary>
    /// Lock che protegge lo stato del personaggio: il SyncRoot del suo game, oppure uno condiviso se è fuori partita
    /// (nessun altro può toccare quella scheda, ma il lock evita di avere un ramo senza protezione).
    /// </summary>
    public object SyncFor(Character character) => FindGameOf(character)?.SyncRoot ?? _noGameSync;

    /// <summary>Lock da usare quando il personaggio non appartiene a nessun game.</summary>
    public object NoGameSync => _noGameSync;
}
