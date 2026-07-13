using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed record Ssx3LevelArea(string Name, int OriginalIndex, uint SubChunkCount, uint MetadataChunkCount,
    uint FirstChunk, uint FirstSubChunk, int FirstGroup, int GroupCount);

public sealed class Ssx3Sdb
{
    public const int HeaderSize = 80;
    public const int LocationSize = 88;
    public required IReadOnlyList<Ssx3LevelArea> Areas { get; init; }
    public required uint ChunkCount { get; init; }
    public required uint SubChunkCount { get; init; }

    public static Ssx3Sdb Parse(ReadOnlySpan<byte> data, string sourceFile, DiagnosticBag diagnostics)
    {
        var reader = new BinarySpanReader(data);
        reader.Ensure(HeaderSize);
        reader.Skip(4);
        var unknownFloat = reader.ReadSingleLittle();
        var locationCount = reader.ReadUInt32Little();
        var chunkCount = reader.ReadUInt32Little();
        var subChunkCount = reader.ReadUInt32Little();
        reader.Skip(60);
        if (locationCount > 4096 || (ulong)HeaderSize + (ulong)locationCount * LocationSize > (ulong)data.Length)
            throw new FormatException("SDB location table is out of bounds", 8, locationCount * LocationSize, data.Length - HeaderSize);
        var raw = new List<(string Name, uint Sub, uint Meta, uint First, uint FirstSub)>((int)locationCount);
        for (var i = 0; i < locationCount; i++)
        {
            var name = reader.ReadFixedString(16);
            var sub = reader.ReadUInt32Little();
            var meta = reader.ReadUInt32Little();
            var first = reader.ReadUInt32Little();
            var firstSub = reader.ReadUInt32Little();
            reader.Skip(56);
            raw.Add((string.IsNullOrWhiteSpace(name) ? $"Area_{i:D2}" : name, sub, meta, first, firstSub));
        }
        var areas = new List<Ssx3LevelArea>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var current = raw[i];
            // SSX 3's SDB associates the following location's metadata chunk count
            // with the current location's SSB span. This is verified against BAM.SDB.
            var groupCount = i + 1 < raw.Count ? checked((int)raw[i + 1].Meta) : checked((int)(chunkCount - current.First));
            if ((ulong)current.First + (uint)Math.Max(0, groupCount) > chunkCount)
            {
                diagnostics.Warn("SDB002", $"Area {current.Name} group span exceeds declared chunk count", sourceFile, "locations", HeaderSize + i * LocationSize);
                groupCount = Math.Max(0, checked((int)chunkCount - (int)current.First));
            }
            areas.Add(new(current.Name, i, current.Sub, current.Meta, current.First, current.FirstSub, checked((int)current.First), groupCount));
        }
        diagnostics.Info("SDB001", $"Parsed {areas.Count} areas, {chunkCount} chunks, {subChunkCount} subchunks (header float {unknownFloat})", sourceFile);
        return new() { Areas = areas, ChunkCount = chunkCount, SubChunkCount = subChunkCount };
    }
}
