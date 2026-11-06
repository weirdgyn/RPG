namespace RPG.Common.Effects;

/// <summary>Tipo di tiro (o di calcolo) in cui gli effetti possono intervenire.</summary>
public enum RollKind
{
    Skill,   // prova di abilità
    Hit,     // tiro per colpire
    Damage,  // tiro dei danni
    Init,    // iniziativa
    Move     // movimento (MA), non è un tiro
}

/// <summary>Il contesto di un tiro: che tipo è, quale statistica e quale abilità usa.</summary>
public readonly record struct EffectContext(RollKind Kind, string? Stat = null, string? Skill = null);

public enum EffectScope
{
    All,     // tutti i tiri di prova (abilità, colpire, iniziativa), non danni né movimento
    Init,
    Hit,
    Damage,
    Move,
    Stat,    // una statistica: vale nei tiri che la usano
    Skill    // una singola abilità
}

/// <summary>
/// A cosa si applica un effetto. Sintassi (maiuscole indifferenti): <c>ALL</c>, <c>INIT</c>, <c>HIT</c>,
/// <c>DAMAGE</c>, <c>MOVE</c>, <c>STAT:REF</c>, <c>SKILL:Pistola</c>. È la stessa grammatica di
/// <c>Character.BonusType.ApplyTo</c> dei cyberware, con in più ALL e MOVE.
/// </summary>
public readonly record struct EffectKey(EffectScope Scope, string Name = "")
{
    private static readonly string[] KnownStats = ["INT", "REF", "TECH", "COOL", "ATTR", "MA", "BODY", "EMP"];

    public static bool TryParse(string? text, Func<string, bool>? skillExists, out EffectKey key, out string error)
    {
        key = default;
        error = string.Empty;

        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            error = "Chiave dell'effetto mancante.";
            return false;
        }

        switch (t.ToUpperInvariant())
        {
            case "ALL": key = new EffectKey(EffectScope.All); return true;
            case "INIT": key = new EffectKey(EffectScope.Init); return true;
            case "HIT": key = new EffectKey(EffectScope.Hit); return true;
            case "DAMAGE": key = new EffectKey(EffectScope.Damage); return true;
            case "MOVE": key = new EffectKey(EffectScope.Move); return true;
        }

        int colon = t.IndexOf(':');
        if (colon > 0)
        {
            string head = t[..colon].Trim().ToUpperInvariant();
            string name = t[(colon + 1)..].Trim();

            if (head == "STAT")
            {
                string stat = name.ToUpperInvariant();
                if (KnownStats.Contains(stat))
                {
                    key = new EffectKey(EffectScope.Stat, stat);
                    return true;
                }

                error = $"Statistica '{name}' sconosciuta: usa {string.Join(", ", KnownStats)}.";
                return false;
            }

            if (head == "SKILL" && name.Length > 0)
            {
                if (skillExists == null || skillExists(name))
                {
                    key = new EffectKey(EffectScope.Skill, name);
                    return true;
                }

                error = $"Abilità '{name}' sconosciuta: usa /skills per l'elenco.";
                return false;
            }
        }

        // Scorciatoia: una statistica o un'abilità senza prefisso
        string upper = t.ToUpperInvariant();
        if (KnownStats.Contains(upper))
        {
            key = new EffectKey(EffectScope.Stat, upper);
            return true;
        }

        if (skillExists != null && skillExists(t))
        {
            key = new EffectKey(EffectScope.Skill, t);
            return true;
        }

        error = $"'{t}' non è una chiave valida: usa ALL, INIT, HIT, DAMAGE, MOVE, una statistica (REF) o un'abilità.";
        return false;
    }

    /// <summary>True se un effetto con questa chiave interviene nel tiro descritto dal contesto.</summary>
    public bool Matches(EffectContext ctx)
    {
        bool isCheck = ctx.Kind is RollKind.Skill or RollKind.Hit or RollKind.Init;

        return Scope switch
        {
            EffectScope.All => isCheck,
            EffectScope.Init => ctx.Kind == RollKind.Init,
            EffectScope.Hit => ctx.Kind == RollKind.Hit,
            EffectScope.Damage => ctx.Kind == RollKind.Damage,
            EffectScope.Move => ctx.Kind == RollKind.Move,
            EffectScope.Stat => isCheck && string.Equals(ctx.Stat, Name, StringComparison.OrdinalIgnoreCase),
            EffectScope.Skill => ctx.Kind is RollKind.Skill or RollKind.Hit
                                 && string.Equals(ctx.Skill, Name, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Forma canonica, ricostruibile con <see cref="TryParse"/>.</summary>
    public override string ToString() => Scope switch
    {
        EffectScope.All => "ALL",
        EffectScope.Init => "INIT",
        EffectScope.Hit => "HIT",
        EffectScope.Damage => "DAMAGE",
        EffectScope.Move => "MOVE",
        EffectScope.Stat => $"STAT:{Name}",
        EffectScope.Skill => $"SKILL:{Name}",
        _ => "NONE"
    };
}
