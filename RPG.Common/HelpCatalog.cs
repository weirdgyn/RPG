using System.Text.Json;

namespace RPG.Common;

public sealed class HelpEntry
{
    public string Command { get; set; } = string.Empty;
    public string Parameters { get; set; } = "-";
    public string Description { get; set; } = string.Empty;
}

public static class HelpCatalog
{
    public static string NormalizeCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();

        return trimmed.StartsWith('/')
            ? trimmed
            : "/" + trimmed;
    }

    public static Dictionary<string, HelpEntry> ParseFromJson(string json)
    {
        var result = new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            // Creare un'istanza condivisa di JsonSerializerOptions per evitare di crearne una nuova ogni volta
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<HelpEntry>>(json, options);

            if (entries == null)
                return result;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Command))
                    continue;

                var key = NormalizeCommand(entry.Command);

                result[key] = new HelpEntry
                {
                    Command = key,
                    Parameters = entry.Parameters,
                    Description = entry.Description
                };
            }
        }
        catch
        {
            // Se il json è malformato, basta tornare vuoto: /help non rompe il client.
        }

        return result;
    }
}