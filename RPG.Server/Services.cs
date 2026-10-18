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
    public static class CharacterLoader
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static PlayerState LoadOrGenerateCharacter(string handle, string directoryPath = "Data/Characters")
        {
            // Risolviamo i percorsi per avere chiarezza nei log
            string absoluteDir = Path.GetFullPath(directoryPath);
            Directory.CreateDirectory(absoluteDir);

            string userFilePath = Path.Combine(absoluteDir, $"{handle}.json");
            Console.WriteLine($"[CHAR] Ricerca scheda utente per '{handle}' in: {userFilePath}");

            // 1. Controllo se esiste già il salvataggio specifico dell'utente
            if (File.Exists(userFilePath))
            {
                try
                {
                    string json = File.ReadAllText(userFilePath);
                    var character = JsonSerializer.Deserialize<PlayerState>(json);
                    if (character != null)
                    {
                        Console.WriteLine($"[CHAR] -> SUCCESSO: Scheda di '{handle}' caricata dal file di salvataggio esistente.");
                        return character;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAR] -> ERRORE nella lettura della scheda '{userFilePath}': {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[CHAR] -> Nessun salvataggio precedente trovato per '{handle}'. Procedo alla creazione.");
            }

            // 2. Cerchiamo il template di default
            string defaultTemplatePath = "Data/DefaultCharacter.json";
            string absoluteTemplatePath = Path.GetFullPath(defaultTemplatePath);
            PlayerState newChar;

            Console.WriteLine($"[CHAR] Ricerca template di default in: {absoluteTemplatePath}");

            if (File.Exists(defaultTemplatePath))
            {
                try
                {
                    string templateJson = File.ReadAllText(defaultTemplatePath);
                    newChar = JsonSerializer.Deserialize<PlayerState>(templateJson) ?? new PlayerState();
                    Console.WriteLine($"[CHAR] -> SUCCESSO: Creato nuovo personaggio basato sul template '{defaultTemplatePath}'.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAR] -> ERRORE lettura template '{defaultTemplatePath}': {ex.Message}. Utilizzo generazione dadi.");
                    newChar = GenerateRandomCharacterWithDice();
                }
            }
            else
            {
                Console.WriteLine($"[CHAR] -> Template '{defaultTemplatePath}' non trovato. Generazione statistiche casuali con dadi.");
                newChar = GenerateRandomCharacterWithDice();
            }

            // Inizializziamo/sovrascriviamo l'handle e calcoliamo i punti ferita
            newChar.Handle = handle;
            newChar.Health.MaxHealth = newChar.Stats.BODY * 5;
            newChar.Health.CurrentHealth = newChar.Health.MaxHealth;

            // 3. Salvataggio forzato e persistente su disco del nuovo personaggio
            SaveCharacter(newChar, directoryPath);

            return newChar;
        }

        public static void SaveCharacter(PlayerState character, string directoryPath = "Data/Characters")
        {
            try
            {
                string absoluteDir = Path.GetFullPath(directoryPath);
                Directory.CreateDirectory(absoluteDir);

                string filePath = Path.Combine(absoluteDir, $"{character.Handle}.json");
                string json = JsonSerializer.Serialize(character, JsonOpts);

                File.WriteAllText(filePath, json);
                Console.WriteLine($"[CHAR] -> PERSISTENZA: Scheda di '{character.Handle}' salvata con successo in '{filePath}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAR] -> ERRORE CRITICO salvataggio scheda di {character.Handle}: {ex.Message}");
            }
        }

        private static PlayerState GenerateRandomCharacterWithDice()
        {
            var character = new PlayerState();
            character.Stats.INT = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.REF = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.TECH = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.COOL = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.ATTR = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.MA = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.BODY = (int)GameDice.Roll("2d4+2").Total;
            character.Stats.EMP = (int)GameDice.Roll("2d4+2").Total;

            character.Skills["Pistola"] = 2;
            character.Skills["ArtiMarziali"] = 2;
            character.Skills["Schivare"] = 2;
            character.Skills["Streetwise"] = 1;

            // Arma 1: Pistola da Fuoco
            var pistol = new Weapon
            {
                Id = "sternmeyer_35",
                Name = "Sternmeyer 35",
                Type = WeaponType.Ranged,
                SkillName = "Pistola",
                DamageDice = "2d6",
                OptimalRangeMeters = 50
            };

            // Arma 2: Corpo a corpo / Arti Marziali
            var unarmed = new Weapon
            {
                Id = "unarmed",
                Name = "ArtiMarziali",
                Type = WeaponType.Melee,
                SkillName = "ArtiMarziali",
                DamageDice = "1d6+2",
                OptimalRangeMeters = 2
            };

            character.Weapons.Add(pistol);
            character.Weapons.Add(unarmed);

            // Di default parte con la pistola in pugno
            character.EquippedWeapon = pistol;

            return character;
        }
    }

    public class ClientSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string IdShort => Id.ToString().Substring(0, 6);
        public TcpClient TcpClient { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public PlayerState Character { get; set; } = new();

        public ClientSession(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
            var stream = tcpClient.GetStream();
            Reader = new StreamReader(stream);
            Writer = new StreamWriter(stream) { AutoFlush = true };
        }

        public async Task SendPacketAsync(NetworkPacket packet)
        {
            string json = JsonSerializer.Serialize(packet);
            await Writer.WriteLineAsync(json);
        }
    }

    public class RpgTcpServer
    {
        private readonly IPAddress _ipAddress;
        private readonly int _port;
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<Guid, ClientSession> _connectedClients = new();
        private bool _isRunning;
        public RpgTcpServer(string ipAddress, int port)
        {
            // Supporto sia per 0.0.0.0 (tutte le interfacce) che per IP specifici (es. 127.0.0.1)
            _ipAddress = ipAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(ipAddress);
            _port = port;
        }
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

                            if (weaponToUse.Type == WeaponType.Ranged)
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