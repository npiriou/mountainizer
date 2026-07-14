using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Export;

public sealed record ObjExportResult(string ObjPath, string MaterialPath, string TextureDirectory, int TextureCount);

public static class ObjExporter
{
    private sealed record ExportMaterial(string Name, TextureAsset? Texture, string? RelativeTexturePath);

    public static ObjExportResult ExportTerrain(MountainizerScene scene, string outputPath) => Export(scene, outputPath, includeProps: false);
    public static ObjExportResult ExportScene(MountainizerScene scene, string outputPath) => Export(scene, outputPath, includeProps: true);

    private static ObjExportResult Export(MountainizerScene scene, string outputPath, bool includeProps)
    {
        outputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var companionBase = SanitizeFileName(Path.GetFileNameWithoutExtension(outputPath));
        var materialPath = Path.Combine(outputDirectory, companionBase + ".mtl");
        var textureDirectoryName = companionBase + "_textures";
        var textureDirectory = Path.Combine(outputDirectory, textureDirectoryName);

        static long Key(int track, int resource) => ((long)track << 32) | (uint)resource;
        var textures = scene.Textures.Where(x => x.Decoded)
            .GroupBy(x => Key(x.TrackId, x.ResourceId)).ToDictionary(x => x.Key, x => x.Last());
        var texturesByResource = scene.Textures.Where(x => x.Decoded)
            .GroupBy(x => x.ResourceId).ToDictionary(x => x.Key, x => x.Last());
        var materials = scene.Materials.GroupBy(x => Key(x.TrackId, x.ResourceId)).ToDictionary(x => x.Key, x => x.Last());
        var models = scene.Models.Where(x => x.Mesh is not null)
            .GroupBy(x => Key(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])))
            .ToDictionary(x => x.Key, x => x.Last());
        var exportMaterials = new Dictionary<TextureAsset, ExportMaterial>(ReferenceEqualityComparer.Instance);
        var untextured = new ExportMaterial("untextured", null, null);

        TextureAsset? ResolveTexture(int trackId, int resourceId) =>
            textures.TryGetValue(Key(trackId, resourceId), out var exact) ? exact : texturesByResource.GetValueOrDefault(resourceId);

        ExportMaterial MaterialFor(TextureAsset? texture)
        {
            if (texture is null) return untextured;
            if (exportMaterials.TryGetValue(texture, out var existing)) return existing;
            var name = $"texture_{texture.TrackId}_{texture.ResourceId}";
            var fileName = name + ".png";
            var relativePath = textureDirectoryName + "/" + fileName;
            var created = new ExportMaterial(name, texture, relativePath);
            exportMaterials.Add(texture, created);
            return created;
        }

        ExportMaterial TerrainMaterial(TerrainPatch patch) => MaterialFor(ResolveTexture(patch.TrackId, patch.TextureResourceId));
        ExportMaterial ModelMaterial(ModelSubmesh submesh)
        {
            if (!materials.TryGetValue(Key(submesh.MaterialTrackId, submesh.MaterialResourceId), out var material)) return untextured;
            return MaterialFor(ResolveTexture(material.TrackId, material.TextureResourceId));
        }

        using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("# Mountainizer read-only SSX 3 scene export");
            writer.WriteLine($"# Level: {scene.Name}");
            writer.WriteLine($"mtllib {Path.GetFileName(materialPath)}");
            var vertexBase = 1;
            foreach (var patch in scene.Terrain)
                WriteMesh(writer, patch.Name, patch.Mesh, Matrix4x4.Identity, TerrainMaterial(patch).Name, ref vertexBase);
            if (includeProps)
            {
                foreach (var (prop, propIndex) in scene.Props.Select((value, index) => (value, index)))
                {
                    if (!models.TryGetValue(Key(prop.ModelTrackId, prop.ModelResourceId), out var model)) continue;
                    foreach (var (submesh, part) in model.Submeshes.Select((value, index) => (value, index)))
                        WriteMesh(writer, $"{prop.Name}_{propIndex:D4}_part_{part:D2}", submesh.Mesh, prop.Transform,
                            ModelMaterial(submesh).Name, ref vertexBase);
                }
            }
        }

        WriteMaterials(materialPath, exportMaterials.Values, untextured);
        if (exportMaterials.Count > 0) Directory.CreateDirectory(textureDirectory);
        foreach (var material in exportMaterials.Values)
            PngWriter.Write(Path.Combine(outputDirectory, material.RelativeTexturePath!.Replace('/', Path.DirectorySeparatorChar)), material.Texture!);
        return new(outputPath, materialPath, textureDirectory, exportMaterials.Count);
    }

    private static void WriteMaterials(string path, IEnumerable<ExportMaterial> texturedMaterials, ExportMaterial untextured)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("# Mountainizer material library");
        WriteMaterial(writer, untextured);
        foreach (var material in texturedMaterials.OrderBy(x => x.Name, StringComparer.Ordinal)) WriteMaterial(writer, material);

        static void WriteMaterial(StreamWriter writer, ExportMaterial material)
        {
            writer.WriteLine(); writer.WriteLine($"newmtl {material.Name}");
            writer.WriteLine("Ka 0 0 0"); writer.WriteLine("Kd 1 1 1"); writer.WriteLine("Ks 0 0 0");
            writer.WriteLine("d 1"); writer.WriteLine("illum 1");
            if (material.RelativeTexturePath is not null)
            {
                writer.WriteLine($"map_Kd {material.RelativeTexturePath}");
                writer.WriteLine($"map_d {material.RelativeTexturePath}");
            }
        }
    }

    private static void WriteMesh(StreamWriter writer, string name, MeshData mesh, Matrix4x4 transform, string materialName, ref int vertexBase)
    {
        writer.WriteLine($"o {SanitizeName(name)}");
        writer.WriteLine($"usemtl {materialName}");
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

    private static string SanitizeName(string value) => string.Concat(value.Select(x => char.IsWhiteSpace(x) ? '_' : x));
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(x => char.IsWhiteSpace(x) || invalid.Contains(x) ? '_' : x));
        return string.IsNullOrWhiteSpace(result) ? "mountainizer_export" : result;
    }

    private static class PngWriter
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

        public static void Write(string path, TextureAsset texture)
        {
            using var output = File.Create(path);
            output.Write(Signature);
            var header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)texture.Width);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)texture.Height);
            header[8] = 8; header[9] = 6;
            WriteChunk(output, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = texture.Width * 4;
                for (var y = 0; y < texture.Height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(texture.RgbaPixels, y * stride, stride);
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", []);
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length); output.Write(length);
            var typeBytes = Encoding.ASCII.GetBytes(type); output.Write(typeBytes); output.Write(data);
            var crc = Crc32(typeBytes, data); Span<byte> encodedCrc = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(encodedCrc, crc); output.Write(encodedCrc);
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            var crc = 0xffffffffu;
            Update(type); Update(data);
            return ~crc;

            void Update(byte[] bytes)
            {
                foreach (var value in bytes)
                {
                    crc ^= value;
                    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
                }
            }
        }
    }
}
