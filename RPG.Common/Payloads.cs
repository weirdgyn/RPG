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
    public long Total { get; set; }
    public string Details { get; set; } = string.Empty;
}