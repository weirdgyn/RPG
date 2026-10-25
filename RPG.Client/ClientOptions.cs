using CommandLine;

namespace RPG.Client;

/// <summary>
/// Opzioni da riga di comando. Host e porta NON hanno un <c>Default</c> nell'attributo:
/// CommandLineParser assegna i default dopo aver creato l'oggetto, sovrascrivendo qualunque
/// valore impostato altrove (costruttore, file di configurazione). Le opzioni non passate restano
/// quindi null e la priorità (riga di comando, poi client.settings.json, poi default) è risolta
/// da <see cref="ClientSettings.Resolve"/>.
/// </summary>
public class ClientOptions
{
    [Option('h', "host", HelpText = "Indirizzo IP o nome del server. Se omesso: 'Host' di client.settings.json, altrimenti 127.0.0.1.")]
    public string? Host { get; set; }

    [Option('p', "port", HelpText = "Porta TCP del server. Se omessa: 'Port' di client.settings.json, altrimenti 5000.")]
    public int? Port { get; set; }
}
