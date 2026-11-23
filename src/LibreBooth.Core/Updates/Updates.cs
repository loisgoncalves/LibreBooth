using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LibreBooth.Core.Updates;

/// <summary>Un fichier d'une version publiée sur GitHub.</summary>
/// <param name="Sha256">Empreinte donnée par GitHub (« digest »), en minuscules, ou null.</param>
public sealed record UpdateFile(string Name, Uri Url, long Size, string? Sha256);

/// <summary>Une version de LibreBooth publiée sur GitHub (page « Releases »).</summary>
/// <param name="Checksums">Le fichier LibreBooth-x.y.z-SHA256.txt publié avec la version, s'il existe.</param>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri PageUrl, UpdateFile Installer, UpdateFile? Checksums);

/// <summary>
/// Recherche d'une nouvelle version de LibreBooth sur GitHub : seulement une lecture de la page publique des
/// versions (aucune donnée envoyée, à part le numéro de version de LibreBooth dans l'en-tête de la requête).
/// </summary>
public static partial class UpdateChecker
{
    public const string Repository = "loisgoncalves/LibreBooth";
    public static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");

    public static HttpClient CreateHttpClient(string appVersion, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LibreBooth", appVersion));
        return http;
    }

    /// <summary>« v2.1.0 », « 2.1.0 » ou « 2.1 » ; les préversions (« 2.1.0-beta ») ne comptent pas.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = VersionPattern().Match(text?.Trim() ?? "");
        if (!match.Success) return false;
        version = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
        return true;
    }

    /// <summary>Plus récente que la version installée ? (2.0 et 2.0.0 sont la même version.)</summary>
    public static bool IsNewer(UpdateInfo info, Version current) => Normalize(info.Version) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Lit la réponse de GitHub pour la dernière version. Null si elle n'est pas utilisable : brouillon, préversion,
    /// numéro illisible, ou pas de programme d'installation parmi ses fichiers.
    /// </summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Text(root, "tag_name");
        if (!TryParseVersion(tag, out var version)) return null;

        UpdateFile? installer = null;
        UpdateFile? checksums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name == null || !Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url)) continue;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var digest = Text(asset, "digest");
                var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                var file = new UpdateFile(name, url, size, sha);
                if (name.EndsWith("-Installation.exe", StringComparison.OrdinalIgnoreCase)) installer ??= file;
                else if (name.EndsWith("-SHA256.txt", StringComparison.OrdinalIgnoreCase)) checksums ??= file;
            }
        }
        if (installer == null) return null;

        var page = Uri.TryCreate(Text(root, "html_url"), UriKind.Absolute, out var html) ? html : ReleasesPage;
        return new UpdateInfo(version, tag!, ShortNotes(Text(root, "body") ?? ""), page, installer, checksums);
    }

    /// <summary>Dernière version publiée ; null s'il n'y en a aucune d'utilisable.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(HttpClient http, Uri source, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        // Aucune version publiée.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return ParseRelease(json);
    }

    /// <summary>
    /// Les nouveautés pour l'admin : le texte de la version jusqu'à la partie « Télécharger », sans la mise en forme
    /// (gras, liens, titres), en quelques lignes.
    /// </summary>
    public static string ShortNotes(string body, int maxLines = 8)
    {
        var lines = new List<string>();
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var title = line.TrimStart('#').Trim();
                if (title.Contains("Télécharger", StringComparison.OrdinalIgnoreCase) || title.Contains("Download", StringComparison.OrdinalIgnoreCase)) break;
                line = title;
            }
            line = MarkdownLink().Replace(line, "$1").Replace("**", "").Replace("`", "");
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..];
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                continue;
            }
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > maxLines) lines = lines.Take(maxLines).Append("…").ToList();
        return string.Join("\n", lines);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^[vV]?(\d{1,4})\.(\d{1,4})(?:\.(\d{1,6}))?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
}

/// <summary>
/// Téléchargement du programme d'installation d'une nouvelle version, contrôlé par son empreinte SHA-256 (celle du
/// fichier LibreBooth-x.y.z-SHA256.txt publié avec la version, et celle que donne GitHub). Un fichier différent de
/// celui attendu est effacé et n'est jamais lancé.
/// </summary>
public static partial class UpdateDownloader
{
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IProgress<double>? progress,
        CancellationToken cancellation)
    {
        var name = Path.GetFileName(info.Installer.Name);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Nom de fichier refusé : " + info.Installer.Name);
        CheckAddress(info.Installer.Url);

        var expected = await ExpectedHashAsync(http, info, cancellation).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        var partial = target + ".telechargement";
        try
        {
            string actual;
            using (var response = await http.GetAsync(info.Installer.Url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? info.Installer.Size;
                await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
                }
                actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Le fichier téléchargé n'est pas celui attendu (empreinte SHA-256 différente) : il a été effacé sans être lancé.");
            }
            File.Move(partial, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>L'empreinte attendue : celle du fichier SHA256.txt et celle de GitHub doivent être d'accord.</summary>
    private static async Task<string> ExpectedHashAsync(HttpClient http, UpdateInfo info, CancellationToken cancellation)
    {
        string? fromFile = null;
        if (info.Checksums != null)
        {
            CheckAddress(info.Checksums.Url);
            var text = await http.GetStringAsync(info.Checksums.Url, cancellation).ConfigureAwait(false);
            fromFile = FindHash(text, info.Installer.Name)
                       ?? throw new InvalidDataException($"Le fichier {info.Checksums.Name} ne donne pas l'empreinte de {info.Installer.Name}.");
        }
        var fromGitHub = info.Installer.Sha256;
        if (fromFile != null && fromGitHub != null && fromFile != fromGitHub)
        {
            throw new InvalidDataException("Les empreintes publiées pour cette version ne concordent pas : mise à jour refusée.");
        }
        return fromFile ?? fromGitHub ?? throw new InvalidDataException("Aucune empreinte n'est publiée pour cette version : mise à jour refusée.");
    }

    /// <summary>Empreinte d'un fichier dans une liste « empreinte  nom » (format de sha256sum et du script de LibreBooth).</summary>
    public static string? FindHash(string checksums, string fileName)
    {
        foreach (var line in checksums.Replace("\r", "").Split('\n'))
        {
            var match = HashLine().Match(line.Trim());
            if (match.Success && string.Equals(match.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>Seulement en HTTPS (ou sur ce PC même, pour les essais).</summary>
    private static void CheckAddress(Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) return;
        throw new InvalidDataException("Adresse de téléchargement refusée : " + url);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Fichier temporaire : nettoyé au prochain téléchargement.
        }
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex HashLine();
}
