using System.IO.Compression;
using LibreBooth.Core.Hardware;

namespace LibreBooth.Core.Tests;

/// <summary>
/// Ajout du SDK Canon : depuis le zip téléchargé chez Canon (qui contient les versions 32 et 64 bits), seuls
/// EDSDK.dll et EdsImage.dll 32 bits doivent être gardés.
/// </summary>
public sealed class CanonSdkImportTests : IDisposable
{
    private const ushort X86 = 0x014C;
    private const ushort X64 = 0x8664;
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_32_bit_dlls_are_taken_from_the_canon_zip()
    {
        var zip = WriteZip("EDSDK_Win.zip", new Dictionary<string, byte[]>
        {
            ["EDSDK_64/Dll/EDSDK.dll"] = FakeDll(X64, 1),
            ["EDSDK_64/Dll/EdsImage.dll"] = FakeDll(X64, 2),
            ["EDSDK/Dll/EDSDK.dll"] = FakeDll(X86, 3),
            ["EDSDK/Dll/EdsImage.dll"] = FakeDll(X86, 4),
            ["Document/EDSDK_API.pdf"] = new byte[] { 1, 2, 3 },
        });
        var target = Path.Combine(_temp.Root, "Pilotes");

        CanonSdkImport.Import(zip, target);

        Assert.Equal(FakeDll(X86, 3), File.ReadAllBytes(Path.Combine(target, "EDSDK.dll")));
        Assert.Equal(FakeDll(X86, 4), File.ReadAllBytes(Path.Combine(target, "EdsImage.dll")));
        Assert.Equal(2, Directory.GetFiles(target).Length);
    }

    [Fact]
    public void A_zip_inside_the_zip_is_searched_too()
    {
        var inner = WriteZip("EDSDK_v13.zip", new Dictionary<string, byte[]>
        {
            ["EDSDK/Dll/EDSDK.dll"] = FakeDll(X86, 5),
            ["EDSDK/Dll/EdsImage.dll"] = FakeDll(X86, 6),
        });
        var outer = WriteZip("telechargement-canon.zip", new Dictionary<string, byte[]>
        {
            ["Licence.pdf"] = new byte[] { 9 },
            ["EDSDK_v13.zip"] = File.ReadAllBytes(inner),
        });
        var target = Path.Combine(_temp.Root, "Pilotes");

        CanonSdkImport.Import(outer, target);

        Assert.Equal(FakeDll(X86, 5), File.ReadAllBytes(Path.Combine(target, "EDSDK.dll")));
        Assert.Equal(FakeDll(X86, 6), File.ReadAllBytes(Path.Combine(target, "EdsImage.dll")));
    }

    [Fact]
    public void The_unzipped_folder_or_EDSDK_dll_itself_can_be_chosen()
    {
        var sdk = _temp.Sub("EDSDK");
        var dll = _temp.Sub(Path.Combine("EDSDK", "Dll"));
        File.WriteAllBytes(Path.Combine(dll, "EDSDK.dll"), FakeDll(X86, 7));
        File.WriteAllBytes(Path.Combine(dll, "EdsImage.dll"), FakeDll(X86, 8));

        var fromFolder = Path.Combine(_temp.Root, "depuis-dossier");
        CanonSdkImport.Import(sdk, fromFolder);
        var fromFile = Path.Combine(_temp.Root, "depuis-fichier");
        CanonSdkImport.Import(Path.Combine(dll, "EDSDK.dll"), fromFile);

        Assert.Equal(FakeDll(X86, 8), File.ReadAllBytes(Path.Combine(fromFolder, "EdsImage.dll")));
        Assert.Equal(FakeDll(X86, 7), File.ReadAllBytes(Path.Combine(fromFile, "EDSDK.dll")));
    }

    [Fact]
    public void A_64_bit_only_sdk_is_refused_and_nothing_is_written()
    {
        var zip = WriteZip("EDSDK_64.zip", new Dictionary<string, byte[]>
        {
            ["EDSDK_64/Dll/EDSDK.dll"] = FakeDll(X64, 1),
            ["EDSDK_64/Dll/EdsImage.dll"] = FakeDll(X64, 2),
        });
        var target = Path.Combine(_temp.Root, "Pilotes");

        var error = Assert.Throws<InvalidDataException>(() => CanonSdkImport.Import(zip, target));

        Assert.Contains("32 bits", error.Message);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A_zip_without_the_sdk_is_refused()
    {
        var zip = WriteZip("autre.zip", new Dictionary<string, byte[]> { ["photo.jpg"] = new byte[] { 1 } });

        var error = Assert.Throws<InvalidDataException>(() => CanonSdkImport.Import(zip, Path.Combine(_temp.Root, "Pilotes")));

        Assert.Contains("EDSDK.dll", error.Message);
    }

    [Fact]
    public void Only_windows_32_bit_files_are_recognised()
    {
        Assert.True(CanonSdkImport.IsX86(FakeDll(X86, 0)));
        Assert.False(CanonSdkImport.IsX86(FakeDll(X64, 0)));
        Assert.False(CanonSdkImport.IsX86(new byte[] { (byte)'M', (byte)'Z' }));
        Assert.False(CanonSdkImport.IsX86(new byte[200]));
    }

    private string WriteZip(string name, Dictionary<string, byte[]> entries)
    {
        var path = Path.Combine(_temp.Root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            using var stream = zip.CreateEntry(entryName).Open();
            stream.Write(data);
        }
        return path;
    }

    /// <summary>Début d'une DLL Windows (en-têtes MZ et PE) pour la machine indiquée, avec un octet distinctif.</summary>
    private static byte[] FakeDll(ushort machine, byte marker)
    {
        var data = new byte[256];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(data, 0x3C);
        data[0x80] = (byte)'P';
        data[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(data, 0x84);
        data[255] = marker;
        return data;
    }
}
