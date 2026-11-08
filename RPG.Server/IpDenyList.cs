using System.Net;

namespace RPG.Server;

/// <summary>
/// Elenco di indirizzi IP (singoli o in notazione CIDR) a cui il server rifiuta la connessione.
/// Formato del file: una voce per riga ("203.0.113.7", "198.51.100.0/24", "2001:db8::/32"); le righe vuote
/// e quelle che iniziano con '#' sono ignorate, così come i commenti dopo un '#'. Il loopback non viene mai
/// rifiutato (per non chiudersi fuori dal proprio server).
/// </summary>
public sealed class IpDenyList
{
    private readonly record struct Rule(byte[] Network, int PrefixBits);

    private readonly List<Rule> _rules = [];

    public int Count => _rules.Count;

    /// <summary>Righe non valide trovate in <see cref="Parse"/>, con il numero di riga (da 1).</summary>
    public List<(int Line, string Text)> Invalid { get; } = [];

    public static IpDenyList Parse(IEnumerable<string> lines)
    {
        var list = new IpDenyList();
        int n = 0;
        foreach (var raw in lines)
        {
            n++;
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (!TryParseRule(line, out var rule)) list.Invalid.Add((n, line));
            else list._rules.Add(rule);
        }
        return list;
    }

    /// <summary>Legge il file; se non esiste restituisce un elenco vuoto.</summary>
    public static IpDenyList Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllLines(path)) : new IpDenyList();

    public bool IsDenied(IPAddress address)
    {
        address = ConnectionGuard.Normalize(address);
        if (IPAddress.IsLoopback(address)) return false;

        byte[] bytes = address.GetAddressBytes();
        foreach (var rule in _rules)
        {
            if (rule.Network.Length == bytes.Length && Matches(bytes, rule)) return true;
        }
        return false;
    }

    private static bool Matches(byte[] address, Rule rule)
    {
        int full = rule.PrefixBits / 8;
        int rest = rule.PrefixBits % 8;
        for (int i = 0; i < full; i++)
        {
            if (address[i] != rule.Network[i]) return false;
        }
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (address[full] & mask) == (rule.Network[full] & mask);
    }

    private static bool TryParseRule(string text, out Rule rule)
    {
        rule = default;
        string addrPart = text;
        int? prefix = null;

        int slash = text.IndexOf('/');
        if (slash >= 0)
        {
            addrPart = text[..slash].Trim();
            if (!int.TryParse(text[(slash + 1)..].Trim(), out int p) || p < 0) return false;
            prefix = p;
        }

        if (!IPAddress.TryParse(addrPart, out var ip)) return false;
        ip = ConnectionGuard.Normalize(ip);
        byte[] bytes = ip.GetAddressBytes();

        // Un prefisso scritto per un IPv4 mappato (es. ::ffff:1.2.3.4/120) non è supportato: si scrive l'IPv4.
        int max = bytes.Length * 8;
        int bits = prefix ?? max;
        if (bits > max) return false;

        rule = new Rule(bytes, bits);
        return true;
    }
}
