using Dice;
using System.Text.RegularExpressions;

namespace RPG.Common.Utils;

public class DiceRollResult
{
    public string Expression { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Details { get; set; } = string.Empty;
}
public static class GameDice
{
    /// <summary>Lunghezza massima accettata per un'espressione di dadi.</summary>
    public const int MaxExpressionLength = 64;

    // Limiti anti-abuso (es. "999999d999999", esplosioni a catena): oltre questi valori la libreria
    // lancia DiceException invece di bloccare il server.
    private static readonly RollerConfig BoundedConfig = new()
    {
        MaxDice = 100,
        MaxSides = 1000,
        MaxRerolls = 20,
        MaxRecursionDepth = 5
    };

    // La libreria documenta l'esplosione come "!e" (compounding "!c", penetrante "!p"), non come "!" da solo.
    // Accettiamo la notazione comune dei GdR: "3d6!" -> "3d6!e", "3d6!!" -> "3d6!c". Il "!" seguito da una
    // lettera o da "=" (es. "!=") è già sintassi della libreria e resta com'è.
    private static readonly Regex CompoundShorthand = new(@"!!", RegexOptions.Compiled);
    private static readonly Regex ExplodeShorthand = new(@"!(?![a-zA-Z=])", RegexOptions.Compiled);

    /// <summary>
    /// Converte le abbreviazioni "!" / "!!" nella sintassi della libreria. Pubblico per poterlo testare.
    /// </summary>
    public static string NormalizeExpression(string expression)
    {
        string e = CompoundShorthand.Replace(expression.Trim(), "!c");
        return ExplodeShorthand.Replace(e, "!e");
    }

    /// <summary>
    /// Esegue un tiro. Espressioni non valide o oltre i limiti lanciano <see cref="DiceException"/>;
    /// un'espressione troppo lunga lancia <see cref="ArgumentException"/>.
    /// </summary>
    public static DiceRollResult Roll(string expression)
    {
        if (expression == null || expression.Length > MaxExpressionLength)
        {
            throw new ArgumentException($"L'espressione dei dadi supera i {MaxExpressionLength} caratteri.", nameof(expression));
        }

        // Esegue il parsing e il tiro usando la libreria esterna
        RollResult result = Roller.Roll(NormalizeExpression(expression), BoundedConfig);

        return new DiceRollResult
        {
            Expression = expression,
            Total = result.Value,
            Details = result.ToString() // Fornisce automaticamente il dettaglio dei singoli dadi e modificatori
        };
    }
    /// <summary>
    /// Tiro d10 "esplosivo" stile Cyberpunk 2020: se esce 10 si ritira e si somma,
    /// continuando finché escono altri 10. (La sintassi "1d10!" della libreria non è
    /// registrata di default: l'esplosione è implementata qui in modo esplicito e fedele).
    /// </summary>
    public static int RollExplodingD10() => RollExplodingD10Detailed().Total;

    /// <summary>
    /// Come <see cref="RollExplodingD10"/> ma conserva il primo dado: un 1 naturale è un fallimento critico
    /// (fumble) e non si ritira. <paramref name="DiceCount"/> è il numero di dadi tirati (&gt;1 = esplosione).
    /// </summary>
    public readonly record struct D10Roll(int Total, int FirstDie, int DiceCount)
    {
        public bool IsFumble => FirstDie == 1;
        public bool Exploded => DiceCount > 1;
    }

    public static D10Roll RollExplodingD10Detailed()
    {
        const int maxChain = 20; // guardia anti-loop (~1/10^20, puramente cautelativa)
        int total = 0;
        int chain = maxChain;
        int first = 0;
        int dice = 0;
        int roll;
        do
        {
            roll = (int)Roller.Roll("1d10").Value;
            if (dice == 0) first = roll;
            total += roll;
            dice++;
            chain--;
        } while (roll == 10 && chain > 0);

        return new D10Roll(total, first, dice);
    }
}