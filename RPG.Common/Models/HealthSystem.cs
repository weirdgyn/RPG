namespace RPG.Common.Models;

public class HealthSystem
{
    public int MaxHealth { get; set; } = 40; // Di norma calcolato da BODY * 5 o simile
    public int CurrentHealth { get; set; } = 40;

    // In Cyberpunk le ferite applicano penalità ai tiri di dado
    public int GetWoundPenalty()
    {
        double ratio = (double)CurrentHealth / MaxHealth;
        if (ratio > 0.75) return 0;   // Leggera (0)
        if (ratio > 0.50) return -2;  // Seria (-2 a tutte le azioni)
        if (ratio > 0.25) return -4;  // Critica (-4 a tutte le azioni)
        if (ratio > 0.00) return -6;  // Mortale (-6 a tutte le azioni)
        return -99;                   // Inconscio / Morto
    }

    public string GetWoundStatusDescription()
    {
        double ratio = (double)CurrentHealth / MaxHealth;
        if (ratio > 0.75) return "Leggera (Nessuna penalità)";
        if (ratio > 0.50) return "Seria (-2 ai tiri)";
        if (ratio > 0.25) return "Critica (-4 ai tiri)";
        if (ratio > 0.00) return "Mortale (-6 ai tiri - Tiro Salvezza sulla Morte)";
        return "K.O. / Morto";
    }
}