// Importiamo l'assembly condiviso con i contratti
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
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
                Console.WriteLine($"[MONDO] {msg}");
                break;

            case OpCode.S2C_Pong:
                Console.WriteLine("[SYSTEM] Pong ricevuto dal server.");
                break;

            default:
                Console.WriteLine($"[SCONOSCIUTO] Ricevuto OpCode: {packet.OpCode}");
                break;
        }

        // Ristampiamo il prompt per l'utente
        Console.Write("> ");
    }
}