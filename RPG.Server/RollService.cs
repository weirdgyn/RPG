using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;
using RPG.Common.Utils;

namespace RPG.Server;

/// <summary>
/// I tiri del gioco che non dipendono dalla rete: abilità, iniziativa, stordimento, recupero e salvezza contro la
/// morte. Quando c'è un game vanno chiamati sotto <see cref="Game.SyncRoot"/> (leggono e consumano gli effetti).
/// </summary>
public static class RollService
{
    // Tiro di stordimento dopo un colpo che ha inflitto danno (solo in combattimento): d10 sotto la soglia
    // BODY - modificatore di stato. Se fallisce il personaggio è stordito. Restituisce la riga di calcolo
    // (null se non applicabile). Da chiamare dopo aver tolto i punti ferita.
    public static string? RollStunSave(Character target)
    {
        if (target.Health.IsDead || target.Health.IsStunned) return null;

        int mod = target.Health.GetStunSaveModifier();
        int threshold = Math.Max(0, target.Stats.BODY - mod);
        int roll = (int)GameDice.Roll("1d10").Total;
        bool resisted = roll < threshold;
        if (!resisted) target.Health.IsStunned = true;

        return $"Tiro di stordimento: d10 {roll} contro meno di {threshold} (BODY {target.Stats.BODY} - stato {mod}): "
            + (resisted ? "resiste" : "STORDITO (non può agire né muoversi finché non si riprende)");
    }

    // Tiro di recupero all'inizio del turno di un personaggio stordito: stessa soglia dello stordimento
    public static string RollStunRecovery(Character c)
    {
        int mod = c.Health.GetStunSaveModifier();
        int threshold = Math.Max(0, c.Stats.BODY - mod);
        int roll = (int)GameDice.Roll("1d10").Total;
        bool recovered = roll < threshold;
        if (recovered) c.Health.IsStunned = false;

        return $"{c.Handle} tenta di riprendersi: d10 {roll} contro meno di {threshold} (BODY {c.Stats.BODY} - stato {mod}): "
            + (recovered ? "si riprende" : "è ancora stordito");
    }

    // Tiro salvezza contro la morte (a inizio turno, chi è in Mortale): d10 inferiore a BODY meno il livello
    // Mortale per sopravvivere. In caso di fallimento segna il personaggio come morto. Restituisce la riga di calcolo.
    public static string RollDeathSave(Character c)
    {
        int body = c.Stats.BODY;
        int level = Math.Max(0, c.Health.MortalLevel);
        int threshold = CombatRules.GetDeathSaveThreshold(body, level);
        int die = (int)GameDice.Roll("1d10").Total;
        bool survives = CombatRules.DeathSaveSurvives(die, body, level);
        if (!survives) c.Health.Kill();

        return $"Salvezza contro la morte di {c.Handle} (Mortale {level}): d10 {die} contro meno di {threshold} (BODY {body} - livello {level}): "
            + (survives ? "sopravvive" : "MUORE");
    }


    public sealed record SkillRollResult(string StatName, int StatValue, int SkillLevel, int Penalty, GameDice.D10Roll Dice,
        IReadOnlyList<EffectTerm> Terms, IReadOnlyList<ActiveEffect> Expired, int Total);

    // Testo per il log del server: " +2 master -1 stordito"
    public static string LogTerms(IEnumerable<EffectTerm> terms) =>
        string.Concat(terms.Select(t => $" {t.Value:+#;-#;0} {t.Label}"));

    // Tiro di abilità (d10 esplosivo + stat + livello + penalità ferite + effetti); null se l'abilità non esiste.
    // Con un game va chiamata sotto Game.SyncRoot (legge e consuma gli effetti); senza game (fuori partita)
    // valgono solo i bonus del cyberware.
    public static SkillRollResult? RollSkill(Game? game, Character actor, string skillName)
    {
        if (!Skills.TryGetStatForSkill(skillName, out string statName)) return null;

        int statVal = actor.Stats.GetStatValue(statName);
        int skillLvl = actor.Skills.TryGetValue(skillName, out int lvl) ? lvl : 0;
        int penalty = actor.Health.GetWoundPenalty();
        var ctx = new EffectContext(RollKind.Skill, statName, skillName);
        var terms = EffectRules.CollectTerms(game?.Effects ?? [], actor, ctx, EffectService.SkillExists);
        var expired = game != null ? EffectRules.ConsumeUses(game.Effects, actor.Handle, ctx) : [];
        var dice = GameDice.RollExplodingD10Detailed();
        return new SkillRollResult(statName, statVal, skillLvl, penalty, dice, terms, expired,
            statVal + skillLvl + penalty + terms.Sum(t => t.Value) + dice.Total);
    }


    public sealed record InitiativeRollResult(int Ref, int CombatSense, IReadOnlyList<EffectTerm> Terms, IReadOnlyList<ActiveEffect> Expired, int Dice, int Total)
    {
        public int Effects => Terms.Sum(t => t.Value);
    }

    // Calcolo dell'iniziativa con tutti i passaggi: REF + CombatSense + effetti (con etichetta) + d10
    public static string DescribeInitiative(InitiativeRollResult init)
    {
        var parts = new List<(string, int, string?)> { ("REF", init.Ref, null), ("CombatSense", init.CombatSense, null) };
        EffectService.AddTermParts(parts, init.Terms);
        parts.Add(("d10", init.Dice, null));
        string text = RollFormatter.FormatSum(parts);
        foreach (var note in EffectService.DescribeExpired(init.Expired)) text += $" [{note}]";
        return text;
    }

    // Iniziativa: REF + CombatSense (solo i Solo) + effetti (cyberware INIT, bonus del master) + d10
    // Va chiamata sotto Game.SyncRoot (legge e consuma gli effetti del game)
    public static InitiativeRollResult RollInitiative(Game game, Character actor)
    {
        int refStat = actor.Stats.REF;

        int combatSense = 0;
        if (actor.Role == Character.RoleType.Solo)
        {
            combatSense = actor.Skills.TryGetValue("CombatSense", out int csLvl) ? csLvl : 0;
        }

        var initCtx = new EffectContext(RollKind.Init, "REF", "CombatSense");
        var terms = EffectService.CollectTerms(game, actor, initCtx);
        var expired = EffectRules.ConsumeUses(game.Effects, actor.Handle, initCtx);

        int dice = (int)GameDice.Roll("1d10").Total;
        return new InitiativeRollResult(refStat, combatSense, terms, expired, dice, refStat + combatSense + terms.Sum(t => t.Value) + dice);
    }
}
