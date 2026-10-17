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