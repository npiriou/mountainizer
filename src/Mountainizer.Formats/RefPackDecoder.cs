using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class RefPackDecoder
{
    public const int DefaultMaximumOutput = 64 * 1024 * 1024;

    public static byte[] Decompress(ReadOnlySpan<byte> source, int maximumOutput = DefaultMaximumOutput)
    {
        if (source.Length < 6 || source[0] != 0x10 || source[1] != 0xFB)
            throw new FormatException("Unsupported RefPack header (expected 10 FB)", 0, 6, source.Length);
        var expected = source[2] << 16 | source[3] << 8 | source[4];
        if (expected < 0 || expected > maximumOutput) throw new FormatException("RefPack output exceeds the safety limit", 2, expected, maximumOutput);
        var output = new byte[expected];
        var inputPosition = 5;
        var outputPosition = 0;
        var stopped = false;
        while (inputPosition < source.Length)
        {
            var controlOffset = inputPosition;
            var first = source[inputPosition++];
            int literal, length = 0, lookback = 0;
            if (first < 0x80)
            {
                Ensure(source, inputPosition, 1); var second = source[inputPosition++];
                literal = first & 3; length = ((first & 0x1C) >> 2) + 3; lookback = (((first & 0x60) << 3) | second) + 1;
            }
            else if (first < 0xC0)
            {
                Ensure(source, inputPosition, 2); var second = source[inputPosition++]; var third = source[inputPosition++];
                literal = second >> 6; length = (first & 0x3F) + 4; lookback = (((second & 0x3F) << 8) | third) + 1;
            }
            else if (first < 0xE0)
            {
                Ensure(source, inputPosition, 3); var second = source[inputPosition++]; var third = source[inputPosition++]; var fourth = source[inputPosition++];
                literal = first & 3; length = ((first & 0x0C) << 6 | fourth) + 5; lookback = ((first & 0x10) << 12 | second << 8 | third) + 1;
            }
            else if (first < 0xFC) literal = ((first & 0x1F) << 2) + 4;
            else { literal = first & 3; stopped = true; }

            Ensure(source, inputPosition, literal);
            EnsureOutput(output, outputPosition, literal + length, controlOffset);
            source.Slice(inputPosition, literal).CopyTo(output.AsSpan(outputPosition));
            inputPosition += literal; outputPosition += literal;
            if (stopped) break;
            if (length == 0) continue;
            if (lookback <= 0 || lookback > outputPosition) throw new FormatException("RefPack lookback precedes output", controlOffset, lookback, outputPosition);
            for (var i = 0; i < length; i++)
            {
                output[outputPosition] = output[outputPosition - lookback];
                outputPosition++;
            }
        }
        if (!stopped) throw new FormatException("RefPack stream has no stop command", inputPosition);
        if (outputPosition != expected) throw new FormatException($"RefPack produced {outputPosition} bytes; header declares {expected}", inputPosition, expected, outputPosition);
        return output;
    }

    private static void Ensure(ReadOnlySpan<byte> source, int position, int count)
    {
        if (count < 0 || position > source.Length - count) throw new FormatException("RefPack input is truncated", position, count, source.Length - position);
    }
    private static void EnsureOutput(byte[] output, int position, int count, int sourceOffset)
    {
        if (count < 0 || position > output.Length - count) throw new FormatException("RefPack command exceeds declared output size", sourceOffset, count, output.Length - position);
    }
}
