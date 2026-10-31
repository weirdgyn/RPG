namespace RPG.Common.Models;

/// <summary>
/// Scheda delle ferite canonica di Cyberpunk 2020: 40 caselle uguali per tutti i personaggi.
/// Stati per danno subito: Leggera 1-4, Seria 5-8, Critica 9-12, Mortale 0-5 da 13 a 36 (4 caselle ciascuno).
/// A 0 punti rimasti il personaggio è K.O. / morto.
/// </summary>
public class HealthSystem
{
    /// <summary>Numero di caselle della scheda ferite (uguale per tutti: il BODY agisce sul BTM, non sulle caselle).</summary>
    public const int TrackBoxes = 40;

    public int MaxHealth { get; set; } = TrackBoxes;
    public int CurrentHealth { get; set; } = TrackBoxes;

    /// <summary>Danno accumulato (caselle perse).</summary>
    public int DamageTaken => Math.Max(0, MaxHealth - CurrentHealth);

    // In Cyberpunk le ferite applicano penalità ai tiri di dado (qui piatte su tutte le azioni)
    public int GetWoundPenalty()
    {
        if (CurrentHealth <= 0) return -99;  // Inconscio / Morto
        int d = DamageTaken;
        if (d <= 4) return 0;                // Illeso / Leggera
        if (d <= 8) return -2;               // Seria
        if (d <= 12) return -4;              // Critica
        return -6;                           // Mortale 0-5
    }

    public string GetWoundStatusDescription()
    {
        if (CurrentHealth <= 0) return "K.O. / Morto";
        int d = DamageTaken;
        if (d == 0) return "Illeso";
        if (d <= 4) return "Leggera (Nessuna penalità)";
        if (d <= 8) return "Seria (-2 ai tiri)";
        if (d <= 12) return "Critica (-4 ai tiri)";
        return $"Mortale {(d - 13) / 4} (-6 ai tiri)";
    }
}