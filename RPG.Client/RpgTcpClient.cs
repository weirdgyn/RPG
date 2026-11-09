using RPG.Common;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.World;
using Spectre.Console;
using RPG.Common.Utils;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace RPG.Client;

public class RpgTcpClient(bool useTls = true)
{
    private const int CONNECTION_TIMEOUT = 15;
    // Impronte dei certificati dei server già visitati (trust on first use, come known_hosts di SSH).
    private readonly TrustStore _trust = TrustStore.Load(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RPG", "known_servers.json"));
    private TcpClient? _tcpClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _isRunning;
    private Player? _player;

    // Ultimo server usato (per /connect senza parametri) e richiesta di uscita dell'utente:
    // la perdita della connessione non chiude più il client, che resta in attesa di /connect o /exit.
    private string _lastHost = string.Empty;
    private int _lastPort;
    private bool _exitRequested;

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
    private static readonly Dictionary<string, HelpEntry> HelpEntries = LoadHelpEntries();

    private static Dictionary<string, HelpEntry> LoadHelpEntries()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "help.json");

        if (!File.Exists(path))
        {
            return new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            string json = File.ReadAllText(path);

            return HelpCatalog.ParseFromJson(json);
        }
        catch
        {
            return new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);
        }
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
            _lastHost = host;
            _lastPort = port;
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);

            Stream stream = _tcpClient.GetStream();
            if (useTls) stream = await AuthenticateTlsAsync(stream, host, port);
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _isRunning = true;

            return true;
        }
        catch (Exception ex)
        {
            try { _tcpClient?.Close(); } catch { /* già chiuso */ }
            AnsiConsole.MarkupLine($"[bold red][[!]]: Errore di connessione:[/] [red]{Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }

    /// <summary>
    /// Handshake TLS con verifica dell'impronta: il certificato del server è autofirmato, quindi la fiducia
    /// si basa sull'impronta memorizzata alla prima connessione. Se cambia, la connessione è rifiutata.
    /// </summary>
    private async Task<Stream> AuthenticateTlsAsync(Stream inner, string host, int port)
    {
        TrustResult? result = null;
        string seen = string.Empty;

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            if (cert == null) return false;
            seen = TrustStore.Fingerprint(cert.GetRawCertData());
            result = _trust.Check(host, port, seen);
            return result != TrustResult.Mismatch;
        });

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CONNECTION_TIMEOUT));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
        }
        catch (Exception ex) when (result == TrustResult.Mismatch)
        {
            AnsiConsole.MarkupLine("[bold red][[!]] ATTENZIONE: il certificato di questo server è cambiato.[/]");
            AnsiConsole.MarkupLine($"[red]Memorizzata:[/] {_trust.Get(host, port)}");
            AnsiConsole.MarkupLine($"[red]Ricevuta:   [/] {seen}");
            AnsiConsole.MarkupLine("[grey]Potrebbe essere un'intercettazione. Se il server è stato reinstallato e l'impronta è quella attesa, " +
                $"rimuovi la voce '{Markup.Escape(TrustStore.Key(host, port))}' dal file known_servers.json (cartella ApplicationData, sottocartella RPG) e riconnettiti.[/]");
            throw new InvalidOperationException("connessione rifiutata: certificato del server diverso da quello memorizzato.", ex);
        }
        catch (Exception ex)
        {
            throw new IOException($"handshake TLS fallito ({ex.Message}). Se il server è avviato con --no-tls, avvia il client con --no-tls.", ex);
        }

        if (result == TrustResult.NewServer)
        {
            bool saved = _trust.Remember(host, port, seen);
            AnsiConsole.MarkupLine($"[grey]Primo collegamento a questo server: impronta del certificato memorizzata{(saved ? "" : " (solo per questa sessione: salvataggio non riuscito)")}.[/]");
            AnsiConsole.MarkupLine($"[grey]SHA-256:[/] {seen}");
        }

        return ssl;
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
            PrintServerOutput(() => AnsiConsole.MarkupLine("[bold red][[!]]: Connessione al server persa. Usa[/] [yellow]/connect [[host]] [[porta]][/] [bold red]per riconnetterti o[/] [yellow]/exit[/] [bold red]per uscire.[/]"));
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
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold yellow][[ROLL]][/] [bold cyan]{Markup.Escape(rollResp.RollerHandle)}[/] tira [bold yellow]{Markup.Escape(rollResp.DiceExpression)}[/]{rollReason} -> [bold green]{rollResp.TotalResult}[/]{rollDetails}"));
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
                        // Tutti i passaggi: stat + livello + d10, poi ferite ed effetti (ognuno con la sua etichetta)
                        string skillCalc = $"Stat:{skillResp.StatValue} + Lvl:{skillResp.SkillLevel} + d10:{skillResp.DiceRoll}";
                        if (skillResp.WoundPenalty != 0)
                        {
                            skillCalc += $" {(skillResp.WoundPenalty < 0 ? "-" : "+")} ferite:{Math.Abs(skillResp.WoundPenalty)}";
                        }
                        foreach (var m in skillResp.Modifiers)
                        {
                            skillCalc += $" {(m.Value < 0 ? "-" : "+")} {m.Label}:{Math.Abs(m.Value)}";
                        }
                        grid.AddRow(new Markup($"   Calcolo: [cyan]{skillResp.TotalResult}[/] [grey]({Markup.Escape(skillCalc)})[/] vs TN: [bold yellow]{skillResp.TargetNumber}[/]"));
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

            case OpCode.S2C_Players:
                var listResp = packet.DeserializePayload<PlayersResponsePayload>();
                if (listResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
                        table.AddColumns(
                            new TableColumn("[bold yellow]Giocatore[/]"),
                            new TableColumn("[bold cyan]Ruolo Utente[/]").Centered(),
                            new TableColumn("[bold white]Personaggio[/]"),
                            new TableColumn("[bold green]Stanza di Gioco[/]")
                        );

                        if (listResp.Players.Count == 0)
                        {
                            table.AddRow("[grey](Nessun giocatore trovato)[/]", "-", "-", "-");
                        }

                        foreach (var p in listResp.Players)
                        {
                            bool isMe = _player != null && string.Equals(_player.Username, p.Handle, StringComparison.OrdinalIgnoreCase);
                            string playerDisplay = isMe
                                ? $"[bold green]{Markup.Escape(p.Handle)} (tu)[/]"
                                : $"[white]{Markup.Escape(p.Handle)}[/]";

                            string roleColor = p.Role == Player.RoleType.master ? "bold magenta" : "grey";
                            string roleDisplay = $"[{roleColor}]{p.Role}[/]";
                            string charDisplay = !string.IsNullOrWhiteSpace(p.CharacterHandle) && p.CharacterHandle != "---"
                                ? $"[bold cyan]{Markup.Escape(p.CharacterHandle)}[/]"
                                : "[grey]---[/]";
                            string gameDisplay = !string.IsNullOrWhiteSpace(p.Game) && p.Game != "---"
                                ? $"[bold yellow]{Markup.Escape(p.Game)}[/]"
                                : "[grey]---[/]";

                            table.AddRow(playerDisplay, roleDisplay, charDisplay, gameDisplay);
                        }

                        string title = string.IsNullOrWhiteSpace(listResp.FilterGame)
                            ? " GIOCATORI ONLINE"
                            : $" GIOCATORI NEL GIOCO '{Markup.Escape(listResp.FilterGame)}'";

                        var panel = new Panel(table)
                        {
                            Header = new PanelHeader($"[bold yellow] {title} [/]"),
                            Border = BoxBorder.Heavy,
                            BorderStyle = new Style(Color.Cyan1)
                        };

                        AnsiConsole.Write(panel);
                    });
                }
                break;

            case OpCode.S2C_JoinResponse:
                var joinResp = packet.DeserializePayload<JoinResponsePayload>();
                if (joinResp != null)
                {
                    if (joinResp.Success)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green][[JOIN]][/] Sei entrato nel game [bold yellow]'{Markup.Escape(joinResp.GameName)}'[/] come [cyan]'{joinResp.Role}'[/]."));
                        if (_player != null)
                        {
                            _player.Role = joinResp.Role;
                        }
                    }
                    else
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold red][[JOIN FALLITO]][/] [white]{Markup.Escape(joinResp.Message)}[/]"));
                    }
                }
                break;

            case OpCode.S2C_NpcResponse:
                var npcResp = packet.DeserializePayload<NpcResponsePayload>();
                if (npcResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        string tag = npcResp.Success ? "[bold green][[NPC]][/]" : "[bold red][[NPC FALLITO]][/]";
                        AnsiConsole.MarkupLine($"{tag} [white]{Markup.Escape(npcResp.Message)}[/]");

                        if (npcResp.Npcs.Count > 0)
                        {
                            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
                            table.AddColumn("[bold yellow]NPC[/]");
                            table.AddColumn("[bold yellow]Ruolo[/]");
                            table.AddColumn("[bold yellow]Salute[/]");
                            table.AddColumn("[bold yellow]Stato[/]");
                            table.AddColumn("[bold yellow]Posizione[/]");
                            foreach (var n in npcResp.Npcs)
                            {
                                table.AddRow(
                                    $"[bold cyan]{Markup.Escape(n.Handle)}[/]",
                                    n.Role.ToString(),
                                    $"{n.CurrentHealth}/{n.MaxHealth}",
                                    Markup.Escape(n.WoundStatus),
                                    $"({n.Position.X:F1}, {n.Position.Y:F1}, {n.Position.Z:F1})");
                            }
                            AnsiConsole.Write(table);
                        }

                        // La scheda di un NPC non sostituisce quella del proprio personaggio (_player.Character)
                        if (npcResp.Character != null)
                        {
                            PrintCharacterStatus(npcResp.Character);
                        }
                    });
                }
                break;

            case OpCode.S2C_LeaveResponse:
                var leaveResp = packet.DeserializePayload<LeaveResponsePayload>();
                if (leaveResp != null)
                {
                    if (leaveResp.Success)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green][[LEAVE]][/] [white]{Markup.Escape(leaveResp.Message)}[/]"));
                        if (_player != null)
                        {
                            _player.Role = leaveResp.Role;
                        }
                    }
                    else
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold red][[LEAVE FALLITO]][/] [white]{Markup.Escape(leaveResp.Message)}[/]"));
                    }
                }
                break;

            case OpCode.S2C_LinkResponse:
                var linkResp = packet.DeserializePayload<LinkResponsePayload>();
                if (linkResp != null)
                {
                    if (linkResp.Success)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green][[LINK]][/] Collegamento riuscito: Player [cyan]'{Markup.Escape(linkResp.PlayerHandle)}'[/] con Character [yellow]'{Markup.Escape(linkResp.CharacterHandle)}'[/]."));
                        if (_player != null)
                        {
                            _player.Role = linkResp.Role;
                            _player.Handle = linkResp.CharacterHandle;
                        }
                    }
                    else
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($" [bold red][[LINK FALLITO]][/] [white]{Markup.Escape(linkResp.Message)}[/]"));
                    }
                }
                break;

            case OpCode.S2C_UnlinkResponse:
                var unlinkResp = packet.DeserializePayload<UnlinkResponsePayload>();
                if (unlinkResp != null)
                {
                    if (unlinkResp.Success)
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold green][[UNLINK]][/] [white]{Markup.Escape(unlinkResp.Message)}[/]"));
                        if (_player != null)
                        {
                            _player.Character = null;
                            _player.Handle = string.Empty;
                            _player.Role = unlinkResp.Role;
                        }
                    }
                    else
                    {
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold red][[UNLINK FALLITO]][/] [white]{Markup.Escape(unlinkResp.Message)}[/]"));
                    }
                }
                break;

            case OpCode.S2C_HealResponse:
                var healResp = packet.DeserializePayload<HealResponsePayload>();
                if (healResp != null)
                {
                    string tag = healResp.Success ? "[bold green][[HEAL]][/]" : "[bold red][[HEAL FALLITO]][/]";
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"{tag} [white]{Markup.Escape(healResp.Message)}[/]"));
                }
                break;

            case OpCode.S2C_CreateCharacterResponse:
                var createResp = packet.DeserializePayload<CreateCharacterResponsePayload>();
                if (createResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        if (createResp.Success)
                        {
                            AnsiConsole.MarkupLine($" [bold green][[CREAZIONE]][/] [white]{Markup.Escape(createResp.Message)}[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($" [bold red][[CREAZIONE FALLITA]][/] [white]{Markup.Escape(createResp.Message)}[/]");
                        }
                    });
                }
                break;

            case OpCode.S2C_InitRollRequest:
                var initReq = packet.DeserializePayload<InitRollRequestPayload>();
                if (initReq != null)
                {
                    string msg = string.IsNullOrWhiteSpace(initReq.Message)
                        ? "Il master ha avviato un tiro di iniziativa."
                        : initReq.Message;
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"⚔️ [bold yellow][[INIZIATIVA]][/] [white]{Markup.Escape(msg)}[/]"));
                }
                break;

            case OpCode.S2C_AreaResult:
                var areaResp = packet.DeserializePayload<AreaResultPayload>();
                if (areaResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var grid = new Grid();
                        grid.AddColumn();
                        grid.AddRow(new Markup($"[bold yellow]{Markup.Escape(areaResp.ThrowerHandle)}[/] [red]lancia[/] [bold yellow]{Markup.Escape(areaResp.WeaponName)}[/]"));

                        foreach (var step in areaResp.Steps)
                        {
                            grid.AddRow(new Markup("  " + FormatCombatStep(step)));
                        }
                        grid.AddRow(new Markup($"[bold yellow]{Markup.Escape(areaResp.Message)}[/] [grey](pezzi rimasti: {areaResp.PiecesLeft})[/]"));

                        if (areaResp.Victims.Count == 0)
                        {
                            grid.AddRow(new Markup("  [grey]Nessuno nel raggio dell'esplosione.[/]"));
                        }
                        else
                        {
                            foreach (var v in areaResp.Victims)
                            {
                                grid.AddRow(new Markup($"[bold cyan]> {Markup.Escape(v.Handle)}[/]"));
                                foreach (var step in v.Steps)
                                {
                                    grid.AddRow(new Markup("    " + FormatCombatStep(step)));
                                }
                            }
                        }

                        AnsiConsole.Write(new Panel(grid)
                        {
                            Header = new PanelHeader("[bold red] ESPLOSIONE [/]"),
                            Border = BoxBorder.Heavy,
                            BorderStyle = new Style(Color.Red1)
                        });
                    });
                }
                break;

            case OpCode.S2C_WhoAmIResponse:
                var whoResp = packet.DeserializePayload<WhoAmIResponsePayload>();
                if (whoResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var grid = new Grid();
                        grid.AddColumn();
                        grid.AddRow(new Markup($"Utente: [bold cyan]{Markup.Escape(whoResp.Username)}[/]  Ruolo: [bold yellow]{Markup.Escape(whoResp.Role)}[/]"));
                        grid.AddRow(new Markup(whoResp.GameName.Length > 0
                            ? $"Game: [bold green]{Markup.Escape(whoResp.GameName)}[/]"
                            : "Game: [grey]nessuno[/] (usa /join)"));
                        if (whoResp.NpcCount > 0) grid.AddRow(new Markup($"NPC nel game: [cyan]{whoResp.NpcCount}[/]"));

                        if (whoResp.HasCharacter)
                        {
                            grid.AddRow(new Markup($"Personaggio: [bold yellow]{Markup.Escape(whoResp.CharacterHandle)}[/]  HP [bold]{whoResp.Health}/{whoResp.MaxHealth}[/] [grey]({Markup.Escape(whoResp.WoundStatus)})[/]"));
                            grid.AddRow(new Markup($"Posizione: ({whoResp.Position.X}, {whoResp.Position.Y}, {whoResp.Position.Z})  MA: {whoResp.MA}m{(whoResp.MAEffective >= 0 && whoResp.MAEffective != whoResp.MA ? $" [yellow](con effetti {whoResp.MAEffective}m)[/]" : "")}"));
                            if (whoResp.WeaponName.Length > 0)
                            {
                                string ammo = whoResp.AmmoCapacity > 0 ? $" - colpi {whoResp.AmmoLeft}/{whoResp.AmmoCapacity}" : "";
                                grid.AddRow(new Markup($"Arma in pugno: [cyan]{Markup.Escape(whoResp.WeaponName)}[/]{ammo}"));
                            }
                        }
                        else
                        {
                            grid.AddRow(new Markup("Personaggio: [grey]nessuno[/] (usa /link o /create)"));
                        }

                        AnsiConsole.Write(new Panel(grid) { Header = new PanelHeader("[bold cyan] CHI SONO [/]"), BorderStyle = new Style(Color.Cyan1) });
                    });
                }
                break;

            case OpCode.S2C_GameStatusResponse:
                var gs = packet.DeserializePayload<GameStatusResponsePayload>();
                if (gs != null)
                {
                    PrintServerOutput(() =>
                    {
                        if (!gs.InGame)
                        {
                            AnsiConsole.MarkupLine("[bold yellow][[GAME]][/] Non sei in nessun game: usa /join <game>.");
                            return;
                        }

                        var grid = new Grid();
                        grid.AddColumn();
                        grid.AddRow(new Markup($"Master: [bold yellow]{Markup.Escape(gs.Master.Length > 0 ? gs.Master : "(libero)")}[/]"));
                        if (gs.Description.Length > 0) grid.AddRow(new Markup($"[grey]{Markup.Escape(gs.Description)}[/]"));

                        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
                        table.AddColumn(new TableColumn("[bold cyan]Personaggio[/]"));
                        table.AddColumn(new TableColumn("[bold white]Utente / Ruolo[/]"));
                        table.AddColumn(new TableColumn("[bold white]Salute[/]"));
                        table.AddColumn(new TableColumn("[bold white]Posizione[/]").Centered());
                        bool anyEffects = gs.Members.Any(x => x.Effects.Count > 0);
                        if (anyEffects) table.AddColumn(new TableColumn("[bold white]Effetti[/]"));
                        foreach (var m in gs.Members)
                        {
                            string label = m.Handle.Length > 0 ? m.Handle : "[grey]-[/]";
                            string turn = (m.Handle.Length > 0 && m.Handle.Equals(gs.CurrentTurn, StringComparison.OrdinalIgnoreCase) ? " [bold green]<< turno[/]" : "")
                                + (m.IsStunned ? " [bold red]STORDITO[/]" : "");
                            string owner = m.IsNpc ? "[magenta]NPC[/]" : $"{Markup.Escape(m.Username)} [grey]({Markup.Escape(m.Role)})[/]";
                            string hp = m.Handle.Length == 0 ? "[grey]-[/]"
                                : m.Health >= 0 ? $"{m.Health}/{m.MaxHealth} [grey]({Markup.Escape(m.WoundStatus)})[/]"
                                : $"[grey]{Markup.Escape(m.WoundStatus)}[/]";
                            var cells = new List<string>
                            {
                                m.Handle.Length > 0 ? Markup.Escape(m.Handle) + turn : label, owner, hp,
                                m.Handle.Length > 0 ? $"({m.Position.X}, {m.Position.Y}, {m.Position.Z})" : "-"
                            };
                            if (anyEffects) cells.Add(m.Effects.Count == 0 ? "[grey]-[/]" : string.Join("\n", m.Effects.Select(Markup.Escape)));
                            table.AddRow(cells.Select(c => new Markup(c)));
                        }
                        grid.AddRow(table);

                        if (gs.CombatActive)
                        {
                            grid.AddRow(new Markup($"[bold red]Combattimento[/] - round [bold]{gs.Round}[/], di turno: [bold green]{Markup.Escape(gs.CurrentTurn)}[/] (movimento {gs.MoveLeft:0.#}m, azione {(gs.ActionUsed ? "usata" : "disponibile")})"));
                        }
                        if (gs.TurnOrder.Count > 0)
                        {
                            grid.AddRow(new Markup($"Ordine: {string.Join(" > ", gs.TurnOrder.Select(t => t.Equals(gs.CurrentTurn, StringComparison.OrdinalIgnoreCase) && gs.CombatActive ? $"[bold green]{Markup.Escape(t)}[/]" : Markup.Escape(t)))}"));
                        }
                        if (gs.CheckDescription.Length > 0)
                        {
                            grid.AddRow(new Markup($"[bold magenta]Check aperto:[/] {Markup.Escape(gs.CheckDescription)} - hanno tirato [[{Markup.Escape(string.Join(", ", gs.CheckRolled))}]], in attesa di [[{Markup.Escape(string.Join(", ", gs.CheckWaiting))}]]"));
                        }

                        string freeEstimate = gs.CharacterCount > 0 && gs.MaxActionsPerRound > 0
                            ? $" [grey](~{gs.FreeActionRounds:0.0} round di gruppo: {gs.FreeActionsCounter} / ({gs.MaxActionsPerRound} az./round x {gs.CharacterCount} personaggi))[/]"
                            : string.Empty;
                        grid.AddRow(new Markup($"[bold yellow]Azioni libere[/] [grey](fuori dal combattimento):[/] [bold]{gs.FreeActionsCounter}[/]{freeEstimate}"));

                        AnsiConsole.Write(new Panel(grid) { Header = new PanelHeader($"[bold green] GAME: {Markup.Escape(gs.Name)} [/]"), BorderStyle = new Style(Color.Green) });
                    });
                }
                break;

            case OpCode.S2C_CheckResult:
                var checkResp = packet.DeserializePayload<CheckResultPayload>();
                if (checkResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var table = new Table().Border(TableBorder.Rounded).BorderColor(checkResp.IsInitiative ? Color.Yellow : Color.Magenta1);
                        string header;

                        if (checkResp.IsInitiative)
                        {
                            header = "[bold yellow] ORDINE DI INIZIATIVA [/]";
                            table.AddColumn(new TableColumn("[bold yellow]#[/]").Centered());
                            table.AddColumn(new TableColumn("[bold cyan]Personaggio[/]"));
                            table.AddColumn(new TableColumn("[bold white]Iniziativa[/]").Centered());
                            foreach (var e in checkResp.Entries)
                            {
                                table.AddRow(
                                    e.HasRoll ? e.Position.ToString() : "-",
                                    Markup.Escape(e.Handle),
                                    e.HasRoll ? e.Total.ToString() : "[grey]nessun tiro[/]");
                            }
                        }
                        else
                        {
                            header = $"[bold magenta] PROVA DI {Markup.Escape(checkResp.SkillName.ToUpperInvariant())} (TN {checkResp.TargetNumber}) [/]";
                            table.AddColumn(new TableColumn("[bold cyan]Personaggio[/]"));
                            table.AddColumn(new TableColumn("[bold white]Tiro[/]").Centered());
                            table.AddColumn(new TableColumn("[bold white]Esito[/]"));
                            foreach (var e in checkResp.Entries)
                            {
                                string outcome = !e.HasRoll ? "[grey]nessun tiro[/]"
                                    : e.IsFumble ? "[bold red]FALLIMENTO CRITICO[/]"
                                    : e.IsSuccess ? "[bold green]SUCCESSO[/]" : "[red]FALLIMENTO[/]";
                                table.AddRow(Markup.Escape(e.Handle), e.HasRoll ? e.Total.ToString() : "-", outcome);
                            }
                        }

                        var panel = new Panel(table)
                        {
                            Header = new PanelHeader(header),
                            Border = BoxBorder.Rounded
                        };
                        AnsiConsole.Write(panel);
                        if (checkResp.Forced)
                        {
                            AnsiConsole.MarkupLine("[grey]Check chiuso dal master prima che tutti tirassero.[/]");
                        }
                    });
                }
                break;

            case OpCode.S2C_GamesResponse:
                var gamesResp = packet.DeserializePayload<GamesResponsePayload>();
                if (gamesResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
                        table.AddColumn("[bold yellow]Stanze di Gioco Attive[/]");
                        if (gamesResp.Games.Count > 0)
                        {
                            foreach (var g in gamesResp.Games) table.AddRow(Markup.Escape(g));
                        }
                        else
                        {
                            table.AddRow("[grey](Nessuna sessione attiva)[/]");
                        }
                        AnsiConsole.Write(new Panel(table) { Header = new PanelHeader("[bold yellow] SESSIONI ATTIVE [/]"), BorderStyle = new Style(Color.Cyan1) });
                    });
                }
                break;

            case OpCode.S2C_CharactersResponse:
                var charactersResp = packet.DeserializePayload<CharactersResponsePayload>();
                if (charactersResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
                        table.AddColumns("[bold yellow]Handle Personaggio[/]", "[bold cyan]Ruolo Archetipo[/]", "[bold white]Stato[/]");
                        if (charactersResp.Characters.Count > 0)
                        {
                            foreach (var cNode in charactersResp.Characters)
                            {
                                table.AddRow(
                                    Markup.Escape(cNode.Handle),
                                    cNode.Role.ToString(),
                                    Markup.Escape(cNode.Status));
                            }
                        }
                        else
                        {
                            table.AddRow("[grey](Nessun personaggio salvato sul server)[/]", "-", "-");
                        }
                        AnsiConsole.Write(new Panel(table) { Header = new PanelHeader("[bold yellow] SCHEDE PERSONAGGI DISPONIBILI [/]"), BorderStyle = new Style(Color.Yellow) });
                    });
                }
                break;

            case OpCode.S2C_ArchetypesResponse:
                var archetypesResp = packet.DeserializePayload<ArchetypesResponsePayload>();
                if (archetypesResp != null)
                {
                    PrintServerOutput(() =>
                    {
                        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
                        table.AddColumn("[bold yellow]Archetipo[/]");
                        if (archetypesResp.Archetypes.Count > 0)
                        {
                            foreach (var a in archetypesResp.Archetypes) table.AddRow(Markup.Escape(a));
                        }
                        else
                        {
                            table.AddRow("[grey](Nessun archetipo sul server)[/]");
                        }
                        AnsiConsole.Write(new Panel(table) { Header = new PanelHeader("[bold yellow] ARCHETIPI DISPONIBILI (/create <archetipo>) [/]"), BorderStyle = new Style(Color.Yellow) });
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

    /// <summary>
    /// Argomenti di /throw: X Y [Z] [arma]. Lo Z è opzionale: se il terzo argomento non è un numero è già
    /// il nome dell'arma; se manca, il server usa la Z corrente del personaggio. Restituisce null se i
    /// parametri non sono validi.
    /// </summary>
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

    /// <summary>
    /// Costruisce la richiesta /check dai token del comando (personaggi separati da spazi e/o virgole).
    /// Restituisce null se i parametri non sono validi.
    /// </summary>
    private static CheckRequestPayload? BuildCheckRequest(string[] parts)
    {
        if (parts.Length < 2) return null;

        string verb = parts[1].ToLowerInvariant();
        string[] SplitHandles(int from) => string.Join(' ', parts.Skip(from))
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return verb switch
        {
            "skill" when parts.Length >= 4 && int.TryParse(parts[3], out int tn) => new CheckRequestPayload { Action = "skill", SkillName = parts[2], TargetNumber = tn, Handles = [.. SplitHandles(4)] },
            "init" or "initiative" or "iniziativa" => new CheckRequestPayload { Action = "init", Handles = [.. SplitHandles(2)] },
            "status" or "close" or "cancel" => new CheckRequestPayload { Action = verb },
            _ => null,
        };
    }

    /// <summary>
    /// Costruisce il pacchetto /as: lo stesso payload del comando normale, avvolto con l'handle dell'NPC.
    /// Restituisce null se i parametri non sono validi.
    /// </summary>
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

            case "move" when args.Length >= 2
                             && int.TryParse(args[0], out int x)
                             && int.TryParse(args[1], out int y):
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

    /// <summary>
    /// Stampa il menu di aiuto in stile BBS anni '90.
    /// </summary>
    private static void PrintHelpMenu()
    {
        lock (_consoleLock)
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
            table.AddColumns(
                new TableColumn("[bold yellow]Comando[/]"),
                new TableColumn("[bold cyan]Parametri[/]"),
                new TableColumn("[bold green]Descrizione[/]")
            );

            foreach (var entry in HelpEntries.Values
                         .OrderBy(x => x.Command, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    $"[bold white]{Markup.Escape(entry.Command)}[/]",
                    $"[grey]{Markup.Escape(entry.Parameters)}[/]",
                    Markup.Escape(entry.Description)
                );
            }

            var panel = new Panel(table)
            {
                Header = new PanelHeader("[bold yellow]  COMANDI NIGHT CITY BBS v2.0 [/]"),
                Border = BoxBorder.Double,
                BorderStyle = new Style(Color.Cyan1)
            };

            AnsiConsole.Write(panel);
        }
    }
    private static void PrintHelpEntry(HelpEntry entry)
    {
        var panel = new Panel(
            new Rows(
                new Markup($"[bold yellow]Comando:[/] [cyan]{Markup.Escape(entry.Command)}[/]"),
                new Markup($"[bold yellow]Parametri:[/] [green]{Markup.Escape(entry.Parameters)}[/]"),
                new Markup($"[bold yellow]Descrizione:[/] [white]{Markup.Escape(entry.Description)}[/]")
            ))
        {
            Header = new PanelHeader("[bold cyan] HELP [/]")
        };

        AnsiConsole.Write(panel);
    }
    private static string FormatWeaponAmmo(Weapon w)
    {
        if (w != null && w.MagazineCapacity > 0 && !string.IsNullOrEmpty(w.Caliber))
        {
            return $" | Caricatore: {w.CurrentAmmo}/{w.MagazineCapacity} ({w.Caliber})";
        }
        return "";
    }

    /// <summary>
    /// Stampa lo stato completo del personaggio con layout Cyberpunk HUD via Spectre.Console.
    /// </summary>
    private static void PrintCharacterStatus(Character c)
    {
        var mainGrid = new Grid();
        mainGrid.AddColumn();

        // 1. Header con Handle, Ruolo, Posizione
        mainGrid.AddRow(new Markup($"[bold yellow]PERSONAGGIO:[/] [bold cyan]{Markup.Escape(c.Handle)}[/]  [grey]|[/]  [bold yellow]RUOLO:[/] [white]{c.Role}[/]  [grey]|[/]  [bold yellow]POS:[/] [green]({c.Position.X:F1}, {c.Position.Y:F1}, {c.Position.Z:F1})[/]"));

        // 2. Barra Salute
        int currentHp = Math.Max(0, c.Health.CurrentHealth);
        int maxHp = Math.Max(1, c.Health.MaxHealth);
        double hpPercent = (double)currentHp / maxHp;
        string hpColor = hpPercent > 0.6 ? "green" : (hpPercent > 0.25 ? "yellow" : "red");

        int barTotal = 20;
        int filled = Math.Clamp((int)Math.Round(hpPercent * barTotal), 0, barTotal);
        string bar = new string('█', filled) + new string('░', barTotal - filled);

        mainGrid.AddRow(new Markup($"[bold yellow]SALUTE:[/] [{hpColor}]{bar}[/] [{hpColor}]{currentHp}/{maxHp}[/] [grey]({Markup.Escape(c.Health.GetWoundStatusDescription())})[/]"));

        // 3. Tabella Statistiche
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
            c.Stats.EMP.ToString()
        );
        mainGrid.AddRow(statsTable);

        // 4. Armatura
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

        // 5. Arma Impugnata
        if (c.EquippedWeapon != null)
        {
            string ammoInfo = FormatWeaponAmmo(c.EquippedWeapon);
            mainGrid.AddRow(new Markup($"[bold yellow]ARMA IN PUGNO:[/] [bold cyan]{Markup.Escape(c.EquippedWeapon.Name)}[/] [grey]({c.EquippedWeapon.Type})[/] [grey]|[/] Danno: [bold red]{c.EquippedWeapon.DamageDice}[/] [grey]|[/] Gittata: [green]{c.EquippedWeapon.OptimalRangeMeters}m[/]{Markup.Escape(ammoInfo)}"));
        }
        else
        {
            mainGrid.AddRow(new Markup("[bold yellow]ARMA IN PUGNO:[/] [grey]Nessuna[/]"));
        }

        // 6. Inventario Armi e Munizioni
        if (c.Weapons != null && c.Weapons.Count > 0)
        {
            var wepLines = c.Weapons.Select(w =>
            {
                string equipped = (c.EquippedWeapon != null && c.EquippedWeapon.Id == w.Id) ? " [bold green](EQUIPAGGIATA)[/]" : "";
                return $"[white]{Markup.Escape(w.Name)}[/] [grey]({w.DamageDice})[/]{equipped}";
            });
            mainGrid.AddRow(new Markup("[bold yellow] ARMI:[/] " + string.Join("  ", wepLines)));
        }

        if (c.Ammos != null && c.Ammos.Count > 0)
        {
            var ammoLines = c.Ammos.Select(a =>
            {
                string effect = a.Effect == AmmoEffect.ArmorPiercing ? " [red](AP)[/]" : (a.Effect == AmmoEffect.Expanding ? " [yellow](EX)[/]" : "");
                return $"[white]{Markup.Escape(a.Name)}[/]{effect}: [green]{a.Quantity}[/]";
            });
            mainGrid.AddRow(new Markup("[bold yellow] MUNIZIONI:[/] " + string.Join("  ", ammoLines)));
        }

        // 7. Cyberware
        if (c.Cyberware != null && c.Cyberware.Count > 0)
        {
            var cwLines = c.Cyberware.Select(cw =>
            {
                string bonus = cw.Bonus != null && cw.Bonus.Value != 0 ? $" [green][[{cw.Bonus.Value:+0;-0} {cw.Bonus.ApplyTo}]][/]" : "";
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

    /// <summary>
    /// /connect [host] [porta]: dopo una caduta della connessione ritenta il collegamento (senza parametri
    /// riusa l'ultimo server). Non fa login, /link o /join automatici: sul nuovo collegamento si riparte dal
    /// login con lo username che l'utente inserisce.
    /// </summary>
    private async Task ReconnectAsync(string[] args)
    {
        if (_isRunning)
        {
            AnsiConsole.MarkupLine("[bold red][[!]]: Sei già connesso al server.[/]");
            return;
        }

        if (!ConnectRules.TryParse(args, _lastHost, _lastPort, out string host, out int port, out string error))
        {
            AnsiConsole.MarkupLine($"[bold red][[!]]:[/] [white]{Markup.Escape(error)}[/]");
            return;
        }

        try { _tcpClient?.Close(); } catch { }

        AnsiConsole.MarkupLine($"[grey]Connessione a:[/] [bold green]{Markup.Escape(host)}:{port}[/]");
        if (!await ConnectAsync(host, port)) return;

        _player = null;
        _commandLoopActive = false;   // durante il login il prompt dei comandi non va ripristinato
        _ = Task.Run(() => ListenToServerAsync());

        if (!await LoginAsync()) return;

        _commandLoopActive = true;
    }

    /// <summary>
    /// Chiede lo username e attende l'esito del login. False se la connessione cade prima del completamento.
    /// </summary>
    private async Task<bool> LoginAsync()
    {
        if (_writer == null) return false;

        bool loggedIn = false;

        while (!loggedIn)
        {
            AnsiConsole.Markup("[bold yellow]NETRUNNER LOGIN[/] [grey]>> Inserisci username:[/] ");
            string? player_name = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(player_name))
            {
                AnsiConsole.MarkupLine("[bold red][[!]]: Inserisci uno username.[/]");
                continue;
            }

            // Digitazione mascherata: la password non compare a schermo né resta nella cronologia.
            string password = AnsiConsole.Prompt(new TextPrompt<string>("[bold yellow]Password:[/] ").Secret().AllowEmpty());

            _loginTcs = new TaskCompletionSource<LoginResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);

            var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
            {
                PlayerName = player_name.Trim(),
                Password = password
            });

            await _writer.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

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

        return loggedIn;
    }

    /// <summary>
    /// Loop principale di I/O: gestisce il login, i comandi dell'utente e l'ascolto dei messaggi del server.
    /// </summary>
    public async Task RunLoopAsync()
    {
        if (_reader == null || _writer == null) return;

        _ = Task.Run(() => ListenToServerAsync());

        if (!await LoginAsync()) return;

        PrintHelpMenu();

        _commandLoopActive = true;

        while (!_exitRequested)
        {
            ShowPromptIfIdle();

            string? input = Console.ReadLine();
            _promptPending = false;

            if (_exitRequested) break;

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            string trimmedInput = input.Trim();

            // Senza connessione non si può fare altro che /connect, /help e /exit
            if (!_isRunning
                && !trimmedInput.Equals("/exit", StringComparison.OrdinalIgnoreCase)
                && !trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase)
                && !trimmedInput.Equals("/connect", StringComparison.OrdinalIgnoreCase)
                && !trimmedInput.StartsWith("/connect ", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[bold red][[!]]: Non sei connesso al server. Usa[/] [yellow]/connect [[host]] [[porta]][/] [bold red]oppure[/] [yellow]/exit[/][bold red].[/]");
                continue;
            }

            if (trimmedInput.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                _lastPingSentUtc = DateTime.UtcNow;
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Ping, new { })));
                continue;
            }

            if (trimmedInput.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? gameFilter = parts.Length > 1 ? parts[1].Trim() : null;
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetPlayers, new PlayersRequestPayload { GameName = gameFilter })));
                continue;
            }

            if (trimmedInput.StartsWith("/create", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso corretto:[/] [yellow]/create <archetipo> <character.handle>[/]");
                    AnsiConsole.MarkupLine("    [grey]Esempio:[/] /create Solo Razor");
                    AnsiConsole.MarkupLine("    [grey]Archetipi:[/] Solo, Netrunner, Nomad, Rocker, Fixer, Corporate, Techie, TechMed, Cop, Media");
                    continue;
                }

                string archetype = parts[1].Trim();
                string characterHandle = parts[2].Trim();

                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_CreateCharacter, new CreateCharacterRequestPayload
                {
                    Archetype = archetype,
                    CharacterHandle = characterHandle
                })));
                continue;
            }

            if (trimmedInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                _exitRequested = true;
                _isRunning = false;
                break;
            }

            if (trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelpMenu();
                continue;
            }

            if (trimmedInput.StartsWith("/help ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    PrintHelpMenu();
                    continue;
                }

                string requested = HelpCatalog.NormalizeCommand(parts[1]);

                if (!HelpEntries.TryGetValue(requested, out var entry))
                {
                    AnsiConsole.MarkupLine($"[bold red][[!]]: Comando non trovato:[/] [yellow]{Markup.Escape(requested)}[/]. Digita [cyan]/help[/] per la lista.");
                    continue;
                }

                PrintHelpEntry(entry);
                continue;
            }

            if (trimmedInput.Equals("/connect", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/connect ", StringComparison.OrdinalIgnoreCase))
            {
                await ReconnectAsync([.. trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)]);
                continue;
            }

            if (_player == null) { continue; }

            if (trimmedInput.Equals("/npc", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/npc ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string action = parts.Length >= 2 ? parts[1].ToLowerInvariant() : string.Empty;

                bool valid = action switch
                {
                    "add" or "remove" or "status" => parts.Length >= 3,
                    "list" => true,
                    _ => false
                };

                if (!valid)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/npc add <handle>[/] | [yellow]/npc list[/] | [yellow]/npc remove <handle>[/] | [yellow]/npc status <handle>[/]");
                    continue;
                }

                var npcPacket = NetworkPacket.Create(OpCode.C2S_Npc, new NpcRequestPayload
                {
                    Action = action,
                    Handle = parts.Length >= 3 ? parts[2] : string.Empty
                });
                await _writer.WriteLineAsync(JsonSerializer.Serialize(npcPacket));
                continue;
            }

            if (trimmedInput.Equals("/as", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/as ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var asPacket = parts.Length >= 3 ? BuildAsPacket(parts[1], parts[2].ToLowerInvariant(), parts[3..]) : null;

                if (asPacket == null)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/as <npc> attack <bersaglio> [[arma|skill]][/] | [yellow]/as <npc> skill <abilità> [[TN]][/] | [yellow]/as <npc> move <X> <Y> [[Z]][/] | [yellow]/as <npc> init[/] | [yellow]/as <npc> reload [[munizioni]] [[arma]][/] | [yellow]/as <npc> throw <X> <Y> [[Z]] [[arma]][/] | [yellow]/as <npc> heal <personaggio>[/]");
                    continue;
                }

                await _writer.WriteLineAsync(JsonSerializer.Serialize(asPacket));
                continue;
            }

            if (trimmedInput.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_player.Handle))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
                    continue;
                }

                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetStatus, new { })));
                continue;
            }

            if (trimmedInput.Equals("/skills", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetSkills, new { })));
                continue;
            }

            if (trimmedInput.Equals("/games", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetGames, new { })));
                continue;
            }

            if (trimmedInput.Equals("/archetypes", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetArchetypes, new { })));
                continue;
            }

            if (trimmedInput.Equals("/characters", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetCharacters, new { })));
                continue;
            }

            if (trimmedInput.Equals("/check", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/check ", StringComparison.OrdinalIgnoreCase))
            {
                var checkReq = BuildCheckRequest(trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (checkReq == null)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/check skill <abilità> <TN> [[personaggi]][/] | [yellow]/check init [[personaggi]][/] | [yellow]/check status[/] | [yellow]/check close[/] | [yellow]/check cancel[/]");
                }
                else
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Check, checkReq)));
                }
                continue;
            }

            if (trimmedInput.Equals("/init", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(
                    JsonSerializer.Serialize(
                        NetworkPacket.Create(OpCode.C2S_Init, new InitRequest { PlayerName = _player.Username })));
                continue;
            }

            if (trimmedInput.Equals("/throw", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/throw ", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_player.Handle))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
                    continue;
                }

                var throwReq = BuildThrowRequest([.. trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)]);
                if (throwReq == null)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /throw <X> <Y> [[Z]] [[arma]][/]");
                }
                else
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Throw, throwReq)));
                }
                continue;
            }

            if (trimmedInput.StartsWith("/move", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_player.Handle))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
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
                        Position = new Position3D { X = x, Y = y, Z = z },
                        ZProvided = true
                    });
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                }
                else if (parts.Length == 3 &&
                    int.TryParse(parts[1], out int x1) &&
                    int.TryParse(parts[2], out int y1))
                {
                    // Senza Z si resta sul piano corrente: la Z la decide il server, che conosce la
                    // posizione reale del personaggio (quella del client può essere vecchia).
                    var movePacket = NetworkPacket.Create(OpCode.C2S_Move, new MoveRequestPayload
                    {
                        Position = new Position3D { X = x1, Y = y1, Z = 0 },
                        ZProvided = false
                    });
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                }
                else
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /move <X> <Y> [[Z]][/]");
                }
                continue;
            }

            if (trimmedInput.Equals("/teleport", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/teleport ", StringComparison.OrdinalIgnoreCase))
            {
                string[] tp = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var targets = TeleportRules.ParseTargets(tp.Skip(4));

                if (tp.Length >= 5 &&
                    int.TryParse(tp[1], out int tx) &&
                    int.TryParse(tp[2], out int ty) &&
                    int.TryParse(tp[3], out int tz) &&
                    targets.Count > 0)
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Teleport, new TeleportRequestPayload
                    {
                        Position = new Position3D { X = tx, Y = ty, Z = tz },
                        Targets = targets
                    })));
                }
                else
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/teleport <X> <Y> <Z> <personaggio> [[altri personaggi]][/] (solo master)");
                }
                continue;
            }

            if (trimmedInput.Equals("/bonus", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/bonus ", StringComparison.OrdinalIgnoreCase))
            {
                // Lo interpreta il server (BonusRules): qui si inviano gli argomenti così come sono.
                var bonusArgs = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Bonus, new BonusRequestPayload { Args = bonusArgs })));
                continue;
            }

            if (trimmedInput.Equals("/whoami", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_WhoAmI, new { })));
                continue;
            }

            if (trimmedInput.Equals("/game", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.Equals("/gamestatus", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GameStatus, new { })));
                continue;
            }

            if (trimmedInput.Equals("/next", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.Equals("/endturn", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Next, new { })));
                continue;
            }

            if (trimmedInput.Equals("/combat", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/combat ", StringComparison.OrdinalIgnoreCase))
            {
                string[] combatParts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string combatAction = combatParts.Length >= 2 ? combatParts[1].ToLowerInvariant() : "status";
                if (combatAction is "start" or "end" or "status")
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(
                        NetworkPacket.Create(OpCode.C2S_Combat, new CombatRequestPayload { Action = combatAction })));
                }
                else
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/combat start[/] | [yellow]/combat end[/] | [yellow]/combat status[/]");
                }
                continue;
            }

            if (trimmedInput.Equals("/reload", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/reload ", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_player.Handle))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
                    continue;
                }

                string[] reloadParts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var reloadPacket = NetworkPacket.Create(OpCode.C2S_Reload, new ReloadRequestPayload
                {
                    AmmoKey = reloadParts.Length >= 2 ? reloadParts[1] : null,
                    WeaponKey = reloadParts.Length >= 3 ? reloadParts[2] : null
                });
                await _writer.WriteLineAsync(JsonSerializer.Serialize(reloadPacket));
                continue;
            }

            if (trimmedInput.StartsWith("/attack", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_player.Handle))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
                    continue;
                }

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
                    AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /attack <Bersaglio> [[Arma|Abilità]][/]");
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
                    AnsiConsole.MarkupLine("[bold red][[!]]: Devi collegarti a un personaggio prima con /link <character>[/]");
                    continue;
                }

                string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string skillName = parts[1];
                    int difficulty = 15;

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
                    AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /skill <NomeAbilità> [[TN]][/]");
                }
                continue;
            }

            if (trimmedInput.StartsWith("/chat", StringComparison.OrdinalIgnoreCase))
            {
                string raw = input.Trim();
                string rest = raw.Length > 5 ? raw[5..].Trim() : string.Empty;

                if (string.IsNullOrWhiteSpace(rest))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso: /chat \"<messaggio>\" [[userid]][/]");
                    continue;
                }

                int firstQuote = rest.IndexOf('"');
                if (firstQuote < 0)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Il messaggio deve essere racchiuso tra virgolette. Es.: /chat \"Salve a tutti\"[/]");
                    continue;
                }
                int lastQuote = rest.LastIndexOf('"');
                if (lastQuote == firstQuote)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Virgolette non chiuse. Es.: /chat \"Salve a tutti\"[/]");
                    continue;
                }

                string message = rest[(firstQuote + 1)..lastQuote].Trim();
                string after = rest[(lastQuote + 1)..].Trim();

                if (string.IsNullOrWhiteSpace(message))
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Messaggio vuoto. Uso: /chat \"<messaggio>\" [[userid]][/]");
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

            if (trimmedInput.Equals("/leave", StringComparison.OrdinalIgnoreCase))
            {
                var leavePacket = NetworkPacket.Create(OpCode.C2S_Leave, new { });
                await _writer.WriteLineAsync(JsonSerializer.Serialize(leavePacket));
                continue;
            }

            if (trimmedInput.StartsWith("/join", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string roleInput = parts.Length >= 3 ? parts[2] : "player";

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

            if (trimmedInput.Equals("/unlink", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_Unlink, new { })));
                continue;
            }

            if (trimmedInput.Equals("/heal", StringComparison.OrdinalIgnoreCase)
                || trimmedInput.StartsWith("/heal ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Uso:[/] [yellow]/heal <personaggio>[/] (master: [yellow]/heal <personaggio> [[caselle]][/])");
                    continue;
                }

                int? healAmount = null;
                if (parts.Length >= 3)
                {
                    if (!int.TryParse(parts[2], out int amountValue) || amountValue <= 0)
                    {
                        AnsiConsole.MarkupLine("[bold red][[!]]: Le caselle devono essere un numero intero positivo.[/]");
                        continue;
                    }
                    healAmount = amountValue;
                }

                await _writer.WriteLineAsync(JsonSerializer.Serialize(
                    NetworkPacket.Create(OpCode.C2S_Heal, new HealRequestPayload { TargetHandle = parts[1], Amount = healAmount })));
                continue;
            }

            if (trimmedInput.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2)
                {
                    var characterHandle = parts[1].Trim();

                    var linkPacket = NetworkPacket.Create(OpCode.C2S_Link, new LinkRequestPayload
                    {
                        PlayerHandle = _player.Username,
                        CharacterHandle = characterHandle
                    });

                    await _writer.WriteLineAsync(JsonSerializer.Serialize(linkPacket));
                }
                else
                {
                    AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /link <NomeCharacter>[/]");
                }
                continue;
            }

            AnsiConsole.MarkupLine($"[bold red][[!]]: Comando non riconosciuto:[/] [yellow]'{Markup.Escape(trimmedInput)}'[/]. Digita [cyan]/help[/] per la lista.");
        }

        _tcpClient?.Close();
    }
}
