using System.Buffers.Binary;
using Mountainizer.Core;

namespace Mountainizer.Formats;

/// <summary>Frames the PS2 VIF command stream embedded in a Type-3 DMA source block.</summary>
public static class Ssx3VifDecoder
{
    public static (IReadOnlyList<Ps2VifCommand> Commands, bool Complete) Decode(ReadOnlySpan<byte> data,
        int sourceOffset = 0)
    {
        var commands = new List<Ps2VifCommand>();
        var position = 0;
        while (position <= data.Length - 4)
        {
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            var command = (byte)(raw >> 24 & 0x7f);
            var interrupt = (raw & 0x80000000) != 0;
            var encodedCount = (byte)(raw >> 16 & 0xff);
            var elementCount = encodedCount == 0 ? 256 : encodedCount;
            var immediate = (ushort)raw;
            var payloadSize = PayloadSize(raw, command, elementCount);
            var payloadOffset = position + 4;
            if (payloadSize < 0 || payloadOffset > data.Length - payloadSize)
                return (commands, false);
            commands.Add(new(sourceOffset + position, raw, command, interrupt, elementCount, immediate,
                Name(raw, command), sourceOffset + payloadOffset, payloadSize));
            position = (payloadOffset + payloadSize + 3) & ~3;
        }
        return (commands, position == data.Length);
    }

    private static int PayloadSize(uint raw, byte command, int elementCount)
    {
        if (raw == 0xdeadbeef) return 0; // loader replaces this word with MSCAL
        return command switch
        {
            0x20 => 4,                    // STMASK
            0x30 or 0x31 => 16,           // STROW / STCOL
            0x4a => checked(elementCount * 8), // MPG: two 32-bit words per microinstruction
            0x50 or 0x51 => checked((int)(raw & 0xffff) * 16), // DIRECT / DIRECTHL QWC
            >= 0x60 and <= 0x7f => UnpackPayloadSize(command, elementCount),
            _ => 0
        };
    }

    private static int UnpackPayloadSize(byte command, int elementCount)
    {
        var vectorSize = (command >> 2 & 3) + 1;
        var elementFormat = command & 3;
        if (elementFormat == 3) return checked(elementCount * 2); // packed V4-5
        var scalarBytes = 4 >> elementFormat; // 32-, 16-, or 8-bit components
        return checked(elementCount * vectorSize * scalarBytes);
    }

    private static string Name(uint raw, byte command)
    {
        if (raw == 0xdeadbeef) return "RuntimeMscalPlaceholder";
        if (command is >= 0x60 and <= 0x7f)
        {
            var vectorSize = (command >> 2 & 3) + 1;
            var elementFormat = command & 3;
            var suffix = elementFormat switch { 0 => "32", 1 => "16", 2 => "8", _ => "5" };
            return $"UNPACK_V{vectorSize}_{suffix}";
        }
        return command switch
        {
            0x00 => "NOP", 0x01 => "STCYCL", 0x02 => "OFFSET", 0x03 => "BASE", 0x04 => "ITOP",
            0x05 => "STMOD", 0x06 => "MSKPATH3", 0x07 => "MARK", 0x10 => "FLUSHE", 0x11 => "FLUSH",
            0x13 => "FLUSHA", 0x14 => "MSCAL", 0x15 => "MSCALF", 0x17 => "MSCNT", 0x20 => "STMASK",
            0x30 => "STROW", 0x31 => "STCOL", 0x4a => "MPG", 0x50 => "DIRECT", 0x51 => "DIRECTHL",
            _ => $"VIF_0x{command:X2}"
        };
    }
}
