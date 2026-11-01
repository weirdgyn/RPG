using RPG.Common.Models;
using RPG.Common.Utils;
using RPG.Common.World;

namespace RPG.Common;

public static class CombatRules
{
    // Calcola la distanza euclidea tra due posizioni 3D
    public static double CalculateDistance(Position3D pos1, Position3D pos2)
    {
        double dx = pos1.X - pos2.X;
        double dy = pos1.Y - pos2.Y;
        double dz = pos1.Z - pos2.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
    // TN per un'arma a distanza: fasce della tabella Cyberpunk 2020 scalate sulla gittata dell'arma.
    // Con gittata 50 m: 1 / 12,5 / 25 / 50 / 100 m. Oltre il doppio della gittata il bersaglio è fuori portata (null).
    public static int? GetRangedTargetNumber(double distanceMeters, double weaponRangeMeters)
    {
        double range = weaponRangeMeters > 0 ? weaponRangeMeters : 50.0;
        if (distanceMeters <= 1.0) return 10;          // Point Blank (A bruciapelo)
        if (distanceMeters <= range / 4) return 15;    // Close (Ravvicinata)
        if (distanceMeters <= range / 2) return 20;    // Medium (Media)
        if (distanceMeters <= range) return 25;        // Long (Lunga)
        if (distanceMeters <= range * 2) return 30;    // Extreme (Estrema)
        return null;                                   // Fuori portata
    }
    /// <summary>
    /// Risultato della locazione di impatto (tabella roll Cyberpunk 2020).
    /// La Testa raddoppia il danno.
    /// </summary>
    public sealed class HitLocationResult
    {
        public int Roll { get; init; }
        public string Name { get; init; } = string.Empty;
        public int DamageMultiplier { get; init; } = 1;
        /// <summary>Locazione dell'armatura che protegge la zona colpita (braccia e gambe: un solo pezzo per lato).</summary>
        public ArmorLocation ArmorLocation { get; init; } = ArmorLocation.Torso;
    }

    /// <summary>Esito dell'impatto contro l'armatura.</summary>
    public sealed record ArmorResult(int DamageDealt, int SpFaced, int Absorbed, bool Ablated);

    /// <summary>
    /// Applica l'armatura al danno di un colpo a segno, nell'ordine del regolamento CP2020:
    /// 1) si sottrae l'SP della locazione (dimezzato, arrotondato per eccesso, con proiettili perforanti);
    /// 2) il danno che passa viene moltiplicato per la locazione (la Testa raddoppia *dopo* l'armatura);
    /// 3) se l'armatura è penetrata, il suo SP cala di 1 (ablazione). Se il danno è tutto assorbito non succede nulla.
    /// Modifica <paramref name="piece"/> (ablazione): chiamare sotto il lock del combattimento.
    /// </summary>
    public static ArmorResult ApplyArmor(ArmorPiece? piece, int rawDamage, int locationMultiplier, bool armorPiercing = false)
    {
        int sp = piece?.CurrentStopPower ?? 0;
        if (armorPiercing) sp = (sp + 1) / 2;

        int penetrating = Math.Max(0, rawDamage - sp);
        int absorbed = rawDamage - penetrating;
        bool ablated = false;

        if (penetrating > 0 && piece != null && piece.CurrentStopPower > 0)
        {
            piece.CurrentStopPower--;
            ablated = true;
        }

        return new ArmorResult(penetrating * locationMultiplier, sp, absorbed, ablated);
    }
    /// <summary>
    /// Body Type Modifier CP2020 (riduzione del danno data dalla corporatura): BODY 2 o meno = 0,
    /// 3-4 = 1, 5-7 = 2, 8-9 = 3, 10 = 4, 11 o più = 5. Restituisce il valore di riduzione (positivo).
    /// </summary>
    public static int GetBodyTypeReduction(int body) => body switch
    {
        <= 2 => 0,
        <= 4 => 1,
        <= 7 => 2,
        <= 9 => 3,
        10 => 4,
        _ => 5
    };

    /// <summary>
    /// Applica il BTM al danno che ha superato l'armatura (e il moltiplicatore di locazione):
    /// un colpo che ha penetrato fa comunque almeno 1 danno; un colpo assorbito (0) resta 0.
    /// </summary>
    public static int ApplyBodyTypeModifier(int damageAfterArmor, int reduction)
        => damageAfterArmor <= 0 ? 0 : Math.Max(1, damageAfterArmor - reduction);

    /// <summary>Distanza massima, in metri, per un attacco in mischia.</summary>
    public const double MeleeMaxDistanceMeters = 2.5;

    /// <summary>TN per colpire in mischia: 10 + REF + Schivare del bersaglio + sua penalità da ferite (negativa).</summary>
    public static int GetMeleeTargetNumber(int targetRef, int dodgeSkill, int targetWoundPenalty)
        => 10 + targetRef + dodgeSkill + targetWoundPenalty;

    /// <summary>Esito del tiro per colpire: totale, fallimento critico, colpito.</summary>
    public sealed record ToHitResult(int Total, bool IsFumble, bool IsHit);

    /// <summary>
    /// Tiro per colpire: d10 (esplosivo) + stat + abilità + penalità ferite + modificatore arma + bonus cyber.
    /// Un 1 naturale sul primo dado è un fallimento critico e manca sempre; altrimenti colpisce se il totale
    /// raggiunge il TN. I dadi arrivano già tirati, così la regola è deterministica e testabile.
    /// </summary>
    public static ToHitResult ResolveToHit(GameDice.D10Roll roll, int stat, int skillLevel, int woundPenalty,
        int weaponModifier, int cyberBonus, int targetNumber)
    {
        int total = roll.Total + stat + skillLevel + woundPenalty + weaponModifier + cyberBonus;
        bool fumble = roll.IsFumble;
        return new ToHitResult(total, fumble, !fumble && total >= targetNumber);
    }

    /// <summary>Esito completo della catena armatura -> moltiplicatore -> BTM per un colpo a segno.</summary>
    /// <param name="Armor">Esito dell'armatura (SP affrontato, assorbito, danno dopo il moltiplicatore, ablazione).</param>
    /// <param name="Penetrating">Danno che ha superato l'armatura, prima del moltiplicatore di locazione.</param>
    /// <param name="BtmReduction">Riduzione del BTM del bersaglio.</param>
    /// <param name="Dealt">Danno finale da togliere alla salute.</param>
    public sealed record DamageResolution(ArmorResult Armor, int Penetrating, int BtmReduction, int Dealt);

    /// <summary>
    /// Catena completa del danno CP2020 su un colpo a segno: armatura (con ablazione), moltiplicatore di
    /// locazione e BTM. Non tocca la salute: la applica il chiamante con <see cref="HealthSystem.TakeDamage"/>.
    /// Modifica <paramref name="piece"/> (ablazione): chiamare sotto il lock del combattimento.
    /// </summary>
    public static DamageResolution ResolveDamage(ArmorPiece? piece, int rawDamage, int locationMultiplier, bool armorPiercing, int targetBody)
    {
        var armor = ApplyArmor(piece, rawDamage, locationMultiplier, armorPiercing);
        int penetrating = rawDamage - armor.Absorbed;
        int btm = GetBodyTypeReduction(targetBody);
        int dealt = ApplyBodyTypeModifier(armor.DamageDealt, btm);
        return new DamageResolution(armor, penetrating, btm, dealt);
    }

    /// <summary>
    /// Mappa il roll di locazione alla zona colpita:
    /// 1 = Testa (x2), 2-4 = Torso, 5 = Braccio Destro, 6 = Braccio Sinistro,
    /// 7-8 = Gamba Destra, 9-10 = Gamba Sinistra.
    /// </summary>
    public static HitLocationResult ResolveHitLocation(int roll)
    {
        return roll switch
        {
            1 => new HitLocationResult { Roll = roll, Name = "Testa", DamageMultiplier = 2, ArmorLocation = ArmorLocation.Head },
            2 or 3 or 4 => new HitLocationResult { Roll = roll, Name = "Torso", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Torso },
            5 => new HitLocationResult { Roll = roll, Name = "Braccio Destro", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Arms },
            6 => new HitLocationResult { Roll = roll, Name = "Braccio Sinistro", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Arms },
            7 or 8 => new HitLocationResult { Roll = roll, Name = "Gamba Destra", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Legs },
            9 or 10 => new HitLocationResult { Roll = roll, Name = "Gamba Sinistra", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Legs },
            _ => new HitLocationResult { Roll = roll, Name = "Torso", DamageMultiplier = 1, ArmorLocation = ArmorLocation.Torso }
        };
    }
}
