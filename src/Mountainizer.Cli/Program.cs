using System.Text.Json;
using Mountainizer.Core;
using Mountainizer.Export;
using Mountainizer.Formats;
using Mountainizer.Iso;

return await MountainizerCli.RunAsync(args);

internal static class MountainizerCli
{
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
                "inspect" when args.Length >= 3 => Inspect(args),
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
        var textureIds = parsed.Scene.Textures.Select(x => x.ResourceId).ToHashSet();
        var materialTextures = parsed.Scene.Materials.GroupBy(x => Key(x.TrackId, x.ResourceId)).ToDictionary(x => x.Key, x => (int)x.Last().TextureResourceId);
        var modelMaterialIds = parsed.Scene.Models.SelectMany(x => x.Submeshes).Where(x => x.MaterialResourceId >= 0).Select(x => Key(x.MaterialTrackId, x.MaterialResourceId)).Distinct().ToArray();
        var terrainNormals = parsed.Scene.Terrain.SelectMany(x => x.Mesh.Normals).ToArray();
        var report = JsonSerializer.Serialize(new { Level = level, TerrainPatches = parsed.Scene.Terrain.Count, PropInstances = parsed.Scene.Props.Count,
            Models = parsed.Scene.Models.Count, ModelsWithGeometry = parsed.Scene.Models.Count(x => x.Mesh is not null), Materials = parsed.Scene.Materials.Count, Textures = parsed.Scene.Textures.Count,
            ReferencedModels = referencedModelIds.Length, ResolvedInstances = parsed.Scene.Props.Count(x => decodedModelIds.Contains(Key(x.ModelTrackId, x.ModelResourceId))),
            ModelMaterials = modelMaterialIds.Length, ResolvedModelMaterials = modelMaterialIds.Count(x => materialTextures.TryGetValue(x, out var texture) && textureIds.Contains(texture)),
            ModelSubmeshes = parsed.Scene.Models.Sum(x => x.Submeshes.Count),
            TexturedModelSubmeshes = parsed.Scene.Models.SelectMany(x => x.Submeshes).Count(x => materialTextures.TryGetValue(Key(x.MaterialTrackId, x.MaterialResourceId), out var texture) && textureIds.Contains(texture)),
            Splines = parsed.Scene.Splines.Count, Triggers = parsed.Scene.Triggers.Count, VisibilityCurtains = parsed.Scene.VisibilityCurtains.Count,
            TerrainNormalAbsAverage = terrainNormals.Length == 0 ? null : new { X = terrainNormals.Average(x => Math.Abs(x.X)), Y = terrainNormals.Average(x => Math.Abs(x.Y)), Z = terrainNormals.Average(x => Math.Abs(x.Z)) },
            SplineDetails = parsed.Scene.Splines.Select(x => new { x.Name, TrackId = x.Properties["TrackId"], ResourceId = x.Properties["ResourceId"],
                PointCount = x.Points.Count, Start = x.Points.FirstOrDefault()?.Position, End = x.Points.LastOrDefault()?.Position }).ToArray(),
            NavigationProps = parsed.Scene.Props.Where(x => x.Name.Contains("start", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("finish", StringComparison.OrdinalIgnoreCase))
                .Select(x => new { x.Name, Position = x.Properties["Position"], AxisX = new { x.Transform.M11, x.Transform.M12, x.Transform.M13 },
                    AxisY = new { x.Transform.M21, x.Transform.M22, x.Transform.M23 }, AxisZ = new { x.Transform.M31, x.Transform.M32, x.Transform.M33 },
                    x.ModelTrackId, x.ModelResourceId }).ToArray(),
            UnresolvedModelMaterialKeys = modelMaterialIds.Where(x => !materialTextures.TryGetValue(x, out var texture) || !textureIds.Contains(texture)).Order().ToArray(),
            UnresolvedModelKeys = referencedModelIds.Where(x => !decodedModelIds.Contains(x)).Order().ToArray(),
            MaterialDetails = parsed.Scene.Materials.Select(x => new { x.TrackId, x.ResourceId, x.TextureResourceId }).ToArray(),
            ModelDetails = parsed.Scene.Models.Select(x => new { x.Name, ResourceId = Convert.ToInt32(x.Properties["ResourceId"]),
                Vertices = x.Mesh?.Positions.Count ?? 0, Triangles = (x.Mesh?.Indices.Count ?? 0) / 3,
                ObjectCount = x.Properties["ObjectCount"], HeaderMaterials = x.Properties["MaterialResourceIds"], DecodedParts = x.Properties["DecodedParts"],
                SpecialPacketHeaders = x.Properties["SpecialPacketHeaders"],
                SpecialPacketPreviews = x.Properties["SpecialPacketPreviews"], ModelDataOffset = x.Properties["ModelDataOffset"], PayloadSize = x.Properties["PayloadSize"],
                Materials = x.Submeshes.Select(s => $"{s.MaterialTrackId}:{s.MaterialResourceId}").Distinct().ToArray(),
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
    private static int Export(string[] args)
    {
        var project = ProjectService.Open(args[1]); var level = args[2]; var format = Option(args, "--format") ?? "obj"; var output = Option(args, "--output") ?? ".";
        if (!format.Equals("obj", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("This vertical slice supports OBJ; glTF is planned.");
        var parsed = Parse(project, level); Directory.CreateDirectory(output); var path = Path.Combine(output, level + ".obj"); ObjExporter.ExportScene(parsed.Scene, path); Console.WriteLine($"Wrote {path}"); return 0;
    }
    private static int DumpTextures(string[] args)
    {
        var project = ProjectService.Open(args[1]); var parsed = Parse(project, args[2]); var output = Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
        for (var i = 0; i < parsed.Scene.Textures.Count; i++)
        {
            var texture = parsed.Scene.Textures[i]; var group = Convert.ToInt32(texture.Properties["GroupIndex"]);
            var path = Path.Combine(output, $"{i:D4}-g{group:D3}-rid{texture.ResourceId:D3}-{texture.Width}x{texture.Height}.bmp");
            using var stream = File.Create(path); using var writer = new BinaryWriter(stream); var imageSize = checked(texture.Width * texture.Height * 4);
            writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(54 + imageSize); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(texture.Width); writer.Write(-texture.Height); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(imageSize); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
            for (var pixel = 0; pixel < texture.Width * texture.Height; pixel++)
            {
                writer.Write(texture.RgbaPixels[pixel * 4 + 2]); writer.Write(texture.RgbaPixels[pixel * 4 + 1]);
                writer.Write(texture.RgbaPixels[pixel * 4]); writer.Write((byte)255);
            }
        }
        Console.WriteLine($"Wrote {parsed.Scene.Textures.Count} textures to {output}"); return parsed.Diagnostics.HasErrors ? 2 : 0;
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
  mountainizer-cli inspect <project> <level> [--json <output>]
  mountainizer-cli dump-textures <project> <level> <output-directory>
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
