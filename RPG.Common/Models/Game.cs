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

    public enum CheckKind { Skill, Initiative }

    /// <summary>Un personaggio iscritto a un check, con il suo tiro (null finché non ha tirato).</summary>
    public class CheckEntry
    {
        public string Handle { get; set; } = string.Empty;
        public int? Total { get; set; }
        public bool IsFumble { get; set; }
        /// <summary>Ordine di arrivo del tiro (spareggio finale dell'iniziativa).</summary>
        public int Sequence { get; set; }
    }

    /// <summary>
    /// Prova collettiva aperta dal master (/check): prova di abilità contro un TN, oppure iniziativa.
    /// Lo spoglio avviene quando tutte le voci hanno un tiro (o il master chiude con /check close).
    /// </summary>
    public class Check
    {
        public CheckKind Kind { get; set; }
        public string SkillName { get; set; } = string.Empty;
        public int TargetNumber { get; set; }
        public List<CheckEntry> Entries { get; set; } = [];
        public int RollCounter { get; set; }
    }

    /// <summary>Una posizione nell'ordine di gioco calcolato dall'ultimo check di iniziativa.</summary>
    public class TurnEntry
    {
        public string Handle { get; set; } = string.Empty;
        public int Initiative { get; set; }
    }

    public Check? ActiveCheck { get; set; }
    public List<TurnEntry> TurnOrder { get; set; } = [];
}