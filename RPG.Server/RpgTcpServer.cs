using RPG.Common;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.Utils;
using RPG.Common.World;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server.Services
{
    public class RpgTcpServer(string ipAddress, int port)
    {
        private readonly IPAddress _ipAddress = ipAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(ipAddress);
        private readonly int _port = port;
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<Guid, ClientSession> _connectedClients = new();
        // Mappa del mondo: valida i movimenti (confini e ostacoli)
        private readonly GameMap _gameMap = new();
        private List<Game> _games = new();
        // Serializza le operazioni di login per rendere atomico il controllo sull'unicità dell'handle
        private readonly object _loginLock = new();
        private bool _isRunning;
        public async Task StartAsync()
        {
            _listener = new TcpListener(_ipAddress, _port);
            _listener.Start();
            _isRunning = true;

            Console.WriteLine($"[SERVER] In ascolto su {_ipAddress}:{_port}...");

            // Loop di accettazione delle connessioni in entrata
            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    var session = new ClientSession(tcpClient);
                    _connectedClients[session.Id] = session;

                    Console.WriteLine($"[SERVER] Nuovo client connesso [{session.IdShort}] da {tcpClient.Client.RemoteEndPoint}");

                    // Gestisce il client in un task asincrono separato per non bloccare il listener
                    _ = Task.Run(() => HandleClientAsync(session));
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break; // Uscita pulita in fase di shutdown
                    Console.WriteLine($"[SERVER] Errore nell'accettazione del client: {ex.Message}");
                }
            }
        }
        private async Task HandleClientAsync(ClientSession client)
        {
            try
            {
                string? line;
                while ((line = await client.Reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var packet = JsonSerializer.Deserialize<NetworkPacket>(line);
                    if (packet == null) continue;

                    await ProcessPacketAsync(client, packet);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERVER] Connessione interrotta con il client [{client.IdShort}]: {ex.Message}");
            }
            finally
            {
                // Pulizia alla disconnessione
                _connectedClients.TryRemove(client.Id, out _);
                client.TcpClient.Close();

                string userName = !string.IsNullOrEmpty(client.Player.Username) ? client.Player.Username : client.IdShort;
                Console.WriteLine($"[SERVER] Client disconnesso: {userName}");

                // Notifica gli altri utenti nella stanza (solo per client che avevano effettuato il login)
                if (client.IsAuthenticated)
                {
                    var leaveNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{userName} ha abbandonato la rete." });
                    await BroadcastPacketAsync(leaveNotify, excludeClientId: client.Id);
                }
            }
        }
        private async Task ProcessPacketAsync(ClientSession client, NetworkPacket packet)
        {
            // Gate di autenticazione: prima del login sono ammessi solo login e ping (check di connettività)
            if (packet.OpCode != OpCode.C2S_Login && packet.OpCode != OpCode.C2S_Ping && !client.IsAuthenticated)
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi effettuare il login prima di poter usare i comandi." }));
                return;
            }

            switch (packet.OpCode)
            {
                case OpCode.C2S_Ping:
                    {
                        var pong = new PongPayload
                        {
                            Message = $"Pong! ({DateTime.UtcNow:HH:mm:ss} UTC)"
                        };
                        Console.WriteLine($"[PING] Risposta Pong inviata a '{client.Player.Username}'.");
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Pong, pong));
                    }
                    break;

                case OpCode.C2S_Login:
                    {
                        var loginReq = packet.DeserializePayload<LoginRequestPayload>();
                        string userName = loginReq?.PlayerName?.Trim() ?? string.Empty;

                        // TODO: validazione userName/accesso presenza in player database (file JSON?)

                        if (string.IsNullOrWhiteSpace(userName))
                        {
                            userName = $"Punk_{client.IdShort}";
                        }

                        // Sezione sincrona nel lock: controllo unicità handle + caricamento scheda.
                        // Gli await sono fuori dal lock (non ammessi al suo interno).
                        LoginResponsePayload? denial = null;
                        
                        lock (_loginLock)
                        {
                            // Unicità dell'handle: rifiuta se un altro client online usa già lo stesso nome
                            bool duplicate = _connectedClients.Values.Any(c =>
                                c.Id != client.Id &&
                                !string.IsNullOrEmpty(c.Player.Username) &&
                                c.Player.Username.Equals(userName, StringComparison.OrdinalIgnoreCase));

                            if (duplicate)
                            {
                                Console.WriteLine($"[LOGIN] RIFIUTATO: username '{userName}' già in uso da un altro client.");
                                denial = new LoginResponsePayload
                                {
                                    Success = false,
                                    PlayerId = string.Empty,
                                    PlayerName = string.Empty,
                                    Message = $"Lo username '{userName}' è già in uso da un altro giocatore. Scegli un altro nome."
                                };
                            }
                            else
                            {
                                // Caricamento o generazione autoritativa tramite CharacterLoader e file JSON

                                //TODO: spostare nel comando di associazione player -> character
                                client.IsAuthenticated = true;
                            }
                        }

                        if (denial != null)
                        {
                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LoginResponse, denial));
                            break;
                        }

                        Console.WriteLine($"[LOGIN] Client {client.IdShort} autenticato come '{userName}'");

                        client.Player.Username = userName;

                        // Risposta di Login OK
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LoginResponse, new LoginResponsePayload
                        {
                            Success = true,
                            PlayerId = client.Player.PlayerId,
                            PlayerName = client.Player.Username,
                            Message = $"Benvenuto a Night City, {client.Player.Username}!"
                        }));

                        // Notifica globale agli altri
                        var joinNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Player.Username} è entrato nella rete." });
                        await BroadcastPacketAsync(joinNotify, excludeClientId: client.Id);
                    }
                    break;

                case OpCode.C2S_Attack:
                    {
                        var attackReq = packet.DeserializePayload<AttackRequestPayload>();
                        
                        if (attackReq != null)
                        {
                            var targetClient = _connectedClients.Values
                                .FirstOrDefault(c => c.IsAuthenticated && c.Player.Character.Handle.Equals(attackReq.TargetHandle, StringComparison.OrdinalIgnoreCase));

                            if (targetClient == null)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Bersaglio '{attackReq.TargetHandle}' non trovato o non online." }));
                                break;
                            }

                            if (targetClient.Id == client.Id)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Non puoi attaccare te stesso!" }));
                                break;
                            }

                            // Risoluzione dell'arma o abilità richiesta dall'attacco
                            Weapon? weaponToUse = null;
                            if (!string.IsNullOrWhiteSpace(attackReq.WeaponOrSkill))
                            {
                                string key = attackReq.WeaponOrSkill.Trim();
                                weaponToUse = client.Player.Character.Weapons.FirstOrDefault(w =>
                                    w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                    w.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                    w.SkillName.Equals(key, StringComparison.OrdinalIgnoreCase));
                            }

                            // Fallback sull'arma equipaggiata o sulla prima disponibile
                            weaponToUse ??= client.Player.Character.EquippedWeapon ?? client.Player.Character.Weapons.FirstOrDefault();

                            if (weaponToUse == null)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Nessuna arma o abilità d'attacco disponibile!" }));
                                break;
                            }

                            // Statistica e abilità dell'attaccante
                            string skillName = weaponToUse.SkillName;
                            string statName = Skills.GetStatForSkill(skillName);

                            int attackerStat = client.Player.Character.Stats.GetStatValue(statName);
                            int attackerSkillLvl = client.Player.Character.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
                            int attackerPenalty = client.Player.Character.Health.GetWoundPenalty();

                            if (attackerPenalty <= -99)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Sei K.O., non puoi attaccare!" }));
                                break;
                            }

                            // Calcolo distanza e difficoltà (TN)
                            double distance = CombatRules.CalculateDistance(client.Player.Character.Position, targetClient.Player.Character.Position);
                            int targetNumber = 15;
                            string attackTypeDesc = "";

                            if (weaponToUse.Type == Weapon.WeaponType.Ranged)
                            {
                                targetNumber = CombatRules.GetRangedTargetNumber(distance);
                                attackTypeDesc = $"A Distanza con {weaponToUse.Name} ({distance:F1}m) -> TN Fisso: {targetNumber}";
                            }
                            else if (weaponToUse.Type == Weapon.WeaponType.Melee)
                            {
                                // TODO: la gestione dell'attacco in mischia non può essere consumata interamente in un unico scambio client/server. L'iterazione deve prevedere lo scambio di messaggi tra client e server di entrambe le parti. Chi difende deve decidere come reagire (schivare, parare, contrattaccare) e il server deve gestire la logica di risoluzione. Per ora, implementiamo un semplice check di distanza per consentire o meno l'attacco in mischia.

                                if (distance > 2.5)
                                {
                                    await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Troppo lontano per la mischia con {weaponToUse.Name}! Distanza: {distance:F1}m" }));
                                    break;
                                }

                                int targetRef = targetClient.Player.Character.Stats.REF;
                                int targetDodge = targetClient.Player.Character.Skills.TryGetValue("Schivare", out int dLvl) ? dLvl : 0;
                                int targetPenalty = targetClient.Player.Character.Health.GetWoundPenalty();

                                targetNumber = 10 + targetRef + targetDodge + targetPenalty;
                                attackTypeDesc = $"In Mischia con {weaponToUse.Name} -> Difesa Bersaglio (REF {targetRef} + Schivare {targetDodge}): TN {targetNumber}";
                            }
                            else // Fallback attacco area
                            {
                                // TODO: gestione attacchi ad area o speciali
                            }

                            // Tiro d10 d'attacco esplosivo (CP2020: il 10 si ritira e si somma)
                            int diceVal = GameDice.RollExplodingD10();
                            int attackTotal = attackerStat + attackerSkillLvl + attackerPenalty + diceVal;

                            bool isHit = attackTotal >= targetNumber;
                            int damageDealt = 0;
                            string hitLocationName = string.Empty;
                            int hitMultiplier = 1;

                            if (isHit)
                            {
                                var damageRoll = GameDice.Roll(weaponToUse.DamageDice);
                                int rawDamage = (int)damageRoll.Total;

                                // Locazione di impatto per armi a distanza (tavola d10, Testa x2)
                                if (weaponToUse.Type == Weapon.WeaponType.Ranged)
                                {
                                    var loc = CombatRules.ResolveHitLocation((int)GameDice.Roll("1d10").Total);
                                    hitLocationName = loc.Name;
                                    hitMultiplier = loc.DamageMultiplier;
                                }

                                damageDealt = rawDamage * hitMultiplier;

                                targetClient.Player.Character.Health.CurrentHealth = Math.Max(0, targetClient.Player.Character.Health.CurrentHealth - damageDealt);
                                CharacterLoader.SaveCharacter(targetClient.Player.Character); // Persistenza su JSON del bersaglio
                            }

                            string hitDesc = isHit
                                ? hitLocationName.Length > 0
                                    ? $"COLPITO ({damageDealt} dmg, Impatto: {hitLocationName}{(hitMultiplier > 1 ? $" x{hitMultiplier}" : "")})"
                                    : $"COLPITO ({damageDealt} dmg)"
                                : "MANCATO";

                            Console.WriteLine($"[COMBAT] {client.Player.Character.Handle} attacca {targetClient.Player.Character.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. Tiro: {attackTotal} vs TN {targetNumber} -> {hitDesc}");

                            // Invio esito a entrambi
                            string impactSuffix = hitLocationName.Length > 0
                                ? $" Impatto: {hitLocationName}{(hitMultiplier > 1 ? $" (x{hitMultiplier})" : "")}"
                                : "";
                            var respPayload = new AttackResponsePayload
                            {
                                AttackerHandle = client.Player.Character.Handle,
                                TargetHandle = targetClient.Player.Character.Handle,
                                AttackTotal = attackTotal,
                                DefenseTN = targetNumber,
                                IsHit = isHit,
                                DamageDealt = damageDealt,
                                HitLocation = hitLocationName,
                                DamageMultiplier = hitMultiplier,
                                TargetRemainingHealth = targetClient.Player.Character.Health.CurrentHealth,
                                TargetWoundStatus = targetClient.Player.Character.Health.GetWoundStatusDescription(),
                                Message = isHit
                                    ? $"COLPO A SEGNO! {client.Player.Character.Handle} usa '{weaponToUse.Name}' e infligge {damageDealt} danni ({weaponToUse.DamageDice})!{impactSuffix}"
                                    : $"MANCATO! {client.Player.Character.Handle} fallisce l'attacco con '{weaponToUse.Name}'."
                            };

                            var attackPacket = NetworkPacket.Create(OpCode.S2C_AttackResult, respPayload);
                            await client.SendPacketAsync(attackPacket);
                            await targetClient.SendPacketAsync(attackPacket);
                        }
                    }
                    break;

                case OpCode.C2S_Roll:
                    {
                        var rollReq = packet.DeserializePayload<RollRequestPayload>();
                        string expr = string.IsNullOrWhiteSpace(rollReq?.DiceExpression) ? "1d10" : rollReq.DiceExpression;
                        string reason = rollReq?.Reason ?? "Tiro generico";

                        var rollResult = GameDice.Roll(expr);
                        int total = (int)rollResult.Total;

                        Console.WriteLine($"[ROLL] {client.Player.Username} tira {expr} ({reason}): {total}");

                        var resp = new RollResponsePayload
                        {
                            RollerHandle = client.Player.Username,
                            DiceExpression = expr,
                            TotalResult = total,
                            Message = $"{client.Player.Username} tira [{expr}] ({reason}) ed ottiene: {total}"
                        };

                        var packetResp = NetworkPacket.Create(OpCode.S2C_RollResult, resp);
                        // Notifica a tutti i client nella stanza (inclusi gli altri)
                        await BroadcastPacketAsync(packetResp);
                    }
                    break;

                case OpCode.C2S_Chat:
                    {
                        var chatReq = packet.DeserializePayload<ChatPayload>();
                        if (chatReq == null || string.IsNullOrWhiteSpace(chatReq.Message)) break;

                        string message = chatReq.Message.Trim();
                        string? recipient = string.IsNullOrWhiteSpace(chatReq.Recipient) ? null : chatReq.Recipient.Trim();

                        // Broadcast: nessun destinatario specificato (il messaggio è già delimitato da virgolette)
                        if (recipient == null)
                        {
                            var broadcast = NetworkPacket.Create(OpCode.S2C_ChatMessage, new ChatPayload
                            {
                                Sender = client.Player.Username,
                                Message = message,
                                Recipient = null
                            });

                            foreach (var c in _connectedClients.Values.Where(c => c.IsAuthenticated))
                            {
                                try { await c.SendPacketAsync(broadcast); } catch { }
                            }

                            Console.WriteLine($"[CHAT] {client.Player.Username} (broadcast): {message}");
                            break;
                        }

                        // Privato esplicito: il destinatario è stato scritto fuori dalle virgolette
                        var target = _connectedClients.Values.FirstOrDefault(c =>
                            c.Id != client.Id &&
                            c.IsAuthenticated &&
                            c.Player.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase));

                        if (target == null)
                        {
                            // Nessun fallback a broadcast: un privato esplicito non deve "sfuggire" in chat pubblica
                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Destinatario '{recipient}' non trovato o non online." }));
                            Console.WriteLine($"[CHAT] {client.Player.Username} -> '{recipient}': destinatario non trovato.");
                            break;
                        }

                        var priv = NetworkPacket.Create(OpCode.S2C_ChatMessage, new ChatPayload
                        {
                            Sender = client.Player.Username,
                            Message = message,
                            Recipient = target.Player.Username
                        });

                        await client.SendPacketAsync(priv);
                        await target.SendPacketAsync(priv);

                        Console.WriteLine($"[CHAT] {client.Player.Username} -> {target.Player.Username} (privato): {message}");
                    }
                    break;

                case OpCode.C2S_GetSkills:
                    {
                        var skillListPayload = new SkillListResponsePayload
                        {
                            Character = client.Player.Character.Handle,
                            Skills = client.Player.Character.Skills
                        };

                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_SkillListResponse, skillListPayload));
                    }
                    break;

                case OpCode.C2S_GetStatus:
                    {
                        // Reinvia lo stato aggiornato del personaggio al client che lo ha richiesto
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new CharacterStatePayload
                        {
                            Character = client.Player.Character
                        }));
                    }
                    break;

                case OpCode.C2S_GetPlayerList:
                    {
                        // Elenco dei giocatori autenticati e online
                        var players = _connectedClients.Values
                            .Where(c => c.IsAuthenticated)
                            .Select(c => new PlayerInfoPayload
                            {
                                Handle = c.Player.Username,
                                Role = c.Player.Role.ToString()
                            })
                            .OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        Console.WriteLine($"[LIST] {client.Player.Username} richiede l'elenco: {players.Count} online.");

                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerList, new PlayerListResponsePayload
                        {
                            Players = players
                        }));
                    }
                    break;

                case OpCode.C2S_SkillCheck:
                    {
                        var skillReq = packet.DeserializePayload<SkillCheckRequestPayload>();
                        if (skillReq != null && !string.IsNullOrWhiteSpace(skillReq.SkillName))
                        {
                            string skillName = skillReq.SkillName.Trim();
                            string statName = Skills.GetStatForSkill(skillName);

                            int statVal = client.Player.Character.Stats.GetStatValue(statName);
                            int skillLvl = client.Player.Character.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
                            int penalty = client.Player.Character.Health.GetWoundPenalty();

                            var roll = GameDice.Roll("1d10");
                            int diceVal = (int)roll.Total;
                            int totalResult = statVal + skillLvl + penalty + diceVal;
                            bool isSuccess = totalResult >= skillReq.Difficulty;

                            string outcome = isSuccess ? "SUCCESSO" : "FALLIMENTO";
                            Console.WriteLine($"[SKILL] {client.Player.Character.Handle} prova '{skillName}' ({statName}:{statVal} + Lvl:{skillLvl} + d10:{diceVal} - Pen:{Math.Abs(penalty)}) = {totalResult} vs TN {skillReq.Difficulty} -> {outcome}");

                            var respPayload = new SkillCheckResponsePayload
                            {
                                CharacterHandle = client.Player.Character.Handle,
                                SkillName = skillName,
                                StatName = statName,
                                StatValue = statVal,
                                SkillLevel = skillLvl,
                                DiceRoll = diceVal,
                                TotalResult = totalResult,
                                TargetNumber = skillReq.Difficulty,
                                IsSuccess = isSuccess,
                                Message = $"{client.Player.Character.Handle} effettua una prova di {skillName} ({statName}): {totalResult} vs TN {skillReq.Difficulty} -> {outcome}!"
                            };

                            // Notifica l'esito a tutti i client
                            await BroadcastPacketAsync(NetworkPacket.Create(OpCode.S2C_SkillCheckResult, respPayload));
                        }
                    }
                    break;

                case OpCode.C2S_Move:
                    {
                        var moveReq = packet.DeserializePayload<MoveRequestPayload>();
                        if (moveReq?.Position != null)
                        {
                            var requested = moveReq.Position;
                            GameMap.MovementResult result = _gameMap.GetMovementResult(requested);

                            string message;
                            if (result == GameMap.MovementResult.Success)
                            {
                                // Update della posizione autoritativa sul server
                                client.Player.Character.Position = requested;

                                // Salvataggio della persistenza su JSON
                                CharacterLoader.SaveCharacter(client.Player.Character);

                                message = $"Posizione aggiornata a ({requested.X}, {requested.Y}, {requested.Z})";
                                Console.WriteLine($"[MOVE] {client.Player.Character.Handle} si è spostato a ({requested.X}, {requested.Y}, {requested.Z})");
                            }
                            else
                            {
                                // Posizione non raggiungibile: lo stato non cambia, la posizione effettiva resta quella attuale
                                message = result == GameMap.MovementResult.OutOfBounds
                                    ? $"Movimento bloccato: ({requested.X}, {requested.Y}, {requested.Z}) è fuori dai confini della mappa."
                                    : $"Movimento bloccato: c'è un ostacolo in ({requested.X}, {requested.Y}, {requested.Z}).";
                                Console.WriteLine($"[MOVE] {client.Player.Character.Handle} movimento negato ({result}) verso ({requested.X}, {requested.Y}, {requested.Z})");
                            }

                            var movePayload = new MoveResultPayload
                            {
                                PlayerHandle = client.Player.Character.Handle,
                                RequestedPosition = requested,
                                ActualPosition = client.Player.Character.Position,
                                Result = result,
                                Message = message
                            };

                            // Notifica dell'esito al client che si è mosso (posizione effettiva + valore MovementResult)
                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload));

                            // Broadcast del movimento agli altri client connessi solo se è riuscito
                            if (result == GameMap.MovementResult.Success)
                            {
                                await BroadcastPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload), excludeClientId: client.Id);
                            }
                        }
                    }
                    break;

                case OpCode.C2S_Link:
                    {
                        var linkReq = packet.DeserializePayload<LinkRequestPayload>();

                        if(linkReq != null 
                            && !string.IsNullOrWhiteSpace(linkReq.PlayerHandle) 
                            && !string.IsNullOrWhiteSpace(linkReq.CharacterHandle))
                        {
                            client.Player.Character = CharacterLoader.LoadOrGenerateCharacter(linkReq.CharacterHandle);
                            client.Player.Character.ClientId = client.Id.ToString();
                            client.Player.Role = Player.RoleType.player;
                            
                            CharacterLoader.SaveCharacter(client.Player.Character);

                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
                            {
                                Success = true,
                                Message = $"Personaggio '{linkReq.CharacterHandle}' associato a '{linkReq.PlayerHandle}'.",
                                Role = client.Player.Role,
                                PlayerHandle = client.Player.Username,
                            }));

                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new CharacterStatePayload
                            {
                                Character = client.Player.Character
                            }));
                        }
                        else
                        {
                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
                            {
                                Success = false,
                                Message = "Richiesta di link non valida."
                            }));
                        }
                    }
                    break;

                case OpCode.C2S_Join:
                    {
                        var joinReq = packet.DeserializePayload<JoinRequestPayload>();
                        if (joinReq != null && !string.IsNullOrWhiteSpace(joinReq.GameName))
                        {
                            string denialMessage = string.Empty;
                            bool success = false;
                            var game = _games.FirstOrDefault(g => g.Name.Equals(joinReq.GameName, StringComparison.OrdinalIgnoreCase));

                            if (joinReq.Role.ToLower().Equals("master"))   //TODO: utiizzare enum RoleType invece di stringa per il controllo del ruolo
                            {
                                if (game == null)
                                {
                                    game = new Game
                                    {
                                        Name = joinReq.GameName,
                                        Description = joinReq.Description ?? "Partita senza descrizione",
                                    };
                                    _games.Add(game);
                                    success = true;
                                }
                                else
                                {
                                    bool masterExists = game.Players.Any(p => p.Role == Player.RoleType.master);

                                    if (masterExists)
                                    {
                                        denialMessage = masterExists ? $"Il gioco '{joinReq.GameName}' ha già un master." : string.Empty;
                                        success = false;
                                    }
                                    else
                                        success = true;
                                }
                            }
                            else
                            {
                                if (game == null)
                                {
                                    denialMessage = $"Il gioco '{joinReq.GameName}' non esiste. Solo un master può creare un nuovo gioco.";
                                    success = false;
                                }
                                else
                                {
                                    bool playerExists = game.Players.Any(p => p == client.Player);

                                    if (playerExists)
                                    {
                                        denialMessage = $"Sei già nel gioco '{joinReq.GameName}'.";
                                        success = false;
                                    }
                                    else
                                        success = true;
                                }
                            }

                            if (success)
                            {
                                game.Players.Add(client.Player);
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_JoinResponse, new JoinResponsePayload
                                {
                                    Success = true,
                                    Message = $"Sei entrato nel gioco '{joinReq.GameName}' come {joinReq.Role}.",
                                    GameName = joinReq.GameName,
                                    Role = joinReq.Role
                                }));
                            }
                            else
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_JoinResponse, new JoinResponsePayload
                                {
                                    Success = false,
                                    Message = denialMessage,
                                    GameName = joinReq.GameName,
                                    Role = joinReq.Role
                                }));
                            }
                        }
                    }
                    break;

                default:
                    Console.WriteLine($"[SERVER] OpCode sconosciuto ricevuto: {packet.OpCode}");
                    break;
            }
        }
        private async Task BroadcastPacketAsync(NetworkPacket packet, Guid? excludeClientId = null)
        {
            foreach (var client in _connectedClients.Values)
            {
                if (excludeClientId.HasValue && client.Id == excludeClientId.Value) continue;
                try
                {
                    await client.SendPacketAsync(packet);
                }
                catch
                {
                    // Ignora errori sui singoli client in broadcast
                }
            }
        }
    }
}