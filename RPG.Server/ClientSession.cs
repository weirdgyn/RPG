using RPG.Common.Models;
using RPG.Common.Protocol;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server;

public class ClientSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public string IdShort => Id.ToString()[..6];
    public TcpClient TcpClient { get; }
    private StreamReader Reader { get; }
    // StreamWriter non è thread-safe: i pacchetti verso questo client partono da più task
    // (il suo handler, i broadcast e le risposte generate dagli altri client). Ogni scrittura
    // passa da SendPacketAsync, che le serializza con _sendLock; per questo il writer è privato.
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Diventa true solo dopo un login riuscito; i comandi di gioco sono bloccati prima.
    public bool IsAuthenticated { get; set; }
    public Player Player { get; set; } = new();
    // Handle con cui la sessione ha fatto /link (quello richiesto e validato, cioè il nome del file della
    // scheda). Il controllo "personaggio già in uso" confronta questo valore e non l'Handle scritto
    // dentro il JSON, che in una scheda creata a mano o copiata può differire dal nome del file.
    // Letto e scritto solo sotto _linkLock del server.
    public string? LinkedCharacterHandle { get; set; }
    // Schede create da questa sessione con /create (tetto per sessione contro l'esaurimento del disco).
    // Letto e scritto solo sotto _createLock del server.
    public int CharactersCreated { get; set; }
    public ClientSession(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        var stream = tcpClient.GetStream();
        Reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }
    /// <summary>
    /// Come <c>StreamReader.ReadLineAsync</c>, ma con un tetto alla lunghezza della riga: un client che
    /// manda dati senza mai andare a capo non può far crescere la memoria del server senza limite.
    /// Restituisce null a fine flusso; lancia <see cref="InvalidDataException"/> se la riga supera il tetto.
    /// </summary>
    public async Task<string?> ReadLineLimitedAsync(int maxLength)
    {
        var sb = new System.Text.StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            int read = await Reader.ReadAsync(buffer, 0, 1);
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

    public async Task SendPacketAsync(NetworkPacket packet)
    {
        // Serializzazione fuori dal lock: solo la scrittura sullo stream va resa esclusiva.
        string json = JsonSerializer.Serialize(packet);

        await _sendLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(json);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}