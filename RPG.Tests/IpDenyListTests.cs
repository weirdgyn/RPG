using System.Net;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class IpDenyListTests
{
    private static IpDenyList Make(params string[] lines) => IpDenyList.Parse(lines);

    [Fact]
    public void SingleAddressMatchesOnlyItself()
    {
        var l = Make("203.0.113.7");
        Assert.True(l.IsDenied(IPAddress.Parse("203.0.113.7")));
        Assert.False(l.IsDenied(IPAddress.Parse("203.0.113.8")));
    }

    [Fact]
    public void CidrMatchesWholeRange()
    {
        var l = Make("198.51.100.0/24");
        Assert.True(l.IsDenied(IPAddress.Parse("198.51.100.0")));
        Assert.True(l.IsDenied(IPAddress.Parse("198.51.100.255")));
        Assert.False(l.IsDenied(IPAddress.Parse("198.51.101.1")));
    }

    [Fact]
    public void CidrWithPartialByteMask()
    {
        var l = Make("10.0.0.128/25");
        Assert.True(l.IsDenied(IPAddress.Parse("10.0.0.200")));
        Assert.False(l.IsDenied(IPAddress.Parse("10.0.0.5")));
    }

    [Fact]
    public void Ipv6RangeAndMappedIpv4()
    {
        var l = Make("2001:db8::/32", "203.0.113.7");
        Assert.True(l.IsDenied(IPAddress.Parse("2001:db8:1::5")));
        Assert.False(l.IsDenied(IPAddress.Parse("2001:db9::1")));
        Assert.True(l.IsDenied(IPAddress.Parse("203.0.113.7").MapToIPv6()));
    }

    [Fact]
    public void CommentsBlankLinesAndInvalidEntries()
    {
        var l = Make("# commento", "", "  192.0.2.1  # trailing", "non-un-ip", "10.0.0.0/33", "10.0.0.0/x");
        Assert.Equal(1, l.Count);
        Assert.True(l.IsDenied(IPAddress.Parse("192.0.2.1")));
        Assert.Equal([4, 5, 6], l.Invalid.Select(i => i.Line).ToArray());
    }

    [Fact]
    public void LoopbackIsNeverDenied()
    {
        var l = Make("127.0.0.1", "0.0.0.0/0", "::1");
        Assert.False(l.IsDenied(IPAddress.Loopback));
        Assert.False(l.IsDenied(IPAddress.IPv6Loopback));
        Assert.True(l.IsDenied(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void MissingFileGivesEmptyList()
    {
        var l = IpDenyList.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt"));
        Assert.Equal(0, l.Count);
        Assert.False(l.IsDenied(IPAddress.Parse("8.8.8.8")));
    }
}
