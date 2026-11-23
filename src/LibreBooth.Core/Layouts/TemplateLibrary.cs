using LibreBooth.Core.Util;
using Serilog;
using SkiaSharp;

namespace LibreBooth.Core.Layouts;

/// <summary>Un modèle installé : un dossier avec layout.json, le cadre PNG et un aperçu.</summary>
public sealed class Template
{
    public Template(string folder, LayoutDefinition layout)
    {
        Folder = Path.GetFullPath(folder);
        Id = Path.GetFileName(Folder);
        Layout = layout;
    }

    public string Id { get; }
    public string Folder { get; }
    public LayoutDefinition Layout { get; set; }
    public string Name => Layout.Name;
    public int ShotCount => Layout.Zones.Count;
    public string LayoutFile => Path.Combine(Folder, TemplateLibrary.LayoutFileName);
    /// <summary>
    /// Aperçu du modèle monté avec les photos d'exemple. Avant la 1.4.0, il s'appelait « apercu.jpg » : ces anciens
    /// aperçus (faits en 1.3.0 avec d'autres photos d'exemple) sont jetés et refaits une fois.
    /// </summary>
    public string PreviewPath => Path.Combine(Folder, TemplateLibrary.PreviewFileName);
    public string LegacyPreviewPath => Path.Combine(Folder, TemplateLibrary.LegacyPreviewFileName);

    public string? FramePath => ResolveFile(Layout.FrameFile);
    public string? BackgroundPath => ResolveFile(Layout.BackgroundFile);

    private string? ResolveFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var path = Path.IsPathRooted(name) ? name : Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public override string ToString() => Name;
}

/// <summary>Gestion des modèles du dossier Modeles des données de LibreBooth.</summary>
public sealed class TemplateLibrary
{
    public const string LayoutFileName = "layout.json";
    public const string PreviewFileName = "apercu-exemple.jpg";
    public const string LegacyPreviewFileName = "apercu.jpg";
    private const string TrashFolderName = "_corbeille";
    private readonly ILogger _log = Serilog.Log.ForContext<TemplateLibrary>();

    public TemplateLibrary(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public IReadOnlyList<Template> LoadAll()
    {
        var list = new List<Template>();
        foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(dir).StartsWith('_')) continue;
            var template = TryLoad(dir);
            if (template != null) list.Add(template);
        }
        return list;
    }

    public Template? Find(string id) => string.IsNullOrWhiteSpace(id) ? null : TryLoad(Path.Combine(Root, id));

    public Template? TryLoad(string folder)
    {
        var file = Path.Combine(folder, LayoutFileName);
        if (!File.Exists(file)) return null;
        try
        {
            var layout = JsonDefaults.Read<LayoutDefinition>(file);
            return layout == null ? null : new Template(folder, layout.Normalize());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Modèle illisible ignoré : {File}", file);
            return null;
        }
    }

    public Template Save(Template template)
    {
        template.Layout.Normalize();
        JsonDefaults.Write(template.LayoutFile, template.Layout);
        AtomicFile.TryDelete(template.PreviewPath);
        AtomicFile.TryDelete(template.LegacyPreviewPath);
        return template;
    }

    /// <summary>Crée un nouveau modèle (le PNG du cadre est copié dans son dossier).</summary>
    public Template Create(string name, LayoutDefinition layout, string? framePngSource = null)
    {
        var folder = UniqueFolder(name);
        Directory.CreateDirectory(folder);
        if (!string.IsNullOrEmpty(framePngSource))
        {
            File.Copy(framePngSource, Path.Combine(folder, "cadre.png"), overwrite: true);
            layout.FrameFile = "cadre.png";
        }
        layout.Name = string.IsNullOrWhiteSpace(name) ? layout.Name : name.Trim();
        return Save(new Template(folder, layout));
    }

    /// <summary>
    /// Importe un cadre PNG fait dans Canva/Photoshop : sa taille devient celle du modèle et ses zones
    /// transparentes deviennent les emplacements des photos.
    /// </summary>
    public Template ImportFrame(string pngPath, string name)
    {
        using var bitmap = SKBitmap.Decode(pngPath) ?? throw new InvalidDataException("Ce fichier n'est pas une image PNG lisible.");
        var layout = new LayoutDefinition
        {
            Name = name,
            Width = bitmap.Width,
            Height = bitmap.Height,
            FrameFile = "cadre.png",
            BackgroundColor = "#FFFFFF",
        };

        var ratio = (double)bitmap.Height / bitmap.Width;
        if (ratio is > 2.5 and < 3.5)
        {
            layout.DuplicateStrip = true;
            layout.PrintProfile = PrintProfiles.Strips;
        }

        layout.Zones = ZoneDetector.Detect(bitmap);
        _log.Information("Cadre importé : {Name} ({W}×{H}), {Zones} zone(s) détectée(s)", name, bitmap.Width, bitmap.Height, layout.Zones.Count);
        return Create(name, layout, pngPath);
    }

    public Template Duplicate(Template source)
    {
        var folder = UniqueFolder(source.Name + " copie");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source.Folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals(PreviewFileName, StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(LegacyPreviewFileName, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(folder, fileName), overwrite: true);
        }
        var layout = JsonDefaults.Clone(source.Layout);
        layout.Name = source.Name + " (copie)";
        return Save(new Template(folder, layout));
    }

    /// <summary>Le modèle n'est pas effacé : il part dans le dossier Modeles\_corbeille.</summary>
    public void Delete(Template template)
    {
        var trash = Path.Combine(Root, TrashFolderName);
        Directory.CreateDirectory(trash);
        var target = Path.Combine(trash, $"{template.Id}_{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.Move(template.Folder, target);
        _log.Information("Modèle déplacé dans la corbeille : {Id}", template.Id);
    }

    private string UniqueFolder(string name)
    {
        var slug = TextUtil.Slug(name);
        var folder = Path.Combine(Root, slug);
        for (var i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Root, $"{slug}-{i}");
        return folder;
    }
}
