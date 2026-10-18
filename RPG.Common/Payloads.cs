using RPG.Common.Model;
using static RPG.Common.World.GameMap;

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
public class PlayerStatePayload
{
    public PlayerState Character { get; set; } = new();
}
public class MoveRequestPayload
{
    // Coordinate assolute o delta (qui usiamo coordinate di destinazione assolute per semplicità)
    public Position3D Position { get; set; } = new();
}
public class MoveResultPayload
{
    public string PlayerHandle { get; set; } = string.Empty;
    // Posizione richiesta dal giocatore
    public Position3D RequestedPosition { get; set; } = new();
    // Posizione effettivamente raggiunta (== richiesta se il movimento è riuscito)
    public Position3D ActualPosition { get; set; } = new();
    // Esito della validazione lato server (Success / OutOfBounds / ObstacleInTheWay)
    public MovementResult Result { get; set; } = MovementResult.Success;
    public bool IsSuccess => Result == MovementResult.Success;
    public string Message { get; set; } = string.Empty;
}
public class SkillCheckRequestPayload
{
    public string SkillName { get; set; } = string.Empty;
    public int Difficulty { get; set; } = 15; // TN predefinito se non specificato (es. 15 = Medio)
}
public class SkillCheckResponsePayload
{
    public string CharacterHandle { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string StatName { get; set; } = string.Empty;
    public int StatValue { get; set; }
    public int SkillLevel { get; set; }
    public int DiceRoll { get; set; }
    public int TotalResult { get; set; }
    public int TargetNumber { get; set; }
    public bool IsSuccess { get; set; }
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
public class SkillListResponsePayload
{
    public string Character { get; set; } = string.Empty;

    // Mappa NomeAbilità -> Livello
    public Dictionary<string, int> Skills { get; set; } = [];
}