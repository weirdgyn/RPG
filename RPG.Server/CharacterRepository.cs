using RPG.Common.Models;
using RPG.Common.Utils;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPG.Server;

/// <summary>
/// Persistenza dei personaggi su disco (Data/Characters) e logica "get-or-create".
/// La creazione ex-novo di un Character è delegata a <see cref="CharacterTemplateFactory"/>.
/// </summary>
public static class CharacterRepository
{
    public const string CharactersFolder = "Data/Characters";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Carica la scheda esistente di <paramref name="handle"/> oppure, se assente,
    /// la crea dall'archetipo di default e la persiste.
    /// </summary>
    /// <exception cref="ArgumentException">L'handle contiene caratteri non validi o tenta un path traversal.</exception>
    /// <exception cref="CharacterLoadException">Scheda presente su disco ma non caricabile.</exception>
    public static Character LoadOrGenerateCharacter(string handle, string directoryPath = CharactersFolder)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException("Character handle cannot be null or empty.", nameof(handle));
        }

        // LAYER 1: Sanitization
        // Path.GetFileName strips any directory separators. 
        // If the original handle contained "../", safeHandle will differ from handle, and we reject it entirely.
        string safeHandle = Path.GetFileName(handle);
        if (safeHandle != handle || string.IsNullOrWhiteSpace(safeHandle))
        {
            throw new ArgumentException("Invalid character handle: contains invalid path characters.", nameof(handle));
        }

        // LAYER 2: Strict Validation
        // Enforce a strict pattern: only alphanumeric and underscores, between 3 and 20 characters.
        if (!Regex.IsMatch(safeHandle, @"^[a-zA-Z0-9_]{3,20}$"))
        {
            throw new ArgumentException("Invalid character handle: must be 3-20 alphanumeric characters or underscores.", nameof(handle));
        }

        // Resolve the base directory
        string absoluteDir = DataPaths.Resolve(directoryPath);
        Directory.CreateDirectory(absoluteDir);

        // LAYER 3: Path Verification (Defense in Depth)
        string userFilePath = Path.Combine(absoluteDir, $"{safeHandle}.json");
        string fullUserFilePath = Path.GetFullPath(userFilePath);
        string fullAbsoluteDir = Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullUserFilePath.StartsWith(fullAbsoluteDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Invalid character handle: Path traversal detected.");
        }

        Console.WriteLine($"[CHAR] Ricerca scheda utente per '{safeHandle}' in: {fullUserFilePath}");

        // 1. Controllo se esiste già il salvataggio specifico dell'utente
        if (File.Exists(fullUserFilePath))
        {
            try
            {
                string json = File.ReadAllText(fullUserFilePath);
                var character = JsonSerializer.Deserialize<Character>(json);
                if (character != null)
                {
                    NormalizeSkills(character);
                    Console.WriteLine($"[CHAR] -> SUCCESSO: Scheda di '{safeHandle}' caricata dal file di salvataggio esistente.");
                    return character;
                }

                // JSON valido ma vuoto ("null"): per noi è come un file corrotto
                Console.WriteLine($"[CHAR] -> ERRORE: la scheda '{fullUserFilePath}' è vuota.");
                throw new CharacterLoadException(safeHandle, $"La scheda di '{safeHandle}' esiste ma è vuota.");
            }
            catch (CharacterLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Non si prosegue alla creazione: salvare un personaggio nuovo sovrascriverebbe il file.
                Console.WriteLine($"[CHAR] -> ERRORE nella lettura della scheda '{fullUserFilePath}': {ex.Message}. File lasciato intatto.");
                throw new CharacterLoadException(safeHandle, $"La scheda di '{safeHandle}' esiste ma non può essere letta.", ex);
            }
        }
        else
        {
            Console.WriteLine($"[CHAR] -> Nessun salvataggio precedente trovato per '{safeHandle}'. Procedo alla creazione.");
        }

        // 2. Creazione ex-novo (archetipo di default oppure generazione casuale)
        Character newChar = CharacterTemplateFactory.CreateFromDefaultTemplateOrRandom();

        // Inizializziamo/sovrascriviamo l'handle e calcoliamo i punti ferita
        newChar.Handle = safeHandle;
        newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
        newChar.Health.CurrentHealth = newChar.Health.MaxHealth;
        NormalizeSkills(newChar);

        // 3. Salvataggio forzato e persistente su disco del nuovo personaggio
        SaveCharacter(newChar, directoryPath);

        return newChar;
    }
    /// <summary>
    /// Salva la scheda del personaggio su disco. 
    /// Applica le stesse validazioni di sicurezza di LoadOrGenerateCharacter.
    /// </summary>
    public static void SaveCharacter(Character character, string directoryPath = CharactersFolder)
    {
        if (character == null || string.IsNullOrWhiteSpace(character.Handle))
        {
            throw new ArgumentException("Character or character handle cannot be null or empty.");
        }

        // Apply the exact same security layers to SaveCharacter
        string safeHandle = Path.GetFileName(character.Handle);
        if (safeHandle != character.Handle || string.IsNullOrWhiteSpace(safeHandle))
        {
            throw new ArgumentException("Invalid character handle: contains invalid path characters.");
        }

        if (!Regex.IsMatch(safeHandle, @"^[a-zA-Z0-9_]{3,20}$"))
        {
            throw new ArgumentException("Invalid character handle: must be 3-20 alphanumeric characters or underscores.");
        }

        try
        {
            string absoluteDir = DataPaths.Resolve(directoryPath);
            Directory.CreateDirectory(absoluteDir);

            string filePath = Path.Combine(absoluteDir, $"{safeHandle}.json");
            string fullFilePath = Path.GetFullPath(filePath);
            string fullAbsoluteDir = Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!fullFilePath.StartsWith(fullAbsoluteDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("Invalid character handle: Path traversal detected.");
            }

            string json = JsonSerializer.Serialize(character, JsonOpts);

            // Optional but recommended: Use FileOptions.WriteThrough or atomic writes for production
            File.WriteAllText(fullFilePath, json);
            Console.WriteLine($"[CHAR] -> PERSISTENZA: Scheda di '{safeHandle}' salvata con successo in '{fullFilePath}'.");
        }
        catch (SecurityException)
        {
            throw; // Re-throw security exceptions
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHAR] -> ERRORE CRITICO salvataggio scheda di {safeHandle}: {ex.Message}");
            throw; // Don't swallow critical save errors silently
        }
    }
    /// <summary>
    /// Elenca le schede salvate su disco, ordinate per handle. Usa lo stesso percorso risolto
    /// di <see cref="LoadOrGenerateCharacter"/> / <see cref="SaveCharacter"/>. Una cartella
    /// inesistente (nessun personaggio ancora creato) dà un elenco vuoto; un file illeggibile
    /// o corrotto viene saltato e segnalato nel log, senza far fallire l'intero elenco.
    /// </summary>
    public static List<Character> ListCharacters(string directoryPath = CharactersFolder)
    {
        var characters = new List<Character>();

        string absoluteDir = DataPaths.Resolve(directoryPath);
        if (!Directory.Exists(absoluteDir)) return characters;

        foreach (string file in Directory.EnumerateFiles(absoluteDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var character = JsonSerializer.Deserialize<Character>(File.ReadAllText(file));
                if (character != null) characters.Add(character);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAR] -> ATTENZIONE: scheda '{file}' ignorata nell'elenco: {ex.Message}");
            }
        }

        return characters;
    }

    // La deserializzazione JSON sostituisce il Dictionary delle abilità perdendo il
    // comparer OrdinalIgnoreCase dell'initializer di Character (diventa case-sensitive).
    // Ricostruiamo il dizionario per garantire lookup case-insensitive in tutto il server.
    private static void NormalizeSkills(Character character)
    {
        if (character.Skills == null || character.Skills.Count == 0)
        {
            character.Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        if (character.Skills.Comparer is StringComparer { } sc && ReferenceEquals(sc, StringComparer.OrdinalIgnoreCase))
            return;

        character.Skills = new Dictionary<string, int>(character.Skills, StringComparer.OrdinalIgnoreCase);
    }
}