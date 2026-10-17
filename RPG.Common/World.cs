using RPG.Common.Model;

namespace RPG.Server.World;

public class GameMap
{
    private readonly int _minX = 0;
    private readonly int _maxX = 100;
    private readonly int _minY = 0;
    private readonly int _maxY = 100;
    private readonly int _minZ = -2; // Es. sotterranei
    private readonly int _maxZ = 5;  // Es. grattacieli

    // In futuro qui potremmo avere un HashSet<Position3D> di muri/ostacoli
    private readonly HashSet<Position3D> _obstacles = new();

    public GameMap()
    {
        // Esempio: possiamo piazzare qualche ostacolo di prova al piano 0
        _obstacles.Add(new Position3D { X = 5, Y = 5, Z = 0 });
        _obstacles.Add(new Position3D { X = 5, Y = 6, Z = 0 });
    }

    public enum MovementResult
    {
        Success,
        OutOfBounds,
        ObstacleInTheWay
    }

    public MovementResult GetMovementResult(Position3D pos)
    {
        // Controlla i confini
        if (pos.X < _minX || pos.X > _maxX ||
            pos.Y < _minY || pos.Y > _maxY ||
            pos.Z < _minZ || pos.Z > _maxZ)         
        {
            return MovementResult.OutOfBounds;
        }

        // Controlla se è un ostacolo
        if (_obstacles.Contains(pos))
        {
            return MovementResult.ObstacleInTheWay;
        }

        return MovementResult.Success;
    }
}