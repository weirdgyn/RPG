using RPG.Common.World;

namespace RPG.Common.Models;

public class Character
{
    public enum RoleType {
        Solo,
        NetRunner,
        Nomad,
        Rocker,
        Fixer,
        Corporate,
        Techie,
        TechMed,
        Cop,
        Media
    }
    public class BonusType
    {
        // NB: proprietà con getter/setter: System.Text.Json serializza solo le proprietà,
        // i campi puri verrebbero ignorati (e il bonus andrebbe perso in persistenza).
        public string ApplyTo { get; set; } = "NONE"; // "STAT:BODY", "SKILL:GUNS", "HIT", "DAMAGE", "NONE", "ARMOR", "HEALTH", "INIT"
        public int Value { get; set; } = 0;
    }
    public class CyberWare {
        public enum CyberType
        {
            Fashionware,
            Neuralware,
            ChipWare,
            CyberOptics,
            CyberAudio,
            Implants,
            Bioware,
            CyberLimbs,
            CyberWeapons, 
            BodyPlating
        }
        public string ID { get; set; } = string.Empty;
        public CyberType Type { get; set; } = CyberType.Neuralware;
        public BonusType Bonus { get; set; } = new();
        public string Description { get; set; } = string.Empty;
    }
    public string Handle { get; set; } = "StreetPunk"; // Nome di strada del personaggio
    public RoleType Role { get; set; } = RoleType.Solo;        // Solo, Netrunner, Techie, Nomad, Rockerboy, Fixer...
    // Statistiche Interlock principali (range standard 1-10)
    public Stats Stats { get; set; } = new();
    // Punti Ferita e Stato di Salute
    public HealthSystem Health { get; set; } = new();
    // Posizione nel mondo virtuale (per quando aggiungeremo il movimento)
    public Position3D Position { get; set; } = new();
    // NUOVO: Mappa delle abilità possedute (Es: "Pistola" -> 4, "RiparareArmi" -> 2)
    public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // INVENTARIO ED EQUIPAGGIAMENTO
    public List<Weapon> Weapons { get; set; } = [];
    public Weapon EquippedWeapon { get; set; } = new Weapon
    {
        Id = "default_pistol",
        Name = "Pistola Sternmeyer 35",
        Type = Weapon.WeaponType.Ranged,
        SkillName = "Pistola",
        DamageDice = "2d6",
        OptimalRangeMeters = 50
    };
    // ARMATURA EQUIPAGGIATA (una voce per locazione, con ablazione SP)
    public ArmorSet Armor { get; set; } = new();
    // MUNIZIONI POSSEDUTE (consumabili per calibro/effetto)
    public List<Ammo> Ammos { get; set; } = [];
    public List<CyberWare> Cyberware { get; set; } = []; 
    public int GetSkillValue(string skill)
    {
        // TODO: search for skill get value
        //      check for appliable bonuses (stats, cyberware)
        //      return result
        return 0;
    }
    public int GetHitBonus(string weapon="")
    {
        // TODO: get hit bonus from weapon
        //      check for appliable bonuses (weapon, stats, cyberware)
        //      return result
        return 0;
    }
    public int GetDamageBonus(string weapon="")
    {
        // TODO: get damage bonus from weapon
        //      get damage from ammo
        //      check for appliable bonuses (weapon, stats, cyberware, ammo)
        //      return result
        return 0;
    }
}
