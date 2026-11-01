using RPG.Common.Models;
using RPG.Server;

namespace RPG.Tests;

public class CharacterMigrationsTests
{
    private static Character Legacy(int max, int current) => new()
    {
        Handle = "legacy",
        SchemaVersion = 0,
        Health = new HealthSystem { MaxHealth = max, CurrentHealth = current }
    };

    [Fact]
    public void V0_HealthMovesTo40Boxes_KeepingAbsoluteDamage()
    {
        var c = Legacy(max: 20, current: 15);   // 5 danni

        bool changed = CharacterMigrations.Migrate(c);

        Assert.True(changed);
        Assert.Equal(CharacterMigrations.CurrentVersion, c.SchemaVersion);
        Assert.Equal(40, c.Health.MaxHealth);
        Assert.Equal(35, c.Health.CurrentHealth);
    }

    [Fact]
    public void V0_CurrentAboveMax_MeansNoDamage()
    {
        var c = Legacy(max: 20, current: 25);

        CharacterMigrations.Migrate(c);

        Assert.Equal(40, c.Health.CurrentHealth);
    }

    [Fact]
    public void CurrentVersion_IsLeftUntouched()
    {
        var c = Legacy(max: 20, current: 15);
        c.SchemaVersion = CharacterMigrations.CurrentVersion;

        bool changed = CharacterMigrations.Migrate(c);

        Assert.False(changed);
        Assert.Equal(20, c.Health.MaxHealth);
        Assert.Equal(15, c.Health.CurrentHealth);
    }

    [Fact]
    public void NewerSchema_IsRejected()
    {
        var c = Legacy(40, 40);
        c.SchemaVersion = CharacterMigrations.CurrentVersion + 1;

        Assert.Throws<CharacterLoadException>(() => CharacterMigrations.Migrate(c));
    }
}
