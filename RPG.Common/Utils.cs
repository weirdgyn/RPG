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

public class SkillCheckResult
{
    public bool IsSuccess { get; set; }
    public int Total { get; set; }
    public int TargetNumber { get; set; }
    public int DiceRoll { get; set; }
    public int Modifiers { get; set; }
    public string Message { get; set; } = string.Empty;
}

public static class InterlockRules
{
    public static SkillCheckResult PerformCheck(int statValue, int skillLevel, int woundPenalty, int targetNumber)
    {
        // Usiamo il nostro motore dadi per tirare un d10
        var roll = GameDice.Roll("1d10");
        int diceVal = (int)roll.Total;

        int total = statValue + skillLevel + woundPenalty + diceVal;
        bool success = total >= targetNumber;

        return new SkillCheckResult
        {
            IsSuccess = success,
            Total = total,
            TargetNumber = targetNumber,
            DiceRoll = diceVal,
            Modifiers = statValue + skillLevel + woundPenalty,
            Message = success
                ? $"SUCCESSO! Totale {total} vs TN {targetNumber} (Dado: {diceVal}, Modificatori: {statValue + skillLevel + woundPenalty})"
                : $"FALLIMENTO! Totale {total} vs TN {targetNumber} (Dado: {diceVal}, Modificatori: {statValue + skillLevel + woundPenalty})"
        };
    }
}