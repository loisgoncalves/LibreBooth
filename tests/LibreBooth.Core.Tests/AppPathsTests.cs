using LibreBooth.Core.Config;

namespace LibreBooth.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_data_folder_is_in_the_user_profile_not_in_documents()
    {
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LibreBooth");
        var candidates = AppPaths.DefaultCandidates().ToList();

        // Documents n'est proposé que s'il contient déjà des réglages d'une installation précédente.
        if (!File.Exists(Path.Combine(documents, "config.json")))
        {
            Assert.DoesNotContain(candidates, c => string.Equals(c, documents, StringComparison.OrdinalIgnoreCase));
        }
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LibreBooth");
        Assert.Contains(candidates, c => string.Equals(c, profile, StringComparison.OrdinalIgnoreCase));
        Assert.All(AppPaths.DefaultCandidates(demo: true), c => Assert.EndsWith("LibreBooth-demo", c));
    }

    [Fact]
    public void Imposed_data_folder_is_created_with_its_subfolders()
    {
        var root = Path.Combine(_temp.Root, "donnees");

        var paths = AppPaths.Create(root);

        Assert.Equal(Path.GetFullPath(root), paths.DataRoot);
        Assert.True(Directory.Exists(paths.EventsFolder));
        Assert.True(Directory.Exists(paths.TemplatesFolder));
        Assert.True(Directory.Exists(paths.LogsFolder));
        Assert.Empty(Directory.GetFiles(paths.DataRoot));
    }
}
