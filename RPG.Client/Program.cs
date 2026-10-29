using CommandLine;
using NAudio.Wave;
using Spectre.Console;

namespace RPG.Client;

internal class Program
{
    private static async Task ShowBootSequenceAsync()
    {
        using var cts = new CancellationTokenSource();

        AnsiConsole.Clear();

        var cursorTask = Task.Run(async () =>
        {
            var frames = new[] { "_", " ", "_", " " };
            var i = 0;

            Color _color = Console.ForegroundColor;
            Console.ForegroundColor = Color.Green;
            while (!cts.IsCancellationRequested)
            {
                Console.SetCursorPosition(0, 0);
                Console.Write("DIALING..." + frames[i % frames.Length]);
                i++;
                await Task.Delay(180);
            }
            Console.ForegroundColor = _color;
        });

        _ = PlayModemAsync(Path.Combine(AppContext.BaseDirectory, "audio", "modem-handshake.mp3"));

        await Task.Delay(28000);

        cts.Cancel();
        await cursorTask;

        Console.Clear();
        AnsiConsole.Write(BuildRandomBanner("NIGHT CITY"));
        AnsiConsole.Write(new Rule("[bold cyan]NETRUNNER BBS // CYBERPUNK 2020[/]").Centered());
    }
    private static FigletText BuildRandomBanner(string text)
    {
        var fontDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

        if (!Directory.Exists(fontDir))
        {
            return new FigletText(text)
                .Centered()
                .Color(Color.Yellow);
        }

        var fonts = Directory.GetFiles(fontDir, "*.flf");

        if (fonts.Length == 0)
        {
            return new FigletText(text)
                .Centered()
                .Color(Color.Yellow);
        }

        var selected = fonts[Random.Shared.Next(fonts.Length)];
        var font = FigletFont.Load(selected);

        return new FigletText(font, text)
            .Centered()
            .Color(Color.Yellow);
    }
    private static Task PlayModemAsync(string wavPath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(wavPath))
                    return;

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
                // fallback silenzioso
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

        if(options.Intro)
            await ShowBootSequenceAsync();

        // Riga di comando > client.settings.json > default (127.0.0.1:5000)
        var (host, port) = ClientSettings.Resolve(options);  
        
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
