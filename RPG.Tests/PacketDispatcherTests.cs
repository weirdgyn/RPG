using System.Net.Sockets;
using RPG.Common.Protocol;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class PacketDispatcherTests
{
    private static ClientSession NewSession(bool authenticated)
    {
        // Flusso in memoria: nessuna rete; la sessione scrive su un MemoryStream che i test non leggono.
        var session = new ClientSession(new TcpClient(), new MemoryStream()) { IsAuthenticated = authenticated };
        return session;
    }

    private static NetworkPacket Packet(OpCode opCode) => NetworkPacket.Create(opCode, new { });

    [Fact]
    public async Task Dispatch_CallsRegisteredHandler_WhenAuthenticated()
    {
        var dispatcher = new PacketDispatcher();
        int calls = 0;
        dispatcher.Register(OpCode.C2S_Roll, (_, _) => { calls++; return Task.CompletedTask; });
        var session = NewSession(authenticated: true);

        await dispatcher.DispatchAsync(session, Packet(OpCode.C2S_Roll));

        Assert.Equal(1, calls);
        session.Abort();
    }

    [Fact]
    public async Task Dispatch_BlocksGameCommands_BeforeLogin()
    {
        var dispatcher = new PacketDispatcher();
        int calls = 0;
        dispatcher.Register(OpCode.C2S_Roll, (_, _) => { calls++; return Task.CompletedTask; });
        var session = NewSession(authenticated: false);

        await dispatcher.DispatchAsync(session, Packet(OpCode.C2S_Roll));

        Assert.Equal(0, calls);
        session.Abort();
    }

    [Theory]
    [InlineData(OpCode.C2S_Login)]
    [InlineData(OpCode.C2S_Ping)]
    public async Task Dispatch_AllowsLoginAndPing_BeforeLogin(OpCode opCode)
    {
        var dispatcher = new PacketDispatcher();
        int calls = 0;
        dispatcher.Register(opCode, (_, _) => { calls++; return Task.CompletedTask; });
        var session = NewSession(authenticated: false);

        await dispatcher.DispatchAsync(session, Packet(opCode));

        Assert.Equal(1, calls);
        session.Abort();
    }

    [Fact]
    public async Task Dispatch_UnknownOpCode_DoesNotThrow()
    {
        var dispatcher = new PacketDispatcher();
        var session = NewSession(authenticated: true);

        await dispatcher.DispatchAsync(session, Packet(OpCode.C2S_Roll));

        session.Abort();
    }

    [Fact]
    public void Register_DuplicateOpCode_Throws()
    {
        var dispatcher = new PacketDispatcher()
            .Register(OpCode.C2S_Roll, (_, _) => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Register(OpCode.C2S_Roll, (_, _) => Task.CompletedTask));
        Assert.True(dispatcher.IsRegistered(OpCode.C2S_Roll));
        Assert.False(dispatcher.IsRegistered(OpCode.C2S_Move));
    }
}
