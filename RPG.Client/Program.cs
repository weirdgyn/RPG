using CommandLine;
using Spectre.Console;

namespace RPG.Client;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var parserResult = Parser.Default.ParseArguments<ClientOptions>(args);

        // Se l'utente ha passato --help, -? o argomenti errati, CommandLineParser 
        // mostra la guida ed esce restituendo NotParsed.
        if (parserResult.Tag == ParserResultType.NotParsed)
        {
            return 1;
        }

        var options = parserResult.Value;

        // Riga di comando > client.settings.json > default (127.0.0.1:5000)
        var (host, port) = ClientSettings.Resolve(options);

        // ==========================================
        // RETRO CYBERPUNK BBS BANNER
        // ==========================================
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new FigletText("NIGHT CITY")
                .Centered()
                .Color(Color.Yellow));

        AnsiConsole.Write(new Rule("[bold cyan]NETRUNNER BBS // CYBERPUNK 2020[/]").Centered());
        AnsiConsole.MarkupLine($"[grey]🔌 Connessione a:[/] [bold green]{host}:{port}[/]\n");

        var client = new RpgTcpClient();
        bool connected = await client.ConnectAsync(host, port);

        if (!connected)
        {
            AnsiConsole.MarkupLine("[bold red][!] Impossibile connettersi al server. Premere un tasto per uscire.[/]");
            Console.ReadKey();
            return 1;
        }

        // Loop di I/O comandi e ascolto di rete
        await client.RunLoopAsync();

        return 0;
    }
}
