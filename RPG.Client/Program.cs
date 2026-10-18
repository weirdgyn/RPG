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
                var statePayload = packet.DeserializePayload<PlayerStatePayload>();
                if (statePayload?.Character != null)
                {
                    var c = statePayload.Character;
                    Console.WriteLine("\n========================================");
                    Console.WriteLine($"📋 SCHEDA PERSONAGGIO: {c.Handle}");
                    Console.WriteLine("----------------------------------------");
                    Console.WriteLine($" Stats -> INT:{c.Stats.INT} | REF:{c.Stats.REF} | TECH:{c.Stats.TECH} | COOL:{c.Stats.COOL}");
                    Console.WriteLine($"          ATTR:{c.Stats.ATTR} | MA:{c.Stats.MA} | BODY:{c.Stats.BODY} | EMP:{c.Stats.EMP}");
                    Console.WriteLine($" Salute -> {c.Health.CurrentHealth} / {c.Health.MaxHealth} ({c.Health.GetWoundStatusDescription()})");

                    // Stampa Arma Equipaggiata
                    if (c.EquippedWeapon != null)
                    {
                        Console.WriteLine($" ⚔️ Arma Impugnata: {c.EquippedWeapon.Name} [{c.EquippedWeapon.Type}] (Danno: {c.EquippedWeapon.DamageDice}, Skill: {c.EquippedWeapon.SkillName})");
                    }
                    else
                    {
                        Console.WriteLine(" ⚔️ Arma Impugnata: Nessuna (Corpo a corpo / Pugni)");
                    }

                    // Stampa Inventario Armi
                    Console.WriteLine(" 🎒 Armi in Inventario:");
                    if (c.Weapons != null && c.Weapons.Count > 0)
                    {
                        foreach (var w in c.Weapons)
                        {
                            string marker = (c.EquippedWeapon != null && c.EquippedWeapon.Id == w.Id) ? " (EQUIPAGGIATA)" : "";
                            Console.WriteLine($"   - {w.Name} [{w.Type}] | Danno: {w.DamageDice} | Skill: {w.SkillName}{marker}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("   (Nessuna arma nell'inventario)");
                    }
                    Console.WriteLine("========================================");
                }
                break;

            case OpCode.S2C_PlayerMoved:
                var moveResp = packet.DeserializePayload<MoveResponsePayload>();
                if (moveResp != null)
                {
                    if (moveResp.Success)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[POSITION] {moveResp.Message}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[MOVEMENT ERROR] {moveResp.Message} (Posizione attuale: X:{moveResp.CurrentPosition.X}, Y:{moveResp.CurrentPosition.Y}, Z:{moveResp.CurrentPosition.Z})");
                        Console.ResetColor();
                    }
                }
                break;

            case OpCode.S2C_SkillResult:
                var skillResp = packet.DeserializePayload<SkillCheckResponsePayload>();
                if (skillResp != null)
                {
                    Console.WriteLine("\n----------------------------------------");
                    Console.WriteLine($"🎲 [PROVA ABILITÀ - {skillResp.PlayerHandle}]");
                    Console.WriteLine($"   Abilità: {skillResp.SkillName} (Lvl: {skillResp.SkillLevel}) | Stat ({skillResp.StatUsed}): {skillResp.StatValue}");
                    Console.WriteLine($"   Penalità Ferite: {skillResp.WoundPenalty} | Lancio d10: [{skillResp.DiceRoll}]");
                    Console.WriteLine($"   TOTALE: {skillResp.Total} vs Difficoltà TN {skillResp.TargetNumber}");

                    Console.ForegroundColor = skillResp.Success ? ConsoleColor.Green : ConsoleColor.Red;
                    Console.WriteLine($"   ESITO: {skillResp.Message}");
                    Console.ResetColor();
                    Console.WriteLine("----------------------------------------");
                }
                break;
            
            case OpCode.S2C_AttackResult:
                var atkResp = packet.DeserializePayload<AttackResponsePayload>();
                if (atkResp != null)
                {
                    Console.WriteLine("\n----------------------------------------");
                    Console.WriteLine($"⚔️ [COMBATTIMENTO] {atkResp.AttackerHandle} vs {atkResp.TargetHandle}");
                    Console.WriteLine($"   Tiro Attacco: {atkResp.AttackTotal} | Difesa TN: {atkResp.DefenseTN}");

                    if (atkResp.IsHit)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"   💥 {atkResp.Message}");
                        Console.WriteLine($"   Danno inflitto: {atkResp.DamageDealt} HP");
                        Console.WriteLine($"   Salute residua di {atkResp.TargetHandle}: {atkResp.TargetRemainingHealth} HP ({atkResp.TargetWoundStatus})");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"   🛡️ {atkResp.Message}");
                    }
                    Console.ResetColor();
                    Console.WriteLine("----------------------------------------");
                }
                break;

            default:
                Console.WriteLine($"[UNKNOWN] Ricevuto OpCode: {packet.OpCode}");
                break;
        }

        // Ristampiamo il prompt per l'utente
        Console.Write("> ");
    }

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
            Console.WriteLine(" - Scrivi '/status' per visualizzare lo stato del personaggio.\n");
            Console.WriteLine(" - Scrivi '/roll <espressione>' per lanciare i dadi.\n");
            Console.WriteLine(" - Scrivi '/move <X,Y,Z>' per spostarti a una nuova posizione (es. /move 10,5,3).\n");
            Console.WriteLine(" - Scrivi '/skill <NomeAbilità> <TN>' per eseguire un controllo di abilità.\n");
            Console.WriteLine(" - Scrivi '/attack <NomeBersaglio> [Arma|Abilità]' per attaccare un bersaglio (es. /attack mike [Pistola]).\n");
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

                if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    var statusPacket = NetworkPacket.Create(OpCode.C2S_GetStatus, new { });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(statusPacket));
                    continue;
                }

                if (input.StartsWith("/roll "))
                {
                    string diceExpr = input[6..].Trim();
                    var rollPacket = NetworkPacket.Create(OpCode.C2S_RollDice, new DiceRequestPayload { Expression = diceExpr });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(rollPacket));
                    continue;
                }

                // Sintassi: /skill <NomeAbilità> <TN>   (es. /skill Pistola 15)
                if (input.StartsWith("/skill ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int tn))
                    {
                        string skillName = parts[1];

                        var skillPacket = NetworkPacket.Create(OpCode.C2S_SkillCheck, new SkillCheckRequestPayload
                        {
                            SkillName = skillName,
                            TargetNumber = tn
                        });
                        await writer.WriteLineAsync(JsonSerializer.Serialize(skillPacket));
                        continue;
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /skill <NomeAbilità> <TN> (es. /skill Pistola 15)");
                        continue;
                    }
                }

                if (input.StartsWith("/move "))
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

                // Sintassi: 
                //   /attack <Bersaglio> 
                //   /attack <Bersaglio> <Arma_o_Abilità>
                if (input.StartsWith("/attack ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string targetHandle = parts[1];
                        string? weaponOrSkill = parts.Length >= 3 ? parts[2] : null;

                        var attackPacket = NetworkPacket.Create(OpCode.C2S_Attack, new AttackRequestPayload
                        {
                            TargetHandle = targetHandle,
                            WeaponOrSkill = weaponOrSkill
                        });
                        await writer.WriteLineAsync(JsonSerializer.Serialize(attackPacket));
                        continue;
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /attack <Bersaglio> [Arma_o_Abilità]");
                        Console.WriteLine("    Esempi: /attack Jackie  oppure  /attack Jackie ArtiMarziali");
                        continue;
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
}