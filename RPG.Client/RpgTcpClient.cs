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
                PrintServerOutput(() => AnsiConsole.MarkupLine("[bold red][[!]]: Connessione al server persa. Premi Invio per uscire.[/]"));
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

                            grid.AddRow(new Markup($"[bold yellow]{Markup.Escape(attackResp.AttackerHandle)}[/] [red]attacca[/] [bold yellow]{Markup.Escape(attackResp.TargetHandle)}[/]"));
                            grid.AddRow(new Markup($"   Tiro: [cyan]{attackResp.AttackTotal}[/] vs Difesa TN: [cyan]{attackResp.DefenseTN}[/]"));
                            grid.AddRow(new Markup($"   Esito: [white]{Markup.Escape(attackResp.Message)}[/]"));

                            if (attackResp.IsHit)
                            {
                                if (!string.IsNullOrEmpty(attackResp.HitLocation))
                                {
                                    string mult = attackResp.DamageMultiplier > 1 ? $"[bold red](danno x{attackResp.DamageMultiplier})[/]" : "";
                                    grid.AddRow(new Markup($"    Impatto: [bold yellow]{Markup.Escape(attackResp.HitLocation)}[/]{mult}"));
                                }
                                grid.AddRow(new Markup($"   Danno: [bold red]{attackResp.DamageDealt}[/]"));
                                grid.AddRow(new Markup($"   Stato {Markup.Escape(attackResp.TargetHandle)}: [bold yellow]{attackResp.TargetRemainingHealth} HP[/] [grey]({Markup.Escape(attackResp.TargetWoundStatus)})[/]"));
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
                        PrintServerOutput(() => AnsiConsole.MarkupLine($"[bold yellow][[ROLL]][/] [bold cyan]{Markup.Escape(rollResp.RollerHandle)}[/] tira [bold yellow]{Markup.Escape(rollResp.DiceExpression)}[/] -> [bold green]{rollResp.TotalResult}[/] [grey]({Markup.Escape(rollResp.Details)})[/]"));
                    }
                    break;

                case OpCode.S2C_SkillCheckResult:
                    var skillResp = packet.DeserializePayload<SkillCheckResponsePayload>();
                    if (skillResp != null)
                    {
                        PrintServerOutput(() =>
                        {
                            string icon = skillResp.IsSuccess ? "[bold green] SUCCESSO[/]" : "[bold red] FALLIMENTO[/]";
                            var grid = new Grid();
                            grid.AddColumn();
                            grid.AddRow(new Markup($"[bold cyan]{Markup.Escape(skillResp.CharacterHandle)}[/] prova [bold yellow]{Markup.Escape(skillResp.SkillName)}[/] ({skillResp.StatName})"));
                            grid.AddRow(new Markup($"   Calcolo: [cyan]{skillResp.TotalResult}[/] [grey](Stat:{skillResp.StatValue} + Lvl:{skillResp.SkillLevel} + d10:{skillResp.DiceRoll})[/] vs TN: [bold yellow]{skillResp.TargetNumber}[/]"));
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
                            table.AddColumns("[bold yellow]Handle Personaggio[/]", "[bold cyan]Ruolo Archetipo[/]");
                            if (charactersResp.Characters.Count > 0)
                            {
                                foreach (var cNode in charactersResp.Characters) table.AddRow(Markup.Escape(cNode.Handle), cNode.Role.ToString());
                            }
                            else
                            {
                                table.AddRow("[grey](Nessun personaggio salvato sul server)[/]", "-");
                            }
                            AnsiConsole.Write(new Panel(table) { Header = new PanelHeader("[bold yellow] SCHEDE PERSONAGGI DISPONIBILI [/]"), BorderStyle = new Style(Color.Yellow) });
                        });
                    }
                    break;

                default:
                    PrintServerOutput(() => AnsiConsole.MarkupLine($"[grey][[PACKET]][/] Ricevuto OpCode: [yellow]{packet.OpCode}[/]"));
                    break;
            }
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
                    new TableColumn("[bold cyan]Argomenti[/]"),
                    new TableColumn("[bold green]Descrizione[/]")
                );

                table.AddRow("[bold white]/status[/]", "[grey]<character>[/]", "Visualizza la scheda del personaggio collegato (o specifico se Master)");
                table.AddRow("[bold white]/skills[/]", "[grey]-[/]", "Elenco abilità, statistiche associate e pool di tiro");
                table.AddRow("[bold white]/create[/]", "[cyan]<archetipo> <handle>[/]", "Crea un nuovo personaggio da un archetipo (con stat casuali)");
                table.AddRow("[bold white]/link[/]", "[cyan]<character>[/]", "Collega la sessione ad un personaggio esistente");
                table.AddRow("[bold white]/list[/]", "[grey][[<game>]][/]", "Elenca i giocatori online (o filtrati per stanza di gioco)");
                table.AddRow("[bold white]/games[/]", "[grey]-[/]", "Elenca le sessioni di gioco attive");
                table.AddRow("[bold white]/characters[/]", "[grey]-[/]", "Elenca i personaggi salvati sul server");
                table.AddRow("[bold white]/join[/]", "[cyan]<game> [[master|player]][/]", "Entra in un gioco o ne crea uno (solo Master)");
                table.AddRow("[bold white]/attack[/]", "[cyan]<target> [[arma|skill]][/]", "Attacca un bersaglio con arma in pugno o specificata");
                table.AddRow("[bold white]/skill[/]", "[cyan]<nome> [[TN]][/]", "Effettua una prova di abilità d10 (es. /skill Pistola 15)");
                table.AddRow("[bold white]/roll[/]", "[cyan]<espressione> [[motivo]][/]", "Tiro di dadi personalizzato (es. /roll 2d6+3 Danno)");
                table.AddRow("[bold white]/move[/]", "[cyan]<X> <Y> [[Z]][/]", "Muove il personaggio nello spazio 3D");
                table.AddRow("[bold white]/chat[/]", "[cyan]\"<msg>\" [[user]][/]", "Messaggio broadcast o privato");
                table.AddRow("[bold white]/init[/]", "[cyan][[modificatore]][/]", "Tiro di iniziativa / avvio turno di combattimento");
                table.AddRow("[bold white]/ping[/]", "[grey]-[/]", "Verifica latenza di rete con il server (RTT)");
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
        /// Loop principale di I/O: gestisce il login, i comandi dell'utente e l'ascolto dei messaggi del server.
        /// </summary>
        public async Task RunLoopAsync()
        {
            if (_reader == null || _writer == null) return;

            _ = Task.Run(() => ListenToServerAsync());

            bool loggedIn = false;

            while (!loggedIn)
            {
                AnsiConsole.Markup("[bold yellow]NETRUNNER LOGIN[/] [grey]>> Inserisci username:[/] ");
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
                    _isRunning = false;
                    break;
                }

                if (trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelpMenu();
                    continue;
                }

                if (_player == null) { continue; }

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
                            NetworkPacket.Create(OpCode.C2S_Init, new InitRequest
                            {
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
                            Position = new Position3D { X = x, Y = y, Z = z }
                        });
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(movePacket));
                    }
                    else if (parts.Length == 3 &&
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
                        AnsiConsole.MarkupLine("[bold red][[!]]: Sintassi non valida. Usa: /move <X> <Y> [[Z]][/]");
                    }
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
}
