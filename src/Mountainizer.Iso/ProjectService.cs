using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mountainizer.Core;
using Mountainizer.Formats;

namespace Mountainizer.Iso;

public sealed record IsoIdentification(string IsoPath, string VolumeIdentifier, string Sha256, string DetectedRevision,
    bool IsTargetRevision, int FileCount, IReadOnlyList<string> WorldArchives);

public sealed class MountainizerProject
{
    public int FormatVersion { get; init; } = 1;
    public required string ProjectName { get; init; }
    public required string SourceIsoPath { get; init; }
    public required string SourceIsoHash { get; init; }
    public required string DetectedRevision { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public string ExtractedRoot { get; init; } = "extracted";
    public string? SelectedLevel { get; set; }
    public int CacheVersion { get; init; } = 1;
    [JsonIgnore] public string ProjectDirectory { get; set; } = string.Empty;
}

public static class ProjectService
{
    public const string TargetExecutable = "SLUS_207.72";

    public static async Task<IsoIdentification> IdentifyAsync(string isoPath, DiagnosticBag diagnostics,
        IProgress<(long Current, long Total, string Stage)>? progress = null, CancellationToken cancellationToken = default)
    {
        string hash;
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[4 * 1024 * 1024]; long current = 0;
            while (true) { var read = await stream.ReadAsync(buffer, cancellationToken); if (read == 0) break; sha.AppendData(buffer, 0, read); current += read; progress?.Report((current, stream.Length, "Hashing ISO")); }
            hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        using var iso = Iso9660Image.Open(isoPath, diagnostics);
        var executable = iso.Entries.FirstOrDefault(x => string.Equals(System.IO.Path.GetFileName(x.Path), TargetExecutable, StringComparison.OrdinalIgnoreCase));
        var revision = executable is null ? "Unrecognized (best-effort read-only)" : TargetExecutable;
        var archives = iso.Entries.Where(x => !x.IsDirectory && x.Path.EndsWith(".BIG", StringComparison.OrdinalIgnoreCase) && x.Path.Contains("WORLDS", StringComparison.OrdinalIgnoreCase)).Select(x => x.Path).ToArray();
        if (executable is null) diagnostics.Warn("ISO010", $"{TargetExecutable} was not found; import will be best-effort and read-only", isoPath);
        return new(isoPath, iso.VolumeIdentifier, hash, revision, executable is not null, iso.Entries.Count, archives);
    }

    public static async Task<MountainizerProject> ImportAsync(string isoPath, string projectDirectory, string projectName,
        DiagnosticBag diagnostics, IProgress<(long Current, long Total, string Stage)>? progress = null, CancellationToken cancellationToken = default)
    {
        var identification = await IdentifyAsync(isoPath, diagnostics, progress, cancellationToken);
        Directory.CreateDirectory(projectDirectory);
        foreach (var child in new[] { "source", "extracted", "cache", "logs" }) Directory.CreateDirectory(System.IO.Path.Combine(projectDirectory, child));
        using var iso = Iso9660Image.Open(isoPath, diagnostics);
        var archiveEntry = iso.Find("DATA/WORLDS/BAM.BIG") ?? iso.Entries.FirstOrDefault(x => x.Path.EndsWith("/BAM.BIG", StringComparison.OrdinalIgnoreCase));
        if (archiveEntry is null) throw new InvalidDataException("No DATA/WORLDS/BAM.BIG archive was found in the ISO");
        var archivePath = System.IO.Path.Combine(projectDirectory, "extracted", "DATA", "WORLDS", "BAM.BIG");
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != archiveEntry.Length) { progress?.Report((0, archiveEntry.Length, "Extracting BAM.BIG")); iso.Extract(archiveEntry, archivePath); }
        var archive = BigArchive.Open(archivePath, diagnostics);
        foreach (var extension in new[] { ".sdb", ".ssb", ".phm", ".psm" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries.FirstOrDefault(x => x.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            if (entry is null) { diagnostics.Warn("PRJ002", $"BAM archive has no {extension} entry", archivePath); continue; }
            var output = System.IO.Path.Combine(projectDirectory, "extracted", "DATA", "WORLDS", System.IO.Path.GetFileName(entry.Name));
            if (!File.Exists(output) || new FileInfo(output).Length != entry.Size) { progress?.Report((0, entry.Size, $"Extracting {entry.Name}")); archive.Extract(entry, output); }
        }
        var project = new MountainizerProject { ProjectName = projectName, SourceIsoPath = System.IO.Path.GetFullPath(isoPath), SourceIsoHash = identification.Sha256,
            DetectedRevision = identification.DetectedRevision, CreatedUtc = DateTime.UtcNow, ProjectDirectory = System.IO.Path.GetFullPath(projectDirectory) };
        Save(project);
        diagnostics.SaveJson(System.IO.Path.Combine(projectDirectory, "logs", "import-diagnostics.json"));
        return project;
    }

    public static MountainizerProject Open(string projectPath)
    {
        if (Directory.Exists(projectPath)) projectPath = System.IO.Path.Combine(projectPath, "project.json");
        var project = JsonSerializer.Deserialize<MountainizerProject>(File.ReadAllText(projectPath), DiagnosticBag.JsonOptions) ?? throw new InvalidDataException("project.json is empty");
        if (project.FormatVersion != 1) throw new InvalidDataException($"Unsupported project format version {project.FormatVersion}");
        project.ProjectDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath))!;
        return project;
    }

    public static void Save(MountainizerProject project)
    {
        var path = System.IO.Path.Combine(project.ProjectDirectory, "project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(project, DiagnosticBag.JsonOptions));
    }

    public static string WorldFile(MountainizerProject project, string extension) =>
        System.IO.Path.Combine(project.ProjectDirectory, project.ExtractedRoot, "DATA", "WORLDS", "bam" + extension);
}
