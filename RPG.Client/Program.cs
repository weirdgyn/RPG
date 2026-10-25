using CommandLine;

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
        // 2. SEQUENZA DI AVVIO ORIGINALE INVARIATA
        // ==========================================
        Console.WriteLine("========================================");
        Console.WriteLine($"🎮 CYBERPUNK RPG CLIENT");
        Console.WriteLine($"🔌 Connessione a: {host}:{port}");
        Console.WriteLine("========================================");

        var client = new RpgTcpClient();
        bool connected = await client.ConnectAsync(host, port);

        if (!connected)
        {
            Console.WriteLine("[!] Impossibile connettersi al server. Premere un tasto per uscire.");
            Console.ReadKey();
            return 1;
        }

        // Loop di I/O comandi e ascolto di rete
        await client.RunLoopAsync();

        return 0;
    }
}