using RPG.Common.World;

namespace RPG.Common.Models;

public class Game
{
    public class Roll
    {
        public int Value { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public TimeOnly Timestamp { get; set; }
    }
    public GameMap Map = new();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Player> Players { get; set; } = [];
    public List<Roll> InitRolls { get; set; } = [];
    /// <summary>Personaggi non giocanti del game: in memoria, gestiti solo dal master (/npc, /as).</summary>
    public List<Character> NPCs { get; set; } = [];
}