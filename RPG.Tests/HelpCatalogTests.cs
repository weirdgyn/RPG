using RPG.Common;
using Xunit;

namespace RPG.Tests;

public class HelpCatalogTests
{
    [Theory]
    [InlineData("attack", "/attack")]
    [InlineData("/attack", "/attack")]
    [InlineData(" /attack ", "/attack")]
    [InlineData("ATTACK", "/ATTACK")]
    public void NormalizeCommand_AlwaysReturnsCanonicalSlashPrefix(string input, string expected)
    {
        var actual = HelpCatalog.NormalizeCommand(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseFromJson_LoadsCommandsAndDescriptions()
    {
        var json = """
        [
          { "command": "/help", "parameters": "[[comando]]", "description": "Mostra il menu dei comandi." },
          { "command": "/attack", "parameters": "<target> [[arma|skill]]", "description": "Attacca un bersaglio." }
        ]
        """;

        var catalog = HelpCatalog.ParseFromJson(json);

        Assert.True(catalog.ContainsKey("/help"));
        Assert.True(catalog.ContainsKey("/attack"));

        Assert.Equal("[[comando]]", catalog["/help"].Parameters);
        Assert.Equal("Attacca un bersaglio.", catalog["/attack"].Description);
    }

    [Fact]
    public void TryGet_FindsCommandEvenWithoutSlash()
    {
        var json = """
        [
          { "command": "/help", "parameters": "[[comando]]", "description": "Mostra il menu dei comandi." },
          { "command": "/attack", "parameters": "<target> [[arma|skill]]", "description": "Attacca un bersaglio." }
        ]
        """;

        var catalog = HelpCatalog.ParseFromJson(json);

        Assert.True(catalog.TryGetValue("/attack", out var entry));
        Assert.Equal("/attack", entry.Command);
        Assert.Equal("Attacca un bersaglio.", entry.Description);
    }

    [Fact]
    public void TryGet_RejectsUnknownCommand()
    {
        var json = """
        [
          { "command": "/help", "parameters": "[[comando]]", "description": "Mostra il menu dei comandi." }
        ]
        """;

        var catalog = HelpCatalog.ParseFromJson(json);

        Assert.False(catalog.ContainsKey("/unknown"));

        Assert.False(catalog.TryGetValue("/unknown", out _));
    }

    [Fact]
    public void ParseFromJson_IgnoresEmptyOrNullCommands()
    {
        var json = """
        [
          { "command": "", "parameters": "-", "description": "vuoto" },
          { "command": null, "parameters": "-", "description": "nullo" },
          { "command": "/move", "parameters": "<X> <Y> [[Z]]", "description": "Muove il personaggio." }
        ]
        """;

        var catalog = HelpCatalog.ParseFromJson(json);

        Assert.True(catalog.ContainsKey("/move"));
        Assert.False(catalog.ContainsKey(""));
        Assert.False(catalog.ContainsKey("null"));
    }
}