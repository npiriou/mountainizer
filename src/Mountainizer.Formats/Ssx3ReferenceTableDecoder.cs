using System.Buffers.Binary;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3ReferenceTableDecoder
{
    public const int NisSlotCount = 18;
    private const int NisPayloadSize = NisSlotCount * 4;

    public static string? NisSlotRole(int slot) => slot switch
    {
        0 => "Finish podium steps",
        1 => "Start gate main modules",
        2 => "Gondola helipad",
        3 => "Gondola station",
        4 => "NIS transport",
        5 => "OS609 in-air model",
        6 => "Gondola in-air model",
        7 => "NIS lodge",
        8 => "Finish podium floor",
        10 => "Start jumbotron",
        15 => "Alternate OS609 in-air model",
        _ => null
    };

    public static IReadOnlyList<int> NisRuntimeCommandIds(int slot) => slot switch
    {
        0 => [20, 21, 22, 23],
        1 => [25],
        2 => [26],
        3 => [27],
        4 => [28],
        5 => [29],
        6 => [30],
        7 => [19],
        8 => [24],
        9 => [31],
        10 => [32],
        11 => [33],
        12 => [34],
        13 => [35],
        14 => [36],
        15 => [37],
        16 => [38],
        17 => [39],
        _ => []
    };

    public static NisScriptObjectTableAsset DecodeNis(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length != NisPayloadSize)
            throw new FormatException("NIS script-object table has an unexpected size", source.LogicalOffset ?? 0, NisPayloadSize, data.Length);
        var slots = new NisScriptObjectSlot[NisSlotCount];
        for (var i = 0; i < slots.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            PackedObjectReference? reference = value == uint.MaxValue
                ? null
                : new((int)(value & 0xff), checked((int)(value >> 8)));
            slots[i] = new(i, reference, NisSlotRole(i), NisRuntimeCommandIds(i));
        }
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 NIS Script-Object Table",
            ["TrackId"] = trackId, ["ResourceId"] = resourceId, ["SlotCount"] = slots.Length,
            ["TargetResourceType"] = 3,
            ["RuntimeConsumer"] = "cSSXScriptEngine object-transform lookup",
            ["RuntimeAddressableSlotCount"] = slots.Count(x => x.IsRuntimeAddressable),
            ["MissingSlotBehavior"] = "Leaves neutral initialized transform outputs unchanged",
            ["PopulatedSlotCount"] = slots.Count(x => x.IsPopulated),
            ["References"] = string.Join(", ", slots.Select(x => x.ObjectReference is not { } reference ? $"{x.Index}: missing"
                : $"{x.Index}{(x.ObservedRole is { } role ? $" ({role})" : "")}: Type 3 {reference.TrackId}:{reference.ResourceId}")),
            ["PayloadSize"] = data.Length };
        return new($"NIS Script-Object Table {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, slots, properties);
    }

    public static NavigationTableMarker DecodeNavigationMarker(SourceByteRange source, int trackId, int resourceId)
    {
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Navigation Table Marker",
            ["TrackId"] = trackId, ["ResourceId"] = resourceId, ["Marker"] = true, ["PayloadSize"] = 0 };
        return new($"Navigation Marker {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Low },
            trackId, resourceId, properties);
    }
}
