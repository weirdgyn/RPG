using System.Net;

namespace RPG.Server;

/// <summary>Esito di <see cref="ConnectionGuard.TryAdmit"/>.</summary>
public enum AdmitResult
{
    Allowed,
    Banned,
    TooManyFromAddress,
    RateLimited
}

/// <summary>
/// Difese per indirizzo IP applicate prima ancora del login: connessioni simultanee per IP, frequenza delle
/// nuove connessioni e ban temporaneo di chi accumula connessioni "cattive" (chiuse senza login valido:
/// scadenza, spazzatura, primo pacchetto diverso da Login). Il loopback è sempre ammesso.
/// L'orologio è passato dal chiamante per poter testare la logica senza attese.
/// Thread-safe: tutti gli stati sono protetti da un unico lock (le operazioni sono brevissime).
/// </summary>
public sealed class ConnectionGuard(
    int maxPerAddress = ConnectionGuard.DefaultMaxPerAddress,
    int maxNewPerWindow = ConnectionGuard.DefaultMaxNewPerWindow,
    TimeSpan? rateWindow = null,
    int maxStrikes = ConnectionGuard.DefaultMaxStrikes,
    TimeSpan? strikeWindow = null,
    TimeSpan? banDuration = null)
{
    public const int DefaultMaxPerAddress = 5;
    public const int DefaultMaxNewPerWindow = 10;
    public static readonly TimeSpan DefaultRateWindow = TimeSpan.FromSeconds(60);
    public const int DefaultMaxStrikes = 5;
    public static readonly TimeSpan DefaultStrikeWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultBanDuration = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Open;
        public readonly Queue<DateTime> Arrivals = new();
        public readonly Queue<DateTime> Strikes = new();
        public DateTime? BannedUntil;
    }

    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    private readonly int _maxPerAddress = maxPerAddress;
    private readonly int _maxNewPerWindow = maxNewPerWindow;
    private readonly TimeSpan _rateWindow = rateWindow ?? DefaultRateWindow;
    private readonly int _maxStrikes = maxStrikes;
    private readonly TimeSpan _strikeWindow = strikeWindow ?? DefaultStrikeWindow;
    private readonly TimeSpan _banDuration = banDuration ?? DefaultBanDuration;

    /// <summary>IPv4 mappato su IPv6 e IPv4 sono lo stesso indirizzo.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Decide se una nuova connessione da <paramref name="address"/> è ammessa. Se lo è, la conta come aperta:
    /// il chiamante DEVE chiamare <see cref="Release"/> alla chiusura. Se non lo è non va chiamato Release.
    /// </summary>
    public AdmitResult TryAdmit(IPAddress address, DateTime nowUtc)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return AdmitResult.Allowed;

        lock (_lock)
        {
            if (!_entries.TryGetValue(address, out var e))
            {
                e = new Entry();
                _entries[address] = e;
            }

            if (e.BannedUntil is { } until)
            {
                if (nowUtc < until) return AdmitResult.Banned;
                e.BannedUntil = null;
                e.Strikes.Clear();
            }

            Trim(e.Arrivals, nowUtc, _rateWindow);
            if (e.Arrivals.Count >= _maxNewPerWindow) return AdmitResult.RateLimited;
            if (e.Open >= _maxPerAddress) return AdmitResult.TooManyFromAddress;

            e.Arrivals.Enqueue(nowUtc);
            e.Open++;
            return AdmitResult.Allowed;
        }
    }

    /// <summary>Chiude una connessione ammessa con <see cref="TryAdmit"/>.</summary>
    public void Release(IPAddress address, DateTime nowUtc)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return;

        lock (_lock)
        {
            if (_entries.TryGetValue(address, out var e) && e.Open > 0) e.Open--;
            Cleanup(address, e, nowUtc);
        }
    }

    /// <summary>
    /// Registra una connessione "cattiva". Raggiunta la soglia nella finestra l'indirizzo è bannato:
    /// restituisce true solo nel momento in cui scatta il ban (per loggarlo una volta sola).
    /// </summary>
    public bool ReportStrike(IPAddress address, DateTime nowUtc)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue(address, out var e))
            {
                e = new Entry();
                _entries[address] = e;
            }

            if (e.BannedUntil is { } until && nowUtc < until) return false;

            Trim(e.Strikes, nowUtc, _strikeWindow);
            e.Strikes.Enqueue(nowUtc);
            if (e.Strikes.Count < _maxStrikes) return false;

            e.BannedUntil = nowUtc + _banDuration;
            e.Strikes.Clear();
            return true;
        }
    }

    /// <summary>Un login riuscito azzera le connessioni cattive accumulate dall'indirizzo.</summary>
    public void ReportSuccess(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return;

        lock (_lock)
        {
            if (_entries.TryGetValue(address, out var e)) e.Strikes.Clear();
        }
    }

    public bool IsBanned(IPAddress address, DateTime nowUtc)
    {
        address = Normalize(address);
        lock (_lock)
        {
            return _entries.TryGetValue(address, out var e) && e.BannedUntil is { } until && nowUtc < until;
        }
    }

    private static void Trim(Queue<DateTime> q, DateTime nowUtc, TimeSpan window)
    {
        while (q.Count > 0 && nowUtc - q.Peek() >= window) q.Dequeue();
    }

    // Evita che la tabella cresca all'infinito con gli indirizzi visti una volta sola.
    private void Cleanup(IPAddress address, Entry? e, DateTime nowUtc)
    {
        if (e == null || e.Open > 0) return;
        Trim(e.Arrivals, nowUtc, _rateWindow);
        Trim(e.Strikes, nowUtc, _strikeWindow);
        if (e.Arrivals.Count == 0 && e.Strikes.Count == 0 && (e.BannedUntil == null || nowUtc >= e.BannedUntil))
            _entries.Remove(address);
    }
}
