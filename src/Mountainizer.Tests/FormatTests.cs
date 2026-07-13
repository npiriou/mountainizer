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
    public void CoordinateConversion_MapsSsxZUpWorldToMountainizerYUpWorld()
    {
        Assert.AreEqual(new Vector3(1, 3, -2), Ssx3Coordinates.ToMountainizer(new Vector3(1, 2, 3)));
    }

    [TestMethod]
    public void TextureCoordinates_KeepMdrSignsUprightWithoutChangingTerrainRendering()
    {
        var uv = new Vector2(0.25f, 0.75f);
        Assert.AreEqual(uv, TextureCoordinateConvention.ModelToOpenGl(uv));
        Assert.AreEqual(new Vector2(0.25f, 0.25f), TextureCoordinateConvention.TerrainToOpenGl(uv));
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
    public void ObjExport_WritesVerticesAndFaces()
    {
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0u, 1u, 2u]);
        var source = new SourceByteRange("fixture", 0, 1, "synthetic", 0, SupportConfidence.Verified);
        var scene = new MountainizerScene { Name = "Synthetic" }; scene.Terrain.Add(new("Patch", source, [], mesh, 0, 0, 0, new Dictionary<string, object?>()));
        scene.Models.Add(new("Model", source, mesh, [new(mesh, 2, 3)], new Dictionary<string, object?> { ["TrackId"] = 1, ["ResourceId"] = 2 }));
        scene.Props.Add(new("Prop", source, Matrix4x4.CreateTranslation(10, 0, 0), 1, 2, new Dictionary<string, object?>()));
        var path = Path.GetTempFileName(); try { ObjExporter.ExportScene(scene, path); var text = File.ReadAllText(path); StringAssert.Contains(text, "v 0 0 0"); StringAssert.Contains(text, "v 10 0 0"); StringAssert.Contains(text, "f 1/1/1 2/2/2 3/3/3"); StringAssert.Contains(text, "f 4/4/4 5/5/5 6/6/6"); } finally { File.Delete(path); }
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
