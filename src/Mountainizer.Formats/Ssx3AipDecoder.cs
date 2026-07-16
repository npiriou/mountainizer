using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed record Ssx3AipDecodeResult(NavigationTableAsset Asset, uint Magic)
{
    public IReadOnlyList<NavigationPath> Paths => Asset.AiPaths.Concat(Asset.TrackPaths).ToArray();
    public int PairCount => Asset.TailPairs.Count;
    public int VolumeCount => Asset.Links.Count;
    public int TrailingBytes => Asset.TrailingBytes;
}

public static class Ssx3AipDecoder
{
    public const uint KnownMagic = 0x69696969;
    private const uint MaximumRecords = 100_000;

    public static Ssx3AipDecodeResult Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var magic = reader.ReadUInt32Little();
        if (magic != KnownMagic)
            throw new FormatException($"Unsupported AIP signature 0x{magic:X8}", source.LogicalOffset ?? 0, 4, data.Length);

        var aiPaths = new List<NavigationPath>();
        var aiCount = ReadCount(ref reader, "AI path");
        for (var i = 0; i < aiCount; i++)
            aiPaths.Add(ReadPath(ref reader, source, trackId, resourceId, i, NavigationPathKind.Ai));

        var trackPaths = new List<NavigationPath>();
        var trackCount = ReadCount(ref reader, "track path");
        for (var i = 0; i < trackCount; i++)
            trackPaths.Add(ReadPath(ref reader, source, trackId, resourceId, i, NavigationPathKind.Track));

        var pairCount = ReadCount(ref reader, "tail pair");
        var pairs = new NavigationPathTailPair[pairCount];
        for (var i = 0; i < pairs.Length; i++)
            pairs[i] = new(reader.ReadUInt32Little(), reader.ReadUInt32Little());
        var linkCount = ReadCount(ref reader, "path link");
        var links = new NavigationPathLink[linkCount];
        for (var i = 0; i < links.Length; i++)
        {
            var value = reader.ReadUInt32Little();
            var rawKind = unchecked((int)reader.ReadUInt32Little());
            var position = reader.ReadVector3();
            var direction = reader.ReadVector3();
            var aiPathIndex = checked((int)reader.ReadUInt32Little());
            var trackPathIndex = checked((int)reader.ReadUInt32Little());
            if (!IsFinite(position) || !IsFinite(direction))
                throw new FormatException($"Path link {i} contains a non-finite vector", reader.AbsolutePosition - 32, 24, 24);
            if ((uint)aiPathIndex >= (uint)aiPaths.Count || (uint)trackPathIndex >= (uint)trackPaths.Count)
                throw new FormatException($"Path link {i} references AI path {aiPathIndex}/{aiPaths.Count} or track path {trackPathIndex}/{trackPaths.Count} out of range",
                    reader.AbsolutePosition - 8, 8, 8);
            links[i] = new(value, rawKind, LinkRuntimeKindIndex(rawKind), position, direction, aiPathIndex, trackPathIndex);
        }
        var trailingBytes = reader.Remaining;
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Navigation Table", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["AiPathCount"] = aiPaths.Count, ["TrackPathCount"] = trackPaths.Count,
            ["TaggedPropertyCount"] = aiPaths.Sum(path => path.TaggedProperties.Count)
                + trackPaths.Sum(path => path.TaggedProperties.Count),
            ["RespawnableAiPathCount"] = aiPaths.Count(path => path.Respawnable == true),
            ["EventCount"] = aiPaths.Sum(path => path.Events.Count) + trackPaths.Sum(path => path.Events.Count),
            ["EventTypes"] = string.Join(", ", aiPaths.Concat(trackPaths).SelectMany(path => path.Events)
                .Select(pathEvent => pathEvent.Type).Distinct().Order()),
            ["SectionEntryCount"] = pairs.Length,
            ["PopulatedSectionEntries"] = string.Join(", ", pairs.Select((pair, index) => (pair, index))
                .Where(item => !item.pair.IsEmpty).Select(item => $"{item.index}:{item.pair.PathDistance:R}/{item.pair.Word1}")),
            ["LinkCount"] = links.Length,
            ["LinkKinds"] = string.Join(", ", links.Select(link => link.RawKind).Distinct().Order()),
            ["TrailingBytes"] = trailingBytes
        };
        var asset = new NavigationTableAsset($"Navigation Table {trackId}:{resourceId}",
            source with { Confidence = SupportConfidence.Medium }, trackId, resourceId,
            aiPaths, trackPaths, pairs, links, trailingBytes, properties);
        return new(asset, magic);
    }

    private static NavigationPath ReadPath(ref BinarySpanReader reader, SourceByteRange source, int trackId, int resourceId,
        int index, NavigationPathKind pathKind)
    {
        var offset = reader.Position;
        var kind = pathKind == NavigationPathKind.Ai ? "AI" : "Track";
        var propertyCount = ReadCount(ref reader, $"{kind} path property");
        var taggedProperties = new NavigationPathProperty[propertyCount];
        for (var i = 0; i < taggedProperties.Length; i++)
        {
            var propertyKind = reader.ReadUInt32Little();
            var payloadLength = ReadCount(ref reader, $"{kind} path property payload");
            taggedProperties[i] = new(propertyKind, reader.ReadBytes(payloadLength).ToArray());
        }
        var pointCount = ReadCount(ref reader, $"{kind} path point");
        var eventCount = ReadCount(ref reader, $"{kind} path event");
        var origin = reader.ReadVector3(); var boundsMin = reader.ReadVector3(); var boundsMax = reader.ReadVector3();
        if (!IsFinite(origin) || !IsFinite(boundsMin) || !IsFinite(boundsMax))
            throw new FormatException($"{kind} path {index} contains non-finite bounds", reader.AbsolutePosition - 36, 36, 36);

        var points = new Vector3[pointCount];
        var encodedPoints = new NavigationPathPoint[pointCount];
        for (var i = 0; i < points.Length; i++)
        {
            var encoded = reader.ReadVector4();
            var point = origin + new Vector3(encoded.X, encoded.Y, encoded.Z) * encoded.W;
            if (!IsFinite(point)) throw new FormatException($"{kind} path {index} contains a non-finite point", reader.AbsolutePosition - 16, 16, 16);
            points[i] = Ssx3Coordinates.ToMountainizer(point);
            encodedPoints[i] = new(new(encoded.X, encoded.Y, encoded.Z), encoded.W, points[i]);
        }
        var events = new NavigationPathEvent[eventCount];
        for (var i = 0; i < events.Length; i++)
        {
            var type = reader.ReadUInt32Little();
            events[i] = new(type, reader.ReadUInt32Little(), reader.ReadSingleLittle(), reader.ReadSingleLittle())
                { RuntimeKindIndex = EventRuntimeKindIndex(type) };
            if (!float.IsFinite(events[i].StartDistance) || !float.IsFinite(events[i].EndDistance))
                throw new FormatException($"{kind} path {index} event {i} contains a non-finite distance interval",
                    reader.AbsolutePosition - 8, 8, 8);
        }

        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = $"SSX3 {kind} Navigation Path", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["PathKind"] = kind, ["TaggedPropertyCount"] = taggedProperties.Length, ["PointCount"] = pointCount,
            ["EventCount"] = eventCount, ["Origin"] = Ssx3Coordinates.ToMountainizer(origin),
            ["BoundingBoxMin"] = Ssx3Coordinates.ToMountainizer(boundsMin),
            ["BoundingBoxMax"] = Ssx3Coordinates.ToMountainizer(boundsMax)
        };
        var distanceToFinish = pathKind == NavigationPathKind.Track
            ? taggedProperties.FirstOrDefault(property => property.Kind == 0)?.SingleValue : null;
        var aiPathMetadata = pathKind == NavigationPathKind.Ai
            ? taggedProperties.FirstOrDefault(property => property.Kind == 100)?.UInt32Value : null;
        var respawnableValue = pathKind == NavigationPathKind.Ai
            ? taggedProperties.FirstOrDefault(property => property.Kind == 101)?.UInt32Value : null;
        var totalLength = encodedPoints.Sum(point => point.Weight);
        if (distanceToFinish is not null) properties["DistanceToFinish"] = distanceToFinish;
        if (aiPathMetadata is not null) properties["AiPathMetadata"] = $"0x{aiPathMetadata.Value:X8}";
        if (respawnableValue is not null) properties["Respawnable"] = respawnableValue != 0;
        var pathSource = source with { SectionName = $"{source.SectionName}/{kind.ToLowerInvariant()} path {index}",
            OriginalIndex = index, LogicalOffset = (source.LogicalOffset ?? 0) + offset, Confidence = SupportConfidence.Medium };
        return new($"{kind} Path {trackId}:{resourceId}/{index:D3}", pathSource, points, events, properties)
        {
            Kind = pathKind, TaggedProperties = taggedProperties, EncodedPoints = encodedPoints,
            AiPathMetadata = aiPathMetadata, Respawnable = respawnableValue is null ? null : respawnableValue != 0,
            DistanceToFinish = distanceToFinish, TotalLength = totalLength
        };
    }

    private static int ReadCount(ref BinarySpanReader reader, string description)
    {
        var count = reader.ReadUInt32Little();
        if (count > MaximumRecords)
            throw new FormatException($"{description} count {count} exceeds the safety limit", reader.AbsolutePosition - 4, count, MaximumRecords);
        return checked((int)count);
    }

    private static readonly uint[] KnownEventTypes =
    {
        uint.MaxValue, 0, 1, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        100, 101, 102, 103, 110, 111, 112, 113, 300
    };
    private static readonly int[] KnownLinkKinds = { -1, 0, 1 };

    public static int EventRuntimeKindIndex(uint serializedType) => Array.IndexOf(KnownEventTypes, serializedType);
    public static int LinkRuntimeKindIndex(int serializedKind) => Array.IndexOf(KnownLinkKinds, serializedKind);

    public static IReadOnlyList<uint> EventTypes => KnownEventTypes;
    public static IReadOnlyList<int> LinkKinds => KnownLinkKinds;

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
