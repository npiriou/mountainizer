using System.Buffers.Binary;
using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3SoundTriggerDecoder
{
    private const int HeaderSize = 16;
    private const int BindingSize = 24;
    private const uint FillWord = 0xCCCCCCCC;
    private const byte EndMarker = 0xFF;
    private const int MaximumBindings = 100_000;
    private const int MaximumBlockItems = 100_000;
    private const int RandomizedBankSoundRecordSize = 56;

    public static SoundTriggerRandomizedBankSoundDefinition DecodeRandomizedBankSoundDefinition(ReadOnlySpan<byte> record)
    {
        if (record.Length < RandomizedBankSoundRecordSize)
            throw new FormatException("Randomized bank-sound record is truncated", 0, RandomizedBankSoundRecordSize, record.Length);
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(record[0x10..]),
            [
                BinaryPrimitives.ReadUInt32LittleEndian(record[0x14..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[0x1C..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[0x20..])
            ],
            [
                BinaryPrimitives.ReadInt32LittleEndian(record[0x24..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[0x28..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[0x2C..])
            ],
            BinaryPrimitives.ReadUInt32LittleEndian(record[0x30..]),
            BinaryPrimitives.ReadInt32LittleEndian(record[0x34..]));
    }

    public static SoundTriggerInfoDefinition? TriggerInfoDefinition(uint id) => id switch
    {
        0 or 1 => new(id, SoundTriggerInfoKind.BuiltInSlot),
        2 => BankSound(id, 2, 50), 3 => BankSound(id, 2, 0), 4 => BankSound(id, 2, 1),
        5 => BankSound(id, 2, 2), 6 => BankSound(id, 2, 16), 7 => BankSound(id, 2, 17),
        8 => BankSound(id, 2, 18), 9 => BankSound(id, 2, 19), 10 => BankSound(id, 2, 20),
        11 => BankSound(id, 2, 21), 12 => BankSound(id, 2, 22), 13 => BankSound(id, 2, 23),
        14 => BankSound(id, 2, 32), 15 => BankSound(id, 2, 48), 16 => BankSound(id, 2, 49),
        17 => BankSound(id, 2, 50), 18 => BankSound(id, 2, 51), 19 => BankSound(id, 2, 52),
        20 => BankSound(id, 2, 56), 21 => BankSound(id, 2, 64), 22 => BankSound(id, 2, 81),
        23 => BankSound(id, 2, 82), 24 => BankSound(id, 2, 83), 25 => BankSound(id, 2, 112),
        26 => Audio(id, "C_Gen_01"), 27 => Audio(id, "C_Gen_02"),
        28 => Audio(id, "Birds_Falcon"), 29 => Audio(id, "Bird_Hawk"),
        30 => Audio(id, "Bird_Eagle"), 31 => Audio(id, "Bird_Raven"),
        32 => Audio(id, "Bird_Owl"), 33 => Audio(id, "Bird_Woodpeck"),
        34 => Audio(id, "Bird_Chichadee"), 35 => Audio(id, "Waterfall1"),
        36 => Audio(id, "C_Gen_03"), 37 => Audio(id, "C_Gen_04"),
        38 => Audio(id, "C_Gen_05"), 39 => Audio(id, "C_Gen_06"),
        40 => Audio(id, "C_Gen_07"),
        41 => BankSound(id, 5, 0), 42 => BankSound(id, 5, 1), 43 => BankSound(id, 5, 2),
        44 => BankSound(id, 2, 17), 45 => BankSound(id, 8, 45), 46 => BankSound(id, 8, 46),
        47 => BankSound(id, 2, 33), 48 => BankSound(id, 8, 47), 49 => BankSound(id, 2, 27),
        50 => BankSound(id, 2, 3), 51 => Audio(id, "Coyote"),
        52 => Audio(id, "Birds_Sparrow"), 53 => Audio(id, "Birds_Nightingale"),
        54 => Audio(id, "Birds_Crows"), 55 => Audio(id, "Bird_Crows"),
        56 => Audio(id, "Birds_Lark"), 57 => Audio(id, "Bird_Woodchirp"),
        58 => Audio(id, "Birds_Vulture"), 59 => Audio(id, "BC_AmbLoop1"),
        60 => Audio(id, "Trafficloop"), 61 => Audio(id, "Traffic_Loop2"),
        62 => Audio(id, "Steam1"), 63 => Audio(id, "Steam2"), 64 => Audio(id, "Sewer"),
        65 => Audio(id, "Parkade"), 66 => Audio(id, "CopCar"),
        67 => Audio(id, "Construction1"), 68 => Audio(id, "Construction2"),
        69 => Audio(id, "Wolves_2"), 70 => Audio(id, "Wolves_1"), 71 => Audio(id, "Elk_1"),
        72 => Audio(id, "Coyotes_1"), 73 => Audio(id, "Cougar_1"), 74 => Audio(id, "Bear_1"),
        75 => Audio(id, "Med_Wind"), 76 => Audio(id, "Heavy_winds2"),
        77 => new(id, SoundTriggerInfoKind.CrowdInstanceActivation), 78 => BankSound(id, 8, 48),
        79 => BankSound(id, 2, 4), 80 => BankSound(id, 2, 5),
        81 => Audio(id, "PoliceRadio"), 82 => Audio(id, "Culdron"),
        83 => Audio(id, "PeakAmb"), 84 => Audio(id, "BlimpLoop"),
        85 => Audio(id, "FlapLoop2"), 86 => Audio(id, "WaterGush1"),
        87 => Audio(id, "Waterfall1"), 88 => Audio(id, "FlagWind_loop"),
        89 => BankSound(id, 8, 125), 90 => Audio(id, "PartyLodge"),
        91 => Audio(id, "PilotAmb"), 92 => Audio(id, "CaveWind"),
        93 => BankSound(id, 9, 155),
        _ => null
    };

    public static string? SoundBankName(uint soundBankId) => soundBankId switch
    {
        0 => "MAIN", 1 => "BOARD", 2 => "MOUNTAIN", 4 => "TRANSPORT", 5 => "CROWD",
        6 => "AUX", 7 => "TRICKY", 8 => "TRACK_BANK_0", 9 => "TRACK_BANK_1", 10 => "LAND",
        16 => "SPUBOARD", _ => null
    };

    private static SoundTriggerInfoDefinition BankSound(uint id, uint soundBankId, uint soundIndex) =>
        new(id, SoundTriggerInfoKind.IndexedBankSound, SoundBankId: soundBankId, SoundIndex: soundIndex,
            SoundBankName: SoundBankName(soundBankId));
    private static SoundTriggerInfoDefinition Audio(uint id, string name) =>
        new(id, SoundTriggerInfoKind.NamedAudioEvent, name);

    public static string SpatialDescriptorKindName(uint kind) => kind switch
    {
        0 => "Sphere",
        1 => "Oriented Ellipsoid",
        2 => "Directional Cone",
        3 => "Sphere (Fixed Falloff)",
        _ => "Unknown"
    };

    public static float EvaluateSpatialFalloff(SoundTriggerFalloffCurve curve, float normalizedDistance)
    {
        var distance = normalizedDistance;
        return curve switch
        {
            SoundTriggerFalloffCurve.OneMinusDistanceSquared => 1f - distance * distance,
            SoundTriggerFalloffCurve.OneMinusDistanceOverOnePointFiveMinusHalfDistance =>
                1f - distance / (1.5f - distance * 0.5f),
            SoundTriggerFalloffCurve.OneMinusDistance => 1f - distance,
            SoundTriggerFalloffCurve.OneMinusDistanceOverOnePlusHalfDistance =>
                (1f - distance) / (1f + distance * 0.5f),
            SoundTriggerFalloffCurve.SquaredOneMinusDistance => (1f - distance) * (1f - distance),
            SoundTriggerFalloffCurve.OuterThirtyPercentLinear =>
                distance <= 0.7f ? 1f : (1f - distance) * 3.3333333f,
            _ => 0f
        };
    }

    public static SoundTriggerTableAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < HeaderSize)
            throw new FormatException("Sound-trigger table is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var header0 = reader.ReadUInt32Little(); var header1 = reader.ReadUInt32Little();
        var scale = reader.ReadSingleLittle(); var bindingCount = checked((int)reader.ReadUInt32Little());
        if (bindingCount is < 0 or > MaximumBindings)
            throw new FormatException($"Sound-trigger binding count {bindingCount} exceeds the safety limit", reader.AbsolutePosition - 4, bindingCount, MaximumBindings);
        var tableBytes = checked(bindingCount * BindingSize); var tableEnd = checked(HeaderSize + tableBytes);
        if (tableEnd > data.Length)
            throw new FormatException("Sound-trigger binding table is out of bounds", source.LogicalOffset ?? 0, tableEnd, data.Length);
        var populated = bindingCount > 0;
        if (populated && (header0 != 0xCCCCCC00 || header1 != FillWord || scale != 1.24f))
            throw new FormatException("Sound-trigger table has an unknown header", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        if (!populated && (header0 != 0 || header1 != 0 || scale != -1f || data.Length != HeaderSize))
            throw new FormatException("Sound-trigger marker has an unknown header", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        if (populated && data[^1] != EndMarker)
            throw new FormatException("Sound-trigger table is missing its trailing end marker", (source.LogicalOffset ?? 0) + data.Length - 1, 1, 1);

        var pending = new (uint Key0, uint Key1, uint ObjectReference, int Offset)[bindingCount];
        for (var i = 0; i < pending.Length; i++)
        {
            var key0 = reader.ReadUInt32Little(); var key1 = reader.ReadUInt32Little(); var fill0 = reader.ReadUInt32Little();
            var objectReference = reader.ReadUInt32Little(); var offset = checked((int)reader.ReadUInt32Little()); var fill1 = reader.ReadUInt32Little();
            if (fill0 != FillWord || fill1 != FillWord)
                throw new FormatException($"Sound-trigger binding {i} has invalid fill words", reader.AbsolutePosition - BindingSize, BindingSize, BindingSize);
            if (offset < tableEnd || offset >= data.Length)
                throw new FormatException($"Sound-trigger binding {i} points outside the trailing block area", reader.AbsolutePosition - 8, offset, data.Length);
            pending[i] = (key0, key1, objectReference, offset);
        }

        var contentEnd = populated ? data.Length - 1 : data.Length;
        var offsets = pending.Select(x => x.Offset).Distinct().Order().ToArray();
        if (populated && (offsets.Length == 0 || offsets[0] != tableEnd || offsets.Any(x => (x & 3) != 0 || x >= contentEnd)))
            throw new FormatException("Sound-trigger blocks do not form an aligned trailing section", source.LogicalOffset ?? 0, data.Length, data.Length);
        var blocks = new SoundTriggerBlock[offsets.Length]; var blockIndices = new Dictionary<int, int>(offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            var end = i + 1 < offsets.Length ? offsets[i + 1] : contentEnd;
            if (end <= offsets[i]) throw new FormatException($"Sound-trigger block {i} has an invalid range", (source.LogicalOffset ?? 0) + offsets[i], end - offsets[i], data.Length - offsets[i]);
            blocks[i] = DecodeBlock(data[offsets[i]..end], source, offsets[i]); blockIndices[offsets[i]] = i;
        }
        var bindings = pending.Select(x => x.ObjectReference == uint.MaxValue
            ? new SoundTriggerBinding(x.Key0, x.Key1, -1, -1, blockIndices[x.Offset])
            : new SoundTriggerBinding(x.Key0, x.Key1, (int)(x.ObjectReference & 0xff),
                checked((int)(x.ObjectReference >> 8)), blockIndices[x.Offset])).ToArray();
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Sound Trigger Table", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Header0"] = $"0x{header0:X8}", ["Header1"] = $"0x{header1:X8}", ["Scale"] = scale,
            ["BindingCount"] = bindings.Length, ["UniqueBlockCount"] = blocks.Length,
            ["UniqueBindingIdentityCount"] = bindings.Select(x => x.SerializedIdentity).Distinct().Count(),
            ["AnchorObjectReferenceCount"] = bindings.Count(x => x.AnchorObjectReference is not null),
            ["UnanchoredBindingCount"] = bindings.Count(x => x.AnchorObjectReference is null),
            ["TriggerInfoReferenceCount"] = blocks.Sum(x => x.TriggerInfoIds.Count),
            ["SpatialDescriptorCount"] = blocks.Sum(x => x.SpatialDescriptors.Count),
            ["SpatialDescriptorKinds"] = string.Join(", ", blocks.SelectMany(x => x.SpatialDescriptors).GroupBy(x => x.Kind)
                .OrderBy(x => x.Key).Select(x => $"{SpatialDescriptorKindName(x.Key)} ({x.Key}): {x.Count()}")),
            ["DistanceFalloffCurves"] = string.Join(", ", blocks.SelectMany(x => x.SpatialDescriptors)
                .Where(x => x.DistanceFalloffCurve is not null).GroupBy(x => x.DistanceFalloffCurve)
                .OrderBy(x => x.Key).Select(x => $"{x.Key} ({(int)x.Key!.Value}): {x.Count()}")),
            ["MinimumRadius"] = blocks.SelectMany(x => x.SpatialDescriptors).Where(x => x.Radius is not null)
                .Select(x => x.Radius).DefaultIfEmpty().Min(),
            ["MaximumRadius"] = blocks.SelectMany(x => x.SpatialDescriptors).Where(x => x.Radius is not null)
                .Select(x => x.Radius).DefaultIfEmpty().Max(),
            ["TrailingBlockBytes"] = contentEnd - tableEnd, ["EndMarker"] = populated ? "0xFF" : "None", ["PayloadSize"] = data.Length
        };
        return new($"Sound Trigger Table {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, bindings, blocks, properties);
    }

    private static SoundTriggerBlock DecodeBlock(ReadOnlySpan<byte> data, SourceByteRange source, int relativeOffset)
    {
        var absoluteOffset = (source.LogicalOffset ?? 0) + relativeOffset;
        if (data.Length < 8)
            throw new FormatException("Sound-trigger block is truncated", absoluteOffset, 8, data.Length);
        var reader = new BinarySpanReader(data, absoluteOffset);
        var triggerInfoCount = checked((int)reader.ReadUInt32Little());
        var descriptorCount = checked((int)reader.ReadUInt32Little());
        if (triggerInfoCount is < 0 or > MaximumBlockItems || descriptorCount is < 0 or > MaximumBlockItems)
            throw new FormatException("Sound-trigger block count exceeds the safety limit", absoluteOffset, data.Length, data.Length);
        if ((long)triggerInfoCount * 4 > reader.Remaining)
            throw new FormatException("Sound-trigger info-ID list is truncated", reader.AbsolutePosition, (long)triggerInfoCount * 4, reader.Remaining);

        var triggerInfoIds = new uint[triggerInfoCount];
        for (var i = 0; i < triggerInfoIds.Length; i++) triggerInfoIds[i] = reader.ReadUInt32Little();
        var descriptors = new SoundTriggerSpatialDescriptor[descriptorCount];
        for (var i = 0; i < descriptors.Length; i++)
        {
            var descriptorOffset = reader.AbsolutePosition;
            var kind = reader.ReadUInt32Little(); var triggerInfoId = reader.ReadUInt32Little();
            var parameterCount = kind switch { 0 => 5, 1 or 2 => 10, 3 => 4, _ => -1 };
            if (parameterCount < 0)
                throw new FormatException($"Sound-trigger spatial descriptor {i} has unknown kind {kind}", descriptorOffset, reader.Remaining + 8, reader.Remaining + 8);
            var values = new float[parameterCount];
            for (var j = 0; j < values.Length; j++) values[j] = reader.ReadSingleLittle();
            if (values.Any(x => !float.IsFinite(x)))
                throw new FormatException($"Sound-trigger spatial descriptor {i} contains non-finite values", descriptorOffset, 8 + parameterCount * 4, 8 + parameterCount * 4);
            var serializedPosition = new Vector3(values[0], values[1], values[2]);
            var parameters = values[3..];
            var serializedOrientationAxis = kind switch
            {
                1 => new Vector3(parameters[3], parameters[4], parameters[5]),
                2 => new Vector3(parameters[1], parameters[2], parameters[3]),
                _ => (Vector3?)null
            };
            descriptors[i] = new(kind, triggerInfoId, Ssx3Coordinates.ToMountainizer(serializedPosition), serializedPosition,
                parameters, 8 + parameterCount * 4)
            {
                Radius = kind is 0 or 2 or 3 ? parameters[0] : null,
                SemiAxisLengths = kind == 1 ? new Vector3(parameters[0], parameters[1], parameters[2]) : null,
                SerializedOrientationAxisSsx = serializedOrientationAxis,
                OrientationAxis = serializedOrientationAxis is Vector3 axis ? Ssx3Coordinates.ToMountainizer(axis) : null,
                DistanceFalloffCurve = FalloffCurve(kind switch
                {
                    0 => parameters[1],
                    1 => parameters[6],
                    2 => parameters[4],
                    _ => null
                }),
                ConeCosineThreshold = kind == 2 ? parameters[5] : null,
                AngularFalloffCurve = FalloffCurve(kind == 2 ? parameters[6] : null)
            };
        }
        if (reader.Remaining != 0)
            throw new FormatException("Sound-trigger block has unconsumed bytes", reader.AbsolutePosition, reader.Remaining, reader.Remaining);
        return new(relativeOffset, data.ToArray(), triggerInfoIds, descriptors, data.Length);
    }

    private static SoundTriggerFalloffCurve? FalloffCurve(float? value)
    {
        if (value is null || value != MathF.Truncate(value.Value)
            || value < (float)SoundTriggerFalloffCurve.OneMinusDistanceSquared
            || value > (float)SoundTriggerFalloffCurve.OuterThirtyPercentLinear)
            return null;
        return (SoundTriggerFalloffCurve)(int)value.Value;
    }
}
