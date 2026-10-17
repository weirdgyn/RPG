using RPG.Common.Model;
using RPG.Common.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RPG.Server;

// Estensione della classe ConnectedClient per gestire l'invio diretto di NetworkPacket
public class ConnectedClient
{
    public Guid Id { get; } = Guid.NewGuid();
    public string IdShort => Id.ToString()[..8];
    public string Username => !string.IsNullOrEmpty(Character?.Handle) ? Character.Handle : "Guest";
    public TcpClient TcpClient { get; }
    public EndPoint? RemoteEndPoint => TcpClient.Client.RemoteEndPoint;
    public PlayerState Character { get; set; } = new();
    private readonly StreamWriter _writer;
    public ConnectedClient(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        var stream = TcpClient.GetStream();
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }
    // Converte l'oggetto NetworkPacket in stringa JSON e lo scrive sul socket
    public async Task SendPacketAsync(NetworkPacket packet)
    {
        string json = JsonSerializer.Serialize(packet);
        await _writer.WriteLineAsync(json);
    }
}
