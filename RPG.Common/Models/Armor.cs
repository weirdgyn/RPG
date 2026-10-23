namespace RPG.Common.Models;

/// <summary>Locazioni coperte dall'armatura (tavola d'impatto CP2020).</summary>
public enum ArmorLocation
{
    Head,
    Torso,
    Arms,
    Legs
}

/// <summary>
/// Materiale dell'armatura: incide sulla regola dei proiettili AP
/// (le armature Soft sono più vulnerabili ai perforanti rispetto alle Hard).
/// </summary>
public enum ArmorMaterial
{
    Soft,
    Hard
}

/// <summary>
/// Un singolo elemento di armatura (elmo, giubbotto, gambali...).
/// <see cref="CurrentStopPower"/> subisce l'ablazione: cala del danno assorbito a ogni colpo.
/// </summary>
public class ArmorPiece
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ArmorLocation Location { get; set; }
    public ArmorMaterial Material { get; set; } = ArmorMaterial.Soft;
    /// <summary>Stopping Power massimo (SP pieno).</summary>
    public int MaxStopPower { get; set; } = 0;
    /// <summary>Stopping Power attuale (l'ablazione lo riduce quando assorbe danno).</summary>
    public int CurrentStopPower { get; set; } = 0;
    public override string ToString() => $"{Name} [{Location}] SP {CurrentStopPower}/{MaxStopPower}";
}

/// <summary>
/// Equipaggiamento di protezione attivo del personaggio: al massimo un pezzo per locazione.
/// </summary>
public class ArmorSet
{
    public ArmorPiece? Head { get; set; }
    public ArmorPiece? Torso { get; set; }
    public ArmorPiece? Arms { get; set; }
    public ArmorPiece? Legs { get; set; }

    public ArmorPiece? GetPiece(ArmorLocation location) => location switch
    {
        ArmorLocation.Head => Head,
        ArmorLocation.Torso => Torso,
        ArmorLocation.Arms => Arms,
        ArmorLocation.Legs => Legs,
        _ => null
    };

    public void SetPiece(ArmorPiece piece)
    {
        switch (piece.Location)
        {
            case ArmorLocation.Head: Head = piece; break;
            case ArmorLocation.Torso: Torso = piece; break;
            case ArmorLocation.Arms: Arms = piece; break;
            case ArmorLocation.Legs: Legs = piece; break;
        }
    }

    /// <summary>SP effettivo della locazione indicata (0 se non protetta).</summary>
    public int GetSP(ArmorLocation location) => GetPiece(location)?.CurrentStopPower ?? 0;

    /// <summary>SP totale sommando i pezzi indossati (utile per riepiloghi).</summary>
    public int TotalSP => (Head?.CurrentStopPower ?? 0) + (Torso?.CurrentStopPower ?? 0)
                        + (Arms?.CurrentStopPower ?? 0) + (Legs?.CurrentStopPower ?? 0);
}
