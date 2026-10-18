using RPG.Common.Model;
using RPG.Common.Protocol;
using System.Net.Sockets;
using System.Text.Json;

namespace RPG.Server.Services
{
    public class ClientSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string IdShort => Id.ToString().Substring(0, 6);
        public TcpClient TcpClient { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
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