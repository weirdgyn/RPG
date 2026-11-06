using RPG.Common.Effects;
using Xunit;

namespace RPG.Tests;

public class EffectRulesTests
{
    private static ActiveEffect Make(string target, string source, string key, int value, EffectDuration? duration = null, string? label = null)
    {
        Assert.True(EffectKey.TryParse(key, null, out var k, out _));
        return ActiveEffect.Create(target, source, label ?? source, k, value, duration ?? EffectDuration.Manual);
    }

    private static readonly EffectContext InitCtx = new(RollKind.Init, Stat: "REF");

    [Fact]
    public void Add_SameSlotReplacesInsteadOfStacking()
    {
        var list = new List<ActiveEffect>();

        Assert.Null(EffectRules.Add(list, Make("morgan", "master", "REF", 2)));
        Assert.Null(EffectRules.Add(list, Make("Morgan", "MASTER", "REF", 3)));

        Assert.Single(list);
        Assert.Equal(3, EffectRules.Total(list, "morgan", InitCtx));
    }

    [Fact]
    public void Add_DifferentSourcesStack()
    {
        var list = new List<ActiveEffect>();

        EffectRules.Add(list, Make("morgan", "master", "REF", 2));
        EffectRules.Add(list, Make("morgan", "Sandevistan", "INIT", 3));

        Assert.Equal(5, EffectRules.Total(list, "morgan", InitCtx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-11)]
    public void Add_RejectsZeroAndOutOfRangeValues(int value)
    {
        var list = new List<ActiveEffect>();

        Assert.NotNull(EffectRules.Add(list, Make("morgan", "master", "REF", value)));
        Assert.Empty(list);
    }

    [Fact]
    public void Add_AllowsNegativeValuesAsMalus()
    {
        var list = new List<ActiveEffect>();

        Assert.Null(EffectRules.Add(list, Make("morgan", "master", "INIT", -2)));
        Assert.Equal(-2, EffectRules.Total(list, "morgan", InitCtx));
    }

    [Fact]
    public void Add_RejectsMissingLabelSourceOrTarget()
    {
        var list = new List<ActiveEffect>();

        Assert.NotNull(EffectRules.Add(list, Make("morgan", "master", "REF", 1, label: " ")));
        Assert.NotNull(EffectRules.Add(list, Make("morgan", " ", "REF", 1)));
        Assert.NotNull(EffectRules.Add(list, Make(" ", "master", "REF", 1)));
        Assert.NotNull(EffectRules.Add(list, Make("morgan", "master", "REF", 1, label: new string('x', EffectRules.MaxLabelLength + 1))));
    }

    [Fact]
    public void Add_EnforcesPerCharacterLimitButStillAllowsReplacing()
    {
        var list = new List<ActiveEffect>();
        for (int i = 0; i < EffectRules.MaxEffectsPerCharacter; i++)
        {
            Assert.Null(EffectRules.Add(list, Make("morgan", "src" + i, "REF", 1)));
        }

        Assert.NotNull(EffectRules.Add(list, Make("morgan", "extra", "REF", 1)));
        Assert.Null(EffectRules.Add(list, Make("morgan", "src0", "REF", 2)));          // sostituzione: ok
        Assert.Null(EffectRules.Add(list, Make("molly", "extra", "REF", 1)));          // altro personaggio: ok
        Assert.Equal(EffectRules.MaxEffectsPerCharacter, list.Count(e => e.TargetHandle == "morgan"));
    }

    [Fact]
    public void Terms_ListsOnlyMatchingEffectsOfThatCharacterInOrder()
    {
        var list = new List<ActiveEffect>();
        EffectRules.Add(list, Make("morgan", "master", "REF", 2, label: "bonus master"));
        EffectRules.Add(list, Make("morgan", "Sandevistan", "INIT", 3, label: "Sandevistan"));
        EffectRules.Add(list, Make("morgan", "pill", "DAMAGE", 1, label: "pillola"));
        EffectRules.Add(list, Make("molly", "master", "REF", 5, label: "altro"));

        var terms = EffectRules.Terms(list, "MORGAN", InitCtx);

        Assert.Equal([new EffectTerm("bonus master", 2), new EffectTerm("Sandevistan", 3)], terms);
    }

    [Fact]
    public void ConsumeUses_RemovesExhaustedEffectsOnlyWhenTheyMatched()
    {
        var list = new List<ActiveEffect>();
        EffectRules.Add(list, Make("morgan", "a", "INIT", 2, new EffectDuration(DurationKind.Uses, 2)));
        EffectRules.Add(list, Make("morgan", "b", "HIT", 2, new EffectDuration(DurationKind.Uses, 1)));

        Assert.Empty(EffectRules.ConsumeUses(list, "morgan", InitCtx));      // 1 uso rimasto sull'INIT
        Assert.Equal(2, list.Count);

        var expired = EffectRules.ConsumeUses(list, "morgan", InitCtx);      // esaurito
        Assert.Single(expired);
        Assert.Equal("a", expired[0].Source);
        Assert.Single(list);
        Assert.Equal("b", list[0].Source);                                   // l'effetto HIT non è stato toccato
    }

    [Fact]
    public void AdvanceRound_ExpiresRoundEffectsAfterTheirCount()
    {
        var list = new List<ActiveEffect>();
        EffectRules.Add(list, Make("morgan", "a", "REF", 1, new EffectDuration(DurationKind.Rounds, 2)));
        EffectRules.Add(list, Make("morgan", "b", "REF", 1, new EffectDuration(DurationKind.Combat)));
        EffectRules.Add(list, Make("morgan", "c", "REF", 1));

        Assert.Empty(EffectRules.AdvanceRound(list));
        var expired = EffectRules.AdvanceRound(list);

        Assert.Single(expired);
        Assert.Equal("a", expired[0].Source);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void EndCombat_RemovesCombatAndRoundEffectsButKeepsManualOnes()
    {
        var list = new List<ActiveEffect>();
        EffectRules.Add(list, Make("morgan", "a", "REF", 1, new EffectDuration(DurationKind.Rounds, 5)));
        EffectRules.Add(list, Make("morgan", "b", "REF", 1, new EffectDuration(DurationKind.Combat)));
        EffectRules.Add(list, Make("morgan", "c", "REF", 1));
        EffectRules.Add(list, Make("morgan", "d", "REF", 1, new EffectDuration(DurationKind.Uses, 1)));

        var removed = EffectRules.EndCombat(list);

        Assert.Equal(2, removed.Count);
        Assert.Equal(["c", "d"], list.Select(e => e.Source).ToArray());
    }

    [Fact]
    public void Clear_RemovesByHandleAndOptionallyBySource()
    {
        var list = new List<ActiveEffect>();
        EffectRules.Add(list, Make("morgan", "master", "REF", 1));
        EffectRules.Add(list, Make("morgan", "Sandevistan", "INIT", 3));
        EffectRules.Add(list, Make("molly", "master", "REF", 1));

        Assert.Single(EffectRules.Clear(list, ["MORGAN"], "master"));
        Assert.Equal(2, list.Count);

        Assert.Equal(2, EffectRules.Clear(list, null).Count);                // tutti
        Assert.Empty(list);
    }

    [Fact]
    public void Describe_ShowsSignedValueKeyLabelAndDuration()
    {
        var e = Make("morgan", "master", "STAT:REF", -2, new EffectDuration(DurationKind.Rounds, 3), "stordito");

        Assert.Equal("-2 STAT:REF (stordito, 3 round)", e.Describe());
        Assert.Equal("+2 STAT:REF (master, manuale)", Make("morgan", "master", "REF", 2).Describe());
    }
}
