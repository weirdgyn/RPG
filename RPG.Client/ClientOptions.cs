using CommandLine;

namespace RPG.Client;

public class ClientOptions
{
    private const string DefaultServerIp = "136.80.188.203";
    private const int DefaultPort = 5000;

    [Option('h', "host", Default = DefaultServerIp, HelpText = "Indirizzo IP del server a cui connettersi.")]
    public string Host { get; set; } = DefaultServerIp;

    [Option('p', "port", Default = DefaultPort, HelpText = "Porta TCP del server.")]
    public int Port { get; set; } = DefaultPort;
}