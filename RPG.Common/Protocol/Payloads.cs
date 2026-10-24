using RPG.Common.Models;
using RPG.Common.World;
using static RPG.Common.World.GameMap;

namespace RPG.Common.Protocol;
// Usato sia per C2S che per S2C
public class ChatPayload
{
    public string Sender { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    // Se valorizzato: destinatario del messaggio privato. Nullo = messaggio broadcast.
    public string? Recipient { get; set; }
}
public class LoginRequestPayload
{
    public string PlayerName { get; set; } = string.Empty;
}
public class LoginResponsePayload
{
    public bool Success { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
public class CharacterStatePayload
{
    public Character Character { get; set; } = new();
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
    // Locazione di impatto (tavola d10): vuoto se l'attacco non è a segno o non applicabile
    public string HitLocation { get; set; } = string.Empty;
    // Moltiplicatore del danno per locazione (es. Testa = 2). 1 quando non applicabile.
    public int DamageMultiplier { get; set; } = 1;
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
public class PongPayload
{
    public string Message { get; set; } = "Pong!";
    // Timestamp server (UTC) utile per eventuali calcoli di latenza
    public long ServerTimestampUtcMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public class PlayerInfoPayload
{
    public string Handle { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;
}
public class PlayersResponsePayload
{
    public List<PlayerInfoPayload> Players { get; set; } = [];
}
public class JoinRequestPayload
{
    public string GameName  { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;  //TODO: utilizzare enum RoleType invece di stringa per il ruolo del giocatore
    public string Description { get; set; } = string.Empty; // Descrizione opzionale del gioco 
}
public class JoinResponsePayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty; // Messaggio di errore o conferma
    public string GameName { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;    // Ruolo assegnato al giocatore nel game
}
public class LinkRequestPayload
{
    public string PlayerHandle { get; set; } = string.Empty;
    public string CharacterHandle { get; set; } = string.Empty;
}
public class LinkResponsePayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty; // Messaggio di errore o conferma
    public string PlayerHandle { get; set; } = string.Empty;
    public string CharacterHandle { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;
}
public class InitRequest
{
    public string PlayerName { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
}
/// <summary>Notifica S2C inviata dal master ai giocatori per avviare il tiro di iniziativa.</summary>
public class InitRollRequestPayload
{
    public string Message { get; set; } = string.Empty;
}
public class GamesResponsePayload 
{
    public List<string> Games { get; set; } = [];
}
public class CharactersResponsePayload 
{
    public class CharacterNode {
        public string Handle { get; set; } = string.Empty;
        public Character.RoleType Role { get; set; } = Character.RoleType.Solo;
    }
    public List<CharacterNode> Characters { get; set; } = [];
}