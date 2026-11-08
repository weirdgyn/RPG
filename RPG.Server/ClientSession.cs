using RPG.Common.Models;
using RPG.Common.Protocol;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Text.Json;

namespace RPG.Server;

public class ClientSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public string IdShort => Id.ToString()[..6];
    public TcpClient TcpClient { get; }
    private StreamReader Reader { get; }
    // StreamWriter non è thread-safe e un client lento non deve bloccare gli altri: i pacchetti verso questo
    // client vengono messi in una coda limitata (SendPacketAsync non aspetta mai la rete) e un solo task
    // scrittore li invia in ordine, con un timeout per ogni scrittura. Se il client non legge (scrittura
    // oltre il timeout o coda piena) la sessione viene chiusa.
    private readonly StreamWriter _writer;
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(InputLimits.MaxQueuedPackets)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Task _writerLoop;
    private volatile bool _closed;
    /// <summary>Istante (UTC) della connessione: serve alla scadenza del login.</summary>
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    // Diventa true solo dopo un login riuscito; i comandi di gioco sono bloccati prima.
    public bool IsAuthenticated { get; set; }
    /// <summary>Indirizzo remoto (normalizzato) con cui la connessione è stata ammessa da <see cref="ConnectionGuard"/>; null se non tracciato.</summary>
    public System.Net.IPAddress? RemoteAddress { get; set; }
    public Player Player { get; set; } = new();
    // Handle con cui la sessione ha fatto /link (quello richiesto e validato, cioè il nome del file della
    // scheda). Il controllo "personaggio già in uso" confronta questo valore e non l'Handle scritto
    // dentro il JSON, che in una scheda creata a mano o copiata può differire dal nome del file.
    // Letto e scritto solo sotto _linkLock del server.
    public string? LinkedCharacterHandle { get; set; }
    // Schede create da questa sessione con /create (tetto per sessione contro l'esaurimento del disco).
    // Letto e scritto solo sotto _createLock del server.
    public int CharactersCreated { get; set; }
    public ClientSession(TcpClient tcpClient, Stream? stream = null)
    {
        TcpClient = tcpClient;
        stream ??= tcpClient.GetStream();
        Reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writerLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Chiude la connessione (idempotente): letture e scritture in corso falliscono e la sessione termina.</summary>
    public void Abort()
    {
        _closed = true;
        _outbound.Writer.TryComplete();
        try { TcpClient.Close(); } catch { /* già chiuso */ }
    }

    /// <summary>
    /// Chiusura ordinata: lascia al massimo un paio di secondi per inviare i pacchetti già in coda, poi chiude.
    /// </summary>
    public async Task CloseAsync()
    {
        _closed = true;
        _outbound.Writer.TryComplete();
        await Task.WhenAny(_writerLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        Abort();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (string json in _outbound.Reader.ReadAllAsync())
            {
                // StreamWriter non rispetta il token mentre il socket è bloccato: un watchdog chiude la
                // connessione se la scrittura non finisce in tempo, e la scrittura fallisce di conseguenza.
                using var watchdog = new CancellationTokenSource();
                var write = _writer.WriteLineAsync(json);
                var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(InputLimits.SendTimeoutSeconds), watchdog.Token));
                if (finished != write)
                {
                    Console.WriteLine($"[NET] Il client [{IdShort}] non legge da {InputLimits.SendTimeoutSeconds}s: sessione chiusa.");
                    Abort();
                    return;
                }
                watchdog.Cancel();
                await write;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] Scrittura verso [{IdShort}] fallita ({ex.GetType().Name}): sessione chiusa.");
        }
        finally
        {
            Abort();
        }
    }
    /// <summary>
    /// Come <c>StreamReader.ReadLineAsync</c>, ma con un tetto alla lunghezza della riga: un client che
    /// manda dati senza mai andare a capo non può far crescere la memoria del server senza limite.
    /// Restituisce null a fine flusso; lancia <see cref="InvalidDataException"/> se la riga supera il tetto.
    /// </summary>
    public async Task<string?> ReadLineLimitedAsync(int maxLength, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            int read = await Reader.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();   // fine flusso (ultima riga senza \n)
            }

            char c = buffer[0];
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }

            if (sb.Length >= maxLength)
            {
                throw new InvalidDataException($"Riga oltre il limite di {maxLength} caratteri.");
            }

            sb.Append(c);
        }
    }

    /// <summary>
    /// Accoda il pacchetto per l'invio e ritorna subito (non aspetta la rete). Se il client è troppo lento la
    /// coda si riempie e la sessione viene chiusa; i pacchetti verso una sessione chiusa sono scartati.
    /// </summary>
    public Task SendPacketAsync(NetworkPacket packet)
    {
        string json = JsonSerializer.Serialize(packet);

        if (!_outbound.Writer.TryWrite(json) && !_closed)
        {
            Console.WriteLine($"[NET] Coda di invio piena per [{IdShort}]: client troppo lento, sessione chiusa.");
            Abort();
        }

        return Task.CompletedTask;
    }
}
