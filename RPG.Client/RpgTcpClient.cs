using RPG.Common;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.World;
using Spectre.Console;
using System.Net.Sockets;
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

        private void ResetLocalSessionState()
        {
            _player = null;
            _commandLoopActive = false;
            _promptPending = false;
            _loginTcs = new TaskCompletionSource<LoginResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task DisconnectAsync()
        {
            _isRunning = false;
            _commandLoopActive = false;
            _promptPending = false;

            try { _writer?.Close(); } catch { }
            try { _reader?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _writer = null;
            _reader = null;
            _tcpClient = null;

            ResetLocalSessionState();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Mostra il prompt solo se non è già visibile (evita prompt duplicati
        /// quando il listener ha già ripristinato il prompt dopo un messaggio).
        /// </summary>
        private void ShowPromptIfIdle()
        {
            lock (_consoleLock)
            {
                if (_promptPending) return;
                AnsiConsole.Markup("[bold yellow]NC-NET[/] [bold cyan]>[/] ");
                _promptPending = true;
            }
        }

        /// <summary>
        /// Stampa un blocco proveniente dal server (thread del listener) e, se il loop
        /// dei comandi è attivo, ripristina immediatamente il prompt.
        /// </summary>
        private void PrintServerOutput(Action render)
        {
            lock (_consoleLock)
            {
                Console.WriteLine(); // chiude l'eventuale riga del prompt corrente
                render();
                if (_commandLoopActive)
                {
                    AnsiConsole.Markup("[bold yellow]NC-NET[/] [bold cyan]>[/] ");
                    _promptPending = true;
                }
            }
        }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(host) && _tcpClient != null && _tcpClient.Connected)
                {
                    return true;
                }

                ResetLocalSessionState();
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
                AnsiConsole.MarkupLine($"[bold red][[!]]: Errore di connessione:[/] [red]{Markup.Escape(ex.Message)}[/]");
                _isRunning = false;
                _reader = null;
                _writer = null;
                _tcpClient = null;
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

                    try
                    {
                        var packet = JsonSerializer.Deserialize<NetworkPacket>(line);
                        if (packet == null) continue;

                        HandleServerPacket(packet);
                    }
                    catch (Exception ex)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold red][[!]]: Pacchetto dal server ignorato:[/] [red]{Markup.Escape(ex.Message)}[/]"));
                    }
                }
            }
            catch (Exception)
            {
                // Errore di rete (connessione interrotta, socket chiuso)
            }

            if (_isRunning)
            {
                _isRunning = false;
                _commandLoopActive = false;
                PrintServerOutput(() => AnsiConsole.MarkupLine("[bold red][[!]]: Connessione al server persa. Digita /connect <host> <port> per riconnetterti.[/]"));
            }
        }

        /// <summary>
        /// Gestisce i pacchetti ricevuti dal server, deserializzando il payload e stampando le informazioni con Spectre.Console
        /// </summary>
        private void HandleServerPacket(NetworkPacket packet)
        {
            switch (packet.OpCode)
            {
                case OpCode.S2C_LoginResponse:
                    var loginResp = packet.DeserializePayload<LoginResponsePayload>();
                    if (loginResp != null)
                    {
                        _loginTcs.TrySetResult(loginResp);
                    }
                    break;

                case OpCode.S2C_Pong:
                    var pong = packet.DeserializePayload<PongPayload>();
                    if (pong != null)
                    {
                        double rttMs = Math.Round((DateTime.UtcNow - _lastPingSentUtc).TotalMilliseconds, 1);
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green][[PONG]][/] [white]{Markup.Escape(pong.Message)}[/] [grey](RTT:[/] [bold yellow]{rttMs} ms[/][grey])[/]"));
                    }
                    break;

                case OpCode.S2C_PlayerStateUpdate:
                    var statePayload = packet.DeserializePayload<CharacterStatePayload>();
                    if (_player != null && statePayload?.Character != null)
                    {
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
                            var grid = new Grid();
                            grid.AddColumn();

                            grid.AddRow(new Markup($"[bold yellow]{Markup.Escape(attackResp.AttackerHandle)}[/] [red]attacca[/] [bold yellow]{Markup.Escape(attackResp.TargetHandle)}[/]" +
                                (attackResp.WeaponName.Length > 0 ? $" con [cyan]{Markup.Escape(attackResp.WeaponName)}[/]" : "")));

                            foreach (var step in attackResp.Steps)
                            {
                                grid.AddRow(new Markup("  " + FormatCombatStep(step)));
                            }

                            string resultColor = attackResp.IsHit ? (attackResp.DamageDealt > 0 ? "bold red" : "bold cyan") : "bold grey";
                            grid.AddRow(new Markup($"[{resultColor}]{Markup.Escape(attackResp.Message)}[/]"));

                            if (attackResp.AmmoCapacity > 0)
                            {
                                string ammoColor = attackResp.AmmoLeft == 0 ? "bold red" : "cyan";
                                grid.AddRow(new Markup($"  Colpi: [{ammoColor}]{attackResp.AmmoLeft}/{attackResp.AmmoCapacity}[/] [grey]({Markup.Escape(attackResp.AmmoName)})[/]"));
                            }

                            var panel = new Panel(grid)
                            {
                                Header = new PanelHeader("[bold red] COMBATTIMENTO [/]"),
                                Border = BoxBorder.Heavy,
                                BorderStyle = new Style(Color.Red1)
                            };

                            AnsiConsole.Write(panel);
                        });
                    }
                    break;

                case OpCode.S2C_Notification:
                    var notif = packet.DeserializePayload<JsonElement>();
                    if (notif.ValueKind == JsonValueKind.Object && notif.TryGetProperty("Message", out var msgProp))
                    {
                        string msg = msgProp.GetString() ?? string.Empty;
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold yellow][[NOTIFICA]][/] [white]{Markup.Escape(msg)}[/]"));
                    }
                    break;

                case OpCode.S2C_RollResult:
                    var rollResp = packet.DeserializePayload<RollResponsePayload>();
                    if (rollResp != null)
                    {
                        string rollReason = string.IsNullOrWhiteSpace(rollResp.Reason) ? "" : $" [italic grey]({Markup.Escape(rollResp.Reason)})[/]";
                        string rollDetails = string.IsNullOrWhiteSpace(rollResp.Details) ? "" : $" [grey]{Markup.Escape(rollResp.Details)}[/]";
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold yellow][[ROLL]][/] [bold cyan]{Markup.Escape(rollResp.RollerHandle)}[/] tira [bold yellow]{Markup.Escape(rollResp.DiceExpression)}[/]{rollReason}{rollDetails}: [bold green]{rollResp.TotalResult}[/]"));
                    }
                    break;

                case OpCode.S2C_SkillCheckResult:
                    var skillResp = packet.DeserializePayload<SkillCheckResponsePayload>();
                    if (skillResp != null)
                    {
                        PrintServerOutput(() =>
                        {
                            string icon = skillResp.IsFumble ? "[bold red] FALLIMENTO CRITICO[/]"
                                : skillResp.IsSuccess ? "[bold green] SUCCESSO[/]" : "[bold red] FALLIMENTO[/]";
                            var grid = new Grid();
                            grid.AddColumn();
                            grid.AddRow(new Markup($"[bold cyan]{Markup.Escape(skillResp.CharacterHandle)}[/] prova [bold yellow]{Markup.Escape(skillResp.SkillName)}[/] ({skillResp.StatName})"));
                            grid.AddRow(new Markup($"   Calcolo: [cyan]{skillResp.TotalResult}[/] [grey](Stat:{skillResp.StatValue} + Lvl:{skillResp.SkillLevel} + d10:{skillResp.DiceRoll})[/] vs [cyan]{skillResp.TargetNumber}[/]"));
                            grid.AddRow(new Markup($"   Esito: {icon} - [white]{Markup.Escape(skillResp.Message)}[/]"));

                            var panel = new Panel(grid)
                            {
                                Header = new PanelHeader("[bold magenta] PROVA ABILITÀ [/]"),
                                Border = BoxBorder.Rounded,
                                BorderStyle = new Style(skillResp.IsSuccess ? Color.Green : Color.Red)
                            };

                            AnsiConsole.Write(panel);
                        });
                    }
                    break;

                case OpCode.S2C_ChatMessage:
                    var chat = packet.DeserializePayload<ChatPayload>();
                    if (chat != null)
                    {
                        bool isMine = _player != null && string.Equals(_player.Username, chat.Sender, StringComparison.OrdinalIgnoreCase);
                        bool hasRecipient = !string.IsNullOrWhiteSpace(chat.Recipient);

                        PrintServerOutput(() =>
                        {
                            if (hasRecipient && isMine)
                            {
                                AnsiConsole.MarkupLine($"[italic fuchsia][[PRIVATO → {Markup.Escape(chat.Recipient!)}]][/] [white]{Markup.Escape(chat.Message)}[/]");
                            }
                            else if (hasRecipient)
                            {
                                AnsiConsole.MarkupLine($"[italic fuchsia][[PRIVATO da {Markup.Escape(chat.Sender)}]][/] [bold white]{Markup.Escape(chat.Message)}[/]");
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[bold cyan][[{Markup.Escape(chat.Sender)}]][/] [white]{Markup.Escape(chat.Message)}[/]");
                            }
                        });
                    }
                    break;

                case OpCode.S2C_SkillListResponse:
                    var skillListResp = packet.DeserializePayload<SkillListResponsePayload>();
                    if (skillListResp != null)
                    {
                        PrintServerOutput(() =>
                        {
                            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
                            table.AddColumns(
                                new TableColumn("[bold yellow]Abilità[/]"),
                                new TableColumn("[bold cyan]Livello[/]").Centered(),
                                new TableColumn("[bold magenta]Statistica[/]").Centered(),
                                new TableColumn("[bold green]Pool Base[/]").Centered(),
                                new TableColumn("[bold green]Pool Effettivo[/]").Centered()
                            );

                            if (skillListResp.Skills.Count == 0)
                            {
                                table.AddRow("[grey](Nessuna abilità registrata)[/]", "-", "-", "-", "-");
                            }

                            foreach (var s in skillListResp.Skills)
                            {
                                int effectivePool = Math.Max(0, s.BasePool + skillListResp.WoundPenalty);
                                string statDisplay = $"{s.Stat} ({s.StatValue})";
                                string effDisplay = skillListResp.WoundPenalty < 0
                                    ? $"[bold red]{effectivePool}[/]"
                                    : $"[bold green]{effectivePool}[/]";

                                table.AddRow(
                                    Markup.Escape(s.Name),
                                    s.Level.ToString(),
                                    statDisplay,
                                    s.BasePool.ToString(),
                                    effDisplay
                                );
                            }

                            string headerText = $"[bold yellow] ABILITÀ - {Markup.Escape(skillListResp.Character)} [/]";
                            if (skillListResp.WoundPenalty < 0)
                            {
                                headerText += $"[bold red](Penalità Ferite: {skillListResp.WoundPenalty})[/]";
                            }

                            var panel = new Panel(table)
                            {
                                Header = new PanelHeader(headerText),
                                Border = BoxBorder.Heavy,
                                BorderStyle = new Style(Color.Yellow)
                            };

                            AnsiConsole.Write(panel);
                        });
                    }
                    break;

                case OpCode.S2C_PlayerMoved:
                    var movePayload = packet.DeserializePayload<MoveResultPayload>();
                    if (movePayload != null)
                    {
                        bool isSelf = _player?.Character != null
                            && string.Equals(_player.Character.Handle, movePayload.PlayerHandle, StringComparison.OrdinalIgnoreCase);

                        PrintServerOutput(() =>
                        {
                            if (movePayload.Result == GameMap.MovementResult.Success)
                            {
                                var pos = movePayload.ActualPosition;
                                if (isSelf)
                                {
                                    AnsiConsole.MarkupLine($"[bold green][[MOVIMENTO]][/] [white]{Markup.Escape(movePayload.Message)}[/]");
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[bold blue][[MOVIMENTO]][/] [bold cyan]{Markup.Escape(movePayload.PlayerHandle)}[/] si è spostato a [green]({pos.X}, {pos.Y}, {pos.Z})[/]");
                                }
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[bold red][[MOVIMENTO FALLITO]][/] [white]{Markup.Escape(movePayload.Message)}[/]");
                            }
                        });
                    }
                    break;

                default:
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[grey][[PACKET]][/] Ricevuto OpCode: [yellow]{packet.OpCode}[/]"));
                    break;
            }
        }

        /// <summary>
        /// Una riga del calcolo di combattimento ("Etichetta: dettaglio"): l'etichetta in evidenza, il resto
        /// con il testo del server sempre escapato.
        /// </summary>
        private static string FormatCombatStep(string step)
        {
            int colon = step.IndexOf(':');
            if (colon <= 0 || colon > 40) return Markup.Escape(step);
            return $"[bold cyan]{Markup.Escape(step[..(colon + 1)])}[/]{Markup.Escape(step[(colon + 1)..])}";
        }

        private static ThrowRequestPayload? BuildThrowRequest(string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y)) return null;

            int z = 0;
            bool zProvided = args.Length >= 3 && int.TryParse(args[2], out z);
            int weaponIndex = zProvided ? 3 : 2;

            return new ThrowRequestPayload
            {
                Position = new Position3D { X = x, Y = y, Z = z },
                ZProvided = zProvided,
                WeaponKey = args.Length > weaponIndex ? string.Join(' ', args.Skip(weaponIndex)) : null
            };
        }

        private static CheckRequestPayload? BuildCheckRequest(string[] parts)
        {
            if (parts.Length < 2) return null;

            string verb = parts[1].ToLowerInvariant();
            string[] SplitHandles(int from) => string.Join(' ', parts.Skip(from))
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            switch (verb)
            {
                case "skill" when parts.Length >= 4 && int.TryParse(parts[3], out int tn):
                    return new CheckRequestPayload { Action = "skill", SkillName = parts[2], TargetNumber = tn, Handles = [.. SplitHandles(4)] };

                case "init":
                case "initiative":
                case "iniziativa":
                    return new CheckRequestPayload { Action = "init", Handles = [.. SplitHandles(2)] };

                case "status":
                case "close":
                case "cancel":
                    return new CheckRequestPayload { Action = verb };

                default:
                    return null;
            }
        }

        private static NetworkPacket? BuildAsPacket(string npcHandle, string verb, string[] args)
        {
            OpCode innerOp;
            string innerJson;

            switch (verb)
            {
                case "attack" when args.Length >= 1:
                    innerOp = OpCode.C2S_Attack;
                    innerJson = JsonSerializer.Serialize(new AttackRequestPayload
                    {
                        TargetHandle = args[0],
                        WeaponOrSkill = args.Length >= 2 ? args[1] : null
                    });
                    break;

                case "skill" when args.Length >= 1:
                    innerOp = OpCode.C2S_SkillCheck;
                    innerJson = JsonSerializer.Serialize(new SkillCheckRequestPayload
                    {
                        SkillName = args[0],
                        Difficulty = args.Length >= 2 && int.TryParse(args[1], out int tn) ? tn : 15
                    });
                    break;

                case "move" when args.Length >= 2 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y):
                    int z = 0;
                    bool zProvided = args.Length >= 3;
                    if (zProvided && !int.TryParse(args[2], out z)) return null;
                    innerOp = OpCode.C2S_Move;
                    innerJson = JsonSerializer.Serialize(new MoveRequestPayload
                    {
                        Position = new Position3D { X = x, Y = y, Z = z },
                        ZProvided = zProvided
                    });
                    break;

                case "reload":
                    innerOp = OpCode.C2S_Reload;
                    innerJson = JsonSerializer.Serialize(new ReloadRequestPayload
                    {
                        AmmoKey = args.Length >= 1 ? args[0] : null,
                        WeaponKey = args.Length >= 2 ? args[1] : null
                    });
                    break;

                case "throw":
                    {
                        var throwPayload = BuildThrowRequest(args);
                        if (throwPayload == null) return null;
                        innerOp = OpCode.C2S_Throw;
                        innerJson = JsonSerializer.Serialize(throwPayload);
                        break;
                    }

                case "init":
                    innerOp = OpCode.C2S_Init;
                    innerJson = JsonSerializer.Serialize(new InitRequest());
                    break;

                case "heal" when args.Length >= 1:
                    innerOp = OpCode.C2S_Heal;
                    innerJson = JsonSerializer.Serialize(new HealRequestPayload { TargetHandle = args[0] });
                    break;

                default:
                    return null;
            }

            return NetworkPacket.Create(OpCode.C2S_As, new AsRequestPayload
            {
                ActorHandle = npcHandle,
                InnerOpCode = innerOp,
                InnerPayload = innerJson
            });
        }

        private static void PrintHelpMenu()
        {
            lock (_consoleLock)
            {
                var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
                table.AddColumns(
                    new TableColumn("[bold yellow]Comando[/]"),
                    new TableColumn("[bold cyan]Argomenti[/]"),
                    new TableColumn("[bold green]Descrizione[/]")
                );

                table.AddRow("[bold white]/connect[/]", "[cyan][[host]] [[port]][/]", "Connette esplicitamente a un server senza eseguire login, /link o /join automatici");
                table.AddRow("[bold white]/status[/]", "[grey][[character>]][/]", "Visualizza la scheda del personaggio collegato (o specifico se Master)");
                table.AddRow("[bold white]/skills[/]", "[grey]-[/]", "Elenco abilità, statistiche associate e pool di tiro");
                table.AddRow("[bold white]/help[/]", "[grey]-[/]", "Mostra questo menu dei comandi");
                table.AddRow("[bold white]/exit[/]", "[grey]-[/]", "Disconnettiti ed esci da Night City");

                var panel = new Panel(table)
                {
                    Header = new PanelHeader("[bold yellow]  COMANDI NIGHT CITY BBS v2.0 [/]"),
                    Border = BoxBorder.Double,
                    BorderStyle = new Style(Color.Cyan1)
                };

                AnsiConsole.Write(panel);
            }
        }

        public async Task RunLoopAsync()
        {
            if (_reader == null || _writer == null)
            {
                AnsiConsole.MarkupLine("[bold yellow][[CONNECT]][/] Nessuna connessione attiva. Usa [cyan]/connect[/] [grey][host] [port][/].");
            }

            while (true)
            {
                if (_reader == null || _writer == null || !_isRunning)
                {
                    AnsiConsole.MarkupLine("[bold yellow][[CONNECT]][/] Nessuna connessione attiva. Usa [cyan]/connect[/] [grey][host] [port][/].");
                    string? input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        continue;
                    }

                    string trimmed = input.Trim();
                    if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!trimmed.Equals("/connect", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("/connect ", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine("[bold red][[!]]: Comando non valido.[/] Usa [yellow]/connect [host] [port][/].");
                        continue;
                    }

                    string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string host = "127.0.0.1";
                    int port = 5000;

                    if (parts.Length >= 2)
                    {
                        host = parts[1];
                    }
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                    else if (parts.Length >= 3)
                    {
                        AnsiConsole.MarkupLine("[bold red][[!]]: Porta non valida.[/] Usa [yellow]/connect [host] [port][/].");
                        continue;
                    }

                    bool connected = await ConnectAsync(host, port);
                    if (!connected)
                    {
                        AnsiConsole.MarkupLine($"[bold red][[!]]: Impossibile connettersi a [yellow]{host}:{port}[/].");
                        continue;
                    }

                    AnsiConsole.MarkupLine($"[bold green][[CONNECT]][/] Connesso a [bold cyan]{host}:{port}[/]. Nessun login, /link o /join automatici.");
                    continue;
                }

                bool loggedIn = false;
                while (!loggedIn)
                {
                    AnsiConsole.Markup("[bold yellow]NETRUNNER LOGIN[/] [grey]>> Inserisci username:[/] ");
                    string? playerName = Console.ReadLine();

                    if (string.IsNullOrWhiteSpace(playerName))
                    {
                        playerName = $"Punk_{Guid.NewGuid().ToString()[..4]}";
                    }

                    _loginTcs = new TaskCompletionSource<LoginResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);

                    var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
                    {
                        PlayerName = playerName.Trim()
                    });

                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

                    LoginResponsePayload loginResp;
                    try
                    {
                        loginResp = await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(CONNECTION_TIMEOUT));
                    }
                    catch (TimeoutException)
                    {
                        AnsiConsole.MarkupLine("[bold red][[!]]: Nessuna risposta dal server. Verifica la connessione e riprova.[/]");
                        if (!_isRunning) break;
                        continue;
                    }

                    if (!loginResp.Success)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold red][[!]]: Login rifiutato:[/] [red]{Markup.Escape(loginResp.Message)}[/]"));
                        continue;
                    }

                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green]✓ Accesso autorizzato:[/] [bold cyan]{Markup.Escape(loginResp.PlayerName)}[/]\n"));

                    _player = new Player
                    {
                        Username = loginResp.PlayerName
                    };

                    loggedIn = true;
                }

                if (!loggedIn) return;

                PrintHelpMenu();
                _commandLoopActive = true;
                _ = Task.Run(() => ListenToServerAsync());

                while (_isRunning)
                {
                    ShowPromptIfIdle();
                    string? input = Console.ReadLine();
                    _promptPending = false;

                    if (!_isRunning) break;
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        continue;
                    }

                    string trimmedInput = input.Trim();

                    if (trimmedInput.Equals("/connect", StringComparison.OrdinalIgnoreCase)
                        || trimmedInput.StartsWith("/connect ", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string host = "127.0.0.1";
                        int port = 5000;

                        if (parts.Length >= 2)
                        {
                            host = parts[1];
                        }
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int parsedPort))
                        {
                            port = parsedPort;
                        }
                        else if (parts.Length >= 3)
                        {
                            AnsiConsole.MarkupLine("[bold red][[!]]: Porta non valida.[/] Usa [yellow]/connect [host] [port][/].");
                            continue;
                        }

                        if (_writer != null && _reader != null && _tcpClient != null && _tcpClient.Connected)
                        {
                            AnsiConsole.MarkupLine("[bold yellow][[CONNECT]][/] Connessione già attiva. Chiudi la sessione esistente con /exit prima di riconnetterti.");
                            continue;
                        }

                        await DisconnectAsync();
                        bool connected = await ConnectAsync(host, port);
                        if (!connected)
                        {
                            AnsiConsole.MarkupLine($"[bold red][[!]]: Impossibile connettersi a [yellow]{host}:{port}[/].");
                            continue;
                        }

                        AnsiConsole.MarkupLine($"[bold green][[CONNECT]][/] Connesso a [bold cyan]{host}:{port}[/]. Nessun login, /link o /join automatici.");
                        continue;
                    }

                    if (trimmedInput.Equals("/ping", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastPingSentUtc = DateTime.UtcNow;
                        await _writer!.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Ping, new { })));
                        continue;
                    }

                    if (trimmedInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    {
                        _isRunning = false;
                        await DisconnectAsync();
                        return;
                    }

                    if (trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintHelpMenu();
                        continue;
                    }

                    if (_player == null) { continue; }

                    if (trimmedInput.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string? gameFilter = parts.Length > 1 ? parts[1].Trim() : null;
                        await _writer!.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetPlayers, new PlayersRequestPayload { GameName = gameFilter })));
                        continue;
                    }

                    if (trimmedInput.StartsWith("/create", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3)
                        {
                            AnsiConsole.MarkupLine("[bold red][[!]]: Uso corretto:[/] [yellow]/create <archetipo> <character.handle>[/]");
                            continue;
                        }

                        await _writer!.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_CreateCharacter, new CreateCharacterRequestPayload
                        {
                            Archetype = parts[1].Trim(),
                            CharacterHandle = parts[2].Trim()
                        })));
                        continue;
                    }

                    AnsiConsole.MarkupLine($"[bold red][[!]]: Comando non riconosciuto:[/] [yellow]'{Markup.Escape(trimmedInput)}'[/]. Digita [cyan]/help[/] per la lista.");
                }

                await DisconnectAsync();
            }
        }
    }
}
