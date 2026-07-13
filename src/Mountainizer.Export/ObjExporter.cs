using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Export;

public static class ObjExporter
{
    public static void ExportTerrain(MountainizerScene scene, string outputPath) => Export(scene, outputPath, includeProps: false);
    public static void ExportScene(MountainizerScene scene, string outputPath) => Export(scene, outputPath, includeProps: true);

    private static void Export(MountainizerScene scene, string outputPath, bool includeProps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var writer = new StreamWriter(outputPath, false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("# Mountainizer read-only SSX 3 scene export");
        writer.WriteLine($"# Level: {scene.Name}");
        var vertexBase = 1;
        foreach (var patch in scene.Terrain) WriteMesh(writer, patch.Name, patch.Mesh, Matrix4x4.Identity, ref vertexBase);
        if (!includeProps) return;

        static long Key(int track, int resource) => ((long)track << 32) | (uint)resource;
        var models = scene.Models.Where(x => x.Mesh is not null)
            .GroupBy(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])))
            .ToDictionary(x => x.Key, x => x.Last());
        foreach (var (prop, propIndex) in scene.Props.Select((value, index) => (value, index)))
        {
            if (!models.TryGetValue(Key(prop.ModelTrackId, prop.ModelResourceId), out var model)) continue;
            foreach (var (submesh, part) in model.Submeshes.Select((value, index) => (value, index)))
                WriteMesh(writer, $"{prop.Name}_{propIndex:D4}_part_{part:D2}", submesh.Mesh, prop.Transform, ref vertexBase);
        }
    }

    private static void WriteMesh(StreamWriter writer, string name, MeshData mesh, Matrix4x4 transform, ref int vertexBase)
    {
        writer.WriteLine($"o {Sanitize(name)}");
        foreach (var p in mesh.Positions.Select(x => Vector3.Transform(x, transform))) writer.WriteLine(FormattableString.Invariant($"v {p.X:R} {p.Y:R} {p.Z:R}"));
        foreach (var uv in mesh.TextureCoordinates) writer.WriteLine(FormattableString.Invariant($"vt {uv.X:R} {1f - uv.Y:R}"));
        foreach (var sourceNormal in mesh.Normals)
        {
            var transformed = Vector3.TransformNormal(sourceNormal, transform);
            var n = transformed.LengthSquared() > 0.000001f ? Vector3.Normalize(transformed) : Vector3.UnitY;
            writer.WriteLine(FormattableString.Invariant($"vn {n.X:R} {n.Y:R} {n.Z:R}"));
        }
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var a = vertexBase + (int)mesh.Indices[i]; var b = vertexBase + (int)mesh.Indices[i + 1]; var c = vertexBase + (int)mesh.Indices[i + 2];
            writer.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
        }
        vertexBase += mesh.Positions.Count;
    }

    private static string Sanitize(string value) => string.Concat(value.Select(x => char.IsWhiteSpace(x) ? '_' : x));
}
