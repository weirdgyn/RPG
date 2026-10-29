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
    // 1 naturale sul primo d10: fallimento automatico
    public bool IsFumble { get; set; }
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
    // 1 naturale sul primo d10: mancato automatico
    public bool IsFumble { get; set; }
    public int DamageDealt { get; set; }
    // Locazione di impatto (tavola d10): vuoto se l'attacco non è a segno o non applicabile
    public string HitLocation { get; set; } = string.Empty;
    // Moltiplicatore del danno per locazione (es. Testa = 2). 1 quando non applicabile.
    public int DamageMultiplier { get; set; } = 1;
    // Armatura sulla locazione colpita: SP affrontato (0 = nessuna protezione), danno assorbito e se l'SP è calato
    public int ArmorSP { get; set; }
    public int ArmorAbsorbed { get; set; }
    public bool ArmorAblated { get; set; }
    public int TargetRemainingHealth { get; set; }
    public int TargetMaxHealth { get; set; }
    public string TargetWoundStatus { get; set; } = string.Empty;
    public string WeaponName { get; set; } = string.Empty;
    // Munizioni dell'attaccante dopo il colpo (AmmoCapacity = 0: arma senza caricatore)
    public int AmmoLeft { get; set; }
    public int AmmoCapacity { get; set; }
    public string AmmoName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ReloadRequestPayload
{
    // Opzionale: Id o nome della pila di munizioni da caricare (default: il tipo già in uso)
    public string? AmmoKey { get; set; }
    // Opzionale: arma da ricaricare per Id o nome (default: l'arma in pugno)
    public string? WeaponKey { get; set; }
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
    public string Reason { get; set; } = string.Empty;
    public int TotalResult { get; set; }
    public string Details { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class SkillInfoPayload
{
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Stat { get; set; } = string.Empty;
    public int StatValue { get; set; }
    public int BasePool { get; set; }
}

public class SkillListResponsePayload
{
    public string Character { get; set; } = string.Empty;
    public int WoundPenalty { get; set; }
    public List<SkillInfoPayload> Skills { get; set; } = [];
}

public class PongPayload
{
    public string Message { get; set; } = "Pong!";
    // Timestamp server (UTC) utile per eventuali calcoli di latenza
    public long ServerTimestampUtcMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public class PlayersRequestPayload
{
    public string? GameName { get; set; }
}

public class PlayerInfoPayload
{
    public string Handle { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;
    public string? CharacterHandle { get; set; }
    public string Game { get; set; } = "---";
}

public class PlayersResponsePayload
{
    public string? FilterGame { get; set; }
    public List<PlayerInfoPayload> Players { get; set; } = [];
}

public class JoinRequestPayload
{
    public string GameName { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none;
    public string Description { get; set; } = string.Empty; // Descrizione opzionale del gioco 
}

public class JoinResponsePayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty; // Messaggio di errore o conferma
    public string GameName { get; set; } = string.Empty;
    public Player.RoleType Role { get; set; } = Player.RoleType.none; // Ruolo assegnato al giocatore nel game
}

public class LeaveResponsePayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    // Ruolo del giocatore dopo l'uscita (player se ha un personaggio collegato, altrimenti none)
    public Player.RoleType Role { get; set; } = Player.RoleType.none;
}

public class NpcRequestPayload
{
    // add | list | remove | status
    public string Action { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
}

public class NpcInfoPayload
{
    public string Handle { get; set; } = string.Empty;
    public Character.RoleType Role { get; set; } = Character.RoleType.Solo;
    public int CurrentHealth { get; set; }
    public int MaxHealth { get; set; }
    public string WoundStatus { get; set; } = string.Empty;
    public Position3D Position { get; set; } = new();
}

public class NpcResponsePayload
{
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<NpcInfoPayload> Npcs { get; set; } = [];
    // Valorizzato solo da "status": la scheda dell'NPC
    public Character? Character { get; set; }
}

// Comando eseguito dal master per conto di un NPC: l'opcode e il payload sono quelli del comando "normale"
public class AsRequestPayload
{
    public string ActorHandle { get; set; } = string.Empty;
    public OpCode InnerOpCode { get; set; }
    public string InnerPayload { get; set; } = string.Empty;
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

public class CreateCharacterRequestPayload
{
    public string Archetype { get; set; } = string.Empty;
    public string CharacterHandle { get; set; } = string.Empty;
}

public class CreateCharacterResponsePayload
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CharacterHandle { get; set; } = string.Empty;
    public string Archetype { get; set; } = string.Empty;
}

public class InitRequest
{
    public string PlayerName { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
}

/// <summary>Notifica S2C inviata dal master ai giocatori per avviare il tiro di iniziativa.</summary>
public class CheckRequestPayload
{
    // "skill", "init", "status", "close", "cancel"
    public string Action { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public int TargetNumber { get; set; }
    // Personaggi iscritti; vuoto = tutti i personaggi del game (giocatori e NPC)
    public List<string> Handles { get; set; } = [];
}

public class CheckEntryResult
{
    public string Handle { get; set; } = string.Empty;
    public bool HasRoll { get; set; }
    public int Total { get; set; }
    public bool IsFumble { get; set; }
    public bool IsSuccess { get; set; }
    // Posizione nell'ordine di gioco (1 = agisce per primo); 0 per le prove di abilità o senza tiro
    public int Position { get; set; }
}

public class CheckResultPayload
{
    public bool IsInitiative { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public int TargetNumber { get; set; }
    // Chiuso dal master prima che tutti avessero tirato
    public bool Forced { get; set; }
    public List<CheckEntryResult> Entries { get; set; } = [];
}

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
        // "libero", "in uso da <giocatore>" oppure "NPC di <game>"
        public string Status { get; set; } = string.Empty;
    }
    public List<CharacterNode> Characters { get; set; } = [];
}
