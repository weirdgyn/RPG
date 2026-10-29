using CommandLine;
using NAudio.Wave;
using Spectre.Console;

namespace RPG.Client;

internal class Program
{
    private static async Task ShowBootSequenceAsync(string overrideFont="")
    {
        AnsiConsole.Clear();

        ConsoleColor previousForeground = Console.ForegroundColor;
        ConsoleColor previousBackground = Console.BackgroundColor;
        bool previousCursorVisible = Console.CursorVisible;

        Console.BackgroundColor = ConsoleColor.Black;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.CursorVisible = false;

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
             * =================================================
             * FASE 1: COMPOSIZIONE
             * =================================================
             *
             * Il cursore solido avanza durante la scrittura
             * di DIALING...
             *
             * Poi lampeggia per tutta la durata del relativo
             * effetto sonoro.
             */

            await TypeTextWithSolidCursorAsync(
                text: "DIALING...",
                averageDelayMs: 100);

            Task dialingTask = PlayAudioAsync(
                dialingAudioPath);

            await BlinkSolidCursorUntilCompletedAsync(
                dialingTask);

            await dialingTask;

            Console.WriteLine();


            /*
             * =================================================
             * FASE 2: HANDSHAKE E NEGOZIAZIONE
             * =================================================
             */

            await TypeTextWithSolidCursorAsync(
                text: "NEGOTIATING PROTOCOL...",
                averageDelayMs: 70);

            Task handshakeTask = PlayAudioAsync(
                handshakeAudioPath);

            await BlinkSolidCursorUntilCompletedAsync(
                handshakeTask);

            await handshakeTask;

            Console.WriteLine();


            /*
             * =================================================
             * FASE 3: RISULTATO DELLA NEGOZIAZIONE
             * =================================================
             *
             * Questi messaggi appaiono soltanto dopo che
             * modem-handshake.mp3 è terminato.
             */

            await Task.Delay(250);

            await TypeTextWithSolidCursorAsync(
                text: "REMOTE SYSTEM IDENTIFIED",
                averageDelayMs: 45);

            Console.WriteLine();

            await Task.Delay(300);

            await TypeTextWithSolidCursorAsync(
                text: "ESTABLISHING UPLINK...",
                averageDelayMs: 55);

            Console.WriteLine();

            await Task.Delay(400);

            Console.ForegroundColor = ConsoleColor.White;

            await TypeTextWithSolidCursorAsync(
                text: "CONNECTION ESTABLISHED",
                averageDelayMs: 55);

            Console.ForegroundColor = ConsoleColor.Green;

            /*
             * Piccola pausa drammatica prima del banner.
             * Tecnicamente inutile, artisticamente necessaria.
             */

            await Task.Delay(1200);


            /*
             * =================================================
             * BANNER DELLA BBS
             * =================================================
             */

            AnsiConsole.Clear();

            AnsiConsole.Write(
                BuildRandomBanner("NIGHT CITY", overrideFont));

            AnsiConsole.Write(
                new Rule(
                    "[bold cyan]" +
                    "NETRUNNER BBS // CYBERPUNK 2020" +
                    "[/]")
                .Centered());
        }
        finally
        {
            Console.ForegroundColor = previousForeground;
            Console.BackgroundColor = previousBackground;
            Console.CursorVisible = previousCursorVisible;
        }
    }

    /// <summary>
    /// Scrive il testo carattere per carattere mantenendo
    /// un cursore solido immediatamente dopo l'ultimo carattere.
    ///
    /// Esempio:
    ///
    /// █
    /// D█
    /// DI█
    /// DIA█
    /// DIAL█
    /// </summary>
    private static async Task TypeTextWithSolidCursorAsync(
        string text,
        int averageDelayMs)
    {
        /*
         * Disegna il cursore nella posizione iniziale.
         */
        Console.Write('█');

        foreach (char character in text)
        {
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

            /*
             * Il blocco si trova nella posizione in cui deve
             * comparire il prossimo carattere.
             *
             * \b         torna sopra il blocco
             * character  sostituisce il blocco con la lettera
             * █          disegna il blocco nella posizione seguente
             */
            Console.Write('\b');
            Console.Write(character);
            Console.Write('█');
        }

        /*
         * Rimuove il blocco finale.
         *
         * La posizione logica della console resta subito dopo
         * l'ultimo carattere scritto, pronta per il lampeggio.
         */
        EraseSolidCursor();
    }

    /// <summary>
    /// Fa lampeggiare il cursore solido fino al completamento
    /// del Task monitorato, normalmente la riproduzione audio.
    /// </summary>
    private static async Task BlinkSolidCursorUntilCompletedAsync(
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
             * Il cursore deve essere cancellato anche quando
             * il Task termina durante la fase visibile.
             */
            EraseSolidCursor();

            if (monitoredTask.IsCompleted)
            {
                break;
            }

            /*
             * Cursore spento.
             */
            await Task.WhenAny(
                monitoredTask,
                Task.Delay(hiddenDurationMs));
        }
    }

    /// <summary>
    /// Cancella il cursore solido e riporta la posizione
    /// di scrittura nella stessa colonna.
    /// </summary>
    private static void EraseSolidCursor()
    {
        Console.Write("\b \b");
    }

    /// <summary>
    /// Riproduce un file audio e completa il Task quando
    /// la riproduzione è terminata.
    /// </summary>
    private static Task PlayAudioAsync(
        string audioPath)
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
                 * Se il file non esiste, il dispositivo audio
                 * non è disponibile o NAudio genera un errore,
                 * l'introduzione prosegue senza audio.
                 */
            }
        });
    }

    private static FigletText BuildRandomBanner(
        string text, string overrideFont="")
    {
        string selectedFont;
        FigletFont font;

        if (string.IsNullOrWhiteSpace(overrideFont))
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

            selectedFont = fonts[
                Random.Shared.Next(fonts.Length)];
        }
        else selectedFont = overrideFont;

        font = FigletFont.Load(selectedFont);

        return new FigletText(font, text)
            .Centered()
            .Color(Color.Cyan);
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
         * la guida ed esce restituendo NotParsed.
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
            await ShowBootSequenceAsync(options.Font);
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
            await client.ConnectAsync(
                host,
                port);

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