using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3TextureDecoder
{
    private const int HeaderSize = 0x80;

    public static TextureAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < HeaderSize) throw new Mountainizer.Core.FormatException("SSH texture header is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var format = data[0];
        var paletteOffset = ReadUInt24(data[1..]);
        var width = data[4] | data[5] << 8;
        var height = data[6] | data[7] << 8;
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096)
            throw new Mountainizer.Core.FormatException("SSH texture dimensions are invalid", source.LogicalOffset ?? 0, 1, width * (long)height);
        if (format == 5)
        {
            var byteCount = checked(width * height * 4);
            if (HeaderSize + byteCount > data.Length) throw new Mountainizer.Core.FormatException("SSH RGBA image is truncated", source.LogicalOffset ?? 0, HeaderSize + byteCount, data.Length);
            var rawRgba = data.Slice(HeaderSize, byteCount).ToArray();
            for (var i = 3; i < rawRgba.Length; i += 4) rawRgba[i] = (byte)Math.Min(255, rawRgba[i] * 2);
            return new($"Texture RID {resourceId}", source, width, height, trackId, resourceId, rawRgba,
                new Dictionary<string, object?> { ["ParsedType"] = "SSX3 SSH RGBA Texture", ["Format"] = format,
                    ["PaletteColors"] = 0, ["PaletteOffset"] = 0, ["PayloadSize"] = data.Length });
        }
        if (paletteOffset < HeaderSize || paletteOffset > data.Length - HeaderSize)
            throw new Mountainizer.Core.FormatException("SSH palette offset is invalid", source.LogicalOffset ?? 0, HeaderSize, paletteOffset);

        var paletteHeader = data[paletteOffset..];
        var colorCount = paletteHeader[8] | paletteHeader[9] << 8;
        if (colorCount <= 0 || colorCount > 256 || paletteOffset + HeaderSize + colorCount * 4 > data.Length)
            throw new Mountainizer.Core.FormatException("SSH palette is invalid", (source.LogicalOffset ?? 0) + paletteOffset, 1, colorCount);
        var paletteBytes = data.Slice(paletteOffset + HeaderSize, colorCount * 4);
        var palette = DecodePalette(paletteBytes, colorCount, format == 2);

        var packedLength = format switch { 1 => (width * height + 1) / 2, 2 => width * height, _ => -1 };
        if (packedLength < 0) throw new NotSupportedException($"SSH pixel format {format} is not supported");
        if (HeaderSize + packedLength > paletteOffset)
            throw new Mountainizer.Core.FormatException("SSH base image overlaps its palette", source.LogicalOffset ?? 0, HeaderSize + packedLength, paletteOffset);
        var packed = data.Slice(HeaderSize, packedLength);
        var indices = format == 1 ? Unswizzle4(packed, width, height) : Unswizzle8(packed, width, height);
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            var color = index < palette.Length ? palette[index] : 0u;
            rgba[i * 4] = (byte)color;
            rgba[i * 4 + 1] = (byte)(color >> 8);
            rgba[i * 4 + 2] = (byte)(color >> 16);
            rgba[i * 4 + 3] = (byte)(color >> 24);
        }
        return new($"Texture RID {resourceId}", source, width, height, trackId, resourceId, rgba,
            new Dictionary<string, object?> { ["ParsedType"] = "SSX3 SSH Texture", ["Format"] = format,
                ["PaletteColors"] = colorCount, ["PaletteOffset"] = paletteOffset, ["PayloadSize"] = data.Length });
    }

    private static uint[] DecodePalette(ReadOnlySpan<byte> bytes, int count, bool unswizzle)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var source = unswizzle ? (i & 0xE7) | ((i & 8) << 1) | ((i & 16) >> 1) : i;
            if (source >= count) source = i;
            var p = bytes.Slice(source * 4, 4);
            var alpha = Math.Min(255, p[3] * 2);
            result[i] = p[0] | (uint)p[1] << 8 | (uint)p[2] << 16 | (uint)alpha << 24;
        }
        return result;
    }

    private static byte[] Unswizzle8(ReadOnlySpan<byte> packed, int width, int height)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var block = (y & ~15) * width + (x & ~15) * 2;
            var swap = (((y + 2) >> 2) & 1) * 4;
            var posY = (((y & ~3) >> 1) + (y & 1)) & 7;
            var column = posY * width * 2 + ((x + swap) & 7) * 4;
            var byteNumber = ((y >> 1) & 1) + ((x >> 2) & 2);
            var source = block + column + byteNumber;
            if ((uint)source < (uint)packed.Length) result[y * width + x] = packed[source];
        }
        return result;
    }

    private static byte[] Unswizzle4(ReadOnlySpan<byte> packed, int width, int height)
    {
        var result = new byte[width * height];
        var pagesHorizontal = (width + 127) / 128;
        var pagesVertical = (height + 127) / 128;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var pageX = x & ~127; var pageY = y & ~127;
            var page = (pageY / 128) * pagesHorizontal + pageX / 128;
            var page32Y = page / pagesVertical * 32; var page32X = page % pagesVertical * 64;
            var pageLocation = page32Y * height * 2 + page32X * 4;
            var locX = x & 127; var locY = y & 127;
            var blockLocation = ((locX & ~31) >> 1) * height + (locY & ~15) * 2;
            var swap = (((y + 2) >> 2) & 1) * 4;
            var posY = (((y & ~3) >> 1) + (y & 1)) & 7;
            var columnLocation = posY * height * 2 + ((x + swap) & 7) * 4;
            var source = pageLocation + blockLocation + columnLocation + ((x >> 3) & 3);
            if ((uint)source >= (uint)packed.Length) continue;
            result[y * width + x] = (byte)(((y >> 1) & 1) == 0 ? packed[source] & 15 : packed[source] >> 4);
        }
        return result;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | data[1] << 8 | data[2] << 16;
}
