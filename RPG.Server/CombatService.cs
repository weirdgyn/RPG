using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.Utils;
using RPG.Common.World;

namespace RPG.Server;

/// <summary>
/// Regole del combattimento senza rete né I/O. Lavora su un <see cref="GameExecutionScope"/>, cioè sotto
/// <see cref="Game.SyncRoot"/>: validazione, consumo dell'azione, tiri ed effetti sul bersaglio avvengono
/// nella stessa sezione critica, e i personaggi modificati sono segnati per il salvataggio.
/// </summary>
public static class CombatService
{
    /// <summary>
    /// Esito di un attacco. Con Error valorizzato l'attacco non è avvenuto (e l'azione è restituita);
    /// altrimenti Result è il risultato per tutto il game e LogLine la riga di log del server.
    /// </summary>
    public sealed record AttackOutcome(string? Error, AttackResponsePayload? Result, string? LogLine, bool AttackerChanged = false);

    // Pila di munizioni da cui provengono i colpi attualmente nel caricatore (null = standard)
    public static Ammo? FindLoadedAmmo(Character owner, Weapon weapon) =>
        string.IsNullOrEmpty(weapon.LoadedAmmoId)
            ? null
            : owner.Ammos.FirstOrDefault(a => a.Id.Equals(weapon.LoadedAmmoId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Attacco di <paramref name="attacker"/> contro <paramref name="target"/>: controlla attore, bersaglio e turno
    /// (consumando l'azione), risolve il colpo e segna come modificati i personaggi toccati.
    /// </summary>
    public static AttackOutcome Attack(GameExecutionScope scope, Character attacker, Character target, string? weaponKey)
    {
        var game = scope.Game;

        string? denial = scope.CheckActor(attacker);
        if (denial == null && !scope.IsInGame(target)) denial = $"{target.Handle} non è più in questo game.";
        if (denial == null && target.Health.IsDead) denial = $"{target.Handle} è già morto.";
        denial ??= scope.CheckTurn(attacker, needsAction: true);

        if (denial != null)
        {
            return new AttackOutcome(denial, null, null);
        }

        var res = ResolveAttackRules(game, attacker, target, weaponKey, game.CombatActive);
        if (res.Error != null)
        {
            scope.RefundAction();
        }
        else
        {
            scope.CountFreeAction();
            if (res.AttackerChanged) scope.MarkDirty(attacker);
            if (res.Result!.IsHit) scope.MarkDirty(target);
        }

        return res;
    }

    // Regole dell'attacco, sincrone e sotto Game.SyncRoot (lo tiene lo scope di chiamata).
    private static AttackOutcome ResolveAttackRules(Game game, Character attacker, Character target, string? weaponKey, bool combatActive)
    {
        Weapon? weaponToUse = null;
        if (!string.IsNullOrWhiteSpace(weaponKey))
        {
            string key = weaponKey.Trim();
            weaponToUse = attacker.Weapons.FirstOrDefault(w =>
                w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                w.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                w.SkillName.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        weaponToUse ??= attacker.EquippedWeapon ?? attacker.Weapons.FirstOrDefault();
        if (weaponToUse == null)
        {
            return new AttackOutcome("Nessuna arma o abilità d'attacco disponibile!", null, null);
        }

        if (weaponToUse.Type == Weapon.WeaponType.Area)
        {
            return new AttackOutcome($"{weaponToUse.Name} è un'arma ad area: usa /throw <X> <Y> [Z] [arma].", null, null);
        }

        string skillName = weaponToUse.SkillName;
        if (!Skills.TryGetStatForSkill(skillName, out string statName))
        {
            return new AttackOutcome($"L'abilità '{skillName}' di {weaponToUse.Name} non è riconosciuta: scheda o arma non valida.", null, null);
        }

        int attackerStat = attacker.Stats.GetStatValue(statName);
        int attackerSkillLvl = attacker.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
        int attackerPenalty = attacker.Health.GetWoundPenalty();

        double distance = CombatRules.CalculateDistance(attacker.Position, target.Position);
        int targetNumber = 15;
        string attackTypeDesc = string.Empty;
        string tnText = "TN 15";

        Ammo? loadedAmmo = null;
        bool ammoSpent = false;

        if (weaponToUse.Type == Weapon.WeaponType.Ranged)
        {
            if (weaponToUse.UsesAmmo && weaponToUse.CurrentAmmo <= 0)
            {
                return new AttackOutcome($"{weaponToUse.Name} ha il caricatore vuoto: usa /reload.", null, null);
            }

            int? rangedTn = CombatRules.GetRangedTargetNumber(distance, weaponToUse.OptimalRangeMeters);
            if (rangedTn == null)
            {
                return new AttackOutcome($"Bersaglio fuori portata: {distance:F1}m, la gittata massima di {weaponToUse.Name} è {weaponToUse.OptimalRangeMeters * 2}m.", null, null);
            }

            targetNumber = rangedTn.Value;
            attackTypeDesc = $"A Distanza con {weaponToUse.Name} ({distance:F1}m, gittata {weaponToUse.OptimalRangeMeters}m) -> TN: {targetNumber}";
            tnText = $"TN {targetNumber} (distanza {distance:F1} m, gittata {weaponToUse.OptimalRangeMeters} m)";

            if (weaponToUse.UsesAmmo)
            {
                loadedAmmo = FindLoadedAmmo(attacker, weaponToUse);
                weaponToUse.CurrentAmmo--;
                ammoSpent = true;
            }
        }
        else if (weaponToUse.Type == Weapon.WeaponType.Melee)
        {
            if (distance > CombatRules.MeleeMaxDistanceMeters)
            {
                return new AttackOutcome($"Troppo lontano per la mischia con {weaponToUse.Name}! Distanza: {distance:F1}m > {CombatRules.MeleeMaxDistanceMeters}m.", null, null);
            }

            int targetRef = target.Stats.REF;
            int targetDodge = target.Skills.TryGetValue("Schivare", out int dLvl) ? dLvl : 0;
            int targetPenalty = target.Health.GetWoundPenalty();

            targetNumber = CombatRules.GetMeleeTargetNumber(targetRef, targetDodge, targetPenalty);
            attackTypeDesc = $"In Mischia con {weaponToUse.Name} -> Difesa Bersaglio (REF {targetRef} + Schivare {targetDodge}): TN {targetNumber}";
            var tnParts = new List<(string, int, string?)> { ("base", 10, null), ("REF", targetRef, null), ("Schivare", targetDodge, null) };
            if (targetPenalty != 0) tnParts.Add(("ferite", targetPenalty, null));
            tnText = $"difesa di {target.Handle}: {RollFormatter.FormatSum(tnParts)} (distanza {distance:F1} m)";
        }

        var attackRoll = GameDice.RollExplodingD10Detailed();
        int diceVal = attackRoll.Total;
        int weaponModifier = weaponToUse.GetAttackModifier();
        var hitCtx = new EffectContext(RollKind.Hit, statName, skillName);
        var hitTerms = EffectService.CollectTerms(game, attacker, hitCtx);
        int effectsHit = hitTerms.Sum(t => t.Value);
        var expiredOnHit = EffectRules.ConsumeUses(game.Effects, attacker.Handle, hitCtx);
        var toHit = CombatRules.ResolveToHit(attackRoll, attackerStat, attackerSkillLvl, attackerPenalty, weaponModifier, effectsHit, targetNumber);
        int attackTotal = toHit.Total;

        var steps = new List<string>();
        var hitParts = new List<(string, int, string?)>
        {
            ("d10", diceVal, RollFormatter.DiceExtra(attackRoll)),
            (statName, attackerStat, null),
            (skillName, attackerSkillLvl, null)
        };
        if (weaponModifier != 0) hitParts.Add(("arma", weaponModifier, null));
        if (attackerPenalty != 0) hitParts.Add(("ferite", attackerPenalty, null));
        EffectService.AddTermParts(hitParts, hitTerms);
        steps.Add($"Tiro per colpire: {RollFormatter.FormatSum(hitParts)}");
        steps.AddRange(EffectService.DescribeExpired(expiredOnHit));
        steps.Add($"Difficoltà: {tnText}");

        // Un 1 naturale sul primo dado è un fallimento critico: manca comunque (il colpo sparato è perso)
        bool isFumble = toHit.IsFumble;
        bool isHit = toHit.IsHit;
        steps.Add(isFumble
            ? "Esito: FALLIMENTO CRITICO (1 naturale sul d10)"
            : isHit ? $"Esito: COLPITO ({attackTotal} contro TN {targetNumber})" : $"Esito: MANCATO ({attackTotal} contro TN {targetNumber})");

        int damageDealt = 0;
        string hitLocationName = string.Empty;
        int hitMultiplier = 1;
        CombatRules.ArmorResult? armorResult = null;
        int btmReduction = 0;
        int healthBefore = target.Health.CurrentHealth;

        if (isHit)
        {
            var damageRoll = GameDice.Roll(weaponToUse.DamageDice);
            int diceDamage = (int)damageRoll.Total;
            int ammoDamage = loadedAmmo?.DamageModifier ?? 0;
            var damageCtx = new EffectContext(RollKind.Damage);
            var damageTerms = EffectService.CollectTerms(game, attacker, damageCtx);
            int effectsDamage = damageTerms.Sum(t => t.Value);
            var expiredOnDamage = EffectRules.ConsumeUses(game.Effects, attacker.Handle, damageCtx);
            int rawDamage = Math.Max(0, diceDamage + ammoDamage + effectsDamage);

            // Locazione d'impatto e armatura di quella zona
            int locRoll = (int)GameDice.Roll("1d10").Total;
            var loc = CombatRules.ResolveHitLocation(locRoll);
            hitLocationName = loc.Name;
            hitMultiplier = loc.DamageMultiplier;
            steps.Add($"Locazione: d10 {locRoll} -> {loc.Name}{(loc.DamageMultiplier > 1 ? $" (danno x{loc.DamageMultiplier} dopo l'armatura)" : "")}");

            steps.Add($"Dadi di danno ({weaponToUse.DamageDice}): {RollFormatter.FormatDiceDetail(damageRoll)}");
            if (ammoDamage != 0 || damageTerms.Count > 0)
            {
                var dmgParts = new List<(string, int, string?)> { ("dadi", diceDamage, null) };
                if (ammoDamage != 0) dmgParts.Add(("munizioni", ammoDamage, null));
                EffectService.AddTermParts(dmgParts, damageTerms);
                steps.Add($"Bonus al danno: {RollFormatter.FormatSum(dmgParts)}");
                steps.AddRange(EffectService.DescribeExpired(expiredOnDamage));
            }

            var armorPiece = target.Armor.GetPiece(loc.ArmorLocation);
            int spBefore = armorPiece?.CurrentStopPower ?? 0;
            bool armorPiercing = loadedAmmo?.HalvesArmorSP == true;

            var resolution = CombatRules.ResolveDamage(armorPiece, rawDamage, loc.DamageMultiplier, armorPiercing, target.Stats.BODY);
            armorResult = resolution.Armor;
            int penetrating = resolution.Penetrating;

            if (armorResult.SpFaced > 0)
            {
                string spText = armorPiercing ? $"SP {spBefore} dimezzato dalle munizioni perforanti = {armorResult.SpFaced}" : $"SP {armorResult.SpFaced}";
                steps.Add($"Armatura ({armorPiece?.Name}, {spText}): {rawDamage} - {armorResult.SpFaced} = {penetrating}{(armorResult.Ablated ? " (SP dell'armatura -1)" : " (l'armatura regge)")}");
            }
            else
            {
                steps.Add($"Armatura: nessuna protezione su {loc.Name} ({rawDamage} danni passano)");
            }

            if (loc.DamageMultiplier > 1 && penetrating > 0)
            {
                steps.Add($"Moltiplicatore {loc.Name} x{loc.DamageMultiplier}: {penetrating} -> {armorResult.DamageDealt}");
            }

            btmReduction = resolution.BtmReduction;
            damageDealt = resolution.Dealt;
            if (armorResult.DamageDealt > 0)
            {
                steps.Add($"BTM (BODY {target.Stats.BODY}): {armorResult.DamageDealt} - {btmReduction} = {damageDealt}{(armorResult.DamageDealt - btmReduction < 1 ? " (minimo 1)" : "")}");
            }

            target.Health.TakeDamage(damageDealt);
            steps.Add($"Danno inflitto: {damageDealt}");
            steps.Add($"Salute di {target.Handle}: {healthBefore} -> {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()})");

            if (combatActive && damageDealt > 0)
            {
                var stunLine = RollService.RollStunSave(target);
                if (stunLine != null) steps.Add(stunLine);
            }
        }

        string log = $"[COMBAT] {attacker.Handle} attacca {target.Handle} con '{weaponToUse.Name}'. {attackTypeDesc}. "
            + string.Join(" | ", steps)
            + (weaponToUse.UsesAmmo ? $" | Colpi: {weaponToUse.CurrentAmmo}/{weaponToUse.MagazineCapacity}" : "");

        var respPayload = new AttackResponsePayload
        {
            AttackerHandle = attacker.Handle,
            TargetHandle = target.Handle,
            AttackTotal = attackTotal,
            DefenseTN = targetNumber,
            IsHit = isHit,
            IsFumble = isFumble,
            DamageDealt = damageDealt,
            HitLocation = hitLocationName,
            DamageMultiplier = hitMultiplier,
            ArmorSP = armorResult?.SpFaced ?? 0,
            ArmorAbsorbed = armorResult?.Absorbed ?? 0,
            ArmorAblated = armorResult?.Ablated ?? false,
            BtmReduction = armorResult != null ? armorResult.DamageDealt - damageDealt : 0,
            TargetRemainingHealth = target.Health.RemainingBoxes,
            TargetMaxHealth = target.Health.MaxHealth,
            TargetWoundStatus = target.Health.GetWoundStatusDescription(),
            WeaponName = weaponToUse.Name,
            Steps = steps,
            AmmoLeft = weaponToUse.UsesAmmo ? weaponToUse.CurrentAmmo : 0,
            AmmoCapacity = weaponToUse.UsesAmmo ? weaponToUse.MagazineCapacity : 0,
            AmmoName = weaponToUse.UsesAmmo ? (loadedAmmo?.Name ?? "Munizioni standard") : string.Empty,
            Message = isHit
                ? (damageDealt > 0
                    ? $"COLPO A SEGNO: {damageDealt} danni a {target.Handle}."
                    : $"COLPO A SEGNO, MA NESSUN DANNO: l'armatura regge.")
                : isFumble
                    ? $"FALLIMENTO CRITICO di {attacker.Handle}."
                    : $"MANCATO da {attacker.Handle}."
        };

        return new AttackOutcome(null, respPayload, log, ammoSpent);
    }

    /// <summary>
    /// Esito di un lancio. Con Error valorizzato il lancio non è avvenuto (e l'azione è restituita);
    /// altrimenti Result è l'esito per tutto il game e Changed i personaggi da salvare.
    /// </summary>
    public sealed record ThrowOutcome(string? Error, AreaResultPayload? Result, string? LogLine, List<Character> Changed);

    /// <summary>
    /// Lancio di un'arma ad area di <paramref name="thrower"/> verso un punto. Colpisce tutti i personaggi del game
    /// (giocatori e NPC, lanciatore compreso) entro il raggio dell'esplosione: i bersagli sono i partecipanti del
    /// momento, letti dentro la stessa sezione critica.
    /// </summary>
    public static ThrowOutcome Throw(GameExecutionScope scope, Character thrower, Position3D aimed, string? weaponKey)
    {
        var game = scope.Game;

        string? denial = scope.CheckActor(thrower) ?? scope.CheckTurn(thrower, needsAction: true);
        if (denial != null)
        {
            return new ThrowOutcome(denial, null, null, []);
        }

        var res = ResolveThrowRules(game, thrower, aimed, weaponKey, scope.GetParticipants(), game.CombatActive);
        if (res.Error != null)
        {
            scope.RefundAction();
        }
        else
        {
            scope.MarkDirty(res.Changed);
        }

        return res;
    }

    // Regole del lancio/esplosione, sincrone e sotto Game.SyncRoot (lo tiene lo scope di chiamata).
    private static ThrowOutcome ResolveThrowRules(Game game, Character thrower, Position3D aimed, string? weaponKey, List<Character> everyone, bool combatActive)
    {
        static ThrowOutcome Error(string message) => new(message, null, null, []);

        Weapon? weapon;
        if (!string.IsNullOrWhiteSpace(weaponKey))
        {
            string key = weaponKey.Trim();
            weapon = thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area &&
                (w.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || w.Id.Equals(key, StringComparison.OrdinalIgnoreCase)));
            if (weapon == null) return Error($"Nessuna arma ad area '{key}' nell'inventario.");
        }
        else
        {
            weapon = thrower.EquippedWeapon is { Type: Weapon.WeaponType.Area, CurrentAmmo: > 0 }
                ? thrower.EquippedWeapon
                : thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area && w.CurrentAmmo > 0)
                  ?? thrower.Weapons.FirstOrDefault(w => w.Type == Weapon.WeaponType.Area);
            if (weapon == null) return Error("Non hai armi ad area da lanciare.");
        }

        if (weapon.CurrentAmmo <= 0) return Error($"{weapon.Name}: pezzi esauriti.");

        double radius = weapon.BlastRadiusMeters ?? 0;
        if (radius <= 0) return Error($"{weapon.Name} non ha un raggio d'esplosione valido.");

        int penalty = thrower.Health.GetWoundPenalty();

        if (!Skills.TryGetStatForSkill(weapon.ScatterSkillName, out string statName))
        {
            return Error($"L'abilità '{weapon.ScatterSkillName}' di {weapon.Name} non è riconosciuta: scheda o arma non valida.");
        }

        double throwRange = weapon.OptimalRangeMeters > 0 ? weapon.OptimalRangeMeters : 15;
        double distance = CombatRules.CalculateDistance(thrower.Position, aimed);
        if (distance > throwRange)
        {
            return Error($"Troppo lontano per {weapon.Name}: {distance:F1}m, gittata di lancio {throwRange:F0}m.");
        }

        int throwTn = CombatRules.GetRangedTargetNumber(distance, throwRange) ?? 30;
        int statVal = thrower.Stats.GetStatValue(statName);
        int skillLvl = thrower.Skills.TryGetValue(weapon.ScatterSkillName, out int lvl) ? lvl : 0;
        var roll = GameDice.RollExplodingD10Detailed();
        // Un lancio è una prova di abilità: valgono ALL, STAT e SKILL ma non HIT (che è del tiro per colpire)
        var throwCtx = new EffectContext(RollKind.Skill, statName, weapon.ScatterSkillName);
        var throwTerms = EffectService.CollectTerms(game, thrower, throwCtx);
        var expiredOnThrow = EffectRules.ConsumeUses(game.Effects, thrower.Handle, throwCtx);
        int total = statVal + skillLvl + penalty + throwTerms.Sum(t => t.Value) + roll.Total;

        bool onTarget = !roll.IsFumble && total >= throwTn;
        weapon.CurrentAmmo--;

        var throwSteps = new List<string>();
        var throwParts = new List<(string, int, string?)>
        {
            ("d10", roll.Total, RollFormatter.DiceExtra(roll)),
            (statName, statVal, null),
            (weapon.ScatterSkillName, skillLvl, null)
        };
        if (penalty != 0) throwParts.Add(("ferite", penalty, null));
        EffectService.AddTermParts(throwParts, throwTerms);
        throwSteps.Add($"Tiro di lancio: {RollFormatter.FormatSum(throwParts)}");
        throwSteps.AddRange(EffectService.DescribeExpired(expiredOnThrow));
        throwSteps.Add($"Difficoltà: TN {throwTn} (distanza {distance:F1} m, gittata di lancio {throwRange:F0} m)");
        throwSteps.Add(roll.IsFumble
            ? "Esito: FALLIMENTO CRITICO (1 naturale sul d10)"
            : onTarget ? $"Esito: A SEGNO ({total} contro TN {throwTn})" : $"Esito: FUORI BERSAGLIO ({total} contro TN {throwTn})");

        // Se il lancio fallisce la granata devia: d10 metri in direzione casuale (doppio con un fallimento critico)
        var landed = new Position3D { X = aimed.X, Y = aimed.Y, Z = aimed.Z };
        double deviation = 0;
        if (!onTarget)
        {
            int deviationRoll = (int)GameDice.Roll("1d10").Total;
            deviation = deviationRoll * (roll.IsFumble ? 2 : 1);
            double angle = Random.Shared.NextDouble() * 2 * Math.PI;
            landed.X += (int)Math.Round(deviation * Math.Cos(angle));
            landed.Y += (int)Math.Round(deviation * Math.Sin(angle));
            throwSteps.Add($"Deviazione: d10 {deviationRoll}{(roll.IsFumble ? " x2 (fallimento critico)" : "")} = {deviation:F0} m in direzione casuale");
        }
        throwSteps.Add($"Esplosione in ({landed.X}, {landed.Y}, {landed.Z}), raggio {radius:0.#} m");

        // Un solo tiro di danno per fascia: tutti quelli nella stessa fascia subiscono lo stesso danno
        var bandRolls = new Dictionary<string, (int Raw, string Text)>();
        var victims = new List<AreaVictim>();
        var changed = new List<Character> { thrower };

        foreach (var target in everyone)
        {
            if (target.Health.IsDead) continue;   // i morti non subiscono altro danno

            double d = CombatRules.CalculateDistance(target.Position, landed);
            if (d > radius) continue;

            string? dice;
            string bandKey;
            string bandLabel;
            if (weapon.AreaDamageBands is { Count: > 0 })
            {
                var band = weapon.AreaDamageBands.FirstOrDefault(b => d >= b.FromMeters && d < b.ToMeters);
                if (band == null) continue;
                dice = band.DamageDice;
                bandKey = $"{band.FromMeters}-{band.ToMeters}";
                bandLabel = $"fascia {band.FromMeters:0.#}-{band.ToMeters:0.#} m";
            }
            else
            {
                dice = weapon.DamageDice;
                bandKey = "all";
                bandLabel = "tutto il raggio";
            }

            var victimSteps = new List<string> { $"Distanza dal centro: {d:F1} m ({bandLabel})" };

            if (!bandRolls.TryGetValue(bandKey, out var bandRoll))
            {
                var dmgRoll = GameDice.Roll(dice);
                bandRoll = (Math.Max(0, (int)dmgRoll.Total), $"Dadi di danno ({dice}): {RollFormatter.FormatDiceDetail(dmgRoll)}");
                bandRolls[bandKey] = bandRoll;
                victimSteps.Add(bandRoll.Text);
            }
            else
            {
                victimSteps.Add($"Dadi di danno ({dice}): stessi dadi della {bandLabel} = {bandRoll.Raw}");
            }
            int rawDamage = bandRoll.Raw;

            // L'esplosione colpisce il corpo: protegge l'armatura del torso, nessun moltiplicatore di locazione
            var torso = target.Armor.GetPiece(ArmorLocation.Torso);
            var resolution = CombatRules.ResolveDamage(torso, rawDamage, 1, armorPiercing: false, target.Stats.BODY);
            var armor = resolution.Armor;
            int penetrating = resolution.Penetrating;
            int btmRed = resolution.BtmReduction;
            int afterBtm = resolution.Dealt;
            int healthBefore = target.Health.RemainingBoxes;
            target.Health.TakeDamage(afterBtm);
            if (!changed.Contains(target)) changed.Add(target);

            if (armor.SpFaced > 0)
            {
                victimSteps.Add($"Armatura (torso, SP {armor.SpFaced}): {rawDamage} - {armor.SpFaced} = {penetrating}{(armor.Ablated ? " (SP dell'armatura -1)" : " (l'armatura regge)")}");
            }
            else
            {
                victimSteps.Add($"Armatura: nessuna protezione sul torso ({rawDamage} danni passano)");
            }
            if (armor.DamageDealt > 0)
            {
                victimSteps.Add($"BTM (BODY {target.Stats.BODY}): {armor.DamageDealt} - {btmRed} = {afterBtm}{(armor.DamageDealt - btmRed < 1 ? " (minimo 1)" : "")}");
            }
            victimSteps.Add($"Danno inflitto: {afterBtm}");
            victimSteps.Add($"Salute: {healthBefore} -> {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()})");
            if (combatActive && afterBtm > 0)
            {
                var stunLine = RollService.RollStunSave(target);
                if (stunLine != null) victimSteps.Add(stunLine);
            }

            victims.Add(new AreaVictim
            {
                Handle = target.Handle,
                Distance = Math.Round(d, 1),
                RawDamage = rawDamage,
                ArmorSP = armor.SpFaced,
                DamageDealt = afterBtm,
                BtmReduction = armor.DamageDealt - afterBtm,
                RemainingHealth = target.Health.RemainingBoxes,
                MaxHealth = target.Health.MaxHealth,
                WoundStatus = target.Health.GetWoundStatusDescription(),
                Steps = victimSteps
            });
        }

        string aimText = $"({aimed.X}, {aimed.Y}, {aimed.Z})";
        string landText = $"({landed.X}, {landed.Y}, {landed.Z})";
        string message = onTarget
            ? $"{thrower.Handle} lancia {weapon.Name} su {aimText}: esplode nel punto indicato."
            : roll.IsFumble
                ? $"FALLIMENTO CRITICO! La granata di {thrower.Handle} devia di {deviation:F0}m: esplode in {landText}."
                : $"La granata di {thrower.Handle} devia di {deviation:F0}m: esplode in {landText}.";

        var result = new AreaResultPayload
        {
            ThrowerHandle = thrower.Handle,
            WeaponName = weapon.Name,
            ThrowTotal = total,
            ThrowTN = throwTn,
            IsFumble = roll.IsFumble,
            OnTarget = onTarget,
            DeviationMeters = deviation,
            Aimed = aimed,
            Landed = landed,
            BlastRadius = radius,
            Steps = throwSteps,
            PiecesLeft = weapon.CurrentAmmo,
            Victims = victims,
            Message = message
        };

        string log = $"[COMBAT] {thrower.Handle} lancia '{weapon.Name}' su {aimText}. {string.Join(" | ", throwSteps)}"
            + $" | Colpiti: {(victims.Count > 0 ? string.Join(", ", victims.Select(v => $"{v.Handle} -{v.DamageDealt}")) : "nessuno")} | Pezzi rimasti: {weapon.CurrentAmmo}";

        return new ThrowOutcome(null, result, log, changed);
    }

    /// <summary>
    /// Ricarica nel game: controlla attore e turno (consumando l'azione), ricarica e segna la scheda da salvare.
    /// Se la ricarica non è possibile l'azione è restituita.
    /// </summary>
    public static (string? Error, string? Message) Reload(GameExecutionScope scope, Character actor, string ammoKey, string weaponKey)
    {
        string? denial = scope.CheckActor(actor) ?? scope.CheckTurn(actor, needsAction: true);
        if (denial != null) return (denial, null);

        var (err, msg) = ReloadRules(actor, ammoKey, weaponKey);
        if (err != null)
        {
            scope.RefundAction();
            return (err, null);
        }

        scope.MarkDirty(actor);
        return (null, msg);
    }

    // Regole della ricarica, sincrone e sotto Game.SyncRoot. I colpi rimasti nel caricatore tornano nella
    // pila di provenienza, poi si caricano fino a capienza dalla pila scelta.
    public static (string? Error, string? Message) ReloadRules(Character actor, string ammoKey, string weaponKey)
    {
        Weapon? weapon = weaponKey.Length == 0
            ? actor.EquippedWeapon
            : actor.Weapons.FirstOrDefault(w =>
                w.Name.Equals(weaponKey, StringComparison.OrdinalIgnoreCase) ||
                w.Id.Equals(weaponKey, StringComparison.OrdinalIgnoreCase));

        if (weapon == null || !weapon.UsesAmmo || string.IsNullOrWhiteSpace(weapon.Caliber))
        {
            return ("Quest'arma non usa munizioni ricaricabili.", null);
        }

        string caliber = weapon.Caliber;
        bool SameCaliber(Ammo a) => a.Caliber.Equals(caliber, StringComparison.OrdinalIgnoreCase);

        // Pila da cui vengono i colpi già nel caricatore (senza indicazione: munizioni standard)
        Ammo? loadedStack = FindLoadedAmmo(actor, weapon)
            ?? actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Effect == AmmoEffect.Standard);

        Ammo? chosen;
        if (ammoKey.Length > 0)
        {
            // Prima l'Id esatto, poi una corrispondenza parziale su Id o nome (es. "ap", "perforanti")
            chosen = actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Id.Equals(ammoKey, StringComparison.OrdinalIgnoreCase))
                ?? actor.Ammos.FirstOrDefault(a => SameCaliber(a) &&
                    (a.Id.Contains(ammoKey, StringComparison.OrdinalIgnoreCase) ||
                     a.Name.Contains(ammoKey, StringComparison.OrdinalIgnoreCase)));
            if (chosen == null)
            {
                return ($"Nessuna munizione '{ammoKey}' compatibile con {weapon.Name} ({caliber}).", null);
            }
        }
        else
        {
            chosen = loadedStack is { Quantity: > 0 }
                ? loadedStack
                : actor.Ammos.FirstOrDefault(a => SameCaliber(a) && a.Quantity > 0) ?? loadedStack;
        }

        if (chosen == null)
        {
            return ($"Nessuna munizione {caliber} nell'inventario.", null);
        }

        bool sameStack = ReferenceEquals(chosen, loadedStack);
        if (sameStack && weapon.CurrentAmmo >= weapon.MagazineCapacity)
        {
            return ($"{weapon.Name} ha già il caricatore pieno.", null);
        }

        if (chosen.Quantity <= 0)
        {
            return ($"Munizioni esaurite: '{chosen.Name}'.", null);
        }

        // Scarico il caricatore nella pila di provenienza (creandola se mancava)...
        if (weapon.CurrentAmmo > 0)
        {
            if (loadedStack == null)
            {
                loadedStack = new Ammo
                {
                    Id = $"ammo_{caliber}_std".Replace(' ', '_'),
                    Name = $"Munizioni {caliber} standard",
                    Caliber = caliber,
                    Effect = AmmoEffect.Standard
                };
                actor.Ammos.Add(loadedStack);
            }
            loadedStack.Quantity += weapon.CurrentAmmo;
        }

        // ...e carico fino a capienza dalla pila scelta
        int loaded = Math.Min(weapon.MagazineCapacity, chosen.Quantity);
        chosen.Quantity -= loaded;
        weapon.CurrentAmmo = loaded;
        weapon.LoadedAmmoId = chosen.Id;

        return (null, $"{actor.Handle} ricarica {weapon.Name} con {chosen.Name}: {loaded}/{weapon.MagazineCapacity} colpi (in riserva: {chosen.Quantity}).");
    }
}
