using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Mountainizer.Core;
using Mountainizer.Export;
using Mountainizer.Formats;
using Mountainizer.Rendering;

namespace Mountainizer.Tests;

[TestClass]
public sealed class FormatTests
{
    [TestMethod]
    [DataRow("mdl_EBC3_E_Load_0")]
    [DataRow("mdl_EBC3_E_Unload_0")]
    [DataRow("mdl_EBC3_bcteleport_1001")]
    [DataRow("mdl_EBC3_fallingIceTrigger_1000")]
    [DataRow("mdl_EBC3_challenge_1000")]
    [DataRow("mdl_E_fenceb_r_proxy_2")]
    [DataRow("mdl_E_NIS_Transport_0")]
    [DataRow("mdl_EBC3_fallingpathaemitter_1001")]
    [DataRow("mdl_EBC3_ospreySpray_1000")]
    [DataRow("mdl_E_chimneysmoke_1000")]
    [DataRow("mdl_EBC3_firefly_1002")]
    [DataRow("mdl_EBC3_roadflare_1000")]
    [DataRow("mdl_EBC3_iceImpact_1019")]
    [DataRow("mdl_EBC3_cannonacharge_1000")]
    public void GameplayMarkerProps_AreHiddenFromNormalModelRendering(string name)
    {
        var source = new SourceByteRange("fixture", 0, 1, "prop", 0, SupportConfidence.Verified);
        var prop = new PropInstance(name, source, Matrix4x4.Identity, 0, 0, new Dictionary<string, object?>());
        Assert.IsTrue(prop.IsNonVisualGameplayProxy);
    }

    [TestMethod]
    [DataRow("mdl_E_fencebuv_1000")]
    [DataRow("mdl_EBC3_E_hubsign_post_4000")]
    public void LowPolyVisualProps_RemainVisible(string name)
    {
        var source = new SourceByteRange("fixture", 0, 1, "prop", 0, SupportConfidence.Verified);
        var prop = new PropInstance(name, source, Matrix4x4.Identity, 0, 0, new Dictionary<string, object?>());
        Assert.IsFalse(prop.IsNonVisualGameplayProxy);
    }

    [TestMethod]
    public void CourseCatalog_ContainsAllPlayableSsx3Courses()
    {
        Assert.AreEqual(17, Ssx3CourseCatalog.Courses.Count);
        CollectionAssert.AreEqual(new[]
        {
            "ARA1:Snow Jam:Race", "ASS1:R&B:Slopestyle", "BRA2:Metro-City:Race",
            "ABA1:Crow's Nest:Big Air", "BHP1:Disfunktion:Super Pipe", "ABC1:Happiness:Backcountry",
            "CRA3:Ruthless Ridge:Race", "DRA4:Intimidator:Race", "DSS2:Style Mile:Slopestyle",
            "CBA2:Launch Time:Big Air", "CHP2:Schizophrenia:Super Pipe", "DBC2:Ruthless:Backcountry",
            "ERA5:Gravitude:Race", "ESS3:Kick Doubt:Slopestyle", "EBA3:Much-2-Much:Big Air",
            "EHP3:Perpendiculous:Super Pipe", "EBC3:The Throne:Backcountry"
        }, Ssx3CourseCatalog.Courses.Select(x => $"{x.Code}:{x.Name}:{x.Discipline}").ToArray());
    }
    [TestMethod]
    public void BinaryReader_ReadsExplicitLittleAndBigEndianValues()
    {
        var reader = new BinarySpanReader([0x34, 0x12, 0x01, 0x02, 0x03, 0x04]);
        Assert.AreEqual((ushort)0x1234, reader.ReadUInt16Little());
        Assert.AreEqual(0x01020304u, reader.ReadUInt32Big());
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void BinaryReader_RejectsOutOfBoundsRead()
    {
        Assert.ThrowsException<Mountainizer.Core.FormatException>(() => ReadTooMuch([1, 2, 3]));
        static void ReadTooMuch(byte[] bytes) { var reader = new BinarySpanReader(bytes); reader.ReadUInt32Little(); }
    }

    [TestMethod]
    public void RefPack_DecodesLiteralBlock()
    {
        byte[] fixture = [0x10, 0xFB, 0, 0, 4, 0xE0, (byte)'T', (byte)'E', (byte)'S', (byte)'T', 0xFC];
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("TEST"), RefPackDecoder.Decompress(fixture));
    }

    [TestMethod]
    public void RefPack_RejectsLookbackBeforeOutput()
    {
        byte[] fixture = [0x10, 0xFB, 0, 0, 3, 0x00, 0x00, 0xFC];
        Assert.ThrowsException<Mountainizer.Core.FormatException>(() => RefPackDecoder.Decompress(fixture));
    }

    [TestMethod]
    public void BigArchive_ParsesMixedEndianAndExtractsEntry()
    {
        var path = Path.GetTempFileName(); var output = Path.GetTempFileName();
        try
        {
            var bytes = MakeBig(); File.WriteAllBytes(path, bytes);
            var archive = BigArchive.Open(path); Assert.AreEqual(BigEndianLayout.MixedArchiveSizeLittle, archive.Layout);
            Assert.AreEqual(1, archive.Entries.Count); Assert.AreEqual("data/test.bin", archive.Entries[0].Name);
            archive.Extract(archive.Entries[0], output); CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [TestMethod]
    public void Sdb_ParsesBoundedSyntheticLocations()
    {
        var data = new byte[Ssx3Sdb.HeaderSize + 2 * Ssx3Sdb.LocationSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 3);
        WriteLocation(data.AsSpan(80), "FIRST", 0, 1); WriteLocation(data.AsSpan(168), "SECOND", 1, 2);
        var sdb = Ssx3Sdb.Parse(data, "synthetic.sdb", new DiagnosticBag());
        Assert.AreEqual(2, sdb.Areas.Count); Assert.AreEqual(2, sdb.Areas[0].GroupCount); Assert.AreEqual(2, sdb.Areas[1].GroupCount);
    }

    [TestMethod]
    public void TerrainConversion_ConstantDifferenceCoefficientsProduceFiniteMesh()
    {
        var coefficients = Enumerable.Repeat(Vector3.Zero, 16).ToArray(); coefficients[15] = new(10, 20, 30);
        var points = TerrainMeshBuilder.DecodeControlPoints(coefficients); var mesh = TerrainMeshBuilder.Tessellate(points, 2);
        Assert.AreEqual(9, mesh.Positions.Count); Assert.AreEqual(24, mesh.Indices.Count);
        Assert.IsTrue(mesh.Positions.All(x => float.IsFinite(x.X + x.Y + x.Z)));
    }

    [TestMethod]
    public void TerrainConversion_MapsLightmapRectangleIntoAtlasCoordinates()
    {
        var points = Enumerable.Repeat(Vector3.Zero, 16).ToArray();
        var mesh = TerrainMeshBuilder.Tessellate(points, 1, lightmapRectangle: new Vector4(0.25f, 0.5f, 0.125f, 0.25f));
        CollectionAssert.AreEqual(new[]
        {
            new Vector2(0.25f, 0.5f), new Vector2(0.375f, 0.5f),
            new Vector2(0.25f, 0.75f), new Vector2(0.375f, 0.75f)
        }, mesh.LightmapTextureCoordinates!.ToArray());
    }

    [TestMethod]
    public void CoordinateConversion_MapsSsxZUpWorldToMountainizerYUpWorld()
    {
        Assert.AreEqual(new Vector3(1, 3, -2), Ssx3Coordinates.ToMountainizer(new Vector3(1, 2, 3)));
    }

    [TestMethod]
    public void TextureCoordinates_KeepMdrSignsUprightAndRotateTerrainRampTiles()
    {
        var uv = new Vector2(0.2f, 0.7f);
        Assert.AreEqual(uv, TextureCoordinateConvention.ModelToOpenGl(uv));
        Assert.AreEqual(new Vector2(0.2f, 0.3f), TextureCoordinateConvention.TerrainToOpenGl(uv));
        Assert.AreEqual(new Vector2(0.3f, 0.8f), TextureCoordinateConvention.TerrainToOpenGl(uv, 238));
        Assert.AreEqual(new Vector2(0.3f, 0.8f), TextureCoordinateConvention.TerrainToOpenGl(uv, 109));
        Assert.AreEqual(TextureCoordinateConvention.TerrainToOpenGl(uv), TextureCoordinateConvention.TerrainToOpenGl(uv, 42));
    }

    [TestMethod]
    public void TerrainRampTextureSet_CoversAllDecodedArrowTileVariants()
    {
        CollectionAssert.AreEquivalent(new[] { 109, 112, 114, 235, 238, 241, 378, 383, 384 },
            Enumerable.Range(0, 512).Where(TextureCoordinateConvention.IsRampTerrainTexture).ToArray());
    }

    [TestMethod]
    public void PropClassification_SeparatesNonVisualGameplayProxiesFromVisibleWallsAndFences()
    {
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var properties = new Dictionary<string, object?>();
        Assert.IsTrue(new PropInstance("mdl_ASS1_fencecollision_1000", source, Matrix4x4.Identity, 1, 1, properties).IsCollisionProxy);
        Assert.IsTrue(new PropInstance("mdl_ARA1_fCollision_s1_1", source, Matrix4x4.Identity, 1, 1, properties).IsCollisionProxy);
        Assert.IsFalse(new PropInstance("mdl_ASS1_noColliderockwall_0", source, Matrix4x4.Identity, 1, 1, properties).IsCollisionProxy);
        Assert.IsFalse(new PropInstance("mdl_ASS1_fence_1001", source, Matrix4x4.Identity, 1, 1, properties).IsCollisionProxy);
        Assert.IsTrue(new PropInstance("mdl_ARA1_Reset_Plane_1021", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
        Assert.IsTrue(new PropInstance("mdl_ARA1_bcvolume_1002", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
        Assert.IsTrue(new PropInstance("mdl_ARA1_RaceRideState_0", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
        Assert.IsTrue(new PropInstance("mdl_ARA1_startfireTrig_1000", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
        Assert.IsFalse(new PropInstance("mdl_ARA1_wallrocks_1031", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
        Assert.IsFalse(new PropInstance("mdl_A_con_bboard_group3_med_1001", source, Matrix4x4.Identity, 1, 1, properties).IsNonVisualGameplayProxy);
    }

    [TestMethod]
    public void PropClassification_ProvidesCachedCategoryAndReason()
    {
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var prop = new PropInstance("mdl_EBC3_roadflare_1000", source, Matrix4x4.Identity, 1, 1, new Dictionary<string, object?>());

        var first = prop.Classification;
        var second = prop.Classification;

        Assert.AreEqual(PropRenderCategory.EffectMarker, first.Category);
        Assert.IsFalse(first.IsVisual);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.Reason));
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void InspectionFrustum_CullsBoundsBehindAndOutsideTheCamera()
    {
        var camera = new InspectionCamera();
        camera.SetPose(new Vector3(0, 0, -10), Vector3.Zero);

        Assert.IsTrue(InspectionFrustum.Contains(camera, new SceneBounds(new(-1), new(1)), 16f / 9f));
        Assert.IsFalse(InspectionFrustum.Contains(camera, new SceneBounds(new(-1, -1, -102), new(1, 1, -100)), 16f / 9f));
        Assert.IsFalse(InspectionFrustum.Contains(camera, new SceneBounds(new(999, -1, -1), new(1001, 1, 1)), 16f / 9f));
    }

    [TestMethod]
    public void CourseCameraPlacement_UsesNamedStartAndAimsTowardFinish()
    {
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Synthetic" };
        scene.Props.Add(new("mdl_TEST_startgate_mainmodules", source, Matrix4x4.CreateTranslation(0, 1000, 0), 0, 0, new Dictionary<string, object?>()));
        scene.Props.Add(new("mdl_TEST_finish_gate", source, Matrix4x4.CreateTranslation(0, 0, 10000), 0, 0, new Dictionary<string, object?>()));
        Assert.IsTrue(CourseCameraPlacement.TryFind(scene, "TEST", out var pose));
        Assert.IsTrue(pose.UsedStartGate);
        Assert.IsTrue(pose.Position.Y > 1000);
        Assert.IsTrue(pose.Target.Z > pose.Position.Z);
    }

    [TestMethod]
    public void ObjExport_WritesVerticesFacesMaterialsAndPngTextures()
    {
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Synthetic" };
        scene.Textures.Add(new("Terrain", source, 1, 1, 0, 5, [10, 20, 30, 255], new Dictionary<string, object?>()));
        scene.Textures.Add(new("Model", source, 1, 1, 2, 9, [40, 50, 60, 128], new Dictionary<string, object?>()));
        scene.Materials.Add(new("Model material", source, 2, 3, 9, new Dictionary<string, object?>()));
        scene.Terrain.Add(new("Patch", source, [], mesh, 0, 5, 0, new Dictionary<string, object?>()));
        scene.Models.Add(new("Model", source, mesh, [new(mesh, 2, 3)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 2 }));
        scene.Props.Add(new("Prop", source, Matrix4x4.CreateTranslation(10, 0, 0), 1, 2, new Dictionary<string, object?>()));
        var directory = Path.Combine(Path.GetTempPath(), "mountainizer-export-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scene.obj");
        try
        {
            var result = ObjExporter.ExportScene(scene, path); var text = File.ReadAllText(path); var mtl = File.ReadAllText(result.MaterialPath);
            StringAssert.Contains(text, "mtllib scene.mtl"); StringAssert.Contains(text, "usemtl texture_0_5"); StringAssert.Contains(text, "usemtl texture_2_9");
            StringAssert.Contains(text, "v 0 0 0"); StringAssert.Contains(text, "v 10 0 0");
            StringAssert.Contains(text, "f 1/1/1 2/2/2 3/3/3"); StringAssert.Contains(text, "f 4/4/4 5/5/5 6/6/6");
            StringAssert.Contains(mtl, "map_Kd scene_textures/texture_0_5.png"); StringAssert.Contains(mtl, "map_Kd scene_textures/texture_2_9.png");
            Assert.AreEqual(2, result.TextureCount);
            foreach (var texturePath in Directory.GetFiles(result.TextureDirectory, "*.png"))
                CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(texturePath)[..8]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ViewportPicking_CenterRaySelectsVisibleTerrainBounds()
    {
        var mesh = new MeshData([new(-10, 0, -10), new(10, 0, -10), new(0, 0, 10)], [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "terrain", 0, SupportConfidence.Verified);
        var patch = new TerrainPatch("Pick me", source, [], mesh, 0, 0, 0, new Dictionary<string, object?>());
        var scene = new MountainizerScene { Name = "Picking" }; scene.Terrain.Add(patch);
        using var renderer = new SceneRenderer(); renderer.SetScene(scene);
        Assert.AreSame(patch, renderer.Pick(400, 300, 800, 600));
    }

    [TestMethod]
    public void ViewportPicking_UsesPropTrianglesInsteadOfNearestOverlappingBounds()
    {
        var source = new SourceByteRange("fixture", 0, 1, "model", 0, SupportConfidence.Verified);
        var miss = Mesh([new(-10, -10, 0), new(10, -10, 0), new(10, 1, 0)]);
        var hit = Mesh([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)]);
        var scene = new MountainizerScene { Name = "Triangle picking" };
        scene.Models.Add(new("Bounding box only", source, miss, [new(miss, 0, 0)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 1 }));
        scene.Models.Add(new("Actual hit", source, hit, [new(hit, 0, 0)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 2 }));
        var decoy = new PropInstance("Visual decoy", source, Matrix4x4.Identity, 1, 1, new Dictionary<string, object?>());
        var target = new PropInstance("Visual target", source, Matrix4x4.CreateTranslation(0, 0, 5), 1, 2, new Dictionary<string, object?>());
        scene.Props.Add(decoy); scene.Props.Add(target);
        using var renderer = new SceneRenderer(); renderer.SetScene(scene); renderer.Camera.SetPose(new(0, 0, -10), Vector3.Zero);

        Assert.AreSame(target, renderer.Pick(400, 300, 800, 600));

        static MeshData Mesh(IReadOnlyList<Vector3> positions) => new(positions, positions.Select(_ => Vector3.UnitZ).ToArray(),
            positions.Select(_ => Vector2.Zero).ToArray(), [0u, 1u, 2u]);
    }

    [TestMethod]
    public void PropVisibilityFilters_ControlPickingAndHiddenInstances()
    {
        var source = new SourceByteRange("fixture", 0, 1, "model", 0, SupportConfidence.Verified);
        var mesh = new MeshData([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, Vector2.Zero, Vector2.Zero], [0u, 1u, 2u]);
        var scene = new MountainizerScene { Name = "Visibility" };
        scene.Models.Add(new("Model", source, mesh, [new(mesh, 0, 0)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 1 }));
        var visual = new PropInstance("mdl_TEST_wall", source, Matrix4x4.Identity, 1, 1, new Dictionary<string, object?>());
        var collision = new PropInstance("mdl_TEST_collision", source, Matrix4x4.CreateTranslation(0, 0, 5), 1, 1, new Dictionary<string, object?>());
        scene.Props.Add(visual); scene.Props.Add(collision);
        using var renderer = new SceneRenderer(); renderer.SetScene(scene); renderer.Camera.SetPose(new(0, 0, -10), Vector3.Zero);

        Assert.AreSame(visual, renderer.Pick(400, 300, 800, 600));
        renderer.ShowOnlyPropCategory(PropRenderCategory.Collision);
        Assert.AreSame(collision, renderer.Pick(400, 300, 800, 600));
        Assert.IsTrue(renderer.HideProp(collision)); Assert.IsTrue(renderer.IsPropHidden(collision));
        Assert.IsNull(renderer.Pick(400, 300, 800, 600));
        renderer.ShowAllHiddenProps();
        Assert.AreSame(collision, renderer.Pick(400, 300, 800, 600));
        renderer.ShowAllPropCategories(); Assert.IsTrue(renderer.IsPropCategoryVisible(PropRenderCategory.Visual));
    }

    [TestMethod]
    public void SceneRenderer_ReusesPreparedSceneWhenSameInstanceIsSetAgain()
    {
        var mesh = new MeshData([new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)], [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "model", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Cached" };
        scene.Terrain.Add(new("Patch", source, [], mesh, 0, 0, 0, new Dictionary<string, object?>()));
        scene.Models.Add(new("Model", source, mesh, [new(mesh, 0, 0)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 2 }));
        var prop = new PropInstance("Prop", source, Matrix4x4.Identity, 1, 2, new Dictionary<string, object?>()); scene.Props.Add(prop);
        using var renderer = new SceneRenderer(); renderer.SetScene(scene);
        Assert.IsTrue(renderer.FrameProp(scene, prop)); Assert.IsTrue(renderer.IsIsolated);
        renderer.SetScene(scene);
        Assert.IsTrue(renderer.IsIsolated, "Setting the unchanged scene should preserve cached renderer state");
    }

    [TestMethod]
    public void SceneTextureResolver_FindsTexturesForTerrainMaterialsModelsAndProps()
    {
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "asset", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Textures" };
        var modelTexture = new TextureAsset("Model texture", source, 1, 1, 2, 9, [1, 2, 3, 255], new Dictionary<string, object?>());
        var terrainTexture = new TextureAsset("Terrain texture", source, 1, 1, 7, 9, [4, 5, 6, 255], new Dictionary<string, object?>());
        scene.Textures.Add(modelTexture); scene.Textures.Add(terrainTexture);
        var material = new MaterialAsset("Material", source, 2, 3, 9, new Dictionary<string, object?>()); scene.Materials.Add(material);
        var model = new ModelAsset("Model", source, mesh, [new(mesh, 2, 3)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 4 }); scene.Models.Add(model);
        var prop = new PropInstance("Prop", source, Matrix4x4.Identity, 1, 4, new Dictionary<string, object?>()); scene.Props.Add(prop);
        var terrain = new TerrainPatch("Terrain", source, [], mesh, 7, 9, 0, new Dictionary<string, object?>()); scene.Terrain.Add(terrain);

        var resolver = new SceneTextureResolver(scene);
        Assert.AreSame(modelTexture, resolver.Resolve(material).Single());
        Assert.AreSame(modelTexture, resolver.Resolve(model).Single());
        Assert.AreSame(modelTexture, resolver.Resolve(prop).Single());
        Assert.AreSame(terrainTexture, resolver.Resolve(terrain).Single());
        Assert.AreSame(resolver.Resolve(prop), resolver.Resolve(prop), "Resolved previews should be cached");
    }

    [TestMethod]
    public void SceneTextureResolver_KeepsDiffuseAndLightmapResourceIdsIndependent()
    {
        var source = new SourceByteRange("fixture", 0, 1, "asset", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Lightmaps" };
        var diffuse = new TextureAsset("Diffuse", source, 1, 1, 255, 77, [1, 2, 3, 255],
            new Dictionary<string, object?> { ["TextureUsage"] = TextureUsage.Diffuse.ToString(), ["GroupIndex"] = 19 });
        var lightmap = new TextureAsset("Lightmap", source, 1, 1, 255, 77, [4, 5, 6, 255],
            new Dictionary<string, object?> { ["TextureUsage"] = TextureUsage.Lightmap.ToString(), ["GroupIndex"] = 19 });
        scene.Textures.Add(diffuse); scene.Textures.Add(lightmap);
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var terrain = new TerrainPatch("Terrain", source, [], mesh, 255, 77, 77,
            new Dictionary<string, object?> { ["GroupIndex"] = 19 });

        var resolved = new SceneTextureResolver(scene).Resolve(terrain);

        Assert.AreEqual(2, resolved.Count);
        Assert.AreSame(diffuse, resolved[0]);
        Assert.AreSame(lightmap, resolved[1]);
    }

    [TestMethod]
    public void SceneTextureResolver_UsesNearestStreamingGroupTextureBank()
    {
        var source = new SourceByteRange("fixture", 0, 1, "asset", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Texture banks" };
        var early = new TextureAsset("Early bank", source, 1, 1, 255, 20, [1, 2, 3, 255],
            new Dictionary<string, object?> { ["GroupIndex"] = 5 });
        var late = new TextureAsset("Late bank", source, 1, 1, 255, 20, [4, 5, 6, 255],
            new Dictionary<string, object?> { ["GroupIndex"] = 20 });
        scene.Textures.Add(early); scene.Textures.Add(late);
        var earlyMaterial = new MaterialAsset("Early material", source, 7, 1, 20,
            new Dictionary<string, object?> { ["GroupIndex"] = 6 });
        var lateMaterial = new MaterialAsset("Late material", source, 7, 2, 20,
            new Dictionary<string, object?> { ["GroupIndex"] = 19 });
        var resolver = new SceneTextureResolver(scene);

        Assert.AreSame(early, resolver.Resolve(earlyMaterial).Single());
        Assert.AreSame(late, resolver.Resolve(lateMaterial).Single());
    }

    [TestMethod]
    public void InspectionCamera_ZoomIsControlledAndOrbitPivotPreservesPosition()
    {
        var camera = new InspectionCamera();
        var initialDistance = camera.Distance;
        var initialTarget = camera.Target;
        var initialYaw = camera.Yaw;
        var initialPitch = camera.Pitch;
        camera.Zoom(1);
        Assert.IsTrue(camera.Distance < initialDistance);
        Assert.IsTrue(camera.Distance > initialDistance * 0.9f);
        Assert.AreEqual(initialTarget, camera.Target);
        Assert.AreEqual(initialYaw, camera.Yaw);
        Assert.AreEqual(initialPitch, camera.Pitch);

        var position = camera.Position;
        camera.SetOrbitPivot(new Vector3(1000, 200, -500));
        Assert.IsTrue(Vector3.Distance(position, camera.Position) < 0.1f);
    }

    [TestMethod]
    public void InspectionCamera_FlyKeepsUsefulSpeedNearAnOrbitPivotAndAfterRotation()
    {
        var camera = new InspectionCamera();
        camera.SetPose(new Vector3(0, 1000, -10000), new Vector3(0, 1000, 0), 2500);
        camera.SetOrbitPivot(camera.Position + camera.Forward);
        Assert.IsTrue(camera.MoveSpeed >= 1250);

        camera.Rotate(150, -40);
        var position = camera.Position;
        camera.Fly(0, 0, 1, 0.1f);
        Assert.IsTrue(Vector3.Distance(position, camera.Position) >= 124);
    }

    private static byte[] MakeBig()
    {
        var headerSize = 16 + 8 + "data/test.bin".Length + 1; var dataOffset = 64; var result = new byte[dataOffset + 4];
        Encoding.ASCII.GetBytes("BIGF").CopyTo(result, 0); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 1); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), (uint)headerSize);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), (uint)dataOffset); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), 4);
        Encoding.ASCII.GetBytes("data/test.bin\0").CopyTo(result, 24); result[dataOffset] = 1; result[dataOffset + 1] = 2; result[dataOffset + 2] = 3; result[dataOffset + 3] = 4; return result;
    }
    private static void WriteLocation(Span<byte> target, string name, uint firstChunk, uint metadataCount)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(target); BinaryPrimitives.WriteUInt32LittleEndian(target[20..], metadataCount); BinaryPrimitives.WriteUInt32LittleEndian(target[24..], firstChunk);
    }
}
