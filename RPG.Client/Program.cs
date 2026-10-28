using CommandLine;
using NAudio.Wave;
using Spectre.Console;

namespace RPG.Client;

internal class Program
{
    static Task PlayModemAsync(string wavPath)
    {
        return Task.Run(() =>
        {
            try
            {
                using var reader = new AudioFileReader(wavPath);
                using var output = new WaveOutEvent();
                output.Init(reader);
                output.Play();

                while (output.PlaybackState == PlaybackState.Playing)
                {
                    Thread.Sleep(50);
                }
            }
            catch
            {
                // fallback silenzioso: nessun audio disponibile
            }
        });
    }
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

        var fontFiles = Directory.GetFiles("fonts", "*.flf");

        _ = PlayModemAsync("audio/modem-handshake.mp3");

        if (fontFiles.Length == 0)
        {
            AnsiConsole.Write(
                new FigletText("NIGHT CITY")
                    .Centered()
                    .Color(Color.Cyan));
        }
        else {
            var font = FigletFont.Load(fontFiles[Random.Shared.Next(fontFiles.Length)]);
            AnsiConsole.Write(
                    new FigletText(font, "NIGHT CITY")
                    .Centered().
                    Color(Color.Cyan));
        }

        AnsiConsole.Write(new Rule("[bold cyan]NETRUNNER BBS // CYBERPUNK 2020[/]").Centered());
        AnsiConsole.MarkupLine($"[grey]Connessione a:[/] [bold green]{host}:{port}[/]\n");

        var client = new RpgTcpClient();
        bool connected = await client.ConnectAsync(host, port);

        if (!connected)
        {
            AnsiConsole.MarkupLine("[bold red][[!]]: Impossibile connettersi al server. Premere un tasto per uscire.[/]");
            Console.ReadKey();
            return 1;
        }

        // Loop di I/O comandi e ascolto di rete
        await client.RunLoopAsync();

        return 0;
    }
}
