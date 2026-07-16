using System.Buffers.Binary;
using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3EffectDecoder
{
    private const int ParticleModelHeaderSize = ParticleModelAsset.SerializedHeaderSize;
    private const int ParticleElementSize = ParticleModelAsset.SerializedElementStride;
    private const int ParticleEmitterSize = ParticleEmitterAsset.SerializedSize;
    private const int LightSize = 112;
    private const int HaloSize = 80;
    private const int MaximumParticleElements = 100_000;

    public static string LightKindName(int kind) => kind switch
    {
        0 => "Directional",
        1 => "Spot",
        2 => "Point",
        3 => "Ambient",
        _ => "Unknown"
    };

    public static ParticleModelAsset DecodeParticleModel(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < ParticleModelHeaderSize)
            throw new FormatException("Particle model is truncated", source.LogicalOffset ?? 0, ParticleModelHeaderSize, data.Length);
        var selfReference = BinaryPrimitives.ReadUInt32LittleEndian(data); var expectedReference = Pack(trackId, resourceId);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]); var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var elementCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[76..]));
        var contentSize = checked(ParticleModelHeaderSize + elementCount * ParticleElementSize);
        var alignedSize = checked((contentSize + 15) & ~15);
        var paddingSize = data.Length - contentSize;
        if (selfReference != expectedReference || version != 1 || headerSize != 32
            || data[12..32].IndexOfAnyExcept((byte)0) >= 0 || BinaryPrimitives.ReadUInt32LittleEndian(data[32..]) != uint.MaxValue
            || BinaryPrimitives.ReadUInt32LittleEndian(data[36..]) != 48 || BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[44..]) != uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(data[72..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[80..]) != 36 || elementCount is < 0 or > MaximumParticleElements
            || (data.Length != contentSize && data.Length != alignedSize)
            || (paddingSize > 0 && data[contentSize..].IndexOfAnyExcept((byte)0) >= 0))
            throw new FormatException("Particle-model header or element count is inconsistent", source.LogicalOffset ?? 0, ParticleModelHeaderSize, data.Length);
        var minimum = ReadVector3(data, 48); var maximum = ReadVector3(data, 60);
        if (!IsFinite(minimum) || !IsFinite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new FormatException("Particle-model bounds are invalid", (source.LogicalOffset ?? 0) + 48, 24, data.Length);
        var elements = new ParticleElement[elementCount];
        var calculatedMinimum = new Vector3(float.PositiveInfinity);
        var calculatedMaximum = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < elements.Length; i++)
        {
            var offset = ParticleModelHeaderSize + i * ParticleElementSize;
            var position = ReadVector3(data, offset); var color = ReadVector3(data, offset + 12); var size = ReadSingle(data, offset + 24);
            if (!IsFinite(position) || !IsFinite(color) || color.X is < 0f or > 1f || color.Y is < 0f or > 1f
                || color.Z is < 0f or > 1f || !float.IsFinite(size) || size < 0)
                throw new FormatException($"Particle element {i} is invalid", (source.LogicalOffset ?? 0) + offset, ParticleElementSize, data.Length);
            calculatedMinimum = Vector3.Min(calculatedMinimum, position - new Vector3(size));
            calculatedMaximum = Vector3.Max(calculatedMaximum, position + new Vector3(size));
            elements[i] = new(Ssx3Coordinates.ToMountainizer(position), color, size);
        }
        if (elementCount > 0 && !ContainsBounds(minimum, maximum, calculatedMinimum, calculatedMaximum))
            throw new FormatException("Particle-model bounds do not enclose the rendered fog sprites", (source.LogicalOffset ?? 0) + 48, 24, data.Length);
        var bounds = ConvertBounds(minimum, maximum);
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Static Fog Particle Model", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["Version"] = version, ["ElementCount"] = elementCount,
            ["ElementStride"] = ParticleElementSize, ["AlignmentBytes"] = paddingSize,
            ["HeaderValue"] = 36, ["RuntimeManagerClass"] = ParticleModelAsset.RuntimeManagerClass,
            ["RuntimeCompileFunction"] = $"0x{ParticleModelAsset.RuntimeCompileFunction:X8}",
            ["RuntimeSubmitFunction"] = $"0x{ParticleModelAsset.RuntimeSubmitFunction:X8}",
            ["RuntimeElementLayout"] = "Position@0x00, RGB@0x0C, Size@0x18, stride 0x1C",
            ["RuntimePrimitive"] = ParticleModelAsset.RuntimePrimitive,
            ["RuntimeGsPrimRegister"] = $"0x{ParticleModelAsset.RuntimeGsPrimRegister:X2}",
            ["RuntimeGsColorScale"] = ParticleModelAsset.RuntimeGsColorScale,
            ["RuntimeTextureArchive"] = ParticleModelAsset.RuntimeTextureArchive,
            ["RuntimeTextureName"] = ParticleModelAsset.RuntimeTextureName,
            ["RuntimeTextureAssetId"] = ParticleModelAsset.RuntimeTextureAssetId,
            ["RuntimeTextureEnumIndex"] = ParticleModelAsset.RuntimeTextureEnumIndex,
            ["RuntimeBlendSelector"] = ParticleModelAsset.RuntimeBlendSelector,
            ["RuntimeGsAlphaRegister"] = $"0x{ParticleModelAsset.RuntimeGsAlphaRegister:X2}",
            ["RuntimeBlendEquation"] = ParticleModelAsset.RuntimeBlendEquation,
            ["RuntimeBlendMode"] = ParticleModelAsset.RuntimeBlendMode,
            ["RuntimeNearDepthFade"] = $"0 at <= {ParticleModelAsset.RuntimeNearFadeStartDepth}; linear to 1 before {ParticleModelAsset.RuntimeNearFadeEndDepth:R}",
            ["RuntimeFarDepthFade"] = $"1 below {ParticleModelAsset.RuntimeFarFadeStartDepth}; linear to 0 at {ParticleModelAsset.RuntimeFarCullDepth}",
            ["RuntimeAlphaEquation"] = "nearDepthFade(element) * farDepthFade(instance AABB midpoint)",
            ["RuntimeDepthSorted"] = true, ["HasIndependentRuntimeWorldObject"] = false,
            ["BoundsContainPositionPlusSizeCubes"] = true,
            ["PayloadSize"] = data.Length };
        return new($"Fog Particle Model {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, bounds, elements, properties);
    }

    public static ParticleEmitterAsset DecodeParticleEmitter(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length != ParticleEmitterSize)
            throw new FormatException("Particle emitter has an unexpected size", source.LogicalOffset ?? 0, ParticleEmitterSize, data.Length);
        if (data[..16].IndexOfAnyExcept((byte)0) >= 0 || BinaryPrimitives.ReadUInt32LittleEndian(data[28..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[44..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(data[60..]) != 0
            || ReadSingle(data, 76) != 1f || data[128..].IndexOfAnyExcept((byte)0) >= 0)
            throw new FormatException("Particle-emitter transform padding is inconsistent", source.LogicalOffset ?? 0, ParticleEmitterSize, data.Length);
        var axes = new[] { ReadVector3(data, 16), ReadVector3(data, 32), ReadVector3(data, 48) };
        var position = ReadVector3(data, 64); var center = ReadVector3(data, 80); var radius = ReadSingle(data, 92);
        var reference0 = BinaryPrimitives.ReadUInt32LittleEndian(data[96..]); var reference1 = BinaryPrimitives.ReadUInt32LittleEndian(data[100..]);
        var minimum = ReadVector3(data, 104); var maximum = ReadVector3(data, 116); var expectedReference = Pack(trackId, resourceId);
        var determinant = Vector3.Dot(Vector3.Cross(axes[0], axes[1]), axes[2]);
        if (axes.Any(x => !IsFinite(x)) || !IsFinite(position) || !IsFinite(center) || !float.IsFinite(radius) || radius < 0
            || !IsFinite(minimum) || !IsFinite(maximum) || reference0 != expectedReference || reference1 != expectedReference
            || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z
            || axes.Any(x => MathF.Abs(x.LengthSquared() - 1f) > 0.001f)
            || MathF.Abs(Vector3.Dot(axes[0], axes[1])) > 0.001f || MathF.Abs(Vector3.Dot(axes[0], axes[2])) > 0.001f
            || MathF.Abs(Vector3.Dot(axes[1], axes[2])) > 0.001f || MathF.Abs(determinant - 1f) > 0.001f
            || Vector3.Distance(center, (minimum + maximum) * 0.5f) > 0.1f)
            throw new FormatException("Particle-emitter geometry or model references are invalid", source.LogicalOffset ?? 0, ParticleEmitterSize, data.Length);
        var convertedAxes = axes.Select(Ssx3Coordinates.ToMountainizer).ToArray(); var bounds = ConvertBounds(minimum, maximum);
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Static Fog Particle Instance", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["ModelReference"] = $"{trackId}:{resourceId}", ["Position"] = Ssx3Coordinates.ToMountainizer(position),
            ["BoundingCenter"] = Ssx3Coordinates.ToMountainizer(center), ["BoundingRadius"] = radius,
            ["BoundingBoxMin"] = bounds.Minimum, ["BoundingBoxMax"] = bounds.Maximum,
            ["RuntimeObjectType"] = ParticleEmitterAsset.RuntimeObjectType,
            ["RuntimeLoaderFunction"] = $"0x{ParticleEmitterAsset.RuntimeLoaderFunction:X8}",
            ["RuntimeVisibilityFunction"] = $"0x{ParticleEmitterAsset.RuntimeVisibilityFunction:X8}",
            ["RuntimeQueueRenderFunction"] = $"0x{ParticleEmitterAsset.RuntimeQueueRenderFunction:X8}",
            ["RuntimeParticleCompileFunction"] = $"0x{ParticleEmitterAsset.RuntimeParticleCompileFunction:X8}",
            ["RuntimeStaticFogInstance"] = true,
            ["RuntimeHasEmissionOrSimulation"] = false,
            ["RuntimeCoarseFadeStartDepth"] = ParticleEmitterAsset.RuntimeCoarseFadeStartDepth,
            ["RuntimeCoarseCullDepth"] = ParticleEmitterAsset.RuntimeCoarseCullDepth,
            ["SerializedTransformOffset"] = $"0x{ParticleEmitterAsset.SerializedTransformOffset:X2}",
            ["SerializedModelReferenceOffsets"] = "0x60/0x64",
            ["RuntimeResolvedModelPointerOffset"] = $"0x{ParticleEmitterAsset.RuntimeResolvedModelPointerOffset:X2}",
            ["SerializedBoundsOffset"] = $"0x{ParticleEmitterAsset.SerializedBoundsOffset:X2}",
            ["RuntimeConsumesSerializedBoundingRadius"] = ParticleEmitterAsset.RuntimeConsumesSerializedBoundingRadius,
            ["RuntimeBoundingRadiusUse"] = ParticleEmitterAsset.RuntimeBoundingRadiusUse,
            ["PayloadSize"] = data.Length };
        return new($"Fog Particle Instance {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium }, trackId, resourceId,
            convertedAxes, Ssx3Coordinates.ToMountainizer(position), Ssx3Coordinates.ToMountainizer(center), radius,
            bounds, trackId, resourceId, properties);
    }

    public static LightAsset DecodeLight(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length != LightSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x005541C9
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 16 || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 0x00114B20
            || BinaryPrimitives.ReadUInt32LittleEndian(data[104..]) != 0x00542BDE || BinaryPrimitives.ReadUInt32LittleEndian(data[108..]) != 16)
            throw new FormatException("Light record header is unknown", source.LogicalOffset ?? 0, LightSize, data.Length);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var kind = BinaryPrimitives.ReadInt32LittleEndian(data[16..]); var intensity = ReadSingle(data, 20);
        var selectionWeight = ReadSingle(data, 24); var range = ReadSingle(data, 28); var color = ReadVector3(data, 32);
        var direction = ReadVector3(data, 44); var position = ReadVector3(data, 56);
        var minimum = ReadVector3(data, 68); var maximum = ReadVector3(data, 80);
        var spotInnerConeCosine = ReadSingle(data, 92); var spotOuterConeCosine = ReadSingle(data, 96);
        var distanceFalloffExponent = unchecked((sbyte)data[100]); var angularFalloffExponent = unchecked((sbyte)data[101]);
        var tailMarker = BinaryPrimitives.ReadUInt16LittleEndian(data[102..]);
        if (kind is < 0 or > 3 || !float.IsFinite(intensity) || !float.IsFinite(selectionWeight) || !float.IsFinite(range)
            || !IsFinite(color) || !IsFinite(direction) || !IsFinite(position) || !IsFinite(minimum) || !IsFinite(maximum)
            || direction.LengthSquared() is < 0.99f or > 1.01f || range < 0
            || !float.IsFinite(spotInnerConeCosine) || !float.IsFinite(spotOuterConeCosine)
            || kind == 1 && (spotInnerConeCosine is < -1f or > 1f || spotOuterConeCosine is < -1f or > 1f
                || spotInnerConeCosine < spotOuterConeCosine)
            || kind != 1 && (spotInnerConeCosine != 0f || spotOuterConeCosine != 0f)
            || tailMarker != LightAsset.ExpectedTailMarker)
            throw new FormatException("Light parameters are invalid", (source.LogicalOffset ?? 0) + 16, 32, data.Length);
        var convertedPosition = Ssx3Coordinates.ToMountainizer(position);
        var convertedDirection = Ssx3Coordinates.ToMountainizer(direction);
        var hasOrderedBounds = minimum.X <= maximum.X && minimum.Y <= maximum.Y && minimum.Z <= maximum.Z;
        var hasUsableBounds = hasOrderedBounds && IsReasonablePosition(minimum) && IsReasonablePosition(maximum);
        SceneBounds? bounds = hasUsableBounds ? ConvertBounds(minimum, maximum) : null;
        var isPlaceholder = intensity == 0.6f && selectionWeight == 1f && range == 1200f
            && color == Vector3.One && direction == Vector3.UnitX && position == Vector3.Zero && !hasOrderedBounds;
        if (kind is 1 or 2 && !hasUsableBounds
            || kind == 3 && !isPlaceholder && !hasUsableBounds
            || kind == 2 && !IsRangeCube(position, minimum, maximum, range)
            || kind == 3 && !isPlaceholder && !IsRangeCube(position, minimum, maximum, range))
            throw new FormatException("Light kind and spatial bounds are inconsistent", (source.LogicalOffset ?? 0) + 56, 36, data.Length);
        var rawWords = ReadWords(data);
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Light", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["LightKind"] = kind,
            ["LightKindName"] = LightKindName(kind), ["IsPlaceholder"] = isPlaceholder,
            ["Flags"] = $"0x{flags:X8}", ["HasRuntimeFilterFlag0x100"] = (flags & 0x100) != 0,
            ["RuntimeLoaderFunction"] = $"0x{LightAsset.RuntimeLoaderFunction:X8}",
            ["RuntimeAdmissionPredicate"] = $"0x{LightAsset.RuntimeAdmissionPredicate:X8}",
            ["RuntimeAdmissionEquation"] = "(Flags & 0x100) == 0 && Kind is Spot (1) or Point (2)",
            ["IsRuntimeAdmitted"] = !((flags & 0x100) != 0) && kind is 1 or 2,
            ["RuntimeAdmissionOutcome"] = !((flags & 0x100) != 0) && kind is 1 or 2
                ? "Admitted as an internal Type-6 spatial light"
                : (flags & 0x100) != 0
                    ? "Rejected by serialized flag 0x100"
                    : kind == 0
                        ? "Directional authoring record rejected before runtime registration"
                        : "Ambient authoring record rejected before runtime registration",
            ["RuntimeInternalResourceType"] = LightAsset.RuntimeInternalResourceType,
            ["Intensity"] = intensity, ["SelectionWeight"] = selectionWeight,
            ["Range"] = range, ["Color"] = color, ["Direction"] = convertedDirection, ["Position"] = convertedPosition,
            ["SpotInnerConeCosine"] = spotInnerConeCosine, ["SpotOuterConeCosine"] = spotOuterConeCosine,
            ["DistanceFalloffExponent"] = distanceFalloffExponent, ["AngularFalloffExponent"] = angularFalloffExponent,
            ["TailMarker"] = $"0x{tailMarker:X4}",
            ["BoundingBoxMin"] = bounds?.Minimum, ["BoundingBoxMax"] = bounds?.Maximum,
            ["PayloadSize"] = data.Length };
        return new($"Light {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, flags, kind, intensity, selectionWeight, range, color, convertedDirection, convertedPosition, bounds,
            spotInnerConeCosine, spotOuterConeCosine, distanceFalloffExponent, angularFalloffExponent, tailMarker,
            isPlaceholder, rawWords, properties);
    }

    public static HaloAsset DecodeHalo(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length != HaloSize || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 0x00114744
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != HaloAsset.InvariantWord08
            || BinaryPrimitives.ReadUInt32LittleEndian(data[64..]) != 33 || BinaryPrimitives.ReadUInt32LittleEndian(data[72..]) != 0x005553C5
            || BinaryPrimitives.ReadUInt32LittleEndian(data[76..]) != 33)
            throw new FormatException("Halo record header is unknown", source.LogicalOffset ?? 0, HaloSize, data.Length);
        var serializedCollectionPointerToken = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var visualModeCode = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var color = ReadVector3(data, 16);
        var position = ReadVector3(data, 28); var minimum = ReadVector3(data, 40); var maximum = ReadVector3(data, 52);
        var serializedEntryPointerToken = BinaryPrimitives.ReadUInt32LittleEndian(data[68..]);
        var expectedHalfExtent = visualModeCode switch { 0x10 => 50f, 0x20 => 100f, _ => float.NaN };
        var extents = (maximum - minimum) * 0.5f;
        if (!float.IsFinite(expectedHalfExtent) || !IsFinite(color) || !IsFinite(position) || !IsFinite(minimum) || !IsFinite(maximum)
            || Vector3.Distance(extents, new(expectedHalfExtent)) > 0.01f
            || Vector3.Distance(position, (minimum + maximum) * 0.5f) > 0.1f
            || (serializedCollectionPointerToken & 7) != 0 || (serializedEntryPointerToken & 7) != 0
            || resourceId < 0 || serializedEntryPointerToken < checked((uint)resourceId * HaloAsset.SerializedEntryStride))
            throw new FormatException("Halo geometry is invalid", (source.LogicalOffset ?? 0) + 16, 56, data.Length);
        var convertedPosition = Ssx3Coordinates.ToMountainizer(position); var bounds = ConvertBounds(minimum, maximum);
        var radius = Vector3.Distance(position, maximum); var rawWords = ReadWords(data);
        var properties = new Dictionary<string, object?> { ["ParsedType"] = "SSX3 Halo", ["TrackId"] = trackId,
            ["ResourceId"] = resourceId, ["SerializedCollectionPointerToken"] = $"0x{serializedCollectionPointerToken:X8}",
            ["InvariantWord08"] = $"0x{HaloAsset.InvariantWord08:X8}", ["Color"] = color,
            ["VisualModeCode"] = $"0x{visualModeCode:X2}",
            ["RuntimeOcclusionProbeScale"] = visualModeCode switch { 0x10 => 100f, 0x20 => 80f, 0x40 => 200f, _ => 0f },
            ["RuntimeRenderScale"] = visualModeCode switch { 0x10 => 180f, 0x20 => 100f, 0x40 => 350f, _ => 0f },
            ["RuntimeColorDirection"] = color.LengthSquared() > 0f ? Vector3.Normalize(color) : Vector3.Zero,
            ["RuntimeTextureName"] = visualModeCode switch { 0x10 or 0x40 => "SHALO", 0x20 => "MHALO", _ => "Unknown" },
            ["RuntimeTextureAssetId"] = visualModeCode switch { 0x10 or 0x40 => "shal", 0x20 => "mhal", _ => string.Empty },
            ["RuntimeTextureArchive"] = "data/textures/effects.ssh",
            ["RuntimeGsColorScale"] = HaloAsset.RuntimeGsColorScale,
            ["RuntimeAlphaSource"] = HaloAsset.RuntimeAlphaSource,
            ["RuntimeBlendSelector"] = HaloAsset.RuntimeBlendSelector,
            ["RuntimeGsAlphaRegister"] = $"0x{HaloAsset.RuntimeGsAlphaRegister:X2}",
            ["RuntimeBlendEquation"] = HaloAsset.RuntimeBlendEquation,
            ["RuntimeBlendMode"] = HaloAsset.RuntimeBlendMode,
            ["RuntimeVisibilityStateOffsets"] = "0x40/0x44",
            ["HalfExtent"] = expectedHalfExtent,
            ["SerializedEntryPointerToken"] = $"0x{serializedEntryPointerToken:X8}",
            ["SerializedEntryTableBasePointerToken"] = $"0x{serializedEntryPointerToken - (uint)resourceId * HaloAsset.SerializedEntryStride:X8}",
            ["SerializedEntryStride"] = HaloAsset.SerializedEntryStride,
            ["Position"] = convertedPosition, ["Radius"] = radius,
            ["BoundingBoxMin"] = bounds.Minimum, ["BoundingBoxMax"] = bounds.Maximum, ["PayloadSize"] = data.Length };
        return new($"Halo {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, color, convertedPosition, bounds, radius, expectedHalfExtent,
            serializedCollectionPointerToken, visualModeCode, serializedEntryPointerToken, rawWords, properties);
    }

    private static uint Pack(int trackId, int resourceId) => (uint)trackId | (uint)resourceId << 8;
    private static uint[] ReadWords(ReadOnlySpan<byte> data)
    {
        var words = new uint[data.Length / 4];
        for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        return words;
    }
    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
    private static Vector3 ReadVector3(ReadOnlySpan<byte> data, int offset) => new(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool ContainsBounds(Vector3 outerMinimum, Vector3 outerMaximum, Vector3 innerMinimum, Vector3 innerMaximum)
    {
        var scale = MathF.Max(1f, MathF.Max(outerMinimum.Length(), outerMaximum.Length()));
        var tolerance = MathF.Max(0.01f, scale * 0.00001f);
        return innerMinimum.X >= outerMinimum.X - tolerance && innerMinimum.Y >= outerMinimum.Y - tolerance
            && innerMinimum.Z >= outerMinimum.Z - tolerance && innerMaximum.X <= outerMaximum.X + tolerance
            && innerMaximum.Y <= outerMaximum.Y + tolerance && innerMaximum.Z <= outerMaximum.Z + tolerance;
    }
    private static bool IsReasonablePosition(Vector3 value) => IsFinite(value)
        && MathF.Abs(value.X) < 1e19f && MathF.Abs(value.Y) < 1e19f && MathF.Abs(value.Z) < 1e19f;
    private static bool IsRangeCube(Vector3 position, Vector3 minimum, Vector3 maximum, float range) =>
        Vector3.Distance(position, (minimum + maximum) * 0.5f) <= 0.1f
        && Vector3.Distance((maximum - minimum) * 0.5f, new(range)) <= 0.1f;
    private static SceneBounds ConvertBounds(Vector3 minimum, Vector3 maximum) => SceneBounds.FromPoints(new[]
        { Ssx3Coordinates.ToMountainizer(minimum), Ssx3Coordinates.ToMountainizer(maximum) });
}
