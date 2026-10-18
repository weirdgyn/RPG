using RPG.Common.Protocol;
using RPG.Common.Protocol.Payloads;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Client;

public class RpgTcpClient
{
    private TcpClient? _tcpClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _isRunning;

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

        // 1. Avvia un task in background per ascoltare i pacchetti in arrivo dal server
        _ = Task.Run(() => ListenToServerAsync());

        // 2. Richiedi subito il login interattivo all'utente
        Console.WriteLine("\n========================================");
        Console.Write("Inserisci il tuo Handle (nome personaggio): ");
        string? handle = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(handle))
        {
            handle = $"Punk_{Guid.NewGuid().ToString().Substring(0, 4)}";
        }

        // Invio pacchetto di login al server
        var loginPacket = NetworkPacket.Create(OpCode.C2S_Login, new LoginRequestPayload
        {
            Username = handle.Trim()
        });
        await _writer.WriteLineAsync(JsonSerializer.Serialize(loginPacket));

        Console.WriteLine("\n[*] Digita i comandi di gioco. Esempi:");
        Console.WriteLine("    /attack <Bersaglio>                  (es. /attack Jackie)");
        Console.WriteLine("    /attack <Bersaglio> <Arma/Abilità>   (es. /attack Jackie ArtiMarziali)");
        Console.WriteLine("    /exit                                (per uscire)");
        Console.WriteLine("========================================\n");

        // 3. Loop principale per la lettura dei comandi da console dell'utente
        while (_isRunning)
        {
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                _isRunning = false;
                break;
            }

            // Gestione del comando /attack versatile
            if (input.StartsWith("/attack ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                    continue;
                }
                else
                {
                    Console.WriteLine("[!] Sintassi non valida. Usa: /attack <Bersaglio> [Arma_o_Abilità]");
                    continue;
                }
            }

            // Eventuali altri comandi futuri possono essere aggiunti qui...
            Console.WriteLine($"[!] Comando non riconosciuto: {input}");
        }

        // Chiusura pulita
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
                    Console.WriteLine($"\n[SERVER LOGIN] {loginResp.Message}");
                }
                break;

            case OpCode.S2C_PlayerStateUpdate:
                var statePayload = packet.DeserializePayload<PlayerStatePayload>();
                if (statePayload?.Character != null)
                {
                    var c = statePayload.Character;
                    Console.WriteLine("\n========================================");
                    Console.WriteLine($"📋 AGGIORNAMENTO SCHEDA: {c.Handle}");
                    Console.WriteLine("----------------------------------------");
                    Console.WriteLine($" Stats -> INT:{c.Stats.INT} | REF:{c.Stats.REF} | TECH:{c.Stats.TECH} | COOL:{c.Stats.COOL}");
                    Console.WriteLine($"          ATTR:{c.Stats.ATTR} | MA:{c.Stats.MA} | BODY:{c.Stats.BODY} | EMP:{c.Stats.EMP}");
                    Console.WriteLine($" Salute -> {c.Health.CurrentHealth} / {c.Health.MaxHealth} ({c.Health.GetWoundStatusDescription()})");

                    if (c.EquippedWeapon != null)
                    {
                        Console.WriteLine($" ⚔️ Arma Impugnata: {c.EquippedWeapon.Name} [{c.EquippedWeapon.Type}] (Danno: {c.EquippedWeapon.DamageDice})");
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
                    Console.WriteLine("========================================");
                }
                break;

            case OpCode.S2C_AttackResult:
                var attackResp = packet.DeserializePayload<AttackResponsePayload>();
                if (attackResp != null)
                {
                    Console.WriteLine($"\n⚔️ [COMBAT] {attackResp.AttackerHandle} vs {attackResp.TargetHandle}");
                    Console.WriteLine($"   Tiro: {attackResp.AttackTotal} vs Difesa TN: {attackResp.DefenseTN}");
                    Console.WriteLine($"   Esito: {attackResp.Message}");
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

            default:
                Console.WriteLine($"\n[PACKET] Ricevuto OpCode non gestito: {packet.OpCode}");
                break;
        }
    }
}
