using System.Buffers.Binary;
using Mountainizer.Core;

namespace Mountainizer.Formats;

/// <summary>
/// Decodes the instance-local PS2 DMA/VIF relocation program appended to Type-3 world instances.
/// The runtime walker is at SLUS_207.72 0x3AC508; its caller relocates the extension pointer at 0x3AC38C.
/// </summary>
public static class Ssx3InstanceDmaDecoder
{
    private const int HeaderSize = 160;
    private const int QuadwordSize = 16;
    private const uint MscalPlaceholder = 0xdeadbeef;

    public static InstanceDmaProgramSet Decode(ReadOnlySpan<byte> data, SourceByteRange source)
    {
        if (data.Length < HeaderSize + QuadwordSize)
            throw new FormatException("Type-3 instance DMA extension is truncated", source.LogicalOffset ?? 0,
                HeaderSize + QuadwordSize, data.Length);
        var extensionOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x98..]));
        if (extensionOffset < HeaderSize || extensionOffset > data.Length - QuadwordSize || (extensionOffset & 15) != 0)
            throw new FormatException($"Type-3 DMA extension offset 0x{extensionOffset:X} is invalid",
                (source.LogicalOffset ?? 0) + 0x98, QuadwordSize, data.Length - extensionOffset);
        var payloadBytes = data.ToArray();

        var programs = new List<InstanceDmaProgram>();
        var sourceBlocks = new List<InstanceDmaSourceBlock>();
        var position = extensionOffset;
        var firstSourceOffset = data.Length;
        while (position < firstSourceOffset)
        {
            var relocations = new List<InstanceDmaRelocation>();
            var usesScratchpadRewrite = false;
            var usesImmediateReturnRewrite = false;
            byte[] workspace = [];
            Ps2DmaTag? returnTag = null;

            while (returnTag is null)
            {
                var tag = ReadTag(data, position, source);
                if (tag.Id == Ps2DmaTagId.Ret)
                {
                    returnTag = tag;
                    position += QuadwordSize;
                    continue;
                }
                RequireRef(tag, source, "model-data relocation");
                AddModel(tag, false);

                var instanceTag = ReadTag(data, position + QuadwordSize, source);
                RequireRef(instanceTag, source, "instance-extension relocation");
                AddSource(instanceTag);
                position += QuadwordSize * 2;

                if (!tag.Scratchpad) continue;
                usesScratchpadRewrite = true;
                var lookahead = ReadTag(data, position + QuadwordSize, source);
                if (lookahead.Id == Ps2DmaTagId.Ret)
                {
                    var extraModelTag = ReadTag(data, position, source);
                    RequireRef(extraModelTag, source, "scratchpad rewrite model relocation");
                    AddModel(extraModelTag, true);
                    returnTag = lookahead;
                    workspace = ReadQuadword(data, position + QuadwordSize * 2, source);
                    if (workspace.Any(value => value != 0))
                        throw new FormatException("Type-3 immediate-return DMA rewrite workspace is not zero",
                            (source.LogicalOffset ?? 0) + position + QuadwordSize * 2, QuadwordSize, QuadwordSize);
                    usesImmediateReturnRewrite = true;
                    position += QuadwordSize * 3;
                    continue;
                }

                var extraModel0 = ReadTag(data, position, source);
                var extraInstance = ReadTag(data, position + QuadwordSize, source);
                var extraModel1 = ReadTag(data, position + QuadwordSize * 2, source);
                var extraModel2 = ReadTag(data, position + QuadwordSize * 3, source);
                RequireRef(extraModel0, source, "scratchpad rewrite model relocation 0");
                RequireRef(extraInstance, source, "scratchpad rewrite instance relocation");
                RequireRef(extraModel1, source, "scratchpad rewrite model relocation 1");
                RequireRef(extraModel2, source, "scratchpad rewrite model relocation 2");
                AddModel(extraModel0, true);
                AddSource(extraInstance);
                AddModel(extraModel1, true);
                AddModel(extraModel2, true);
                position += QuadwordSize * 4;
            }

            programs.Add(new(relocations, returnTag, usesScratchpadRewrite,
                usesImmediateReturnRewrite, workspace));
            if (position > firstSourceOffset)
                throw new FormatException("Type-3 DMA structure overlaps a referenced source block",
                    (source.LogicalOffset ?? 0) + position, 0, firstSourceOffset - position);

            void AddModel(Ps2DmaTag value, bool patchTerminal) =>
                relocations.Add(new(value, DmaRelocationTarget.ModelData, patchTerminal));

            void AddSource(Ps2DmaTag value)
            {
                var absoluteOffset = checked(extensionOffset + (int)value.Address);
                var byteCount = checked(value.QuadwordCount * QuadwordSize);
                if (value.Scratchpad || value.QuadwordCount <= 0 || absoluteOffset < extensionOffset
                    || absoluteOffset > payloadBytes.Length - byteCount)
                    throw new FormatException("Type-3 DMA source reference is out of bounds",
                        (source.LogicalOffset ?? 0) + value.Offset, byteCount, payloadBytes.Length - absoluteOffset);
                var bytes = payloadBytes.AsSpan(absoluteOffset, byteCount).ToArray();
                var terminalPlaceholder = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(byteCount - 8, 4));
                var terminalVifCode1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(byteCount - 4, 4));
                if (terminalPlaceholder != MscalPlaceholder)
                    throw new FormatException($"Type-3 DMA terminal MSCAL placeholder is 0x{terminalPlaceholder:X8}",
                        (source.LogicalOffset ?? 0) + absoluteOffset + byteCount - 8, 4, 4);
                var vif = Ssx3VifDecoder.Decode(bytes, absoluteOffset);
                sourceBlocks.Add(new(absoluteOffset, value.QuadwordCount, bytes, terminalPlaceholder, terminalVifCode1,
                    vif.Commands, vif.Complete));
                firstSourceOffset = Math.Min(firstSourceOffset, absoluteOffset);
                relocations.Add(new(value, DmaRelocationTarget.InstanceExtension, true));
            }
        }

        if (position != firstSourceOffset)
            throw new FormatException("Type-3 DMA structural stream does not end at its first source block",
                (source.LogicalOffset ?? 0) + position, 0, firstSourceOffset - position);

        var coverage = new byte[data.Length - extensionOffset];
        Array.Fill(coverage, (byte)1, 0, position - extensionOffset);
        foreach (var block in sourceBlocks)
        {
            var start = block.Offset - extensionOffset;
            for (var index = start; index < start + block.Data.Length; index++)
            {
                if (coverage[index] != 0)
                    throw new FormatException("Type-3 DMA source blocks overlap",
                        (source.LogicalOffset ?? 0) + extensionOffset + index, 1, 1);
                coverage[index] = 1;
            }
        }
        var uncovered = Array.IndexOf(coverage, (byte)0);
        if (uncovered >= 0)
            throw new FormatException("Type-3 DMA extension contains unreferenced bytes",
                (source.LogicalOffset ?? 0) + extensionOffset + uncovered, 1, coverage.Length - uncovered);

        return new(extensionOffset, programs, sourceBlocks, position - extensionOffset,
            sourceBlocks.Sum(block => block.Data.Length));
    }

    private static Ps2DmaTag ReadTag(ReadOnlySpan<byte> data, int offset, SourceByteRange source)
    {
        if (offset < 0 || offset > data.Length - QuadwordSize || (offset & 15) != 0)
            throw new FormatException($"Type-3 DMA tag offset 0x{offset:X} is invalid",
                (source.LogicalOffset ?? 0) + offset, QuadwordSize, data.Length - offset);
        var word0 = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        var word1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
        return new(offset, checked((int)(word0 & 0xffff)), (Ps2DmaTagId)((word0 >> 28) & 7),
            (word0 & 0x80000000) != 0, word1 & 0x7fffffff, (word1 & 0x80000000) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]));
    }

    private static byte[] ReadQuadword(ReadOnlySpan<byte> data, int offset, SourceByteRange source)
    {
        if (offset < 0 || offset > data.Length - QuadwordSize)
            throw new FormatException("Type-3 DMA rewrite workspace is out of bounds",
                (source.LogicalOffset ?? 0) + offset, QuadwordSize, data.Length - offset);
        return data.Slice(offset, QuadwordSize).ToArray();
    }

    private static void RequireRef(Ps2DmaTag tag, SourceByteRange source, string role)
    {
        if (tag.Id != Ps2DmaTagId.Ref)
            throw new FormatException($"Type-3 {role} uses DMA tag {tag.Id}, expected REF",
                (source.LogicalOffset ?? 0) + tag.Offset, QuadwordSize, QuadwordSize);
    }
}
