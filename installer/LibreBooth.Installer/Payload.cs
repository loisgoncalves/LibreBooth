using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LibreBooth.Installer;

/// <summary>
/// Contenu à installer, ajouté à la fin de l'exécutable : une archive zip (dossiers « LibreBooth/ » et
/// « PiloteDNP/ »), puis une marque de 8 octets et la taille de l'archive sur 8 octets.
/// </summary>
internal sealed class Payload : IDisposable
{
    public const string Marker = "LBSETUP1";
    public const string AppFolder = "LibreBooth/";
    public const string DriverFolder = "PiloteDNP/";

    private readonly FileStream _file;

    private Payload(FileStream file, long start, long length)
    {
        _file = file;
        ExecutableLength = start;
        Archive = new ZipArchive(new SubStream(file, start, length), ZipArchiveMode.Read);
    }

    /// <summary>Taille du programme seul, sans le contenu : c'est aussi le programme de désinstallation.</summary>
    public long ExecutableLength { get; }

    public ZipArchive Archive { get; }

    /// <summary>
    /// Le pilote de la DNP est-il dans le contenu ? Il n'est que dans la version privée : la version publique
    /// (GitHub) ne contient aucun fichier de Canon ni de DNP.
    /// </summary>
    public bool HasDriver
    {
        get
        {
            foreach (var entry in Archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith(DriverFolder, StringComparison.Ordinal) && !name.EndsWith("/", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>Ouvre le contenu ajouté à l'exécutable ; null s'il n'y en a pas (programme de désinstallation).</summary>
    public static Payload? Open(string executable)
    {
        var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length < 16) return Close(file);
            var trailer = new byte[16];
            file.Seek(-16, SeekOrigin.End);
            ReadExactly(file, trailer);
            if (Encoding.ASCII.GetString(trailer, 0, 8) != Marker) return Close(file);
            var length = BitConverter.ToInt64(trailer, 8);
            var start = file.Length - 16 - length;
            if (length <= 0 || start <= 0) return Close(file);
            return new Payload(file, start, length);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Copie le programme seul (sans le contenu) : il sert à désinstaller.</summary>
    public void CopyExecutableTo(string path)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        _file.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[81920];
        var remaining = ExecutableLength;
        while (remaining > 0)
        {
            var read = _file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public void Dispose()
    {
        Archive.Dispose();
        _file.Dispose();
    }

    private static Payload? Close(FileStream file)
    {
        file.Dispose();
        return null;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

/// <summary>Fenêtre en lecture seule sur une partie d'un fichier.</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SubStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0, Math.Min(value, _length));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = _length - _position;
        if (available <= 0) return 0;
        _inner.Seek(_start + _position, SeekOrigin.Begin);
        var read = _inner.Read(buffer, offset, (int)Math.Min(count, available));
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
