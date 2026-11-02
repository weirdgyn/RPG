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
                Console.WriteLine();
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
            }

            if (_isRunning)
            {
                _isRunning = false;
                _commandLoopActive = false;
                PrintServerOutput(() => AnsiConsole.MarkupLine("[bold red][[!]]: Connessione al server persa. Digita /connect <host> <port> per riconnetterti.[/]"));
            }
        }

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

                default:
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[grey][[PACKET]][/] Ricevuto OpCode: [yellow]{packet.OpCode}[/]"));
                    break;
            }
        }

        private static string FormatCombatStep(string step)
        {
            int colon = step.IndexOf(':');
            if (colon <= 0 || colon > 40) return Markup.Escape(step);
            return $"[bold cyan]{Markup.Escape(step[..(colon + 1)])}[/]{Markup.Escape(step[(colon + 1)..])}";
        }

        private static string FormatWeaponAmmo(Weapon w)
        {
            if (w != null && w.MagazineCapacity > 0 && !string.IsNullOrEmpty(w.Caliber))
            {
                return $" | Caricatore: {w.CurrentAmmo}/{w.MagazineCapacity} ({w.Caliber})";
            }
            return string.Empty;
        }

        private static void PrintCharacterStatus(Character c)
        {
            var mainGrid = new Grid();
            mainGrid.AddColumn();

            mainGrid.AddRow(new Markup($"[bold yellow]PERSONAGGIO:[/] [bold cyan]{Markup.Escape(c.Handle)}[/]  [grey]|[/]  [bold yellow]RUOLO:[/] [white]{Markup.Escape(c.Role.ToString())}[/]  [grey]|[/]  [bold yellow]POS:[/] [cyan]({c.Position.X}, {c.Position.Y}, {c.Position.Z})[/]"));

            int currentHp = Math.Max(0, c.Health.CurrentHealth);
            int maxHp = Math.Max(1, c.Health.MaxHealth);
            double hpPercent = (double)currentHp / maxHp;
            string hpColor = hpPercent > 0.6 ? "green" : (hpPercent > 0.25 ? "yellow" : "red");
            int barTotal = 20;
            int filled = Math.Clamp((int)Math.Round(hpPercent * barTotal), 0, barTotal);
            string bar = new string('█', filled) + new string('░', barTotal - filled);
            mainGrid.AddRow(new Markup($"[bold yellow]SALUTE:[/] [{hpColor}]{bar}[/] [{hpColor}]{currentHp}/{maxHp}[/] [grey]({Markup.Escape(c.Health.GetWoundStatusDescription())})[/]"));

            var statsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            statsTable.AddColumns("[yellow]INT[/]", "[yellow]REF[/]", "[yellow]TECH[/]", "[yellow]COOL[/]", "[yellow]ATTR[/]", "[yellow]MA[/]", "[yellow]BODY[/]", "[yellow]EMP[/]");
            statsTable.AddRow(
                c.Stats.INT.ToString(),
                c.Stats.REF.ToString(),
                c.Stats.TECH.ToString(),
                c.Stats.COOL.ToString(),
                c.Stats.ATTR.ToString(),
                c.Stats.MA.ToString(),
                c.Stats.BODY.ToString(),
                c.Stats.EMP.ToString());
            mainGrid.AddRow(statsTable);

            var equipTable = new Table().Border(TableBorder.Simple).BorderColor(Color.DarkSlateGray3);
            equipTable.AddColumns("[cyan]Locazione[/]", "[cyan]Armatura[/]", "[cyan]SP Attuale / Max[/]");

            bool anyArmor = false;
            if (c.Armor != null)
            {
                foreach (var loc in new[] { ArmorLocation.Head, ArmorLocation.Torso, ArmorLocation.Arms, ArmorLocation.Legs })
                {
                    var piece = c.Armor.GetPiece(loc);
                    if (piece != null)
                    {
                        anyArmor = true;
                        string spColor = piece.CurrentStopPower == piece.MaxStopPower ? "green" : (piece.CurrentStopPower > 0 ? "yellow" : "red");
                        equipTable.AddRow(loc.ToString(), Markup.Escape(piece.Name), $"[{spColor}]{piece.CurrentStopPower}/{piece.MaxStopPower}[/]");
                    }
                }
            }
            if (!anyArmor)
            {
                equipTable.AddRow("-", "[grey]Nessuna armatura equipaggiata[/]", "-");
            }
            mainGrid.AddRow(equipTable);

            if (c.EquippedWeapon != null)
            {
                string ammoInfo = FormatWeaponAmmo(c.EquippedWeapon);
                mainGrid.AddRow(new Markup($"[bold yellow]ARMA IN PUGNO:[/] [bold cyan]{Markup.Escape(c.EquippedWeapon.Name)}[/] [grey]({c.EquippedWeapon.Type})[/] [grey]|[/] Danno: [bold red]{c.EquippedWeapon.DamageDice}[/]{ammoInfo}"));
            }
            else
            {
                mainGrid.AddRow(new Markup("[bold yellow]ARMA IN PUGNO:[/] [grey]Nessuna[/]"));
            }

            if (c.Weapons != null && c.Weapons.Count > 0)
            {
                var wepLines = c.Weapons.Select(w =>
                {
                    string equipped = (c.EquippedWeapon != null && c.EquippedWeapon.Id == w.Id) ? " [bold green](EQUIPAGGIATA)[/]" : string.Empty;
                    return $"[white]{Markup.Escape(w.Name)}[/] [grey]({w.DamageDice})[/]{equipped}";
                });
                mainGrid.AddRow(new Markup("[bold yellow] ARMI:[/] " + string.Join("  ", wepLines)));
            }

            if (c.Ammos != null && c.Ammos.Count > 0)
            {
                var ammoLines = c.Ammos.Select(a =>
                {
                    string effect = a.Effect == AmmoEffect.ArmorPiercing ? " [red](AP)[/]" : (a.Effect == AmmoEffect.Expanding ? " [yellow](EX)[/]" : string.Empty);
                    return $"[white]{Markup.Escape(a.Name)}[/]{effect}: [green]{a.Quantity}[/]";
                });
                mainGrid.AddRow(new Markup("[bold yellow] MUNIZIONI:[/] " + string.Join("  ", ammoLines)));
            }

            if (c.Cyberware != null && c.Cyberware.Count > 0)
            {
                var cwLines = c.Cyberware.Select(cw =>
                {
                    string bonus = cw.Bonus != null && cw.Bonus.Value != 0 ? $" [green][[{cw.Bonus.Value:+0;-0} {cw.Bonus.ApplyTo}]][/ ]" : string.Empty;
                    return $"[cyan]{Markup.Escape(cw.ID)}[/] [grey]({cw.Type})[/]{bonus} - [grey]{Markup.Escape(cw.Description)}[/]";
                });
                mainGrid.AddRow(new Markup("[bold yellow] CYBERWARE:[/]\n  " + string.Join("\n  ", cwLines)));
            }

            var panel = new Panel(mainGrid)
            {
                Border = BoxBorder.Heavy,
                Header = new PanelHeader($"[bold yellow] SCHEDA PERSONAGGIO - {Markup.Escape(c.Handle)} [/]"),
                BorderStyle = new Style(Color.Yellow)
            };

            AnsiConsole.Write(panel);
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

                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green]��� Accesso autorizzato:[/] [bold cyan]{Markup.Escape(loginResp.PlayerName)}[/]\n"));

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
