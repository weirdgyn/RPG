using System.Text.Json;
using RPG.Common.Utils;

namespace RPG.Common.Models;

public static class Skills
{
    private const string DefaultSkillMap = "Data/DefaultSkillMap.json";
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
    public static void Initialize(string filePath = DefaultSkillMap)
    {
        if (_isInitialized) return;

        try
        {
            string fullPath = DataPaths.Resolve(filePath);
            Console.WriteLine($"[CONFIG] Tentativo di caricamento mappa abilità da: {fullPath}");

            if (File.Exists(fullPath))
            {
                string json = File.ReadAllText(fullPath);
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