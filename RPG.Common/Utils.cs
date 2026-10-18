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
}