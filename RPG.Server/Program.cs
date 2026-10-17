using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RPG.Server
{
    class Program
    {
        private const int Port = 5000;
        // Registro thread-safe dei client connessi (ID univoco -> TcpClient)
        private static readonly ConcurrentDictionary<Guid, ConnectedClient> Clients = new();

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== RPG SERVER CORE v0.1 ===");
            var listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Console.WriteLine($"[*] Server in ascolto su 0.0.0.0:{Port}...");

            var cts = new CancellationTokenSource();

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Accetta una nuova connessione senza bloccare
                    TcpClient tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
                    var client = new ConnectedClient(tcpClient);

                    Clients.TryAdd(client.Id, client);
                    Console.WriteLine($"[+] [Client {client.IdShort}] Connesso da {client.RemoteEndPoint}");

                    // Avvia la gestione del client in un Task separato (non bloccante)
                    _ = Task.Run(() => HandleClientAsync(client, cts.Token));
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
        {
            // Notifica il nuovo client della connessione avvenuta
            await client.SendMessageAsync($"WELCOME|Il tuo ID e': {client.IdShort}");

            // Broadcast agli altri giocatori
            await BroadcastMessageAsync($"SYSTEM|[Client {client.IdShort}] e' entrato nel mondo.", client.Id);

            try
            {
                using var stream = client.TcpClient.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!ct.IsCancellationRequested && client.TcpClient.Connected)
                {
                    string? rawMessage = await reader.ReadLineAsync(ct);

                    if (rawMessage == null) break; // Disconnessione pulita dal client

                    Console.WriteLine($"[<] [Client {client.IdShort}]: {rawMessage}");

                    // Broadcast del messaggio a TUTTI gli altri client
                    await BroadcastMessageAsync($"CHAT|[{client.IdShort}]: {rawMessage}", client.Id);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Errore gestione client {client.IdShort}: {ex.Message}");
            }
            finally
            {
                // Cleanup alla disconnessione
                Clients.TryRemove(client.Id, out _);
                client.TcpClient.Close();
                Console.WriteLine($"[-] [Client {client.IdShort}] Disconnesso.");
                await BroadcastMessageAsync($"SYSTEM|[Client {client.IdShort}] e' uscito dal mondo.", client.Id);
            }
        }

        private static async Task BroadcastMessageAsync(string message, Guid? excludeClientId = null)
        {
            foreach (var kvp in Clients)
            {
                if (excludeClientId.HasValue && kvp.Key == excludeClientId.Value)
                    continue; // Salta il mittente se richiesto

                try
                {
                    await kvp.Value.SendMessageAsync(message);
                }
                catch
                {
                    // Se fallisce l'invio, il client verrà rimosso dal suo loop di lettura
                }
            }
        }
    }

    // Abstraction minimale per rappresentare un Client connesso
    public class ConnectedClient
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string IdShort => Id.ToString()[..8];
        public TcpClient TcpClient { get; }
        public EndPoint? RemoteEndPoint => TcpClient.Client.RemoteEndPoint;

        private readonly StreamWriter _writer;

        public ConnectedClient(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
            var stream = TcpClient.GetStream();
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }

        public async Task SendMessageAsync(string message)
        {
            await _writer.WriteLineAsync(message);
        }
    }
}