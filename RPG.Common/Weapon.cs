namespace RPG.Common.Model;

public enum WeaponType
{
    Ranged, // Arma a distanza (Pistola, Fucile, SMG)
    Melee   // Arma da mischia (Spada, Coltello, Pugni)
}

public class Weapon
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WeaponType Type { get; set; } = WeaponType.Ranged;
    public string SkillName { get; set; } = "Pistola"; // Es. Pistola, Fucile, ArtiMarziali, Scherma
    public string DamageDice { get; set; } = "2d6";
    public int OptimalRangeMeters { get; set; } = 15;   // Gittata/Portata massima efficacia
}
