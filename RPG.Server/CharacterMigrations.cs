using RPG.Common.Models;

namespace RPG.Server;

/// <summary>
/// Versioning e migrazioni delle schede personaggio.
///
/// Ogni scheda salvata porta con sé la propria <see cref="Character.SchemaVersion"/>. Al caricamento le
/// migrazioni mancanti vengono applicate in ordine, così una scheda scritta da una versione precedente del
/// server resta leggibile. Le migrazioni devono essere idempotenti e veloci: girano ad ogni load (la scheda
/// su disco viene riscritta, con la versione aggiornata, alla prossima modifica).
///
/// Per aggiungere una versione: incrementare <see cref="CurrentVersion"/> e aggiungere lo step in
/// <see cref="Migrate"/>. Gli step esistenti non vanno riordinati né rimossi: sono applicati in sequenza.
/// </summary>
public static class CharacterMigrations
{
    /// <summary>Versione di schema corrente: quella scritta da <see cref="CharacterRepository.SaveCharacter"/>.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Porta la scheda all'ultima versione di schema. Restituisce true se l'ha modificata.
    /// </summary>
    /// <exception cref="CharacterLoadException">La scheda è stata scritta da una versione più recente del server.</exception>
    public static bool Migrate(Character character)
    {
        if (character.SchemaVersion > CurrentVersion)
        {
            throw new CharacterLoadException(character.Handle,
                $"La scheda di '{character.Handle}' è stata salvata da una versione più recente del server ({character.SchemaVersion} > {CurrentVersion}).");
        }

        if (character.SchemaVersion == CurrentVersion) return false;

        if (character.SchemaVersion < 1)
        {
            // v0 -> v1: la salute era pista BODY*5; ora è di 40 caselle per tutti.
            MigrateHealthTo40Boxes(character);
        }

        character.SchemaVersion = CurrentVersion;
        return true;
    }

    /// <summary>Schede con la vecchia salute (BODY*5): porta a 40 caselle mantenendo il danno assoluto.</summary>
    private static void MigrateHealthTo40Boxes(Character character)
    {
        character.Health ??= new HealthSystem();
        var h = character.Health;
        if (h.MaxHealth == HealthSystem.TrackBoxes) return;

        int damage = Math.Max(0, h.MaxHealth - h.CurrentHealth);
        h.MaxHealth = HealthSystem.TrackBoxes;
        h.CurrentHealth = Math.Max(0, HealthSystem.TrackBoxes - damage);
    }
}
