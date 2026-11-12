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

public class CombatServiceThrowReloadTests
{
    private static Weapon Grenade(int pieces = 2) => new()
    {
        Id = "gr", Name = "Granata a frammentazione", Type = Weapon.WeaponType.Area, SkillName = "Atletica",
        DamageDice = "2d6", BlastRadiusMeters = 5, OptimalRangeMeters = 15, ScatterSkillName = "Atletica", CurrentAmmo = pieces
    };

    private static Weapon Pistol(int ammo = 3) => new()
    {
        Id = "p35", Name = "Pistola", Type = Weapon.WeaponType.Ranged, SkillName = "Pistola", Caliber = "10mm",
        MagazineCapacity = 12, CurrentAmmo = ammo
    };

    private static (Game Game, Character Thrower, Character Bystander) Setup(params Weapon[] weapons)
    {
        var thrower = new Character { Handle = "Hero", Position = new Position3D { X = 0 } };
        thrower.Weapons = [.. weapons];
        var bystander = new Character { Handle = "Goon", Position = new Position3D { X = 8 } };
        var game = new Game();
        game.Players.Add(new Player { Username = "mike", Character = thrower });
        game.NPCs.Add(bystander);
        return (game, thrower, bystander);
    }

    private static Position3D At(int x, int y = 0) => new() { X = x, Y = y };

    private static CombatService.ThrowOutcome Throw(Game game, Character thrower, Position3D aimed, string? weapon = null, List<Character>? dirty = null) =>
        GameExecutionContext.Execute(game, s => CombatService.Throw(s, thrower, aimed, weapon), (dirty ?? []).Add);

    [Fact]
    public void Throw_SpendsOnePieceAndReportsConsistentResult()
    {
        var (game, hero, goon) = Setup(Grenade());
        var dirty = new List<Character>();

        var res = Throw(game, hero, At(8), "gr", dirty);

        Assert.Null(res.Error);
        Assert.Equal(1, hero.Weapons[0].CurrentAmmo);
        Assert.Equal(1, res.Result!.PiecesLeft);
        Assert.Contains(hero, dirty);
        Assert.All(res.Result.Victims, v => Assert.True(v.Distance <= res.Result.BlastRadius));
        foreach (var v in res.Result.Victims)
        {
            Assert.Contains(dirty, c => c.Handle == v.Handle);
        }
        Assert.Contains("[COMBAT] Hero lancia", res.LogLine);
    }

    [Fact]
    public void Throw_NoAreaWeapon_IsRefused()
    {
        var (game, hero, _) = Setup(Pistol());

        var res = Throw(game, hero, At(8));

        Assert.Contains("Non hai armi ad area", res.Error);
    }

    [Fact]
    public void Throw_UnknownWeaponKey_IsRefused()
    {
        var (game, hero, _) = Setup(Grenade());

        var res = Throw(game, hero, At(8), "bazooka");

        Assert.Contains("Nessuna arma ad area 'bazooka'", res.Error);
    }

    [Fact]
    public void Throw_OutOfPieces_IsRefusedAndRefunded()
    {
        var (game, hero, _) = Setup(Grenade(pieces: 0));
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });

        var res = Throw(game, hero, At(8), "gr");

        Assert.Contains("pezzi esauriti", res.Error);
        Assert.False(game.TurnActionUsed);
    }

    [Fact]
    public void Throw_TooFar_IsRefusedAndRefunded()
    {
        var (game, hero, _) = Setup(Grenade());
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });

        var res = Throw(game, hero, At(40), "gr");

        Assert.Contains("Troppo lontano", res.Error);
        Assert.False(game.TurnActionUsed);
        Assert.Equal(2, hero.Weapons[0].CurrentAmmo);
    }

    [Fact]
    public void Throw_WrongTurn_IsRefusedBeforeAnythingHappens()
    {
        var (game, hero, _) = Setup(Grenade());
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Goon" });

        var res = Throw(game, hero, At(8), "gr");

        Assert.Contains("Non è il turno", res.Error);
        Assert.Equal(2, hero.Weapons[0].CurrentAmmo);
    }

    [Fact]
    public void Throw_InvalidBlastRadius_IsRefused()
    {
        var weapon = Grenade();
        weapon.BlastRadiusMeters = 0;
        var (game, hero, _) = Setup(weapon);

        var res = Throw(game, hero, At(8), "gr");

        Assert.Contains("raggio d'esplosione", res.Error);
    }

    // ---- ricarica ----

    private static Character Shooter(Weapon weapon, params Ammo[] ammos)
    {
        var c = new Character { Handle = "Hero" };
        c.Weapons = [weapon];
        c.EquippedWeapon = weapon;
        c.Ammos = [.. ammos];
        return c;
    }

    private static Ammo Standard(int qty) => new() { Id = "std", Name = "10mm standard", Caliber = "10mm", Quantity = qty, Effect = AmmoEffect.Standard };

    [Fact]
    public void ReloadRules_RefillsTheMagazineFromTheStack()
    {
        var weapon = Pistol(ammo: 3);
        var hero = Shooter(weapon, Standard(20));

        var (err, msg) = CombatService.ReloadRules(hero, "", "");

        Assert.Null(err);
        Assert.Equal(12, weapon.CurrentAmmo);
        // i 3 colpi rimasti tornano nella pila, poi se ne caricano 12: 20 + 3 - 12
        Assert.Equal(11, hero.Ammos[0].Quantity);
        Assert.Contains("12/12 colpi", msg);
    }

    [Fact]
    public void ReloadRules_FullMagazine_IsRefused()
    {
        var weapon = Pistol(ammo: 12);
        weapon.LoadedAmmoId = "std";
        var hero = Shooter(weapon, Standard(20));

        var (err, _) = CombatService.ReloadRules(hero, "", "");

        Assert.Contains("già il caricatore pieno", err);
        Assert.Equal(20, hero.Ammos[0].Quantity);
    }

    [Fact]
    public void ReloadRules_NoCompatibleAmmo_IsRefused()
    {
        var hero = Shooter(Pistol(ammo: 0));

        var (err, _) = CombatService.ReloadRules(hero, "", "");

        Assert.Contains("Nessuna munizione 10mm", err);
    }

    [Fact]
    public void ReloadRules_UnknownAmmoKey_IsRefused()
    {
        var hero = Shooter(Pistol(ammo: 0), Standard(20));

        var (err, _) = CombatService.ReloadRules(hero, "laser", "");

        Assert.Contains("Nessuna munizione 'laser'", err);
    }

    [Fact]
    public void ReloadRules_WeaponWithoutAmmo_IsRefused()
    {
        var knife = new Weapon { Id = "k", Name = "Coltello", Type = Weapon.WeaponType.Melee };
        var hero = Shooter(knife, Standard(20));

        var (err, _) = CombatService.ReloadRules(hero, "", "");

        Assert.Contains("non usa munizioni ricaricabili", err);
    }

    [Fact]
    public void Reload_InCombat_ConsumesTheAction_AndRefundsOnError()
    {
        var weapon = Pistol(ammo: 0);
        var hero = Shooter(weapon, Standard(20));
        var game = new Game();
        game.Players.Add(new Player { Username = "mike", Character = hero });
        game.CombatActive = true;
        game.TurnOrder.Add(new Game.TurnEntry { Handle = "Hero" });
        var dirty = new List<Character>();

        var ok = GameExecutionContext.Execute(game, s => CombatService.Reload(s, hero, "", ""), dirty.Add);
        Assert.Null(ok.Error);
        Assert.True(game.TurnActionUsed);
        Assert.Equal([hero], dirty);

        game.TurnActionUsed = false;
        weapon.CurrentAmmo = 12;
        weapon.LoadedAmmoId = "std";
        var full = GameExecutionContext.Execute(game, s => CombatService.Reload(s, hero, "", ""));
        Assert.Contains("caricatore pieno", full.Error);
        Assert.False(game.TurnActionUsed);
    }
}
