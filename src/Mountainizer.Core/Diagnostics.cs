using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mountainizer.Core;

public enum DiagnosticSeverity { Trace, Debug, Information, Warning, Error }

public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    string? Section = null,
    long? AbsoluteOffset = null,
    long? RelativeOffset = null,
    long? ExpectedSize = null,
    long? AvailableSize = null,
    long? CountOrIndex = null,
    string? Exception = null,
    double? ElapsedMilliseconds = null);

public sealed class DiagnosticBag
{
    private readonly List<ParseDiagnostic> _items = [];
    private readonly object _gate = new();
    public IReadOnlyList<ParseDiagnostic> Items { get { lock (_gate) return _items.ToArray(); } }
    public bool HasErrors => Items.Any(x => x.Severity == DiagnosticSeverity.Error);
    public void Add(ParseDiagnostic diagnostic) { lock (_gate) _items.Add(diagnostic); }
    public void Info(string code, string message, string? file = null) => Add(new(DiagnosticSeverity.Information, code, message, file));
    public void Warn(string code, string message, string? file = null, string? section = null, long? offset = null) =>
        Add(new(DiagnosticSeverity.Warning, code, message, file, section, offset));
    public void Error(string code, string message, string? file = null, string? section = null, long? offset = null, Exception? exception = null) =>
        Add(new(DiagnosticSeverity.Error, code, message, file, section, offset, Exception: exception?.ToString()));
    public string ToJson() => JsonSerializer.Serialize(Items, JsonOptions);
    public void SaveJson(string path) => File.WriteAllText(path, ToJson());
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class FormatException(string message, long offset, long expected = 0, long available = 0)
    : IOException(message)
{
    public long Offset { get; } = offset;
    public long Expected { get; } = expected;
    public long Available { get; } = available;
}
