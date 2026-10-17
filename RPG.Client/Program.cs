// Importiamo l'assembly condiviso con i contratti
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using RPG.Common.Model;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RPG.Client;

class Program
{
    private const string ServerIp = "8.35.196.247"; // IP della tua VM GCP
    private const int ServerPort = 5000;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== RPG CLIENT (JSON Protocol) ===");

        // Chiediamo subito il nome utente per il login iniziale
        Console.Write("Inserisci il tuo nome utente: ");
        string? username = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(username)) username = "Hero_" + Random.Shared.Next(100, 999);

        using var client = new TcpClient();

        try
        {
            Console.WriteLine($"Connessione a {ServerIp}:{ServerPort}...");
            await client.ConnectAsync(ServerIp, ServerPort);
            Console.WriteLine("[+] Connesso al server di gioco!");

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            using var cts = new CancellationTokenSource();

            // --- 1. TASK DI RICEZIONE CONTINUA DAL SERVER ---
            var receiveTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        string? rawJson = await reader.ReadLineAsync(cts.Token);
                        if (rawJson == null)
                        {
                            Console.WriteLine("\n[-] Connessione chiusa dal server.");
                            break;
                        }

                        // Deserializziamo il pacchetto di rete generale
                        var packet = JsonSerializer.Deserialize<NetworkPacket>(rawJson);
                        if (packet != null)
                        {
                            HandleIncomingPacket(packet);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[Errore Ricezione]: {ex.Message}");
                }
            });

            // --- 2. INVIO DELLA RICHIESTA DI LOGIN INIZIALE ---
            var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
            {
                Username = username
            });
            await writer.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

            // --- 3. LOOP PRINCIPALE DI INPUT UTENTE ---
            Console.WriteLine("\nComandi disponibili:");
            Console.WriteLine(" - Scrivi qualsiasi testo per chattare con gli altri.");
            Console.WriteLine(" - Scrivi '/roll <espressione>' per lanciare i dadi.\n");
            Console.WriteLine(" - Scrivi '/status' per visualizzare lo stato del personaggio.\n");
            Console.WriteLine(" - Scrivi '/move X,Y,Z' per spostarti a una nuova posizione (es. /move 10,5,3).\n");
            Console.WriteLine(" - Scrivi 'exit' per disconnetterti.\n");

            while (!cts.IsCancellationRequested)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input)) continue;

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    cts.Cancel();
                    break;
                }

                if (input.StartsWith("/roll "))
                {
                    string diceExpr = input[6..].Trim();
                    var rollPacket = NetworkPacket.Create(OpCode.C2S_RollDice, new DiceRequestPayload { Expression = diceExpr });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(rollPacket));
                    continue;
                }

                if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    var statusPacket = NetworkPacket.Create(OpCode.C2S_GetStatus, new { });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(statusPacket));
                    continue;
                }

                if(input.StartsWith("/move "))
                {
                    string positionStr = input[6..].Trim();
                    // Qui dovremmo parsare la posizione e inviare il pacchetto di movimento
                    var coords = positionStr.Split(',');
                    if (coords.Length == 3 && int.TryParse(coords[0], out int x) && int.TryParse(coords[1], out int y) && int.TryParse(coords[2], out int z))
                    {
                        var movePacket = NetworkPacket.Create(OpCode.C2S_Move, new MoveRequestPayload
                        {
                            TargetPosition = new Position3D { X = x, Y = y, Z = z }
                        });
                        await writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                    }
                    else
                    {
                        Console.WriteLine("[ERRORE] Formato posizione non valido. Usa: /move X,Y,Z");
                    }
                }

                // Ogni messaggio scritto dall'utente viene impacchettato in C2S_Chat
                var chatPacket = NetworkPacket.Create(OpCode.C2S_Chat, new ChatPayload
                {
                    Message = input
                });

                await writer.WriteLineAsync(JsonSerializer.Serialize(chatPacket));
            }

            await receiveTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Errore di Connessione]: {ex.Message}");
        }

        Console.WriteLine("Client terminato.");
    }

    // --- GESTIONE DEI PACCHETTI IN ARRIVO DAL SERVER ---
    private static void HandleIncomingPacket(NetworkPacket packet)
    {
        // Puliamo la riga corrente della console per evitare sovrapposizioni visive
        Console.Write("\r\u001b[K");

        switch (packet.OpCode)
        {
            case OpCode.S2C_LoginResponse:
                var loginResp = packet.DeserializePayload<LoginResponsePayload>();
                Console.WriteLine($"[SERVER LOGIN] {loginResp?.Message} (ID Assegnato: {loginResp?.PlayerId})");
                break;

            case OpCode.S2C_ChatMessage:
                var chat = packet.DeserializePayload<ChatPayload>();
                Console.WriteLine($"[{chat?.Sender}]: {chat?.Message}");
                break;

            case OpCode.S2C_Notification:
                var notif = packet.DeserializePayload<JsonElement>(); // Esempio di payload dinamico o oggetto anonimo
                string? msg = notif.TryGetProperty("Message", out var val) ? val.GetString() : "Notifica di sistema";
                Console.WriteLine($"[NOTIFICATION] {msg}");
                break;

            case OpCode.S2C_Pong:
                Console.WriteLine("[SYSTEM] Pong ricevuto dal server.");
                break;

            case OpCode.S2C_DiceResult:
                var diceResp = packet.DeserializePayload<DiceResponsePayload>();
                Console.WriteLine($"[🎲 DICE ROLL] {diceResp?.Player} lancia [{diceResp?.Expression}] ➔ Risultato: {diceResp?.Total}  ({diceResp?.Details})");
                break;

            case OpCode.S2C_PlayerStateUpdate:
                var stateResp = packet.DeserializePayload<PlayerStatePayload>();
                var c = stateResp?.Character;
                if (c != null)
                {
                    Console.WriteLine("\n=== [SCHEDA PERSONAGGIO] ===");
                    Console.WriteLine($" Nome/Handle : {c.Handle} ({c.Role})");
                    Console.WriteLine($" Salute      : {c.Health.CurrentHealth}/{c.Health.MaxHealth} HP -> Stato: {c.Health.GetWoundStatusDescription()}");
                    Console.WriteLine($" Statistiche : INT:{c.Stats.INT} | REF:{c.Stats.REF} | TECH:{c.Stats.TECH} | COOL:{c.Stats.COOL}");
                    Console.WriteLine($"               ATTR:{c.Stats.ATTR} | MA:{c.Stats.MA} | BODY:{c.Stats.BODY} | EMP:{c.Stats.EMP}");
                    Console.WriteLine($" Posizione   : X: {c.Position.X}, Y: {c.Position.Y}. Z: {c.Position.Z}");
                    Console.WriteLine("=============================");
                }
                break;

            case OpCode.S2C_PlayerMoved:
                var moveResp = packet.DeserializePayload<JsonElement>();
                string? playerId = moveResp.TryGetProperty("PlayerId", out var pid) ? pid.GetString() : "Unknown";
                int x = moveResp.TryGetProperty("X", out var xVal) ? xVal.GetInt32() : 0;
                int y = moveResp.TryGetProperty("Y", out var yVal) ? yVal.GetInt32() : 0;
                int z = moveResp.TryGetProperty("Z", out var zVal) ? zVal.GetInt32() : 0;
                Console.WriteLine($"[MOVIMENTO] Giocatore {playerId} si è spostato a posizione ({x}, {y}, {z})");
                break;

            default:
                Console.WriteLine($"[UNKNOWN] Ricevuto OpCode: {packet.OpCode}");
                break;
        }

        // Ristampiamo il prompt per l'utente
        Console.Write("> ");
    }
}