using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed record SsbGroupInfo(int Index, long SourceOffset, long SourceLength, int DecompressedLength, int ResourceCount);
public sealed record Ssx3LevelParseResult(MountainizerScene Scene, IReadOnlyList<SsbGroupInfo> Groups, DiagnosticBag Diagnostics);

public static class Ssx3SsbReader
{
    private const int OuterHeaderSize = 8;
    private const int MaximumOuterBlockSize = 16 * 1024 * 1024;
    private const int MaximumGroupSize = 256 * 1024 * 1024;
    private const int TerrainPayloadMinimum = 428;

    public static Ssx3LevelParseResult ParseLevel(string ssbPath, Ssx3LevelArea area, int terrainSubdivisions = 8,
        IProgress<(int Current, int Total, string Stage)>? progress = null, CancellationToken cancellationToken = default)
        => ParseAreas(ssbPath, area.Name, [area], terrainSubdivisions, progress, cancellationToken);

    public static Ssx3LevelParseResult ParseCourse(string ssbPath, Ssx3Sdb sdb, Ssx3CourseDefinition course,
        int terrainSubdivisions = 8, IProgress<(int Current, int Total, string Stage)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var areas = Ssx3CourseCatalog.ResolveAreas(sdb, course);
        if (areas.Count == 0) throw new InvalidDataException($"Course '{course.Code}' has no matching SDB areas");
        return ParseAreas(ssbPath, course.Name, areas, terrainSubdivisions, progress, cancellationToken);
    }

    public static Ssx3LevelParseResult ParseAreas(string ssbPath, string sceneName, IReadOnlyList<Ssx3LevelArea> areas,
        int terrainSubdivisions = 8, IProgress<(int Current, int Total, string Stage)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (areas.Count == 0) throw new ArgumentException("At least one SDB area is required", nameof(areas));
        var diagnostics = new DiagnosticBag();
        var scene = new MountainizerScene { Name = sceneName };
        var names = Ssx3ResourceNames.TryLoad(Path.ChangeExtension(ssbPath, ".phm"), Path.ChangeExtension(ssbPath, ".psm"), diagnostics);
        var groups = new List<SsbGroupInfo>();
        var selectedGroups = areas.SelectMany(x => Enumerable.Range(x.FirstGroup, x.GroupCount)).ToHashSet();
        var lastSelectedGroup = selectedGroups.Max();
        var parsedGroupOrdinal = 0;
        using var stream = new FileStream(ssbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var groupIndex = 0;
        var groupStart = 0L;
        var groupCompressedLength = 0L;
        using var group = new MemoryStream();
        Span<byte> header = stackalloc byte[OuterHeaderSize];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = stream.Position;
            if (stream.Length - stream.Position < OuterHeaderSize) { diagnostics.Warn("SSB001", "Trailing bytes smaller than an outer block header", ssbPath, "outer", blockStart); break; }
            stream.ReadExactly(header);
            var magic = Encoding.ASCII.GetString(header[..4]).ToUpperInvariant();
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (size < OuterHeaderSize || size > MaximumOuterBlockSize || size > stream.Length - blockStart)
            {
                diagnostics.Error("SSB002", $"Invalid outer block size {size}", ssbPath, magic, blockStart);
                break;
            }
            if (groupCompressedLength == 0) groupStart = blockStart;
            groupCompressedLength += size;
            var compressed = new byte[size - OuterHeaderSize];
            stream.ReadExactly(compressed);
            try
            {
                var decompressed = RefPackDecoder.Decompress(compressed);
                if (group.Length + decompressed.Length > MaximumGroupSize) throw new FormatException("SSB group exceeds safety limit", blockStart, group.Length + decompressed.Length, MaximumGroupSize);
                group.Write(decompressed);
            }
            catch (Exception ex)
            {
                diagnostics.Error("SSB003", "RefPack block could not be decoded", ssbPath, magic, blockStart, ex);
            }
            if (magic != "CEND")
            {
                if (magic != "CBXS") diagnostics.Warn("SSB004", $"Unknown outer magic '{magic}'", ssbPath, "outer", blockStart);
                continue;
            }

            var shouldParse = selectedGroups.Contains(groupIndex);
            var resourceCount = 0;
            if (shouldParse)
            {
                parsedGroupOrdinal++;
                progress?.Report((parsedGroupOrdinal, selectedGroups.Count, $"Parsing {sceneName} group {groupIndex}"));
                var stopwatch = Stopwatch.StartNew();
                resourceCount = ParseGroup(group.GetBuffer().AsSpan(0, checked((int)group.Length)), ssbPath, groupIndex, groupStart,
                    groupCompressedLength, scene, diagnostics, terrainSubdivisions, names);
                stopwatch.Stop();
                diagnostics.Add(new(DiagnosticSeverity.Information, "SSB010", $"Parsed group {groupIndex}: {resourceCount} resources", ssbPath,
                    $"group {groupIndex}", groupStart, ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds));
            }
            else if (groupIndex < lastSelectedGroup)
            {
                ParseSharedTextures(group.GetBuffer().AsSpan(0, checked((int)group.Length)), ssbPath, groupIndex, groupStart,
                    groupCompressedLength, scene, diagnostics);
            }
            groups.Add(new(groupIndex, groupStart, groupCompressedLength, checked((int)group.Length), resourceCount));
            group.SetLength(0); groupCompressedLength = 0; groupIndex++;
            if (groupIndex > lastSelectedGroup) break;
        }
        ConsolidateTextures(scene, selectedGroups);
        diagnostics.Info("SSB011", $"Scene contains {scene.Terrain.Count} terrain patches, {scene.Props.Count} prop instances, {scene.Models.Count} models, {scene.Materials.Count} materials, {scene.Textures.Count} textures, {scene.Splines.Count} splines, {scene.Triggers.Count} triggers, {scene.VisibilityCurtains.Count} visibility curtains and {scene.UnknownSections.Count} unsupported resources", ssbPath);
        return new(scene, groups, diagnostics);
    }

    private static void ConsolidateTextures(MountainizerScene scene, IReadOnlySet<int> selectedGroups)
    {
        var selected = scene.Textures.GroupBy(x => x.ResourceId).Select(group =>
        {
            var local = group.Where(x => selectedGroups.Contains(Convert.ToInt32(x.Properties["GroupIndex"]))).ToArray();
            if (local.Length > 0) return local[^1];
            var meaningful = group.Where(IsMeaningful).ToArray(); return meaningful.Length > 0 ? meaningful[^1] : group.Last();
        }).OrderBy(x => x.ResourceId).ToArray();
        scene.Textures.Clear(); scene.Textures.AddRange(selected);

        static bool IsMeaningful(TextureAsset texture)
        {
            for (var i = 0; i < texture.RgbaPixels.Length; i += 4)
                if (texture.RgbaPixels[i + 3] > 4 && texture.RgbaPixels[i] + texture.RgbaPixels[i + 1] + texture.RgbaPixels[i + 2] > 6) return true;
            return false;
        }
    }

    private static int ParseGroup(ReadOnlySpan<byte> data, string sourceFile, int groupIndex, long groupSourceOffset,
        long groupSourceLength, MountainizerScene scene, DiagnosticBag diagnostics, int subdivisions, Ssx3ResourceNames? names)
    {
        var position = 0; var resourceIndex = 0;
        while (position < data.Length)
        {
            if (data.Length - position < 8) { diagnostics.Warn("SSB020", "Truncated resource header", sourceFile, $"group {groupIndex}", groupSourceOffset); break; }
            var type = data[position];
            var payloadSize = data[position + 1] | data[position + 2] << 8 | data[position + 3] << 16;
            var trackId = data[position + 4];
            var resourceId = data[position + 5] | data[position + 6] << 8 | data[position + 7] << 16;
            var payloadOffset = position + 8;
            if (payloadSize < 0 || payloadOffset > data.Length - payloadSize)
            {
                diagnostics.Warn("SSB021", $"Resource {resourceIndex} payload is out of bounds", sourceFile, $"group {groupIndex}/type {type}", groupSourceOffset);
                break;
            }
            var payload = data.Slice(payloadOffset, payloadSize);
            var source = new SourceByteRange(sourceFile, groupSourceOffset, groupSourceLength,
                $"SSB group {groupIndex}/type {type}/decompressed+0x{payloadOffset:X}", resourceIndex,
                type is 0 or 1 ? SupportConfidence.Medium : type is 2 or 3 or 8 or 9 or 11 or 17 ? SupportConfidence.Low : SupportConfidence.Unknown, payloadOffset);
            if (type == 0)
            {
                try { scene.Materials.Add(ParseMaterial(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB027", $"Material resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 1)
            {
                try { scene.Terrain.Add(ParseTerrain(payload, source, trackId, resourceId, scene.Terrain.Count, subdivisions)); }
                catch (Exception ex) { diagnostics.Error("SSB022", $"Terrain resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex); }
            }
            else if (type == 2)
            {
                try
                {
                    var model = Ssx3MdrDecoder.Decode(payload, source, trackId, resourceId);
                    scene.Models.Add(names?.Find(2, trackId, resourceId) is { Length: > 0 } modelName ? model with { Name = modelName } : model);
                }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB025", $"MDR model {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 8)
            {
                try { scene.Splines.Add(ParseSpline(payload, source, trackId, resourceId, scene.Splines.Count, names?.Find(3, trackId, resourceId))); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB028", $"Spline resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 9)
            {
                try { scene.Textures.Add(TagTexture(Ssx3TextureDecoder.Decode(payload, source, trackId, resourceId), groupIndex)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB023", $"Texture resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 11)
            {
                try { scene.VisibilityCurtains.Add(ParseVisibilityCurtain(payload, source, trackId, resourceId, scene.VisibilityCurtains.Count)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB029", $"Visibility curtain {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 17)
            {
                try { scene.Triggers.AddRange(ParseCameraTriggers(payload, source, trackId, resourceId, scene.Triggers.Count)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB030", $"Camera trigger table {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 3)
            {
                try { scene.Props.Add(ParseProp(payload, source, trackId, resourceId, scene.Props.Count, names?.Find(1, trackId, resourceId))); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB024", $"Prop instance {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else
            {
                AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
            }
            position = payloadOffset + payloadSize;
            resourceIndex++;
        }
        return resourceIndex;
    }

    private static void ParseSharedTextures(ReadOnlySpan<byte> data, string sourceFile, int groupIndex, long groupSourceOffset,
        long groupSourceLength, MountainizerScene scene, DiagnosticBag diagnostics)
    {
        var position = 0; var resourceIndex = 0;
        while (position <= data.Length - 8)
        {
            var type = data[position]; var payloadSize = data[position + 1] | data[position + 2] << 8 | data[position + 3] << 16;
            var trackId = data[position + 4]; var resourceId = data[position + 5] | data[position + 6] << 8 | data[position + 7] << 16;
            var payloadOffset = position + 8; if (payloadSize < 0 || payloadOffset > data.Length - payloadSize) break;
            if (type == 9)
            {
                var source = new SourceByteRange(sourceFile, groupSourceOffset, groupSourceLength,
                    $"SSB shared group {groupIndex}/type 9/decompressed+0x{payloadOffset:X}", resourceIndex, SupportConfidence.Low, payloadOffset);
                try
                {
                    scene.Textures.Add(TagTexture(Ssx3TextureDecoder.Decode(data.Slice(payloadOffset, payloadSize), source, trackId, resourceId), groupIndex));
                }
                catch (Exception ex) { diagnostics.Warn("SSB026", $"Shared texture {trackId}:{resourceId} could not be decoded: {ex.Message}; header={Convert.ToHexString(data.Slice(payloadOffset, Math.Min(payloadSize, 32)))}", sourceFile, source.SectionName, groupSourceOffset); }
            }
            position = payloadOffset + payloadSize; resourceIndex++;
        }
    }

    private static void AddUnknown(MountainizerScene scene, ReadOnlySpan<byte> payload, SourceByteRange source,
        int type, int trackId, int resourceId, int payloadSize)
    {
        var preview = payload[..Math.Min(payload.Length, 64)].ToArray();
        scene.UnknownSections.Add(new($"Type {type} / RID {resourceId}", source, type, trackId, resourceId, preview,
            new Dictionary<string, object?> { ["ResourceType"] = type, ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                ["PayloadSize"] = payloadSize, ["PreviewHex"] = Convert.ToHexString(preview) }));
    }

    private static TextureAsset TagTexture(TextureAsset texture, int groupIndex)
    {
        var properties = new Dictionary<string, object?>(texture.Properties) { ["GroupIndex"] = groupIndex };
        return texture with { Properties = properties };
    }

    private static MaterialAsset ParseMaterial(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < 20) throw new Mountainizer.Core.FormatException($"Material payload is truncated ({data.Length} bytes)", source.LogicalOffset ?? 0, 20, data.Length);
        var r = new BinarySpanReader(data, source.LogicalOffset ?? 0); var values = new short[10];
        for (var i = 0; i < values.Length; i++) values[i] = r.ReadInt16Little();
        return new($"Material {trackId}:{resourceId}", source, trackId, resourceId, values[0],
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 World Material", ["TrackId"] = trackId,
                ["ResourceId"] = resourceId, ["TextureResourceId"] = values[0],
                ["UnknownValues"] = string.Join(", ", values.Skip(1)), ["PayloadSize"] = data.Length });
    }

    private static PropInstance ParseProp(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index, string? resolvedName)
    {
        if (data.Length < 160) throw new Mountainizer.Core.FormatException("Prop instance payload is truncated", source.LogicalOffset ?? 0, 160, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var unknownHeader = new uint[4]; for (var i = 0; i < 4; i++) unknownHeader[i] = reader.ReadUInt32Little();
        var m = new float[16]; for (var i = 0; i < m.Length; i++) m[i] = reader.ReadSingleLittle();
        var transform = Ssx3Coordinates.ToMountainizerWorldTransform(new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
        var vector0 = reader.ReadVector4(); var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var objectTrack = reader.ReadByte(); var objectRid = reader.ReadUInt24Little(); var unknown4 = reader.ReadUInt32Little();
        var modelTrack = reader.ReadByte(); var modelRid = reader.ReadUInt24Little();
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Prop Instance", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["ModelTrackId"] = modelTrack, ["ModelResourceId"] = modelRid,
            ["ObjectTrackId"] = objectTrack, ["ObjectResourceId"] = objectRid, ["Position"] = new Vector3(transform.M41, transform.M42, transform.M43),
            ["LocalBoundsMin"] = boundsMin, ["LocalBoundsMax"] = boundsMax, ["Vector0"] = vector0,
            ["HeaderHex"] = string.Join(" ", unknownHeader.Select(x => $"{x:X8}")), ["U4"] = $"0x{unknown4:X8}", ["PayloadSize"] = data.Length };
        return new(resolvedName is { Length: > 0 } ? resolvedName : $"Prop Instance {index:D4}", source with { OriginalIndex = index }, transform, modelTrack, (int)modelRid, properties);
    }

    private static Spline ParseSpline(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index, string? resolvedName)
    {
        const int headerSize = 48;
        const int segmentSize = 144;
        if (data.Length < headerSize) throw new FormatException("Spline payload is truncated", source.LogicalOffset ?? 0, headerSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var u0 = reader.ReadUInt32Little();
        var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var u1 = reader.ReadUInt32Little(); var segmentCount = reader.ReadUInt32Little();
        var u2 = reader.ReadSingleLittle(); var u3 = reader.ReadUInt32Little(); var u4 = reader.ReadUInt32Little();
        if (segmentCount > 100_000 || (ulong)headerSize + (ulong)segmentCount * segmentSize > (ulong)data.Length)
            throw new FormatException($"Spline segment table is out of bounds ({segmentCount} segments)", source.LogicalOffset ?? 0, checked((long)segmentCount * segmentSize), data.Length - headerSize);

        var points = new List<SplinePoint>(checked((int)segmentCount * 17));
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var rawHeader = reader.ReadBytes(12).ToArray(); var segmentU1 = reader.ReadSingleLittle();
            var point4 = reader.ReadVector4(); var point3 = reader.ReadVector4(); var point2 = reader.ReadVector4(); var point1 = reader.ReadVector4();
            var coefficients = new[] { reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle() };
            var segmentU2 = reader.ReadUInt32Little(); var segmentU3 = reader.ReadUInt32Little(); var segmentU4 = reader.ReadUInt32Little();
            var segmentMin = reader.ReadVector3(); var segmentMax = reader.ReadVector3(); var segmentLength = reader.ReadSingleLittle();
            var segmentU6 = reader.ReadUInt32Little(); var segmentU7 = reader.ReadUInt32Little();
            var control = DecodeCubicControlPoints(ToVector3(point1), ToVector3(point2), ToVector3(point3), ToVector3(point4));
            for (var sample = segment == 0 ? 0 : 1; sample <= 16; sample++)
            {
                var t = sample / 16f;
                points.Add(new(Ssx3Coordinates.ToMountainizer(EvaluateBezier(control, t)), segment + t));
            }
        }
        return new(resolvedName is { Length: > 0 } ? resolvedName : $"Spline {index:D4}", source with { OriginalIndex = index }, points,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 World Spline", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                ["SegmentCount"] = segmentCount, ["BoundingBoxMin"] = boundsMin, ["BoundingBoxMax"] = boundsMax,
                ["U0"] = $"0x{u0:X8}", ["U1"] = $"0x{u1:X8}", ["U2"] = u2, ["U3"] = $"0x{u3:X8}", ["U4"] = $"0x{u4:X8}", ["PayloadSize"] = data.Length });
    }

    private static VisibilityCurtain ParseVisibilityCurtain(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index)
    {
        const int knownSize = 184;
        if (data.Length < knownSize) throw new FormatException("Visibility curtain payload is truncated", source.LogicalOffset ?? 0, knownSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var header = new[] { reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle() };
        var point4 = reader.ReadVector4(); var point3 = reader.ReadVector4(); var point2 = reader.ReadVector4(); var controlPoint = reader.ReadVector4();
        var values = new[] { reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle(), reader.ReadSingleLittle() };
        var unknown = reader.ReadBytes(64).ToArray(); var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var control = DecodeCubicControlPoints(ToVector3(controlPoint), ToVector3(point2), ToVector3(point3), ToVector3(point4));
        var points = Enumerable.Range(0, 17).Select(x => Ssx3Coordinates.ToMountainizer(EvaluateBezier(control, x / 16f))).ToArray();
        return new($"Visibility Curtain {index:D4}", source with { OriginalIndex = index }, points,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Visibility Curtain", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                ["BoundingBoxMin"] = boundsMin, ["BoundingBoxMax"] = boundsMax, ["HeaderValues"] = string.Join(", ", header),
                ["TrailingValues"] = string.Join(", ", values), ["UnknownHex"] = Convert.ToHexString(unknown), ["PayloadSize"] = data.Length });
    }

    private static IReadOnlyList<TriggerVolume> ParseCameraTriggers(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int firstIndex)
    {
        const int headerSize = 28;
        const int volumeSize = 72;
        if (data.Length < headerSize) throw new FormatException("Camera trigger table is truncated", source.LogicalOffset ?? 0, headerSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var version = reader.ReadUInt32Little(); var scale = reader.ReadSingleLittle(); var count = reader.ReadUInt32Little();
        var header1 = reader.ReadUInt32Little(); var header2 = reader.ReadUInt32Little(); var header3 = reader.ReadUInt32Little(); var header4 = reader.ReadUInt32Little();
        if (count > 10_000 || (ulong)headerSize + (ulong)count * volumeSize > (ulong)data.Length)
            throw new FormatException($"Camera trigger volume table is out of bounds ({count} volumes)", source.LogicalOffset ?? 0, checked((long)count * volumeSize), data.Length - headerSize);
        var result = new List<TriggerVolume>((int)count);
        for (var i = 0; i < count; i++)
        {
            var recordOffset = reader.Position;
            var center = reader.ReadVector3(); var halfSize = reader.ReadVector3(); var remaining = reader.ReadBytes(volumeSize - 24).ToArray();
            if (!IsFinite(center) || !IsFinite(halfSize)) throw new FormatException($"Camera trigger {i} contains non-finite bounds", (source.LogicalOffset ?? 0) + recordOffset, 24, 24);
            center = Ssx3Coordinates.ToMountainizer(center);
            halfSize = Ssx3Coordinates.ToMountainizer(Vector3.Abs(halfSize));
            halfSize = Vector3.Abs(halfSize);
            var triggerSource = source with { SectionName = $"{source.SectionName}/camera trigger {i}", OriginalIndex = firstIndex + (int)i, LogicalOffset = (source.LogicalOffset ?? 0) + recordOffset };
            result.Add(new($"Camera Trigger {firstIndex + i:D4}", triggerSource, center - halfSize, center + halfSize,
                new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Camera Trigger Volume", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                    ["Center"] = center, ["HalfSize"] = halfSize, ["TableVersion"] = version, ["TableScale"] = scale,
                    ["HeaderValues"] = $"{header1}, {header2}, {header3}, {header4}", ["RecordHex"] = Convert.ToHexString(remaining) }));
        }
        return result;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Vector3[] DecodeCubicControlPoints(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        var c0 = p0;
        var c1 = p1 / 3f + c0;
        var c2 = (p2 + p1) / 3f + c1;
        var c3 = p3 + p2 + p1 + p0;
        return [c0, c1, c2, c3];
    }

    private static Vector3 EvaluateBezier(IReadOnlyList<Vector3> p, float t)
    {
        var u = 1f - t;
        return u * u * u * p[0] + 3f * u * u * t * p[1] + 3f * u * t * t * p[2] + t * t * t * p[3];
    }

    private static Vector3 ToVector3(Vector4 value) => new(value.X, value.Y, value.Z);

    private static TerrainPatch ParseTerrain(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index, int subdivisions)
    {
        if (data.Length < TerrainPayloadMinimum) throw new FormatException("Terrain payload is smaller than the known structure", source.LogicalOffset ?? 0, TerrainPayloadMinimum, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var u0 = reader.ReadUInt32Little(); var u1 = reader.ReadUInt32Little();
        var flags = new short[4]; for (var i = 0; i < flags.Length; i++) flags[i] = reader.ReadInt16Little();
        var lightmap = reader.ReadVector4();
        var uv = new Vector2[4]; for (var i = 0; i < uv.Length; i++) uv[i] = reader.ReadVector2();
        var stored = new Vector3[16];
        for (var i = 0; i < stored.Length; i++) { var value = reader.ReadVector4(); stored[i] = new(value.X, value.Y, value.Z); }
        var u7 = reader.ReadVector4();
        var objectTrack = reader.ReadByte(); var objectRid = reader.ReadUInt24Little();
        var u10 = reader.ReadInt16Little(); var u11 = reader.ReadInt16Little();
        var cornerPoints = new Vector3[4]; for (var i = 0; i < 4; i++) cornerPoints[i] = reader.ReadVector3();
        var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var textureRid = reader.ReadInt16Little(); var lightmapRid = reader.ReadInt16Little();
        var trailing = new short[4]; for (var i = 0; i < trailing.Length; i++) trailing[i] = reader.ReadInt16Little();
        var controlPoints = TerrainMeshBuilder.DecodeControlPoints(stored);
        var mesh = TerrainMeshBuilder.Tessellate(controlPoints, subdivisions, uv);
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 World Patch", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["ObjectTrackId"] = objectTrack, ["ObjectResourceId"] = objectRid, ["TextureResourceId"] = textureRid,
            ["LightmapResourceId"] = lightmapRid, ["BoundingBoxMin"] = boundsMin, ["BoundingBoxMax"] = boundsMax,
            ["U0"] = $"0x{u0:X8}", ["U1"] = $"0x{u1:X8}", ["FlagsHex"] = string.Join(" ", flags.Select(x => $"{(ushort)x:X4}")),
            ["U7"] = u7, ["U10"] = $"0x{(ushort)u10:X4}", ["U11"] = $"0x{(ushort)u11:X4}",
            ["TrailingHex"] = string.Join(" ", trailing.Select(x => $"{(ushort)x:X4}")), ["LightmapVector"] = lightmap,
            ["UVs"] = string.Join("; ", uv.Select(x => x.ToString())), ["CornerPoints"] = string.Join("; ", cornerPoints.Select(x => x.ToString())),
            ["PayloadSize"] = data.Length
        };
        return new($"Terrain Patch {index:D4}", source with { OriginalIndex = index }, controlPoints, mesh, trackId, textureRid, lightmapRid, properties);
    }
}
