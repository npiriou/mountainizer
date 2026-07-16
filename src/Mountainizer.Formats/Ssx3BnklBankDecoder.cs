using System.Buffers.Binary;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3BnklBankDecoder
{
    public const int DefaultRootMidiNote = 60;
    public const string RuntimeEnvelopeDurationClamp = "(int32)duration < 0 ? 0x7FFFFFFF : duration";
    public const string RuntimeEnvelopeSlopeEquation = "(targetVolumeFixed16 - currentVolumeFixed16) / runtimeDurationHundredths";
    public const string RuntimeEnvelopeAdvanceEquation = "currentVolumeFixed16 += slopeFixed16; remainingDuration--; advance when remainingDuration == 0";
    public const string RuntimeLayerSelectionEquation = "minimumMidiNote <= midiNote <= maximumMidiNote && minimumVelocity <= velocity <= maximumVelocity";
    private const int HeaderSize = 20;
    private const int SlotOffsetSize = 4;
    private const int MaximumEntries = 65_535;
    private const int Ps2Platform = 5;

    public static BnklBankAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.IsEmpty)
        {
            var markerProperties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 BNKl Bank Marker",
                ["TrackId"] = trackId, ["ResourceId"] = resourceId, ["Marker"] = true, ["PayloadSize"] = 0 };
            return new($"BNKl Bank Marker {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Low },
                trackId, resourceId, 0, 0, [], [], 0, [], [], markerProperties);
        }
        if (data.Length < HeaderSize)
            throw new FormatException("BNKl bank is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        if (!data[..4].SequenceEqual("BNKl"u8))
            throw new FormatException("Type-20 resource has an unknown bank signature", source.LogicalOffset ?? 0, 4, data.Length);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var declaredSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        if (version != 5 || entryCount > MaximumEntries || declaredSize != data.Length)
            throw new FormatException("BNKl header values are unsupported or inconsistent", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var tableOffset = HeaderSize;
        var bodyOffset = checked(tableOffset + entryCount * SlotOffsetSize);
        if (bodyOffset > data.Length)
            throw new FormatException("BNKl slot table is out of bounds", (source.LogicalOffset ?? 0) + tableOffset, bodyOffset - tableOffset, data.Length - tableOffset);
        var reservedWords = new[]
        {
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..])
        };
        var slotRelativeOffsets = new uint[entryCount];
        var sounds = new List<BnklSoundEntry>();
        var previousHeaderOffset = -1;
        for (var slot = 0; slot < entryCount; slot++)
        {
            var tableEntryOffset = tableOffset + slot * SlotOffsetSize;
            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[tableEntryOffset..]);
            slotRelativeOffsets[slot] = relativeOffset;
            if (relativeOffset == 0)
                continue;
            var headerOffsetLong = (long)tableEntryOffset + relativeOffset;
            if ((relativeOffset & 3) != 0 || headerOffsetLong < bodyOffset || headerOffsetLong > data.Length - 4)
                throw new FormatException($"BNKl slot {slot} points to an unaligned or out-of-bounds sound header",
                    (source.LogicalOffset ?? 0) + tableEntryOffset, SlotOffsetSize, data.Length - tableEntryOffset);
            var headerOffset = (int)headerOffsetLong;
            if (headerOffset <= previousHeaderOffset)
                throw new FormatException("BNKl populated slot headers are not strictly ordered",
                    (source.LogicalOffset ?? 0) + tableEntryOffset, SlotOffsetSize, data.Length - tableEntryOffset);
            var sound = DecodeSound(data, source, slot, tableEntryOffset, relativeOffset, headerOffset);
            if (previousHeaderOffset >= 0 && sounds[^1].HeaderOffset + sounds[^1].SerializedSize > headerOffset)
                throw new FormatException("BNKl sound headers overlap", (source.LogicalOffset ?? 0) + headerOffset, 4, data.Length - headerOffset);
            sounds.Add(sound);
            previousHeaderOffset = headerOffset;
        }
        var body = data[bodyOffset..].ToArray();
        var infoSections = sounds.SelectMany(x => x.InfoSections).ToArray();
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 BNKl Bank", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Signature"] = Encoding.ASCII.GetString(data[..4]), ["Version"] = version, ["EntryCount"] = entryCount,
            ["ReservedWords"] = reservedWords, ["PopulatedSlots"] = sounds.Count, ["DummySlots"] = entryCount - sounds.Count,
            ["InfoSections"] = infoSections.Length, ["LoopedInfoSections"] = infoSections.Count(x => x.LoopStart is not null),
            ["LayeredSounds"] = sounds.Count(x => x.InfoSections.Count > 1),
            ["MaximumLayersPerSound"] = sounds.Count == 0 ? 0 : sounds.Max(x => x.InfoSections.Count),
            ["RuntimeLayerSelectionEquation"] = RuntimeLayerSelectionEquation,
            ["PlaybackEnvelopeSections"] = infoSections.Count(x => x.PlaybackEnvelopeOffset is not null),
            ["PlaybackEnvelopeSegments"] = infoSections.Sum(x => x.PlaybackEnvelopeSegments.Count),
            ["RuntimeEnvelopeDurationClamp"] = RuntimeEnvelopeDurationClamp,
            ["RuntimeEnvelopeSlopeEquation"] = RuntimeEnvelopeSlopeEquation,
            ["RuntimeEnvelopeAdvanceEquation"] = RuntimeEnvelopeAdvanceEquation,
            ["Codecs"] = infoSections.GroupBy(x => x.Codec).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count()),
            ["SampleRates"] = infoSections.GroupBy(x => x.SampleRate).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count()),
            ["RootMidiNotes"] = infoSections.GroupBy(x => x.RootMidiNote).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count()),
            ["BodyOffset"] = bodyOffset, ["BodyBytes"] = body.Length, ["PayloadSize"] = data.Length
        };
        return new($"BNKl Bank {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, version, entryCount, reservedWords, slotRelativeOffsets, bodyOffset, sounds, body, properties);
    }

    private static BnklSoundEntry DecodeSound(ReadOnlySpan<byte> data, SourceByteRange source, int slot,
        int tableEntryOffset, uint relativeOffset, int headerOffset)
    {
        if (!data.Slice(headerOffset, 2).SequenceEqual("PT"u8))
            throw new FormatException($"BNKl slot {slot} has an unknown sound-header signature",
                (source.LogicalOffset ?? 0) + headerOffset, 2, data.Length - headerOffset);
        var platform = BinaryPrimitives.ReadUInt16LittleEndian(data[(headerOffset + 2)..]);
        if (platform != Ps2Platform)
            throw new FormatException($"BNKl slot {slot} uses unsupported platform {platform}",
                (source.LogicalOffset ?? 0) + headerOffset + 2, 2, data.Length - headerOffset - 2);

        var sections = new List<BnklSoundInfoSection>();
        var cursor = headerOffset + 4;
        while (true)
        {
            var sectionOffset = cursor;
            var patches = new List<BnklPatch>();
            byte terminator;
            while (true)
            {
                EnsureAvailable(data, source, cursor, 1, "BNKl patch opcode is truncated");
                var patchOffset = cursor;
                var opcode = data[cursor++];
                if (opcode is 0xfe or 0xff)
                {
                    terminator = opcode;
                    break;
                }
                if (opcode is 0xfc or 0xfd)
                {
                    patches.Add(new(patchOffset, cursor, opcode, PatchName(opcode), null, []));
                    continue;
                }

                EnsureAvailable(data, source, cursor, 1, "BNKl patch length is truncated");
                var encodedLength = data[cursor++];
                int payloadLength;
                if (encodedLength == byte.MaxValue)
                {
                    EnsureAvailable(data, source, cursor, 4, "BNKl extended patch length is truncated");
                    var extendedLength = BinaryPrimitives.ReadUInt32BigEndian(data[cursor..]);
                    cursor += 4;
                    if (extendedLength > int.MaxValue)
                        throw new FormatException("BNKl extended patch is too large", (source.LogicalOffset ?? 0) + patchOffset, 6, data.Length - patchOffset);
                    payloadLength = (int)extendedLength;
                }
                else
                {
                    payloadLength = encodedLength;
                }
                EnsureAvailable(data, source, cursor, payloadLength, "BNKl patch payload is truncated");
                var payloadOffset = cursor;
                var payload = data.Slice(cursor, payloadLength).ToArray();
                cursor += payloadLength;
                uint? value = payloadLength <= 4 ? ReadBigEndianValue(payload) : null;
                patches.Add(new(patchOffset, payloadOffset, opcode, PatchName(opcode), value, payload));
            }
            sections.Add(CreateInfoSection(data, source, sectionOffset, cursor, terminator, patches));
            if (terminator == 0xff)
                break;
        }
        return new(slot, tableEntryOffset, relativeOffset, headerOffset, platform, sections, cursor - headerOffset);
    }

    private static BnklSoundInfoSection CreateInfoSection(ReadOnlySpan<byte> data, SourceByteRange source,
        int offset, int endOffset, byte terminator, IReadOnlyList<BnklPatch> patches)
    {
        uint? Value(byte opcode) => patches.LastOrDefault(x => x.Opcode == opcode)?.Value;
        var codec = Value(0xa0) ?? Value(0x83) ?? 0;
        var channelCount = Value(0x82) ?? 1;
        var sampleRate = Value(0x84) ?? 22_050;
        var channelOffsets = new byte[] { 0x88, 0x89, 0x94, 0x95, 0xa2, 0xa3 }
            .Select(Value).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var loopEnd = Value(0x87);
        var releaseEnvelopeSegmentIndex = Value(0x08) is uint releaseIndex ? checked((int)releaseIndex) : -1;
        var playbackEnvelopeSegmentCount = checked((int)(Value(0x09) ?? 1));
        var playbackEnvelopePointerPatch = patches.LastOrDefault(x => x.Opcode == 0x19);
        int? playbackEnvelopeOffset = null;
        var playbackEnvelopeSegments = new List<BnklEnvelopeSegment>();
        if (playbackEnvelopePointerPatch?.Value is uint relativeEnvelopeOffset)
        {
            var targetOffset = checked(playbackEnvelopePointerPatch.PayloadOffset + (int)relativeEnvelopeOffset);
            var byteCount = checked(playbackEnvelopeSegmentCount * 8);
            EnsureAvailable(data, source, targetOffset, byteCount, "BNKl playback-envelope data is out of bounds");
            playbackEnvelopeOffset = targetOffset;
            for (var index = 0; index < playbackEnvelopeSegmentCount; index++)
            {
                var segmentOffset = targetOffset + index * 8;
                var durationHundredths = BinaryPrimitives.ReadUInt32LittleEndian(data[segmentOffset..]);
                var volume = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(segmentOffset + 4)..]));
                playbackEnvelopeSegments.Add(new(segmentOffset, durationHundredths, durationHundredths / 100d, volume));
            }
        }
        return new(offset, endOffset, terminator, patches,
            checked((int)(Value(0x01) ?? 0)), checked((int)(Value(0x02) ?? 127)),
            checked((int)(Value(0x03) ?? 0)), checked((int)(Value(0x04) ?? 127)),
            checked((int)(Value(0x07) ?? DefaultRootMidiNote)), releaseEnvelopeSegmentIndex,
            playbackEnvelopeSegmentCount, playbackEnvelopeOffset, checked((int)(Value(0x1c) ?? 127)), playbackEnvelopeSegments,
            ToNullableInt(Value(0x80)), ToNullableInt(Value(0x81)),
            checked((int)channelCount), checked((int)codec),
            checked((int)sampleRate), checked((int)(Value(0x85) ?? 0)), ToNullableInt(Value(0x86)),
            loopEnd is null ? null : checked((int)loopEnd.Value + 1), Value(0x1a), Value(0x8c) ?? 0, channelOffsets);
    }

    private static int? ToNullableInt(uint? value) => value is null ? null : checked((int)value.Value);

    private static uint ReadBigEndianValue(ReadOnlySpan<byte> data)
    {
        uint value = 0;
        foreach (var item in data)
            value = (value << 8) | item;
        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, SourceByteRange source, int offset, int count, string message)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new FormatException(message, (source.LogicalOffset ?? 0) + offset, count, Math.Max(0, data.Length - offset));
    }

    public static string PatchName(byte opcode) => opcode switch
    {
        0x01 => "MinimumVelocity", 0x02 => "MaximumVelocity", 0x03 => "MinimumMidiNote", 0x04 => "MaximumMidiNote",
        0x06 => "Priority", 0x07 => "RootMidiNote", 0x08 => "ReleaseEnvelopeSegmentIndex",
        0x09 => "PlaybackEnvelopeSegmentCount", 0x0a => "BendRange",
        0x0b => "BankChannels", 0x0c => "Pan", 0x0d => "RandomPan", 0x0e => "Volume",
        0x0f => "RandomVolume", 0x10 => "Detune", 0x11 => "RandomDetune", 0x13 => "EffectBus",
        0x14 => "UserData", 0x19 => "PlaybackEnvelopeRelativeOffset", 0x1a => "LoopOffsetChannel1",
        0x1c => "InitialEnvelopeVolume", 0x80 => "StreamVersion", 0x81 => "BitsPerSample",
        0x82 => "ChannelCount", 0x83 => "LegacyCodec", 0x84 => "SampleRate", 0x85 => "SampleCount",
        0x86 => "LoopStart", 0x87 => "LoopEndMinusOne", 0x88 => "Channel1Offset", 0x89 => "Channel2Offset",
        0x8c => "Flags", 0x94 => "Channel3Offset", 0x95 => "Channel4Offset", 0xa0 => "Codec",
        0xa2 => "Channel5Offset", 0xa3 => "Channel6Offset", 0xfc => "Alignment", 0xfd => "InfoStart",
        _ => $"Unknown_{opcode:X2}"
    };
}
