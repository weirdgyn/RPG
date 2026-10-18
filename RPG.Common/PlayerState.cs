namespace RPG.Common.Model;

public class PlayerState
{
    public string PlayerId { get; set; } = string.Empty;
    public string Handle { get; set; } = "StreetPunk"; // Nome di strada del personaggio
    public string Role { get; set; } = "Solo";        // Solo, Netrunner, Techie, Nomad, Rockerboy, Fixer...
    // Statistiche Interlock principali (range standard 1-10)
    public CoreStats Stats { get; set; } = new();
    // Punti Ferita e Stato di Salute
    public HealthSystem Health { get; set; } = new();
    // Posizione nel mondo virtuale (per quando aggiungeremo il movimento)
    public Position3D Position { get; set; } = new();
    // NUOVO: Mappa delle abilità possedute (Es: "Pistola" -> 4, "RiparareArmi" -> 2)
    public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // INVENTARIO ED EQUIPAGGIAMENTO
    public List<Weapon> Weapons { get; set; } = new();
    public Weapon EquippedWeapon { get; set; } = new Weapon
    {
        Id = "default_pistol",
        Name = "Pistola Sternmeyer 35",
        Type = Weapon.WeaponType.Ranged,
        SkillName = "Pistola",
        DamageDice = "2d6",
        OptimalRangeMeters = 50
    };
}
