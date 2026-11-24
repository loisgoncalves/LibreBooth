using System.Net;
using System.Security.Cryptography;
using System.Text;
using LibreBooth.Core.Updates;

namespace LibreBooth.Core.Tests;

/// <summary>Version 2.0.0 : recherche et téléchargement des mises à jour (sans réseau : réponses simulées).</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes(new string('L', 300_000));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag = "v2.1.0", bool prerelease = false, string? digest = null, bool withChecksums = true, bool withInstaller = true,
        string host = "https://github.com")
    {
        var assets = new List<string>();
        if (withInstaller)
        {
            assets.Add($$"""
                { "name": "LibreBooth-2.1.0-Installation.exe", "size": {{InstallerBytes.Length}},
                  "digest": {{(digest == null ? "null" : $"\"sha256:{digest}\"")}},
                  "browser_download_url": "{{host}}/loisgoncalves/LibreBooth/releases/download/v2.1.0/LibreBooth-2.1.0-Installation.exe" }
                """);
        }
        if (withChecksums)
        {
            assets.Add($$"""
                { "name": "LibreBooth-2.1.0-SHA256.txt", "size": 197, "digest": null,
                  "browser_download_url": "{{host}}/loisgoncalves/LibreBooth/releases/download/v2.1.0/LibreBooth-2.1.0-SHA256.txt" }
                """);
        }
        assets.Add($$"""
            { "name": "LibreBooth-2.1.0-win-x86.zip", "size": 1000, "digest": null,
              "browser_download_url": "{{host}}/loisgoncalves/LibreBooth/releases/download/v2.1.0/LibreBooth-2.1.0-win-x86.zip" }
            """);
        return $$"""
            {
              "tag_name": "{{tag}}", "name": "LibreBooth 2.1.0", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/loisgoncalves/LibreBooth/releases/tag/{{tag}}",
              "body": "**Mises à jour automatiques** : l'admin installe les nouvelles versions.\n\n- Plus simple\n- Plus [rapide](https://example.org)\n\n## Télécharger\n\n- `LibreBooth-2.1.0-Installation.exe` : programme d'installation.",
              "assets": [ {{string.Join(",", assets)}} ]
            }
            """;
    }

    // ---------------------------------------------------------------- Numéros de version

    [Theory]
    [InlineData("v2.1.0", 2, 1, 0)]
    [InlineData("2.0.10", 2, 0, 10)]
    [InlineData("V3.2", 3, 2, 0)]
    public void Version_numbers_are_read_from_the_release_tag(string tag, int major, int minor, int patch)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("v2.1.0-beta")]
    [InlineData("dernière")]
    [InlineData("")]
    [InlineData(null)]
    public void Previews_and_unreadable_tags_are_ignored(string? tag) => Assert.False(UpdateChecker.TryParseVersion(tag, out _));

    [Theory]
    [InlineData("2.0.0", "2.0.1", true)]
    [InlineData("2.0.9", "2.0.10", true)]
    [InlineData("2.0.0", "2.1.0", true)]
    [InlineData("2.0.0", "3.0.0", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.1.0", "2.0.5", false)]
    [InlineData("2.0", "2.0.0", false)]
    public void Only_a_more_recent_version_is_offered(string installed, string published, bool offered)
    {
        Assert.True(UpdateChecker.TryParseVersion(published, out var version));
        var info = UpdateChecker.ParseRelease(ReleaseJson())! with { Version = version };
        Assert.Equal(offered, UpdateChecker.IsNewer(info, Version.Parse(installed)));
    }

    // ---------------------------------------------------------------- Lecture de la page de GitHub

    [Fact]
    public void The_installer_its_checksums_and_the_news_are_read_from_github()
    {
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: "AB" + new string('0', 62)));

        Assert.NotNull(info);
        Assert.Equal(new Version(2, 1, 0), info.Version);
        Assert.Equal("LibreBooth-2.1.0-Installation.exe", info.Installer.Name);
        Assert.Equal(InstallerBytes.Length, info.Installer.Size);
        Assert.Equal("ab" + new string('0', 62), info.Installer.Sha256);
        Assert.Equal("LibreBooth-2.1.0-SHA256.txt", info.Checksums?.Name);
        Assert.Equal("https://github.com/loisgoncalves/LibreBooth/releases/tag/v2.1.0", info.PageUrl.ToString());
        // Nouveautés sans la mise en forme, et sans la partie « Télécharger ».
        Assert.Equal("Mises à jour automatiques : l'admin installe les nouvelles versions.\n\n• Plus simple\n• Plus rapide", info.Notes);
    }

    [Fact]
    public void A_preview_or_a_release_without_installer_is_not_offered()
    {
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(prerelease: true)));
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(withInstaller: false)));
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson(tag: "derniere")));
    }

    [Fact]
    public async Task No_published_release_means_no_update()
    {
        using var http = new HttpClient(new FakeServer());
        Assert.Empty(await UpdateChecker.GetReleasesAsync(http, UpdateChecker.LatestReleaseUri, CancellationToken.None));
    }

    [Fact]
    public async Task The_latest_release_is_read_from_github()
    {
        var server = new FakeServer { [UpdateChecker.LatestReleaseUri.ToString()] = Encoding.UTF8.GetBytes(ReleaseJson()) };
        using var http = new HttpClient(server);

        var releases = await UpdateChecker.GetReleasesAsync(http, UpdateChecker.LatestReleaseUri, CancellationToken.None);

        Assert.Equal(new Version(2, 1, 0), Assert.Single(releases).Version);
    }

    // ---------------------------------------------------------------- Pré-versions (3.1.0)

    private static string ListJson(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static IReadOnlyList<UpdateInfo> Releases(params (string Tag, bool Prerelease)[] releases) =>
        UpdateChecker.ParseReleases(ListJson(releases.Select(r => ReleaseJson(r.Tag, r.Prerelease)).ToArray()));

    [Fact]
    public async Task The_list_of_releases_keeps_previews_and_drops_drafts()
    {
        var draft = ReleaseJson("v3.3.0").Replace("\"draft\": false", "\"draft\": true");
        var server = new FakeServer
        {
            [UpdateChecker.ReleasesUri.ToString()] = Encoding.UTF8.GetBytes(ListJson(draft, ReleaseJson("v3.2.0", prerelease: true), ReleaseJson("v3.1.0"))),
        };
        using var http = new HttpClient(server);

        var releases = await UpdateChecker.GetReleasesAsync(http, UpdateChecker.ReleasesUri, CancellationToken.None);

        Assert.Equal(new[] { ("3.2.0", true), ("3.1.0", false) }, releases.Select(r => (r.Version.ToString(3), r.IsPrerelease)));
        Assert.Equal(UpdateChecker.ReleasesUri.ToString(), Assert.Single(server.Requests));
    }

    [Fact]
    public void Previews_are_offered_only_when_asked()
    {
        var releases = Releases(("v3.2.0", true), ("v3.1.0", false), ("v3.0.0", false));
        var current = new Version(3, 1, 0);

        Assert.Null(UpdateChecker.Choose(releases, current, includePrereleases: false).Update);
        var choice = UpdateChecker.Choose(releases, current, includePrereleases: true);
        Assert.Equal(new Version(3, 2, 0), choice.Update?.Version);
        Assert.True(choice.Update?.IsPrerelease);
        Assert.Null(choice.ReturnToStable);
        Assert.Equal(new Version(3, 1, 0), choice.LatestStable?.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_newer_stable_version_is_offered_whatever_the_setting(bool includePrereleases)
    {
        var releases = Releases(("v3.2.1", false), ("v3.2.0", true), ("v3.1.0", false));

        var update = UpdateChecker.Choose(releases, new Version(3, 1, 0), includePrereleases).Update;

        Assert.Equal(new Version(3, 2, 1), update?.Version);
        Assert.False(update?.IsPrerelease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void From_a_preview_the_latest_stable_version_is_offered_to_return_to(bool includePrereleases)
    {
        var releases = Releases(("v3.2.0", true), ("v3.1.0", false));

        var choice = UpdateChecker.Choose(releases, new Version(3, 2, 0), includePrereleases);

        Assert.Null(choice.Update);
        Assert.Equal(new Version(3, 1, 0), choice.ReturnToStable?.Version);
    }

    [Fact]
    public void A_preview_that_became_the_stable_version_is_not_offered_again()
    {
        // La 3.1.0 essayée en pré-version, puis passée en version normale : même numéro, rien à proposer.
        var choice = UpdateChecker.Choose(Releases(("v3.1.0", false), ("v3.0.0", false)), new Version(3, 1, 0), includePrereleases: true);

        Assert.Null(choice.Update);
        Assert.Null(choice.ReturnToStable);
    }

    [Fact]
    public void The_stable_version_wins_over_a_preview_with_the_same_number()
    {
        var choice = UpdateChecker.Choose(Releases(("3.2.0", true), ("v3.2.0", false)), new Version(3, 1, 0), includePrereleases: true);

        Assert.False(choice.Update?.IsPrerelease);
    }

    [Fact]
    public void The_latest_address_still_gives_a_single_stable_release()
    {
        var releases = UpdateChecker.ParseReleases(ReleaseJson());

        Assert.False(Assert.Single(releases).IsPrerelease);
        Assert.Empty(UpdateChecker.ParseReleases("[]"));
    }

    [Fact]
    public void Requests_only_tell_github_the_librebooth_version()
    {
        using var http = UpdateChecker.CreateHttpClient("2.0.0", TimeSpan.FromSeconds(5));
        Assert.Equal("LibreBooth/2.0.0", http.DefaultRequestHeaders.UserAgent.ToString());
    }

    // ---------------------------------------------------------------- Téléchargement contrôlé

    [Theory]
    [InlineData("{0}  LibreBooth-2.1.0-Installation.exe")]
    [InlineData("{0} *LibreBooth-2.1.0-Installation.exe")]
    public void The_checksum_file_gives_the_installer_hash(string format)
    {
        var hash = Sha(InstallerBytes);
        var text = $"{new string('1', 64)}  LibreBooth-2.1.0-win-x86.zip\r\n" + string.Format(format, hash.ToUpperInvariant()) + "\r\n";
        Assert.Equal(hash, UpdateDownloader.FindHash(text, "LibreBooth-2.1.0-Installation.exe"));
        Assert.Null(UpdateDownloader.FindHash(text, "autre.exe"));
    }

    private static FakeServer ServerFor(string? checksums)
    {
        var server = new FakeServer
        {
            ["https://github.com/loisgoncalves/LibreBooth/releases/download/v2.1.0/LibreBooth-2.1.0-Installation.exe"] = InstallerBytes,
        };
        if (checksums != null) server["https://github.com/loisgoncalves/LibreBooth/releases/download/v2.1.0/LibreBooth-2.1.0-SHA256.txt"] = Encoding.ASCII.GetBytes(checksums);
        return server;
    }

    [Fact]
    public async Task A_matching_download_is_kept_and_its_progress_reported()
    {
        var hash = Sha(InstallerBytes);
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: hash))!;
        using var http = new HttpClient(ServerFor($"{hash}  LibreBooth-2.1.0-Installation.exe\n"));
        var progress = new List<double>();

        var path = await UpdateDownloader.DownloadAsync(http, info, _temp.Root, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Root, "LibreBooth-2.1.0-Installation.exe"), path);
        Assert.Equal(InstallerBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "LibreBooth-2.1.0-Installation.exe" }, Directory.GetFiles(_temp.Root).Select(Path.GetFileName));
        Assert.True(progress.Count > 1);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task A_different_file_is_erased_and_never_kept()
    {
        var wrong = new string('9', 64);
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: null))!;
        using var http = new HttpClient(ServerFor($"{wrong}  LibreBooth-2.1.0-Installation.exe\n"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownloader.DownloadAsync(http, info, _temp.Root, null, CancellationToken.None));

        Assert.Contains("empreinte", error.Message);
        Assert.Empty(Directory.GetFiles(_temp.Root));
    }

    [Fact]
    public async Task Checksums_that_disagree_stop_the_update_before_downloading()
    {
        var hash = Sha(InstallerBytes);
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: new string('7', 64)))!;
        var server = ServerFor($"{hash}  LibreBooth-2.1.0-Installation.exe\n");
        using var http = new HttpClient(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownloader.DownloadAsync(http, info, _temp.Root, null, CancellationToken.None));

        Assert.DoesNotContain(server.Requests, r => r.EndsWith("Installation.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_any_published_checksum_the_update_is_refused()
    {
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: null, withChecksums: false))!;
        using var http = new HttpClient(ServerFor(null));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownloader.DownloadAsync(http, info, _temp.Root, null, CancellationToken.None));

        Assert.Contains("Aucune empreinte", error.Message);
    }

    [Fact]
    public async Task The_github_checksum_alone_is_enough()
    {
        var info = UpdateChecker.ParseRelease(ReleaseJson(digest: Sha(InstallerBytes), withChecksums: false))!;
        using var http = new HttpClient(ServerFor(null));

        var path = await UpdateDownloader.DownloadAsync(http, info, _temp.Root, null, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Unencrypted_addresses_are_refused_except_on_this_pc()
    {
        var hash = Sha(InstallerBytes);
        var insecure = UpdateChecker.ParseRelease(ReleaseJson(digest: hash, withChecksums: false, host: "http://example.org"))!;
        using var http = new HttpClient(new FakeServer());
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownloader.DownloadAsync(http, insecure, _temp.Root, null, CancellationToken.None));

        var local = UpdateChecker.ParseRelease(ReleaseJson(digest: hash, withChecksums: false, host: "http://127.0.0.1:8765"))!;
        using var localHttp = new HttpClient(new FakeServer { [local.Installer.Url.ToString()] = InstallerBytes });
        Assert.True(File.Exists(await UpdateDownloader.DownloadAsync(localHttp, local, _temp.Root, null, CancellationToken.None)));
    }

    /// <summary>Progression appelée tout de suite (Progress&lt;T&gt; passerait par le contexte de l'interface).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Faux serveur : une réponse par adresse, 404 sinon.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new();
        public List<string> Requests { get; } = new();

        public byte[] this[string url]
        {
            set => _files[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (!_files.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
