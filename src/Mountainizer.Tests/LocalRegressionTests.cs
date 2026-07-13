using Mountainizer.Core;
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
            Assert.IsTrue(result.Scene.Models.Count > 0, $"{course.Code} has no models");
            Assert.IsTrue(result.Scene.Splines.Count > 0, $"{course.Code} has no splines");
            Assert.IsTrue(CourseCameraPlacement.TryFind(result.Scene, course.Code, out var pose), $"{course.Code} has no usable start camera pose");
            Assert.IsTrue(float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z + pose.Target.X + pose.Target.Y + pose.Target.Z), $"{course.Code} start camera is not finite");
        }
    }
}
