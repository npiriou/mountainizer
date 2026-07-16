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
    private const int TerrainPayloadMinimum = TerrainPatch.SerializedSize;

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
        ConsolidateTextureBanks(scene);
        diagnostics.Info("SSB011", $"Scene contains {scene.Terrain.Count} terrain patches, {scene.Props.Count} prop instances, {scene.Models.Count} models, {scene.Materials.Count} materials, {scene.Textures.Count} textures, {scene.Splines.Count} splines, {scene.NavigationPaths.Count} navigation paths, {scene.NavigationMarkers.Count} navigation markers, {scene.PlanarRoutes.Count} radar routes, {scene.Collisions.Count} collision meshes, {scene.SphereTrees.Count} collision sphere trees, {scene.SoundTriggerTables.Count} sound-trigger tables, {scene.StructuredTables.Count} structured tables, {scene.AudioBanks.Count} BNKl banks, {scene.AvalancheAnimations.Count} avalanche streams, {scene.ParticleModels.Count} particle models, {scene.ParticleEmitters.Count} particle emitters, {scene.Lights.Count} lights, {scene.Halos.Count} halos, {scene.NisReferenceTables.Count} NIS script-object tables, {scene.Triggers.Count} triggers, {scene.VisibilityCurtains.Count} visibility curtains and {scene.UnknownSections.Count} preserved fallback resources", ssbPath);
        return new(scene, groups, diagnostics);
    }

    private static void ConsolidateTextureBanks(MountainizerScene scene)
    {
        // Resource ids are local to a streaming-group texture bank. Preserve one
        // decoded variant per group instead of allowing a later bank to replace
        // every earlier texture that happens to reuse the same numeric id.
        var selected = scene.Textures.GroupBy(x => (x.ResourceId, x.Usage, GroupIndex: PropertyInt(x, "GroupIndex"))).Select(group =>
        {
            var meaningful = group.Where(IsMeaningful).ToArray(); return meaningful.Length > 0 ? meaningful[^1] : group.Last();
        }).OrderBy(x => x.Usage).ThenBy(x => PropertyInt(x, "GroupIndex")).ThenBy(x => x.ResourceId).ToArray();
        scene.Textures.Clear(); scene.Textures.AddRange(selected);

        static bool IsMeaningful(TextureAsset texture)
        {
            for (var i = 0; i < texture.RgbaPixels.Length; i += 4)
                if (texture.RgbaPixels[i + 3] > 4 && texture.RgbaPixels[i] + texture.RgbaPixels[i + 1] + texture.RgbaPixels[i + 2] > 6) return true;
            return false;
        }

        static int PropertyInt(ISceneItem item, string name) =>
            item.Properties.TryGetValue(name, out var value) ? Convert.ToInt32(value) : -1;
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
                type is 0 or 1 or 3 ? SupportConfidence.Medium : type is 2 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 17 or 18 ? SupportConfidence.Low : SupportConfidence.Unknown, payloadOffset);
            if (type == 0)
            {
                try { scene.Materials.Add(ParseMaterial(payload, source, trackId, resourceId, groupIndex)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB027", $"Material resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 1)
            {
                try { scene.Terrain.Add(ParseTerrain(payload, source, trackId, resourceId, groupIndex, scene.Terrain.Count, subdivisions)); }
                catch (Exception ex) { diagnostics.Error("SSB022", $"Terrain resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex); }
            }
            else if (type == 2)
            {
                try
                {
                    var model = Ssx3MdrDecoder.Decode(payload, source, trackId, resourceId);
                    model = model with { Properties = new Dictionary<string, object?>(model.Properties) { ["GroupIndex"] = groupIndex } };
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
            else if (type == 10)
            {
                try { scene.Textures.Add(TagTexture(Ssx3TextureDecoder.Decode(payload, source, trackId, resourceId, TextureUsage.Lightmap), groupIndex)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB031", $"Lightmap texture {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
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
            else if (type == 12)
            {
                var version = payload.Length < 2 ? -1 : BinaryPrimitives.ReadUInt16LittleEndian(payload);
                if (version is not (1 or 3))
                {
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
                else try
                {
                    var resolvedName = names?.Find(4, trackId, resourceId);
                    if (version == 1) scene.Collisions.Add(Ssx3CollisionDecoder.Decode(payload, source, trackId, resourceId, resolvedName));
                    else scene.SphereTrees.Add(Ssx3SphereTreeDecoder.Decode(payload, source, trackId, resourceId, resolvedName));
                }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB032", $"Collision resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 14 && payload.Length > 0)
            {
                try
                {
                    var decoded = Ssx3AipDecoder.Decode(payload, source, trackId, resourceId);
                    scene.NavigationTables.Add(decoded.Asset);
                    scene.NavigationPaths.AddRange(decoded.Paths);
                }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB033", $"AIP resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 13)
            {
                try { scene.SoundTriggerTables.Add(Ssx3SoundTriggerDecoder.Decode(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB034", $"Sound-trigger resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 21)
            {
                try { scene.PlanarRoutes.Add(Ssx3PlanarRouteDecoder.Decode(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB035", $"Planar-route resource {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 15)
            {
                try { scene.StructuredTables.Add(Ssx3StructuredTableDecoder.DecodeType15(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB036", $"Type-15 structured table {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 16)
            {
                try { scene.StructuredTables.Add(Ssx3StructuredTableDecoder.DecodeType16(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB037", $"Type-16 structured table {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 20)
            {
                try { scene.AudioBanks.Add(Ssx3BnklBankDecoder.Decode(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB038", $"BNKl bank {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 22)
            {
                try { scene.AvalancheAnimations.Add(Ssx3AvalancheDecoder.Decode(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB039", $"Avalanche animation {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 4)
            {
                try { scene.ParticleModels.Add(Ssx3EffectDecoder.DecodeParticleModel(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB040", $"Particle model {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 5)
            {
                try { scene.ParticleEmitters.Add(Ssx3EffectDecoder.DecodeParticleEmitter(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB041", $"Particle emitter {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 6)
            {
                try { scene.Lights.Add(Ssx3EffectDecoder.DecodeLight(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB042", $"Light {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 7)
            {
                try { scene.Halos.Add(Ssx3EffectDecoder.DecodeHalo(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB043", $"Halo {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 18)
            {
                try { scene.NisReferenceTables.Add(Ssx3ReferenceTableDecoder.DecodeNis(payload, source, trackId, resourceId)); }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB044", $"NIS script-object table {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 14)
            {
                scene.NavigationMarkers.Add(Ssx3ReferenceTableDecoder.DecodeNavigationMarker(source, trackId, resourceId));
            }
            else if (type == 17)
            {
                try
                {
                    var table = Ssx3CameraTriggerDecoder.Decode(payload, source, trackId, resourceId);
                    scene.CameraTriggerTables.Add(table);
                    scene.Triggers.AddRange(Ssx3CameraTriggerDecoder.CreateDebugVolumes(table, scene.Triggers.Count));
                }
                catch (Exception ex)
                {
                    diagnostics.Error("SSB030", $"Camera trigger table {resourceIndex} failed", sourceFile, source.SectionName, groupSourceOffset, ex);
                    AddUnknown(scene, payload, source, type, trackId, resourceId, payloadSize);
                }
            }
            else if (type == 3)
            {
                try { scene.Props.Add(ParseProp(payload, source, trackId, resourceId, groupIndex, scene.Props.Count, names?.Find(1, trackId, resourceId))); }
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
            if (type is 9 or 10)
            {
                var source = new SourceByteRange(sourceFile, groupSourceOffset, groupSourceLength,
                    $"SSB shared group {groupIndex}/type {type}/decompressed+0x{payloadOffset:X}", resourceIndex, SupportConfidence.Low, payloadOffset);
                try
                {
                    var usage = type == 10 ? TextureUsage.Lightmap : TextureUsage.Diffuse;
                    scene.Textures.Add(TagTexture(Ssx3TextureDecoder.Decode(data.Slice(payloadOffset, payloadSize), source, trackId, resourceId, usage), groupIndex));
                }
                catch (Exception ex) { diagnostics.Warn("SSB026", $"Shared texture {trackId}:{resourceId} could not be decoded: {ex.Message}; header={Convert.ToHexString(data.Slice(payloadOffset, Math.Min(payloadSize, 32)))}", sourceFile, source.SectionName, groupSourceOffset); }
            }
            position = payloadOffset + payloadSize; resourceIndex++;
        }
    }

    private static void AddUnknown(MountainizerScene scene, ReadOnlySpan<byte> payload, SourceByteRange source,
        int type, int trackId, int resourceId, int payloadSize)
    {
        var preview = payload[..Math.Min(payload.Length, 128)].ToArray();
        var properties = new Dictionary<string, object?> { ["ResourceType"] = type, ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["PayloadSize"] = payloadSize, ["PreviewHex"] = Convert.ToHexString(preview) };
        var name = $"Type {type} / RID {resourceId}";
        if (type == 4 && payload.Length >= 48)
        {
            properties["ParsedType"] = "SSX3 Particle Program";
            properties["SelfReference"] = ObjectId(BinaryPrimitives.ReadUInt32LittleEndian(payload));
            properties["Version"] = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            properties["HeaderSize"] = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
            properties["ReferencedObject0"] = ObjectId(BinaryPrimitives.ReadUInt32LittleEndian(payload[32..]));
            properties["ReferencedObject1"] = ObjectId(BinaryPrimitives.ReadUInt32LittleEndian(payload[44..]));
            name = $"Particle Program {trackId}:{resourceId}";
        }
        else if (type == 5 && payload.Length == 144)
        {
            var position = ReadVector3(payload, 64, source);
            var minimum = ReadVector3(payload, 104, source); var maximum = ReadVector3(payload, 116, source);
            properties["ParsedType"] = "SSX3 Particle Emitter Instance"; properties["Position"] = Ssx3Coordinates.ToMountainizer(position);
            properties["BoundingBoxMin"] = Ssx3Coordinates.ToMountainizer(minimum); properties["BoundingBoxMax"] = Ssx3Coordinates.ToMountainizer(maximum);
            properties["ParticleModelReference0"] = ObjectId(BinaryPrimitives.ReadUInt32LittleEndian(payload[96..]));
            properties["ParticleModelReference1"] = ObjectId(BinaryPrimitives.ReadUInt32LittleEndian(payload[100..]));
            name = $"Particle Emitter {trackId}:{resourceId}";
        }
        else if (type == 6 && payload.Length == 112)
        {
            var p0 = ReadVector3(payload, 56, source); var p1 = ReadVector3(payload, 68, source); var p2 = ReadVector3(payload, 80, source);
            var center = (p0 + p1 + p2) / 3f; var color = ReadVector4(payload, 32, source);
            properties["ParsedType"] = "SSX3 Light"; properties["LightKind"] = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);
            properties["Color"] = color; properties["Range"] = ReadSingle(payload, 28);
            properties["AnchorPoints"] = $"{p0}; {p1}; {p2}";
            if (center.LengthSquared() > 1f && IsFinite(center)) properties["Position"] = Ssx3Coordinates.ToMountainizer(center);
            name = $"Light {trackId}:{resourceId}";
        }
        else if (type == 7 && payload.Length == 80)
        {
            var p0 = ReadVector3(payload, 28, source); var p1 = ReadVector3(payload, 40, source); var p2 = ReadVector3(payload, 52, source);
            var center = (p0 + p1 + p2) / 3f;
            properties["ParsedType"] = "SSX3 Halo"; properties["Color"] = ReadVector3(payload, 16, source);
            properties["Position"] = Ssx3Coordinates.ToMountainizer(center);
            properties["Radius"] = (Vector3.Distance(center, p0) + Vector3.Distance(center, p1) + Vector3.Distance(center, p2)) / 3f;
            properties["AnchorPoints"] = $"{p0}; {p1}; {p2}";
            name = $"Halo {trackId}:{resourceId}";
        }
        else if (type == 18 && payload.Length % 4 == 0)
        {
            var references = new List<string>();
            for (var offset = 0; offset < payload.Length; offset += 4)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
                if (value != uint.MaxValue) references.Add(ObjectId(value));
            }
            properties["ParsedType"] = "SSX3 NIS Script-Object Table";
            properties["RuntimeConsumer"] = "cSSXScriptEngine object-transform lookup";
            properties["References"] = string.Join(", ", references);
            name = $"NIS Script-Object Table {trackId}:{resourceId}";
        }
        else if (type == 12 && payload.Length >= 16)
        {
            properties["ParsedType"] = "SSX3 Collision Variant";
            properties["Version"] = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            properties["RecordCount"] = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            properties["SectionOffsets"] = string.Join(", ", new[]
            {
                BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(payload[12..])
            });
            name = $"Collision Variant {trackId}:{resourceId}";
        }
        else if (type == 14 && payload.IsEmpty)
        {
            properties["ParsedType"] = "SSX3 Navigation Table Marker";
            name = $"Navigation Marker {trackId}:{resourceId}";
        }
        else if (type == 22 && payload.IsEmpty)
        {
            properties["ParsedType"] = "SSX3 Avalanche Animation Marker";
            name = $"Avalanche Marker {trackId}:{resourceId}";
        }
        scene.UnknownSections.Add(new(name, source, type, trackId, resourceId, preview, properties));

        static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
        static Vector3 ReadVector3(ReadOnlySpan<byte> bytes, int offset, SourceByteRange _) =>
            new(ReadSingle(bytes, offset), ReadSingle(bytes, offset + 4), ReadSingle(bytes, offset + 8));
        static Vector4 ReadVector4(ReadOnlySpan<byte> bytes, int offset, SourceByteRange _) =>
            new(ReadSingle(bytes, offset), ReadSingle(bytes, offset + 4), ReadSingle(bytes, offset + 8), ReadSingle(bytes, offset + 12));
        static string ObjectId(uint value) => $"{value & 0xff}:{value >> 8}";
    }

    private static TextureAsset TagTexture(TextureAsset texture, int groupIndex)
    {
        var properties = new Dictionary<string, object?>(texture.Properties) { ["GroupIndex"] = groupIndex };
        return texture with { Properties = properties };
    }

    private static MaterialAsset ParseMaterial(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int groupIndex)
    {
        if (data.Length < MaterialAsset.SerializedBaseSize)
            throw new Mountainizer.Core.FormatException($"Material payload is truncated ({data.Length} bytes)", source.LogicalOffset ?? 0,
                MaterialAsset.SerializedBaseSize, data.Length);
        var r = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var primaryTextureResourceId = r.ReadInt16Little();
        var textureStateWord02 = r.ReadUInt16Little();
        var packetAddressAdjustment = r.ReadUInt32Little();
        var runtimeScratch = new[] { r.ReadInt16Little(), r.ReadInt16Little() };
        var stateWord0 = r.ReadUInt16Little(); var stateWord1 = r.ReadUInt16Little();
        var textureFrameTableToken = r.ReadUInt32Little();
        var textureFrames = new List<int>();
        if (textureFrameTableToken == MaterialAsset.NoTextureFrameTableToken)
        {
            if (r.Remaining != 0)
                throw new Mountainizer.Core.FormatException("Material without a texture-frame table has trailing bytes",
                    r.AbsolutePosition, 0, r.Remaining);
        }
        else
        {
            if (r.Remaining < 4)
                throw new Mountainizer.Core.FormatException("Material texture-frame count is truncated", r.AbsolutePosition, 4, r.Remaining);
            var frameCount = r.ReadUInt32Little();
            if (frameCount == 0 || (long)frameCount * 4 != r.Remaining)
                throw new Mountainizer.Core.FormatException("Material texture-frame table has an invalid size", r.AbsolutePosition,
                    (long)frameCount * 4, r.Remaining);
            for (var i = 0u; i < frameCount; i++)
            {
                var textureId = r.ReadUInt32Little();
                if (textureId > ushort.MaxValue)
                    throw new Mountainizer.Core.FormatException("Material texture-frame RID exceeds the runtime 16-bit range", r.AbsolutePosition - 4, 4, 4);
                textureFrames.Add((int)textureId);
            }
        }

        var material = new MaterialAsset($"Material {trackId}:{resourceId}", source, trackId, resourceId, primaryTextureResourceId,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 World Material", ["TrackId"] = trackId,
                ["ResourceId"] = resourceId, ["TextureResourceId"] = primaryTextureResourceId,
                ["TextureStateWord02"] = $"0x{textureStateWord02:X4}", ["PacketAddressAdjustment"] = $"0x{packetAddressAdjustment:X8}",
                ["SerializedRuntimeScratch"] = runtimeScratch, ["StateWord0"] = $"0x{stateWord0:X4}", ["StateWord1"] = $"0x{stateWord1:X4}",
                ["SerializedTextureFrameTableToken"] = $"0x{textureFrameTableToken:X8}",
                ["TextureFrameResourceIds"] = textureFrames.ToArray(), ["GroupIndex"] = groupIndex, ["PayloadSize"] = data.Length })
        {
            TextureStateWord02 = textureStateWord02, PacketAddressAdjustment = packetAddressAdjustment,
            SerializedRuntimeScratch = runtimeScratch, StateWord0 = stateWord0, StateWord1 = stateWord1,
            SerializedTextureFrameTableToken = textureFrameTableToken, TextureFrameResourceIds = textureFrames
        };
        if (!material.HasValidObservedRetailLayout || material.ExpectedSerializedSize != data.Length)
            throw new Mountainizer.Core.FormatException("Material fields do not match the observed retail layout", source.LogicalOffset ?? 0,
                material.ExpectedSerializedSize, data.Length);
        return material;
    }

    private static PropInstance ParseProp(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int groupIndex, int index, string? resolvedName)
    {
        if (data.Length < 160) throw new Mountainizer.Core.FormatException("Prop instance payload is truncated", source.LogicalOffset ?? 0, 160, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var runtimeHeader = new uint[4]; for (var i = 0; i < 4; i++) runtimeHeader[i] = reader.ReadUInt32Little();
        var m = new float[16]; for (var i = 0; i < m.Length; i++) m[i] = reader.ReadSingleLittle();
        var transform = Ssx3Coordinates.ToMountainizerWorldTransform(new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
        var boundingSphere = reader.ReadVector4(); var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var objectTrack = reader.ReadByte(); var objectRid = reader.ReadUInt24Little(); var trackTextureSubChunkWord = reader.ReadUInt32Little();
        var modelTrack = reader.ReadByte(); var modelRid = reader.ReadUInt24Little();
        var scale = reader.ReadSingleLittle();
        var collisionMetadataScratch = reader.ReadUInt32Little();
        var runtimeScratch0 = reader.ReadUInt32Little();
        var unusedSentinelHalfword90 = reader.ReadInt16Little();
        var windDeformation = reader.ReadUInt16Little();
        var runtimeModelScratch = reader.ReadUInt32Little();
        var dmaExtensionOffset = reader.ReadUInt32Little();
        var runtimeScratch1 = reader.ReadUInt32Little();
        var dmaProgram = Ssx3InstanceDmaDecoder.Decode(data, source);
        var convertedBounds = SceneBounds.FromPoints(new[]
        {
            Ssx3Coordinates.ToMountainizer(boundsMin), Ssx3Coordinates.ToMountainizer(boundsMax)
        });
        var convertedSphereCenter = Ssx3Coordinates.ToMountainizer(new Vector3(
            boundingSphere.X, boundingSphere.Y, boundingSphere.Z));
        var locatorTrack = (int)(trackTextureSubChunkWord >> 8 & 0xff);
        var textureSubChunkId = (int)(trackTextureSubChunkWord >> 16 & 0xff);
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Prop Instance", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["ModelTrackId"] = modelTrack, ["ModelResourceId"] = modelRid, ["GroupIndex"] = groupIndex,
            ["ObjectTrackId"] = objectTrack, ["ObjectResourceId"] = objectRid, ["Position"] = new Vector3(transform.M41, transform.M42, transform.M43),
            ["BoundingSphereCenter"] = convertedSphereCenter, ["BoundingSphereRadius"] = boundingSphere.W,
            ["WorldBoundsMin"] = convertedBounds.Minimum, ["WorldBoundsMax"] = convertedBounds.Maximum,
            ["SelfReference"] = $"{objectTrack}:{objectRid}", ["LocatorTrackId"] = locatorTrack,
            ["TextureSubChunkId"] = textureSubChunkId, ["TrackTextureSubChunkWord"] = $"0x{trackTextureSubChunkWord:X8}",
            ["SerializedRuntimeHeader"] = string.Join(" ", runtimeHeader.Select(x => $"{x:X8}")),
            ["Scale"] = scale, ["UnusedSentinelHalfword90"] = unusedSentinelHalfword90,
            ["WindDeformationRaw"] = windDeformation, ["WindDeformationEnabled"] = (windDeformation & 1) != 0,
            ["DmaExtensionOffset"] = dmaExtensionOffset,
            ["DmaPrograms"] = dmaProgram.Programs.Count, ["DmaRelocations"] = dmaProgram.Programs.Sum(program => program.Relocations.Count),
            ["DmaSourceBlocks"] = dmaProgram.SourceBlocks.Count, ["DmaSourceQuadwords"] = dmaProgram.SourceBlocks.Sum(block => block.QuadwordCount),
            ["DmaVifCommands"] = dmaProgram.SourceBlocks.Sum(block => block.VifCommands.Count),
            ["DmaVifDecodeComplete"] = dmaProgram.SourceBlocks.All(block => block.VifDecodeComplete),
            ["DmaVertexColors"] = dmaProgram.SourceBlocks.Sum(block => block.VifCommands
                .Where(command => command.Name == "UNPACK_V4_5").Sum(command => command.ElementCount)),
            ["DmaStructuralBytes"] = dmaProgram.StructuralBytes, ["DmaSourceBytes"] = dmaProgram.SourceBytes,
            ["DmaScratchpadRewritePrograms"] = dmaProgram.Programs.Count(program => program.UsesScratchpadRewrite),
            ["DmaImmediateReturnRewritePrograms"] = dmaProgram.Programs.Count(program => program.UsesImmediateReturnRewrite),
            ["CollisionMetadataStorage"] = "single shared loader profile injected at runtime; constructor-only defaults in NTSC-U retail; not serialized in Type 3",
            ["RuntimeCollisionProfileCount"] = 1, ["RuntimeCollisionProfileMutable"] = false,
            ["RuntimeCollisionMetadataSlots"] = 24, ["RuntimeDefaultCollisionAttribute"] = -1,
            ["SerializedRuntimeScratch"] = $"{collisionMetadataScratch:X8} {runtimeScratch0:X8} {runtimeModelScratch:X8} {runtimeScratch1:X8}",
            ["PayloadSize"] = data.Length };
        return new(resolvedName is { Length: > 0 } ? resolvedName : $"Prop Instance {index:D4}", source with { OriginalIndex = index },
            transform, modelTrack, (int)modelRid, properties, dmaProgram);
    }

    private static Spline ParseSpline(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index, string? resolvedName)
    {
        const int headerSize = Spline.SerializedHeaderSize;
        const int segmentSize = Spline.SerializedSegmentStride;
        if (data.Length < headerSize) throw new FormatException("Spline payload is truncated", source.LogicalOffset ?? 0, headerSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var selfReference = reader.ReadUInt32Little();
        var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var runtimeScratch = reader.ReadUInt32Little(); var segmentCount = checked((int)reader.ReadUInt32Little());
        var serializedSegmentPointerToken = reader.ReadUInt32Little();
        var previousSplineSentinel = reader.ReadUInt32Little(); var runtimeScratch2 = reader.ReadUInt32Little();
        var requiredSize = (ulong)headerSize + (ulong)segmentCount * segmentSize;
        if (segmentCount == 0 || segmentCount > 100_000 || requiredSize != (ulong)data.Length)
            throw new FormatException($"Spline segment table is out of bounds ({segmentCount} segments)", source.LogicalOffset ?? 0, checked((long)segmentCount * segmentSize), data.Length - headerSize);
        if (selfReference != ((uint)resourceId << 8 | (byte)trackId) || runtimeScratch != 0 || previousSplineSentinel != uint.MaxValue
            || runtimeScratch2 != 0 || serializedSegmentPointerToken == 0 || !IsFinite(boundsMin) || !IsFinite(boundsMax)
            || boundsMin.X > boundsMax.X || boundsMin.Y > boundsMax.Y || boundsMin.Z > boundsMax.Z)
            throw new FormatException("Spline header is unknown or inconsistent", source.LogicalOffset ?? 0, headerSize, data.Length);

        var points = new List<SplinePoint>(checked((int)segmentCount * 17));
        var segments = new List<SplineSegment>(checked((int)segmentCount));
        float expectedCumulativeDistance = 0;
        Vector3? previousEnd = null;
        int? globalSegmentStartIndex = null;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var word0 = reader.ReadUInt32Little(); var word4 = reader.ReadUInt32Little(); var word8 = reader.ReadUInt32Little();
            var segmentLength = reader.ReadSingleLittle();
            var cubic = reader.ReadVector4(); var quadratic = reader.ReadVector4();
            var linear = reader.ReadVector4(); var constant = reader.ReadVector4();
            var distanceToParameter = reader.ReadVector4();
            var previousGlobalSegmentIndex = unchecked((int)reader.ReadUInt32Little());
            var nextGlobalSegmentIndex = unchecked((int)reader.ReadUInt32Little());
            var ownerSplineResourceId = checked((int)reader.ReadUInt32Little());
            var segmentMin = reader.ReadVector3(); var segmentMax = reader.ReadVector3(); var cumulativeDistance = reader.ReadSingleLittle();
            var tailTag = reader.ReadUInt32Little(); var tailFlags = reader.ReadUInt32Little();

            if (segment == 0 && segmentCount > 1)
                globalSegmentStartIndex = checked(nextGlobalSegmentIndex - 1);
            var expectedPrevious = segment == 0 ? -1 : checked(globalSegmentStartIndex!.Value + segment - 1);
            var expectedNext = segment == segmentCount - 1 ? -1 : checked(globalSegmentStartIndex!.Value + segment + 1);
            var start = ToVector3(constant);
            var end = ToVector3(cubic + quadratic + linear + constant);
            var distanceTolerance = MathF.Max(0.01f, MathF.Abs(expectedCumulativeDistance) * 0.000001f);
            if (word4 != Spline.SerializedSegmentWord4 || word8 != Spline.SerializedSegmentWord8
                || !float.IsFinite(segmentLength) || segmentLength <= 0
                || !IsFinite(cubic) || !IsFinite(quadratic) || !IsFinite(linear) || !IsFinite(constant)
                || cubic.W != 0 || quadratic.W != 0 || linear.W != 0 || constant.W != 1
                || !IsFinite(distanceToParameter) || previousGlobalSegmentIndex != expectedPrevious
                || nextGlobalSegmentIndex != expectedNext || ownerSplineResourceId != resourceId
                || !IsFinite(segmentMin) || !IsFinite(segmentMax)
                || segmentMin.X > segmentMax.X || segmentMin.Y > segmentMax.Y || segmentMin.Z > segmentMax.Z
                || MathF.Abs(cumulativeDistance - expectedCumulativeDistance) > distanceTolerance
                || !Contains(segmentMin, segmentMax, start, 0.1f) || !Contains(segmentMin, segmentMax, end, 0.1f)
                || !Contains(boundsMin, boundsMax, segmentMin, 0.1f) || !Contains(boundsMin, boundsMax, segmentMax, 0.1f)
                || previousEnd is Vector3 prior && Vector3.Distance(prior, start) > 0.1f
                || tailTag != Spline.SerializedSegmentTailTag || tailFlags != Spline.SerializedSegmentTailFlags)
                throw new FormatException($"Spline segment {segment} is unknown or inconsistent",
                    (source.LogicalOffset ?? 0) + headerSize + (long)segment * segmentSize, segmentSize, data.Length);

            var decodedSegment = new SplineSegment(segment, word0, word4, word8, segmentLength,
                cubic, quadratic, linear, constant, distanceToParameter, previousGlobalSegmentIndex,
                nextGlobalSegmentIndex, ownerSplineResourceId, segmentMin, segmentMax, cumulativeDistance,
                tailTag, tailFlags);
            segments.Add(decodedSegment);
            var control = DecodeCubicControlPoints(ToVector3(constant), ToVector3(linear), ToVector3(quadratic), ToVector3(cubic));
            for (var sample = segment == 0 ? 0 : 1; sample <= 16; sample++)
            {
                var t = sample / 16f;
                points.Add(new(Ssx3Coordinates.ToMountainizer(EvaluateBezier(control, t)), segment + t));
            }
            expectedCumulativeDistance = cumulativeDistance + segmentLength;
            previousEnd = end;
        }
        return new Spline(resolvedName is { Length: > 0 } ? resolvedName : $"Spline {index:D4}", source with { OriginalIndex = index }, points,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 World Spline", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                ["SegmentCount"] = segmentCount, ["BoundingBoxMin"] = boundsMin, ["BoundingBoxMax"] = boundsMax,
                ["SelfReference"] = $"{trackId}:{resourceId}", ["SerializedSegmentPointerToken"] = serializedSegmentPointerToken,
                ["GlobalSegmentStartIndex"] = globalSegmentStartIndex, ["TotalLength"] = expectedCumulativeDistance,
                ["SegmentWord0Values"] = string.Join(", ", segments.Select(segment => $"0x{segment.SerializedWord0:X8}").Distinct()),
                ["SegmentLinkStorage"] = "serialized per-track indices; runtime previous/next/owner pointers at +0x60/+0x64/+0x68",
                ["DistanceMapping"] = "runtime cubic distance-to-parameter polynomial at segment +0x50..+0x5C",
                ["RuntimeLoaderFunction"] = $"0x{Spline.RuntimeLoaderFunction:X8}",
                ["RuntimeEvaluateAtDistanceFunction"] = $"0x{Spline.RuntimeEvaluateAtDistanceFunction:X8}",
                ["RuntimeCalculateLengthFunction"] = $"0x{Spline.RuntimeCalculateLengthFunction:X8}",
                ["PayloadSize"] = data.Length }) { Segments = segments };
    }

    private static VisibilityCurtain ParseVisibilityCurtain(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int index)
    {
        const int knownSize = VisibilityCurtain.SerializedSize;
        if (data.Length != knownSize) throw new FormatException("Visibility curtain payload has an unknown size", source.LogicalOffset ?? 0, knownSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var boundingSphere = reader.ReadVector4();
        var cornerVectors = new Vector4[4]; for (var i = 0; i < cornerVectors.Length; i++) cornerVectors[i] = reader.ReadVector4();
        var plane = reader.ReadVector4();
        var runtimeScratch = reader.ReadBytes(VisibilityCurtain.RuntimeScratchSize).ToArray();
        var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var loadedFlag = reader.ReadUInt32Little();
        var previousListPointerScratch = reader.ReadUInt32Little(); var nextListPointerScratch = reader.ReadUInt32Little();
        var trailingScratch = new[] { reader.ReadUInt32Little(), reader.ReadUInt32Little(), reader.ReadUInt32Little() };
        var corners = cornerVectors.Select(ToVector3).ToArray();
        var cornerMin = new Vector3(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Min(point => point.Z));
        var cornerMax = new Vector3(corners.Max(point => point.X), corners.Max(point => point.Y), corners.Max(point => point.Z));
        var sphereCenter = ToVector3(boundingSphere);
        var expectedCenter = (boundsMin + boundsMax) * 0.5f;
        var expectedRadius = corners.Max(point => Vector3.Distance(point, sphereCenter));
        var expectedNormal = Vector3.Normalize(Vector3.Cross(corners[2] - corners[0], corners[1] - corners[0]));
        var planeNormal = ToVector3(plane);
        if (!IsFinite(boundingSphere) || boundingSphere.W <= 0 || cornerVectors.Any(corner => !IsFinite(corner) || corner.W != 1)
            || !IsFinite(plane) || MathF.Abs(planeNormal.LengthSquared() - 1) > 0.001f
            || Vector3.Distance(expectedNormal, planeNormal) > 0.001f
            || corners.Take(3).Any(corner => MathF.Abs(Vector3.Dot(planeNormal, corner) + plane.W) > 0.1f)
            || runtimeScratch.Any(value => value != 0) || !IsFinite(boundsMin) || !IsFinite(boundsMax)
            || Vector3.Distance(cornerMin, boundsMin) > 0.1f || Vector3.Distance(cornerMax, boundsMax) > 0.1f
            || Vector3.Distance(sphereCenter, expectedCenter) > 0.1f || MathF.Abs(boundingSphere.W - expectedRadius) > 0.1f
            || loadedFlag != 1 || previousListPointerScratch != 0 || nextListPointerScratch != 0
            || trailingScratch.Any(value => value != 0))
            throw new FormatException("Visibility curtain record is unknown or inconsistent", source.LogicalOffset ?? 0, knownSize, data.Length);

        var points = corners.Select(Ssx3Coordinates.ToMountainizer).Append(Ssx3Coordinates.ToMountainizer(corners[0])).ToArray();
        var convertedBounds = SceneBounds.FromPoints(new[] { Ssx3Coordinates.ToMountainizer(boundsMin), Ssx3Coordinates.ToMountainizer(boundsMax) });
        return new VisibilityCurtain($"Visibility Curtain {index:D4}", source with { OriginalIndex = index, Confidence = SupportConfidence.Medium }, points,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Visibility Curtain Quadrilateral", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
                ["CornerCount"] = corners.Length, ["BoundingSphereCenterSsx"] = sphereCenter, ["BoundingSphereRadius"] = boundingSphere.W,
                ["PlaneNormalSsx"] = planeNormal, ["PlaneConstant"] = plane.W,
                ["BoundingBoxMinSsx"] = boundsMin, ["BoundingBoxMaxSsx"] = boundsMax,
                ["SerializedLoadedFlag"] = loadedFlag, ["RuntimeScratchBytes"] = runtimeScratch.Length,
                ["RuntimeIntrusiveListPointers"] = "+0xBC/+0xC0", ["RuntimeMaximumSelectedCurtains"] = VisibilityCurtain.RuntimeMaximumSelectedCurtains,
                ["RuntimeInsertFunction"] = $"0x{VisibilityCurtain.RuntimeInsertFunction:X8}",
                ["RuntimeRemoveFunction"] = $"0x{VisibilityCurtain.RuntimeRemoveFunction:X8}",
                ["RuntimeSelectFunction"] = $"0x{VisibilityCurtain.RuntimeSelectFunction:X8}",
                ["RuntimeCandidateMetric"] = "squared SSX-space viewer distance to bounding-sphere center (XYZ only)",
                ["RuntimeCandidateSort"] = "ascending; nearest two eligible curtains",
                ["RuntimeCandidateComparatorFunction"] = $"0x{VisibilityCurtain.RuntimeCandidateComparatorFunction:X8}",
                ["RuntimePrepareFunction"] = $"0x{VisibilityCurtain.RuntimePrepareFunction:X8}",
                ["RuntimeInstallPlaneFunction"] = $"0x{VisibilityCurtain.RuntimeInstallPlaneFunction:X8}",
                ["PayloadSize"] = data.Length })
        {
            CornersSsx = corners, BoundingSphereSsx = boundingSphere, PlaneSsx = plane,
            Bounds = convertedBounds, LoadedFlag = loadedFlag
        };
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Contains(Vector3 minimum, Vector3 maximum, Vector3 point, float tolerance) =>
        point.X >= minimum.X - tolerance && point.X <= maximum.X + tolerance
        && point.Y >= minimum.Y - tolerance && point.Y <= maximum.Y + tolerance
        && point.Z >= minimum.Z - tolerance && point.Z <= maximum.Z + tolerance;

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

    private static TerrainPatch ParseTerrain(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId, int groupIndex, int index, int subdivisions)
    {
        if (data.Length != TerrainPayloadMinimum) throw new FormatException("Terrain payload does not match the exact retail structure", source.LogicalOffset ?? 0, TerrainPayloadMinimum, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var serializedTrackWord = reader.ReadUInt32Little(); var headerWord = reader.ReadUInt32Little();
        var surfaceValue = reader.ReadUInt16Little(); var patchFlags = reader.ReadUInt16Little();
        var textureStateFlags = reader.ReadUInt16Little(); var renderFlags = reader.ReadUInt16Little();
        var lightmap = reader.ReadVector4();
        var uv = new Vector2[4]; for (var i = 0; i < uv.Length; i++) uv[i] = reader.ReadVector2();
        var storedVectors = new Vector4[16];
        var stored = new Vector3[16];
        for (var i = 0; i < stored.Length; i++) { var value = reader.ReadVector4(); storedVectors[i] = value; stored[i] = new(value.X, value.Y, value.Z); }
        var boundingSphere = reader.ReadVector4();
        var objectTrack = reader.ReadByte(); var objectRid = reader.ReadUInt24Little();
        var textureBankTrackWord = reader.ReadUInt16Little(); var textureSubChunkId = reader.ReadUInt16Little();
        var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        var cornerPoints = new Vector3[4]; for (var i = 0; i < 4; i++) cornerPoints[i] = reader.ReadVector3();
        var textureRid = reader.ReadInt16Little(); var lightmapRid = reader.ReadInt16Little();
        var secondaryTextureRid = reader.ReadInt16Little();
        var queueValues = new[] { reader.ReadInt16Little(), reader.ReadInt16Little(), reader.ReadInt16Little() };
        var tailWord0 = reader.ReadUInt16Little(); var tailWord1 = reader.ReadUInt16Little();
        var controlPointsSsx = TerrainMeshBuilder.DecodeControlPointsSsx(stored);
        var controlPoints = controlPointsSsx.Select(Ssx3Coordinates.ToMountainizer).ToArray();
        var mesh = TerrainMeshBuilder.Tessellate(controlPoints, subdivisions, uv, lightmap);
        var expectedCorners = new[] { controlPointsSsx[0], controlPointsSsx[12], controlPointsSsx[3], controlPointsSsx[15] };
        static bool Near(Vector3 a, Vector3 b, float tolerance = 2f) => Vector3.Distance(a, b) <= tolerance;
        var calculatedMinimum = new Vector3(controlPointsSsx.Min(value => value.X), controlPointsSsx.Min(value => value.Y), controlPointsSsx.Min(value => value.Z));
        var calculatedMaximum = new Vector3(controlPointsSsx.Max(value => value.X), controlPointsSsx.Max(value => value.Y), controlPointsSsx.Max(value => value.Z));
        var sphereCenter = new Vector3(boundingSphere.X, boundingSphere.Y, boundingSphere.Z);
        if (!Near(boundsMin, calculatedMinimum) || !Near(boundsMax, calculatedMaximum)
            || !cornerPoints.Select((value, corner) => Near(value, expectedCorners[corner], 0.02f)).All(valid => valid)
            || controlPointsSsx.Any(value => Vector3.Distance(sphereCenter, value) > boundingSphere.W * 1.002f + 0.01f))
            throw new FormatException("Terrain bounds, corner controls, or bounding sphere do not match the bicubic patch", source.LogicalOffset ?? 0,
                TerrainPatch.SerializedSize, data.Length);
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 World Patch", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["GroupIndex"] = groupIndex,
            ["ObjectTrackId"] = objectTrack, ["ObjectResourceId"] = objectRid, ["TextureResourceId"] = textureRid,
            ["LightmapResourceId"] = lightmapRid, ["SecondaryTextureResourceId"] = secondaryTextureRid,
            ["BoundingBoxMin"] = boundsMin, ["BoundingBoxMax"] = boundsMax, ["BoundingSphere"] = boundingSphere,
            ["SerializedTrackWord"] = $"0x{serializedTrackWord:X8}", ["HeaderWord"] = $"0x{headerWord:X8}",
            ["Surface"] = ((SsxSurfaceType)surfaceValue).ToString(), ["PatchFlags"] = $"0x{patchFlags:X4}",
            ["TextureStateFlags"] = $"0x{textureStateFlags:X4}", ["RenderFlags"] = $"0x{renderFlags:X4}",
            ["RequestsRuntimeSecondaryPass"] = (renderFlags & TerrainPatch.RuntimeSecondaryPassMask) != 0,
            ["RuntimeSecondaryPassUsesDestinationAlpha"] = (renderFlags & TerrainPatch.RuntimeDestinationAlphaMask) != 0,
            ["RuntimeSecondaryPassBlendEquation"] = (renderFlags & TerrainPatch.RuntimeSecondaryPassMask) == 0
                ? null : (renderFlags & TerrainPatch.RuntimeDestinationAlphaMask) != 0
                    ? TerrainPatch.RuntimeSecondaryBlendEquation : TerrainPatch.RuntimeFallbackBlendEquation,
            ["TextureBankTrackWord"] = $"0x{textureBankTrackWord:X4}", ["TextureSubChunkId"] = textureSubChunkId,
            ["QueueValues"] = queueValues, ["TailWords"] = $"{tailWord0:X4} {tailWord1:X4}", ["LightmapVector"] = lightmap,
            ["UVs"] = string.Join("; ", uv.Select(x => x.ToString())), ["CornerPoints"] = string.Join("; ", cornerPoints.Select(x => x.ToString())),
            ["PayloadSize"] = data.Length
        };
        var patch = new TerrainPatch($"Terrain Patch {index:D4}", source with { OriginalIndex = index }, controlPoints, mesh, trackId, textureRid, lightmapRid, properties)
        {
            SerializedTrackWord = serializedTrackWord, HeaderWord = headerWord, Surface = (SsxSurfaceType)surfaceValue,
            PatchFlags = patchFlags, TextureStateFlags = textureStateFlags, RenderFlags = renderFlags,
            LightmapRectangle = lightmap, DiffuseUvCorners = uv, StoredDifferenceCoefficientsSsx = storedVectors,
            BoundingSphereSsx = boundingSphere, ObjectTrackId = objectTrack, ObjectResourceId = checked((int)objectRid),
            TextureBankTrackWord = textureBankTrackWord, TextureSubChunkId = textureSubChunkId,
            BoundsMinimumSsx = boundsMin, BoundsMaximumSsx = boundsMax, CornerControlPointsSsx = cornerPoints,
            SecondaryTextureResourceId = secondaryTextureRid, QueueValues = queueValues, TailWord0 = tailWord0, TailWord1 = tailWord1
        };
        if (!patch.HasValidObservedRetailLayout || patch.ObjectResourceId != resourceId)
            throw new FormatException("Terrain fields do not match the observed retail layout", source.LogicalOffset ?? 0,
                TerrainPatch.SerializedSize, data.Length);
        return patch;
    }
}
