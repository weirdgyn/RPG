namespace RPG.Server;

/// <summary>
/// Dove il server tiene i file di stato che genera da solo (certificato TLS, account): una cartella fissa,
/// indipendente da dove viene lanciato il programma. Prima finivano nella cartella corrente, e avviando il server
/// da posti diversi (Visual Studio, dotnet run, cartella bin) se ne creavano copie diverse: un nuovo certificato
/// fa scattare l'avviso di certificato cambiato nei client, un nuovo accounts.json fa sparire gli account.
/// La cartella è quella indicata dalla variabile d'ambiente RPG_SERVER_DATA, altrimenti
/// LocalApplicationData\RPG.Server.
/// </summary>
public static class ServerPaths
{
    public const string DataDirEnvVar = "RPG_SERVER_DATA";

    public static string DataDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(DataDirEnvVar);
        string dir = !string.IsNullOrWhiteSpace(fromEnv)
            ? Path.GetFullPath(fromEnv)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPG.Server");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Percorso del file di stato: quello nella cartella fissa se esiste; altrimenti, se esiste una copia "vecchia"
    /// (<paramref name="legacyPath"/>, cartella corrente o dell'eseguibile) viene copiata nella cartella fissa e da
    /// quel momento si usa quella; altrimenti il file andrà creato nella cartella fissa.
    /// <paramref name="migratedFrom"/> è valorizzato solo se è appena avvenuta la copia.
    /// </summary>
    public static string Resolve(string fileName, string dataDir, string legacyPath, out string? migratedFrom)
    {
        migratedFrom = null;
        string target = Path.Combine(dataDir, fileName);
        if (File.Exists(target)) return target;

        if (File.Exists(legacyPath))
        {
            try
            {
                File.Copy(legacyPath, target, overwrite: false);
                migratedFrom = legacyPath;
                return target;
            }
            catch (IOException)
            {
                // Copia non riuscita: uso il file dov'è, senza perderlo.
                return legacyPath;
            }
        }

        return target;
    }

    public static string Resolve(string fileName)
    {
        string path = Resolve(fileName, DataDirectory(), RPG.Common.Utils.DataPaths.Resolve(fileName), out var migrated);
        if (migrated != null) Console.WriteLine($"[SERVER] {fileName} copiato in {path} (da {migrated}): da ora si usa questa copia.");
        return path;
    }
}
