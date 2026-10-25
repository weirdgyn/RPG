using RPG.Common.Utils;
using System.Text.Json;

namespace RPG.Client;

/// <summary>Contenuto di client.settings.json: tutti i campi sono facoltativi.</summary>
public class ClientConfig
{
    public string? Host { get; set; }
    public int? Port { get; set; }
}

/// <summary>
/// Risolve a quale server connettersi. Priorità: riga di comando, poi client.settings.json
/// (file locale, escluso dal repository tramite .gitignore), poi i default (127.0.0.1:5000).
/// </summary>
public static class ClientSettings
{
    public const string FileName = "client.settings.json";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 5000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static (string Host, int Port) Resolve(ClientOptions options)
    {
        ClientConfig? config = Load();

        string? cliHost = options.Host;
        string? cfgHost = config?.Host;
        string host = !string.IsNullOrWhiteSpace(cliHost) ? cliHost.Trim()
                    : !string.IsNullOrWhiteSpace(cfgHost) ? cfgHost.Trim()
                    : DefaultHost;

        int? cfgPort = config?.Port;
        int port = options.Port
                   ?? (cfgPort is > 0 and <= 65535 ? cfgPort.Value : DefaultPort);

        return (host, port);
    }

    /// <summary>
    /// Legge il file di configurazione, se esiste. Lo cerca prima nella cartella da cui è stato
    /// lanciato il client e poi accanto all'eseguibile (<see cref="DataPaths.Resolve"/>), così il
    /// risultato non dipende da come si avvia il programma. Un file illeggibile non è bloccante.
    /// </summary>
    private static ClientConfig? Load()
    {
        string path = DataPaths.Resolve(FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), JsonOpts);
            Console.WriteLine($"[CONFIG] Impostazioni lette da: {path}");
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Impossibile leggere '{path}': {ex.Message}. Uso le impostazioni predefinite.");
            return null;
        }
    }
}
