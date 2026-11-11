using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Common.Protocol;
using RPG.Common.Utils;

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
}
