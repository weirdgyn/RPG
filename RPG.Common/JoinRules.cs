using RPG.Common.Models;

namespace RPG.Common;

/// <summary>Regole di ingresso in un game legate al personaggio collegato (funzioni pure, testabili).</summary>
public static class JoinRules
{
    /// <summary>
    /// Il master non interpreta personaggi (con una scheda collegata deve usare /unlink); un giocatore deve
    /// avere un personaggio collegato prima di entrare. Restituisce il motivo del rifiuto, o null se può entrare.
    /// </summary>
    public static string? GetRoleDenial(Player.RoleType role, bool hasLinkedCharacter)
    {
        if (role == Player.RoleType.master)
        {
            return hasLinkedCharacter
                ? "Un master non può avere un personaggio collegato: usa /unlink per scollegarlo prima di fare il master."
                : null;
        }

        return hasLinkedCharacter
            ? null
            : "Per entrare in un game come giocatore devi prima collegare un personaggio: usa /link <personaggio> (oppure /create <archetipo> <handle>) e poi /join.";
    }
}
