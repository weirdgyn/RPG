using RPG.Common.Model;
using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Client
{
    public class RpgTcpClient
    {
        private TcpClient? _tcpClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isRunning;
        private PlayerState? _lastKnownCharacter;
        private static void PrintPrompt()
        {
            Console.Write("\n> ");
        }
        private static void PrintHelpMenu()
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("🎮 COMANDI DISPONIBILI - NIGHT CITY RPG");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("  /status                               - Richiede la scheda personaggio completa");
            Console.WriteLine("  /skills                               - Visualizza la lista delle tue abilità");
            Console.WriteLine("  /move <X> <Y> <Z>                     - Spostati nello spazio 3D");
            Console.WriteLine("  /attack <Bersaglio>                   - Attacca con l'arma equipaggiata");
            Console.WriteLine("  /attack <Bersaglio> <Arma/Abilità>    - Attacca specificando arma o abilità (es. /attack Jackie ArtiMarziali)");
            Console.WriteLine("  /roll <Espressione> [Motivazione]     - Effettua un tiro di dadi (es. /roll 1d10+4 Iniziativa)");
            Console.WriteLine("  /skill <NomeAbilità> [TN]             - Effettua un test di abilità (es. /skill Percezione 15)");
            Console.WriteLine("  /help                                 - Mostra questo menu");
            Console.WriteLine("  /exit                                 - Disconnettiti ed esci");
            Console.WriteLine("========================================\n");
        }
        private static void PrintSkillList(PlayerState character)
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
        public async Task RunLoopAsync()
        {
            if (_reader == null || _writer == null) return;

            // 1. Avvia l'ascolto dei pacchetti dal server in background
            _ = Task.Run(() => ListenToServerAsync());

            // 2. Login interattivo
            Console.WriteLine("\n========================================");
            Console.Write("Inserisci il tuo Handle (nome personaggio): ");
            string? handle = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(handle))
            {
                handle = $"Punk_{Guid.NewGuid().ToString()[..4]}";
            }

            var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
            {
                Username = handle.Trim()
            });
            await _writer.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

            // 3. Guida comandi
            PrintHelpMenu();
            PrintPrompt();

            // 4. Loop principale di input console
            while (_isRunning)
            {
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) { PrintPrompt(); continue; }

                string trimmedInput = input.Trim();

                if (trimmedInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    _isRunning = false;
                    break;
                }

                if (trimmedInput.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelpMenu();
                    PrintPrompt();
                    continue;
                }

                // --- COMANDO: /status (Richiede e mostra la scheda completa) ---
                if (trimmedInput.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    // Invia al server la richiesta esplicita di aggiornamento stato
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetStatus, new { })));
                    continue;
                }

                // --- COMANDO: /skills (PLURALE: Mostra SOLTANTO la lista abilità) ---
                if (trimmedInput.Equals("/skills", StringComparison.OrdinalIgnoreCase))
                {
                    if (_lastKnownCharacter?.Skills != null && _lastKnownCharacter.Skills.Count > 0)
                    {
                        PrintSkillList(_lastKnownCharacter);
                        PrintPrompt();
                    }
                    else
                    {
                        Console.WriteLine("[*] Scheda non presente in memoria. Richiesta al server in corso...");
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(NetworkPacket.Create(OpCode.C2S_GetStatus, new { })));
                    }
                    continue;
                }

                // --- COMANDO: /move X Y Z ---
                if (trimmedInput.StartsWith("/move", StringComparison.OrdinalIgnoreCase))
                {
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
                    else
                    {
                        Console.WriteLine("[!] Sintassi non valida. Usa: /move <X> <Y> <Z> (es. /move 10 0 5)");
                    }
                    PrintPrompt();
                    continue;
                }

                // --- COMANDO: /attack <Target> [WeaponOrSkill] ---
                if (trimmedInput.StartsWith("/attack", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                    PrintPrompt();
                    continue;
                }

                // --- COMANDO: /roll o /rool ---
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
                    PrintPrompt();
                    continue;
                }

                // --- COMANDO: /skill (SINGOLARE: Test di Abilità, es. /skill Percezione 15) ---
                if (trimmedInput.StartsWith("/skill", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                        PrintPrompt();
                    }
                    continue;
                }

                Console.WriteLine($"[!] Comando non riconosciuto: '{trimmedInput}'. Digita /help per la lista comandi.");
                PrintPrompt();
            }

            _tcpClient?.Close();
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
                    Console.WriteLine("\n[!] Connessione al server persa.");
                    _isRunning = false;
                }
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
                        Console.WriteLine($"\n[SERVER] {loginResp.Message}");
                    }
                    break;

                case OpCode.S2C_PlayerStateUpdate:
                    var statePayload = packet.DeserializePayload<PlayerStatePayload>();
                    if (statePayload?.Character != null)
                    {
                        // Salva lo stato in memoria locale per i comandi /skills e consultazione rapida
                        
                        var c = statePayload.Character;
                        Console.WriteLine("\n========================================");
                        Console.WriteLine($"📋 SCHEDA PERSONAGGIO: {c.Handle}");
                        Console.WriteLine("----------------------------------------");
                        Console.WriteLine($" Posizione 3D -> X:{c.Position.X:F1} | Y:{c.Position.Y:F1} | Z:{c.Position.Z:F1}");
                        Console.WriteLine($" Stats        -> INT:{c.Stats.INT} | REF:{c.Stats.REF} | TECH:{c.Stats.TECH} | COOL:{c.Stats.COOL}");
                        Console.WriteLine($"                 ATTR:{c.Stats.ATTR} | MA:{c.Stats.MA} | BODY:{c.Stats.BODY} | EMP:{c.Stats.EMP}");
                        Console.WriteLine($" Salute       -> {c.Health.CurrentHealth} / {c.Health.MaxHealth} ({c.Health.GetWoundStatusDescription()})");

                        if (c.EquippedWeapon != null)
                        {
                            Console.WriteLine($" ⚔️ Arma Impugnata : {c.EquippedWeapon.Name} [{c.EquippedWeapon.Type}] (Danno: {c.EquippedWeapon.DamageDice}, Skill: {c.EquippedWeapon.SkillName})");
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
                                Console.WriteLine($"   - {w.Name} [{w.Type}] | Danno: {w.DamageDice} | Skill: {w.SkillName}{marker}");
                            }
                        }
                        else
                        {
                            Console.WriteLine("   (Inventario vuoto)");
                        }
                        Console.WriteLine("========================================");
                    }
                    break;

                case OpCode.S2C_AttackResult:
                    var attackResp = packet.DeserializePayload<AttackResponsePayload>();
                    if (attackResp != null)
                    {
                        Console.WriteLine($"\n⚔️ [COMBAT] {attackResp.AttackerHandle} vs {attackResp.TargetHandle}");
                        Console.WriteLine($"   Tiro Attacco: {attackResp.AttackTotal} vs Difesa TN: {attackResp.DefenseTN}");
                        Console.WriteLine($"   Esito       : {attackResp.Message}");
                        if (attackResp.IsHit)
                        {
                            Console.WriteLine($"   Stato {attackResp.TargetHandle}: {attackResp.TargetRemainingHealth} HP ({attackResp.TargetWoundStatus})");
                        }
                    }
                    break;

                case OpCode.S2C_Notification:
                    var notif = packet.DeserializePayload<JsonElement>();
                    if (notif.TryGetProperty("Message", out var msgProp))
                    {
                        Console.WriteLine($"\n📢 [NOTIFICA] {msgProp.GetString()}");
                    }
                    break;

                case OpCode.S2C_RollResult:
                    var rollResp = packet.DeserializePayload<RollResponsePayload>();
                    if (rollResp != null)
                    {
                        Console.WriteLine($"\n🎲 [TIRO DADI] {rollResp.Message}");
                    }
                    break;

                case OpCode.S2C_SkillCheckResult:
                    var skillResp = packet.DeserializePayload<SkillCheckResponsePayload>();
                    if (skillResp != null)
                    {
                        string icon = skillResp.IsSuccess ? "✅" : "❌";
                        Console.WriteLine($"\n🧠 [PROVA ABILITÀ] {skillResp.CharacterHandle} -> {skillResp.SkillName} ({skillResp.StatName})");
                        Console.WriteLine($"   Tiro: {skillResp.TotalResult} (Stat:{skillResp.StatValue} + Lvl:{skillResp.SkillLevel} + d10:{skillResp.DiceRoll}) vs TN: {skillResp.TargetNumber}");
                        Console.WriteLine($"   Esito: {icon} {skillResp.Message}");
                    }
                    break;

                default:
                    Console.WriteLine($"\n[PACKET] Ricevuto OpCode non gestito: {packet.OpCode}");
                    break;
            }

            // Ristampa il prompt per indicare che la console è in attesa di input
            PrintPrompt();
        }
    }
}