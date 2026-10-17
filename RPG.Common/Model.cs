namespace RPG.Common.Model;

public class PlayerState
{
    public string PlayerId { get; set; } = string.Empty;
    public string Handle { get; set; } = "StreetPunk"; // Nome di strada del personaggio
    public string Role { get; set; } = "Solo";        // Solo, Netrunner, Techie, Nomad, Rockerboy, Fixer...

    // Statistiche Interlock principali (range standard 1-10)
    public CoreStats Stats { get; set; } = new();

    // Punti Ferita e Stato di Salute
    public HealthSystem Health { get; set; } = new();

    // Posizione nel mondo virtuale (per quando aggiungeremo il movimento)
    public Position3D Position { get; set; } = new();
}

public class CoreStats
{
    public int INT { get; set; } = 6; // Intelligenza
    public int REF { get; set; } = 6; // Riflessi (chiave per iniziativa e combattimento)
    public int TECH { get; set; } = 5; // Tecnica
    public int COOL { get; set; } = 5; // Ragionamento / Sangue Freddo
    public int ATTR { get; set; } = 5; // Attrattiva
    public int MA { get; set; } = 5;   // Movimento (Movement Allowance)
    public int BODY { get; set; } = 6; // Costituzione / Forza Fisica
    public int EMP { get; set; } = 5;  // Empatia (scende con il Cyberware)
}

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

public class Position3D
{
    public int X { get; set; } = 0;
    public int Y { get; set; } = 0;
    public int Z { get; set; } = 0;
}