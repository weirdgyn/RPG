using RPG.Common.Protocol;

namespace RPG.Server;

/// <summary>
/// Smista i pacchetti ricevuti dai client verso l'handler registrato per il loro OpCode.
/// Prima del login sono ammessi solo <see cref="OpCode.C2S_Login"/> e <see cref="OpCode.C2S_Ping"/>:
/// ogni altro comando riceve una notifica e non raggiunge alcun handler.
/// </summary>
public sealed class PacketDispatcher
{
    public delegate Task PacketHandler(ClientSession client, NetworkPacket packet);

    private readonly Dictionary<OpCode, PacketHandler> _handlers = [];

    /// <summary>Registra l'handler di un OpCode. Un OpCode può avere un solo handler.</summary>
    public PacketDispatcher Register(OpCode opCode, PacketHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(opCode, handler))
        {
            throw new InvalidOperationException($"OpCode {opCode} già registrato.");
        }
        return this;
    }

    public bool IsRegistered(OpCode opCode) => _handlers.ContainsKey(opCode);

    /// <summary>True se il comando si può usare senza aver fatto il login.</summary>
    public static bool AllowedBeforeLogin(OpCode opCode) => opCode is OpCode.C2S_Login or OpCode.C2S_Ping;

    public async Task DispatchAsync(ClientSession client, NetworkPacket packet)
    {
        if (!AllowedBeforeLogin(packet.OpCode) && !client.IsAuthenticated)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi effettuare il login prima di poter usare i comandi." }));
            return;
        }

        if (!_handlers.TryGetValue(packet.OpCode, out var handler))
        {
            Console.WriteLine($"[SERVER] OpCode sconosciuto ricevuto: {packet.OpCode}");
            return;
        }

        await handler(client, packet);
    }
}
