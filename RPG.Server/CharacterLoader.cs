using RPG.Common.Models;
using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Server.Services
{
    public static class CharacterLoader
    {
        private const string CharactersFolder = "Data/Characters";
        private const string DefaultTemplatePath = "Data/Archetypes/DefaultCharacter.json";
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
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
            string absoluteTemplatePath = DataPaths.Resolve(DefaultTemplatePath);
            Character newChar;

            Console.WriteLine($"[CHAR] Ricerca template di default in: {absoluteTemplatePath}");

            if (File.Exists(absoluteTemplatePath))
            {
                try
                {
                    string templateJson = File.ReadAllText(absoluteTemplatePath);
                    newChar = JsonSerializer.Deserialize<Character>(templateJson) ?? new Character();
                    Console.WriteLine($"[CHAR] -> SUCCESSO: Creato nuovo personaggio basato sul template '{absoluteTemplatePath}'.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAR] -> ERRORE lettura template '{absoluteTemplatePath}': {ex.Message}. Utilizzo generazione dadi.");
                    newChar = GenerateRandomCharacterWithDice();
                }
            }
            else
            {
                Console.WriteLine($"[CHAR] -> Template '{absoluteTemplatePath}' non trovato. Generazione statistiche casuali con dadi.");
                newChar = GenerateRandomCharacterWithDice();
            }

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
        private static Character GenerateRandomCharacterWithDice()
        {
            var character = new Character();
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
                Type = Weapon.WeaponType.Ranged,
                SkillName = "Pistola",
                DamageDice = "2d6",
                OptimalRangeMeters = 50,
                AccuracyBonus = 1,
                Caliber = "10mm",
                MagazineCapacity = 12,
                CurrentAmmo = 12
            };

            // Arma 2: Corpo a corpo / Arti Marziali
            var unarmed = new Weapon
            {
                Id = "unarmed",
                Name = "ArtiMarziali",
                Type = Weapon.WeaponType.Melee,
                SkillName = "ArtiMarziali",
                DamageDice = "1d6+2",
                OptimalRangeMeters = 2,
                AccuracyBonus = 1,
                CanParry = true
            };

            character.Weapons.Add(pistol);
            character.Weapons.Add(unarmed);

            // Di default parte con la pistola in pugno
            character.EquippedWeapon = pistol;

            // Armatura di base (per-locazione) con munizioni in inventario
            character.Armor.SetPiece(new ArmorPiece
            {
                Id = "helmet_motocross",
                Name = "Elmo da motocross",
                Location = ArmorLocation.Head,
                Material = ArmorMaterial.Soft,
                MaxStopPower = 4,
                CurrentStopPower = 4
            });
            character.Armor.SetPiece(new ArmorPiece
            {
                Id = "vest_kevlar",
                Name = "Giubbotto antiproiettile in Kevlar",
                Location = ArmorLocation.Torso,
                Material = ArmorMaterial.Soft,
                MaxStopPower = 14,
                CurrentStopPower = 14
            });

            character.AmmoInventory.Add(new Ammo
            {
                Id = "ammo_10mm_std",
                Name = "Munizioni 10mm standard",
                Caliber = "10mm",
                Quantity = 48,
                Effect = AmmoEffect.Standard
            });
            character.AmmoInventory.Add(new Ammo
            {
                Id = "ammo_10mm_ap",
                Name = "Munizioni 10mm perforanti",
                Caliber = "10mm",
                Quantity = 24,
                Effect = AmmoEffect.ArmorPiercing,
                HalvesArmorSP = true
            });

            return character;
        }
    }
}