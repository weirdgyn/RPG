using RPG.Common.Model;
using RPG.Common.Protocol;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server.Services
{
    public class ClientSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string IdShort => Id.ToString()[..6];
        public TcpClient TcpClient { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        // Diventa true solo dopo un login riuscito; i comandi di gioco sono bloccati prima.
        public bool IsAuthenticated { get; set; }
        public PlayerState Character { get; set; } = new();
        public ClientSession(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
            var stream = tcpClient.GetStream();
            Reader = new StreamReader(stream);
            Writer = new StreamWriter(stream) { AutoFlush = true };
        }
        public async Task SendPacketAsync(NetworkPacket packet)
        {
            string json = JsonSerializer.Serialize(packet);
            await Writer.WriteLineAsync(json);
        }
    }
}