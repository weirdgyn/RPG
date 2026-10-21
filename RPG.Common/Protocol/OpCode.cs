namespace RPG.Common.Protocol;

public enum OpCode
{
    // Client -> Server (C2S)
    C2S_Ping = 1,
    C2S_Login = 10,
    C2S_Chat = 20,
    C2S_Roll = 30,
    C2S_GetStatus = 40,
    C2S_Move = 50,
    C2S_GetSkills = 60,
    C2S_Attack = 70,        // Richiesta di attacco verso un altro player
    C2S_SkillCheck = 80,
    C2S_GetPlayerList = 90, // Richiesta della lista dei giocatori online
    C2S_Join = 100,        // Richiesta di entrare in una stanza di gioco
    // Server -> Client (S2C)
    S2C_Pong = 101,
    S2C_LoginResponse = 110,
    S2C_ChatMessage = 120,
    S2C_Notification = 130,
    S2C_RollResult = 140,
    S2C_PlayerStateUpdate = 150,
    S2C_PlayerMoved = 160,
    S2C_SkillListResponse = 170,
    S2C_AttackResult = 180,  // Risultato dell'attacco (notificato a chi attacca, al bersaglio e alla stanza)
    S2C_SkillCheckResult = 190,
    S2C_PlayerList = 200,     // Elenco dei giocatori online
    S2C_JoinResponse = 210    // Risposta alla richiesta di entrare in un game
}
