namespace RPG.Server;

/// <summary>
/// Limiti sull'input che arriva dai client (non fidato): lunghezza massima della riga di protocollo e dei
/// testi liberi, più la pulizia dei caratteri di controllo (che permetterebbero righe finte nel log).
/// </summary>
internal static class InputLimits
{
    /// <summary>Lunghezza massima di un pacchetto (una riga JSON). Il pacchetto più grande lato client è una chat.</summary>
    public const int MaxLineLength = 16 * 1024;

    public const int MaxUsernameLength = 24;
    public const int MaxChatLength = 500;
    public const int MaxGameNameLength = 32;
    public const int MaxNameFieldLength = 50;   // handle bersaglio, abilità, arma, filtri
    public const int MaxGames = 50;
    public const int MaxConnections = 100;

    public static bool HasControlChars(string? s) => s != null && s.Any(char.IsControl);

    /// <summary>Toglie i caratteri di controllo, elimina gli spazi ai bordi e tronca a <paramref name="max"/>.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        string cleaned = HasControlChars(s) ? new string(s.Where(c => !char.IsControl(c)).ToArray()) : s;
        cleaned = cleaned.Trim();
        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }
}
