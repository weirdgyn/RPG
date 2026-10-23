namespace RPG.Common.Models
{
    public class Player
    {
        public enum RoleType { player, master, none };
        public string PlayerId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public RoleType Role { get; set; } = RoleType.none;
        public string Email { get; set; } = string.Empty;
        public Character? Character { get; set; } = null;
        public string Handle { get; set; } = string.Empty;
    }
}
