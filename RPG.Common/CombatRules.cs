using RPG.Common.Models;
using RPG.Common.World;

namespace RPG.Common;

public static class CombatRules
{
    // Calcola la distanza euclidea tra due posizioni 3D
    public static double CalculateDistance(Position3D pos1, Position3D pos2)
    {
        double dx = pos1.X - pos2.X;
        double dy = pos1.Y - pos2.Y;
        double dz = pos1.Z - pos2.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
    // TN per un'arma a distanza: fasce della tabella Cyberpunk 2020 scalate sulla gittata dell'arma.
    // Con gittata 50 m: 1 / 12,5 / 25 / 50 / 100 m. Oltre il doppio della gittata il bersaglio è fuori portata (null).
    public static int? GetRangedTargetNumber(double distanceMeters, double weaponRangeMeters)
    {
        double range = weaponRangeMeters > 0 ? weaponRangeMeters : 50.0;
        if (distanceMeters <= 1.0) return 10;          // Point Blank (A bruciapelo)
        if (distanceMeters <= range / 4) return 15;    // Close (Ravvicinata)
        if (distanceMeters <= range / 2) return 20;    // Medium (Media)
        if (distanceMeters <= range) return 25;        // Long (Lunga)
        if (distanceMeters <= range * 2) return 30;    // Extreme (Estrema)
        return null;                                   // Fuori portata
    }
    /// <summary>
    /// Risultato della locazione di impatto (tabella roll Cyberpunk 2020).
    /// La Testa raddoppia il danno.
    /// </summary>
    public sealed class HitLocationResult
    {
        public int Roll { get; init; }
        public string Name { get; init; } = string.Empty;
        public int DamageMultiplier { get; init; } = 1;
        /// <summary>Locazione dell'armatura che protegge la zona colpita (braccia e gambe: un solo pezzo per lato).</summary>
        public ArmorLocation ArmorLocation { get; init; } = ArmorLocation.Torso;
    }

    /// <summary>Esito dell'impatto contro l'armatura.</summary>
    public sealed record ArmorResult(int DamageDealt, int SpFaced, int Absorbed, bool Ablated);

    /// <summary>
    /// Applica l'armatura al danno di un colpo a segno, nell'ordine del regolamento CP2020:
    /// 1) si sottrae l'SP della locazione (dimezzato, arrotondato per eccesso, con proiettili perforanti);
    /// 2) il danno che passa viene moltiplicato per la locazione (la Testa raddoppia *dopo* l'armatura);
    /// 3) se l'armatura è penetrata, il suo SP cala di 1 (ablazione). Se il danno è tutto assorbito non succede nulla.
    /// Modifica <paramref name="piece"/> (ablazione): chiamare sotto il lock del combattimento.
    /// </summary>
    public static ArmorResult ApplyArmor(ArmorPiece? piece, int rawDamage, int locationMultiplier, bool armorPiercing = false)
    {
        int sp = piece?.CurrentStopPower ?? 0;
        if (armorPiercing) sp = (sp + 1) / 2;

        int penetrating = Math.Max(0, rawDamage - sp);
        int absorbed = rawDamage - penetrating;
        bool ablated = false;

        if (penetrating > 0 && piece != null && piece.CurrentStopPower > 0)
        {
            piece.CurrentStopPower--;
            ablated = true;
        }

        return new ArmorResult(penetrating * locationMultiplier, sp, absorbed, ablated);
    }
    /// <summary>
    /// Mappa il roll di locazione alla zona colpita:
    /// 1 = Testa (x2), 2-4 = Torso, 5 = Braccio Destro, 6 = Braccio Sinistro,
    /// 7-8 = Gamba Destra, 9-10 = Gamba Sinistra.
    /// </summary>
    public static HitLocationResult ResolveHitLocation(int roll)
    {
        return roll switch
        {
            1 => new HitLocationResult { Roll = roll, Name = "Testa", DamageMultiplier = 2, ArmorLocation = ArmorLocation.Head },
            2 or 3 or 4 => new HitLocationResult { Roll = roll, Name = "Torso", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Torso },
            5 => new HitLocationResult { Roll = roll, Name = "Braccio Destro", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Arms },
            6 => new HitLocationResult { Roll = roll, Name = "Braccio Sinistro", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Arms },
            7 or 8 => new HitLocationResult { Roll = roll, Name = "Gamba Destra", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Legs },
            9 or 10 => new HitLocationResult { Roll = roll, Name = "Gamba Sinistra", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Legs },
            _ => new HitLocationResult { Roll = roll, Name = "Torso", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Torso }
        };
    }
}
