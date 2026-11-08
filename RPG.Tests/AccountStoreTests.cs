using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class AccountStoreTests
{
    private const int FastIterations = 1000;
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static AccountStore Make(string? path = null) => new(path, FastIterations);

    [Fact]
    public void CreateThenVerifyReturnsCanonicalName()
    {
        var s = Make();
        Assert.True(s.TryCreate("Morgan", "correct horse", out _));

        Assert.Equal(AuthResult.Ok, s.Verify("morgan", "correct horse", T0, out var name));
        Assert.Equal("Morgan", name);
    }

    [Fact]
    public void WrongPasswordAndUnknownUserLookTheSame()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);

        Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "wrong pass", T0, out var n1));
        Assert.Equal(AuthResult.BadCredentials, s.Verify("Nobody", "correct horse", T0, out var n2));
        Assert.Null(n1);
        Assert.Null(n2);
    }

    [Fact]
    public void LocksAfterTooManyFailuresThenUnlocks()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);

        for (int i = 0; i < AccountStore.MaxFailures; i++)
            Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "bad", T0.AddSeconds(i), out _));

        // Anche la password giusta è respinta mentre l'account è bloccato.
        Assert.Equal(AuthResult.LockedOut, s.Verify("Morgan", "correct horse", T0.AddMinutes(1), out _));
        Assert.Equal(AuthResult.Ok, s.Verify("Morgan", "correct horse", T0.AddMinutes(6), out _));
    }

    [Fact]
    public void SuccessResetsTheFailureCount()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);

        for (int i = 0; i < AccountStore.MaxFailures - 1; i++) s.Verify("Morgan", "bad", T0, out _);
        Assert.Equal(AuthResult.Ok, s.Verify("Morgan", "correct horse", T0, out _));
        for (int i = 0; i < AccountStore.MaxFailures - 1; i++)
            Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "bad", T0, out _));
    }

    [Fact]
    public void OldFailuresOutsideWindowDoNotLock()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);

        for (int i = 0; i < AccountStore.MaxFailures - 1; i++) s.Verify("Morgan", "bad", T0, out _);
        Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "bad", T0.AddMinutes(11), out _));
        Assert.Equal(AuthResult.Ok, s.Verify("Morgan", "correct horse", T0.AddMinutes(11), out _));
    }

    [Fact]
    public void UnknownUsernameCanBeLockedToo()
    {
        var s = Make();
        for (int i = 0; i < AccountStore.MaxFailures; i++) s.Verify("Ghost", "bad", T0, out _);
        Assert.Equal(AuthResult.LockedOut, s.Verify("Ghost", "bad", T0.AddSeconds(1), out _));
    }

    [Fact]
    public void ValidationRejectsBadInput()
    {
        var s = Make();
        Assert.False(s.TryCreate("", "correct horse", out _));
        Assert.False(s.TryCreate(new string('x', InputLimits.MaxUsernameLength + 1), "correct horse", out _));
        Assert.False(s.TryCreate("bad\nname", "correct horse", out _));
        Assert.False(s.TryCreate("Morgan", "short", out _));
        Assert.False(s.TryCreate("Morgan", new string('p', AccountStore.MaxPasswordLength + 1), out _));
        Assert.Equal(0, s.Count);

        Assert.True(s.TryCreate("Morgan", "correct horse", out _));
        Assert.False(s.TryCreate("MORGAN", "another pass", out var error));
        Assert.Contains("esiste", error);
    }

    [Fact]
    public void SetPasswordChangesItAndClearsLock()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);
        for (int i = 0; i < AccountStore.MaxFailures; i++) s.Verify("Morgan", "bad", T0, out _);

        Assert.True(s.TrySetPassword("morgan", "new password", out _));
        Assert.Equal(AuthResult.Ok, s.Verify("Morgan", "new password", T0, out _));
        Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "correct horse", T0, out _));
        Assert.False(s.TrySetPassword("Nobody", "new password", out _));
    }

    [Fact]
    public void RemoveDeletesTheAccount()
    {
        var s = Make();
        s.TryCreate("Morgan", "correct horse", out _);
        Assert.True(s.Remove("morgan"));
        Assert.False(s.Remove("morgan"));
        Assert.Equal(AuthResult.BadCredentials, s.Verify("Morgan", "correct horse", T0, out _));
    }

    [Fact]
    public void AccountsAreSavedWithoutPlaintextAndReloaded()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.True(Make(path).TryCreate("Morgan", "correct horse", out _));

            Assert.DoesNotContain("correct horse", File.ReadAllText(path));
            var reloaded = AccountStore.Load(path, FastIterations);
            Assert.Equal(1, reloaded.Count);
            Assert.Equal(AuthResult.Ok, reloaded.Verify("morgan", "correct horse", T0, out var name));
            Assert.Equal("Morgan", name);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
