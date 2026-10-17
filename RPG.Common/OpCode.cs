namespace RPG.Common.Protocol
{
    public enum OpCode
    {
        // Client -> Server (C2S)
        C2S_Ping = 1,
        C2S_Login = 10,
        C2S_Chat = 20,
        C2S_RollDice = 30,

        // Server -> Client (S2C)
        S2C_Pong = 101,
        S2C_LoginResponse = 110,
        S2C_ChatMessage = 120,
        S2C_Notification = 130,
        S2C_DiceResult = 140
    }
}
