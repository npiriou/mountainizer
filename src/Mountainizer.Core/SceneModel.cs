using System.Numerics;

namespace Mountainizer.Core;

public enum SupportConfidence { Unknown, Low, Medium, High, Verified }

public sealed record SourceByteRange(
    string SourceFile,
    long SourceOffset,
    long SourceLength,
    string SectionName,
    int OriginalIndex,
    SupportConfidence Confidence,
    long? LogicalOffset = null);

public interface ISceneItem
{
    string Name { get; }
    SourceByteRange Source { get; }
    IReadOnlyDictionary<string, object?> Properties { get; }
}

public sealed class MountainizerScene
{
    public required string Name { get; init; }
    public List<TerrainPatch> Terrain { get; } = [];
    public List<PropInstance> Props { get; } = [];
    public List<Spline> Splines { get; } = [];
    public List<TriggerVolume> Triggers { get; } = [];
    public List<VisibilityCurtain> VisibilityCurtains { get; } = [];
    public List<ModelAsset> Models { get; } = [];
    public List<MaterialAsset> Materials { get; } = [];
    public List<TextureAsset> Textures { get; } = [];
    public List<UnknownSection> UnknownSections { get; } = [];
    public SceneBounds Bounds => SceneBounds.FromPoints(Terrain.SelectMany(x => x.Mesh.Positions));
}

public sealed record MeshData(IReadOnlyList<Vector3> Positions, IReadOnlyList<Vector3> Normals,
    IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<uint> Indices);

public sealed record TerrainPatch(
    string Name,
    SourceByteRange Source,
    IReadOnlyList<Vector3> ControlPoints,
    MeshData Mesh,
    int TrackId,
    short TextureResourceId,
    short LightmapResourceId,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;

public sealed record PropInstance(string Name, SourceByteRange Source, Matrix4x4 Transform,
    int ModelTrackId, int ModelResourceId, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record ModelSubmesh(MeshData Mesh, int MaterialTrackId, int MaterialResourceId);
public sealed record ModelAsset(string Name, SourceByteRange Source, MeshData? Mesh, IReadOnlyList<ModelSubmesh> Submeshes,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record MaterialAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId, short TextureResourceId,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record TextureAsset(string Name, SourceByteRange Source, int Width, int Height, int TrackId,
    int ResourceId, byte[] RgbaPixels, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public bool Decoded => RgbaPixels.Length == Width * Height * 4;
}
public sealed record SplinePoint(Vector3 Position, float? Time = null);
public sealed record Spline(string Name, SourceByteRange Source, IReadOnlyList<SplinePoint> Points,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record TriggerVolume(string Name, SourceByteRange Source, Vector3 Minimum, Vector3 Maximum,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record VisibilityCurtain(string Name, SourceByteRange Source, IReadOnlyList<Vector3> Points,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record UnknownSection(string Name, SourceByteRange Source, int ResourceType, int TrackId,
    int ResourceId, byte[] PreviewBytes, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;

public readonly record struct SceneBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;
    public float Radius => Vector3.Distance(Minimum, Maximum) * 0.5f;
    public bool IsEmpty => Minimum == Maximum;

    public static SceneBounds FromPoints(IEnumerable<Vector3> points)
    {
        using var e = points.GetEnumerator();
        if (!e.MoveNext()) return new(Vector3.Zero, Vector3.Zero);
        var min = e.Current;
        var max = e.Current;
        while (e.MoveNext()) { min = Vector3.Min(min, e.Current); max = Vector3.Max(max, e.Current); }
        return new(min, max);
    }
}

public static class Ssx3Coordinates
{
    // SSX 3 stores world data with +Z as up. Mountainizer/OpenGL uses +Y as up.
    // This is a right-handed +90 degree rotation around X: (x, y, z) -> (x, z, -y).
    public static Vector3 ToMountainizer(Vector3 value) => new(value.X, value.Z, -value.Y);

    // MDR vertices remain in SSX-local coordinates, so an instance transform only
    // needs the world-space conversion appended to it.
    public static Matrix4x4 ToMountainizerWorldTransform(Matrix4x4 value) => value * new Matrix4x4(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);
}
