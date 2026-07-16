using System.Buffers.Binary;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Iso;

public sealed record IsoFileEntry(string Path, uint ExtentSector, uint Length, bool IsDirectory)
{
    public long ByteOffset => (long)ExtentSector * Iso9660Image.SectorSize;
}

public sealed class Iso9660Image : IDisposable
{
    public const int SectorSize = 2048;
    private readonly FileStream _stream;
    public string Path { get; }
    public string VolumeIdentifier { get; }
    public IReadOnlyList<IsoFileEntry> Entries { get; }

    private Iso9660Image(string path, FileStream stream, string volume, IReadOnlyList<IsoFileEntry> entries)
    { Path = path; _stream = stream; VolumeIdentifier = volume; Entries = entries; }

    public static Iso9660Image Open(string path, DiagnosticBag? diagnostics = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        try
        {
            if (stream.Length < 17L * SectorSize) throw new FormatException("File is too small to be ISO9660", 0);
            var pvd = ReadAt(stream, 16L * SectorSize, SectorSize);
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new FormatException("ISO9660 primary volume descriptor not found", 16L * SectorSize);
            var volume = Encoding.ASCII.GetString(pvd, 40, 32).Trim(' ', '\0');
            var root = ParseRecord(pvd.AsSpan(156), string.Empty);
            var entries = new List<IsoFileEntry>();
            var visited = new HashSet<uint>();
            ReadDirectory(stream, root, string.Empty, entries, visited, 0);
            diagnostics?.Info("ISO001", $"Indexed {entries.Count} entries from volume '{volume}'", path);
            return new(path, stream, volume, entries);
        }
        catch { stream.Dispose(); throw; }
    }

    public IsoFileEntry? Find(string path)
    {
        var normalized = Normalize(path);
        return Entries.FirstOrDefault(x => Normalize(x.Path) == normalized);
    }

    public byte[] ReadFile(IsoFileEntry entry, int maximumBytes = 256 * 1024 * 1024)
    {
        if (entry.IsDirectory) throw new InvalidOperationException("Cannot read a directory as a file");
        if (entry.Length > maximumBytes) throw new FormatException("ISO file exceeds in-memory safety limit", entry.ByteOffset, entry.Length, maximumBytes);
        return ReadAt(_stream, entry.ByteOffset, checked((int)entry.Length));
    }

    public void Extract(IsoFileEntry entry, string outputPath)
    {
        if (entry.IsDirectory) throw new InvalidOperationException("Cannot extract a directory as a file");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
        _stream.Position = entry.ByteOffset;
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var remaining = (long)entry.Length; var buffer = new byte[1024 * 1024];
        while (remaining > 0) { var read = _stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length)); if (read == 0) throw new EndOfStreamException(); output.Write(buffer, 0, read); remaining -= read; }
    }

    public void Dispose() => _stream.Dispose();

    private static void ReadDirectory(FileStream stream, IsoFileEntry directory, string parent, List<IsoFileEntry> entries, HashSet<uint> visited, int depth)
    {
        if (depth > 32) throw new FormatException("ISO directory nesting exceeds safety limit", directory.ByteOffset);
        if (!visited.Add(directory.ExtentSector)) return;
        if (directory.Length > 64 * 1024 * 1024) throw new FormatException("ISO directory exceeds safety limit", directory.ByteOffset, directory.Length);
        var data = ReadAt(stream, directory.ByteOffset, checked((int)directory.Length));
        var position = 0;
        while (position < data.Length)
        {
            var length = data[position];
            if (length == 0) { position = ((position / SectorSize) + 1) * SectorSize; continue; }
            if (length < 34 || position > data.Length - length) throw new FormatException("Invalid ISO directory record", directory.ByteOffset + position, length, data.Length - position);
            var nameLength = data[position + 32];
            if (33 + nameLength > length) throw new FormatException("ISO directory name exceeds record", directory.ByteOffset + position);
            var rawName = data.AsSpan(position + 33, nameLength);
            if (nameLength == 1 && rawName[0] is 0 or 1) { position += length; continue; }
            var name = Encoding.ASCII.GetString(rawName);
            var semicolon = name.LastIndexOf(';'); if (semicolon >= 0) name = name[..semicolon];
            var childPath = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
            var child = ParseRecord(data.AsSpan(position, length), childPath);
            entries.Add(child);
            if (child.IsDirectory) ReadDirectory(stream, child, childPath, entries, visited, depth + 1);
            position += length;
        }
    }

    private static IsoFileEntry ParseRecord(ReadOnlySpan<byte> record, string path)
    {
        if (record.Length < 34) throw new FormatException("ISO directory record is too small", 0, 34, record.Length);
        var extent = BinaryPrimitives.ReadUInt32LittleEndian(record[2..6]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(record[10..14]);
        return new(path, extent, length, (record[25] & 2) != 0);
    }
    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count) throw new FormatException("ISO read is out of bounds", offset, count, stream.Length - offset);
        var data = new byte[count]; stream.Position = offset; stream.ReadExactly(data); return data;
    }
    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/').ToUpperInvariant();
}
