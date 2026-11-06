namespace RPG.Common.Effects;

/// <summary>
/// Un effetto (bonus o malus) attivo su un personaggio. È stato di sessione: vive nel Game, non nella scheda.
/// <see cref="Remaining"/> è il contatore residuo per le durate a round, usi o giorni.
/// </summary>
public class ActiveEffect
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Handle del personaggio che subisce l'effetto.</summary>
    public string TargetHandle { get; init; } = string.Empty;

    /// <summary>Chi lo ha prodotto: "master", oppure l'id di un oggetto/droga. Serve a non duplicare e a mostrarlo.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Testo mostrato nei pannelli di calcolo (es. "Sandevistan").</summary>
    public string Label { get; init; } = string.Empty;

    public EffectKey Key { get; init; }

    /// <summary>Valore con segno: un malus è negativo.</summary>
    public int Value { get; init; }

    public DurationKind DurationKind { get; init; } = DurationKind.Manual;

    public int Remaining { get; set; }

    public static ActiveEffect Create(string targetHandle, string source, string label, EffectKey key, int value, EffectDuration duration) =>
        new()
        {
            TargetHandle = targetHandle,
            Source = source,
            Label = label,
            Key = key,
            Value = value,
            DurationKind = duration.Kind,
            Remaining = duration.Amount
        };

    /// <summary>Es. "+2 STAT:REF (master, 3 round)".</summary>
    public string Describe()
    {
        string duration = new EffectDuration(DurationKind, Remaining).ToString();
        return $"{Value:+#;-#;0} {Key} ({Label}, {duration})";
    }
}
