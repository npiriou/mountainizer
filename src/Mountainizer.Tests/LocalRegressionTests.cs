using System.Numerics;
using Mountainizer.Core;
using Mountainizer.Export;
using Mountainizer.Formats;
using Mountainizer.Iso;
using Mountainizer.Rendering;

namespace Mountainizer.Tests;

[TestClass]
public sealed class LocalRegressionTests
{
    [TestMethod]
    [TestCategory("LocalGameData")]
    public void ReferenceType20Bank_MicroTalkDecodeIsStableAndHonorsPcmCorrections()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "type20-bank1.bin"));
        if (!File.Exists(path))
            Assert.Inconclusive("The local Type-20 reference corpus is not available.");
        var data = File.ReadAllBytes(path);
        var source = new SourceByteRange(path, 0, data.Length, "type 20 oracle", 0, SupportConfidence.High);
        var bank = Ssx3BnklBankDecoder.Decode(data, source, 0, 0);
        var expected = new Dictionary<int, string>
        {
            [32] = "1684EC92A1CCCED7374EB23E71849DBF914F5E9775FFF41C27879BBBFDF0734D"
        };
        foreach (var pair in expected)
        {
            var section = bank.Sounds.Single(sound => sound.Slot == pair.Key).InfoSections[0];
            var samples = EaMicroTalkDecoder.DecodeBankSection(bank, section);
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan());
            Assert.AreEqual(pair.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                $"slot {pair.Key}");
            Assert.AreEqual(section.SampleCount, samples.Length);
            Assert.IsTrue(section.UsesPcmCorrectionBlocks);
            Assert.AreEqual((section.SampleCount + 431) / 432, section.MicroTalkFrameCount);
            Assert.IsTrue(samples.Take(216).All(sample => sample == 0), "The first version-3 PCM correction block was not applied.");
            var wave = EaMicroTalkDecoder.CreatePcm16Wave(samples, section.SampleRate);
            CollectionAssert.AreEqual("RIFF"u8.ToArray(), wave[..4]);
            Assert.AreEqual(samples.Length * 2, BitConverter.ToInt32(wave, 40));
        }
    }

    [TestMethod]
    [TestCategory("LocalGameData")]
    public void ReferenceType20Corpus_AllMicroTalkSectionsDecodeToDeclaredSampleCount()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "type20-corpus"));
        if (!Directory.Exists(directory))
            Assert.Inconclusive("The local Type-20 reference corpus is not available.");
        var decodedSections = 0;
        long decodedSamples = 0;
        foreach (var path in Directory.GetFiles(directory, "*.bin"))
        {
            var data = File.ReadAllBytes(path);
            var source = new SourceByteRange(path, 0, data.Length, "type 20 corpus", 0, SupportConfidence.High);
            var bank = Ssx3BnklBankDecoder.Decode(data, source, 0, 0);
            foreach (var sound in bank.Sounds)
            foreach (var item in sound.InfoSections.Select((section, index) => (section, index)))
            {
                try
                {
                    var samples = EaMicroTalkDecoder.DecodeBankSection(bank, item.section);
                    Assert.AreEqual(item.section.SampleCount, samples.Length, $"{Path.GetFileName(path)} slot {sound.Slot} section {item.index}");
                    decodedSections++;
                    decodedSamples += samples.Length;
                }
                catch (Exception exception)
                {
                    Assert.Fail($"{Path.GetFileName(path)} slot {sound.Slot} section {item.index}, version {item.section.StreamVersion}, "
                        + $"offset {item.section.ChannelOffsets.SingleOrDefault()}: {exception}");
                }
            }
        }
        Assert.AreEqual(404, decodedSections);
        Assert.IsTrue(decodedSamples > 0);
        Console.WriteLine($"Decoded {decodedSections} unique MicroTalk sections / {decodedSamples} PCM samples.");
    }

    [TestMethod]
    [TestCategory("LocalGameData")]
    public void UserLocalAreaA_ParsesKnownTerrainCount()
    {
        var projectPath = Environment.GetEnvironmentVariable("MOUNTAINIZER_TEST_PROJECT");
        if (string.IsNullOrWhiteSpace(projectPath)) Assert.Inconclusive("Set MOUNTAINIZER_TEST_PROJECT to a local imported project; game data is never committed.");
        var project = ProjectService.Open(projectPath!); var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics); var area = sdb.Areas.Single(x => x.Name == "A");
        var result = Ssx3SsbReader.ParseLevel(ProjectService.WorldFile(project, ".ssb"), area);
        Assert.AreEqual(291, result.Scene.Terrain.Count); Assert.IsFalse(result.Diagnostics.HasErrors);
    }

    [TestMethod]
    [TestCategory("LocalGameData")]
    public void UserLocalAllPlayableCourses_ParseRecognizedSceneContentWithoutErrors()
    {
        var projectPath = Environment.GetEnvironmentVariable("MOUNTAINIZER_TEST_PROJECT");
        if (string.IsNullOrWhiteSpace(projectPath)) Assert.Inconclusive("Set MOUNTAINIZER_TEST_PROJECT to a local imported project; game data is never committed.");
        var project = ProjectService.Open(projectPath!); var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics); var ssbPath = ProjectService.WorldFile(project, ".ssb");
        var particleModels = 0; var particleEmitters = 0; var lights = 0; var halos = 0;
        var nisReferenceTables = 0; var navigationMarkers = 0; var cameraTriggerTables = 0;
        foreach (var course in Ssx3CourseCatalog.Courses)
        {
            var result = Ssx3SsbReader.ParseCourse(ssbPath, sdb, course);
            static long ResourceKey(int trackId, int resourceId) => ((long)trackId << 32) | (uint)resourceId;
            var splineIds = result.Scene.Splines.Select(spline => ResourceKey(Convert.ToInt32(spline.Properties["TrackId"]),
                Convert.ToInt32(spline.Properties["ResourceId"]))).ToHashSet();
            var propIds = result.Scene.Props.Select(prop => ResourceKey(Convert.ToInt32(prop.Properties["TrackId"]),
                Convert.ToInt32(prop.Properties["ResourceId"]))).ToHashSet();
            Assert.IsFalse(result.Diagnostics.HasErrors, $"{course.Code} produced parse errors");
            Assert.IsTrue(result.Scene.Terrain.Count > 0, $"{course.Code} has no terrain");
            Assert.IsTrue(result.Scene.Terrain.All(patch => patch.HasValidObservedRetailLayout
                && patch.HasSecondaryTexture == patch.RequestsRuntimeSecondaryPass
                && (!patch.RequestsRuntimeSecondaryPass || patch.RuntimeSecondaryPassUsesDestinationAlpha)
                && patch.ObjectResourceId == Convert.ToInt32(patch.Properties["ResourceId"])
                && Convert.ToInt32(patch.Properties["PayloadSize"]) == TerrainPatch.SerializedSize),
                $"{course.Code} has invalid Type-1 terrain framing, surface, bounds, texture state, or tail fields");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != TerrainPatch.Ssx3ResourceType),
                $"{course.Code} still has a fallback Type-1 terrain resource");
            Assert.IsTrue(result.Scene.Props.Count > 0, $"{course.Code} has no props");
            Assert.IsTrue(result.Scene.Props.All(x => !string.IsNullOrWhiteSpace(x.Classification.Reason)), $"{course.Code} has unclassified props");
            Assert.IsTrue(result.Scene.Props.All(x => x.RenderDmaProgram is { Programs.Count: > 0, SourceBlocks.Count: > 0 }
                && x.RenderDmaProgram.StructuralBytes + x.RenderDmaProgram.SourceBytes
                    == Convert.ToInt32(x.Properties["PayloadSize"]) - 160),
                $"{course.Code} has an incomplete Type-3 DMA/VIF extension");
            Assert.IsTrue(result.Scene.Props.All(x => Convert.ToInt32(x.Properties["LocatorTrackId"])
                    == Convert.ToInt32(x.Properties["TrackId"])
                && Convert.ToString(x.Properties["SerializedRuntimeHeader"]) == "00000000 00000000 00000000 00000000"
                && x.Properties["BoundingSphereCenter"] is Vector3 center
                && x.Properties["WorldBoundsMin"] is Vector3 minimum && x.Properties["WorldBoundsMax"] is Vector3 maximum
                && center.X >= minimum.X && center.X <= maximum.X && center.Y >= minimum.Y && center.Y <= maximum.Y
                && center.Z >= minimum.Z && center.Z <= maximum.Z
                && float.IsFinite(Convert.ToSingle(x.Properties["BoundingSphereRadius"]))
                && Convert.ToSingle(x.Properties["BoundingSphereRadius"]) >= 0),
                $"{course.Code} has an invalid Type-3 fixed header");
            Assert.IsTrue(result.Scene.Models.Count > 0, $"{course.Code} has no models");
            var modelsByResource = result.Scene.Models.Where(x => x.Mesh is not null).ToDictionary(
                x => (Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])));
            Assert.IsTrue(result.Scene.Props.All(x => modelsByResource.ContainsKey((x.ModelTrackId, x.ModelResourceId))), $"{course.Code} has unresolved model instances");
            Assert.IsTrue(result.Scene.Models.All(x => x.Submeshes.Count > 0 && x.Submeshes.All(s => s.Mesh.Indices.Count >= 3)), $"{course.Code} has models without renderable triangles");
            var textureResolver = new SceneTextureResolver(result.Scene);
            var modelsWithoutTextures = result.Scene.Models.Where(x => textureResolver.Resolve(x).Count == 0).Select(x => x.Name).ToArray();
            Assert.IsTrue(modelsWithoutTextures.Length == 0, $"{course.Code} has models without a texture preview: {string.Join(", ", modelsWithoutTextures.Take(12))}");
            Assert.IsTrue(result.Scene.Props.All(x => textureResolver.Resolve(x).Count > 0), $"{course.Code} has props without a texture preview");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 10), $"{course.Code} still has undecoded Type-10 lightmaps");
            var unresolvedLightmaps = result.Scene.Terrain.Where(x => x.LightmapResourceId >= 0 && !textureResolver.Resolve(x).Any(t => t.IsLightmap)).ToArray();
            Assert.IsTrue(unresolvedLightmaps.Length == 0, $"{course.Code} has referenced terrain lightmaps that do not resolve: "
                + string.Join(", ", unresolvedLightmaps.Take(8).Select(x => $"group {x.Properties["GroupIndex"]}/RID {x.LightmapResourceId}")));
            Assert.IsTrue(result.Scene.Textures.Where(texture => texture.IsLightmap).All(texture =>
                Convert.ToInt32(texture.Properties["Format"]) == 5
                && Convert.ToInt32(texture.Properties["PayloadSize"]) == 0x80 + texture.Width * texture.Height * 4
                && Convert.ToByte(texture.Properties["RawPs2AlphaMinimum"])
                    <= Convert.ToByte(texture.Properties["RawPs2AlphaMaximum"])),
                $"{course.Code} has a Type-10 lightmap outside the direct RGBA32 corpus");
            particleModels += result.Scene.ParticleModels.Count; particleEmitters += result.Scene.ParticleEmitters.Count;
            lights += result.Scene.Lights.Count; halos += result.Scene.Halos.Count;
            nisReferenceTables += result.Scene.NisReferenceTables.Count; navigationMarkers += result.Scene.NavigationMarkers.Count;
            cameraTriggerTables += result.Scene.CameraTriggerTables.Count;
            Assert.IsTrue(result.Scene.ParticleModels.All(x => x.Elements.Count > 0
                && Convert.ToString(x.Properties["RuntimeTextureAssetId"]) == ParticleModelAsset.RuntimeTextureAssetId
                && Convert.ToString(x.Properties["RuntimeGsAlphaRegister"]) == $"0x{ParticleModelAsset.RuntimeGsAlphaRegister:X2}"),
                $"{course.Code} has invalid Type-4 particle models");
            Assert.IsTrue(result.Scene.ParticleEmitters.All(x => x.ModelTrackId == x.TrackId
                && x.ModelResourceId == x.ResourceId
                && !Convert.ToBoolean(x.Properties["RuntimeConsumesSerializedBoundingRadius"])),
                $"{course.Code} has invalid Type-5 particle emitters");
            Assert.IsTrue(result.Scene.Lights.All(x => x.Kind is >= 0 and <= 3),
                $"{course.Code} has invalid Type-6 lights");
            Assert.IsTrue(result.Scene.Lights.All(x => Ssx3EffectDecoder.LightKindName(x.Kind) != "Unknown"
                && Math.Abs(x.Direction.LengthSquared() - 1f) < 0.01f), $"{course.Code} has an unknown or non-normalized Type-6 light");
            Assert.IsTrue(result.Scene.Lights.All(x => x.IsRuntimeAdmitted
                    == (!x.HasRuntimeFilterFlag0x100 && x.Kind is 1 or 2)),
                $"{course.Code} has an inconsistent Type-6 runtime-admission classification");
            Assert.IsTrue(result.Scene.Halos.All(x => x.Radius >= 0 && x.VisualModeCode is 0x10 or 0x20
                && x.HalfExtent == (x.VisualModeCode == 0x10 ? 50f : 100f)
                && x.RuntimeOcclusionProbeScale == (x.VisualModeCode == 0x10 ? 100f : 80f)
                && x.RuntimeRenderScale == (x.VisualModeCode == 0x10 ? 180f : 100f)
                && Math.Abs(x.RuntimeColorDirection.LengthSquared() - 1f) < 0.01f
                && x.RuntimeTextureName == (x.VisualModeCode == 0x10 ? "SHALO" : "MHALO")),
                $"{course.Code} has invalid Type-7 halos");
            Assert.IsTrue(result.Scene.Halos.GroupBy(x => x.TrackId).All(group =>
                    group.Select(x => x.SerializedCollectionPointerToken).Distinct().Count() == 1
                    && group.Select(x => x.SerializedEntryTableBasePointerToken).Distinct().Count() == 1),
                $"{course.Code} has an inconsistent Type-7 serialized pointer-token sequence");
            Assert.IsTrue(result.Scene.NisReferenceTables.All(x => x.Slots.Count == 18),
                $"{course.Code} has invalid Type-18 NIS script-object tables");
            Assert.IsTrue(result.Scene.NisReferenceTables.SelectMany(x => x.Slots).Where(slot => slot.ObjectReference is not null).All(slot =>
                slot.ObjectReference!.TargetResourceType == 3 && result.Scene.Props.Any(prop =>
                    Convert.ToInt32(prop.Properties["TrackId"]) == slot.ObjectReference.TrackId
                    && Convert.ToInt32(prop.Properties["ResourceId"]) == slot.ObjectReference.ResourceId)),
                $"{course.Code} has an unresolved Type-18 NIS instance reference");
            Assert.IsTrue(result.Scene.NisReferenceTables.SelectMany(x => x.Slots).Where(slot => slot.IsPopulated)
                .All(slot => slot.ObservedRole is not null),
                $"{course.Code} has an unnamed populated Type-18 NIS slot");
            Assert.IsTrue(result.Scene.NisReferenceTables.SelectMany(x => x.Slots.Select((slot, index) => (slot, index)))
                .All(x => x.slot.Index == x.index && x.slot.IsRuntimeAddressable
                    && x.slot.RuntimeCommandIds.SequenceEqual(Ssx3ReferenceTableDecoder.NisRuntimeCommandIds(x.index))),
                $"{course.Code} has an invalid Type-18 runtime command mapping");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType is not (4 or 5 or 6 or 7 or 14 or 18)),
                $"{course.Code} still has a fallback Type-4/5/6/7/14/18 resource");
            Assert.AreEqual(result.Scene.Triggers.Count, result.Scene.CameraTriggerTables.Sum(x => x.Records.Count),
                $"{course.Code} has inconsistent Type-17 trigger projections");
            Assert.IsTrue(result.Scene.CameraTriggerTables.All(table => table.Version == 7
                && table.Records.Select(x => x.TriggerId).Distinct().Count() == table.Records.Count
                && table.Records.All(record => record.TriggerId < table.NextTriggerId
                    && (record.Flags & ~3u) == 0 && Ssx3CameraTriggerDecoder.TriggerFlagNames(record.Flags) != "Unknown"
                    && Ssx3CameraTriggerDecoder.VolumeKindName(record.Shape.Kind) != "Unknown"
                    && Ssx3CameraTriggerDecoder.ActionKindName(record.Action0.Kind) != "Unknown"
                    && Ssx3CameraTriggerDecoder.ActionKindName(record.Action1.Kind) != "Unknown"
                    && record.Shape.ContainsRuntimePointSsx(Ssx3Coordinates.ToSsx3(record.Shape.Center))
                    && record.Shape.RuntimeContainmentFunction == (record.Shape.Kind == 0
                        ? CameraTriggerShape.RuntimeEllipseContainmentFunction : CameraTriggerShape.RuntimeBoxContainmentFunction)
                    && new[] { record.Action0, record.Action1 }.All(action => action.Kind == 3
                        ? action.BlendDurationSeconds is null && action.RuntimeBlendFractionPerFrame is null
                            && action.RuntimeCameraAlgorithmId is null
                        : action.BlendDurationSeconds is not null && action.RuntimeCameraAlgorithmId is not null
                            && float.IsFinite(action.RuntimeBlendFractionPerFrame ?? float.NaN))
                    && new[] { record.Action0.BoundObject, record.Action1.BoundObject }.Where(x => x is not null)
                        .All(x => Ssx3CameraTriggerDecoder.BoundKindName(x!.Kind) != "Unknown"))),
                $"{course.Code} has inconsistent Type-17 trigger records");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 17),
                $"{course.Code} still has a fallback Type-17 camera-trigger resource");
            Assert.IsTrue(result.Scene.Materials.Count > 0, $"{course.Code} has no decoded Type-0 materials");
            Assert.IsTrue(result.Scene.Materials.All(material => material.HasValidObservedRetailLayout
                && Convert.ToInt32(material.Properties["PayloadSize"]) == material.ExpectedSerializedSize),
                $"{course.Code} has invalid Type-0 material texture slots, state words, relocation token, or texture-frame table");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != MaterialAsset.Ssx3ResourceType),
                $"{course.Code} still has a fallback Type-0 material resource");
            Assert.IsTrue(result.Scene.Splines.Count > 0, $"{course.Code} has no splines");
            Assert.IsTrue(result.Scene.Splines.All(spline => spline.Segments.Count > 0
                && spline.Segments[0].PreviousGlobalSegmentIndex == -1
                && spline.Segments[^1].NextGlobalSegmentIndex == -1
                && MathF.Abs(spline.TotalLength - (spline.Segments[^1].CumulativeDistance + spline.Segments[^1].Length)) <= 0.01f
                && spline.Segments.All(segment => segment.OwnerSplineResourceId == Convert.ToInt32(spline.Properties["ResourceId"])
                    && segment.Length > 0 && float.IsFinite(segment.Length)
                    && segment.SerializedWord4 == Spline.SerializedSegmentWord4
                    && segment.SerializedWord8 == Spline.SerializedSegmentWord8
                    && segment.TailTag == Spline.SerializedSegmentTailTag
                    && segment.TailFlags == Spline.SerializedSegmentTailFlags)),
                $"{course.Code} has invalid Type-8 spline runtime fields");
            Assert.IsTrue(result.Scene.VisibilityCurtains.All(curtain => curtain.CornersSsx.Count == 4
                && curtain.Points.Count == 5 && curtain.Points[0] == curtain.Points[^1]
                && curtain.LoadedFlag == 1 && Convert.ToInt32(curtain.Properties["PayloadSize"]) == VisibilityCurtain.SerializedSize
                && curtain.RuntimeCandidateScore(curtain.BoundingSphereCenterSsx) == 0
                && MathF.Abs(MathF.Sqrt(curtain.CornersSsx.Max(curtain.RuntimeCandidateScore)) - curtain.BoundingSphereSsx.W) <= 0.1f
                && MathF.Abs(new Vector3(curtain.PlaneSsx.X, curtain.PlaneSsx.Y, curtain.PlaneSsx.Z).LengthSquared() - 1) <= 0.001f),
                $"{course.Code} has invalid Type-11 visibility-curtain runtime fields");
            Assert.IsTrue(result.Scene.NavigationPaths.Count > 0, $"{course.Code} has no decoded Type-14 navigation paths");
            Assert.IsTrue(result.Scene.NavigationPaths.All(x => x.Points.Count > 0 && x.Points.All(p => float.IsFinite(p.X + p.Y + p.Z))),
                $"{course.Code} has an empty or non-finite navigation path");
            Assert.IsTrue(result.Scene.Collisions.Count > 0, $"{course.Code} has no decoded Type-12 collision assets");
            Assert.IsTrue(result.Scene.Collisions.All(x => x.Submeshes.Count > 0 && x.Submeshes.All(s =>
                (s.Vertices.Count == 0 && s.Indices.Count == 0) || (s.Vertices.Count > 0 && s.Indices.Count >= 3
                    && s.Indices.All(i => i < s.Vertices.Count)))), $"{course.Code} has invalid collision topology");
            Assert.IsTrue(result.Scene.SphereTrees.Count > 0, $"{course.Code} has no decoded Type-12 sphere-tree collision assets");
            Assert.IsTrue(result.Scene.SphereTrees.All(x => x.Trees.Count > 0 && x.Trees.All(t => t.Levels.Count > 0
                && t.PackedNodeStorage.Length == t.PackedPayloadSize + t.AlignmentBytes
                && t.DecodedNodeMasks.Length == t.Levels.Sum(level => (long)level.Capacity)
                && t.NodeLevels.Count == t.Levels.Count
                && t.NodeLevels[^1].ChildMasks.All(mask => mask == 0)
                && t.Levels.Select((level, i) => level.Capacity == (1u << Math.Min(i * 3, 30))).All(valid => valid))),
                $"{course.Code} has invalid sphere-tree collision metadata");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 12), $"{course.Code} still has an undecoded Type-12 collision resource");
            Assert.IsTrue(result.Scene.SoundTriggerTables.Count > 0, $"{course.Code} has no decoded Type-13 sound-trigger tables");
            Assert.IsTrue(result.Scene.SoundTriggerTables.All(x => x.Bindings.All(binding => binding.BlockIndex >= 0
                && binding.BlockIndex < x.Blocks.Count)), $"{course.Code} has an invalid sound-trigger block reference");
            Assert.IsTrue(result.Scene.SoundTriggerTables.All(x => x.Blocks.All(block => block.SerializedSize == block.Data.Length
                && block.SpatialDescriptors.All(descriptor => descriptor.SerializedSize is 24 or 28 or 48
                    && Ssx3SoundTriggerDecoder.SpatialDescriptorKindName(descriptor.Kind) != "Unknown"
                    && (descriptor.Kind is not (0 or 2 or 3) || descriptor.Radius is > 0)
                    && (descriptor.Kind != 0 || descriptor.DistanceFalloffCurve is not null)
                    && (descriptor.Kind != 1 || descriptor.SemiAxisLengths is Vector3 axes
                        && axes.X > 0 && axes.Y > 0 && axes.Z > 0
                        && descriptor.OrientationAxis is Vector3 axis
                        && MathF.Abs(axis.LengthSquared() - 1f) <= 0.001f
                        && descriptor.DistanceFalloffCurve is not null)))),
                $"{course.Code} has invalid structured sound-trigger block data");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 13), $"{course.Code} still has an undecoded Type-13 sound-trigger resource");
            Assert.IsTrue(result.Scene.PlanarRoutes.All(x => x.Samples.Count > 0 && x.Markers.Count >= 2
                && x.Samples[0].Distance == 0 && Math.Abs(x.Samples[^1].Distance - x.TotalLength) <= 1f
                && x.Samples.Zip(x.Samples.Skip(1)).All(pair => pair.First.Distance <= pair.Second.Distance)
                && x.Samples.Zip(x.Samples.Skip(1)).All(pair =>
                {
                    var travel = pair.Second.Position - pair.First.Position;
                    return travel.LengthSquared() <= 0.0001f
                        || MathF.Abs(Vector2.Dot(Vector2.Normalize(travel), pair.First.LateralNormal)) <= 0.05f;
                })
                && x.Markers.All(marker => Ssx3PlanarRouteDecoder.MarkerKindName(marker.Kind) != "Unknown"
                    && Ssx3PlanarRouteDecoder.MarkerTextureName(marker.Kind) != "Unknown")
                && x.SelectRuntimeSampleIndex(float.MinValue) == 0
                && x.SelectRuntimeSampleIndex(float.MaxValue) == x.Samples.Count - 1),
                $"{course.Code} has invalid Type-21 radar-route data");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 21), $"{course.Code} still has an undecoded Type-21 radar-route resource");
            Assert.IsTrue(result.Scene.StructuredTables.All(x => x.Sections.All(section => section.Offset >= 0
                && section.Offset + section.Data.Length <= Convert.ToInt32(x.Properties["PayloadSize"]))),
                $"{course.Code} has invalid Type-15/16 structured-table data");
            Assert.IsTrue(result.Scene.StructuredTables.Where(x => x.ResourceType == 16 && x.Sections.Count > 0).All(x =>
                    x.RootRailReferences.All(reference => reference.TrackId == x.TrackId)
                    && x.RailReferenceSets.Select((set, index) => (set, index)).All(item => item.set.Index == item.index && item.set.Slots.Count == 6
                        && item.set.Slots.Where(reference => reference is not null).All(reference => reference!.TrackId == x.TrackId))
                    && x.ModifierProgramBlocks.Select((block, index) => (block, index)).All(item => item.block.Index == item.index
                        && item.block.ModifierSlots.Count == 13 && item.block.ModifierSlots.Where(reference => reference is not null)
                            .All(reference => reference!.TrackId == x.TrackId && reference.ProgramIndex < x.LunPrograms.Count))
                    && x.ModifierProgramGroups.Select((group, index) => (group, index)).All(item => item.group.Index == item.index
                        && item.group.Kind == 2 && item.group.ModifierSlots.Count == 13
                        && item.group.FirstBlockIndex + item.group.BlockCount <= x.ModifierProgramBlocks.Count
                        && item.group.ProgramReferences.Concat(item.group.ModifierSlots).Where(reference => reference is not null)
                            .All(reference => reference!.TrackId == x.TrackId && reference.ProgramIndex < x.LunPrograms.Count))
                    && x.ModifierProgramGroups.Sum(group => group.BlockCount) == x.ModifierProgramBlocks.Count
                    && x.LunPrograms.Select((program, index) => (program, index)).All(item => item.program.Index == item.index
                        && item.program.ProgramLength == item.program.Program.Length
                        && item.program.BytecodeLength + 16 == item.program.ProgramLength
                        && item.program.Instructions.Count > 0 && item.program.Instructions[^1].Opcode == 0x2a
                        && item.program.Instructions[^1].Operation == LunOperation.End
                        && item.program.Instructions.Select((instruction, instructionIndex) => (instruction, instructionIndex))
                            .All(instruction => instruction.instruction.SerializedSize == Ssx3StructuredTableDecoder.LunInstructionSize(instruction.instruction.Opcode)
                                && instruction.instruction.Operation == Ssx3StructuredTableDecoder.LunInstructionOperation(instruction.instruction.Opcode)
                                && (instruction.instruction.Operation == LunOperation.CallNative
                                    ? instruction.instruction.DestinationSlot == instruction.instruction.Operand0
                                        && instruction.instruction.NativeFunctionId == instruction.instruction.Operand1
                                        && instruction.instruction.ArgumentCount == instruction.instruction.Operand2
                                        && instruction.instruction.NativeFunctionName == Ssx3StructuredTableDecoder.LunNativeFunctionName(instruction.instruction.Operand1)
                                        && instruction.instruction.NativeFunctionSubsystem == Ssx3StructuredTableDecoder.LunNativeFunctionSubsystem(instruction.instruction.Operand1)
                                    : instruction.instruction.DestinationSlot is null && instruction.instruction.NativeFunctionId is null
                                        && instruction.instruction.ArgumentCount is null && instruction.instruction.NativeFunctionName is null
                                        && instruction.instruction.NativeFunctionSubsystem is null)
                                && instruction.instruction.Offset == item.program.Instructions.Take(instruction.instructionIndex).Sum(value => value.SerializedSize))
                        && item.program.Instructions.Sum(instruction => instruction.SerializedSize) == item.program.BytecodeLength
                        && item.program.Routines.Count == 1 + item.program.AdditionalDescriptors.Count
                        && item.program.Routines.Select(routine => routine.Offset).SequenceEqual(
                            new[] { item.program.PrimaryDescriptor }.Concat(item.program.AdditionalDescriptors)
                                .Select(descriptor => checked((int)descriptor.EntryWordOffset * 4)))
                        && item.program.DeclaredSize == 16 + item.program.Program.Length + item.program.AdditionalDescriptors.Count * 16
                        && item.program.PaddingBytes is >= 0 and <= 12)
                    && x.RailProgramRecords.Select((record, index) => (record, index)).All(item => item.record.Index == item.index
                        && item.record.Kind <= 3 && item.record.SerializedSize == 16 + item.record.Descriptors.Count * 12
                        && (x.TrackId == 0 && item.record.GeneratedRailId is null && item.record.GeneratedRailReference is null
                            || x.TrackId != 0 && item.record.GeneratedRailId == x.RailSplineMetadataEntries.Count + item.index
                                && item.record.GeneratedRailReference?.RailId == item.record.GeneratedRailId
                                && item.record.GeneratedRailReference?.TrackId == x.TrackId)
                        && item.record.ControlLow == 0 && item.record.ControlLow == (ushort)item.record.ControlWord
                        && item.record.ControlHigh == (ushort)(item.record.ControlWord >> 16)
                        && item.record.PrimaryInputRailReference == item.record.PrimaryRailReference
                        && item.record.SecondaryInputRailReference == item.record.SecondaryRailReference
                        && item.record.OutputDescriptors == item.record.Descriptors
                        && item.record.InputRailCount == (item.record.Kind is 0 or 2 ? 1 : 2)
                        && item.record.PrimaryInputRailReference is not null
                        && (item.record.Kind is 0 or 2
                            ? item.record.SecondaryInputRailReference is null
                            : item.record.SecondaryInputRailReference is not null)
                        && item.record.Descriptors.All(descriptor => float.IsFinite(descriptor.Scalar0) && float.IsFinite(descriptor.Scalar1)
                            && descriptor.Low == (ushort)descriptor.Word2 && descriptor.High == (ushort)(descriptor.Word2 >> 16)
                            && descriptor.Low <= (ushort)RailSplineRole.GrindRail && descriptor.Role == (RailSplineRole)descriptor.Low
                            && (descriptor.High == ushort.MaxValue && descriptor.SurfaceOverride is null
                                || descriptor.High <= (ushort)SsxSurfaceType.WipeoutRock
                                    && descriptor.SurfaceOverride == (SsxSurfaceType)descriptor.High))
                        && (item.record.PrimaryRailReference is null || item.record.PrimaryRailReference.TrackId == x.TrackId))
                    && x.RailProgramRecords.All(record => record.SecondaryRailReference is null || record.SecondaryRailReference.TrackId == x.TrackId)
                    && x.RailProgramReferenceIndices.All(index => index < x.RailProgramRecords.Count)
                    && (x.TrackId == 0 || x.RootRailReferences
                        .Concat(x.RailReferenceSets.SelectMany(set => set.Slots).Where(reference => reference is not null).Select(reference => reference!))
                        .Concat(x.RailProgramRecords.SelectMany(record => new[] { record.PrimaryRailReference, record.SecondaryRailReference })
                            .Where(reference => reference is not null).Select(reference => reference!))
                        .All(reference => reference.RailId < x.RailSplineMetadataEntries.Count
                            || x.RailProgramRecords.Any(record => record.GeneratedRailReference == reference)))
                    && x.RailSplineMetadataEntries.Count == result.Scene.Splines.Count(spline =>
                        Convert.ToInt32(spline.Properties["TrackId"]) == x.TrackId)
                    && x.RailSplineMetadataEntries.All(entry => entry.Low <= (ushort)RailSplineRole.GrindRail
                        && entry.High <= (ushort)SsxSurfaceType.WipeoutRock
                        && entry.Role == (RailSplineRole)entry.Low && entry.Surface == (SsxSurfaceType)entry.High)),
                $"{course.Code} has invalid Type-16 packed rail/program references");
            Assert.IsTrue(result.Scene.StructuredTables.Where(x => x.ResourceType == 15 && x.Sections.Count > 0).All(x =>
                    x.ModifierSections.All(section => section.TypeId == section.Slot + 1
                        && Ssx3StructuredTableDecoder.ModifierTypeName(section.TypeId) == section.TypeName
                        && section.HeaderSize == 12 + section.RecordCount * 8 && section.IndexRecordSize == 12
                        && section.IndexData.Length == 40 + section.SpatialIndex.EntryCount * 8
                        && section.SpatialIndex.Scale > 0
                        && float.IsFinite(section.SpatialIndex.Scale + section.SpatialIndex.Origin.X + section.SpatialIndex.Origin.Y
                            + section.SpatialIndex.Extent)
                        && section.SpatialIndex.SerializedCapacity == 0 && section.SpatialIndex.Reserved == 0
                        && section.SpatialIndex.DefaultLeafWord0 == 0 && section.SpatialIndex.DefaultLeafWord1 == uint.MaxValue
                        && section.SpatialIndex.SerializedNodeEndPointerPlaceholder == 0
                        && section.SpatialIndex.RootEntryIndex < section.SpatialIndex.EntryCount
                        && section.SpatialIndex.Entries.Count == section.SpatialIndex.EntryCount
                        && section.SpatialIndex.Entries.All(entry => entry.Kind == WorldModifierIndexEntryKind.RecordLeaf
                                && entry.ModifierRecordIndex is not null && entry.ModifierRecordIndex < section.RecordCount
                            || entry.Kind == WorldModifierIndexEntryKind.EmptyLeaf && entry.Word1 == uint.MaxValue
                            || entry.Kind == WorldModifierIndexEntryKind.Branch && entry.Children.Count == 4
                                && entry.Children.Select(child => child.Quadrant).SequenceEqual(Enum.GetValues<WorldModifierSpatialQuadrant>())
                                && entry.Children.All(child => child.EntryIndex < section.SpatialIndex.EntryCount
                                    && child.EntryIndex == child.Handle >> 1))
                        && section.SpatialIndex.Entries.All(entry => entry.Kind == WorldModifierIndexEntryKind.Branch
                            || entry.Children.Count == 0)
                        && section.SpatialIndex.EntryCount == section.SpatialIndex.Entries.Count(entry =>
                            entry.Kind == WorldModifierIndexEntryKind.Branch) * 4 + 1
                        && section.Records.Count == section.RecordCount
                        && section.Records.Select((record, index) => (record, index)).All(item => item.record.Index == item.index
                            && item.record.Offset >= section.Offset + section.HeaderSize
                            && item.record.Data.Length == Ssx3StructuredTableDecoder.ModifierRecordSize(section.TypeId)
                            && item.record.Words.Count * 4 == item.record.Data.Length
                            && (section.TypeId != 11 || item.record.Tags.Count == 4)
                            && (section.TypeId switch
                            {
                                1 => item.record.Words[0] == 0 && item.record.ReferencedResourceType == 8
                                    && item.record.ReferencedTrackId == x.TrackId && item.record.ReferencedResourceId is int resourceId
                                    && splineIds.Contains(ResourceKey(x.TrackId, resourceId)),
                                2 => item.record.Words[0] == 0 && item.record.ReferencedResourceType == 3
                                    && item.record.ReferencedTrackId == x.TrackId && item.record.ReferencedResourceId is int resourceId
                                    && propIds.Contains(ResourceKey(x.TrackId, resourceId)),
                                _ => item.record.ReferencedResourceType is null && item.record.ReferencedTrackId is null
                                    && item.record.ReferencedResourceId is null
                            })))),
                $"{course.Code} has invalid Type-15 World Painter records");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType is not (15 or 16)),
                $"{course.Code} still has an undecoded Type-15/16 structured-table resource");
            Assert.IsTrue(result.Scene.AudioBanks.All(x => x.EntryCount == 0 || (x.ReservedWords.Count == 2
                && x.SlotRelativeOffsets.Count == x.EntryCount
                && x.Sounds.Count == x.SlotRelativeOffsets.Count(offset => offset != 0)
                && x.Sounds.All(sound => sound.Platform == 5 && sound.InfoSections.Count > 0
                    && sound.InfoSections[^1].Terminator == 0xff
                    && sound.InfoSections.Take(sound.InfoSections.Count - 1).All(section => section.Terminator == 0xfe)
                    && sound.InfoSections.All(section => section.Codec == 4 && section.ChannelCount == 1
                        && section.RootMidiNote is >= 0 and <= 127
                        && section.MinimumVelocity is >= 0 and <= 127 && section.MaximumVelocity is >= 0 and <= 127
                        && section.MinimumVelocity <= section.MaximumVelocity
                        && section.MinimumMidiNote is >= 0 and <= 127 && section.MaximumMidiNote is >= 0 and <= 127
                        && section.MinimumMidiNote <= section.MaximumMidiNote
                        && section.ReleaseEnvelopeSegmentIndex >= -1
                        && section.ReleaseEnvelopeSegmentIndex < section.PlaybackEnvelopeSegmentCount
                        && section.PlaybackEnvelopeSegmentCount is >= 1 and <= 128
                        && section.InitialEnvelopeVolume is >= 0 and <= 127
                        && (section.PlaybackEnvelopeOffset is null && section.PlaybackEnvelopeSegments.Count == 0
                            || section.PlaybackEnvelopeOffset is not null
                            && section.PlaybackEnvelopeSegments.Count == section.PlaybackEnvelopeSegmentCount)
                        && section.PlaybackEnvelopeSegments.All(segment => segment.Volume is >= 0 and <= 127)
                        && section.SampleRate is 16_000 or 22_050 or 32_000 && section.SampleCount > 0
                        && (section.LoopStart is null || section.MicroTalkLoopRelativeOffset is not null)
                        && section.ChannelOffsets.Count == 1)))),
                $"{course.Code} has invalid Type-20 BNKl bank data");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 20), $"{course.Code} still has an undecoded Type-20 BNKl resource");
            Assert.IsTrue(result.Scene.AvalancheAnimations.All(x => x.Blocks.Count == 0 || x.Blocks.All(block =>
                    block.Data.Length == 12 + block.UnitCount * 300 && block.Frames.Count == block.UnitCount * 30
                    && block.RuntimeDurationSeconds == Math.Max(0, block.Frames.Count - 1) / 30f
                    && float.IsFinite(block.Origin.X + block.Origin.Y + block.Origin.Z)
                    && block.Frames.All(frame => float.IsFinite(frame.Position.X + frame.Position.Y + frame.Position.Z
                        + frame.SerializedScaleSsx.X + frame.SerializedScaleSsx.Y + frame.SerializedScaleSsx.Z
                        + frame.Scale.X + frame.Scale.Y + frame.Scale.Z
                        + frame.RotationAxis.X + frame.RotationAxis.Y + frame.RotationAxis.Z + frame.RotationAngleRadians)))
                && x.MetadataSegments.All(segment => segment.Parameters.Count == segment.ParameterCount
                    && segment.Pairs.Count == segment.PairCount
                    && segment.Data.Length == 4 + 8 * segment.ParameterCount + 4 * segment.PairCount
                    && segment.Parameters.All(parameter => float.IsFinite(parameter.TimeSeconds) && parameter.TimeSeconds >= 0)
                    && segment.Parameters.Select(parameter => parameter.TimeSeconds)
                        .SequenceEqual(segment.Parameters.Select(parameter => parameter.TimeSeconds).Order())
                    && segment.Pairs.Select(pair => pair.FrameIndex)
                        .SequenceEqual(segment.Pairs.Select(pair => pair.FrameIndex).Order())
                    && segment.Pairs.All(pair => pair.BlockIndex < x.Blocks.Count
                        && pair.FrameIndex < x.Blocks[pair.BlockIndex].Frames.Count))),
                $"{course.Code} has invalid Type-22 avalanche block data");
            Assert.IsTrue(result.Scene.UnknownSections.All(x => x.ResourceType != 22), $"{course.Code} still has an undecoded Type-22 avalanche resource");
            Assert.IsTrue(result.Scene.NavigationMarkers.All(x => Convert.ToBoolean(x.Properties["Marker"])),
                $"{course.Code} has an invalid Type-14 navigation marker");
            Assert.IsTrue(CourseCameraPlacement.TryFind(result.Scene, course.Code, out var pose), $"{course.Code} has no usable start camera pose");
            Assert.IsTrue(float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z + pose.Target.X + pose.Target.Y + pose.Target.Z), $"{course.Code} start camera is not finite");
            using var renderer = new SceneRenderer(); renderer.SetScene(result.Scene);
            Assert.IsTrue(renderer.FrameCourseStart(result.Scene, course.Code), $"{course.Code} start camera could not be applied");
            var visibleCenterGeometry = new[] { 450f, 550f, 650f }.Any(y => renderer.Pick(800, y, 1600, 900) is not null);
            Assert.IsTrue(visibleCenterGeometry, $"{course.Code} start camera does not keep scene geometry in the central viewport");
        }
        Assert.IsTrue(particleModels > 0 && particleEmitters > 0 && lights > 0 && halos > 0
            && nisReferenceTables > 0 && navigationMarkers > 0 && cameraTriggerTables > 0,
            "The playable-course corpus did not exercise every Type-4/5/6/7/14/17/18 decoder");
    }

    [TestMethod]
    [TestCategory("LocalGameData")]
    public void UserLocalCourseSubset_ExportsObjMtlAndDecodedPngTextures()
    {
        var projectPath = Environment.GetEnvironmentVariable("MOUNTAINIZER_TEST_PROJECT");
        if (string.IsNullOrWhiteSpace(projectPath)) Assert.Inconclusive("Set MOUNTAINIZER_TEST_PROJECT to a local imported project; game data is never committed.");
        var project = ProjectService.Open(projectPath!); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        var sdb = Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, new DiagnosticBag());
        var parsed = Ssx3SsbReader.ParseCourse(ProjectService.WorldFile(project, ".ssb"), sdb, Ssx3CourseCatalog.Courses.Single(x => x.Code == "EBC3"));
        var source = parsed.Scene; var subset = new MountainizerScene { Name = "EBC3 export regression" };
        subset.Terrain.Add(source.Terrain[0]);
        var prop = source.Props.First(x => !x.IsNonVisualGameplayProxy); subset.Props.Add(prop);
        static long Key(int track, int resource) => ((long)track << 32) | (uint)resource;
        var model = source.Models.Single(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])) == Key(prop.ModelTrackId, prop.ModelResourceId));
        subset.Models.Add(model);
        var materialKeys = model.Submeshes.Select(x => Key(x.MaterialTrackId, x.MaterialResourceId)).ToHashSet();
        subset.Materials.AddRange(source.Materials.Where(x => materialKeys.Contains(Key(x.TrackId, x.ResourceId))));
        var resolver = new SceneTextureResolver(source);
        subset.Textures.AddRange(resolver.Resolve(source.Terrain[0]).Concat(resolver.Resolve(model))
            .GroupBy(x => (x.TrackId, x.ResourceId)).Select(x => x.First()));

        var directory = Path.Combine(Path.GetTempPath(), "mountainizer-local-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exported = ObjExporter.ExportScene(subset, Path.Combine(directory, "EBC3.obj"));
            Assert.IsTrue(File.Exists(exported.ObjPath)); Assert.IsTrue(File.Exists(exported.MaterialPath));
            Assert.IsTrue(exported.TextureCount > 0); Assert.AreEqual(exported.TextureCount, Directory.GetFiles(exported.TextureDirectory, "*.png").Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
