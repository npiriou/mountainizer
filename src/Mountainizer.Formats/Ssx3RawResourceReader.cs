using System.Buffers.Binary;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed record Ssx3RawResource(int GroupIndex, int ResourceIndex, int Type, int TrackId, int ResourceId, byte[] Payload);

public static class Ssx3RawResourceReader
{
    private const int OuterHeaderSize = 8;
    private const int MaximumOuterBlockSize = 16 * 1024 * 1024;
    private const int MaximumGroupSize = 256 * 1024 * 1024;

    public static IReadOnlyList<Ssx3RawResource> Read(string ssbPath, IReadOnlySet<int> groupIndices,
        int type, int? trackId = null, int? resourceId = null)
    {
        if (groupIndices.Count == 0) return [];
        var result = new List<Ssx3RawResource>(); var lastGroup = groupIndices.Max(); var groupIndex = 0;
        using var stream = new FileStream(ssbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var group = new MemoryStream(); Span<byte> header = stackalloc byte[OuterHeaderSize];
        while (stream.Position < stream.Length && groupIndex <= lastGroup)
        {
            if (stream.Length - stream.Position < OuterHeaderSize) throw new FormatException("Truncated SSB outer header", stream.Position, OuterHeaderSize, stream.Length - stream.Position);
            stream.ReadExactly(header); var magic = Encoding.ASCII.GetString(header[..4]).ToUpperInvariant();
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (size < OuterHeaderSize || size > MaximumOuterBlockSize || size > stream.Length - stream.Position + OuterHeaderSize)
                throw new FormatException($"Invalid SSB outer block size {size}", stream.Position - OuterHeaderSize, size, stream.Length - stream.Position + OuterHeaderSize);
            var compressed = new byte[size - OuterHeaderSize]; stream.ReadExactly(compressed);
            var decompressed = RefPackDecoder.Decompress(compressed);
            if (group.Length + decompressed.Length > MaximumGroupSize)
                throw new FormatException("SSB group exceeds safety limit", stream.Position - size, group.Length + decompressed.Length, MaximumGroupSize);
            group.Write(decompressed);
            if (magic != "CEND") continue;
            if (groupIndices.Contains(groupIndex))
                ReadGroup(group.GetBuffer().AsSpan(0, checked((int)group.Length)), groupIndex, type, trackId, resourceId, result);
            group.SetLength(0); groupIndex++;
        }
        return result;
    }

    private static void ReadGroup(ReadOnlySpan<byte> data, int groupIndex, int requestedType, int? requestedTrack,
        int? requestedResource, List<Ssx3RawResource> destination)
    {
        var position = 0; var resourceIndex = 0;
        while (position < data.Length)
        {
            if (data.Length - position < 8) throw new FormatException("Truncated SSB resource header", position, 8, data.Length - position);
            var type = data[position]; var payloadSize = data[position + 1] | data[position + 2] << 8 | data[position + 3] << 16;
            var trackId = data[position + 4]; var resourceId = data[position + 5] | data[position + 6] << 8 | data[position + 7] << 16;
            var payloadOffset = position + 8;
            if (payloadOffset > data.Length - payloadSize)
                throw new FormatException($"SSB resource {resourceIndex} is out of bounds", payloadOffset, payloadSize, data.Length - payloadOffset);
            if (type == requestedType && (requestedTrack is null || trackId == requestedTrack)
                && (requestedResource is null || resourceId == requestedResource))
                destination.Add(new(groupIndex, resourceIndex, type, trackId, resourceId, data.Slice(payloadOffset, payloadSize).ToArray()));
            position = payloadOffset + payloadSize; resourceIndex++;
        }
    }
}
