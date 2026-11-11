using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Common.Utils;

namespace RPG.Server;

/// <summary>
/// /heal senza rete né I/O: la cura del master e la stabilizzazione di un giocatore (o di un NPC con /as).
/// Lavora su un <see cref="GameExecutionScope"/>, cioè sotto <see cref="Game.SyncRoot"/>: controlli, consumo
/// dell'azione, tiro ed effetto sul ferito avvengono nella stessa sezione critica.
/// </summary>
public static class MedicalService
{
    /// <summary>Massimo di caselle che il master può restituire con un solo comando.</summary>
    public const int MaxHealAmount = 1000;

    // Abilità mediche valide per la stabilizzazione: vale il punteggio migliore fra Pronto Soccorso e
    // Tecnomedicina ("MedicalTech" è l'alias inglese storico della stessa abilità).
    public static readonly string[] HealSkills = ["Pronto Soccorso", "Tecnomedicina", "MedicalTech"];

    public static int HealSkillLevel(Character c) => MedicalRules.BestSkillLevel(c.Skills, HealSkills);

    /// <summary>Esito della cura del master: se Error è null, Message è l'annuncio.</summary>
    public sealed record MasterHealOutcome(string? Error, string Message);

    /// <summary>
    /// Cura del master, gratuita e senza tiro. Senza <paramref name="amount"/> cura completamente e azzera morte,
    /// stordimento e stabilizzazione; con <paramref name="amount"/> restituisce solo quelle caselle.
    /// </summary>
    public static MasterHealOutcome MasterHeal(GameExecutionScope scope, string targetHandle, int? amount)
    {
        if (amount is <= 0 or > MaxHealAmount)
        {
            return new MasterHealOutcome($"Le caselle da restituire devono essere tra 1 e {MaxHealAmount}.", string.Empty);
        }

        var target = scope.FindCharacter(targetHandle);
        if (target == null) return new MasterHealOutcome($"Personaggio '{targetHandle}' non trovato in questo game.", string.Empty);

        string text;
        if (amount == null)
        {
            target.Health.FullHeal();
            text = $"Il master cura completamente {target.Handle}: salute {target.Health.RemainingBoxes}/{target.Health.MaxHealth}.";
        }
        else
        {
            target.Health.Heal(amount.Value);
            text = $"Il master restituisce {amount} caselle a {target.Handle}: salute {target.Health.RemainingBoxes}/{target.Health.MaxHealth} ({target.Health.GetWoundStatusDescription()}).";
        }

        scope.MarkDirty(target);
        return new MasterHealOutcome(null, text);
    }

    /// <summary>
    /// Esito di una stabilizzazione. Con Error valorizzato il tentativo non è avvenuto (l'azione non è consumata);
    /// altrimenti Success dice se il ferito è stato stabilizzato e Calc è il calcolo del tiro.
    /// </summary>
    public sealed record StabilizeOutcome(string? Error, Character? Target, bool Success, int Total, int TargetNumber, bool IsFumble, string Calc)
    {
        public static StabilizeOutcome Fail(string error) => new(error, null, false, 0, 0, false, string.Empty);
    }

    /// <summary>
    /// Stabilizzazione di <paramref name="targetHandle"/> da parte di <paramref name="healer"/>: solo se il ferito è
    /// in Mortale, entro 2,5 m, con l'azione del turno. Un tentativo fallito costa comunque l'azione.
    /// </summary>
    public static StabilizeOutcome Stabilize(GameExecutionScope scope, Character healer, string targetHandle)
    {
        var game = scope.Game;

        string? err = scope.CheckActor(healer);
        var target = scope.FindCharacter(targetHandle);
        if (err == null && target == null) err = $"Personaggio '{targetHandle}' non trovato in questo game.";

        if (err == null)
        {
            var block = MedicalRules.GetStabilizeBlock(target!.Health);
            if (block != MedicalRules.StabilizeBlock.None)
            {
                err = block switch
                {
                    MedicalRules.StabilizeBlock.Dead => $"{target.Handle} è morto: solo il master può rianimarlo.",
                    MedicalRules.StabilizeBlock.NotMortal => $"{target.Handle} non è in Mortale ({target.Health.GetWoundStatusDescription()}): non c'è nulla da stabilizzare.",
                    _ => $"{target.Handle} è già stabilizzato."
                };
            }
        }

        if (err == null)
        {
            double distance = CombatRules.CalculateDistance(healer.Position, target!.Position);
            if (distance > MedicalRules.HealRangeMeters)
            {
                err = $"{target.Handle} è troppo lontano per essere curato da {healer.Handle}: {distance:F1} m, serve essere a {MedicalRules.HealRangeMeters} m o meno.";
            }
        }

        err ??= scope.CheckTurn(healer, needsAction: true);
        if (err != null) return StabilizeOutcome.Fail(err);

        scope.CountFreeAction();

        int skillLvl = HealSkillLevel(healer);
        string healSkill = HealSkills.OrderByDescending(n => healer.Skills.TryGetValue(n, out int l) ? l : 0).First();
        int tech = healer.Stats.GetStatValue("TECH");
        int penalty = healer.Health.GetWoundPenalty();

        // Una cura è una prova di abilità: valgono ALL, STAT:TECH e SKILL:<abilità usata>
        var healCtx = new EffectContext(RollKind.Skill, "TECH", healSkill);
        var healTerms = EffectService.CollectTerms(game, healer, healCtx);
        var healExpired = EffectRules.ConsumeUses(game.Effects, healer.Handle, healCtx);
        var roll = GameDice.RollExplodingD10Detailed();
        var stab = MedicalRules.ResolveStabilization(roll, tech, skillLvl, penalty + healTerms.Sum(t => t.Value), target!.Health.DamageTaken);

        var calcParts = new List<(string, int, string?)>
        {
            ("d10", roll.Total, RollFormatter.DiceExtra(roll)), ("TECH", tech, null), (healSkill, skillLvl, null)
        };
        if (penalty != 0) calcParts.Add(("ferite", penalty, null));
        EffectService.AddTermParts(calcParts, healTerms);
        string calc = RollFormatter.FormatSum(calcParts);
        foreach (var note in EffectService.DescribeExpired(healExpired)) calc += $" [{note}]";

        Console.WriteLine($"[HEAL] {healer.Handle} tenta di stabilizzare {target.Handle}: {calc} vs TN {stab.TargetNumber} -> {(stab.Success ? "OK" : "FALLITO")}");

        if (stab.Success)
        {
            target.Health.IsStabilized = true;
            scope.MarkDirty(target);
        }

        return new StabilizeOutcome(null, target, stab.Success, stab.Total, stab.TargetNumber, stab.IsFumble, calc);
    }
}
