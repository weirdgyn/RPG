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
    private const int MaxCharactersPerSession = 20;
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
            var masterNotices = RemovePlayerFromGames(client.Player, out var completedChecks);

            string userName = !string.IsNullOrEmpty(client.Player.Username) ? client.Player.Username : client.IdShort;
            Console.WriteLine($"[SERVER] Client disconnesso: {userName}");

            if (client.IsAuthenticated)
            {
                var leaveNotify = NetworkPacket.Create(OpCode.S2C_Notification, new { Message = $"{userName} ha abbandonato la rete." });
                await BroadcastPacketAsync(leaveNotify, excludeClientId: client.Id);

                // Se se n'è andato il master, i giocatori rimasti nel suo game devono saperlo
                foreach (var (gameName, remaining) in masterNotices)
                {
                    await NotifyPlayersAsync(remaining, $"Il master {userName} si è disconnesso da '{gameName}': il ruolo di master è libero.");
                }

                // Un check in attesa solo di chi si è disconnesso va spogliato con chi è rimasto
                foreach (var (checkGame, checkDone) in completedChecks)
                {
                    await FinalizeCheckAsync(checkGame, checkDone, forced: false);
                }
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
            case OpCode.C2S_Reload:
                await HandleReloadAsync(client, packet);
                break;
            case OpCode.C2S_Check:
                await HandleCheckAsync(client, packet);
                break;
            case OpCode.C2S_Throw:
                await HandleThrowAsync(client, packet);
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
            case OpCode.C2S_Npc:
                await HandleNpcAsync(client, packet);
                break;
            case OpCode.C2S_As:
                await HandleAsAsync(client, packet);
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

    private sealed record AttackOutcome(string? Error, AttackResponsePayload? Result, string? LogLine, bool AttackerChanged = false);

    // Somma dei bonus cyberware con un dato ApplyTo ("HIT", "DAMAGE", ...)
    private static int SumCyberBonus(Character actor, string applyTo) =>
        actor.Cyberware
            .Where(cw => cw.Bonus != null && cw.Bonus.ApplyTo.Equals(applyTo, StringComparison.OrdinalIgnoreCase))
            .Sum(cw => cw.Bonus.Value);

    // Pila di munizioni da cui provengono i colpi attualmente nel caricatore (null = standard)
    private static Ammo? FindLoadedAmmo(Character owner, Weapon weapon) =>
        string.IsNullOrEmpty(weapon.LoadedAmmoId)
            ? null
            : owner.Ammos.FirstOrDefault(a => a.Id.Equals(weapon.LoadedAmmoId, StringComparison.OrdinalIgnoreCase));

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

        // Poi gli NPC del game (nessuna sessione: il master li controlla)
        var npc = game.NPCs.FirstOrDefault(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        return npc != null ? new Combatant(npc, null) : null;
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

        // Il colpo sparato consuma munizioni: la scheda dell'attaccante va salvata anche se ha mancato
        if (outcome.AttackerChanged)
        {
            CharacterRepository.TrySaveCharacter(attacker);
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

    private async Task HandleThrowAsync(ClientSession client, NetworkPacket packet)
    {
        var throwReq = packet.DeserializePayload<ThrowRequestPayload>();
        if (throwReq?.Position == null) return;
        throwReq.WeaponKey = InputLimits.Clean(throwReq.WeaponKey, InputLimits.MaxNameFieldLength);

        if (!await EnsureCharacterLinkedAsync(client)) return;

        await ResolveThrowAsync(client, client.Player.Character!, throwReq);
    }

    private sealed record ThrowOutcome(string? Error, AreaResultPayload? Result, string? LogLine, List<Character> Changed);

    /// <summary>
    /// Lancio di un'arma ad area di <paramref name="thrower"/> verso un punto. Colpisce tutti i personaggi del game
    /// (giocatori e NPC, lanciatore compreso) entro il raggio dell'esplosione.
    /// </summary>
    private async Task ResolveThrowAsync(ClientSession reporter, Character thrower, ThrowRequestPayload throwReq)
    {
        Game? game;
        GameMap? map = null;
        List<Character> everyone = [];
        List<ClientSession> audience = [];

        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
            if (game != null)
            {
                map = game.Map;
                audience = GetGameSessions(game);
                foreach (var p in game.Players)
                {
                    if (p.Character != null && !everyone.Contains(p.Character)) everyone.Add(p.Character);
                }
                foreach (var npc in game.NPCs)
                {
                    if (!everyone.Contains(npc)) everyone.Add(npc);
                }
            }
        }

        if (game == null || map == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi essere in un game per lanciare: usa /join <game>." }));
            return;
        }

        if (map.GetMovementResult(throwReq.Position) == GameMap.MovementResult.OutOfBounds)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Il punto indicato è fuori dai confini della mappa." }));
            return;
        }

        var outcome = ResolveThrowRules(thrower, throwReq.Position, throwReq.WeaponKey, everyone);
        if (outcome.Error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = outcome.Error }));
            return;
        }

        Console.WriteLine(outcome.LogLine);

        foreach (var changed in outcome.Changed)
        {
            CharacterRepository.TrySaveCharacter(changed);
        }

        var packet = NetworkPacket.Create(OpCode.S2C_AreaResult, outcome.Result);
        foreach (var session in audience)
        {
            try { await session.SendPacketAsync(packet); }
            catch (Exception ex) { Console.WriteLine($"[COMBAT] Esito area non consegnato a [{session.IdShort}]: {ex.Message}"); }
        }
    }

    // Regole del lancio/esplosione, sincrone e sotto _combatLock. Nessun I/O di rete qui dentro.
    private ThrowOutcome ResolveThrowRules(Character thrower, Position3D aimed, string? weaponKey, List<Character> everyone)
    {
        ThrowOutcome Error(string message) => new(message, null, null, []);

        lock (_combatLock)
        {
            Weapon? weapon;
            if (!string.IsNullOrWhiteSpace(weaponKey))
            {
                string key = weaponKey.Trim();
                weapon = thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area &&
                    (w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || w.Id.Equals(key, StringComparison.OrdinalIgnoreCase)));
                if (weapon == null) return Error($"Nessuna arma ad area '{key}' nell'inventario.");
            }
            else
            {
                weapon = thrower.EquippedWeapon is { Type: Weapon.WeaponType.Area, CurrentAmmo: > 0 }
                    ? thrower.EquippedWeapon
                    : thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area && w.CurrentAmmo > 0)
                      ?? thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area);
                if (weapon == null) return Error("Non hai armi ad area da lanciare.");
            }

            if (weapon.CurrentAmmo <= 0) return Error($"{weapon.Name}: pezzi esauriti.");

            double radius = weapon.BlastRadiusMeters ?? 0;
            if (radius <= 0) return Error($"{weapon.Name} non ha un raggio d'esplosione valido.");

            int penalty = thrower.Health.GetWoundPenalty();
            if (penalty <= -99) return Error("Sei K.O., non puoi lanciare!");

            if (!Skills.TryGetStatForSkill(weapon.ScatterSkillName, out string statName))
            {
                return Error($"L'abilità '{weapon.ScatterSkillName}' di {weapon.Name} non è riconosciuta: scheda o arma non valida.");
            }

            double throwRange = weapon.OptimalRangeMeters > 0 ? weapon.OptimalRangeMeters : 15;
            double distance = CombatRules.CalculateDistance(thrower.Position, aimed);
            if (distance > throwRange)
            {
                return Error($"Troppo lontano per {weapon.Name}: {distance:F1}m, gittata di lancio {throwRange:F0}m.");
            }

            int throwTn = CombatRules.GetRangedTargetNumber(distance, throwRange) ?? 30;
            int statVal = thrower.Stats.GetStatValue(statName);
            int skillLvl = thrower.Skills.TryGetValue(weapon.ScatterSkillName, out int lvl) ? lvl : 0;
            var roll = GameDice.RollExplodingD10Detailed();
            int total = statVal + skillLvl + penalty + roll.Total;

            bool onTarget = !roll.IsFumble && total >= throwTn;
            weapon.CurrentAmmo--;

            // Se il lancio fallisce la granata devia: d10 metri in direzione casuale (doppio con un fallimento critico)
            var landed = new Position3D { X = aimed.X, Y = aimed.Y, Z = aimed.Z };
            double deviation = 0;
            if (!onTarget)
            {
                deviation = GameDice.Roll("1d10").Total * (roll.IsFumble ? 2 : 1);
                double angle = Random.Shared.NextDouble() * 2 * Math.PI;
                landed.X += (int)Math.Round(deviation * Math.Cos(angle));
                landed.Y += (int)Math.Round(deviation * Math.Sin(angle));
            }

            // Un solo tiro di danno per fascia: tutti quelli nella stessa fascia subiscono lo stesso danno
            var bandRolls = new Dictionary<string, int>();
            var victims = new List<AreaVictim>();
            var changed = new List<Character> { thrower };

            foreach (var target in everyone)
            {
                double d = CombatRules.CalculateDistance(target.Position, landed);
                if (d > radius) continue;

                string? dice;
                string bandKey;
                if (weapon.AreaDamageBands is { Count: > 0 })
                {
                    var band = weapon.AreaDamageBands.FirstOrDefault(b => d >= b.FromMeters && d < b.ToMeters);
                    if (band == null) continue;
                    dice = band.DamageDice;
                    bandKey = $"{band.FromMeters}-{band.ToMeters}";
                }
                else
                {
                    dice = weapon.DamageDice;
                    bandKey = "all";
                }

                if (!bandRolls.TryGetValue(bandKey, out int rawDamage))
                {
                    rawDamage = Math.Max(0, (int)GameDice.Roll(dice).Total);
                    bandRolls[bandKey] = rawDamage;
                }

                // L'esplosione colpisce il corpo: protegge l'armatura del torso, nessun moltiplicatore di locazione
                var armor = CombatRules.ApplyArmor(target.Armor.GetPiece(ArmorLocation.Torso), rawDamage, 1);
                target.Health.CurrentHealth = Math.Max(0, target.Health.CurrentHealth - armor.DamageDealt);
                if (!changed.Contains(target)) changed.Add(target);

                victims.Add(new AreaVictim
                {
                    Handle = target.Handle,
                    Distance = Math.Round(d, 1),
                    RawDamage = rawDamage,
                    ArmorSP = armor.SpFaced,
                    DamageDealt = armor.DamageDealt,
                    RemainingHealth = target.Health.CurrentHealth,
                    MaxHealth = target.Health.MaxHealth,
                    WoundStatus = target.Health.GetWoundStatusDescription()
                });
            }

            string aimText = $"({aimed.X}, {aimed.Y}, {aimed.Z})";
            string landText = $"({landed.X}, {landed.Y}, {landed.Z})";
            string message = onTarget
                ? $"{thrower.Handle} lancia {weapon.Name} su {aimText}: centro perfetto! Esplode nel punto indicato."
                : roll.IsFumble
                    ? $"FALLIMENTO CRITICO! {thrower.Handle} lancia {weapon.Name} su {aimText} e la granata devia di {deviation:F0}m: esplode in {landText}."
                    : $"{thrower.Handle} lancia {weapon.Name} su {aimText}, ma devia di {deviation:F0}m: esplode in {landText}.";

            var result = new AreaResultPayload
            {
                ThrowerHandle = thrower.Handle,
                WeaponName = weapon.Name,
                ThrowTotal = total,
                ThrowTN = throwTn,
                IsFumble = roll.IsFumble,
                OnTarget = onTarget,
                DeviationMeters = deviation,
                Aimed = aimed,
                Landed = landed,
                BlastRadius = radius,
                PiecesLeft = weapon.CurrentAmmo,
                Victims = victims,
                Message = message
            };

            string log = $"[COMBAT] {thrower.Handle} lancia '{weapon.Name}' su {aimText} ({distance:F1}m). Tiro: {total} (d10 {roll.Total}) vs TN {throwTn} -> "
                + (onTarget ? "a segno" : $"devia di {deviation:F0}m in {landText}")
                + $". Colpiti: {(victims.Count > 0 ? string.Join(", ", victims.Select(v => $"{v.Handle} -{v.DamageDealt}")) : "nessuno")}. Pezzi rimasti: {weapon.CurrentAmmo}.";

            return new ThrowOutcome(null, result, log, changed);
        }
    }

    private async Task HandleReloadAsync(ClientSession client, NetworkPacket packet)
    {
        var reloadReq = packet.DeserializePayload<ReloadRequestPayload>();
        if (!await EnsureCharacterLinkedAsync(client)) return;

        await ResolveReloadAsync(client, client.Player.Character!, reloadReq);
    }

    /// <summary>Ricarica l'arma di <paramref name="actor"/> (giocatore o NPC); gli errori vanno a <paramref name="reporter"/>.</summary>
    private async Task ResolveReloadAsync(ClientSession reporter, Character actor, ReloadRequestPayload? reloadReq)
    {
        string ammoKey = InputLimits.Clean(reloadReq?.AmmoKey, InputLimits.MaxNameFieldLength) ?? string.Empty;
        string weaponKey = InputLimits.Clean(reloadReq?.WeaponKey, InputLimits.MaxNameFieldLength) ?? string.Empty;

        string? error;
        string? message;
        lock (_combatLock)
        {
            (error, message) = ReloadRules(actor, ammoKey, weaponKey);
        }

        if (error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = error }));
            return;
        }

        CharacterRepository.TrySaveCharacter(actor);
        Console.WriteLine($"[COMBAT] {message}");
        await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));
    }

    // Regole della ricarica, sincrone e sotto _combatLock. I colpi rimasti nel caricatore tornano nella
    // pila di provenienza, poi si caricano fino a capienza dalla pila scelta.
    private static (string? Error, string? Message) ReloadRules(Character actor, string ammoKey, string weaponKey)
    {
        Weapon? weapon = weaponKey.Length == 0
            ? actor.EquippedWeapon
            : actor.Weapons.FirstOrDefault(w =>
                w.Name.Equals(weaponKey, StringComparison.OrdinalIgnoreCase) ||
                w.Id.Equals(weaponKey, StringComparison.OrdinalIgnoreCase));

        if (weapon == null || !weapon.UsesAmmo || string.IsNullOrWhiteSpace(weapon.Caliber))
        {
            return ("Quest'arma non usa munizioni ricaricabili.", null);
        }

        string caliber = weapon.Caliber;
        bool SameCaliber(Ammo a) => a.Caliber.Equals(caliber, StringComparison.OrdinalIgnoreCase);

        // Pila da cui vengono i colpi già nel caricatore (senza indicazione: munizioni standard)
        Ammo? loadedStack = FindLoadedAmmo(actor, weapon)
            ?? actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Effect == AmmoEffect.Standard);

        Ammo? chosen;
        if (ammoKey.Length > 0)
        {
            // Prima l'Id esatto, poi una corrispondenza parziale su Id o nome (es. "ap", "perforanti")
            chosen = actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Id.Equals(ammoKey, StringComparison.OrdinalIgnoreCase))
                ?? actor.Ammos.FirstOrDefault(a => SameCaliber(a) &&
                    (a.Id.Contains(ammoKey, StringComparison.OrdinalIgnoreCase) ||
                     a.Name.Contains(ammoKey, StringComparison.OrdinalIgnoreCase)));
            if (chosen == null)
            {
                return ($"Nessuna munizione '{ammoKey}' compatibile con {weapon.Name} ({caliber}).", null);
            }
        }
        else
        {
            chosen = loadedStack is { Quantity: > 0 }
                ? loadedStack
                : actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Quantity > 0) ?? loadedStack;
        }

        if (chosen == null)
        {
            return ($"Nessuna munizione {caliber} nell'inventario.", null);
        }

        bool sameStack = ReferenceEquals(chosen, loadedStack);
        if (sameStack && weapon.CurrentAmmo >= weapon.MagazineCapacity)
        {
            return ($"{weapon.Name} ha già il caricatore pieno.", null);
        }

        if (chosen.Quantity <= 0)
        {
            return ($"Munizioni esaurite: '{chosen.Name}'.", null);
        }

        // Scarico il caricatore nella pila di provenienza (creandola se mancava)...
        if (weapon.CurrentAmmo > 0)
        {
            if (loadedStack == null)
            {
                loadedStack = new Ammo
                {
                    Id = $"ammo_{caliber}_std".Replace(' ', '_'),
                    Name = $"Munizioni {caliber} standard",
                    Caliber = caliber,
                    Effect = AmmoEffect.Standard
                };
                actor.Ammos.Add(loadedStack);
            }
            loadedStack.Quantity += weapon.CurrentAmmo;
        }

        // ...e carico fino a capienza dalla pila scelta
        int loaded = Math.Min(weapon.MagazineCapacity, chosen.Quantity);
        chosen.Quantity -= loaded;
        weapon.CurrentAmmo = loaded;
        weapon.LoadedAmmoId = chosen.Id;

        return (null, $"{actor.Handle} ricarica {weapon.Name} con {chosen.Name}: {loaded}/{weapon.MagazineCapacity} colpi (in riserva: {chosen.Quantity}).");
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

            if (weaponToUse.Type == Weapon.WeaponType.Area)
            {
                return new AttackOutcome($"{weaponToUse.Name} è un'arma ad area: usa /throw <X> <Y> [Z] [arma].", null, null);
            }

            string skillName = weaponToUse.SkillName;
            if (!Skills.TryGetStatForSkill(skillName, out string statName))
            {
                return new AttackOutcome($"L'abilità '{skillName}' di {weaponToUse.Name} non è riconosciuta: scheda o arma non valida.", null, null);
            }

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

            Ammo? loadedAmmo = null;
            bool ammoSpent = false;

            if (weaponToUse.Type == Weapon.WeaponType.Ranged)
            {
                if (weaponToUse.UsesAmmo && weaponToUse.CurrentAmmo <= 0)
                {
                    return new AttackOutcome($"{weaponToUse.Name} ha il caricatore vuoto: usa /reload.", null, null);
                }

                int? rangedTn = CombatRules.GetRangedTargetNumber(distance, weaponToUse.OptimalRangeMeters);
                if (rangedTn == null)
                {
                    return new AttackOutcome($"Bersaglio fuori portata: {distance:F1}m, la gittata massima di {weaponToUse.Name} è {weaponToUse.OptimalRangeMeters * 2}m.", null, null);
                }

                targetNumber = rangedTn.Value;
                attackTypeDesc = $"A Distanza con {weaponToUse.Name} ({distance:F1}m, gittata {weaponToUse.OptimalRangeMeters}m) -> TN: {targetNumber}";

                if (weaponToUse.UsesAmmo)
                {
                    loadedAmmo = FindLoadedAmmo(attacker, weaponToUse);
                    weaponToUse.CurrentAmmo--;
                    ammoSpent = true;
                }
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

            var attackRoll = GameDice.RollExplodingD10Detailed();
            int diceVal = attackRoll.Total;
            int weaponModifier = weaponToUse.GetAttackModifier();
            int cyberHit = SumCyberBonus(attacker, "HIT");
            int attackTotal = attackerStat + attackerSkillLvl + attackerPenalty + weaponModifier + cyberHit + diceVal;

            // Un 1 naturale sul primo dado è un fallimento critico: manca comunque (il colpo sparato è perso)
            bool isFumble = attackRoll.IsFumble;
            bool isHit = !isFumble && attackTotal >= targetNumber;
            int damageDealt = 0;
            string hitLocationName = string.Empty;
            int hitMultiplier = 1;
            CombatRules.ArmorResult? armorResult = null;

            if (isHit)
            {
                var damageRoll = GameDice.Roll(weaponToUse.DamageDice);
                int rawDamage = Math.Max(0, (int)damageRoll.Total + (loadedAmmo?.DamageModifier ?? 0) + SumCyberBonus(attacker, "DAMAGE"));

                if (weaponToUse.Type != Weapon.WeaponType.Area)
                {
                    // Locazione d'impatto (a distanza e in mischia) e armatura di quella zona
                    var loc = CombatRules.ResolveHitLocation((int)GameDice.Roll("1d10").Total);
                    hitLocationName = loc.Name;
                    hitMultiplier = loc.DamageMultiplier;

                    armorResult = CombatRules.ApplyArmor(target.Armor.GetPiece(loc.ArmorLocation), rawDamage, loc.DamageMultiplier,
                        armorPiercing: loadedAmmo?.HalvesArmorSP == true);
                    damageDealt = armorResult.DamageDealt;
                }
                else
                {
                    damageDealt = rawDamage;
                }

                target.Health.CurrentHealth = Math.Max(0, target.Health.CurrentHealth - damageDealt);
            }

            string armorNote = armorResult is { SpFaced: > 0 }
                ? $" Armatura SP {armorResult.SpFaced}: assorbiti {armorResult.Absorbed}{(armorResult.Ablated ? ", SP -1" : "")}."
                : string.Empty;

            string hitDesc = isHit
                ? hitLocationName.Length > 0
                    ? $"COLPITO ({damageDealt} dmg, Impatto: {hitLocationName}{(hitMultiplier > 1 ? $" x{hitMultiplier}" : "")}){armorNote}"
                    : $"COLPITO ({damageDealt} dmg)"
                : isFumble ? "FALLIMENTO CRITICO" : "MANCATO";

            string log = $"[COMBAT] {attacker.Handle} attacca {target.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. Tiro: {attackTotal} (d10 {diceVal}, arma {weaponModifier:+0;-0;+0}, cyber {cyberHit:+0;-0;+0}) vs TN {targetNumber}. {hitDesc}{(weaponToUse.UsesAmmo ? $" Colpi: {weaponToUse.CurrentAmmo}/{weaponToUse.MagazineCapacity}." : "")}";

            var respPayload = new AttackResponsePayload
            {
                AttackerHandle = attacker.Handle,
                TargetHandle = target.Handle,
                AttackTotal = attackTotal,
                DefenseTN = targetNumber,
                IsHit = isHit,
                IsFumble = isFumble,
                DamageDealt = damageDealt,
                HitLocation = hitLocationName,
                DamageMultiplier = hitMultiplier,
                ArmorSP = armorResult?.SpFaced ?? 0,
                ArmorAbsorbed = armorResult?.Absorbed ?? 0,
                ArmorAblated = armorResult?.Ablated ?? false,
                TargetRemainingHealth = target.Health.CurrentHealth,
                TargetMaxHealth = target.Health.MaxHealth,
                TargetWoundStatus = target.Health.GetWoundStatusDescription(),
                WeaponName = weaponToUse.Name,
                AmmoLeft = weaponToUse.UsesAmmo ? weaponToUse.CurrentAmmo : 0,
                AmmoCapacity = weaponToUse.UsesAmmo ? weaponToUse.MagazineCapacity : 0,
                AmmoName = weaponToUse.UsesAmmo ? (loadedAmmo?.Name ?? "Munizioni standard") : string.Empty,
                // Locazione e armatura sono righe a parte nel pannello: qui solo l'esito
                Message = isHit
                    ? (damageDealt > 0
                        ? $"COLPO A SEGNO! {attacker.Handle} usa '{weaponToUse.Name}' e infligge {damageDealt} danni ({weaponToUse.DamageDice})!"
                        : $"COLPO A SEGNO, MA L'ARMATURA REGGE! {attacker.Handle} colpisce con '{weaponToUse.Name}' ({weaponToUse.DamageDice}) senza infliggere danni.")
                    : isFumble
                        ? $"FALLIMENTO CRITICO! {attacker.Handle} sbaglia clamorosamente con '{weaponToUse.Name}'."
                        : $"MANCATO! {attacker.Handle} fallisce l'attacco con '{weaponToUse.Name}'."
            };

            return new AttackOutcome(null, respPayload, log, ammoSpent);
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

    private sealed record SkillRollResult(string StatName, int StatValue, int SkillLevel, int Penalty, GameDice.D10Roll Dice, int Total);

    // Tiro di abilità (d10 esplosivo + stat + livello + penalità ferite); null se l'abilità non esiste
    private static SkillRollResult? RollSkill(Character actor, string skillName)
    {
        if (!Skills.TryGetStatForSkill(skillName, out string statName)) return null;

        int statVal = actor.Stats.GetStatValue(statName);
        int skillLvl = actor.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
        int penalty = actor.Health.GetWoundPenalty();
        var dice = GameDice.RollExplodingD10Detailed();
        return new SkillRollResult(statName, statVal, skillLvl, penalty, dice, statVal + skillLvl + penalty + dice.Total);
    }

    private async Task ResolveSkillCheckAsync(ClientSession reporter, Character actor, SkillCheckRequestPayload skillReq)
    {
        string skillName = InputLimits.Clean(skillReq.SkillName, InputLimits.MaxNameFieldLength);
        var rolled = RollSkill(actor, skillName);
        if (rolled == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
            {
                Message = $"Abilità '{skillName}' sconosciuta: usa /skills per l'elenco."
            }));
            return;
        }

        string statName = rolled.StatName;
        int statVal = rolled.StatValue;
        int skillLvl = rolled.SkillLevel;
        int penalty = rolled.Penalty;
        var roll = rolled.Dice;
        int diceVal = roll.Total;
        int totalResult = rolled.Total;
        bool isFumble = roll.IsFumble;

        // Se il master ha aperto un check su questa abilità per questo personaggio, il tiro va allo spoglio
        // (il TN è quello del check) e non viene mostrato agli altri finché non hanno tirato tutti.
        Game? checkGame;
        lock (_gamesLock)
        {
            checkGame = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
        }

        if (checkGame != null)
        {
            CheckRegistration reg;
            Game.Check? completed;
            lock (_gamesLock)
            {
                reg = RegisterCheckRoll(checkGame, Game.CheckKind.Skill, skillName, actor.Handle, totalResult, isFumble, out completed);
            }

            if (reg != CheckRegistration.NotInCheck)
            {
                Console.WriteLine($"[CHECK] {actor.Handle} tira '{skillName}' ({statName}:{statVal} + Lvl:{skillLvl} + d10:{diceVal}{(roll.Exploded ? " esploso" : "")} - Pen:{Math.Abs(penalty)}) = {totalResult} ({reg})");
                await ReplyCheckRegistrationAsync(reporter, actor, reg, $"prova di {skillName}");
                if (completed != null) await FinalizeCheckAsync(checkGame, completed, forced: false);
                return;
            }
        }

        bool isSuccess = !isFumble && totalResult >= skillReq.Difficulty;

        string outcome = isFumble ? "FALLIMENTO CRITICO" : isSuccess ? "SUCCESSO" : "FALLIMENTO";
        Console.WriteLine($"[SKILL] {actor.Handle} prova '{skillName}' ({statName}:{statVal} + Lvl:{skillLvl} + d10:{diceVal}{(roll.Exploded ? " esploso" : "")} - Pen:{Math.Abs(penalty)}) = {totalResult} vs TN {skillReq.Difficulty} -> {outcome}");

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
            IsFumble = isFumble,
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

            // Il master non interpreta personaggi: per quelli del game usa gli NPC (/npc, /as). Senza questo
            // divieto un /link lo riporterebbe al ruolo di giocatore.
            if (client.Player.Role == Player.RoleType.master)
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
                {
                    Success = false,
                    Message = "Un master non collega personaggi: per usare una scheda nel tuo game aggiungila come NPC con /npc add."
                }));
                return;
            }

            // Una scheda è usata da un solo "attore" alla volta: o un giocatore (questa sessione) o un NPC di un
            // game (/npc add), mai entrambi. Il controllo "già in
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

                // Ordine dei lock: _linkLock, poi _gamesLock (come in /npc add)
                string? npcGame = null;
                lock (_gamesLock)
                {
                    npcGame = _games.FirstOrDefault(g => g.NPCs.Any(n =>
                        n.Handle.Equals(linkReq.CharacterHandle, StringComparison.OrdinalIgnoreCase)))?.Name;
                }

                Console.WriteLine($"[LINK] '{client.Player.Username}' chiede '{linkReq.CharacterHandle}': " +
                                  (holder != null ? $"occupato da '{holder.Player.Username}'."
                                   : npcGame != null ? $"NPC del game '{npcGame}'."
                                   : "libero."));

                if (holder != null)
                {
                    Console.WriteLine($"[LINK] RIFIUTATO: '{linkReq.CharacterHandle}' è già in uso da '{holder.Player.Username}'.");
                    failure = new LinkResponsePayload
                    {
                        Success = false,
                        Message = $"Il personaggio '{linkReq.CharacterHandle}' è già in uso da '{holder.Player.Username}'."
                    };
                }
                else if (npcGame != null)
                {
                    failure = new LinkResponsePayload
                    {
                        Success = false,
                        Message = $"Il personaggio '{linkReq.CharacterHandle}' è un NPC del game '{npcGame}': non è disponibile finché il master non lo rimuove."
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
                        Message = $"La scheda '{safeHandle}' esiste già. Scegli un altro handle."
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
            Message = $"Personaggio '{safeHandle}' ({newChar.Role}) creato con successo! Puoi associarlo al tuo account con /link {safeHandle}, o (se sei master) usarlo come NPC con /npc add {safeHandle}."
        }));
    }

    private const int MaxNpcsPerGame = 30;

    // Comandi che il master può dare a un NPC con /as (gli stessi dei giocatori, con l'NPC come attore)
    private static readonly HashSet<OpCode> NpcAllowedCommands =
        [OpCode.C2S_Attack, OpCode.C2S_SkillCheck, OpCode.C2S_Move, OpCode.C2S_Init, OpCode.C2S_Reload, OpCode.C2S_Throw];

    private static Task SendNpcResponseAsync(ClientSession client, NpcResponsePayload payload)
        => client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_NpcResponse, payload));

    /// <summary>
    /// /npc add|list|remove|status: gestione degli NPC del game, riservata al suo master. Un NPC è una qualunque
    /// scheda di Data/Characters (creata con /create) collegata al game invece che a un giocatore: lo stato
    /// (ferite, posizione) si salva come per i giocatori, e uscire dal game non cancella la scheda. La stessa
    /// scheda può essere un PG o un NPC, ma non entrambe le cose nello stesso momento.
    /// </summary>
    private async Task HandleNpcAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<NpcRequestPayload>();
        string action = (req?.Action ?? string.Empty).Trim().ToLowerInvariant();

        if (req == null || action.Length == 0)
        {
            await SendNpcResponseAsync(client, new NpcResponsePayload
            {
                Success = false,
                Message = "Uso: /npc add <handle> | /npc list | /npc remove <handle> | /npc status <handle>"
            });
            return;
        }

        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
        }

        if (game == null || client.Player.Role != Player.RoleType.master)
        {
            await SendNpcResponseAsync(client, new NpcResponsePayload
            {
                Success = false,
                Action = action,
                Message = "Solo il master di un game può gestire gli NPC: entra con /join <game> master."
            });
            return;
        }

        string handle = InputLimits.Clean(req.Handle, InputLimits.MaxNameFieldLength);

        switch (action)
        {
            case "add":
                await HandleNpcAddAsync(client, game, handle);
                break;

            case "list":
            {
                List<NpcInfoPayload> infos;
                lock (_gamesLock)
                {
                    infos = game.NPCs.Select(n => new NpcInfoPayload
                    {
                        Handle = n.Handle,
                        Role = n.Role,
                        CurrentHealth = n.Health.CurrentHealth,
                        MaxHealth = n.Health.MaxHealth,
                        WoundStatus = n.Health.GetWoundStatusDescription(),
                        Position = n.Position
                    }).ToList();
                }

                await SendNpcResponseAsync(client, new NpcResponsePayload
                {
                    Success = true,
                    Action = action,
                    Message = infos.Count == 0 ? "Nessun NPC in questo game." : $"{infos.Count} NPC nel game '{game.Name}'.",
                    Npcs = infos
                });
                break;
            }

            case "remove":
            {
                bool removed;
                lock (_gamesLock)
                {
                    removed = game.NPCs.RemoveAll(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) > 0;
                    if (removed)
                    {
                        game.InitRolls.RemoveAll(r => r.PlayerName.Equals(handle, StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (removed) Console.WriteLine($"[NPC] {client.Player.Username} ha rimosso l'NPC '{handle}' da '{game.Name}'.");

                await SendNpcResponseAsync(client, new NpcResponsePayload
                {
                    Success = removed,
                    Action = action,
                    Message = removed ? $"NPC '{handle}' rimosso." : $"NPC '{handle}' non trovato in questo game."
                });
                break;
            }

            case "status":
            {
                Character? npc;
                lock (_gamesLock)
                {
                    npc = game.NPCs.FirstOrDefault(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
                }

                await SendNpcResponseAsync(client, new NpcResponsePayload
                {
                    Success = npc != null,
                    Action = action,
                    Message = npc != null ? $"Scheda dell'NPC '{npc.Handle}'." : $"NPC '{handle}' non trovato in questo game.",
                    Character = npc
                });
                break;
            }

            default:
                await SendNpcResponseAsync(client, new NpcResponsePayload
                {
                    Success = false,
                    Action = action,
                    Message = "Azione sconosciuta. Uso: /npc add <handle> | list | remove <handle> | status <handle>"
                });
                break;
        }
    }

    // Collega al game una scheda NPC già esistente. Una scheda può essere NPC di un solo game alla volta.
    private async Task HandleNpcAddAsync(ClientSession client, Game game, string handle)
    {
        Task Respond(bool ok, string message) => SendNpcResponseAsync(client, new NpcResponsePayload { Success = ok, Action = "add", Message = message });

        if (!CharacterHandleValidator.IsValid(handle))
        {
            await Respond(false, CharacterHandleValidator.RulesDescription);
            return;
        }

        Character npc;
        try
        {
            npc = CharacterRepository.LoadCharacter(handle);
        }
        catch (FileNotFoundException)
        {
            await Respond(false, $"Scheda '{handle}' non trovata. Creala con: /create <archetipo> {handle}");
            return;
        }
        catch (CharacterLoadException ex)
        {
            await Respond(false, $"{ex.Message} Contatta l'amministratore del server.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Security.SecurityException)
        {
            Console.WriteLine($"[NPC] Errore con la scheda '{handle}': {ex.Message}");
            await Respond(false, "Impossibile caricare la scheda: errore del server, riprova più tardi.");
            return;
        }

        npc.Handle = handle;

        // Una scheda è usata da un solo "attore" alla volta: un giocatore (/link) oppure un NPC di un game.
        // Ordine dei lock: _linkLock, poi _gamesLock (come in /link).
        string? error = null;
        lock (_linkLock)
        {
            var holder = _connectedClients.Values.FirstOrDefault(c =>
                c.LinkedCharacterHandle != null
                && c.LinkedCharacterHandle.Equals(handle, StringComparison.OrdinalIgnoreCase));

            lock (_gamesLock)
            {
                var otherGame = _games.FirstOrDefault(g => g.NPCs.Any(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)));

                if (game.NPCs.Count >= MaxNpcsPerGame)
                {
                    error = $"Limite raggiunto: al massimo {MaxNpcsPerGame} NPC per game.";
                }
                else if (holder != null)
                {
                    error = $"Il personaggio '{handle}' è in uso da '{holder.Player.Username}': non può essere un NPC finché lo interpreta.";
                }
                else if (otherGame != null)
                {
                    error = ReferenceEquals(otherGame, game)
                        ? $"L'NPC '{handle}' è già in questo game."
                        : $"L'NPC '{handle}' è già in uso nel game '{otherGame.Name}'.";
                }
                else if (game.Players.Any(p => p.Character != null && p.Character.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"In questo game esiste già un personaggio chiamato '{handle}'.";
                }
                else
                {
                    game.NPCs.Add(npc);
                }
            }
        }

        if (error != null)
        {
            await Respond(false, error);
            return;
        }

        Console.WriteLine($"[NPC] {client.Player.Username} ha aggiunto l'NPC '{handle}' ({npc.Role}) a '{game.Name}'.");
        await Respond(true, $"NPC '{handle}' ({npc.Role}) aggiunto al game. Usa /as {handle} attack|skill|move|init ... per farlo agire.");
    }

    /// <summary>
    /// /as &lt;npc&gt; &lt;comando&gt;: il master esegue attack, skill, move o init con un suo NPC come attore.
    /// Riusa le stesse routine dei giocatori; le uniche differenze sono che l'attore è l'NPC e che nulla
    /// viene salvato su disco.
    /// </summary>
    private async Task HandleAsAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<AsRequestPayload>();

        Task Fail(string message) => client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        string actorHandle = InputLimits.Clean(req?.ActorHandle, InputLimits.MaxNameFieldLength);
        if (req == null || actorHandle.Length == 0)
        {
            await Fail("Uso: /as <npc> <attack|skill|move|init> ...");
            return;
        }

        Game? game;
        Character? npc = null;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            if (game != null && client.Player.Role == Player.RoleType.master)
            {
                npc = game.NPCs.FirstOrDefault(n => n.Handle.Equals(actorHandle, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (game == null || client.Player.Role != Player.RoleType.master)
        {
            await Fail("Solo il master di un game può usare /as.");
            return;
        }

        if (npc == null)
        {
            await Fail($"NPC '{actorHandle}' non trovato in questo game: vedi /npc list.");
            return;
        }

        if (!NpcAllowedCommands.Contains(req.InnerOpCode))
        {
            await Fail("Con /as si possono usare solo: attack, skill, move, init, reload, throw.");
            return;
        }

        try
        {
            switch (req.InnerOpCode)
            {
                case OpCode.C2S_Attack:
                {
                    var attackReq = JsonSerializer.Deserialize<AttackRequestPayload>(req.InnerPayload);
                    if (attackReq == null) { await Fail("Comando non valido."); return; }

                    attackReq.TargetHandle = InputLimits.Clean(attackReq.TargetHandle, InputLimits.MaxNameFieldLength);
                    attackReq.WeaponOrSkill = InputLimits.Clean(attackReq.WeaponOrSkill, InputLimits.MaxNameFieldLength);
                    if (attackReq.TargetHandle.Length == 0) { await Fail("Uso: /as <npc> attack <bersaglio> [arma|skill]"); return; }

                    await ResolveAttackAsync(client, npc, attackReq);
                    break;
                }

                case OpCode.C2S_SkillCheck:
                {
                    var skillReq = JsonSerializer.Deserialize<SkillCheckRequestPayload>(req.InnerPayload);
                    if (skillReq == null || string.IsNullOrWhiteSpace(skillReq.SkillName)) { await Fail("Uso: /as <npc> skill <abilità> [TN]"); return; }

                    await ResolveSkillCheckAsync(client, npc, skillReq);
                    break;
                }

                case OpCode.C2S_Move:
                {
                    var moveReq = JsonSerializer.Deserialize<MoveRequestPayload>(req.InnerPayload);
                    if (moveReq?.Position == null) { await Fail("Uso: /as <npc> move <X> <Y> [Z]"); return; }

                    await ResolveMoveAsync(client, npc, moveReq);
                    break;
                }

                case OpCode.C2S_Init:
                {
                    var initReq = JsonSerializer.Deserialize<InitRequest>(req.InnerPayload);
                    await ResolveInitiativeAsync(client, npc, initReq?.Modifiers);
                    break;
                }

                case OpCode.C2S_Reload:
                {
                    var reloadReq = JsonSerializer.Deserialize<ReloadRequestPayload>(req.InnerPayload);
                    await ResolveReloadAsync(client, npc, reloadReq);
                    break;
                }

                case OpCode.C2S_Throw:
                {
                    var throwReq = JsonSerializer.Deserialize<ThrowRequestPayload>(req.InnerPayload);
                    if (throwReq?.Position == null) { await Fail("Uso: /as <npc> throw <X> <Y> [Z] [arma]"); return; }

                    throwReq.WeaponKey = InputLimits.Clean(throwReq.WeaponKey, InputLimits.MaxNameFieldLength);
                    await ResolveThrowAsync(client, npc, throwReq);
                    break;
                }
            }
        }
        catch (JsonException)
        {
            await Fail("Comando non valido.");
        }
    }

    private async Task HandleLeaveAsync(ClientSession client)
    {
        string gameName = string.Empty;
        bool gameClosed = false;
        bool wasMaster = false;
        List<Player> remaining = [];
        Game? leftGame = null;
        Game.Check? completedCheck = null;

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            if (game != null)
            {
                leftGame = game;
                gameName = game.Name;
                wasMaster = client.Player.Role == Player.RoleType.master;

                game.Players.Remove(client.Player);

                // Un tiro di iniziativa di un personaggio che esce non ha più senso nel game
                if (client.Player.Character != null)
                {
                    game.InitRolls.RemoveAll(r => r.PlayerName.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    game.TurnOrder.RemoveAll(t => t.Handle.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    completedCheck = PruneCheckEntry(game, client.Player.Character.Handle);
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

        await NotifyPlayersAsync(remaining, notice);

        // Se chi è uscito era l'ultimo a dover tirare, il check si spoglia con chi è rimasto
        if (completedCheck != null && leftGame != null && !gameClosed)
        {
            await FinalizeCheckAsync(leftGame, completedCheck, forced: false);
        }
    }

    // Notifica testuale ai giocatori indicati (le sessioni non più collegate vengono saltate).
    private async Task NotifyPlayersAsync(IEnumerable<Player> players, string message)
    {
        foreach (var other in players)
        {
            var session = _connectedClients.Values.FirstOrDefault(s => ReferenceEquals(s.Player, other));
            if (session == null) continue;

            try
            {
                await session.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GAME] Notifica non consegnata a [{session.IdShort}]: {ex.Message}");
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
                    else if (client.Player.Character != null
                        && game.NPCs.Any(n => n.Handle.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase)))
                    {
                        denialMessage = $"Nel gioco '{joinReq.GameName}' esiste già un NPC chiamato '{client.Player.Character.Handle}'.";
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
            // /init del master = /check init per tutti i personaggi del game
            Game? game;
            lock (_gamesLock)
            {
                game = _games.FirstOrDefault(g =>
                    g.Players.Any(p => p.Username == client.Player.Username && p.Role == Player.RoleType.master));
            }

            if (game != null)
            {
                await OpenCheckAsync(client, game, new CheckRequestPayload { Action = "init" });
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

    // ---------------------------------------------------------------------------------------------
    // /check: prove collettive (abilità o iniziativa) aperte dal master
    // ---------------------------------------------------------------------------------------------

    private enum CheckRegistration { NotInCheck, Registered, AlreadyRolled }

    private const int MaxCheckHandles = 64;
    private const int MaxCheckTargetNumber = 99;

    // Da chiamare sotto _gamesLock. Associa il tiro alla voce del check aperto (stesso tipo, stessa abilità,
    // stesso personaggio). Vale il primo tiro. Se è l'ultimo che mancava, chiude il check e lo restituisce.
    private static CheckRegistration RegisterCheckRoll(Game game, Game.CheckKind kind, string? skillName, string handle,
        int total, bool isFumble, out Game.Check? completed)
    {
        completed = null;
        var check = game.ActiveCheck;
        if (check == null || check.Kind != kind) return CheckRegistration.NotInCheck;
        if (kind == Game.CheckKind.Skill && !check.SkillName.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            return CheckRegistration.NotInCheck;

        var entry = check.Entries.FirstOrDefault(e => e.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return CheckRegistration.NotInCheck;
        if (entry.Total.HasValue) return CheckRegistration.AlreadyRolled;

        entry.Total = total;
        entry.IsFumble = isFumble;
        entry.Sequence = ++check.RollCounter;

        if (check.Entries.All(e => e.Total.HasValue))
        {
            game.ActiveCheck = null;
            completed = check;
        }

        return CheckRegistration.Registered;
    }

    // Da chiamare sotto _gamesLock. Toglie dal check aperto il personaggio che esce dal game; se così non
    // manca più nessuno restituisce il check da spogliare, se non resta nessuno lo annulla.
    private static Game.Check? PruneCheckEntry(Game game, string handle)
    {
        var check = game.ActiveCheck;
        if (check == null) return null;

        check.Entries.RemoveAll(e => e.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (check.Entries.Count == 0)
        {
            game.ActiveCheck = null;
            return null;
        }

        if (check.Entries.All(e => e.Total.HasValue))
        {
            game.ActiveCheck = null;
            return check;
        }

        return null;
    }

    private static bool IsKnockedOut(Character c) => c.Health.CurrentHealth <= 0 || c.Health.GetWoundPenalty() <= -99;

    private async Task ReplyCheckRegistrationAsync(ClientSession reporter, Character actor, CheckRegistration reg, string what)
    {
        string message = reg == CheckRegistration.Registered
            ? $"Tiro registrato per {actor.Handle} ({what}): i risultati usciranno allo spoglio."
            : $"{actor.Handle} ha già tirato per questo check ({what}): vale il primo tiro.";
        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));
    }

    private async Task HandleCheckAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<CheckRequestPayload>();
        if (req == null) return;

        async Task Fail(string message) =>
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
        }

        if (game == null || client.Player.Role != Player.RoleType.master)
        {
            await Fail("Solo il master di un game può usare /check.");
            return;
        }

        string action = InputLimits.Clean(req.Action, 16).ToLowerInvariant();
        switch (action)
        {
            case "skill":
            case "init":
                await OpenCheckAsync(client, game, req);
                break;

            case "status":
            {
                string text;
                lock (_gamesLock)
                {
                    var check = game.ActiveCheck;
                    if (check == null)
                    {
                        text = "Nessun check aperto.";
                    }
                    else
                    {
                        string what = check.Kind == Game.CheckKind.Skill ? $"{check.SkillName} (TN {check.TargetNumber})" : "iniziativa";
                        string done = string.Join(", ", check.Entries.Where(e => e.Total.HasValue).Select(e => e.Handle));
                        string waiting = string.Join(", ", check.Entries.Where(e => !e.Total.HasValue).Select(e => e.Handle));
                        text = $"Check {what}: hanno tirato [{(done.Length > 0 ? done : "-")}], in attesa di [{(waiting.Length > 0 ? waiting : "-")}].";
                    }
                }
                await Fail(text);
                break;
            }

            case "close":
            {
                Game.Check? check;
                lock (_gamesLock)
                {
                    check = game.ActiveCheck;
                    game.ActiveCheck = null;
                }

                if (check == null)
                {
                    await Fail("Nessun check aperto.");
                }
                else if (!check.Entries.Any(e => e.Total.HasValue))
                {
                    await Fail("Nessuno ha ancora tirato: check annullato.");
                }
                else
                {
                    await FinalizeCheckAsync(game, check, forced: true);
                }
                break;
            }

            case "cancel":
            {
                bool had;
                lock (_gamesLock)
                {
                    had = game.ActiveCheck != null;
                    game.ActiveCheck = null;
                }

                if (!had)
                {
                    await Fail("Nessun check aperto.");
                }
                else
                {
                    await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Il master ha annullato il check." }));
                }
                break;
            }

            default:
                await Fail("Uso: /check skill <abilità> <TN> [personaggi...] | /check init [personaggi...] | /check status | /check close | /check cancel");
                break;
        }
    }

    private async Task OpenCheckAsync(ClientSession master, Game game, CheckRequestPayload req)
    {
        async Task Fail(string message) =>
            await master.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        bool isSkill = req.Action.Equals("skill", StringComparison.OrdinalIgnoreCase);
        string skillName = string.Empty;
        int targetNumber = 0;

        if (isSkill)
        {
            skillName = InputLimits.Clean(req.SkillName, InputLimits.MaxNameFieldLength);
            if (!Skills.TryGetStatForSkill(skillName, out _))
            {
                await Fail($"Abilità '{skillName}' sconosciuta: usa /skills per l'elenco.");
                return;
            }

            targetNumber = req.TargetNumber;
            if (targetNumber < 1 || targetNumber > MaxCheckTargetNumber)
            {
                await Fail($"TN non valido: usa un valore tra 1 e {MaxCheckTargetNumber}.");
                return;
            }
        }

        var handles = (req.Handles ?? [])
            .Select(h => InputLimits.Clean(h, InputLimits.MaxNameFieldLength))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCheckHandles)
            .ToList();

        var combatants = new List<Combatant>();
        var excluded = new List<string>();
        string? error = null;

        lock (_gamesLock)
        {
            if (game.ActiveCheck != null)
            {
                error = "C'è già un check aperto: usa /check status, /check close o /check cancel.";
            }
            else
            {
                var candidates = new List<Combatant>();
                var unknown = new List<string>();

                if (handles.Count == 0)
                {
                    foreach (var p in game.Players)
                    {
                        if (p.Role == Player.RoleType.master || p.Character == null) continue;
                        var c = FindCombatantInGame(game, p.Character.Handle);
                        if (c != null) candidates.Add(c);
                    }
                    candidates.AddRange(game.NPCs.Select(n => new Combatant(n, null)));
                }
                else
                {
                    foreach (var h in handles)
                    {
                        var c = FindCombatantInGame(game, h);
                        if (c == null) unknown.Add(h);
                        else candidates.Add(c);
                    }
                }

                if (unknown.Count > 0)
                {
                    error = $"Personaggi non trovati in questo game: {string.Join(", ", unknown)}.";
                }
                else
                {
                    foreach (var c in candidates)
                    {
                        if (IsKnockedOut(c.Character)) excluded.Add(c.Character.Handle);
                        else if (!combatants.Any(x => ReferenceEquals(x.Character, c.Character))) combatants.Add(c);
                    }

                    if (combatants.Count == 0)
                    {
                        error = "Nessun personaggio disponibile per il check (nessuno in gioco, o tutti K.O.).";
                    }
                    else
                    {
                        game.ActiveCheck = new Game.Check
                        {
                            Kind = isSkill ? Game.CheckKind.Skill : Game.CheckKind.Initiative,
                            SkillName = skillName,
                            TargetNumber = targetNumber,
                            Entries = combatants.Select(x => new Game.CheckEntry { Handle = x.Character.Handle }).ToList()
                        };
                    }
                }
            }
        }

        if (error != null)
        {
            await Fail(error);
            return;
        }

        // Gli NPC non hanno una sessione che tiri per loro: tira il server all'apertura
        Game.Check? completed = null;
        var npcRolled = new List<string>();
        foreach (var c in combatants.Where(x => x.Session == null))
        {
            int total;
            bool fumble = false;
            if (isSkill)
            {
                var r = RollSkill(c.Character, skillName);
                if (r == null) continue;
                total = r.Total;
                fumble = r.Dice.IsFumble;
            }
            else
            {
                total = RollInitiative(c.Character, null).Total;
            }

            lock (_gamesLock)
            {
                RegisterCheckRoll(game, isSkill ? Game.CheckKind.Skill : Game.CheckKind.Initiative, skillName,
                    c.Character.Handle, total, fumble, out var done);
                if (done != null) completed = done;
            }
            npcRolled.Add(c.Character.Handle);
        }

        string what = isSkill ? $"prova di {skillName}" : "iniziativa";
        Console.WriteLine($"[CHECK] {master.Player.Username} apre un check di {what}{(isSkill ? $" (TN {targetNumber})" : "")}: {string.Join(", ", combatants.Select(x => x.Character.Handle))}");

        // Avviso ai giocatori iscritti (il TN resta nascosto fino allo spoglio)
        foreach (var c in combatants.Where(x => x.Session != null))
        {
            string text = isSkill
                ? $"Il master ha aperto una prova di {skillName} per {c.Character.Handle}: tira con /skill {skillName}."
                : $"Il master ha avviato il tiro di iniziativa: tira con /init ({c.Character.Handle}).";
            try
            {
                await c.Session!.SendPacketAsync(isSkill
                    ? NetworkPacket.Create(OpCode.S2C_Notification, new { Message = text })
                    : NetworkPacket.Create(OpCode.S2C_InitRollRequest, new InitRollRequestPayload { Message = text }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHECK] Avviso non consegnato a [{c.Session!.IdShort}]: {ex.Message}");
            }
        }

        var waitingFor = combatants.Where(x => x.Session != null).Select(x => x.Character.Handle).ToList();
        string masterMsg = $"Check di {what} aperto{(isSkill ? $" (TN {targetNumber})" : "")}. In attesa di: {(waitingFor.Count > 0 ? string.Join(", ", waitingFor) : "-")}."
            + (npcRolled.Count > 0 ? $" NPC già tirati dal server: {string.Join(", ", npcRolled)}." : "")
            + (excluded.Count > 0 ? $" Esclusi (K.O.): {string.Join(", ", excluded)}." : "");
        await Fail(masterMsg);

        if (completed != null) await FinalizeCheckAsync(game, completed, forced: false);
    }

    // Spoglio: esiti contro il TN (abilità) oppure ordine di gioco (iniziativa), inviato a tutto il game
    private async Task FinalizeCheckAsync(Game game, Game.Check check, bool forced)
    {
        List<ClientSession> audience;
        var payload = new CheckResultPayload
        {
            IsInitiative = check.Kind == Game.CheckKind.Initiative,
            SkillName = check.SkillName,
            TargetNumber = check.TargetNumber,
            Forced = forced
        };

        lock (_gamesLock)
        {
            audience = GetGameSessions(game);

            if (check.Kind == Game.CheckKind.Skill)
            {
                foreach (var e in check.Entries)
                {
                    bool has = e.Total.HasValue;
                    payload.Entries.Add(new CheckEntryResult
                    {
                        Handle = e.Handle,
                        HasRoll = has,
                        Total = e.Total ?? 0,
                        IsFumble = e.IsFumble,
                        IsSuccess = has && !e.IsFumble && e.Total!.Value >= check.TargetNumber
                    });
                }
            }
            else
            {
                // Più alto prima; a parità REF più alto, poi chi ha tirato per primo
                var ordered = check.Entries.Where(e => e.Total.HasValue)
                    .OrderByDescending(e => e.Total!.Value)
                    .ThenByDescending(e => FindCombatantInGame(game, e.Handle)?.Character.Stats.REF ?? 0)
                    .ThenBy(e => e.Sequence)
                    .ToList();

                int pos = 1;
                foreach (var e in ordered)
                {
                    payload.Entries.Add(new CheckEntryResult { Handle = e.Handle, HasRoll = true, Total = e.Total!.Value, Position = pos++ });
                }
                foreach (var e in check.Entries.Where(e => !e.Total.HasValue))
                {
                    payload.Entries.Add(new CheckEntryResult { Handle = e.Handle });
                }

                game.TurnOrder = ordered.Select(e => new Game.TurnEntry { Handle = e.Handle, Initiative = e.Total!.Value }).ToList();
                game.InitRolls = ordered.Select(e => new Game.Roll
                {
                    Value = e.Total!.Value,
                    PlayerName = e.Handle,
                    Timestamp = TimeOnly.FromDateTime(DateTime.UtcNow)
                }).ToList();
            }
        }

        Console.WriteLine($"[CHECK] Spoglio {(payload.IsInitiative ? "iniziativa" : $"{check.SkillName} TN {check.TargetNumber}")}{(forced ? " (chiuso dal master)" : "")}: "
            + string.Join(", ", payload.Entries.Select(e => e.HasRoll ? $"{e.Handle}={e.Total}" : $"{e.Handle}=-")));

        var resultPacket = NetworkPacket.Create(OpCode.S2C_CheckResult, payload);
        foreach (var session in audience)
        {
            try { await session.SendPacketAsync(resultPacket); }
            catch (Exception ex) { Console.WriteLine($"[CHECK] Spoglio non consegnato a [{session.IdShort}]: {ex.Message}"); }
        }
    }

    private sealed record InitiativeRollResult(int Ref, int CombatSense, int Cyber, int Modifier, int Dice, int Total);

    // Iniziativa: REF + CombatSense (solo i Solo) + cyberware INIT + modificatore + d10
    private static InitiativeRollResult RollInitiative(Character actor, string? modifiers)
    {
        int refStat = actor.Stats.REF;

        int combatSense = 0;
        if (actor.Role == Character.RoleType.Solo)
        {
            combatSense = actor.Skills.TryGetValue("CombatSense", out int csLvl) ? csLvl : 0;
        }

        int cyberInit = SumCyberBonus(actor, "INIT");

        int requestedMod = 0;
        if (!string.IsNullOrWhiteSpace(modifiers))
        {
            int.TryParse(modifiers.Trim(), out requestedMod);
        }

        int dice = (int)GameDice.Roll("1d10").Total;
        return new InitiativeRollResult(refStat, combatSense, cyberInit, requestedMod, dice, refStat + combatSense + cyberInit + requestedMod + dice);
    }

    /// <summary>
    /// Tiro di iniziativa di <paramref name="actor"/> nel game di <paramref name="reporter"/> (la sessione che ha
    /// dato il comando: il giocatore stesso, o il master che tira per un NPC). Con un check di iniziativa aperto il tiro va
    /// allo spoglio (vale il primo); senza, un nuovo tiro sostituisce il precedente.
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

        var init = RollInitiative(actor, modifiers);
        int total = init.Total;

        CheckRegistration reg;
        Game.Check? completed;
        lock (_gamesLock)
        {
            reg = RegisterCheckRoll(game, Game.CheckKind.Initiative, null, actor.Handle, total, false, out completed);

            if (reg == CheckRegistration.NotInCheck)
            {
                // Nessun check di iniziativa aperto per questo personaggio: tiro libero, l'ultimo sostituisce il precedente
                game.InitRolls.RemoveAll(r => r.PlayerName.Equals(actor.Handle, StringComparison.OrdinalIgnoreCase));
                game.InitRolls.Add(new Game.Roll
                {
                    Value = total,
                    PlayerName = actor.Handle,
                    Timestamp = TimeOnly.FromDateTime(DateTime.UtcNow)
                });
            }
        }

        Console.WriteLine($"[INIT] {reporter.Player.Username} ({actor.Handle}) tira iniziativa: REF {init.Ref} + CombatSense {init.CombatSense} + Cyberware {init.Cyber} + Mod {init.Modifier} + d10 {init.Dice} = {total}{(reg == CheckRegistration.NotInCheck ? "" : $" ({reg})")}");

        if (reg != CheckRegistration.NotInCheck)
        {
            await ReplyCheckRegistrationAsync(reporter, actor, reg, "iniziativa");
            if (completed != null) await FinalizeCheckAsync(game, completed, forced: false);
            return;
        }

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
        {
            Message = $"Tiro di iniziativa registrato per {actor.Handle}: {total} (REF {init.Ref} + CS {init.CombatSense} + Cyber {init.Cyber} + d10 {init.Dice})."
        }));
    }

    private async Task HandleGetCharactersAsync(ClientSession client)
    {
        // Chi sta usando quale scheda: NPC di un game, oppure giocatore collegato
        var npcOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_gamesLock)
        {
            foreach (var game in _games)
            {
                foreach (var npc in game.NPCs) npcOwner[npc.Handle] = game.Name;
            }
        }

        var linkedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _connectedClients.Values)
        {
            string? linked = session.LinkedCharacterHandle;
            if (linked != null && session.Player.Character != null) linkedBy[linked] = session.Player.Username;
        }

        var charactersResponsePayload = new CharactersResponsePayload();

        foreach (var character in CharacterRepository.ListCharacters())
        {
            string status =
                npcOwner.TryGetValue(character.Handle, out var gameName) ? $"NPC di '{gameName}'" :
                linkedBy.TryGetValue(character.Handle, out var player) ? $"in uso da {player}" :
                "libero";

            charactersResponsePayload.Characters.Add(new CharactersResponsePayload.CharacterNode
            {
                Handle = character.Handle,
                Role = character.Role,
                Status = status
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

    // Rimuove il giocatore da tutti i game. Restituisce, per ogni game rimasto aperto di cui era master,
    // il nome e i giocatori rimasti (da avvisare).
    private List<(string GameName, List<Player> Remaining)> RemovePlayerFromGames(Player player, out List<(Game Game, Game.Check Check)> completedChecks)
    {
        var masterNotices = new List<(string, List<Player>)>();
        completedChecks = [];

        lock (_gamesLock)
        {
            foreach (var game in _games.Where(g => g.Players.Contains(player)).ToList())
            {
                game.Players.Remove(player);

                if (player.Character != null)
                {
                    game.InitRolls.RemoveAll(r => r.PlayerName.Equals(player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    game.TurnOrder.RemoveAll(t => t.Handle.Equals(player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    var done = PruneCheckEntry(game, player.Character.Handle);
                    if (done != null && game.Players.Count > 0) completedChecks.Add((game, done));
                }

                if (game.Players.Count == 0)
                {
                    _games.Remove(game);
                    Console.WriteLine($"[GAME] Game '{game.Name}' rimosso: nessun giocatore rimasto.");
                }
                else if (player.Role == Player.RoleType.master)
                {
                    Console.WriteLine($"[GAME] Il master '{player.Username}' ha lasciato '{game.Name}': il ruolo è di nuovo libero.");
                    masterNotices.Add((game.Name, [.. game.Players]));
                }
            }
        }

        return masterNotices;
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
