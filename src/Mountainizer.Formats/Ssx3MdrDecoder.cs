using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3MdrDecoder
{
    private const int MaximumObjects = 4096;
    private const int MaximumArrays = 16384;
    private const int MaximumVertices = 2_000_000;

    private sealed record ObjectDescriptor(uint ParentId, uint GeometryOffset, uint MetadataOffset, uint MatrixOffset);
    private sealed record PacketHeader(int LineCount, byte Kind, int Offset, byte Flags);

    public static ModelAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        var header = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        header.Ensure(44);
        var objectTrack = header.ReadByte(); var objectRid = header.ReadUInt24Little();
        var objectCount = checked((int)header.ReadUInt32Little());
        var objectTableOffset = checked((int)header.ReadUInt32Little());
        var unknown3 = header.ReadUInt32Little(); var unknown4 = header.ReadUInt32Little();
        var unknown6 = header.ReadSingleLittle(); var scale = header.ReadVector3();
        var modelDataOffset = checked((int)header.ReadUInt32Little());
        var materialCount = checked((int)header.ReadUInt32Little());
        if (objectCount is < 0 or > MaximumObjects || materialCount is < 0 or > MaximumArrays)
            throw new Mountainizer.Core.FormatException("MDR header count exceeds safety limits", source.LogicalOffset ?? 0, MaximumObjects, Math.Max(objectCount, materialCount));
        var materials = new List<(int Track, int Resource)>(materialCount);
        for (var i = 0; i < materialCount; i++) materials.Add((header.ReadByte(), checked((int)header.ReadUInt24Little())));

        // The object table immediately follows the material IDs. The earlier offset names a later structure.
        var tableOffset = header.Position;
        var table = new BinarySpanReader(data, source.LogicalOffset ?? 0); table.Seek(tableOffset);
        var objects = new List<ObjectDescriptor>(objectCount);
        for (var i = 0; i < objectCount; i++) objects.Add(new(table.ReadUInt32Little(), table.ReadUInt32Little(), table.ReadUInt32Little(), table.ReadUInt32Little()));

        var localMatrices = new Matrix4x4[objects.Count];
        for (var i = 0; i < objects.Count; i++) localMatrices[i] = ReadMatrix(data, objects[i].MatrixOffset);
        var worldMatrices = new Matrix4x4[objects.Count]; var matrixState = new byte[objects.Count];
        for (var i = 0; i < objects.Count; i++) ResolveWorldMatrix(i);

        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var indices = new List<uint>();
        var submeshes = new List<ModelSubmesh>();
        var decodedParts = 0; var packetPairs = 0; var specialPacketHeaders = new List<string>(); var specialPacketPreviews = new List<string>();
        for (var objectIndex = 0; objectIndex < objects.Count; objectIndex++)
        {
            var descriptor = objects[objectIndex]; if (descriptor.GeometryOffset is 0 or uint.MaxValue) continue;
            var geometry = At(data, descriptor.GeometryOffset, source, "MDR object geometry");
            geometry.Skip(24); geometry.ReadUInt32Little();
            var arrayCount = checked((int)geometry.ReadUInt32Little()); var arrayOffset = checked((int)geometry.ReadUInt32Little());
            if (arrayCount is < 0 or > MaximumArrays) throw new Mountainizer.Core.FormatException("MDR geometry array count exceeds safety limit", source.LogicalOffset ?? 0, MaximumArrays, arrayCount);
            var offsets = At(data, (uint)arrayOffset, source, "MDR geometry offset array");
            for (var part = 0; part < arrayCount; part++)
            {
                var partOffset = offsets.ReadUInt32Little();
                var partHeader = At(data, partOffset, source, "MDR part header");
                var materialIndex = partHeader.ReadInt16Little(); partHeader.ReadInt16Little();
                var packetListOffset = checked((int)partHeader.ReadUInt24Little()); partHeader.ReadByte();
                var packetHeaders = ReadPacketHeaders(data, checked(modelDataOffset + packetListOffset), source);
                if (packetHeaders.LastOrDefault() is { Flags: 128 } special)
                {
                    specialPacketHeaders.Add($"lines={special.LineCount}, kind={special.Kind}, offset={special.Offset}");
                    var absolute = modelDataOffset + special.Offset;
                    if (absolute >= 0 && absolute < data.Length) specialPacketPreviews.Add(Convert.ToHexString(data.Slice(absolute, Math.Min(96, data.Length - absolute))));
                }
                var usable = packetHeaders.TakeWhile(x => x.Kind != 96).Where(x => x.Offset > 0 && x.Flags == 0).ToArray();
                var partPositions = new List<Vector3>(); var partNormals = new List<Vector3>(); var partUvs = new List<Vector2>(); var partIndices = new List<uint>();
                var firstVertexPacket = true;
                for (var packet = 0; packet + 1 < usable.Length; packet += 2)
                {
                    var vertexPacket = usable[packet]; var normalPacket = usable[packet + 1];
                    var vertices = ReadVertices(data, checked(modelDataOffset + vertexPacket.Offset), scale, firstVertexPacket, source);
                    firstVertexPacket = false;
                    var packetNormals = ReadNormals(data, checked(modelDataOffset + normalPacket.Offset), source);
                    if (vertices.Positions.Count == 0) continue;
                    var vertexBase = (uint)partPositions.Count; var objectMatrix = worldMatrices[objectIndex];
                    for (var vertex = 0; vertex < vertices.Positions.Count; vertex++)
                    {
                        partPositions.Add(Vector3.Transform(vertices.Positions[vertex], objectMatrix));
                        var normal = vertex < packetNormals.Count ? packetNormals[vertex] : Vector3.UnitY;
                        normal = Vector3.TransformNormal(normal, objectMatrix);
                        partNormals.Add(normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitY);
                        partUvs.Add(vertices.Uvs[vertex]);
                    }
                    AddTriangleStrips(partIndices, vertexBase, vertices.StripLengths, vertices.Positions);
                    packetPairs++;
                }
                if (packetHeaders.LastOrDefault() is { Flags: 128, Offset: > 0 } combinedPacket)
                {
                    var vertices = ReadVertices(data, checked(modelDataOffset + combinedPacket.Offset), scale, firstVertexPacket, source);
                    var vertexBase = (uint)partPositions.Count; var objectMatrix = worldMatrices[objectIndex];
                    foreach (var vertex in vertices.Positions) partPositions.Add(Vector3.Transform(vertex, objectMatrix));
                    partNormals.AddRange(Enumerable.Repeat(Vector3.Zero, vertices.Positions.Count)); partUvs.AddRange(vertices.Uvs);
                    AddTriangleStrips(partIndices, vertexBase, vertices.StripLengths, vertices.Positions); packetPairs++;
                    for (var triangle = 0; triangle < partIndices.Count; triangle += 3)
                    {
                        var a = partIndices[triangle]; var b = partIndices[triangle + 1]; var c = partIndices[triangle + 2];
                        var normal = Vector3.Cross(partPositions[(int)b] - partPositions[(int)a], partPositions[(int)c] - partPositions[(int)a]);
                        partNormals[(int)a] += normal; partNormals[(int)b] += normal; partNormals[(int)c] += normal;
                    }
                    for (var i = 0; i < partNormals.Count; i++)
                        if (partNormals[i].LengthSquared() > 0.000001f) partNormals[i] = Vector3.Normalize(partNormals[i]); else partNormals[i] = Vector3.UnitY;
                }
                if (partPositions.Count > 0)
                {
                    var material = materialIndex >= 0 && materialIndex < materials.Count ? materials[materialIndex] : (-1, -1);
                    var partMesh = new MeshData(partPositions, partNormals, partUvs, partIndices); submeshes.Add(new(partMesh, material.Item1, material.Item2));
                    var combinedBase = (uint)positions.Count; positions.AddRange(partPositions); normals.AddRange(partNormals); uvs.AddRange(partUvs);
                    indices.AddRange(partIndices.Select(x => x + combinedBase));
                    if (positions.Count > MaximumVertices) throw new Mountainizer.Core.FormatException("MDR decoded vertex count exceeds safety limit", source.LogicalOffset ?? 0, MaximumVertices, positions.Count);
                }
                decodedParts++;
            }
        }
        var mesh = positions.Count == 0 ? null : new MeshData(positions, normals, uvs, indices);
        return new($"Model RID {resourceId}", source, mesh, submeshes, new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 MDR Model", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["ObjectTrackId"] = objectTrack, ["ObjectResourceId"] = objectRid, ["ObjectCount"] = objectCount,
            ["MaterialResourceIds"] = string.Join(", ", materials.Select(x => $"{x.Track}:{x.Resource}")), ["Scale"] = scale, ["DecodedParts"] = decodedParts,
            ["PacketPairs"] = packetPairs, ["VertexCount"] = positions.Count, ["TriangleCount"] = indices.Count / 3,
            ["SpecialPacketHeaders"] = string.Join("; ", specialPacketHeaders),
            ["SpecialPacketPreviews"] = string.Join("; ", specialPacketPreviews), ["ModelDataOffset"] = modelDataOffset,
            ["U3"] = $"0x{unknown3:X8}", ["U4"] = $"0x{unknown4:X8}", ["U6"] = unknown6, ["PayloadSize"] = data.Length
        });

        void ResolveWorldMatrix(int index)
        {
            if (matrixState[index] == 2) return;
            if (matrixState[index] == 1) throw new InvalidDataException("MDR object hierarchy contains a cycle");
            matrixState[index] = 1; var parent = objects[index].ParentId;
            if (parent != uint.MaxValue && parent < objects.Count) { ResolveWorldMatrix((int)parent); worldMatrices[index] = localMatrices[index] * worldMatrices[parent]; }
            else worldMatrices[index] = localMatrices[index];
            matrixState[index] = 2;
        }
    }

    private static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset is 0 or uint.MaxValue) return Matrix4x4.Identity;
        if (offset > data.Length - 64) throw new InvalidDataException($"MDR matrix offset {offset} exceeds payload length {data.Length}");
        var r = new BinarySpanReader(data); r.Seek(checked((int)offset)); var m = new float[16]; for (var i = 0; i < 16; i++) m[i] = r.ReadSingleLittle();
        return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    }

    private static List<PacketHeader> ReadPacketHeaders(ReadOnlySpan<byte> data, int offset, SourceByteRange source)
    {
        var result = new List<PacketHeader>(); var position = offset;
        for (var i = 0; i < 1024; i++)
        {
            var r = At(data, (uint)position, source, "MDR packet list");
            var header = new PacketHeader(checked((int)r.ReadUInt24Little()), r.ReadByte(), checked((int)r.ReadUInt24Little()), r.ReadByte());
            // 0x80 terminates this descriptor chain without the usual kind-96 record.
            if (header.Flags == 128) { result.Add(header); return result; }
            if (header.Flags != 0) throw new NotSupportedException($"MDR packet flag {header.Flags} is not supported");
            result.Add(header); position = Align16(position + 8); if (header.Kind == 96) return result;
        }
        throw new InvalidDataException("MDR packet list has no terminator");
    }

    private sealed record VertexPacket(List<Vector3> Positions, List<Vector2> Uvs, List<int> StripLengths);

    private static VertexPacket ReadVertices(ReadOnlySpan<byte> data, int offset, Vector3 scale, bool hasModelHeader, SourceByteRange source)
    {
        var r = At(data, (uint)offset, source, "MDR vertex packet");
        if (hasModelHeader) r.Skip(32); r.Skip(32);
        var stripCount = checked((int)r.ReadUInt32Little()); r.Skip(4); var vertexCount = checked((int)r.ReadUInt32Little()); r.Skip(20);
        if (stripCount is < 0 or > MaximumArrays || vertexCount is < 0 or > MaximumVertices)
            throw new Mountainizer.Core.FormatException("MDR packet count exceeds safety limits", r.AbsolutePosition, MaximumVertices, Math.Max(stripCount, vertexCount));
        var strips = new List<int>(stripCount); for (var i = 0; i < stripCount; i++) strips.Add(r.ReadUInt16Little());
        r.Seek(Align16(r.Position)); r.Skip(16);
        var textureCoordinates = new List<Vector2>(vertexCount);
        for (var i = 0; i < vertexCount; i++) textureCoordinates.Add(new(r.ReadInt16Little() / 4096f, r.ReadInt16Little() / 4096f));
        r.Seek(Align16(r.Position)); r.Skip(16);
        var positions = new List<Vector3>(vertexCount);
        for (var i = 0; i < vertexCount; i++) positions.Add(new(r.ReadInt16Little() / 32768f * scale.X,
            r.ReadInt16Little() / 32768f * scale.Y, r.ReadInt16Little() / 32768f * scale.Z));
        return new(positions, textureCoordinates, strips);
    }

    private static List<Vector3> ReadNormals(ReadOnlySpan<byte> data, int offset, SourceByteRange source)
    {
        var r = At(data, (uint)offset, source, "MDR normal packet"); r.Skip(14); var count = r.ReadByte(); r.Skip(1);
        var result = new List<Vector3>(count);
        for (var i = 0; i < count; i++) result.Add(new(r.ReadInt16Little() / 32768f, r.ReadInt16Little() / 32768f, r.ReadInt16Little() / 32768f));
        return result;
    }

    private static void AddTriangleStrips(List<uint> indices, uint vertexBase, IReadOnlyList<int> strips, IReadOnlyList<Vector3> positions)
    {
        var start = 0;
        foreach (var length in strips)
        {
            var end = Math.Min(start + length, positions.Count);
            for (var i = start + 2; i < end; i++)
            {
                var a = i % 2 == 0 ? i - 2 : i; var b = i - 1; var c = i % 2 == 0 ? i : i - 2;
                if (Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).LengthSquared() < 0.0000001f) continue;
                indices.Add(vertexBase + (uint)a); indices.Add(vertexBase + (uint)b); indices.Add(vertexBase + (uint)c);
            }
            start += length; if (start >= positions.Count) break;
        }
    }

    private static BinarySpanReader At(ReadOnlySpan<byte> data, uint offset, SourceByteRange source, string section)
    {
        if (offset >= data.Length) throw new Mountainizer.Core.FormatException($"{section} offset is outside the payload", (source.LogicalOffset ?? 0) + offset, 1, data.Length);
        var r = new BinarySpanReader(data, source.LogicalOffset ?? 0); r.Seek(checked((int)offset)); return r;
    }
    private static int Align16(int value) => checked((value + 15) & ~15);
}
