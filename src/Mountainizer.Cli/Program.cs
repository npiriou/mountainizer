using System.Numerics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Mountainizer.Core;
using Mountainizer.Export;
using Mountainizer.Formats;
using Mountainizer.Iso;

return await MountainizerCli.RunAsync(args);

internal static class MountainizerCli
{
    private sealed record InstanceDmaSurvey(
        int ExpectedPrograms,
        int ParsedPrograms,
        int ModelReferenceTags,
        int SourceReferenceTags,
        int StructuralRecordCount,
        int ReturnTags,
        int SourceQuadwords,
        int SprReferenceTags,
        int SprImmediateReturnRewrites,
        int SprExtendedRewrites,
        int InvalidTagCount,
        int InvalidSourceTagCount,
        int InvalidSourceRangeCount,
        int InvalidModelRangeCount,
        int MisalignedSourceAddressCount,
        int TerminalMscalPlaceholderCount,
        int InvalidTerminalMscalPlaceholderCount,
        int StructuralWorkspaceViolations,
        int UnreferencedPayloadBytes);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Help(); return 0; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "identify" when args.Length >= 2 => await Identify(args[1]),
                "list-files" when args.Length >= 2 => ListFiles(args[1]),
                "extract-file" when args.Length >= 4 => ExtractFile(args[1], args[2], args[3]),
                "extract" when args.Length >= 3 => await Import(args[1], args[2], Path.GetFileNameWithoutExtension(args[1])),
                "import" when args.Length >= 4 => await Import(args[1], args[2], args[3]),
                "list-levels" when args.Length >= 2 => ListLevels(args[1]),
                "audit" when args.Length >= 2 => Audit(args),
                "inspect" when args.Length >= 3 => Inspect(args),
                "dump-resource" when args.Length >= 7 => DumpResource(args),
                "survey-resource" when args.Length >= 3 => SurveyResource(args),
                "dump-textures" when args.Length >= 4 => DumpTextures(args),
                "export" when args.Length >= 3 => Export(args),
                _ => Fail("Invalid command or arguments. Run with --help.")
            };
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static async Task<int> Identify(string isoPath)
    {
        var diagnostics = new DiagnosticBag();
        var result = await ProjectService.IdentifyAsync(isoPath, diagnostics, Progress());
        Console.WriteLine(JsonSerializer.Serialize(new { result, Diagnostics = diagnostics.Items }, DiagnosticBag.JsonOptions)); return 0;
    }
    private static int ListFiles(string isoPath)
    {
        using var iso = Iso9660Image.Open(isoPath);
        foreach (var entry in iso.Entries.OrderBy(x => x.Path)) Console.WriteLine($"{entry.Length,12}  {(entry.IsDirectory ? "d" : "f")}  {entry.Path}");
        return 0;
    }
    private static int ExtractFile(string isoPath, string isoFilePath, string outputPath)
    {
        using var iso = Iso9660Image.Open(isoPath);
        var entry = iso.Find(isoFilePath) ?? throw new FileNotFoundException($"'{isoFilePath}' was not found in the ISO");
        if (entry.IsDirectory) throw new InvalidDataException($"'{isoFilePath}' is a directory");
        iso.Extract(entry, outputPath);
        Console.WriteLine($"Wrote {Path.GetFullPath(outputPath)}");
        return 0;
    }
    private static async Task<int> Import(string isoPath, string output, string name)
    {
        var diagnostics = new DiagnosticBag();
        var project = await ProjectService.ImportAsync(isoPath, output, name, diagnostics, Progress());
        Console.WriteLine($"Created {Path.Combine(project.ProjectDirectory, "project.json")}");
        return diagnostics.HasErrors ? 2 : 0;
    }
    private static int ListLevels(string projectPath)
    {
        var project = ProjectService.Open(projectPath); var diagnostics = new DiagnosticBag();
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(ProjectService.WorldFile(project, ".sdb")), ProjectService.WorldFile(project, ".sdb"), diagnostics);
        Console.WriteLine("Playable courses:");
        foreach (var course in Ssx3CourseCatalog.Courses)
            Console.WriteLine($"  {course.Code,-5}  Peak {course.Peak}  {course.Name,-22} {course.Discipline,-12} areas: {string.Join(", ", Ssx3CourseCatalog.ResolveAreas(sdb, course).Select(x => x.Name))}");
        Console.WriteLine("Technical streaming areas:");
        foreach (var area in sdb.Areas) Console.WriteLine($"  {area.OriginalIndex,2}  {area.Name,-16} groups {area.FirstGroup}..{area.FirstGroup + area.GroupCount - 1}");
        return 0;
    }
    private static int Inspect(string[] args)
    {
        var project = ProjectService.Open(args[1]); var level = args[2];
        var output = Option(args, "--json");
        var parsed = Parse(project, level);
        static long Key(int track, int resource) => ((long)track << 32) | (uint)resource;
        var decodedModelIds = parsed.Scene.Models.Where(x => x.Mesh is not null).Select(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).ToHashSet();
        var referencedModelIds = parsed.Scene.Props.Select(x => Key(x.ModelTrackId, x.ModelResourceId)).Distinct().ToArray();
        var propNames = parsed.Scene.Props.GroupBy(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])))
            .ToDictionary(x => x.Key, x => x.Last().Name);
        var propIds = propNames.Keys.ToHashSet();
        var splineIds = parsed.Scene.Splines.Select(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).ToHashSet();
        bool ResolvesWorldModifierReference(WorldModifierRecord record) => record.ReferencedResourceType switch
        {
            8 when record.ReferencedTrackId is int trackId && record.ReferencedResourceId is int resourceId => splineIds.Contains(Key(trackId, resourceId)),
            3 when record.ReferencedTrackId is int trackId && record.ReferencedResourceId is int resourceId => propIds.Contains(Key(trackId, resourceId)),
            _ => false
        };
        var textureIds = parsed.Scene.Textures.Select(x => x.ResourceId).ToHashSet();
        var materialTextures = parsed.Scene.Materials.GroupBy(x => Key(x.TrackId, x.ResourceId)).ToDictionary(x => x.Key, x => (int)x.Last().TextureResourceId);
        var modelMaterialIds = parsed.Scene.Models.SelectMany(x => x.Submeshes).Where(x => x.MaterialResourceId >= 0).Select(x => Key(x.MaterialTrackId, x.MaterialResourceId)).Distinct().ToArray();
        var terrainNormals = parsed.Scene.Terrain.SelectMany(x => x.Mesh.Normals).ToArray();
        var textureResolver = new SceneTextureResolver(parsed.Scene);
        var report = JsonSerializer.Serialize(new { Level = level, TerrainPatches = parsed.Scene.Terrain.Count, PropInstances = parsed.Scene.Props.Count,
            Models = parsed.Scene.Models.Count, ModelsWithGeometry = parsed.Scene.Models.Count(x => x.Mesh is not null), Materials = parsed.Scene.Materials.Count, Textures = parsed.Scene.Textures.Count,
            ReferencedModels = referencedModelIds.Length, ResolvedInstances = parsed.Scene.Props.Count(x => decodedModelIds.Contains(Key(x.ModelTrackId, x.ModelResourceId))),
            InstanceDmaPrograms = parsed.Scene.Props.Sum(x => x.RenderDmaProgram?.Programs.Count ?? 0),
            InstanceDmaRelocations = parsed.Scene.Props.Sum(x => x.RenderDmaProgram?.Programs.Sum(program => program.Relocations.Count) ?? 0),
            InstanceDmaSourceBlocks = parsed.Scene.Props.Sum(x => x.RenderDmaProgram?.SourceBlocks.Count ?? 0),
            InstanceDmaSourceQuadwords = parsed.Scene.Props.Sum(x => x.RenderDmaProgram?.SourceBlocks.Sum(block => block.QuadwordCount) ?? 0),
            ModelMaterials = modelMaterialIds.Length, ResolvedModelMaterials = modelMaterialIds.Count(x => materialTextures.TryGetValue(x, out var texture) && textureIds.Contains(texture)),
            ModelSubmeshes = parsed.Scene.Models.Sum(x => x.Submeshes.Count),
            TexturedModelSubmeshes = parsed.Scene.Models.SelectMany(x => x.Submeshes).Count(x => materialTextures.TryGetValue(Key(x.MaterialTrackId, x.MaterialResourceId), out var texture) && textureIds.Contains(texture)),
            Splines = parsed.Scene.Splines.Count, NavigationPaths = parsed.Scene.NavigationPaths.Count,
            NavigationEvents = parsed.Scene.NavigationPaths.Sum(x => x.Events.Count),
            NavigationTables = parsed.Scene.NavigationTables.Count,
            NavigationAiPaths = parsed.Scene.NavigationTables.Sum(x => x.AiPaths.Count),
            NavigationTrackPaths = parsed.Scene.NavigationTables.Sum(x => x.TrackPaths.Count),
            NavigationEncodedPoints = parsed.Scene.NavigationPaths.Sum(x => x.EncodedPoints.Count),
            NavigationNonnegativePointWeights = parsed.Scene.NavigationPaths.Sum(path => path.EncodedPoints.Count(point =>
                point.Weight >= 0)),
            NavigationTaggedProperties = parsed.Scene.NavigationPaths.SelectMany(x => x.TaggedProperties)
                .GroupBy(x => new { x.Kind, PayloadLength = x.Payload.Length, x.UInt32Value })
                .OrderBy(x => x.Key.Kind).ThenBy(x => x.Key.UInt32Value)
                .Select(x => new { x.Key.Kind, x.Key.PayloadLength, x.Key.UInt32Value, Count = x.Count() }).ToArray(),
            NavigationEventTypes = parsed.Scene.NavigationPaths.SelectMany(x => x.Events)
                .GroupBy(x => new { x.Type, x.RuntimeKindIndex }).OrderBy(x => x.Key.RuntimeKindIndex)
                .Select(x => new { x.Key.Type, x.Key.RuntimeKindIndex, Count = x.Count() }).ToArray(),
            NavigationEventDistancesWithinPathLength = parsed.Scene.NavigationPaths.Sum(path => path.Events.Count(pathEvent =>
                pathEvent.StartDistance >= 0 && pathEvent.StartDistance <= path.TotalLength + 0.01f
                && pathEvent.EndDistance >= 0 && pathEvent.EndDistance <= path.TotalLength + 0.01f)),
            NavigationWrappedEventRanges = parsed.Scene.NavigationPaths.Sum(path => path.Events.Count(pathEvent =>
                pathEvent.StartDistance > pathEvent.EndDistance)),
            NavigationEventIntervalExceptions = parsed.Scene.NavigationPaths.SelectMany(path => path.Events.Select((pathEvent, index) =>
                    new { path.Name, EventIndex = index, pathEvent.Type, pathEvent.Value, pathEvent.StartDistance,
                        pathEvent.EndDistance, path.TotalLength }))
                .Where(x => x.StartDistance < 0 || x.StartDistance > x.TotalLength + 0.01f
                    || x.EndDistance < 0 || x.EndDistance > x.TotalLength + 0.01f).ToArray(),
            NavigationTailPairs = parsed.Scene.NavigationTables.Sum(x => x.TailPairs.Count),
            NavigationTailPairValues = parsed.Scene.NavigationTables.SelectMany(x => x.TailPairs)
                .GroupBy(x => new { x.Word0, x.Word1 }).OrderByDescending(x => x.Count())
                .Select(x => new { x.Key.Word0, PathDistance = BitConverter.UInt32BitsToSingle(x.Key.Word0), x.Key.Word1, Count = x.Count() }).ToArray(),
            NavigationLinks = parsed.Scene.NavigationTables.Sum(x => x.Links.Count),
            NavigationLinkKinds = parsed.Scene.NavigationTables.SelectMany(x => x.Links)
                .GroupBy(x => new { x.RawKind, x.RuntimeKindIndex }).OrderBy(x => x.Key.RuntimeKindIndex)
                .Select(x => new { x.Key.RawKind, x.Key.RuntimeKindIndex, Count = x.Count() }).ToArray(),
            NavigationLinkDetails = parsed.Scene.NavigationTables.SelectMany(table => table.Links.Select((link, index) =>
                {
                    var aiPath = table.AiPaths[link.AiPathIndex];
                    var trackPath = table.TrackPaths[link.TrackPathIndex];
                    return new
                    {
                        table.TrackId, table.ResourceId, Index = index, link.Value, link.RawKind, link.RuntimeKindIndex,
                        link.Position, link.Direction, DirectionLength = link.Direction.Length(),
                        link.AiPathIndex, link.TrackPathIndex,
                        DistanceToAiStart = Vector3.Distance(link.Position, aiPath.Points[0]),
                        DistanceToAiEnd = Vector3.Distance(link.Position, aiPath.Points[^1]),
                        DistanceToTrackStart = Vector3.Distance(link.Position, trackPath.Points[0]),
                        DistanceToTrackEnd = Vector3.Distance(link.Position, trackPath.Points[^1])
                    };
                })).ToArray(),
            CollisionAssets = parsed.Scene.Collisions.Count, CollisionSubmeshes = parsed.Scene.Collisions.Sum(x => x.Submeshes.Count),
            CollisionTriangles = parsed.Scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.Indices.Count / 3)),
            CollisionTriangleBatches = parsed.Scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.TriangleBatches.Count)),
            CollisionEmptyAssets = parsed.Scene.Collisions.Count(x => x.Submeshes.Sum(s => s.Indices.Count) == 0),
            CollisionEmptySubmeshes = parsed.Scene.Collisions.Sum(x => x.Submeshes.Count(s => s.Vertices.Count == 0 || s.Indices.Count == 0)),
            CollisionSphereTreeAssets = parsed.Scene.SphereTrees.Count,
            CollisionSphereTreeRecords = parsed.Scene.SphereTrees.Sum(x => x.Trees.Count),
            CollisionSphereTreePackedBytes = parsed.Scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.PackedPayloadSize)),
            CollisionSphereTreeDecodedNodeBytes = parsed.Scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.DecodedNodeMasks.Length)),
            CollisionSphereTreeReferencedNodes = parsed.Scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.NodeLevels.Sum(level => level.ReferencedNodeCount))),
            CollisionSphereTreeChildLinks = parsed.Scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.NodeLevels.Sum(level => level.ReferencedChildCount))),
            SoundTriggerTables = parsed.Scene.SoundTriggerTables.Count,
            SoundTriggerBindings = parsed.Scene.SoundTriggerTables.Sum(x => x.Bindings.Count),
            SoundTriggerUniqueBindingIdentities = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                .Select(x => x.SerializedIdentity).Distinct().Count(),
            SoundTriggerBlocks = parsed.Scene.SoundTriggerTables.Sum(x => x.Blocks.Count),
            SoundTriggerInfoReferences = parsed.Scene.SoundTriggerTables.Sum(x => x.Blocks.Sum(block => block.TriggerInfoIds.Count)),
            SoundTriggerInfoDefinitions = parsed.Scene.SoundTriggerTables.SelectMany(table => table.Blocks).SelectMany(block =>
                    block.SharedTriggerInfoIds.Select(id => (Id: id, Channel: "Shared"))
                        .Concat(block.SpatialDescriptors.Select(descriptor => (Id: descriptor.TriggerInfoId, Channel: "Spatial"))))
                .GroupBy(x => x.Id).OrderBy(x => x.Key).Select(group =>
                {
                    var definition = Ssx3SoundTriggerDecoder.TriggerInfoDefinition(group.Key);
                    return new { Id = group.Key, definition?.Kind, definition?.Name, definition?.SoundBankId,
                        definition?.SoundBankName, definition?.SoundIndex,
                        Channels = group.Select(x => x.Channel).Distinct().Order().ToArray(),
                        Count = group.Count() };
                }).ToArray(),
            SoundTriggerSpatialDescriptors = parsed.Scene.SoundTriggerTables.Sum(x => x.Blocks.Sum(block => block.SpatialDescriptors.Count)),
            SoundTriggerBlockShapes = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                .GroupBy(x => new { TriggerInfoIds = x.TriggerInfoIds.Count, SpatialDescriptors = x.SpatialDescriptors.Count })
                .OrderBy(x => x.Key.TriggerInfoIds).ThenBy(x => x.Key.SpatialDescriptors)
                .Select(x => new { x.Key.TriggerInfoIds, x.Key.SpatialDescriptors, Count = x.Count() }).ToArray(),
            SoundTriggerSpatialDescriptorKinds = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Blocks).SelectMany(x => x.SpatialDescriptors)
                .GroupBy(x => x.Kind).OrderBy(x => x.Key)
                .Select(x => new { Kind = x.Key, Name = Ssx3SoundTriggerDecoder.SpatialDescriptorKindName(x.Key), Count = x.Count() }).ToArray(),
            SoundTriggerDistanceFalloffCurves = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                .SelectMany(x => x.SpatialDescriptors).Where(x => x.DistanceFalloffCurve is not null)
                .GroupBy(x => x.DistanceFalloffCurve).OrderBy(x => x.Key)
                .Select(x => new { Curve = x.Key, Value = (int)x.Key!.Value, Count = x.Count() }).ToArray(),
            SoundTriggerAngularFalloffCurves = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                .SelectMany(x => x.SpatialDescriptors).Where(x => x.AngularFalloffCurve is not null)
                .GroupBy(x => x.AngularFalloffCurve).OrderBy(x => x.Key)
                .Select(x => new { Curve = x.Key, Value = (int)x.Key!.Value, Count = x.Count() }).ToArray(),
            SoundTriggerSpatialDescriptorDetails = parsed.Scene.SoundTriggerTables.SelectMany(table => table.Blocks
                .SelectMany((block, blockIndex) => block.SpatialDescriptors.Select((descriptor, descriptorIndex) => new
                {
                    table.TrackId, table.ResourceId, BlockIndex = blockIndex, DescriptorIndex = descriptorIndex,
                    descriptor.Kind, KindName = Ssx3SoundTriggerDecoder.SpatialDescriptorKindName(descriptor.Kind),
                    descriptor.TriggerInfoId, descriptor.Position, descriptor.Radius, descriptor.SemiAxisLengths,
                    descriptor.OrientationAxis, descriptor.DistanceFalloffCurve,
                    descriptor.ConeCosineThreshold, descriptor.AngularFalloffCurve
                }))).ToArray(),
            SoundTriggerBindingDetails = parsed.Scene.SoundTriggerTables.SelectMany(table => table.Bindings
                .Select((binding, bindingIndex) =>
                {
                    var block = table.Blocks[binding.BlockIndex];
                    var anchorName = binding.AnchorObjectReference is PackedObjectReference anchor
                        && propNames.TryGetValue(Key(anchor.TrackId, anchor.ResourceId), out var name) ? name : null;
                    return new
                    {
                        TableTrackId = table.TrackId, TableResourceId = table.ResourceId, BindingIndex = bindingIndex,
                        Identity = binding.SerializedIdentity.ToString(), IdentityMatchesAnchorName = anchorName is not null
                            && binding.SerializedIdentity.MatchesName(anchorName), binding.BlockIndex,
                        AnchorTrackId = binding.AnchorObjectReference?.TrackId,
                        AnchorResourceId = binding.AnchorObjectReference?.ResourceId,
                        AnchorName = anchorName,
                        SharedTriggerInfoIds = block.SharedTriggerInfoIds,
                        SpatialTriggerInfoIds = block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId).ToArray()
                    };
                })).ToArray(),
            SoundTriggerObjectReferences = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                .Count(x => x.AnchorObjectReference is not null),
            SoundTriggerUnanchoredBindings = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                .Count(x => x.AnchorObjectReference is null),
            ResolvedSoundTriggerObjectReferences = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                .Count(x => x.AnchorObjectReference is not null && propIds.Contains(Key(x.ObjectTrackId, x.ObjectResourceId))),
            SoundTriggerReferencedProps = parsed.Scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                .Where(x => x.AnchorObjectReference is not null)
                .Select(x => parsed.Scene.Props.FirstOrDefault(prop => Convert.ToInt32(prop.Properties["TrackId"]) == x.ObjectTrackId
                    && Convert.ToInt32(prop.Properties["ResourceId"]) == x.ObjectResourceId)?.Name)
                .Where(x => x is not null).GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
                .Select(x => new { Name = x.Key, Count = x.Count() }).ToArray(),
            PlanarRoutes = parsed.Scene.PlanarRoutes.Count,
            PlanarRouteSamples = parsed.Scene.PlanarRoutes.Sum(x => x.Samples.Count),
            PlanarRouteMarkers = parsed.Scene.PlanarRoutes.Sum(x => x.Markers.Count),
            PlanarRouteMarkerDetails = parsed.Scene.PlanarRoutes.SelectMany(route => route.Markers.Select((marker, index) => new
                { route.TrackId, route.ResourceId, Index = index, marker.Kind, marker.Distance })).ToArray(),
            StructuredType15Tables = parsed.Scene.StructuredTables.Count(x => x.ResourceType == 15),
            StructuredType16Tables = parsed.Scene.StructuredTables.Count(x => x.ResourceType == 16),
            StructuredType16TableDetails = parsed.Scene.StructuredTables.Where(table => table.ResourceType == 16)
                .OrderBy(table => table.TrackId).ThenBy(table => table.ResourceId).Select(table => new
                {
                    table.TrackId, table.ResourceId, RailReferenceSets = table.RailReferenceSets.Count,
                    TerrainPatchesOnTrack = parsed.Scene.Terrain.Count(patch => patch.TrackId == table.TrackId),
                    PropsOnTrack = parsed.Scene.Props.Count(prop => Convert.ToInt32(prop.Properties["TrackId"]) == table.TrackId),
                    SplinesOnTrack = parsed.Scene.Splines.Count(spline => Convert.ToInt32(spline.Properties["TrackId"]) == table.TrackId),
                    NavigationPathsOnTrack = parsed.Scene.NavigationPaths.Count(path => Convert.ToInt32(path.Properties["TrackId"]) == table.TrackId),
                    CollisionAssetsOnTrack = parsed.Scene.Collisions.Count(asset => asset.TrackId == table.TrackId),
                    SoundTriggerTablesOnTrack = parsed.Scene.SoundTriggerTables.Count(asset => asset.TrackId == table.TrackId),
                    CameraTriggerTablesOnTrack = parsed.Scene.CameraTriggerTables.Count(asset => asset.TrackId == table.TrackId),
                    RailRoots = table.RootRailReferences.Count, GeneratedRailPrograms = table.RailProgramRecords.Count,
                    ModifierProgramBlocks = table.ModifierProgramBlocks.Count,
                    ModifierProgramGroups = table.ModifierProgramGroups.Count, LunPrograms = table.LunPrograms.Count,
                    SetShapes = table.RailReferenceSets.GroupBy(set => string.Join(",",
                            set.Slots.Select((reference, slot) => (reference, slot)).Where(item => item.reference is not null)
                                .Select(item => item.slot)))
                        .OrderBy(group => group.Key).Select(group => new
                            { PopulatedSlots = group.Key.Length == 0 ? "empty" : group.Key, Count = group.Count() }).ToArray(),
                    SlotProfiles = Enumerable.Range(0, 6).Select(slot =>
                    {
                        var references = table.RailReferenceSets.Select(set => set.Slots[slot]).Where(reference => reference is not null)
                            .Select(reference => reference!).ToArray();
                        return new
                        {
                            Slot = slot, ObservedRole = slot == 0 ? "Reserved/unused in the course corpus" : null,
                            Populated = references.Length,
                            SourceSplineReferences = references.Count(reference => reference.TrackId == table.TrackId
                                && reference.RailId < table.RailSplineMetadataEntries.Count),
                            GeneratedRailReferences = references.Count(reference => reference.TrackId == table.TrackId
                                && reference.RailId >= table.RailSplineMetadataEntries.Count),
                            Roles = references.Where(reference => reference.TrackId == table.TrackId
                                    && reference.RailId < table.RailSplineMetadataEntries.Count)
                                .GroupBy(reference => table.RailSplineMetadataEntries[reference.RailId].Role)
                                .OrderBy(group => group.Key).Select(group => new { Role = group.Key, Count = group.Count() }).ToArray(),
                            Surfaces = references.Where(reference => reference.TrackId == table.TrackId
                                    && reference.RailId < table.RailSplineMetadataEntries.Count)
                                .GroupBy(reference => table.RailSplineMetadataEntries[reference.RailId].Surface)
                                .OrderBy(group => group.Key).Select(group => new { Surface = group.Key, Count = group.Count() }).ToArray()
                        };
                    }).ToArray()
                }).ToArray(),
            StructuredTableSections = parsed.Scene.StructuredTables.Sum(x => x.Sections.Count),
            WorldPainterSections = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Count),
            WorldPainterRecords = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.Records.Count)),
            WorldModifierSections = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Count),
            WorldModifierRecords = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.Records.Count)),
            WorldModifierReferences = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections).SelectMany(section => section.Records)
                .Count(record => record.ReferencedResourceType is not null),
            ResolvedWorldModifierReferences = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections).SelectMany(section => section.Records)
                .Count(ResolvesWorldModifierReference),
            WorldModifierIndexEntries = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.SpatialIndex.EntryCount)),
            WorldModifierBranchChildReferences = parsed.Scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section =>
                section.SpatialIndex.Entries.Sum(entry => entry.Children.Count))),
            WorldModifierIndexEntryKinds = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections)
                .SelectMany(section => section.SpatialIndex.Entries).GroupBy(entry => entry.Kind).OrderBy(group => group.Key)
                .Select(group => new { Kind = group.Key, Count = group.Count() }).ToArray(),
            WorldModifierTypes = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections)
                .GroupBy(x => new { x.TypeId, x.TypeName }).OrderBy(x => x.Key.TypeId)
                .Select(x => new { x.Key.TypeId, x.Key.TypeName, Sections = x.Count(), Records = x.Sum(section => section.RecordCount) }).ToArray(),
            WorldPainterRuntimeTypes = Enumerable.Range(0, 14).Select(typeId => new
            {
                TypeId = typeId,
                TypeName = Ssx3StructuredTableDecoder.WorldPainterTypeName(typeId),
                RuntimeClass = Ssx3StructuredTableDecoder.WorldPainterRuntimeClassName(typeId),
                SerializedRecordSize = Ssx3StructuredTableDecoder.WorldPainterRecordSize(typeId),
                RuntimeObjectSize = Ssx3StructuredTableDecoder.WorldPainterRuntimeObjectSize(typeId),
                PropertyNames = WorldPainterPropertyNames(typeId)
            }).ToArray(),
            WorldModifierRecordSizes = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections)
                .SelectMany(section => section.Records.Select(record => new { section.TypeId, section.TypeName, Size = record.Data.Length }))
                .GroupBy(x => new { x.TypeId, x.TypeName, x.Size }).OrderBy(x => x.Key.TypeId)
                .Select(x => new { x.Key.TypeId, x.Key.TypeName, x.Key.Size, Count = x.Count() }).ToArray(),
            WorldModifierSpatialIndexHeaders = parsed.Scene.StructuredTables.SelectMany(table => table.ModifierSections.Select(section => new
                {
                    table.TrackId, table.ResourceId, section.TypeId, section.TypeName, section.SpatialIndex.Scale,
                    section.SpatialIndex.Origin, section.SpatialIndex.Extent, section.SpatialIndex.EntryCount,
                    section.SpatialIndex.SerializedCapacity, section.SpatialIndex.RootHandle, section.SpatialIndex.Reserved,
                    section.SpatialIndex.DefaultLeafWord0, section.SpatialIndex.DefaultLeafWord1,
                    section.SpatialIndex.SerializedNodePointerPlaceholder,
                    section.SpatialIndex.SerializedNodeEndPointerPlaceholder, section.SpatialIndex.RootEntryIndex
                })).ToArray(),
            WorldModifierReferenceDetails = parsed.Scene.StructuredTables.SelectMany(table => table.ModifierSections.SelectMany(section =>
                section.Records.Where(record => record.ReferencedResourceType is not null).Select(record => new
                {
                    TableTrackId = table.TrackId, section.TypeId, section.TypeName, RecordIndex = record.Index,
                    record.ReferencedResourceType, record.ReferencedTrackId, record.ReferencedResourceId,
                    Resolved = ResolvesWorldModifierReference(record),
                    TargetName = record.ReferencedResourceType switch
                    {
                        8 => parsed.Scene.Splines.FirstOrDefault(spline => Convert.ToInt32(spline.Properties["TrackId"]) == record.ReferencedTrackId
                            && Convert.ToInt32(spline.Properties["ResourceId"]) == record.ReferencedResourceId)?.Name,
                        3 => parsed.Scene.Props.FirstOrDefault(prop => Convert.ToInt32(prop.Properties["TrackId"]) == record.ReferencedTrackId
                            && Convert.ToInt32(prop.Properties["ResourceId"]) == record.ReferencedResourceId)?.Name,
                        _ => null
                    }
                }))).ToArray(),
            LightingTags = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierSections)
                .Where(section => section.TypeId == 11).SelectMany(section => section.Records).SelectMany(record => record.Tags)
                .Where(tag => tag.Length > 0).GroupBy(tag => tag).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
                .Select(x => new { Tag = x.Key, Count = x.Count() }).ToArray(),
            RailRootReferences = parsed.Scene.StructuredTables.Sum(x => x.RootRailReferences.Count),
            RailReferenceSets = parsed.Scene.StructuredTables.Sum(x => x.RailReferenceSets.Count),
            RailReferences = parsed.Scene.StructuredTables.Sum(x => x.RootRailReferences.Count
                + x.RailReferenceSets.Sum(set => set.Slots.Count(reference => reference is not null))),
            ModifierProgramBlocks = parsed.Scene.StructuredTables.Sum(x => x.ModifierProgramBlocks.Count),
            ModifierProgramGroups = parsed.Scene.StructuredTables.Sum(x => x.ModifierProgramGroups.Count),
            ModifierProgramReferences = parsed.Scene.StructuredTables.Sum(x =>
                x.ModifierProgramBlocks.Sum(block => block.ModifierSlots.Count(reference => reference is not null))
                + x.ModifierProgramGroups.Sum(group => group.ProgramReferences.Count(reference => reference is not null)
                    + group.ModifierSlots.Count(reference => reference is not null))),
            ModifierProgramFamilies = parsed.Scene.StructuredTables.SelectMany(x => x.ModifierProgramBlocks)
                .SelectMany(block => block.ModifierSlots.Select((reference, slot) => new { reference, TypeId = slot + 1 }))
                .Concat(parsed.Scene.StructuredTables.SelectMany(x => x.ModifierProgramGroups)
                    .SelectMany(group => group.ModifierSlots.Select((reference, slot) => new { reference, TypeId = slot + 1 })))
                .Where(item => item.reference is not null).GroupBy(item => item.TypeId).OrderBy(group => group.Key)
                .Select(group => new { TypeId = group.Key, TypeName = Ssx3StructuredTableDecoder.ModifierTypeName(group.Key), Count = group.Count() }).ToArray(),
            LunPrograms = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Count),
            LunProgramBytes = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Program.Length)),
            LunBytecodeBytes = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.BytecodeLength)),
            LunRoutines = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Routines.Count)),
            LunRoutineDescriptors = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => 1 + program.AdditionalDescriptors.Count)),
            LunInstructions = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Instructions.Count)),
            LunInstructionBytes = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Instructions.Sum(instruction => instruction.SerializedSize))),
            LunOpcodes = parsed.Scene.StructuredTables.SelectMany(x => x.LunPrograms).SelectMany(program => program.Instructions)
                .GroupBy(instruction => instruction.Opcode).OrderBy(group => group.Key)
                .Select(group => new { Opcode = $"0x{group.Key:X2}", Operation = Ssx3StructuredTableDecoder.LunInstructionOperation(group.Key).ToString(),
                    Size = Ssx3StructuredTableDecoder.LunInstructionSize(group.Key), Count = group.Count() }).ToArray(),
            LunNativeCalls = parsed.Scene.StructuredTables.SelectMany(x => x.LunPrograms).SelectMany(program => program.Instructions)
                .Where(instruction => instruction.Operation == LunOperation.CallNative)
                .GroupBy(instruction => new { instruction.NativeFunctionId, instruction.NativeFunctionName, instruction.NativeFunctionSubsystem })
                .OrderBy(group => group.Key.NativeFunctionId)
                .Select(group => new { group.Key.NativeFunctionId, group.Key.NativeFunctionName, group.Key.NativeFunctionSubsystem, Count = group.Count(),
                    ArgumentCounts = group.GroupBy(instruction => instruction.ArgumentCount).OrderBy(values => values.Key)
                        .Select(values => new { Count = values.Key, Occurrences = values.Count() }).ToArray() }).ToArray(),
            LunPaddingBytes = parsed.Scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.PaddingBytes)),
            RailProgramRecords = parsed.Scene.StructuredTables.Sum(x => x.RailProgramRecords.Count),
            RailProgramDescriptors = parsed.Scene.StructuredTables.Sum(x => x.RailProgramRecords.Sum(record => record.Descriptors.Count)),
            RailProgramKinds = parsed.Scene.StructuredTables.SelectMany(x => x.RailProgramRecords)
                .GroupBy(record => record.Kind).OrderBy(group => group.Key)
                .Select(group => new
                {
                    Kind = group.Key, Records = group.Count(), Inputs = group.Sum(record => record.InputRailCount),
                    Descriptors = group.Sum(record => record.OutputDescriptors.Count),
                    OutputRoles = group.SelectMany(record => record.OutputDescriptors).GroupBy(descriptor => descriptor.Role)
                        .OrderBy(values => values.Key).Select(values => new { Role = values.Key, Count = values.Count() }).ToArray(),
                    ControlHighValues = group.GroupBy(record => record.ControlHigh).OrderBy(values => values.Key)
                        .Select(values => new { Value = $"0x{values.Key:X4}", Count = values.Count() }).ToArray()
                }).ToArray(),
            RailProgramRecordReferences = parsed.Scene.StructuredTables.Sum(x => x.RailProgramReferenceIndices.Count),
            RailSplineMetadataEntries = parsed.Scene.StructuredTables.Sum(x => x.RailSplineMetadataEntries.Count),
            RailSplineRoles = parsed.Scene.StructuredTables.SelectMany(x => x.RailSplineMetadataEntries)
                .GroupBy(entry => entry.Role).OrderBy(group => group.Key)
                .Select(group => new { Role = group.Key, Value = (ushort)group.Key, Count = group.Count() }).ToArray(),
            RailSplineSurfaces = parsed.Scene.StructuredTables.SelectMany(x => x.RailSplineMetadataEntries)
                .GroupBy(entry => entry.Surface).OrderBy(group => group.Key)
                .Select(group => new { Surface = group.Key, Value = (ushort)group.Key, Count = group.Count() }).ToArray(),
            RailProgramRailReferences = parsed.Scene.StructuredTables.Sum(x => x.RailProgramRecords.Sum(record =>
                (record.PrimaryRailReference is null ? 0 : 1) + (record.SecondaryRailReference is null ? 0 : 1))),
            RailProgramRailReferencesMatchingSplineIds = parsed.Scene.StructuredTables.SelectMany(x => x.RailProgramRecords)
                .SelectMany(record => new[] { record.PrimaryRailReference, record.SecondaryRailReference })
                .Where(reference => reference is not null).Select(reference => reference!)
                .Count(reference => parsed.Scene.Splines.Any(spline => Convert.ToInt32(spline.Properties["TrackId"]) == reference.TrackId
                    && Convert.ToInt32(spline.Properties["ResourceId"]) == reference.RailId)),
            RailProgramRailReferencesMatchingGeneratedIds = parsed.Scene.StructuredTables.SelectMany(x => x.RailProgramRecords)
                .SelectMany(record => new[] { record.PrimaryRailReference, record.SecondaryRailReference })
                .Where(reference => reference is not null).Select(reference => reference!)
                .Count(reference => parsed.Scene.StructuredTables.SelectMany(table => table.RailProgramRecords)
                    .Any(record => record.GeneratedRailReference == reference)),
            RailSplineMetadataDetails = parsed.Scene.StructuredTables.SelectMany(table => table.RailSplineMetadataEntries
                .Select((entry, splineId) => new
                {
                    table.TrackId, SplineId = splineId, entry.PackedValue, entry.Low, entry.High, entry.Role, entry.Surface,
                    SplineName = parsed.Scene.Splines.FirstOrDefault(spline => Convert.ToInt32(spline.Properties["TrackId"]) == table.TrackId
                        && Convert.ToInt32(spline.Properties["ResourceId"]) == splineId)?.Name
                })).ToArray(),
            RailReferencesMatchingSplineIds = parsed.Scene.StructuredTables.SelectMany(x => x.RootRailReferences
                    .Concat(x.RailReferenceSets.SelectMany(set => set.Slots).Where(reference => reference is not null).Select(reference => reference!)))
                .Count(reference => parsed.Scene.Splines.Any(spline => Convert.ToInt32(spline.Properties["TrackId"]) == reference.TrackId
                    && Convert.ToInt32(spline.Properties["ResourceId"]) == reference.RailId)),
            RailRootReferenceDetails = parsed.Scene.StructuredTables.SelectMany(table => table.RootRailReferences.Select(reference => new
                {
                    TableTrackId = table.TrackId, reference.PackedValue, reference.TrackId, reference.RailId,
                    SplineName = parsed.Scene.Splines.FirstOrDefault(spline => Convert.ToInt32(spline.Properties["TrackId"]) == reference.TrackId
                        && Convert.ToInt32(spline.Properties["ResourceId"]) == reference.RailId)?.Name
                })).ToArray(),
            RailSetReferenceDetails = parsed.Scene.StructuredTables.SelectMany(table => table.RailReferenceSets.SelectMany(set => set.Slots)
                .Where(reference => reference is not null).Select(reference => reference!).DistinctBy(reference => reference.PackedValue).Select(reference => new
                {
                    TableTrackId = table.TrackId, reference.PackedValue, reference.TrackId, reference.RailId,
                    SplineName = parsed.Scene.Splines.FirstOrDefault(spline => Convert.ToInt32(spline.Properties["TrackId"]) == reference.TrackId
                        && Convert.ToInt32(spline.Properties["ResourceId"]) == reference.RailId)?.Name
                })).ToArray(),
            BnklBanks = parsed.Scene.AudioBanks.Count,
            BnklEntries = parsed.Scene.AudioBanks.Sum(x => x.EntryCount),
            BnklPopulatedSlots = parsed.Scene.AudioBanks.Sum(x => x.Sounds.Count),
            BnklBankDetails = parsed.Scene.AudioBanks.Select(bank => new
            {
                bank.TrackId,
                bank.ResourceId,
                bank.Version,
                bank.EntryCount,
                PopulatedSlots = bank.Sounds.Select(sound => sound.Slot).ToArray(),
                PayloadBytes = bank.Source.SourceLength,
                BodyBytes = bank.Body.Length
            }).ToArray(),
            BnklInfoSections = parsed.Scene.AudioBanks.Sum(x => x.Sounds.Sum(sound => sound.InfoSections.Count)),
            BnklLoopedInfoSections = parsed.Scene.AudioBanks.Sum(x => x.Sounds.Sum(sound => sound.InfoSections.Count(section => section.LoopStart is not null))),
            BnklLayeredSounds = parsed.Scene.AudioBanks.Sum(x => x.Sounds.Count(sound => sound.InfoSections.Count > 1)),
            BnklMaximumLayersPerSound = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).Select(x => x.InfoSections.Count).DefaultIfEmpty().Max(),
            BnklCodecs = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                .GroupBy(x => x.Codec).OrderBy(x => x.Key).Select(x => new { Codec = x.Key, Count = x.Count() }).ToArray(),
            BnklSampleRates = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                .GroupBy(x => x.SampleRate).OrderBy(x => x.Key).Select(x => new { SampleRate = x.Key, Count = x.Count() }).ToArray(),
            BnklRootMidiNotes = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                .GroupBy(x => x.RootMidiNote).OrderBy(x => x.Key).Select(x => new { RootMidiNote = x.Key, Count = x.Count() }).ToArray(),
            BnklPlaybackEnvelopeSections = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                .Count(x => x.PlaybackEnvelopeOffset is not null),
            BnklPlaybackEnvelopeSegments = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                .Sum(x => x.PlaybackEnvelopeSegments.Count),
            BnklPlaybackEnvelopes = parsed.Scene.AudioBanks.SelectMany(bank => bank.Sounds.SelectMany(sound =>
                sound.InfoSections.Select((section, sectionIndex) => new
                {
                    bank.TrackId, bank.ResourceId, sound.Slot, SectionIndex = sectionIndex,
                    section.ReleaseEnvelopeSegmentIndex, section.InitialEnvelopeVolume,
                    Segments = section.PlaybackEnvelopeSegments.Select(segment => new
                    {
                        segment.DurationHundredths, segment.DurationSeconds, segment.Volume,
                        segment.RuntimeDurationHundredths, segment.RuntimeDurationSeconds,
                        segment.RuntimeTargetVolumeFixed16
                    }).ToArray()
                }))).Where(x => x.Segments.Length > 0).ToArray(),
            BnklPatchOpcodes = parsed.Scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections).SelectMany(x => x.Patches)
                .GroupBy(x => x.Opcode).OrderBy(x => x.Key).Select(x => new { Opcode = $"0x{x.Key:X2}", Name = Ssx3BnklBankDecoder.PatchName(x.Key), Count = x.Count() }).ToArray(),
            BnklBodyBytes = parsed.Scene.AudioBanks.Sum(x => x.Body.Length),
            AvalancheAnimations = parsed.Scene.AvalancheAnimations.Count,
            AvalancheBlocks = parsed.Scene.AvalancheAnimations.Sum(x => x.Blocks.Count),
            AvalancheFrames = parsed.Scene.AvalancheAnimations.Sum(x => x.Blocks.Sum(block => block.Frames.Count)),
            AvalancheMetadataSegments = parsed.Scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Count),
            AvalancheMetadataPairs = parsed.Scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Pairs.Count)),
            AvalancheMetadataParameters = parsed.Scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Parameters.Count)),
            AvalancheObjectReferences = parsed.Scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Parameters.Count)),
            ResolvedAvalancheObjectReferences = parsed.Scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments)
                .SelectMany(x => x.Parameters).Count(x => propIds.Contains(Key(x.ObjectTrackId, x.ObjectResourceId))),
            AvalancheCapturedTargetIdentities = parsed.Scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Parameters.Count)),
            AvalancheCapturedTargetIdentitiesCoincidingWithType3 = parsed.Scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments)
                .SelectMany(x => x.Parameters).Count(x => propIds.Contains(Key(x.ObjectTrackId, x.ObjectResourceId))),
            AvalancheRuntimeLoadDiscardsCapturedTargetIdentity = true,
            AvalancheRuntimeFramesPerSecond = Ssx3AvalancheDecoder.RuntimeFramesPerSecond,
            AvalancheRuntimeTranslationEquation = Ssx3AvalancheDecoder.RuntimeTranslationEquation,
            AvalancheRuntimeScaleEquation = Ssx3AvalancheDecoder.RuntimeScaleEquation,
            AvalancheRuntimeRotationEquation = Ssx3AvalancheDecoder.RuntimeRotationEquation,
            AvalancheRuntimeScheduleEquation = Ssx3AvalancheDecoder.RuntimeScheduleEquation,
            AvalancheReferencedProps = parsed.Scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments).SelectMany(x => x.Parameters)
                .Select(x => parsed.Scene.Props.FirstOrDefault(prop => Convert.ToInt32(prop.Properties["TrackId"]) == x.ObjectTrackId
                    && Convert.ToInt32(prop.Properties["ResourceId"]) == x.ObjectResourceId)?.Name)
                .Where(x => x is not null).GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
                .Select(x => new { Name = x.Key, Count = x.Count() }).ToArray(),
            AvalancheMetadataShapes = parsed.Scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments)
                .GroupBy(x => new { x.ParameterCount, x.PairCount }).OrderBy(x => x.Key.ParameterCount).ThenBy(x => x.Key.PairCount)
                .Select(x => new { x.Key.ParameterCount, x.Key.PairCount, Count = x.Count() }).ToArray(),
            ParticleModels = parsed.Scene.ParticleModels.Count,
            ParticleElements = parsed.Scene.ParticleModels.Sum(x => x.Elements.Count),
            ParticleRuntimeTexture = new { ParticleModelAsset.RuntimeTextureArchive, ParticleModelAsset.RuntimeTextureName,
                ParticleModelAsset.RuntimeTextureAssetId, ParticleModelAsset.RuntimeTextureEnumIndex },
            ParticleRuntimeBlend = new { ParticleModelAsset.RuntimeBlendSelector,
                GsAlphaRegister = $"0x{ParticleModelAsset.RuntimeGsAlphaRegister:X2}", ParticleModelAsset.RuntimeBlendEquation },
            ParticleEmitters = parsed.Scene.ParticleEmitters.Count,
            Lights = parsed.Scene.Lights.Count, LightPlaceholders = parsed.Scene.Lights.Count(x => x.IsPlaceholder),
            LightsByKind = parsed.Scene.Lights.GroupBy(x => x.Kind).OrderBy(x => x.Key)
                .Select(x => new { Kind = x.Key, Name = Ssx3EffectDecoder.LightKindName(x.Key), Count = x.Count() }).ToArray(),
            Halos = parsed.Scene.Halos.Count,
            NisReferenceTables = parsed.Scene.NisReferenceTables.Count,
            NisReferences = parsed.Scene.NisReferenceTables.Sum(x => x.Slots.Count(slot => slot.IsPopulated)),
            NisObservedRoleBindings = parsed.Scene.NisReferenceTables.Sum(x => x.Slots.Count(slot => slot.IsPopulated && slot.ObservedRole is not null)),
            NisRuntimeAddressableSlots = parsed.Scene.NisReferenceTables.Sum(x => x.Slots.Count(slot => slot.IsRuntimeAddressable)),
            NisMissingSlots = parsed.Scene.NisReferenceTables.Sum(x => x.Slots.Count(slot => !slot.IsPopulated)),
            NisReferenceDetails = parsed.Scene.NisReferenceTables.Select(table => new { table.Name, table.TrackId, table.ResourceId,
                RuntimeConsumer = "cSSXScriptEngine object-transform lookup",
                MissingSlotBehavior = "neutral initialized outputs",
                Slots = table.Slots.Select(slot => new { Slot = slot.Index,
                    Reference = slot.ObjectReference is null ? null : $"{slot.ObjectReference.TrackId}:{slot.ObjectReference.ResourceId}",
                    Role = slot.ObservedRole, slot.RuntimeCommandIds,
                    TargetResourceType = slot.ObjectReference?.TargetResourceType,
                    InstanceName = slot.ObjectReference is null ? null : parsed.Scene.Props.FirstOrDefault(prop =>
                        Convert.ToInt32(prop.Properties["TrackId"]) == slot.ObjectReference.TrackId
                        && Convert.ToInt32(prop.Properties["ResourceId"]) == slot.ObjectReference.ResourceId)?.Name }).ToArray() }).ToArray(),
            NavigationMarkers = parsed.Scene.NavigationMarkers.Count,
            CameraTriggerTables = parsed.Scene.CameraTriggerTables.Count,
            Triggers = parsed.Scene.Triggers.Count,
            CameraTriggerRecordSizes = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records).GroupBy(x => x.SerializedSize)
                .OrderBy(x => x.Key).Select(x => new { Size = x.Key, Count = x.Count() }).ToArray(),
            CameraTriggerVolumeKinds = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records).GroupBy(x => x.Shape.Kind)
                .OrderBy(x => x.Key).Select(x => new { Kind = x.Key, Name = Ssx3CameraTriggerDecoder.VolumeKindName(x.Key), Count = x.Count() }).ToArray(),
            CameraTriggerFlags = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records).GroupBy(x => x.Flags)
                .OrderBy(x => x.Key).Select(x => new { Flags = x.Key, Names = Ssx3CameraTriggerDecoder.TriggerFlagNames(x.Key), Count = x.Count() }).ToArray(),
            CameraTriggerActionKinds = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records)
                .SelectMany(x => new[] { x.Action0, x.Action1 }).GroupBy(x => x.Kind).OrderBy(x => x.Key)
                .Select(x => new { Kind = x.Key, Name = Ssx3CameraTriggerDecoder.ActionKindName(x.Key), Count = x.Count() }).ToArray(),
            CameraTriggerActionRuntimeDispatch = $"0x{CameraTriggerAction.RuntimeDispatchFunction:X8}",
            CameraTriggerSwitchMappings = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records)
                .SelectMany(x => new[] { x.Action0, x.Action1 }).Where(x => x.Kind == 0)
                .GroupBy(x => x.Value).OrderBy(x => x.Key)
                .Select(x => new { SwitchCode = x.Key, RuntimeCameraAlgorithmId = x.First().RuntimeCameraAlgorithmId, Count = x.Count() }).ToArray(),
            CameraTriggerBoundKinds = parsed.Scene.CameraTriggerTables.SelectMany(x => x.Records)
                .SelectMany(x => new[] { x.Action0.BoundObject, x.Action1.BoundObject }).Where(x => x is not null)
                .GroupBy(x => x!.Kind).OrderBy(x => x.Key)
                .Select(x => new { Kind = x.Key, Name = Ssx3CameraTriggerDecoder.BoundKindName(x.Key), Count = x.Count() }).ToArray(),
            VisibilityCurtains = parsed.Scene.VisibilityCurtains.Count,
            VisibilityCurtainDetails = parsed.Scene.VisibilityCurtains.Select(x => new
            {
                x.Name, TrackId = x.Properties["TrackId"], ResourceId = x.Properties["ResourceId"],
                CornerCount = x.CornersSsx.Count, SerializedSize = Convert.ToInt32(x.Properties["PayloadSize"]),
                x.BoundingSphereSsx, x.PlaneSsx, x.Bounds,
                RuntimeCandidateMetric = x.Properties["RuntimeCandidateMetric"],
                RuntimeCandidateSort = x.Properties["RuntimeCandidateSort"],
                FourthCornerPlaneResidual = MathF.Abs(Vector3.Dot(new Vector3(x.PlaneSsx.X, x.PlaneSsx.Y, x.PlaneSsx.Z), x.CornersSsx[3]) + x.PlaneSsx.W)
            }).ToArray(),
            TerrainNormalAbsAverage = terrainNormals.Length == 0 ? null : new { X = terrainNormals.Average(x => Math.Abs(x.X)), Y = terrainNormals.Average(x => Math.Abs(x.Y)), Z = terrainNormals.Average(x => Math.Abs(x.Z)) },
            TerrainLightmapSamples = parsed.Scene.Terrain.Take(24).Select(x => new { x.TrackId, x.TextureResourceId,
                x.SecondaryTextureResourceId, x.LightmapResourceId, Surface = x.Surface.ToString(),
                PatchFlags = $"0x{x.PatchFlags:X4}", TextureStateFlags = $"0x{x.TextureStateFlags:X4}", RenderFlags = $"0x{x.RenderFlags:X4}",
                x.TextureSubChunkId, x.BoundingSphereSsx, x.BoundsMinimumSsx, x.BoundsMaximumSsx,
                LightmapVector = x.Properties["LightmapVector"], DiffuseUvs = x.Properties["UVs"] }).ToArray(),
            TerrainTextureUsage = parsed.Scene.Terrain.GroupBy(x => x.TextureResourceId).OrderBy(x => x.Key)
                .Select(x => new { TextureResourceId = x.Key, Count = x.Count() }).ToArray(),
            RampTerrainDetails = parsed.Scene.Terrain.Select((x, index) => (Patch: x, Index: index))
                .Where(x => x.Patch.TextureResourceId is 109 or 112 or 114 or 235 or 238 or 241 or 378 or 383 or 384)
                .Select(x => new { x.Index, x.Patch.Name, x.Patch.TrackId, x.Patch.TextureResourceId,
                    GroupIndex = x.Patch.Properties["GroupIndex"],
                    Minimum = new { X = x.Patch.Mesh.Positions.Min(v => v.X), Y = x.Patch.Mesh.Positions.Min(v => v.Y), Z = x.Patch.Mesh.Positions.Min(v => v.Z) },
                    Maximum = new { X = x.Patch.Mesh.Positions.Max(v => v.X), Y = x.Patch.Mesh.Positions.Max(v => v.Y), Z = x.Patch.Mesh.Positions.Max(v => v.Z) },
                    Center = new { X = x.Patch.Mesh.Positions.Average(v => v.X), Y = x.Patch.Mesh.Positions.Average(v => v.Y), Z = x.Patch.Mesh.Positions.Average(v => v.Z) },
                    Uvs = x.Patch.Properties["UVs"], CornerPoints = x.Patch.Properties["CornerPoints"],
                    VertexSample = x.Patch.Mesh.Positions.Select((position, vertex) => new { Position = position,
                        Uv = x.Patch.Mesh.TextureCoordinates[vertex] }).Take(24).ToArray() }).ToArray(),
            SplineDetails = parsed.Scene.Splines.Select(x => new { x.Name, TrackId = x.Properties["TrackId"], ResourceId = x.Properties["ResourceId"],
                PointCount = x.Points.Count, SegmentCount = x.Segments.Count, x.TotalLength, x.SerializedSegmentPointerToken,
                GlobalSegmentStartIndex = x.Properties["GlobalSegmentStartIndex"],
                Start = x.Points.FirstOrDefault()?.Position, End = x.Points.LastOrDefault()?.Position }).ToArray(),
            NavigationProps = parsed.Scene.Props.Where(x => x.Name.Contains("start", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("finish", StringComparison.OrdinalIgnoreCase))
                .Select(x => new { x.Name, Position = x.Properties["Position"], AxisX = new { x.Transform.M11, x.Transform.M12, x.Transform.M13 },
                    AxisY = new { x.Transform.M21, x.Transform.M22, x.Transform.M23 }, AxisZ = new { x.Transform.M31, x.Transform.M32, x.Transform.M33 },
                    x.ModelTrackId, x.ModelResourceId }).ToArray(),
            UnresolvedModelMaterialKeys = modelMaterialIds.Where(x => !materialTextures.TryGetValue(x, out var texture) || !textureIds.Contains(texture)).Order().ToArray(),
            UnresolvedModelKeys = referencedModelIds.Where(x => !decodedModelIds.Contains(x)).Order().ToArray(),
            MaterialDetails = parsed.Scene.Materials.Select(x => new { x.TrackId, x.ResourceId, x.TextureResourceId,
                TextureStateWord02 = $"0x{x.TextureStateWord02:X4}", PacketAddressAdjustment = $"0x{x.PacketAddressAdjustment:X8}",
                x.HasNondefaultTextureStateWord02, x.AddsPrimaryTextureStateBit, x.SerializedRuntimeScratch,
                StateWord0 = $"0x{x.StateWord0:X4}", StateWord1 = $"0x{x.StateWord1:X4}",
                SerializedTextureFrameTableToken = $"0x{x.SerializedTextureFrameTableToken:X8}", x.TextureFrameResourceIds, x.HasTextureFrameTable,
                GroupIndex = x.Properties["GroupIndex"], SourceSection = x.Source.SectionName,
                ResolvedTextures = textureResolver.Resolve(x).Select(t => new { t.TrackId, t.ResourceId, GroupIndex = t.Properties["GroupIndex"] }).ToArray(),
                PayloadSize = x.Properties["PayloadSize"] }).ToArray(),
            TextureDetails = parsed.Scene.Textures.Select(x => new { x.TrackId, x.ResourceId, Usage = x.Usage.ToString(), x.Width, x.Height,
                GroupIndex = x.Properties["GroupIndex"], Format = x.Properties["Format"], HeaderHex = x.Properties["HeaderHex"] }).ToArray(),
            PropCategories = parsed.Scene.Props.GroupBy(x => x.Classification.Category).OrderBy(x => x.Key)
                .Select(x => new { Category = x.Key.ToString(), Count = x.Count(), Reason = x.First().Classification.Reason }).ToArray(),
            ModelDetails = parsed.Scene.Models.Select(x => new { x.Name, TrackId = Convert.ToInt32(x.Properties["TrackId"]), ResourceId = Convert.ToInt32(x.Properties["ResourceId"]), GroupIndex = x.Properties["GroupIndex"], SourceSection = x.Source.SectionName,
                ResolvedTextures = textureResolver.Resolve(x).Select(t => new { t.TrackId, t.ResourceId, GroupIndex = t.Properties["GroupIndex"] }).ToArray(),
                Vertices = x.Mesh?.Positions.Count ?? 0, Triangles = (x.Mesh?.Indices.Count ?? 0) / 3,
                ObjectCount = x.Properties["ObjectCount"], HeaderMaterials = x.Properties["MaterialResourceIds"], DecodedParts = x.Properties["DecodedParts"],
                MaterialTableOffset = x.Properties["MaterialTableOffset"], ObjectTableOffset = x.Properties["ObjectTableOffset"],
                ModelFlags = x.Properties["ModelFlags"], AnimationDurationSeconds = x.Properties["AnimationDurationSeconds"],
                AnimationDurationTicks120Hz = x.Properties["AnimationDurationTicks120Hz"],
                SpecialPacketHeaders = x.Properties["SpecialPacketHeaders"],
                SpecialPacketPreviews = x.Properties["SpecialPacketPreviews"], ModelDataOffset = x.Properties["ModelDataOffset"], PayloadSize = x.Properties["PayloadSize"],
                Materials = x.Submeshes.Select(s => $"{s.MaterialTrackId}:{s.MaterialResourceId}").Distinct().ToArray(),
                SubmeshDetails = x.Submeshes.Select(s => new { Material = $"{s.MaterialTrackId}:{s.MaterialResourceId}",
                    PositionMinimum = s.Mesh.Positions.Count == 0 ? null : new { X = s.Mesh.Positions.Min(v => v.X), Y = s.Mesh.Positions.Min(v => v.Y), Z = s.Mesh.Positions.Min(v => v.Z) },
                    PositionMaximum = s.Mesh.Positions.Count == 0 ? null : new { X = s.Mesh.Positions.Max(v => v.X), Y = s.Mesh.Positions.Max(v => v.Y), Z = s.Mesh.Positions.Max(v => v.Z) },
                    AverageNormal = s.Mesh.Normals.Count == 0 ? null : new { X = s.Mesh.Normals.Average(v => v.X), Y = s.Mesh.Normals.Average(v => v.Y), Z = s.Mesh.Normals.Average(v => v.Z) },
                    UvMinimum = s.Mesh.TextureCoordinates.Count == 0 ? null : new { X = s.Mesh.TextureCoordinates.Min(v => v.X), Y = s.Mesh.TextureCoordinates.Min(v => v.Y) },
                    UvMaximum = s.Mesh.TextureCoordinates.Count == 0 ? null : new { X = s.Mesh.TextureCoordinates.Max(v => v.X), Y = s.Mesh.TextureCoordinates.Max(v => v.Y) },
                    UvSample = s.Mesh.TextureCoordinates.Take(12).ToArray(),
                    VertexSample = s.Mesh.Positions.Select((position, index) => new { Position = position,
                        Normal = index < s.Mesh.Normals.Count ? s.Mesh.Normals[index] : default,
                        Uv = index < s.Mesh.TextureCoordinates.Count ? s.Mesh.TextureCoordinates[index] : default }).Take(24).ToArray(),
                    TopVertexSample = s.Mesh.Positions.Select((position, index) => new { Position = position,
                        Normal = index < s.Mesh.Normals.Count ? s.Mesh.Normals[index] : default,
                        Uv = index < s.Mesh.TextureCoordinates.Count ? s.Mesh.TextureCoordinates[index] : default })
                        .Where(v => v.Normal.Y > 0.45f).Take(48).ToArray() }).ToArray(),
                Instances = parsed.Scene.Props.Count(p => Key(p.ModelTrackId, p.ModelResourceId) == Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))),
                InstanceIndices = parsed.Scene.Props.Select((p, i) => (p, i)).Where(v => Key(v.p.ModelTrackId, v.p.ModelResourceId) == Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).Select(v => v.i).ToArray(),
                InstanceNames = parsed.Scene.Props.Where(p => Key(p.ModelTrackId, p.ModelResourceId) == Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).Select(p => p.Name).ToArray() }).ToArray(),
            UnsupportedResources = parsed.Scene.UnknownSections.Count,
            UnsupportedByType = parsed.Scene.UnknownSections.GroupBy(x => x.ResourceType).OrderBy(x => x.Key)
                .Select(x => new { Type = x.Key, Count = x.Count(), Sizes = x.GroupBy(v => Convert.ToInt32(v.Properties["PayloadSize"])).OrderBy(v => v.Key).Select(v => new { Size = v.Key, Count = v.Count() }).ToArray() }).ToArray(),
            UnsupportedSamples = parsed.Scene.UnknownSections.GroupBy(x => x.ResourceType).OrderBy(x => x.Key)
                .Select(x => new { Type = x.Key, Samples = x.Take(8).Select(v => new { v.TrackId, v.ResourceId, Size = v.Properties["PayloadSize"], Preview = v.Properties["PreviewHex"] }).ToArray() }).ToArray(),
            Bounds = parsed.Scene.Bounds, Groups = parsed.Groups, Diagnostics = parsed.Diagnostics.Items }, DiagnosticBag.JsonOptions);
        if (output is null) Console.WriteLine(report); else { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, report); Console.WriteLine($"Wrote {output}"); }
        return parsed.Diagnostics.HasErrors ? 2 : 0;
    }
    private static int Audit(string[] args)
    {
        var project = ProjectService.Open(args[1]); var diagnostics = new DiagnosticBag();
        var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics);
        var ssbPath = ProjectService.WorldFile(project, ".ssb");
        static long Key(int track, int resource) => ((long)track << 32) | (uint)resource;
        static double MatrixSymmetryError(IReadOnlyList<float> matrix) => Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 3).Select(column =>
                Math.Abs((double)matrix[row * 3 + column] - matrix[column * 3 + row]))).Max();
        static bool IsPositiveDefinite3x3(IReadOnlyList<float> matrix)
        {
            var firstMinor = (double)matrix[0];
            var secondMinor = matrix[0] * (double)matrix[4] - matrix[1] * (double)matrix[3];
            var determinant = matrix[0] * (matrix[4] * (double)matrix[8] - matrix[5] * (double)matrix[7])
                - matrix[1] * (matrix[3] * (double)matrix[8] - matrix[5] * (double)matrix[6])
                + matrix[2] * (matrix[3] * (double)matrix[7] - matrix[4] * (double)matrix[6]);
            return firstMinor > 0 && secondMinor > 0 && determinant > 0;
        }
        static bool HasValidAvalancheRuntimeSamples(AvalancheDataBlock block)
        {
            if (block.Frames.Count == 0) return false;
            var expectedPosition = block.SerializedOriginSsx;
            foreach (var frame in block.Frames)
            {
                expectedPosition += frame.SerializedTranslationDeltaSsx;
                if (frame.SerializedPositionSsx != expectedPosition) return false;
            }
            foreach (var time in new[] { 0f, block.RuntimeDurationSeconds * 0.5f, block.RuntimeDurationSeconds })
            {
                var sample = Ssx3AvalancheDecoder.EvaluateRuntimeTransform(block, time);
                if (!float.IsFinite(sample.Position.X + sample.Position.Y + sample.Position.Z
                    + sample.Scale.X + sample.Scale.Y + sample.Scale.Z
                    + sample.Rotation.X + sample.Rotation.Y + sample.Rotation.Z + sample.Rotation.W)
                    || MathF.Abs(sample.Rotation.LengthSquared() - 1f) > 0.001f) return false;
            }
            return true;
        }
        var courses = new List<object>(); var failed = false;
        var uniqueTerrainByResource = new Dictionary<long, TerrainPatch>();
        var uniqueLightmapsByResource = new Dictionary<long, TextureAsset>();
        foreach (var course in Ssx3CourseCatalog.Courses)
        {
            var parsed = Ssx3SsbReader.ParseCourse(ssbPath, sdb, course);
            foreach (var item in diagnostics.Items) parsed.Diagnostics.Add(item);
            var scene = parsed.Scene; var resolver = new SceneTextureResolver(scene);
            foreach (var patch in scene.Terrain)
                uniqueTerrainByResource.TryAdd(Key(patch.TrackId, patch.ObjectResourceId), patch);
            foreach (var texture in scene.Textures.Where(texture => texture.IsLightmap))
                uniqueLightmapsByResource.TryAdd(Key(texture.TrackId, texture.ResourceId), texture);
            var decodedModels = scene.Models.Where(x => x.Mesh is not null).Select(x => Key(
                Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).ToHashSet();
            var unresolvedProps = scene.Props.Count(x => !decodedModels.Contains(Key(x.ModelTrackId, x.ModelResourceId)));
            var modelsWithoutTextures = scene.Models.Count(x => resolver.Resolve(x).Count == 0);
            var unresolvedLightmaps = scene.Terrain.Count(x => x.LightmapResourceId >= 0 && !resolver.Resolve(x).Any(t => t.IsLightmap));
            var terrainDiffuseTextureReferences = scene.Terrain.SelectMany(patch => new[]
                { (int)patch.TextureResourceId, patch.SecondaryTextureResourceId }
                .Where(textureId => textureId >= 0).Distinct().Select(textureId => (Patch: patch, TextureId: textureId))).ToArray();
            var resolvedTerrainDiffuseTextureReferences = terrainDiffuseTextureReferences.Count(reference => resolver.Resolve(reference.Patch)
                .Any(texture => texture.ResourceId == reference.TextureId && texture.Usage == TextureUsage.Diffuse));
            var materialTextureReferences = scene.Materials.SelectMany(material => new[] { (int)material.TextureResourceId }
                .Concat(material.TextureFrameResourceIds).Where(textureId => textureId >= 0).Distinct()
                    .Select(textureId => (Material: material, TextureId: textureId))).ToArray();
            var resolvedMaterialTextureReferences = materialTextureReferences.Count(reference => resolver.Resolve(reference.Material)
                .Any(texture => texture.ResourceId == reference.TextureId && texture.Usage == TextureUsage.Diffuse));
            var materialsByKey = scene.Materials.GroupBy(material => Key(material.TrackId, material.ResourceId))
                .ToDictionary(group => group.Key, group => group.Last());
            var materialsByResourceId = scene.Materials.GroupBy(material => material.ResourceId)
                .ToDictionary(group => group.Key, group => group.Last());
            MaterialAsset? ResolveSubmeshMaterial(ModelSubmesh submesh) =>
                materialsByKey.TryGetValue(Key(submesh.MaterialTrackId, submesh.MaterialResourceId), out var exact) ? exact
                    : materialsByResourceId.GetValueOrDefault(submesh.MaterialResourceId);
            var modelTextureFrameCounts = scene.Models.Select(model => model.Submeshes
                    .Select(ResolveSubmeshMaterial).Where(material => material?.HasTextureFrameTable == true)
                    .Select(material => material!.TextureFrameCount).Distinct().Order().ToArray())
                .ToArray();
            var propNames = scene.Props.GroupBy(x => Key(Convert.ToInt32(x.Properties["TrackId"]),
                    Convert.ToInt32(x.Properties["ResourceId"])))
                .ToDictionary(x => x.Key, x => x.First().Name);
            var propIds = propNames.Keys.ToHashSet();
            var splineIds = scene.Splines.Select(x => Key(Convert.ToInt32(x.Properties["TrackId"]),
                Convert.ToInt32(x.Properties["ResourceId"]))).ToHashSet();
            bool ValidSplineTrack(IEnumerable<Spline> sourceSplines)
            {
                var ordered = sourceSplines.OrderBy(x => Convert.ToInt32(x.Properties["ResourceId"])).ToArray();
                if (ordered.Length == 0) return true;
                var expectedToken = ordered[0].SerializedSegmentPointerToken;
                var expectedGlobalSegmentIndex = 0;
                foreach (var spline in ordered)
                {
                    var resourceId = Convert.ToInt32(spline.Properties["ResourceId"]);
                    if (spline.SerializedSegmentPointerToken != expectedToken || spline.Segments.Count == 0
                        || MathF.Abs(spline.TotalLength - Convert.ToSingle(spline.Properties["TotalLength"])) > 0.01f)
                        return false;
                    for (var segmentIndex = 0; segmentIndex < spline.Segments.Count; segmentIndex++)
                    {
                        var segment = spline.Segments[segmentIndex];
                        var expectedPrevious = segmentIndex == 0 ? -1 : expectedGlobalSegmentIndex + segmentIndex - 1;
                        var expectedNext = segmentIndex == spline.Segments.Count - 1 ? -1 : expectedGlobalSegmentIndex + segmentIndex + 1;
                        if (segment.Index != segmentIndex || segment.OwnerSplineResourceId != resourceId
                            || segment.PreviousGlobalSegmentIndex != expectedPrevious || segment.NextGlobalSegmentIndex != expectedNext
                            || segment.SerializedWord4 != Spline.SerializedSegmentWord4 || segment.SerializedWord8 != Spline.SerializedSegmentWord8
                            || segment.TailTag != Spline.SerializedSegmentTailTag || segment.TailFlags != Spline.SerializedSegmentTailFlags)
                            return false;
                    }
                    expectedToken = checked(expectedToken + (uint)spline.Segments.Count);
                    expectedGlobalSegmentIndex += spline.Segments.Count;
                }
                return true;
            }
            bool ValidWorldModifierReference(StructuredTableAsset table, WorldModifierSection section, WorldModifierRecord record)
            {
                var expectedType = section.TypeId switch { 1 => 8, 2 => 3, _ => (int?)null };
                if (expectedType is null)
                    return record.ReferencedResourceType is null && record.ReferencedTrackId is null && record.ReferencedResourceId is null;
                if (record.Words[0] != 0 || record.ReferencedResourceType != expectedType
                    || record.ReferencedTrackId != table.TrackId || record.ReferencedResourceId is not int resourceId)
                    return false;
                var key = Key(table.TrackId, resourceId);
                return expectedType == 8 ? splineIds.Contains(key) : propIds.Contains(key);
            }
            var unresolvedNisReferences = scene.NisReferenceTables.Sum(table => table.Slots.Count(slot => slot.ObjectReference is { } reference
                && !propIds.Contains(Key(reference.TrackId, reference.ResourceId))));
            var errors = parsed.Diagnostics.Items.Count(x => x.Severity == DiagnosticSeverity.Error);
            var issues = new List<string>();
            if (scene.Terrain.Count == 0) issues.Add("no terrain");
            if (scene.Terrain.Any(patch => !patch.HasValidObservedRetailLayout
                    || patch.ObjectResourceId != Convert.ToInt32(patch.Properties["ResourceId"])
                    || Convert.ToInt32(patch.Properties["PayloadSize"]) != TerrainPatch.SerializedSize))
                issues.Add("invalid Type-1 terrain framing, surface, bounds, texture state, or tail fields");
            if (resolvedTerrainDiffuseTextureReferences != terrainDiffuseTextureReferences.Length)
                issues.Add($"{terrainDiffuseTextureReferences.Length - resolvedTerrainDiffuseTextureReferences} unresolved Type-1 diffuse texture references");
            if (scene.Props.Count == 0) issues.Add("no props");
            if (scene.Models.Count == 0) issues.Add("no models");
            if (scene.Materials.Count == 0) issues.Add("no materials");
            if (scene.Materials.Any(material => !material.HasValidObservedRetailLayout
                    || Convert.ToInt32(material.Properties["PayloadSize"]) != material.ExpectedSerializedSize))
                issues.Add("invalid Type-0 material texture slots, state words, relocation token, or texture-frame table");
            if (modelTextureFrameCounts.Any(counts => counts.Length > 1))
                issues.Add("Type-2 model references Type-0 texture-frame tables with inconsistent counts");
            if (resolvedMaterialTextureReferences != materialTextureReferences.Length)
                issues.Add($"{materialTextureReferences.Length - resolvedMaterialTextureReferences} unresolved Type-0 texture references");
            if (scene.Textures.Any(texture => texture.Usage == TextureUsage.Diffuse && texture.RendererDispatchState != 0))
                issues.Add("Type-9 texture uses an unobserved base renderer dispatch state");
            if (scene.Splines.Count == 0) issues.Add("no splines");
            if (scene.Splines.GroupBy(spline => Convert.ToInt32(spline.Properties["TrackId"])).Any(group => !ValidSplineTrack(group)))
                issues.Add("invalid Type-8 spline segment links, tokens, or runtime fields");
            if (scene.Props.Any(prop => prop.RenderDmaProgram is not { Programs.Count: > 0, SourceBlocks.Count: > 0 } program
                || program.ExtensionOffset != 0xa0
                || program.StructuralBytes + program.SourceBytes != Convert.ToInt32(prop.Properties["PayloadSize"]) - 160
                || program.Programs.Any(subprogram => subprogram.ReturnTag.Id != Ps2DmaTagId.Ret
                    || subprogram.Relocations.Any(relocation => relocation.Tag.Id != Ps2DmaTagId.Ref))
                || program.SourceBlocks.Any(block => block.TerminalPlaceholder != 0xdeadbeef)))
                issues.Add("invalid Type-3 DMA/VIF relocation program");
            if (scene.NavigationTables.Any(table => table.TrailingBytes != 0 || table.TailPairs.Count != 6
                    || table.TailPairs.Any(pair => !float.IsFinite(pair.PathDistance))
                    || table.AiPaths.Any(path => path.TaggedProperties.Count != 2
                        || path.TaggedProperties[0].Kind != 100 || path.TaggedProperties[0].Payload.Length != 4
                        || path.TaggedProperties[1].Kind != 101 || path.TaggedProperties[1].Payload.Length != 4
                        || path.TaggedProperties[1].UInt32Value is not (0 or 1))
                    || table.TrackPaths.Any(path => path.TaggedProperties.Count != 1
                        || path.TaggedProperties[0].Kind != 0 || path.TaggedProperties[0].Payload.Length != 4
                        || !float.IsFinite(path.TaggedProperties[0].SingleValue!.Value))
                    || table.AiPaths.Concat(table.TrackPaths).Any(path => path.TaggedProperties.Any(property => property.Payload.Length == 0)
                        || path.EncodedPoints.Count != path.Points.Count
                        || path.EncodedPoints.Any(point => !float.IsFinite(point.Weight) || point.Weight < 0
                            || !float.IsFinite(point.EncodedVectorSsx.X + point.EncodedVectorSsx.Y + point.EncodedVectorSsx.Z))
                        || path.Events.Any(pathEvent => pathEvent.RuntimeKindIndex < 0
                            || pathEvent.StartDistance < 0 || pathEvent.StartDistance > path.TotalLength + 0.01f
                            || pathEvent.EndDistance < 0 || pathEvent.EndDistance > path.TotalLength + 0.01f))
                    || table.Links.Any(link => link.RuntimeKindIndex < 0
                        || MathF.Abs(link.Direction.LengthSquared() - 1f) > 0.001f
                        || (uint)link.AiPathIndex >= (uint)table.AiPaths.Count
                        || (uint)link.TrackPathIndex >= (uint)table.TrackPaths.Count)))
                issues.Add("invalid Type-14 navigation properties, event kinds, or path links");
            if (scene.Collisions.Any(asset => asset.RuntimeScratchHeader.Length != 16
                    || asset.SubmeshPointerScratch.Length != asset.Submeshes.Count * 4
                    || asset.Submeshes.Any(submesh => submesh.Indices.Count % 3 != 0
                        || submesh.FaceNormals.Count != submesh.Indices.Count / 3
                        || submesh.TriangleBatches.Count != (submesh.FaceNormals.Count + 9) / 10
                        || submesh.TriangleBatches.Select((batch, index) => (batch, index)).Any(item =>
                            item.batch.FirstTriangle != item.index * 10
                            || item.batch.TriangleCount != Math.Min(10, submesh.FaceNormals.Count - item.index * 10)))))
                issues.Add("invalid Type-12/v1 triangle-batch collision layout");
            if (scene.NisReferenceTables.Any(table => table.Slots.Count != Ssx3ReferenceTableDecoder.NisSlotCount
                    || table.Slots.Select((slot, index) => (slot, index)).Any(item => item.slot.Index != item.index
                        || !item.slot.IsRuntimeAddressable
                        || !item.slot.RuntimeCommandIds.SequenceEqual(Ssx3ReferenceTableDecoder.NisRuntimeCommandIds(item.index))
                        || item.slot.IsPopulated && item.slot.ObservedRole is null
                        || item.slot.ObjectReference is { } reference && reference.TargetResourceType != 3)))
                issues.Add("invalid Type-18 script-object slots or runtime command mapping");
            if (scene.ParticleModels.Any(model => Convert.ToString(model.Properties["RuntimeTextureAssetId"])
                    != ParticleModelAsset.RuntimeTextureAssetId
                || Convert.ToString(model.Properties["RuntimeGsAlphaRegister"]) != $"0x{ParticleModelAsset.RuntimeGsAlphaRegister:X2}"))
                issues.Add("invalid Type-4 fog texture or GS alpha state");
            if (scene.ParticleEmitters.Any(instance =>
                Convert.ToBoolean(instance.Properties["RuntimeConsumesSerializedBoundingRadius"])))
                issues.Add("invalid Type-5 stored-radius runtime-consumer classification");
            if (scene.Lights.Any(light => light.TailMarker != LightAsset.ExpectedTailMarker
                    || light.Flags is not (0 or 0x100)
                    || light.DistanceFalloffExponent is < 0 or > 3
                    || light.Kind == 1 && (light.SpotInnerConeCosine < light.SpotOuterConeCosine
                        || light.AngularFalloffExponent < 0)
                    || light.Kind != 1 && (light.SpotInnerConeCosine != 0f || light.SpotOuterConeCosine != 0f)))
                issues.Add("invalid Type-6 runtime light parameters");
            if (scene.Lights.Any(light => light.IsRuntimeAdmitted
                    != (!light.HasRuntimeFilterFlag0x100 && light.Kind is 1 or 2)))
                issues.Add("invalid Type-6 runtime-admission classification");
            if (scene.Halos.Any(halo => (halo.SerializedCollectionPointerToken & 7) != 0
                    || (halo.SerializedEntryPointerToken & 7) != 0)
                || scene.Halos.GroupBy(halo => halo.TrackId).Any(group =>
                    group.Select(halo => halo.SerializedCollectionPointerToken).Distinct().Count() != 1
                    || group.Select(halo => halo.SerializedEntryTableBasePointerToken).Distinct().Count() != 1))
                issues.Add("invalid Type-7 serialized pointer-token sequence");
            if (scene.VisibilityCurtains.Any(curtain => curtain.CornersSsx.Count != 4 || curtain.Points.Count != 5
                    || curtain.Points[0] != curtain.Points[^1] || curtain.LoadedFlag != 1
                    || MathF.Abs(new Vector3(curtain.PlaneSsx.X, curtain.PlaneSsx.Y, curtain.PlaneSsx.Z).LengthSquared() - 1) > 0.001f
                    || curtain.RuntimeCandidateScore(curtain.BoundingSphereCenterSsx) != 0
                    || MathF.Abs(MathF.Sqrt(curtain.CornersSsx.Max(curtain.RuntimeCandidateScore)) - curtain.BoundingSphereSsx.W) > 0.1f
                    || Convert.ToInt32(curtain.Properties["PayloadSize"]) != VisibilityCurtain.SerializedSize))
                issues.Add("invalid Type-11 visibility-curtain quadrilateral, candidate metric, or runtime framing");
            if (unresolvedProps > 0) issues.Add($"{unresolvedProps} unresolved props");
            if (modelsWithoutTextures > 0) issues.Add($"{modelsWithoutTextures} models without decoded diffuse previews");
            if (unresolvedLightmaps > 0) issues.Add($"{unresolvedLightmaps} unresolved lightmap references");
            if (unresolvedNisReferences > 0) issues.Add($"{unresolvedNisReferences} unresolved NIS instance references");
            if (scene.UnknownSections.Any(x => x.ResourceType == 17)) issues.Add("fallback Type-17 camera-trigger resources");
            if (scene.Triggers.Count != scene.CameraTriggerTables.Sum(x => x.Records.Count)) issues.Add("inconsistent Type-17 trigger projections");
            if (scene.CameraTriggerTables.SelectMany(table => table.Records).Any(record =>
                    record.Shape.RuntimeContainmentFunction != (record.Shape.Kind == 0
                        ? CameraTriggerShape.RuntimeEllipseContainmentFunction : CameraTriggerShape.RuntimeBoxContainmentFunction)
                    || !record.Shape.ContainsRuntimePointSsx(Ssx3Coordinates.ToSsx3(record.Shape.Center))))
                issues.Add("invalid Type-17 runtime volume transform or center containment");
            if (scene.CameraTriggerTables.SelectMany(table => table.Records)
                .SelectMany(record => new[] { record.Action0, record.Action1 }).Any(action => action.Kind != 3
                    && (action.BlendDurationSeconds is null || action.RuntimeCameraAlgorithmId is null
                        || !float.IsFinite(action.RuntimeBlendFractionPerFrame ?? float.NaN))))
                issues.Add("invalid Type-17 runtime action dispatch, camera-algorithm mapping, or blend fraction");
            if (scene.SoundTriggerTables.Any(table => table.Bindings.Any(binding => binding.BlockIndex < 0 || binding.BlockIndex >= table.Blocks.Count
                    || binding.AnchorObjectReference is PackedObjectReference anchor
                        && (anchor.TrackId is < 0 or > byte.MaxValue || anchor.ResourceId is < 0 or > 0x00ffffff))
                || table.Bindings.Select(binding => binding.SerializedIdentity).Distinct().Count() != table.Bindings.Count
                || table.Bindings.Any(binding => binding.AnchorObjectReference is PackedObjectReference anchor
                    && (!propNames.TryGetValue(Key(anchor.TrackId, anchor.ResourceId), out var anchorName)
                        || !binding.SerializedIdentity.MatchesName(anchorName)))
                || table.Blocks.SelectMany(block => block.SharedTriggerInfoIds
                        .Concat(block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId)))
                    .Any(id => Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id) is null)
                || table.Blocks.Any(block => block.SerializedSize != block.Data.Length || block.SpatialDescriptors.Any(descriptor =>
                    descriptor.SerializedSize is not (24 or 28 or 48)
                    || Ssx3SoundTriggerDecoder.SpatialDescriptorKindName(descriptor.Kind) == "Unknown"
                    || descriptor.Kind is 0 or 2 or 3 && descriptor.Radius is not > 0
                    || descriptor.Kind == 0 && descriptor.DistanceFalloffCurve is null
                    || descriptor.Kind == 1 && (descriptor.SemiAxisLengths is not Vector3 axes
                        || axes.X <= 0 || axes.Y <= 0 || axes.Z <= 0
                        || descriptor.OrientationAxis is not Vector3 axis
                        || MathF.Abs(axis.LengthSquared() - 1f) > 0.001f
                        || descriptor.DistanceFalloffCurve is null)))))
                issues.Add("invalid Type-13 sound-trigger bindings or descriptor parameters");
            if (scene.StructuredTables.Where(x => x.ResourceType == 15 && x.Sections.Count > 0).Any(table =>
                    table.ModifierSections.Any(section => section.TypeId != section.Slot + 1
                        || Ssx3StructuredTableDecoder.ModifierTypeName(section.TypeId) != section.TypeName
                        || section.HeaderSize != 12 + section.RecordCount * 8 || section.IndexRecordSize != 12
                        || section.IndexData.Length != 40 + section.SpatialIndex.EntryCount * 8
                        || !float.IsFinite(section.SpatialIndex.Scale + section.SpatialIndex.Origin.X + section.SpatialIndex.Origin.Y
                            + section.SpatialIndex.Extent) || section.SpatialIndex.Scale <= 0
                        || section.SpatialIndex.SerializedCapacity != 0 || section.SpatialIndex.Reserved != 0
                        || section.SpatialIndex.DefaultLeafWord0 != 0 || section.SpatialIndex.DefaultLeafWord1 != uint.MaxValue
                        || section.SpatialIndex.SerializedNodeEndPointerPlaceholder != 0
                        || section.SpatialIndex.RootEntryIndex >= section.SpatialIndex.EntryCount
                        || section.SpatialIndex.Entries.Count != section.SpatialIndex.EntryCount
                        || section.SpatialIndex.Entries.Any(entry => entry.Kind == WorldModifierIndexEntryKind.RecordLeaf
                                && (entry.ModifierRecordIndex is null || entry.ModifierRecordIndex >= section.RecordCount)
                            || entry.Kind == WorldModifierIndexEntryKind.EmptyLeaf && entry.Word1 != uint.MaxValue
                            || entry.Kind == WorldModifierIndexEntryKind.Branch && (entry.Children.Count != 4
                                || entry.Children.Select(child => child.Quadrant).SequenceEqual(Enum.GetValues<WorldModifierSpatialQuadrant>()) is false
                                || entry.Children.Any(child => child.EntryIndex >= section.SpatialIndex.EntryCount
                                    || child.EntryIndex != child.Handle >> 1))
                            || entry.Kind != WorldModifierIndexEntryKind.Branch && entry.Children.Count != 0
                            || entry.Kind is not (WorldModifierIndexEntryKind.Branch or WorldModifierIndexEntryKind.RecordLeaf
                                or WorldModifierIndexEntryKind.EmptyLeaf))
                        || section.SpatialIndex.EntryCount != section.SpatialIndex.Entries.Count(entry =>
                            entry.Kind == WorldModifierIndexEntryKind.Branch) * 4 + 1
                        || section.Records.Count != section.RecordCount
                        || section.Records.Any(record => record.Data.Length != Ssx3StructuredTableDecoder.ModifierRecordSize(section.TypeId)
                            || record.Words.Count * 4 != record.Data.Length || section.TypeId == 11 && record.Tags.Count != 4
                            || !ValidWorldModifierReference(table, section, record)))))
                issues.Add("invalid Type-15 World Painter records");
            if (scene.StructuredTables.Where(x => x.ResourceType == 16 && x.Sections.Count > 0).Any(table =>
                    table.RootRailReferences.Any(reference => reference.TrackId != table.TrackId)
                    || table.RailReferenceSets.Select((set, index) => (set, index)).Any(item => item.set.Index != item.index
                        || item.set.Slots.Count != 6 || item.set.Slots.Any(reference => reference is not null && reference.TrackId != table.TrackId))
                    || table.ModifierProgramBlocks.Select((block, index) => (block, index)).Any(item => item.block.Index != item.index
                        || item.block.ModifierSlots.Count != 13 || item.block.ModifierSlots.Any(reference => reference is not null
                            && (reference.TrackId != table.TrackId || reference.ProgramIndex >= table.LunPrograms.Count)))
                    || table.ModifierProgramGroups.Select((group, index) => (group, index)).Any(item => item.group.Index != item.index
                        || item.group.Kind != 2 || item.group.ModifierSlots.Count != 13
                        || item.group.FirstBlockIndex + item.group.BlockCount > table.ModifierProgramBlocks.Count
                        || item.group.ProgramReferences.Concat(item.group.ModifierSlots).Any(reference => reference is not null
                            && (reference.TrackId != table.TrackId || reference.ProgramIndex >= table.LunPrograms.Count)))
                    || table.ModifierProgramGroups.Sum(group => group.BlockCount) != table.ModifierProgramBlocks.Count
                    || table.LunPrograms.Select((program, index) => (program, index)).Any(item => item.program.Index != item.index
                        || item.program.ProgramLength != item.program.Program.Length
                        || item.program.BytecodeLength + 16 != item.program.ProgramLength
                        || item.program.Instructions.Count == 0 || item.program.Instructions[^1].Opcode != 0x2a
                        || item.program.Instructions.Any(instruction => instruction.SerializedSize != Ssx3StructuredTableDecoder.LunInstructionSize(instruction.Opcode)
                            || instruction.Operation != Ssx3StructuredTableDecoder.LunInstructionOperation(instruction.Opcode)
                            || instruction.Operation == LunOperation.CallNative && (instruction.DestinationSlot != instruction.Operand0
                                || instruction.NativeFunctionId != instruction.Operand1 || instruction.ArgumentCount != instruction.Operand2
                                || instruction.NativeFunctionName != Ssx3StructuredTableDecoder.LunNativeFunctionName(instruction.Operand1)
                                || instruction.NativeFunctionSubsystem != Ssx3StructuredTableDecoder.LunNativeFunctionSubsystem(instruction.Operand1))
                            || instruction.Operation != LunOperation.CallNative && (instruction.DestinationSlot is not null
                                || instruction.NativeFunctionId is not null || instruction.ArgumentCount is not null
                                || instruction.NativeFunctionName is not null || instruction.NativeFunctionSubsystem is not null))
                        || item.program.Instructions.Sum(instruction => instruction.SerializedSize) != item.program.BytecodeLength
                        || item.program.Routines.Count != 1 + item.program.AdditionalDescriptors.Count
                        || item.program.PrimaryDescriptor.ProgramOffset != 0
                        || item.program.Routines.Select(routine => routine.Offset).SequenceEqual(
                            new[] { item.program.PrimaryDescriptor }.Concat(item.program.AdditionalDescriptors)
                                .Select(descriptor => checked((int)descriptor.EntryWordOffset * 4))) is false
                        || item.program.DeclaredSize != 16 + item.program.Program.Length + item.program.AdditionalDescriptors.Count * 16
                        || item.program.PaddingBytes is < 0 or > 12)
                    || table.RailProgramRecords.Select((record, index) => (record, index)).Any(item => item.record.Index != item.index
                        || item.record.Kind > 3 || item.record.SerializedSize != 16 + item.record.Descriptors.Count * 12
                        || item.record.ControlLow != 0 || item.record.PrimaryInputRailReference is null
                        || item.record.InputRailCount != (item.record.Kind is 0 or 2 ? 1 : 2)
                        || (item.record.Kind is 0 or 2) != (item.record.SecondaryInputRailReference is null)
                        || item.record.Kind == 0 && item.record.OutputDescriptors.Any(descriptor => descriptor.Role != RailSplineRole.NonRailMotionPath)
                        || item.record.Kind is 2 or 3 && item.record.OutputDescriptors.Any(descriptor => descriptor.Role is not (RailSplineRole.HandplantRail or RailSplineRole.GrindRail))
                        || table.TrackId != 0 && (item.record.GeneratedRailId != table.RailSplineMetadataEntries.Count + item.index
                            || item.record.GeneratedRailReference?.RailId != item.record.GeneratedRailId
                            || item.record.GeneratedRailReference?.TrackId != table.TrackId)
                        || table.TrackId == 0 && (item.record.GeneratedRailId is not null || item.record.GeneratedRailReference is not null)
                        || item.record.ControlLow != (ushort)item.record.ControlWord || item.record.ControlHigh != (ushort)(item.record.ControlWord >> 16)
                        || item.record.Descriptors.Any(descriptor => !float.IsFinite(descriptor.Scalar0) || !float.IsFinite(descriptor.Scalar1)
                            || descriptor.Low != (ushort)descriptor.Word2 || descriptor.High != (ushort)(descriptor.Word2 >> 16)
                            || descriptor.Low > (ushort)RailSplineRole.GrindRail || descriptor.Role != (RailSplineRole)descriptor.Low
                            || descriptor.High != ushort.MaxValue && (descriptor.High > (ushort)SsxSurfaceType.WipeoutRock
                                || descriptor.SurfaceOverride != (SsxSurfaceType)descriptor.High)
                            || descriptor.High == ushort.MaxValue && descriptor.SurfaceOverride is not null)
                        || item.record.PrimaryRailReference is not null && item.record.PrimaryRailReference.TrackId != table.TrackId
                        || item.record.SecondaryRailReference is not null && item.record.SecondaryRailReference.TrackId != table.TrackId)
                    || table.RailProgramReferenceIndices.Any(index => index >= table.RailProgramRecords.Count)))
                issues.Add("invalid Type-16 packed rail/program references");
            if (scene.StructuredTables.Where(table => table.ResourceType == 16 && table.TrackId != 0)
                .Any(table => table.RootRailReferences
                    .Concat(table.RailReferenceSets.SelectMany(set => set.Slots).Where(reference => reference is not null).Select(reference => reference!))
                    .Concat(table.RailProgramRecords.SelectMany(record => new[] { record.PrimaryRailReference, record.SecondaryRailReference })
                        .Where(reference => reference is not null).Select(reference => reference!))
                    .Any(reference => reference.RailId >= table.RailSplineMetadataEntries.Count
                        && !table.RailProgramRecords.Any(record => record.GeneratedRailReference == reference))))
                issues.Add("Type-16 generated rail reference does not resolve to a rail-program record");
            if (scene.StructuredTables.Where(table => table.ResourceType == 16 && table.Sections.Count > 0)
                .Any(table => table.RailSplineMetadataEntries.Count != scene.Splines.Count(spline =>
                    Convert.ToInt32(spline.Properties["TrackId"]) == table.TrackId)))
                issues.Add("Type-16 per-spline metadata count does not match Type-8 splines");
            if (scene.StructuredTables.Where(table => table.ResourceType == 16)
                .SelectMany(table => table.RailSplineMetadataEntries)
                .Any(entry => entry.Low > (ushort)RailSplineRole.GrindRail
                    || entry.High > (ushort)SsxSurfaceType.WipeoutRock
                    || entry.Role != (RailSplineRole)entry.Low || entry.Surface != (SsxSurfaceType)entry.High))
                issues.Add("invalid Type-16 per-spline role/surface metadata");
            if (scene.AudioBanks.Any(x => x.EntryCount != 0 && (x.ReservedWords.Count != 2
                || x.SlotRelativeOffsets.Count != x.EntryCount
                || x.Sounds.Count != x.SlotRelativeOffsets.Count(offset => offset != 0)
                || x.Sounds.Any(sound => sound.Platform != 5 || sound.InfoSections.Count == 0
                    || sound.InfoSections[^1].Terminator != 0xff
                    || sound.InfoSections.Take(sound.InfoSections.Count - 1).Any(section => section.Terminator != 0xfe)
                    || sound.InfoSections.Any(section => section.Codec != 4 || section.ChannelCount != 1
                        || section.RootMidiNote is < 0 or > 127
                        || section.MinimumVelocity is < 0 or > 127 || section.MaximumVelocity is < 0 or > 127
                        || section.MinimumVelocity > section.MaximumVelocity
                        || section.MinimumMidiNote is < 0 or > 127 || section.MaximumMidiNote is < 0 or > 127
                        || section.MinimumMidiNote > section.MaximumMidiNote
                        || section.ReleaseEnvelopeSegmentIndex < -1
                        || section.ReleaseEnvelopeSegmentIndex >= section.PlaybackEnvelopeSegmentCount
                        || section.PlaybackEnvelopeSegmentCount is < 1 or > 128
                        || section.InitialEnvelopeVolume is < 0 or > 127
                        || section.PlaybackEnvelopeOffset is null && section.PlaybackEnvelopeSegments.Count != 0
                        || section.PlaybackEnvelopeOffset is not null && section.PlaybackEnvelopeSegments.Count != section.PlaybackEnvelopeSegmentCount
                        || section.PlaybackEnvelopeSegments.Any(segment => segment.Volume is < 0 or > 127)
                        || section.SampleRate is not (16_000 or 22_050 or 32_000) || section.SampleCount <= 0
                        || section.LoopStart is not null && section.MicroTalkLoopRelativeOffset is null
                        || section.ChannelOffsets.Count != 1)))))
                issues.Add("invalid Type-20 BNKl sound records");
            var decodedMicroTalkSections = 0;
            long decodedMicroTalkSamples = 0;
            var microTalkDecodeErrors = 0;
            foreach (var bank in scene.AudioBanks.Where(bank => bank.EntryCount != 0))
            foreach (var section in bank.Sounds.SelectMany(sound => sound.InfoSections))
            {
                try
                {
                    var samples = EaMicroTalkDecoder.DecodeBankSection(bank, section);
                    if (samples.Length != section.SampleCount)
                    {
                        microTalkDecodeErrors++;
                        continue;
                    }
                    decodedMicroTalkSections++;
                    decodedMicroTalkSamples += samples.Length;
                }
                catch (Exception exception) when (exception is Mountainizer.Core.FormatException or NotSupportedException or OverflowException)
                {
                    microTalkDecodeErrors++;
                }
            }
            if (microTalkDecodeErrors != 0)
                issues.Add($"{microTalkDecodeErrors} Type-20 MicroTalk sections failed PCM decoding");
            if (scene.PlanarRoutes.Any(route => route.Samples.Count == 0
                    || route.SelectRuntimeSampleIndex(float.MinValue) != 0
                    || route.SelectRuntimeSampleIndex(float.MaxValue) != route.Samples.Count - 1
                    || route.Samples.Zip(route.Samples.Skip(1)).Any(pair =>
                    {
                        var travel = pair.Second.Position - pair.First.Position;
                        return travel.LengthSquared() > 0.0001f
                            && MathF.Abs(Vector2.Dot(Vector2.Normalize(travel), pair.First.LateralNormal)) > 0.05f;
                    })
                    || route.Markers.Any(marker => Ssx3PlanarRouteDecoder.MarkerTextureName(marker.Kind) == "Unknown")))
                issues.Add("invalid Type-21 radar-route runtime projection data");
            if (scene.AvalancheAnimations.Any(x => x.Blocks.Any(block => block.Frames.Count != block.UnitCount * 30
                    || !HasValidAvalancheRuntimeSamples(block))
                || x.MetadataSegments.Any(segment => segment.Parameters.Count != segment.ParameterCount
                    || segment.Pairs.Count != segment.PairCount
                    || segment.Data.Length != 4 + 8 * segment.ParameterCount + 4 * segment.PairCount
                    || segment.Parameters.Any(parameter => !float.IsFinite(parameter.TimeSeconds) || parameter.TimeSeconds < 0)
                    || !segment.Parameters.Select(parameter => parameter.TimeSeconds)
                        .SequenceEqual(segment.Parameters.Select(parameter => parameter.TimeSeconds).Order())
                    || !segment.Pairs.Select(pair => pair.FrameIndex)
                        .SequenceEqual(segment.Pairs.Select(pair => pair.FrameIndex).Order())
                    || segment.Parameters.Count > 0 && Ssx3AvalancheDecoder.TimedTargetsDue(segment,
                        segment.Parameters[^1].TimeSeconds).Count != segment.Parameters.Count
                    || segment.Pairs.Count > 0 && Ssx3AvalancheDecoder.SchedulePairsDue(segment,
                        segment.Pairs[^1].TriggerTimeSeconds).Count != segment.Pairs.Count
                    || segment.Pairs.Any(pair => pair.BlockIndex >= x.Blocks.Count
                        || pair.FrameIndex >= x.Blocks[pair.BlockIndex].Frames.Count))))
                issues.Add("invalid Type-22 avalanche records");
            if (errors > 0) issues.Add($"{errors} structured parser errors");
            var trackBankReferences = scene.SoundTriggerTables.SelectMany(table => table.Bindings
                .Where(binding => binding.BlockIndex >= 0 && binding.BlockIndex < table.Blocks.Count)
                .SelectMany(binding => table.Blocks[binding.BlockIndex].SharedTriggerInfoIds
                    .Concat(table.Blocks[binding.BlockIndex].SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId))
                    .Select(id => (Binding: binding, Definition: Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id)))
                    .Where(item => item.Definition?.SoundBankId is 8 or 9)
                    .Select(item => (item.Binding, Definition: item.Definition!)))).ToArray();
            failed |= issues.Count > 0;
            courses.Add(new { course.Code, course.Name, Terrain = scene.Terrain.Count,
                TerrainWithSecondaryTextures = scene.Terrain.Count(x => x.HasSecondaryTexture),
                TerrainDiffuseTextureReferences = terrainDiffuseTextureReferences.Length,
                ResolvedTerrainDiffuseTextureReferences = resolvedTerrainDiffuseTextureReferences,
                TerrainSurfaceTypes = scene.Terrain.GroupBy(x => x.Surface).OrderBy(x => x.Key)
                    .Select(x => new { Value = (int)x.Key, Name = x.Key.ToString(), Count = x.Count() }).ToArray(),
                TerrainPatchFlagValues = scene.Terrain.GroupBy(x => x.PatchFlags).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                TerrainTextureStateValues = scene.Terrain.GroupBy(x => x.TextureStateFlags).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                TerrainRenderFlagValues = scene.Terrain.GroupBy(x => x.RenderFlags).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                Props = scene.Props.Count,
                Models = scene.Models.Count, Materials = scene.Materials.Count,
                MaterialsWithNondefaultTextureStateWord02 = scene.Materials.Count(x => x.HasNondefaultTextureStateWord02),
                MaterialsAddingPrimaryTextureStateBit = scene.Materials.Count(x => x.AddsPrimaryTextureStateBit),
                MaterialsUsingPrimaryTextureAlphaBlend = scene.Materials.Count(x => x.UsesPrimaryTextureAlphaBlend),
                MaterialsUsingOpaquePrimaryTextureReplacement = scene.Materials.Count(x => !x.UsesPrimaryTextureAlphaBlend),
                MaterialsWithTextureFrameTables = scene.Materials.Count(x => x.HasTextureFrameTable),
                MaterialTextureFrames = scene.Materials.Sum(x => x.TextureFrameCount),
                ModelsWithTextureFrameTables = modelTextureFrameCounts.Count(counts => counts.Length == 1),
                ModelTextureFrameCountValues = modelTextureFrameCounts.Where(counts => counts.Length == 1)
                    .GroupBy(counts => counts[0]).OrderBy(group => group.Key)
                    .Select(group => new { FrameCount = group.Key, Models = group.Count() }).ToArray(),
                MaterialTextureReferences = materialTextureReferences.Length,
                ResolvedMaterialTextureReferences = resolvedMaterialTextureReferences,
                MaterialStateWord0Values = scene.Materials.GroupBy(x => x.StateWord0).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                MaterialStateWord1Values = scene.Materials.GroupBy(x => x.StateWord1).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                MaterialTextureStateWord02Values = scene.Materials.GroupBy(x => x.TextureStateWord02).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X4}", Count = x.Count() }).ToArray(),
                Textures = scene.Textures.Count, Lightmaps = scene.Textures.Count(x => x.IsLightmap),
                Type9RendererDispatchStateValues = scene.Textures.Where(x => x.Usage == TextureUsage.Diffuse)
                    .GroupBy(x => x.RendererDispatchState).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X8}", Count = x.Count() }).ToArray(),
                InstanceDmaPrograms = scene.Props.Sum(x => x.RenderDmaProgram?.Programs.Count ?? 0),
                InstanceDmaRelocations = scene.Props.Sum(x => x.RenderDmaProgram?.Programs.Sum(program => program.Relocations.Count) ?? 0),
                InstanceDmaSourceBlocks = scene.Props.Sum(x => x.RenderDmaProgram?.SourceBlocks.Count ?? 0),
                InstanceDmaSourceQuadwords = scene.Props.Sum(x => x.RenderDmaProgram?.SourceBlocks.Sum(block => block.QuadwordCount) ?? 0),
                Splines = scene.Splines.Count, SplineSegments = scene.Splines.Sum(x => x.Segments.Count),
                SplineTotalLength = scene.Splines.Sum(x => (double)x.TotalLength),
                SplineSerializedPointerTokens = scene.Splines.Select(x => x.SerializedSegmentPointerToken).Distinct().Count(),
                NavigationPaths = scene.NavigationPaths.Count,
                NavigationEvents = scene.NavigationPaths.Sum(x => x.Events.Count),
                NavigationTables = scene.NavigationTables.Count,
                NavigationAiPaths = scene.NavigationTables.Sum(x => x.AiPaths.Count),
                NavigationTrackPaths = scene.NavigationTables.Sum(x => x.TrackPaths.Count),
                NavigationEncodedPoints = scene.NavigationPaths.Sum(x => x.EncodedPoints.Count),
                NavigationNonnegativePointWeights = scene.NavigationPaths.Sum(path => path.EncodedPoints.Count(point =>
                    point.Weight >= 0)),
                NavigationTaggedProperties = scene.NavigationPaths.Sum(x => x.TaggedProperties.Count),
                NavigationEventTypes = scene.NavigationPaths.SelectMany(x => x.Events)
                    .GroupBy(x => new { x.Type, x.RuntimeKindIndex }).OrderBy(x => x.Key.RuntimeKindIndex)
                    .Select(x => new { x.Key.Type, x.Key.RuntimeKindIndex, Count = x.Count() }).ToArray(),
                NavigationEventDistancesWithinPathLength = scene.NavigationPaths.Sum(path => path.Events.Count(pathEvent =>
                    pathEvent.StartDistance >= 0 && pathEvent.StartDistance <= path.TotalLength + 0.01f
                    && pathEvent.EndDistance >= 0 && pathEvent.EndDistance <= path.TotalLength + 0.01f)),
                NavigationWrappedEventRanges = scene.NavigationPaths.Sum(path => path.Events.Count(pathEvent =>
                    pathEvent.StartDistance > pathEvent.EndDistance)),
                NavigationTailPairs = scene.NavigationTables.Sum(x => x.TailPairs.Count),
                NavigationLinks = scene.NavigationTables.Sum(x => x.Links.Count),
                NavigationLinkKinds = scene.NavigationTables.SelectMany(x => x.Links)
                    .GroupBy(x => new { x.RawKind, x.RuntimeKindIndex }).OrderBy(x => x.Key.RuntimeKindIndex)
                    .Select(x => new { x.Key.RawKind, x.Key.RuntimeKindIndex, Count = x.Count() }).ToArray(),
                CollisionAssets = scene.Collisions.Count,
                CollisionSubmeshes = scene.Collisions.Sum(x => x.Submeshes.Count),
                CollisionTriangles = scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.Indices.Count / 3)),
                CollisionVertices = scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.Vertices.Count)),
                CollisionTriangleBatches = scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.TriangleBatches.Count)),
                CollisionIndexPaddingBytes = scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.IndexPadding.Length)),
                CollisionTriangleBatchPaddingBytes = scene.Collisions.Sum(x => x.Submeshes.Sum(s => s.TriangleBatchPadding.Length)),
                CollisionRuntimeScratchBytes = scene.Collisions.Sum(x => x.RuntimeScratchHeader.Length + x.SubmeshPointerScratch.Length),
                CollisionSphereTrees = scene.SphereTrees.Count, CollisionSphereTreeRecords = scene.SphereTrees.Sum(x => x.Trees.Count),
                CollisionSphereTreePackedBytes = scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.PackedPayloadSize)),
                CollisionSphereTreeDecodedNodeBytes = scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.DecodedNodeMasks.Length)),
                CollisionSphereTreeReferencedNodes = scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.NodeLevels.Sum(level => level.ReferencedNodeCount))),
                CollisionSphereTreeChildLinks = scene.SphereTrees.Sum(x => x.Trees.Sum(t => t.NodeLevels.Sum(level => level.ReferencedChildCount))),
                SoundTriggerTables = scene.SoundTriggerTables.Count, SoundTriggerBindings = scene.SoundTriggerTables.Sum(x => x.Bindings.Count),
                SoundTriggerUniqueBindingIdentities = scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                    .Select(x => x.SerializedIdentity).Distinct().Count(),
                SoundTriggerVerifiedAnchorNameHashes = scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                    .Count(x => x.AnchorObjectReference is PackedObjectReference anchor
                        && propNames.TryGetValue(Key(anchor.TrackId, anchor.ResourceId), out var anchorName)
                        && x.SerializedIdentity.MatchesName(anchorName)),
                SoundTriggerAnchorObjectReferences = scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                    .Count(x => x.AnchorObjectReference is not null),
                SoundTriggerUnanchoredBindings = scene.SoundTriggerTables.SelectMany(x => x.Bindings)
                    .Count(x => x.AnchorObjectReference is null),
                SoundTriggerBlocks = scene.SoundTriggerTables.Sum(x => x.Blocks.Count),
                SoundTriggerInfoReferences = scene.SoundTriggerTables.Sum(x => x.Blocks.Sum(block => block.TriggerInfoIds.Count)),
                SoundTriggerNamedAudioReferences = scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                    .SelectMany(block => block.SharedTriggerInfoIds.Concat(block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId)))
                    .Count(id => Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id)?.Kind == SoundTriggerInfoKind.NamedAudioEvent),
                SoundTriggerIndexedBankSoundReferences = scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                    .SelectMany(block => block.SharedTriggerInfoIds.Concat(block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId)))
                    .Count(id => Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id)?.Kind == SoundTriggerInfoKind.IndexedBankSound),
                SoundTriggerBuiltInSlotReferences = scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                    .SelectMany(block => block.SharedTriggerInfoIds.Concat(block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId)))
                    .Count(id => Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id)?.Kind == SoundTriggerInfoKind.BuiltInSlot),
                SoundTriggerCrowdInstanceActivationReferences = scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                    .SelectMany(block => block.SharedTriggerInfoIds.Concat(block.SpatialDescriptors.Select(descriptor => descriptor.TriggerInfoId)))
                    .Count(id => Ssx3SoundTriggerDecoder.TriggerInfoDefinition(id)?.Kind == SoundTriggerInfoKind.CrowdInstanceActivation),
                SoundTriggerTrackBankReferences = trackBankReferences.Length,
                SoundTriggerTrackBankReferencesWithinDeclaredTable = trackBankReferences.Count(item =>
                    item.Binding.AnchorObjectReference is PackedObjectReference anchor
                    && scene.AudioBanks.FirstOrDefault(bank => bank.TrackId == anchor.TrackId
                        && bank.ResourceId == checked((int)item.Definition.SoundBankId!.Value - 8)) is { } bank
                    && item.Definition.SoundIndex!.Value < checked((uint)bank.EntryCount)),
                SoundTriggerTrackBankReferencesToPopulatedSlots = trackBankReferences.Count(item =>
                    item.Binding.AnchorObjectReference is PackedObjectReference anchor
                    && scene.AudioBanks.FirstOrDefault(bank => bank.TrackId == anchor.TrackId
                        && bank.ResourceId == checked((int)item.Definition.SoundBankId!.Value - 8)) is { } bank
                    && bank.Sounds.Any(sound => checked((uint)sound.Slot) == item.Definition.SoundIndex!.Value)),
                SoundTriggerSpatialDescriptors = scene.SoundTriggerTables.Sum(x => x.Blocks.Sum(block => block.SpatialDescriptors.Count)),
                SoundTriggerDistanceFalloffCurves = scene.SoundTriggerTables.SelectMany(x => x.Blocks)
                    .SelectMany(x => x.SpatialDescriptors).Where(x => x.DistanceFalloffCurve is not null)
                    .GroupBy(x => x.DistanceFalloffCurve).OrderBy(x => x.Key)
                    .Select(x => new { Curve = x.Key, Value = (int)x.Key!.Value, Count = x.Count() }).ToArray(),
                PlanarRoutes = scene.PlanarRoutes.Count, PlanarRouteSamples = scene.PlanarRoutes.Sum(x => x.Samples.Count),
                StructuredType15Tables = scene.StructuredTables.Count(x => x.ResourceType == 15),
                StructuredType16Tables = scene.StructuredTables.Count(x => x.ResourceType == 16),
                WorldPainterSections = scene.StructuredTables.Sum(x => x.ModifierSections.Count),
                WorldPainterRecords = scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.Records.Count)),
                WorldModifierSections = scene.StructuredTables.Sum(x => x.ModifierSections.Count),
                WorldModifierRecords = scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.Records.Count)),
                WorldModifierReferences = scene.StructuredTables.SelectMany(x => x.ModifierSections).SelectMany(section => section.Records)
                    .Count(record => record.ReferencedResourceType is not null),
                ResolvedWorldModifierReferences = scene.StructuredTables.SelectMany(table => table.ModifierSections.SelectMany(section =>
                    section.Records.Where(record => record.ReferencedResourceType is not null)
                        .Where(record => ValidWorldModifierReference(table, section, record)))).Count(),
                WorldModifierIndexEntries = scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section => section.SpatialIndex.EntryCount)),
                WorldModifierBranchChildReferences = scene.StructuredTables.Sum(x => x.ModifierSections.Sum(section =>
                    section.SpatialIndex.Entries.Sum(entry => entry.Children.Count))),
                WorldModifierTypes = scene.StructuredTables.SelectMany(x => x.ModifierSections)
                    .GroupBy(x => new { x.TypeId, x.TypeName }).OrderBy(x => x.Key.TypeId)
                    .Select(x => new { x.Key.TypeId, x.Key.TypeName, Sections = x.Count(), Records = x.Sum(section => section.RecordCount) }).ToArray(),
                WorldPainterRuntimeTypes = Enumerable.Range(0, 14).Select(typeId => new
                {
                    TypeId = typeId,
                    TypeName = Ssx3StructuredTableDecoder.WorldPainterTypeName(typeId),
                    RuntimeClass = Ssx3StructuredTableDecoder.WorldPainterRuntimeClassName(typeId),
                    SerializedRecordSize = Ssx3StructuredTableDecoder.WorldPainterRecordSize(typeId),
                    RuntimeObjectSize = Ssx3StructuredTableDecoder.WorldPainterRuntimeObjectSize(typeId),
                    PropertyNames = WorldPainterPropertyNames(typeId)
                }).ToArray(),
                RailRootReferences = scene.StructuredTables.Sum(x => x.RootRailReferences.Count),
                RailReferenceSets = scene.StructuredTables.Sum(x => x.RailReferenceSets.Count),
                RailReferences = scene.StructuredTables.Sum(x => x.RootRailReferences.Count
                    + x.RailReferenceSets.Sum(set => set.Slots.Count(reference => reference is not null))),
                ModifierProgramBlocks = scene.StructuredTables.Sum(x => x.ModifierProgramBlocks.Count),
                ModifierProgramGroups = scene.StructuredTables.Sum(x => x.ModifierProgramGroups.Count),
                ModifierProgramReferences = scene.StructuredTables.Sum(x =>
                    x.ModifierProgramBlocks.Sum(block => block.ModifierSlots.Count(reference => reference is not null))
                    + x.ModifierProgramGroups.Sum(group => group.ProgramReferences.Count(reference => reference is not null)
                        + group.ModifierSlots.Count(reference => reference is not null))),
                LunPrograms = scene.StructuredTables.Sum(x => x.LunPrograms.Count),
                LunProgramBytes = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Program.Length)),
                LunBytecodeBytes = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.BytecodeLength)),
                LunRoutines = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Routines.Count)),
                LunRoutineDescriptors = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => 1 + program.AdditionalDescriptors.Count)),
                LunInstructions = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Instructions.Count)),
                LunInstructionBytes = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.Instructions.Sum(instruction => instruction.SerializedSize))),
                LunNativeCalls = scene.StructuredTables.SelectMany(x => x.LunPrograms).SelectMany(program => program.Instructions)
                    .Where(instruction => instruction.Operation == LunOperation.CallNative)
                    .GroupBy(instruction => new { instruction.NativeFunctionId, instruction.NativeFunctionName, instruction.NativeFunctionSubsystem })
                    .OrderBy(group => group.Key.NativeFunctionId)
                    .Select(group => new { group.Key.NativeFunctionId, group.Key.NativeFunctionName, group.Key.NativeFunctionSubsystem, Count = group.Count() }).ToArray(),
                LunPaddingBytes = scene.StructuredTables.Sum(x => x.LunPrograms.Sum(program => program.PaddingBytes)),
                RailProgramRecords = scene.StructuredTables.Sum(x => x.RailProgramRecords.Count),
                RailProgramDescriptors = scene.StructuredTables.Sum(x => x.RailProgramRecords.Sum(record => record.Descriptors.Count)),
                RailProgramKinds = scene.StructuredTables.SelectMany(x => x.RailProgramRecords)
                    .GroupBy(record => record.Kind).OrderBy(group => group.Key)
                    .Select(group => new { Kind = group.Key, Records = group.Count(), Inputs = group.Sum(record => record.InputRailCount),
                        Descriptors = group.Sum(record => record.OutputDescriptors.Count) }).ToArray(),
                RailProgramRecordReferences = scene.StructuredTables.Sum(x => x.RailProgramReferenceIndices.Count),
                RailSplineMetadataEntries = scene.StructuredTables.Sum(x => x.RailSplineMetadataEntries.Count),
                RailProgramRailReferences = scene.StructuredTables.Sum(x => x.RailProgramRecords.Sum(record =>
                    (record.PrimaryRailReference is null ? 0 : 1) + (record.SecondaryRailReference is null ? 0 : 1))),
                RailProgramRailReferencesMatchingSplineIds = scene.StructuredTables.SelectMany(x => x.RailProgramRecords)
                    .SelectMany(record => new[] { record.PrimaryRailReference, record.SecondaryRailReference })
                    .Where(reference => reference is not null).Select(reference => reference!)
                    .Count(reference => scene.Splines.Any(spline => Convert.ToInt32(spline.Properties["TrackId"]) == reference.TrackId
                        && Convert.ToInt32(spline.Properties["ResourceId"]) == reference.RailId)),
                BnklBanks = scene.AudioBanks.Count, BnklEntries = scene.AudioBanks.Sum(x => x.EntryCount),
                BnklPopulatedSlots = scene.AudioBanks.Sum(x => x.Sounds.Count),
                BnklInfoSections = scene.AudioBanks.Sum(x => x.Sounds.Sum(sound => sound.InfoSections.Count)),
                BnklLoopedInfoSections = scene.AudioBanks.Sum(x => x.Sounds.Sum(sound => sound.InfoSections.Count(section => section.LoopStart is not null))),
                BnklLayeredSounds = scene.AudioBanks.Sum(x => x.Sounds.Count(sound => sound.InfoSections.Count > 1)),
                BnklMaximumLayersPerSound = scene.AudioBanks.SelectMany(x => x.Sounds).Select(x => x.InfoSections.Count).DefaultIfEmpty().Max(),
                BnklRuntimeLayerSelectionEquation = Ssx3BnklBankDecoder.RuntimeLayerSelectionEquation,
                BnklRootMidiNotes = scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                    .GroupBy(x => x.RootMidiNote).OrderBy(x => x.Key)
                    .Select(x => new { RootMidiNote = x.Key, Count = x.Count() }).ToArray(),
                BnklPlaybackEnvelopeSections = scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                    .Count(x => x.PlaybackEnvelopeOffset is not null),
                BnklPlaybackEnvelopeSegments = scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                    .Sum(x => x.PlaybackEnvelopeSegments.Count),
                BnklMicroTalkDecodedSections = decodedMicroTalkSections,
                BnklMicroTalkDecodedSamples = decodedMicroTalkSamples,
                BnklMicroTalkDecodeErrors = microTalkDecodeErrors,
                BnklMicroTalkSamplesPerFrame = EaMicroTalkDecoder.SamplesPerFrame,
                BnklMicroTalkPcmCorrectionSections = scene.AudioBanks.SelectMany(x => x.Sounds).SelectMany(x => x.InfoSections)
                    .Count(x => x.UsesPcmCorrectionBlocks),
                BnklRuntimeEnvelopeDurationClamp = Ssx3BnklBankDecoder.RuntimeEnvelopeDurationClamp,
                BnklRuntimeEnvelopeSlopeEquation = Ssx3BnklBankDecoder.RuntimeEnvelopeSlopeEquation,
                AvalancheAnimations = scene.AvalancheAnimations.Count, AvalancheBlocks = scene.AvalancheAnimations.Sum(x => x.Blocks.Count),
                AvalancheFrames = scene.AvalancheAnimations.Sum(x => x.Blocks.Sum(block => block.Frames.Count)),
                AvalancheMetadataSegments = scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Count),
                AvalancheObjectReferences = scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Parameters.Count)),
                ResolvedAvalancheObjectReferences = scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments)
                    .SelectMany(x => x.Parameters).Count(x => propIds.Contains(Key(x.ObjectTrackId, x.ObjectResourceId))),
                AvalancheCapturedTargetIdentities = scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Parameters.Count)),
                AvalancheCapturedTargetIdentitiesCoincidingWithType3 = scene.AvalancheAnimations.SelectMany(x => x.MetadataSegments)
                    .SelectMany(x => x.Parameters).Count(x => propIds.Contains(Key(x.ObjectTrackId, x.ObjectResourceId))),
                AvalancheRuntimeLoadDiscardsCapturedTargetIdentity = true,
                AvalancheRuntimeFramesPerSecond = Ssx3AvalancheDecoder.RuntimeFramesPerSecond,
                AvalancheRuntimeTranslationEquation = Ssx3AvalancheDecoder.RuntimeTranslationEquation,
                AvalancheRuntimeScaleEquation = Ssx3AvalancheDecoder.RuntimeScaleEquation,
                AvalancheRuntimeRotationEquation = Ssx3AvalancheDecoder.RuntimeRotationEquation,
                AvalancheRuntimeScheduleEquation = Ssx3AvalancheDecoder.RuntimeScheduleEquation,
                AvalancheRuntimeSampledBlocks = scene.AvalancheAnimations.Sum(x => x.Blocks.Count),
                AvalancheSchedulePairs = scene.AvalancheAnimations.Sum(x => x.MetadataSegments.Sum(segment => segment.Pairs.Count)),
                ParticleModels = scene.ParticleModels.Count, ParticleElements = scene.ParticleModels.Sum(x => x.Elements.Count),
                ParticleRuntimeTextureArchive = ParticleModelAsset.RuntimeTextureArchive,
                ParticleRuntimeTextureName = ParticleModelAsset.RuntimeTextureName,
                ParticleRuntimeTextureAssetId = ParticleModelAsset.RuntimeTextureAssetId,
                ParticleRuntimeBlendSelector = ParticleModelAsset.RuntimeBlendSelector,
                ParticleRuntimeGsAlphaRegister = $"0x{ParticleModelAsset.RuntimeGsAlphaRegister:X2}",
                ParticleRuntimeBlendEquation = ParticleModelAsset.RuntimeBlendEquation,
                ParticleEmitters = scene.ParticleEmitters.Count,
                ParticleEmittersRuntimeConsumingSerializedRadius = scene.ParticleEmitters.Count(x =>
                    Convert.ToBoolean(x.Properties["RuntimeConsumesSerializedBoundingRadius"])),
                ParticleEmitterRuntimeBoundingRadiusUse = ParticleEmitterAsset.RuntimeBoundingRadiusUse,
                Lights = scene.Lights.Count,
                LightPlaceholders = scene.Lights.Count(x => x.IsPlaceholder),
                LightsByKind = scene.Lights.GroupBy(x => x.Kind).OrderBy(x => x.Key)
                    .Select(x => new { Kind = x.Key, Name = Ssx3EffectDecoder.LightKindName(x.Key), Count = x.Count() }).ToArray(),
                LightFlagValues = scene.Lights.GroupBy(x => x.Flags).OrderBy(x => x.Key)
                    .Select(x => new { Value = $"0x{x.Key:X8}", Count = x.Count() }).ToArray(),
                LightDistanceFalloffExponents = scene.Lights.GroupBy(x => x.DistanceFalloffExponent).OrderBy(x => x.Key)
                    .Select(x => new { Exponent = x.Key, Count = x.Count() }).ToArray(),
                SpotAngularFalloffExponents = scene.Lights.Where(x => x.Kind == 1)
                    .GroupBy(x => x.AngularFalloffExponent).OrderBy(x => x.Key)
                    .Select(x => new { Exponent = x.Key, Count = x.Count() }).ToArray(),
                SpotConeOrderViolations = scene.Lights.Count(x => x.Kind == 1
                    && x.SpotInnerConeCosine < x.SpotOuterConeCosine),
                Halos = scene.Halos.Count,
                HaloSerializedCollectionPointerTokens = scene.Halos.Select(x => x.SerializedCollectionPointerToken).Distinct().Count(),
                HaloSerializedEntryTableBasePointerTokens = scene.Halos.Select(x => x.SerializedEntryTableBasePointerToken).Distinct().Count(),
                HaloPointerTokenSequenceViolations = scene.Halos.GroupBy(x => x.TrackId).Count(group =>
                    group.Select(x => x.SerializedCollectionPointerToken).Distinct().Count() != 1
                    || group.Select(x => x.SerializedEntryTableBasePointerToken).Distinct().Count() != 1),
                NisReferenceTables = scene.NisReferenceTables.Count,
                NisReferences = scene.NisReferenceTables.Sum(table => table.Slots.Count(slot => slot.IsPopulated)),
                NisObservedRoleBindings = scene.NisReferenceTables.Sum(table => table.Slots.Count(slot => slot.IsPopulated && slot.ObservedRole is not null)),
                NisRuntimeAddressableSlots = scene.NisReferenceTables.Sum(table => table.Slots.Count(slot => slot.IsRuntimeAddressable)),
                NisMissingSlots = scene.NisReferenceTables.Sum(table => table.Slots.Count(slot => !slot.IsPopulated)),
                NavigationMarkers = scene.NavigationMarkers.Count,
                CameraTriggerTables = scene.CameraTriggerTables.Count, Triggers = scene.Triggers.Count,
                CameraTriggerRecordBytes = scene.CameraTriggerTables.Sum(x => x.Records.Sum(record => record.SerializedSize)),
                CameraTriggerFillWords = scene.CameraTriggerTables.Sum(x => x.FillWordCount),
                CameraTriggerEllipseVolumes = scene.CameraTriggerTables.Sum(x => x.Records.Count(record => record.Shape.Kind == 0)),
                CameraTriggerBoxVolumes = scene.CameraTriggerTables.Sum(x => x.Records.Count(record => record.Shape.Kind == 1)),
                CameraTriggerRuntimeCenterContainmentViolations = scene.CameraTriggerTables.Sum(x => x.Records.Count(record =>
                    !record.Shape.ContainsRuntimePointSsx(Ssx3Coordinates.ToSsx3(record.Shape.Center)))),
                CameraTriggerBoxNonzeroRetailIgnoredRotationZ = scene.CameraTriggerTables.Sum(x => x.Records.Count(record =>
                    record.Shape.Kind == 1 && record.Shape.RotationRadiansSsx.Z != 0)),
                CameraTriggerEllipseInverseTransform = CameraTriggerShape.RuntimeEllipseInverseTransform,
                CameraTriggerBoxInverseTransform = CameraTriggerShape.RuntimeBoxInverseTransform,
                CameraTriggerRuntimeActionDispatchFunction = $"0x{CameraTriggerAction.RuntimeDispatchFunction:X8}",
                CameraTriggerRuntimeControllerSelectFunction = $"0x{CameraTriggerAction.RuntimeControllerSelectFunction:X8}",
                CameraTriggerRuntimeCreateBlendFunction = $"0x{CameraTriggerAction.RuntimeCreateBlendFunction:X8}",
                CameraTriggerRuntimeBlendFractionEquation = CameraTriggerAction.RuntimeBlendFractionEquation,
                CameraTriggerImmediateActionBlends = scene.CameraTriggerTables.SelectMany(x => x.Records)
                    .SelectMany(record => new[] { record.Action0, record.Action1 })
                    .Count(action => action.Kind != 3 && action.BlendDurationSeconds == 0),
                CameraTriggerSwitchCameraMappings = scene.CameraTriggerTables.SelectMany(x => x.Records)
                    .SelectMany(record => new[] { record.Action0, record.Action1 }).Where(action => action.Kind == 0)
                    .GroupBy(action => action.Value).OrderBy(group => group.Key)
                    .Select(group => new { SwitchCode = group.Key,
                        RuntimeCameraAlgorithmId = group.First().RuntimeCameraAlgorithmId, Count = group.Count() }).ToArray(),
                CameraTriggerBoundedCameraAlgorithmId = CameraTriggerAction.RuntimeBoundedCameraAlgorithmId,
                CameraTriggerBoundedCameraConstructorFunction = $"0x{CameraTriggerAction.RuntimeBoundedCameraConstructorFunction:X8}",
                CameraTriggerBoundedCameraInitializeFunction = $"0x{CameraTriggerAction.RuntimeBoundedCameraInitializeFunction:X8}",
                CameraTriggerBoundedCameraUpdateFunction = $"0x{CameraTriggerAction.RuntimeBoundedCameraUpdateFunction:X8}",
                CameraTriggerBoundedFocusEquation = CameraTriggerAction.RuntimeBoundedFocusEquation,
                CameraTriggerBoundedPitchEquation = CameraTriggerAction.RuntimeBoundedPitchEquation,
                CameraTriggerBoundedCameraPositionEquation = CameraTriggerAction.RuntimeBoundedCameraPositionEquation,
                CameraTriggerBoundedFieldOfViewClampEquation = CameraTriggerAction.RuntimeBoundedFieldOfViewClampEquation,
                CameraTriggerBoundedParameterRanges = scene.CameraTriggerTables.SelectMany(x => x.Records)
                    .SelectMany(record => new[] { record.Action0, record.Action1 }).Where(action => action.Kind == 1)
                    .Aggregate(new
                    {
                        Count = 0, MinDistance = float.PositiveInfinity, MaxDistance = float.NegativeInfinity,
                        MinFieldOfViewRadians = float.PositiveInfinity, MaxFieldOfViewRadians = float.NegativeInfinity,
                        MinVerticalTargetOffset = float.PositiveInfinity, MaxVerticalTargetOffset = float.NegativeInfinity,
                        MinPitchOffsetDegrees = float.PositiveInfinity, MaxPitchOffsetDegrees = float.NegativeInfinity,
                        MinForwardTargetOffset = float.PositiveInfinity, MaxForwardTargetOffset = float.NegativeInfinity
                    }, (range, action) => new
                    {
                        Count = range.Count + 1,
                        MinDistance = MathF.Min(range.MinDistance, action.BoundedCameraDistance!.Value),
                        MaxDistance = MathF.Max(range.MaxDistance, action.BoundedCameraDistance!.Value),
                        MinFieldOfViewRadians = MathF.Min(range.MinFieldOfViewRadians, action.BoundedFieldOfViewRadians!.Value),
                        MaxFieldOfViewRadians = MathF.Max(range.MaxFieldOfViewRadians, action.BoundedFieldOfViewRadians!.Value),
                        MinVerticalTargetOffset = MathF.Min(range.MinVerticalTargetOffset, action.BoundedVerticalTargetOffset!.Value),
                        MaxVerticalTargetOffset = MathF.Max(range.MaxVerticalTargetOffset, action.BoundedVerticalTargetOffset!.Value),
                        MinPitchOffsetDegrees = MathF.Min(range.MinPitchOffsetDegrees, action.BoundedPitchOffsetDegrees!.Value),
                        MaxPitchOffsetDegrees = MathF.Max(range.MaxPitchOffsetDegrees, action.BoundedPitchOffsetDegrees!.Value),
                        MinForwardTargetOffset = MathF.Min(range.MinForwardTargetOffset, action.BoundedForwardTargetOffset!.Value),
                        MaxForwardTargetOffset = MathF.Max(range.MaxForwardTargetOffset, action.BoundedForwardTargetOffset!.Value)
                    }),
                CameraTriggerSplineCameraAlgorithmId = CameraTriggerAction.RuntimeSplineCameraAlgorithmId,
                CameraTriggerSplineCameraObjectAlgorithmId = CameraTriggerAction.RuntimeSplineCameraObjectAlgorithmId,
                CameraTriggerSplineCameraConstructorFunction = $"0x{CameraTriggerAction.RuntimeSplineCameraConstructorFunction:X8}",
                CameraTriggerSplineCameraMotionFunction = $"0x{CameraTriggerAction.RuntimeSplineCameraMotionFunction:X8}",
                CameraTriggerSplineCameraUpdateFunction = $"0x{CameraTriggerAction.RuntimeSplineCameraUpdateFunction:X8}",
                CameraTriggerSplineControlTimesEquation = CameraTriggerAction.RuntimeSplineControlTimesEquation,
                CameraTriggerSplineApproximateSpeedEquation = CameraTriggerAction.RuntimeSplineApproximateSpeedEquation,
                CameraTriggerSplineParameterAdvanceEquation = CameraTriggerAction.RuntimeSplineParameterAdvanceEquation,
                CameraTriggerSplineCameraPositionEquation = CameraTriggerAction.RuntimeSplineCameraPositionEquation,
                CameraTriggerSplineFocusEquation = CameraTriggerAction.RuntimeSplineFocusEquation,
                CameraTriggerSplineParameters = scene.CameraTriggerTables.SelectMany(x => x.Records)
                    .SelectMany(record => new[] { record.Action0, record.Action1 }).Where(action => action.Kind == 2)
                    .Select(action => new
                    {
                        action.SplineFieldOfViewRadians, action.SplineForwardTargetOffset,
                        action.SplineDurationSeconds, action.SplineVerticalTargetOffset
                    }).ToArray(),
                VisibilityCurtains = scene.VisibilityCurtains.Count,
                VisibilityCurtainCorners = scene.VisibilityCurtains.Sum(x => x.CornersSsx.Count),
                VisibilityCurtainRuntimeScratchBytes = scene.VisibilityCurtains.Count * VisibilityCurtain.RuntimeScratchSize,
                VisibilityCurtainCandidateMetric = "squared SSX-space viewer distance to bounding-sphere center (XYZ only)",
                VisibilityCurtainCandidateSort = "ascending; nearest two eligible curtains",
                VisibilityCurtainCandidateScoreChecks = scene.VisibilityCurtains.Count,
                VisibilityCurtainCandidateScoreViolations = scene.VisibilityCurtains.Count(x =>
                    x.RuntimeCandidateScore(x.BoundingSphereCenterSsx) != 0
                    || MathF.Abs(MathF.Sqrt(x.CornersSsx.Max(x.RuntimeCandidateScore)) - x.BoundingSphereSsx.W) > 0.1f),
                VisibilityCurtainWarpedQuadrilaterals = scene.VisibilityCurtains.Count(x =>
                    MathF.Abs(Vector3.Dot(new Vector3(x.PlaneSsx.X, x.PlaneSsx.Y, x.PlaneSsx.Z), x.CornersSsx[3]) + x.PlaneSsx.W) > 0.1f),
                PreservedUnsupportedResources = scene.UnknownSections.Count, Errors = errors, Issues = issues });
        }
        var allGroups = sdb.Areas.SelectMany(area => Enumerable.Range(area.FirstGroup, area.GroupCount)).ToHashSet();
        var rawType2 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 2);
        var rawType2ResourceById = rawType2.GroupBy(resource => Key(resource.TrackId, resource.ResourceId))
            .ToDictionary(group => group.Key, group => group.First());
        var rawType2ById = rawType2.GroupBy(resource => Key(resource.TrackId, resource.ResourceId))
            .ToDictionary(group => group.Key, group => group.First().Payload);
        var rawType0 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 0);
        var rawMaterialById = rawType0.GroupBy(resource => Key(resource.TrackId, resource.ResourceId))
            .ToDictionary(group => group.Key, group => group.First());
        var rawType9 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 9);
        var textureGroupsByResource = rawType9.GroupBy(resource => resource.ResourceId)
            .ToDictionary(group => group.Key, group => group.Select(resource => resource.GroupIndex).ToHashSet());
        var rawType3 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 3);
        var decodedInstanceDma = rawType3.Select(resource => Ssx3InstanceDmaDecoder.Decode(resource.Payload,
            new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 3 DMA/VIF extension",
                resource.GroupIndex, SupportConfidence.Medium))).ToArray();
        var modelVifCache = new Dictionary<(long ModelKey, uint Address, int QuadwordCount),
            (IReadOnlyList<Ps2VifCommand> Commands, bool Complete)>();
        var instanceModelVifPairs = new List<(InstanceDmaSourceBlock Source,
            IReadOnlyList<Ps2VifCommand> ModelCommands, bool ModelComplete)>();
        var instanceVifSourcesWithoutPrecedingModel = 0;
        var modelVifInvalidRanges = 0;
        for (var instanceIndex = 0; instanceIndex < rawType3.Count; instanceIndex++)
        {
            var resource = rawType3[instanceIndex];
            var packedModel = BinaryPrimitives.ReadUInt32LittleEndian(resource.Payload.AsSpan(0x80, 4));
            var modelKey = Key((int)(packedModel & 0xff), (int)(packedModel >> 8));
            if (!rawType2ResourceById.TryGetValue(modelKey, out var model) || model.Payload.Length < 0x28)
                continue;
            var modelDataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.Payload.AsSpan(0x24, 4)));
            var sourceIndex = 0;
            foreach (var program in decodedInstanceDma[instanceIndex].Programs)
            {
                for (var relocationIndex = 0; relocationIndex < program.Relocations.Count; relocationIndex++)
                {
                    if (program.Relocations[relocationIndex].Target != DmaRelocationTarget.InstanceExtension)
                        continue;
                    var sourceBlock = decodedInstanceDma[instanceIndex].SourceBlocks[sourceIndex++];
                    if (relocationIndex == 0
                        || program.Relocations[relocationIndex - 1].Target != DmaRelocationTarget.ModelData)
                    {
                        instanceVifSourcesWithoutPrecedingModel++;
                        continue;
                    }
                    var modelTag = program.Relocations[relocationIndex - 1].Tag;
                    var cacheKey = (modelKey, modelTag.Address, modelTag.QuadwordCount);
                    if (!modelVifCache.TryGetValue(cacheKey, out var modelVif))
                    {
                        var offset = checked(modelDataOffset + (int)modelTag.Address);
                        var byteCount = checked(modelTag.QuadwordCount * 16);
                        if (offset < modelDataOffset || offset > model.Payload.Length - byteCount)
                        {
                            modelVifInvalidRanges++;
                            continue;
                        }
                        modelVif = Ssx3VifDecoder.Decode(model.Payload.AsSpan(offset, byteCount), offset);
                        modelVifCache.Add(cacheKey, modelVif);
                    }
                    instanceModelVifPairs.Add((sourceBlock, modelVif.Commands, modelVif.Complete));
                }
            }
        }
        var packedVifValues = new List<ushort>(decodedInstanceDma.Sum(program => program.SourceBlocks
            .SelectMany(block => block.VifCommands).Where(command => command.Name == "UNPACK_V4_5")
            .Sum(command => command.ElementCount)));
        foreach (var block in decodedInstanceDma.SelectMany(program => program.SourceBlocks))
        foreach (var command in block.VifCommands.Where(command => command.Name == "UNPACK_V4_5"))
        {
            var payloadOffset = command.PayloadOffset - block.Offset;
            for (var index = 0; index < command.ElementCount; index++)
                packedVifValues.Add(BinaryPrimitives.ReadUInt16LittleEndian(block.Data.AsSpan(payloadOffset + index * 2, 2)));
        }
        var instanceDmaPrograms = rawType3.Select(resource =>
        {
            var packedModel = BinaryPrimitives.ReadUInt32LittleEndian(resource.Payload.AsSpan(0x80, 4));
            rawType2ById.TryGetValue(Key((int)(packedModel & 0xff), (int)(packedModel >> 8)), out var model);
            return SurveyInstanceDma(resource.Payload, model);
        }).ToArray();
        var referencedInstanceModelObjectCounts = rawType3.Select(resource =>
        {
            var packedModel = BinaryPrimitives.ReadUInt32LittleEndian(resource.Payload.AsSpan(0x80, 4));
            return rawType2ById.TryGetValue(Key((int)(packedModel & 0xff), (int)(packedModel >> 8)), out var model)
                ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(4, 4))) : -1;
        }).ToArray();
        var instanceNames = Ssx3ResourceNames.TryLoad(Path.ChangeExtension(ssbPath, ".phm"),
            Path.ChangeExtension(ssbPath, ".psm"), new DiagnosticBag());
        static Vector3 InstanceVector3(Ssx3RawResource resource, int offset) => new(
            BitConverter.ToSingle(resource.Payload, offset), BitConverter.ToSingle(resource.Payload, offset + 4),
            BitConverter.ToSingle(resource.Payload, offset + 8));
        var instanceBoundingSpheres = rawType3.Select(resource =>
        {
            var sphereCenter = InstanceVector3(resource, 0x50);
            var minimum = InstanceVector3(resource, 0x60); var maximum = InstanceVector3(resource, 0x6c);
            var radius = BitConverter.ToSingle(resource.Payload, 0x5c);
            var boxCenter = (minimum + maximum) * 0.5f;
            return new
            {
                FinitePositive = float.IsFinite(sphereCenter.X) && float.IsFinite(sphereCenter.Y)
                    && float.IsFinite(sphereCenter.Z) && float.IsFinite(radius) && radius >= 0,
                CenterBoxDistance = Vector3.Distance(sphereCenter, boxCenter),
                CenterInsideBounds = sphereCenter.X >= minimum.X && sphereCenter.X <= maximum.X
                    && sphereCenter.Y >= minimum.Y && sphereCenter.Y <= maximum.Y
                    && sphereCenter.Z >= minimum.Z && sphereCenter.Z <= maximum.Z
            };
        }).ToArray();
        static uint InstanceWord(Ssx3RawResource resource, int offset) => BitConverter.ToUInt32(resource.Payload, offset);
        static short InstanceInt16(Ssx3RawResource resource, int offset) => BitConverter.ToInt16(resource.Payload, offset);
        static ushort InstanceUInt16(Ssx3RawResource resource, int offset) => BitConverter.ToUInt16(resource.Payload, offset);
        var instanceModelKeys = rawType3.Select(resource =>
        {
            var packedModel = InstanceWord(resource, 0x80);
            return Key((int)(packedModel & 0xff), (int)(packedModel >> 8));
        }).ToArray();
        var instancePackedPayloadProfiles = decodedInstanceDma.Select(program =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var values = 0;
            var highBitClear = 0;
            foreach (var block in program.SourceBlocks)
            foreach (var command in block.VifCommands.Where(command => command.Name == "UNPACK_V4_5"))
            {
                var payloadOffset = command.PayloadOffset - block.Offset;
                var payload = block.Data.AsSpan(payloadOffset, command.PayloadSize);
                hash.AppendData(payload);
                values += command.ElementCount;
                for (var index = 0; index < command.ElementCount; index++)
                    if ((BinaryPrimitives.ReadUInt16LittleEndian(payload[(index * 2)..]) & 0x8000) == 0)
                        highBitClear++;
            }
            return (Hash: Convert.ToHexString(hash.GetHashAndReset()), Values: values, HighBitClear: highBitClear);
        }).ToArray();
        var packedPayloadsByModel = Enumerable.Range(0, rawType3.Count).GroupBy(index => instanceModelKeys[index]).ToArray();
        var windDeformationByModel = Enumerable.Range(0, rawType3.Count).GroupBy(index => instanceModelKeys[index])
            .ToDictionary(group => group.Key, group => group.Select(index => InstanceUInt16(rawType3[index], 0x92)).Distinct().ToArray());
        var instanceWindDeformationStructuralProfiles = Enumerable.Range(0, rawType3.Count)
            .GroupBy(index => InstanceUInt16(rawType3[index], 0x92)).OrderBy(group => group.Key)
            .Select(group => new
            {
                Value = $"0x{group.Key:X8}", Assets = group.Count(),
                DistinctModels = group.Select(index => instanceModelKeys[index]).Distinct().Count(),
                ModelsSharedAcrossStates = group.Select(index => instanceModelKeys[index]).Distinct()
                    .Count(model => windDeformationByModel[model].Length > 1),
                MinimumModelObjects = group.Min(index => referencedInstanceModelObjectCounts[index]),
                MaximumModelObjects = group.Max(index => referencedInstanceModelObjectCounts[index]),
                MaterialReferences = group.Sum(index =>
                {
                    var model = rawType2ResourceById[instanceModelKeys[index]].Payload;
                    return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(0x28, 4)));
                }),
                DmaPrograms = group.Sum(index => instanceDmaPrograms[index].ExpectedPrograms),
                DmaSprReferences = group.Sum(index => instanceDmaPrograms[index].SprReferenceTags),
                DmaImmediateRewrites = group.Sum(index => instanceDmaPrograms[index].SprImmediateReturnRewrites),
                DmaExtendedRewrites = group.Sum(index => instanceDmaPrograms[index].SprExtendedRewrites),
                TerminalVifCode1Patterns = group.SelectMany(index => decodedInstanceDma[index].SourceBlocks)
                    .GroupBy(block => block.TerminalVifCode1).OrderBy(pattern => pattern.Key)
                    .Select(pattern => new { Value = $"0x{pattern.Key:X8}", Count = pattern.Count() }).ToArray(),
                ModelSamples = group.Select(index =>
                {
                    var packedModel = InstanceWord(rawType3[index], 0x80);
                    return instanceNames?.Find(2, (int)(packedModel & 0xff), (int)(packedModel >> 8));
                }).Where(name => name is not null).Distinct().Take(32).ToArray()
            }).ToArray();
        var instanceTextureSubChunkReferences = rawType3.SelectMany(resource =>
        {
            var packedModel = InstanceWord(resource, 0x80);
            if (!rawType2ResourceById.TryGetValue(Key((int)(packedModel & 0xff), (int)(packedModel >> 8)), out var model)
                || model.Payload.Length < 44) return [];
            var materialCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.Payload.AsSpan(0x28, 4)));
            if (materialCount < 0 || model.Payload.Length < 44 + materialCount * 4) return [];
            var textureSubChunk = checked((int)(InstanceWord(resource, 0x7c) >> 16 & 0xff));
            var references = new List<(bool MaterialResolved, bool TextureInPartition)>(materialCount);
            for (var i = 0; i < materialCount; i++)
            {
                var packedMaterial = BinaryPrimitives.ReadUInt32LittleEndian(model.Payload.AsSpan(44 + i * 4, 4));
                var resolved = rawMaterialById.TryGetValue(Key((int)(packedMaterial & 0xff), (int)(packedMaterial >> 8)), out var material);
                var textureId = resolved && material!.Payload.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(material.Payload) : -1;
                references.Add((resolved, textureId >= 0 && textureGroupsByResource.TryGetValue(textureId, out var groups)
                    && groups.Contains(textureSubChunk)));
            }
            return references;
        }).ToArray();
        var instanceExtensionRecords = rawType3.SelectMany(resource => resource.Payload.Skip(160).Chunk(16)).ToArray();
        var canonicalVifSequence = new[] { "STCYCL", "UNPACK_V4_5", "STMASK", "RuntimeMscalPlaceholder", "ITOP" };
        var uniqueInstanceCorpus = new
        {
            Assets = rawType3.Count,
            MinimumBytes = rawType3.Min(resource => resource.Payload.Length),
            MaximumBytes = rawType3.Max(resource => resource.Payload.Length),
            PayloadAlignmentViolations = rawType3.Count(resource => resource.Payload.Length < 160 || resource.Payload.Length % 16 != 0),
            ResolvedModelReferences = referencedInstanceModelObjectCounts.Count(count => count >= 0),
            MaximumReferencedModelObjects = referencedInstanceModelObjectCounts.Max(),
            ReferencedModelsExceedingRuntimeCollisionSlots = referencedInstanceModelObjectCounts.Count(count => count > 24),
            RuntimeCollisionMetadataSlots = 24,
            RuntimeCollisionProfileCount = 1,
            RuntimeCollisionProfileMutable = false,
            RuntimeCollisionProfileHeader = "00000000 00030000 FFFFFFFF FFFFFFFF",
            RuntimeDefaultCollisionAttribute = -1,
            RuntimeDefaultCollisionAttributeResolvedIndex = (ushort)SsxSurfaceType.PackedSnow,
            RuntimeCollisionAttributeMaximumKnownIndex = (ushort)SsxSurfaceType.WipeoutRock,
            ZeroPrefixViolations = rawType3.Count(resource => resource.Payload.AsSpan(0, 16).IndexOfAnyExcept((byte)0) >= 0),
            SelfReferenceViolations = rawType3.Count(resource => InstanceWord(resource, 0x78)
                != ((uint)resource.ResourceId << 8 | (uint)resource.TrackId)),
            TrackTextureSubChunkReservedByteViolations = rawType3.Count(resource => (InstanceWord(resource, 0x7c) & 0xff0000ff) != 0),
            TrackTextureSubChunkTrackByteViolations = rawType3.Count(resource => ((InstanceWord(resource, 0x7c) >> 8) & 0xff) != resource.TrackId),
            TextureSubChunkIdOutOfRangeViolations = rawType3.Count(resource =>
                (InstanceWord(resource, 0x7c) >> 16 & 0xff) >= sdb.SubChunkCount),
            TextureSubChunkDescriptorIdentityViolations = rawType3.Count(resource =>
            {
                var id = checked((int)(InstanceWord(resource, 0x7c) >> 16 & 0xff));
                return id >= sdb.SubChunks.Count || sdb.SubChunks[id].SubChunkId != id;
            }),
            TextureSubChunksWithoutDeclaredType9Textures = rawType3.Count(resource =>
            {
                var id = checked((int)(InstanceWord(resource, 0x7c) >> 16 & 0xff));
                return id >= sdb.SubChunks.Count || sdb.SubChunks[id].DeclaredType9TextureCount == 0;
            }),
            SdbSubChunkIdentityViolations = sdb.SubChunks.Count(descriptor => descriptor.SubChunkId != descriptor.Index),
            SdbSubChunkReservedWordViolations = sdb.SubChunks.Sum(descriptor => descriptor.ReservedWords.Count(word => word != 0)),
            TextureSubChunkMaterialReferences = instanceTextureSubChunkReferences.Length,
            TextureSubChunkUnresolvedMaterialReferences = instanceTextureSubChunkReferences.Count(reference => !reference.MaterialResolved),
            TextureSubChunkBankMismatchViolations = instanceTextureSubChunkReferences.Count(reference => !reference.TextureInPartition),
            SerializedRuntimeScratchViolations = rawType3.Count(resource =>
                InstanceWord(resource, 0x88) != 0 || InstanceWord(resource, 0x8c) != 0
                || InstanceWord(resource, 0x94) != 0 || InstanceWord(resource, 0x9c) != 0),
            UnusedSentinelHalfword90Violations = rawType3.Count(resource => InstanceInt16(resource, 0x90) != -1),
            WindDeformationValueViolations = rawType3.Count(resource => InstanceUInt16(resource, 0x92) > 1),
            InvalidBoundingSphereValues = instanceBoundingSpheres.Count(value => !value.FinitePositive),
            BoundingSphereCentersOutsideWorldBounds = instanceBoundingSpheres.Count(value => !value.CenterInsideBounds),
            BoundingSphereCentersMatchingAabbCenter = instanceBoundingSpheres.Count(value => value.CenterBoxDistance <= 0.01f),
            TrackTextureSubChunkWordPatterns = rawType3.GroupBy(resource => InstanceWord(resource, 0x7c))
                .OrderByDescending(group => group.Count()).Select(group => new
                {
                    Value = $"0x{group.Key:X8}", Count = group.Count(),
                    TrackIds = group.Select(resource => resource.TrackId).Distinct().Order().ToArray()
                }).ToArray(),
            WindDeformationPatterns = rawType3.GroupBy(resource => InstanceUInt16(resource, 0x92))
                .OrderBy(group => group.Key).Select(group => new
                {
                    Enabled = group.Key != 0, RawValue = group.Key, Count = group.Count(),
                    Categories = group.Select(resource => instanceNames?.Find(1, resource.TrackId, resource.ResourceId))
                        .Where(name => name is not null).Select(name => PropClassifier.Classify(name!).Category)
                        .GroupBy(category => category).OrderByDescending(category => category.Count())
                        .Select(category => new { Name = category.Key.ToString(), Count = category.Count() }).ToArray(),
                    Samples = group.Select(resource => instanceNames?.Find(1, resource.TrackId, resource.ResourceId))
                        .Where(name => name is not null).Distinct().Take(32).ToArray()
                }).ToArray(),
            WindDeformationStructuralProfiles = instanceWindDeformationStructuralProfiles,
            ExtensionRecords = instanceExtensionRecords.Length,
            DmaExpectedPrograms = instanceDmaPrograms.Sum(program => program.ExpectedPrograms),
            DmaParsedPrograms = instanceDmaPrograms.Sum(program => program.ParsedPrograms),
            DmaDecodedPrograms = decodedInstanceDma.Sum(program => program.Programs.Count),
            DmaDecodedSourceBlocks = decodedInstanceDma.Sum(program => program.SourceBlocks.Count),
            DmaVifCommands = decodedInstanceDma.Sum(program => program.SourceBlocks.Sum(block => block.VifCommands.Count)),
            DmaVifIncompleteSourceBlocks = decodedInstanceDma.Sum(program => program.SourceBlocks.Count(block => !block.VifDecodeComplete)),
            DmaVifUnknownCommands = decodedInstanceDma.Sum(program => program.SourceBlocks.Sum(block =>
                block.VifCommands.Count(command => command.Name.StartsWith("VIF_", StringComparison.Ordinal)))),
            DmaVifCanonicalShapeViolations = decodedInstanceDma.SelectMany(program => program.SourceBlocks).Count(block =>
                !block.VifCommands.Where(command => command.Name != "NOP").Select(command => command.Name)
                    .SequenceEqual(canonicalVifSequence)),
            DmaVifPackedVectorElements = decodedInstanceDma.SelectMany(program => program.SourceBlocks)
                .SelectMany(block => block.VifCommands).Where(command => command.Name == "UNPACK_V4_5")
                .Sum(command => command.ElementCount),
            DmaVifPackedDistinctValues = packedVifValues.Distinct().Count(),
            DmaVifPackedHighBitClearValues = packedVifValues.Count(value => (value & 0x8000) == 0),
            DmaVifPackedHighBitClearAssets = instancePackedPayloadProfiles.Count(profile => profile.HighBitClear != 0),
            DmaVifPackedHighBitClearSamples = Enumerable.Range(0, rawType3.Count)
                .Where(index => instancePackedPayloadProfiles[index].HighBitClear != 0)
                .Select(index => new
                {
                    Name = instanceNames?.Find(1, rawType3[index].TrackId, rawType3[index].ResourceId),
                    instancePackedPayloadProfiles[index].Values,
                    instancePackedPayloadProfiles[index].HighBitClear
                }).Take(32).ToArray(),
            DmaVifModelsWithMultipleInstances = packedPayloadsByModel.Count(group => group.Count() > 1),
            DmaVifModelsWithInstanceVariantPackedPayloads = packedPayloadsByModel.Count(group =>
                group.Count() > 1 && group.Select(index => instancePackedPayloadProfiles[index].Hash).Distinct().Count() > 1),
            DmaVifInstancesUsingModelsWithVariantPackedPayloads = packedPayloadsByModel.Where(group =>
                    group.Count() > 1 && group.Select(index => instancePackedPayloadProfiles[index].Hash).Distinct().Count() > 1)
                .Sum(group => group.Count()),
            DmaVifPackedLaneProfiles = Enumerable.Range(0, 4).Select(lane =>
            {
                var values = packedVifValues.Select(value => lane == 3 ? value >> 15 : value >> (lane * 5) & 31).ToArray();
                return new { Lane = lane, Minimum = values.Min(), Maximum = values.Max(), Distinct = values.Distinct().Count() };
            }).ToArray(),
            DmaVifPackedValueProfiles = packedVifValues.GroupBy(value => value).OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key).Take(32).Select(group => new
                {
                    Value = $"0x{group.Key:X4}", Count = group.Count(),
                    X = group.Key & 31, Y = group.Key >> 5 & 31, Z = group.Key >> 10 & 31, W = group.Key >> 15
                }).ToArray(),
            DmaVifModelSourcePairs = instanceModelVifPairs.Count,
            DmaVifSourcesWithoutPrecedingModel = instanceVifSourcesWithoutPrecedingModel,
            DmaVifModelInvalidRanges = modelVifInvalidRanges,
            DmaVifIncompleteModelBlocks = instanceModelVifPairs.Count(pair => !pair.ModelComplete),
            DmaVifSourceCountWithoutMatchingModelUnpack = instanceModelVifPairs.Count(pair =>
            {
                var sourceUnpack = pair.Source.VifCommands.Single(command => command.Name == "UNPACK_V4_5");
                return !pair.ModelCommands.Any(command => command.IsUnpack && command.ElementCount == sourceUnpack.ElementCount);
            }),
            DmaVifSourceDestinationWithoutContiguousModelArray = instanceModelVifPairs.Count(pair =>
            {
                var sourceUnpack = pair.Source.VifCommands.Single(command => command.Name == "UNPACK_V4_5");
                return !pair.ModelCommands.Any(command => command.IsUnpack
                    && command.ElementCount == sourceUnpack.ElementCount
                    && command.UnpackDestinationAddress + command.ElementCount == sourceUnpack.UnpackDestinationAddress);
            }),
            DmaVifCommandProfiles = decodedInstanceDma.SelectMany(program => program.SourceBlocks)
                .SelectMany(block => block.VifCommands).GroupBy(command => new { command.Command, command.Name })
                .OrderBy(group => group.Key.Command).Select(group => new { Value = $"0x{group.Key.Command:X2}", group.Key.Name,
                    Count = group.Count(), MinimumPayloadBytes = group.Min(command => command.PayloadSize),
                    MaximumPayloadBytes = group.Max(command => command.PayloadSize),
                    MinimumElements = group.Any(command => command.IsUnpack)
                        ? group.Where(command => command.IsUnpack).Min(command => (int?)command.ElementCount) : null,
                    MaximumElements = group.Any(command => command.IsUnpack)
                        ? group.Where(command => command.IsUnpack).Max(command => (int?)command.ElementCount) : null,
                    RawPatterns = group.Select(command => command.Raw).Distinct().Count() }).ToArray(),
            DmaModelReferenceTags = instanceDmaPrograms.Sum(program => program.ModelReferenceTags),
            DmaSourceReferenceTags = instanceDmaPrograms.Sum(program => program.SourceReferenceTags),
            DmaStructuralRecords = instanceDmaPrograms.Sum(program => program.StructuralRecordCount),
            DmaReturnTags = instanceDmaPrograms.Sum(program => program.ReturnTags),
            DmaSourceQuadwords = instanceDmaPrograms.Sum(program => program.SourceQuadwords),
            DmaSprReferenceTags = instanceDmaPrograms.Sum(program => program.SprReferenceTags),
            DmaSprImmediateReturnRewrites = instanceDmaPrograms.Sum(program => program.SprImmediateReturnRewrites),
            DmaSprExtendedRewrites = instanceDmaPrograms.Sum(program => program.SprExtendedRewrites),
            DmaInvalidTags = instanceDmaPrograms.Sum(program => program.InvalidTagCount),
            DmaInvalidSourceTags = instanceDmaPrograms.Sum(program => program.InvalidSourceTagCount),
            DmaInvalidSourceRanges = instanceDmaPrograms.Sum(program => program.InvalidSourceRangeCount),
            DmaInvalidModelRanges = instanceDmaPrograms.Sum(program => program.InvalidModelRangeCount),
            DmaMisalignedSourceAddresses = instanceDmaPrograms.Sum(program => program.MisalignedSourceAddressCount),
            DmaTerminalMscalPlaceholders = instanceDmaPrograms.Sum(program => program.TerminalMscalPlaceholderCount),
            DmaInvalidTerminalMscalPlaceholders = instanceDmaPrograms.Sum(program => program.InvalidTerminalMscalPlaceholderCount),
            DmaStructuralWorkspaceViolations = instanceDmaPrograms.Sum(program => program.StructuralWorkspaceViolations),
            DmaUnreferencedPayloadBytes = instanceDmaPrograms.Sum(program => program.UnreferencedPayloadBytes),
            DmaTerminalVifCode1Patterns = decodedInstanceDma.SelectMany(program => program.SourceBlocks)
                .GroupBy(block => block.TerminalVifCode1).OrderByDescending(group => group.Count())
                .Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray(),
            DmaProgramCounts = instanceDmaPrograms.GroupBy(program => program.ExpectedPrograms).OrderBy(group => group.Key)
                .Select(group => new { Programs = group.Key, Count = group.Count() }).ToArray(),
            DmaInvalidSamples = rawType3.Zip(instanceDmaPrograms)
                .Where(item => item.Second.InvalidTagCount != 0 || item.Second.InvalidSourceTagCount != 0
                    || item.Second.InvalidSourceRangeCount != 0 || item.Second.InvalidModelRangeCount != 0
                    || item.Second.ExpectedPrograms != item.Second.ParsedPrograms)
                .Take(32).Select(item => new { item.First.TrackId, item.First.ResourceId,
                    Bytes = item.First.Payload.Length, item.Second }).ToArray(),
            ExtensionOffsetWords = rawType3.GroupBy(resource => InstanceWord(resource, 0x98))
                .OrderByDescending(group => group.Count()).Take(32)
                .Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray(),
            FixedTailWordPatterns = Enumerable.Range(0, 7).Select(index => new
            {
                Index = index,
                Offset = $"0x{0x84 + index * 4:X2}",
                Values = rawType3.GroupBy(resource => InstanceWord(resource, 0x84 + index * 4))
                    .OrderByDescending(group => group.Count()).Take(16)
                    .Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray()
            }).ToArray(),
            ExtensionFirstWordHighNibbles = instanceExtensionRecords
                .GroupBy(record => BitConverter.ToUInt32(record, 0) >> 28).OrderBy(group => group.Key)
                .Select(group => new { Value = $"0x{group.Key:X}", Count = group.Count() }).ToArray(),
            ExtensionFirstWordHighBytes = instanceExtensionRecords
                .GroupBy(record => BitConverter.ToUInt32(record, 0) >> 24).OrderByDescending(group => group.Count())
                .Take(64).Select(group => new { Value = $"0x{group.Key:X2}", Count = group.Count() }).ToArray(),
            ExtensionFirstWordPatterns = instanceExtensionRecords
                .GroupBy(record => BitConverter.ToUInt32(record, 0)).OrderByDescending(group => group.Count())
                .Take(64).Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray(),
            FirstRecordPatterns = rawType3.GroupBy(resource => Convert.ToHexString(resource.Payload.AsSpan(160, 16)))
                .OrderByDescending(group => group.Count()).Take(32)
                .Select(group => new { Hex = group.Key, Count = group.Count() }).ToArray(),
            LastRecordPatterns = rawType3.GroupBy(resource => Convert.ToHexString(resource.Payload.AsSpan(resource.Payload.Length - 16, 16)))
                .OrderByDescending(group => group.Count()).Take(32)
                .Select(group => new { Hex = group.Key, Count = group.Count() }).ToArray()
        };
        var rawType6 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 6);
        var uniqueLights = rawType6.Select(resource => Ssx3EffectDecoder.DecodeLight(resource.Payload,
            new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 6", resource.GroupIndex,
                SupportConfidence.Medium), resource.TrackId, resource.ResourceId)).ToArray();
        var uniqueLightCorpus = new
        {
            Records = uniqueLights.Length,
            Kinds = uniqueLights.GroupBy(light => light.Kind).OrderBy(group => group.Key)
                .Select(group => new { Kind = group.Key, Name = Ssx3EffectDecoder.LightKindName(group.Key), Count = group.Count() }).ToArray(),
            FlagValues = uniqueLights.GroupBy(light => light.Flags).OrderBy(group => group.Key)
                .Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray(),
            RuntimeFilterFlag0x100Records = uniqueLights.Count(light => light.HasRuntimeFilterFlag0x100),
            RuntimeLoaderFunction = $"0x{LightAsset.RuntimeLoaderFunction:X8}",
            RuntimeAdmissionPredicate = $"0x{LightAsset.RuntimeAdmissionPredicate:X8}",
            RuntimeAdmissionEquation = "(Flags & 0x100) == 0 && Kind is Spot (1) or Point (2)",
            RuntimeAdmittedRecords = uniqueLights.Count(light => light.IsRuntimeAdmitted),
            RuntimeRejectedRecords = uniqueLights.Count(light => !light.IsRuntimeAdmitted),
            RuntimeRejectedDirectionalRecords = uniqueLights.Count(light => light.Kind == 0 && !light.IsRuntimeAdmitted),
            RuntimeRejectedAmbientRecords = uniqueLights.Count(light => light.Kind == 3 && !light.IsRuntimeAdmitted),
            RuntimeRejectedFlag0x100Records = uniqueLights.Count(light => light.HasRuntimeFilterFlag0x100),
            RuntimeInternalResourceType = LightAsset.RuntimeInternalResourceType,
            SpotRecords = uniqueLights.Count(light => light.Kind == 1),
            SpotConeOrderViolations = uniqueLights.Count(light => light.Kind == 1
                && light.SpotInnerConeCosine < light.SpotOuterConeCosine),
            DistanceFalloffExponents = uniqueLights.GroupBy(light => light.DistanceFalloffExponent).OrderBy(group => group.Key)
                .Select(group => new { Exponent = group.Key, Count = group.Count() }).ToArray(),
            SpotAngularFalloffExponents = uniqueLights.Where(light => light.Kind == 1)
                .GroupBy(light => light.AngularFalloffExponent).OrderBy(group => group.Key)
                .Select(group => new { Exponent = group.Key, Count = group.Count() }).ToArray(),
            TailMarker = $"0x{LightAsset.ExpectedTailMarker:X4}",
            TailMarkerViolations = uniqueLights.Count(light => light.TailMarker != LightAsset.ExpectedTailMarker),
            NearFalloffDistance = LightAsset.NearFalloffDistance,
            LongRangeFadeStart = LightAsset.LongRangeFadeStart,
            LongRangeFadeEnd = LightAsset.LongRangeFadeEnd,
            RuntimeSelectionFunction = "0x002F5D30",
            RuntimeRgbContributionFunction = "0x0038A6A8",
            SelectionWeightConsumedByRgbContribution = false,
            LayoutViolations = 0
        };
        var rawType7 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 7);
        var uniqueHalos = rawType7.Select(resource => Ssx3EffectDecoder.DecodeHalo(resource.Payload,
            new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 7", resource.GroupIndex,
                SupportConfidence.Medium), resource.TrackId, resource.ResourceId)).ToArray();
        var uniqueHaloCorpus = new
        {
            Records = uniqueHalos.Length,
            Tracks = uniqueHalos.Select(halo => halo.TrackId).Distinct().Count(),
            VisualModes = uniqueHalos.GroupBy(halo => halo.VisualModeCode).OrderBy(group => group.Key)
                .Select(group => new { VisualModeCode = $"0x{group.Key:X2}",
                    RuntimeOcclusionProbeScale = group.First().RuntimeOcclusionProbeScale,
                    RuntimeRenderScale = group.First().RuntimeRenderScale, Count = group.Count() }).ToArray(),
            SerializedCollectionPointerTokens = uniqueHalos.Select(halo => halo.SerializedCollectionPointerToken).Distinct().Count(),
            SerializedEntryTableBasePointerTokens = uniqueHalos.Select(halo => halo.SerializedEntryTableBasePointerToken).Distinct().Count(),
            PointerAlignmentViolations = uniqueHalos.Count(halo => (halo.SerializedCollectionPointerToken & 7) != 0
                || (halo.SerializedEntryPointerToken & 7) != 0),
            PointerTokenSequenceViolations = uniqueHalos.GroupBy(halo => halo.TrackId).Count(group =>
                group.Select(halo => halo.SerializedCollectionPointerToken).Distinct().Count() != 1
                || group.Select(halo => halo.SerializedEntryTableBasePointerToken).Distinct().Count() != 1),
            NoncontiguousResourceIdTracks = uniqueHalos.GroupBy(halo => halo.TrackId).Count(group =>
                !group.Select(halo => halo.ResourceId).OrderBy(id => id)
                    .SequenceEqual(Enumerable.Range(0, group.Select(halo => halo.ResourceId).Max() + 1))),
            SerializedEntryStride = HaloAsset.SerializedEntryStride,
            InvariantWord08 = $"0x{HaloAsset.InvariantWord08:X8}",
            ResourceLoaderFunction = "0x003ABA70",
            ResourceLoaderConsumedBoundsRange = "0x28..0x3F",
            RuntimeInternalResourceType = 8,
            RuntimeOctreeInsertionFunctions = new[] { "0x003284B8", "0x00328660" },
            RuntimeIntrusiveLinkOverwriteRange = "0x00..0x07",
            RuntimeVisibilityTraversalFunctions = new[] { "0x0022A4A8", "0x0022A770" },
            RuntimeHaloQueueCountOffset = "0x7BC0",
            RuntimeHaloQueueBaseOffset = "0x7BC4",
            RuntimeBatchFunction = "0x002E2FF8",
            RuntimeOcclusionProbeSetupFunction = "0x002E2B00",
            RuntimeOcclusionQueryPacketFunction = "0x002EC478",
            RuntimeOcclusionReadbackLoopRange = "0x002EC874..0x002EC99C",
            RuntimeVisibilityResultOffsets = new[] { "0x40", "0x44" },
            RuntimeVisibilityWritebackFunction = "0x002E3130",
            RuntimeFinalSubmissionFunction = "0x002E2868",
            RuntimeColorNormalizationRange = "0x002E2880..0x002E28F4",
            RuntimePs2ViewerSubmissionFunction = "0x003781A0",
            RuntimeSubmissionColorLayout = "float[4] { OcclusionAlpha, NormalizedR, NormalizedG, NormalizedB }",
            RuntimeGsColorScale = HaloAsset.RuntimeGsColorScale,
            RuntimePackedColorOrder = "RGBA",
            RuntimePackedStateSetupFunction = "0x002E3578",
            RuntimePs2StateCompilerRange = "0x00363F1C..0x00363F68",
            RuntimeBlendTableFunction = "0x00362478",
            RuntimeBlendTableAddress = "0x00491FB0",
            RuntimeBlendSelector = HaloAsset.RuntimeBlendSelector,
            RuntimeGsAlphaRegister = $"0x{HaloAsset.RuntimeGsAlphaRegister:X2}",
            RuntimeBlendEquation = HaloAsset.RuntimeBlendEquation,
            RuntimeBlendMode = HaloAsset.RuntimeBlendMode,
            RuntimeTextureArchive = "data/textures/effects.ssh",
            RuntimeTextureLoaderFunction = "0x002DBD10",
            RuntimeTextureHandleTableBaseOffset = "0x0F50",
            RuntimeModeTextures = new Dictionary<string, object>
            {
                ["0x10"] = new { EnumName = "SHALO", AssetId = "shal", EnumIndex = 53, HandleOffset = "global+0x1024" },
                ["0x20"] = new { EnumName = "MHALO", AssetId = "mhal", EnumIndex = 54, HandleOffset = "global+0x1028" },
                ["0x40"] = new { EnumName = "SHALO", AssetId = "shal", EnumIndex = 53, HandleOffset = "global+0x1024" }
            },
            DormantVisualMode40OcclusionProbeScale = 200f,
            DormantVisualMode40RenderScale = 350f,
            LayoutViolations = 0
        };
        var rawType21 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 21);
        var uniquePlanarRoutes = rawType21.Select(resource => Ssx3PlanarRouteDecoder.Decode(resource.Payload,
            new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 21", resource.GroupIndex,
                SupportConfidence.High), resource.TrackId, resource.ResourceId)).ToArray();
        var routeNormalAlignmentMaxError = uniquePlanarRoutes.SelectMany(route => route.Samples.Zip(route.Samples.Skip(1))
            .Select(pair =>
            {
                var travel = pair.Second.Position - pair.First.Position;
                return travel.LengthSquared() <= 0.0001f ? 0f
                    : MathF.Abs(Vector2.Dot(Vector2.Normalize(travel), pair.First.LateralNormal));
            })).DefaultIfEmpty().Max();
        var uniquePlanarRouteCorpus = new
        {
            Routes = uniquePlanarRoutes.Length,
            Samples = uniquePlanarRoutes.Sum(route => route.Samples.Count),
            Markers = uniquePlanarRoutes.Sum(route => route.Markers.Count),
            SampleStride = 20,
            SampleVectorRole = "unit lateral normal perpendicular to route travel",
            LateralNormalAlignmentMaxError = routeNormalAlignmentMaxError,
            RuntimeLoaderFunction = $"0x{PlanarRouteAsset.RuntimeLoaderFunction:X8}",
            RuntimeCursorFunction = $"0x{PlanarRouteAsset.RuntimeCursorFunction:X8}",
            RuntimeLateralProjectionFunction = $"0x{PlanarRouteAsset.RuntimeLateralProjectionFunction:X8}",
            RuntimeOnePlayerRadarFunction = $"0x{PlanarRouteAsset.RuntimeOnePlayerRadarFunction:X8}",
            RuntimeCursorRule = "largest sample distance <= course distance, clamped to route ends",
            RuntimeLateralProjection = "dot(rider position - sample position, sample lateral normal)",
            RuntimeRadarHalfWindow = PlanarRouteAsset.RuntimeRadarHalfWindow,
            RuntimeRadarWindow = PlanarRouteAsset.RuntimeRadarWindow,
            RuntimeLineTexture = "radarline",
            MarkerKinds = uniquePlanarRoutes.SelectMany(route => route.Markers).GroupBy(marker => marker.Kind)
                .OrderBy(group => group.Key).Select(group => new
                {
                    Kind = group.Key, Name = Ssx3PlanarRouteDecoder.MarkerKindName(group.Key),
                    Texture = Ssx3PlanarRouteDecoder.MarkerTextureName(group.Key), Count = group.Count()
                }).ToArray(),
            CheckpointRoutes = uniquePlanarRoutes.Count(route => route.Markers.Any(marker => marker.Kind == 1)),
            LayoutViolations = 0
        };
        var rawType12 = Ssx3RawResourceReader.Read(ssbPath, allGroups, 12);
        var rawCollisionMeshes = rawType12
            .Where(resource => resource.Payload.Length >= 2 && (resource.Payload[0] | resource.Payload[1] << 8) == 1)
            .Select(resource => Ssx3CollisionDecoder.Decode(resource.Payload,
                new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 12/v1", resource.GroupIndex,
                    SupportConfidence.Medium), resource.TrackId, resource.ResourceId)).ToArray();
        var uniqueCollisionSubmeshes = rawCollisionMeshes.SelectMany(asset => asset.Submeshes).ToArray();
        var uniqueCollisionMeshCorpus = new
        {
            Assets = rawCollisionMeshes.Length,
            Submeshes = uniqueCollisionSubmeshes.Length,
            Triangles = uniqueCollisionSubmeshes.Sum(submesh => submesh.Indices.Count / 3),
            Vertices = uniqueCollisionSubmeshes.Sum(submesh => submesh.Vertices.Count),
            TriangleBatches = uniqueCollisionSubmeshes.Sum(submesh => submesh.TriangleBatches.Count),
            IndexPaddingBytes = uniqueCollisionSubmeshes.Sum(submesh => submesh.IndexPadding.Length),
            TriangleBatchPaddingBytes = uniqueCollisionSubmeshes.Sum(submesh => submesh.TriangleBatchPadding.Length),
            RuntimeScratchHeaderBytes = rawCollisionMeshes.Sum(asset => asset.RuntimeScratchHeader.Length),
            SubmeshPointerScratchBytes = rawCollisionMeshes.Sum(asset => asset.SubmeshPointerScratch.Length),
            RuntimeScratchHeaderPatterns = rawCollisionMeshes.GroupBy(asset => Convert.ToHexString(asset.RuntimeScratchHeader))
                .OrderByDescending(group => group.Count()).Select(group => new { Hex = group.Key, Count = group.Count() }).ToArray(),
            SubmeshPointerScratchWordPatterns = rawCollisionMeshes.SelectMany(asset => asset.SubmeshPointerScratch.Chunk(4))
                .GroupBy(word => Convert.ToHexString(word)).OrderByDescending(group => group.Count())
                .Select(group => new { Hex = group.Key, Count = group.Count() }).ToArray(),
            TriangleBatchBoundsMaxError = rawCollisionMeshes.Select(asset =>
                    Convert.ToSingle(asset.Properties["TriangleBatchBoundsMaxError"]))
                .DefaultIfEmpty().Max(),
            SurfaceAttributesSerializedInType12 = 0,
            OwningColliderDefaultCollisionAttribute = -1,
            OwningColliderDefaultResolvedSurface = SsxSurfaceType.PackedSnow.ToString(),
            OwningColliderCollisionAttributeMaximumKnownIndex = (ushort)SsxSurfaceType.WipeoutRock,
            LayoutViolations = 0
        };
        var rawSphereTrees = rawType12
            .Where(resource => resource.Payload.Length >= 2 && (resource.Payload[0] | resource.Payload[1] << 8) == 3)
            .Select(resource => Ssx3SphereTreeDecoder.Decode(resource.Payload,
                new SourceByteRange(ssbPath, 0, resource.Payload.Length, "Type 12/v3", resource.GroupIndex,
                    SupportConfidence.Medium), resource.TrackId, resource.ResourceId)).ToArray();
        var uniqueSphereTreeRecords = rawSphereTrees.SelectMany(asset => asset.Trees).ToArray();
        var inverseMatrixMaxError = uniqueSphereTreeRecords.SelectMany(tree => Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 3).Select(column =>
                Math.Abs(Enumerable.Range(0, 3).Sum(k => tree.RetainedSymmetricMatrix[row * 3 + k] * tree.RetainedInverseSymmetricMatrix[k * 3 + column])
                    - (row == column ? 1f : 0f))))).DefaultIfEmpty().Max();
        var uniqueSphereTreeCorpus = new
        {
            Assets = rawSphereTrees.Length, Records = uniqueSphereTreeRecords.Length,
            PackedBytes = uniqueSphereTreeRecords.Sum(tree => tree.PackedPayloadSize),
            DecodedNodeBytes = uniqueSphereTreeRecords.Sum(tree => tree.DecodedNodeMasks.Length),
            ReferencedNodes = uniqueSphereTreeRecords.Sum(tree => tree.NodeLevels.Sum(level => level.ReferencedNodeCount)),
            ChildLinks = uniqueSphereTreeRecords.Sum(tree => tree.NodeLevels.Sum(level => level.ReferencedChildCount)),
            CompressionTypes = uniqueSphereTreeRecords.GroupBy(tree => tree.CompressionType)
                .OrderBy(group => group.Key).Select(group => new { Type = group.Key, Count = group.Count() }).ToArray(),
            LevelCounts = uniqueSphereTreeRecords.GroupBy(tree => tree.Levels.Count)
                .OrderBy(group => group.Key).Select(group => new { Levels = group.Key, Count = group.Count() }).ToArray(),
            ShapeViolations = uniqueSphereTreeRecords.Sum(tree => tree.NodeLevels.Count(level =>
                level.IsTerminal ? level.ChildMasks.Any(mask => mask != 0)
                    : level.ChildMasks.Count(mask => mask != 0) > level.ReferencedNodeCount)),
            RetainedMatrixSymmetryMaxError = uniqueSphereTreeRecords.Select(tree => MatrixSymmetryError(tree.RetainedSymmetricMatrix))
                .DefaultIfEmpty().Max(),
            RetainedPositiveDefiniteMatrixViolations = uniqueSphereTreeRecords.Count(tree => !IsPositiveDefinite3x3(tree.RetainedSymmetricMatrix)),
            RetainedInverseMatrixMaxError = inverseMatrixMaxError,
            RetainedMatrixMetadataCopiedByRetailLoader = true,
            RetainedMatrixMetadataConsumedByRetailRuntime = false,
            RetailRuntimeConsumedRecordFields = "0x08, 0x0C, 0x14..0x28",
            RetailRuntimeUnreadRetainedMetadataRange = "0x2C..0x7F"
        };
        var uniqueTerrain = uniqueTerrainByResource.Values.ToArray();
        var uniqueTerrainCorpus = new
        {
            Records = uniqueTerrain.Length,
            LightmapRecords = uniqueTerrain.Count(patch => patch.LightmapResourceId >= 0),
            SecondaryTextureRecords = uniqueTerrain.Count(patch => patch.HasSecondaryTexture),
            SecondaryPassRecords = uniqueTerrain.Count(patch => patch.RequestsRuntimeSecondaryPass),
            DestinationAlphaSecondaryPassRecords = uniqueTerrain.Count(patch =>
                patch.RequestsRuntimeSecondaryPass && patch.RuntimeSecondaryPassUsesDestinationAlpha),
            SecondaryTextureCorrelationViolations = uniqueTerrain.Count(patch =>
                patch.HasSecondaryTexture != patch.RequestsRuntimeSecondaryPass),
            DestinationAlphaCorrelationViolations = uniqueTerrain.Count(patch =>
                patch.RequestsRuntimeSecondaryPass && !patch.RuntimeSecondaryPassUsesDestinationAlpha),
            RenderFlagValues = uniqueTerrain.GroupBy(patch => patch.RenderFlags).OrderBy(group => group.Key)
                .Select(group => new { Value = $"0x{group.Key:X4}", Count = group.Count() }).ToArray(),
            RuntimeRenderFunction = $"0x{TerrainPatch.RetailRenderFunction:X8}",
            RuntimeTerrainStateSetupRange = $"0x{TerrainPatch.RetailTerrainStateSetupStart:X8}..0x{TerrainPatch.RetailTerrainStateSetupEnd:X8}",
            RuntimePrimaryAlphaSelector = TerrainPatch.RuntimePrimaryAlphaSelector,
            RuntimePrimaryGsAlphaRegister = $"0x{TerrainPatch.RuntimePrimaryGsAlphaRegister:X}",
            RuntimePrimaryBlendEquation = TerrainPatch.RuntimePrimaryBlendEquation,
            RuntimeLightmapAlphaSelector = TerrainPatch.RuntimeLightmapAlphaSelector,
            RuntimeLightmapGsAlphaRegister = $"0x{TerrainPatch.RuntimeLightmapGsAlphaRegister:X}",
            RuntimeLightmapBlendEquation = TerrainPatch.RuntimeLightmapBlendEquation,
            RuntimeSecondaryPassFunction = $"0x{TerrainPatch.RetailSecondaryPassFunction:X8}",
            RuntimeSecondaryPassMask = $"0x{TerrainPatch.RuntimeSecondaryPassMask:X4}",
            RuntimeDestinationAlphaMask = $"0x{TerrainPatch.RuntimeDestinationAlphaMask:X4}",
            RuntimeStateCompilerRange = "0x00363F9C..0x00363FE4",
            RuntimeAlphaTableFunction = "0x00362478",
            RuntimeAlphaTableAddress = "0x00491FB0",
            RuntimeSecondaryAlphaSelector = TerrainPatch.RuntimeSecondaryAlphaSelector,
            RuntimeSecondaryGsAlphaRegister = $"0x{TerrainPatch.RuntimeSecondaryGsAlphaRegister:X}",
            RuntimeSecondaryBlendEquation = TerrainPatch.RuntimeSecondaryBlendEquation,
            RuntimeFallbackAlphaSelector = TerrainPatch.RuntimeFallbackAlphaSelector,
            RuntimeFallbackGsAlphaRegister = $"0x{TerrainPatch.RuntimeFallbackGsAlphaRegister:X16}",
            RuntimeFallbackBlendEquation = TerrainPatch.RuntimeFallbackBlendEquation,
            LayoutViolations = 0
        };
        var uniqueLightmaps = uniqueLightmapsByResource.Values.ToArray();
        var uniqueTerrainLightmapCorpus = new
        {
            Records = uniqueLightmaps.Length,
            Pixels = uniqueLightmaps.Sum(texture => (long)texture.Width * texture.Height),
            Formats = uniqueLightmaps.GroupBy(texture => Convert.ToInt32(texture.Properties["Format"]))
                .OrderBy(group => group.Key).Select(group => new { Format = group.Key, Count = group.Count() }).ToArray(),
            Dimensions = uniqueLightmaps.GroupBy(texture => new { texture.Width, texture.Height })
                .OrderBy(group => group.Key.Width).ThenBy(group => group.Key.Height)
                .Select(group => new { group.Key.Width, group.Key.Height, Count = group.Count() }).ToArray(),
            RawPs2AlphaMinimum = uniqueLightmaps.Min(texture => Convert.ToByte(texture.Properties["RawPs2AlphaMinimum"])),
            RawPs2AlphaMaximum = uniqueLightmaps.Max(texture => Convert.ToByte(texture.Properties["RawPs2AlphaMaximum"])),
            Ps2AlphaOne = 128,
            DirectRgba32Violations = uniqueLightmaps.Count(texture => Convert.ToInt32(texture.Properties["Format"]) != 5
                || Convert.ToInt32(texture.Properties["PayloadSize"]) != 0x80 + texture.Width * texture.Height * 4)
        };
        var uniqueMaterials = rawMaterialById.Values.ToArray();
        var uniqueMaterialCorpus = new
        {
            Records = uniqueMaterials.Length,
            ResourceIdentityContentViolations = rawType0.GroupBy(resource => Key(resource.TrackId, resource.ResourceId))
                .Count(group => group.Select(resource => Convert.ToHexString(SHA256.HashData(resource.Payload))).Distinct().Skip(1).Any()),
            PrimaryTextureAlphaBlendRecords = uniqueMaterials.Count(resource =>
                (BinaryPrimitives.ReadUInt16LittleEndian(resource.Payload.AsSpan(2, 2))
                    & MaterialAsset.PrimaryTextureStateSourceBit) != 0),
            OpaquePrimaryTextureReplacementRecords = uniqueMaterials.Count(resource =>
                (BinaryPrimitives.ReadUInt16LittleEndian(resource.Payload.AsSpan(2, 2))
                    & MaterialAsset.PrimaryTextureStateSourceBit) == 0),
            TextureStateWord02Values = uniqueMaterials.GroupBy(resource =>
                    BinaryPrimitives.ReadUInt16LittleEndian(resource.Payload.AsSpan(2, 2)))
                .OrderBy(group => group.Key).Select(group => new { Value = $"0x{group.Key:X4}", Count = group.Count() }).ToArray(),
            RuntimeTextureStateSourceBit = $"0x{MaterialAsset.PrimaryTextureStateSourceBit:X4}",
            RuntimeTextureStateDestinationBit = $"0x{MaterialAsset.PrimaryTextureStateDestinationBit:X8}",
            OpaqueAlphaSelector = MaterialAsset.RuntimeOpaquePrimaryAlphaSelector,
            OpaqueGsAlphaRegister = $"0x{MaterialAsset.RuntimeOpaquePrimaryGsAlphaRegister:X}",
            OpaqueBlendEquation = MaterialAsset.RuntimeOpaquePrimaryBlendEquation,
            BlendedAlphaSelector = MaterialAsset.RuntimeBlendedPrimaryAlphaSelector,
            BlendedGsAlphaRegister = $"0x{MaterialAsset.RuntimeBlendedPrimaryGsAlphaRegister:X}",
            BlendedBlendEquation = MaterialAsset.RuntimeBlendedPrimaryBlendEquation,
            RuntimeFrameIndexInitializer = $"0x{MaterialAsset.Ssx3RuntimeFrameIndexInitializerFunction:X8}",
            RuntimeRandomFunction = $"0x{MaterialAsset.Ssx3RuntimeRandomFunction:X8}",
            RuntimeInitialFrameRule = "unsigned random value modulo shared model frame count",
            RuntimeDmaCallTagWord0 = $"0x{MaterialAsset.RuntimeDmaCallTagWord0:X8}",
            RuntimeDmaCallTagId = MaterialAsset.RuntimeDmaCallTagId.ToString(),
            RuntimeDmaCallQuadwordCount = MaterialAsset.RuntimeDmaCallQuadwordCount,
            RuntimeDmaCallTargetRule = "base address + complete material dword at +0x04"
        };
        var malformedType9RendererStateRecords = rawType9.Count(resource =>
            resource.Payload.Length < TextureAsset.Ssx3RendererStateOffset + sizeof(uint));
        var type9RendererStates = rawType9.Where(resource =>
                resource.Payload.Length >= TextureAsset.Ssx3RendererStateOffset + sizeof(uint))
            .Select(resource => BinaryPrimitives.ReadUInt32LittleEndian(
                resource.Payload.AsSpan(TextureAsset.Ssx3RendererStateOffset, sizeof(uint)))).ToArray();
        var uniqueType9TextureCorpus = new
        {
            Records = rawType9.Count,
            MalformedRendererStateRecords = malformedType9RendererStateRecords,
            NonzeroRendererDispatchStateRecords = type9RendererStates.Count(state =>
                (state & TextureAsset.Ssx3RendererDispatchMask) != 0),
            RendererStateWord0CValues = type9RendererStates.GroupBy(state => state).OrderBy(group => group.Key)
                .Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray(),
            RendererDispatchMask = $"0x{TextureAsset.Ssx3RendererDispatchMask:X8}",
            RendererDispatchStateValues = type9RendererStates.GroupBy(state => state & TextureAsset.Ssx3RendererDispatchMask)
                .OrderBy(group => group.Key).Select(group => new { Value = $"0x{group.Key:X8}", Count = group.Count() }).ToArray()
        };
        failed |= uniqueMaterialCorpus.ResourceIdentityContentViolations != 0
            || malformedType9RendererStateRecords != 0
            || type9RendererStates.Any(state => (state & TextureAsset.Ssx3RendererDispatchMask) != 0);
        var report = JsonSerializer.Serialize(new { Project = project.ProjectName, project.DetectedRevision,
            AuditedUtc = DateTime.UtcNow, Passed = !failed, CourseCount = courses.Count,
            UniqueMaterialCorpus = uniqueMaterialCorpus,
            UniqueType9TextureCorpus = uniqueType9TextureCorpus,
            UniqueTerrainCorpus = uniqueTerrainCorpus,
            UniqueTerrainLightmapCorpus = uniqueTerrainLightmapCorpus,
            UniqueInstanceCorpus = uniqueInstanceCorpus,
            UniqueLightCorpus = uniqueLightCorpus,
            UniqueHaloCorpus = uniqueHaloCorpus,
            UniquePlanarRouteCorpus = uniquePlanarRouteCorpus,
            UniqueCollisionMeshCorpus = uniqueCollisionMeshCorpus,
            UniqueSphereTreeCorpus = uniqueSphereTreeCorpus, Courses = courses }, DiagnosticBag.JsonOptions);
        var output = Option(args, "--json");
        if (output is null) Console.WriteLine(report);
        else { var fullPath = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); File.WriteAllText(fullPath, report); Console.WriteLine($"Wrote {fullPath}"); }
        return failed ? 2 : 0;
    }

    private static InstanceDmaSurvey SurveyInstanceDma(byte[] payload, byte[]? model)
    {
        static int TagId(ReadOnlySpan<byte> bytes, int offset) =>
            (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)) >> 28) & 7;

        var expectedPrograms = ModelDmaProgramCount(model);
        if (payload.Length < 176 || model is null)
            return new(expectedPrograms, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 0, payload.Length);
        var extensionOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x98, 4)));
        if (extensionOffset < 160 || extensionOffset > payload.Length - 16 || (extensionOffset & 15) != 0)
            return new(expectedPrograms, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 0, payload.Length);
        var modelDataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(0x24, 4)));

        var position = extensionOffset; var parsedPrograms = 0; var modelReferences = 0; var sourceReferences = 0;
        var structuralRecords = 0; var returnTags = 0; var sourceQuadwords = 0; var sprReferences = 0;
        var sprImmediate = 0; var sprExtended = 0; var invalidTags = 0; var invalidSourceTags = 0;
        var invalidSourceRanges = 0; var invalidModelRanges = 0; var misalignedSources = 0;
        var placeholders = 0; var invalidPlaceholders = 0; var workspaceViolations = 0;
        var sourceRanges = new List<(int Start, int End)>();

        bool HasRecords(int count) => position >= 0 && position <= payload.Length - count * 16;
        void ModelReference(int offset)
        {
            modelReferences++;
            if (TagId(payload, offset) != 3) { invalidTags++; return; }
            var tagWord = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            var qwc = checked((int)(tagWord & 0xffff));
            var addressWord = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4, 4));
            if ((addressWord & 0x80000000) != 0) sprReferences++;
            var address = addressWord & 0x7fffffff;
            var end = (long)modelDataOffset + address + (long)qwc * 16;
            if ((address & 15) != 0 || qwc <= 0 || modelDataOffset < 0 || end > model.Length) invalidModelRanges++;
        }
        void SourceReference(int offset)
        {
            sourceReferences++;
            if (TagId(payload, offset) != 3) { invalidSourceTags++; return; }
            var tagWord = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            var qwc = checked((int)(tagWord & 0xffff));
            var address = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4, 4)) & 0x7fffffff;
            if ((address & 15) != 0) misalignedSources++;
            var start = (long)extensionOffset + address; var end = start + (long)qwc * 16;
            if (qwc <= 0 || start < extensionOffset || end > payload.Length)
            {
                invalidSourceRanges++;
                return;
            }
            sourceQuadwords += qwc; sourceRanges.Add((checked((int)start), checked((int)end)));
            var finalQuadword = checked((int)end - 16);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(finalQuadword + 8, 4)) == 0xdeadbeef) placeholders++;
            else invalidPlaceholders++;
        }

        for (var program = 0; program < expectedPrograms && invalidTags == 0; program++)
        {
            var returned = false;
            while (!returned)
            {
                if (!HasRecords(1)) { invalidTags++; break; }
                var id = TagId(payload, position); structuralRecords++;
                if (id == 6)
                {
                    returnTags++; position += 16; parsedPrograms++; returned = true; continue;
                }
                if (id != 3 || !HasRecords(2)) { invalidTags++; break; }

                var destination = position; var source = position + 16;
                ModelReference(destination); SourceReference(source); structuralRecords++;
                var spr = (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(destination + 4, 4)) & 0x80000000) != 0;
                position += 32;
                if (!spr) continue;

                if (!HasRecords(2)) { invalidTags++; break; }
                if (TagId(payload, position + 16) == 6)
                {
                    ModelReference(position); structuralRecords += 3; returnTags++; sprImmediate++;
                    if (!HasRecords(3) || payload.AsSpan(position + 32, 16).IndexOfAnyExcept((byte)0) >= 0) workspaceViolations++;
                    position += 48; parsedPrograms++; returned = true;
                    continue;
                }

                if (!HasRecords(4)) { invalidTags++; break; }
                ModelReference(position); SourceReference(position + 16);
                ModelReference(position + 32); ModelReference(position + 48);
                structuralRecords += 4; sprExtended++; position += 64;
            }
        }

        var referenced = new bool[payload.Length];
        for (var i = extensionOffset; i < Math.Min(position, payload.Length); i++) referenced[i] = true;
        foreach (var range in sourceRanges)
            for (var i = range.Start; i < range.End; i++) referenced[i] = true;
        var unreferenced = Enumerable.Range(extensionOffset, payload.Length - extensionOffset).Count(offset => !referenced[offset]);
        return new(expectedPrograms, parsedPrograms, modelReferences, sourceReferences, structuralRecords, returnTags,
            sourceQuadwords, sprReferences, sprImmediate, sprExtended, invalidTags, invalidSourceTags,
            invalidSourceRanges, invalidModelRanges, misalignedSources, placeholders, invalidPlaceholders,
            workspaceViolations, unreferenced);
    }

    private static int ModelDmaProgramCount(byte[]? model)
    {
        if (model is null || model.Length < 44) return 0;
        var objectCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(4, 4)));
        var materialCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(0x28, 4)));
        if (objectCount < 0 || materialCount < 0) return 0;
        var tableOffset = 44L + materialCount * 4L;
        if (tableOffset < 0 || tableOffset + objectCount * 16L > model.Length) return 0;
        var result = 0;
        for (var i = 0; i < objectCount; i++)
        {
            var descriptor = checked((int)tableOffset + i * 16);
            var geometry = BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(descriptor + 4, 4));
            if (geometry is 0 or uint.MaxValue || geometry > model.Length - 32) continue;
            result = checked(result + (int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(checked((int)geometry) + 0x1c, 4)));
        }
        return result;
    }
    private static int DumpResource(string[] args)
    {
        var project = ProjectService.Open(args[1]); var level = args[2];
        if (!int.TryParse(args[3], out var type) || !int.TryParse(args[4], out var trackId) || !int.TryParse(args[5], out var resourceId))
            return Fail("Type, track, and resource ID must be integers.");
        var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics); var course = Ssx3CourseCatalog.Find(level);
        var areas = course is not null ? Ssx3CourseCatalog.ResolveAreas(sdb, course)
            : [sdb.Areas.FirstOrDefault(x => x.Name.Equals(level, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Level '{level}' was not found")];
        var groups = areas.SelectMany(x => Enumerable.Range(x.FirstGroup, x.GroupCount)).ToHashSet();
        var resources = Ssx3RawResourceReader.Read(ProjectService.WorldFile(project, ".ssb"), groups, type, trackId, resourceId);
        if (resources.Count == 0) throw new InvalidDataException($"No Type-{type} resource {trackId}:{resourceId} was found in {level}");
        var names = Ssx3ResourceNames.TryLoad(ProjectService.WorldFile(project, ".phm"), ProjectService.WorldFile(project, ".psm"), diagnostics);
        var resolvedName = names?.Find(type switch { 3 => 1, 2 => 2, 8 => 3, 12 => 4, _ => -1 }, trackId, resourceId);
        var output = Path.GetFullPath(args[6]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (resources.Count == 1)
        {
            File.WriteAllBytes(output, resources[0].Payload); Console.WriteLine($"Wrote {output} ({resources[0].Payload.Length} bytes, group {resources[0].GroupIndex}, name {resolvedName ?? "unresolved"})");
        }
        else
        {
            var directory = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
            Directory.CreateDirectory(directory);
            foreach (var resource in resources)
            {
                var path = Path.Combine(directory, $"g{resource.GroupIndex:D3}-r{resource.ResourceIndex:D5}.bin");
                File.WriteAllBytes(path, resource.Payload);
            }
            Console.WriteLine($"Wrote {resources.Count} matching resources to {directory}");
        }
        return 0;
    }
    private static int SurveyResource(string[] args)
    {
        var project = ProjectService.Open(args[1]);
        if (!int.TryParse(args[2], out var type) || type is < 0 or > 255) return Fail("Type must be an integer from 0 to 255.");
        var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics);
        var groups = sdb.Areas.SelectMany(x => Enumerable.Range(x.FirstGroup, x.GroupCount)).ToHashSet();
        var resources = Ssx3RawResourceReader.Read(ProjectService.WorldFile(project, ".ssb"), groups, type);
        var headerBytes = int.TryParse(Option(args, "--header-bytes"), out var requestedHeaderBytes)
            ? Math.Clamp(requestedHeaderBytes, 0, 4096) : 64;
        var report = JsonSerializer.Serialize(new
        {
            Type = type, Count = resources.Count,
            Resources = resources.Select(x => new { x.GroupIndex, x.ResourceIndex, x.TrackId, x.ResourceId,
                Size = x.Payload.Length, HeaderHex = Convert.ToHexString(x.Payload.AsSpan(0, Math.Min(x.Payload.Length, headerBytes))) }).ToArray()
        }, DiagnosticBag.JsonOptions);
        var output = Option(args, "--json");
        if (output is null) Console.WriteLine(report);
        else { var fullPath = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); File.WriteAllText(fullPath, report); Console.WriteLine($"Wrote {fullPath}"); }
        return 0;
    }
    private static int Export(string[] args)
    {
        var project = ProjectService.Open(args[1]); var level = args[2]; var format = Option(args, "--format") ?? "obj"; var output = Option(args, "--output") ?? ".";
        if (!format.Equals("obj", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("This vertical slice supports OBJ; glTF is planned.");
        var parsed = Parse(project, level); Directory.CreateDirectory(output); var path = Path.Combine(output, level + ".obj"); var result = ObjExporter.ExportScene(parsed.Scene, path);
        Console.WriteLine($"Wrote {result.ObjPath}"); Console.WriteLine($"Wrote {result.MaterialPath}"); Console.WriteLine($"Wrote {result.TextureCount} PNG texture(s) to {result.TextureDirectory}"); return 0;
    }
    private static int DumpTextures(string[] args)
    {
        var project = ProjectService.Open(args[1]); var parsed = Parse(project, args[2]); var output = Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
        var resourceFilter = int.TryParse(Option(args, "--resource-id"), out var resourceId) ? resourceId : (int?)null;
        var groupFilter = int.TryParse(Option(args, "--group"), out var groupIndex) ? groupIndex : (int?)null;
        var written = 0;
        for (var i = 0; i < parsed.Scene.Textures.Count; i++)
        {
            var texture = parsed.Scene.Textures[i]; var group = Convert.ToInt32(texture.Properties["GroupIndex"]);
            if (resourceFilter is not null && texture.ResourceId != resourceFilter || groupFilter is not null && group != groupFilter) continue;
            var path = Path.Combine(output, $"{i:D4}-g{group:D3}-{texture.Usage.ToString().ToLowerInvariant()}-rid{texture.ResourceId:D3}-{texture.Width}x{texture.Height}.bmp");
            using var stream = File.Create(path); using var writer = new BinaryWriter(stream); var imageSize = checked(texture.Width * texture.Height * 4);
            writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(54 + imageSize); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(texture.Width); writer.Write(-texture.Height); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(imageSize); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
            for (var pixel = 0; pixel < texture.Width * texture.Height; pixel++)
            {
                writer.Write(texture.RgbaPixels[pixel * 4 + 2]); writer.Write(texture.RgbaPixels[pixel * 4 + 1]);
                writer.Write(texture.RgbaPixels[pixel * 4]); writer.Write(texture.RgbaPixels[pixel * 4 + 3]);
            }
            written++;
        }
        Console.WriteLine($"Wrote {written} textures to {output}"); return parsed.Diagnostics.HasErrors ? 2 : 0;
    }
    private static Ssx3LevelParseResult Parse(MountainizerProject project, string level)
    {
        var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics);
        var course = Ssx3CourseCatalog.Find(level);
        var result = course is not null
            ? Ssx3SsbReader.ParseCourse(ProjectService.WorldFile(project, ".ssb"), sdb, course)
            : Ssx3SsbReader.ParseLevel(ProjectService.WorldFile(project, ".ssb"),
                sdb.Areas.FirstOrDefault(x => x.Name.Equals(level, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"Level '{level}' was not found"));
        foreach (var d in diagnostics.Items) result.Diagnostics.Add(d);
        return result;
    }
    private static string[] WorldPainterPropertyNames(int typeId)
        => Ssx3StructuredTableDecoder.WorldPainterRecordSize(typeId) is { } size
            ? Enumerable.Range(0, Math.Max(0, size / sizeof(uint) - 1))
                .Select(index => Ssx3StructuredTableDecoder.WorldPainterPropertyName(typeId, index)).ToArray()
            : [];
    private static string? Option(string[] args, string name) { var i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static IProgress<(long Current, long Total, string Stage)> Progress() => new ConsoleProgress();
    private static int Fail(string text) { Console.Error.WriteLine(text); return 1; }
    private static void Help() => Console.WriteLine("""
Mountainizer CLI (read-only)
  mountainizer-cli identify <iso>
  mountainizer-cli list-files <iso>
  mountainizer-cli extract-file <iso> <iso-file-path> <output-path>
  mountainizer-cli extract <iso> <project-directory>
  mountainizer-cli import <iso> <project-directory> <project-name>
  mountainizer-cli list-levels <project-or-project.json>
  mountainizer-cli audit <project-or-project.json> [--json <output>]
  mountainizer-cli inspect <project> <level> [--json <output>]
  mountainizer-cli dump-resource <project> <level> <type> <track> <resource-id> <output-file>
  mountainizer-cli survey-resource <project> <type> [--header-bytes <0-4096>] [--json <output>]
  mountainizer-cli dump-textures <project> <level> <output-directory> [--resource-id <id>] [--group <index>]
  mountainizer-cli export <project> <level> --format obj --output <directory>
""");

    private sealed class ConsoleProgress : IProgress<(long Current, long Total, string Stage)>
    {
        private int _lastPercent = -1;
        private string _lastStage = string.Empty;
        public void Report((long Current, long Total, string Stage) value)
        {
            var percent = value.Total == 0 ? 0 : (int)(value.Current * 100 / value.Total);
            if (percent == _lastPercent && value.Stage == _lastStage) return;
            _lastPercent = percent; _lastStage = value.Stage; Console.Error.Write($"\r{value.Stage}: {percent,3}%");
            if (percent >= 100) Console.Error.WriteLine();
        }
    }
}
