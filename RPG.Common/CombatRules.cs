using RPG.Common.Model;

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
}
