using LibreBooth.Core.Design;
using LibreBooth.Core.Util;

namespace LibreBooth.Core.Events;

/// <summary>Un événement des versions 1.x converti au rangement de la version 2.0.0.</summary>
public sealed record EventUpgrade(string From, string To)
{
    /// <summary>Où se trouve maintenant ce dossier d'événement (le même s'il n'a pas été converti).</summary>
    public static string? Follow(IEnumerable<EventUpgrade> upgrades, string? folder) =>
        upgrades.FirstOrDefault(u => EventStore.SamePath(u.From, folder))?.To ?? folder;
}

/// <summary>
/// Conversion des événements des versions 1.0 à 1.6, au premier lancement de la 2.0.0 : mêmes photos, nouveaux noms
/// de dossiers. « originaux » devient « Photos », « montages » devient « Montages » (les fiches des sessions partent dans
/// le dossier caché), « refusees » devient « Photos refusées » ; event.json et « personnalisation » partent dans le dossier
/// caché ; le dossier de l'événement prend le nom « 2026-11-23 Mariage Julie et Tom ». Rien n'est jamais écrasé ni
/// supprimé, sauf les dossiers vides de l'ancien envoi par e-mail.
/// </summary>
public sealed partial class EventStore
{
    private const string LegacyInfoFile = "event.json";
    private const string LegacyOriginals = "originaux";
    private const string LegacyComposites = "montages";
    private const string LegacyRefused = "refusees";
    private const string LegacyDemoPrints = "impressions-demo";
    private const string LegacyCustomization = "personnalisation";
    private static readonly string[] LegacyEmailFolders = { "outbox", "envoyes", "echecs" };
    /// <summary>Ce qui restait de l'envoi par e-mail (retiré en 2.0.0), gardé dans le dossier caché s'il n'était pas vide.</summary>
    private const string OldEmailFolder = "ancien-envoi-email";

    /// <summary>Convertit tous les événements rangés à la manière des versions 1.x.</summary>
    public IReadOnlyList<EventUpgrade> UpgradeOldEvents()
    {
        var done = new List<EventUpgrade>();
        lock (_lock)
        {
            foreach (var dir in Directory.GetDirectories(EventsFolder))
            {
                if (!NeedsUpgrade(dir)) continue;
                try
                {
                    done.Add(UpgradeLegacyEvent(dir));
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Conversion de l'événement impossible : {Folder}", dir);
                }
            }
        }
        return done;
    }

    /// <summary>Événement rangé à la manière des versions 1.x (noms de dossiers exacts, majuscules comprises).</summary>
    internal static bool NeedsUpgrade(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, LegacyInfoFile))) return true;
            var names = Directory.EnumerateDirectories(folder).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.Ordinal);
            return names.Contains(LegacyOriginals) || names.Contains(LegacyComposites) || names.Contains(LegacyRefused)
                   || names.Contains(LegacyDemoPrints) || names.Contains(LegacyCustomization) || LegacyEmailFolders.Any(names.Contains);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private EventUpgrade UpgradeLegacyEvent(string folder)
    {
        var data = BoothEvent.DataFolderFor(folder);
        CreateHiddenFolder(data);

        void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // L'étape sera retentée au prochain lancement ; les photos restent où elles sont.
                _log.Warning(ex, "Conversion de {Folder} : {Step} impossible pour l'instant", Path.GetFileName(folder), what);
            }
        }

        // 1. Ce qui ne sert qu'à LibreBooth part dans le dossier caché.
        Step("personnalisation", () => MoveChildFolder(folder, LegacyCustomization, data, CustomizationStore.FolderName));
        Step("fiches des sessions", () =>
        {
            var composites = ExactChild(folder, LegacyComposites);
            if (composites == null) return;
            var sessions = Path.Combine(data, BoothEvent.SessionsFolderName);
            Directory.CreateDirectory(sessions);
            foreach (var json in Directory.GetFiles(composites, "*.json"))
            {
                var target = Path.Combine(sessions, Path.GetFileName(json));
                if (!Path.GetFileName(json).StartsWith('.') && !File.Exists(target)) File.Move(json, target);
            }
        });

        // 2. Les dossiers de photos prennent leur nouveau nom.
        Step("Photos", () => RenameChild(folder, LegacyOriginals, BoothEvent.PhotosFolderName));
        Step("Montages", () => RenameChild(folder, LegacyComposites, BoothEvent.CompositesFolderName));
        Step("Photos refusées", () => RenameChild(folder, LegacyRefused, BoothEvent.RefusedFolderName));
        Step("impressions du mode démo", () => RenameChild(folder, LegacyDemoPrints, BoothEvent.DemoPrintsFolderName));

        // 3. L'ancien envoi par e-mail : dossiers vides supprimés, sinon gardés dans le dossier caché.
        foreach (var name in LegacyEmailFolders)
        {
            Step(name, () =>
            {
                var path = ExactChild(folder, name);
                if (path == null) return;
                if (!Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any()) Directory.Delete(path, recursive: true);
                else MoveChildFolder(folder, name, Path.Combine(data, OldEmailFolder), name);
            });
        }

        // 4. Les informations de l'événement en dernier : tant qu'elles sont à l'ancienne place, la conversion reprendra.
        var info = Path.Combine(data, BoothEvent.InfoFileName);
        Step("informations", () =>
        {
            var old = Path.Combine(folder, LegacyInfoFile);
            if (!File.Exists(old)) return;
            if (File.Exists(info)) File.Move(old, Path.Combine(data, "event-version-1.json"), overwrite: true);
            else File.Move(old, info);
        });

        // 5. Le dossier prend le nom de l'événement : « 2026-11-23 Mariage Julie et Tom ».
        var result = folder;
        Step("nom du dossier", () =>
        {
            var current = File.Exists(info) ? JsonDefaults.Read<EventInfo>(info) : null;
            if (current == null || string.IsNullOrWhiteSpace(current.Name)) return;
            var target = UniqueFolder(FolderNameFor(current.Name, current.Date), except: folder);
            if (string.Equals(target, folder, StringComparison.Ordinal)) return;
            MoveDirectory(folder, target);
            result = target;
        });

        _cache.Remove(folder);
        _log.Information("Événement converti au rangement 2.0 : {From} → {To}", Path.GetFileName(folder), Path.GetFileName(result));
        return new EventUpgrade(folder, result);
    }

    /// <summary>Sous-dossier dont le nom est exactement celui-ci (majuscules comprises), ou null.</summary>
    private static string? ExactChild(string parent, string name) =>
        Directory.EnumerateDirectories(parent).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.Ordinal));

    private static void RenameChild(string parent, string oldName, string newName)
    {
        var from = ExactChild(parent, oldName);
        if (from == null) return;
        var to = Path.Combine(parent, newName);
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(to)) MoveDirectory(from, to);
        else MergeFolder(from, to);
    }

    private static void MoveChildFolder(string parent, string name, string newParent, string newName)
    {
        var from = ExactChild(parent, name);
        if (from == null) return;
        Directory.CreateDirectory(newParent);
        var to = Path.Combine(newParent, newName);
        if (Directory.Exists(to)) MergeFolder(from, to);
        else Directory.Move(from, to);
    }

    /// <summary>Déplace le contenu d'un dossier dans un autre qui existe déjà, sans rien écraser.</summary>
    private static void MergeFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            var name = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            for (var i = 2; File.Exists(target); i++) target = Path.Combine(to, $"{name} ({i}){extension}");
            File.Move(file, target);
        }
        foreach (var dir in Directory.GetDirectories(from)) MergeFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
        if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
    }
}
