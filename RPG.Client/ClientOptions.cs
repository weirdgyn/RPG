using CommandLine;
using System.Text.Json;

namespace RPG.Client;

public class ClientOptions
{
    private const string DefaultServerIp = "127.0.0.1";
    private const int DefaultPort = 5000;

    public ClientOptions()
    {
        LoadFromConfigFile();
    }

    [Option('h', "host", Default = DefaultServerIp, HelpText = "Indirizzo IP del server a cui connettersi.")]
    public string Host { get; set; } = DefaultServerIp;
    [Option('p', "port", Default = DefaultPort, HelpText = "Porta TCP del server.")]
    public int Port { get; set; } = DefaultPort;

    private void LoadFromConfigFile()
    {
        string configPath = "client.settings.json";
        if (File.Exists(configPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(configPath));
                if (config != null)
                {
                    // Only override if the config has a valid value
                    if (!string.IsNullOrWhiteSpace(config.Host))
                        Host = config.Host;

                    if (config.Port > 0)
                        Port = config.Port;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Impossibile leggere {configPath}: {ex.Message}. Verranno usate le impostazioni di default (localhost).");
            }
        }
    }
}

// Helper record/class for deserialization
public class ClientConfig
{
    public string? Host { get; set; }
    public int Port { get; set; }
}