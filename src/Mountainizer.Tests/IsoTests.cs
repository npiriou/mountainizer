using System.Buffers.Binary;
using System.Text;
using Mountainizer.Iso;

namespace Mountainizer.Tests;

[TestClass]
public sealed class IsoTests
{
    [TestMethod]
    public void Iso9660_IndexesTargetExecutableFromSyntheticFixture()
    {
        var bytes = new byte[24 * Iso9660Image.SectorSize];
        var pvd = bytes.AsSpan(16 * Iso9660Image.SectorSize, Iso9660Image.SectorSize);
        pvd[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(pvd[1..]); pvd[6] = 1;
        Encoding.ASCII.GetBytes("SYNTHETIC").CopyTo(pvd[40..]);
        WriteRecord(pvd[156..], 20, 128, true, [0]);
        var terminator = bytes.AsSpan(17 * Iso9660Image.SectorSize, Iso9660Image.SectorSize); terminator[0] = 255; Encoding.ASCII.GetBytes("CD001").CopyTo(terminator[1..]); terminator[6] = 1;
        var directory = bytes.AsSpan(20 * Iso9660Image.SectorSize, 128); var cursor = 0;
        cursor += WriteRecord(directory[cursor..], 20, 128, true, [0]); cursor += WriteRecord(directory[cursor..], 20, 128, true, [1]);
        cursor += WriteRecord(directory[cursor..], 21, 4, false, Encoding.ASCII.GetBytes("SLUS_207.72;1"));
        bytes[21 * Iso9660Image.SectorSize] = 0x7F;
        var path = Path.GetTempFileName();
        try { File.WriteAllBytes(path, bytes); using var iso = Iso9660Image.Open(path); Assert.AreEqual("SYNTHETIC", iso.VolumeIdentifier); Assert.IsNotNull(iso.Find("SLUS_207.72")); Assert.AreEqual(4u, iso.Find("SLUS_207.72")!.Length); }
        finally { File.Delete(path); }
    }

    private static int WriteRecord(Span<byte> target, uint sector, uint length, bool directory, byte[] name)
    {
        var recordLength = 33 + name.Length + ((name.Length & 1) == 0 ? 1 : 0); target[..recordLength].Clear(); target[0] = (byte)recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(target[2..], sector); BinaryPrimitives.WriteUInt32BigEndian(target[6..], sector);
        BinaryPrimitives.WriteUInt32LittleEndian(target[10..], length); BinaryPrimitives.WriteUInt32BigEndian(target[14..], length);
        target[25] = directory ? (byte)2 : (byte)0; target[28] = 1; target[31] = 1; target[32] = (byte)name.Length; name.CopyTo(target[33..]); return recordLength;
    }
}
