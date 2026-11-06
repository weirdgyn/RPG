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
    public async Task Save_WithSyncRoot_WaitsForTheLockBeforeSerializing()
    {
        var ct = TestContext.Current.CancellationToken;
        var character = new Character { Handle = "SnapLock" };
        var sync = new object();

        // Il lock lo tiene un task a parte: in C# non si può fare await dentro un lock
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (sync)
            {
                entered.SetResult();
                release.Wait();
            }
        }, ct);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var save = Task.Run(() => CharacterRepository.SaveCharacter(character, _dir, sync), ct);

        // Finché qualcuno tiene il lock del game la scheda non può essere serializzata
        var first = await Task.WhenAny(save, Task.Delay(300, ct));
        Assert.NotSame(save, first);

        release.Set();
        await save.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await holder;

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
