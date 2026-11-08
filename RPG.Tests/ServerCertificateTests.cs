using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class ServerCertificateTests
{
    [Fact]
    public void CreatesThenReloadsTheSameCertificate()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx");
        try
        {
            using var first = ServerCertificate.LoadOrCreate(path, out bool created1);
            using var second = ServerCertificate.LoadOrCreate(path, out bool created2);

            Assert.True(created1);
            Assert.False(created2);
            Assert.True(first.HasPrivateKey);
            Assert.Equal(first.Thumbprint, second.Thumbprint);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TlsHandshakeAndLineExchangeOverLoopback()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx");
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var cert = ServerCertificate.LoadOrCreate(path, out _);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(ct);
                using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct);
                using var reader = new StreamReader(ssl);
                using var writer = new StreamWriter(ssl) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(ct);
                await writer.WriteLineAsync("echo:" + line);
            }, ct);

            string? seenThumbprint = null;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            using var clientSsl = new SslStream(client.GetStream(), false, (_, c, _, _) =>
            {
                seenThumbprint = (c as System.Security.Cryptography.X509Certificates.X509Certificate2)?.Thumbprint
                    ?? new System.Security.Cryptography.X509Certificates.X509Certificate2(c!).Thumbprint;
                return true;
            });
            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, ct);
            using var cReader = new StreamReader(clientSsl);
            using var cWriter = new StreamWriter(clientSsl) { AutoFlush = true };
            await cWriter.WriteLineAsync("ciao");
            string? reply = await cReader.ReadLineAsync(ct);
            await serverTask;

            Assert.Equal("echo:ciao", reply);
            Assert.Equal(cert.Thumbprint, seenThumbprint);
        }
        finally
        {
            listener.Stop();
            File.Delete(path);
        }
    }
}
