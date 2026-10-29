using CommandLine;
using NAudio.Wave;
using Spectre.Console;

namespace RPG.Client;

internal class Program
{
    private static async Task ShowBootSequenceAsync()
    {
        AnsiConsole.Clear();

        Console.CursorVisible = false;

        ConsoleColor previousForeground = Console.ForegroundColor;
        ConsoleColor previousBackground = Console.BackgroundColor;

        Console.BackgroundColor = ConsoleColor.Black;
        Console.ForegroundColor = ConsoleColor.Green;

        try
        {
            string audioDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "audio");

            string dialingAudioPath = Path.Combine(
                audioDirectory,
                "modem-dialing.mp3");

            string handshakeAudioPath = Path.Combine(
                audioDirectory,
                "modem-handshake.mp3");

            /*
             * FASE 1
             *
             * Composizione del numero.
             */
            await RunAudioPhaseAsync(
                prompt: "DIALING",
                audioPath: dialingAudioPath,
                typingDelayMs: 110);

            Console.WriteLine();

            /*
             * FASE 2
             *
             * Handshake e negoziazione del protocollo.
             */
            await RunAudioPhaseAsync(
                prompt: "NEGOTIATING PROTOCOL",
                audioPath: handshakeAudioPath,
                typingDelayMs: 65);

            Console.WriteLine();
            Console.WriteLine();

            /*
             * FASE 3
             *
             * Connessione stabilita.
             */
            Console.ForegroundColor = ConsoleColor.White;

            await TypeTextAsync(
                "CONNECTION ESTABLISHED",
                averageDelayMs: 55);

            Console.ForegroundColor = ConsoleColor.Green;

            await Task.Delay(750);

            /*
             * Transizione verso il client.
             */
            Console.Clear();

            AnsiConsole.Write(
                BuildRandomBanner("NIGHT CITY"));

            AnsiConsole.Write(
                new Rule(
                    "[bold cyan]NETRUNNER BBS // CYBERPUNK 2020[/]")
                    .Centered());
        }
        finally
        {
            Console.ForegroundColor = previousForeground;
            Console.BackgroundColor = previousBackground;
            Console.CursorVisible = true;
        }
    }

    private static async Task RunAudioPhaseAsync(
        string prompt,
        string audioPath,
        int typingDelayMs)
    {
        /*
         * Prima compare il prompt.
         */
        await TypeTextAsync(
            prompt,
            typingDelayMs);

        /*
         * Poi parte il relativo effetto sonoro.
         *
         * Il Task termina quando termina effettivamente
         * la riproduzione del file audio.
         */
        Task audioTask = PlayAudioAsync(audioPath);

        /*
         * Il cursore lampeggia finché il file audio
         * è in riproduzione.
         */
        await BlinkCursorUntilCompletedAsync(audioTask);

        /*
         * Propaga eventuali eccezioni non gestite
         * e garantisce la conclusione del Task.
         */
        await audioTask;
    }

    private static async Task TypeTextAsync(
        string text,
        int averageDelayMs)
    {
        foreach (char character in text)
        {
            Console.Write(character);

            int variation = Math.Max(
                1,
                averageDelayMs / 3);

            int minimumDelay = Math.Max(
                1,
                averageDelayMs - variation);

            int maximumDelay =
                averageDelayMs + variation + 1;

            int delay = Random.Shared.Next(
                minimumDelay,
                maximumDelay);

            await Task.Delay(delay);
        }
    }

    private static async Task BlinkCursorUntilCompletedAsync(
        Task monitoredTask)
    {
        const int visibleDurationMs = 380;
        const int hiddenDurationMs = 320;

        while (!monitoredTask.IsCompleted)
        {
            /*
             * Cursore acceso.
             */
            Console.Write('█');

            await Task.WhenAny(
                monitoredTask,
                Task.Delay(visibleDurationMs));

            /*
             * Cancella soltanto il cursore:
             *
             * \b   torna indietro
             * ' '  sovrascrive il blocco
             * \b   torna nuovamente nella posizione corretta
             */
            Console.Write("\b \b");

            if (monitoredTask.IsCompleted)
                break;

            /*
             * Cursore spento.
             */
            await Task.WhenAny(
                monitoredTask,
                Task.Delay(hiddenDurationMs));
        }
    }

    private static Task PlayAudioAsync(string audioPath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(audioPath))
                {
                    return;
                }

                using var reader =
                    new AudioFileReader(audioPath);

                using var output =
                    new WaveOutEvent();

                output.Init(reader);
                output.Play();

                while (output.PlaybackState ==
                       PlaybackState.Playing)
                {
                    Thread.Sleep(50);
                }
            }
            catch
            {
                /*
                 * Fallback silenzioso.
                 *
                 * Se l'audio non può essere riprodotto,
                 * l'applicazione continua comunque.
                 */
            }
        });
    }

    private static FigletText BuildRandomBanner(
        string text)
    {
        string fontDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fonts");

        if (!Directory.Exists(fontDirectory))
        {
            return new FigletText(text)
                .Centered()
                .Color(Color.Yellow);
        }

        string[] fonts = Directory.GetFiles(
            fontDirectory,
            "*.flf");

        if (fonts.Length == 0)
        {
            return new FigletText(text)
                .Centered()
                .Color(Color.Yellow);
        }

        string selectedFont = fonts[
            Random.Shared.Next(fonts.Length)];

        FigletFont font =
            FigletFont.Load(selectedFont);

        return new FigletText(font, text)
            .Centered()
            .Color(Color.Yellow);
    }

    private static async Task<int> Main(
        string[] args)
    {
        ParserResult<ClientOptions> parserResult =
            Parser.Default.ParseArguments<ClientOptions>(
                args);

        /*
         * Se l'utente ha passato --help, -? oppure
         * argomenti errati, CommandLineParser mostra
         * la guida ed esce con NotParsed.
         */
        if (parserResult.Tag ==
            ParserResultType.NotParsed)
        {
            return 1;
        }

        ClientOptions options =
            parserResult.Value;

        if (options.Intro)
        {
            await ShowBootSequenceAsync();
        }

        /*
         * Priorità:
         *
         * 1. Riga di comando
         * 2. client.settings.json
         * 3. Default 127.0.0.1:5000
         */
        var (host, port) =
            ClientSettings.Resolve(options);

        AnsiConsole.MarkupLine(
            $"[grey]Connessione a:[/] " +
            $"[bold green]{host}:{port}[/]\n");

        var client = new RpgTcpClient();

        bool connected =
            await client.ConnectAsync(host, port);

        if (!connected)
        {
            AnsiConsole.MarkupLine(
                "[bold red][[!]]: " +
                "Impossibile connettersi al server. " +
                "Premere un tasto per uscire.[/]");

            Console.ReadKey(true);

            return 1;
        }

        /*
         * Loop di I/O, comandi e ascolto di rete.
         */
        await client.RunLoopAsync();

        return 0;
    }
}