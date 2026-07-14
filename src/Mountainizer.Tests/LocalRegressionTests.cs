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
        foreach (var course in Ssx3CourseCatalog.Courses)
        {
            var result = Ssx3SsbReader.ParseCourse(ssbPath, sdb, course);
            Assert.IsFalse(result.Diagnostics.HasErrors, $"{course.Code} produced parse errors");
            Assert.IsTrue(result.Scene.Terrain.Count > 0, $"{course.Code} has no terrain");
            Assert.IsTrue(result.Scene.Props.Count > 0, $"{course.Code} has no props");
            Assert.IsTrue(result.Scene.Props.All(x => !string.IsNullOrWhiteSpace(x.Classification.Reason)), $"{course.Code} has unclassified props");
            Assert.IsTrue(result.Scene.Props.All(x => x.Properties.TryGetValue("TrailingHex", out var value)
                && value is string trailing && trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 7), $"{course.Code} has incomplete type-3 trailing fields");
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
            var unenriched = result.Scene.UnknownSections.Where(x => x.ResourceType is 4 or 5 or 6 or 7 or 18)
                .Where(x => !x.Properties.ContainsKey("ParsedType")).ToArray();
            Assert.IsTrue(unenriched.Length == 0, $"{course.Code} has unenriched visual/NIS resources: "
                + string.Join(", ", unenriched.GroupBy(x => (x.ResourceType, Size: x.Properties["PayloadSize"]))
                    .Select(x => $"type {x.Key.ResourceType}/size {x.Key.Size} x{x.Count()}")));
            Assert.IsTrue(result.Scene.UnknownSections.Where(x => x.ResourceType == 7)
                .All(x => x.Properties.TryGetValue("Position", out var value) && value is System.Numerics.Vector3), $"{course.Code} has a halo without a position");
            Assert.IsTrue(result.Scene.Splines.Count > 0, $"{course.Code} has no splines");
            Assert.IsTrue(CourseCameraPlacement.TryFind(result.Scene, course.Code, out var pose), $"{course.Code} has no usable start camera pose");
            Assert.IsTrue(float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z + pose.Target.X + pose.Target.Y + pose.Target.Z), $"{course.Code} start camera is not finite");
        }
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
