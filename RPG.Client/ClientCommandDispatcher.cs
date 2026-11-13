namespace RPG.Client;

internal enum ClientCommand
{
    Unknown,
    Ping,
    List,
    Create,
    Exit,
    Help,
    HelpTopic,
    Connect,
    Npc,
    As,
    Status,
    Skills,
    Games,
    Archetypes,
    Characters,
    Check,
    Init,
    Throw,
    Move,
    Teleport,
    Activate,
    Bonus,
    WhoAmI,
    GameStatus,
    Next,
    Combat,
    Reload,
    Attack,
    Roll,
    Skill,
    Chat,
    Leave,
    Join,
    Unlink,
    Heal,
    Link
}

internal static class ClientCommandDispatcher
{
    private enum MatchMode
    {
        Exact,
        ExactOrSpacePrefix,
        AnyPrefix
    }

    private readonly record struct Route(ClientCommand Command, MatchMode Mode, params string[] Names);

    private static readonly Route[] Routes =
    [
        new(ClientCommand.Ping, MatchMode.Exact, "/ping"),
        new(ClientCommand.List, MatchMode.AnyPrefix, "/list"),
        new(ClientCommand.Create, MatchMode.AnyPrefix, "/create"),
        new(ClientCommand.Exit, MatchMode.Exact, "/exit"),
        new(ClientCommand.Help, MatchMode.Exact, "/help"),
        new(ClientCommand.HelpTopic, MatchMode.ExactOrSpacePrefix, "/help"),
        new(ClientCommand.Connect, MatchMode.ExactOrSpacePrefix, "/connect"),
        new(ClientCommand.Npc, MatchMode.ExactOrSpacePrefix, "/npc"),
        new(ClientCommand.As, MatchMode.ExactOrSpacePrefix, "/as"),
        new(ClientCommand.Status, MatchMode.Exact, "/status"),
        new(ClientCommand.Skills, MatchMode.Exact, "/skills"),
        new(ClientCommand.Games, MatchMode.Exact, "/games"),
        new(ClientCommand.Archetypes, MatchMode.Exact, "/archetypes"),
        new(ClientCommand.Characters, MatchMode.Exact, "/characters"),
        new(ClientCommand.Check, MatchMode.ExactOrSpacePrefix, "/check"),
        new(ClientCommand.Init, MatchMode.Exact, "/init"),
        new(ClientCommand.Throw, MatchMode.ExactOrSpacePrefix, "/throw"),
        new(ClientCommand.Move, MatchMode.AnyPrefix, "/move"),
        new(ClientCommand.Teleport, MatchMode.ExactOrSpacePrefix, "/teleport"),
        new(ClientCommand.Activate, MatchMode.ExactOrSpacePrefix, "/activate"),
        new(ClientCommand.Bonus, MatchMode.ExactOrSpacePrefix, "/bonus"),
        new(ClientCommand.WhoAmI, MatchMode.Exact, "/whoami"),
        new(ClientCommand.GameStatus, MatchMode.Exact, "/game", "/gamestatus"),
        new(ClientCommand.Next, MatchMode.Exact, "/next", "/endturn"),
        new(ClientCommand.Combat, MatchMode.ExactOrSpacePrefix, "/combat"),
        new(ClientCommand.Reload, MatchMode.ExactOrSpacePrefix, "/reload"),
        new(ClientCommand.Attack, MatchMode.AnyPrefix, "/attack"),
        new(ClientCommand.Roll, MatchMode.AnyPrefix, "/roll"),
        new(ClientCommand.Skill, MatchMode.AnyPrefix, "/skill"),
        new(ClientCommand.Chat, MatchMode.AnyPrefix, "/chat"),
        new(ClientCommand.Leave, MatchMode.Exact, "/leave"),
        new(ClientCommand.Join, MatchMode.AnyPrefix, "/join"),
        new(ClientCommand.Unlink, MatchMode.Exact, "/unlink"),
        new(ClientCommand.Heal, MatchMode.ExactOrSpacePrefix, "/heal"),
        new(ClientCommand.Link, MatchMode.AnyPrefix, "/link")
    ];

    internal static ClientCommand Match(string input)
    {
        foreach (Route route in Routes)
        {
            foreach (string name in route.Names)
            {
                bool matches = route.Mode switch
                {
                    MatchMode.Exact => input.Equals(name, StringComparison.OrdinalIgnoreCase),
                    MatchMode.ExactOrSpacePrefix =>
                        input.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || input.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase),
                    MatchMode.AnyPrefix => input.StartsWith(name, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (matches) return route.Command;
            }
        }

        return ClientCommand.Unknown;
    }
}
