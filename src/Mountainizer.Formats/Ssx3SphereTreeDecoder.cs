using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3SphereTreeDecoder
{
    private const int FileHeaderSize = 16;
    private const int RecordHeaderSize = 16;
    private const int FixedMetadataSize = 108;
    private const int LevelSize = 12;

    public static SphereTreeCollisionAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source,
        int trackId, int resourceId, string? name = null)
    {
        if (data.Length < FileHeaderSize)
            throw new FormatException("Sphere-tree collision payload is truncated", source.LogicalOffset ?? 0, FileHeaderSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var version = reader.ReadUInt16Little(); var treeCount = reader.ReadUInt16Little();
        var recordsOffset = checked((int)reader.ReadUInt32Little());
        var recordsEnd = checked((int)reader.ReadUInt32Little());
        var usedEnd = checked((int)reader.ReadUInt32Little());
        if (version != 3) throw new FormatException($"Unsupported sphere-tree version {version}", source.LogicalOffset ?? 0, 2, data.Length);
        if (treeCount > 4096) throw new FormatException($"Sphere-tree record count {treeCount} exceeds the safety limit",
            (source.LogicalOffset ?? 0) + 2, treeCount, 4096);
        if (recordsOffset < FileHeaderSize || recordsEnd < recordsOffset || usedEnd < recordsEnd || usedEnd > data.Length)
            throw new FormatException("Sphere-tree section offsets are out of bounds", source.LogicalOffset ?? 0, usedEnd, data.Length);

        var trees = new List<SphereTreeRecord>(treeCount); var position = recordsOffset;
        for (var treeIndex = 0; treeIndex < treeCount; treeIndex++)
        {
            reader.Seek(position); var alignmentBytes = checked((int)reader.ReadUInt32Little());
            var packedPayloadSize = checked((int)reader.ReadUInt32Little()); var compressionType = reader.ReadUInt32Little();
            var childLevelCount = checked((int)reader.ReadUInt32Little());
            if (alignmentBytes is < 1 or > 4 || childLevelCount is < 0 or > 4 || packedPayloadSize < 0)
                throw new FormatException($"Sphere-tree record {treeIndex} header is invalid", reader.AbsolutePosition - RecordHeaderSize, RecordHeaderSize, recordsEnd - position);
            if (alignmentBytes != 4 - packedPayloadSize % 4)
                throw new FormatException($"Sphere-tree record {treeIndex} has inconsistent packed alignment", reader.AbsolutePosition - RecordHeaderSize, alignmentBytes, 4 - packedPayloadSize % 4);
            var recordSize = checked(RecordHeaderSize + FixedMetadataSize + childLevelCount * LevelSize + packedPayloadSize + alignmentBytes);
            if (position > recordsEnd - recordSize)
                throw new FormatException($"Sphere-tree record {treeIndex} exceeds the record section", (source.LogicalOffset ?? 0) + position, recordSize, recordsEnd - position);

            var correction = reader.ReadVector3();
            var retainedMetadataVector = Ssx3Coordinates.ToMountainizer(reader.ReadVector3());
            var retainedSymmetricMatrix = new float[9];
            for (var i = 0; i < retainedSymmetricMatrix.Length; i++) retainedSymmetricMatrix[i] = reader.ReadSingleLittle();
            var retainedInverseSymmetricMatrix = new float[9];
            for (var i = 0; i < retainedInverseSymmetricMatrix.Length; i++) retainedInverseSymmetricMatrix[i] = reader.ReadSingleLittle();
            var levels = new SphereTreeLevel[childLevelCount + 1];
            levels[0] = ReadLevel(ref reader);
            for (var i = 1; i < levels.Length; i++) levels[i] = ReadLevel(ref reader);
            ValidateMetadata(treeIndex, correction, retainedMetadataVector, retainedSymmetricMatrix,
                retainedInverseSymmetricMatrix, levels, source, position);
            var packedPayload = reader.ReadBytes(packedPayloadSize).ToArray();
            var padding = reader.ReadBytes(alignmentBytes).ToArray();
            var decodedNodeMasks = DecodeNodeMasks(treeIndex, packedPayload, compressionType, levels, source, position);
            var nodeLevels = BuildNodeLevels(decodedNodeMasks, levels);
            trees.Add(new(correction, retainedMetadataVector, retainedSymmetricMatrix, retainedInverseSymmetricMatrix,
                levels, packedPayload, padding, compressionType, decodedNodeMasks, nodeLevels));
            position = checked(position + recordSize);
        }
        if (position != recordsEnd)
            throw new FormatException("Sphere-tree records do not end at the declared boundary", (source.LogicalOffset ?? 0) + position, recordsEnd - position, data.Length - position);

        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Collision Sphere Tree", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Version"] = version, ["TreeCount"] = trees.Count, ["MaximumDepth"] = trees.Count == 0 ? 0 : trees.Max(x => x.Levels.Count),
            ["PackedPayloadBytes"] = trees.Sum(x => x.PackedPayloadSize),
            ["DecodedNodeBytes"] = trees.Sum(x => x.DecodedNodeMasks.Length),
            ["ReferencedNodes"] = trees.Sum(x => x.NodeLevels.Sum(level => level.ReferencedNodeCount)),
            ["ReferencedChildLinks"] = trees.Sum(x => x.NodeLevels.Sum(level => level.ReferencedChildCount)),
            ["RunLengthEncodedRecords"] = trees.Count(x => x.CompressionType != 0),
            ["EmptyRootRecords"] = trees.Count(x => x.DecodedNodeMasks.Length > 0 && x.DecodedNodeMasks[0] == 0),
            ["RetainedMatrixMetadataCopiedByRetailLoader"] = true,
            ["RetainedMatrixMetadataConsumedByRetailRuntime"] = false,
            ["RetailRuntimeConsumedRecordFields"] = "0x08, 0x0C, 0x14..0x28",
            ["RetailRuntimeUnreadRetainedMetadataRange"] = "0x2C..0x7F",
            ["AuxiliaryBytes"] = usedEnd - recordsEnd,
            ["TrailingCapacityBytes"] = data.Length - usedEnd, ["PayloadSize"] = data.Length
        };
        return new(name is { Length: > 0 } ? name : $"Collision Sphere Tree {trackId}:{resourceId}",
            source with { Confidence = SupportConfidence.Medium }, trackId, resourceId, trees, properties);
    }

    private static SphereTreeLevel ReadLevel(ref BinarySpanReader reader) =>
        new(reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadUInt32Little());

    private static void ValidateMetadata(int treeIndex, Vector3 correction, Vector3 retainedMetadataVector,
        IReadOnlyList<float> retainedSymmetricMatrix, IReadOnlyList<float> retainedInverseSymmetricMatrix,
        IReadOnlyList<SphereTreeLevel> levels,
        SourceByteRange source, int position)
    {
        if (!IsFinite(correction) || !IsFinite(retainedMetadataVector)
            || retainedSymmetricMatrix.Any(x => !float.IsFinite(x))
            || retainedInverseSymmetricMatrix.Any(x => !float.IsFinite(x))
            || levels.Any(x => !float.IsFinite(x.MaximumRadius) || !float.IsFinite(x.MinimumRadius)
                || x.MaximumRadius < 0 || x.MinimumRadius < 0 || x.MinimumRadius > x.MaximumRadius))
            throw new FormatException($"Sphere-tree record {treeIndex} contains invalid metadata", (source.LogicalOffset ?? 0) + position, FixedMetadataSize, FixedMetadataSize);
        for (var i = 0; i < levels.Count; i++)
        {
            var expectedCapacity = 1u << Math.Min(i * 3, 30);
            if (levels[i].Capacity != expectedCapacity)
                throw new FormatException($"Sphere-tree record {treeIndex} level {i} has capacity {levels[i].Capacity}, expected {expectedCapacity}",
                    (source.LogicalOffset ?? 0) + position, levels[i].Capacity, expectedCapacity);
        }
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var product = 0d;
            for (var k = 0; k < 3; k++)
                product += retainedSymmetricMatrix[row * 3 + k] * retainedInverseSymmetricMatrix[k * 3 + column];
            var expected = row == column ? 1d : 0d;
            if (Math.Abs(product - expected) > 0.001)
                throw new FormatException($"Sphere-tree record {treeIndex} matrix pair is not inverse",
                    (source.LogicalOffset ?? 0) + position, FixedMetadataSize, FixedMetadataSize);
        }
    }

    private static byte[] DecodeNodeMasks(int treeIndex, ReadOnlySpan<byte> payload, uint compressionType,
        IReadOnlyList<SphereTreeLevel> levels, SourceByteRange source, int position)
    {
        var expectedSize = checked((int)levels.Sum(level => (long)level.Capacity));
        if (compressionType == 0)
        {
            if (payload.Length != expectedSize)
                throw new FormatException($"Sphere-tree record {treeIndex} raw node payload has {payload.Length} bytes, expected {expectedSize}",
                    (source.LogicalOffset ?? 0) + position, expectedSize, payload.Length);
            return payload.ToArray();
        }

        var decoded = new byte[expectedSize]; var input = 0; var output = 0; var terminated = false;
        while (input < payload.Length)
        {
            var control = unchecked((sbyte)payload[input++]);
            if (control == 0) { terminated = true; break; }
            var count = control < 0 ? -control : control + 1;
            if (output > decoded.Length - count)
                throw new FormatException($"Sphere-tree record {treeIndex} RLE output exceeds {expectedSize} bytes",
                    (source.LogicalOffset ?? 0) + position, expectedSize, output + count);
            if (control < 0)
            {
                if (input > payload.Length - count)
                    throw new FormatException($"Sphere-tree record {treeIndex} RLE literal is truncated",
                        (source.LogicalOffset ?? 0) + position, count, payload.Length - input);
                payload.Slice(input, count).CopyTo(decoded.AsSpan(output)); input += count;
            }
            else
            {
                if (input >= payload.Length)
                    throw new FormatException($"Sphere-tree record {treeIndex} RLE run is truncated",
                        (source.LogicalOffset ?? 0) + position, 1, 0);
                decoded.AsSpan(output, count).Fill(payload[input++]);
            }
            output += count;
        }
        if (!terminated || input != payload.Length || output != decoded.Length)
            throw new FormatException($"Sphere-tree record {treeIndex} RLE stream is inconsistent",
                (source.LogicalOffset ?? 0) + position, expectedSize, output);
        return decoded;
    }

    private static IReadOnlyList<SphereTreeNodeLevel> BuildNodeLevels(byte[] decoded,
        IReadOnlyList<SphereTreeLevel> levels)
    {
        var result = new SphereTreeNodeLevel[levels.Count];
        var active = new HashSet<int> { 0 }; var levelStart = 0;
        for (var depth = 0; depth < levels.Count; depth++)
        {
            var capacity = checked((int)levels[depth].Capacity);
            var masks = decoded.AsSpan(levelStart, capacity).ToArray();
            var children = new HashSet<int>();
            if (depth + 1 < levels.Count)
            {
                foreach (var nodeIndex in active)
                {
                    var mask = decoded[nodeIndex];
                    for (var child = 0; child < 8; child++)
                        if ((mask & (1 << child)) != 0)
                            children.Add(checked(nodeIndex + (child + 1) * capacity));
                }
            }
            result[depth] = new(depth, levels[depth].Capacity, masks, active.Count, children.Count,
                depth + 1 == levels.Count);
            active = children; levelStart += capacity;
        }
        return result;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
