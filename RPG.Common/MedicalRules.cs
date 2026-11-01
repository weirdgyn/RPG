using RPG.Common.Models;
using RPG.Common.Utils;

namespace RPG.Common;

/// <summary>
/// Regole del pronto soccorso (/heal di un personaggio): funzioni pure, con i dadi già tirati, così sono
/// deterministiche e testabili.
/// </summary>
public static class MedicalRules
{
    /// <summary>Distanza massima, in metri, tra chi cura e chi viene curato (come la mischia).</summary>
    public const double HealRangeMeters = 2.5;

    /// <summary>Perché una stabilizzazione non si può nemmeno tentare.</summary>
    public enum StabilizeBlock { None, Dead, NotMortal, AlreadyStable }

    /// <summary>
    /// Si stabilizza solo chi è vivo, in Mortale e non ancora stabilizzato. Il controllo avviene prima del
    /// tiro, così un tentativo inutile non consuma l'azione.
    /// </summary>
    public static StabilizeBlock GetStabilizeBlock(HealthSystem target)
    {
        if (target.IsDead) return StabilizeBlock.Dead;
        if (!target.IsInMortalRange) return StabilizeBlock.NotMortal;
        if (target.IsStabilized) return StabilizeBlock.AlreadyStable;
        return StabilizeBlock.None;
    }

    /// <summary>Esito di un tentativo di stabilizzazione.</summary>
    public sealed record StabilizationResult(int Total, int TargetNumber, bool IsFumble, bool Success);

    /// <summary>
    /// Tentativo di stabilizzazione: TECH + abilità medica + penalità ferite del medico + d10 esplosivo contro
    /// un TN pari al danno subito dal ferito. Un 1 naturale sul primo dado fallisce sempre.
    /// </summary>
    public static StabilizationResult ResolveStabilization(GameDice.D10Roll roll, int tech, int skillLevel,
        int healerWoundPenalty, int targetDamageTaken)
    {
        int total = roll.Total + tech + skillLevel + healerWoundPenalty;
        bool fumble = roll.IsFumble;
        return new StabilizationResult(total, targetDamageTaken, fumble, !fumble && total >= targetDamageTaken);
    }

    /// <summary>Il livello più alto tra le abilità indicate (0 se il personaggio non ne ha nessuna).</summary>
    public static int BestSkillLevel(IReadOnlyDictionary<string, int> skills, IEnumerable<string> names)
    {
        int best = 0;
        foreach (var name in names)
        {
            if (skills.TryGetValue(name, out int level) && level > best) best = level;
        }
        return best;
    }
}
