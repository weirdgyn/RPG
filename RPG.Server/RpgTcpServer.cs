using RPG.Common;
using RPG.Common.Model;
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using RPG.Common.Utils;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server.Services
{
    public class RpgTcpServer(string ipAddress, int port)
    {
        private const string DefaultListenIpAddr = "0.0.0.0";
        private readonly IPAddress _ipAddress = ipAddress == DefaultListenIpAddr ? IPAddress.Any : IPAddress.Parse(ipAddress);
        private readonly int _port = port;
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<Guid, ClientSession> _connectedClients = new();
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

                string handleName = !string.IsNullOrEmpty(client.Character?.Handle) ? client.Character.Handle : client.IdShort;
                Console.WriteLine($"[SERVER] Client disconnesso: {handleName}");

                // Notifica gli altri utenti nella stanza
                var leaveNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{handleName} ha abbandonato la rete." });
                await BroadcastPacketAsync(leaveNotify, excludeClientId: client.Id);
            }
        }
        private async Task ProcessPacketAsync(ClientSession client, NetworkPacket packet)
        {
            switch (packet.OpCode)
            {
                case OpCode.C2S_Login:
                    {
                        var loginReq = packet.DeserializePayload<LoginRequestPayload>();
                        string desiredHandle = loginReq?.Username?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(desiredHandle))
                        {
                            desiredHandle = $"Punk_{client.IdShort}";
                        }

                        // Caricamento o generazione autoritativa tramite CharacterLoader e file JSON
                        client.Character = CharacterLoader.LoadOrGenerateCharacter(desiredHandle);
                        client.Character.PlayerId = client.Id.ToString();

                        Console.WriteLine($"[LOGIN] Client {client.IdShort} autenticato come '{client.Character.Handle}' (HP: {client.Character.Health.CurrentHealth}/{client.Character.Health.MaxHealth})");

                        // Risposta di Login OK
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LoginResponse, new LoginResponsePayload
                        {
                            Success = true,
                            PlayerId = client.Character.PlayerId,
                            Message = $"Benvenuto a Night City, {client.Character.Handle}!"
                        }));

                        // Invio stato aggiornato del personaggio al client
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new PlayerStatePayload
                        {
                            Character = client.Character
                        }));

                        // Notifica globale agli altri
                        var joinNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Character.Handle} è entrato nella rete." });
                        await BroadcastPacketAsync(joinNotify, excludeClientId: client.Id);
                    }
                    break;

                case OpCode.C2S_Attack:
                    {
                        var attackReq = packet.DeserializePayload<AttackRequestPayload>();
                        if (attackReq != null)
                        {
                            var targetClient = _connectedClients.Values
                                .FirstOrDefault(c => c.Character.Handle.Equals(attackReq.TargetHandle, StringComparison.OrdinalIgnoreCase));

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
                                weaponToUse = client.Character.Weapons.FirstOrDefault(w =>
                                    w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                    w.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                    w.SkillName.Equals(key, StringComparison.OrdinalIgnoreCase));
                            }

                            // Fallback sull'arma equipaggiata o sulla prima disponibile
                            weaponToUse ??= client.Character.EquippedWeapon ?? client.Character.Weapons.FirstOrDefault();

                            if (weaponToUse == null)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Nessuna arma o abilità d'attacco disponibile!" }));
                                break;
                            }

                            // Statistica e abilità dell'attaccante
                            string skillName = weaponToUse.SkillName;
                            string statName = Skills.GetStatForSkill(skillName);

                            int attackerStat = client.Character.Stats.GetStatValue(statName);
                            int attackerSkillLvl = client.Character.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
                            int attackerPenalty = client.Character.Health.GetWoundPenalty();

                            if (attackerPenalty <= -99)
                            {
                                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Sei K.O., non puoi attaccare!" }));
                                break;
                            }

                            // Calcolo distanza e difficoltà (TN)
                            double distance = CombatRules.CalculateDistance(client.Character.Position, targetClient.Character.Position);
                            int targetNumber = 15;
                            string attackTypeDesc = "";

                            if (weaponToUse.Type == Weapon.WeaponType.Ranged)
                            {
                                targetNumber = CombatRules.GetRangedTargetNumber(distance);
                                attackTypeDesc = $"A Distanza con {weaponToUse.Name} ({distance:F1}m) -> TN Fisso: {targetNumber}";
                            }
                            else // Melee
                            {
                                if (distance > 2.5)
                                {
                                    await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Troppo lontano per la mischia con {weaponToUse.Name}! Distanza: {distance:F1}m" }));
                                    break;
                                }

                                int targetRef = targetClient.Character.Stats.REF;
                                int targetDodge = targetClient.Character.Skills.TryGetValue("Schivare", out int dLvl) ? dLvl : 0;
                                int targetPenalty = targetClient.Character.Health.GetWoundPenalty();

                                targetNumber = 10 + targetRef + targetDodge + targetPenalty;
                                attackTypeDesc = $"In Mischia con {weaponToUse.Name} -> Difesa Bersaglio (REF {targetRef} + Schivare {targetDodge}): TN {targetNumber}";
                            }

                            // Tiro d10 d'attacco
                            var roll = GameDice.Roll("1d10");
                            int diceVal = (int)roll.Total;
                            int attackTotal = attackerStat + attackerSkillLvl + attackerPenalty + diceVal;

                            bool isHit = attackTotal >= targetNumber;
                            int damageDealt = 0;

                            if (isHit)
                            {
                                var damageRoll = GameDice.Roll(weaponToUse.DamageDice);
                                damageDealt = (int)damageRoll.Total;

                                targetClient.Character.Health.CurrentHealth = Math.Max(0, targetClient.Character.Health.CurrentHealth - damageDealt);
                                CharacterLoader.SaveCharacter(targetClient.Character); // Persistenza su JSON del bersaglio
                            }

                            Console.WriteLine($"[COMBAT] {client.Character.Handle} attacca {targetClient.Character.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. Tiro: {attackTotal} vs TN {targetNumber} -> {(isHit ? $"COLPITO ({damageDealt} dmg)" : "MANCATO")}");

                            // Invio esito a entrambi
                            var respPayload = new AttackResponsePayload
                            {
                                AttackerHandle = client.Character.Handle,
                                TargetHandle = targetClient.Character.Handle,
                                AttackTotal = attackTotal,
                                DefenseTN = targetNumber,
                                IsHit = isHit,
                                DamageDealt = damageDealt,
                                TargetRemainingHealth = targetClient.Character.Health.CurrentHealth,
                                TargetWoundStatus = targetClient.Character.Health.GetWoundStatusDescription(),
                                Message = isHit
                                    ? $"COLPO A SEGNO! {client.Character.Handle} usa '{weaponToUse.Name}' e infligge {damageDealt} danni ({weaponToUse.DamageDice})!"
                                    : $"MANCATO! {client.Character.Handle} fallisce l'attacco con '{weaponToUse.Name}'."
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

                        Console.WriteLine($"[ROLL] {client.Character.Handle} tira {expr} ({reason}): {total}");

                        var resp = new RollResponsePayload
                        {
                            RollerHandle = client.Character.Handle,
                            DiceExpression = expr,
                            TotalResult = total,
                            Message = $"{client.Character.Handle} tira [{expr}] ({reason}) ed ottiene: {total}"
                        };

                        var packetResp = NetworkPacket.Create(OpCode.S2C_RollResult, resp);
                        // Notifica a tutti i client nella stanza (inclusi gli altri)
                        await BroadcastPacketAsync(packetResp);
                    }
                    break;

                case OpCode.C2S_GetSkills:
                    {
                        var skillListPayload = new SkillListResponsePayload
                        {
                            Character = client.Character.Handle,
                            Skills = client.Character.Skills
                        };

                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_SkillListResponse, skillListPayload));
                    }
                    break;

                case OpCode.C2S_GetStatus:
                    {
                        // Reinvia lo stato aggiornato del personaggio al client che lo ha richiesto
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new PlayerStatePayload
                        {
                            Character = client.Character
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

                            int statVal = client.Character.Stats.GetStatValue(statName);
                            int skillLvl = client.Character.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
                            int penalty = client.Character.Health.GetWoundPenalty();

                            var roll = GameDice.Roll("1d10");
                            int diceVal = (int)roll.Total;
                            int totalResult = statVal + skillLvl + penalty + diceVal;
                            bool isSuccess = totalResult >= skillReq.Difficulty;

                            string outcome = isSuccess ? "SUCCESSO" : "FALLIMENTO";
                            Console.WriteLine($"[SKILL] {client.Character.Handle} prova '{skillName}' ({statName}:{statVal} + Lvl:{skillLvl} + d10:{diceVal} - Pen:{Math.Abs(penalty)}) = {totalResult} vs TN {skillReq.Difficulty} -> {outcome}");

                            var respPayload = new SkillCheckResponsePayload
                            {
                                CharacterHandle = client.Character.Handle,
                                SkillName = skillName,
                                StatName = statName,
                                StatValue = statVal,
                                SkillLevel = skillLvl,
                                DiceRoll = diceVal,
                                TotalResult = totalResult,
                                TargetNumber = skillReq.Difficulty,
                                IsSuccess = isSuccess,
                                Message = $"{client.Character.Handle} effettua una prova di {skillName} ({statName}): {totalResult} vs TN {skillReq.Difficulty} -> {outcome}!"
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
                            // Update della posizione autoritativa sul server
                            client.Character.Position = moveReq.Position;

                            // Salvataggio della persistenza su JSON
                            CharacterLoader.SaveCharacter(client.Character);

                            Console.WriteLine($"[MOVE] {client.Character.Handle} si è spostato a ({client.Character.Position.X:F1}, {client.Character.Position.Y:F1}, {client.Character.Position.Z:F1})");

                            // Notifica di conferma al client che si è mosso
                            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
                            {
                                Message = $"Posizione aggiornata a ({client.Character.Position.X:F1}, {client.Character.Position.Y:F1}, {client.Character.Position.Z:F1})"
                            }));

                            // Broadcast del movimento agli altri client connessi
                            var moveBroadcast = NetworkPacket.Create(OpCode.S2C_Notification, new
                            {
                                Message = $"{client.Character.Handle} si è spostato in posizione ({client.Character.Position.X:F1}, {client.Character.Position.Y:F1}, {client.Character.Position.Z:F1})"
                            });
                            await BroadcastPacketAsync(moveBroadcast, excludeClientId: client.Id);
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