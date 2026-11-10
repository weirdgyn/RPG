using System.Text.RegularExpressions;
using RPG.Common.Models;

namespace RPG.Common.Effects;

/// <summary>Durata letta dalla scheda di un oggetto: tipo e quantità, che può essere un intero o un'espressione di dadi.</summary>
public readonly record struct DurationSpec(DurationKind Kind, string Amount)
{
    public bool IsDice => Amount.Contains('d', StringComparison.OrdinalIgnoreCase);
}

/// <summary>Regole pure degli oggetti ad attivazione (scheda, durata con dadi, validazione).</summary>
public static partial class ActivationRules
{
    public const int DefaultActivationTN = 15;
    public const int MaxEffectsPerItem = 10;
    public const int MaxTN = 40;

    [GeneratedRegex(@"^(\d{1,2})d(\d{1,3})([+-]\d{1,2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceRegex();

    /// <summary>
    /// Come <see cref="EffectDuration.TryParse"/>, ma la quantità può essere un'espressione di dadi (<c>r1d6</c>, <c>u1d3+1</c>).
    /// Vuoto = manuale.
    /// </summary>
    public static bool TryParseDuration(string? text, out DurationSpec spec, out string error)
    {
        spec = new DurationSpec(DurationKind.Manual, string.Empty);
        error = string.Empty;

        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "manual") return true;
        if (t is "combat" or "c")
        {
            spec = new DurationSpec(DurationKind.Combat, string.Empty);
            return true;
        }

        DurationKind? kind = t[0] switch
        {
            'r' => DurationKind.Rounds,
            'u' => DurationKind.Uses,
            'd' => DurationKind.Days,
            _ => null
        };
        string amount = t.Length > 1 ? t[1..] : string.Empty;

        if (kind != null)
        {
            if (int.TryParse(amount, out int n) && n >= 1 && n <= EffectDuration.MaxAmount)
            {
                spec = new DurationSpec(kind.Value, amount);
                return true;
            }

            var m = DiceRegex().Match(amount);
            if (m.Success && int.Parse(m.Groups[1].Value) is >= 1 and <= 10 && int.Parse(m.Groups[2].Value) is >= 2 and <= 100)
            {
                spec = new DurationSpec(kind.Value, amount);
                return true;
            }
        }

        error = $"Durata '{text}' non valida: usa manual, combat, r<N>, u<N>, d<N> (round, usi, giorni) con N da 1 a {EffectDuration.MaxAmount} " +
                "oppure con i dadi (es. r1d6, r2d6+1).";
        return false;
    }

    /// <summary>
    /// Durata effettiva: se la quantità è un'espressione di dadi la tira con <paramref name="roll"/> (restituisce il totale),
    /// e la porta sempre nell'intervallo 1..<see cref="EffectDuration.MaxAmount"/>.
    /// </summary>
    public static EffectDuration Resolve(DurationSpec spec, Func<string, int> roll)
    {
        if (spec.Kind is DurationKind.Manual or DurationKind.Combat) return new EffectDuration(spec.Kind);

        int amount = spec.IsDice ? roll(spec.Amount) : int.Parse(spec.Amount);
        return new EffectDuration(spec.Kind, Math.Clamp(amount, 1, EffectDuration.MaxAmount));
    }

    /// <summary>Gli effetti validi dell'oggetto, già interpretati (quelli con chiave non riconosciuta sono saltati).</summary>
    public static IEnumerable<(EffectKey Key, int Value)> ParsedEffects(ActivationSpec spec, Func<string, bool>? skillExists)
    {
        foreach (var e in spec.Effects)
        {
            if (e.Value == 0 || !EffectKey.TryParse(e.Key, skillExists, out var key, out _)) continue;
            yield return (key, e.Value);
        }
    }

    /// <summary>Gli oggetti che si possono attivare (modalità Active).</summary>
    public static List<Character.CyberWare> Activable(IEnumerable<Character.CyberWare> items) =>
        [.. items.Where(c => c.Activation is { Mode: ActivationMode.Active })];

    /// <summary>
    /// Sceglie l'oggetto da attivare. Senza chiave va bene solo se ce n'è uno solo; con la chiave vale l'ID esatto,
    /// poi una parte dell'ID (senza distinguere maiuscole).
    /// </summary>
    public static bool TryFindActivable(IEnumerable<Character.CyberWare> items, string? key, out Character.CyberWare? item, out string error)
    {
        item = null;
        error = string.Empty;
        var list = Activable(items);
        string names = string.Join(", ", list.Select(c => c.ID));

        if (list.Count == 0)
        {
            error = "Non hai oggetti da attivare.";
            return false;
        }

        string k = (key ?? string.Empty).Trim();
        if (k.Length == 0)
        {
            if (list.Count == 1)
            {
                item = list[0];
                return true;
            }
            error = $"Quale oggetto vuoi attivare? Disponibili: {names}.";
            return false;
        }

        item = list.FirstOrDefault(c => c.ID.Equals(k, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(c => c.ID.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            error = $"Nessun oggetto attivabile '{k}'. Disponibili: {names}.";
            return false;
        }
        return true;
    }

    /// <summary>Controlla la scheda di attivazione di un oggetto. Restituisce il problema trovato, null se è valida (o se non c'è scheda).</summary>
    public static string? Validate(Character.CyberWare item, Func<string, bool>? skillExists)
    {
        var spec = item.Activation;
        if (spec == null) return null;

        string name = string.IsNullOrWhiteSpace(item.ID) ? "oggetto" : $"'{item.ID}'";

        if (spec.Mode == ActivationMode.Passive)
        {
            if (spec.ActionCost != 0) return $"{name}: un oggetto Passive non può avere un costo di azione.";
            if (!string.IsNullOrWhiteSpace(spec.ActivationSkill)) return $"{name}: un oggetto Passive non può avere un'abilità di attivazione.";
            if (!string.IsNullOrWhiteSpace(spec.Duration)) return $"{name}: un oggetto Passive è sempre attivo e non ha durata.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(item.ID)) return "Un oggetto Active deve avere un ID (serve per /activate).";
            if (spec.ActionCost is not (0 or 1)) return $"{name}: il costo di azione deve essere 0 o 1.";
            if (spec.Effects.Count == 0) return $"{name}: un oggetto Active deve avere almeno un effetto.";

            if (!string.IsNullOrWhiteSpace(spec.ActivationSkill))
            {
                if (skillExists != null && !skillExists(spec.ActivationSkill.Trim()))
                    return $"{name}: abilità di attivazione '{spec.ActivationSkill}' inesistente.";
                if (spec.ActivationTN < 1 || spec.ActivationTN > MaxTN)
                    return $"{name}: la difficoltà di attivazione deve essere tra 1 e {MaxTN}.";
            }

            if (!TryParseDuration(spec.Duration, out _, out var durationError)) return $"{name}: {durationError}";
        }

        if (spec.Effects.Count > MaxEffectsPerItem) return $"{name}: al massimo {MaxEffectsPerItem} effetti per oggetto.";
        foreach (var e in spec.Effects)
        {
            if (!EffectKey.TryParse(e.Key, skillExists, out _, out var keyError)) return $"{name}: effetto '{e.Key}': {keyError}";
            if (e.Value == 0 || Math.Abs(e.Value) > EffectRules.MaxAbsValue)
                return $"{name}: effetto '{e.Key}': il valore deve essere diverso da 0 e al massimo ±{EffectRules.MaxAbsValue}.";
        }

        return null;
    }
}
