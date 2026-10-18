using CommandLine;

namespace RPG.Server;

public class ServerOptions
{
    public const string DefaultListenIpAddress = "0.0.0.0";
    public const int DefaultPort = 5000;

    [Option('h', "host", Default = DefaultListenIpAddress, HelpText = "Indirizzo IP di ascolto.")]
    public string Host { get; set; } = DefaultListenIpAddress;

    [Option('p', "port", Default = DefaultPort, HelpText = "Porta TCP di ascolto.")]
    public int Port { get; set; } = DefaultPort;
}
