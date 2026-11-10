using RPG.Common.Models;

namespace RPG.Common.Effects;

/// <summary>Un termine mostrato nel calcolo di un tiro: l'etichetta dell'effetto e il suo valore.</summary>
public readonly record struct EffectTerm(string Label, int Value);

/// <summary>
/// Regole pure sugli effetti attivi di un game (lista di <see cref="ActiveEffect"/>): aggiunta con sostituzione,
/// somma per un tiro, consumo e scadenza. Nessun lock qui: chi la chiama tiene il Game.SyncRoot.
/// </summary>
public static class EffectRules
{
    /// <summary>Valore massimo (in modulo) di un singolo effetto.</summary>
    public const int MaxAbsValue = 10;

    /// <summary>Numero massimo di effetti contemporanei su uno stesso personaggio.</summary>
    public const int MaxEffectsPerCharacter = 20;

    public const int MaxLabelLength = 40;

    public static string? Validate(ActiveEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.TargetHandle)) return "Bersaglio dell'effetto mancante.";
        if (string.IsNullOrWhiteSpace(effect.Source)) return "Origine dell'effetto mancante.";
        if (string.IsNullOrWhiteSpace(effect.Label) || effect.Label.Length > MaxLabelLength)
            return $"L'etichetta dell'effetto deve avere da 1 a {MaxLabelLength} caratteri.";
        if (effect.Value == 0) return "Un effetto con valore 0 non ha senso.";
        if (Math.Abs(effect.Value) > MaxAbsValue) return $"Il valore di un effetto non può superare ±{MaxAbsValue}.";
        return null;
    }

    private static bool SameSlot(ActiveEffect a, ActiveEffect b) =>
        string.Equals(a.TargetHandle, b.TargetHandle, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase)
        && a.Key.Equals(b.Key);

    /// <summary>
    /// Aggiunge l'effetto. Stesso bersaglio, stessa origine e stessa chiave: il nuovo sostituisce il vecchio
    /// (rilanciare lo stesso bonus non lo raddoppia); origini diverse si sommano.
    /// Restituisce il messaggio d'errore, o null se l'effetto è stato aggiunto.
    /// </summary>
    public static string? Add(List<ActiveEffect> effects, ActiveEffect effect)
    {
        string? error = Validate(effect);
        if (error != null) return error;

        int existing = effects.Count(e =>
            string.Equals(e.TargetHandle, effect.TargetHandle, StringComparison.OrdinalIgnoreCase) && !SameSlot(e, effect));

        if (existing >= MaxEffectsPerCharacter)
            return $"{effect.TargetHandle} ha già {MaxEffectsPerCharacter} effetti attivi: rimuovine qualcuno.";

        int at = effects.FindIndex(e => SameSlot(e, effect));
        if (at >= 0) effects[at] = effect;
        else effects.Add(effect);

        return null;
    }

    /// <summary>
    /// Tutti i termini che intervengono in un tiro di un personaggio: prima i bonus del suo cyberware (oggi sempre
    /// attivi, la chiave è <c>ApplyTo</c>; quelli con chiave non riconosciuta o valore 0 sono ignorati), poi gli
    /// effetti attivi del game.
    /// </summary>
    public static List<EffectTerm> CollectTerms(IEnumerable<ActiveEffect> gameEffects, Character character, EffectContext ctx, Func<string, bool>? skillExists)
    {
        var terms = new List<EffectTerm>();

        foreach (var cw in character.Cyberware)
        {
            if (cw.Bonus == null || cw.Bonus.Value == 0) continue;
            if (!EffectKey.TryParse(cw.Bonus.ApplyTo, skillExists, out var key, out _) || !key.Matches(ctx)) continue;

            terms.Add(new EffectTerm(string.IsNullOrWhiteSpace(cw.ID) ? "cyberware" : cw.ID, cw.Bonus.Value));
        }

        // Oggetti Passive con scheda di attivazione: i loro effetti valgono sempre, come il Bonus.
        foreach (var cw in character.Cyberware)
        {
            if (cw.Activation is not { Mode: ActivationMode.Passive } spec) continue;
            foreach (var (key, value) in ActivationRules.ParsedEffects(spec, skillExists))
            {
                if (key.Matches(ctx)) terms.Add(new EffectTerm(string.IsNullOrWhiteSpace(cw.ID) ? "cyberware" : cw.ID, value));
            }
        }

        terms.AddRange(Terms(gameEffects, character.Handle, ctx));
        return terms;
    }

    /// <summary>Contesto del movimento: gli effetti con chiave MOVE o STAT:MA intervengono sull'MA.</summary>
    public static readonly EffectContext MoveContext = new(RollKind.Move, "MA");

    /// <summary>
    /// Movimento (MA) disponibile per turno: MA della scheda più i termini con chiave MOVE o STAT:MA
    /// (cyberware ed effetti del game), mai sotto zero. Gli effetti "a usi" non si consumano muovendosi:
    /// un bonus al movimento durerebbe un solo passo, che non ha senso.
    /// </summary>
    public static int MovementAllowance(IEnumerable<ActiveEffect> gameEffects, Character character, Func<string, bool>? skillExists = null) =>
        Math.Max(0, character.Stats.MA + CollectTerms(gameEffects, character, MoveContext, skillExists).Sum(t => t.Value));

    /// <summary>Gli effetti del personaggio che intervengono nel tiro, nell'ordine in cui sono stati aggiunti.</summary>
    public static List<EffectTerm> Terms(IEnumerable<ActiveEffect> effects, string handle, EffectContext ctx) =>
        [.. effects
            .Where(e => string.Equals(e.TargetHandle, handle, StringComparison.OrdinalIgnoreCase) && e.Key.Matches(ctx))
            .Select(e => new EffectTerm(e.Label, e.Value))];

    public static int Total(IEnumerable<ActiveEffect> effects, string handle, EffectContext ctx) =>
        Terms(effects, handle, ctx).Sum(t => t.Value);

    /// <summary>
    /// Da chiamare dopo un tiro: consuma un uso degli effetti a "usi" che sono intervenuti.
    /// Restituisce quelli esauriti (già rimossi dalla lista).
    /// </summary>
    public static List<ActiveEffect> ConsumeUses(List<ActiveEffect> effects, string handle, EffectContext ctx)
    {
        var expired = new List<ActiveEffect>();

        foreach (var e in effects.ToList())
        {
            if (e.DurationKind != DurationKind.Uses) continue;
            if (!string.Equals(e.TargetHandle, handle, StringComparison.OrdinalIgnoreCase) || !e.Key.Matches(ctx)) continue;

            e.Remaining--;
            if (e.Remaining <= 0)
            {
                effects.Remove(e);
                expired.Add(e);
            }
        }

        return expired;
    }

    /// <summary>
    /// Da chiamare quando il round avanza: toglie un round agli effetti a round e rimuove quelli arrivati a zero.
    /// Un effetto "3 round" applicato durante il round 2 vale per i round 2, 3 e 4 e scade all'inizio del 5.
    /// </summary>
    public static List<ActiveEffect> AdvanceRound(List<ActiveEffect> effects)
    {
        var expired = new List<ActiveEffect>();

        foreach (var e in effects.ToList())
        {
            if (e.DurationKind != DurationKind.Rounds) continue;

            e.Remaining--;
            if (e.Remaining <= 0)
            {
                effects.Remove(e);
                expired.Add(e);
            }
        }

        return expired;
    }

    /// <summary>Fine del combattimento: cadono gli effetti "combat" e quelli a round.</summary>
    public static List<ActiveEffect> EndCombat(List<ActiveEffect> effects)
    {
        var removed = effects.Where(e => e.DurationKind is DurationKind.Combat or DurationKind.Rounds).ToList();
        effects.RemoveAll(removed.Contains);
        return removed;
    }

    /// <summary>
    /// Rimuove gli effetti dei personaggi indicati (null = di tutti), opzionalmente solo quelli di una certa origine.
    /// Restituisce quelli rimossi.
    /// </summary>
    public static List<ActiveEffect> Clear(List<ActiveEffect> effects, IEnumerable<string>? handles, string? source = null)
    {
        var set = handles == null ? null : new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase);

        var removed = effects.Where(e =>
            (set == null || set.Contains(e.TargetHandle))
            && (source == null || string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))).ToList();

        effects.RemoveAll(removed.Contains);
        return removed;
    }
}
