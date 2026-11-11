using RPG.Common.Utils;

namespace RPG.Server;

/// <summary>Testi dei calcoli di un tiro: ogni addendo con il suo nome, così il calcolo si legge senza ricostruirlo.</summary>
public static class RollFormatter
{
    /// <summary>
    /// "d10 6 + REF 7 - ferite 2 = 11". Extra è un testo opzionale accanto al valore
    /// (es. " (10+4)" per un dado esploso).
    /// </summary>
    public static string FormatSum(IReadOnlyList<(string Label, int Value, string? Extra)> parts)
    {
        var chunks = new List<string>();
        int total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var (label, value, extra) = parts[i];
            total += value;
            if (i == 0) chunks.Add($"{label} {value}{extra}");
            else if (value >= 0) chunks.Add($"+ {label} {value}{extra}");
            else chunks.Add($"- {label} {-value}{extra}");
        }
        return $"{string.Join(" ", chunks)} = {total}";
    }

    /// <summary>"2d6 => 4 + 3 => 7" (testo della libreria) diventa "4 + 3 = 7": i dadi tirati e la somma, senza l'espressione.</summary>
    public static string FormatDiceDetail(DiceRollResult roll)
    {
        var parts = roll.Details.Split("=>", StringSplitOptions.TrimEntries);
        return parts.Length == 3 && parts[1].Length > 0
            ? $"{parts[1]} = {(int)roll.Total}"
            : $"{(int)roll.Total} ({roll.Details})";
    }

    /// <summary>" (10+4, esploso)" se il d10 è esploso, altrimenti vuoto.</summary>
    public static string DiceExtra(GameDice.D10Roll roll) =>
        roll.Exploded ? $" ({string.Join("+", roll.Rolls)}, esploso)" : string.Empty;
}
