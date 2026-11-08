namespace RPG.Server;

/// <summary>
/// Comandi digitati nella console del server mentre gira: gestione degli account.
/// Non esiste nessun modo di creare account dalla rete.
/// </summary>
public static class ServerConsole
{
    public static void Run(AccountStore accounts)
    {
        // Con input rediretto (servizio, test) ReadLine può restituire null subito: in quel caso si esce.
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            string arg = parts.Length > 1 ? parts[1] : string.Empty;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "help":
                        Console.WriteLine("Comandi: users | adduser <nome> | passwd <nome> | deluser <nome> | help");
                        break;
                    case "users":
                        Console.WriteLine(accounts.Count == 0 ? "Nessun account." : $"Account ({accounts.Count}): {string.Join(", ", accounts.Names())}");
                        break;
                    case "adduser":
                        if (AccountStore.ValidateUsername(arg) is { } e1) { Console.WriteLine(e1); break; }
                        if (accounts.Exists(arg)) { Console.WriteLine($"L'account '{arg}' esiste già."); break; }
                        if (ReadNewPassword() is { } p1)
                            Console.WriteLine(accounts.TryCreate(arg, p1, out var err1) ? $"Account '{arg}' creato." : err1);
                        break;
                    case "passwd":
                        if (!accounts.Exists(arg)) { Console.WriteLine($"L'account '{arg}' non esiste."); break; }
                        if (ReadNewPassword() is { } p2)
                            Console.WriteLine(accounts.TrySetPassword(arg, p2, out var err2) ? $"Password di '{arg}' aggiornata." : err2);
                        break;
                    case "deluser":
                        Console.WriteLine(accounts.Remove(arg)
                            ? $"Account '{arg}' rimosso (un'eventuale sessione già aperta resta attiva fino alla disconnessione)."
                            : $"L'account '{arg}' non esiste.");
                        break;
                    default:
                        Console.WriteLine("Comando sconosciuto. Digita 'help'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore: {ex.Message}");
            }
        }
    }

    private static string? ReadNewPassword()
    {
        string first = ReadHidden("Password: ");
        if (AccountStore.ValidatePassword(first) is { } error) { Console.WriteLine(error); return null; }
        string second = ReadHidden("Ripeti la password: ");
        if (first != second) { Console.WriteLine("Le password non coincidono."); return null; }
        return first;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;

        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
