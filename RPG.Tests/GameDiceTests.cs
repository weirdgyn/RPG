using RPG.Common.Utils;

namespace RPG.Tests;

public class GameDiceTests
{
    [Theory]
    [InlineData("3d6!", "3d6!e")]
    [InlineData("3d6!!", "3d6!c")]
    [InlineData("3d6!e", "3d6!e")]
    [InlineData("  1d10  ", "1d10")]
    [InlineData("2d6!+1", "2d6!e+1")]
    public void NormalizeExpression_TranslatesRpgShorthand(string input, string expected)
    {
        Assert.Equal(expected, GameDice.NormalizeExpression(input));
    }

    [Fact]
    public void Roll_SimpleExpression_StaysInRange()
    {
        for (int i = 0; i < 200; i++)
        {
            var r = GameDice.Roll("1d10");
            Assert.InRange((int)r.Total, 1, 10);
        }
    }

    [Fact]
    public void Roll_TooLongExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => GameDice.Roll(new string('1', GameDice.MaxExpressionLength + 1)));
    }

    [Fact]
    public void Roll_AbsurdDiceCount_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => GameDice.Roll("999999d6"));
    }

    [Fact]
    public void ExplodingD10_IsConsistent()
    {
        for (int i = 0; i < 500; i++)
        {
            var r = GameDice.RollExplodingD10Detailed();

            Assert.Equal(r.Rolls.Sum(), r.Total);
            Assert.Equal(r.Rolls.Length, r.DiceCount);
            Assert.Equal(r.Rolls[0], r.FirstDie);
            Assert.Equal(r.FirstDie == 1, r.IsFumble);
            Assert.All(r.Rolls.Take(r.Rolls.Length - 1), d => Assert.Equal(10, d));
            Assert.All(r.Rolls, d => Assert.InRange(d, 1, 10));
        }
    }
}
