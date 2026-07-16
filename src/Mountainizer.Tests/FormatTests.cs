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
    public void Ssx3TextureDecoder_ExposesSerializedRendererDispatchState()
    {
        var data = new byte[0x84];
        data[0] = 5; data[4] = 1; data[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TextureAsset.Ssx3RendererStateOffset), 0x20003000);
        data[0x80] = 10; data[0x81] = 20; data[0x82] = 30; data[0x83] = 0x40;
        var source = new SourceByteRange("fixture", 0, data.Length, "type 9", 0, SupportConfidence.Verified);

        var texture = Ssx3TextureDecoder.Decode(data, source, 255, 7);

        Assert.AreEqual(0x20003000u, texture.SerializedRendererStateWord0C);
        Assert.AreEqual(0u, texture.RendererDispatchState);
        Assert.AreEqual("0x20003000", texture.Properties["SerializedRendererStateWord0C"]);
        Assert.AreEqual("0x00000000", texture.Properties["RendererDispatchState"]);
    }

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
        var descriptorOffset = (Ssx3Sdb.HeaderSize + 2 * Ssx3Sdb.LocationSize + 15) & ~15;
        var data = new byte[descriptorOffset + 3 * Ssx3Sdb.ChunkDescriptorSize + 3 * Ssx3Sdb.SubChunkDescriptorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 3);
        WriteLocation(data.AsSpan(80), "FIRST", 0, 1); WriteLocation(data.AsSpan(168), "SECOND", 1, 2);
        var subChunkOffset = descriptorOffset + 3 * Ssx3Sdb.ChunkDescriptorSize;
        for (ushort i = 0; i < 3; i++)
        {
            var descriptor = data.AsSpan(subChunkOffset + i * Ssx3Sdb.SubChunkDescriptorSize);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor, (ushort)(10 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[2..], i);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[(4 + 13 * 2)..], (ushort)(20 + i));
        }
        var sdb = Ssx3Sdb.Parse(data, "synthetic.sdb", new DiagnosticBag());
        Assert.AreEqual(2, sdb.Areas.Count); Assert.AreEqual(2, sdb.Areas[0].GroupCount); Assert.AreEqual(2, sdb.Areas[1].GroupCount);
        Assert.AreEqual(3, sdb.Chunks.Count); Assert.AreEqual(3, sdb.SubChunks.Count);
        Assert.AreEqual((ushort)2, sdb.SubChunks[2].SubChunkId); Assert.AreEqual((ushort)12, sdb.SubChunks[2].ResourceCount);
        Assert.AreEqual((ushort)22, sdb.SubChunks[2].DeclaredType9TextureCount);
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
    public void TerrainSecondaryPass_DecodesRetailDestinationAlphaBlendState()
    {
        var source = new SourceByteRange("fixture", 0, TerrainPatch.SerializedSize, "Type 1", 0,
            SupportConfidence.High);
        var mesh = new MeshData([], [], [], []);
        var patch = new TerrainPatch("secondary", source, [], mesh, 0, 1, 2,
            new Dictionary<string, object?>())
        {
            RenderFlags = 0x0060,
            SecondaryTextureResourceId = 3
        };

        Assert.IsTrue(patch.RequestsRuntimeSecondaryPass);
        Assert.IsTrue(patch.RuntimeSecondaryPassUsesDestinationAlpha);
        Assert.AreEqual(17, patch.RuntimeSecondaryPassAlphaSelector);
        Assert.AreEqual(0x58UL, patch.RuntimeSecondaryPassGsAlphaRegister);
        Assert.AreEqual("(Cs - 0) * Ad + Cd", patch.RuntimeSecondaryPassBlendEquation);

        patch = patch with { RenderFlags = 0x0020 };
        Assert.AreEqual(2, patch.RuntimeSecondaryPassAlphaSelector);
        Assert.AreEqual(0x0000008000000068UL, patch.RuntimeSecondaryPassGsAlphaRegister);
        Assert.AreEqual("(Cs - 0) * FIX(128) + Cd = Cs + Cd", patch.RuntimeSecondaryPassBlendEquation);
    }

    [TestMethod]
    public void TerrainPrimaryLightmapPass_ExposesExactGsContextEquations()
    {
        Assert.AreEqual(1, TerrainPatch.RuntimePrimaryAlphaSelector);
        Assert.AreEqual(0x2AUL, TerrainPatch.RuntimePrimaryGsAlphaRegister);
        Assert.AreEqual("Cs (source replacement)", TerrainPatch.RuntimePrimaryBlendEquation);
        Assert.AreEqual(8, TerrainPatch.RuntimeLightmapAlphaSelector);
        Assert.AreEqual(0x81UL, TerrainPatch.RuntimeLightmapGsAlphaRegister);
        Assert.AreEqual("(Cd - Cs) * As", TerrainPatch.RuntimeLightmapBlendEquation);
    }

    [TestMethod]
    public void CoordinateConversion_MapsSsxZUpWorldToMountainizerYUpWorld()
    {
        var ssx = new Vector3(1, 2, 3);
        Assert.AreEqual(new Vector3(1, 3, -2), Ssx3Coordinates.ToMountainizer(ssx));
        Assert.AreEqual(ssx, Ssx3Coordinates.ToSsx3(Ssx3Coordinates.ToMountainizer(ssx)));
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
    public void Ssx3VifDecoder_FramesPackedUnpackMaskAndRuntimeMscalPlaceholder()
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x00000000); // NOP
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x01000101); // STCYCL 1,1
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x00000000); // NOP
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x6f048026); // four packed V4-5 values
        data.AsSpan(16, 8).Fill(0xa4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0x20000000); // STMASK
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 0xdeadbeef);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 0x04000001); // ITOP 1

        var decoded = Ssx3VifDecoder.Decode(data, 0x200);

        Assert.IsTrue(decoded.Complete);
        CollectionAssert.AreEqual(new[] { "NOP", "STCYCL", "NOP", "UNPACK_V4_5", "NOP", "NOP", "STMASK",
            "RuntimeMscalPlaceholder", "ITOP" }, decoded.Commands.Select(command => command.Name).ToArray());
        Assert.AreEqual(8, decoded.Commands[3].PayloadSize); Assert.AreEqual(0x210, decoded.Commands[3].PayloadOffset);
        Assert.AreEqual(0x26, decoded.Commands[3].UnpackDestinationAddress); Assert.IsTrue(decoded.Commands[3].UnpackUsesTops);
        Assert.IsFalse(decoded.Commands[3].UnpackIsUnsigned); Assert.IsFalse(decoded.Commands[3].UnpackIsMasked);
        Assert.AreEqual(4, decoded.Commands[6].PayloadSize);
        var colors = new InstanceDmaSourceBlock(0x200, 3, data, 0xdeadbeef, 0x04000001,
            decoded.Commands, decoded.Complete).DecodeVertexColors();
        Assert.AreEqual(4, colors.Count); Assert.AreEqual(new PackedVertexColor5(0xa4a4, 4, 5, 9, 1), colors[0]);
        Assert.AreEqual(new Vector4(4 / 31f, 5 / 31f, 9 / 31f, 1), colors[0].Normalized);
    }

    [TestMethod]
    public void ObjExport_UsesInverseTransposeNormalsForNonUniformScale()
    {
        var sourceNormal = Vector3.Normalize(new Vector3(1, 1, 0));
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [sourceNormal, sourceNormal, sourceNormal],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Scaled normals" };
        scene.Models.Add(new("Model", source, mesh, [new(mesh, 0, 0)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 2 }));
        scene.Props.Add(new("Prop", source, Matrix4x4.CreateScale(2, 1, 1), 1, 2, new Dictionary<string, object?>()));
        var directory = Path.Combine(Path.GetTempPath(), "mountainizer-normal-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = ObjExporter.ExportScene(scene, Path.Combine(directory, "scene.obj"));
            var values = File.ReadLines(result.ObjPath).First(x => x.StartsWith("vn ", StringComparison.Ordinal)).Split(' ')
                .Skip(1).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var expected = Vector3.Normalize(new Vector3(0.5f, 1, 0));
            Assert.AreEqual(expected.X, values[0], 0.00001f);
            Assert.AreEqual(expected.Y, values[1], 0.00001f);
            Assert.AreEqual(expected.Z, values[2], 0.00001f);
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
    public void SceneTextureResolver_UsesSerializedPropTextureSubChunk()
    {
        var source = new SourceByteRange("fixture", 0, 1, "asset", 0, SupportConfidence.Verified);
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var scene = new MountainizerScene { Name = "Prop texture banks" };
        var selected = new TextureAsset("Selected bank", source, 1, 1, 255, 20, [1, 2, 3, 255],
            new Dictionary<string, object?> { ["GroupIndex"] = 5 });
        var nearerToModel = new TextureAsset("Model-nearest bank", source, 1, 1, 255, 20, [4, 5, 6, 255],
            new Dictionary<string, object?> { ["GroupIndex"] = 20 });
        scene.Textures.Add(selected); scene.Textures.Add(nearerToModel);
        scene.Materials.Add(new MaterialAsset("Material", source, 7, 1, 20,
            new Dictionary<string, object?> { ["GroupIndex"] = 19 }));
        scene.Models.Add(new ModelAsset("Model", source, mesh, [new(mesh, 7, 1)],
            new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 4, ["GroupIndex"] = 19 }));
        var prop = new PropInstance("Prop", source, Matrix4x4.Identity, 1, 4,
            new Dictionary<string, object?> { ["TextureSubChunkId"] = 5 });

        Assert.AreSame(selected, new SceneTextureResolver(scene).Resolve(prop).Single());
    }

    [TestMethod]
    public void Ssx3AipDecoder_DecodesWeightedPointsAndTimedEvents()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Ssx3AipDecoder.KnownMagic); writer.Write(1u);
        foreach (var value in new uint[] { 2, 100, 4, 46, 101, 4, 1 }) writer.Write(value);
        writer.Write(2u); writer.Write(1u);
        WriteVector3(writer, new(100, 200, 300)); WriteVector3(writer, new(100, 200, 300)); WriteVector3(writer, new(110, 220, 330));
        WriteVector4(writer, new(1, 0, 0, 10)); WriteVector4(writer, new(0, 1, 0, 20));
        writer.Write(110u); writer.Write(9u); writer.Write(0.25f); writer.Write(0.75f);
        writer.Write(1u);
        writer.Write(1u); writer.Write(0u); writer.Write(4u); writer.Write(1234f);
        writer.Write(1u); writer.Write(0u);
        WriteVector3(writer, new(10, 20, 30)); WriteVector3(writer, Vector3.Zero); WriteVector3(writer, new(20, 30, 40));
        WriteVector4(writer, new(0, 0, 1, 25));
        writer.Write(6u);
        for (var i = 0; i < 6; i++) { writer.Write(0u); writer.Write(0u); }
        writer.Write(1u); writer.Write(7u); writer.Write(1);
        WriteVector3(writer, new(12, 22, 32)); WriteVector3(writer, Vector3.UnitX);
        writer.Write(0u); writer.Write(0u);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 14", 0, SupportConfidence.Low);

        var decoded = Ssx3AipDecoder.Decode(stream.ToArray(), source, 3, 8);

        Assert.AreEqual(2, decoded.Paths.Count); Assert.AreEqual(0, decoded.TrailingBytes);
        var path = decoded.Paths[0]; Assert.AreEqual(2, path.Points.Count); Assert.AreEqual(1, path.Events.Count);
        Assert.AreEqual(NavigationPathKind.Ai, path.Kind); Assert.AreEqual(2, path.TaggedProperties.Count);
        Assert.AreEqual(100u, path.TaggedProperties[0].Kind); Assert.AreEqual(46u, path.TaggedProperties[0].UInt32Value);
        Assert.AreEqual(101u, path.TaggedProperties[1].Kind); Assert.AreEqual(1u, path.TaggedProperties[1].UInt32Value);
        Assert.AreEqual(46u, path.AiPathMetadata); Assert.AreEqual(true, path.Respawnable); Assert.IsNull(path.DistanceToFinish);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new Vector3(110, 200, 300)), path.Points[0]);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new Vector3(100, 220, 300)), path.Points[1]);
        Assert.AreEqual(new Vector3(1, 0, 0), path.EncodedPoints[0].EncodedVectorSsx); Assert.AreEqual(10f, path.EncodedPoints[0].Weight);
        Assert.AreEqual(30f, path.TotalLength);
        Assert.AreEqual(new NavigationPathEvent(110, 9, 0.25f, 0.75f) { RuntimeKindIndex = 16 }, path.Events[0]);
        var trackPath = decoded.Asset.TrackPaths[0];
        Assert.AreEqual(NavigationPathKind.Track, trackPath.Kind); Assert.AreEqual(1234f, trackPath.DistanceToFinish);
        Assert.AreEqual(25f, trackPath.TotalLength); Assert.AreEqual(6, decoded.Asset.TailPairs.Count);
        var link = decoded.Asset.Links[0]; Assert.AreEqual(7u, link.Value); Assert.AreEqual(1, link.RawKind);
        Assert.AreEqual(2, link.RuntimeKindIndex); Assert.AreEqual(Vector3.UnitX, link.DirectionSsx);
        Assert.AreEqual(0, link.AiPathIndex); Assert.AreEqual(0, link.TrackPathIndex);
    }

    [TestMethod]
    public void Ssx3CollisionDecoder_DecodesVersionOneTopology()
    {
        var data = new byte[196];
        using (var stream = new MemoryStream(data, writable: true))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(16); writer.Write(176); writer.Write(192);
            writer.Write((ushort)2); writer.Write((ushort)4); writer.Write(20); writer.Write(28); writer.Write(64); writer.Write(128);
            writer.Write(new byte[] { 0, 1, 2, 0, 2, 3 });
            stream.Position = 44; WriteVector3(writer, Vector3.Zero); WriteVector3(writer, new(1, 1, 0));
            stream.Position = 80;
            WriteVector4(writer, new(0, 0, 0, 1)); WriteVector4(writer, new(1, 0, 0, 1));
            WriteVector4(writer, new(1, 1, 0, 1)); WriteVector4(writer, new(0, 1, 0, 1));
            stream.Position = 144; WriteVector4(writer, new(0, 0, 1, 0)); WriteVector4(writer, new(0, 0, 1, 0));
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 12", 0, SupportConfidence.Low);

        var decoded = Ssx3CollisionDecoder.Decode(data, source, 2, 4);

        Assert.AreEqual(1, decoded.Submeshes.Count); var mesh = decoded.Submeshes[0];
        Assert.AreEqual(4, mesh.Vertices.Count); Assert.AreEqual(6, mesh.Indices.Count); Assert.AreEqual(2, mesh.FaceNormals.Count);
        CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 0, 2, 3 }, mesh.Indices.ToArray());
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new Vector3(1, 1, 0)), mesh.Vertices[2]);
        Assert.AreEqual(1, mesh.TriangleBatches.Count);
        Assert.AreEqual(new CollisionTriangleBatch(0, 2,
            SceneBounds.FromPoints(mesh.Vertices)), mesh.TriangleBatches[0]);
        CollectionAssert.AreEqual(new byte[2], mesh.IndexPadding);
        CollectionAssert.AreEqual(new byte[12], mesh.TriangleBatchPadding);
        CollectionAssert.AreEqual(new byte[16], decoded.RuntimeScratchHeader);
        CollectionAssert.AreEqual(new byte[4], decoded.SubmeshPointerScratch);
        Assert.AreEqual(SupportConfidence.Medium, decoded.Source.Confidence);
    }

    [TestMethod]
    public void Ssx3InstanceDmaDecoder_DecodesRelocationProgramAndSourceBlock()
    {
        var data = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x98), 0xa0);
        WriteTag(0xa0, 12, Ps2DmaTagId.Ref, 0x30);
        WriteTag(0xb0, 3, Ps2DmaTagId.Ref, 0x30);
        WriteTag(0xc0, 0, Ps2DmaTagId.Ret, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xf0), 0x20000000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xf8), 0xdeadbeef);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xfc), 0x04000001);
        var source = new SourceByteRange("fixture", 0, data.Length, "Type 3", 0, SupportConfidence.Medium);

        var decoded = Ssx3InstanceDmaDecoder.Decode(data, source);

        Assert.AreEqual(0xa0, decoded.ExtensionOffset);
        Assert.AreEqual(1, decoded.Programs.Count);
        Assert.AreEqual(2, decoded.Programs[0].Relocations.Count);
        Assert.AreEqual(DmaRelocationTarget.ModelData, decoded.Programs[0].Relocations[0].Target);
        Assert.AreEqual(DmaRelocationTarget.InstanceExtension, decoded.Programs[0].Relocations[1].Target);
        Assert.AreEqual(Ps2DmaTagId.Ret, decoded.Programs[0].ReturnTag.Id);
        Assert.AreEqual(3, decoded.SourceBlocks[0].QuadwordCount);
        Assert.AreEqual(0xdeadbeefu, decoded.SourceBlocks[0].TerminalPlaceholder);
        Assert.AreEqual(48, decoded.StructuralBytes);
        Assert.AreEqual(48, decoded.SourceBytes);

        void WriteTag(int offset, int qwc, Ps2DmaTagId id, uint address)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)qwc | (uint)id << 28);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), address);
        }
    }

    [TestMethod]
    public void Ssx3SphereTreeDecoder_DecodesHierarchyMetadataAndPackedStorage()
    {
        var data = new byte[208];
        using (var stream = new MemoryStream(data, writable: true))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)3); writer.Write((ushort)1); writer.Write(16); writer.Write(160); writer.Write(172);
            writer.Write(3); writer.Write(5); writer.Write(1u); writer.Write(1);
            WriteVector3(writer, Vector3.Zero);
            WriteVector3(writer, new(1, 2, 3));
            foreach (var value in new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }) writer.Write(value);
            foreach (var value in new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }) writer.Write(value);
            writer.Write(10f); writer.Write(0f); writer.Write(1u);
            writer.Write(5f); writer.Write(2f); writer.Write(8u);
            writer.Write(new byte[] { 0xFF, 0x03, 0x07, 0x00, 0x00, 0xAA, 0xBB, 0xCC });
            writer.Write(0xDEADBEEFu); writer.Write(0xDEADBEEFu); writer.Write(0u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 12/v3", 0, SupportConfidence.Low);

        var decoded = Ssx3SphereTreeDecoder.Decode(data, source, 2, 9);

        Assert.AreEqual(1, decoded.Trees.Count); var tree = decoded.Trees[0];
        Assert.AreEqual(new Vector3(1, 3, -2), tree.RetainedMetadataVector);
        CollectionAssert.AreEqual(new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }, tree.RetainedSymmetricMatrix.ToArray());
        CollectionAssert.AreEqual(new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }, tree.RetainedInverseSymmetricMatrix.ToArray());
        Assert.IsFalse(tree.RetainedMatrixMetadataConsumedByRetailRuntime);
        Assert.AreEqual(true, decoded.Properties["RetainedMatrixMetadataCopiedByRetailLoader"]);
        Assert.AreEqual(false, decoded.Properties["RetainedMatrixMetadataConsumedByRetailRuntime"]);
        Assert.AreEqual(2, tree.Levels.Count); Assert.AreEqual(new SphereTreeLevel(10, 0, 1), tree.Levels[0]);
        Assert.AreEqual(new SphereTreeLevel(5, 2, 8), tree.Levels[1]); Assert.AreEqual(5, tree.PackedPayloadSize);
        Assert.AreEqual(3, tree.AlignmentBytes);
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0x03, 0x07, 0x00, 0x00, 0xAA, 0xBB, 0xCC }, tree.PackedNodeStorage);
        CollectionAssert.AreEqual(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0 }, tree.DecodedNodeMasks);
        Assert.AreEqual(1, tree.NodeLevels[0].ReferencedNodeCount);
        Assert.AreEqual(2, tree.NodeLevels[0].ReferencedChildCount);
        Assert.AreEqual(2, tree.NodeLevels[1].ReferencedNodeCount);
    }

    [TestMethod]
    public void Ssx3SoundTriggerDecoder_DecodesBindingsAndSharedTrailingBlocks()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0xCCCCCC00u); writer.Write(0xCCCCCCCCu); writer.Write(1.24f); writer.Write(3u);
        WriteBinding(writer, 10, 11, 2, 300, 88); WriteBinding(writer, 20, 21, 4, 500, 88); WriteBinding(writer, 30, 31, 6, 700, 104);
        writer.Write(2u); writer.Write(0u); writer.Write(14u); writer.Write(16u);
        writer.Write(1u); writer.Write(1u); writer.Write(47u); writer.Write(0u); writer.Write(90u);
        writer.Write(1f); writer.Write(2f); writer.Write(3f); writer.Write(8325f); writer.Write(2f); writer.Write((byte)0xFF);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 13", 0, SupportConfidence.Low);

        var decoded = Ssx3SoundTriggerDecoder.Decode(stream.ToArray(), source, 1, 0);

        Assert.AreEqual(3, decoded.Bindings.Count); Assert.AreEqual(2, decoded.Blocks.Count);
        Assert.AreEqual(0, decoded.Bindings[0].BlockIndex); Assert.AreEqual(0, decoded.Bindings[1].BlockIndex);
        Assert.AreEqual(1, decoded.Bindings[2].BlockIndex); Assert.AreEqual(2, decoded.Bindings[0].ObjectTrackId);
        Assert.AreEqual(300, decoded.Bindings[0].ObjectResourceId); Assert.AreEqual(88, decoded.Blocks[0].RelativeOffset);
        Assert.AreEqual(new SoundTriggerBindingIdentity(10, 11), decoded.Bindings[0].SerializedIdentity);
        Assert.AreEqual((11UL << 32) | 10UL, decoded.Bindings[0].SerializedIdentity.PackedLittleEndian);
        Assert.AreEqual(new PackedObjectReference(2, 300), decoded.Bindings[0].AnchorObjectReference);
        CollectionAssert.AreEqual(new uint[] { 14, 16 }, decoded.Blocks[0].TriggerInfoIds.ToArray());
        CollectionAssert.AreEqual(new uint[] { 14, 16 }, decoded.Blocks[0].SharedTriggerInfoIds.ToArray());
        Assert.AreEqual(1, decoded.Blocks[1].SpatialDescriptors.Count);
        var sphere = decoded.Blocks[1].SpatialDescriptors[0];
        Assert.AreEqual(new Vector3(1, 3, -2), sphere.Position);
        CollectionAssert.AreEqual(new[] { 8325f, 2f }, sphere.Parameters.ToArray());
        Assert.AreEqual(8325f, sphere.Radius);
        Assert.AreEqual(SoundTriggerFalloffCurve.OneMinusDistance, sphere.DistanceFalloffCurve);
        Assert.IsNull(sphere.SemiAxisLengths); Assert.IsNull(sphere.OrientationAxis);
    }

    [TestMethod]
    public void Ssx3SoundTriggerDecoder_DecodesMissingAnchorSentinel()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0xCCCCCC00u); writer.Write(0xCCCCCCCCu); writer.Write(1.24f); writer.Write(1u);
        writer.Write(0x11223344u); writer.Write(0x55667788u); writer.Write(0xCCCCCCCCu);
        writer.Write(uint.MaxValue); writer.Write(40u); writer.Write(0xCCCCCCCCu);
        writer.Write(1u); writer.Write(0u); writer.Write(16u); writer.Write((byte)0xFF);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 13", 0, SupportConfidence.Low);

        var decoded = Ssx3SoundTriggerDecoder.Decode(stream.ToArray(), source, 1, 0);

        Assert.IsNull(decoded.Bindings[0].AnchorObjectReference);
        Assert.AreEqual(-1, decoded.Bindings[0].ObjectTrackId);
        Assert.AreEqual(-1, decoded.Bindings[0].ObjectResourceId);
        Assert.AreEqual("5566778811223344", decoded.Bindings[0].SerializedIdentity.ToString());
    }

    [TestMethod]
    public void Ssx3SoundTriggerDecoder_ExposesExecutableTriggerInfoCatalog()
    {
        Assert.AreEqual(SoundTriggerInfoKind.BuiltInSlot,
            Ssx3SoundTriggerDecoder.TriggerInfoDefinition(0)!.Kind);
        var numeric = Ssx3SoundTriggerDecoder.TriggerInfoDefinition(42)!;
        Assert.AreEqual(SoundTriggerInfoKind.IndexedBankSound, numeric.Kind);
        Assert.AreEqual(5u, numeric.SoundBankId); Assert.AreEqual("CROWD", numeric.SoundBankName);
        Assert.AreEqual(1u, numeric.SoundIndex);
        Assert.AreEqual("MOUNTAIN", Ssx3SoundTriggerDecoder.SoundBankName(2));
        Assert.AreEqual("TRACK_BANK_0", Ssx3SoundTriggerDecoder.SoundBankName(8));
        Assert.AreEqual("TRACK_BANK_1", Ssx3SoundTriggerDecoder.SoundBankName(9));
        var audio = Ssx3SoundTriggerDecoder.TriggerInfoDefinition(88)!;
        Assert.AreEqual(SoundTriggerInfoKind.NamedAudioEvent, audio.Kind);
        Assert.AreEqual("FlagWind_loop", audio.Name);
        Assert.AreEqual(SoundTriggerInfoKind.CrowdInstanceActivation,
            Ssx3SoundTriggerDecoder.TriggerInfoDefinition(77)!.Kind);
        Assert.IsNull(Ssx3SoundTriggerDecoder.TriggerInfoDefinition(94));
    }

    [TestMethod]
    public void SoundTriggerRandomizedBankSound_DecodesThreeBucketsAndImplicitRemainder()
    {
        var record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x10), 8);
        for (var index = 0; index < 4; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x14 + index * 4), (uint)(100 + index));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x24), 10);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x28), 20);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x2C), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x30), 0xCCCCCCCC);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x34), 100);

        var definition = Ssx3SoundTriggerDecoder.DecodeRandomizedBankSoundDefinition(record);
        Assert.AreEqual(8u, definition.SoundBankId);
        Assert.AreEqual(0xCCCCCCCCu, definition.SelectionIgnoredWord);
        Assert.AreEqual(100, definition.TotalWeight);
        Assert.AreEqual(100u, definition.SelectSoundIndexForScaledWeight(9));
        Assert.AreEqual(101u, definition.SelectSoundIndexForScaledWeight(10));
        Assert.AreEqual(102u, definition.SelectSoundIndexForScaledWeight(30));
        Assert.AreEqual(103u, definition.SelectSoundIndexForScaledWeight(60));
    }

    [TestMethod]
    public void SoundTriggerBindingIdentity_IsCaseSensitiveMd5NamePrefix()
    {
        const string name = "mdl_A_con_pinwheel_tall_med_n_01_00013";
        var identity = SoundTriggerBindingIdentity.FromName(name);
        Assert.AreEqual(0x265E5AA0u, identity.Word0);
        Assert.AreEqual(0x96BB5F8Bu, identity.Word1);
        Assert.AreEqual("96BB5F8B265E5AA0", identity.ToString());
        Assert.IsTrue(identity.MatchesName(name));
        Assert.IsFalse(identity.MatchesName(name.ToLowerInvariant()));
    }

    [TestMethod]
    public void Ssx3SoundTriggerDecoder_SupportsEveryRuntimeDescriptorKind()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0xCCCCCC00u); writer.Write(0xCCCCCCCCu); writer.Write(1.24f); writer.Write(1u);
        WriteBinding(writer, 1, 2, 3, 4, 40);
        writer.Write(0u); writer.Write(4u);
        WriteSoundDescriptor(writer, 0, 10, 5); WriteSoundDescriptor(writer, 1, 11, 10);
        WriteSoundDescriptor(writer, 2, 12, 10); WriteSoundDescriptor(writer, 3, 13, 4); writer.Write((byte)0xFF);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 13", 0, SupportConfidence.Low);

        var decoded = Ssx3SoundTriggerDecoder.Decode(stream.ToArray(), source, 3, 0);

        CollectionAssert.AreEqual(new[] { 28, 48, 48, 24 }, decoded.Blocks[0].SpatialDescriptors.Select(x => x.SerializedSize).ToArray());
        CollectionAssert.AreEqual(new[] { "Sphere", "Oriented Ellipsoid", "Directional Cone", "Sphere (Fixed Falloff)" },
            decoded.Blocks[0].SpatialDescriptors.Select(x => Ssx3SoundTriggerDecoder.SpatialDescriptorKindName(x.Kind)).ToArray());
        var descriptors = decoded.Blocks[0].SpatialDescriptors;
        Assert.AreEqual(3f, descriptors[0].Radius);
        Assert.AreEqual(SoundTriggerFalloffCurve.SquaredOneMinusDistance, descriptors[0].DistanceFalloffCurve);
        Assert.AreEqual(new Vector3(3, 4, 5), descriptors[1].SemiAxisLengths);
        Assert.AreEqual(new Vector3(6, 8, -7), descriptors[1].OrientationAxis);
        Assert.IsNull(descriptors[1].DistanceFalloffCurve);
        Assert.AreEqual(3f, descriptors[2].Radius); Assert.AreEqual(8f, descriptors[2].ConeCosineThreshold);
        Assert.IsNull(descriptors[2].DistanceFalloffCurve); Assert.IsNull(descriptors[2].AngularFalloffCurve);
        Assert.AreEqual(3f, descriptors[3].Radius); Assert.IsNull(descriptors[3].DistanceFalloffCurve);
    }

    [TestMethod]
    public void Ssx3SoundTriggerDecoder_EvaluatesAllExecutableFalloffCurves()
    {
        var expectedAtHalfDistance = new[] { 0.75f, 0.6f, 0.5f, 0.4f, 0.25f, 1f };
        foreach (var curve in Enum.GetValues<SoundTriggerFalloffCurve>())
        {
            Assert.AreEqual(1f, Ssx3SoundTriggerDecoder.EvaluateSpatialFalloff(curve, 0f), 0.000001f, curve.ToString());
            Assert.AreEqual(expectedAtHalfDistance[(int)curve],
                Ssx3SoundTriggerDecoder.EvaluateSpatialFalloff(curve, 0.5f), 0.000001f, curve.ToString());
            Assert.AreEqual(0f, Ssx3SoundTriggerDecoder.EvaluateSpatialFalloff(curve, 1f), 0.000001f, curve.ToString());
        }
        Assert.AreEqual(0.5f, Ssx3SoundTriggerDecoder.EvaluateSpatialFalloff(
            SoundTriggerFalloffCurve.OuterThirtyPercentLinear, 0.85f), 0.000001f);
    }

    [TestMethod]
    public void Ssx3PlanarRouteDecoder_DecodesSamplesAndDistanceMarkers()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(3u); writer.Write(20u); writer.Write(4u); writer.Write(80u); writer.Write(10f);
        WriteRouteSample(writer, Vector2.UnitX, new(1, 2), 0);
        WriteRouteSample(writer, Vector2.Normalize(new(1, 1)), new(4, 6), 5);
        WriteRouteSample(writer, Vector2.UnitY, new(7, 8), 10);
        writer.Write(0u); writer.Write(0f); writer.Write(2u); writer.Write(10f);
        writer.Write(1u); writer.Write(2.5f); writer.Write(1u); writer.Write(7.5f);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 21", 0, SupportConfidence.Low);

        var decoded = Ssx3PlanarRouteDecoder.Decode(stream.ToArray(), source, 9, 0);

        Assert.AreEqual(10f, decoded.TotalLength); Assert.AreEqual(3, decoded.Samples.Count); Assert.AreEqual(4, decoded.Markers.Count);
        Assert.AreEqual(new Vector2(4, 6), decoded.Samples[1].Position); Assert.AreEqual(5f, decoded.Samples[1].Distance);
        Assert.AreEqual(Vector2.Normalize(new(1, 1)), decoded.Samples[1].LateralNormal);
        Assert.AreEqual(new PlanarRouteMarker(1, 7.5f), decoded.Markers[3]);
        Assert.AreEqual("Radar / minimap course line", decoded.Properties["Role"]);
        Assert.AreEqual("Start", Ssx3PlanarRouteDecoder.MarkerKindName(0));
        Assert.AreEqual("Checkpoint", Ssx3PlanarRouteDecoder.MarkerKindName(1));
        Assert.AreEqual("Finish", Ssx3PlanarRouteDecoder.MarkerKindName(2));
        Assert.AreEqual("chkstart", Ssx3PlanarRouteDecoder.MarkerTextureName(0));
        Assert.AreEqual("chkpt", Ssx3PlanarRouteDecoder.MarkerTextureName(1));
        Assert.AreEqual("chkstart", Ssx3PlanarRouteDecoder.MarkerTextureName(2));

        Assert.AreEqual(0, decoded.SelectRuntimeSampleIndex(-10));
        Assert.AreEqual(0, decoded.SelectRuntimeSampleIndex(4.999f));
        Assert.AreEqual(1, decoded.SelectRuntimeSampleIndex(5f));
        Assert.AreEqual(2, decoded.SelectRuntimeSampleIndex(100f));
        var projection = decoded.ProjectRuntimePosition(new Vector2(7, 9), 5f);
        Assert.AreEqual(1, projection.SampleIndex);
        Assert.AreEqual(5f, projection.SampleDistance);
        Assert.AreEqual(Vector2.Dot(new Vector2(3, 3), Vector2.Normalize(new(1, 1))), projection.LateralOffset, 0.00001f);
    }

    [TestMethod]
    public void Ssx3StructuredTableDecoder_DecodesType15WorldPainterSectionsAndRecords()
    {
        var data = new byte[296]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(16u); writer.Write(14u); writer.Write(64u); writer.Write(80u); writer.Write(188u);
            for (var i = 2; i < 13; i++) writer.Write(uint.MaxValue);
            stream.Position = 80; WriteModifierSection(writer, 1, 0f, 7u);
            stream.Position = 188; WriteModifierSection(writer, 2, 0f, 9u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 15", 0, SupportConfidence.Low);

        var decoded = Ssx3StructuredTableDecoder.DecodeType15(data, source, 3, 0);

        Assert.AreEqual(3, decoded.Sections.Count); Assert.AreEqual(16, decoded.Sections[0].Data.Length);
        Assert.AreEqual(108, decoded.Sections[1].Data.Length); Assert.AreEqual(108, decoded.Sections[2].Data.Length);
        Assert.AreEqual(2, decoded.WorldPainterSections.Count); Assert.AreEqual("Mix", decoded.ModifierSections[0].TypeName);
        Assert.AreEqual(1, decoded.ModifierSections[0].Records.Count); Assert.AreEqual(8, decoded.ModifierSections[0].Records[0].Data.Length);
        Assert.AreEqual(0f, decoded.ModifierSections[0].Records[0].ScalarValues[0]);
        Assert.AreEqual(0f, decoded.ModifierSections[0].Records[0].WorldPainterBlendControl);
        Assert.AreEqual(1, decoded.ModifierSections[0].Records[0].TypeId);
        Assert.AreEqual(1, decoded.ModifierSections[0].Records[0].WorldPainterPropertyCount);
        CollectionAssert.AreEqual(new uint[] { 0, 7 }, decoded.ModifierSections[0].Records[0].Words.ToArray());
        Assert.AreEqual(8, decoded.ModifierSections[0].Records[0].ReferencedResourceType);
        Assert.AreEqual(3, decoded.ModifierSections[0].Records[0].ReferencedTrackId);
        Assert.AreEqual(7, decoded.ModifierSections[0].Records[0].ReferencedResourceId);
        Assert.AreEqual(5, decoded.ModifierSections[0].SpatialIndex.EntryCount);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, decoded.ModifierSections[0].SpatialIndex.Entries[0].ChildEntryIndices.ToArray());
        CollectionAssert.AreEqual(Enum.GetValues<WorldModifierSpatialQuadrant>(),
            decoded.ModifierSections[0].SpatialIndex.Entries[0].Children.Select(child => child.Quadrant).ToArray());
        Assert.IsTrue(decoded.ModifierSections[0].SpatialIndex.Entries.Skip(1).All(entry => entry.Children.Count == 0));
        Assert.AreEqual(new Vector2(10, 20), decoded.ModifierSections[0].SpatialIndex.Origin);
        Assert.AreEqual(16384f, decoded.ModifierSections[0].SpatialIndex.Extent);
        Assert.AreEqual(0, decoded.ModifierSections[0].SpatialIndex.SerializedCapacity);
        Assert.AreEqual((ushort)0, decoded.ModifierSections[0].SpatialIndex.RootHandle);
        Assert.AreEqual(uint.MaxValue, decoded.ModifierSections[0].SpatialIndex.DefaultLeafWord1);
        Assert.AreEqual(BitConverter.SingleToUInt32Bits(0.001f), decoded.ModifierSections[0].SpatialIndex.SerializedNodePointerPlaceholder);
        Assert.AreEqual("Ambience", decoded.ModifierSections[1].TypeName);
        Assert.AreEqual(3, decoded.ModifierSections[1].Records[0].ReferencedResourceType);
        Assert.AreEqual(3, decoded.ModifierSections[1].Records[0].ReferencedTrackId);
        Assert.AreEqual(9, decoded.ModifierSections[1].Records[0].ReferencedResourceId);
        CollectionAssert.AreEqual(
            new[] { "MusicTrigger", "Mix", "Ambience", "Speech", "Camera", "Fog", "LightGlow", "ScreenTint", "SkyBox", "Sun", "Surface", "Lighting", "Weather", "Danger" },
            Enumerable.Range(0, 14).Select(Ssx3StructuredTableDecoder.WorldPainterTypeName).ToArray());
        Assert.AreEqual("tWPIGD_Camera", Ssx3StructuredTableDecoder.WorldPainterRuntimeClassName(4));
        Assert.AreEqual(24, Ssx3StructuredTableDecoder.WorldPainterRuntimeObjectSize(0));
        Assert.AreEqual(16, Ssx3StructuredTableDecoder.WorldPainterRuntimeObjectSize(4));
        Assert.AreEqual(12, Ssx3StructuredTableDecoder.WorldPainterRecordSize(13));
        Assert.AreEqual(24, Ssx3StructuredTableDecoder.WorldPainterRuntimeObjectSize(13));
        Assert.AreEqual("Density", Ssx3StructuredTableDecoder.WorldPainterPropertyName(5, 0));
        Assert.AreEqual("PS2MinimumIntensityCutoff", Ssx3StructuredTableDecoder.WorldPainterPropertyName(6, 0));
        Assert.AreEqual("PS2BlendTexture3", Ssx3StructuredTableDecoder.WorldPainterPropertyName(6, 6));
        Assert.AreEqual("FillBlue", Ssx3StructuredTableDecoder.WorldPainterPropertyName(7, 5));
        Assert.AreEqual("ColourGreen", Ssx3StructuredTableDecoder.WorldPainterPropertyName(9, 3));
        Assert.AreEqual("SnowfallIntensity", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 0));
        Assert.AreEqual("LightningChance", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 10));
        Assert.AreEqual("SnowFlakeColourAlpha", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 11));
        Assert.AreEqual("SnowFlakeColourG", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 13));
        Assert.AreEqual("SnowFluffColourAlpha", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 15));
        Assert.AreEqual("SnowFluffColourB", Ssx3StructuredTableDecoder.WorldPainterPropertyName(12, 18));
        Assert.AreEqual("Property0", Ssx3StructuredTableDecoder.WorldPainterPropertyName(4, 0));

        static void WriteModifierSection(BinaryWriter writer, uint typeId, float value, uint word)
        {
            writer.Write(20u); writer.Write(1u); writer.Write(12u); writer.Write(typeId); writer.Write(100u);
            writer.Write(2f); writer.Write(10f); writer.Write(20f); writer.Write(5u);
            writer.Write(0u); writer.Write(0u);
            writer.Write(0u); writer.Write(uint.MaxValue);
            writer.Write(0.001f); writer.Write(0u);
            writer.Write(0x00040003u); writer.Write(0x00090007u);
            writer.Write(0u); writer.Write(0u);
            writer.Write(0u); writer.Write(uint.MaxValue);
            writer.Write(0u); writer.Write(0u);
            writer.Write(0u); writer.Write(uint.MaxValue);
            writer.Write(value); writer.Write(word);
        }
    }

    [TestMethod]
    public void Ssx3StructuredTableDecoder_DecodesType16BoundedSections()
    {
        var data = new byte[260]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x1000u); writer.Write(0x3800u); writer.Write(0u); writer.Write(0.89f); writer.Write(1u);
            writer.Write(92u); writer.Write(1u); writer.Write(96u);
            writer.Write(0u); writer.Write(120u); writer.Write(0u); writer.Write(120u); writer.Write(0u); writer.Write(120u);
            writer.Write(1u); writer.Write(120u); writer.Write(160u); writer.Write(2u); writer.Write(188u);
            writer.Write(1u); writer.Write(192u); writer.Write(16u); writer.Write(196u);
            stream.Position = 92; writer.Write(0x00000103u); for (var i = 0; i < 6; i++) writer.Write(uint.MaxValue);
            stream.Position = 120; writer.Write(124u); stream.Position = 124;
            writer.Write(Encoding.ASCII.GetBytes("LUN\0")); writer.Write(20u); writer.Write(36u); writer.Write(36u);
            writer.Write(0x2au); writer.Write(-20); writer.Write(0u); writer.Write(2u); writer.Write(0u);
            stream.Position = 160; writer.Write(1u); writer.Write(0x00210000u); writer.Write(0x00000103u); writer.Write(uint.MaxValue);
            writer.Write(0x7149f2cau); writer.Write(0u); writer.Write(0xffff0003u);
            stream.Position = 188; writer.Write((ushort)0); writer.Write((ushort)0);
            stream.Position = 192; writer.Write(0u);
            stream.Position = 196; for (var i = 0; i < 16; i++) writer.Write((uint)(10 << 16 | i % 4));
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 16", 0, SupportConfidence.Low);

        var decoded = Ssx3StructuredTableDecoder.DecodeType16(data, source, 3, 0);

        Assert.AreEqual(7, decoded.Sections.Count); Assert.AreEqual(4, decoded.Sections[0].Data.Length);
        Assert.AreEqual(24, decoded.Sections[1].Data.Length); Assert.AreEqual(40, decoded.Sections[2].Data.Length);
        Assert.AreEqual(new PackedRailReference(0x00000103, 3, 1), decoded.RootRailReferences[0]);
        Assert.AreEqual(1, decoded.RailReferenceSets.Count); Assert.IsTrue(decoded.RailReferenceSets[0].Slots.All(x => x is null));
        Assert.AreEqual(1, decoded.LunPrograms.Count); Assert.AreEqual(20, decoded.LunPrograms[0].ProgramLength);
        Assert.AreEqual(36, decoded.LunPrograms[0].DeclaredSize); Assert.AreEqual(0, decoded.LunPrograms[0].PaddingBytes);
        Assert.AreEqual(1, decoded.LunPrograms[0].Instructions.Count); Assert.AreEqual((byte)0x2a, decoded.LunPrograms[0].Instructions[^1].Opcode);
        Assert.AreEqual(LunOperation.End, decoded.LunPrograms[0].Instructions[^1].Operation);
        Assert.IsNull(decoded.LunPrograms[0].Instructions[^1].NativeFunctionId);
        Assert.IsNull(decoded.LunPrograms[0].Instructions[^1].NativeFunctionSubsystem);
        Assert.AreEqual(1, decoded.LunPrograms[0].Routines.Count); Assert.AreEqual(0, decoded.LunPrograms[0].PrimaryDescriptor.ProgramOffset);
        Assert.AreEqual(1, decoded.RailProgramRecords.Count); Assert.AreEqual(1, decoded.RailProgramRecords[0].Descriptors.Count);
        Assert.AreEqual(16, decoded.RailProgramRecords[0].GeneratedRailId);
        Assert.AreEqual(new PackedRailReference(0x00001003, 3, 16), decoded.RailProgramRecords[0].GeneratedRailReference);
        Assert.AreEqual(decoded.RailProgramRecords[0].PrimaryRailReference, decoded.RailProgramRecords[0].PrimaryInputRailReference);
        Assert.AreEqual(decoded.RailProgramRecords[0].SecondaryRailReference, decoded.RailProgramRecords[0].SecondaryInputRailReference);
        Assert.AreSame(decoded.RailProgramRecords[0].Descriptors, decoded.RailProgramRecords[0].OutputDescriptors);
        Assert.AreEqual(1, decoded.RailProgramRecords[0].InputRailCount);
        Assert.AreEqual(1e30f, decoded.RailProgramRecords[0].Descriptors[0].Scalar0);
        Assert.AreEqual((ushort)3, decoded.RailProgramRecords[0].Descriptors[0].Low);
        Assert.AreEqual(ushort.MaxValue, decoded.RailProgramRecords[0].Descriptors[0].High);
        Assert.AreEqual(RailSplineRole.GrindRail, decoded.RailProgramRecords[0].Descriptors[0].Role);
        Assert.IsNull(decoded.RailProgramRecords[0].Descriptors[0].SurfaceOverride);
        Assert.AreEqual(2, decoded.RailProgramReferenceIndices.Count); Assert.AreEqual(16, decoded.RailSplineMetadataEntries.Count);
        Assert.AreEqual(new RailSplineMetadataEntry(0x000a0003, 3, 10, RailSplineRole.GrindRail, SsxSurfaceType.Metal),
            decoded.RailSplineMetadataEntries[15]);
    }

    [TestMethod]
    public void SsxSurfaceType_ExtendedCollisionValues_AreStable()
    {
        Assert.AreEqual((ushort)16, (ushort)SsxSurfaceType.RidableRock);
        Assert.AreEqual((ushort)17, (ushort)SsxSurfaceType.Pavement);
        Assert.AreEqual((ushort)18, (ushort)SsxSurfaceType.WipeoutRock);
    }

    [TestMethod]
    public void Ssx3StructuredTableDecoder_DecodesType16ModifierProgramBlocksAndGroups()
    {
        var data = new byte[272]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x1000u); writer.Write(0x3800u); writer.Write(0u); writer.Write(0.89f); writer.Write(0u);
            writer.Write(92u); writer.Write(0u); writer.Write(92u);
            writer.Write(1u); writer.Write(148u); writer.Write(0u); writer.Write(232u); writer.Write(1u); writer.Write(92u);
            writer.Write(1u); writer.Write(232u); writer.Write(272u); writer.Write(0u); writer.Write(272u);
            writer.Write(0u); writer.Write(272u); writer.Write(0u); writer.Write(272u);
            stream.Position = 92; writer.Write(0x12345678u);
            writer.Write(uint.MaxValue); writer.Write(0x00000403u);
            for (var i = 2; i < 13; i++) writer.Write(uint.MaxValue);
            stream.Position = 148; writer.Write(0x87654321u); writer.Write(1u); writer.Write(92u);
            writer.Write(0x00000103u); writer.Write(0x00000203u);
            for (var i = 0; i < 13; i++) writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue); writer.Write(0x00000303u); writer.Write(2u);
            stream.Position = 232; writer.Write(236u); stream.Position = 236;
            writer.Write(Encoding.ASCII.GetBytes("LUN\0")); writer.Write(20u); writer.Write(36u); writer.Write(36u);
            writer.Write(0x2au); writer.Write(-20); writer.Write(0u); writer.Write(2u); writer.Write(0u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 16", 0, SupportConfidence.Low);

        var decoded = Ssx3StructuredTableDecoder.DecodeType16(data, source, 3, 0);

        Assert.AreEqual(1, decoded.ModifierProgramBlocks.Count);
        Assert.AreEqual(new PackedProgramReference(0x00000403, 3, 4), decoded.ModifierProgramBlocks[0].ModifierSlots[1]);
        Assert.AreEqual(1, decoded.ModifierProgramGroups.Count); Assert.AreEqual(1, decoded.ModifierProgramGroups[0].BlockCount);
        Assert.AreEqual(0, decoded.ModifierProgramGroups[0].FirstBlockIndex);
        Assert.AreEqual(new PackedProgramReference(0x00000303, 3, 3), decoded.ModifierProgramGroups[0].ProgramReferences[2]);
    }

    [TestMethod]
    public void Ssx3StructuredTableDecoder_MapsAllExecutableLunOperations()
    {
        var expected = new Dictionary<byte, LunOperation>
        {
            [0x00] = LunOperation.Jump, [0x01] = LunOperation.JumpIfTruthy, [0x02] = LunOperation.JumpIfFalsy,
            [0x03] = LunOperation.Equal, [0x04] = LunOperation.NotEqual,
            [0x05] = LunOperation.GreaterThanOrEqual, [0x06] = LunOperation.GreaterThan,
            [0x07] = LunOperation.LessThanOrEqual, [0x08] = LunOperation.LessThan,
            [0x09] = LunOperation.LogicalOr, [0x0a] = LunOperation.LogicalAnd,
            [0x0b] = LunOperation.CopySlot, [0x0c] = LunOperation.Add, [0x0d] = LunOperation.Subtract,
            [0x0e] = LunOperation.Multiply, [0x0f] = LunOperation.Divide,
            [0x10] = LunOperation.UnsupportedBinaryOperation, [0x11] = LunOperation.Remainder,
            [0x12] = LunOperation.MapSet, [0x13] = LunOperation.CopySlot,
            [0x14] = LunOperation.StoreType3Immediate,
            [0x15] = LunOperation.StoreIntegerImmediate, [0x16] = LunOperation.StoreIntegerImmediate,
            [0x17] = LunOperation.StoreFloatImmediate, [0x18] = LunOperation.MapGet,
            [0x19] = LunOperation.CreateMap, [0x1a] = LunOperation.NoOp,
            [0x1b] = LunOperation.CallRoutine, [0x1c] = LunOperation.MapSetAndIncrementKey,
            [0x1d] = LunOperation.StoreReferencePairImmediate, [0x1e] = LunOperation.ClearSlot,
            [0x1f] = LunOperation.ReturnSlot, [0x20] = LunOperation.AppendSlotArgument,
            [0x21] = LunOperation.CallNative, [0x22] = LunOperation.LogicalNot,
            [0x23] = LunOperation.Negate, [0x24] = LunOperation.ForLoopAdvance,
            [0x25] = LunOperation.AppendIntegerImmediateArgument, [0x26] = LunOperation.AppendFloatImmediateArgument,
            [0x27] = LunOperation.AppendIntegerImmediateArgument, [0x28] = LunOperation.AppendU8IntegerArgument,
            [0x29] = LunOperation.AppendU8FloatArgument, [0x2a] = LunOperation.End
        };

        foreach (var pair in expected)
            Assert.AreEqual(pair.Value, Ssx3StructuredTableDecoder.LunInstructionOperation(pair.Key), $"opcode 0x{pair.Key:X2}");
        Assert.AreEqual(43, expected.Count);
        Assert.IsFalse(Enumerable.Range(0, 43).Select(value => Ssx3StructuredTableDecoder.LunInstructionOperation((byte)value))
            .Any(operation => operation == LunOperation.Unknown));
        Assert.AreEqual(LunOperation.CopySlot, Ssx3StructuredTableDecoder.LunInstructionOperation(0x0b));
        Assert.AreEqual(LunOperation.CopySlot, Ssx3StructuredTableDecoder.LunInstructionOperation(0x13));
        Assert.AreEqual(LunOperation.Unknown, Ssx3StructuredTableDecoder.LunInstructionOperation(0x2b));
        Assert.AreEqual("AddParticleData", Ssx3StructuredTableDecoder.LunNativeFunctionName(25));
        Assert.AreEqual("ParticleModifier", Ssx3StructuredTableDecoder.LunNativeFunctionSubsystem(25));
        Assert.AreEqual("ParentModifier", Ssx3StructuredTableDecoder.LunNativeFunctionSubsystem(17));
        Assert.IsNull(Ssx3StructuredTableDecoder.LunNativeFunctionName(17));
        Assert.IsNull(Ssx3StructuredTableDecoder.LunNativeFunctionSubsystem(110));
    }

    [TestMethod]
    public void Ssx3BnklBankDecoder_DecodesSlotsChainedInfoSectionsAndPatches()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("BNKl")); writer.Write((ushort)5); writer.Write((ushort)2); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); writer.Write(8u); writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("PT")); writer.Write((ushort)5);
        writer.Write(new byte[] { 0x0c, 1, 60, 0x07, 1, 58, 0x08, 1, 3, 0x09, 1, 4, 0x19, 4 });
        var envelopePointerPayloadOffset = checked((int)stream.Position); writer.Write(0u);
        writer.Write(new byte[] { 0x1c, 1, 0, 0xfd, 0x80, 1, 3, 0x85, 2, 0x03, 0xe8,
            0xa0, 1, 4, 0x84, 2, 0x3e, 0x80, 0x8c, 1, 4, 0x88, 1, 0x60, 0xfe });
        writer.Write(new byte[] { 0xfd, 0x80, 1, 3, 0x85, 2, 0x01, 0xf4, 0xa0, 1, 4,
            0x86, 1, 10, 0x87, 1, 99, 0x8c, 1, 4, 0x88, 1, 0x70, 0xff });
        writer.Write(new byte[16]);
        var envelopeOffset = checked((int)stream.Position);
        writer.Write(229u); writer.Write(127u); writer.Write(12u); writer.Write(127u);
        writer.Write((uint)int.MaxValue); writer.Write(0u); writer.Write(20u); writer.Write(0u);
        var envelopeRelativeOffset = checked((uint)(envelopeOffset - envelopePointerPayloadOffset));
        stream.Position = envelopePointerPayloadOffset;
        writer.Write(new[] { (byte)(envelopeRelativeOffset >> 24), (byte)(envelopeRelativeOffset >> 16),
            (byte)(envelopeRelativeOffset >> 8), (byte)envelopeRelativeOffset });
        stream.Position = 8; writer.Write((uint)stream.Length);
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 20", 0, SupportConfidence.Low);

        var decoded = Ssx3BnklBankDecoder.Decode(stream.ToArray(), source, 4, 1);

        Assert.AreEqual(5, decoded.Version); Assert.AreEqual(2, decoded.EntryCount); Assert.AreEqual(2, decoded.ReservedWords.Count);
        CollectionAssert.AreEqual(new uint[] { 8, 0 }, decoded.SlotRelativeOffsets.ToArray());
        Assert.AreEqual(1, decoded.Sounds.Count); Assert.AreEqual(0, decoded.Sounds[0].Slot);
        Assert.AreEqual(5, decoded.Sounds[0].Platform); Assert.AreEqual(2, decoded.Sounds[0].InfoSections.Count);
        Assert.AreEqual(4, decoded.Sounds[0].InfoSections[0].Codec); Assert.AreEqual(16_000, decoded.Sounds[0].InfoSections[0].SampleRate);
        Assert.AreEqual(58, decoded.Sounds[0].InfoSections[0].RootMidiNote);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[0].MinimumVelocity);
        Assert.AreEqual(127, decoded.Sounds[0].InfoSections[0].MaximumVelocity);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[0].MinimumMidiNote);
        Assert.AreEqual(127, decoded.Sounds[0].InfoSections[0].MaximumMidiNote);
        Assert.IsTrue(decoded.Sounds[0].InfoSections[0].MatchesRuntimeLayerSelection(60, 127));
        Assert.IsFalse(decoded.Sounds[0].InfoSections[0].MatchesRuntimeLayerSelection(128, 127));
        Assert.AreEqual(3, decoded.Sounds[0].InfoSections[0].ReleaseEnvelopeSegmentIndex);
        Assert.AreEqual(4, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegmentCount);
        Assert.AreEqual(envelopeOffset, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeOffset);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[0].InitialEnvelopeVolume);
        Assert.AreEqual(4, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments.Count);
        Assert.AreEqual(229u, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[0].DurationHundredths);
        Assert.AreEqual(2.29d, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[0].DurationSeconds, 0.00001d);
        Assert.AreEqual(127, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[0].Volume);
        Assert.AreEqual((uint)int.MaxValue, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[2].DurationHundredths);
        Assert.AreEqual(int.MaxValue, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[2].RuntimeDurationHundredths);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[0].PlaybackEnvelopeSegments[2].RuntimeTargetVolumeFixed16);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[0].RuntimeInitialVolumeFixed16);
        var saturatedDuration = new BnklEnvelopeSegment(0, uint.MaxValue, uint.MaxValue / 100d, 127);
        Assert.AreEqual(int.MaxValue, saturatedDuration.RuntimeDurationHundredths);
        Assert.AreEqual(int.MaxValue / 100d, saturatedDuration.RuntimeDurationSeconds, 0.00001d);
        Assert.AreEqual(127 << 16, saturatedDuration.RuntimeTargetVolumeFixed16);
        Assert.AreEqual(1_000, decoded.Sounds[0].InfoSections[0].SampleCount);
        Assert.AreEqual((byte)0xfe, decoded.Sounds[0].InfoSections[0].Terminator);
        Assert.AreEqual(22_050, decoded.Sounds[0].InfoSections[1].SampleRate);
        Assert.AreEqual(Ssx3BnklBankDecoder.DefaultRootMidiNote, decoded.Sounds[0].InfoSections[1].RootMidiNote);
        Assert.AreEqual(-1, decoded.Sounds[0].InfoSections[1].ReleaseEnvelopeSegmentIndex);
        Assert.AreEqual(1, decoded.Sounds[0].InfoSections[1].PlaybackEnvelopeSegmentCount);
        Assert.IsNull(decoded.Sounds[0].InfoSections[1].PlaybackEnvelopeOffset);
        Assert.AreEqual(127, decoded.Sounds[0].InfoSections[1].InitialEnvelopeVolume);
        Assert.AreEqual(0, decoded.Sounds[0].InfoSections[1].PlaybackEnvelopeSegments.Count);
        Assert.AreEqual(10, decoded.Sounds[0].InfoSections[1].LoopStart); Assert.AreEqual(100, decoded.Sounds[0].InfoSections[1].LoopEnd);
        Assert.IsNull(decoded.Sounds[0].InfoSections[1].MicroTalkLoopRelativeOffset);
        Assert.AreEqual((byte)0xff, decoded.Sounds[0].InfoSections[1].Terminator);
        Assert.AreEqual("Pan", decoded.Sounds[0].InfoSections[0].Patches[0].Name);
        Assert.AreEqual(60u, decoded.Sounds[0].InfoSections[0].Patches[0].Value);
        Assert.AreEqual("RootMidiNote", decoded.Sounds[0].InfoSections[0].Patches[1].Name);
        Assert.AreEqual("MinimumVelocity", Ssx3BnklBankDecoder.PatchName(0x01));
        Assert.AreEqual("MaximumMidiNote", Ssx3BnklBankDecoder.PatchName(0x04));
    }

    [TestMethod]
    public void Ssx3AvalancheDecoder_DecodesFramesAndTypedMetadataSegments()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x02BEEF00u); writer.Write(0u);
        writer.Write((ushort)0xBEEF); writer.Write((ushort)312);
        WriteVector3(writer, new(100, 200, 300));
        writer.Write(new byte[] { 0xFF, 2, 0xFD, 4, 5, 6, 0xF9, 8, 0xF7, 10 });
        writer.Write(new byte[] { 3, 0xFC, 5, 8, 9, 10, 0, 0, 0x7F, 20 });
        writer.Write(new byte[280]);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)12);
        writer.Write((ushort)0xBEEF); writer.Write((ushort)612); writer.Write(new byte[612]);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(1.5f); writer.Write(0x00012306u);
        writer.Write((ushort)1); writer.Write((ushort)22);
        stream.Position = 4; writer.Write((uint)(stream.Length - 8));
        var source = new SourceByteRange("fixture", 0, stream.Length, "type 22", 0, SupportConfidence.Low);

        var decoded = Ssx3AvalancheDecoder.Decode(stream.ToArray(), source, 6, 0);

        Assert.AreEqual(2, decoded.Blocks.Count); Assert.AreEqual(1, decoded.Blocks[0].UnitCount); Assert.AreEqual(2, decoded.Blocks[1].UnitCount);
        Assert.AreEqual(30, decoded.Blocks[0].Frames.Count); Assert.AreEqual(60, decoded.Blocks[1].Frames.Count);
        Assert.AreEqual(new Vector3(98, 204, 294), decoded.Blocks[0].Frames[0].SerializedPositionSsx);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new Vector3(98, 204, 294)), decoded.Blocks[0].Frames[0].Position);
        Assert.AreEqual(new Vector3(104, 196, 304), decoded.Blocks[0].Frames[1].SerializedPositionSsx);
        Assert.AreEqual(new Vector3(-2, 4, -6), decoded.Blocks[0].Frames[0].SerializedTranslationDeltaSsx);
        Assert.AreEqual((sbyte)-1, decoded.Blocks[0].Frames[0].DeltaX);
        Assert.AreEqual(new Vector3(4, 5, 6) / 128f, decoded.Blocks[0].Frames[0].SerializedScaleSsx);
        Assert.AreEqual(new Vector3(4, 6, 5) / 128f, decoded.Blocks[0].Frames[0].Scale);
        var expectedAxis = Vector3.Normalize(new Vector3(-7, 8, -9));
        Assert.AreEqual(expectedAxis, decoded.Blocks[0].Frames[0].SerializedRotationAxisSsx);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(expectedAxis), decoded.Blocks[0].Frames[0].RotationAxis);
        Assert.AreEqual(10 * ((2f * MathF.PI / 3f) / byte.MaxValue), decoded.Blocks[0].Frames[0].RotationAngleRadians);
        Assert.AreEqual((byte)4, decoded.Blocks[0].Frames[0].ScaleX); Assert.AreEqual((sbyte)-7, decoded.Blocks[0].Frames[0].RotationAxisX);
        Assert.AreEqual((byte)10, decoded.Blocks[0].Frames[0].RotationAngle);
        Assert.AreEqual(2, decoded.MetadataSegments.Count); Assert.AreEqual((ushort)0, decoded.MetadataSegments[0].ParameterCount);
        Assert.AreEqual((ushort)1, decoded.MetadataSegments[0].PairCount); Assert.AreEqual(1, decoded.MetadataSegments[0].Pairs.Count);
        Assert.AreEqual(new AvalancheMetadataPair(0, 12), decoded.MetadataSegments[0].Pairs[0]);
        Assert.AreEqual((ushort)1, decoded.MetadataSegments[1].ParameterCount); Assert.AreEqual((ushort)1, decoded.MetadataSegments[1].PairCount);
        Assert.AreEqual(new AvalancheMetadataParameter(1.5f, 0x00012306, 6, 0x123), decoded.MetadataSegments[1].Parameters[0]);
        Assert.AreEqual(new AvalancheMetadataPair(1, 22), decoded.MetadataSegments[1].Pairs[0]);
        Assert.AreEqual(12 / 30f, decoded.MetadataSegments[0].Pairs[0].TriggerTimeSeconds);

        var halfway = Ssx3AvalancheDecoder.EvaluateRuntimeTransform(decoded.Blocks[0], 1f / 60f);
        Assert.AreEqual(0, halfway.FrameIndex); Assert.AreEqual(0.5f, halfway.FrameFraction, 0.00001f);
        Assert.AreEqual(new Vector3(99, 202, 297), halfway.SerializedPositionSsx);
        Assert.AreEqual(new Vector3(6, 7, 8) / 128f, halfway.SerializedScaleSsx);
        var expectedHalfRotation = Quaternion.CreateFromAxisAngle(expectedAxis,
            5 * ((2f * MathF.PI / 3f) / byte.MaxValue));
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(expectedHalfRotation, halfway.SerializedRotationSsx)), 0.00001f);
        Assert.AreEqual(0, Ssx3AvalancheDecoder.SchedulePairsDue(decoded.MetadataSegments[0], 0.39f).Count);
        Assert.AreEqual(1, Ssx3AvalancheDecoder.SchedulePairsDue(decoded.MetadataSegments[0], 0.4f).Count);
        Assert.AreEqual(0, Ssx3AvalancheDecoder.TimedTargetsDue(decoded.MetadataSegments[1], 1.49f).Count);
        Assert.AreEqual(1, Ssx3AvalancheDecoder.TimedTargetsDue(decoded.MetadataSegments[1], 1.5f).Count);
    }

    [TestMethod]
    public void Ssx3EffectDecoder_DecodesParticleModelElementsAndAlignment()
    {
        var data = new byte[112]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x00000302u); writer.Write(1u); writer.Write(32u); stream.Position = 32;
            writer.Write(uint.MaxValue); writer.Write(48u); writer.Write(0u); writer.Write(uint.MaxValue);
            WriteVector3(writer, new(-3, -2, -1)); WriteVector3(writer, new(5, 6, 7));
            writer.Write(0u); writer.Write(1u); writer.Write(36u);
            WriteVector3(writer, new(1, 2, 3)); WriteVector3(writer, new(0.25f, 0.5f, 0.75f)); writer.Write(4f);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 4", 0, SupportConfidence.Low);

        var decoded = Ssx3EffectDecoder.DecodeParticleModel(data, source, 2, 3);

        Assert.AreEqual(1, decoded.Elements.Count); Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new(1, 2, 3)), decoded.Elements[0].Position);
        Assert.AreEqual(new Vector3(0.25f, 0.5f, 0.75f), decoded.Elements[0].Color); Assert.AreEqual(4f, decoded.Elements[0].Size);
        Assert.AreEqual(0, decoded.Properties["AlignmentBytes"]); Assert.AreEqual(SupportConfidence.Medium, decoded.Source.Confidence);
        Assert.AreEqual("cPS2FogParticleMan", decoded.Properties["RuntimeManagerClass"]);
        Assert.AreEqual("0x002DC190", decoded.Properties["RuntimeCompileFunction"]);
        Assert.AreEqual(0x56, ParticleModelAsset.RuntimeGsPrimRegister); Assert.IsFalse(decoded.HasIndependentRuntimeWorldObject);
        Assert.AreEqual(128f, decoded.Properties["RuntimeGsColorScale"]);
        Assert.AreEqual("data/textures/particle.ssh", decoded.Properties["RuntimeTextureArchive"]);
        Assert.AreEqual("FOG", decoded.Properties["RuntimeTextureName"]);
        Assert.AreEqual("fog0", decoded.Properties["RuntimeTextureAssetId"]);
        Assert.AreEqual(4, decoded.Properties["RuntimeTextureEnumIndex"]);
        Assert.AreEqual(5, decoded.Properties["RuntimeBlendSelector"]);
        Assert.AreEqual("0x44", decoded.Properties["RuntimeGsAlphaRegister"]);
        Assert.AreEqual("(SourceColor - DestinationColor) * SourceAlpha + DestinationColor", decoded.Properties["RuntimeBlendEquation"]);
        Assert.AreEqual("Source-alpha interpolation", decoded.Properties["RuntimeBlendMode"]);
        Assert.AreEqual(0f, ParticleModelAsset.EvaluateRuntimeNearDepthFade(500));
        Assert.AreEqual(0.50008315f, ParticleModelAsset.EvaluateRuntimeNearDepthFade(1500), 0.000001f);
        Assert.AreEqual(1f, ParticleModelAsset.EvaluateRuntimeNearDepthFade(2500));
        Assert.AreEqual(1f, ParticleModelAsset.EvaluateRuntimeFarDepthFade(17999));
        Assert.AreEqual(0.5f, ParticleModelAsset.EvaluateRuntimeFarDepthFade(21500), 0.000001f);
        Assert.AreEqual(0f, ParticleModelAsset.EvaluateRuntimeFarDepthFade(25000));
        Assert.AreEqual(0.25004157f, ParticleModelAsset.EvaluateRuntimeAlpha(21500, 1500), 0.000001f);
    }

    [TestMethod]
    public void Ssx3MdrDecoder_DecodesMaterialTableFlagsAndAnimationDuration()
    {
        var data = new byte[48]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x00000302u); writer.Write(0u); writer.Write(44u); writer.Write(40u); writer.Write(8u);
            writer.Write(2.5f); WriteVector3(writer, Vector3.One); writer.Write(48u); writer.Write(0u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 2", 0, SupportConfidence.Low);

        var decoded = Ssx3MdrDecoder.Decode(data, source, 2, 3);

        Assert.AreEqual(8u, decoded.MdrFlags); Assert.AreEqual(2.5f, decoded.AnimationDurationSeconds);
        Assert.AreEqual(40, decoded.Properties["MaterialTableOffset"]); Assert.AreEqual(44, decoded.Properties["ObjectTableOffset"]);
        Assert.AreEqual(300, decoded.Properties["AnimationDurationTicks120Hz"]);
        Assert.AreEqual("0x0034442C", decoded.Properties["RuntimeAnimationSetupFunction"]);
    }

    [TestMethod]
    public void Ssx3SplineSegment_EvaluatesRuntimeDistanceAndPositionPolynomials()
    {
        var segment = new SplineSegment(0, 0, Spline.SerializedSegmentWord4, Spline.SerializedSegmentWord8,
            10f, Vector4.Zero, Vector4.Zero, new Vector4(10, 0, 0, 0), new Vector4(4, 5, 6, 1),
            new Vector4(0, 0, 0.1f, 0), -1, -1, 7, new Vector3(4, 5, 6), new Vector3(14, 5, 6),
            0, Spline.SerializedSegmentTailTag, Spline.SerializedSegmentTailFlags);

        Assert.AreEqual(0.5f, segment.EvaluateParameterAtDistance(5f), 0.000001f);
        Assert.AreEqual(new Vector3(9, 5, 6), segment.EvaluatePositionSsx(0.5f));
        Assert.AreEqual(0x00345048, Spline.RuntimeEvaluateAtDistanceFunction);
        Assert.AreEqual(0x003454E8, Spline.RuntimeCalculateLengthFunction);
    }

    [TestMethod]
    public void Ssx3EffectDecoder_DecodesParticleEmitterTransformAndModelReference()
    {
        var data = new byte[144]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 16; WriteVector3(writer, Vector3.UnitX); stream.Position = 32; WriteVector3(writer, Vector3.UnitY);
            stream.Position = 48; WriteVector3(writer, Vector3.UnitZ); stream.Position = 64; WriteVector3(writer, new(3, 4, 5)); writer.Write(1f);
            WriteVector3(writer, new(10, 20, 30)); writer.Write(8f); writer.Write(0x00000302u); writer.Write(0x00000302u);
            WriteVector3(writer, new(6, 16, 26)); WriteVector3(writer, new(14, 24, 34));
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 5", 0, SupportConfidence.Low);

        var decoded = Ssx3EffectDecoder.DecodeParticleEmitter(data, source, 2, 3);

        Assert.AreEqual(2, decoded.ModelTrackId); Assert.AreEqual(3, decoded.ModelResourceId);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new(3, 4, 5)), decoded.Position); Assert.AreEqual(3, decoded.OrientationAxes.Count);
        Assert.AreEqual(SupportConfidence.Medium, decoded.Source.Confidence); Assert.AreEqual(7, decoded.Properties["RuntimeObjectType"]);
        Assert.AreEqual("0x0022C708", decoded.Properties["RuntimeQueueRenderFunction"]);
        Assert.AreEqual(100, ParticleEmitterAsset.RuntimeResolvedModelPointerOffset);
        Assert.AreEqual(true, decoded.Properties["RuntimeStaticFogInstance"]);
        Assert.AreEqual(false, decoded.Properties["RuntimeHasEmissionOrSimulation"]);
        Assert.AreEqual(false, decoded.Properties["RuntimeConsumesSerializedBoundingRadius"]);
        Assert.AreEqual("Serialized authoring metadata; not read by the retail loader, visibility, or fog renderer",
            decoded.Properties["RuntimeBoundingRadiusUse"]);
    }

    [TestMethod]
    public void Ssx3EffectDecoder_DecodesLightColorDirectionPositionAndBounds()
    {
        var data = new byte[112]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x005541C9u); writer.Write(16u); writer.Write(0x00114B20u); stream.Position = 16;
            writer.Write(2); writer.Write(1.5f); writer.Write(2.5f); writer.Write(30f); WriteVector4(writer, new(1, 0.5f, 0.25f, 1));
            stream.Position = 56; WriteVector3(writer, new(10, 20, 30));
            WriteVector3(writer, new(-20, -10, 0)); WriteVector3(writer, new(40, 50, 60));
            stream.Position = 100; writer.Write((byte)2); writer.Write((byte)7); writer.Write(LightAsset.ExpectedTailMarker);
            stream.Position = 104; writer.Write(0x00542BDEu); writer.Write(16u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 6", 0, SupportConfidence.Low);

        var decoded = Ssx3EffectDecoder.DecodeLight(data, source, 4, 5);

        Assert.AreEqual(2, decoded.Kind); Assert.AreEqual(1.5f, decoded.Intensity); Assert.AreEqual(2.5f, decoded.SelectionWeight);
        Assert.AreEqual(30f, decoded.Range); Assert.AreEqual(new Vector3(1, 0.5f, 0.25f), decoded.Color);
        Assert.AreEqual("Point", Ssx3EffectDecoder.LightKindName(decoded.Kind)); Assert.IsFalse(decoded.IsPlaceholder);
        Assert.AreEqual((sbyte)2, decoded.DistanceFalloffExponent); Assert.AreEqual((sbyte)7, decoded.AngularFalloffExponent);
        Assert.AreEqual(LightAsset.ExpectedTailMarker, decoded.TailMarker);
        Assert.IsTrue(decoded.IsRuntimeAdmitted);
        Assert.AreEqual("0x003AACA8", decoded.Properties["RuntimeAdmissionPredicate"]);
        Assert.AreEqual("Admitted as an internal Type-6 spatial light", decoded.RuntimeAdmissionOutcome);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(Vector3.UnitX), decoded.Direction);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new(10, 20, 30)), decoded.Position); Assert.AreEqual(28, decoded.RawWords.Count);
        Assert.AreEqual(0f, decoded.EvaluateRuntimeLocalIntensity(decoded.Position + new Vector3(31, 0, 0)));
        Assert.AreEqual(1.5f, decoded.EvaluateRuntimeLocalIntensity(decoded.Position + new Vector3(10, 0, 0)), 0.0001f);
        Assert.AreEqual(3.75f, decoded.EvaluateRuntimeSelectionScore(decoded.Position + new Vector3(10, 0, 0)), 0.0001f);
    }

    [TestMethod]
    public void LightAsset_EvaluatesRetailSpotConeAndDistancePowers()
    {
        var source = new SourceByteRange("fixture", 0, 112, "type 6", 0, SupportConfidence.Medium);
        var light = new LightAsset("Spot", source, 1, 2, 0, 1, 2f, 0.5f, 5000f, Vector3.One,
            Vector3.UnitX, Vector3.Zero, null, 0.8f, 0.5f, 2, 2, LightAsset.ExpectedTailMarker, false, [],
            new Dictionary<string, object?>());

        Assert.AreEqual(2f, light.EvaluateRuntimeLocalIntensity(new(50, 0, 0)), 0.0001f);
        Assert.AreEqual(0.5f, light.EvaluateRuntimeLocalIntensity(new(200, 0, 0)), 0.0001f);
        Assert.AreEqual(0.25f, light.EvaluateRuntimeSelectionScore(new(200, 0, 0)), 0.0001f);
        Assert.AreEqual(0f, light.EvaluateRuntimeLocalIntensity(new(0, 50, 0)), 0.0001f);
        var blended = light.EvaluateRuntimeLocalIntensity(Vector3.Normalize(new(0.65f, MathF.Sqrt(1 - 0.65f * 0.65f), 0)) * 50f);
        Assert.AreEqual(2f * 0.8f * 0.8f * 0.5f, blended, 0.0001f);
    }

    [TestMethod]
    public void LightAsset_ClassifiesRetailAdmissionBeforeWorldIndexing()
    {
        var source = new SourceByteRange("fixture", 0, 112, "type 6", 0, SupportConfidence.Medium);
        LightAsset Make(int kind, uint flags = 0) => new("Light", source, 1, 2, flags, kind, 2f, 0.5f, 5000f,
            Vector3.One, Vector3.UnitX, Vector3.Zero, null, kind == 1 ? 0.8f : 0f, kind == 1 ? 0.5f : 0f,
            2, 2, LightAsset.ExpectedTailMarker, false, [], new Dictionary<string, object?>());

        Assert.IsFalse(Make(0).IsRuntimeAdmitted);
        Assert.AreEqual("Directional authoring record rejected before runtime registration", Make(0).RuntimeAdmissionOutcome);
        Assert.IsTrue(Make(1).IsRuntimeAdmitted);
        Assert.IsTrue(Make(2).IsRuntimeAdmitted);
        Assert.IsFalse(Make(3).IsRuntimeAdmitted);
        Assert.AreEqual("Ambient authoring record rejected before runtime registration", Make(3).RuntimeAdmissionOutcome);
        Assert.IsFalse(Make(1, 0x100).IsRuntimeAdmitted);
        Assert.AreEqual("Rejected by serialized flag 0x100", Make(1, 0x100).RuntimeAdmissionOutcome);
    }

    [TestMethod]
    public void Ssx3EffectDecoder_DecodesHaloBoundsAndRadius()
    {
        var data = new byte[80]; using (var stream = new MemoryStream(data, true)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x12345678u); writer.Write(0x00114744u); writer.Write(HaloAsset.InvariantWord08); writer.Write(16u);
            WriteVector3(writer, new(1, 0.5f, 0.25f)); WriteVector3(writer, new(10, 20, 30));
            WriteVector3(writer, new(-40, -30, -20)); WriteVector3(writer, new(60, 70, 80));
            writer.Write(33u); writer.Write(0.75f); writer.Write(0x005553C5u); writer.Write(33u);
        }
        var source = new SourceByteRange("fixture", 0, data.Length, "type 7", 0, SupportConfidence.Low);

        var decoded = Ssx3EffectDecoder.DecodeHalo(data, source, 6, 7);

        Assert.AreEqual(0x12345678u, decoded.SerializedCollectionPointerToken);
        Assert.AreEqual(0x10u, decoded.VisualModeCode); Assert.AreEqual(50f, decoded.HalfExtent);
        Assert.AreEqual(100f, decoded.RuntimeOcclusionProbeScale);
        Assert.AreEqual(180f, decoded.RuntimeRenderScale);
        Assert.AreEqual(Vector3.Normalize(new Vector3(1, 0.5f, 0.25f)), decoded.RuntimeColorDirection);
        Assert.AreEqual("SHALO", decoded.RuntimeTextureName);
        Assert.AreEqual("shal", decoded.RuntimeTextureAssetId);
        Assert.AreEqual(128f, HaloAsset.RuntimeGsColorScale);
        Assert.AreEqual(7, HaloAsset.RuntimeBlendSelector);
        Assert.AreEqual(0x48UL, HaloAsset.RuntimeGsAlphaRegister);
        Assert.AreEqual("(SourceColor - 0) * SourceAlpha + DestinationColor", HaloAsset.RuntimeBlendEquation);
        Assert.AreEqual("Source-alpha additive", HaloAsset.RuntimeBlendMode);
        Assert.AreEqual("0x48", decoded.Properties["RuntimeGsAlphaRegister"]);
        Assert.AreEqual(0x3F400000u, decoded.SerializedEntryPointerToken);
        Assert.AreEqual(0x3F3FFFC8u, decoded.SerializedEntryTableBasePointerToken);
        Assert.AreEqual(MathF.Sqrt(7500), decoded.Radius, 0.0001f);
        Assert.AreEqual(Ssx3Coordinates.ToMountainizer(new(10, 20, 30)), decoded.Position); Assert.AreEqual(20, decoded.RawWords.Count);
    }

    [TestMethod]
    public void Ssx3CameraTriggerDecoder_DecodesVariableRecordsActionsAndFill()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(7u); writer.Write(0.75f); writer.Write(2u); writer.Write(4u);

        writer.Write(0u); writer.Write(1u); writer.Write(1u);
        WriteVector3(writer, new(1, 2, 3)); WriteVector3(writer, new(4, 5, 6)); WriteVector3(writer, new(0.1f, 0.2f, 0.3f));
        writer.Write(1u); for (var i = 1; i <= 6; i++) writer.Write((float)i); writer.Write(99u); WriteVector3(writer, new(7, 8, 9));
        writer.Write(2u); WriteVector3(writer, new(10, 11, 12)); WriteVector3(writer, new(13, 14, 15));
        writer.Write(16f); writer.Write(17f); writer.Write(18f); WriteVector3(writer, new(19, 20, 21)); WriteVector3(writer, new(22, 23, 24));
        writer.Write(3u);

        writer.Write(3u); writer.Write(2u); writer.Write(0u);
        WriteVector3(writer, new(-1, -2, -3)); WriteVector3(writer, new(2, 3, 4)); WriteVector3(writer, Vector3.Zero);
        writer.Write(2u); for (var i = 0; i < 5; i++) writer.Write(i + 0.5f);
        for (var i = 0; i < 2; i++) WriteVector3(writer, new(i + 1, i + 2, i + 3));
        writer.Write(5.5f); writer.Write(6.5f); writer.Write(7.5f);
        for (var i = 0; i < 4; i++) WriteVector3(writer, new(i + 8, i + 9, i + 10));
        writer.Write(0u); writer.Write(1.25f); writer.Write(42u);
        writer.Write(0xDEADBEEFu); writer.Write(0xDEADBEEFu);

        var source = new SourceByteRange("fixture", 0, stream.Length, "type17", 0, SupportConfidence.Low, 0);
        var decoded = Ssx3CameraTriggerDecoder.Decode(stream.ToArray(), source, 5, 6);
        var volumes = Ssx3CameraTriggerDecoder.CreateDebugVolumes(decoded, 10);
        Assert.AreEqual(7, decoded.Version); Assert.AreEqual(4u, decoded.NextTriggerId); Assert.AreEqual(2, decoded.Records.Count);
        Assert.AreEqual(2, decoded.FillWordCount); Assert.AreEqual(160, decoded.Records[0].SerializedSize);
        Assert.AreEqual(168, decoded.Records[1].SerializedSize); Assert.AreEqual("Box", Ssx3CameraTriggerDecoder.VolumeKindName(decoded.Records[0].Shape.Kind));
        Assert.AreEqual("Replay", Ssx3CameraTriggerDecoder.TriggerFlagNames(decoded.Records[0].Flags));
        Assert.AreEqual(new Vector3(2, 3, 4), decoded.Records[1].Shape.SerializedExtentsSsx);
        Assert.AreEqual("Line", Ssx3CameraTriggerDecoder.BoundKindName(decoded.Records[0].Action0.BoundObject!.Kind));
        Assert.AreEqual("Spline Camera", Ssx3CameraTriggerDecoder.ActionKindName(decoded.Records[1].Action0.Kind));
        Assert.AreEqual(6, decoded.Records[1].Action0.Spline!.VectorsSsx.Count); Assert.AreEqual(2, volumes.Count);
        Assert.AreEqual(new Vector3(-3, -7, -1), volumes[1].Minimum); Assert.AreEqual(new Vector3(1, 1, 5), volumes[1].Maximum);
        Assert.AreEqual("0x00172278", decoded.Properties["RuntimeEllipseContainmentFunction"]);
        Assert.AreEqual("0x001728F8", decoded.Properties["RuntimeBoxContainmentFunction"]);
        Assert.AreEqual(false, decoded.Properties["RuntimeBoxConsumesSerializedRotationZ"]);
        Assert.AreEqual("0x0016E1D8", decoded.Properties["RuntimeActionDispatchFunction"]);
        Assert.AreEqual("duration == 0 ? 1 : (1 / 60) / duration", decoded.Properties["RuntimeActionBlendFractionEquation"]);
        Assert.AreEqual(false, volumes[0].Properties["RuntimeConsumesSerializedRotationZ"]);
        Assert.AreEqual(true, volumes[0].Properties["RuntimeContainsCenter"]);
        Assert.AreEqual(1f, decoded.Records[0].Action0.BlendDurationSeconds);
        Assert.AreEqual(1f / 60f, decoded.Records[0].Action0.RuntimeBlendFractionPerFrame);
        Assert.AreEqual(0x5B, decoded.Records[0].Action0.RuntimeCameraAlgorithmId);
        Assert.AreEqual(2f, decoded.Records[0].Action0.BoundedCameraDistance);
        Assert.AreEqual(3f, decoded.Records[0].Action0.BoundedFieldOfViewRadians);
        Assert.AreEqual(4f, decoded.Records[0].Action0.BoundedVerticalTargetOffset);
        Assert.AreEqual(5f, decoded.Records[0].Action0.BoundedPitchOffsetDegrees);
        Assert.AreEqual(6f, decoded.Records[0].Action0.BoundedForwardTargetOffset);
        Assert.AreEqual(99u, decoded.Records[0].Action0.BoundedReferenceMode);
        Assert.IsNull(decoded.Records[0].Action0.BoundedExplicitReferencePointSsx);
        Assert.AreEqual(2f, volumes[0].Properties["Action0BoundedCameraDistance"]);
        Assert.AreEqual("focus = targetPosition + worldUpAxis * verticalTargetOffset + targetForward * forwardTargetOffset",
            decoded.Properties["RuntimeBoundedFocusEquation"]);
        Assert.AreEqual(0.5f, decoded.Records[1].Action0.BlendDurationSeconds);
        Assert.AreEqual(1f / 30f, decoded.Records[1].Action0.RuntimeBlendFractionPerFrame);
        Assert.AreEqual(0x5C, decoded.Records[1].Action0.RuntimeCameraAlgorithmId);
        Assert.AreEqual(1.5f, decoded.Records[1].Action0.SplineFieldOfViewRadians);
        Assert.AreEqual(2.5f, decoded.Records[1].Action0.SplineForwardTargetOffset);
        Assert.AreEqual(3.5f, decoded.Records[1].Action0.SplineDurationSeconds);
        Assert.AreEqual(4.5f, decoded.Records[1].Action0.SplineVerticalTargetOffset);
        Assert.AreEqual(3.5f, volumes[1].Properties["Action0SplineDurationSeconds"]);
        Assert.AreEqual("0x00161060", decoded.Properties["RuntimeSplineCameraConstructorFunction"]);
        Assert.AreEqual("0x4B", decoded.Properties["RuntimeSplineCameraObjectAlgorithmId"]);
        Assert.AreEqual(76, decoded.Records[1].Action1.RuntimeCameraAlgorithmId);
        Assert.AreEqual(1.25f, volumes[1].Properties["Action1BlendDurationSeconds"]);
        Assert.AreEqual(76, volumes[1].Properties["Action1RuntimeCameraAlgorithmId"]);
    }

    [TestMethod]
    public void CameraTriggerAction_ReproducesRetailBlendAndSwitchCameraDispatch()
    {
        var expected = new[]
        {
            0, 2, 1, 4, 7, 5, 6, 3, 8, 10, 9,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            41, 33, 34, 35, 36, 37, 38, 39, 40, 76, 0,
            42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
            60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73
        };
        Assert.AreEqual(76, expected.Length);
        for (uint code = 0; code < expected.Length; code++)
            Assert.AreEqual(expected[code], CameraTriggerAction.MapSwitchCameraCodeToRuntimeAlgorithm(code), $"switch code {code}");
        Assert.AreEqual(0, CameraTriggerAction.MapSwitchCameraCodeToRuntimeAlgorithm(76));
        Assert.AreEqual(0, CameraTriggerAction.MapSwitchCameraCodeToRuntimeAlgorithm(uint.MaxValue));

        var immediate = new CameraTriggerAction(0, new[] { 0f }, 1, [], null, null);
        var timed = new CameraTriggerAction(1, new[] { 2f }, null, [], null, null);
        var none = new CameraTriggerAction(3, [], null, [], null, null);
        Assert.AreEqual(1f, immediate.RuntimeBlendFractionPerFrame);
        Assert.AreEqual(2, immediate.RuntimeCameraAlgorithmId);
        Assert.AreEqual(1f / 120f, timed.RuntimeBlendFractionPerFrame);
        Assert.AreEqual(0x5B, timed.RuntimeCameraAlgorithmId);
        Assert.IsNull(none.BlendDurationSeconds);
        Assert.IsNull(none.RuntimeBlendFractionPerFrame);
        Assert.IsNull(none.RuntimeCameraAlgorithmId);
    }

    [TestMethod]
    public void CameraTriggerAction_ExposesExecutableProvenBoundedCameraParameters()
    {
        var action = new CameraTriggerAction(1, new[] { 1.5f, 350f, 0.4f, 70f, 25f, 12f }, 1,
            new[] { new Vector3(10, 20, 30) }, null, null);

        Assert.AreEqual(350f, action.BoundedCameraDistance);
        Assert.AreEqual(0.4f, action.BoundedFieldOfViewRadians);
        Assert.AreEqual(70f, action.BoundedVerticalTargetOffset);
        Assert.AreEqual(25f, action.BoundedPitchOffsetDegrees);
        Assert.AreEqual(12f, action.BoundedForwardTargetOffset);
        Assert.AreEqual(1u, action.BoundedReferenceMode);
        Assert.AreEqual(new Vector3(10, 20, 30), action.BoundedExplicitReferencePointSsx);
        Assert.AreEqual(0x00174200, CameraTriggerAction.RuntimeBoundedCameraInitializeFunction);
        Assert.AreEqual(0.8f, CameraTriggerAction.RuntimeBoundedElevationScale);
        Assert.AreEqual("clamp(fieldOfViewRadians, 0, configuredFieldOfViewRadians)",
            CameraTriggerAction.RuntimeBoundedFieldOfViewClampEquation);
    }

    [TestMethod]
    public void CameraTriggerAction_ExposesExecutableProvenSplineCameraParameters()
    {
        var action = new CameraTriggerAction(2, new[] { 1.25f, 0.42f, 18f, 4.5f, 65f }, null,
            [], null, new CameraTriggerSpline([], []));

        Assert.AreEqual(0.42f, action.SplineFieldOfViewRadians);
        Assert.AreEqual(18f, action.SplineForwardTargetOffset);
        Assert.AreEqual(4.5f, action.SplineDurationSeconds);
        Assert.AreEqual(65f, action.SplineVerticalTargetOffset);
        Assert.AreEqual(0x5C, CameraTriggerAction.RuntimeSplineCameraSelectionId);
        Assert.AreEqual(0x4B, CameraTriggerAction.RuntimeSplineCameraObjectAlgorithmId);
        Assert.AreEqual(0x00161060, CameraTriggerAction.RuntimeSplineCameraConstructorFunction);
        Assert.AreEqual(0x001613F0, CameraTriggerAction.RuntimeSplineCameraMotionFunction);
        Assert.AreEqual(0x00161630, CameraTriggerAction.RuntimeSplineCameraUpdateFunction);
        Assert.AreEqual(101, CameraTriggerAction.RuntimeSplinePathSampleCount);
        Assert.AreEqual(100, CameraTriggerAction.RuntimeSplineArcLengthSegmentCount);
        Assert.AreEqual(0.9f, CameraTriggerAction.RuntimeSplineSpeedScale);
        Assert.AreEqual("controlTimes = [0, durationSeconds / 3, 2 * durationSeconds / 3, durationSeconds]",
            CameraTriggerAction.RuntimeSplineControlTimesEquation);
        Assert.AreEqual("cameraPosition = splinePosition(adjustedTime)",
            CameraTriggerAction.RuntimeSplineCameraPositionEquation);
        Assert.AreEqual("focus = targetPosition + worldUpAxis * verticalTargetOffset + targetForward * forwardTargetOffset",
            CameraTriggerAction.RuntimeSplineFocusEquation);
    }

    [TestMethod]
    public void CameraTriggerShape_ReproducesRetailEllipseAndBoxContainmentTransforms()
    {
        var centerSsx = new Vector3(10, 20, 30);
        var ellipse = new CameraTriggerShape(0, Ssx3Coordinates.ToMountainizer(centerSsx), new(2, 4, 3),
            new(2, 3, 4), new(0.31f, -0.27f, 0.18f));
        var ellipseBoundarySsx = centerSsx + ForwardEllipseVector(new(2, 0, 0), ellipse.RotationRadiansSsx);
        var ellipseOutsideSsx = centerSsx + ForwardEllipseVector(new(2.01f, 0, 0), ellipse.RotationRadiansSsx);

        Assert.AreEqual(0x00172278, ellipse.RuntimeContainmentFunction);
        AssertVector3Near(new Vector3(1, 0, 0), ellipse.TransformPointToRuntimeVolumeSpaceSsx(ellipseBoundarySsx), 0.00001f);
        Assert.IsTrue(ellipse.ContainsRuntimePointSsx(centerSsx));
        Assert.IsTrue(ellipse.ContainsRuntimePointSsx(ellipseBoundarySsx));
        Assert.IsFalse(ellipse.ContainsRuntimePointSsx(ellipseOutsideSsx));

        var box = new CameraTriggerShape(1, Ssx3Coordinates.ToMountainizer(centerSsx), new(2, 4, 3),
            new(2, 3, 4), new(0.31f, -0.27f, 0.18f));
        var boxDifferentUnusedZ = box with { RotationRadiansSsx = box.RotationRadiansSsx with { Z = -1.4f } };
        var boxBoundarySsx = centerSsx + ForwardBoxVector(new(2, 0, 0), box.RotationRadiansSsx);
        var boxOutsideSsx = centerSsx + ForwardBoxVector(new(2.01f, 0, 0), box.RotationRadiansSsx);

        Assert.AreEqual(0x001728F8, box.RuntimeContainmentFunction);
        AssertVector3Near(new Vector3(2, 0, 0), box.TransformPointToRuntimeVolumeSpaceSsx(boxBoundarySsx), 0.00001f);
        AssertVector3Near(box.TransformPointToRuntimeVolumeSpaceSsx(boxOutsideSsx),
            boxDifferentUnusedZ.TransformPointToRuntimeVolumeSpaceSsx(boxOutsideSsx), 0.00001f);
        Assert.IsTrue(box.ContainsRuntimePointSsx(boxBoundarySsx));
        Assert.IsFalse(box.ContainsRuntimePointSsx(boxOutsideSsx));
    }

    [TestMethod]
    public void Ssx3ReferenceTableDecoder_DecodesNisSlotsAndNavigationMarker()
    {
        var data = Enumerable.Repeat((byte)0xff, 72).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x00012304u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(7 * 4), 0x00045608u);
        var source = new SourceByteRange("fixture", 0, data.Length, "type 18", 0, SupportConfidence.Low);

        var decoded = Ssx3ReferenceTableDecoder.DecodeNis(data, source, 9, 1);
        var marker = Ssx3ReferenceTableDecoder.DecodeNavigationMarker(source with { SourceLength = 0 }, 10, 2);

        Assert.AreEqual(18, decoded.Slots.Count); Assert.AreEqual(new PackedObjectReference(4, 0x123), decoded.Slots[0].ObjectReference);
        Assert.IsNull(decoded.Slots[1].ObjectReference); Assert.AreEqual(new PackedObjectReference(8, 0x456), decoded.Slots[7].ObjectReference);
        Assert.IsTrue(decoded.Slots.All(slot => slot.IsRuntimeAddressable));
        CollectionAssert.AreEqual(new[] { 20, 21, 22, 23 }, decoded.Slots[0].RuntimeCommandIds.ToArray());
        CollectionAssert.AreEqual(new[] { 19 }, decoded.Slots[7].RuntimeCommandIds.ToArray());
        CollectionAssert.AreEqual(new[] { 39 }, decoded.Slots[17].RuntimeCommandIds.ToArray());
        Assert.AreEqual("Finish podium steps", Ssx3ReferenceTableDecoder.NisSlotRole(0));
        Assert.AreEqual("NIS lodge", Ssx3ReferenceTableDecoder.NisSlotRole(7));
        Assert.AreEqual("Alternate OS609 in-air model", Ssx3ReferenceTableDecoder.NisSlotRole(15)); Assert.IsNull(Ssx3ReferenceTableDecoder.NisSlotRole(17));
        Assert.AreEqual("cSSXScriptEngine object-transform lookup", decoded.Properties["RuntimeConsumer"]);
        Assert.AreEqual(SupportConfidence.Medium, decoded.Source.Confidence);
        Assert.AreEqual(true, marker.Properties["Marker"]); Assert.AreEqual(10, marker.TrackId); Assert.AreEqual(2, marker.ResourceId);
    }

    [TestMethod]
    public void InspectionCamera_DollyUsesConsistentStepsAndLookPreservesPosition()
    {
        var camera = new InspectionCamera();
        var initialDistance = camera.Distance;
        var initialTarget = camera.Target;
        var initialYaw = camera.Yaw;
        var initialPitch = camera.Pitch;
        var initialPosition = camera.Position;
        camera.Dolly(1);
        var firstStep = camera.Position - initialPosition;
        var afterFirstStep = camera.Position;
        camera.Dolly(1);
        var secondStep = camera.Position - afterFirstStep;
        Assert.AreEqual(initialDistance, camera.Distance);
        Assert.IsTrue(Vector3.Distance(initialTarget, camera.Target) > 0);
        Assert.AreEqual(camera.MoveSpeed * 0.15f, firstStep.Length(), 0.01f);
        Assert.IsTrue(Vector3.Distance(firstStep, secondStep) < 0.01f);
        Assert.AreEqual(initialYaw, camera.Yaw);
        Assert.AreEqual(initialPitch, camera.Pitch);

        var position = camera.Position;
        var direction = camera.Forward;
        camera.Look(100, -40);
        Assert.IsTrue(Vector3.Distance(position, camera.Position) < 0.1f);
        Assert.IsTrue(Vector3.Distance(direction, camera.Forward) > 0.1f);
    }

    [TestMethod]
    public void InspectionCamera_FlyKeepsUsefulSpeedNearAnOrbitPivotAndAfterRotation()
    {
        var camera = new InspectionCamera();
        camera.SetPose(new Vector3(0, 1000, -10000), new Vector3(0, 1000, 0), 2500);
        camera.SetOrbitPivot(camera.Position + camera.Forward);
        Assert.IsTrue(camera.MoveSpeed >= 20000);

        camera.Look(150, -40);
        var position = camera.Position;
        camera.Fly(0, 0, 1, 0.1f);
        Assert.IsTrue(Vector3.Distance(position, camera.Position) >= 1999);
    }

    [TestMethod]
    public void VisibilityCurtain_RuntimeCandidateScoreUsesSphereCenterXyzAndSortPlaneSide()
    {
        var curtain = new VisibilityCurtain("fixture", new SourceByteRange("fixture", 0, 208, "type 11", 0, SupportConfidence.Verified), [],
            new Dictionary<string, object?>())
        {
            BoundingSphereSsx = new Vector4(10, 20, 30, 999),
            PlaneSsx = new Vector4(0, 0, 1, -25)
        };

        Assert.AreEqual(0f, curtain.RuntimeCandidateScore(new Vector3(10, 20, 30)));
        Assert.AreEqual(50f, curtain.RuntimeCandidateScore(new Vector3(13, 24, 35)));
        Assert.IsTrue(curtain.PassesRuntimeViewerPlaneTest(new Vector3(0, 0, 25)));
        Assert.IsFalse(curtain.PassesRuntimeViewerPlaneTest(new Vector3(0, 0, 26)));
    }

    [TestMethod]
    public void MaterialTextureFrameTable_SelectsByRuntimeIndex()
    {
        var source = new SourceByteRange("fixture", 0, 32, "type 0", 0, SupportConfidence.Verified);
        var material = new MaterialAsset("fixture", source, 1, 2, 40, new Dictionary<string, object?>())
        {
            SerializedTextureFrameTableToken = 2,
            TextureFrameResourceIds = [40, 41]
        };

        Assert.IsTrue(material.HasTextureFrameTable);
        Assert.AreEqual(2, material.TextureFrameCount);
        Assert.AreEqual(40, material.TextureResourceIdForFrame(0));
        Assert.AreEqual(41, material.TextureResourceIdForFrame(1));
        Assert.AreEqual(0, material.InitialTextureFrameIndex(4));
        Assert.AreEqual(1, material.InitialTextureFrameIndex(5));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => material.TextureResourceIdForFrame(2));

        var staticMaterial = material with
        {
            SerializedTextureFrameTableToken = MaterialAsset.NoTextureFrameTableToken,
            TextureFrameResourceIds = []
        };
        Assert.AreEqual(40, staticMaterial.TextureResourceIdForFrame(99));
        Assert.AreEqual(0, staticMaterial.InitialTextureFrameIndex(uint.MaxValue));
    }

    [TestMethod]
    public void MaterialPrimaryTextureStateBit_SelectsExactRetailBlendEquation()
    {
        var source = new SourceByteRange("fixture", 0, 20, "type 0", 0, SupportConfidence.Verified);
        var opaque = new MaterialAsset("opaque", source, 1, 2, 40, new Dictionary<string, object?>())
            { TextureStateWord02 = 0x0032 };
        var blended = opaque with { TextureStateWord02 = 0x003e };

        Assert.IsFalse(opaque.UsesPrimaryTextureAlphaBlend);
        Assert.AreEqual(1, opaque.RuntimePrimaryAlphaSelector);
        Assert.AreEqual(0x2AUL, opaque.RuntimePrimaryGsAlphaRegister);
        Assert.AreEqual("Cs", opaque.RuntimePrimaryBlendEquation);
        Assert.IsTrue(blended.UsesPrimaryTextureAlphaBlend);
        Assert.AreEqual(5, blended.RuntimePrimaryAlphaSelector);
        Assert.AreEqual(0x44UL, blended.RuntimePrimaryGsAlphaRegister);
        Assert.AreEqual("(Cs - Cd) * As + Cd", blended.RuntimePrimaryBlendEquation);
        var packet = blended with { PacketAddressAdjustment = MaterialAsset.ObservedPacketAddressAdjustment };
        Assert.AreEqual(0x12355677u, packet.RuntimeDmaCallTargetAddress(0x12345678));
        Assert.AreEqual(Ps2DmaTagId.Call, MaterialAsset.RuntimeDmaCallTagId);
        Assert.AreEqual(0, MaterialAsset.RuntimeDmaCallQuadwordCount);
    }

    private static Vector3 ForwardEllipseVector(Vector3 value, Vector3 rotation)
    {
        value = RotateZ(value, rotation.X);
        value = RotateY(value, rotation.Z);
        return RotateX(value, rotation.Y);
    }

    private static Vector3 ForwardBoxVector(Vector3 value, Vector3 rotation) =>
        RotateZ(RotateX(value, rotation.Y), rotation.X);

    private static Vector3 RotateX(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X, value.Y * cosine - value.Z * sine, value.Y * sine + value.Z * cosine);
    }

    private static Vector3 RotateY(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X * cosine + value.Z * sine, value.Y, -value.X * sine + value.Z * cosine);
    }

    private static Vector3 RotateZ(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X * cosine - value.Y * sine, value.X * sine + value.Y * cosine, value.Z);
    }

    private static void AssertVector3Near(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.AreEqual(expected.X, actual.X, tolerance);
        Assert.AreEqual(expected.Y, actual.Y, tolerance);
        Assert.AreEqual(expected.Z, actual.Z, tolerance);
    }

    private static byte[] MakeBig()
    {
        var headerSize = 16 + 8 + "data/test.bin".Length + 1; var dataOffset = 64; var result = new byte[dataOffset + 4];
        Encoding.ASCII.GetBytes("BIGF").CopyTo(result, 0); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 1); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), (uint)headerSize);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), (uint)dataOffset); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), 4);
        Encoding.ASCII.GetBytes("data/test.bin\0").CopyTo(result, 24); result[dataOffset] = 1; result[dataOffset + 1] = 2; result[dataOffset + 2] = 3; result[dataOffset + 3] = 4; return result;
    }
    private static void WriteVector3(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    private static void WriteVector4(BinaryWriter writer, Vector4 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }
    private static void WriteBinding(BinaryWriter writer, uint key0, uint key1, byte objectTrack, int objectResource, int offset)
    {
        writer.Write(key0); writer.Write(key1); writer.Write(0xCCCCCCCCu);
        writer.Write((uint)objectTrack | (uint)objectResource << 8); writer.Write(offset); writer.Write(0xCCCCCCCCu);
    }
    private static void WriteSoundDescriptor(BinaryWriter writer, uint kind, uint triggerInfoId, int floatCount)
    {
        writer.Write(kind); writer.Write(triggerInfoId);
        for (var i = 0; i < floatCount; i++) writer.Write((float)i);
    }
    private static void WriteRouteSample(BinaryWriter writer, Vector2 tangent, Vector2 position, float distance)
    {
        writer.Write(tangent.X); writer.Write(tangent.Y); writer.Write(position.X); writer.Write(position.Y); writer.Write(distance);
    }
    private static void WriteLocation(Span<byte> target, string name, uint firstSubChunk, uint subChunkCount)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(target); BinaryPrimitives.WriteUInt32LittleEndian(target[20..], subChunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(target[24..], firstSubChunk);
    }
}
