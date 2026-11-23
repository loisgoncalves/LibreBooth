using System.Security.Cryptography;
using System.Text;

namespace LibreBooth.Core.Config;

/// <summary>
/// Chiffre le mot de passe SMTP avec la protection de données de Windows (DPAPI, compte utilisateur courant).
/// Le fichier config.json ne contient jamais le mot de passe en clair.
/// </summary>
public static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LibreBooth.SMTP.v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Renvoie une chaîne vide si le mot de passe vient d'un autre PC ou d'un autre compte Windows.</summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
