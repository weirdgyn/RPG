using System.Text.Json;
using RPG.Common.Protocol;
using Xunit;

namespace RPG.Tests;

public class SkillCheckPayloadTests
{
    [Fact]
    public void Modifiers_SurviveSerialization()
    {
        var payload = new SkillCheckResponsePayload
        {
            CharacterHandle = "morgan",
            WoundPenalty = -2,
            Modifiers = [new RollTermPayload { Label = "Sandevistan", Value = 3 }, new RollTermPayload { Label = "master", Value = -1 }]
        };

        var back = JsonSerializer.Deserialize<SkillCheckResponsePayload>(JsonSerializer.Serialize(payload));

        Assert.NotNull(back);
        Assert.Equal(-2, back.WoundPenalty);
        Assert.Equal(2, back.Modifiers.Count);
        Assert.Equal("Sandevistan", back.Modifiers[0].Label);
        Assert.Equal(-1, back.Modifiers[1].Value);
    }

    [Fact]
    public void OldPayloadWithoutNewFields_StillDeserializes()
    {
        const string json = "{\"CharacterHandle\":\"morgan\",\"SkillName\":\"Pistola\",\"StatValue\":6,\"SkillLevel\":4,\"DiceRoll\":7,\"TotalResult\":17}";

        var payload = JsonSerializer.Deserialize<SkillCheckResponsePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal(0, payload.WoundPenalty);
        Assert.Empty(payload.Modifiers);
        Assert.Equal(17, payload.TotalResult);
    }
}
