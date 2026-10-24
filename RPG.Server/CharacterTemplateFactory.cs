using RPG.Common.Models;
using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Server;

/// <summary>
/// Crea nuove istanze di <see cref="Character"/> partendo dall'archetipo JSON di default
/// (<see cref="DefaultTemplatePath"/>) o, in ultima istanza, dalla generazione casuale con dadi.
/// Non si occupa di persistenza: quella è compito di <see cref="CharacterRepository"/>.
/// </summary>
public static class CharacterTemplateFactory
{
    public const string DefaultTemplatePath = "Data/Archetypes/DefaultCharacter.json";

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
            Description = "Sandestivan, si attiva solo quando l'utente lo desidera. L'utente deve impartire un comando mentale affinché il potenziamento si attivi."
        });

        return character;
    }
}