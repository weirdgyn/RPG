namespace RPG.Common.Models;

public class Weapon
{
    public enum WeaponType
    {
        Area,     // Granate e armi d'area
        Ranged,   // Pistola, Fucile, SMG
        Melee     // Spada, Coltello, Pugni
    }

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WeaponType Type { get; set; } = WeaponType.Ranged;
    public string SkillName { get; set; } = "Pistola"; // Es. Pistola, Fucile, ArtiMarziali, Scherma
    public string DamageDice { get; set; } = "2d6";
    /// <summary>
    /// Ranged/Area: gittata ottimale in metri. Melee: reach (portata utile della mischia).
    /// </summary>
    public int OptimalRangeMeters { get; set; } = 15;
    /// <summary>
    /// Qualità dell'arma CP2020 (0 = Difettosa, 1 = Standard, 2 = Ottima):
    /// bonus applicato al tiro per colpire.
    /// </summary>
    public int AccuracyBonus { get; set; } = 1;
    /// <summary>
    /// Calibro delle munizioni richieste (null/empty per armi senza cartucce: es. melee, granate).
    /// Deve corrispondere a <see cref="Ammo.Caliber"/> per essere ricaricabile.
    /// </summary>
    public string? Caliber { get; set; }
    /// <summary>Capacità del caricatore (0 se non applicabile).</summary>
    public int MagazineCapacity { get; set; } = 0;
    /// <summary>Colpi attualmente presenti nel caricatore.</summary>
    public int CurrentAmmo { get; set; } = 0;
    /// <summary>Raggio della deflagrazione per le armi d'area (null per armi non area).</summary>
    public double? BlastRadiusMeters { get; set; }
    /// <summary>
    /// Fasce di danno concentriche per le armi d'area (es. granata a frammentazione).
    /// Se null, il danno base <see cref="DamageDice"/> si applica su tutto il raggio.
    /// </summary>
    public List<DamageBand>? AreaDamageBands { get; set; }
    /// <summary>Skill usata per il tiro di lancio delle armi d'area (es. Atletica).</summary>
    public string ScatterSkillName { get; set; } = "Atletica";
    /// <summary>Indica se l'arma (da mischia) è adatta alla parata.</summary>
    public bool CanParry { get; set; } = true;
}

/// <summary>
/// Fascia di danno concentrica di un'arma d'area:
/// vale per le distanze da <see cref="FromMeters"/> (incluso) a <see cref="ToMeters"/> (escluso).
/// </summary>
public class DamageBand
{
    public double FromMeters { get; set; }
    public double ToMeters { get; set; }
    public string DamageDice { get; set; } = "1d6";
}
