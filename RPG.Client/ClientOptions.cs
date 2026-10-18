using CommandLine;

namespace RPG.Client;

public class ClientOptions
{
    [Option('h', "host", Default = "8.35.196.247", HelpText = "Indirizzo IP del server a cui connettersi.")]
    public string Host { get; set; } = "8.35.196.247";

    [Option('p', "port", Default = 5000, HelpText = "Porta TCP del server.")]
    public int Port { get; set; } = 5000;
}