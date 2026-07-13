using System.Buffers.Binary;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public enum BigEndianLayout { Big, MixedArchiveSizeLittle, Little }
public sealed record BigArchiveEntry(string Name, uint Offset, uint Size, int OriginalIndex);

public sealed class BigArchive
{
    public required string Path { get; init; }
    public required string Magic { get; init; }
    public required uint ArchiveSize { get; init; }
    public required uint HeaderSize { get; init; }
    public required BigEndianLayout Layout { get; init; }
    public required IReadOnlyList<BigArchiveEntry> Entries { get; init; }

    public static BigArchive Open(string path, DiagnosticBag? diagnostics = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[16];
        stream.ReadExactly(prefix);
        var magic = Encoding.ASCII.GetString(prefix[..4]);
        if (magic is not ("BIGF" or "BIG4" or "BIG ")) throw new FormatException($"Unsupported BIG magic '{magic}'", 0, 4, stream.Length);
        var layouts = new[]
        {
            (BigEndianLayout.Big, ReadBe(prefix[4..]), ReadBe(prefix[8..]), ReadBe(prefix[12..]), true),
            (BigEndianLayout.MixedArchiveSizeLittle, ReadLe(prefix[4..]), ReadBe(prefix[8..]), ReadBe(prefix[12..]), true),
            (BigEndianLayout.Little, ReadLe(prefix[4..]), ReadLe(prefix[8..]), ReadLe(prefix[12..]), false)
        };
        Exception? last = null;
        foreach (var candidate in layouts)
        {
            if (!Plausible(candidate.Item2, candidate.Item3, candidate.Item4, stream.Length)) continue;
            try
            {
                var header = new byte[candidate.Item4];
                stream.Position = 0;
                stream.ReadExactly(header);
                var entries = ParseEntries(header, candidate.Item3, candidate.Item4, candidate.Item2, stream.Length, candidate.Item5);
                diagnostics?.Info("BIG001", $"Indexed {entries.Count} entries ({candidate.Item1})", path);
                return new() { Path = path, Magic = magic, ArchiveSize = candidate.Item2, HeaderSize = candidate.Item4, Layout = candidate.Item1, Entries = entries };
            }
            catch (Exception ex) { last = ex; }
        }
        throw new FormatException($"No plausible BIG header layout: {last?.Message}", 0, 16, stream.Length);
    }

    public BigArchiveEntry? Find(string name)
    {
        var normalized = Normalize(name);
        return Entries.FirstOrDefault(x => Normalize(x.Name) == normalized);
    }

    public void Extract(BigArchiveEntry entry, string outputPath)
    {
        var fullOutput = System.IO.Path.GetFullPath(outputPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullOutput)!);
        using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((ulong)entry.Offset + entry.Size > (ulong)input.Length) throw new FormatException("BIG entry is out of bounds", entry.Offset, entry.Size, input.Length - entry.Offset);
        input.Position = entry.Offset;
        using var output = new FileStream(fullOutput, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyExactly(input, output, entry.Size);
    }

    private static List<BigArchiveEntry> ParseEntries(byte[] header, uint count, uint headerSize, uint archiveSize, long actualSize, bool big)
    {
        if (count > 1_000_000) throw new FormatException("BIG entry count exceeds safety limit", 8);
        var result = new List<BigArchiveEntry>((int)count);
        var cursor = 16;
        for (var index = 0; index < count; index++)
        {
            if (cursor + 9 > headerSize) throw new FormatException("BIG entry table ended early", cursor, 9, headerSize - cursor);
            uint offset = big ? ReadBe(header.AsSpan(cursor)) : ReadLe(header.AsSpan(cursor));
            uint size = big ? ReadBe(header.AsSpan(cursor + 4)) : ReadLe(header.AsSpan(cursor + 4));
            cursor += 8;
            var start = cursor;
            while (cursor < headerSize && header[cursor] != 0) cursor++;
            if (cursor >= headerSize) throw new FormatException("BIG entry name is not terminated", start);
            var name = Encoding.UTF8.GetString(header, start, cursor - start);
            cursor++;
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException("BIG entry name is empty", start);
            if ((ulong)offset + size > (ulong)actualSize || (ulong)offset + size > archiveSize)
                throw new FormatException($"BIG entry '{name}' extends beyond the archive", offset, size, actualSize - offset);
            result.Add(new(name, offset, size, index));
        }
        return result;
    }

    private static bool Plausible(uint size, uint count, uint header, long actual) => size is >= 16 && size <= actual && header is >= 16 && header <= size && (ulong)count * 9 <= header - 16;
    private static uint ReadLe(ReadOnlySpan<byte> x) => BinaryPrimitives.ReadUInt32LittleEndian(x);
    private static uint ReadBe(ReadOnlySpan<byte> x) => BinaryPrimitives.ReadUInt32BigEndian(x);
    private static string Normalize(string x) => x.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    private static void CopyExactly(Stream input, Stream output, long count)
    {
        var buffer = new byte[1024 * 1024];
        while (count > 0) { var read = input.Read(buffer, 0, (int)Math.Min(count, buffer.Length)); if (read == 0) throw new EndOfStreamException(); output.Write(buffer, 0, read); count -= read; }
    }
}
