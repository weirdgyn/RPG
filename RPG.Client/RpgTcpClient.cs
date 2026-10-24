using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.World;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json;

namespace RPG.Client
{
    public class RpgTcpClient
    {
        private const int CONNECTION_TIMEOUT = 15;
        private TcpClient? _tcpClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isRunning;
        private Player? _player;
        // Serializza l'accesso alla console: il listener di rete e il loop dei comandi
        // scrivono entrambi, e il prompt deve restare in fondo senza essere "seppellito"
        // dai messaggi asincroni del server.
        private static readonly object _consoleLock = new();
        private bool _promptPending;
        // True solo quando il loop dei comandi è attivo: prima (es. durante il login)
        // i messaggi del server vengono stampati senza ripristinare il prompt.
        private bool _commandLoopActive;
        // Timestamp dell'ultimo /ping inviato (per calcolare il RTT del Pong)
        private DateTime _lastPingSentUtc;
        // Completato dal listener di rete quando arriva la risposta al login
        private TaskCompletionSource<LoginResponsePayload> _loginTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// Mostra il prompt solo se non è già visibile (evita "> > " duplicati
        /// quando il listener ha già ripristinato il prompt dopo un messaggio).
        /// </summary>
        private void ShowPromptIfIdle()
        {
            lock (_consoleLock)
            {
                if (_promptPending) return;
                Console.Write("> ");
                _promptPending = true;
            }
        }
        /// <summary>
        /// Stampa un blocco proveniente dal server (thread del listener) e, se il loop
        /// dei comandi è attivo, ripristina immediatamente il prompt: l'utente non resta
        /// mai "senza prompt" dopo una risposta asincrona.
        /// </summary>
        private void PrintServerOutput(Action render)
        {
            lock (_consoleLock)
            {
                Console.WriteLine(); // chiude l'eventuale riga del prompt corrente
                render();
                if (_commandLoopActive)
                {
                    Console.Write("> ");
                    _promptPending = true;
                }
            }
        }
        public async Task<bool> ConnectAsync(string host, int port)
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(host, port);

                var stream = _tcpClient.GetStream();
                _reader = new StreamReader(stream);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                _isRunning = true;

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Errore di connessione: {ex.Message}");
                return false;
            }
        }
        private async Task ListenToServerAsync()
        {
            try
            {
                string? line;
                while (_isRunning && _reader != null && (line = await _reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var packet = JsonSerializer.Deserialize<NetworkPacket>(line);
                    if (packet == null) continue;

                    HandleServerPacket(packet);
                }
            }
            catch (Exception)
            {
                if (_isRunning)
                {
                    _isRunning = false;
                    PrintServerOutput(() => Console.WriteLine("[!] Connessione al server persa."));
                }
            }
        }
        /// <summary>
        /// Gestisce i pacchetti ricevuti dal server, deserializzando il payload e stampando le informazioni
        /// </summary>
        /// <param name="packet"></param>
        private void HandleServerPacket(NetworkPacket packet)
        {
            switch (packet.OpCode)
            {
                case OpCode.S2C_LoginResponse:
                    var loginResp = packet.DeserializePayload<LoginResponsePayload>();
                    if (loginResp != null)
                    {
                        // Sblocca il loop di login in RunLoopAsync, che gestisce la visualizzazione e il retry
                        _loginTcs.TrySetResult(loginResp);
                    }
                    break;

                case OpCode.S2C_Pong:
                    var pong = packet.DeserializePayload<PongPayload>();
                    if (pong != null)
                    {
                        double rttMs = Math.Round((DateTime.UtcNow - _lastPingSentUtc).TotalMilliseconds, 1);
                        PrintServerOutput(() => Console.WriteLine($"🏓 [PONG] {pong.Message} (RTT: {rttMs} ms)"));
                    }
                    break;

                case OpCode.S2C_PlayerStateUpdate:
                    var statePayload = packet.DeserializePayload<CharacterStatePayload>();
                    if (_player != null && statePayload?.Character != null)
                    {
                        // Salva lo stato in memoria locale per i comandi /skills e consultazione rapida
                        _player.Character = statePayload.Character;
                        PrintServerOutput(() => PrintCharacterStatus(_player.Character));
                    }
                    break;

                case OpCode.S2C_AttackResult:
                    var attackResp = packet.DeserializePayload<AttackResponsePayload>();
                    if (attackResp != null)
                    {
                        PrintServerOutput(() =>
                        {
                            Console.WriteLine($"⚔️ [COMBAT] {attackResp.AttackerHandle} vs {attackResp.TargetHandle}");
                            Console.WriteLine($"   Tiro Attacco: {attackResp.AttackTotal} vs Difesa TN: {attackResp.DefenseTN}");
                            Console.WriteLine($"   Esito       : {attackResp.Message}");
                            if (attackResp.IsHit)
                            {
                                if (!string.IsNullOrEmpty(attackResp.HitLocation))
                                {
                                    string mult = attackResp.DamageMultiplier > 1 ? $" (danno x{attackResp.DamageMultiplier})" : "";
                                    Console.WriteLine($"   Impatto     : {attackResp.HitLocation}{mult}");
                                }
                                Console.WriteLine($"   Stato {attackResp.TargetHandle}: {attackResp.TargetRemainingHealth} HP ({attackResp.TargetWoundStatus})");
                            }
                        });
                    }
                    break;

                case OpCode.S2C_Notification:
                    var notif = packet.DeserializePayload<JsonElement>();
                    if (notif.TryGetProperty("Message", out var msgProp))
                    {
                        string msg = msgProp.GetString() ?? string.Empty;
                        PrintServerOutput(() => Console.WriteLine($"📢 [NOTIFICA] {msg}"));
                    }
                    break;

                case OpCode.S2C_RollResult:
                    var rollResp = packet.DeserializePayload<RollResponsePayload>();
                    if (rollResp != null)
                    {
                        PrintServerOutput(() => Console.WriteLine($"🎲 [TIRO DADI] {rollResp.Message}"));
                    }
                    break;

                case OpCode.S2C_SkillCheckResult:
                    var skillResp = packet.DeserializePayload<SkillCheckResponsePayload>();
                    if (skillResp != null)
                    {
                        string icon = skillResp.IsSuccess ? "✅" : "❌";
                        PrintServerOutput(() =>
                        {
                            Console.WriteLine($"🧠 [PROVA ABILITÀ] {skillResp.CharacterHandle} -> {skillResp.SkillName} ({skillResp.StatName})");
                            Console.WriteLine($"   Tiro: {skillResp.TotalResult} (Stat:{skillResp.StatValue} + Lvl:{skillResp.SkillLevel} + d10:{skillResp.DiceRoll}) vs TN: {skillResp.TargetNumber}");
                            Console.WriteLine($"   Esito: {icon} {skillResp.Message}");
                        });
                    }
                    break;

                case OpCode.S2C_ChatMessage:
                    var chat = packet.DeserializePayload<ChatPayload>();
                    if (chat != null)
                    {
                        bool isMine = _player != null
                            && string.Equals(_player.Username, chat.Sender, StringComparison.OrdinalIgnoreCase);
                        bool hasRecipient = !string.IsNullOrWhiteSpace(chat.Recipient);

                        PrintServerOutput(() =>
                        {
                            if (hasRecipient && isMine)
                                Console.WriteLine($"🤫 [PRIVATO → {chat.Recipient}] {chat.Message}");
                            else if (hasRecipient)
                                Console.WriteLine($"🤫 [PRIVATO da {chat.Sender}] {chat.Message}");
                            else
                                Console.WriteLine($"💬 [{chat.Sender}] {chat.Message}");
                        });
                    }
                    break;

                case OpCode.S2C_SkillListResponse:
                    var skillListResp = packet.DeserializePayload<SkillListResponsePayload>();
                    if (skillListResp?.Skills != null)
                    {
                        if (_player != null)
                        {
                            // Se abbiamo lo stato locale, aggiorniamo il dizionario
                            if (_player.Character != null)
                            {
                                _player.Character.Skills = skillListResp.Skills;
                                PrintServerOutput(() => PrintSkillList(_player.Character));
                            }
                            else
                            {
                                // TODO: Skill list should be gathered from server if the _player.Character is invalid
                                // Fallback se la scheda completa non è ancora in memoria
                                PrintServerOutput(() =>
                                {
                                    Console.WriteLine("========================================");
                                    Console.WriteLine($"🧠 ELENCO ABILITÀ - {skillListResp.Character}");
                                    Console.WriteLine("----------------------------------------");
                                    foreach (var kv in skillListResp.Skills)
                                    {
                                        string statAssoc = Skills.GetStatForSkill(kv.Key);
                                        Console.WriteLine($"  - {kv.Key,-20} : Livello {kv.Value} (Stat: {statAssoc})");
                                    }
                                    Console.WriteLine("========================================");
                                });
                            }
                        }
                    }

                    break;

                case OpCode.S2C_PlayerMoved:
                    var movePayload = packet.DeserializePayload<MoveResultPayload>();
                    if (movePayload != null)
                    {
                        bool isSelf = _player.Character != null
                            && string.Equals(_player.Character.Handle, movePayload.PlayerHandle, StringComparison.OrdinalIgnoreCase);

                        PrintServerOutput(() =>
                        {
                            if (movePayload.Result == GameMap.MovementResult.Success)
                            {
                                var pos = movePayload.ActualPosition;
                                if (isSelf)
                                {
                                    Console.WriteLine($"✅ [MOVIMENTO] {movePayload.Message}");
                                }
                                else
                                {
                                    Console.WriteLine($"📍 [MOVIMENTO] {movePayload.PlayerHandle} si è spostato a ({pos.X}, {pos.Y}, {pos.Z})");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"🚫 [MOVIMENTO] {movePayload.Message}");
                            }
                        });
                    }
                    break;

                case OpCode.S2C_PlayerList:
                    var listResp = packet.DeserializePayload<PlayerListResponsePayload>();
                    if (listResp != null)
                    {
                        PrintServerOutput(() =>
                        {
                            Console.WriteLine("👥 GIOCATORI ONLINE");
                            Console.WriteLine("----------------------------------------");
                            if (listResp.Players.Count == 0)
                            {
                                Console.WriteLine("  (Nessun giocatore online)");
                            }
                            foreach (var p in listResp.Players)
                            {
                                string marker = (_player != null
                                    && string.Equals(_player.Username, p.Handle, StringComparison.OrdinalIgnoreCase))
                                    ? "  (tu)" : "";
                                Console.WriteLine($"  - {p.Handle,-16}{marker,-8} [{p.Role}]");
                            }
                            Console.WriteLine("----------------------------------------");
                        });
                    }
                    break;

                case OpCode.S2C_JoinResponse:
                    var joinResp = packet.DeserializePayload<JoinResponsePayload>();
                    if (joinResp != null)
                    {
                        if(joinResp.Success)
                        {
                            PrintServerOutput(() => Console.WriteLine($"[JOIN] Sei entrato nel game '{joinResp.GameName}' come '{joinResp.Role}'."));
                        }
                        else
                        {
                            PrintServerOutput(() => Console.WriteLine($"[JOIN] Fallito: {joinResp.Message}"));
                        }
                    }
                    break;

                case OpCode.S2C_LinkResponse:
                    var linkResp = packet.DeserializePayload<LinkResponsePayload>();
                    
                    if(linkResp != null)
                    {
                        if (linkResp.Success)
                        {
                            PrintServerOutput(() => Console.WriteLine($"[LINK] Collegamento riuscito: Player '{linkResp.PlayerHandle}' con Character '{linkResp.CharacterHandle}'."));
                            _player.Role = linkResp.Role;
                            _player.Handle = linkResp.CharacterHandle;
                        }
                        else
                        {
                            PrintServerOutput(() => Console.WriteLine($"[LINK] Fallito: {linkResp.Message}"));
                        }
                    }
                    break;

                case OpCode.S2C_InitRollRequest:
                    var initReq = packet.DeserializePayload<InitRollRequestPayload>();

                    if (initReq != null)
                    {
                        string msg = string.IsNullOrWhiteSpace(initReq.Message)
                            ? "Il master ha avviato un tiro di iniziativa."
                            : initReq.Message;
                        PrintServerOutput(() => Console.WriteLine($"[INIT] {msg}"));
                    }
                    break;

                case OpCode.S2C_GamesResponse:
                    var gamesResp = packet.DeserializePayload<GamesResponsePayload>();
                    if (gamesResp != null)
                    {
                        PrintServerOutput(() => {
                            Console.WriteLine("👥 SESSIONI DI GIOCO");
                            Console.WriteLine("----------------------------------------");
                            foreach (var game in gamesResp.Games)
                                Console.WriteLine($"{game}");
                            Console.WriteLine("----------------------------------------");
                        });
                    }
                    break;

                case OpCode.S2C_CharactersResponse:
                    var charactersResp = packet.DeserializePayload<CharactersResponsePayload>();
                    if (charactersResp != null)
                    {
                        PrintServerOutput(() => {
                            Console.WriteLine("👥 PERSONAGGI DISPONIBILI");
                            Console.WriteLine("----------------------------------------");
                            foreach (var _char in charactersResp.Characters)
                                Console.WriteLine($"{_char.Handle}\t{_char.Role}");
                            Console.WriteLine("----------------------------------------");
                        });
                    }
                    break;

                default:
                    PrintServerOutput(() => Console.WriteLine($"[PACKET] Ricevuto OpCode non gestito: {packet.OpCode}"));
                    break;
            }
        }
        /// <summary>
        /// Stampa il menu di aiuto con la lista dei comandi disponibili, formattata in modo leggibile.
        /// </summary>
        private static void PrintHelpMenu()
        {
            lock (_consoleLock)
            {
                Console.WriteLine("\n========================================");
                Console.WriteLine("🎮 COMANDI DISPONIBILI - NIGHT CITY RPG");
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("  /status                               - Visualizza la scheda personaggio");
                Console.WriteLine("  /status \"<Character>\"               - Visualizza la scheda personaggio (master only)");
                Console.WriteLine("  /skills                               - Visualizza la lista delle abilità del personaggio");
                Console.WriteLine("  /move <X> <Y> [Z]                     - Muova il personaggio nello spazio 3D");
                Console.WriteLine("  /attack <Bersaglio>                   - Attacca il bersaglio con l'arma equipaggiata");
                Console.WriteLine("  /attack <Bersaglio> <Arma/Abilità>    - Attacca il bersaglio specificando arma o abilità (es. /attack Jackie ArtiMarziali)");
                Console.WriteLine("  /roll <Espressione> [Motivazione]     - Effettua un tiro di dadi (es. /roll 1d10+4 Iniziativa)");
                Console.WriteLine("  /skill <NomeAbilità> [TN]             - Effettua un test di abilità (es. /skill Percezione 15)");
                Console.WriteLine("  /chat \"<Messaggio>\" [userid]        - Chat broadcast, o privata aggiungendo l'userid dopo le virgolette");
                Console.WriteLine("  /link \"<Character>\"                 - Collega il player ad un personaggio esistente");
                Console.WriteLine("  /join \"<Game>\" [master|player]      - Entra in un game esistente");
                Console.WriteLine("  /init <Modifier>                      - Avvia tiro iniziativa (master) / Esegue tiro iniziativa (plasyer)");
                Console.WriteLine("  /characters                           - Elenca i personaggi disponibili sul server");
                Console.WriteLine("  /games                                - Elenca le sessioni di gioco attive");
                Console.WriteLine("  /ping                                 - Verifica la latenza con il server (ping/pong)");
                Console.WriteLine("  /list                                 - Elenca i giocatori online");
                Console.WriteLine("  /help                                 - Mostra questo menu");
                Console.WriteLine("  /exit                                 - Disconnettiti ed esci");
                Console.WriteLine("========================================\n");
            }
        }
        /// <summary>
        /// Stampa la lista delle abilità del personaggio, mostrando il livello, la statistica associata e il pool totale calcolato.
        /// </summary>
        /// <param name="character"></param>
        private static void PrintSkillList(Character character)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine($"🧠 ELENCO ABILITÀ - {character.Handle}");
            Console.WriteLine("----------------------------------------");
            foreach (var kv in character.Skills)
            {
                string statAssoc = Skills.GetStatForSkill(kv.Key);
                int statVal = character.Stats.GetStatValue(statAssoc);
                int totalPool = statVal + kv.Value;
                Console.WriteLine($"  - {kv.Key,-20} : Livello {kv.Value} ({statAssoc}: {statVal}) -> Pool Base: {totalPool}");
            }
            Console.WriteLine("========================================");
        }
        /// <summary>
        /// Stampa le informazioni sul caricatore e il calibro dell'arma, se disponibili. Restituisce una stringa formattata da aggiungere alla descrizione dell'arma.
        /// </summary>
        /// <param name="w">L'arma di cui stampare le informazioni sul caricatore</param>
        /// <returns>Una stringa formattata con le informazioni sul caricatore</returns>
        private static string FormatWeaponAmmo(Weapon w)
        {
            if (w != null && w.MagazineCapacity > 0 && !string.IsNullOrEmpty(w.Caliber))
            {
                return $" | Caricatore: {w.CurrentAmmo}/{w.MagazineCapacity} ({w.Caliber})";
            }
            return "";
        }
        /// <summary>
        /// Stampa lo stato completo del personaggio, inclusi posizione, statistiche, salute, armatura, armi e munizioni.
        /// </summary>
        /// <param name="c">Lo stato del personaggio da stampare</param>
        private static void PrintCharacterStatus(Character c)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine($"📋 SCHEDA PERSONAGGIO: {c.Handle}");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($" Posizione 3D -> X:{c.Position.X:F1} | Y:{c.Position.Y:F1} | Z:{c.Position.Z:F1}");
            Console.WriteLine($" Stats        -> INT:{c.Stats.INT} | REF:{c.Stats.REF} | TECH:{c.Stats.TECH} | COOL:{c.Stats.COOL}");
            Console.WriteLine($"                 ATTR:{c.Stats.ATTR} | MA:{c.Stats.MA} | BODY:{c.Stats.BODY} | EMP:{c.Stats.EMP}");
            Console.WriteLine($" Salute       -> {c.Health.CurrentHealth} / {c.Health.MaxHealth} ({c.Health.GetWoundStatusDescription()})");

            Console.WriteLine(" 🛡️ Armatura:");
            bool anyArmor = false;
            if (c.Armor != null)
            {
                foreach (var loc in new[] { ArmorLocation.Head, ArmorLocation.Torso, ArmorLocation.Arms, ArmorLocation.Legs })
                {
                    var piece = c.Armor.GetPiece(loc);
                    if (piece != null)
                    {
                        anyArmor = true;
                        Console.WriteLine($"   - {loc,-5}: {piece.Name} [{piece.Material}] SP {piece.CurrentStopPower}/{piece.MaxStopPower}");
                    }
                }
            }
            if (!anyArmor)
            {
                Console.WriteLine("   (Nessuna armatura equipaggiata)");
            }

            if (c.EquippedWeapon != null)
            {
                Console.WriteLine($" ⚔️ Arma Impugnata : {c.EquippedWeapon.Name} [{c.EquippedWeapon.Type}] (Danno: {c.EquippedWeapon.DamageDice}, Skill: {c.EquippedWeapon.SkillName}{FormatWeaponAmmo(c.EquippedWeapon)})");
            }
            else
            {
                Console.WriteLine(" ⚔️ Arma Impugnata : Nessuna");
            }

            Console.WriteLine(" 🎒 Armi in Inventario:");
            if (c.Weapons != null && c.Weapons.Count > 0)
            {
                foreach (var w in c.Weapons)
                {
                    string marker = (c.EquippedWeapon != null && c.EquippedWeapon.Id == w.Id) ? " (EQUIPAGGIATA)" : "";
                    Console.WriteLine($"   - {w.Name} [{w.Type}] | Danno: {w.DamageDice} | Skill: {w.SkillName}{FormatWeaponAmmo(w)}{marker}");
                }
            }
            else
            {
                Console.WriteLine("   (Inventario vuoto)");
            }

            Console.WriteLine(" 🔫 Munizioni:");
            if (c.Ammos != null && c.Ammos.Count > 0)
            {
                foreach (var a in c.Ammos)
                {
                    string effect = a.Effect == AmmoEffect.ArmorPiercing ? " (AP)" : (a.Effect == AmmoEffect.Expanding ? " (EX)" : "");
                    Console.WriteLine($"   - {a.Name}{effect} | {a.Quantity} colpi");
                }
            }
            else
            {
                Console.WriteLine("   (Nessuna munizione in inventario)");
            }

            Console.WriteLine("========================================");

        }
        /// <summary>
        /// Loop principale di I/O: gestisce il login, i comandi dell'utente e l'ascolto dei messaggi del server.
        /// </summary>
        /// <returns></returns>
        public async Task RunLoopAsync()
        {
            if (_reader == null || _writer == null) return;

            _ = Task.Run(() => ListenToServerAsync());

            Console.WriteLine("\n========================================");

            // Loop di login con retry: si ripropone l'player_name finché il server non accetta (es. player_name già in uso)
            bool loggedIn = false;
            
            while (!loggedIn)
            {
                Console.Write("Inserisci il tuo username: ");

                string? player_name = Console.ReadLine();
 
                if (string.IsNullOrWhiteSpace(player_name))
                {
                    player_name = $"Punk_{Guid.NewGuid().ToString()[..4]}";
                }

                _loginTcs = new TaskCompletionSource<LoginResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);

                var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
                {
                    PlayerName = player_name.Trim()
                });

                await _writer.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

                // Attende la risposta del server (con timeout per non restare appesi se la connessione cade)
                LoginResponsePayload loginResp;
                try
                {
                    loginResp = await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(CONNECTION_TIMEOUT));
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("[!] Nessuna risposta dal server. Verifica la connessione e riprova.");
                    if (!_isRunning) break;
                    continue;
                }

                if (!loginResp.Success)
                {
                    PrintServerOutput(() => Console.WriteLine($"[!] Login rifiutato: {loginResp.Message}"));
                    continue; // ripete il prompt con un nuovo player_name
                }

                PrintServerOutput(() => Console.WriteLine($"[SERVER] {loginResp.Message}"));

                _player = new Player
                {
                    Username = loginResp.PlayerName
                };

                loggedIn = true;
            }

            if (!loggedIn) return; // chiusura senza login (es. connessione persa)

            PrintHelpMenu();

            // D'ora in poi i messaggi del server ripristinano il prompt quando arrivano
            _commandLoopActive = true;

            while (_isRunning)
            {
                ShowPromptIfIdle();

                string? input = Console.ReadLine();
                // La riga del prompt è stata "consumata" dall'input utente
                _promptPending = false;

                if (string.IsNullOrWhiteSpace(input)) { continue; }

                string trimmedInput = input.Trim().ToLower();

                if (string.IsNullOrWhiteSpace(trimmedInput)) { continue; }
                else if (!trimmedInput.StartsWith('/'))
                {
                    Console.WriteLine("[!] I comandi devono iniziare con il caratter '/'.");
                    continue;
                }
                else if (trimmedInput.StartsWith('/'))
                {
                    if (_player == null)
                    {
                        Console.WriteLine("[!] Devi essere loggato per utilizzare i comandi.");
                        continue;
                    }
                }

                if (trimmedInput.Equals("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    // Registra il momento dell'invio per calcolare il RTT quando arriva il Pong
                    _lastPingSentUtc = DateTime.UtcNow;
                    
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Ping, new { })));
                    
                    continue;
                }

                if (trimmedInput.Equals("/list", StringComparison.OrdinalIgnoreCase))
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetPlayerList, new { })));
                    
                    continue;
                }

                if (trimmedInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    _isRunning = false;
                    
                    break;
                }

                if (trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelpMenu();
                    
                    continue;
                }

                // Commands after this check need to be applied to valid players
                if (_player == null) { continue; }

                if (trimmedInput.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(_player.Handle))
                    {
                        Console.WriteLine("[!] Questo tipo di comando può essere eseguito solo dopo il link al personaggio.");
                        
                        continue;
                    }

                    // Invia al server la richiesta esplicita di aggiornamento stato
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetStatus, new { })));
                    
                    continue;
                }

                if (trimmedInput.Equals("/skills", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(_player.Handle))
                    {
                        Console.WriteLine("[!] Questo tipo di comando può essere eseguito solo dopo il link al personaggio.");
                        
                        continue;
                    }

                    // Richiesta autoritativa delle abilità al server
                    // (risposta gestita nel case S2C_SkillListResponse)
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetSkills, new { })));
                    
                    continue;
                }

                if(trimmedInput.Equals("/games", StringComparison.OrdinalIgnoreCase))
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetGames, new { })));

                    continue;
                }

                if (trimmedInput.Equals("/characters", StringComparison.OrdinalIgnoreCase))
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetCharacters, new { })));

                    continue;
                }

                if (trimmedInput.StartsWith("/init", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string modifier = (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1])) ? parts[1] : string.Empty;

                    await _writer.WriteLineAsync(
                        JsonSerializer.Serialize(
                            NetworkPacket.Create( OpCode.C2S_Init, new InitRequest { 
                                PlayerName = _player.Username,
                                Modifiers = modifier
                            }
                    )));

                    continue;
                }

                if (trimmedInput.StartsWith("/move", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(_player.Handle))
                    {
                        Console.WriteLine("[!] Questo tipo di comando può essere eseguito solo dopo il link al personaggio.");
                        
                        continue;
                    }

                    string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    
                    if (parts.Length >= 4 &&
                        int.TryParse(parts[1], out int x) &&
                        int.TryParse(parts[2], out int y) &&
                        int.TryParse(parts[3], out int z))
                    {
                        var movePacket = NetworkPacket.Create(OpCode.C2S_Move, new MoveRequestPayload
                        {
                            Position = new Position3D { X = x, Y = y, Z = z }
                        });
                        
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                    }
                    else if(parts.Length==3 &&
                        int.TryParse(parts[1], out int x1) &&
                        int.TryParse(parts[2], out int y1))
                    {
                        var movePacket = NetworkPacket.Create(OpCode.C2S_Move, new MoveRequestPayload
                        {
                            Position = new Position3D { X = x1, Y = y1, Z = _player.Character?.Position.Z ?? 0 }
                        });
                        
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /move <X> <Y> [Z] (es. /move 10 0 5)");
                    }
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/attack", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(_player.Handle))
                    {
                        Console.WriteLine("[!] Questo tipo di comando può essere eseguito solo dopo il link al personaggio.");
                        
                        continue;
                    }

                    // Parsing dall'input ORIGINALE (non lowercased) per preservare il case
                    // di player_name, armi e abilità (es. "ArtiMarziali").
                    string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string targetHandle = parts[1];
                        string? weaponOrSkill = parts.Length >= 3 ? parts[2] : null;

                        var attackPacket = NetworkPacket.Create(OpCode.C2S_Attack, new AttackRequestPayload
                        {
                            TargetHandle = targetHandle,
                            WeaponOrSkill = weaponOrSkill
                        });
                        
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(attackPacket));
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /attack <Bersaglio> [Arma|Abilità]");
                    }
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/roll", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmedInput.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    string diceExpr = parts.Length >= 2 ? parts[1] : "1d10";
                    string reason = parts.Length >= 3 ? parts[2] : "Tiro libero";

                    var rollPacket = NetworkPacket.Create(OpCode.C2S_Roll, new RollRequestPayload
                    {
                        DiceExpression = diceExpr,
                        Reason = reason
                    });
                    
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(rollPacket));
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/skill", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(_player.Handle))
                    {
                        Console.WriteLine("[!] Questo tipo di comando può essere eseguito solo dopo il link al personaggio.");
                        
                        continue;
                    }

                    // Parsing dall'input ORIGINALE (non lowercased) per preservare il case
                    // del nome dell'abilità (es. "Pistola", "ArtiMarziali").
                    string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string skillName = parts[1];
                        int difficulty = 15; // TN Standard di default (Medio)

                        if (parts.Length >= 3 && int.TryParse(parts[2], out int parsedDiff))
                        {
                            difficulty = parsedDiff;
                        }

                        var skillPacket = NetworkPacket.Create(OpCode.C2S_SkillCheck, new SkillCheckRequestPayload
                        {
                            SkillName = skillName,
                            Difficulty = difficulty
                        });
                       
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(skillPacket));
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /skill <NomeAbilità> [Difficoltà/TN]");
                        Console.WriteLine("    Esempi: /skill Percezione 15  oppure  /skill Guidare");
                    }
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/chat", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = input.Trim();
                    string rest = raw.Length > 5 ? raw[5..].Trim() : string.Empty;

                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        Console.WriteLine("[!] Uso: /chat \"<messaggio>\" [userid]");
                        
                        continue;
                    }

                    // Il messaggio è delimitato da virgolette: /chat "testo" [userid]
                    int firstQuote = rest.IndexOf('"');
                    if (firstQuote < 0)
                    {
                        Console.WriteLine("[!] Il messaggio deve essere racchiuso tra virgolette. Es.: /chat \"Salve a tutti\" oppure /chat \"Ti cerco\" Nome");
                        
                        continue;
                    }
                    int lastQuote = rest.LastIndexOf('"');
                    if (lastQuote == firstQuote)
                    {
                        Console.WriteLine("[!] Virgolette non chiuse. Es.: /chat \"Salve a tutti\"");
                        
                        continue;
                    }

                    string message = rest[(firstQuote + 1)..lastQuote].Trim();
                    string after = rest[(lastQuote + 1)..].Trim(); // eventuale userid

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        Console.WriteLine("[!] Messaggio vuoto. Uso: /chat \"<messaggio>\" [userid]");
                        
                        continue;
                    }

                    string? recipient = string.IsNullOrWhiteSpace(after) ? null : after;

                    var chatPacket = NetworkPacket.Create(OpCode.C2S_Chat, new ChatPayload
                    {
                        Message = message,
                        Recipient = recipient
                    });
                   
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(chatPacket));
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/join", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string roleInput = parts.Length >= 3 ? parts[2] : "player";

                    // 2. Convertiamo la stringa nell'enum RoleType in modo sicuro (ignoreCase: true gestisce "Master", "PLAYER", ecc.)
                    Player.RoleType parsedRole = Enum.TryParse<Player.RoleType>(roleInput, true, out var result)
                        ? result
                        : Player.RoleType.player;

                    var joinPacket = NetworkPacket.Create(OpCode.C2S_Join, new JoinRequestPayload
                    {
                        GameName = parts.Length >= 2 ? parts[1] : "default",
                        Role = parsedRole
                    });

                    await _writer.WriteLineAsync(JsonSerializer.Serialize(joinPacket));
                    
                    continue;
                }

                if (trimmedInput.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2)
                    {
                        var chartacterHandle = parts[1].Trim();

                        var linkPacket = NetworkPacket.Create(OpCode.C2S_Link, new LinkRequestPayload
                        {
                            PlayerHandle = _player.Username,
                            CharacterHandle = chartacterHandle
                        });

                        await _writer.WriteLineAsync(JsonSerializer.Serialize(linkPacket));
                    }
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /link <NomeCharacter>");
                    }
                    
                    continue;
                }

                Console.WriteLine($"[!] Comando non riconosciuto: '{trimmedInput}'. Digita /help per la lista comandi.");
            }

            _tcpClient?.Close();
        }
    }
}