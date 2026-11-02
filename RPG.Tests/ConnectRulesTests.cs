using RPG.Common;
using Xunit;

namespace RPG.Tests;

public class ConnectRulesTests
{
    [Fact]
    public void NoArguments_ReusesLastServer()
    {
        Assert.True(ConnectRules.TryParse([], "10.0.0.5", 5000, out var host, out var port, out _));
        Assert.Equal("10.0.0.5", host);
        Assert.Equal(5000, port);
    }

    [Fact]
    public void OneArgument_ChangesHostOnly()
    {
        Assert.True(ConnectRules.TryParse(["example.org"], "10.0.0.5", 5000, out var host, out var port, out _));
        Assert.Equal("example.org", host);
        Assert.Equal(5000, port);
    }

    [Fact]
    public void TwoArguments_ChangeHostAndPort()
    {
        Assert.True(ConnectRules.TryParse(["example.org", "6000"], "10.0.0.5", 5000, out var host, out var port, out _));
        Assert.Equal("example.org", host);
        Assert.Equal(6000, port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidPort_IsRejected(string badPort)
    {
        Assert.False(ConnectRules.TryParse(["example.org", badPort], "10.0.0.5", 5000, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TooManyArguments_AreRejected()
    {
        Assert.False(ConnectRules.TryParse(["a", "1", "b"], "10.0.0.5", 5000, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void NoKnownServer_IsRejected()
    {
        Assert.False(ConnectRules.TryParse([], "", 0, out _, out _, out var error));
        Assert.NotEmpty(error);
    }
}
