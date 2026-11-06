namespace RPG.Common.Effects;

public enum DurationKind
{
    Manual,   // fino a rimozione esplicita (/bonus clear)
    Combat,   // fino alla fine del combattimento
    Rounds,   // N round di combattimento
    Uses,     // N usi: l'effetto si consuma nei tiri in cui interviene
    Days      // N giorni di gioco (usato quando esisterà il tempo di gioco)
}

/// <summary>Durata di un effetto: un tipo e, per Rounds/Uses/Days, una quantità.</summary>
public readonly record struct EffectDuration(DurationKind Kind, int Amount = 0)
{
    public const int MaxAmount = 99;

    public static readonly EffectDuration Manual = new(DurationKind.Manual);

    /// <summary>
    /// Sintassi: vuoto o <c>manual</c>; <c>combat</c>; <c>r3</c> (3 round); <c>u1</c> (1 uso); <c>d2</c> (2 giorni).
    /// </summary>
    public static bool TryParse(string? text, out EffectDuration duration, out string error)
    {
        duration = Manual;
        error = string.Empty;

        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "manual") return true;

        if (t is "combat" or "c")
        {
            duration = new EffectDuration(DurationKind.Combat);
            return true;
        }

        DurationKind? kind = t[0] switch
        {
            'r' => DurationKind.Rounds,
            'u' => DurationKind.Uses,
            'd' => DurationKind.Days,
            _ => null
        };

        if (kind != null && int.TryParse(t[1..], out int amount) && amount >= 1 && amount <= MaxAmount)
        {
            duration = new EffectDuration(kind.Value, amount);
            return true;
        }

        error = $"Durata '{text}' non valida: usa manual, combat, r<N> (round), u<N> (usi) o d<N> (giorni), con N da 1 a {MaxAmount}.";
        return false;
    }

    public override string ToString() => Kind switch
    {
        DurationKind.Manual => "manuale",
        DurationKind.Combat => "fino a fine combattimento",
        DurationKind.Rounds => Amount == 1 ? "1 round" : $"{Amount} round",
        DurationKind.Uses => Amount == 1 ? "1 uso" : $"{Amount} usi",
        DurationKind.Days => Amount == 1 ? "1 giorno" : $"{Amount} giorni",
        _ => string.Empty
    };
}
