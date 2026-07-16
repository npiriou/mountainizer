using System.Buffers.Binary;
using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3AvalancheDecoder
{
    private const uint HeaderTag = 0x02BEEF00;
    private const ushort BlockTag = 0xBEEF;
    private const int HeaderSize = 8;
    private const int BlockHeaderSize = 4;
    private const int BlockFixedBytes = 12;
    private const int FrameBytes = 10;
    private const int FramesPerUnit = 30;
    private const int BlockUnitBytes = FrameBytes * FramesPerUnit;
    public const float RuntimeFramesPerSecond = 30f;
    public const string RuntimeTranslationEquation = "position(t) = origin + 2 * (sum(completed s8 deltas) + fraction * current s8 delta)";
    public const string RuntimeScaleEquation = "scale(t) = lerp(current u8 / 128, next u8 / 128, fraction)";
    public const string RuntimeRotationEquation = "rotation(t) = product(completed axis-angle increments) * current axis-angle(fraction * angle)";
    public const string RuntimeScheduleEquation = "dispatch ordered pairs while frameIndex <= elapsedSeconds * 30, preserving duplicates";
    private const float ScaleDivisor = 128f;
    private const float MaximumRotationRadians = 2f * MathF.PI / 3f;
    private const int MaximumBlocks = 100_000;
    private const int MaximumMetadataRecords = 100_000;

    public static AvalancheAnimationAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.IsEmpty)
        {
            var markerProperties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Avalanche Animation Marker",
                ["TrackId"] = trackId, ["ResourceId"] = resourceId, ["Marker"] = true, ["PayloadSize"] = 0 };
            return new($"Avalanche Marker {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Low },
                trackId, resourceId, [], [], markerProperties);
        }
        if (data.Length < HeaderSize + BlockHeaderSize)
            throw new FormatException("Avalanche stream is truncated", source.LogicalOffset ?? 0, HeaderSize + BlockHeaderSize, data.Length);
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var declaredBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        if (tag != HeaderTag || declaredBytes != data.Length - HeaderSize)
            throw new FormatException("Avalanche stream header is unknown or inconsistent", source.LogicalOffset ?? 0, HeaderSize, data.Length);

        var blocks = new List<AvalancheDataBlock>(); var metadata = new List<AvalancheMetadataSegment>(); var position = HeaderSize;
        while (position < data.Length)
        {
            if (IsBlock(data, position, out var blockBytes, out var unitCount))
            {
                if (blocks.Count >= MaximumBlocks)
                    throw new FormatException("Avalanche stream exceeds the block safety limit", (source.LogicalOffset ?? 0) + position, blocks.Count, MaximumBlocks);
                blocks.Add(DecodeBlock(data.Slice(position + BlockHeaderSize, blockBytes), position, unitCount, source));
                position = checked(position + BlockHeaderSize + blockBytes); continue;
            }
            if (metadata.Count >= MaximumMetadataRecords)
                throw new FormatException("Avalanche stream exceeds the metadata safety limit", (source.LogicalOffset ?? 0) + position,
                    metadata.Count, MaximumMetadataRecords);
            var segment = DecodeMetadata(data[position..], position, source);
            metadata.Add(segment); position = checked(position + segment.Data.Length);
        }
        if (blocks.Count == 0)
            throw new FormatException("Avalanche stream contains no recognized data blocks", (source.LogicalOffset ?? 0) + HeaderSize, data.Length - HeaderSize, data.Length);
        foreach (var segment in metadata)
        {
            if (!IsNondecreasing(segment.Parameters.Select(parameter => parameter.TimeSeconds)))
                throw new FormatException("Avalanche timed targets are not ordered by time",
                    (source.LogicalOffset ?? 0) + segment.Offset, segment.Data.Length, data.Length - segment.Offset);
            if (!IsNondecreasing(segment.Pairs.Select(pair => pair.FrameIndex)))
                throw new FormatException("Avalanche schedule pairs are not ordered by frame",
                    (source.LogicalOffset ?? 0) + segment.Offset, segment.Data.Length, data.Length - segment.Offset);
            foreach (var pair in segment.Pairs)
            {
                if (pair.BlockIndex >= blocks.Count || pair.FrameIndex >= blocks[pair.BlockIndex].Frames.Count)
                    throw new FormatException($"Avalanche schedule references invalid block/frame {pair.BlockIndex}/{pair.FrameIndex}",
                        (source.LogicalOffset ?? 0) + segment.Offset, segment.Data.Length, data.Length - segment.Offset);
            }
        }
        var blockSizes = blocks.GroupBy(x => x.Data.Length).OrderBy(x => x.Key).Select(x => $"{x.Key} x{x.Count()}");
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Avalanche Animation Stream", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["HeaderTag"] = $"0x{tag:X8}", ["BlockCount"] = blocks.Count, ["BlockSizes"] = string.Join(", ", blockSizes),
            ["TotalBlockUnits"] = blocks.Sum(x => x.UnitCount), ["FrameCount"] = blocks.Sum(x => x.Frames.Count),
            ["MetadataSegmentCount"] = metadata.Count,
            ["MetadataParameterCount"] = metadata.Sum(x => x.Parameters.Count),
            ["SchedulePairCount"] = metadata.Sum(x => x.Pairs.Count),
            ["RuntimeFramesPerSecond"] = RuntimeFramesPerSecond,
            ["RuntimeTranslationEquation"] = RuntimeTranslationEquation,
            ["RuntimeScaleEquation"] = RuntimeScaleEquation,
            ["RuntimeRotationEquation"] = RuntimeRotationEquation,
            ["RuntimeScheduleEquation"] = RuntimeScheduleEquation,
            ["RuntimeScheduleDiskOrder"] = "block index, frame index",
            ["RuntimeScheduleMemoryOrder"] = "frame index, block index",
            ["SerializedTargetIdentitySource"] = "target runtime object's packed self-reference at object offset 0x78",
            ["RuntimeLoadDiscardsSerializedTargetIdentity"] = true,
            ["MetadataShapes"] = string.Join(", ", metadata.GroupBy(x => new { x.ParameterCount, x.PairCount })
                .OrderBy(x => x.Key.ParameterCount).ThenBy(x => x.Key.PairCount)
                .Select(x => $"{x.Key.ParameterCount} parameters / {x.Key.PairCount} pairs x{x.Count()}")),
            ["MetadataBytes"] = metadata.Sum(x => x.Data.Length), ["PayloadSize"] = data.Length
        };
        return new($"Avalanche Animation {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, blocks, metadata, properties);
    }

    private static AvalancheDataBlock DecodeBlock(ReadOnlySpan<byte> data, int offset, int unitCount, SourceByteRange source)
    {
        var serializedOrigin = ReadVector3(data);
        if (!IsFinite(serializedOrigin))
            throw new FormatException("Avalanche block origin is not finite", (source.LogicalOffset ?? 0) + offset + BlockHeaderSize,
                BlockFixedBytes, data.Length);
        var frameCount = checked(unitCount * FramesPerUnit);
        var frames = new AvalancheFrame[frameCount];
        var serializedPosition = serializedOrigin;
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = data.Slice(BlockFixedBytes + i * FrameBytes, FrameBytes);
            var deltaX = unchecked((sbyte)frame[0]); var deltaY = unchecked((sbyte)frame[1]);
            var deltaZ = unchecked((sbyte)frame[2]);
            serializedPosition += new Vector3(deltaX, deltaY, deltaZ) * 2f;
            var scale = new Vector3(frame[3], frame[4], frame[5]) / ScaleDivisor;
            var axisX = unchecked((sbyte)frame[6]); var axisY = unchecked((sbyte)frame[7]); var axisZ = unchecked((sbyte)frame[8]);
            var serializedAxis = new Vector3(axisX, axisY, axisZ);
            if (serializedAxis != Vector3.Zero) serializedAxis = Vector3.Normalize(serializedAxis);
            frames[i] = new(Ssx3Coordinates.ToMountainizer(serializedPosition), serializedPosition,
                new Vector3(scale.X, scale.Z, scale.Y), scale,
                Ssx3Coordinates.ToMountainizer(serializedAxis), serializedAxis, frame[9] * (MaximumRotationRadians / byte.MaxValue),
                deltaX, deltaY, deltaZ, frame[3], frame[4], frame[5], axisX, axisY, axisZ, frame[9]);
        }
        return new(offset, unitCount, Ssx3Coordinates.ToMountainizer(serializedOrigin), serializedOrigin, frames, data.ToArray());
    }

    public static AvalancheRuntimeTransform EvaluateRuntimeTransform(AvalancheDataBlock block, float timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!float.IsFinite(timeSeconds) || timeSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeSeconds), "Avalanche sample time must be finite and non-negative");
        if (block.Frames.Count == 0)
            throw new ArgumentException("Avalanche block has no frame records", nameof(block));

        var clampedTime = MathF.Min(timeSeconds, block.RuntimeDurationSeconds);
        var frameTime = clampedTime * RuntimeFramesPerSecond;
        int frameIndex;
        float fraction;
        if (block.Frames.Count == 1)
        {
            frameIndex = 0;
            fraction = 0;
        }
        else if (frameTime >= block.Frames.Count - 1)
        {
            frameIndex = block.Frames.Count - 2;
            fraction = 1;
        }
        else
        {
            frameIndex = (int)MathF.Floor(frameTime);
            fraction = frameTime - frameIndex;
        }

        var serializedPosition = block.SerializedOriginSsx;
        var serializedRotation = Quaternion.Identity;
        for (var i = 0; i < frameIndex; i++)
        {
            var completed = block.Frames[i];
            serializedPosition += completed.SerializedTranslationDeltaSsx;
            serializedRotation = AppendRotation(serializedRotation, completed.SerializedRotationAxisSsx,
                completed.RotationAngleIncrementRadians);
        }

        var current = block.Frames[frameIndex];
        serializedPosition += current.SerializedTranslationDeltaSsx * fraction;
        serializedRotation = AppendRotation(serializedRotation, current.SerializedRotationAxisSsx,
            current.RotationAngleIncrementRadians * fraction);
        var next = block.Frames[Math.Min(frameIndex + 1, block.Frames.Count - 1)];
        var serializedScale = Vector3.Lerp(current.SerializedScaleSsx, next.SerializedScaleSsx, fraction);
        var mountainRotation = ConvertRotationToMountainizer(serializedRotation);

        return new(clampedTime, frameTime, frameIndex, fraction,
            Ssx3Coordinates.ToMountainizer(serializedPosition), serializedPosition,
            new Vector3(serializedScale.X, serializedScale.Z, serializedScale.Y), serializedScale,
            mountainRotation, serializedRotation);
    }

    public static IReadOnlyList<AvalancheMetadataPair> SchedulePairsDue(
        AvalancheMetadataSegment segment, float elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateElapsedSeconds(elapsedSeconds);
        var frameTime = elapsedSeconds * RuntimeFramesPerSecond;
        return segment.Pairs.TakeWhile(pair => pair.FrameIndex <= frameTime).ToArray();
    }

    public static IReadOnlyList<AvalancheMetadataParameter> TimedTargetsDue(
        AvalancheMetadataSegment segment, float elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateElapsedSeconds(elapsedSeconds);
        return segment.Parameters.TakeWhile(parameter => parameter.TimeSeconds <= elapsedSeconds).ToArray();
    }

    private static AvalancheMetadataSegment DecodeMetadata(ReadOnlySpan<byte> remaining, int offset, SourceByteRange source)
    {
        if (remaining.Length < 4)
            throw new FormatException("Avalanche metadata header is truncated", (source.LogicalOffset ?? 0) + offset, 4, remaining.Length);
        var parameterCount = BinaryPrimitives.ReadUInt16LittleEndian(remaining);
        var pairCount = BinaryPrimitives.ReadUInt16LittleEndian(remaining[2..]);
        var size = 4 + 8 * parameterCount + 4 * pairCount;
        if (size > remaining.Length)
            throw new FormatException("Avalanche metadata segment is truncated", (source.LogicalOffset ?? 0) + offset, size, remaining.Length);

        var data = remaining[..size]; var cursor = 4;
        var parameters = new List<AvalancheMetadataParameter>(); var pairs = new List<AvalancheMetadataPair>();
        for (var i = 0; i < parameterCount; i++)
        {
            var time = ReadSingle(data, cursor); var packedReference = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
            if (!float.IsFinite(time) || time < 0)
                throw new FormatException("Avalanche metadata time is invalid", (source.LogicalOffset ?? 0) + offset + cursor, 8, size - cursor);
            parameters.Add(new(time, packedReference, (int)(packedReference & 0xff), checked((int)(packedReference >> 8)))); cursor += 8;
        }
        for (var i = 0; i < pairCount; i++)
        {
            pairs.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 2)..]))); cursor += 4;
        }
        if (cursor != size)
            throw new FormatException("Avalanche metadata decoder did not consume its segment", (source.LogicalOffset ?? 0) + offset, size, cursor);
        return new(offset, parameterCount, pairCount, parameters, pairs, data.ToArray());
    }

    private static bool IsBlock(ReadOnlySpan<byte> data, int offset, out int blockBytes, out int unitCount)
    {
        blockBytes = 0; unitCount = 0;
        if (offset < 0 || offset > data.Length - BlockHeaderSize || BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) != BlockTag) return false;
        blockBytes = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
        if (blockBytes < BlockFixedBytes || (blockBytes - BlockFixedBytes) % BlockUnitBytes != 0
            || offset > data.Length - BlockHeaderSize - blockBytes) return false;
        unitCount = (blockBytes - BlockFixedBytes) / BlockUnitBytes; return true;
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> data) => new(ReadSingle(data, 0), ReadSingle(data, 4), ReadSingle(data, 8));
    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsNondecreasing(IEnumerable<float> values)
    {
        var previous = float.NegativeInfinity;
        foreach (var value in values) { if (value < previous) return false; previous = value; }
        return true;
    }
    private static bool IsNondecreasing(IEnumerable<ushort> values)
    {
        ushort previous = 0; var first = true;
        foreach (var value in values) { if (!first && value < previous) return false; previous = value; first = false; }
        return true;
    }
    private static Quaternion AppendRotation(Quaternion rotation, Vector3 axis, float angle)
    {
        if (axis == Vector3.Zero || angle == 0) return rotation;
        return Quaternion.Normalize(Quaternion.Multiply(rotation, Quaternion.CreateFromAxisAngle(axis, angle)));
    }
    private static Quaternion ConvertRotationToMountainizer(Quaternion serializedRotation)
    {
        serializedRotation = Quaternion.Normalize(serializedRotation);
        var vector = Ssx3Coordinates.ToMountainizer(new Vector3(
            serializedRotation.X, serializedRotation.Y, serializedRotation.Z));
        return Quaternion.Normalize(new Quaternion(vector, serializedRotation.W));
    }
    private static void ValidateElapsedSeconds(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Avalanche elapsed time must be finite and non-negative");
    }
}
