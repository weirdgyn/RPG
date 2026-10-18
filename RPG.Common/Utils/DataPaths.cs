namespace RPG.Common.Utils;

/// <summary>
/// Helper per la risoluzione dei percorsi dei dati (JSON, mappe, ecc.).
/// Cerca prima nella working directory corrente, poi nella directory di output
/// dell'assembly (AppContext.BaseDirectory), così gli stessi percorsi funzionano
/// sia in sviluppo sia quando il server viene eseguito dalla cartella bin.
/// </summary>
public static class DataPaths
{
    public static string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return relativePath;

        string cwdPath = Path.GetFullPath(relativePath);
        if (Directory.Exists(cwdPath) || File.Exists(cwdPath)) return cwdPath;

        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }
}
