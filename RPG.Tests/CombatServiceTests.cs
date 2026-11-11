using RPG.Common.Models;
using RPG.Common.World;
using RPG.Server;
using Xunit;

namespace RPG.Tests;

public class CombatServiceTests
{
    private static Weapon Pistol(int ammo = 12) => new()
    {
        Id = "p35", Name = "Pistola Sternmeyer 35", Type = Weapon.WeaponType.Ranged, SkillName = "Pistola",
        DamageDice = "2d6", OptimalRangeMeters = 50, MagazineCapacity = 12, CurrentAmmo = ammo
    };

    private static Weapon Knife() => new()
    {
        Id = "knife", Name = "Coltello", Type = Weapon.WeaponType.Melee, SkillName = "ArtiMarziali",
        DamageDice = "1d6", MagazineCapacity = 0
    };

    private static Weapon Grenade() => new()
    {
        Id = "gr", Name = "Granata", Type = Weapon.WeaponType.Area, SkillName = "Atletica", DamageDice = "2d6"
    };

    private static (Game Game, Character Attacker, Character Target) Setup(params Weapon[] weapons)
    {
        var attacker = new Character { Handle = "Hero", Position = new Position3D() };
        attacker.Weapons = [.. weapons];
        var target = new Character { Handle = "Goon", Position = new Position3D { X = 3 } };
        var game = new Game();
        game.Players.Add(new Player { Username = "mike", Character = attacker });
        game.NPCs.Add(target);
        return (game, attacker, target);
    }

    private static CombatService.AttackOutcome Attack(Game game, Character attacker, Character target, string? weapon, List<Character>? dirty = null) =>
        GameExecutionContext.Execute(game, s => CombatService.Attack(s, attacker, target, weapon), (dirty ?? []).Add);

    [Fact]
    public void Attack_RangedShot_SpendsOneRoundAndReportsEverything()
    {
        var (game, hero, goon) = Setup(Pistol());
        var dirty = new List<Character>();

        var res = Attack(game, hero, goon, "Pistola Sternmeyer 35", dirty);

        Assert.Null(res.Error);
        Assert.NotNull(res.Result);
        Assert.Equal(11, hero.Weapons[0].CurrentAmmo);
        Assert.True(res.AttackerChanged);
        Assert.Contains(hero, dirty);
        Assert.Equal(1, game.FreeActionsCounter);
        Assert.Contains(res.Result!.Steps, s => s.StartsWith("Tiro per colpire:"));
        Assert.Contains("[COMBAT] Hero attacca Goon", res.LogLine);
        Assert.Equal(res.Result.IsHit, dirty.Contains(goon));
    }

    [Fact]
    public void Attack_EmptyMagazine_IsRefusedAndRefundsTheAction()
    {
        var (game, hero, goon) = Setup(Pistol(ammo: 0));
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });
        var dirty = new List<Character>();

        var res = Attack(game, hero, goon, "p35", dirty);

        Assert.Contains("caricatore vuoto", res.Error);
        Assert.False(game.TurnActionUsed);
        Assert.Empty(dirty);
    }

    [Fact]
    public void Attack_InCombat_ConsumesTheTurnAction_AndASecondAttackIsRefused()
    {
        var (game, hero, goon) = Setup(Pistol());
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });

        var first = Attack(game, hero, goon, "p35");
        var second = Attack(game, hero, goon, "p35");

        Assert.Null(first.Error);
        Assert.True(game.TurnActionUsed);
        Assert.Contains("già usato l'azione", second.Error);
        Assert.Equal(11, hero.Weapons[0].CurrentAmmo);
        Assert.Equal(0, game.FreeActionsCounter);
    }

    [Fact]
    public void Attack_WrongTurn_IsRefused()
    {
        var (game, hero, goon) = Setup(Pistol());
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Goon" });

        var res = Attack(game, hero, goon, "p35");

        Assert.Contains("Non è il turno", res.Error);
        Assert.Equal(12, hero.Weapons[0].CurrentAmmo);
    }

    [Fact]
    public void Attack_DeadTarget_IsRefused()
    {
        var (game, hero, goon) = Setup(Pistol());
        goon.Health.Kill();

        var res = Attack(game, hero, goon, "p35");

        Assert.Contains("già morto", res.Error);
        Assert.Equal(12, hero.Weapons[0].CurrentAmmo);
    }

    [Fact]
    public void Attack_TargetNoLongerInGame_IsRefused()
    {
        var (game, hero, _) = Setup(Pistol());
        var stranger = new Character { Handle = "Stranger" };

        var res = Attack(game, hero, stranger, "p35");

        Assert.Contains("non è più in questo game", res.Error);
    }

    [Fact]
    public void Attack_DeadAttacker_IsRefused()
    {
        var (game, hero, goon) = Setup(Pistol());
        hero.Health.Kill();

        var res = Attack(game, hero, goon, "p35");

        Assert.Contains("morto", res.Error);
    }

    [Fact]
    public void Attack_AreaWeapon_PointsToThrow()
    {
        var (game, hero, goon) = Setup(Grenade());

        var res = Attack(game, hero, goon, "Granata");

        Assert.Contains("/throw", res.Error);
    }

    [Fact]
    public void Attack_OutOfRange_IsRefusedAndRefunded()
    {
        var (game, hero, goon) = Setup(Pistol());
        goon.Position = new Position3D { X = 200 };
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });

        var res = Attack(game, hero, goon, "p35");

        Assert.Contains("fuori portata", res.Error);
        Assert.False(game.TurnActionUsed);
        Assert.Equal(12, hero.Weapons[0].CurrentAmmo);
    }

    [Fact]
    public void Attack_MeleeTooFar_IsRefused()
    {
        var (game, hero, goon) = Setup(Knife());
        goon.Position = new Position3D { X = 10 };

        var res = Attack(game, hero, goon, "knife");

        Assert.Contains("Troppo lontano per la mischia", res.Error);
    }

    [Fact]
    public void Attack_MeleeInReach_ResolvesWithoutAmmo()
    {
        var (game, hero, goon) = Setup(Knife());
        goon.Position = new Position3D { X = 1 };

        var res = Attack(game, hero, goon, "ArtiMarziali");

        Assert.Null(res.Error);
        Assert.False(res.AttackerChanged);
        Assert.Contains(res.Result!.Steps, s => s.StartsWith("Difficoltà: difesa di Goon"));
    }

    [Fact]
    public void FindLoadedAmmo_ReturnsTheStackOrNullForStandard()
    {
        var owner = new Character { Handle = "Hero" };
        owner.Ammos = [new Ammo { Id = "ap", Name = "Perforanti" }];

        Assert.Null(CombatService.FindLoadedAmmo(owner, new Weapon { LoadedAmmoId = null }));
        Assert.Equal("Perforanti", CombatService.FindLoadedAmmo(owner, new Weapon { LoadedAmmoId = "AP" })!.Name);
        Assert.Null(CombatService.FindLoadedAmmo(owner, new Weapon { LoadedAmmoId = "altro" }));
    }
}
