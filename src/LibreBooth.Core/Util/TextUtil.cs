using System.Globalization;
using System.Text;

namespace LibreBooth.Core.Util;

public static class TextUtil
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Transforme un texte libre en nom de dossier sûr : « Mariage d'Élodie » → « mariage-d-elodie ».</summary>
    public static string Slug(string? text, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(text)) return "sans-nom";

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasDash = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].Trim('-');
        return slug.Length == 0 ? "sans-nom" : slug;
    }

    /// <summary>Remplace {evenement}, {date} (22.11.2026) et {date_longue} (22 novembre 2026) dans un texte de modèle.</summary>
    public static string ApplyVariables(string? template, string eventName, DateTime eventDate) =>
        (template ?? string.Empty)
            .Replace("{evenement}", eventName, StringComparison.OrdinalIgnoreCase)
            .Replace("{événement}", eventName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date_longue}", FormatMediumDate(eventDate), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", FormatDate(eventDate), StringComparison.OrdinalIgnoreCase);

    public static string FormatDate(DateTime date) => date.ToString("dd.MM.yyyy", French);

    /// <summary>« 22 novembre 2026 » (« 1er » pour le premier du mois).</summary>
    public static string FormatMediumDate(DateTime date) =>
        (date.Day == 1 ? "1er" : date.Day.ToString(French)) + " " + date.ToString("MMMM yyyy", French);

    public static string FormatLongDate(DateTime date) => date.ToString("dddd d MMMM yyyy", French);

    public static string ToUpperFrench(string text) => text.ToUpper(French);
}
