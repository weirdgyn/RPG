using RPG.Common.Model;
using static RPG.Server.World.GameMap;

namespace RPG.Common.Protocol.Payloads;
// Usato sia per C2S che per S2C
public class ChatPayload
{
    public string Sender { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class LoginRequestPayload
{
    public string Username { get; set; } = string.Empty;
}

public class LoginResponsePayload
{
    public bool Success { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class DiceRequestPayload
{
    public string Expression { get; set; } = "1d20";
}

public class DiceResponsePayload
{
    public string Player { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Details { get; set; } = string.Empty;
}

public class PlayerStatePayload
{
    public PlayerState Character { get; set; } = new();
}

public class MoveRequestPayload
{
    // Coordinate assolute o delta (qui usiamo coordinate di destinazione assolute per semplicità)
    public Position3D TargetPosition { get; set; } = new();
}

public class MoveResponsePayload
{
    public bool Success { get; set; }
    public MovementResult Result { get; set; }
    public Position3D CurrentPosition { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}