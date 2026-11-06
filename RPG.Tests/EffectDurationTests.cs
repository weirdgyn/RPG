using RPG.Common.Effects;
using Xunit;

namespace RPG.Tests;

public class EffectDurationTests
{
    [Theory]
    [InlineData("", DurationKind.Manual, 0)]
    [InlineData("manual", DurationKind.Manual, 0)]
    [InlineData("combat", DurationKind.Combat, 0)]
    [InlineData("C", DurationKind.Combat, 0)]
    [InlineData("r3", DurationKind.Rounds, 3)]
    [InlineData("R10", DurationKind.Rounds, 10)]
    [InlineData("u1", DurationKind.Uses, 1)]
    [InlineData("d2", DurationKind.Days, 2)]
    public void TryParse_AcceptsValidDurations(string text, DurationKind kind, int amount)
    {
        Assert.True(EffectDuration.TryParse(text, out var d, out _));
        Assert.Equal(kind, d.Kind);
        Assert.Equal(amount, d.Amount);
    }

    [Theory]
    [InlineData("r0")]
    [InlineData("r100")]
    [InlineData("r")]
    [InlineData("rx")]
    [InlineData("x3")]
    [InlineData("-3")]
    public void TryParse_RejectsInvalidDurations(string text)
    {
        Assert.False(EffectDuration.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ToString_IsReadable()
    {
        Assert.Equal("manuale", EffectDuration.Manual.ToString());
        Assert.Equal("1 round", new EffectDuration(DurationKind.Rounds, 1).ToString());
        Assert.Equal("3 round", new EffectDuration(DurationKind.Rounds, 3).ToString());
        Assert.Equal("2 usi", new EffectDuration(DurationKind.Uses, 2).ToString());
    }
}
