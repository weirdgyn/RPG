using System.Text.Json.Serialization;
using RPG.Common.Effects;

namespace RPG.Common.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivationMode
{
    /// <summary>Sempre attivo: i suoi effetti valgono senza /activate.</summary>
    Passive,
    /// <summary>I suoi effetti partono solo dopo /activate.</summary>
    Active
}

/// <summary>Un effetto prodotto da un oggetto: chiave (come in /bonus: ALL, INIT, STAT:REF, SKILL:...) e valore con segno.</summary>
public class ActivationEffect
{
    public string Key { get; set; } = string.Empty;
    public int Value { get; set; }
}

/// <summary>
/// Scheda di attivazione di un oggetto (oggi il cyberware). Un oggetto senza scheda si comporta come prima:
/// il suo <c>Bonus</c> è sempre attivo.
/// </summary>
public class ActivationSpec
{
    public ActivationMode Mode { get; set; } = ActivationMode.Passive;

    /// <summary>Azioni del turno consumate dall'attivazione: 0 (gratuita) o 1. Solo per Active.</summary>
    public int ActionCost { get; set; }

    /// <summary>Abilità della prova richiesta per attivare (vuoto = nessuna prova). Indipendente da <see cref="ActionCost"/>.</summary>
    public string ActivationSkill { get; set; } = string.Empty;

    /// <summary>Difficoltà della prova di attivazione (usata solo se c'è un'abilità).</summary>
    public int ActivationTN { get; set; } = ActivationRules.DefaultActivationTN;

    /// <summary>
    /// Durata degli effetti: <c>manual</c>, <c>combat</c>, <c>r3</c>, <c>u2</c>, <c>d1</c>, oppure con i dadi
    /// <c>r1d6</c> (tirata all'attivazione). Vuoto = manuale.
    /// </summary>
    public string Duration { get; set; } = string.Empty;

    public List<ActivationEffect> Effects { get; set; } = [];
}
