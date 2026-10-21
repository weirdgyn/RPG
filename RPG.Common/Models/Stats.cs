namespace RPG.Common.Models;

public class Stats
{
    public int INT { get; set; } = 6; // Intelligenza
    public int REF { get; set; } = 6; // Riflessi (chiave per iniziativa e combattimento)
    public int TECH { get; set; } = 5; // Tecnica
    public int COOL { get; set; } = 5; // Ragionamento / Sangue Freddo
    public int ATTR { get; set; } = 5; // Attrattiva
    public int MA { get; set; } = 5;   // Movimento (Movement Allowance)
    public int BODY { get; set; } = 6; // Costituzione / Forza Fisica
    public int EMP { get; set; } = 5;  // Empatia (scende con il Cyberware)
    /// <summary>
    /// Restituisce il valore della statistica in base al nome (es. "REF", "INT").
    /// </summary>
    public int GetStatValue(string statName)
    {
        if (string.IsNullOrWhiteSpace(statName)) return REF;

        return statName.Trim().ToUpperInvariant() switch
        {
            "INT" => INT,
            "REF" => REF,
            "TECH" => TECH,
            "COOL" => COOL,
            "ATTR" => ATTR,
            "MA" => MA,
            "BODY" => BODY,
            "EMP" => EMP,
            _ => REF // Fallback di sicurezza su REF
        };
    }
}
