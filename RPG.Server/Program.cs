// Importiamo le classi definite nell'assembly condiviso RPG.Common
using RPG.Common.Model;
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using RPG.Common.Utils;
using RPG.Server.World;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static RPG.Server.World.GameMap;

namespace RPG.Server;

class Program
{
    private const int Port = 5000;
    private static GameMap gameMap = new(); // <--- LA MAPPA GLOBALE DEL SERVER
    private static readonly ConcurrentDictionary<Guid, ConnectedClient> Clients = new();

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== RPG SERVER CORE (JSON Protocol) ===");
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"[*] Server in ascolto su 0.0.0.0:{Port}...");

        var cts = new CancellationTokenSource();

        while (!cts.Token.IsCancellationRequested)
        {
            TcpClient tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
            var client = new ConnectedClient(tcpClient);

            Clients.TryAdd(client.Id, client);
            Console.WriteLine($"[+] [Client {client.IdShort}] Connesso da {client.RemoteEndPoint}");

            _ = Task.Run(() => HandleClientAsync(client, cts.Token));
        }
    }

    private static async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.TcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested && client.TcpClient.Connected)
            {
                // 1. LEGGE LA RIGA JSON RAW DALLA RETE
                string? rawJson = await reader.ReadLineAsync(ct);
                if (rawJson == null) break; // Client disconnesso

                Console.WriteLine($"[RAW IN] [{client.IdShort}]: {rawJson}");

                // 2. DESERIALIZZA IL JSON NEL PACCHETTO BASE NetworkPacket
                NetworkPacket? packet = null;
                try
                {
                    packet = JsonSerializer.Deserialize<NetworkPacket>(rawJson);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[!] Errore parsing JSON da {client.IdShort}: {ex.Message}");
                    continue;
                }

                // 3. SE IL PACCHETTO È VALIDO, PROCESSA L'OPCODE
                if (packet != null)
                {
                    await ProcessPacketAsync(client, packet);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Errore gestione client {client.IdShort}: {ex.Message}");
        }
        finally
        {
            Clients.TryRemove(client.Id, out _);
            client.TcpClient.Close();
            Console.WriteLine($"[-] [Client {client.IdShort} ({client.Username})] Disconnesso.");

            // Notifica gli altri della disconnessione
            var exitNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Username} ha lasciato il gioco." });
            await BroadcastPacketAsync(exitNotify, excludeClientId: client.Id);
        }
    }

    // --- SMISTAMENTO E GESTIONE DEI PACCHETTI ---
    private static async Task ProcessPacketAsync(ConnectedClient client, NetworkPacket packet)
    {
        switch (packet.OpCode)
        {
            case OpCode.C2S_Login:
                // Estraiamo il payload specifico del Login
                var loginReq = packet.DeserializePayload<LoginRequestPayload>();
                client.Username = string.IsNullOrWhiteSpace(loginReq?.Username) ? $"Player_{client.IdShort}" : loginReq.Username;

                Console.WriteLine($"[LOGIN] Client {client.IdShort} autenticato come '{client.Username}'");

                // Prepariamo la risposta di Login (S2C_LoginResponse)
                var loginRespPacket = NetworkPacket.Create(OpCode.S2C_LoginResponse, new LoginResponsePayload
                {
                    Success = true,
                    PlayerId = client.Id.ToString(),
                    Message = $"Benvenuto {client.Username}!"
                });

                // Invia la risposta SOLO a questo client
                await client.SendPacketAsync(loginRespPacket);

                // 2. Inviamo subito lo stato iniziale del personaggio al client
                var statePacket = NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new PlayerStatePayload
                {
                    Character = client.Character
                });
                await client.SendPacketAsync(statePacket);

                // Manda una notifica di BENVENUTO a tutti gli altri
                var joinNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Username} e' entrato nel mondo!" });
                await BroadcastPacketAsync(joinNotify, excludeClientId: client.Id);
                break;
            
            case OpCode.C2S_GetStatus:
                // Il client richiede il suo stato attuale
                var statusPacket = NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new PlayerStatePayload
                {
                    Character = client.Character
                });
                await client.SendPacketAsync(statusPacket);
                break;

            case OpCode.C2S_Chat:
                // Estraiamo il payload della Chat
                var chatReq = packet.DeserializePayload<ChatPayload>();

                Console.WriteLine($"[CHAT] {client.Username}: {chatReq?.Message}");

                // Prepariamo il pacchetto Chat per il broadcast
                var chatBroadcastPacket = NetworkPacket.Create(OpCode.S2C_ChatMessage, new ChatPayload
                {
                    Sender = client.Username,
                    Message = chatReq?.Message ?? ""
                });

                // Invia a TUTTI i client connessi
                await BroadcastPacketAsync(chatBroadcastPacket);
                break;

            case OpCode.C2S_Ping:
                var pongPacket = NetworkPacket.Create(OpCode.S2C_Pong, new { ServerTime = DateTime.UtcNow });
                await client.SendPacketAsync(pongPacket);
                break;

            case OpCode.C2S_RollDice:
                var diceReq = packet.DeserializePayload<DiceRequestPayload>();
                string expr = string.IsNullOrWhiteSpace(diceReq?.Expression) ? "1d20" : diceReq.Expression;

                try
                {
                    // Tiro autorevole sul server
                    var rollResult = GameDice.Roll(expr);

                    Console.WriteLine($"[🎲 DICE] {client.Username} ha tirato '{expr}' -> Totale: {rollResult.Total} ({rollResult.Details})");

                    // Prepara il pacchetto di broadcast per la stanza
                    var diceBroadcast = NetworkPacket.Create(OpCode.S2C_DiceResult, new DiceResponsePayload
                    {
                        Player = client.Username,
                        Expression = rollResult.Expression,
                        Total = rollResult.Total,
                        Details = rollResult.Details
                    });

                    await BroadcastPacketAsync(diceBroadcast); // Invia a tutti (incluso il mittente, o usa exclude se preferisci)
                }
                catch (Exception ex)
                {
                    // Se la sintassi della stringa dei dadi non è valida
                    var errPacket = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Espressione dadi non valida: '{expr}' ({ex.Message})" });
                    await client.SendPacketAsync(errPacket);
                }
                break;

            case OpCode.C2S_Move:
                var moveReq = packet.DeserializePayload<MoveRequestPayload>();
                if (moveReq?.TargetPosition != null)
                {
                    // Valutiamo il movimento tramite la mappa
                    MovementResult moveResult = gameMap.GetMovementResult(moveReq.TargetPosition);

                    if (moveResult == MovementResult.Success)
                    {
                        // Aggiorniamo la posizione autoritativa del player nello stato
                        client.Character.Position = moveReq.TargetPosition;
                        Console.WriteLine($"[MOVE] {client.Character.Handle} si è spostato a ({client.Character.Position.X}, {client.Character.Position.Y}, {client.Character.Position.Z})");
                    }

                    // Rispondiamo al client con l'esito
                    string message = moveResult switch
                    {
                        MovementResult.Success => "Movimento effettuato.",
                        MovementResult.OutOfBounds => "Area inaccessibile (Fuori dai confini).",
                        MovementResult.ObstacleInTheWay => "La via è sbarrata da un ostacolo.",
                        _ => "Movimento non valido."
                    };

                    var moveResp = NetworkPacket.Create(OpCode.S2C_MoveResponse, new MoveResponsePayload
                    {
                        Success = (moveResult == MovementResult.Success),
                        Result = moveResult,
                        CurrentPosition = client.Character.Position,
                        Message = message
                    });
                    await client.SendPacketAsync(moveResp);

                    // (Opzionale) Se il movimento è riuscito, potremmo anche fare un broadcast agli altri player vicini
                }
                break;

            default:
                Console.WriteLine($"[!] OpCode non gestito: {packet.OpCode}");
                break;
        }
    }

    // Funzione helper per inviare un pacchetto a tutti i client
    private static async Task BroadcastPacketAsync(NetworkPacket packet, Guid? excludeClientId = null)
    {
        foreach (var kvp in Clients)
        {
            if (excludeClientId.HasValue && kvp.Key == excludeClientId.Value)
                continue;

            try
            {
                await kvp.Value.SendPacketAsync(packet);
            }
            catch { }
        }
    }
}

// Estensione della classe ConnectedClient per gestire l'invio diretto di NetworkPacket
public class ConnectedClient
{
    public Guid Id { get; } = Guid.NewGuid();
    public string IdShort => Id.ToString()[..8];
    public string Username { get; set; } = "Guest";
    public TcpClient TcpClient { get; }
    public EndPoint? RemoteEndPoint => TcpClient.Client.RemoteEndPoint;
    public PlayerState Character { get; set; } = new();
    private readonly StreamWriter _writer;

    public ConnectedClient(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        var stream = TcpClient.GetStream();
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }

    // Converte l'oggetto NetworkPacket in stringa JSON e lo scrive sul socket
    public async Task SendPacketAsync(NetworkPacket packet)
    {
        string json = JsonSerializer.Serialize(packet);
        await _writer.WriteLineAsync(json);
    }
}