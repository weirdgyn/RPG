using CommandLine;

namespace RPG.Server;

public class ServerOptions
{
    [Option('h', "host", Default = "0.0.0.0", HelpText = "Indirizzo IP di ascolto.")]
    public string Host { get; set; } = "0.0.0.0";

    [Option('p', "port", Default = 5000, HelpText = "Porta TCP di ascolto.")]
    public int Port { get; set; } = 5000;
}
