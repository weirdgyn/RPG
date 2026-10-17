// Importiamo le classi definite nell'assembly condiviso RPG.Common
using RPG.Common.Model;
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using RPG.Common.Utils;
using RPG.Server.Services;
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
    private static readonly GameMap gameMap = new(); // <--- LA MAPPA GLOBALE DEL SERVER
    private static readonly ConcurrentDictionary<Guid, ConnectedClient> Clients = new();
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
                var loginReq = packet.DeserializePayload<LoginRequestPayload>();

                // 1. Determiniamo l'handle richiesto dal client (con fallback)
                string desiredHandle = loginReq?.Username?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(desiredHandle))
                {
                    desiredHandle = $"Punk_{client.IdShort}";
                }

                // 2. ORA INOCULIAMO IL CARICAMENTO DA FILE O LA CREAZIONE AUTOMATICA TRAMITE IL LOADER!
                client.Character = CharacterLoader.LoadOrGenerateCharacter(desiredHandle);

                // Assicuriamoci che il PlayerId corrisponda all'ID univoco del client di rete
                client.Character.PlayerId = client.Id.ToString();

                Console.WriteLine($"[LOGIN] Client {client.IdShort} autenticato come '{client.Character.Handle}' (HP: {client.Character.Health.CurrentHealth}/{client.Character.Health.MaxHealth}, Skills caricate: {client.Character.Skills.Count})");

                // 3. Risposta di login al client
                var loginRespPacket = NetworkPacket.Create(OpCode.S2C_LoginResponse, new LoginResponsePayload
                {
                    Success = true,
                    PlayerId = client.Character.PlayerId,
                    Message = $"Benvenuto a Night City, {client.Character.Handle}!"
                });
                await client.SendPacketAsync(loginRespPacket);

                // 4. Inviamo lo stato aggiornato del personaggio al client
                var statePacket = NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new PlayerStatePayload
                {
                    Character = client.Character
                });
                await client.SendPacketAsync(statePacket);

                // 5. Notifica agli altri nella stanza
                var joinNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Character.Handle} è entrato nella rete." });
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
                    // 1. Valutiamo il movimento sulla mappa
                    MovementResult moveResult = gameMap.GetMovementResult(moveReq.TargetPosition);

                    if (moveResult == MovementResult.Success)
                    {
                        // Aggiorniamo la posizione autoritativa del personaggio
                        client.Character.Position = moveReq.TargetPosition;
                        Console.WriteLine($"[MOVE] {client.Character.Handle} si è spostato a ({client.Character.Position.X}, {client.Character.Position.Y}, {client.Character.Position.Z})");
                    }
                    else
                    {
                        Console.WriteLine($"[MOVE REJECTED] {client.Character.Handle} tentato spostamento non valido verso ({moveReq.TargetPosition.X}, {moveReq.TargetPosition.Y}, {moveReq.TargetPosition.Z}) -> Esito: {moveResult}");
                    }

                    // 2. Mappiamo il risultato in un messaggio chiaro per l'utente
                    string message = moveResult switch
                    {
                        MovementResult.Success => $"Spostato con successo in ({client.Character.Position.X}, {client.Character.Position.Y}, {client.Character.Position.Z}).",
                        MovementResult.OutOfBounds => "Movimento fallito: Sei ai confini della mappa/area inaccessibile!",
                        MovementResult.ObstacleInTheWay => "Movimento fallito: La strada è bloccata da un ostacolo!",
                        _ => "Movimento non valido."
                    };

                    // 3. Invia SEMPRE la risposta al client (sia in caso di successo che di errore)
                    var moveResp = NetworkPacket.Create(OpCode.S2C_PlayerMoved, new MoveResponsePayload
                    {
                        Success = (moveResult == MovementResult.Success),
                        Result = moveResult,
                        CurrentPosition = client.Character.Position, // Invia la posizione reale (invariata se fallito)
                        Message = message
                    });

                    await client.SendPacketAsync(moveResp);
                }
                break;

            case OpCode.C2S_SkillCheck:
                var skillReq = packet.DeserializePayload<SkillCheckRequestPayload>();
                if (skillReq != null)
                {
                    string skillName = skillReq.SkillName.Trim();

                    // 1. Identifichiamo la Statistica correlata
                    string statName = Skills.GetStatForSkill(skillName);

                    // 2. Estraiamo il valore della Statistica dal personaggio
                    int statVal = statName.ToUpper() switch
                    {
                        "INT" => client.Character.Stats.INT,
                        "REF" => client.Character.Stats.REF,
                        "TECH" => client.Character.Stats.TECH,
                        "COOL" => client.Character.Stats.COOL,
                        "ATTR" => client.Character.Stats.ATTR,
                        "MA" => client.Character.Stats.MA,
                        "BODY" => client.Character.Stats.BODY,
                        "EMP" => client.Character.Stats.EMP,
                        _ => client.Character.Stats.REF
                    };

                    // 3. Leggiamo il livello dell'abilità dalla scheda (0 se non addestrata)
                    int skillLvl = client.Character.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;

                    // 4. Calcoliamo la penalità ferite
                    int woundPenalty = client.Character.Health.GetWoundPenalty();
                    if (woundPenalty <= -99)
                    {
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Sei K.O., non puoi eseguire prove!" }));
                        break;
                    }

                    // 5. Tiro d10 autorevole con DiceRoller
                    var roll = GameDice.Roll("1d10");
                    int diceVal = (int)roll.Total;

                    // 6. Formula Interlock canonica
                    int total = statVal + skillLvl + woundPenalty + diceVal;
                    bool success = total >= skillReq.TargetNumber;

                    Console.WriteLine($"[SKILL] {client.Character.Handle} test '{skillName}' -> Stat({statName}):{statVal} + Skill:{skillLvl} + Pen:{woundPenalty} + d10:[{diceVal}] = {total} vs TN {skillReq.TargetNumber} -> {(success ? "SUCCESSO" : "FALLIMENTO")}");

                    var resultPayload = new SkillCheckResponsePayload
                    {
                        PlayerHandle = client.Character.Handle,
                        SkillName = skillName,
                        StatUsed = statName,
                        StatValue = statVal,
                        SkillLevel = skillLvl,
                        WoundPenalty = woundPenalty,
                        DiceRoll = diceVal,
                        Total = total,
                        TargetNumber = skillReq.TargetNumber,
                        Success = success,
                        Message = success
                            ? $"SUCCESSO! ({total} vs TN {skillReq.TargetNumber})"
                            : $"FALLIMENTO! ({total} vs TN {skillReq.TargetNumber})"
                    };

                    await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_SkillResult, resultPayload));
                }
                break;
            case OpCode.C2S_Attack:
                var attackReq = packet.DeserializePayload<AttackRequestPayload>();
                if (attackReq != null)
                {
                    // 1. Trova il bersaglio tra i client connessi (escludendo se stesso)
                    var targetClient = Clients.Values
                        .FirstOrDefault(c => c.Character.Handle.Equals(attackReq.TargetHandle, StringComparison.OrdinalIgnoreCase));

                    if (targetClient == null)
                    {
                        var errPacket = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Bersaglio '{attackReq.TargetHandle}' non trovato o non online." });
                        await client.SendPacketAsync(errPacket);
                        break;
                    }

                    if (targetClient.Id == client.Id)
                    {
                        var selfErr = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Non puoi attaccare te stesso!" });
                        await client.SendPacketAsync(selfErr);
                        break;
                    }

                    // 2. Calcolo Attacco dell'aggressore
                    int attackerRef = client.Character.Stats.REF;
                    int attackerPenalty = client.Character.Health.GetWoundPenalty();

                    if (attackerPenalty <= -99)
                    {
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Sei K.O., non puoi attaccare!" }));
                        break;
                    }

                    var attackRoll = GameDice.Roll("1d10");
                    int attackDiceVal = (int)attackRoll.Total;
                    int attackTotal = attackerRef + attackReq.WeaponSkillLevel + attackerPenalty + attackDiceVal;

                    // 3. Calcolo Difesa del bersaglio (Base 15 + Riflessi del bersaglio + sue eventuali penalità ferite)
                    int targetRef = targetClient.Character.Stats.REF;
                    int targetPenalty = targetClient.Character.Health.GetWoundPenalty();
                    int defenseTN = 15 + targetRef + targetPenalty; // TN dinamico basato sul bersaglio

                    bool isHit = attackTotal >= defenseTN;
                    int damageDealt = 0;

                    if (isHit)
                    {
                        // 4. Tiro Danno Arma
                        var damageRoll = GameDice.Roll(string.IsNullOrWhiteSpace(attackReq.WeaponDice) ? "2d6" : attackReq.WeaponDice);
                        damageDealt = (int)damageRoll.Total;

                        // Applica il danno al bersaglio (autoritativo)
                        targetClient.Character.Health.CurrentHealth = Math.Max(0, targetClient.Character.Health.CurrentHealth - damageDealt);
                    }

                    // 5. Costruzione del messaggio e risposta
                    string outcomeMsg = isHit
                        ? $"COLPO! {client.Character.Handle} colpisce {targetClient.Character.Handle} per {damageDealt} danni con '{attackReq.WeaponDice}'!"
                        : $"MANCATO! {client.Character.Handle} fallisce l'attacco contro {targetClient.Character.Handle} (Tot: {attackTotal} vs TN {defenseTN}).";

                    Console.WriteLine($"[COMBAT] {outcomeMsg}");

                    var respPayload = new AttackResponsePayload
                    {
                        AttackerHandle = client.Character.Handle,
                        TargetHandle = targetClient.Character.Handle,
                        AttackTotal = attackTotal,
                        DefenseTN = defenseTN,
                        IsHit = isHit,
                        DamageDealt = damageDealt,
                        TargetRemainingHealth = targetClient.Character.Health.CurrentHealth,
                        TargetWoundStatus = targetClient.Character.Health.GetWoundStatusDescription(),
                        Message = outcomeMsg
                    };

                    var attackPacket = NetworkPacket.Create(OpCode.S2C_AttackResult, respPayload);

                    // Invia il risultato sia all'attaccante che al bersaglio (e opzionalmente broadcast alla stanza)
                    await client.SendPacketAsync(attackPacket);
                    await targetClient.SendPacketAsync(attackPacket);
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

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== RPG SERVER CORE (JSON Protocol) ===");
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"[*] Server in ascolto su 0.0.0.0:{Port}...");

        var cts = new CancellationTokenSource();

        Console.WriteLine("[SERVER] Inizializzazione configurazioni di gioco...");
        Skills.Initialize(); // Forza il caricamento immediato di DefaultSkillMap.json all'avvio!

        while (!cts.Token.IsCancellationRequested)
        {
            TcpClient tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
            var client = new ConnectedClient(tcpClient);

            Clients.TryAdd(client.Id, client);
            Console.WriteLine($"[+] [Client {client.IdShort}] Connesso da {client.RemoteEndPoint}");

            _ = Task.Run(() => HandleClientAsync(client, cts.Token));
        }
    }
}