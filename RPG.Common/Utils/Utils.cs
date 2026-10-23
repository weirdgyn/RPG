using Dice;

namespace RPG.Common.Utils;

public class DiceRollResult
{
    public string Expression { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Details { get; set; } = string.Empty;
}
public static class GameDice
{
    public static DiceRollResult Roll(string expression)
    {
        // Esegue il parsing e il tiro usando la libreria esterna
        RollResult result = Roller.Roll(expression);

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
    public static int RollExplodingD10()
    {
        const int maxChain = 20; // guardia anti-loop (~1/10^20, puramente cautelativa)
        int total = 0;
        int chain = maxChain;
        int roll;
        do
        {
            roll = (int)Roller.Roll("1d10").Value;
            total += roll;
            chain--;
        } while (roll == 10 && chain > 0);

        return total;
    }
}