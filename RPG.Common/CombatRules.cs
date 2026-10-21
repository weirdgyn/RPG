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

    // Determina il TN per un'arma a distanza basato sulla tabella canonica Cyberpunk 2020
    public static int GetRangedTargetNumber(double distanceMeters)
    {
        if (distanceMeters <= 1.0) return 10; // Point Blank (A bruciapelo)
        if (distanceMeters <= 12.5) return 15; // Close (Ravvicinata)
        if (distanceMeters <= 25.0) return 20; // Medium (Media)
        if (distanceMeters <= 50.0) return 25; // Long (Lunga)
        return 30;                             // Extreme (Estrema)
    }

    /// <summary>
    /// Risultato della locazione di impatto (tabella d10 Cyberpunk 2020).
    /// La Testa raddoppia il danno.
    /// </summary>
    public sealed class HitLocationResult
    {
        public int Roll { get; init; }
        public string Name { get; init; } = string.Empty;
        public int DamageMultiplier { get; init; } = 1;
    }

    /// <summary>
    /// Mappa il d10 di locazione alla zona colpita:
    /// 1 = Testa (x2), 2-4 = Torso, 5 = Braccio Destro, 6 = Braccio Sinistro,
    /// 7-8 = Gamba Destra, 9-10 = Gamba Sinistra.
    /// </summary>
    public static HitLocationResult ResolveHitLocation(int d10)
    {
        return d10 switch
        {
            1 => new HitLocationResult { Roll = d10, Name = "Testa", DamageMultiplier = 2 },
            2 or 3 or 4 => new HitLocationResult { Roll = d10, Name = "Torso", DamageMultiplier = 1 },
            5 => new HitLocationResult { Roll = d10, Name = "Braccio Destro", DamageMultiplier = 1 },
            6 => new HitLocationResult { Roll = d10, Name = "Braccio Sinistro", DamageMultiplier = 1 },
            7 or 8 => new HitLocationResult { Roll = d10, Name = "Gamba Destra", DamageMultiplier = 1 },
            9 or 10 => new HitLocationResult { Roll = d10, Name = "Gamba Sinistra", DamageMultiplier = 1 },
            _ => new HitLocationResult { Roll = d10, Name = "Torso", DamageMultiplier = 1 }
        };
    }
}
