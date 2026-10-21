namespace RPG.Common.Models;

/// <summary>
/// Effetto balistico della munizione (CP2020). I modificatori numerici sono
/// "data-driven": il resolver li legge al posto di hardcodare le tabelle.
/// </summary>
public enum AmmoEffect
{
    Standard,        // Nessun modificatore
    ArmorPiercing,   // Dimezza l'SP dell'armatura (HalvesArmorSP = true)
    Expanding        // +1 al danno (DamageModifier), comportamento contro l'armatura da confermare
}

/// <summary>
/// Munizioni in inventario: oggetti consumabili identificati da calibro ed effetto.
/// Per essere utilizzabile da un'arma, <see cref="Caliber"/> deve coincidere con <see cref="Weapon.Caliber"/>.
/// </summary>
public class Ammo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Caliber { get; set; } = string.Empty;
    public int Quantity { get; set; } = 0;
    public AmmoEffect Effect { get; set; } = AmmoEffect.Standard;

    /// <summary>Modificatore al danno (es. Expanding: +1).</summary>
    public int DamageModifier { get; set; } = 0;

    /// <summary>AP: l'SP dell'armatura viene dimezzato prima di applicare il danno.</summary>
    public bool HalvesArmorSP { get; set; }

    public override string ToString() => $"{Name} ({Caliber}, {Quantity} colpi, {Effect})";
}
