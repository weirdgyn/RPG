using System.Net;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class ConnectionGuardTests
{
    private static readonly IPAddress A = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress B = IPAddress.Parse("203.0.113.6");
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LimitsSimultaneousConnectionsPerAddress()
    {
        var g = new ConnectionGuard(maxPerAddress: 2, maxNewPerWindow: 100);
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0));
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0));
        Assert.Equal(AdmitResult.TooManyFromAddress, g.TryAdmit(A, T0));
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(B, T0));

        g.Release(A, T0);
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0));
    }

    [Fact]
    public void LimitsConnectionRateAndRecoversAfterWindow()
    {
        var g = new ConnectionGuard(maxPerAddress: 100, maxNewPerWindow: 3, rateWindow: TimeSpan.FromSeconds(60));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0));
            g.Release(A, T0);
        }
        Assert.Equal(AdmitResult.RateLimited, g.TryAdmit(A, T0.AddSeconds(10)));
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0.AddSeconds(61)));
    }

    [Fact]
    public void BansAfterEnoughStrikesThenExpires()
    {
        var g = new ConnectionGuard(maxStrikes: 3, banDuration: TimeSpan.FromMinutes(15));
        Assert.False(g.ReportStrike(A, T0));
        Assert.False(g.ReportStrike(A, T0.AddSeconds(1)));
        Assert.True(g.ReportStrike(A, T0.AddSeconds(2)));

        Assert.True(g.IsBanned(A, T0.AddMinutes(1)));
        Assert.Equal(AdmitResult.Banned, g.TryAdmit(A, T0.AddMinutes(1)));
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(B, T0.AddMinutes(1)));

        Assert.False(g.IsBanned(A, T0.AddMinutes(16)));
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0.AddMinutes(16)));
    }

    [Fact]
    public void OldStrikesOutsideWindowDoNotCount()
    {
        var g = new ConnectionGuard(maxStrikes: 2, strikeWindow: TimeSpan.FromMinutes(10));
        Assert.False(g.ReportStrike(A, T0));
        Assert.False(g.ReportStrike(A, T0.AddMinutes(11)));
        Assert.False(g.IsBanned(A, T0.AddMinutes(11)));
    }

    [Fact]
    public void SuccessfulLoginClearsStrikes()
    {
        var g = new ConnectionGuard(maxStrikes: 2);
        Assert.False(g.ReportStrike(A, T0));
        g.ReportSuccess(A);
        Assert.False(g.ReportStrike(A, T0.AddSeconds(1)));
        Assert.False(g.IsBanned(A, T0.AddSeconds(1)));
    }

    [Fact]
    public void LoopbackIsNeverLimitedOrBanned()
    {
        var g = new ConnectionGuard(maxPerAddress: 1, maxNewPerWindow: 1, maxStrikes: 1);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(AdmitResult.Allowed, g.TryAdmit(IPAddress.Loopback, T0));
            Assert.False(g.ReportStrike(IPAddress.Loopback, T0));
        }
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(IPAddress.IPv6Loopback, T0));
    }

    [Fact]
    public void Ipv4MappedAddressIsTheSameAsIpv4()
    {
        var g = new ConnectionGuard(maxPerAddress: 1, maxNewPerWindow: 100);
        Assert.Equal(AdmitResult.Allowed, g.TryAdmit(A, T0));
        Assert.Equal(AdmitResult.TooManyFromAddress, g.TryAdmit(A.MapToIPv6(), T0));
    }
}
