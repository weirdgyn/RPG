using RPG.Common.Models;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class CharacterRepositorySnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rpgtest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Save_WithSyncRoot_WaitsForTheLockBeforeSerializing()
    {
        var character = new Character { Handle = "SnapLock" };
        var sync = new object();
        Task save;

        lock (sync)
        {
            save = Task.Run(() => CharacterRepository.SaveCharacter(character, _dir, sync));

            // Finché qualcuno tiene il lock del game la scheda non può essere serializzata
            Assert.False(save.Wait(300));
        }

        Assert.True(save.Wait(5000));
        Assert.True(CharacterRepository.CharacterExists("SnapLock", _dir));
    }

    [Fact]
    public void Save_WithoutSyncRoot_WritesImmediately()
    {
        var character = new Character { Handle = "SnapFree" };

        Assert.True(CharacterRepository.TrySaveCharacter(character, _dir));
        Assert.True(CharacterRepository.CharacterExists("SnapFree", _dir));
    }

    [Fact]
    public void TrySave_WithSyncRoot_SavesWhenFree()
    {
        var character = new Character { Handle = "SnapSync" };

        Assert.True(CharacterRepository.TrySaveCharacter(character, _dir, new object()));
        Assert.True(CharacterRepository.CharacterExists("SnapSync", _dir));
    }
}
