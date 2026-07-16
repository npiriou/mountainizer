using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed record Ssx3LevelArea(string Name, int OriginalIndex, uint ChunkCount, uint SubChunkCount,
    uint FirstSubChunk, uint FirstChunk, int FirstGroup, int GroupCount);

public sealed record Ssx3ChunkDescriptor(Vector4 BoundsMinimumSsx, Vector4 BoundsMaximumSsx,
    IReadOnlyList<float> Values, IReadOnlyList<uint> Words);

public sealed record Ssx3SubChunkDescriptor(int Index, ushort ResourceCount, ushort SubChunkId,
    IReadOnlyList<ushort> Metadata, IReadOnlyList<uint> ReservedWords)
{
    // The first four metadata halfwords describe framing; the following slots
    // mirror resource-type counts. Slot 13 therefore declares Type-9 SSH textures.
    public ushort DeclaredType9TextureCount => Metadata.Count > 13 ? Metadata[13] : (ushort)0;
}

public sealed class Ssx3Sdb
{
    public const int HeaderSize = 80;
    public const int LocationSize = 88;
    public const int ChunkDescriptorSize = 96;
    public const int SubChunkDescriptorSize = 68;
    public required IReadOnlyList<Ssx3LevelArea> Areas { get; init; }
    public required IReadOnlyList<Ssx3ChunkDescriptor> Chunks { get; init; }
    public required IReadOnlyList<Ssx3SubChunkDescriptor> SubChunks { get; init; }
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
        var raw = new List<(string Name, uint Chunks, uint SubChunks, uint FirstSubChunk, uint FirstChunk)>((int)locationCount);
        for (var i = 0; i < locationCount; i++)
        {
            var name = reader.ReadFixedString(16);
            var locationChunkCount = reader.ReadUInt32Little();
            var locationSubChunkCount = reader.ReadUInt32Little();
            var firstSubChunk = reader.ReadUInt32Little();
            var firstChunk = reader.ReadUInt32Little();
            reader.Skip(56);
            raw.Add((string.IsNullOrWhiteSpace(name) ? $"Area_{i:D2}" : name, locationChunkCount, locationSubChunkCount,
                firstSubChunk, firstChunk));
        }
        var areas = new List<Ssx3LevelArea>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var current = raw[i];
            // SSB has one CEND-terminated group per SDB subchunk. BAM.SDB's extractor-style
            // location span starts at this row's anchor and ends after the following row's
            // declared subchunk count; Type-3 track placement confirms the cross-row layout.
            var groupCount = i + 1 < raw.Count ? checked((int)raw[i + 1].SubChunks) : checked((int)(subChunkCount - current.FirstSubChunk));
            if ((ulong)current.FirstSubChunk + (uint)Math.Max(0, groupCount) > subChunkCount)
            {
                diagnostics.Warn("SDB002", $"Area {current.Name} group span exceeds declared subchunk count", sourceFile,
                    "locations", HeaderSize + i * LocationSize);
                groupCount = Math.Max(0, checked((int)subChunkCount - (int)current.FirstSubChunk));
            }
            areas.Add(new(current.Name, i, current.Chunks, current.SubChunks, current.FirstSubChunk, current.FirstChunk,
                checked((int)current.FirstSubChunk), groupCount));
        }

        var descriptorOffset = (HeaderSize + checked((int)locationCount) * LocationSize + 15) & ~15;
        var requiredBytes = (ulong)descriptorOffset + (ulong)chunkCount * ChunkDescriptorSize
            + (ulong)subChunkCount * SubChunkDescriptorSize;
        if (requiredBytes > (ulong)data.Length)
            throw new FormatException("SDB chunk/subchunk descriptor tables are out of bounds", descriptorOffset,
                checked((long)(requiredBytes - (ulong)descriptorOffset)), data.Length - descriptorOffset);
        reader.Seek(descriptorOffset);
        var chunks = new List<Ssx3ChunkDescriptor>(checked((int)chunkCount));
        for (var i = 0; i < chunkCount; i++)
        {
            var minimum = reader.ReadVector4(); var maximum = reader.ReadVector4();
            var values = new float[12];
            for (var value = 0; value < values.Length; value++) values[value] = reader.ReadSingleLittle();
            var words = new uint[4];
            for (var word = 0; word < words.Length; word++) words[word] = reader.ReadUInt32Little();
            chunks.Add(new(minimum, maximum, values, words));
        }
        var subChunks = new List<Ssx3SubChunkDescriptor>(checked((int)subChunkCount));
        for (var i = 0; i < subChunkCount; i++)
        {
            var resourceCount = reader.ReadUInt16Little(); var subChunkId = reader.ReadUInt16Little();
            var metadata = new ushort[18];
            for (var value = 0; value < metadata.Length; value++) metadata[value] = reader.ReadUInt16Little();
            var reserved = new uint[7];
            for (var word = 0; word < reserved.Length; word++) reserved[word] = reader.ReadUInt32Little();
            subChunks.Add(new(i, resourceCount, subChunkId, metadata, reserved));
        }
        diagnostics.Info("SDB001", $"Parsed {areas.Count} areas, {chunkCount} chunks, {subChunkCount} subchunks (header float {unknownFloat})", sourceFile);
        return new() { Areas = areas, Chunks = chunks, SubChunks = subChunks, ChunkCount = chunkCount, SubChunkCount = subChunkCount };
    }
}
