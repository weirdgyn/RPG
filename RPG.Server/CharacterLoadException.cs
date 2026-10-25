namespace RPG.Server;

/// <summary>
/// La scheda di un personaggio esiste su disco ma non può essere caricata (file corrotto,
/// non leggibile, in uso...). Distinta dall'assenza della scheda, che porta alla creazione
/// di un personaggio nuovo: in questo caso il file va lasciato intatto, altrimenti
/// salvare un personaggio nuovo ne sovrascriverebbe i dati.
/// </summary>
public class CharacterLoadException(string handle, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Handle { get; } = handle;
}
