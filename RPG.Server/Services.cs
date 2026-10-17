using RPG.Common.Model;
using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Server.Services;

public static class CharacterLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static PlayerState LoadOrGenerateCharacter(string handle, string directoryPath = "Data/Characters")
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, $"{handle}.json");

        // 1. Se esiste un salvataggio specifico per il giocatore, lo carichiamo
        if (File.Exists(filePath))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var character = JsonSerializer.Deserialize<PlayerState>(json);
                if (character != null)
                {
                    Console.WriteLine($"[CHAR] Scheda personaggio '{handle}' caricata da file JSON.");
                    return character;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRORE] Errore lettura scheda '{filePath}': {ex.Message}");
            }
        }

        // 2. Se esiste un template di default, possiamo caricarlo e personalizzare l'Handle
        string defaultTemplatePath = "Data/DefaultCharacter.json";
        PlayerState newChar;

        if (File.Exists(defaultTemplatePath))
        {
            try
            {
                string templateJson = File.ReadAllText(defaultTemplatePath);
                newChar = JsonSerializer.Deserialize<PlayerState>(templateJson) ?? new PlayerState();
                Console.WriteLine($"[CHAR] Creato nuovo personaggio per '{handle}' da template di default.");
            }
            catch
            {
                newChar = GenerateRandomCharacterWithDice();
            }
        }
        else
        {
            // 3. Fallback: Generazione tramite tiri dadi con DiceRoller
            newChar = GenerateRandomCharacterWithDice();
        }

        newChar.Handle = handle;
        newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
        newChar.Health.CurrentHealth = newChar.Health.MaxHealth;

        // Salva il nuovo personaggio su file per le prossime connessioni
        SaveCharacter(newChar, directoryPath);

        return newChar;
    }

    public static void SaveCharacter(PlayerState character, string directoryPath = "Data/Characters")
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            string filePath = Path.Combine(directoryPath, $"{character.Handle}.json");
            string json = JsonSerializer.Serialize(character, JsonOpts);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRORE] Impossibile salvare la scheda di {character.Handle}: {ex.Message}");
        }
    }

    private static PlayerState GenerateRandomCharacterWithDice()
    {
        var character = new PlayerState();
        character.Stats.INT = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.REF = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.TECH = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.COOL = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.ATTR = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.MA = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.BODY = (int)GameDice.Roll("2d4+2").Total;
        character.Stats.EMP = (int)GameDice.Roll("2d4+2").Total;

        // Alcune abilità base di default
        character.Skills["Pistola"] = 2;
        character.Skills["Schivare"] = 2;
        character.Skills["Streetwise"] = 1;

        return character;
    }
}