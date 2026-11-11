using RPG.Common;
using RPG.Common.Effects;
using RPG.Common.Models;

namespace RPG.Server;

/// <summary>
/// Gli effetti (bonus e malus) attivi in un game: raccolta dei termini di un tiro, descrizioni, scadenze e la
/// logica di /bonus. Nessuna rete né I/O: lavora sotto <see cref="Game.SyncRoot"/>, direttamente o tramite
/// <see cref="GameExecutionScope"/>.
/// </summary>
public static class EffectService
{
    /// <summary>L'abilità esiste? Serve a validare le chiavi <c>SKILL:&lt;abilità&gt;</c>.</summary>
    public static bool SkillExists(string name) => Skills.TryGetStatForSkill(name, out _);

    /// <summary>Termini (cyberware ed effetti del game) che intervengono in un tiro. Sotto Game.SyncRoot.</summary>
    public static List<EffectTerm> CollectTerms(Game game, Character character, EffectContext ctx) =>
        EffectRules.CollectTerms(game.Effects, character, ctx, SkillExists);

    /// <summary>Gli effetti attivi su un personaggio, già descritti. Sotto Game.SyncRoot.</summary>
    public static List<string> DescribeEffectsOf(Game game, string handle) =>
        [.. game.Effects.Where(e => e.TargetHandle.Equals(handle, StringComparison.OrdinalIgnoreCase)).Select(e => e.Describe())];

    /// <summary>Aggiunge i termini a un calcolo da mostrare con FormatSum: ognuno con la sua etichetta.</summary>
    public static void AddTermParts(List<(string, int, string?)> parts, IEnumerable<EffectTerm> terms)
    {
        foreach (var t in terms) parts.Add((t.Label, t.Value, null));
    }

    /// <summary>Righe da aggiungere ai passaggi di un tiro quando un effetto a usi si esaurisce.</summary>
    public static IEnumerable<string> DescribeExpired(IEnumerable<ActiveEffect> expired) =>
        expired.Select(e => $"Effetto esaurito: {e.Label} ({e.Key}) su {e.TargetHandle}");

    public static IEnumerable<string> DescribeLapsed(IEnumerable<ActiveEffect> lapsed) =>
        lapsed.Select(e => $"Effetto scaduto: {e.Label} ({e.Key}) su {e.TargetHandle}");

    /// <summary>
    /// Fine del combattimento: toglie gli effetti "combat" e "a round". Sotto Game.SyncRoot.
    /// Restituisce il testo da annunciare ("" se nessuno).
    /// </summary>
    public static string EndCombatEffects(Game game)
    {
        var removed = EffectRules.EndCombat(game.Effects);
        return removed.Count == 0 ? string.Empty : " " + string.Join(". ", DescribeLapsed(removed)) + ".";
    }

    /// <summary>Esito di <see cref="TryAddAll"/>: se Error è valorizzato Failed è l'effetto rifiutato e nulla è cambiato.</summary>
    public sealed record AddAllResult(ActiveEffect? Failed, string? Error);

    /// <summary>Aggiunge gli effetti al game: tutto o niente (se uno non entra, il game non cambia).</summary>
    public static AddAllResult TryAddAll(Game game, IEnumerable<ActiveEffect> effects)
    {
        var scratch = new List<ActiveEffect>(game.Effects);
        foreach (var effect in effects)
        {
            string? error = EffectRules.Add(scratch, effect);
            if (error != null) return new AddAllResult(effect, error);
        }

        game.Effects.Clear();
        game.Effects.AddRange(scratch);
        return new AddAllResult(null, null);
    }

    /// <summary>
    /// Esito di /bonus. Con Error valorizzato non è cambiato nulla. Summary è l'annuncio per tutto il game
    /// (vuoto se non c'è nulla da annunciare); Reply è la risposta privata al master.
    /// </summary>
    public sealed record BonusOutcome(string? Error, string Summary, string? Reply);

    /// <summary>
    /// /bonus (solo master): applica, toglie o elenca bonus e malus di circostanza. Tutto o niente: se un bersaglio
    /// non esiste o un effetto non è valido, non cambia nulla.
    /// </summary>
    public static BonusOutcome ApplyBonus(GameExecutionScope scope, BonusRequest parsed)
    {
        var game = scope.Game;

        // Bersagli: handle, oppure party (PG), npc, all; senza doppioni.
        var targets = new List<Character>();
        void AddTarget(Character c)
        {
            if (!targets.Any(t => t.Handle.Equals(c.Handle, StringComparison.OrdinalIgnoreCase))) targets.Add(c);
        }

        var wanted = parsed.Targets;
        if (wanted.Count == 0 && parsed.Action == BonusAction.List) wanted = ["all"];

        foreach (var name in wanted)
        {
            if (name.Equals("party", StringComparison.OrdinalIgnoreCase) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
                foreach (var p in game.Players) if (p.Character != null) AddTarget(p.Character);
            if (name.Equals("npc", StringComparison.OrdinalIgnoreCase) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
                foreach (var n in game.NPCs) AddTarget(n);
            if (BonusRules.IsShortcut(name)) continue;

            var found = scope.FindCharacter(name);
            if (found == null) return new BonusOutcome($"Personaggio '{name}' non trovato in questo game: nessun effetto modificato.", string.Empty, null);
            AddTarget(found);
        }

        if (targets.Count == 0) return new BonusOutcome("Nessun personaggio corrisponde ai bersagli indicati.", string.Empty, null);

        string handles = string.Join(", ", targets.Select(t => t.Handle));
        switch (parsed.Action)
        {
            case BonusAction.Apply:
                {
                    var effects = targets.Select(t =>
                        ActiveEffect.Create(t.Handle, BonusRules.MasterSource, BonusRules.MasterSource, parsed.Key, parsed.Value, parsed.Duration));
                    var added = TryAddAll(game, effects);
                    if (added.Error != null) return new BonusOutcome($"{added.Failed!.TargetHandle}: {added.Error} Nessun effetto modificato.", string.Empty, null);

                    var sample = ActiveEffect.Create(string.Empty, BonusRules.MasterSource, BonusRules.MasterSource, parsed.Key, parsed.Value, parsed.Duration);
                    return new BonusOutcome(null, $"Il master applica {sample.Describe()} a {handles}.", null);
                }
            case BonusAction.Clear:
                {
                    var removed = EffectRules.Clear(game.Effects, targets.Select(t => t.Handle), BonusRules.MasterSource);
                    if (removed.Count == 0) return new BonusOutcome(null, string.Empty, $"Nessun bonus del master da togliere a {handles}.");

                    return new BonusOutcome(null,
                        $"Il master toglie {removed.Count} bonus/malus a {string.Join(", ", removed.Select(r => r.TargetHandle).Distinct(StringComparer.OrdinalIgnoreCase))}.", null);
                }
            case BonusAction.List:
                {
                    var lines = targets
                        .Select(t => (t.Handle, Effects: DescribeEffectsOf(game, t.Handle)))
                        .Where(x => x.Effects.Count > 0)
                        .Select(x => $"{x.Handle}: {string.Join("; ", x.Effects)}")
                        .ToList();
                    return new BonusOutcome(null, string.Empty, lines.Count == 0 ? "Nessun effetto attivo." : "Effetti attivi - " + string.Join(" | ", lines));
                }
            default:
                return new BonusOutcome(null, string.Empty, null);
        }
    }
}
