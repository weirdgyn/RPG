using RPG.Common;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.Utils;
using RPG.Common.World;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server;

public class RpgTcpServer(string ipAddress, int port)
{
    private readonly IPAddress _ipAddress = ipAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(ipAddress);
    private readonly int _port = port;
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<Guid, ClientSession> _connectedClients = new();
    private readonly List<Game> _games = [];
    // Protegge _games e Game.Players: vengono modificati da più task (join, disconnessioni).
    // Gli await non sono ammessi dentro il lock: si copia ciò che serve e si invia fuori.
    private readonly object _gamesLock = new();
    // Serializza le operazioni di login per rendere atomico il controllo sull'unicità dell'handle
    private readonly object _loginLock = new();
    // Rende atomico "personaggio già in uso? / carica / associa alla sessione" in /link
    private readonly object _linkLock = new();
    // Serializza /create: controllo "esiste già", conteggio e scrittura devono essere un'unica operazione.
    private readonly object _createLock = new();
    private const int MaxCharactersPerSession = 3;
    private const int MaxCharactersTotal = 500;
    private bool _isRunning;

    public async Task StartAsync()
    {
        _listener = new TcpListener(_ipAddress, _port);
        _listener.Start();
        _isRunning = true;

        Console.WriteLine($"[SERVER] In ascolto su {_ipAddress}:{_port}...");

        while (_isRunning)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();

                if (_connectedClients.Count >= InputLimits.MaxConnections)
                {
                    Console.WriteLine($"[SERVER] Connessione rifiutata: raggiunto il limite di {InputLimits.MaxConnections} client.");
                    tcpClient.Close();
                    continue;
                }

                var session = new ClientSession(tcpClient);
                _connectedClients[session.Id] = session;

                Console.WriteLine($"[SERVER] Nuovo client connesso [{session.IdShort}] da {tcpClient.Client.RemoteEndPoint}");

                _ = Task.Run(() => HandleClientAsync(session));
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[SERVER] Errore nell'accettazione del client: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(ClientSession client)
    {
        try
        {
            string? line;
            while ((line = await client.ReadLineLimitedAsync(InputLimits.MaxLineLength)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                NetworkPacket? packet;
                try
                {
                    packet = JsonSerializer.Deserialize<NetworkPacket>(line);
                }
                catch (JsonException)
                {
                    // Riga non valida: la scarto senza chiudere la sessione
                    Console.WriteLine($"[SERVER] Pacchetto malformato da [{client.IdShort}] ignorato.");
                    continue;
                }
                if (packet == null) continue;

                try
                {
                    await ProcessPacketAsync(client, packet);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && client.TcpClient.Connected)
                {
                    // Un comando che fallisce non deve chiudere la sessione del giocatore: lo registro,
                    // avviso il client e passo al comando successivo. Se è la connessione stessa a essere
                    // caduta, il prossimo ReadLineAsync lancerà e il ciclo uscirà come prima.
                    Console.WriteLine($"[SERVER] Errore nel comando {packet.OpCode} di [{client.IdShort}]: {ex.GetType().Name}: {ex.Message}");
                    try
                    {
                        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
                        {
                            Message = "Errore del server nell'eseguire il comando."
                        }));
                    }
                    catch { /* connessione ormai persa */ }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVER] Connessione interrotta con il client [{client.IdShort}]: {ex.Message}");
        }
        finally
        {
            _connectedClients.TryRemove(client.Id, out _);
            client.TcpClient.Close();
            RemovePlayerFromGames(client.Player);

            string userName = !string.IsNullOrEmpty(client.Player.Username) ? client.Player.Username : client.IdShort;
            Console.WriteLine($"[SERVER] Client disconnesso: {userName}");

            if (client.IsAuthenticated)
            {
                var leaveNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{userName} ha abbandonato la rete." });
                await BroadcastPacketAsync(leaveNotify, excludeClientId: client.Id);
            }
        }
    }

    private async Task ProcessPacketAsync(ClientSession client, NetworkPacket packet)
    {
        if (packet.OpCode != OpCode.C2S_Login && packet.OpCode != OpCode.C2S_Ping && !client.IsAuthenticated)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi effettuare il login prima di poter usare i comandi." }));
            return;
        }

        switch (packet.OpCode)
        {
            case OpCode.C2S_Ping:
                await HandlePingAsync(client);
                break;
            case OpCode.C2S_Login:
                await HandleLoginAsync(client, packet);
                break;
            case OpCode.C2S_Attack:
                await HandleAttackAsync(client, packet);
                break;
            case OpCode.C2S_Roll:
                await HandleRollAsync(client, packet);
                break;
            case OpCode.C2S_Chat:
                await HandleChatAsync(client, packet);
                break;
            case OpCode.C2S_GetSkills:
                await HandleGetSkillsAsync(client);
                break;
            case OpCode.C2S_GetStatus:
                await HandleGetStatusAsync(client);
                break;
            case OpCode.C2S_GetPlayers:
                await HandleGetPlayersAsync(client, packet);
                break;
            case OpCode.C2S_CreateCharacter:
                await HandleCreateCharacterAsync(client, packet);
                break;
            case OpCode.C2S_SkillCheck:
                await HandleSkillCheckAsync(client, packet);
                break;
            case OpCode.C2S_Move:
                await HandleMoveAsync(client, packet);
                break;
            case OpCode.C2S_Link:
                await HandleLinkAsync(client, packet);
                break;
            case OpCode.C2S_Join:
                await HandleJoinAsync(client, packet);
                break;
            case OpCode.C2S_Leave:
                await HandleLeaveAsync(client);
                break;
            case OpCode.C2S_Init:
                await HandleInitAsync(client, packet);
                break;
            case OpCode.C2S_GetCharacters:
                await HandleGetCharactersAsync(client);
                break;
            case OpCode.C2S_GetGames:
                await HandleGetGamesAsync(client);
                break;
            default:
                Console.WriteLine($"[SERVER] OpCode sconosciuto ricevuto: {packet.OpCode}");
                break;
        }
    }

    private async Task HandlePingAsync(ClientSession client)
    {
        var pong = new PongPayload
        {
            Message = $"Pong! ({DateTime.UtcNow:HH:mm:ss} UTC)"
        };
        Console.WriteLine($"[PING] Risposta Pong inviata a '{client.Player.Username}'.");
        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Pong, pong));
    }

    private async Task HandleLoginAsync(ClientSession client, NetworkPacket packet)
    {
        if (client.IsAuthenticated)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = $"Sei già autenticato come '{client.Player.Username}'."
            }));
            return;
        }

        var loginReq = packet.DeserializePayload<LoginRequestPayload>();
        string userName = loginReq?.PlayerName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(userName))
        {
            userName = $"Punk_{client.IdShort}";
        }

        LoginResponsePayload? denial = null;

        if (userName.Length > InputLimits.MaxUsernameLength || InputLimits.HasControlChars(userName))
        {
            denial = new LoginResponsePayload
            {
                Success = false,
                PlayerId = string.Empty,
                PlayerName = string.Empty,
                Message = $"Username non valido: massimo {InputLimits.MaxUsernameLength} caratteri e nessun carattere di controllo."
            };
        }

        lock (_loginLock)
        {
            bool duplicate = denial == null && _connectedClients.Values.Any(c =>
                c.Id != client.Id &&
                !string.IsNullOrEmpty(c.Player.Username) &&
                c.Player.Username.Equals(userName, StringComparison.OrdinalIgnoreCase));

            if (denial != null)
            {
                Console.WriteLine($"[LOGIN] RIFIUTATO: username non valido da [{client.IdShort}].");
            }
            else if (duplicate)
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
                client.Player.Username = userName;
                client.IsAuthenticated = true;
            }
        }

        if (denial != null)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LoginResponse, denial));
            return;
        }

        Console.WriteLine($"[LOGIN] Client {client.IdShort} autenticato come '{userName}'");

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LoginResponse, new LoginResponsePayload
        {
            Success = true,
            PlayerName = client.Player.Username,
            Message = $"Benvenuto a Night City, {client.Player.Username}!"
        }));

        var joinNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{client.Player.Username} è entrato nella rete." });
        await BroadcastPacketAsync(joinNotify, excludeClientId: client.Id);
    }

    // Serializza la risoluzione degli attacchi: lettura e modifica dei punti ferita di attaccante e
    // bersaglio avvengono sotto questo lock, altrimenti due attacchi simultanei sullo stesso bersaglio
    // possono perdere un colpo (lettura, calcolo e scrittura non atomici).
    private readonly object _combatLock = new();

    /// <summary>Un partecipante al combattimento: il personaggio e la sua sessione (se è un giocatore).</summary>
    private sealed record Combatant(Character Character, ClientSession? Session);

    private sealed record AttackOutcome(string? Error, AttackResponsePayload? Result, string? LogLine);

    // Da chiamare sotto _gamesLock. Cerca il personaggio per handle tra i partecipanti del game.
    private Combatant? FindCombatantInGame(Game game, string handle)
    {
        foreach (var p in game.Players)
        {
            if (p.Character != null && p.Character.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase))
            {
                var session = _connectedClients.Values.FirstOrDefault(s => s.IsAuthenticated && ReferenceEquals(s.Player, p));
                return new Combatant(p.Character, session);
            }
        }

        return null;
    }

    // Da chiamare sotto _gamesLock. Sessioni dei partecipanti del game.
    private List<ClientSession> GetGameSessions(Game game)
    {
        return game.Players
            .Select(p => _connectedClients.Values.FirstOrDefault(s => ReferenceEquals(s.Player, p)))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();
    }

    private async Task HandleAttackAsync(ClientSession client, NetworkPacket packet)
    {
        var attackReq = packet.DeserializePayload<AttackRequestPayload>();
        if (attackReq == null) return;
        attackReq.TargetHandle = InputLimits.Clean(attackReq.TargetHandle, InputLimits.MaxNameFieldLength);
        attackReq.WeaponOrSkill = InputLimits.Clean(attackReq.WeaponOrSkill, InputLimits.MaxNameFieldLength);

        if (!await EnsureCharacterLinkedAsync(client)) return;

        await ResolveAttackAsync(client, client.Player.Character!, attackReq);
    }

    /// <summary>
    /// Attacco di <paramref name="attacker"/> (il personaggio che agisce) contro un personaggio dello stesso
    /// game. <paramref name="reporter"/> è la sessione che ha dato il comando e riceve gli errori.
    /// </summary>
    private async Task ResolveAttackAsync(ClientSession reporter, Character attacker, AttackRequestPayload attackReq)
    {
        Game? game;
        Combatant? target = null;
        List<ClientSession> audience = [];

        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
            if (game != null)
            {
                target = FindCombatantInGame(game, attackReq.TargetHandle);
                audience = GetGameSessions(game);
            }
        }

        if (game == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi essere in un game per attaccare: usa /join <game>." }));
            return;
        }

        if (target == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Bersaglio '{attackReq.TargetHandle}' non trovato in questo game." }));
            return;
        }

        if (ReferenceEquals(target.Character, attacker))
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Non puoi attaccare te stesso!" }));
            return;
        }

        var outcome = ResolveAttackRules(attacker, target.Character, attackReq.WeaponOrSkill);
        if (outcome.Error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = outcome.Error }));
            return;
        }

        Console.WriteLine(outcome.LogLine);

        if (outcome.Result!.IsHit)
        {
            CharacterRepository.TrySaveCharacter(target.Character);
        }

        // Il risultato va a tutto il game (attaccante e bersaglio compresi), non agli altri game
        var attackPacket = NetworkPacket.Create(OpCode.S2C_AttackResult, outcome.Result);
        foreach (var session in audience)
        {
            try { await session.SendPacketAsync(attackPacket); }
            catch (Exception ex) { Console.WriteLine($"[COMBAT] Risultato non consegnato a [{session.IdShort}]: {ex.Message}"); }
        }
    }

    // Regole dell'attacco, sincrone e sotto _combatLock. Nessun I/O di rete qui dentro.
    private AttackOutcome ResolveAttackRules(Character attacker, Character target, string? weaponKey)
    {
        lock (_combatLock)
        {
            Weapon? weaponToUse = null;
            if (!string.IsNullOrWhiteSpace(weaponKey))
            {
                string key = weaponKey.Trim();
                weaponToUse = attacker.Weapons.FirstOrDefault(w =>
                    w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    w.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    w.SkillName.Equals(key, StringComparison.OrdinalIgnoreCase));
            }

            weaponToUse ??= attacker.EquippedWeapon ?? attacker.Weapons.FirstOrDefault();
            if (weaponToUse == null)
            {
                return new AttackOutcome("Nessuna arma o abilità d'attacco disponibile!", null, null);
            }

            string skillName = weaponToUse.SkillName;
            string statName = Skills.GetStatForSkill(skillName);

            int attackerStat = attacker.Stats.GetStatValue(statName);
            int attackerSkillLvl = attacker.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
            int attackerPenalty = attacker.Health.GetWoundPenalty();

            if (attackerPenalty <= -99)
            {
                return new AttackOutcome("Sei K.O., non puoi attaccare!", null, null);
            }

            double distance = CombatRules.CalculateDistance(attacker.Position, target.Position);
            int targetNumber = 15;
            string attackTypeDesc = string.Empty;

            if (weaponToUse.Type == Weapon.WeaponType.Ranged)
            {
                targetNumber = CombatRules.GetRangedTargetNumber(distance);
                attackTypeDesc = $"A Distanza con {weaponToUse.Name} ({distance:F1}m) -> TN Fisso: {targetNumber}";
            }
            else if (weaponToUse.Type == Weapon.WeaponType.Melee)
            {
                if (distance > 2.5)
                {
                    return new AttackOutcome($"Troppo lontano per la mischia con {weaponToUse.Name}! Distanza: {distance:F1}m > 2.5m.", null, null);
                }

                int targetRef = target.Stats.REF;
                int targetDodge = target.Skills.TryGetValue("Schivare", out int dLvl) ? dLvl : 0;
                int targetPenalty = target.Health.GetWoundPenalty();

                targetNumber = 10 + targetRef + targetDodge + targetPenalty;
                attackTypeDesc = $"In Mischia con {weaponToUse.Name} -> Difesa Bersaglio (REF {targetRef} + Schivare {targetDodge}): TN {targetNumber}";
            }

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

                if (weaponToUse.Type == Weapon.WeaponType.Ranged)
                {
                    var loc = CombatRules.ResolveHitLocation((int)GameDice.Roll("1d10").Total);
                    hitLocationName = loc.Name;
                    hitMultiplier = loc.DamageMultiplier;
                }

                damageDealt = rawDamage * hitMultiplier;
                target.Health.CurrentHealth = Math.Max(0, target.Health.CurrentHealth - damageDealt);
            }

            string hitDesc = isHit
                ? hitLocationName.Length > 0
                    ? $"COLPITO ({damageDealt} dmg, Impatto: {hitLocationName}{(hitMultiplier > 1 ? $" x{hitMultiplier}" : "")})"
                    : $"COLPITO ({damageDealt} dmg)"
                : "MANCATO";

            string log = $"[COMBAT] {attacker.Handle} attacca {target.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. Tiro: {attackTotal} vs TN {targetNumber}. {hitDesc}";

            string impactSuffix = hitLocationName.Length > 0
                ? $" Impatto: {hitLocationName}{(hitMultiplier > 1 ? $" (x{hitMultiplier})" : "")}"
                : string.Empty;

            var respPayload = new AttackResponsePayload
            {
                AttackerHandle = attacker.Handle,
                TargetHandle = target.Handle,
                AttackTotal = attackTotal,
                DefenseTN = targetNumber,
                IsHit = isHit,
                DamageDealt = damageDealt,
                HitLocation = hitLocationName,
                DamageMultiplier = hitMultiplier,
                TargetRemainingHealth = target.Health.CurrentHealth,
                TargetWoundStatus = target.Health.GetWoundStatusDescription(),
                Message = isHit
                    ? $"COLPO A SEGNO! {attacker.Handle} usa '{weaponToUse.Name}' e infligge {damageDealt} danni ({weaponToUse.DamageDice})!{impactSuffix}"
                    : $"MANCATO! {attacker.Handle} fallisce l'attacco con '{weaponToUse.Name}'."
            };

            return new AttackOutcome(null, respPayload, log);
        }
    }

    private const int MaxRollReasonLength = 100;

    private async Task HandleRollAsync(ClientSession client, NetworkPacket packet)
    {
        var rollReq = packet.DeserializePayload<RollRequestPayload>();
        string expr = string.IsNullOrWhiteSpace(rollReq?.DiceExpression) ? "1d10" : rollReq.DiceExpression.Trim();

        // Il motivo è testo libero che finisce nel log e nelle notifiche di tutti: tolgo i caratteri di
        // controllo (niente righe finte nel log) e lo limito in lunghezza.
        string reason = new string((rollReq?.Reason ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (reason.Length > MaxRollReasonLength) reason = reason[..MaxRollReasonLength];
        if (reason.Length == 0) reason = "Tiro generico";

        DiceRollResult rollResult;
        try
        {
            rollResult = GameDice.Roll(expr);
        }
        catch (Exception ex) when (ex is Dice.DiceException or ArgumentException)
        {
            // Espressione non valida o oltre i limiti: errore al solo mittente, la sessione resta aperta.
            Console.WriteLine($"[ROLL] {client.Player.Username}: espressione rifiutata ({ex.GetType().Name}).");
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = $"Espressione dadi non valida: {(ex is Dice.DiceException ? ex.Message : "troppo lunga")}. " +
                          $"Esempi: 1d10, 3d6+2, 4d6dl1, 3d6! (max {GameDice.MaxExpressionLength} caratteri, 100 dadi, 1000 facce)."
            }));
            return;
        }

        int total = (int)rollResult.Total;
        string loggedExpr = new string(expr.Where(c => !char.IsControl(c)).ToArray());

        Console.WriteLine($"[ROLL] {client.Player.Username} tira {loggedExpr} ({reason}): {total}");

        var resp = new RollResponsePayload
        {
            RollerHandle = client.Player.Username,
            DiceExpression = expr,
            Reason = reason,
            TotalResult = total,
            Details = rollResult.Details,
            Message = $"{client.Player.Username} tira [{expr}] ({reason}) ed ottiene: {total}"
        };

        var packetResp = NetworkPacket.Create(OpCode.S2C_RollResult, resp);
        await BroadcastToGameAsync(client, packetResp);
    }

    private async Task HandleChatAsync(ClientSession client, NetworkPacket packet)
    {
        var chatReq = packet.DeserializePayload<ChatPayload>();
        if (chatReq == null || string.IsNullOrWhiteSpace(chatReq.Message)) return;

        if (chatReq.Message.Length > InputLimits.MaxChatLength * 2)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = $"Messaggio troppo lungo (massimo {InputLimits.MaxChatLength} caratteri)."
            }));
            return;
        }

        string message = InputLimits.Clean(chatReq.Message, InputLimits.MaxChatLength + 1);
        if (message.Length == 0) return;
        if (message.Length > InputLimits.MaxChatLength)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = $"Messaggio troppo lungo (massimo {InputLimits.MaxChatLength} caratteri)."
            }));
            return;
        }

        string recipientClean = InputLimits.Clean(chatReq.Recipient, InputLimits.MaxUsernameLength);
        string? recipient = recipientClean.Length == 0 ? null : recipientClean;

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
            return;
        }

        var target = _connectedClients.Values.FirstOrDefault(c =>
            c.Id != client.Id &&
            c.IsAuthenticated &&
            c.Player.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"Destinatario '{recipient}' non trovato o non online." }));
            Console.WriteLine($"[CHAT] {client.Player.Username} -> '{recipient}': destinatario non trovato.");
            return;
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

    private async Task HandleGetSkillsAsync(ClientSession client)
    {
        if (client.Player.Character != null)
        {
            await SendSkillsAsync(client, client.Player.Character);
        }
        else
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = "Nessun personaggio associato: usa /link \"<Character>\" prima di richiedere le abilità."
            }));
        }
    }

    // Elenco abilità di <paramref name="character"/> inviato a <paramref name="reporter"/>.
    private static async Task SendSkillsAsync(ClientSession reporter, Character character)
    {
        int penalty = character.Health.GetWoundPenalty();

        var skillInfos = character.Skills
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                string statName = Skills.GetStatForSkill(kv.Key);
                int statVal = character.Stats.GetStatValue(statName);
                return new SkillInfoPayload
                {
                    Name = kv.Key,
                    Level = kv.Value,
                    Stat = statName,
                    StatValue = statVal,
                    BasePool = statVal + kv.Value
                };
            })
            .ToList();

        var skillListPayload = new SkillListResponsePayload
        {
            Character = character.Handle,
            WoundPenalty = penalty,
            Skills = skillInfos
        };

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_SkillListResponse, skillListPayload));
    }

    private async Task HandleGetStatusAsync(ClientSession client)
    {
        if (client.Player.Character != null)
        {
            await SendStatusAsync(client, client.Player.Character);
        }
    }

    // La scheda di <paramref name="character"/> inviata a <paramref name="reporter"/> (chi ha dato il comando).
    private static async Task SendStatusAsync(ClientSession reporter, Character character)
    {
        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new CharacterStatePayload
        {
            Character = character
        }));
    }

    private async Task HandleGetPlayersAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<PlayersRequestPayload>();
        string? filterGame = InputLimits.Clean(req?.GameName, InputLimits.MaxGameNameLength);

        var allAuthenticated = _connectedClients.Values
            .Where(c => c.IsAuthenticated)
            .ToList();

        var playerInfos = new List<PlayerInfoPayload>();

        foreach (var c in allAuthenticated)
        {
            string gameName = "---";
            lock (_gamesLock)
            {
                var g = _games.FirstOrDefault(game => game.Players.Any(p => p.Username.Equals(c.Player.Username, StringComparison.OrdinalIgnoreCase)));
                if (g != null) gameName = g.Name;
            }

            if (!string.IsNullOrWhiteSpace(filterGame))
            {
                if (!gameName.Equals(filterGame, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            playerInfos.Add(new PlayerInfoPayload
            {
                Handle = c.Player.Username,
                Role = c.Player.Role,
                CharacterHandle = c.Player.Character?.Handle ?? "---",
                Game = gameName
            });
        }

        playerInfos = playerInfos.OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"[LIST] {client.Player.Username} richiede l'elenco (Filtro Game: '{filterGame ?? "nessuno"}'): {playerInfos.Count} trovati.");

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Players, new PlayersResponsePayload
        {
            FilterGame = filterGame,
            Players = playerInfos
        }));
    }

    private async Task HandleSkillCheckAsync(ClientSession client, NetworkPacket packet)
    {
        var skillReq = packet.DeserializePayload<SkillCheckRequestPayload>();
        if (skillReq == null || string.IsNullOrWhiteSpace(skillReq.SkillName)) return;

        if (!await EnsureCharacterLinkedAsync(client)) return;

        await ResolveSkillCheckAsync(client, client.Player.Character!, skillReq);
    }

    private async Task ResolveSkillCheckAsync(ClientSession reporter, Character actor, SkillCheckRequestPayload skillReq)
    {
        string skillName = InputLimits.Clean(skillReq.SkillName, InputLimits.MaxNameFieldLength);
        string statName = Skills.GetStatForSkill(skillName);

        int statVal = actor.Stats.GetStatValue(statName);
        int skillLvl = actor.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
        int penalty = actor.Health.GetWoundPenalty();

        var roll = GameDice.Roll("1d10");
        int diceVal = (int)roll.Total;
        int totalResult = statVal + skillLvl + penalty + diceVal;
        bool isSuccess = totalResult >= skillReq.Difficulty;

        string outcome = isSuccess ? "SUCCESSO" : "FALLIMENTO";
        Console.WriteLine($"[SKILL] {actor.Handle} prova '{skillName}' ({statName}:{statVal} + Lvl:{skillLvl} + d10:{diceVal} - Pen:{Math.Abs(penalty)}) = {totalResult} vs TN {skillReq.Difficulty} -> {outcome}");

        var respPayload = new SkillCheckResponsePayload
        {
            CharacterHandle = actor.Handle,
            SkillName = skillName,
            StatName = statName,
            StatValue = statVal,
            SkillLevel = skillLvl,
            DiceRoll = diceVal,
            TotalResult = totalResult,
            TargetNumber = skillReq.Difficulty,
            IsSuccess = isSuccess,
            Message = $"{actor.Handle} effettua una prova di {skillName} ({statName}): {totalResult} vs TN {skillReq.Difficulty} -> {outcome}!"
        };

        await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_SkillCheckResult, respPayload));
    }
    private async Task HandleMoveAsync(ClientSession client, NetworkPacket packet)
    {
        var moveReq = packet.DeserializePayload<MoveRequestPayload>();
        if (moveReq?.Position == null) return;

        if (!await EnsureCharacterLinkedAsync(client)) return;

        await ResolveMoveAsync(client, client.Player.Character!, moveReq);
    }

    private async Task ResolveMoveAsync(ClientSession reporter, Character actor, MoveRequestPayload moveReq)
    {
        GameMap? map;
        lock (_gamesLock)
        {
            map = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player))?.Map;
        }

        if (map == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Non sei in una partita attiva. Non puoi muoverti." }));
            return;
        }

        var requested = moveReq.Position;
        GameMap.MovementResult result = map.GetMovementResult(requested);

        string message;
        if (result == GameMap.MovementResult.Success)
        {
            actor.Position = requested;
            CharacterRepository.TrySaveCharacter(actor);

            message = $"Posizione aggiornata a ({requested.X}, {requested.Y}, {requested.Z})";
            Console.WriteLine($"[MOVE] {actor.Handle} si è spostato a ({requested.X}, {requested.Y}, {requested.Z})");
        }
        else
        {
            message = result == GameMap.MovementResult.OutOfBounds
                ? $"Movimento bloccato: ({requested.X}, {requested.Y}, {requested.Z}) è fuori dai confini della mappa."
                : $"Movimento bloccato: c'è un ostacolo in ({requested.X}, {requested.Y}, {requested.Z}).";
            Console.WriteLine($"[MOVE] {actor.Handle} movimento negato ({result}) verso ({requested.X}, {requested.Y}, {requested.Z})");
        }

        var movePayload = new MoveResultPayload
        {
            PlayerHandle = actor.Handle,
            RequestedPosition = requested,
            ActualPosition = actor.Position,
            Result = result,
            Message = message
        };

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload));

        if (result == GameMap.MovementResult.Success)
        {
            // Solo gli altri partecipanti dello stesso game vedono lo spostamento
            await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload), includeOrigin: false);
        }
    }

    private async Task HandleLinkAsync(ClientSession client, NetworkPacket packet)
    {
        var linkReq = packet.DeserializePayload<LinkRequestPayload>();

        if (linkReq != null
            && !string.IsNullOrWhiteSpace(linkReq.PlayerHandle)
            && !string.IsNullOrWhiteSpace(linkReq.CharacterHandle))
        {
            // Handle non conforme alle regole (compresi i tentativi di path traversal): rifiuto con un
            // messaggio. Senza questo controllo l'ArgumentException del repository arriverebbe fino a
            // HandleClientAsync e chiuderebbe la connessione del giocatore.
            if (!CharacterHandleValidator.IsValid(linkReq.CharacterHandle))
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
                {
                    Success = false,
                    Message = CharacterHandleValidator.RulesDescription
                }));
                return;
            }

            // Un personaggio può essere interpretato da una sola sessione alla volta. Il controllo "già in
            // uso", il caricamento e l'associazione avvengono nello stesso lock: senza, due /link
            // simultanei sullo stesso personaggio passerebbero entrambi il controllo e lavorerebbero su
            // due copie separate della scheda, sovrascrivendosi i salvataggi. Gli await restano fuori.
            // L'associazione si libera da sola alla disconnessione (la sessione esce da _connectedClients);
            // un secondo /link della stessa sessione (sullo stesso o su un altro personaggio) è ammesso.
            LinkResponsePayload? failure = null;
            Character? loadedCharacter = null;

            lock (_linkLock)
            {
                var holder = _connectedClients.Values.FirstOrDefault(c =>
                    c.Id != client.Id
                    && c.LinkedCharacterHandle != null
                    && c.LinkedCharacterHandle.Equals(linkReq.CharacterHandle, StringComparison.OrdinalIgnoreCase));

                Console.WriteLine($"[LINK] '{client.Player.Username}' chiede '{linkReq.CharacterHandle}': " +
                                  (holder == null ? "libero." : $"occupato da '{holder.Player.Username}'."));

                if (holder != null)
                {
                    Console.WriteLine($"[LINK] RIFIUTATO: '{linkReq.CharacterHandle}' è già in uso da '{holder.Player.Username}'.");
                    failure = new LinkResponsePayload
                    {
                        Success = false,
                        Message = $"Il personaggio '{linkReq.CharacterHandle}' è già in uso da '{holder.Player.Username}'."
                    };
                }
                else
                {
                    try
                    {
                        loadedCharacter = CharacterRepository.LoadCharacter(linkReq.CharacterHandle);
                        // Il nome del file è la fonte di verità: allineo l'Handle interno, altrimenti
                        // i salvataggi (che usano character.Handle) finirebbero in un altro file.
                        loadedCharacter.Handle = linkReq.CharacterHandle;
                        client.Player.Character = loadedCharacter;
                        client.LinkedCharacterHandle = linkReq.CharacterHandle;
                        client.Player.Role = Player.RoleType.player;
                    }
                    catch (FileNotFoundException)
                    {
                        failure = new LinkResponsePayload
                        {
                            Success = false,
                            Message = $"Personaggio '{linkReq.CharacterHandle}' non trovato. Usa prima '/create <archetipo> {linkReq.CharacterHandle}' per crearlo."
                        };
                    }
                    catch (CharacterLoadException ex)
                    {
                        failure = new LinkResponsePayload
                        {
                            Success = false,
                            Message = $"{ex.Message} Contatta l'amministratore del server."
                        };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                                  or System.Security.SecurityException)
                    {
                        // Errore di accesso al disco (o di validazione) nel caricare/creare la scheda
                        Console.WriteLine($"[LINK] Errore con la scheda '{linkReq.CharacterHandle}': {ex.Message}");
                        failure = new LinkResponsePayload
                        {
                            Success = false,
                            Message = $"Impossibile caricare o creare la scheda di '{linkReq.CharacterHandle}': errore del server, riprova più tardi."
                        };
                    }
                }
            }

            if (failure != null)
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, failure));
                return;
            }

            // Senza failure il personaggio è stato caricato e associato alla sessione
            Character linkedCharacter = loadedCharacter!;

            CharacterRepository.TrySaveCharacter(linkedCharacter);

            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
            {
                Success = true,
                Message = $"Personaggio '{linkReq.CharacterHandle}' associato a '{linkReq.PlayerHandle}'.",
                Role = client.Player.Role,
                PlayerHandle = client.Player.Username,
                CharacterHandle = linkReq.CharacterHandle
            }));

            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerStateUpdate, new CharacterStatePayload
            {
                Character = linkedCharacter
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

    private async Task HandleCreateCharacterAsync(ClientSession client, NetworkPacket packet)
    {
        var createReq = packet.DeserializePayload<CreateCharacterRequestPayload>();
        if (createReq == null || string.IsNullOrWhiteSpace(createReq.CharacterHandle) || string.IsNullOrWhiteSpace(createReq.Archetype))
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, new CreateCharacterResponsePayload
            {
                Success = false,
                Message = "Uso corretto: /create <archetipo> <handle>"
            }));
            return;
        }

        string safeHandle;
        try
        {
            safeHandle = CharacterHandleValidator.ValidateAndNormalize(createReq.CharacterHandle, nameof(createReq.CharacterHandle));
        }
        catch (ArgumentException)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, new CreateCharacterResponsePayload
            {
                Success = false,
                Message = CharacterHandleValidator.RulesDescription
            }));
            return;
        }

        Character newChar;
        try
        {
            newChar = CharacterTemplateFactory.CreateFromArchetype(createReq.Archetype);
        }
        catch (FileNotFoundException)
        {
            var available = CharacterTemplateFactory.GetAvailableArchetypes();
            string archetypesList = available.Count > 0 ? string.Join(", ", available) : "nessuno";
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, new CreateCharacterResponsePayload
            {
                Success = false,
                Message = $"Archetipo non trovato. Archetipi disponibili: {archetypesList}"
            }));
            return;
        }
        catch (Exception ex)
        {
            // Dettagli (percorsi compresi) solo nel log del server, non al client
            Console.WriteLine($"[CREATE] Errore nel caricare l'archetipo '{createReq.Archetype}': {ex.Message}");
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, new CreateCharacterResponsePayload
            {
                Success = false,
                Message = "Errore del server nel caricare l'archetipo. Contatta l'amministratore."
            }));
            return;
        }

        newChar.Handle = safeHandle;
        CharacterTemplateFactory.RandomizeStats(newChar);
        CharacterRepository.NormalizeSkills(newChar);

        // Controlli, conteggio e scrittura nello stesso lock: senza, due /create sullo stesso handle
        // passerebbero entrambi "esiste già" e il secondo sovrascriverebbe la scheda del primo.
        CreateCharacterResponsePayload? failure = null;
        lock (_createLock)
        {
            try
            {
                if (client.CharactersCreated >= MaxCharactersPerSession)
                {
                    failure = new CreateCharacterResponsePayload
                    {
                        Success = false,
                        Message = $"Hai già creato {MaxCharactersPerSession} personaggi in questa sessione: limite raggiunto."
                    };
                }
                else if (CharacterRepository.CountCharacters() >= MaxCharactersTotal)
                {
                    Console.WriteLine($"[CREATE] RIFIUTATO: raggiunto il limite globale di {MaxCharactersTotal} schede.");
                    failure = new CreateCharacterResponsePayload
                    {
                        Success = false,
                        Message = "Il server ha raggiunto il numero massimo di personaggi. Contatta l'amministratore."
                    };
                }
                else if (CharacterRepository.CharacterExists(safeHandle))
                {
                    failure = new CreateCharacterResponsePayload
                    {
                        Success = false,
                        Message = $"La scheda del personaggio '{safeHandle}' esiste già. Scegli un altro handle o usa /link."
                    };
                }
                else
                {
                    CharacterRepository.SaveCharacter(newChar);
                    client.CharactersCreated++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CREATE] Errore creando '{safeHandle}': {ex.Message}");
                failure = new CreateCharacterResponsePayload
                {
                    Success = false,
                    Message = "Errore del server nel salvare la scheda. Riprova più tardi."
                };
            }
        }

        if (failure != null)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, failure));
            return;
        }

        Console.WriteLine($"[CHAR] Creato nuovo personaggio '{safeHandle}' dall'archetipo '{createReq.Archetype}' (Ruolo: {newChar.Role}).");
        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CreateCharacterResponse, new CreateCharacterResponsePayload
        {
            Success = true,
            CharacterHandle = safeHandle,
            Archetype = createReq.Archetype,
            Message = $"Personaggio '{safeHandle}' ({newChar.Role}) creato con successo! Puoi associarlo al tuo account con: /link {safeHandle}"
        }));
    }

    private async Task HandleLeaveAsync(ClientSession client)
    {
        string gameName = string.Empty;
        bool gameClosed = false;
        bool wasMaster = false;
        List<Player> remaining = [];

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            if (game != null)
            {
                gameName = game.Name;
                wasMaster = client.Player.Role == Player.RoleType.master;

                game.Players.Remove(client.Player);

                // Un tiro di iniziativa di un personaggio che esce non ha più senso nel game
                if (client.Player.Character != null)
                {
                    game.InitRolls.RemoveAll(r => r.PlayerName.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                }

                if (game.Players.Count == 0)
                {
                    _games.Remove(game);
                    gameClosed = true;
                    Console.WriteLine($"[GAME] Game '{game.Name}' rimosso: nessun giocatore rimasto.");
                }
                else
                {
                    remaining = [.. game.Players];
                }
            }
        }

        if (gameName.Length == 0)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LeaveResponse, new LeaveResponsePayload
            {
                Success = false,
                Message = "Non sei in nessun game: usa /join <game> per entrare in una partita.",
                Role = client.Player.Role
            }));
            return;
        }

        // Il personaggio collegato resta tuo (il /link è indipendente dal game): senza personaggio, e
        // per un master, il ruolo torna "none"; altrimenti si resta "player".
        client.Player.Role = (!wasMaster && client.Player.Character != null)
            ? Player.RoleType.player
            : Player.RoleType.none;

        Console.WriteLine($"[GAME] {client.Player.Username} ha lasciato '{gameName}'{(wasMaster ? " (era il master)" : "")}.");

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LeaveResponse, new LeaveResponsePayload
        {
            Success = true,
            Message = gameClosed
                ? $"Hai lasciato '{gameName}'. Eri l'ultimo giocatore: il game è stato chiuso."
                : $"Hai lasciato '{gameName}'.",
            GameName = gameName,
            Role = client.Player.Role
        }));

        // Avviso agli altri giocatori del game (in particolare se se n'è andato il master)
        string notice = wasMaster
            ? $"Il master {client.Player.Username} ha lasciato '{gameName}': il ruolo di master è libero."
            : $"{client.Player.Username} ha lasciato '{gameName}'.";

        foreach (var other in remaining)
        {
            var session = _connectedClients.Values.FirstOrDefault(s => ReferenceEquals(s.Player, other));
            if (session == null) continue;

            try
            {
                await session.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = notice }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GAME] Notifica di uscita non consegnata a [{session.IdShort}]: {ex.Message}");
            }
        }
    }

    private async Task HandleJoinAsync(ClientSession client, NetworkPacket packet)
    {
        var joinReq = packet.DeserializePayload<JoinRequestPayload>();
        if (joinReq == null || string.IsNullOrWhiteSpace(joinReq.GameName)) return;

        string cleanGameName = InputLimits.Clean(joinReq.GameName, InputLimits.MaxGameNameLength + 1);
        if (cleanGameName.Length > InputLimits.MaxGameNameLength || InputLimits.HasControlChars(joinReq.GameName))
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_JoinResponse, new JoinResponsePayload
            {
                Success = false,
                Message = $"Nome del gioco non valido: massimo {InputLimits.MaxGameNameLength} caratteri, senza caratteri di controllo."
            }));
            return;
        }
        joinReq.GameName = cleanGameName;
        joinReq.Description = InputLimits.Clean(joinReq.Description, 200);
        // Qualunque ruolo diverso da master è "player" (un join con ruolo "none" non ha senso)
        if (joinReq.Role != Player.RoleType.master) joinReq.Role = Player.RoleType.player;

        string denialMessage = string.Empty;
        bool success = false;

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Name.Equals(joinReq.GameName, StringComparison.OrdinalIgnoreCase));

            // Una sessione sta in un solo game alla volta (i comandi cercano "il" game del giocatore):
            // per cambiare game si esce prima con /leave.
            var currentGame = _games.FirstOrDefault(g => g.Players.Contains(client.Player));

            if (currentGame != null && !ReferenceEquals(currentGame, game))
            {
                denialMessage = $"Sei già nel gioco '{currentGame.Name}': usa /leave prima di entrare in un altro.";
            }
            else if (joinReq.Role == Player.RoleType.master)
            {
                if (game == null && _games.Count >= InputLimits.MaxGames)
                {
                    denialMessage = $"Il server ha raggiunto il numero massimo di giochi ({InputLimits.MaxGames}).";
                }
                else if (game == null)
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
                        denialMessage = $"Il gioco '{joinReq.GameName}' ha già un master.";
                        success = false;
                    }
                    else
                    {
                        success = true;
                    }
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
                    {
                        success = true;
                    }
                }
            }

            if (success)
            {
                game.Players.Add(client.Player);
                client.Player.Role = joinReq.Role;
            }
        }

        if (success)
        {
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

    private async Task HandleInitAsync(ClientSession client, NetworkPacket packet)
    {
        var initReq = packet.DeserializePayload<InitRequest>();

        if (client.Player != null && client.Player.Role == Player.RoleType.master)
        {
            Game? game;
            List<Player> playersSnapshot = [];
            lock (_gamesLock)
            {
                game = _games.FirstOrDefault(g =>
                    g.Players.Any(p => p.Username == client.Player.Username && p.Role == Player.RoleType.master));

                if (game != null) playersSnapshot = [.. game.Players];
            }

            if (game != null)
            {
                lock (_gamesLock) { game.InitRolls.Clear(); }

                foreach (var player in playersSnapshot)
                {
                    if (player.Role == Player.RoleType.master) continue;

                    var targetSession = _connectedClients.Values
                        .FirstOrDefault(s => s.Player?.Username == player.Username);

                    if (targetSession != null)
                    {
                        await targetSession.SendPacketAsync(NetworkPacket.Create(
                            OpCode.S2C_InitRollRequest,
                            new InitRollRequestPayload { Message = "Il Master ha avviato il tiro di iniziativa." }
                        ));
                    }
                }
            }
            else
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
                {
                    Message = "Non sei master di nessun game attivo: impossibile avviare il tiro di iniziativa."
                }));
            }
        }
        else if (client.Player != null && client.Player.Role == Player.RoleType.player)
        {
            if (client.Player.Character == null)
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
                {
                    Message = "Nessun personaggio associato: usa /link <Character> prima del tiro di iniziativa."
                }));
                return;
            }

            await ResolveInitiativeAsync(client, client.Player.Character, initReq?.Modifiers);
        }
        else
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = "Non sei in nessun game: unisciti a una partita con /join prima del tiro di iniziativa."
            }));
        }
    }

    /// <summary>
    /// Tiro di iniziativa di <paramref name="actor"/> nel game di <paramref name="reporter"/> (la sessione che ha
    /// dato il comando: il giocatore stesso, o il master che tira per un NPC). Un nuovo tiro dello stesso
    /// personaggio sostituisce quello precedente.
    /// </summary>
    private async Task ResolveInitiativeAsync(ClientSession reporter, Character actor, string? modifiers)
    {
        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
        }

        if (game == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = "Non sei in nessun game attivo: impossibile tirare l'iniziativa."
            }));
            return;
        }

        int refStat = actor.Stats.REF;

        int combatSense = 0;
        if (actor.Role == Character.RoleType.Solo)
        {
            combatSense = actor.Skills.TryGetValue("CombatSense", out int csLvl) ? csLvl : 0;
        }

        int cyberInit = actor.Cyberware
            .Where(cw => cw.Bonus != null && cw.Bonus.ApplyTo.Equals("INIT", StringComparison.OrdinalIgnoreCase))
            .Sum(cw => cw.Bonus.Value);

        int requestedMod = 0;
        if (!string.IsNullOrWhiteSpace(modifiers))
        {
            int.TryParse(modifiers.Trim(), out requestedMod);
        }

        int dice = (int)GameDice.Roll("1d10").Total;
        int total = refStat + combatSense + cyberInit + requestedMod + dice;

        lock (_gamesLock)
        {
            game.InitRolls.RemoveAll(r => r.PlayerName.Equals(actor.Handle, StringComparison.OrdinalIgnoreCase));
            game.InitRolls.Add(new Game.Roll
            {
                Value = total,
                PlayerName = actor.Handle,
                Timestamp = TimeOnly.FromDateTime(DateTime.UtcNow)
            });
        }

        Console.WriteLine($"[INIT] {reporter.Player.Username} ({actor.Handle}) tira iniziativa: REF {refStat} + CombatSense {combatSense} + Cyberware {cyberInit} + Mod {requestedMod} + d10 {dice} = {total}");

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
        {
            Message = $"Tiro di iniziativa registrato per {actor.Handle}: {total} (REF {refStat} + CS {combatSense} + Cyber {cyberInit} + d10 {dice})."
        }));
    }

    private async Task HandleGetCharactersAsync(ClientSession client)
    {
        var charactersResponsePayload = new CharactersResponsePayload();

        foreach (var character in CharacterRepository.ListCharacters())
        {
            charactersResponsePayload.Characters.Add(new CharactersResponsePayload.CharacterNode
            {
                Handle = character.Handle,
                Role = character.Role
            });
        }

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_CharactersResponse, charactersResponsePayload));
    }

    private async Task HandleGetGamesAsync(ClientSession client)
    {
        var gamesResponsePayload = new GamesResponsePayload();

        lock (_gamesLock)
        {
            foreach (var game in _games)
                gamesResponsePayload.Games.Add(game.Name);
        }

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_GamesResponse, gamesResponsePayload));
    }

    private static async Task<bool> EnsureCharacterLinkedAsync(ClientSession client)
    {
        if (client.Player.Character != null) return true;

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
        {
            Message = "Nessun personaggio associato: usa /link <Character> prima di usare questo comando."
        }));
        return false;
    }

    private void RemovePlayerFromGames(Player player)
    {
        lock (_gamesLock)
        {
            foreach (var game in _games.Where(g => g.Players.Contains(player)).ToList())
            {
                game.Players.Remove(player);

                if (game.Players.Count == 0)
                {
                    _games.Remove(game);
                    Console.WriteLine($"[GAME] Game '{game.Name}' rimosso: nessun giocatore rimasto.");
                }
                else if (player.Role == Player.RoleType.master)
                {
                    Console.WriteLine($"[GAME] Il master '{player.Username}' ha lasciato '{game.Name}': il ruolo è di nuovo libero.");
                }
            }
        }
    }

    /// <summary>
    /// Invia un pacchetto ai partecipanti del game di <paramref name="origin"/>; se non è in nessun game,
    /// solo a lui. Evita che tiri, prove e movimenti di una partita siano visibili a tutte le altre.
    /// </summary>
    private async Task BroadcastToGameAsync(ClientSession origin, NetworkPacket packet, bool includeOrigin = true)
    {
        List<ClientSession> audience;
        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(origin.Player));
            audience = game != null ? GetGameSessions(game) : [origin];
        }

        foreach (var session in audience)
        {
            if (!includeOrigin && session.Id == origin.Id) continue;
            try
            {
                await session.SendPacketAsync(packet);
            }
            catch
            {
            }
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
            }
        }
    }
}
