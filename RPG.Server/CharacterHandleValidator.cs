namespace RPG.Server;

/// <summary>
/// Unica fonte delle regole sugli handle dei personaggi. L'handle diventa il nome del file
/// della scheda (<c>Data/Characters/&lt;handle&gt;.json</c>), quindi deve essere un nome di file
/// sicuro: solo lettere ASCII, cifre e underscore, da <see cref="MinLength"/> a
/// <see cref="MaxLength"/> caratteri, e non un nome riservato di Windows.
/// </summary>
internal static class CharacterHandleValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 20;

    /// <summary>Descrizione delle regole, adatta a essere mostrata al giocatore.</summary>
    public const string RulesDescription =
        "Handle non valido: usa da 3 a 20 caratteri tra lettere (a-z), cifre e underscore (_).";

    // Su Windows questi nomi indicano dispositivi (CON, NUL, COM1...) anche con un'estensione:
    // un file "CON.json" non sarebbe un normale file di scheda.
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verifica un handle senza lanciare eccezioni. Il controllo è carattere per carattere (niente
    /// regex): con <c>$</c> .NET accetterebbe anche un "\n" finale, e <see cref="char.IsLetterOrDigit(char)"/>
    /// ammetterebbe lettere non ASCII.
    /// </summary>
    public static bool IsValid(string? handle)
    {
        if (handle is null || handle.Length < MinLength || handle.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in handle)
        {
            bool allowed = (c >= 'a' && c <= 'z')
                        || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9')
                        || c == '_';

            if (!allowed)
            {
                return false;
            }
        }

        return !ReservedNames.Contains(handle);
    }

    /// <summary>Restituisce l'handle se valido, altrimenti lancia <see cref="ArgumentException"/>.</summary>
    /// <exception cref="ArgumentException">Handle vuoto o non conforme alle regole.</exception>
    public static string ValidateAndNormalize(string? handle, string parameterName = "handle")
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException($"{parameterName} cannot be null or empty.", parameterName);
        }

        if (!IsValid(handle))
        {
            throw new ArgumentException(
                "Invalid character handle: must be 3-20 letters, digits or underscores, and not a reserved name.",
                parameterName);
        }

        return handle;
    }
}
