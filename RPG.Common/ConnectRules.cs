namespace RPG.Common;

/// <summary>
/// Regole pure di /connect [host] [porta]: se i parametri mancano si riusano quelli dell'ultima connessione.
/// </summary>
public static class ConnectRules
{
    /// <summary>
    /// Ricava host e porta dagli argomenti di /connect (esclusa la parola "/connect" stessa).
    /// Un solo argomento è l'host; due argomenti sono host e porta. Restituisce false con il messaggio d'errore
    /// se gli argomenti sono troppi o la porta non è un numero 1-65535.
    /// </summary>
    public static bool TryParse(string[] args, string lastHost, int lastPort, out string host, out int port, out string error)
    {
        host = lastHost;
        port = lastPort;
        error = string.Empty;

        if (args.Length > 2)
        {
            error = "Uso: /connect [host] [porta]";
            return false;
        }

        if (args.Length >= 1) host = args[0];

        if (args.Length == 2)
        {
            if (!int.TryParse(args[1], out port) || port < 1 || port > 65535)
            {
                error = "La porta deve essere un numero tra 1 e 65535.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Nessun server noto: usa /connect <host> <porta>";
            return false;
        }

        return true;
    }
}
