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
    // Gerarchia dei lock (si prendono sempre in quest'ordine, mai al contrario):
    //   _linkLock -> _gamesLock -> Game.SyncRoot
    // Game.SyncRoot protegge lo stato di gioco di UN game: turni, movimento, azioni, salute, munizioni e
    // posizione dei suoi personaggi. Chi tiene un Game.SyncRoot non deve prendere _gamesLock né il SyncRoot
    // di un altro game.
    // Salvataggio schede: SaveCharacter() prende CharacterRepository._saveLock e poi il SyncRoot del game del
    // personaggio per serializzare la scheda; va chiamato senza tenere alcun lock di gioco.
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
            while (true)
            {
                // Fino al login la connessione ha una scadenza assoluta dalla connessione (i ping non la
                // prolungano): chi apre socket senza autenticarsi non può occupare i posti a lungo.
                using var loginDeadline = new CancellationTokenSource();
                if (!client.IsAuthenticated)
                {
                    var left = TimeSpan.FromSeconds(InputLimits.LoginTimeoutSeconds) - (DateTime.UtcNow - client.ConnectedAtUtc);
                    loginDeadline.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                }

                try
                {
                    line = await client.ReadLineLimitedAsync(InputLimits.MaxLineLength, loginDeadline.Token);
                }
                catch (OperationCanceledException) when (loginDeadline.IsCancellationRequested && !client.IsAuthenticated)
                {
                    Console.WriteLine($"[SERVER] Login non completato entro {InputLimits.LoginTimeoutSeconds}s da [{client.IdShort}]: connessione chiusa.");
                    break;
                }

                if (line == null) break;
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
            await client.CloseAsync();
            var masterNotices = RemovePlayerFromGames(client.Player, out var completedChecks, out var turnNotices, out var deadCharacters);

            // Il salvezza morte può essere scattato al passaggio di turno: le schede dei morti vanno salvate.
            foreach (var dead in deadCharacters) SaveCharacter(dead);

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

                foreach (var (turnPlayers, turnText) in turnNotices)
                {
                    await NotifyPlayersAsync(turnPlayers, turnText);
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
            case OpCode.C2S_Combat:
                await HandleCombatAsync(client, packet);
                break;
            case OpCode.C2S_Next:
                await HandleNextAsync(client);
                break;
            case OpCode.C2S_WhoAmI:
                await HandleWhoAmIAsync(client);
                break;
            case OpCode.C2S_GameStatus:
                await HandleGameStatusAsync(client);
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
            case OpCode.C2S_Unlink:
                await HandleUnlinkAsync(client);
                break;
            case OpCode.C2S_Heal:
                await HandleHealAsync(client, packet);
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
            case OpCode.C2S_Teleport:
                await HandleTeleportAsync(client, packet);
                break;
            case OpCode.C2S_GetArchetypes:
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_ArchetypesResponse,
                    new ArchetypesResponsePayload { Archetypes = CharacterTemplateFactory.GetAvailableArchetypes() }));
                break;
            default:
                Console.WriteLine($"[SERVER] OpCode sconosciuto ricevuto: {packet.OpCode}");
                break;
        }
    }

    private static async Task HandlePingAsync(ClientSession client)
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

    // Fuori da un game (es. /reload di un personaggio non in partita) non c'è un Game.SyncRoot: nessun altro
    // può toccare quella scheda, ma il lock evita comunque di avere un ramo senza protezione.
    private readonly object _noGameSync = new();

    // Il game che contiene il personaggio (come giocatore o NPC), o null se è fuori da ogni game.
    // Prende _gamesLock e lo rilascia prima di restituire: non va chiamato da chi tiene un Game.SyncRoot.
    private Game? FindGameOf(Character character)
    {
        lock (_gamesLock)
        {
            return _games.FirstOrDefault(g =>
                g.NPCs.Any(n => ReferenceEquals(n, character)) || g.Players.Any(p => ReferenceEquals(p.Character, character)));
        }
    }

    // Lock che protegge lo stato del personaggio: il SyncRoot del suo game, oppure uno condiviso se è fuori partita.
    private object SyncFor(Character character) => FindGameOf(character)?.SyncRoot ?? _noGameSync;

    // Salva la scheda serializzandola sotto il lock del suo game: il file è sempre uno stato coerente.
    // Da chiamare SENZA tenere _gamesLock né alcun Game.SyncRoot (vedi la gerarchia dei lock).
    private void SaveCharacter(Character character)
        => CharacterRepository.TrySaveCharacter(character, CharacterRepository.CharactersFolder, SyncFor(character));

    // Pacchetto con una scheda (o un payload che la contiene): la serializzazione avviene sotto il lock del
    // game del personaggio, così il client riceve uno stato coerente. Anche questo SENZA lock già in mano.
    private NetworkPacket CreateCharacterPacket<T>(OpCode opCode, T payload, Character character)
    {
        lock (SyncFor(character))
        {
            return NetworkPacket.Create(opCode, payload);
        }
    }

    /// <summary>Un partecipante al combattimento: il personaggio e la sua sessione (se è un giocatore).</summary>
    private sealed record Combatant(Character Character, ClientSession? Session);

    private sealed record AttackOutcome(string? Error, AttackResponsePayload? Result, string? LogLine, bool AttackerChanged = false);

    // Tiro di stordimento dopo un colpo che ha inflitto danno (solo in combattimento): d10 sotto la soglia
    // BODY - modificatore di stato. Se fallisce il personaggio è stordito. Restituisce la riga di calcolo
    // (null se non applicabile). Da chiamare dopo aver tolto i punti ferita.
    private static string? RollStunSave(Character target)
    {
        if (target.Health.IsDead || target.Health.IsStunned) return null;

        int mod = target.Health.GetStunSaveModifier();
        int threshold = Math.Max(0, target.Stats.BODY - mod);
        int roll = (int)GameDice.Roll("1d10").Total;
        bool resisted = roll < threshold;
        if (!resisted) target.Health.IsStunned = true;

        return $"Tiro di stordimento: d10 {roll} contro meno di {threshold} (BODY {target.Stats.BODY} - stato {mod}): "
            + (resisted ? "resiste" : "STORDITO (non può agire né muoversi finché non si riprende)");
    }

    // Tiro di recupero all'inizio del turno di un personaggio stordito: stessa soglia dello stordimento
    private static string RollStunRecovery(Character c)
    {
        int mod = c.Health.GetStunSaveModifier();
        int threshold = Math.Max(0, c.Stats.BODY - mod);
        int roll = (int)GameDice.Roll("1d10").Total;
        bool recovered = roll < threshold;
        if (recovered) c.Health.IsStunned = false;

        return $"{c.Handle} tenta di riprendersi: d10 {roll} contro meno di {threshold} (BODY {c.Stats.BODY} - stato {mod}): "
            + (recovered ? "si riprende" : "è ancora stordito");
    }

    // Tiro salvezza contro la morte (a inizio turno, chi è in Mortale): d10 inferiore a BODY meno il livello
    // Mortale per sopravvivere. In caso di fallimento segna il personaggio come morto. Restituisce la riga di calcolo.
    private static string RollDeathSave(Character c)
    {
        int body = c.Stats.BODY;
        int level = Math.Max(0, c.Health.MortalLevel);
        int threshold = CombatRules.GetDeathSaveThreshold(body, level);
        int die = (int)GameDice.Roll("1d10").Total;
        bool survives = CombatRules.DeathSaveSurvives(die, body, level);
        if (!survives) c.Health.Kill();

        return $"Salvezza contro la morte di {c.Handle} (Mortale {level}): d10 {die} contro meno di {threshold} (BODY {body} - livello {level}): "
            + (survives ? "sopravvive" : "MUORE");
    }

    // "d10 6 + REF 7 - ferite 2 = 11": ogni addendo con il suo nome, così il calcolo si legge senza ricostruirlo.
    // Extra è un testo opzionale accanto al valore (es. " (10+4)" per un dado esploso).
    private static string FormatSum(IReadOnlyList<(string Label, int Value, string? Extra)> parts)
    {
        var chunks = new List<string>();
        int total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var (label, value, extra) = parts[i];
            total += value;
            if (i == 0) chunks.Add($"{label} {value}{extra}");
            else if (value >= 0) chunks.Add($"+ {label} {value}{extra}");
            else chunks.Add($"- {label} {-value}{extra}");
        }
        return $"{string.Join(" ", chunks)} = {total}";
    }

    // "2d6 => 4 + 3 => 7" (testo della libreria) -> "4 + 3 = 7": i dadi tirati e la somma, senza ripetere l'espressione
    private static string FormatDiceDetail(DiceRollResult roll)
    {
        var parts = roll.Details.Split("=>", StringSplitOptions.TrimEntries);
        return parts.Length == 3 && parts[1].Length > 0
            ? $"{parts[1]} = {(int)roll.Total}"
            : $"{(int)roll.Total} ({roll.Details})";
    }

    private static string DiceExtra(GameDice.D10Roll roll) =>
        roll.Exploded ? $" ({string.Join("+", roll.Rolls)}, esploso)" : string.Empty;

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
        return [.. game.Players
            .Select(p => _connectedClients.Values.FirstOrDefault(s => ReferenceEquals(s.Player, p)))
            .Where(s => s != null)
            .Select(s => s!)];
    }

    private async Task HandleAttackAsync(ClientSession client, NetworkPacket packet)
    {
        var attackReq = packet.DeserializePayload<AttackRequestPayload>();
        if (attackReq == null) return;
        attackReq.TargetHandle = InputLimits.Clean(attackReq.TargetHandle, InputLimits.MaxNameFieldLength);
        attackReq.WeaponOrSkill = InputLimits.Clean(attackReq.WeaponOrSkill, InputLimits.MaxNameFieldLength);

        if (!await EnsureCharacterLinkedAsync(client)) return;
        if (!await EnsureAliveAsync(client, client.Player.Character!)) return;

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

        // Validazione, consumo dell'azione e risoluzione nella stessa sezione critica: tra un controllo e
        // l'altro non possono infilarsi /next, l'uscita del bersaglio o la sua morte.
        AttackOutcome outcome;
        lock (game.SyncRoot)
        {
            string? denial = CheckActorLocked(game, attacker);
            if (denial == null && !IsInGameLocked(game, target.Character)) denial = $"{target.Character.Handle} non è più in questo game.";
            if (denial == null && target.Character.Health.IsDead) denial = $"{target.Character.Handle} è già morto.";
            denial ??= CheckTurnLocked(game, attacker, needsAction: true);

            if (denial != null)
            {
                outcome = new AttackOutcome(denial, null, null);
            }
            else
            {
                outcome = ResolveAttackRules(game, attacker, target.Character, attackReq.WeaponOrSkill, game.CombatActive);
                if (outcome.Error != null) RefundActionLocked(game);
                else if (!game.CombatActive) game.FreeActionsCounter++;
            }
        }

        if (outcome.Error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = outcome.Error }));
            return;
        }

        // Il colpo sparato consuma munizioni: la scheda dell'attaccante va salvata anche se ha mancato
        if (outcome.AttackerChanged)
        {
            SaveCharacter(attacker);
        }

        Console.WriteLine(outcome.LogLine);

        if (outcome.Result!.IsHit)
        {
            SaveCharacter(target.Character);
        }

        // Il risultato va a tutto il game (attaccante e bersaglio compresi), non agli altri game
        var attackPacket = NetworkPacket.Create(OpCode.S2C_AttackResult, outcome.Result);
        foreach (var session in audience)
        {
            try { await session.SendPacketAsync(attackPacket); }
            catch (Exception ex) { Console.WriteLine($"[COMBAT] Risultato non consegnato a [{session.IdShort}]: {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Combattimento a turni
    // ---------------------------------------------------------------------------------------------

    // Da chiamare sotto Game.SyncRoot. La composizione del game (Players, NPCs) cambia solo con entrambi i lock
    // (_gamesLock e SyncRoot), quindi chi tiene il SyncRoot la vede stabile.
    private static bool IsInGameLocked(Game game, Character character) =>
        game.NPCs.Any(n => ReferenceEquals(n, character)) || game.Players.Any(p => ReferenceEquals(p.Character, character));

    // Da chiamare sotto Game.SyncRoot: tutti i personaggi del game (giocatori e NPC), senza doppioni.
    private static List<Character> ParticipantsLocked(Game game)
    {
        var all = new List<Character>();
        foreach (var p in game.Players)
        {
            if (p.Character != null && !all.Contains(p.Character)) all.Add(p.Character);
        }
        foreach (var npc in game.NPCs)
        {
            if (!all.Contains(npc)) all.Add(npc);
        }
        return all;
    }

    // Da chiamare sotto Game.SyncRoot, nella stessa sezione critica di un CheckTurnLocked(needsAction: true) andato
    // a buon fine, se la risoluzione fallisce: restituisce l'azione (il turno non può essere cambiato nel frattempo).
    private static void RefundActionLocked(Game game)
    {
        if (game.CombatActive) game.TurnActionUsed = false;
    }

    // Controlli comuni a chi agisce (attacco, lancio, ricarica, cura): in partita e vivo. Sotto SyncRoot.
    private static string? CheckActorLocked(Game game, Character actor)
    {
        if (!IsInGameLocked(game, actor)) return $"{actor.Handle} non è più in questo game.";
        if (actor.Health.IsDead) return $"{actor.Handle} è morto e non può agire.";
        return null;
    }

    // Da chiamare sotto Game.SyncRoot. Verifica che l'attore possa agire adesso e, se needsAction e siamo in
    // combattimento, consuma l'azione nella stessa sezione critica: tra il controllo e il consumo non può
    // infilarsi un /next. Restituisce null se l'azione è consentita.
    private static string? CheckTurnLocked(Game game, Character actor, bool needsAction)
    {
        if (!game.CombatActive) return null;

        string? current = game.CurrentTurnHandle;
        if (current == null || !current.Equals(actor.Handle, StringComparison.OrdinalIgnoreCase))
        {
            return $"Non è il turno di {actor.Handle}: tocca a {current ?? "nessuno"} (round {game.Round}).";
        }

        if (actor.Health.IsStunned)
        {
            return $"{actor.Handle} è stordito: non può agire né muoversi (a inizio turno tenta di riprendersi, nel frattempo può solo passare con /next).";
        }

        if (needsAction && game.TurnActionUsed)
        {
            return $"{actor.Handle} ha già usato l'azione di questo turno: termina il turno con /next.";
        }

        if (needsAction) game.TurnActionUsed = true;
        return null;
    }

    // Esito di un movimento atomico: se Error è null, Result è la posizione raggiunta (o il motivo del
    // blocco); se Error è valorizzato, Result va ignorato.
    private readonly record struct MoveOutcome(string? Error, GameMap.MovementResult Result);

    // Movimento atomico: turno, budget di MA rimasto e aggiornamento della posizione (con TurnMoveUsed in
    // combattimento, oppure il contatore delle azioni libere fuori) avvengono nella stessa sezione critica.
    private MoveOutcome TryMove(Game game, GameMap map, Character actor, Position3D requested)
    {
        lock (game.SyncRoot)
        {
            string? error = CheckTurnLocked(game, actor, needsAction: false);
            if (error != null) return new MoveOutcome(error, GameMap.MovementResult.Success);

            double distance = CombatRules.CalculateDistance(actor.Position, requested);
            int allowance = Math.Max(0, actor.Stats.MA);
            double available = game.CombatActive ? Math.Max(0, allowance - game.TurnMoveUsed) : allowance;

            if (distance > available + 1e-9)
            {
                return new MoveOutcome(game.CombatActive
                    ? $"Troppo lontano: a {actor.Handle} restano {available:F1}m di movimento in questo turno (MA {allowance}), la destinazione dista {distance:F1}m."
                    : $"Troppo lontano: {actor.Handle} può muoversi al massimo di {allowance}m per volta (MA {allowance}), la destinazione dista {distance:F1}m.",
                    GameMap.MovementResult.Success);
            }

            var result = map.GetMovementResult(requested);
            if (result == GameMap.MovementResult.Success)
            {
                actor.Position = requested;
                if (game.CombatActive) game.TurnMoveUsed += distance;
                else game.FreeActionsCounter++;
            }

            return new MoveOutcome(null, result);
        }
    }

    // In un round di combattimento un personaggio può fare movimento + azione: 2 comandi contati.
    // Serve solo alla stima mostrata nel game status, non è un limite di gioco.
    private const int MaxActionsPerRound = 2;

    // Registra un'azione libera (fuori dal combattimento) nel game: alimenta FreeActionsCounter, che
    // misura quanto il gruppo ha agito fuori dai round. Da chiamare solo per azioni eseguite
    // (/skill, /roll, /attack risolto; il /move è contato direttamente dentro TryMove).
    private void CountFreeAction(Game game)
    {
        lock (game.SyncRoot)
        {
            if (!game.CombatActive) game.FreeActionsCounter++;
        }
    }

    // Da chiamare sotto _gamesLock. Passa al prossimo personaggio valido dell'ordine (ancora nel game e non morto),
    // incrementando il round al giro completo, e azzera movimento e azione. False se non c'è più nessuno.
    // I personaggi morti durante la scansione vengono aggiunti a newlyDead (da salvare su disco fuori dal lock).
    private bool MoveToNextTurn(Game game, List<string> notes, List<Character> newlyDead)
    {
        int n = game.TurnOrder.Count;
        int fallbackRaw = -1;

        for (int step = 1; step <= n; step++)
        {
            int raw = game.TurnIndex + step;
            int idx = raw % n;
            var combatant = FindCombatantInGame(game, game.TurnOrder[idx].Handle);
            if (combatant == null || combatant.Character.Health.IsDead) continue;

            // Salvezza contro la morte: a inizio turno, chi è in Mortale e non stabilizzato tira 1d10 ≤ BODY.
            if (combatant.Character.Health.NeedsDeathSave)
            {
                notes.Add(RollDeathSave(combatant.Character));
                if (combatant.Character.Health.IsDead)
                {
                    newlyDead.Add(combatant.Character);
                    continue;   // morto: salta il turno
                }
            }

            // Chi è stordito tenta di riprendersi all'inizio del suo turno; se non ci riesce il turno salta
            if (combatant.Character.Health.IsStunned)
            {
                notes.Add(RollStunRecovery(combatant.Character));
                if (combatant.Character.Health.IsStunned)
                {
                    if (fallbackRaw < 0) fallbackRaw = raw;
                    continue;
                }
            }

            game.Round += raw / n;
            game.TurnIndex = idx;
            game.TurnMoveUsed = 0;
            game.TurnActionUsed = false;
            game.TurnStamp++;
            return true;
        }

        // Tutti gli altri sono storditi o morti: il turno va comunque al primo stordito (che può solo passare con /next)
        if (fallbackRaw >= 0)
        {
            notes.Add("Nessuno in grado di agire: il turno passa a un personaggio stordito (può solo passare con /next).");
            game.Round += fallbackRaw / n;
            game.TurnIndex = fallbackRaw % n;
            game.TurnMoveUsed = 0;
            game.TurnActionUsed = false;
            game.TurnStamp++;
            return true;
        }

        return false;
    }

    // Da chiamare sotto _gamesLock. Toglie un personaggio dall'ordine di gioco tenendo coerente il turno corrente.
    // Restituisce l'annuncio da inviare al game se il turno corrente è cambiato (o il combattimento è finito), altrimenti null.
    private string? RemoveFromTurnOrder(Game game, string handle, List<Character> newlyDead)
    {
        int i = game.TurnOrder.FindIndex(t => t.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;

        game.TurnOrder.RemoveAt(i);
        if (!game.CombatActive) return null;

        if (game.TurnOrder.Count == 0)
        {
            game.CombatActive = false;
            return $"{handle} ha lasciato il combattimento: nessuno è rimasto nell'ordine, combattimento terminato.";
        }
        else if (i < game.TurnIndex)
        {
            game.TurnIndex--;
        }
        else if (i == game.TurnIndex)
        {
            // Se n'è andato chi era di turno: si passa a chi lo seguiva con le stesse regole di /next
            // (salta i morti, recupero dallo stordimento, nuovo TurnStamp, movimento e azione freschi).
            // MoveToNextTurn parte dalla posizione successiva a TurnIndex: chi seguiva ora sta in TurnIndex.
            game.TurnIndex--;
            var notes = new List<string>();
            if (!MoveToNextTurn(game, notes, newlyDead))
            {
                game.CombatActive = false;
                game.TurnIndex = 0;
                return $"{handle} ha lasciato il combattimento: nessuno è in grado di agire, combattimento terminato.";
            }

            string noteText = notes.Count > 0 ? string.Join(" ", notes.Select(x => x + ".")) + " " : string.Empty;
            return $"{handle} ha lasciato il combattimento durante il suo turno. {noteText}{DescribeTurn(game)}";
        }

        return null;
    }

    private string DescribeTurn(Game game)
    {
        string current = game.CurrentTurnHandle ?? "nessuno";
        var combatant = game.CurrentTurnHandle != null ? FindCombatantInGame(game, game.CurrentTurnHandle) : null;
        string budget = combatant != null
            ? $" Movimento: {Math.Max(0, combatant.Character.Stats.MA - game.TurnMoveUsed):F1}/{combatant.Character.Stats.MA}m, azione: {(game.TurnActionUsed ? "usata" : "disponibile")}."
            : string.Empty;
        return $"Round {game.Round} - tocca a {current}.{budget}";
    }

    private async Task HandleCombatAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<CombatRequestPayload>();
        if (req == null) return;

        async Task Reply(string message) =>
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        Game? game;
        bool isMaster;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            isMaster = client.Player.Role == Player.RoleType.master;
        }

        if (game == null)
        {
            await Reply("Non sei in nessun game: usa /join <game>.");
            return;
        }

        string action = InputLimits.Clean(req.Action, 16).ToLowerInvariant();

        if (action == "status")
        {
            string text;
            lock (_gamesLock)
            lock (game.SyncRoot)
            {
                if (!game.CombatActive)
                {
                    text = game.TurnOrder.Count > 0
                        ? $"Nessun combattimento in corso. Ordine di iniziativa pronto: {string.Join(", ", game.TurnOrder.Select(t => $"{t.Handle} ({t.Initiative})"))}."
                        : "Nessun combattimento in corso.";
                }
                else
                {
                    var order = game.TurnOrder.Select((t, i) => i == game.TurnIndex ? $"[{t.Handle}]" : t.Handle);
                    text = $"{DescribeTurn(game)} Ordine: {string.Join(" > ", order)}.";
                }
            }
            await Reply(text);
            return;
        }

        if (!isMaster)
        {
            await Reply("Solo il master può avviare o terminare il combattimento.");
            return;
        }

        switch (action)
        {
            case "start":
                {
                    string? error = null;
                    string announce = string.Empty;
                    var newlyDead = new List<Character>();
                    lock (_gamesLock)
                    lock (game.SyncRoot)
                    {
                        if (game.CombatActive)
                        {
                            error = "Il combattimento è già in corso: usa /combat status oppure /combat end.";
                        }
                        else if (game.ActiveCheck is { Kind: Game.CheckKind.Initiative })
                        {
                            error = "L'iniziativa è ancora aperta: attendi lo spoglio o chiudila con /check close.";
                        }
                        else if (game.TurnOrder.Count == 0)
                        {
                            error = "Nessun ordine di iniziativa: usa prima /check init.";
                        }
                        else
                        {
                            game.CombatActive = true;
                            game.Round = 0;
                            game.TurnIndex = game.TurnOrder.Count - 1;
                            foreach (var m in game.Players.Select(pl => pl.Character).Concat(game.NPCs).OfType<Character>())
                            {
                                m.Health.IsStunned = false;
                            }
                            var startNotes = new List<string>();
                            if (!MoveToNextTurn(game, startNotes, newlyDead))
                            {
                                game.CombatActive = false;
                                error = "Nessun personaggio in grado di agire nell'ordine di iniziativa.";
                            }
                            else
                            {
                                game.FreeActionsCounter = 0;
                                announce = $"COMBATTIMENTO INIZIATO! Ordine: {string.Join(" > ", game.TurnOrder.Select(t => t.Handle))}. {(startNotes.Count > 0 ? string.Join(" ", startNotes.Select(x => x + ".")) + " " : "")}{DescribeTurn(game)}";
                            }
                        }
                    }

                    foreach (var dead in newlyDead) SaveCharacter(dead);

                    if (error != null) await Reply(error);
                    else
                    {
                        Console.WriteLine($"[TURN] {client.Player.Username} avvia il combattimento in '{game.Name}'.");
                        await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = announce }));
                    }
                    break;
                }

            case "end":
                {
                    bool was;
                    lock (_gamesLock)
                    lock (game.SyncRoot)
                    {
                        was = game.CombatActive;
                        game.CombatActive = false;
                        game.TurnMoveUsed = 0;
                        game.TurnActionUsed = false;
                        foreach (var m in game.Players.Select(pl => pl.Character).Concat(game.NPCs).OfType<Character>())
                        {
                            m.Health.IsStunned = false;
                        }
                    }

                    if (!was) await Reply("Nessun combattimento in corso.");
                    else
                    {
                        Console.WriteLine($"[TURN] {client.Player.Username} termina il combattimento in '{game.Name}'.");
                        await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Il master ha terminato il combattimento: movimento e azioni tornano liberi." }));
                    }
                    break;
                }

            default:
                await Reply("Uso: /combat start | /combat end | /combat status");
                break;
        }
    }

    private async Task HandleWhoAmIAsync(ClientSession client)
    {
        var me = client.Player;
        var payload = new WhoAmIResponsePayload
        {
            Username = me.Username,
            Role = me.Role.ToString()
        };

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(me));
            if (game != null)
            {
                payload.GameName = game.Name;
                if (me.Role == Player.RoleType.master) payload.NpcCount = game.NPCs.Count;
            }
        }

        var c = me.Character;
        if (c != null)
        {
            payload.HasCharacter = true;
            payload.CharacterHandle = c.Handle;
            payload.Health = c.Health.RemainingBoxes;
            payload.MaxHealth = c.Health.MaxHealth;
            payload.WoundStatus = c.Health.GetWoundStatusDescription();
            payload.MA = c.Stats.MA;
            payload.Position = c.Position;
            var w = c.EquippedWeapon;
            if (w != null)
            {
                payload.WeaponName = w.Name;
                if (w.UsesAmmo)
                {
                    payload.AmmoLeft = w.CurrentAmmo;
                    payload.AmmoCapacity = w.MagazineCapacity;
                }
            }
        }

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_WhoAmIResponse, payload));
    }

    private async Task HandleGameStatusAsync(ClientSession client)
    {
        var me = client.Player;
        var payload = new GameStatusResponsePayload();

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(me));
            if (game != null)
            lock (game.SyncRoot)
            {
                bool viewerIsMaster = me.Role == Player.RoleType.master;
                payload.InGame = true;
                payload.Name = game.Name;
                payload.Description = game.Description;
                payload.Master = game.Players.FirstOrDefault(p => p.Role == Player.RoleType.master)?.Username ?? string.Empty;

                foreach (var p in game.Players)
                {
                    var ch = p.Character;
                    var member = new GameMemberInfo
                    {
                        Username = p.Username,
                        Role = p.Role.ToString(),
                        Handle = ch?.Handle ?? string.Empty
                    };
                    if (ch != null)
                    {
                        member.Health = ch.Health.RemainingBoxes;
                        member.MaxHealth = ch.Health.MaxHealth;
                        member.WoundStatus = ch.Health.GetWoundStatusDescription();
                        member.IsStunned = ch.Health.IsStunned;
                        member.Position = ch.Position;
                    }
                    payload.Members.Add(member);
                }

                // Gli NPC sono visibili a tutti (si possono attaccare), ma i giocatori ne vedono solo lo stato qualitativo
                foreach (var npc in game.NPCs)
                {
                    payload.Members.Add(new GameMemberInfo
                    {
                        Handle = npc.Handle,
                        Role = "npc",
                        IsNpc = true,
                        Health = viewerIsMaster ? npc.Health.RemainingBoxes : -1,
                        MaxHealth = viewerIsMaster ? npc.Health.MaxHealth : -1,
                        WoundStatus = npc.Health.GetWoundStatusDescription(),
                        IsStunned = npc.Health.IsStunned,
                        Position = npc.Position
                    });
                }

                payload.CombatActive = game.CombatActive;
                if (game.CombatActive)
                {
                    payload.Round = game.Round;
                    payload.CurrentTurn = game.CurrentTurnHandle ?? string.Empty;
                    payload.ActionUsed = game.TurnActionUsed;
                    var cur = game.CurrentTurnHandle != null ? FindCombatantInGame(game, game.CurrentTurnHandle) : null;
                    if (cur != null) payload.MoveLeft = Math.Max(0, cur.Character.Stats.MA - game.TurnMoveUsed);
                }
                payload.TurnOrder = [.. game.TurnOrder.Select(t => t.Handle)];

                // Azioni libere fuori dal combattimento e stima delle "round di gruppo" equivalenti
                payload.FreeActionsCounter = game.FreeActionsCounter;
                payload.MaxActionsPerRound = MaxActionsPerRound;
                payload.CharacterCount = game.Players.Count(p => p.Character != null) + game.NPCs.Count;
                payload.FreeActionRounds = payload.CharacterCount > 0
                    ? (double)game.FreeActionsCounter / MaxActionsPerRound / payload.CharacterCount
                    : 0;

                var check = game.ActiveCheck;
                if (check != null)
                {
                    payload.CheckDescription = check.Kind == Game.CheckKind.Skill
                        ? $"Prova di {check.SkillName}{(viewerIsMaster ? $" (TN {check.TargetNumber})" : "")}"
                        : "Tiro di iniziativa";
                    payload.CheckRolled = [.. check.Entries.Where(e => e.Total.HasValue).Select(e => e.Handle)];
                    payload.CheckWaiting = [.. check.Entries.Where(e => !e.Total.HasValue).Select(e => e.Handle)];
                }
            }
        }

        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_GameStatusResponse, payload));
    }

    // /next: il giocatore di turno (o il master, sempre) termina il turno corrente
    private async Task HandleNextAsync(ClientSession client)
    {
        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
        }

        async Task Reply(string message) =>
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        if (game == null)
        {
            await Reply("Non sei in nessun game: usa /join <game>.");
            return;
        }

        string? error = null;
        string announce = string.Empty;
        var newlyDead = new List<Character>();
        lock (_gamesLock)
        lock (game.SyncRoot)
        {
            if (!game.CombatActive)
            {
                error = "Nessun combattimento in corso.";
            }
            else
            {
                bool isMaster = client.Player.Role == Player.RoleType.master;
                string? current = game.CurrentTurnHandle;
                bool isCurrent = client.Player.Character != null && current != null
                    && current.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase);

                if (!isMaster && !isCurrent)
                {
                    error = $"Non è il tuo turno: tocca a {current ?? "nessuno"}.";
                }
                else
                {
                    string ended = current ?? "nessuno";
                    var turnNotes = new List<string>();
                    if (MoveToNextTurn(game, turnNotes, newlyDead))
                    {
                        announce = $"Fine turno di {ended}. {(turnNotes.Count > 0 ? string.Join(" ", turnNotes.Select(x => x + ".")) + " " : "")}{DescribeTurn(game)}";
                    }
                    else
                    {
                        game.CombatActive = false;
                        announce = "Nessun personaggio in grado di agire: combattimento terminato.";
                    }
                }
            }
        }

        foreach (var dead in newlyDead) SaveCharacter(dead);

        if (error != null)
        {
            await Reply(error);
            return;
        }

        Console.WriteLine($"[TURN] {announce}");
        await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = announce }));
    }

    private async Task HandleThrowAsync(ClientSession client, NetworkPacket packet)
    {
        var throwReq = packet.DeserializePayload<ThrowRequestPayload>();
        if (throwReq?.Position == null) return;
        throwReq.WeaponKey = InputLimits.Clean(throwReq.WeaponKey, InputLimits.MaxNameFieldLength);

        if (!await EnsureCharacterLinkedAsync(client)) return;
        if (!await EnsureAliveAsync(client, client.Player.Character!)) return;

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
        List<ClientSession> audience = [];

        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
            if (game != null)
            {
                map = game.Map;
                audience = GetGameSessions(game);
            }
        }

        if (game == null || map == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Devi essere in un game per lanciare: usa /join <game>." }));
            return;
        }

        var target = WithCurrentZ(throwReq.Position, throwReq.ZProvided, thrower);

        if (map.GetMovementResult(target) == GameMap.MovementResult.OutOfBounds)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Il punto indicato è fuori dai confini della mappa." }));
            return;
        }

        // Validazione, consumo dell'azione e risoluzione nella stessa sezione critica; i bersagli dell'area
        // sono i partecipanti di quel momento, non una copia presa prima.
        ThrowOutcome outcome;
        lock (game.SyncRoot)
        {
            string? denial = CheckActorLocked(game, thrower) ?? CheckTurnLocked(game, thrower, needsAction: true);
            if (denial != null)
            {
                outcome = new ThrowOutcome(denial, null, null, []);
            }
            else
            {
                outcome = ResolveThrowRules(game, thrower, target, throwReq.WeaponKey, ParticipantsLocked(game), game.CombatActive);
                if (outcome.Error != null) RefundActionLocked(game);
            }
        }

        if (outcome.Error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = outcome.Error }));
            return;
        }

        Console.WriteLine(outcome.LogLine);

        foreach (var changed in outcome.Changed)
        {
            SaveCharacter(changed);
        }

        var packet = NetworkPacket.Create(OpCode.S2C_AreaResult, outcome.Result);
        foreach (var session in audience)
        {
            try { await session.SendPacketAsync(packet); }
            catch (Exception ex) { Console.WriteLine($"[COMBAT] Esito area non consegnato a [{session.IdShort}]: {ex.Message}"); }
        }
    }

    // Regole del lancio/esplosione, sincrone e sotto Game.SyncRoot. Nessun I/O di rete qui dentro.
    private ThrowOutcome ResolveThrowRules(Game game, Character thrower, Position3D aimed, string? weaponKey, List<Character> everyone, bool combatActive)
    {
        static ThrowOutcome Error(string message) => new(message, null, null, []);

        lock (game.SyncRoot)
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

            var throwSteps = new List<string>();
            var throwParts = new List<(string, int, string?)>
            {
                ("d10", roll.Total, DiceExtra(roll)),
                (statName, statVal, null),
                (weapon.ScatterSkillName, skillLvl, null)
            };
            if (penalty != 0) throwParts.Add(("ferite", penalty, null));
            throwSteps.Add($"Tiro di lancio: {FormatSum(throwParts)}");
            throwSteps.Add($"Difficoltà: TN {throwTn} (distanza {distance:F1} m, gittata di lancio {throwRange:F0} m)");
            throwSteps.Add(roll.IsFumble
                ? "Esito: FALLIMENTO CRITICO (1 naturale sul d10)"
                : onTarget ? $"Esito: A SEGNO ({total} contro TN {throwTn})" : $"Esito: FUORI BERSAGLIO ({total} contro TN {throwTn})");

            // Se il lancio fallisce la granata devia: d10 metri in direzione casuale (doppio con un fallimento critico)
            var landed = new Position3D { X = aimed.X, Y = aimed.Y, Z = aimed.Z };
            double deviation = 0;
            if (!onTarget)
            {
                int deviationRoll = (int)GameDice.Roll("1d10").Total;
                deviation = deviationRoll * (roll.IsFumble ? 2 : 1);
                double angle = Random.Shared.NextDouble() * 2 * Math.PI;
                landed.X += (int)Math.Round(deviation * Math.Cos(angle));
                landed.Y += (int)Math.Round(deviation * Math.Sin(angle));
                throwSteps.Add($"Deviazione: d10 {deviationRoll}{(roll.IsFumble ? " x2 (fallimento critico)" : "")} = {deviation:F0} m in direzione casuale");
            }
            throwSteps.Add($"Esplosione in ({landed.X}, {landed.Y}, {landed.Z}), raggio {radius:0.#} m");

            // Un solo tiro di danno per fascia: tutti quelli nella stessa fascia subiscono lo stesso danno
            var bandRolls = new Dictionary<string, (int Raw, string Text)>();
            var victims = new List<AreaVictim>();
            var changed = new List<Character> { thrower };

            foreach (var target in everyone)
            {
                if (target.Health.IsDead) continue;   // i morti non subiscono altro danno

                double d = CombatRules.CalculateDistance(target.Position, landed);
                if (d > radius) continue;

                string? dice;
                string bandKey;
                string bandLabel;
                if (weapon.AreaDamageBands is { Count: > 0 })
                {
                    var band = weapon.AreaDamageBands.FirstOrDefault(b => d >= b.FromMeters && d < b.ToMeters);
                    if (band == null) continue;
                    dice = band.DamageDice;
                    bandKey = $"{band.FromMeters}-{band.ToMeters}";
                    bandLabel = $"fascia {band.FromMeters:0.#}-{band.ToMeters:0.#} m";
                }
                else
                {
                    dice = weapon.DamageDice;
                    bandKey = "all";
                    bandLabel = "tutto il raggio";
                }

                var victimSteps = new List<string> { $"Distanza dal centro: {d:F1} m ({bandLabel})" };

                if (!bandRolls.TryGetValue(bandKey, out var bandRoll))
                {
                    var dmgRoll = GameDice.Roll(dice);
                    bandRoll = (Math.Max(0, (int)dmgRoll.Total), $"Dadi di danno ({dice}): {FormatDiceDetail(dmgRoll)}");
                    bandRolls[bandKey] = bandRoll;
                    victimSteps.Add(bandRoll.Text);
                }
                else
                {
                    victimSteps.Add($"Dadi di danno ({dice}): stessi dadi della {bandLabel} = {bandRoll.Raw}");
                }
                int rawDamage = bandRoll.Raw;

                // L'esplosione colpisce il corpo: protegge l'armatura del torso, nessun moltiplicatore di locazione
                var torso = target.Armor.GetPiece(ArmorLocation.Torso);
                var resolution = CombatRules.ResolveDamage(torso, rawDamage, 1, armorPiercing: false, target.Stats.BODY);
                var armor = resolution.Armor;
                int penetrating = resolution.Penetrating;
                int btmRed = resolution.BtmReduction;
                int afterBtm = resolution.Dealt;
                int healthBefore = target.Health.RemainingBoxes;
                target.Health.TakeDamage(afterBtm);
                if (!changed.Contains(target)) changed.Add(target);

                if (armor.SpFaced > 0)
                {
                    victimSteps.Add($"Armatura (torso, SP {armor.SpFaced}): {rawDamage} - {armor.SpFaced} = {penetrating}{(armor.Ablated ? " (SP dell'armatura -1)" : " (l'armatura regge)")}");
                }
                else
                {
                    victimSteps.Add($"Armatura: nessuna protezione sul torso ({rawDamage} danni passano)");
                }
                if (armor.DamageDealt > 0)
                {
                    victimSteps.Add($"BTM (BODY {target.Stats.BODY}): {armor.DamageDealt} - {btmRed} = {afterBtm}{(armor.DamageDealt - btmRed < 1 ? " (minimo 1)" : "")}");
                }
                victimSteps.Add($"Danno inflitto: {afterBtm}");
                victimSteps.Add($"Salute: {healthBefore} -> {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()})");
                if (combatActive && afterBtm > 0)
                {
                    var stunLine = RollStunSave(target);
                    if (stunLine != null) victimSteps.Add(stunLine);
                }

                victims.Add(new AreaVictim
                {
                    Handle = target.Handle,
                    Distance = Math.Round(d, 1),
                    RawDamage = rawDamage,
                    ArmorSP = armor.SpFaced,
                    DamageDealt = afterBtm,
                    BtmReduction = armor.DamageDealt - afterBtm,
                    RemainingHealth = target.Health.RemainingBoxes,
                    MaxHealth = target.Health.MaxHealth,
                    WoundStatus = target.Health.GetWoundStatusDescription(),
                    Steps = victimSteps
                });
            }

            string aimText = $"({aimed.X}, {aimed.Y}, {aimed.Z})";
            string landText = $"({landed.X}, {landed.Y}, {landed.Z})";
            string message = onTarget
                ? $"{thrower.Handle} lancia {weapon.Name} su {aimText}: esplode nel punto indicato."
                : roll.IsFumble
                    ? $"FALLIMENTO CRITICO! La granata di {thrower.Handle} devia di {deviation:F0}m: esplode in {landText}."
                    : $"La granata di {thrower.Handle} devia di {deviation:F0}m: esplode in {landText}.";

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
                Steps = throwSteps,
                PiecesLeft = weapon.CurrentAmmo,
                Victims = victims,
                Message = message
            };

            string log = $"[COMBAT] {thrower.Handle} lancia '{weapon.Name}' su {aimText}. {string.Join(" | ", throwSteps)}"
                + $" | Colpiti: {(victims.Count > 0 ? string.Join(", ", victims.Select(v => $"{v.Handle} -{v.DamageDealt}")) : "nessuno")} | Pezzi rimasti: {weapon.CurrentAmmo}";

            return new ThrowOutcome(null, result, log, changed);
        }
    }

    private async Task HandleReloadAsync(ClientSession client, NetworkPacket packet)
    {
        var reloadReq = packet.DeserializePayload<ReloadRequestPayload>();
        if (!await EnsureCharacterLinkedAsync(client)) return;
        if (!await EnsureAliveAsync(client, client.Player.Character!)) return;

        await ResolveReloadAsync(client, client.Player.Character!, reloadReq);
    }

    /// <summary>Ricarica l'arma di <paramref name="actor"/> (giocatore o NPC); gli errori vanno a <paramref name="reporter"/>.</summary>
    private async Task ResolveReloadAsync(ClientSession reporter, Character actor, ReloadRequestPayload? reloadReq)
    {
        string ammoKey = InputLimits.Clean(reloadReq?.AmmoKey, InputLimits.MaxNameFieldLength) ?? string.Empty;
        string weaponKey = InputLimits.Clean(reloadReq?.WeaponKey, InputLimits.MaxNameFieldLength) ?? string.Empty;

        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
        }

        string? error;
        string? message;
        lock (game?.SyncRoot ?? _noGameSync)
        {
            string? denial = game != null ? CheckActorLocked(game, actor) ?? CheckTurnLocked(game, actor, needsAction: true) : null;
            if (denial != null)
            {
                (error, message) = (denial, null);
            }
            else
            {
                (error, message) = ReloadRules(actor, ammoKey, weaponKey);
                if (error != null && game != null) RefundActionLocked(game);
            }
        }

        if (error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = error }));
            return;
        }

        SaveCharacter(actor);
        Console.WriteLine($"[COMBAT] {message}");
        await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));
    }

    // Regole della ricarica, sincrone e sotto Game.SyncRoot. I colpi rimasti nel caricatore tornano nella
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

    // Regole dell'attacco, sincrone e sotto Game.SyncRoot. Nessun I/O di rete qui dentro.
    private AttackOutcome ResolveAttackRules(Game game, Character attacker, Character target, string? weaponKey, bool combatActive)
    {
        lock (game.SyncRoot)
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

            double distance = CombatRules.CalculateDistance(attacker.Position, target.Position);
            int targetNumber = 15;
            string attackTypeDesc = string.Empty;
            string tnText = "TN 15";

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
                tnText = $"TN {targetNumber} (distanza {distance:F1} m, gittata {weaponToUse.OptimalRangeMeters} m)";

                if (weaponToUse.UsesAmmo)
                {
                    loadedAmmo = FindLoadedAmmo(attacker, weaponToUse);
                    weaponToUse.CurrentAmmo--;
                    ammoSpent = true;
                }
            }
            else if (weaponToUse.Type == Weapon.WeaponType.Melee)
            {
                if (distance > CombatRules.MeleeMaxDistanceMeters)
                {
                    return new AttackOutcome($"Troppo lontano per la mischia con {weaponToUse.Name}! Distanza: {distance:F1}m > {CombatRules.MeleeMaxDistanceMeters}m.", null, null);
                }

                int targetRef = target.Stats.REF;
                int targetDodge = target.Skills.TryGetValue("Schivare", out int dLvl) ? dLvl : 0;
                int targetPenalty = target.Health.GetWoundPenalty();

                targetNumber = CombatRules.GetMeleeTargetNumber(targetRef, targetDodge, targetPenalty);
                attackTypeDesc = $"In Mischia con {weaponToUse.Name} -> Difesa Bersaglio (REF {targetRef} + Schivare {targetDodge}): TN {targetNumber}";
                var tnParts = new List<(string, int, string?)> { ("base", 10, null), ("REF", targetRef, null), ("Schivare", targetDodge, null) };
                if (targetPenalty != 0) tnParts.Add(("ferite", targetPenalty, null));
                tnText = $"difesa di {target.Handle}: {FormatSum(tnParts)} (distanza {distance:F1} m)";
            }

            var attackRoll = GameDice.RollExplodingD10Detailed();
            int diceVal = attackRoll.Total;
            int weaponModifier = weaponToUse.GetAttackModifier();
            int cyberHit = SumCyberBonus(attacker, "HIT");
            var toHit = CombatRules.ResolveToHit(attackRoll, attackerStat, attackerSkillLvl, attackerPenalty, weaponModifier, cyberHit, targetNumber);
            int attackTotal = toHit.Total;

            var steps = new List<string>();
            var hitParts = new List<(string, int, string?)>
            {
                ("d10", diceVal, DiceExtra(attackRoll)),
                (statName, attackerStat, null),
                (skillName, attackerSkillLvl, null)
            };
            if (weaponModifier != 0) hitParts.Add(("arma", weaponModifier, null));
            if (cyberHit != 0) hitParts.Add(("cyber", cyberHit, null));
            if (attackerPenalty != 0) hitParts.Add(("ferite", attackerPenalty, null));
            steps.Add($"Tiro per colpire: {FormatSum(hitParts)}");
            steps.Add($"Difficoltà: {tnText}");

            // Un 1 naturale sul primo dado è un fallimento critico: manca comunque (il colpo sparato è perso)
            bool isFumble = toHit.IsFumble;
            bool isHit = toHit.IsHit;
            steps.Add(isFumble
                ? "Esito: FALLIMENTO CRITICO (1 naturale sul d10)"
                : isHit ? $"Esito: COLPITO ({attackTotal} contro TN {targetNumber})" : $"Esito: MANCATO ({attackTotal} contro TN {targetNumber})");

            int damageDealt = 0;
            string hitLocationName = string.Empty;
            int hitMultiplier = 1;
            CombatRules.ArmorResult? armorResult = null;
            int btmReduction = 0;
            int healthBefore = target.Health.CurrentHealth;

            if (isHit)
            {
                var damageRoll = GameDice.Roll(weaponToUse.DamageDice);
                int diceDamage = (int)damageRoll.Total;
                int ammoDamage = loadedAmmo?.DamageModifier ?? 0;
                int cyberDamage = SumCyberBonus(attacker, "DAMAGE");
                int rawDamage = Math.Max(0, diceDamage + ammoDamage + cyberDamage);

                // Locazione d'impatto e armatura di quella zona
                int locRoll = (int)GameDice.Roll("1d10").Total;
                var loc = CombatRules.ResolveHitLocation(locRoll);
                hitLocationName = loc.Name;
                hitMultiplier = loc.DamageMultiplier;
                steps.Add($"Locazione: d10 {locRoll} -> {loc.Name}{(loc.DamageMultiplier > 1 ? $" (danno x{loc.DamageMultiplier} dopo l'armatura)" : "")}");

                steps.Add($"Dadi di danno ({weaponToUse.DamageDice}): {FormatDiceDetail(damageRoll)}");
                if (ammoDamage != 0 || cyberDamage != 0)
                {
                    var dmgParts = new List<(string, int, string?)> { ("dadi", diceDamage, null) };
                    if (ammoDamage != 0) dmgParts.Add(("munizioni", ammoDamage, null));
                    if (cyberDamage != 0) dmgParts.Add(("cyber", cyberDamage, null));
                    steps.Add($"Bonus al danno: {FormatSum(dmgParts)}");
                }

                var armorPiece = target.Armor.GetPiece(loc.ArmorLocation);
                int spBefore = armorPiece?.CurrentStopPower ?? 0;
                bool armorPiercing = loadedAmmo?.HalvesArmorSP == true;

                var resolution = CombatRules.ResolveDamage(armorPiece, rawDamage, loc.DamageMultiplier, armorPiercing, target.Stats.BODY);
                armorResult = resolution.Armor;
                int penetrating = resolution.Penetrating;

                if (armorResult.SpFaced > 0)
                {
                    string spText = armorPiercing ? $"SP {spBefore} dimezzato dalle munizioni perforanti = {armorResult.SpFaced}" : $"SP {armorResult.SpFaced}";
                    steps.Add($"Armatura ({armorPiece?.Name}, {spText}): {rawDamage} - {armorResult.SpFaced} = {penetrating}{(armorResult.Ablated ? " (SP dell'armatura -1)" : " (l'armatura regge)")}");
                }
                else
                {
                    steps.Add($"Armatura: nessuna protezione su {loc.Name} ({rawDamage} danni passano)");
                }

                if (loc.DamageMultiplier > 1 && penetrating > 0)
                {
                    steps.Add($"Moltiplicatore {loc.Name} x{loc.DamageMultiplier}: {penetrating} -> {armorResult.DamageDealt}");
                }

                btmReduction = resolution.BtmReduction;
                damageDealt = resolution.Dealt;
                if (armorResult.DamageDealt > 0)
                {
                    steps.Add($"BTM (BODY {target.Stats.BODY}): {armorResult.DamageDealt} - {btmReduction} = {damageDealt}{(armorResult.DamageDealt - btmReduction < 1 ? " (minimo 1)" : "")}");
                }

                target.Health.TakeDamage(damageDealt);
                steps.Add($"Danno inflitto: {damageDealt}");
                steps.Add($"Salute di {target.Handle}: {healthBefore} -> {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()})");

                if (combatActive && damageDealt > 0)
                {
                    var stunLine = RollStunSave(target);
                    if (stunLine != null) steps.Add(stunLine);
                }
            }

            string log = $"[COMBAT] {attacker.Handle} attacca {target.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. "
                + string.Join(" | ", steps)
                + (weaponToUse.UsesAmmo ? $" | Colpi: {weaponToUse.CurrentAmmo}/{weaponToUse.MagazineCapacity}" : "");

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
                BtmReduction = armorResult != null ? armorResult.DamageDealt - damageDealt : 0,
                TargetRemainingHealth = target.Health.RemainingBoxes,
                TargetMaxHealth = target.Health.MaxHealth,
                TargetWoundStatus = target.Health.GetWoundStatusDescription(),
                WeaponName = weaponToUse.Name,
                Steps = steps,
                AmmoLeft = weaponToUse.UsesAmmo ? weaponToUse.CurrentAmmo : 0,
                AmmoCapacity = weaponToUse.UsesAmmo ? weaponToUse.MagazineCapacity : 0,
                AmmoName = weaponToUse.UsesAmmo ? (loadedAmmo?.Name ?? "Munizioni standard") : string.Empty,
                Message = isHit
                    ? (damageDealt > 0
                        ? $"COLPO A SEGNO: {damageDealt} danni a {target.Handle}."
                        : $"COLPO A SEGNO, MA NESSUN DANNO: l'armatura regge.")
                    : isFumble
                        ? $"FALLIMENTO CRITICO di {attacker.Handle}."
                        : $"MANCATO da {attacker.Handle}."
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
        string reason = new string([.. (rollReq?.Reason ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
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
        string loggedExpr = new([.. expr.Where(c => !char.IsControl(c))]);

        Console.WriteLine($"[ROLL] {client.Player.Username} tira {loggedExpr} ({reason}): {total}");

        // Il tiro del master non è l'azione di un PG: non alimenta il contatore delle azioni libere.
        Game? rollGame;
        lock (_gamesLock)
        {
            rollGame = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
        }
        if (rollGame != null && client.Player.Role != Player.RoleType.master) CountFreeAction(rollGame);

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
    private async Task SendSkillsAsync(ClientSession reporter, Character character)
    {
        SkillListResponsePayload skillListPayload;
        lock (SyncFor(character))
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

            skillListPayload = new SkillListResponsePayload
            {
                Character = character.Handle,
                WoundPenalty = penalty,
                Skills = skillInfos
            };
        }

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
    private async Task SendStatusAsync(ClientSession reporter, Character character)
    {
        await reporter.SendPacketAsync(CreateCharacterPacket(OpCode.S2C_PlayerStateUpdate, new CharacterStatePayload
        {
            Character = character
        }, character));
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

        playerInfos = [.. playerInfos.OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase)];

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
        if (!await EnsureAliveAsync(client, client.Player.Character!)) return;

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
            CountFreeAction(checkGame);

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
    // Punto di destinazione con la Z risolta: se il client non ha indicato la Z, si resta sul piano
    // corrente dell'attore (movimento e lancio non cambiano quota da soli).
    private static Position3D WithCurrentZ(Position3D requested, bool zProvided, Character actor) =>
        zProvided ? requested : new Position3D { X = requested.X, Y = requested.Y, Z = actor.Position.Z };

    private async Task HandleMoveAsync(ClientSession client, NetworkPacket packet)
    {
        var moveReq = packet.DeserializePayload<MoveRequestPayload>();
        if (moveReq?.Position == null) return;

        if (!await EnsureCharacterLinkedAsync(client)) return;
        if (!await EnsureAliveAsync(client, client.Player.Character!)) return;

        await ResolveMoveAsync(client, client.Player.Character!, moveReq);
    }

    private async Task ResolveMoveAsync(ClientSession reporter, Character actor, MoveRequestPayload moveReq)
    {
        Game? game;
        GameMap? map;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(reporter.Player));
            map = game?.Map;
        }

        if (game == null || map == null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = "Non sei in una partita attiva. Non puoi muoverti." }));
            return;
        }

        var requested = WithCurrentZ(moveReq.Position, moveReq.ZProvided, actor);

        // Turno, budget di movimento e aggiornamento della posizione in un'unica operazione atomica
        var move = TryMove(game, map, actor, requested);
        if (move.Error != null)
        {
            await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = move.Error }));
            return;
        }

        string message;
        if (move.Result == GameMap.MovementResult.Success)
        {
            SaveCharacter(actor);

            message = $"Posizione aggiornata a ({requested.X}, {requested.Y}, {requested.Z})";
            Console.WriteLine($"[MOVE] {actor.Handle} si è spostato a ({requested.X}, {requested.Y}, {requested.Z})");
        }
        else
        {
            message = move.Result == GameMap.MovementResult.OutOfBounds
                ? $"Movimento bloccato: ({requested.X}, {requested.Y}, {requested.Z}) è fuori dai confini della mappa."
                : $"Movimento bloccato: c'è un ostacolo in ({requested.X}, {requested.Y}, {requested.Z}).";
            Console.WriteLine($"[MOVE] {actor.Handle} movimento negato ({move.Result}) verso ({requested.X}, {requested.Y}, {requested.Z})");
        }

        var movePayload = new MoveResultPayload
        {
            PlayerHandle = actor.Handle,
            RequestedPosition = requested,
            ActualPosition = actor.Position,
            Result = move.Result,
            Message = message
        };

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload));

        if (move.Result == GameMap.MovementResult.Success)
        {
            // Solo gli altri partecipanti dello stesso game vedono lo spostamento
            await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_PlayerMoved, movePayload), includeOrigin: false);
        }
    }

    /// <summary>
    /// /teleport X Y Z personaggio1 personaggio2 ... (solo master): sposta personaggi e NPC del game nel punto
    /// indicato. Non è un'azione e non è soggetto al limite di movimento; la destinazione deve però essere
    /// dentro la mappa e libera da ostacoli. O si spostano tutti o nessuno.
    /// </summary>
    private async Task HandleTeleportAsync(ClientSession client, NetworkPacket packet)
    {
        async Task Fail(string message) =>
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }));

        var req = packet.DeserializePayload<TeleportRequestPayload>();
        if (req?.Position == null) { await Fail("Uso: /teleport <X> <Y> <Z> <personaggio> [altri personaggi]"); return; }

        if (client.Player.Role != Player.RoleType.master)
        {
            await Fail("Solo il master può usare /teleport.");
            return;
        }

        var handles = TeleportRules.ParseTargets(req.Targets.Select(t => InputLimits.Clean(t, InputLimits.MaxNameFieldLength)));
        if (handles.Count == 0) { await Fail("Uso: /teleport <X> <Y> <Z> <personaggio> [altri personaggi]"); return; }
        if (handles.Count > TeleportRules.MaxTargets) { await Fail($"Troppi personaggi: al massimo {TeleportRules.MaxTargets} per comando."); return; }

        var destination = new Position3D { X = req.Position.X, Y = req.Position.Y, Z = req.Position.Z };
        var moved = new List<Character>();
        string? error = null;

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            if (game?.Map == null)
            {
                error = "Devi essere in un game per usare /teleport: usa /join <game>.";
            }
            else
            lock (game.SyncRoot)
            {
                var found = new List<Character>();
                foreach (var handle in handles)
                {
                    var character = FindCombatantInGame(game, handle)?.Character;
                    if (character == null) { error = $"Personaggio '{handle}' non trovato in questo game: nessuno spostato."; break; }
                    found.Add(character);
                }

                if (error == null)
                {
                    var result = game.Map.GetMovementResult(destination);
                    if (result != GameMap.MovementResult.Success)
                    {
                        error = result == GameMap.MovementResult.OutOfBounds
                            ? $"({destination.X}, {destination.Y}, {destination.Z}) è fuori dai confini della mappa: nessuno spostato."
                            : $"C'è un ostacolo in ({destination.X}, {destination.Y}, {destination.Z}): nessuno spostato.";
                    }
                }

                if (error == null)
                {
                    foreach (var character in found)
                    {
                        character.Position = new Position3D { X = destination.X, Y = destination.Y, Z = destination.Z };
                        moved.Add(character);
                    }
                }
            }
        }

        if (error != null) { await Fail(error); return; }

        foreach (var character in moved)
        {
            SaveCharacter(character);
            Console.WriteLine($"[TELEPORT] {client.Player.Username} (master) sposta {character.Handle} a ({destination.X}, {destination.Y}, {destination.Z}).");

            var payload = new MoveResultPayload
            {
                PlayerHandle = character.Handle,
                RequestedPosition = destination,
                ActualPosition = character.Position,
                Result = GameMap.MovementResult.Success,
                Message = $"Il master ti ha spostato a ({destination.X}, {destination.Y}, {destination.Z})."
            };
            await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_PlayerMoved, payload), includeOrigin: true);
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

            // Cambiare scheda mentre si è in un game lascerebbe turno e check appesi al vecchio personaggio:
            // per cambiare si esce prima con /leave.
            bool alreadyInGame;
            lock (_gamesLock)
            {
                alreadyInGame = _games.Any(g => g.Players.Contains(client.Player));
            }

            if (alreadyInGame)
            {
                await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_LinkResponse, new LinkResponsePayload
                {
                    Success = false,
                    Message = "Sei già in un game: usa /leave prima di cambiare personaggio."
                }));
                return;
            }

            // Una scheda è usata da un solo "attore" alla volta: o un giocatore (questa sessione) o un NPC di un
            // game (/npc add), mai entrambi. Il controllo "già in
            // uso", il caricamento e l'associazione avvengono nello stesso lock: senza, due /link
            // simultanei sullo stesso personaggio passerebbero entrambi il controllo e lavorerebbero su
            // due copie separate della scheda, sovrascrivendosi i salvataggi. Gli await restano fuori.
            // L'associazione si libera da sola alla disconnessione (la sessione esce da _connectedClients);
            // un secondo /link della stessa sessione è ammesso solo fuori da un game (vedi controllo sopra).
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

            SaveCharacter(linkedCharacter);

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

    /// <summary>
    /// /unlink: scioglie il legame tra la sessione e la scheda. È inibito dentro un game perché la scheda
    /// può essere nell'ordine di turno o in un check aperto: prima si esce con /leave.
    /// </summary>
    private async Task HandleUnlinkAsync(ClientSession client)
    {
        UnlinkResponsePayload? failure = null;
        string handle = string.Empty;

        lock (_linkLock)
        {
            bool inGame;
            lock (_gamesLock)
            {
                inGame = _games.Any(g => g.Players.Contains(client.Player));
            }

            if (inGame)
            {
                failure = new UnlinkResponsePayload
                {
                    Success = false,
                    Message = "Sei già in un game: usa /leave prima di scollegare il personaggio."
                };
            }
            else
            {
                string? linkedHandle = string.IsNullOrWhiteSpace(client.LinkedCharacterHandle)
                    ? null
                    : client.LinkedCharacterHandle;
                Character? character = client.Player.Character;

                if (character == null && linkedHandle == null)
                {
                    failure = new UnlinkResponsePayload
                    {
                        Success = false,
                        Message = "Nessun personaggio collegato da scollegare."
                    };
                }
                else
                {
                    handle = linkedHandle ?? character?.Handle ?? string.Empty;

                    // Salva prima di liberare la scheda: un /link o /npc add successivo non deve
                    // caricare dal disco uno stato precedente alle ultime azioni.
                    if (character != null) SaveCharacter(character);

                    client.Player.Character = null;
                    client.LinkedCharacterHandle = null;
                    client.Player.Role = Player.RoleType.none;
                }
            }
        }

        if (failure != null)
        {
            await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_UnlinkResponse, failure));
            return;
        }

        Console.WriteLine($"[UNLINK] {client.Player.Username} ha scollegato la scheda '{handle}'.");
        await client.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_UnlinkResponse, new UnlinkResponsePayload
        {
            Success = true,
            Message = handle.Length > 0 ? $"Personaggio '{handle}' scollegato." : "Personaggio scollegato.",
            CharacterHandle = handle,
            Role = Player.RoleType.none
        }));
    }

    // Abilità mediche valide per la stabilizzazione: vale il punteggio migliore fra Pronto Soccorso e
    // Tecnomedicina ("MedicalTech" è l'alias inglese storico della stessa abilità).
    private static readonly string[] HealSkills = ["Pronto Soccorso", "Tecnomedicina", "MedicalTech"];

    private static int HealSkillLevel(Character c) => MedicalRules.BestSkillLevel(c.Skills, HealSkills);

    /// <summary>
    /// /heal &lt;personaggio&gt;. Un giocatore tenta la <b>stabilizzazione</b> (Pronto Soccorso/Tecnomedicina,
    /// TN = ferite subite dal bersaglio): ferma i tiri salvezza contro la morte. Il <b>master</b> invece cura
    /// completamente e azzera morte e stordimento. La guarigione off-combat (0.5/1 casella al giorno) è fuori
    /// da questo comando.
    /// </summary>
    private const int MaxHealAmount = 1000;

    private static Task ReplyHealAsync(ClientSession reporter, bool success, string message, string target = "") =>
        reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_HealResponse, new HealResponsePayload
        {
            Success = success,
            Message = message,
            TargetHandle = target
        }));

    private async Task HandleHealAsync(ClientSession client, NetworkPacket packet)
    {
        var req = packet.DeserializePayload<HealRequestPayload>();
        string targetHandle = InputLimits.Clean(req?.TargetHandle, InputLimits.MaxNameFieldLength);

        if (targetHandle.Length == 0)
        {
            await ReplyHealAsync(client, false, "Uso: /heal <personaggio> [caselle]");
            return;
        }

        Game? game;
        lock (_gamesLock)
        {
            game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
        }

        if (game == null)
        {
            await ReplyHealAsync(client, false, "Devi essere in un game per curare: usa /join <game>.");
            return;
        }

        if (client.Player.Role == Player.RoleType.master)
        {
            await MasterHealAsync(client, game, targetHandle, req?.Amount);
            return;
        }

        if (req?.Amount != null)
        {
            await ReplyHealAsync(client, false, "Solo il master può indicare quante caselle restituire.");
            return;
        }

        // Giocatore: stabilizzazione con la migliore fra Pronto Soccorso e Tecnomedicina (azione del turno).
        if (!await EnsureCharacterLinkedAsync(client)) return;
        Character healer = client.Player.Character!;
        if (!await EnsureAliveAsync(client, healer)) return;

        await ResolveStabilizeAsync(client, game, healer, targetHandle);
    }

    // Master: cura gratuita e senza tiro. Senza <amount> cura completamente e azzera morte, stordimento e
    // stabilizzazione; con <amount> restituisce solo quelle caselle.
    private async Task MasterHealAsync(ClientSession client, Game game, string targetHandle, int? amount)
    {
        if (amount is <= 0 or > MaxHealAmount)
        {
            await ReplyHealAsync(client, false, $"Le caselle da restituire devono essere tra 1 e {MaxHealAmount}.");
            return;
        }

        Character? target;
        lock (_gamesLock)
        {
            target = FindCombatantInGame(game, targetHandle)?.Character;
        }

        if (target == null)
        {
            await ReplyHealAsync(client, false, $"Personaggio '{targetHandle}' non trovato in questo game.");
            return;
        }

        // La salute si modifica sotto il lock del game, come negli attacchi
        string text;
        lock (game.SyncRoot)
        {
            if (!IsInGameLocked(game, target))
            {
                text = string.Empty;
            }
            else if (amount == null)
            {
                target.Health.FullHeal();
                text = $"Il master cura completamente {target.Handle}: salute {target.Health.RemainingBoxes}/{target.Health.MaxHealth}.";
            }
            else
            {
                target.Health.Heal(amount.Value);
                text = $"Il master restituisce {amount} caselle a {target.Handle}: salute {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()}).";
            }
        }

        if (text.Length == 0)
        {
            await ReplyHealAsync(client, false, $"{target.Handle} non è più in questo game.");
            return;
        }

        SaveCharacter(target);

        Console.WriteLine($"[HEAL] {client.Player.Username} (master) cura {target.Handle}{(amount != null ? $" di {amount}" : " completamente")}.");
        await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = text }), includeOrigin: false);
        await ReplyHealAsync(client, true, text, target.Handle);
    }

    /// <summary>
    /// Stabilizzazione di <paramref name="targetHandle"/> da parte di <paramref name="healer"/> (un giocatore, o un
    /// NPC con /as): solo se il ferito è in Mortale, entro 2,5 m, con l'azione del turno. <paramref name="reporter"/>
    /// è la sessione che ha dato il comando e riceve la risposta.
    /// </summary>
    private async Task ResolveStabilizeAsync(ClientSession reporter, Game game, Character healer, string targetHandle)
    {
        Character? target;
        lock (_gamesLock)
        {
            target = FindCombatantInGame(game, targetHandle)?.Character;
        }

        if (target == null)
        {
            await ReplyHealAsync(reporter, false, $"Personaggio '{targetHandle}' non trovato in questo game.");
            return;
        }

        // Controlli, consumo dell'azione, tiro ed effetto nella stessa sezione critica: il ferito non può morire,
        // allontanarsi o essere stabilizzato da altri tra il controllo e l'esito.
        string? error = null;
        bool success = false;
        int total = 0, tn = 0;
        bool fumble = false;

        lock (game.SyncRoot)
        {
            error = CheckActorLocked(game, healer);
            if (error == null && !IsInGameLocked(game, target)) error = $"{target.Handle} non è più in questo game.";

            if (error == null)
            {
                var block = MedicalRules.GetStabilizeBlock(target.Health);
                if (block != MedicalRules.StabilizeBlock.None)
                {
                    error = block switch
                    {
                        MedicalRules.StabilizeBlock.Dead => $"{target.Handle} è morto: solo il master può rianimarlo.",
                        MedicalRules.StabilizeBlock.NotMortal => $"{target.Handle} non è in Mortale ({target.Health.GetWoundStatusDescription()}): non c'è nulla da stabilizzare.",
                        _ => $"{target.Handle} è già stabilizzato."
                    };
                }
            }

            if (error == null)
            {
                double distance = CombatRules.CalculateDistance(healer.Position, target.Position);
                if (distance > MedicalRules.HealRangeMeters)
                {
                    error = $"{target.Handle} è troppo lontano per essere curato da {healer.Handle}: {distance:F1} m, serve essere a {MedicalRules.HealRangeMeters} m o meno.";
                }
            }

            error ??= CheckTurnLocked(game, healer, needsAction: true);

            if (error == null)
            {
                if (!game.CombatActive) game.FreeActionsCounter++;

                int skillLvl = HealSkillLevel(healer);
                int tech = healer.Stats.GetStatValue("TECH");
                int penalty = healer.Health.GetWoundPenalty();
                var roll = GameDice.RollExplodingD10Detailed();
                var stab = MedicalRules.ResolveStabilization(roll, tech, skillLvl, penalty, target.Health.DamageTaken);
                total = stab.Total;
                tn = stab.TargetNumber;
                success = stab.Success;
                fumble = stab.IsFumble;

                Console.WriteLine($"[HEAL] {healer.Handle} tenta di stabilizzare {target.Handle}: d10 {roll.Total}{(roll.Exploded ? " (esploso)" : "")} + TECH {tech} + cura {skillLvl} - ferite {Math.Abs(penalty)} = {total} vs TN {tn} -> {(success ? "OK" : "FALLITO")}");

                if (success) target.Health.IsStabilized = true;
            }
        }

        if (error != null)
        {
            await ReplyHealAsync(reporter, false, error);
            return;
        }

        // Un tentativo fallito costa comunque l'azione del turno: niente rimborso (il rimborso è solo per errori).
        if (!success)
        {
            string failMessage = $"{healer.Handle} tenta di stabilizzare {target.Handle} ma fallisce: {total} contro TN {tn}{(fumble ? " (fallimento critico)" : "")}.";
            await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = failMessage }), includeOrigin: false);
            await ReplyHealAsync(reporter, false, failMessage, target.Handle);
            return;
        }

        string message = $"{healer.Handle} stabilizza {target.Handle}: {total} contro TN {tn}. Non tira più il salvezza contro la morte.";
        await BroadcastToGameAsync(reporter, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = message }), includeOrigin: false);
        await ReplyHealAsync(reporter, true, message, target.Handle);
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
        CharacterRepository.NormalizeForUse(newChar);

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
        [OpCode.C2S_Attack, OpCode.C2S_SkillCheck, OpCode.C2S_Move, OpCode.C2S_Init, OpCode.C2S_Reload, OpCode.C2S_Throw, OpCode.C2S_Heal];

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
                    lock (game.SyncRoot)
                    {
                        infos = [.. game.NPCs.Select(n => new NpcInfoPayload
                        {
                            Handle = n.Handle,
                            Role = n.Role,
                            CurrentHealth = n.Health.RemainingBoxes,
                            MaxHealth = n.Health.MaxHealth,
                            WoundStatus = n.Health.GetWoundStatusDescription(),
                            Position = n.Position
                        })];
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
                    string? turnNotice = null;
                    Game.Check? completedCheck = null;
                    var newlyDead = new List<Character>();
                    lock (_gamesLock)
                    lock (game.SyncRoot)
                    {
                        removed = game.NPCs.RemoveAll(n => n.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) > 0;
                        if (removed)
                        {
                            game.InitRolls.RemoveAll(r => r.PlayerName.Equals(handle, StringComparison.OrdinalIgnoreCase));
                            turnNotice = RemoveFromTurnOrder(game, handle, newlyDead);
                            // Senza questo, il check resterebbe in attesa di un NPC che non è più nel game.
                            completedCheck = PruneCheckEntry(game, handle);
                        }
                    }

                    foreach (var dead in newlyDead) SaveCharacter(dead);

                    if (turnNotice != null)
                    {
                        await BroadcastToGameAsync(client, NetworkPacket.Create(OpCode.S2C_Notification, new { Message = turnNotice }));
                    }

                    if (completedCheck != null)
                    {
                        await FinalizeCheckAsync(game, completedCheck, forced: false);
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

                    var npcStatus = new NpcResponsePayload
                    {
                        Success = npc != null,
                        Action = action,
                        Message = npc != null ? $"Scheda dell'NPC '{npc.Handle}'." : $"NPC '{handle}' non trovato in questo game.",
                        Character = npc
                    };

                    // La scheda si serializza sotto il lock del game (vedi CreateCharacterPacket)
                    NetworkPacket npcPacket;
                    lock (game.SyncRoot)
                    {
                        npcPacket = NetworkPacket.Create(OpCode.S2C_NpcResponse, npcStatus);
                    }
                    await client.SendPacketAsync(npcPacket);
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
                    lock (game.SyncRoot)
                    {
                        game.NPCs.Add(npc);
                    }
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
    /// Riusa le stesse routine dei giocatori; l'unica differenza è che l'attore è l'NPC. Lo stato
    /// dell'NPC si salva come quello di un personaggio giocante (stessa politica di persistenza).
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

        if (!await EnsureAliveAsync(client, npc)) return;

        if (!NpcAllowedCommands.Contains(req.InnerOpCode))
        {
            await Fail("Con /as si possono usare solo: attack, skill, move, init, reload, throw, heal.");
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
                    await ResolveInitiativeAsync(client, npc);
                    break;

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

                case OpCode.C2S_Heal:
                    {
                        var healReq = JsonSerializer.Deserialize<HealRequestPayload>(req.InnerPayload);
                        string healTarget = InputLimits.Clean(healReq?.TargetHandle, InputLimits.MaxNameFieldLength);
                        if (healTarget.Length == 0) { await Fail("Uso: /as <npc> heal <personaggio>"); return; }

                        await ResolveStabilizeAsync(client, game, npc, healTarget);
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
        string? leaveTurnNotice = null;
        var newlyDead = new List<Character>();

        lock (_gamesLock)
        {
            var game = _games.FirstOrDefault(g => g.Players.Contains(client.Player));
            if (game != null)
            lock (game.SyncRoot)
            {
                leftGame = game;
                gameName = game.Name;
                wasMaster = client.Player.Role == Player.RoleType.master;

                game.Players.Remove(client.Player);

                // Un tiro di iniziativa di un personaggio che esce non ha più senso nel game
                if (client.Player.Character != null)
                {
                    game.InitRolls.RemoveAll(r => r.PlayerName.Equals(client.Player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    leaveTurnNotice = RemoveFromTurnOrder(game, client.Player.Character.Handle, newlyDead);
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

        foreach (var dead in newlyDead) SaveCharacter(dead);

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

        if (leaveTurnNotice != null)
        {
            await NotifyPlayersAsync(remaining, leaveTurnNotice);
        }

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

            if (currentGame != null)
            {
                denialMessage = $"Sei già nel gioco '{currentGame.Name}': usa /leave prima di entrare in un altro o di cambiare ruolo.";
            }
            // Il master non interpreta personaggi (stessa regola di /link); un giocatore deve avere già il suo
            // personaggio collegato, perché dentro un game /link e /unlink sono vietati (vedi JoinRules).
            else if (JoinRules.GetRoleDenial(joinReq.Role,
                         client.Player.Character != null || !string.IsNullOrEmpty(client.LinkedCharacterHandle)) is { } roleDenial)
            {
                denialMessage = roleDenial;
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

            if (success && game != null)
            {
                // Difesa in profondità: una sessione non deve comparire due volte nello stesso game.
                // La composizione del game cambia sotto entrambi i lock (vedi IsInGameLocked)
                lock (game.SyncRoot)
                {
                    if (!game.Players.Contains(client.Player)) game.Players.Add(client.Player);
                }
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

            await ResolveInitiativeAsync(client, client.Player.Character);
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

    private static async Task ReplyCheckRegistrationAsync(ClientSession reporter, Character actor, CheckRegistration reg, string what)
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
        lock (game.SyncRoot)
        {
            if (game.ActiveCheck != null)
            {
                error = "C'è già un check aperto: usa /check status, /check close o /check cancel.";
            }
            else if (!isSkill && game.CombatActive)
            {
                error = "Combattimento in corso: termina con /combat end prima di tirare una nuova iniziativa.";
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
                        if (c.Character.Health.IsDead) excluded.Add(c.Character.Handle);
                        else if (!combatants.Any(x => ReferenceEquals(x.Character, c.Character))) combatants.Add(c);
                    }

                    if (combatants.Count == 0)
                    {
                        error = "Nessun personaggio disponibile per il check (nessuno in gioco, o tutti morti).";
                    }
                    else
                    {
                        game.ActiveCheck = new Game.Check
                        {
                            Kind = isSkill ? Game.CheckKind.Skill : Game.CheckKind.Initiative,
                            SkillName = skillName,
                            TargetNumber = targetNumber,
                            Entries = [.. combatants.Select(x => new Game.CheckEntry { Handle = x.Character.Handle })]
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
                total = RollInitiative(c.Character).Total;
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
            + (excluded.Count > 0 ? $" Esclusi (morti): {string.Join(", ", excluded)}." : "");
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
        lock (game.SyncRoot)
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

                game.TurnOrder = [.. ordered.Select(e => new Game.TurnEntry { Handle = e.Handle, Initiative = e.Total!.Value })];
                game.InitRolls = [.. ordered.Select(e => new Game.Roll { Value = e.Total!.Value, PlayerName = e.Handle, Timestamp = TimeOnly.FromDateTime(DateTime.UtcNow) })];
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

    private sealed record InitiativeRollResult(int Ref, int CombatSense, int Cyber, int Dice, int Total);

    // Iniziativa: REF + CombatSense (solo i Solo) + cyberware INIT + d10
    private static InitiativeRollResult RollInitiative(Character actor)
    {
        int refStat = actor.Stats.REF;

        int combatSense = 0;
        if (actor.Role == Character.RoleType.Solo)
        {
            combatSense = actor.Skills.TryGetValue("CombatSense", out int csLvl) ? csLvl : 0;
        }

        int cyberInit = SumCyberBonus(actor, "INIT");

        int dice = (int)GameDice.Roll("1d10").Total;
        return new InitiativeRollResult(refStat, combatSense, cyberInit, dice, refStat + combatSense + cyberInit + dice);
    }

    /// <summary>
    /// Tiro di iniziativa di <paramref name="actor"/> nel game di <paramref name="reporter"/> (la sessione che ha
    /// dato il comando: il giocatore stesso, o il master che tira per un NPC). Con un check di iniziativa aperto il tiro va
    /// allo spoglio (vale il primo); senza, un nuovo tiro sostituisce il precedente.
    /// </summary>
    private async Task ResolveInitiativeAsync(ClientSession reporter, Character actor)
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

        var init = RollInitiative(actor);
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

        Console.WriteLine($"[INIT] {reporter.Player.Username} ({actor.Handle}) tira iniziativa: REF {init.Ref} + CombatSense {init.CombatSense} + Cyberware {init.Cyber} + d10 {init.Dice} = {total}{(reg == CheckRegistration.NotInCheck ? "" : $" ({reg})")}");

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

    // Un personaggio morto non agisce: nessuna azione (attacco, lancio, ricarica, movimento, prova).
    private static async Task<bool> EnsureAliveAsync(ClientSession reporter, Character actor)
    {
        if (!actor.Health.IsDead) return true;

        await reporter.SendPacketAsync(NetworkPacket.Create(OpCode.S2C_Notification, new
        {
            Message = $"{actor.Handle} è morto: non può agire."
        }));
        return false;
    }

    // Rimuove il giocatore da tutti i game. Restituisce, per ogni game rimasto aperto di cui era master,
    // il nome e i giocatori rimasti (da avvisare).
    private List<(string GameName, List<Player> Remaining)> RemovePlayerFromGames(Player player, out List<(Game Game, Game.Check Check)> completedChecks, out List<(List<Player> Remaining, string Text)> turnNotices, out List<Character> deadCharacters)
    {
        var masterNotices = new List<(string, List<Player>)>();
        completedChecks = [];
        turnNotices = [];
        deadCharacters = [];

        lock (_gamesLock)
        {
            foreach (var game in _games.Where(g => g.Players.Contains(player)).ToList())
            lock (game.SyncRoot)
            {
                game.Players.Remove(player);

                if (player.Character != null)
                {
                    game.InitRolls.RemoveAll(r => r.PlayerName.Equals(player.Character.Handle, StringComparison.OrdinalIgnoreCase));
                    var turnText = RemoveFromTurnOrder(game, player.Character.Handle, deadCharacters);
                    if (turnText != null && game.Players.Count > 0) turnNotices.Add(([.. game.Players], turnText));
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
            // Chi è ancora nel login non riceve le notifiche di rete
            if (!client.IsAuthenticated) continue;
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
