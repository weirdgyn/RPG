using RPG.Common.Models;
using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Server;

/// <summary>
/// Crea nuove istanze di <see cref="Character"/> partendo da un archetipo JSON
/// o, in ultima istanza, dalla generazione casuale con dadi.
/// Non si occupa di persistenza: quella è compito di <see cref="CharacterRepository"/>.
/// </summary>
public static class CharacterTemplateFactory
{
    public const string ArchetypesFolder = "Data/Archetypes";
    public const string DefaultTemplatePath = "Data/Archetypes/DefaultCharacter.json";

    /// <summary>
    /// Restituisce l'elenco dei nomi di archetipi disponibili sul server.
    /// </summary>
    public static List<string> GetAvailableArchetypes(string folder = ArchetypesFolder)
    {
        string absoluteDir = DataPaths.Resolve(folder);
        if (!Directory.Exists(absoluteDir)) return [];

        return Directory.EnumerateFiles(absoluteDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(name => !name.Equals("DefaultCharacter", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Crea un personaggio a partire da un archetipo specifico (es. Solo, Netrunner, Cop...).
    /// </summary>
    /// <exception cref="FileNotFoundException">Se il file dell'archetipo non esiste.</exception>
    public static Character CreateFromArchetype(string archetypeName, string folder = ArchetypesFolder)
    {
        // Il nome arriva dal client: non va mai usato per costruire un percorso. Si accetta solo se
        // coincide (senza distinguere maiuscole/minuscole) con un archetipo realmente presente, e il
        // file si ricava dall'elenco della cartella, non dal testo ricevuto.
        string absoluteDir = DataPaths.Resolve(folder);
        string? targetFile = Directory.Exists(absoluteDir)
            ? Directory.EnumerateFiles(absoluteDir, "*.json").FirstOrDefault(f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    return !name.Equals("DefaultCharacter", StringComparison.OrdinalIgnoreCase)
                        && name.Equals(archetypeName, StringComparison.OrdinalIgnoreCase);
                })
            : null;

        if (targetFile == null)
        {
            throw new FileNotFoundException($"Archetipo '{archetypeName}' non trovato.");
        }

        string templateJson = File.ReadAllText(targetFile);
        var character = JsonSerializer.Deserialize<Character>(templateJson);
        if (character == null)
        {
            throw new InvalidOperationException($"L'archetipo in '{targetFile}' è vuoto o non valido.");
        }

        return character;
    }

    /// <summary>
    /// Aggiunge una leggera variazione casuale alle statistiche del personaggio (-2..+2),
    /// mantenendo i valori canonici CP2020 nel range 2-10, e aggiorna i punti ferita (BODY * 5).
    /// </summary>
    public static void RandomizeStats(Character character)
    {
        character.Stats.INT = RandomizeStatValue(character.Stats.INT);
        character.Stats.REF = RandomizeStatValue(character.Stats.REF);
        character.Stats.TECH = RandomizeStatValue(character.Stats.TECH);
        character.Stats.COOL = RandomizeStatValue(character.Stats.COOL);
        character.Stats.ATTR = RandomizeStatValue(character.Stats.ATTR);
        character.Stats.MA = RandomizeStatValue(character.Stats.MA);
        character.Stats.BODY = RandomizeStatValue(character.Stats.BODY);
        character.Stats.EMP = RandomizeStatValue(character.Stats.EMP);

        character.Health.MaxHealth = character.Stats.BODY * 5;
        character.Health.CurrentHealth = character.Health.MaxHealth;
    }

    private static int RandomizeStatValue(int baseVal)
    {
        int delta = (int)GameDice.Roll("1d5-3").Total; // produce valori tra -2 e +2
        return Math.Clamp(baseVal + delta, 2, 10);
    }

    /// <summary>
    /// Crea un nuovo personaggio dall'archetipo di default; se il template non è disponibile
    /// o non è parsabile, ripiega sulla generazione casuale. L'Handle non è impostato qui:
    /// lo decide il chiamante (es. CharacterRepository).
    /// </summary>
    public static Character CreateFromDefaultTemplateOrRandom()
    {
        string absoluteTemplatePath = DataPaths.Resolve(DefaultTemplatePath);
        Console.WriteLine($"[CHAR] Ricerca template di default in: {absoluteTemplatePath}");

        if (File.Exists(absoluteTemplatePath))
        {
            try
            {
                string templateJson = File.ReadAllText(absoluteTemplatePath);
                var character = JsonSerializer.Deserialize<Character>(templateJson) ?? new Character();
                Console.WriteLine($"[CHAR] -> SUCCESSO: Creato nuovo personaggio basato sul template '{absoluteTemplatePath}'.");
                return character;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAR] -> ERRORE lettura template '{absoluteTemplatePath}': {ex.Message}. Utilizzo generazione dadi.");
            }
        }
        else
        {
            Console.WriteLine($"[CHAR] -> Template '{absoluteTemplatePath}' non trovato. Generazione statistiche casuali con dadi.");
        }

        return GenerateRandomCharacterWithDice();
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

        character.Weapons.Add(pistol);

        // Arma 2: Corpo a corpo / Arti Marziali
        character.Weapons.Add(new Weapon
        {
            Id = "unarmed",
            Name = "ArtiMarziali",
            Type = Weapon.WeaponType.Melee,
            SkillName = "ArtiMarziali",
            DamageDice = "1d6+2",
            OptimalRangeMeters = 2,
            AccuracyBonus = 1,
            CanParry = true
        });

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

        character.Ammos.Add(new Ammo
        {
            Id = "ammo_10mm_std",
            Name = "Munizioni 10mm standard",
            Caliber = "10mm",
            Quantity = 48,
            Effect = AmmoEffect.Standard
        });
        character.Ammos.Add(new Ammo
        {
            Id = "ammo_10mm_ap",
            Name = "Munizioni 10mm perforanti",
            Caliber = "10mm",
            Quantity = 24,
            Effect = AmmoEffect.ArmorPiercing,
            HalvesArmorSP = true
        });

        character.Cyberware.Add(new Character.CyberWare
        {
            ID = "Sandevistan",
            Type = Character.CyberWare.CyberType.Neuralware,
            Bonus = new Character.BonusType
            {
                ApplyTo = "INIT",
                Value = 3
            },
            Description = "Sandevistan, si attiva solo quando l'utente lo desidera. L'utente deve impartire un comando mentale affinché il potenziamento si attivi."
        });

        return character;
    }
}
