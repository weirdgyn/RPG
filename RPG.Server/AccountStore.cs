using System.Security.Cryptography;
using System.Text.Json;

namespace RPG.Server;

/// <summary>Esito di <see cref="AccountStore.Verify"/>.</summary>
public enum AuthResult
{
    Ok,
    /// <summary>Username inesistente o password errata (volutamente indistinguibili).</summary>
    BadCredentials,
    /// <summary>Troppi tentativi falliti su questo username: bloccato per qualche minuto.</summary>
    LockedOut
}

/// <summary>
/// Account dei giocatori: username e password (hash PBKDF2-SHA256 con sale casuale per account), salvati in
/// accounts.json. Gli account si creano solo dalla console del server. Dopo troppi tentativi falliti su uno
/// stesso username l'account è bloccato per qualche minuto, da qualunque IP arrivino i tentativi.
/// L'orologio è passato dal chiamante per poter testare il blocco senza attese.
/// </summary>
public sealed class AccountStore(string? path = null, int iterations = AccountStore.DefaultIterations)
{
    public const string FileName = "accounts.json";
    public const int DefaultIterations = 600_000;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private const int MaxTrackedNames = 5000;

    public class AccountRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public int Iterations { get; set; }
    }

    private sealed class Attempts
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime? LockedUntil;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, AccountRecord> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Attempts> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path = path;
    private readonly int _iterations = iterations;
    // Hash di un account inesistente: verificare contro questo rende uguale il tempo di risposta.
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(16);

    /// <summary>Percorso di accounts.json nella cartella dati fissa del server (vedi <see cref="ServerPaths"/>).</summary>
    public static string ResolvePath() => ServerPaths.Resolve(FileName);

    public static AccountStore Load(string path, int iterations = DefaultIterations)
    {
        var store = new AccountStore(path, iterations);
        if (File.Exists(path))
        {
            var list = JsonSerializer.Deserialize<List<AccountRecord>>(File.ReadAllText(path)) ?? [];
            foreach (var a in list)
                if (!string.IsNullOrWhiteSpace(a.Name)) store._accounts[a.Name] = a;
        }
        return store;
    }

    public int Count { get { lock (_lock) return _accounts.Count; } }

    public IReadOnlyList<string> Names()
    {
        lock (_lock) return [.. _accounts.Values.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public bool Exists(string username)
    {
        lock (_lock) return _accounts.ContainsKey(username.Trim());
    }

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "Lo username non può essere vuoto.";
        string u = username.Trim();
        if (u.Length > InputLimits.MaxUsernameLength) return $"Username troppo lungo (massimo {InputLimits.MaxUsernameLength} caratteri).";
        if (InputLimits.HasControlChars(u)) return "Lo username non può contenere caratteri di controllo.";
        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) return $"Password troppo corta (minimo {MinPasswordLength} caratteri).";
        if (password.Length > MaxPasswordLength) return $"Password troppo lunga (massimo {MaxPasswordLength} caratteri).";
        return null;
    }

    public bool TryCreate(string username, string password, out string error)
    {
        error = ValidateUsername(username) ?? ValidatePassword(password) ?? string.Empty;
        if (error.Length > 0) return false;

        string name = username.Trim();
        lock (_lock)
        {
            if (_accounts.ContainsKey(name)) { error = $"L'account '{name}' esiste già."; return false; }
            _accounts[name] = NewRecord(name, password);
            Save();
            return true;
        }
    }

    public bool TrySetPassword(string username, string password, out string error)
    {
        error = ValidatePassword(password) ?? string.Empty;
        if (error.Length > 0) return false;

        lock (_lock)
        {
            if (!_accounts.TryGetValue(username.Trim(), out var existing)) { error = $"L'account '{username}' non esiste."; return false; }
            _accounts[existing.Name] = NewRecord(existing.Name, password);
            _attempts.Remove(existing.Name);
            Save();
            return true;
        }
    }

    public bool Remove(string username)
    {
        lock (_lock)
        {
            if (!_accounts.Remove(username.Trim())) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Controlla le credenziali. Con <see cref="AuthResult.Ok"/> <paramref name="canonicalName"/> è lo username
    /// con le maiuscole con cui l'account è stato creato.
    /// </summary>
    public AuthResult Verify(string username, string password, DateTime nowUtc, out string? canonicalName)
    {
        canonicalName = null;
        string key = (username ?? string.Empty).Trim();
        password ??= string.Empty;

        AccountRecord? record;
        lock (_lock)
        {
            if (_attempts.TryGetValue(key, out var a) && a.LockedUntil is { } until)
            {
                if (nowUtc < until) return AuthResult.LockedOut;
                _attempts.Remove(key);
            }
            _accounts.TryGetValue(key, out record);
        }

        // L'hash (costoso) è calcolato fuori dal lock. Se l'account non esiste si verifica comunque contro un hash finto.
        bool ok;
        if (password.Length > MaxPasswordLength)
        {
            ok = false;
        }
        else if (record != null)
        {
            ok = CryptographicOperations.FixedTimeEquals(
                Derive(password, Convert.FromBase64String(record.Salt), record.Iterations),
                Convert.FromBase64String(record.Hash));
        }
        else
        {
            Derive(password, _dummySalt, _iterations);
            ok = false;
        }

        lock (_lock)
        {
            if (ok)
            {
                _attempts.Remove(key);
                canonicalName = record!.Name;
                return AuthResult.Ok;
            }

            if (_attempts.Count >= MaxTrackedNames) PurgeAttempts(nowUtc);
            if (!_attempts.TryGetValue(key, out var att))
            {
                att = new Attempts();
                _attempts[key] = att;
            }
            while (att.Failures.Count > 0 && nowUtc - att.Failures.Peek() >= FailureWindow) att.Failures.Dequeue();
            att.Failures.Enqueue(nowUtc);
            if (att.Failures.Count >= MaxFailures)
            {
                att.LockedUntil = nowUtc + LockDuration;
                att.Failures.Clear();
            }
            return AuthResult.BadCredentials;
        }
    }

    private AccountRecord NewRecord(string name, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new AccountRecord
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt, _iterations)),
            Iterations = _iterations
        };
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private void PurgeAttempts(DateTime nowUtc)
    {
        foreach (var k in _attempts.Where(p => (p.Value.LockedUntil ?? DateTime.MinValue) <= nowUtc &&
                                              (p.Value.Failures.Count == 0 || nowUtc - p.Value.Failures.Peek() >= FailureWindow))
                                   .Select(p => p.Key).ToList())
            _attempts.Remove(k);
    }

    // Scrittura atomica: file temporaneo e poi sostituzione. Chiamato sempre sotto _lock.
    private void Save()
    {
        if (_path == null) return;
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_accounts.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
