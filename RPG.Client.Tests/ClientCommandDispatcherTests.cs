using Xunit;

namespace RPG.Client.Tests;

public class ClientCommandDispatcherTests
{
    [Theory]
    [InlineData("/PING", "Ping")]
    [InlineData("/list", "List")]
    [InlineData("/create Solo Razor", "Create")]
    [InlineData("/exit", "Exit")]
    [InlineData("/help", "Help")]
    [InlineData("/help status", "HelpTopic")]
    [InlineData("/connect host 1234", "Connect")]
    [InlineData("/npc list", "Npc")]
    [InlineData("/as npc init", "As")]
    [InlineData("/status", "Status")]
    [InlineData("/skills", "Skills")]
    [InlineData("/games", "Games")]
    [InlineData("/archetypes", "Archetypes")]
    [InlineData("/characters", "Characters")]
    [InlineData("/check status", "Check")]
    [InlineData("/init", "Init")]
    [InlineData("/throw 1 2", "Throw")]
    [InlineData("/move 1 2", "Move")]
    [InlineData("/teleport 1 2 3 npc", "Teleport")]
    [InlineData("/activate item", "Activate")]
    [InlineData("/bonus add", "Bonus")]
    [InlineData("/whoami", "WhoAmI")]
    [InlineData("/game", "GameStatus")]
    [InlineData("/gamestatus", "GameStatus")]
    [InlineData("/next", "Next")]
    [InlineData("/endturn", "Next")]
    [InlineData("/combat start", "Combat")]
    [InlineData("/reload", "Reload")]
    [InlineData("/attack target", "Attack")]
    [InlineData("/roll 1d10", "Roll")]
    [InlineData("/skill athletics", "Skill")]
    [InlineData("/chat \"hello\"", "Chat")]
    [InlineData("/leave", "Leave")]
    [InlineData("/join game", "Join")]
    [InlineData("/unlink", "Unlink")]
    [InlineData("/heal target", "Heal")]
    [InlineData("/link character", "Link")]
    [InlineData("/unknown", "Unknown")]
    public void Match_ResolvesCommand(string input, string expected)
    {
        Assert.Equal(expected, ClientCommandDispatcher.Match(input).ToString());
    }

    [Theory]
    [InlineData("/listening", "List")]
    [InlineData("/movefast", "Move")]
    [InlineData("/attacktarget", "Attack")]
    [InlineData("/connectivity", "Unknown")]
    [InlineData("/npcadd", "Unknown")]
    public void Match_PreservesExistingPrefixRules(string input, string expected)
    {
        Assert.Equal(expected, ClientCommandDispatcher.Match(input).ToString());
    }
}
