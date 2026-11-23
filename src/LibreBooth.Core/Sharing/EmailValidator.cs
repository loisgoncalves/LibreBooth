using System.Text.RegularExpressions;
using MimeKit;

namespace LibreBooth.Core.Sharing;

/// <summary>Vérification des adresses saisies sur le clavier tactile, avec correction des fautes de frappe courantes.</summary>
public static partial class EmailValidator
{
    private static readonly Dictionary<string, string> CommonTypos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmial.com"] = "gmail.com",
        ["gmai.com"] = "gmail.com",
        ["gmal.com"] = "gmail.com",
        ["gnail.com"] = "gmail.com",
        ["gmail.co"] = "gmail.com",
        ["gmail.con"] = "gmail.com",
        ["gmail.fr"] = "gmail.com",
        ["hotmial.com"] = "hotmail.com",
        ["hotmail.con"] = "hotmail.com",
        ["hotmial.fr"] = "hotmail.fr",
        ["hotmal.fr"] = "hotmail.fr",
        ["hotmail.f"] = "hotmail.fr",
        ["yaho.fr"] = "yahoo.fr",
        ["yahou.fr"] = "yahoo.fr",
        ["yahoo.f"] = "yahoo.fr",
        ["ornage.fr"] = "orange.fr",
        ["orange.f"] = "orange.fr",
        ["wanadoo.f"] = "wanadoo.fr",
        ["outlok.fr"] = "outlook.fr",
        ["outlook.f"] = "outlook.fr",
        ["iclou.com"] = "icloud.com",
        ["icloud.fr"] = "icloud.com",
        ["laposte.ne"] = "laposte.net",
        ["free.f"] = "free.fr",
        ["sfr.f"] = "sfr.fr",
    };

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$")]
    private static partial Regex AddressPattern();

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.Trim();
        return trimmed.Length <= 254
               && !trimmed.Contains("..", StringComparison.Ordinal)
               && AddressPattern().IsMatch(trimmed)
               && MailboxAddress.TryParse(trimmed, out _);
    }

    /// <summary>« marie@gmial.com » → « marie@gmail.com » ; null s'il n'y a rien à proposer.</summary>
    public static string? Suggest(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1) return null;
        var domain = address[(at + 1)..];
        return CommonTypos.TryGetValue(domain, out var fixedDomain) ? address[..(at + 1)] + fixedDomain : null;
    }
}
