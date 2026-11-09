using CommandLine;
using RPG.Common.Models;

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

        Console.WriteLine("========================================");
        Console.WriteLine($" NATIVE CYBERPUNK RPG SERVER");
        Console.WriteLine($" Bind Target: {options.Host}:{options.Port}");
        Console.WriteLine("========================================");

        Skills.Initialize();

        string accountsPath = AccountStore.ResolvePath();
        Console.WriteLine($"[SERVER] Account: {accountsPath}");
        var accounts = AccountStore.Load(accountsPath);
        _ = Task.Run(() => ServerConsole.Run(accounts));

        var server = new RpgTcpServer(options.Host, options.Port, accounts, useTls: !options.NoTls);
        await server.StartAsync();

        return 0;
    }
}