using System.Text.Json;
using RPG.Common.Models;
using Xunit;

namespace RPG.Tests;

public class GameLockTests
{
    [Fact]
    public void SyncRoot_IsNotSerialized()
    {
        string json = JsonSerializer.Serialize(new Game());

        Assert.DoesNotContain("SyncRoot", json);
    }

    [Fact]
    public void SyncRoot_IsDistinctPerGame()
    {
        var a = new Game();
        var b = new Game();

        Assert.NotSame(a.SyncRoot, b.SyncRoot);
        Assert.Same(a.SyncRoot, a.SyncRoot);
    }
}
