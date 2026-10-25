using RPG.Common.Models;
using RPG.Common.Utils;
using System.Security;
using System.Text.Json;

namespace RPG.Server;

/// <summary>
/// Persistenza dei personaggi su disco (Data/Characters).
/// </summary>
public static class CharacterRepository
{
    public const string CharactersFolder = "Data/Characters";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Verifica se la scheda del personaggio esiste già su disco.
    /// </summary>
    public static bool CharacterExists(string handle, string directoryPath = CharactersFolder)
    {
        string safeHandle = CharacterHandleValidator.ValidateAndNormalize(handle, nameof(handle));
        string absoluteDir = DataPaths.Resolve(directoryPath);
        string fullPath = GetCharacterFilePath(absoluteDir, safeHandle);
        return File.Exists(fullPath);
    }

    /// <summary>
    /// Carica una scheda personaggio esistente da disco. Non crea nulla se la scheda è assente.
    /// </summary>
    /// <exception cref="FileNotFoundException">La scheda non esiste su disco.</exception>
    /// <exception cref="CharacterLoadException">Scheda presente ma corrotta o non leggibile.</exception>
    public static Character LoadCharacter(string handle, string directoryPath = CharactersFolder)
    {
        string safeHandle = CharacterHandleValidator.ValidateAndNormalize(handle, nameof(handle));
        string absoluteDir = DataPaths.Resolve(directoryPath);
        string fullUserFilePath = GetCharacterFilePath(absoluteDir, safeHandle);

        Console.WriteLine($"[CHAR] Ricerca scheda utente per '{safeHandle}' in: {fullUserFilePath}");

        if (!File.Exists(fullUserFilePath))
        {
            throw new FileNotFoundException($"La scheda del personaggio '{safeHandle}' non esiste.", fullUserFilePath);
        }

        try
        {
            string json = File.ReadAllText(fullUserFilePath);
            var character = JsonSerializer.Deserialize<Character>(json);
            if (character != null)
            {
                NormalizeSkills(character);
                Console.WriteLine($"[CHAR] -> SUCCESSO: Scheda di '{safeHandle}' caricata con successo.");
                return character;
            }

            Console.WriteLine($"[CHAR] -> ERRORE: la scheda '{fullUserFilePath}' è vuota.");
            throw new CharacterLoadException(safeHandle, $"La scheda di '{safeHandle}' esiste ma è vuota.");
        }
        catch (CharacterLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHAR] -> ERRORE nella lettura della scheda '{fullUserFilePath}': {ex.Message}.");
            throw new CharacterLoadException(safeHandle, $"La scheda di '{safeHandle}' esiste ma non può essere letta.", ex);
        }
    }

    /// <summary>
    /// Carica la scheda esistente di <paramref name="handle"/> oppure, se assente,
    /// la crea dall'archetipo di default e la persiste (metodo legacy mantenuto per compatibilità).
    /// </summary>
    public static Character LoadOrGenerateCharacter(string handle, string directoryPath = CharactersFolder)
    {
        string safeHandle = CharacterHandleValidator.ValidateAndNormalize(handle, nameof(handle));
        string absoluteDir = DataPaths.Resolve(directoryPath);
        Directory.CreateDirectory(absoluteDir);

        string fullUserFilePath = GetCharacterFilePath(absoluteDir, safeHandle);

        if (File.Exists(fullUserFilePath))
        {
            return LoadCharacter(safeHandle, directoryPath);
        }

        Console.WriteLine($"[CHAR] -> Nessun salvataggio precedente per '{safeHandle}'. Procedo alla generazione.");
        Character newChar = CharacterTemplateFactory.CreateFromDefaultTemplateOrRandom();
        newChar.Handle = safeHandle;
        newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
        newChar.Health.CurrentHealth = newChar.Health.MaxHealth;
        NormalizeSkills(newChar);

        SaveCharacter(newChar, directoryPath);
        return newChar;
    }

    /// <summary>
    /// Salva la scheda del personaggio su disco. 
    /// Applica le stesse validazioni di sicurezza di LoadCharacter.
    /// </summary>
    public static void SaveCharacter(Character character, string directoryPath = CharactersFolder)
    {
        if (character == null || string.IsNullOrWhiteSpace(character.Handle))
        {
            throw new ArgumentException("Character or character handle cannot be null or empty.");
        }

        string safeHandle = CharacterHandleValidator.ValidateAndNormalize(character.Handle, nameof(character));

        try
        {
            string absoluteDir = DataPaths.Resolve(directoryPath);
            Directory.CreateDirectory(absoluteDir);

            string fullFilePath = GetCharacterFilePath(absoluteDir, safeHandle);
            string json = JsonSerializer.Serialize(character, JsonOpts);

            File.WriteAllText(fullFilePath, json);
            Console.WriteLine($"[CHAR] -> PERSISTENZA: Scheda di '{safeHandle}' salvata con successo in '{fullFilePath}'.");
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHAR] -> ERRORE CRITICO salvataggio scheda di {safeHandle}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Come <see cref="SaveCharacter"/>, ma senza propagare le eccezioni: restituisce false e lo scrive
    /// nel log.
    /// </summary>
    public static bool TrySaveCharacter(Character character, string directoryPath = CharactersFolder)
    {
        try
        {
            SaveCharacter(character, directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHAR] -> ATTENZIONE: salvataggio non riuscito per '{character?.Handle}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Percorso completo del file di una scheda, con verifica che resti dentro la cartella dei
    /// personaggi (difesa in profondità: <paramref name="safeHandle"/> è già validato).
    /// </summary>
    private static string GetCharacterFilePath(string absoluteDir, string safeHandle)
    {
        string fullFilePath = Path.GetFullPath(Path.Combine(absoluteDir, $"{safeHandle}.json"));
        string fullDir = Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullFilePath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Invalid character handle: Path traversal detected.");
        }

        return fullFilePath;
    }

    /// <summary>
    /// Elenca le schede salvate su disco, ordinate per handle.
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

    public static void NormalizeSkills(Character character)
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
