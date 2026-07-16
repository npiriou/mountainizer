using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3CollisionDecoder
{
    private const int HeaderSize = 16;
    private const int SubmeshHeaderSize = 20;
    private const int RuntimeScratchHeaderSize = 16;
    private const int TrianglesPerBatch = 10;
    private const int MaximumElements = 1_000_000;

    public static CollisionAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, string? name = null)
    {
        if (data.Length < HeaderSize)
            throw new FormatException("Collision payload is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var version = reader.ReadUInt16Little(); var submeshCount = reader.ReadUInt16Little();
        var modelsOffset = checked((int)reader.ReadUInt32Little());
        var runtimeScratchHeaderOffset = checked((int)reader.ReadUInt32Little());
        var submeshPointerScratchOffset = checked((int)reader.ReadUInt32Little());
        if (version != 1) throw new FormatException($"Unsupported collision version {version}", source.LogicalOffset ?? 0, 2, data.Length);
        if (modelsOffset != HeaderSize || runtimeScratchHeaderOffset < modelsOffset
            || submeshPointerScratchOffset != runtimeScratchHeaderOffset + RuntimeScratchHeaderSize
            || submeshPointerScratchOffset > data.Length
            || data.Length - submeshPointerScratchOffset != submeshCount * 4)
            throw new FormatException("Collision section offsets do not match the version-one runtime layout",
                source.LogicalOffset ?? 0, submeshPointerScratchOffset + submeshCount * 4, data.Length);
        if (submeshCount > 4096) throw new FormatException($"Collision submesh count {submeshCount} exceeds the safety limit",
            (source.LogicalOffset ?? 0) + 2, submeshCount, 4096);

        var submeshes = new List<CollisionSubmesh>(submeshCount);
        var maximumBatchBoundsError = 0f;
        var position = modelsOffset;
        for (var modelIndex = 0; modelIndex < submeshCount; modelIndex++)
        {
            if (position > runtimeScratchHeaderOffset - SubmeshHeaderSize)
                throw new FormatException($"Collision submesh {modelIndex} header is out of bounds", (source.LogicalOffset ?? 0) + position, SubmeshHeaderSize, runtimeScratchHeaderOffset - position);
            reader.Seek(position); var baseOffset = position;
            var triangleCount = reader.ReadUInt16Little(); var vertexCount = reader.ReadUInt16Little();
            var indicesOffset = checked((int)reader.ReadUInt32Little()); var boundsOffset = checked((int)reader.ReadUInt32Little());
            var verticesOffset = checked((int)reader.ReadUInt32Little()); var normalsOffset = checked((int)reader.ReadUInt32Little());
            ValidateCount(triangleCount, "triangle", reader); ValidateCount(vertexCount, "vertex", reader);
            var batchCount = (triangleCount + TrianglesPerBatch - 1) / TrianglesPerBatch;
            var expectedBoundsOffset = Align(SubmeshHeaderSize + triangleCount * 3, 4);
            var expectedVerticesOffset = Align(expectedBoundsOffset + batchCount * 24, 16);
            var expectedNormalsOffset = checked(expectedVerticesOffset + vertexCount * 16);
            if (indicesOffset != SubmeshHeaderSize || boundsOffset != expectedBoundsOffset
                || verticesOffset != expectedVerticesOffset || normalsOffset != expectedNormalsOffset)
                throw new FormatException($"Collision submesh {modelIndex} offsets do not match its packed triangle-batch layout",
                    (source.LogicalOffset ?? 0) + baseOffset, expectedNormalsOffset, runtimeScratchHeaderOffset - baseOffset);
            var next = checked(baseOffset + normalsOffset + triangleCount * 16);
            if (next > runtimeScratchHeaderOffset) throw new FormatException($"Collision submesh {modelIndex} exceeds its model section", (source.LogicalOffset ?? 0) + baseOffset, next - baseOffset, runtimeScratchHeaderOffset - baseOffset);

            reader.Seek(checked(baseOffset + indicesOffset));
            var indices = new uint[triangleCount * 3];
            for (var i = 0; i < indices.Length; i++)
            {
                var value = reader.ReadByte();
                if (value >= vertexCount) throw new FormatException($"Collision submesh {modelIndex} index {value} exceeds {vertexCount} vertices", reader.AbsolutePosition - 1, value, vertexCount);
                indices[i] = value;
            }
            var indexPadding = reader.ReadBytes(boundsOffset - (indicesOffset + indices.Length)).ToArray();
            reader.Seek(checked(baseOffset + boundsOffset));
            var triangleBatches = new CollisionTriangleBatch[batchCount];
            for (var i = 0; i < triangleBatches.Length; i++)
            {
                var minimum = reader.ReadVector3(); var maximum = reader.ReadVector3();
                triangleBatches[i] = new(i * TrianglesPerBatch,
                    Math.Min(TrianglesPerBatch, triangleCount - i * TrianglesPerBatch), ConvertedBounds(minimum, maximum));
            }
            var triangleBatchPadding = reader.ReadBytes(verticesOffset - (boundsOffset + batchCount * 24)).ToArray();
            reader.Seek(checked(baseOffset + verticesOffset));
            var vertices = new Vector3[vertexCount];
            for (var i = 0; i < vertices.Length; i++)
            {
                var value = reader.ReadVector4();
                var position3 = new Vector3(value.X, value.Y, value.Z);
                if (!IsFinite(position3)) throw new FormatException($"Collision submesh {modelIndex} contains a non-finite vertex", reader.AbsolutePosition - 16, 16, 16);
                vertices[i] = Ssx3Coordinates.ToMountainizer(position3);
            }
            reader.Seek(checked(baseOffset + normalsOffset));
            var normals = new Vector3[triangleCount];
            for (var i = 0; i < normals.Length; i++)
            {
                var value = reader.ReadVector4();
                var normal = Ssx3Coordinates.ToMountainizer(new Vector3(value.X, value.Y, value.Z));
                normals[i] = normal.LengthSquared() > 0.000001f && IsFinite(normal) ? Vector3.Normalize(normal) : Vector3.UnitY;
            }
            foreach (var batch in triangleBatches)
            {
                var firstIndex = batch.FirstTriangle * 3;
                var computed = SceneBounds.FromPoints(indices.Skip(firstIndex).Take(batch.TriangleCount * 3)
                    .Select(index => vertices[checked((int)index)]));
                maximumBatchBoundsError = Math.Max(maximumBatchBoundsError, BoundsError(batch.Bounds, computed));
                if (BoundsError(batch.Bounds, computed) > 0.0001f)
                    throw new FormatException($"Collision submesh {modelIndex} triangle batch {batch.FirstTriangle / TrianglesPerBatch} bounds do not enclose its serialized triangles",
                        (source.LogicalOffset ?? 0) + baseOffset + boundsOffset + batch.FirstTriangle / TrianglesPerBatch * 24, 24, 24);
            }
            submeshes.Add(new(vertices, indices, normals, triangleBatches, indexPadding, triangleBatchPadding));
            position = next;
        }
        if (position != runtimeScratchHeaderOffset)
            throw new FormatException("Collision model section does not end at its declared runtime scratch header",
                (source.LogicalOffset ?? 0) + position, runtimeScratchHeaderOffset - position, data.Length - position);

        reader.Seek(runtimeScratchHeaderOffset);
        var runtimeScratchHeader = reader.ReadBytes(RuntimeScratchHeaderSize).ToArray();
        var submeshPointerScratch = reader.ReadBytes(submeshCount * 4).ToArray();

        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Collision Mesh", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Version"] = version, ["SubmeshCount"] = submeshes.Count,
            ["TriangleCount"] = submeshes.Sum(x => x.Indices.Count / 3), ["VertexCount"] = submeshes.Sum(x => x.Vertices.Count),
            ["TriangleBatchCount"] = submeshes.Sum(x => x.TriangleBatches.Count),
            ["IndexPaddingBytes"] = submeshes.Sum(x => x.IndexPadding.Length),
            ["TriangleBatchPaddingBytes"] = submeshes.Sum(x => x.TriangleBatchPadding.Length),
            ["TriangleBatchBoundsMaxError"] = maximumBatchBoundsError,
            ["RuntimeScratchHeaderBytes"] = runtimeScratchHeader.Length,
            ["SubmeshPointerScratchBytes"] = submeshPointerScratch.Length,
            ["SurfaceAttributeStorage"] = "Owning collider metadata; not serialized in Type 12/v1",
            ["PayloadSize"] = data.Length
        };
        return new(name is { Length: > 0 } ? name : $"Collision {trackId}:{resourceId}",
            source with { Confidence = SupportConfidence.Medium }, trackId, resourceId, submeshes,
            runtimeScratchHeader, submeshPointerScratch, properties);
    }

    private static void ValidateCount(int count, string description, BinarySpanReader reader)
    {
        if (count > MaximumElements) throw new FormatException($"Collision {description} count {count} exceeds the safety limit", reader.AbsolutePosition, count, MaximumElements);
    }

    private static SceneBounds ConvertedBounds(Vector3 a, Vector3 b)
    {
        if (!IsFinite(a) || !IsFinite(b)) throw new FormatException("Collision bounds contain non-finite coordinates", 0, 24, 24);
        var ca = Ssx3Coordinates.ToMountainizer(a); var cb = Ssx3Coordinates.ToMountainizer(b);
        return new(Vector3.Min(ca, cb), Vector3.Max(ca, cb));
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static float BoundsError(SceneBounds serialized, SceneBounds computed) => new[]
    {
        Math.Abs(serialized.Minimum.X - computed.Minimum.X), Math.Abs(serialized.Minimum.Y - computed.Minimum.Y),
        Math.Abs(serialized.Minimum.Z - computed.Minimum.Z), Math.Abs(serialized.Maximum.X - computed.Maximum.X),
        Math.Abs(serialized.Maximum.Y - computed.Maximum.Y), Math.Abs(serialized.Maximum.Z - computed.Maximum.Z)
    }.Max();

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
