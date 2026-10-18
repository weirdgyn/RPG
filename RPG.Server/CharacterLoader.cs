using RPG.Common.Model;
using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Server.Services
{
    public static class CharacterLoader
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static PlayerState LoadOrGenerateCharacter(string handle, string directoryPath = "Data/Characters")
        {
            // Risolviamo i percorsi per avere chiarezza nei log
            string absoluteDir = Path.GetFullPath(directoryPath);
            Directory.CreateDirectory(absoluteDir);

            string userFilePath = Path.Combine(absoluteDir, $"{handle}.json");
            Console.WriteLine($"[CHAR] Ricerca scheda utente per '{handle}' in: {userFilePath}");

            // 1. Controllo se esiste già il salvataggio specifico dell'utente
            if (File.Exists(userFilePath))
            {
                try
                {
                    string json = File.ReadAllText(userFilePath);
                    var character = JsonSerializer.Deserialize<PlayerState>(json);
                    if (character != null)
                    {
                        Console.WriteLine($"[CHAR] -> SUCCESSO: Scheda di '{handle}' caricata dal file di salvataggio esistente.");
                        return character;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAR] -> ERRORE nella lettura della scheda '{userFilePath}': {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[CHAR] -> Nessun salvataggio precedente trovato per '{handle}'. Procedo alla creazione.");
            }

            // 2. Cerchiamo il template di default
            string defaultTemplatePath = "Data/DefaultCharacter.json";
            string absoluteTemplatePath = Path.GetFullPath(defaultTemplatePath);
            PlayerState newChar;

            Console.WriteLine($"[CHAR] Ricerca template di default in: {absoluteTemplatePath}");

            if (File.Exists(defaultTemplatePath))
            {
                try
                {
                    string templateJson = File.ReadAllText(defaultTemplatePath);
                    newChar = JsonSerializer.Deserialize<PlayerState>(templateJson) ?? new PlayerState();
                    Console.WriteLine($"[CHAR] -> SUCCESSO: Creato nuovo personaggio basato sul template '{defaultTemplatePath}'.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAR] -> ERRORE lettura template '{defaultTemplatePath}': {ex.Message}. Utilizzo generazione dadi.");
                    newChar = GenerateRandomCharacterWithDice();
                }
            }
            else
            {
                Console.WriteLine($"[CHAR] -> Template '{defaultTemplatePath}' non trovato. Generazione statistiche casuali con dadi.");
                newChar = GenerateRandomCharacterWithDice();
            }

            // Inizializziamo/sovrascriviamo l'handle e calcoliamo i punti ferita
            newChar.Handle = handle;
            newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
            newChar.Health.CurrentHealth = newChar.Health.MaxHealth;

            // 3. Salvataggio forzato e persistente su disco del nuovo personaggio
            SaveCharacter(newChar, directoryPath);

            return newChar;
        }

        public static void SaveCharacter(PlayerState character, string directoryPath = "Data/Characters")
        {
            try
            {
                string absoluteDir = Path.GetFullPath(directoryPath);
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

            character.Skills["Pistola"] = 2;
            character.Skills["ArtiMarziali"] = 2;
            character.Skills["Schivare"] = 2;
            character.Skills["Streetwise"] = 1;

            // Arma 1: Pistola da Fuoco
            var pistol = new Weapon
            {
                Id = "sternmeyer_35",
                Name = "Sternmeyer 35",
                Type = WeaponType.Ranged,
                SkillName = "Pistola",
                DamageDice = "2d6",
                OptimalRangeMeters = 50
            };

            // Arma 2: Corpo a corpo / Arti Marziali
            var unarmed = new Weapon
            {
                Id = "unarmed",
                Name = "ArtiMarziali",
                Type = WeaponType.Melee,
                SkillName = "ArtiMarziali",
                DamageDice = "1d6+2",
                OptimalRangeMeters = 2
            };

            character.Weapons.Add(pistol);
            character.Weapons.Add(unarmed);

            // Di default parte con la pistola in pugno
            character.EquippedWeapon = pistol;

            return character;
        }
    }
}