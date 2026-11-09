namespace RPG.Common.Effects;

public enum BonusAction
{
    Apply,
    Clear,
    List
}

/// <summary>Comando /bonus già interpretato. Per Clear e List <see cref="Key"/>, <see cref="Value"/> e <see cref="Duration"/> non servono.</summary>
public sealed record BonusRequest(BonusAction Action, EffectKey Key, int Value, EffectDuration Duration, List<string> Targets);

/// <summary>
/// Regole pure di /bonus (solo master). Sintassi:
/// <c>/bonus &lt;chiave&gt; &lt;±N&gt; [durata] &lt;bersagli&gt;</c>, <c>/bonus clear &lt;bersagli&gt;</c>, <c>/bonus list [bersagli]</c>.
/// I bersagli sono handle separati da spazi o virgole, oppure le scorciatoie <c>party</c>, <c>npc</c>, <c>all</c> (espanse dal server).
/// </summary>
public static class BonusRules
{
    public const string Usage = "Uso: /bonus <chiave> <±N> [durata] <bersagli> | /bonus clear <bersagli> | /bonus list [bersagli]  " +
        "(chiave: ALL, INIT, HIT, DAMAGE, MOVE, STAT:<stat>, SKILL:<abilità>; durata: manual, combat, r<N>, u<N>, d<N>; bersagli: handle, party, npc, all)";

    /// <summary>Nome dell'origine degli effetti prodotti da /bonus: serve a /bonus clear e compare nei pannelli di calcolo.</summary>
    public const string MasterSource = "master";

    public static bool IsShortcut(string target) =>
        target.Equals("party", StringComparison.OrdinalIgnoreCase)
        || target.Equals("npc", StringComparison.OrdinalIgnoreCase)
        || target.Equals("all", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(IReadOnlyList<string> args, Func<string, bool>? skillExists, out BonusRequest? request, out string error)
    {
        request = null;
        error = Usage;

        var tokens = args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        if (tokens.Count == 0) return false;

        string first = tokens[0];
        if (first.Equals("list", StringComparison.OrdinalIgnoreCase) || first.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            var targets = TeleportRules.ParseTargets(tokens.Skip(1));
            bool isClear = first.Equals("clear", StringComparison.OrdinalIgnoreCase);
            if (isClear && targets.Count == 0)
            {
                error = "Indica a chi togliere i bonus: /bonus clear <bersagli>  (es. /bonus clear morgan, /bonus clear all).";
                return false;
            }
            if (targets.Count > TeleportRules.MaxTargets)
            {
                error = $"Troppi bersagli: al massimo {TeleportRules.MaxTargets} per comando.";
                return false;
            }
            request = new BonusRequest(isClear ? BonusAction.Clear : BonusAction.List, default, 0, EffectDuration.Manual, targets);
            return true;
        }

        // Chiave: di norma un solo token, ma un'abilità può contenere spazi ("SKILL:Pronto Soccorso").
        EffectKey key = default;
        int used = 0;
        string keyError = string.Empty;
        for (int n = 1; n <= Math.Min(3, tokens.Count - 1); n++)
        {
            string candidate = string.Join(' ', tokens.Take(n));
            if (EffectKey.TryParse(candidate, skillExists, out key, out var e))
            {
                used = n;
                break;
            }
            if (n == 1) keyError = e;
        }
        if (used == 0)
        {
            error = keyError.Length > 0 ? keyError : Usage;
            return false;
        }

        int index = used;
        if (index >= tokens.Count || !int.TryParse(tokens[index], out int value))
        {
            error = $"Manca il valore (un intero con segno, es. +2 o -1, massimo {EffectRules.MaxAbsValue} in valore assoluto).";
            return false;
        }
        if (value == 0 || Math.Abs(value) > EffectRules.MaxAbsValue)
        {
            error = $"Valore {value} non valido: deve essere diverso da 0 e al massimo {EffectRules.MaxAbsValue} in valore assoluto.";
            return false;
        }
        index++;

        // La durata è facoltativa: la riconosco solo se dopo ci sono ancora dei bersagli.
        var duration = EffectDuration.Manual;
        if (index < tokens.Count - 1 && EffectDuration.TryParse(tokens[index], out var parsed, out _))
        {
            duration = parsed;
            index++;
        }

        var applyTargets = TeleportRules.ParseTargets(tokens.Skip(index));
        if (applyTargets.Count == 0)
        {
            error = "Indica a chi applicare il bonus (handle, party, npc o all).";
            return false;
        }
        if (applyTargets.Count > TeleportRules.MaxTargets)
        {
            error = $"Troppi bersagli: al massimo {TeleportRules.MaxTargets} per comando.";
            return false;
        }

        request = new BonusRequest(BonusAction.Apply, key, value, duration, applyTargets);
        error = string.Empty;
        return true;
    }
}
