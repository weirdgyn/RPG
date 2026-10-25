using RPG.Common.Models;
using RPG.Common.Utils;
using System.Text.Json;

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
    /// Se la scheda esiste ma non è leggibile NON ne crea una nuova (la salverebbe sopra il
    /// file esistente, cancellandone i dati): lancia <see cref="CharacterLoadException"/> e
    /// lascia il file intatto.
    /// </summary>
    /// <exception cref="CharacterLoadException">Scheda presente su disco ma non caricabile.</exception>
    public static Character LoadOrGenerateCharacter(string handle, string directoryPath = CharactersFolder)
    {
        // Risolviamo i percorsi per avere chiarezza nei log
        string absoluteDir = DataPaths.Resolve(directoryPath);
        Directory.CreateDirectory(absoluteDir);

        string userFilePath = Path.Combine(absoluteDir, $"{handle}.json");
        Console.WriteLine($"[CHAR] Ricerca scheda utente per '{handle}' in: {userFilePath}");

        // 1. Controllo se esiste già il salvataggio specifico dell'utente
        if (File.Exists(userFilePath))
        {
            try
            {
                string json = File.ReadAllText(userFilePath);
                var character = JsonSerializer.Deserialize<Character>(json);
                if (character != null)
                {
                    NormalizeSkills(character);
                    Console.WriteLine($"[CHAR] -> SUCCESSO: Scheda di '{handle}' caricata dal file di salvataggio esistente.");
                    return character;
                }

                // JSON valido ma vuoto ("null"): per noi è come un file corrotto
                Console.WriteLine($"[CHAR] -> ERRORE: la scheda '{userFilePath}' è vuota.");
                throw new CharacterLoadException(handle, $"La scheda di '{handle}' esiste ma è vuota.");
            }
            catch (CharacterLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Non si prosegue alla creazione: salvare un personaggio nuovo sovrascriverebbe il file.
                // Vale anche per errori transitori (file in uso, permessi) su una scheda perfettamente valida.
                Console.WriteLine($"[CHAR] -> ERRORE nella lettura della scheda '{userFilePath}': {ex.Message}. File lasciato intatto.");
                throw new CharacterLoadException(handle, $"La scheda di '{handle}' esiste ma non può essere letta.", ex);
            }
        }
        else
        {
            Console.WriteLine($"[CHAR] -> Nessun salvataggio precedente trovato per '{handle}'. Procedo alla creazione.");
        }

        // 2. Creazione ex-novo (archetipo di default oppure generazione casuale)
        Character newChar = CharacterTemplateFactory.CreateFromDefaultTemplateOrRandom();

        // Inizializziamo/sovrascriviamo l'handle e calcoliamo i punti ferita
        newChar.Handle = handle;
        newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
        newChar.Health.CurrentHealth = newChar.Health.MaxHealth;
        NormalizeSkills(newChar);

        // 3. Salvataggio forzato e persistente su disco del nuovo personaggio
        SaveCharacter(newChar, directoryPath);

        return newChar;
    }

    public static void SaveCharacter(Character character, string directoryPath = CharactersFolder)
    {
        try
        {
            string absoluteDir = DataPaths.Resolve(directoryPath);
            Directory.CreateDirectory(absoluteDir);

            string filePath = Path.Combine(absoluteDir, $"{character.Handle}.json");
            string json = JsonSerializer.Serialize(character, JsonOpts);

            File.WriteAllText(filePath, json);
            Console.WriteLine($"[CHAR] -> PERSISTENZA: Scheda di '{character.Handle}' salvata con successo in '{filePath}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHAR] -> ERRORE CRITICO salvataggio scheda di {character.Handle}: {ex.Message}");
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