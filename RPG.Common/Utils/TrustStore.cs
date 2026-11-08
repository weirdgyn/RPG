using System.Security.Cryptography;
using System.Text.Json;

namespace RPG.Common.Utils;

/// <summary>Esito del controllo dell'impronta del certificato di un server.</summary>
public enum TrustResult
{
    /// <summary>Impronta uguale a quella memorizzata.</summary>
    Trusted,
    /// <summary>Server mai visto: alla prima connessione l'impronta viene memorizzata (trust on first use).</summary>
    NewServer,
    /// <summary>Impronta diversa da quella memorizzata: possibile intercettazione o certificato rigenerato.</summary>
    Mismatch
}

/// <summary>
/// Memoria delle impronte (SHA-256) dei certificati dei server già visitati, nello stile di known_hosts di SSH.
/// Il server usa un certificato autofirmato: la fiducia non viene da una CA ma dal fatto che l'impronta
/// resti la stessa a ogni connessione.
/// </summary>
public sealed class TrustStore
{
    private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly object _lock = new();

    public TrustStore(string? path = null) => _path = path;

    public static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    /// <summary>Impronta SHA-256 in esadecimale maiuscolo dei byte DER del certificato.</summary>
    public static string Fingerprint(byte[] rawCertData) => Convert.ToHexString(SHA256.HashData(rawCertData));

    /// <summary>Legge il file; un file mancante o illeggibile dà un archivio vuoto.</summary>
    public static TrustStore Load(string path)
    {
        var store = new TrustStore(path);
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (data != null)
                    foreach (var (k, v) in data) store._pins[k] = v;
            }
        }
        catch { /* archivio danneggiato: si riparte vuoti, la prossima connessione ri-memorizza */ }
        return store;
    }

    public string? Get(string host, int port)
    {
        lock (_lock) return _pins.TryGetValue(Key(host, port), out var fp) ? fp : null;
    }

    public TrustResult Check(string host, int port, string fingerprint)
    {
        string? known = Get(host, port);
        if (known == null) return TrustResult.NewServer;
        return string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase) ? TrustResult.Trusted : TrustResult.Mismatch;
    }

    /// <summary>Memorizza (o sostituisce) l'impronta e la salva su disco. False se il salvataggio fallisce.</summary>
    public bool Remember(string host, int port, string fingerprint)
    {
        lock (_lock)
        {
            _pins[Key(host, port)] = fingerprint;
            if (_path == null) return true;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
