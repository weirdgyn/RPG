using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class ServerPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpg-paths-" + Guid.NewGuid());
    private readonly string _data;
    private readonly string _legacyDir;

    public ServerPathsTests()
    {
        _data = Path.Combine(_root, "data");
        _legacyDir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_legacyDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NoFileAnywhere_ReturnsTheFixedFolder()
    {
        string path = ServerPaths.Resolve("server.pfx", _data, Path.Combine(_legacyDir, "server.pfx"), out var migrated);
        Assert.Equal(Path.Combine(_data, "server.pfx"), path);
        Assert.Null(migrated);
    }

    [Fact]
    public void FixedFolderWinsOverALegacyCopy()
    {
        File.WriteAllText(Path.Combine(_data, "server.pfx"), "nuovo");
        string legacy = Path.Combine(_legacyDir, "server.pfx");
        File.WriteAllText(legacy, "vecchio");

        string path = ServerPaths.Resolve("server.pfx", _data, legacy, out var migrated);

        Assert.Equal(Path.Combine(_data, "server.pfx"), path);
        Assert.Null(migrated);
        Assert.Equal("nuovo", File.ReadAllText(path));
    }

    [Fact]
    public void LegacyCopyIsMigratedOnceAndThenUsed()
    {
        string legacy = Path.Combine(_legacyDir, "accounts.json");
        File.WriteAllText(legacy, "[]");

        string first = ServerPaths.Resolve("accounts.json", _data, legacy, out var migrated);
        Assert.Equal(Path.Combine(_data, "accounts.json"), first);
        Assert.Equal(legacy, migrated);
        Assert.Equal("[]", File.ReadAllText(first));
        Assert.True(File.Exists(legacy)); // l'originale non viene toccato

        string second = ServerPaths.Resolve("accounts.json", _data, legacy, out var migratedAgain);
        Assert.Equal(first, second);
        Assert.Null(migratedAgain);
    }

    [Fact]
    public void DataDirectoryHonoursTheEnvironmentVariable()
    {
        string custom = Path.Combine(_root, "custom");
        string? old = Environment.GetEnvironmentVariable(ServerPaths.DataDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ServerPaths.DataDirEnvVar, custom);
            Assert.Equal(Path.GetFullPath(custom), ServerPaths.DataDirectory());
            Assert.True(Directory.Exists(custom));
        }
        finally { Environment.SetEnvironmentVariable(ServerPaths.DataDirEnvVar, old); }
    }
}
