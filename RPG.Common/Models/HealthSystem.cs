using System.Text.Json.Serialization;

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

    /// <summary>
    /// Stordito (solo in combattimento): non può agire né muoversi finché non supera il tiro di recupero.
    /// Stato di sessione: non viene salvato nella scheda.
    /// </summary>
    [JsonIgnore]
    public bool IsStunned { get; set; }

    /// <summary>
    /// Modificatore di stato per i tiri di stordimento e di recupero (CP2020): Leggera 0, Seria 1, Critica 2,
    /// Mortale 0 = 3, Mortale 1 = 4, e così via.
    /// </summary>
    public int GetStunSaveModifier()
    {
        int d = DamageTaken;
        if (d <= 4) return 0;
        if (d <= 8) return 1;
        if (d <= 12) return 2;
        return 3 + (d - 13) / 4;
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