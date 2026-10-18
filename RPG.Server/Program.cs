// Importiamo le classi definite nell'assembly condiviso RPG.Common
using CommandLine;
using RPG.Common.Model;
using RPG.Server.Services;

namespace RPG.Server;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var parserResult = Parser.Default.ParseArguments<ServerOptions>(args);

        // Se l'utente ha chiesto --help o ha passato argomenti errati, la libreria gestisce tutto e restituisce NotParsed
        if (parserResult.Tag == ParserResultType.NotParsed)
        {
            return 1;
        }

        var options = parserResult.Value;

        // ==========================================
        // 2. SEQUENZA DI AVVIO ORIGINALE INVARIATA
        // ==========================================
        Console.WriteLine("========================================");
        Console.WriteLine($"🚀 NATIVE CYBERPUNK RPG SERVER");
        Console.WriteLine($"📍 Bind Target: {options.Host}:{options.Port}");
        Console.WriteLine("========================================");

        Skills.Initialize();

        var server = new RpgTcpServer(options.Host, options.Port);
        await server.StartAsync();

        return 0;
    }
}