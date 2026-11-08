using RPG.Common.Utils;
using Xunit;

namespace RPG.Tests;

public class TrustStoreTests
{
    [Fact]
    public void UnknownServerIsNewThenTrustedThenMismatch()
    {
        var store = new TrustStore();
        Assert.Equal(TrustResult.NewServer, store.Check("Example.org", 5000, "AA"));

        store.Remember("example.org", 5000, "AA");
        Assert.Equal(TrustResult.Trusted, store.Check("EXAMPLE.org", 5000, "aa"));
        Assert.Equal(TrustResult.Mismatch, store.Check("example.org", 5000, "BB"));
    }

    [Fact]
    public void SamePinIsPerHostAndPort()
    {
        var store = new TrustStore();
        store.Remember("example.org", 5000, "AA");
        Assert.Equal(TrustResult.NewServer, store.Check("example.org", 5001, "BB"));
        Assert.Equal(TrustResult.NewServer, store.Check("other.org", 5000, "BB"));
    }

    [Fact]
    public void PinsSurviveReload()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.True(new TrustStore(path).Remember("h", 1, "FF00"));
            var reloaded = TrustStore.Load(path);
            Assert.Equal("FF00", reloaded.Get("h", 1));
            Assert.Equal(TrustResult.Mismatch, reloaded.Check("h", 1, "0000"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptFileGivesEmptyStore()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{ non json");
            Assert.Equal(TrustResult.NewServer, TrustStore.Load(path).Check("h", 1, "AA"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FingerprintIsUppercaseHexSha256()
    {
        string fp = TrustStore.Fingerprint([1, 2, 3]);
        Assert.Equal(64, fp.Length);
        Assert.Equal(fp.ToUpperInvariant(), fp);
    }
}
