using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3PlanarRouteDecoder
{
    private const int HeaderSize = 16;
    private const int SampleSize = 20;
    private const int MarkerSize = 8;
    private const int MandatoryMarkerCount = 2;
    private const int MaximumSamples = 100_000;
    private const int MaximumMarkers = 4096;

    public static string MarkerKindName(uint kind) => kind switch
    {
        0 => "Start",
        1 => "Checkpoint",
        2 => "Finish",
        _ => "Unknown"
    };

    public static string MarkerTextureName(uint kind) => kind switch
    {
        0 or 2 => "chkstart",
        1 => "chkpt",
        _ => "Unknown"
    };

    public static PlanarRouteAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < HeaderSize)
            throw new FormatException("Planar-route table is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var sampleCount = checked((int)reader.ReadUInt32Little());
        var sampleStride = checked((int)reader.ReadUInt32Little());
        var markerCount = checked((int)reader.ReadUInt32Little());
        var baseSectionSize = checked((int)reader.ReadUInt32Little());
        if (sampleCount is <= 0 or > MaximumSamples)
            throw new FormatException($"Planar-route sample count {sampleCount} is invalid", reader.AbsolutePosition - 16, sampleCount, MaximumSamples);
        if (sampleStride != SampleSize)
            throw new FormatException($"Planar-route sample stride {sampleStride} is unsupported", reader.AbsolutePosition - 12, sampleStride, SampleSize);
        if (markerCount is < MandatoryMarkerCount or > MaximumMarkers)
            throw new FormatException($"Planar-route marker count {markerCount} is invalid", reader.AbsolutePosition - 8, markerCount, MaximumMarkers);
        var expectedBaseSize = checked(sizeof(float) + sampleCount * SampleSize + MandatoryMarkerCount * MarkerSize);
        var expectedSize = checked(HeaderSize + sizeof(float) + sampleCount * SampleSize + markerCount * MarkerSize);
        if (baseSectionSize != expectedBaseSize || data.Length != expectedSize)
            throw new FormatException("Planar-route section sizes do not match the header", source.LogicalOffset ?? 0, expectedSize, data.Length);

        var totalLength = reader.ReadSingleLittle();
        if (!float.IsFinite(totalLength) || totalLength <= 0)
            throw new FormatException("Planar-route total length is invalid", reader.AbsolutePosition - 4, 4, data.Length);
        var samples = new PlanarRouteSample[sampleCount]; var previousDistance = -1f;
        for (var i = 0; i < samples.Length; i++)
        {
            var lateralNormal = new Vector2(reader.ReadSingleLittle(), reader.ReadSingleLittle());
            var position = new Vector2(reader.ReadSingleLittle(), reader.ReadSingleLittle());
            var distance = reader.ReadSingleLittle();
            if (!IsFinite(lateralNormal) || !IsFinite(position) || !float.IsFinite(distance)
                || MathF.Abs(lateralNormal.LengthSquared() - 1f) > 0.01f || distance < previousDistance || distance < 0 || distance > totalLength + 1f)
                throw new FormatException($"Planar-route sample {i} has invalid geometry or distance", reader.AbsolutePosition - SampleSize, SampleSize, data.Length);
            samples[i] = new(lateralNormal, position, distance); previousDistance = distance;
        }
        if (MathF.Abs(samples[0].Distance) > 0.01f || MathF.Abs(samples[^1].Distance - totalLength) > 1f)
            throw new FormatException("Planar-route sample distances do not span the declared length", source.LogicalOffset ?? 0, sampleCount, data.Length);

        var markers = new PlanarRouteMarker[markerCount];
        for (var i = 0; i < markers.Length; i++)
        {
            var kind = reader.ReadUInt32Little(); var distance = reader.ReadSingleLittle();
            if (kind > 2 || !float.IsFinite(distance) || distance < 0 || distance > totalLength + 1f)
                throw new FormatException($"Planar-route marker {i} is invalid", reader.AbsolutePosition - MarkerSize, MarkerSize, data.Length);
            markers[i] = new(kind, distance);
        }
        if (!markers.Any(x => x.Kind == 0 && MathF.Abs(x.Distance) <= 0.01f)
            || !markers.Any(x => x.Kind == 2 && MathF.Abs(x.Distance - totalLength) <= 1f))
            throw new FormatException("Planar-route start/end markers are missing", source.LogicalOffset ?? 0, markerCount, data.Length);

        var minimum = new Vector2(samples.Min(x => x.Position.X), samples.Min(x => x.Position.Y));
        var maximum = new Vector2(samples.Max(x => x.Position.X), samples.Max(x => x.Position.Y));
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Type-21 Radar Route", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Role"] = "Radar / minimap course line",
            ["SampleCount"] = sampleCount, ["SampleStride"] = sampleStride, ["MarkerCount"] = markerCount,
            ["TotalLength"] = totalLength, ["PlanarMinimum"] = minimum, ["PlanarMaximum"] = maximum,
            ["SampleVectorRole"] = "Unit lateral normal used for cross-track HUD projection",
            ["RuntimeLoaderFunction"] = $"0x{PlanarRouteAsset.RuntimeLoaderFunction:X8}",
            ["RuntimeCursorFunction"] = $"0x{PlanarRouteAsset.RuntimeCursorFunction:X8}",
            ["RuntimeLateralProjectionFunction"] = $"0x{PlanarRouteAsset.RuntimeLateralProjectionFunction:X8}",
            ["RuntimeOnePlayerRadarFunction"] = $"0x{PlanarRouteAsset.RuntimeOnePlayerRadarFunction:X8}",
            ["RuntimeRadarWindow"] = PlanarRouteAsset.RuntimeRadarWindow,
            ["RuntimeCursorRule"] = "largest sample distance <= course distance, clamped to route ends",
            ["RuntimeLateralProjection"] = "dot(rider position - sample position, sample lateral normal)",
            ["MarkerKinds"] = string.Join(", ", markers.Select(x => $"{MarkerKindName(x.Kind)}@{x.Distance}")),
            ["MarkerTextures"] = string.Join(", ", markers.Select(x => $"{MarkerTextureName(x.Kind)}@{x.Distance}")),
            ["PayloadSize"] = data.Length
        };
        return new($"Radar Route {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, totalLength, samples, markers, properties);
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
