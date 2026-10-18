// Importiamo l'assembly condiviso con i contratti
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

        // ==========================================
        // 2. SEQUENZA DI AVVIO ORIGINALE INVARIATA
        // ==========================================
        Console.WriteLine("========================================");
        Console.WriteLine($"🎮 CYBERPUNK RPG CLIENT");
        Console.WriteLine($"🔌 Connessione a: {options.Host}:{options.Port}");
        Console.WriteLine("========================================");

        var client = new RpgTcpClient();
        bool connected = await client.ConnectAsync(options.Host, options.Port);

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