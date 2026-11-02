namespace RPG.Common;

/// <summary>
/// Regole pure di /teleport (solo master): normalizzazione dell'elenco dei personaggi da spostare.
/// </summary>
public static class TeleportRules
{
    /// <summary>Numero massimo di personaggi spostabili con un solo comando.</summary>
    public const int MaxTargets = 20;

    /// <summary>
    /// Divide gli argomenti su virgole e spazi, scarta i vuoti e i doppioni (senza distinguere maiuscole/minuscole),
    /// mantenendo l'ordine di comparsa.
    /// </summary>
    public static List<string> ParseTargets(IEnumerable<string> args)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            foreach (var part in (arg ?? string.Empty).Split((char[])[',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part)) result.Add(part);
            }
        }

        return result;
    }
}
