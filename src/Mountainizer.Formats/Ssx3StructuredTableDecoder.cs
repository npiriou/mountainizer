using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3StructuredTableDecoder
{
    private const int Type15HeaderSize = 64;
    private const int Type16HeaderSize = 92;
    private const int MaximumCount = 1_000_000;
    public const int WorldPainterQueryInitFunction = 0x002C0408;
    public const int WorldPainterSectionFixupFunction = 0x002BAF08;
    public const int WorldPainterSpatialLookupFunction = 0x002BAF90;
    public const int WorldPainterQueryFunction = 0x002C0778;

    public static StructuredTableAsset DecodeType15(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length == 8 && data.IndexOfAnyExcept((byte)0) < 0)
            return Marker(15, data.Length, source, trackId, resourceId);
        if (data.Length < Type15HeaderSize)
            throw new FormatException("Type-15 table is truncated", source.LogicalOffset ?? 0, Type15HeaderSize, data.Length);
        var length = data.Length; var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var headerWords = checked((int)reader.ReadUInt32Little());
        var directorySlotsWithSentinel = checked((int)reader.ReadUInt32Little());
        var headerSize = checked((int)reader.ReadUInt32Little());
        if (headerWords != 16 || directorySlotsWithSentinel != 14 || headerSize != Type15HeaderSize)
            throw new FormatException("Type-15 table has an unknown header", source.LogicalOffset ?? 0, Type15HeaderSize, data.Length);
        var offsets = new uint[13];
        for (var i = 0; i < offsets.Length; i++) offsets[i] = reader.ReadUInt32Little();
        var present = offsets.Select((value, index) => (value, index)).Where(x => x.value != uint.MaxValue).ToArray();
        if (present.Any(x => x.value < Type15HeaderSize || x.value >= length || (x.value & 3) != 0))
            throw new FormatException("Type-15 directory contains an invalid section offset", source.LogicalOffset ?? 0, Type15HeaderSize, data.Length);
        var starts = present.Select(x => checked((int)x.value)).Distinct().Order().ToArray();
        var sections = new List<StructuredTableSection>();
        var modifierSections = new List<WorldModifierSection>();
        var firstOffset = starts.Length == 0 ? data.Length : starts[0];
        if (firstOffset > Type15HeaderSize)
            sections.Add(new("Preamble", Type15HeaderSize, 1, 0, data[Type15HeaderSize..firstOffset].ToArray()));
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : data.Length;
            var slots = present.Where(x => x.value == (uint)starts[i]).Select(x => x.index).ToArray();
            if (slots.Length != 1)
                throw new FormatException("Type-15 World Painter sections unexpectedly share an offset",
                    (source.LogicalOffset ?? 0) + starts[i], end - starts[i], data.Length - starts[i]);
            var modifier = DecodeModifierSection(data[starts[i]..end], source, starts[i], slots[0], trackId);
            modifierSections.Add(modifier);
            sections.Add(new($"{modifier.TypeName} index and records", starts[i], modifier.RecordCount, 0,
                data[starts[i]..end].ToArray()));
        }
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Type-15 Structured Table", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["HeaderWords"] = headerWords, ["DirectorySlotCount"] = offsets.Length,
            ["PresentSectionCount"] = starts.Length, ["MissingSlotCount"] = offsets.Count(x => x == uint.MaxValue),
            ["WorldPainterRecordCount"] = modifierSections.Sum(x => x.RecordCount),
            ["WorldPainterTypes"] = string.Join(", ", modifierSections.OrderBy(x => x.Slot).Select(x => x.TypeName)),
            ["ModifierRecordCount"] = modifierSections.Sum(x => x.RecordCount),
            ["ModifierTypes"] = string.Join(", ", modifierSections.OrderBy(x => x.Slot).Select(x => x.TypeName)),
            ["RuntimeQueryInitFunction"] = $"0x{WorldPainterQueryInitFunction:X8}",
            ["RuntimeSectionFixupFunction"] = $"0x{WorldPainterSectionFixupFunction:X8}",
            ["RuntimeSpatialLookupFunction"] = $"0x{WorldPainterSpatialLookupFunction:X8}",
            ["RuntimeQueryFunction"] = $"0x{WorldPainterQueryFunction:X8}",
            ["DirectoryOffsets"] = string.Join(", ", offsets.Select(x => x == uint.MaxValue ? "missing" : x.ToString())),
            ["PayloadSize"] = data.Length
        };
        return new($"Type-15 World Painter Bank {trackId}:{resourceId}", source with { Confidence = SupportConfidence.High },
            15, trackId, resourceId, sections, properties) { ModifierSections = modifierSections };
    }

    public static StructuredTableAsset DecodeType16(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length == Type16HeaderSize && data.IndexOfAnyExcept((byte)0) < 0)
            return Marker(16, data.Length, source, trackId, resourceId);
        if (data.Length < Type16HeaderSize)
            throw new FormatException("Type-16 table is truncated", source.LogicalOffset ?? 0, Type16HeaderSize, data.Length);
        var length = data.Length; var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var signature0 = reader.ReadUInt32Little(); var signature1 = reader.ReadUInt32Little(); var reserved = reader.ReadUInt32Little();
        var scale = reader.ReadSingleLittle(); var rootReferenceCount = Count(reader.ReadUInt32Little(), "root-reference");
        var dataOffset = checked((int)reader.ReadUInt32Little()); var baseRecordCount = Count(reader.ReadUInt32Little(), "base-record");
        var baseRecordOffset = checked((int)reader.ReadUInt32Little());
        var count84 = Count(reader.ReadUInt32Little(), "84-byte"); var offset84 = checked((int)reader.ReadUInt32Little());
        var opaqueCount = Count(reader.ReadUInt32Little(), "opaque"); var opaqueOffset = checked((int)reader.ReadUInt32Little());
        var count56 = Count(reader.ReadUInt32Little(), "56-byte"); var offset56 = checked((int)reader.ReadUInt32Little());
        var variableCount = Count(reader.ReadUInt32Little(), "variable-record"); var variableOffset = checked((int)reader.ReadUInt32Little());
        var programOffset = checked((int)reader.ReadUInt32Little());
        var u16Count = Count(reader.ReadUInt32Little(), "u16-tail"); var u16Offset = checked((int)reader.ReadUInt32Little());
        var u32ACount = Count(reader.ReadUInt32Little(), "u32-tail-a"); var u32AOffset = checked((int)reader.ReadUInt32Little());
        var u32BCount = Count(reader.ReadUInt32Little(), "u32-tail-b"); var u32BOffset = checked((int)reader.ReadUInt32Little());
        if (signature0 != 0x1000 || signature1 != 0x3800 || reserved != 0 || !float.IsFinite(scale) || dataOffset != Type16HeaderSize)
            throw new FormatException("Type-16 table has an unknown header", source.LogicalOffset ?? 0, Type16HeaderSize, data.Length);

        var rootBytes = checked(rootReferenceCount * 4); var baseBytes = checked(baseRecordCount * 24);
        var baseOffset = checked(dataOffset + rootBytes); var baseEnd = checked(baseOffset + baseBytes);
        var orderedBoundaries = new[] { offset84, opaqueOffset, offset56, variableOffset, programOffset, u16Offset, u32AOffset, u32BOffset, length }
            .Where(x => x >= dataOffset).Distinct().Order().ToArray();
        if (baseRecordOffset != baseOffset || orderedBoundaries.Any(x => x > length) || baseEnd > orderedBoundaries[0])
            throw new FormatException("Type-16 base arrays overlap a later section", source.LogicalOffset ?? 0, baseEnd, data.Length);
        ValidateFixed(offset84, count84, 84, data.Length, "84-byte");
        ValidateFixed(offset56, count56, 56, data.Length, "56-byte");
        var expectedOffset84 = checked(offset56 + count56 * 56);
        var expectedOpaqueOffset = checked(offset84 + count84 * 84);
        if (offset56 != baseEnd || offset84 != expectedOffset84 || opaqueCount != 0
            || opaqueOffset != expectedOpaqueOffset || variableOffset != opaqueOffset)
            throw new FormatException("Type-16 modifier-block sections are not contiguous",
                (source.LogicalOffset ?? 0) + baseEnd, variableOffset - baseEnd, data.Length - baseEnd);
        if (variableOffset < dataOffset || programOffset < variableOffset || u16Offset < programOffset
            || u32AOffset < u16Offset || u32BOffset < u32AOffset || u32BOffset > data.Length)
            throw new FormatException("Type-16 section ordering is invalid", source.LogicalOffset ?? 0, Type16HeaderSize, data.Length);
        var u16End = checked(u16Offset + u16Count * 2); var u32AEnd = checked(u32AOffset + u32ACount * 4);
        var u32BEnd = checked(u32BOffset + u32BCount * 4);
        if ((u16End + 3 & ~3) != u32AOffset || u32AEnd != u32BOffset || u32BEnd != data.Length)
            throw new FormatException("Type-16 tail arrays do not match their counts", (source.LogicalOffset ?? 0) + u16Offset, data.Length - u16Offset, data.Length);

        var variableTableBytes = checked(variableCount * 4); var variableTableEnd = checked(variableOffset + variableTableBytes);
        if (variableTableEnd > programOffset)
            throw new FormatException("Type-16 variable-record directory is out of bounds", (source.LogicalOffset ?? 0) + variableOffset, variableTableBytes, programOffset - variableOffset);
        var variableOffsets = new int[variableCount]; reader.Seek(variableOffset);
        for (var i = 0; i < variableOffsets.Length; i++) variableOffsets[i] = checked((int)reader.ReadUInt32Little());
        if (variableOffsets.Any(x => x < variableTableEnd || x >= programOffset)
            || variableOffsets.Zip(variableOffsets.Skip(1)).Any(x => x.First >= x.Second))
            throw new FormatException("Type-16 variable-record offsets are invalid", (source.LogicalOffset ?? 0) + variableOffset, variableTableBytes, programOffset - variableOffset);

        var rootRailReferences = new PackedRailReference[rootReferenceCount];
        reader.Seek(dataOffset);
        for (var i = 0; i < rootRailReferences.Length; i++)
            rootRailReferences[i] = DecodeRailReference(reader.ReadUInt32Little());
        var railReferenceSets = new RailReferenceSet[baseRecordCount];
        reader.Seek(baseOffset);
        for (var recordIndex = 0; recordIndex < railReferenceSets.Length; recordIndex++)
        {
            var slots = new PackedRailReference?[6];
            for (var slot = 0; slot < slots.Length; slot++)
            {
                var packed = reader.ReadUInt32Little();
                if (packed != uint.MaxValue)
                    slots[slot] = DecodeRailReference(packed);
            }
            railReferenceSets[recordIndex] = new(recordIndex, slots);
        }

        var modifierProgramBlocks = new ModifierProgramBlock[count56];
        reader.Seek(offset56);
        for (var blockIndex = 0; blockIndex < modifierProgramBlocks.Length; blockIndex++)
        {
            var controlWord = reader.ReadUInt32Little();
            var slots = new PackedProgramReference?[13];
            for (var slot = 0; slot < slots.Length; slot++)
                slots[slot] = DecodeOptionalProgramReference(reader.ReadUInt32Little());
            modifierProgramBlocks[blockIndex] = new(blockIndex, controlWord, slots);
        }

        var modifierProgramGroups = new ModifierProgramGroup[count84];
        reader.Seek(offset84); var expectedFirstBlock = 0;
        for (var groupIndex = 0; groupIndex < modifierProgramGroups.Length; groupIndex++)
        {
            var controlWord = reader.ReadUInt32Little(); var blockCount = Count(reader.ReadUInt32Little(), "modifier-block group");
            var blockOffset = checked((int)reader.ReadUInt32Little());
            if (blockOffset != checked(offset56 + expectedFirstBlock * 56) || expectedFirstBlock + blockCount > count56)
                throw new FormatException("Type-16 modifier-block group has an invalid child range",
                    (source.LogicalOffset ?? 0) + offset84 + groupIndex * 84, 84, data.Length - offset84 - groupIndex * 84);
            var programReferences = new PackedProgramReference?[]
            {
                DecodeOptionalProgramReference(reader.ReadUInt32Little()),
                DecodeOptionalProgramReference(reader.ReadUInt32Little())
            };
            var slots = new PackedProgramReference?[13];
            for (var slot = 0; slot < slots.Length; slot++)
                slots[slot] = DecodeOptionalProgramReference(reader.ReadUInt32Little());
            var reservedReference = reader.ReadUInt32Little();
            var finalProgramReference = DecodeOptionalProgramReference(reader.ReadUInt32Little());
            var kind = reader.ReadUInt32Little();
            if (reservedReference != uint.MaxValue || kind != 2)
                throw new FormatException("Type-16 modifier-block group has unknown trailing fields",
                    (source.LogicalOffset ?? 0) + offset84 + groupIndex * 84 + 72, 12, data.Length - offset84 - groupIndex * 84 - 72);
            modifierProgramGroups[groupIndex] = new(groupIndex, controlWord, blockCount, expectedFirstBlock,
                [programReferences[0], programReferences[1], finalProgramReference], slots, kind);
            expectedFirstBlock += blockCount;
        }
        if (expectedFirstBlock != count56)
            throw new FormatException("Type-16 modifier-block groups do not cover the child block array",
                (source.LogicalOffset ?? 0) + offset84, count84 * 84, data.Length - offset84);

        var lunPrograms = new LunProgramRecord[variableCount];
        for (var programIndex = 0; programIndex < lunPrograms.Length; programIndex++)
        {
            var offset = variableOffsets[programIndex];
            var end = programIndex + 1 < variableOffsets.Length ? variableOffsets[programIndex + 1] : programOffset;
            lunPrograms[programIndex] = DecodeLunProgram(data[offset..end], source, offset, programIndex);
        }

        var railProgramOffsets = new int[u32ACount]; reader.Seek(u32AOffset);
        for (var i = 0; i < railProgramOffsets.Length; i++) railProgramOffsets[i] = checked((int)reader.ReadUInt32Little());
        var railProgramLength = u16Offset - programOffset;
        if (railProgramOffsets.Length > 0 && (railProgramOffsets[0] != 0
            || railProgramOffsets.Any(offset => offset < 0 || offset >= railProgramLength)
            || railProgramOffsets.Zip(railProgramOffsets.Skip(1)).Any(pair => pair.First >= pair.Second)))
            throw new FormatException("Type-16 rail-program record directory is invalid",
                (source.LogicalOffset ?? 0) + u32AOffset, u32ACount * 4, data.Length - u32AOffset);
        var railProgramRecords = new RailProgramRecord[railProgramOffsets.Length];
        for (var recordIndex = 0; recordIndex < railProgramRecords.Length; recordIndex++)
        {
            var relativeOffset = railProgramOffsets[recordIndex];
            var relativeEnd = recordIndex + 1 < railProgramOffsets.Length ? railProgramOffsets[recordIndex + 1] : railProgramLength;
            railProgramRecords[recordIndex] = DecodeRailProgramRecord(data[(programOffset + relativeOffset)..(programOffset + relativeEnd)],
                relativeOffset, recordIndex, source, programOffset, trackId, trackId == 0 ? null : u32BCount + recordIndex);
        }
        var railProgramReferenceIndices = new ushort[u16Count]; reader.Seek(u16Offset);
        for (var i = 0; i < railProgramReferenceIndices.Length; i++)
        {
            railProgramReferenceIndices[i] = reader.ReadUInt16Little();
            if (railProgramReferenceIndices[i] >= railProgramRecords.Length)
                throw new FormatException("Type-16 rail-program reference index is out of bounds",
                    (source.LogicalOffset ?? 0) + u16Offset + i * 2, 2, data.Length - u16Offset - i * 2);
        }
        var railSplineMetadataEntries = new RailSplineMetadataEntry[u32BCount]; reader.Seek(u32BOffset);
        for (var i = 0; i < railSplineMetadataEntries.Length; i++)
        {
            var packed = reader.ReadUInt32Little();
            var low = (ushort)packed; var high = (ushort)(packed >> 16);
            railSplineMetadataEntries[i] = new(packed, low, high, (RailSplineRole)low, (SsxSurfaceType)high);
        }

        var sections = new List<StructuredTableSection>
        {
            CreateSection(data, "Root packed-rail references", dataOffset, rootReferenceCount, 4),
            CreateSection(data, "Six-slot packed-rail reference sets", baseOffset, baseRecordCount, 24)
        };
        if (count56 > 0) sections.Add(CreateSection(data, "13-slot modifier program blocks", offset56, count56, 56));
        if (count84 > 0) sections.Add(CreateSection(data, "Modifier program block groups", offset84, count84, 84));
        sections.Add(new("LUN program directory and records", variableOffset, variableCount, 0, data[variableOffset..programOffset].ToArray()));
        sections.Add(new("Variable rail-program records", programOffset, railProgramRecords.Length, 0, data[programOffset..u16Offset].ToArray()));
        sections.Add(CreateSection(data, "Rail-program record references", u16Offset, u16Count, 2));
        sections.Add(CreateSection(data, "Rail-program record directory", u32AOffset, u32ACount, 4));
        sections.Add(CreateSection(data, "Per-spline packed rail metadata", u32BOffset, u32BCount, 4));
        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Type-16 Structured Table", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Scale"] = scale, ["RootReferenceCount"] = rootReferenceCount, ["BaseRecordCount"] = baseRecordCount,
            ["RailReferenceCount"] = rootRailReferences.Length + railReferenceSets.Sum(x => x.Slots.Count(slot => slot is not null)),
            ["PopulatedRailReferenceSlots"] = railReferenceSets.Sum(x => x.Slots.Count(slot => slot is not null)),
            ["MissingRailReferenceSlots"] = railReferenceSets.Sum(x => x.Slots.Count(slot => slot is null)),
            ["BaseRecordOffset"] = baseRecordOffset, ["ModifierProgramBlockCount"] = count56,
            ["ModifierProgramGroupCount"] = count84, ["ReservedRecordCount"] = opaqueCount,
            ["LunProgramCount"] = variableCount, ["LunProgramOffsets"] = string.Join(", ", variableOffsets),
            ["LunProgramBytes"] = lunPrograms.Sum(program => program.Program.Length),
            ["LunBytecodeBytes"] = lunPrograms.Sum(program => program.BytecodeLength),
            ["LunRoutineCount"] = lunPrograms.Sum(program => program.Routines.Count),
            ["LunRoutineDescriptorCount"] = lunPrograms.Sum(program => 1 + program.AdditionalDescriptors.Count),
            ["LunInstructionCount"] = lunPrograms.Sum(program => program.Instructions.Count),
            ["LunInstructionBytes"] = lunPrograms.Sum(program => program.Instructions.Sum(instruction => instruction.SerializedSize)),
            ["LunPaddingBytes"] = lunPrograms.Sum(program => program.PaddingBytes), ["RailProgramBytes"] = u16Offset - programOffset,
            ["RailProgramRecordCount"] = railProgramRecords.Length,
            ["RailProgramDescriptorCount"] = railProgramRecords.Sum(record => record.Descriptors.Count),
            ["RailProgramReferenceIndexCount"] = railProgramReferenceIndices.Length,
            ["RailSplineMetadataEntryCount"] = railSplineMetadataEntries.Length, ["PayloadSize"] = data.Length
        };
        return new($"Type-16 Rail/Spline Table {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            16, trackId, resourceId, sections, properties)
        {
            RootRailReferences = rootRailReferences,
            RailReferenceSets = railReferenceSets,
            ModifierProgramBlocks = modifierProgramBlocks,
            ModifierProgramGroups = modifierProgramGroups,
            LunPrograms = lunPrograms,
            RailProgramRecords = railProgramRecords,
            RailProgramReferenceIndices = railProgramReferenceIndices,
            RailSplineMetadataEntries = railSplineMetadataEntries
        };

    }

    private static StructuredTableSection CreateSection(ReadOnlySpan<byte> data, string name, int offset, int count, int stride) =>
        new(name, offset, count, stride, data.Slice(offset, checked(count * stride)).ToArray());

    private static int Count(uint value, string name)
    {
        if (value > MaximumCount) throw new InvalidDataException($"Type-16 {name} count {value} exceeds the safety limit");
        return checked((int)value);
    }

    private static PackedRailReference DecodeRailReference(uint packed) =>
        new(packed, (byte)packed, checked((int)(packed >> 8)));

    private static PackedProgramReference? DecodeOptionalProgramReference(uint packed) => packed == uint.MaxValue
        ? null
        : new(packed, (byte)packed, checked((int)(packed >> 8)));

    private static LunProgramRecord DecodeLunProgram(ReadOnlySpan<byte> data, SourceByteRange source, int offset, int index)
    {
        const uint lunMagic = 0x004E554C;
        const int headerSize = 16;
        if (data.Length < headerSize)
            throw new FormatException("Type-16 LUN program record is truncated",
                (source.LogicalOffset ?? 0) + offset, headerSize, data.Length);
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        var programLength = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        var declaredSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        var repeatedSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        var programEnd = checked(headerSize + programLength);
        if (magic != lunMagic || (programLength & 3) != 0 || declaredSize != repeatedSize
            || programEnd > declaredSize || declaredSize > data.Length || data[declaredSize..].IndexOfAnyExcept((byte)0) >= 0)
            throw new FormatException("Type-16 LUN program framing is invalid",
                (source.LogicalOffset ?? 0) + offset, data.Length, data.Length);
        const int descriptorSize = 16;
        var descriptorBytes = declaredSize - programEnd;
        if (programLength < descriptorSize + 4 || descriptorBytes % descriptorSize != 0)
            throw new FormatException("Type-16 LUN routine descriptors are misaligned",
                (source.LogicalOffset ?? 0) + offset, data.Length, data.Length);
        var program = data[headerSize..programEnd]; var bytecodeLength = programLength - descriptorSize;
        var routines = new List<LunRoutine>(); var instructions = new List<LunInstruction>(); var position = 0;
        var routineStart = 0; var routineInstructions = new List<LunInstruction>();
        while (position < bytecodeLength)
        {
            if (bytecodeLength - position < 4)
                throw new FormatException("Type-16 LUN bytecode ends inside an instruction",
                    (source.LogicalOffset ?? 0) + offset + headerSize + position, bytecodeLength - position, bytecodeLength - position);
            var opcode = program[position];
            if (opcode > 0x2a)
                throw new FormatException("Type-16 LUN bytecode contains an invalid opcode",
                    (source.LogicalOffset ?? 0) + offset + headerSize + position, 4, bytecodeLength - position);
            var instructionSize = LunInstructionSize(opcode);
            if (instructionSize > bytecodeLength - position)
                throw new FormatException("Type-16 LUN bytecode ends inside an instruction",
                    (source.LogicalOffset ?? 0) + offset + headerSize + position, instructionSize, bytecodeLength - position);
            uint? immediate = instructionSize == 8
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(program[(position + 4)..])
                : null;
            var operand0 = program[position + 1]; var operand1 = program[position + 2]; var operand2 = program[position + 3];
            var instruction = new LunInstruction(position, opcode, operand0, operand1, operand2,
                immediate, instructionSize, LunInstructionOperation(opcode),
                opcode == 0x21 ? operand0 : null, opcode == 0x21 ? operand1 : null, opcode == 0x21 ? operand2 : null,
                opcode == 0x21 ? LunNativeFunctionName(operand1) : null,
                opcode == 0x21 ? LunNativeFunctionSubsystem(operand1) : null);
            instructions.Add(instruction); routineInstructions.Add(instruction);
            position += instructionSize;
            if (opcode == 0x2a)
            {
                routines.Add(new(routines.Count, routineStart, routineInstructions.ToArray()));
                routineStart = position; routineInstructions = [];
            }
        }
        if (routineInstructions.Count != 0 || routines.Count == 0)
            throw new FormatException("Type-16 LUN routine has no valid terminator",
                (source.LogicalOffset ?? 0) + offset + headerSize + routineStart, bytecodeLength - routineStart, bytecodeLength - routineStart);

        var descriptors = new List<LunRoutineDescriptor>();
        descriptors.Add(DecodeLunRoutineDescriptor(program[bytecodeLength..], programLength, headerSize, bytecodeLength, 0));
        for (var descriptorIndex = 0; descriptorIndex < descriptorBytes / descriptorSize; descriptorIndex++)
        {
            var descriptorOffset = programEnd + descriptorIndex * descriptorSize;
            descriptors.Add(DecodeLunRoutineDescriptor(data[descriptorOffset..], descriptorOffset, headerSize,
                bytecodeLength, descriptorIndex + 1));
        }
        if (descriptors.Count != routines.Count
            || descriptors.Any(descriptor => descriptor.ProgramOffset != 0)
            || descriptors.Select(descriptor => checked((int)descriptor.EntryWordOffset * 4)).SequenceEqual(routines.Select(routine => routine.Offset)) is false)
            throw new FormatException($"Type-16 LUN program {index} routine descriptors [{string.Join(",", descriptors.Select(descriptor => descriptor.EntryWordOffset * 4))}] do not match bytecode entries [{string.Join(",", routines.Select(routine => routine.Offset))}]",
                (source.LogicalOffset ?? 0) + offset + programLength, descriptorSize + descriptorBytes, data.Length - programLength);
        return new(index, offset, programLength, declaredSize, program.ToArray(), bytecodeLength, routines, instructions,
            descriptors[0], descriptors.Skip(1).ToArray(), data.Length - declaredSize);
    }

    private static LunRoutineDescriptor DecodeLunRoutineDescriptor(ReadOnlySpan<byte> data, int descriptorOffset,
        int headerSize, int bytecodeLength, int index)
    {
        var relativeProgramOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
        var programOffset = checked(descriptorOffset + 16 + relativeProgramOffset - headerSize);
        if (programOffset < 0 || programOffset >= bytecodeLength || (programOffset & 3) != 0)
            throw new InvalidDataException("Type-16 LUN routine descriptor has an invalid program offset");
        return new(index, descriptorOffset, relativeProgramOffset, programOffset,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
    }

    private static RailProgramRecord DecodeRailProgramRecord(ReadOnlySpan<byte> data, int offset, int index,
        SourceByteRange source, int sectionOffset, int trackId, int? generatedRailId)
    {
        const int headerSize = 16;
        const int descriptorSize = 12;
        if (data.Length < headerSize || (data.Length - headerSize) % descriptorSize != 0)
            throw new FormatException("Type-16 rail-program record has invalid framing",
                (source.LogicalOffset ?? 0) + sectionOffset + offset, data.Length, data.Length);
        var kind = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        var controlWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        static PackedRailReference? Reference(uint packed) => packed == uint.MaxValue ? null : DecodeRailReference(packed);
        var primaryReference = Reference(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        var secondaryReference = Reference(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        var descriptors = new RailProgramDescriptor[(data.Length - headerSize) / descriptorSize];
        for (var descriptorIndex = 0; descriptorIndex < descriptors.Length; descriptorIndex++)
        {
            var descriptorOffset = headerSize + descriptorIndex * descriptorSize;
            var word0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[descriptorOffset..]);
            var word1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(descriptorOffset + 4)..]);
            var word2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(descriptorOffset + 8)..]);
            var scalar0 = BitConverter.Int32BitsToSingle(unchecked((int)word0));
            var scalar1 = BitConverter.Int32BitsToSingle(unchecked((int)word1));
            if (!float.IsFinite(scalar0) || !float.IsFinite(scalar1))
                throw new FormatException("Type-16 rail-program descriptor contains a non-finite scalar",
                    (source.LogicalOffset ?? 0) + sectionOffset + offset + descriptorOffset, descriptorSize, data.Length - descriptorOffset);
            var low = (ushort)word2; var high = (ushort)(word2 >> 16);
            descriptors[descriptorIndex] = new(word0, word1, word2, scalar0, scalar1, low, high,
                (RailSplineRole)low, high == ushort.MaxValue ? null : (SsxSurfaceType)high);
        }
        var generatedReference = generatedRailId is null ? null : new PackedRailReference(
            checked((uint)(generatedRailId.Value << 8 | trackId)), trackId, generatedRailId.Value);
        return new(index, offset, data.Length, kind, controlWord, (ushort)controlWord, (ushort)(controlWord >> 16),
            generatedRailId, generatedReference,
            primaryReference, secondaryReference, descriptors);
    }

    public static int LunInstructionSize(byte opcode) => opcode is 0x14 or 0x15 or 0x16 or 0x17 or 0x1d
        or 0x24 or 0x25 or 0x26 or 0x27 ? 8 : 4;

    public static LunOperation LunInstructionOperation(byte opcode) => opcode switch
    {
        0x00 => LunOperation.Jump,
        0x01 => LunOperation.JumpIfTruthy,
        0x02 => LunOperation.JumpIfFalsy,
        0x03 => LunOperation.Equal,
        0x04 => LunOperation.NotEqual,
        0x05 => LunOperation.GreaterThanOrEqual,
        0x06 => LunOperation.GreaterThan,
        0x07 => LunOperation.LessThanOrEqual,
        0x08 => LunOperation.LessThan,
        0x09 => LunOperation.LogicalOr,
        0x0a => LunOperation.LogicalAnd,
        0x0c => LunOperation.Add,
        0x0d => LunOperation.Subtract,
        0x0e => LunOperation.Multiply,
        0x0f => LunOperation.Divide,
        0x10 => LunOperation.UnsupportedBinaryOperation,
        0x11 => LunOperation.Remainder,
        0x12 => LunOperation.MapSet,
        0x0b or 0x13 => LunOperation.CopySlot,
        0x14 => LunOperation.StoreType3Immediate,
        0x15 or 0x16 => LunOperation.StoreIntegerImmediate,
        0x17 => LunOperation.StoreFloatImmediate,
        0x18 => LunOperation.MapGet,
        0x19 => LunOperation.CreateMap,
        0x1a => LunOperation.NoOp,
        0x1b => LunOperation.CallRoutine,
        0x1c => LunOperation.MapSetAndIncrementKey,
        0x1d => LunOperation.StoreReferencePairImmediate,
        0x1e => LunOperation.ClearSlot,
        0x1f => LunOperation.ReturnSlot,
        0x20 => LunOperation.AppendSlotArgument,
        0x21 => LunOperation.CallNative,
        0x22 => LunOperation.LogicalNot,
        0x23 => LunOperation.Negate,
        0x24 => LunOperation.ForLoopAdvance,
        0x25 or 0x27 => LunOperation.AppendIntegerImmediateArgument,
        0x26 => LunOperation.AppendFloatImmediateArgument,
        0x28 => LunOperation.AppendU8IntegerArgument,
        0x29 => LunOperation.AppendU8FloatArgument,
        0x2a => LunOperation.End,
        _ => LunOperation.Unknown
    };

    public static string? LunNativeFunctionName(byte id) => id switch
    {
        0 => "Object",
        1 => "Debounce",
        3 => "AnimObject",
        4 => "AnimDelta",
        5 => "AnimCombo",
        6 => "AnimTeeter",
        7 => "Boost",
        8 => "Conveyor",
        9 => "Counter",
        11 => "FlexBridge",
        12 => "Flag",
        13 => "MeshAnim",
        14 => "Floating",
        16 => "MakeParticleData/ParticleNode",
        25 => "AddParticleData",
        26 => "AddDynamicParticleData",
        29 => "Hide",
        49 => "FloatRail",
        70 => "SpringRail",
        71 => "DeadFade",
        _ => null
    };

    public static string? LunNativeFunctionSubsystem(byte id) => id switch
    {
        2 => "DeadNode/RestoreNode",
        15 => "RollerModifier",
        17 or 18 => "ParentModifier",
        19 => "SplineModifier",
        20 => "MultiSplineModifier",
        21 => "UVScrollModifier",
        22 => "TexFlipModifier",
        23 => "PositionModifier",
        24 => "BoostModifier",
        25 => "ParticleModifier",
        26 => "DynamicParticleModifier",
        35 => "RailMan",
        48 => "RailModifier",
        68 => "BELibrary",
        90 => "MagnetModifier",
        93 => "Avalanche/AvalancheNode",
        95 => "AvaSplineModifier",
        97 => "HaloModifier",
        105 => "MultiParticle",
        _ => null
    };

    private static WorldModifierSection DecodeModifierSection(ReadOnlySpan<byte> section, SourceByteRange source,
        int absoluteOffset, int slot, int trackId)
    {
        if (section.Length < 12)
            throw new FormatException("Type-15 World Painter section is truncated", (source.LogicalOffset ?? 0) + absoluteOffset, 12, section.Length);
        var headerSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section));
        var recordCount = Count15(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section[4..]));
        var indexRecordSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section[8..]));
        var expectedHeaderSize = checked(12 + recordCount * 8);
        if (headerSize != expectedHeaderSize || headerSize > section.Length || indexRecordSize != 12)
            throw new FormatException("Type-15 World Painter section header is inconsistent",
                (source.LogicalOffset ?? 0) + absoluteOffset, Math.Min(section.Length, 12), section.Length);
        var relativeOffsets = new int[recordCount];
        for (var i = 0; i < recordCount; i++)
        {
            var entryOffset = 12 + i * 8;
            var typeId = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section[entryOffset..]));
            var relativeOffset = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section[(entryOffset + 4)..]));
            if (typeId != slot + 1 || relativeOffset < headerSize || relativeOffset >= section.Length || (relativeOffset & 3) != 0)
                throw new FormatException("Type-15 World Painter descriptor is invalid",
                    (source.LogicalOffset ?? 0) + absoluteOffset + entryOffset, 8, section.Length - entryOffset);
            relativeOffsets[i] = relativeOffset;
        }
        if (relativeOffsets.Zip(relativeOffsets.Skip(1)).Any(x => x.First >= x.Second))
            throw new FormatException("Type-15 World Painter payload offsets are not strictly ordered",
                (source.LogicalOffset ?? 0) + absoluteOffset + 12, recordCount * 8, section.Length - 12);
        var firstRecordOffset = relativeOffsets.Length == 0 ? section.Length : relativeOffsets[0];
        var records = new WorldModifierRecord[recordCount];
        for (var i = 0; i < records.Length; i++)
        {
            var end = i + 1 < records.Length ? relativeOffsets[i + 1] : section.Length;
            records[i] = DecodeModifierRecord(slot + 1, i, absoluteOffset + relativeOffsets[i],
                section[relativeOffsets[i]..end], trackId);
        }
        var indexData = section[headerSize..firstRecordOffset];
        var spatialIndex = DecodeModifierSpatialIndex(indexData, source, absoluteOffset + headerSize, recordCount);
        return new(slot, slot + 1, WorldPainterTypeName(slot + 1), absoluteOffset, headerSize, recordCount,
            indexRecordSize, spatialIndex, indexData.ToArray(), records);
    }

    private static int Count15(uint value)
    {
        if (value > MaximumCount) throw new InvalidDataException($"Type-15 World Painter count {value} exceeds the safety limit");
        return checked((int)value);
    }

    private static WorldModifierRecord DecodeModifierRecord(int typeId, int index, int offset, ReadOnlySpan<byte> data, int trackId)
    {
        var expectedSize = WorldPainterRecordSize(typeId);
        if (expectedSize is not null && data.Length != expectedSize)
            throw new InvalidDataException($"Type-15 {WorldPainterTypeName(typeId)} record has size {data.Length}, expected {expectedSize}");
        if ((data.Length & 3) != 0)
            throw new InvalidDataException($"Type-15 {WorldPainterTypeName(typeId)} record is not word-aligned");
        var words = new uint[data.Length / 4];
        for (var i = 0; i < words.Length; i++)
            words[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        float FloatAt(int word) => BitConverter.Int32BitsToSingle(unchecked((int)words[word]));
        float[] scalars;
        string[] tags;
        if (typeId == 11)
        {
            scalars = [FloatAt(0), FloatAt(9), FloatAt(10)];
            tags = new string[4];
            for (var i = 0; i < tags.Length; i++)
                tags[i] = Encoding.ASCII.GetString(data.Slice(4 + i * 8, 8)).TrimEnd('\0');
        }
        else
        {
            scalars = Enumerable.Range(0, words.Length).Select(FloatAt).ToArray();
            tags = [];
        }
        var referencedResourceType = typeId switch { 1 => 8, 2 => 3, _ => (int?)null };
        int? referencedResourceId = referencedResourceType is null ? null : checked((int)words[1]);
        return new(typeId, index, offset, words, scalars, tags, data.ToArray(), referencedResourceType,
            referencedResourceType is null ? null : trackId, referencedResourceId);
    }

    private static WorldModifierSpatialIndex DecodeModifierSpatialIndex(ReadOnlySpan<byte> data, SourceByteRange source, int offset, int recordCount)
    {
        const int headerSize = 40;
        const int entrySize = 8;
        if (data.Length < headerSize)
            throw new FormatException("Type-15 World Painter spatial index is truncated", (source.LogicalOffset ?? 0) + offset,
                headerSize, data.Length);
        var scale = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data));
        var origin = new System.Numerics.Vector2(
            BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[4..])),
            BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[8..])));
        var entryCount = Count15(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        var serializedCapacity = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));
        var rootHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
        var reserved = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        var defaultLeafWord0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var defaultLeafWord1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        var serializedNodePointerPlaceholder = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var serializedNodeEndPointerPlaceholder = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        var rootEntryIndex = rootHandle >> 1;
        var expectedSize = checked(headerSize + entryCount * entrySize);
        if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
            || data.Length != expectedSize || serializedCapacity != 0 || reserved != 0
            || defaultLeafWord0 != 0 || defaultLeafWord1 != uint.MaxValue || serializedNodeEndPointerPlaceholder != 0
            || rootEntryIndex >= entryCount)
            throw new FormatException("Type-15 World Painter spatial-index size or coordinates are invalid",
                (source.LogicalOffset ?? 0) + offset, headerSize, data.Length);
        var entries = new WorldModifierIndexEntry[entryCount];
        for (var i = 0; i < entries.Length; i++) entries[i] = ReadModifierIndexEntry(data, i, recordCount, entryCount);
        ValidateModifierSpatialTree(entries, rootEntryIndex);
        return new(scale, origin, entryCount, serializedCapacity, rootHandle, reserved, defaultLeafWord0,
            defaultLeafWord1, serializedNodePointerPlaceholder, serializedNodeEndPointerPlaceholder,
            rootEntryIndex, entries);
    }

    private static WorldModifierIndexEntry ReadModifierIndexEntry(ReadOnlySpan<byte> data, int index,
        int recordCount, int treeEntryCount)
    {
        const int headerSize = 40;
        const int entrySize = 8;
        var entryOffset = headerSize + index * entrySize;
        var word0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[entryOffset..]);
        var word1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(entryOffset + 4)..]);
        var low0 = (ushort)word0; var high0 = (ushort)(word0 >> 16);
        var low1 = (ushort)word1; var high1 = (ushort)(word1 >> 16);
        WorldModifierIndexEntryKind kind; int? modifierRecordIndex = null;
        IReadOnlyList<WorldModifierIndexChild> children = Array.Empty<WorldModifierIndexChild>();
        if ((low0 & 1) != 0)
        {
            children =
            [
                new(WorldModifierSpatialQuadrant.LowXLowY, low0, low0 >> 1),
                new(WorldModifierSpatialQuadrant.LowXHighY, high0, high0 >> 1),
                new(WorldModifierSpatialQuadrant.HighXLowY, low1, low1 >> 1),
                new(WorldModifierSpatialQuadrant.HighXHighY, high1, high1 >> 1)
            ];
            if (children.Any(child => child.EntryIndex >= treeEntryCount))
                throw new InvalidDataException("Type-15 World Painter spatial-index branch child is out of bounds");
            kind = WorldModifierIndexEntryKind.Branch;
        }
        else if (word1 == uint.MaxValue) kind = WorldModifierIndexEntryKind.EmptyLeaf;
        else if (high1 == 0 && low1 < recordCount)
        {
            kind = WorldModifierIndexEntryKind.RecordLeaf; modifierRecordIndex = low1;
        }
        else throw new InvalidDataException("Type-15 World Painter spatial-index leaf has an invalid record index");
        return new(index, index, kind, word0, word1, low0, high0, low1, high1,
            modifierRecordIndex, children);
    }

    private static void ValidateModifierSpatialTree(IReadOnlyList<WorldModifierIndexEntry> entries, int rootEntryIndex)
    {
        if (entries.Count == 0 || entries.Count != checked(entries.Count(entry => entry.Kind == WorldModifierIndexEntryKind.Branch) * 4 + 1))
            throw new InvalidDataException("Type-15 World Painter spatial index is not a full four-child tree");
        var parentCounts = new int[entries.Count];
        foreach (var child in entries.SelectMany(entry => entry.ChildEntryIndices)) parentCounts[child]++;
        if (parentCounts[rootEntryIndex] != 0 || parentCounts.Where((_, index) => index != rootEntryIndex).Any(count => count != 1))
            throw new InvalidDataException("Type-15 World Painter spatial-index child topology is invalid");
        var reached = new HashSet<int>();
        var pending = new Stack<int>(); pending.Push(rootEntryIndex);
        while (pending.TryPop(out var index))
        {
            if (!reached.Add(index)) continue;
            foreach (var child in entries[index].ChildEntryIndices) pending.Push(child);
        }
        if (reached.Count != entries.Count)
            throw new InvalidDataException("Type-15 World Painter spatial-index tree is disconnected");
    }

    public static int? WorldPainterRecordSize(int typeId) => typeId switch
    {
        1 or 2 or 3 or 4 or 8 or 10 => 8,
        5 or 7 => 28,
        6 => 32,
        9 => 40,
        11 => 44,
        12 => 80,
        13 => 12,
        _ => null
    };

    public static int? WorldPainterRuntimeObjectSize(int typeId)
        => typeId == 0 ? 24 : WorldPainterRecordSize(typeId) is { } size ? checked(size * 2) : null;

    public static string WorldPainterRuntimeClassName(int typeId)
        => $"tWPIGD_{WorldPainterTypeName(typeId)}";

    public static string WorldPainterTypeName(int typeId) => typeId switch
    {
        0 => "MusicTrigger", 1 => "Mix", 2 => "Ambience", 3 => "Speech", 4 => "Camera",
        5 => "Fog", 6 => "LightGlow", 7 => "ScreenTint", 8 => "SkyBox", 9 => "Sun",
        10 => "Surface", 11 => "Lighting", 12 => "Weather", 13 => "Danger",
        _ => $"UnknownWorldPainter_{typeId}"
    };

    public static string WorldPainterPropertyName(int typeId, int propertyIndex) => (typeId, propertyIndex) switch
    {
        (5, 0) => "Density", (5, 1) => "NearPlane", (5, 2) => "FarPlane",
        (5, 3) => "ColourRed", (5, 4) => "ColourGreen", (5, 5) => "ColourBlue",
        (6, 0) => "PS2MinimumIntensityCutoff", (6, 1) => "PS2PostCutoffScale",
        (6, 2) => "PS2CopyIntensity", (6, 3) => "PS2FrameSourceIntensity",
        (6, 4) => "PS2FrameBlendIntensity", (6, 5) => "PS2BlendTexture2",
        (6, 6) => "PS2BlendTexture3",
        (7, 0) => "TintRed", (7, 1) => "TintGreen", (7, 2) => "TintBlue",
        (7, 3) => "FillRed", (7, 4) => "FillGreen", (7, 5) => "FillBlue",
        (9, 2) => "ColourRed", (9, 3) => "ColourGreen", (9, 4) => "ColourBlue",
        (12, 0) => "SnowfallIntensity", (12, 1) => "SnowflakeSize", (12, 2) => "SnowfallWind",
        (12, 3) => "WindRotation", (12, 6) => "SnowFlurries", (12, 7) => "SnowGravityMultiplier",
        (12, 8) => "SnowFluffIntensity", (12, 9) => "SnowFluffWindIntensity",
        (12, 10) => "LightningChance", (12, 11) => "SnowFlakeColourAlpha",
        (12, 12) => "SnowFlakeColourR", (12, 13) => "SnowFlakeColourG", (12, 14) => "SnowFlakeColourB",
        (12, 15) => "SnowFluffColourAlpha",
        (12, 16) => "SnowFluffColourR", (12, 17) => "SnowFluffColourG", (12, 18) => "SnowFluffColourB",
        _ => $"Property{propertyIndex}"
    };

    public static int? ModifierRecordSize(int typeId) => WorldPainterRecordSize(typeId);
    public static string ModifierTypeName(int typeId) => WorldPainterTypeName(typeId);

    private static void ValidateFixed(int offset, int count, int stride, int length, string name)
    {
        if (offset < Type16HeaderSize || offset > length || checked(offset + count * stride) > length)
            throw new InvalidDataException($"Type-16 {name} section is out of bounds");
    }

    private static StructuredTableAsset Marker(int type, int size, SourceByteRange source, int trackId, int resourceId)
    {
        var properties = new Dictionary<string, object?> { ["ParsedType"] = $"SSX3 Type-{type} Table Marker", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["Marker"] = true, ["PayloadSize"] = size };
        return new($"Type-{type} Table Marker {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Low },
            type, trackId, resourceId, [], properties);
    }
}
