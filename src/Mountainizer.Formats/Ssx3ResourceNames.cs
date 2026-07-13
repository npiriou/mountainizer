using System.Text;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public sealed class Ssx3ResourceNames
{
    private readonly Dictionary<(int Array, int Track, int Resource), string> _names = [];
    public string? Find(int array, int track, int resource) => _names.GetValueOrDefault((array, track, resource));

    public static Ssx3ResourceNames? TryLoad(string phmPath, string psmPath, DiagnosticBag diagnostics)
    {
        if (!File.Exists(phmPath) || !File.Exists(psmPath)) return null;
        try
        {
            var result = new Ssx3ResourceNames(); var phm = File.ReadAllBytes(phmPath); var psm = File.ReadAllBytes(psmPath);
            var links = ReadLinks(phm); var names = ReadNames(psm);
            for (var array = 0; array < Math.Min(links.Count, names.Count); array++)
                for (var i = 0; i < Math.Min(links[array].Count, names[array].Count); i++)
                    result._names[(array, links[array][i].Track, links[array][i].Resource)] = names[array][i];
            diagnostics.Info("NAM001", $"Resolved {result._names.Count} PHM/PSM resource names", phmPath);
            return result;
        }
        catch (Exception ex) { diagnostics.Warn("NAM002", $"PHM/PSM names could not be parsed: {ex.Message}", phmPath); return null; }
    }

    private static List<List<(int Track, int Resource)>> ReadLinks(ReadOnlySpan<byte> data)
    {
        var r = new BinarySpanReader(data); r.Skip(8); var arrayCount = checked((int)r.ReadUInt32Little());
        if (arrayCount is < 0 or > 64) throw new InvalidDataException("PHM array count is invalid");
        var result = new List<List<(int, int)>>(arrayCount);
        for (var array = 0; array < arrayCount; array++)
        {
            r.ReadUInt32Little(); var count = checked((int)r.ReadUInt32Little()); if (count is < 0 or > 1_000_000) throw new InvalidDataException("PHM entry count is invalid");
            var entries = new List<(int, int)>(count);
            for (var i = 0; i < count; i++) { r.Skip(8); var track = r.ReadByte(); var rid = checked((int)r.ReadUInt24Little()); r.Skip(4); entries.Add((track, rid)); }
            result.Add(entries);
        }
        return result;
    }

    private static List<List<string>> ReadNames(ReadOnlySpan<byte> data)
    {
        var r = new BinarySpanReader(data); r.Skip(8); var arrayCount = checked((int)r.ReadUInt32Little());
        if (arrayCount is < 0 or > 64) throw new InvalidDataException("PSM array count is invalid");
        var result = new List<List<string>>(arrayCount);
        for (var array = 0; array < arrayCount; array++)
        {
            r.ReadUInt32Little(); var count = checked((int)r.ReadUInt32Little()); if (count is < 0 or > 1_000_000) throw new InvalidDataException("PSM string count is invalid");
            var strings = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var bytes = new List<byte>();
                while (true) { var value = r.ReadByte(); if (value == 0) break; bytes.Add(value); if (bytes.Count > 4096) throw new InvalidDataException("PSM string is too long"); }
                strings.Add(Encoding.ASCII.GetString(bytes.ToArray()));
            }
            r.Seek((r.Position + 3) & ~3); result.Add(strings);
        }
        return result;
    }
}
