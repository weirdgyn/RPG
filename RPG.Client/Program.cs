using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPG.Client
{
    class Program
    {
        private const string ServerIp = "8.35.196.247"; // IP della tua VM GCP
        private const int ServerPort = 5000;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== RPG CLIENT TEST ===");
            Console.WriteLine($"Connessione a {ServerIp}:{ServerPort}...");

            using var client = new TcpClient();

            try
            {
                await client.ConnectAsync(ServerIp, ServerPort);
                Console.WriteLine("[+] Connesso al server di gioco!");

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                using var cts = new CancellationTokenSource();

                // 1. Task in background per la RICEZIONE continua dei messaggi dal server
                var receiveTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            string? msg = await reader.ReadLineAsync(cts.Token);
                            if (msg == null)
                            {
                                Console.WriteLine("\n[-] Connessione persa dal server.");
                                break;
                            }
                            // Stampa il messaggio in arrivo dal server senza bloccare l'input dell'utente
                            Console.WriteLine($"\n[SERVER] {msg}");
                            Console.Write("> ");
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n[Errore Ricezione]: {ex.Message}");
                    }
                });

                // 2. Loop principale per l'INVIO dei messaggi
                Console.WriteLine("Scrivi un messaggio e premi Invio (scrivi 'exit' per uscire):\n");
                Console.Write("> ");

                while (true)
                {
                    string? input = Console.ReadLine();

                    if (string.IsNullOrWhiteSpace(input))
                    {
                        Console.Write("> ");
                        continue;
                    }

                    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        cts.Cancel();
                        break;
                    }

                    await writer.WriteLineAsync(input);
                }

                await receiveTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Errore Connessione]: {ex.Message}");
            }

            Console.WriteLine("Client chiuso.");
        }
    }
}