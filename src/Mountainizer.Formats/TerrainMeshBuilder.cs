using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class TerrainMeshBuilder
{
    public static IReadOnlyList<Vector3> DecodeControlPoints(IReadOnlyList<Vector3> storedDifferenceCoefficients)
    {
        if (storedDifferenceCoefficients.Count != 16) throw new ArgumentException("A bicubic patch requires 16 coefficients");
        var coefficients = storedDifferenceCoefficients.Reverse().ToArray();
        var intermediate = new Vector3[16];
        var result = new Vector3[16];
        for (var row = 0; row < 4; row++)
        {
            var b = row * 4;
            intermediate[b] = coefficients[b];
            intermediate[b + 1] = coefficients[b + 1] / 3f + intermediate[b];
            intermediate[b + 2] = (coefficients[b + 2] + coefficients[b + 1]) / 3f + intermediate[b + 1];
            intermediate[b + 3] = coefficients[b + 3] + coefficients[b + 2] + coefficients[b + 1] + coefficients[b];
        }
        for (var column = 0; column < 4; column++)
        {
            result[column] = intermediate[column];
            result[4 + column] = intermediate[4 + column] / 3f + result[column];
            result[8 + column] = (intermediate[8 + column] + intermediate[4 + column]) / 3f + result[4 + column];
            result[12 + column] = intermediate[12 + column] + intermediate[8 + column] + intermediate[4 + column] + intermediate[column];
        }
        return result.Select(Ssx3Coordinates.ToMountainizer).ToArray();
    }

    public static MeshData Tessellate(IReadOnlyList<Vector3> points, int subdivisions = 8,
        IReadOnlyList<Vector2>? textureCorners = null)
    {
        if (points.Count != 16) throw new ArgumentException("A bicubic patch requires 16 control points");
        subdivisions = Math.Clamp(subdivisions, 1, 64);
        var width = subdivisions + 1;
        var vertices = new Vector3[width * width];
        var normals = new Vector3[vertices.Length];
        var uvs = new Vector2[vertices.Length];
        for (var y = 0; y < width; y++) for (var x = 0; x < width; x++)
        {
            var u = x / (float)subdivisions;
            var v = y / (float)subdivisions;
            var bu = Basis(u); var bv = Basis(v);
            var p = Vector3.Zero;
            for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++) p += points[row * 4 + column] * bv[row] * bu[column];
            var i = y * width + x; vertices[i] = p;
            uvs[i] = textureCorners is { Count: 4 }
                ? Vector2.Lerp(Vector2.Lerp(textureCorners[0], textureCorners[1], u),
                    Vector2.Lerp(textureCorners[2], textureCorners[3], u), v)
                : new(u, v);
        }
        var indices = new uint[subdivisions * subdivisions * 6]; var cursor = 0;
        for (var y = 0; y < subdivisions; y++) for (var x = 0; x < subdivisions; x++)
        {
            uint a = (uint)(y * width + x), b = a + 1, c = a + (uint)width, d = c + 1;
            indices[cursor++] = a; indices[cursor++] = c; indices[cursor++] = b;
            indices[cursor++] = b; indices[cursor++] = c; indices[cursor++] = d;
        }
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        for (var i = 0; i < normals.Length; i++) normals[i] = normals[i].LengthSquared() > 0 ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        return new(vertices, normals, uvs, indices);
    }

    private static float[] Basis(float t) { var s = 1f - t; return [s * s * s, 3f * s * s * t, 3f * s * t * t, t * t * t]; }
}
