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

public class SkillCheckRequestPayload
{
    public string SkillName { get; set; } = string.Empty; // es. "Pistola"
    public int TargetNumber { get; set; } = 15;            // Difficoltà TN
}

public class SkillCheckResponsePayload
{
    public string PlayerHandle { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string StatUsed { get; set; } = string.Empty;
    public int StatValue { get; set; }
    public int SkillLevel { get; set; }
    public int WoundPenalty { get; set; }
    public int DiceRoll { get; set; }
    public int Total { get; set; }
    public int TargetNumber { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class AttackRequestPayload
{
    public string TargetHandle { get; set; } = string.Empty; // Nome del bersaglio!

    // Opzionale: specifica l'arma o l'abilità da usare per questo attacco
    public string? WeaponOrSkill { get; set; }
}
public class AttackResponsePayload
{
    public string AttackerHandle { get; set; } = string.Empty;
    public string TargetHandle { get; set; } = string.Empty;
    public int AttackTotal { get; set; }
    public int DefenseTN { get; set; }
    public bool IsHit { get; set; }
    public int DamageDealt { get; set; }
    public int TargetRemainingHealth { get; set; }
    public string TargetWoundStatus { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
public class RollRequestPayload
{
    public string DiceExpression { get; set; } = "1d10";
    public string Reason { get; set; } = string.Empty;
}
public class RollResponsePayload
{
    public string RollerHandle { get; set; } = string.Empty;
    public string DiceExpression { get; set; } = string.Empty;
    public int TotalResult { get; set; }
    public string Details { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}