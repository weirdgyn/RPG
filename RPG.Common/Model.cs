using System.Text.Json;

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

    // NUOVO: Mappa delle abilità possedute (Es: "Pistola" -> 4, "RiparareArmi" -> 2)
    public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    public override bool Equals(object? obj)
    {
        return obj is Position3D p && X == p.X && Y == p.Y && Z == p.Z;
    }

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}

public class SkillDefinition
{
    public string Name { get; set; } = string.Empty;
    public string RelatedStat { get; set; } = "REF"; // Es. REF, TECH, INT...
}

public static class Skills
{
    private static Dictionary<string, string> _skillToStatMap = new(StringComparer.OrdinalIgnoreCase);
    private static bool _isInitialized = false;

    private static readonly Dictionary<string, string> DefaultMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Pistola", "REF" },
            { "Fucile", "REF" },
            { "ArtiMarziali", "REF" },
            { "Schivare", "REF" },
            { "Guidare", "REF" },
            { "RiparareArmi", "TECH" },
            { "Cybertech", "TECH" },
            { "Elettronica", "TECH" },
            { "Scassinare", "TECH" },
            { "Netrunning", "INT" },
            { "Streetwise", "INT" },
            { "Medicina", "INT" },
            { "Intimidire", "COOL" },
            { "Interrogare", "COOL" },
            { "Persuasione", "EMP" },
            { "Seduzione", "EMP" }
        };

    // Metodo da chiamare esplicitamente all'avvio del server
    public static void Initialize(string filePath = "Data/DefaultSkillMap.json")
    {
        if (_isInitialized) return;

        try
        {
            string fullPath = Path.GetFullPath(filePath);
            Console.WriteLine($"[CONFIG] Tentativo di caricamento mappa abilità da: {fullPath}");

            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var loadedMap = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (loadedMap != null && loadedMap.Count > 0)
                {
                    _skillToStatMap = new Dictionary<string, string>(loadedMap, StringComparer.OrdinalIgnoreCase);
                    _isInitialized = true;
                    Console.WriteLine($"[CONFIG] -> SUCCESSO: Caricate {_skillToStatMap.Count} abilità da '{filePath}'.");
                    return;
                }
            }
            else
            {
                Console.WriteLine($"[CONFIG] -> File '{filePath}' non trovato. Verrà utilizzata la mappa di fallback hardcoded.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] -> ERRORE durante la lettura di '{filePath}': {ex.Message}. Utilizzo mappa di fallback.");
        }

        // Fallback
        _skillToStatMap = new Dictionary<string, string>(DefaultMap, StringComparer.OrdinalIgnoreCase);
        _isInitialized = true;
        Console.WriteLine($"[CONFIG] -> Fallback attivo: {_skillToStatMap.Count} abilità caricate da codice.");
    }

    public static string GetStatForSkill(string skillName)
    {
        // Assicurazione di sicurezza: se per qualsiasi motivo non è stato inizializzato, lo fa al volo
        if (!_isInitialized)
        {
            Initialize();
        }

        return _skillToStatMap.TryGetValue(skillName, out var stat) ? stat : "REF";
    }
}