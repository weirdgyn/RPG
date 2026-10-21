namespace RPG.Common.World;

public class Position3D
{
    public int X { get; set; } = 0;
    public int Y { get; set; } = 0;
    public int Z { get; set; } = 0;
    public override bool Equals(object? obj)
    {
        return obj is Position3D p && X == p.X && Y == p.Y && Z == p.Z;
    }
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}