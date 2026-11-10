using System.Text.Json;
using RPG.Common.Effects;
using RPG.Common.Models;
using Xunit;

namespace RPG.Tests;

public class ActivationRulesTests
{
    private static bool Skill(string n) => n is "Pistola" or "Elettronica";

    private static Character.CyberWare Active(Action<ActivationSpec>? tweak = null, string id = "sandevistan")
    {
        var spec = new ActivationSpec
        {
            Mode = ActivationMode.Active,
            Duration = "r3",
            Effects = [new ActivationEffect { Key = "INIT", Value = 3 }]
        };
        tweak?.Invoke(spec);
        return new Character.CyberWare { ID = id, Activation = spec };
    }

    [Theory]
    [InlineData("", DurationKind.Manual, "")]
    [InlineData("manual", DurationKind.Manual, "")]
    [InlineData("combat", DurationKind.Combat, "")]
    [InlineData("r3", DurationKind.Rounds, "3")]
    [InlineData("U1", DurationKind.Uses, "1")]
    [InlineData("d2", DurationKind.Days, "2")]
    [InlineData("r1d6", DurationKind.Rounds, "1d6")]
    [InlineData("r2d6+1", DurationKind.Rounds, "2d6+1")]
    public void ParsesValidDurations(string text, DurationKind kind, string amount)
    {
        Assert.True(ActivationRules.TryParseDuration(text, out var spec, out var error), error);
        Assert.Equal(kind, spec.Kind);
        Assert.Equal(amount, spec.Amount);
    }

    [Theory]
    [InlineData("r0")]
    [InlineData("r100")]
    [InlineData("x3")]
    [InlineData("r")]
    [InlineData("r1d1")]
    [InlineData("r0d6")]
    [InlineData("r11d6")]
    [InlineData("rd6")]
    public void RejectsInvalidDurations(string text)
    {
        Assert.False(ActivationRules.TryParseDuration(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ResolveRollsDiceAndClamps()
    {
        ActivationRules.TryParseDuration("r1d6", out var dice, out _);
        Assert.Equal(new EffectDuration(DurationKind.Rounds, 4), ActivationRules.Resolve(dice, expr => { Assert.Equal("1d6", expr); return 4; }));
        Assert.Equal(new EffectDuration(DurationKind.Rounds, 1), ActivationRules.Resolve(dice, _ => -3));
        Assert.Equal(new EffectDuration(DurationKind.Rounds, EffectDuration.MaxAmount), ActivationRules.Resolve(dice, _ => 500));

        ActivationRules.TryParseDuration("u2", out var fixedUses, out _);
        Assert.Equal(new EffectDuration(DurationKind.Uses, 2), ActivationRules.Resolve(fixedUses, _ => throw new InvalidOperationException("nessun tiro")));

        ActivationRules.TryParseDuration("combat", out var combat, out _);
        Assert.Equal(new EffectDuration(DurationKind.Combat), ActivationRules.Resolve(combat, _ => 0));
    }

    [Fact]
    public void ItemWithoutSpecIsValid() =>
        Assert.Null(ActivationRules.Validate(new Character.CyberWare { ID = "occhio" }, Skill));

    [Fact]
    public void ValidActiveItem() => Assert.Null(ActivationRules.Validate(Active(), Skill));

    [Fact]
    public void ActiveWithSkillCheck_IsValid_AndIndependentOfActionCost()
    {
        Assert.Null(ActivationRules.Validate(Active(s => { s.ActionCost = 0; s.ActivationSkill = "Elettronica"; s.ActivationTN = 12; }), Skill));
        Assert.Null(ActivationRules.Validate(Active(s => { s.ActionCost = 1; }), Skill));
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("skill")]
    [InlineData("duration")]
    public void PassiveWithActivationFieldsIsRejected(string field)
    {
        var item = new Character.CyberWare
        {
            ID = "occhio",
            Activation = new ActivationSpec
            {
                Mode = ActivationMode.Passive,
                ActionCost = field == "cost" ? 1 : 0,
                ActivationSkill = field == "skill" ? "Pistola" : "",
                Duration = field == "duration" ? "r3" : "",
                Effects = [new ActivationEffect { Key = "HIT", Value = 1 }]
            }
        };
        Assert.NotNull(ActivationRules.Validate(item, Skill));
    }

    [Fact]
    public void PassiveWithOnlyEffectsIsValid()
    {
        var item = new Character.CyberWare
        {
            ID = "occhio",
            Activation = new ActivationSpec { Mode = ActivationMode.Passive, Effects = [new ActivationEffect { Key = "HIT", Value = 1 }] }
        };
        Assert.Null(ActivationRules.Validate(item, Skill));
    }

    [Fact]
    public void ActiveRejectsBadSpecs()
    {
        Assert.NotNull(ActivationRules.Validate(Active(s => s.ActionCost = 2), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Effects = []), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.ActivationSkill = "Inesistente"), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => { s.ActivationSkill = "Pistola"; s.ActivationTN = 0; }), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Duration = "r0"), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Effects = [new ActivationEffect { Key = "BOH", Value = 1 }]), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Effects = [new ActivationEffect { Key = "INIT", Value = 0 }]), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Effects = [new ActivationEffect { Key = "INIT", Value = 11 }]), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(id: ""), Skill));
        Assert.NotNull(ActivationRules.Validate(Active(s => s.Effects =
            [.. Enumerable.Range(0, ActivationRules.MaxEffectsPerItem + 1).Select(_ => new ActivationEffect { Key = "INIT", Value = 1 })]), Skill));
    }

    [Fact]
    public void PassiveEffectsCountInRollsButActiveOnesDoNot()
    {
        var c = new Character { Handle = "morgan" };
        c.Cyberware.Add(new Character.CyberWare
        {
            ID = "occhio",
            Activation = new ActivationSpec { Mode = ActivationMode.Passive, Effects = [new ActivationEffect { Key = "HIT", Value = 2 }] }
        });
        c.Cyberware.Add(Active());

        var hit = EffectRules.CollectTerms([], c, new EffectContext(RollKind.Hit, "REF", "Pistola"), Skill);
        var init = EffectRules.CollectTerms([], c, new EffectContext(RollKind.Init, "REF", null), Skill);

        Assert.Equal([new EffectTerm("occhio", 2)], hit);
        Assert.Empty(init); // la Sandevistan (Active) non conta finché non viene attivata
    }

    [Fact]
    public void SheetJsonRoundTripAndLegacySheets()
    {
        var item = Active(s => { s.ActivationSkill = "Elettronica"; s.ActivationTN = 18; s.Duration = "r1d6"; });
        string json = JsonSerializer.Serialize(item);
        Assert.Contains("\"Active\"", json); // enum scritto come testo, leggibile a mano

        var back = JsonSerializer.Deserialize<Character.CyberWare>(json)!;
        Assert.Equal(ActivationMode.Active, back.Activation!.Mode);
        Assert.Equal("Elettronica", back.Activation.ActivationSkill);
        Assert.Equal(18, back.Activation.ActivationTN);
        Assert.Equal("r1d6", back.Activation.Duration);
        Assert.Single(back.Activation.Effects);

        // Una scheda precedente, senza Activation, si legge come prima.
        var legacy = JsonSerializer.Deserialize<Character.CyberWare>("{\"ID\":\"occhio\",\"Bonus\":{\"ApplyTo\":\"HIT\",\"Value\":1}}")!;
        Assert.Null(legacy.Activation);
    }
}
