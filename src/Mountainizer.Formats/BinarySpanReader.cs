using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public ref struct BinarySpanReader(ReadOnlySpan<byte> data, long baseOffset = 0)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Position { get; private set; }
    public int Remaining => _data.Length - Position;
    public long AbsolutePosition => baseOffset + Position;

    public void Ensure(int count)
    {
        if (count < 0 || count > Remaining)
            throw new FormatException($"Read of {count} bytes exceeds the available span", AbsolutePosition, count, Remaining);
    }
    public byte ReadByte() { Ensure(1); return _data[Position++]; }
    public ushort ReadUInt16Little() { Ensure(2); var x = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]); Position += 2; return x; }
    public short ReadInt16Little() => unchecked((short)ReadUInt16Little());
    public uint ReadUInt24Little() { Ensure(3); uint x = (uint)(_data[Position] | _data[Position + 1] << 8 | _data[Position + 2] << 16); Position += 3; return x; }
    public uint ReadUInt32Little() { Ensure(4); var x = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]); Position += 4; return x; }
    public uint ReadUInt32Big() { Ensure(4); var x = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]); Position += 4; return x; }
    public float ReadSingleLittle() => BitConverter.UInt32BitsToSingle(ReadUInt32Little());
    public Vector2 ReadVector2() => new(ReadSingleLittle(), ReadSingleLittle());
    public Vector3 ReadVector3() => new(ReadSingleLittle(), ReadSingleLittle(), ReadSingleLittle());
    public Vector4 ReadVector4() => new(ReadSingleLittle(), ReadSingleLittle(), ReadSingleLittle(), ReadSingleLittle());
    public ReadOnlySpan<byte> ReadBytes(int count) { Ensure(count); var x = _data.Slice(Position, count); Position += count; return x; }
    public void Skip(int count) { Ensure(count); Position += count; }
    public void Seek(int position)
    {
        if ((uint)position > (uint)_data.Length) throw new FormatException("Seek is outside the span", baseOffset + position, 0, _data.Length);
        Position = position;
    }
    public string ReadFixedString(int count)
    {
        var bytes = ReadBytes(count);
        var nul = bytes.IndexOf((byte)0);
        if (nul >= 0) bytes = bytes[..nul];
        return Encoding.ASCII.GetString(bytes);
    }
}
