using System.Text.Json;

namespace RPG.Common.Protocol;

public class NetworkPacket
{
    public OpCode OpCode { get; set; }
    // Il payload contiene la sotto-struttura JSON serializzata come stringa
    public string Payload { get; set; } = string.Empty;
    // Factory method per creare velocemente un pacchetto da un oggetto C#
    public static NetworkPacket Create<T>(OpCode opCode, T payloadObject)
    {
        return new NetworkPacket
        {
            OpCode = opCode,
            Payload = JsonSerializer.Serialize(payloadObject)
        };
    }
    // Metodo helper per estrarre l'oggetto C# dal payload
    public T? DeserializePayload<T>()
    {
        if (string.IsNullOrEmpty(Payload)) return default;
        return JsonSerializer.Deserialize<T>(Payload);
    }
}
