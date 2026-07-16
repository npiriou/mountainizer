using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Mountainizer.Core;

public enum SupportConfidence { Unknown, Low, Medium, High, Verified }
public enum TextureUsage { Diffuse, Lightmap }

public sealed record SourceByteRange(
    string SourceFile,
    long SourceOffset,
    long SourceLength,
    string SectionName,
    int OriginalIndex,
    SupportConfidence Confidence,
    long? LogicalOffset = null);

public interface ISceneItem
{
    string Name { get; }
    SourceByteRange Source { get; }
    IReadOnlyDictionary<string, object?> Properties { get; }
}

public sealed class MountainizerScene
{
    public required string Name { get; init; }
    public List<TerrainPatch> Terrain { get; } = [];
    public List<PropInstance> Props { get; } = [];
    public List<Spline> Splines { get; } = [];
    public List<NavigationPath> NavigationPaths { get; } = [];
    public List<NavigationTableAsset> NavigationTables { get; } = [];
    public List<TriggerVolume> Triggers { get; } = [];
    public List<CameraTriggerTableAsset> CameraTriggerTables { get; } = [];
    public List<VisibilityCurtain> VisibilityCurtains { get; } = [];
    public List<CollisionAsset> Collisions { get; } = [];
    public List<SphereTreeCollisionAsset> SphereTrees { get; } = [];
    public List<SoundTriggerTableAsset> SoundTriggerTables { get; } = [];
    public List<PlanarRouteAsset> PlanarRoutes { get; } = [];
    public List<StructuredTableAsset> StructuredTables { get; } = [];
    public List<BnklBankAsset> AudioBanks { get; } = [];
    public List<AvalancheAnimationAsset> AvalancheAnimations { get; } = [];
    public List<ParticleModelAsset> ParticleModels { get; } = [];
    public List<ParticleEmitterAsset> ParticleEmitters { get; } = [];
    public List<LightAsset> Lights { get; } = [];
    public List<HaloAsset> Halos { get; } = [];
    public List<NisScriptObjectTableAsset> NisReferenceTables { get; } = [];
    public List<NavigationTableMarker> NavigationMarkers { get; } = [];
    public List<ModelAsset> Models { get; } = [];
    public List<MaterialAsset> Materials { get; } = [];
    public List<TextureAsset> Textures { get; } = [];
    public List<UnknownSection> UnknownSections { get; } = [];
    public SceneBounds Bounds => SceneBounds.FromPoints(Terrain.SelectMany(x => x.Mesh.Positions));
}

public sealed record MeshData(IReadOnlyList<Vector3> Positions, IReadOnlyList<Vector3> Normals,
    IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<uint> Indices,
    IReadOnlyList<Vector2>? LightmapTextureCoordinates = null);

public sealed record TerrainPatch(
    string Name,
    SourceByteRange Source,
    IReadOnlyList<Vector3> ControlPoints,
    MeshData Mesh,
    int TrackId,
    short TextureResourceId,
    short LightmapResourceId,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3ResourceType = 1;
    public const int SerializedSize = 432;
    public const uint InvariantHeaderWord = 0x004045B7;
    public const ushort ObservedPatchFlagsMask = 0x003B;
    public const ushort ObservedTextureStateFlagsMask = 0x01A9;
    public const ushort ObservedRenderFlagsMask = 0x00F8;
    public const ushort TextureStateWithoutSecondary = 0x0029;
    public const ushort TextureStateWithSecondary = 0x01A9;
    public const short InvariantQueueValue = -1;
    public const ushort InvariantTailWord0 = 0x483C;
    public const ushort InvariantTailWord1 = 0x0011;
    public const int RetailRenderFunction = 0x0038CE20;
    public const int RetailSecondaryPassFunction = 0x0038D168;
    public const int RetailTerrainStateSetupStart = 0x0038BCB4;
    public const int RetailTerrainStateSetupEnd = 0x0038BD78;
    public const int RuntimePrimaryAlphaSelector = 1;
    public const ulong RuntimePrimaryGsAlphaRegister = 0x2A;
    public const string RuntimePrimaryBlendEquation = "Cs (source replacement)";
    public const int RuntimeLightmapAlphaSelector = 8;
    public const ulong RuntimeLightmapGsAlphaRegister = 0x81;
    public const string RuntimeLightmapBlendEquation = "(Cd - Cs) * As";
    public const ushort RuntimeSecondaryPassMask = 0x0060;
    public const ushort RuntimeDestinationAlphaMask = 0x0040;
    public const int RuntimeSecondaryAlphaSelector = 17;
    public const ulong RuntimeSecondaryGsAlphaRegister = 0x58;
    public const string RuntimeSecondaryBlendEquation = "(Cs - 0) * Ad + Cd";
    public const int RuntimeFallbackAlphaSelector = 2;
    public const ulong RuntimeFallbackGsAlphaRegister = 0x0000008000000068;
    public const string RuntimeFallbackBlendEquation = "(Cs - 0) * FIX(128) + Cd = Cs + Cd";

    public uint SerializedTrackWord { get; init; }
    public uint HeaderWord { get; init; }
    public SsxSurfaceType Surface { get; init; }
    public ushort PatchFlags { get; init; }
    public ushort TextureStateFlags { get; init; }
    public ushort RenderFlags { get; init; }
    public Vector4 LightmapRectangle { get; init; }
    public IReadOnlyList<Vector2> DiffuseUvCorners { get; init; } = [];
    public IReadOnlyList<Vector4> StoredDifferenceCoefficientsSsx { get; init; } = [];
    public Vector4 BoundingSphereSsx { get; init; }
    public int ObjectTrackId { get; init; }
    public int ObjectResourceId { get; init; }
    public ushort TextureBankTrackWord { get; init; }
    public ushort TextureSubChunkId { get; init; }
    public Vector3 BoundsMinimumSsx { get; init; }
    public Vector3 BoundsMaximumSsx { get; init; }
    public IReadOnlyList<Vector3> CornerControlPointsSsx { get; init; } = [];
    public short SecondaryTextureResourceId { get; init; } = -1;
    public IReadOnlyList<short> QueueValues { get; init; } = [];
    public ushort TailWord0 { get; init; }
    public ushort TailWord1 { get; init; }

    public bool HasSecondaryTexture => SecondaryTextureResourceId >= 0;
    public bool RequestsRuntimeSecondaryPass => (RenderFlags & RuntimeSecondaryPassMask) != 0;
    public bool RuntimeSecondaryPassUsesDestinationAlpha => (RenderFlags & RuntimeDestinationAlphaMask) != 0;
    public int RuntimeSecondaryPassAlphaSelector => RuntimeSecondaryPassUsesDestinationAlpha
        ? RuntimeSecondaryAlphaSelector : RuntimeFallbackAlphaSelector;
    public ulong RuntimeSecondaryPassGsAlphaRegister => RuntimeSecondaryPassUsesDestinationAlpha
        ? RuntimeSecondaryGsAlphaRegister : RuntimeFallbackGsAlphaRegister;
    public string RuntimeSecondaryPassBlendEquation => RuntimeSecondaryPassUsesDestinationAlpha
        ? RuntimeSecondaryBlendEquation : RuntimeFallbackBlendEquation;
    public bool HasValidObservedRetailLayout => HeaderWord == InvariantHeaderWord
        && (ushort)Surface <= (ushort)SsxSurfaceType.WipeoutRock
        && (PatchFlags & ~ObservedPatchFlagsMask) == 0
        && TextureStateFlags == (HasSecondaryTexture ? TextureStateWithSecondary : TextureStateWithoutSecondary)
        && (RenderFlags & ~ObservedRenderFlagsMask) == 0
        && DiffuseUvCorners.Count == 4
        && StoredDifferenceCoefficientsSsx.Count == 16
        && StoredDifferenceCoefficientsSsx.All(value => value.W == 1f)
        && BoundingSphereSsx.W > 0 && float.IsFinite(BoundingSphereSsx.X + BoundingSphereSsx.Y + BoundingSphereSsx.Z + BoundingSphereSsx.W)
        && ObjectTrackId == TrackId && ObjectResourceId >= 0
        && TextureBankTrackWord == TrackId << 8 && TextureSubChunkId > 0
        && CornerControlPointsSsx.Count == 4
        && TextureResourceId >= 0 && LightmapResourceId >= 0 && SecondaryTextureResourceId >= -1
        && HasSecondaryTexture == RequestsRuntimeSecondaryPass
        && (!RequestsRuntimeSecondaryPass || RuntimeSecondaryPassUsesDestinationAlpha)
        && QueueValues.Count == 3 && QueueValues.All(value => value == InvariantQueueValue)
        && TailWord0 == InvariantTailWord0 && TailWord1 == InvariantTailWord1;
}

public enum Ps2DmaTagId
{
    Refe = 0,
    Cnt = 1,
    Next = 2,
    Ref = 3,
    Refs = 4,
    Call = 5,
    Ret = 6,
    End = 7
}

public enum DmaRelocationTarget
{
    ModelData,
    InstanceExtension
}

public sealed record Ps2DmaTag(int Offset, int QuadwordCount, Ps2DmaTagId Id, bool Irq,
    uint Address, bool Scratchpad, uint VifCode0, uint VifCode1);

public sealed record InstanceDmaRelocation(Ps2DmaTag Tag, DmaRelocationTarget Target,
    bool RuntimePatchesTerminalMscal);

public sealed record InstanceDmaProgram(IReadOnlyList<InstanceDmaRelocation> Relocations,
    Ps2DmaTag ReturnTag, bool UsesScratchpadRewrite, bool UsesImmediateReturnRewrite,
    byte[] RuntimeRewriteWorkspace);

public sealed record Ps2VifCommand(int Offset, uint Raw, byte Command, bool Interrupt,
    int ElementCount, ushort Immediate, string Name, int PayloadOffset, int PayloadSize)
{
    public bool IsUnpack => Command is >= 0x60 and <= 0x7f;
    public int? UnpackDestinationAddress => IsUnpack ? Immediate & 0x03ff : null;
    public bool UnpackUsesTops => IsUnpack && (Immediate & 0x8000) != 0;
    public bool UnpackIsUnsigned => IsUnpack && (Immediate & 0x4000) != 0;
    public bool UnpackIsMasked => IsUnpack && (Command & 0x10) != 0;
}

public readonly record struct PackedVertexColor5(ushort Raw, byte Red, byte Green, byte Blue, byte Alpha)
{
    public Vector4 Normalized => new(Red / 31f, Green / 31f, Blue / 31f, Alpha);

    public static PackedVertexColor5 Decode(ushort value) => new(value,
        (byte)(value & 31), (byte)(value >> 5 & 31), (byte)(value >> 10 & 31), (byte)(value >> 15));
}

public sealed record InstanceDmaSourceBlock(int Offset, int QuadwordCount, byte[] Data,
    uint TerminalPlaceholder, uint TerminalVifCode1, IReadOnlyList<Ps2VifCommand> VifCommands,
    bool VifDecodeComplete)
{
    /// <summary>
    /// Decodes the per-vertex RGBA5 array appended to the matching model packet. The complete NTSC-U corpus uses
    /// exactly one V4-5 UNPACK here, with one color for every vertex in the model packet's terminal attribute array.
    /// </summary>
    public IReadOnlyList<PackedVertexColor5> DecodeVertexColors()
    {
        var colors = new List<PackedVertexColor5>();
        foreach (var command in VifCommands.Where(command => command.Name == "UNPACK_V4_5"))
        {
            var payloadOffset = command.PayloadOffset - Offset;
            for (var index = 0; index < command.ElementCount; index++)
                colors.Add(PackedVertexColor5.Decode(BinaryPrimitives.ReadUInt16LittleEndian(
                    Data.AsSpan(payloadOffset + index * 2, 2))));
        }
        return colors;
    }
}

public sealed record InstanceDmaProgramSet(int ExtensionOffset, IReadOnlyList<InstanceDmaProgram> Programs,
    IReadOnlyList<InstanceDmaSourceBlock> SourceBlocks, int StructuralBytes, int SourceBytes);

public sealed record PropInstance(string Name, SourceByteRange Source, Matrix4x4 Transform,
    int ModelTrackId, int ModelResourceId, IReadOnlyDictionary<string, object?> Properties,
    InstanceDmaProgramSet? RenderDmaProgram = null) : ISceneItem
{
    private PropClassification? _classification;
    public PropClassification Classification => _classification ??= PropClassifier.Classify(Name);
    public bool IsCollisionProxy => Classification.Category == PropRenderCategory.Collision;
    public bool IsNonVisualGameplayProxy => !Classification.IsVisual;
}
public sealed record ModelSubmesh(MeshData Mesh, int MaterialTrackId, int MaterialResourceId);
public sealed record ModelAsset(string Name, SourceByteRange Source, MeshData? Mesh, IReadOnlyList<ModelSubmesh> Submeshes,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3MdrResourceType = 2;
    public const int Ssx3MdrHeaderSize = 44;
    public const int Ssx3MdrMaterialTableOffset = 40;
    public const uint Ssx3MdrObservedFlagMask = 0x0000000B;
    public const int Ssx3MdrAnimationSetupFunction = 0x0034442C;
    public uint MdrFlags => Properties.TryGetValue("ModelFlags", out var value) ? Convert.ToUInt32(value) : 0;
    public float AnimationDurationSeconds => Properties.TryGetValue("AnimationDurationSeconds", out var value) ? Convert.ToSingle(value) : 0f;
}
public sealed record MaterialAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId, short TextureResourceId,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3ResourceType = 0;
    public const int SerializedBaseSize = 20;
    public const uint NoTextureFrameTableToken = uint.MaxValue;
    public const int Ssx3LoaderFunction = 0x003AA758;
    public const int Ssx3RuntimeSetupFunction = 0x0034442C;
    public const int Ssx3RuntimeFrameSelectorFunction = 0x00344730;
    public const int Ssx3RuntimeFrameIndexInitializerFunction = 0x00343C60;
    public const int Ssx3RuntimeRandomFunction = 0x003177F0;
    public const int Ssx3PrimaryTextureRendererFunction = 0x0037F240;
    public const ushort ObservedStateWord1Mask = 0x007f;
    public const ushort DefaultTextureStateWord02 = ushort.MaxValue;
    public const ushort PrimaryTextureStateSourceBit = 0x0008;
    public const uint PrimaryTextureStateDestinationBit = 0x00040000;
    public const int RuntimeOpaquePrimaryAlphaSelector = 1;
    public const ulong RuntimeOpaquePrimaryGsAlphaRegister = 0x2A;
    public const string RuntimeOpaquePrimaryBlendEquation = "Cs";
    public const int RuntimeBlendedPrimaryAlphaSelector = 5;
    public const ulong RuntimeBlendedPrimaryGsAlphaRegister = 0x44;
    public const string RuntimeBlendedPrimaryBlendEquation = "(Cs - Cd) * As + Cd";
    public const uint ObservedPacketAddressAdjustment = 0x0000ffff;
    public const uint RuntimeDmaCallTagWord0 = 0x50000000;
    public const Ps2DmaTagId RuntimeDmaCallTagId = Ps2DmaTagId.Call;
    public const int RuntimeDmaCallQuadwordCount = 0;
    public const ushort NondefaultTextureStateWord02Flag = 0x0020;

    public ushort TextureStateWord02 { get; init; } = DefaultTextureStateWord02;
    public uint PacketAddressAdjustment { get; init; } = ObservedPacketAddressAdjustment;
    public IReadOnlyList<short> SerializedRuntimeScratch { get; init; } = [];
    public ushort StateWord0 { get; init; }
    public ushort StateWord1 { get; init; }
    public uint SerializedTextureFrameTableToken { get; init; } = NoTextureFrameTableToken;
    public IReadOnlyList<int> TextureFrameResourceIds { get; init; } = [];
    public bool HasTextureFrameTable => SerializedTextureFrameTableToken != NoTextureFrameTableToken;
    public bool HasNondefaultTextureStateWord02 => TextureStateWord02 != DefaultTextureStateWord02;
    public bool AddsPrimaryTextureStateBit => (TextureStateWord02 & PrimaryTextureStateSourceBit) != 0;
    public bool UsesPrimaryTextureAlphaBlend => AddsPrimaryTextureStateBit;
    public int RuntimePrimaryAlphaSelector => UsesPrimaryTextureAlphaBlend
        ? RuntimeBlendedPrimaryAlphaSelector : RuntimeOpaquePrimaryAlphaSelector;
    public ulong RuntimePrimaryGsAlphaRegister => UsesPrimaryTextureAlphaBlend
        ? RuntimeBlendedPrimaryGsAlphaRegister : RuntimeOpaquePrimaryGsAlphaRegister;
    public string RuntimePrimaryBlendEquation => UsesPrimaryTextureAlphaBlend
        ? RuntimeBlendedPrimaryBlendEquation : RuntimeOpaquePrimaryBlendEquation;
    public int TextureFrameCount => TextureFrameResourceIds.Count;
    public int ExpectedSerializedSize => SerializedBaseSize + (HasTextureFrameTable ? 4 + TextureFrameCount * 4 : 0);

    public int TextureResourceIdForFrame(int frameIndex)
    {
        if (!HasTextureFrameTable)
            return TextureResourceId;
        if ((uint)frameIndex >= (uint)TextureFrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return TextureFrameResourceIds[frameIndex];
    }

    public int InitialTextureFrameIndex(uint runtimeRandomValue) =>
        TextureFrameCount == 0 ? 0 : (int)(runtimeRandomValue % (uint)TextureFrameCount);

    public uint RuntimeDmaCallTargetAddress(uint baseAddress) =>
        unchecked(baseAddress + PacketAddressAdjustment);

    public bool HasValidObservedRetailLayout => TextureResourceId >= 0
        && PacketAddressAdjustment == ObservedPacketAddressAdjustment
        && SerializedRuntimeScratch.Count == 2 && SerializedRuntimeScratch.All(value => value == 0)
        && StateWord0 == (HasNondefaultTextureStateWord02 ? 49 : 1)
        && (StateWord1 & ~ObservedStateWord1Mask) == 0 && (StateWord1 & 1) != 0
        && ((StateWord1 & NondefaultTextureStateWord02Flag) != 0) == HasNondefaultTextureStateWord02
        && (!HasTextureFrameTable
            ? TextureFrameCount == 0
            : TextureFrameCount > 0
                && TextureFrameResourceIds[0] == TextureResourceId
                && TextureFrameResourceIds.All(textureId => textureId is >= 0 and <= ushort.MaxValue)
                && TextureFrameResourceIds.Select((textureId, index) => textureId == TextureResourceId + index).All(valid => valid));
}
public sealed record TextureAsset(string Name, SourceByteRange Source, int Width, int Height, int TrackId,
    int ResourceId, byte[] RgbaPixels, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3RendererStateOffset = 0x0c;
    public const uint Ssx3RendererDispatchMask = 0x00660000;
    public uint SerializedRendererStateWord0C { get; init; }
    public uint RendererDispatchState => SerializedRendererStateWord0C & Ssx3RendererDispatchMask;
    public bool Decoded => RgbaPixels.Length == Width * Height * 4;
    public TextureUsage Usage => Properties.TryGetValue("TextureUsage", out var value) && Enum.TryParse<TextureUsage>(value?.ToString(), out var usage) ? usage : TextureUsage.Diffuse;
    public bool IsLightmap => Usage == TextureUsage.Lightmap;
}
public sealed record SplinePoint(Vector3 Position, float? Time = null);
public sealed record SplineSegment(int Index, uint SerializedWord0, uint SerializedWord4, uint SerializedWord8,
    float Length, Vector4 CubicCoefficient, Vector4 QuadraticCoefficient, Vector4 LinearCoefficient,
    Vector4 ConstantCoefficient, Vector4 DistanceToParameterCoefficients, int PreviousGlobalSegmentIndex,
    int NextGlobalSegmentIndex, int OwnerSplineResourceId, Vector3 BoundsMinimumSsx, Vector3 BoundsMaximumSsx,
    float CumulativeDistance, uint TailTag, uint TailFlags)
{
    public Vector3 EvaluatePositionSsx(float parameter)
    {
        var value = ((CubicCoefficient * parameter + QuadraticCoefficient) * parameter
            + LinearCoefficient) * parameter + ConstantCoefficient;
        return new(value.X, value.Y, value.Z);
    }

    public float EvaluateParameterAtDistance(float localDistance)
    {
        var c = DistanceToParameterCoefficients;
        return ((c.X * localDistance + c.Y) * localDistance + c.Z) * localDistance + c.W;
    }
}
public sealed record Spline(string Name, SourceByteRange Source, IReadOnlyList<SplinePoint> Points,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3ResourceType = 8;
    public const int SerializedHeaderSize = 48;
    public const int SerializedSegmentStride = 144;
    public const uint SerializedSegmentWord4 = 0x00114A34;
    public const uint SerializedSegmentWord8 = 0x006157CF;
    public const uint SerializedSegmentTailTag = 0x00556D05;
    public const uint SerializedSegmentTailFlags = 15;
    public const int RuntimeLoaderFunction = 0x003AB798;
    public const int RuntimeEvaluateAtDistanceFunction = 0x00345048;
    public const int RuntimeCalculateLengthFunction = 0x003454E8;

    public IReadOnlyList<SplineSegment> Segments { get; init; } = [];
    public float TotalLength => Segments.Count == 0 ? 0f : Segments[^1].CumulativeDistance + Segments[^1].Length;
    public uint SerializedSegmentPointerToken => Properties.TryGetValue("SerializedSegmentPointerToken", out var value)
        ? Convert.ToUInt32(value) : 0;
}
public enum NavigationPathKind { Ai, Track }
public sealed record NavigationPathProperty(uint Kind, byte[] Payload)
{
    public uint? UInt32Value => Payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Payload) : null;
    public float? SingleValue => UInt32Value is uint value ? BitConverter.UInt32BitsToSingle(value) : null;
}
public sealed record NavigationPathPoint(Vector3 EncodedVectorSsx, float Weight, Vector3 Position);
public sealed record NavigationPathEvent(uint Type, uint Value, float StartDistance, float EndDistance)
{
    public int RuntimeKindIndex { get; init; } = -1;
}
public sealed record NavigationPath(string Name, SourceByteRange Source, IReadOnlyList<Vector3> Points,
    IReadOnlyList<NavigationPathEvent> Events, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public NavigationPathKind Kind { get; init; }
    public IReadOnlyList<NavigationPathProperty> TaggedProperties { get; init; } = [];
    public IReadOnlyList<NavigationPathPoint> EncodedPoints { get; init; } = [];
    public uint? AiPathMetadata { get; init; }
    public bool? Respawnable { get; init; }
    public float? DistanceToFinish { get; init; }
    public float TotalLength { get; init; }
}
public sealed record NavigationPathTailPair(uint Word0, uint Word1)
{
    public float PathDistance => BitConverter.UInt32BitsToSingle(Word0);
    public bool IsEmpty => Word0 == 0 && Word1 == 0;
}
public sealed record NavigationPathLink(uint Value, int RawKind, int RuntimeKindIndex,
    Vector3 PositionSsx, Vector3 DirectionSsx, int AiPathIndex, int TrackPathIndex)
{
    public Vector3 Position => Ssx3Coordinates.ToMountainizer(PositionSsx);
    public Vector3 Direction => Ssx3Coordinates.ToMountainizer(DirectionSsx);
}
public sealed record NavigationTableAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<NavigationPath> AiPaths, IReadOnlyList<NavigationPath> TrackPaths,
    IReadOnlyList<NavigationPathTailPair> TailPairs, IReadOnlyList<NavigationPathLink> Links,
    int TrailingBytes, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record TriggerVolume(string Name, SourceByteRange Source, Vector3 Minimum, Vector3 Maximum,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record CameraTriggerShape(int Kind, Vector3 Center, Vector3 HalfExtents,
    Vector3 SerializedExtentsSsx, Vector3 RotationRadiansSsx)
{
    public const int RuntimeEllipseBoundsFunction = 0x001721C0;
    public const int RuntimeEllipseContainmentFunction = 0x00172278;
    public const int RuntimeBoxBoundsFunction = 0x00172840;
    public const int RuntimeBoxContainmentFunction = 0x001728F8;
    public const string RuntimeEllipseInverseTransform =
        "S^-1 * Rz(-rotation.X) * Ry(-rotation.Z) * Rx(-rotation.Y) * T(-center)";
    public const string RuntimeBoxInverseTransform =
        "Rx(-rotation.Y) * Rz(-rotation.X) * T(-center)";
    public const string RuntimeEllipseContainmentEquation =
        "lengthSquared(runtimeVolumePoint) <= 1";
    public const string RuntimeBoxContainmentEquation =
        "-extents <= runtimeLocalPoint <= extents";

    public int RuntimeBoundsFunction => Kind switch
    {
        0 => RuntimeEllipseBoundsFunction,
        1 => RuntimeBoxBoundsFunction,
        _ => throw new InvalidOperationException($"Unknown camera-trigger volume kind {Kind}")
    };

    public int RuntimeContainmentFunction => Kind switch
    {
        0 => RuntimeEllipseContainmentFunction,
        1 => RuntimeBoxContainmentFunction,
        _ => throw new InvalidOperationException($"Unknown camera-trigger volume kind {Kind}")
    };

    public string RuntimeInverseTransform => Kind switch
    {
        0 => RuntimeEllipseInverseTransform,
        1 => RuntimeBoxInverseTransform,
        _ => throw new InvalidOperationException($"Unknown camera-trigger volume kind {Kind}")
    };

    // The retail routines use column-vector transforms. Written as point operations,
    // the rightmost matrix above is applied first. Box rotation.Z is never loaded.
    public Vector3 TransformPointToRuntimeVolumeSpaceSsx(Vector3 pointSsx)
    {
        var point = pointSsx - Ssx3Coordinates.ToSsx3(Center);
        if (Kind == 0)
        {
            point = RotateX(point, -RotationRadiansSsx.Y);
            point = RotateY(point, -RotationRadiansSsx.Z);
            point = RotateZ(point, -RotationRadiansSsx.X);
            return new(point.X / SerializedExtentsSsx.X, point.Y / SerializedExtentsSsx.Y,
                point.Z / SerializedExtentsSsx.Z);
        }
        if (Kind == 1)
        {
            point = RotateZ(point, -RotationRadiansSsx.X);
            return RotateX(point, -RotationRadiansSsx.Y);
        }
        throw new InvalidOperationException($"Unknown camera-trigger volume kind {Kind}");
    }

    public bool ContainsRuntimePointSsx(Vector3 pointSsx)
    {
        var point = TransformPointToRuntimeVolumeSpaceSsx(pointSsx);
        if (Kind == 0)
            return point.LengthSquared() <= 1f;
        return point.X >= -SerializedExtentsSsx.X && point.X <= SerializedExtentsSsx.X
            && point.Y >= -SerializedExtentsSsx.Y && point.Y <= SerializedExtentsSsx.Y
            && point.Z >= -SerializedExtentsSsx.Z && point.Z <= SerializedExtentsSsx.Z;
    }

    private static Vector3 RotateX(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X, value.Y * cosine - value.Z * sine, value.Y * sine + value.Z * cosine);
    }

    private static Vector3 RotateY(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X * cosine + value.Z * sine, value.Y, -value.X * sine + value.Z * cosine);
    }

    private static Vector3 RotateZ(Vector3 value, float radians)
    {
        var cosine = MathF.Cos(radians); var sine = MathF.Sin(radians);
        return new(value.X * cosine - value.Y * sine, value.X * sine + value.Y * cosine, value.Z);
    }
}
public sealed record CameraTriggerBoundObject(int Kind, IReadOnlyList<Vector3> VectorsSsx,
    IReadOnlyList<float> Scalars);
public sealed record CameraTriggerSpline(IReadOnlyList<Vector3> VectorsSsx, IReadOnlyList<float> Scalars);
public sealed record CameraTriggerAction(int Kind, IReadOnlyList<float> Scalars, uint? Value,
    IReadOnlyList<Vector3> VectorsSsx, CameraTriggerBoundObject? BoundObject, CameraTriggerSpline? Spline)
{
    public const int RuntimeDispatchFunction = 0x0016E1D8;
    public const int RuntimeControllerSelectFunction = 0x00162060;
    public const int RuntimeCreateBlendFunction = 0x0015D078;
    public const int RuntimeBoundedCameraAlgorithmId = 0x5B;
    public const int RuntimeSplineCameraSelectionId = 0x5C;
    public const int RuntimeSplineCameraAlgorithmId = RuntimeSplineCameraSelectionId;
    public const int RuntimeSplineCameraObjectAlgorithmId = 0x4B;
    public const int RuntimeBoundedCameraConstructorFunction = 0x00174190;
    public const int RuntimeBoundedCameraInitializeFunction = 0x00174200;
    public const int RuntimeBoundedCameraUpdateFunction = 0x001744B0;
    public const int RuntimeChaseCameraUpdateFunction = 0x001732E8;
    public const int RuntimeCameraAlgorithmMixFunction = 0x0015E668;
    public const int RuntimeSplineCameraConstructorFunction = 0x00161060;
    public const int RuntimeSplineCameraMotionFunction = 0x001613F0;
    public const int RuntimeSplineCameraUpdateFunction = 0x00161630;
    public const float RuntimeFrameSeconds = 1f / 60f;
    public const float RuntimeBoundedElevationScale = 0.8f;
    public const float RuntimeSplineSpeedScale = 0.9f;
    public const int RuntimeSplinePathSampleCount = 101;
    public const int RuntimeSplineArcLengthSegmentCount = RuntimeSplinePathSampleCount - 1;
    public const string RuntimeBlendFractionEquation = "duration == 0 ? 1 : (1 / 60) / duration";
    public const string RuntimeBoundedFocusEquation =
        "focus = targetPosition + worldUpAxis * verticalTargetOffset + targetForward * forwardTargetOffset";
    public const string RuntimeBoundedPitchEquation =
        "elevation = wrapRadians(-(targetElevation * 0.8 + pitchOffsetDegrees * PI / 180))";
    public const string RuntimeBoundedCameraPositionEquation =
        "cameraPosition = focus - viewDirection(elevation, azimuth) * cameraDistance";
    public const string RuntimeBoundedFieldOfViewClampEquation =
        "clamp(fieldOfViewRadians, 0, configuredFieldOfViewRadians)";
    public const string RuntimeSplineControlTimesEquation =
        "controlTimes = [0, durationSeconds / 3, 2 * durationSeconds / 3, durationSeconds]";
    public const string RuntimeSplineApproximateSpeedEquation =
        "approximateSpeed = 0.9 * sum(distance(sample[i], sample[i - 1]), i = 1..100) / durationSeconds";
    public const string RuntimeSplineParameterAdvanceEquation =
        "candidateTime = min(currentTime + 1 / 60, durationSeconds); if distance > 0: adjustedTime = clamp(currentTime + (candidateTime - currentTime) * (approximateSpeed / 60) / distance(splinePosition(candidateTime), cameraPosition), 0, durationSeconds)";
    public const string RuntimeSplineCameraPositionEquation = "cameraPosition = splinePosition(adjustedTime)";
    public const string RuntimeSplineFocusEquation =
        "focus = targetPosition + worldUpAxis * verticalTargetOffset + targetForward * forwardTargetOffset";

    public float? BlendDurationSeconds => Kind is >= 0 and <= 2 && Scalars.Count > 0 ? Scalars[0] : null;
    public float? BoundedCameraDistance => Kind == 1 && Scalars.Count >= 6 ? Scalars[1] : null;
    public float? BoundedFieldOfViewRadians => Kind == 1 && Scalars.Count >= 6 ? Scalars[2] : null;
    public float? BoundedVerticalTargetOffset => Kind == 1 && Scalars.Count >= 6 ? Scalars[3] : null;
    public float? BoundedPitchOffsetDegrees => Kind == 1 && Scalars.Count >= 6 ? Scalars[4] : null;
    public float? BoundedForwardTargetOffset => Kind == 1 && Scalars.Count >= 6 ? Scalars[5] : null;
    public uint? BoundedReferenceMode => Kind == 1 ? Value : null;
    public Vector3? BoundedExplicitReferencePointSsx => Kind == 1 && Value == 1 && VectorsSsx.Count > 0
        ? VectorsSsx[0] : null;
    public float? SplineFieldOfViewRadians => Kind == 2 && Scalars.Count >= 5 ? Scalars[1] : null;
    public float? SplineForwardTargetOffset => Kind == 2 && Scalars.Count >= 5 ? Scalars[2] : null;
    public float? SplineDurationSeconds => Kind == 2 && Scalars.Count >= 5 ? Scalars[3] : null;
    public float? SplineVerticalTargetOffset => Kind == 2 && Scalars.Count >= 5 ? Scalars[4] : null;

    public float? RuntimeBlendFractionPerFrame
    {
        get
        {
            var duration = BlendDurationSeconds;
            if (duration is null) return null;
            return duration.Value == 0 ? 1f : RuntimeFrameSeconds / duration.Value;
        }
    }

    public int? RuntimeCameraAlgorithmId => Kind switch
    {
        0 when Value is uint switchCameraCode => MapSwitchCameraCodeToRuntimeAlgorithm(switchCameraCode),
        1 => RuntimeBoundedCameraAlgorithmId,
        2 => RuntimeSplineCameraAlgorithmId,
        _ => null
    };

    public static int MapSwitchCameraCodeToRuntimeAlgorithm(uint switchCameraCode)
    {
        if (switchCameraCode >= 76) return 0;
        return switchCameraCode switch
        {
            0 or 43 => 0,
            1 => 2,
            2 => 1,
            3 => 4,
            4 => 7,
            5 => 5,
            6 => 6,
            7 => 3,
            8 => 8,
            9 => 10,
            10 => 9,
            33 => 41,
            >= 34 and <= 41 => checked((int)switchCameraCode - 1),
            42 => 76,
            >= 44 => checked((int)switchCameraCode - 2),
            _ => checked((int)switchCameraCode)
        };
    }
}
public sealed record CameraTriggerRecord(uint TriggerId, uint Flags, CameraTriggerShape Shape,
    CameraTriggerAction Action0, CameraTriggerAction Action1, int SerializedOffset, int SerializedSize,
    SourceByteRange Source);
public sealed record CameraTriggerTableAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    int Version, float Scale, uint NextTriggerId, IReadOnlyList<CameraTriggerRecord> Records, int FillWordCount,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record VisibilityCurtain(string Name, SourceByteRange Source, IReadOnlyList<Vector3> Points,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int Ssx3ResourceType = 11;
    public const int SerializedSize = 208;
    public const int RuntimeScratchOffset = 96;
    public const int RuntimeScratchSize = 64;
    public const int RuntimeInsertFunction = 0x00242540;
    public const int RuntimeRemoveFunction = 0x00242570;
    public const int RuntimeSelectFunction = 0x002425C0;
    public const int RuntimeCandidateComparatorFunction = 0x00242978;
    public const int RuntimePrepareFunction = 0x002429B0;
    public const int RuntimeInstallPlaneFunction = 0x00229F88;
    public const int RuntimeMaximumSelectedCurtains = 2;

    public IReadOnlyList<Vector3> CornersSsx { get; init; } = [];
    public Vector4 BoundingSphereSsx { get; init; }
    public Vector4 PlaneSsx { get; init; }
    public SceneBounds Bounds { get; init; }
    public uint LoadedFlag { get; init; }

    public Vector3 BoundingSphereCenterSsx => new(BoundingSphereSsx.X, BoundingSphereSsx.Y, BoundingSphereSsx.Z);
    public float RuntimeCandidateScore(Vector3 viewerPositionSsx) =>
        Vector3.DistanceSquared(viewerPositionSsx, BoundingSphereCenterSsx);
    public float RuntimeViewerPlaneValue(Vector3 viewerPositionSsx) =>
        Vector3.Dot(viewerPositionSsx, new Vector3(PlaneSsx.X, PlaneSsx.Y, PlaneSsx.Z)) + PlaneSsx.W;
    public bool PassesRuntimeViewerPlaneTest(Vector3 viewerPositionSsx) => RuntimeViewerPlaneValue(viewerPositionSsx) <= 0;
}
public sealed record UnknownSection(string Name, SourceByteRange Source, int ResourceType, int TrackId,
    int ResourceId, byte[] PreviewBytes, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record CollisionTriangleBatch(int FirstTriangle, int TriangleCount, SceneBounds Bounds);
public sealed record CollisionSubmesh(IReadOnlyList<Vector3> Vertices, IReadOnlyList<uint> Indices,
    IReadOnlyList<Vector3> FaceNormals, IReadOnlyList<CollisionTriangleBatch> TriangleBatches,
    byte[] IndexPadding, byte[] TriangleBatchPadding)
{
    public IReadOnlyList<SceneBounds> Bounds => TriangleBatches.Select(batch => batch.Bounds).ToArray();
}
public sealed record CollisionAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<CollisionSubmesh> Submeshes, byte[] RuntimeScratchHeader, byte[] SubmeshPointerScratch,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record SphereTreeLevel(float MaximumRadius, float MinimumRadius, uint Capacity);
public sealed record SphereTreeNodeLevel(int Depth, uint Capacity, byte[] ChildMasks,
    int ReferencedNodeCount, int ReferencedChildCount, bool IsTerminal);
public sealed record SphereTreeRecord(Vector3 Correction, Vector3 RetainedMetadataVector,
    IReadOnlyList<float> RetainedSymmetricMatrix, IReadOnlyList<float> RetainedInverseSymmetricMatrix, IReadOnlyList<SphereTreeLevel> Levels,
    byte[] PackedNodePayload, byte[] RecordPadding, uint CompressionType, byte[] DecodedNodeMasks,
    IReadOnlyList<SphereTreeNodeLevel> NodeLevels)
{
    public int PackedPayloadSize => PackedNodePayload.Length;
    public int AlignmentBytes => RecordPadding.Length;
    public uint HeaderValue => CompressionType;
    public bool RetainedMatrixMetadataConsumedByRetailRuntime => false;
    public byte[] PackedNodeStorage => [.. PackedNodePayload, .. RecordPadding];
}
public sealed record SphereTreeCollisionAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<SphereTreeRecord> Trees, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public readonly record struct SoundTriggerBindingIdentity(uint Word0, uint Word1)
{
    public ulong PackedLittleEndian => Word0 | (ulong)Word1 << 32;
    public static SoundTriggerBindingIdentity FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(name));
        return new(BinaryPrimitives.ReadUInt32LittleEndian(digest),
            BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)));
    }
    public bool MatchesName(string name) => this == FromName(name);
    public override string ToString() => $"{Word1:X8}{Word0:X8}";
}
public sealed record SoundTriggerBinding(uint Key0, uint Key1, int ObjectTrackId, int ObjectResourceId, int BlockIndex)
{
    public SoundTriggerBindingIdentity SerializedIdentity => new(Key0, Key1);
    public SoundTriggerBindingIdentity NameHashIdentity => SerializedIdentity;
    public PackedObjectReference? AnchorObjectReference => ObjectTrackId < 0 || ObjectResourceId < 0
        ? null : new PackedObjectReference(ObjectTrackId, ObjectResourceId);
}
public enum SoundTriggerInfoKind
{
    BuiltInSlot,
    IndexedBankSound,
    NamedAudioEvent,
    CrowdInstanceActivation
}
public sealed record SoundTriggerInfoDefinition(uint Id, SoundTriggerInfoKind Kind, string? Name = null,
    uint? SoundBankId = null, uint? SoundIndex = null, string? SoundBankName = null);
public sealed record SoundTriggerRandomizedBankSoundDefinition(uint SoundBankId,
    IReadOnlyList<uint> CandidateSoundIndices, IReadOnlyList<int> ExplicitBucketWidths,
    uint SelectionIgnoredWord, int TotalWeight)
{
    public uint SelectSoundIndexForScaledWeight(int scaledWeight)
    {
        if (CandidateSoundIndices.Count != 4 || ExplicitBucketWidths.Count != 3)
            throw new InvalidOperationException("Randomized bank sound requires four candidates and three explicit bucket widths");
        var threshold = ExplicitBucketWidths[0];
        if (scaledWeight < threshold) return CandidateSoundIndices[0];
        threshold = checked(threshold + ExplicitBucketWidths[1]);
        if (scaledWeight < threshold) return CandidateSoundIndices[1];
        threshold = checked(threshold + ExplicitBucketWidths[2]);
        return scaledWeight < threshold ? CandidateSoundIndices[2] : CandidateSoundIndices[3];
    }
}
public enum SoundTriggerFalloffCurve
{
    OneMinusDistanceSquared = 0,
    OneMinusDistanceOverOnePointFiveMinusHalfDistance = 1,
    OneMinusDistance = 2,
    OneMinusDistanceOverOnePlusHalfDistance = 3,
    SquaredOneMinusDistance = 4,
    OuterThirtyPercentLinear = 5
}
public sealed record SoundTriggerSpatialDescriptor(uint Kind, uint TriggerInfoId, Vector3 Position,
    Vector3 SerializedPositionSsx, IReadOnlyList<float> Parameters, int SerializedSize)
{
    public float? Radius { get; init; }
    public Vector3? SemiAxisLengths { get; init; }
    public Vector3? OrientationAxis { get; init; }
    public Vector3? SerializedOrientationAxisSsx { get; init; }
    public SoundTriggerFalloffCurve? DistanceFalloffCurve { get; init; }
    public float? ConeCosineThreshold { get; init; }
    public SoundTriggerFalloffCurve? AngularFalloffCurve { get; init; }
}
public sealed record SoundTriggerBlock(int RelativeOffset, byte[] Data, IReadOnlyList<uint> TriggerInfoIds,
    IReadOnlyList<SoundTriggerSpatialDescriptor> SpatialDescriptors, int SerializedSize)
{
    public IReadOnlyList<uint> SharedTriggerInfoIds => TriggerInfoIds;
}
public sealed record SoundTriggerTableAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<SoundTriggerBinding> Bindings, IReadOnlyList<SoundTriggerBlock> Blocks,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record PlanarRouteSample(Vector2 LateralNormal, Vector2 Position, float Distance)
{
    // Kept as a compatibility alias for callers written before the retail HUD trace
    // established that this vector is perpendicular to route travel, not tangent to it.
    public Vector2 Tangent => LateralNormal;
}
public sealed record PlanarRouteMarker(uint Kind, float Distance);
public sealed record PlanarRouteAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    float TotalLength, IReadOnlyList<PlanarRouteSample> Samples, IReadOnlyList<PlanarRouteMarker> Markers,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int RuntimeLoaderFunction = 0x002105D0;
    public const int RuntimeCursorFunction = 0x00210618;
    public const int RuntimeLateralProjectionFunction = 0x00210820;
    public const int RuntimeOnePlayerRadarFunction = 0x0020EDA0;
    public const float RuntimeRadarHalfWindow = 5_000f;
    public const float RuntimeRadarWindow = RuntimeRadarHalfWindow * 2f;

    public int SelectRuntimeSampleIndex(float courseDistance)
    {
        if (Samples.Count == 0)
            throw new InvalidOperationException("A planar route has no samples.");
        if (!float.IsFinite(courseDistance))
            throw new ArgumentOutOfRangeException(nameof(courseDistance));
        if (courseDistance <= Samples[0].Distance)
            return 0;
        if (courseDistance >= Samples[^1].Distance)
            return Samples.Count - 1;

        var low = 0;
        var high = Samples.Count - 1;
        while (low + 1 < high)
        {
            var middle = low + (high - low) / 2;
            if (Samples[middle].Distance <= courseDistance)
                low = middle;
            else
                high = middle;
        }
        return low;
    }

    public PlanarRouteRuntimeProjection ProjectRuntimePosition(Vector2 riderPosition, float courseDistance)
    {
        if (!float.IsFinite(riderPosition.X) || !float.IsFinite(riderPosition.Y))
            throw new ArgumentOutOfRangeException(nameof(riderPosition));
        var sampleIndex = SelectRuntimeSampleIndex(courseDistance);
        var sample = Samples[sampleIndex];
        var lateralOffset = Vector2.Dot(riderPosition - sample.Position, sample.LateralNormal);
        return new(sampleIndex, courseDistance, sample.Distance, lateralOffset);
    }
}
public sealed record PlanarRouteRuntimeProjection(int SampleIndex, float CourseDistance,
    float SampleDistance, float LateralOffset);
public sealed record StructuredTableSection(string Name, int Offset, int Count, int ElementSize, byte[] Data);
public sealed record PackedRailReference(uint PackedValue, int TrackId, int RailId);
public sealed record RailReferenceSet(int Index, IReadOnlyList<PackedRailReference?> Slots);
public sealed record PackedProgramReference(uint PackedValue, int TrackId, int ProgramIndex);
public sealed record ModifierProgramBlock(int Index, uint ControlWord,
    IReadOnlyList<PackedProgramReference?> ModifierSlots);
public sealed record ModifierProgramGroup(int Index, uint ControlWord, int BlockCount, int FirstBlockIndex,
    IReadOnlyList<PackedProgramReference?> ProgramReferences,
    IReadOnlyList<PackedProgramReference?> ModifierSlots, uint Kind);
public enum LunOperation
{
    Unknown,
    Jump,
    JumpIfTruthy,
    JumpIfFalsy,
    Equal,
    NotEqual,
    GreaterThanOrEqual,
    GreaterThan,
    LessThanOrEqual,
    LessThan,
    LogicalOr,
    LogicalAnd,
    Add,
    Subtract,
    Multiply,
    Divide,
    UnsupportedBinaryOperation,
    Remainder,
    MapSet,
    CopySlot,
    StoreType3Immediate,
    StoreIntegerImmediate,
    StoreFloatImmediate,
    MapGet,
    CreateMap,
    NoOp,
    CallRoutine,
    MapSetAndIncrementKey,
    StoreReferencePairImmediate,
    ClearSlot,
    ReturnSlot,
    AppendSlotArgument,
    CallNative,
    LogicalNot,
    Negate,
    ForLoopAdvance,
    AppendIntegerImmediateArgument,
    AppendFloatImmediateArgument,
    AppendU8IntegerArgument,
    AppendU8FloatArgument,
    End
}
public sealed record LunInstruction(int Offset, byte Opcode, byte Operand0, byte Operand1, byte Operand2,
    uint? ImmediateWord, int SerializedSize, LunOperation Operation,
    byte? DestinationSlot, byte? NativeFunctionId, byte? ArgumentCount, string? NativeFunctionName,
    string? NativeFunctionSubsystem);
public sealed record LunRoutine(int Index, int Offset, IReadOnlyList<LunInstruction> Instructions);
public sealed record LunRoutineDescriptor(int Index, int Offset, int RelativeProgramOffset, int ProgramOffset,
    uint EntryWordOffset, uint LocalSlotCount, uint ArgumentCapacity);
public sealed record LunProgramRecord(int Index, int Offset, int ProgramLength, int DeclaredSize,
    byte[] Program, int BytecodeLength, IReadOnlyList<LunRoutine> Routines,
    IReadOnlyList<LunInstruction> Instructions, LunRoutineDescriptor PrimaryDescriptor,
    IReadOnlyList<LunRoutineDescriptor> AdditionalDescriptors, int PaddingBytes);
public sealed record RailProgramDescriptor(uint Word0, uint Word1, uint Word2,
    float Scalar0, float Scalar1, ushort Low, ushort High,
    RailSplineRole Role, SsxSurfaceType? SurfaceOverride);
public sealed record RailProgramRecord(int Index, int Offset, int SerializedSize, uint Kind, uint ControlWord,
    ushort ControlLow, ushort ControlHigh,
    int? GeneratedRailId, PackedRailReference? GeneratedRailReference,
    PackedRailReference? PrimaryRailReference, PackedRailReference? SecondaryRailReference,
    IReadOnlyList<RailProgramDescriptor> Descriptors)
{
    public PackedRailReference? PrimaryInputRailReference => PrimaryRailReference;
    public PackedRailReference? SecondaryInputRailReference => SecondaryRailReference;
    public int InputRailCount => (PrimaryRailReference is null ? 0 : 1) + (SecondaryRailReference is null ? 0 : 1);
    public IReadOnlyList<RailProgramDescriptor> OutputDescriptors => Descriptors;
}
public enum RailSplineRole : ushort
{
    NonRailMotionPath = 0,
    SpecialRail = 1,
    HandplantRail = 2,
    GrindRail = 3
}
public enum SsxSurfaceType : ushort
{
    PackedSnow = 0,
    LooseSnow = 1,
    PowderSnow = 2,
    DeepPowderSnow = 3,
    Ice = 4,
    Water = 5,
    Slush = 6,
    Rock = 7,
    Gravel = 8,
    Wood = 9,
    Metal = 10,
    Glass = 11,
    Sand = 12,
    Impassable = 13,
    BlackIce = 14,
    Concrete = 15,
    RidableRock = 16,
    Pavement = 17,
    WipeoutRock = 18
}
public sealed record RailSplineMetadataEntry(uint PackedValue, ushort Low, ushort High,
    RailSplineRole Role, SsxSurfaceType Surface);
public sealed record WorldModifierRecord(int TypeId, int Index, int Offset, IReadOnlyList<uint> Words,
    IReadOnlyList<float> ScalarValues, IReadOnlyList<string> Tags, byte[] Data,
    int? ReferencedResourceType, int? ReferencedTrackId, int? ReferencedResourceId)
{
    public float WorldPainterBlendControl => ScalarValues[0];
    public int WorldPainterPropertyCount => Words.Count - 1;
    public float? FogDensity => TypeId == 5 ? ScalarValues[1] : null;
    public float? FogNearPlane => TypeId == 5 ? ScalarValues[2] : null;
    public float? FogFarPlane => TypeId == 5 ? ScalarValues[3] : null;
    public Vector3? FogColour => TypeId == 5 ? new(ScalarValues[4], ScalarValues[5], ScalarValues[6]) : null;
    public float? LightGlowPS2MinimumIntensityCutoff => TypeId == 6 ? ScalarValues[1] : null;
    public float? LightGlowPS2PostCutoffScale => TypeId == 6 ? ScalarValues[2] : null;
    public float? LightGlowPS2CopyIntensity => TypeId == 6 ? ScalarValues[3] : null;
    public float? LightGlowPS2FrameSourceIntensity => TypeId == 6 ? ScalarValues[4] : null;
    public float? LightGlowPS2FrameBlendIntensity => TypeId == 6 ? ScalarValues[5] : null;
    public float? LightGlowPS2BlendTexture2 => TypeId == 6 ? ScalarValues[6] : null;
    public float? LightGlowPS2BlendTexture3 => TypeId == 6 ? ScalarValues[7] : null;
    public Vector3? ScreenTintColour => TypeId == 7 ? new(ScalarValues[1], ScalarValues[2], ScalarValues[3]) : null;
    public Vector3? ScreenFillColour => TypeId == 7 ? new(ScalarValues[4], ScalarValues[5], ScalarValues[6]) : null;
    public Vector3? SunColour => TypeId == 9 ? new(ScalarValues[3], ScalarValues[4], ScalarValues[5]) : null;
    public float? WeatherSnowfallIntensity => TypeId == 12 ? ScalarValues[1] : null;
    public float? WeatherSnowflakeSize => TypeId == 12 ? ScalarValues[2] : null;
    public float? WeatherSnowfallWind => TypeId == 12 ? ScalarValues[3] : null;
    public float? WeatherWindRotation => TypeId == 12 ? ScalarValues[4] : null;
    public float? WeatherSnowFlurries => TypeId == 12 ? ScalarValues[7] : null;
    public float? WeatherSnowGravityMultiplier => TypeId == 12 ? ScalarValues[8] : null;
    public float? WeatherSnowFluffIntensity => TypeId == 12 ? ScalarValues[9] : null;
    public float? WeatherSnowFluffWindIntensity => TypeId == 12 ? ScalarValues[10] : null;
    public float? WeatherLightningChance => TypeId == 12 ? ScalarValues[11] : null;
    public Vector3? WeatherSnowFlakeColour => TypeId == 12 ? new(ScalarValues[13], ScalarValues[14], ScalarValues[15]) : null;
    public Vector4? WeatherSnowFlakeColourRgba => TypeId == 12
        ? new(ScalarValues[13], ScalarValues[14], ScalarValues[15], ScalarValues[12]) : null;
    public Vector3? WeatherSnowFluffColour => TypeId == 12 ? new(ScalarValues[17], ScalarValues[18], ScalarValues[19]) : null;
    public Vector4? WeatherSnowFluffColourRgba => TypeId == 12
        ? new(ScalarValues[17], ScalarValues[18], ScalarValues[19], ScalarValues[16]) : null;
}
public enum WorldModifierIndexEntryKind { EmptyLeaf, Branch, RecordLeaf }
public enum WorldModifierSpatialQuadrant { LowXLowY, LowXHighY, HighXLowY, HighXHighY }
public sealed record WorldModifierIndexChild(WorldModifierSpatialQuadrant Quadrant, ushort Handle, int EntryIndex);
public sealed record WorldModifierIndexEntry(int Index, int SerializedIndex, WorldModifierIndexEntryKind Kind,
    uint Word0, uint Word1, ushort Word0Low, ushort Word0High, ushort Word1Low, ushort Word1High,
    int? ModifierRecordIndex, IReadOnlyList<WorldModifierIndexChild> Children)
{
    public IReadOnlyList<int> ChildEntryIndices => Children.Select(child => child.EntryIndex).ToArray();
}
public sealed record WorldModifierSpatialIndex(float Scale, Vector2 Origin, int EntryCount,
    int SerializedCapacity, ushort RootHandle, ushort Reserved, uint DefaultLeafWord0, uint DefaultLeafWord1,
    uint SerializedNodePointerPlaceholder, uint SerializedNodeEndPointerPlaceholder, int RootEntryIndex,
    IReadOnlyList<WorldModifierIndexEntry> Entries)
{
    public float Extent => 32768f / Scale;
}
public sealed record WorldModifierSection(int Slot, int TypeId, string TypeName, int Offset, int HeaderSize,
    int RecordCount, int IndexRecordSize, WorldModifierSpatialIndex SpatialIndex, byte[] IndexData,
    IReadOnlyList<WorldModifierRecord> Records);
public sealed record StructuredTableAsset(string Name, SourceByteRange Source, int ResourceType, int TrackId, int ResourceId,
    IReadOnlyList<StructuredTableSection> Sections, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public IReadOnlyList<PackedRailReference> RootRailReferences { get; init; } = [];
    public IReadOnlyList<RailReferenceSet> RailReferenceSets { get; init; } = [];
    public IReadOnlyList<ModifierProgramBlock> ModifierProgramBlocks { get; init; } = [];
    public IReadOnlyList<ModifierProgramGroup> ModifierProgramGroups { get; init; } = [];
    public IReadOnlyList<LunProgramRecord> LunPrograms { get; init; } = [];
    public IReadOnlyList<RailProgramRecord> RailProgramRecords { get; init; } = [];
    public IReadOnlyList<ushort> RailProgramReferenceIndices { get; init; } = [];
    public IReadOnlyList<RailSplineMetadataEntry> RailSplineMetadataEntries { get; init; } = [];
    public IReadOnlyList<WorldModifierSection> ModifierSections { get; init; } = [];
    public IReadOnlyList<WorldModifierSection> WorldPainterSections => ModifierSections;
}
public sealed record BnklPatch(int Offset, int PayloadOffset, byte Opcode, string Name, uint? Value, byte[] Data);
public sealed record BnklEnvelopeSegment(int Offset, uint DurationHundredths, double DurationSeconds, int Volume)
{
    public int RuntimeDurationHundredths => DurationHundredths > int.MaxValue ? int.MaxValue : (int)DurationHundredths;
    public double RuntimeDurationSeconds => RuntimeDurationHundredths / 100d;
    public int RuntimeTargetVolumeFixed16 => Volume << 16;
}
public sealed record BnklSoundInfoSection(int Offset, int EndOffset, byte Terminator,
    IReadOnlyList<BnklPatch> Patches, int MinimumVelocity, int MaximumVelocity,
    int MinimumMidiNote, int MaximumMidiNote, int RootMidiNote, int ReleaseEnvelopeSegmentIndex,
    int PlaybackEnvelopeSegmentCount, int? PlaybackEnvelopeOffset, int InitialEnvelopeVolume,
    IReadOnlyList<BnklEnvelopeSegment> PlaybackEnvelopeSegments,
    int? StreamVersion, int? BitsPerSample, int ChannelCount,
    int Codec, int SampleRate, int SampleCount, int? LoopStart, int? LoopEnd, uint? MicroTalkLoopRelativeOffset, uint Flags,
    IReadOnlyList<uint> ChannelOffsets)
{
    public int RuntimeInitialVolumeFixed16 => InitialEnvelopeVolume << 16;
    public bool UsesPcmCorrectionBlocks => Codec == 4 && StreamVersion >= 3;
    public int MicroTalkFrameCount => SampleCount <= 0 ? 0 : checked((SampleCount + 431) / 432);
    public bool MatchesRuntimeLayerSelection(int midiNote, int velocity) =>
        midiNote >= MinimumMidiNote && midiNote <= MaximumMidiNote
        && velocity >= MinimumVelocity && velocity <= MaximumVelocity;
}
public sealed record BnklSoundEntry(int Slot, int TableEntryOffset, uint RelativeOffset, int HeaderOffset,
    int Platform, IReadOnlyList<BnklSoundInfoSection> InfoSections, int SerializedSize);
public sealed record BnklBankAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId, int Version,
    int EntryCount, IReadOnlyList<uint> ReservedWords, IReadOnlyList<uint> SlotRelativeOffsets,
    int BodyOffset, IReadOnlyList<BnklSoundEntry> Sounds, byte[] Body, IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record AvalancheFrame(Vector3 Position, Vector3 SerializedPositionSsx,
    Vector3 Scale, Vector3 SerializedScaleSsx, Vector3 RotationAxis, Vector3 SerializedRotationAxisSsx, float RotationAngleRadians,
    sbyte DeltaX, sbyte DeltaY, sbyte DeltaZ, byte ScaleX, byte ScaleY, byte ScaleZ,
    sbyte RotationAxisX, sbyte RotationAxisY, sbyte RotationAxisZ, byte RotationAngle)
{
    // Position is the accumulated end position after this interval. The serialized
    // signed bytes are world-space translation increments, not absolute offsets.
    public Vector3 SerializedTranslationDeltaSsx => new(DeltaX * 2f, DeltaY * 2f, DeltaZ * 2f);
    public Vector3 TranslationDelta => Ssx3Coordinates.ToMountainizer(SerializedTranslationDeltaSsx);
    public float RotationAngleIncrementRadians => RotationAngleRadians;
}
public sealed record AvalancheMetadataPair(ushort BlockIndex, ushort FrameIndex)
{
    public float TriggerTimeSeconds => FrameIndex / 30f;
}
public sealed record AvalancheMetadataParameter(float TimeSeconds, uint PackedReference,
    int ObjectTrackId, int ObjectResourceId)
{
    public PackedObjectReference SerializedTargetReference => new(ObjectTrackId, ObjectResourceId);
}
public sealed record AvalancheDataBlock(int Offset, int UnitCount, Vector3 Origin, Vector3 SerializedOriginSsx,
    IReadOnlyList<AvalancheFrame> Frames, byte[] Data)
{
    // N frame records describe N scale samples but only N-1 transform intervals;
    // the retail sampler clamps to the final endpoint at frame N-1.
    public float RuntimeDurationSeconds => Math.Max(0, Frames.Count - 1) / 30f;
}
public sealed record AvalancheMetadataSegment(int Offset, ushort ParameterCount, ushort PairCount,
    IReadOnlyList<AvalancheMetadataParameter> Parameters, IReadOnlyList<AvalancheMetadataPair> Pairs, byte[] Data);
public sealed record AvalancheRuntimeTransform(float TimeSeconds, float FrameTime, int FrameIndex, float FrameFraction,
    Vector3 Position, Vector3 SerializedPositionSsx, Vector3 Scale, Vector3 SerializedScaleSsx,
    Quaternion Rotation, Quaternion SerializedRotationSsx);
public sealed record AvalancheAnimationAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<AvalancheDataBlock> Blocks, IReadOnlyList<AvalancheMetadataSegment> MetadataSegments,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;
public sealed record ParticleElement(Vector3 Position, Vector3 Color, float Size);
public sealed record ParticleModelAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    SceneBounds Bounds, IReadOnlyList<ParticleElement> Elements, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int SourceResourceType = 4;
    public const int SerializedHeaderSize = 84;
    public const int SerializedElementStride = 28;
    public const string RuntimeManagerClass = "cPS2FogParticleMan";
    public const int RuntimeCompileFunction = 0x002DC190;
    public const int RuntimeSubmitFunction = 0x002DBF98;
    public const int RuntimeGsPrimRegister = 0x56;
    public const string RuntimePrimitive = "Textured alpha-blended sprite (STQ)";
    public const float RuntimeGsColorScale = 128f;
    public const string RuntimeTextureArchive = "data/textures/particle.ssh";
    public const string RuntimeTextureName = "FOG";
    public const string RuntimeTextureAssetId = "fog0";
    public const int RuntimeTextureEnumIndex = 4;
    public const int RuntimeBlendSelector = 5;
    public const ulong RuntimeGsAlphaRegister = 0x44;
    public const string RuntimeBlendEquation = "(SourceColor - DestinationColor) * SourceAlpha + DestinationColor";
    public const string RuntimeBlendMode = "Source-alpha interpolation";
    public const int RuntimeNearFadeStartDepth = 500;
    public const float RuntimeNearFadeEndDepth = 2499.66748046875f;
    public const float RuntimeNearFadeReciprocal = 0.0005000831442885101f;
    public const int RuntimeFarFadeStartDepth = 18_000;
    public const int RuntimeFarCullDepth = 25_000;
    public const float RuntimeFarFadeReciprocal = 0.0001428571413271129f;

    // The retail compiler converts projected depth to an integer before applying
    // these fades. The coarse instance fade and per-element near fade multiply.
    public static float EvaluateRuntimeNearDepthFade(int projectedDepth) => projectedDepth switch
    {
        <= RuntimeNearFadeStartDepth => 0f,
        _ when projectedDepth < RuntimeNearFadeEndDepth
            => (projectedDepth - RuntimeNearFadeStartDepth) * RuntimeNearFadeReciprocal,
        _ => 1f
    };

    public static float EvaluateRuntimeFarDepthFade(int projectedDepth) => projectedDepth switch
    {
        < RuntimeFarFadeStartDepth => 1f,
        >= RuntimeFarCullDepth => 0f,
        _ => (RuntimeFarCullDepth - projectedDepth) * RuntimeFarFadeReciprocal
    };

    public static float EvaluateRuntimeAlpha(int instanceProjectedDepth, int elementProjectedDepth) =>
        EvaluateRuntimeFarDepthFade(instanceProjectedDepth) * EvaluateRuntimeNearDepthFade(elementProjectedDepth);

    // Type 4 is referenced by the paired Type-5 world instance and has no loader-created
    // octree object of its own.
    public bool HasIndependentRuntimeWorldObject => false;
}
public sealed record ParticleEmitterAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<Vector3> OrientationAxes, Vector3 Position, Vector3 BoundingCenter, float BoundingRadius,
    SceneBounds Bounds, int ModelTrackId, int ModelResourceId, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int SourceResourceType = 5;
    public const int SerializedSize = 144;
    public const int RuntimeObjectType = 7;
    public const int RuntimeLoaderFunction = 0x003ABBB0;
    public const int RuntimeVisibilityFunction = 0x0022A270;
    public const int RuntimeQueueRenderFunction = 0x0022C708;
    public const int RuntimeParticleCompileFunction = ParticleModelAsset.RuntimeCompileFunction;
    public const int RuntimeCoarseFadeStartDepth = ParticleModelAsset.RuntimeFarFadeStartDepth;
    public const int RuntimeCoarseCullDepth = ParticleModelAsset.RuntimeFarCullDepth;
    public const int SerializedTransformOffset = 16;
    public const int SerializedModelReferenceOffset = 96;
    public const int RuntimeResolvedModelPointerOffset = 100;
    public const int SerializedBoundsOffset = 104;
    public const bool RuntimeConsumesSerializedBoundingRadius = false;
    public const string RuntimeBoundingRadiusUse = "Serialized authoring metadata; not read by the retail loader, visibility, or fog renderer";
}
public sealed record LightAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId, uint Flags, int Kind,
    float Intensity, float SelectionWeight, float Range, Vector3 Color, Vector3 Direction, Vector3 Position, SceneBounds? Bounds,
    float SpotInnerConeCosine, float SpotOuterConeCosine, sbyte DistanceFalloffExponent, sbyte AngularFalloffExponent,
    ushort TailMarker, bool IsPlaceholder, IReadOnlyList<uint> RawWords, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int RuntimeLoaderFunction = 0x003AB918;
    public const int RuntimeAdmissionPredicate = 0x003AACA8;
    public const int RuntimeInternalResourceType = 6;
    public const float NearFalloffDistance = 100f;
    public const float LongRangeFadeStart = 3750f;
    public const float LongRangeFadeEnd = 5000f;
    public const ushort ExpectedTailMarker = 0x4039;

    public bool HasRuntimeFilterFlag0x100 => (Flags & 0x100) != 0;
    // The Type-6 loader calls 0x003AACA8 before assigning internal type 6 or inserting the
    // record into the world index. Retail admits only unflagged Spot/Point records.
    public bool IsRuntimeAdmitted => !HasRuntimeFilterFlag0x100 && Kind is 1 or 2;
    public string RuntimeAdmissionOutcome => IsRuntimeAdmitted
        ? "Admitted as an internal Type-6 spatial light"
        : HasRuntimeFilterFlag0x100
            ? "Rejected by serialized flag 0x100"
            : Kind switch
            {
                0 => "Directional authoring record rejected before runtime registration",
                3 => "Ambient authoring record rejected before runtime registration",
                _ => "Unsupported light kind rejected before runtime registration"
            };

    // Mirrors cWorldLightMan's 0x002F5D30 top-N ranking score. SelectionWeight is deliberately
    // absent from the RGB contribution path at 0x0038A6A8.
    public float EvaluateRuntimeSelectionScore(Vector3 queryPosition) => Kind switch
    {
        0 => SelectionWeight,
        1 or 2 => EvaluateUnclampedLocalIntensity(queryPosition) * SelectionWeight,
        _ => 0f
    };

    // Mirrors the local point/spot contribution scalar at 0x0038A6A8, before multiplication by RGB.
    public float EvaluateRuntimeLocalIntensity(Vector3 queryPosition) =>
        Math.Clamp(EvaluateUnclampedLocalIntensity(queryPosition), 0f, 5f);

    private float EvaluateUnclampedLocalIntensity(Vector3 queryPosition)
    {
        if (Kind is not (1 or 2)) return 0f;
        var offset = queryPosition - Position;
        var distance = offset.Length();
        if (distance > Range) return 0f;

        var farFade = Range < LongRangeFadeEnd || distance <= LongRangeFadeStart
            ? 1f
            : 1f - (distance - LongRangeFadeStart) / (LongRangeFadeEnd - LongRangeFadeStart);
        var distanceBase = distance > NearFalloffDistance ? NearFalloffDistance / distance : 1f;
        var value = Intensity * farFade * PowInteger(distanceBase, DistanceFalloffExponent);
        if (Kind == 2) return value;

        var coneDot = distance == 0f ? 0f : Vector3.Dot(offset / distance, Direction);
        if (coneDot < SpotOuterConeCosine) return 0f;
        if (coneDot >= SpotInnerConeCosine)
            return value * PowInteger(coneDot, AngularFalloffExponent);
        var coneBlend = (coneDot - SpotOuterConeCosine) / (SpotInnerConeCosine - SpotOuterConeCosine);
        return value * PowInteger(SpotInnerConeCosine, AngularFalloffExponent) * coneBlend;
    }

    private static float PowInteger(float value, int exponent)
    {
        if (exponent == 0) return 1f;
        if (value == 0f) return 0f;
        var power = exponent;
        var factor = value;
        if (power < 0) { power = -power; factor = 1f / factor; }
        var result = 1f;
        while (power != 0)
        {
            if ((power & 1) != 0) result *= factor;
            power >>= 1;
            if (power != 0) factor *= factor;
        }
        return result;
    }
}
public sealed record HaloAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId, Vector3 Color,
    Vector3 Position, SceneBounds Bounds, float Radius, float HalfExtent, uint SerializedCollectionPointerToken, uint VisualModeCode,
    uint SerializedEntryPointerToken, IReadOnlyList<uint> RawWords, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const uint InvariantWord08 = 0xBE9FFAB8;
    public const int SerializedEntryStride = 8;
    public uint SerializedEntryTableBasePointerToken =>
        SerializedEntryPointerToken - checked((uint)ResourceId * SerializedEntryStride);
    public float RuntimeOcclusionProbeScale => VisualModeCode switch
    {
        0x10 => 100f,
        0x20 => 80f,
        0x40 => 200f,
        _ => 0f
    };
    public float RuntimeRenderScale => VisualModeCode switch
    {
        0x10 => 180f,
        0x20 => 100f,
        0x40 => 350f,
        _ => 0f
    };
    public Vector3 RuntimeColorDirection => Color.LengthSquared() > 0f ? Vector3.Normalize(Color) : Vector3.Zero;
    public string RuntimeTextureName => VisualModeCode switch
    {
        0x10 or 0x40 => "SHALO",
        0x20 => "MHALO",
        _ => "Unknown"
    };
    public string RuntimeTextureAssetId => VisualModeCode switch
    {
        0x10 or 0x40 => "shal",
        0x20 => "mhal",
        _ => string.Empty
    };
    public const float RuntimeGsColorScale = 128f;
    public const string RuntimeAlphaSource = "Double-buffered occlusion visibility fraction";
    public const int RuntimeBlendSelector = 7;
    public const ulong RuntimeGsAlphaRegister = 0x48;
    public const string RuntimeBlendEquation = "(SourceColor - 0) * SourceAlpha + DestinationColor";
    public const string RuntimeBlendMode = "Source-alpha additive";
}
public sealed record PackedObjectReference(int TrackId, int ResourceId, int TargetResourceType = 3);
public sealed record NisScriptObjectSlot(int Index, PackedObjectReference? ObjectReference, string? ObservedRole,
    IReadOnlyList<int> RuntimeCommandIds)
{
    public bool IsPopulated => ObjectReference is not null;
    public bool IsRuntimeAddressable => RuntimeCommandIds.Count != 0;
}
public sealed record NisScriptObjectTableAsset(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyList<NisScriptObjectSlot> Slots, IReadOnlyDictionary<string, object?> Properties) : ISceneItem
{
    public const int ResourceType = 18;
}
public sealed record NavigationTableMarker(string Name, SourceByteRange Source, int TrackId, int ResourceId,
    IReadOnlyDictionary<string, object?> Properties) : ISceneItem;

public readonly record struct SceneBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;
    public float Radius => Vector3.Distance(Minimum, Maximum) * 0.5f;
    public bool IsEmpty => Minimum == Maximum;

    public static SceneBounds FromPoints(IEnumerable<Vector3> points)
    {
        using var e = points.GetEnumerator();
        if (!e.MoveNext()) return new(Vector3.Zero, Vector3.Zero);
        var min = e.Current;
        var max = e.Current;
        while (e.MoveNext()) { min = Vector3.Min(min, e.Current); max = Vector3.Max(max, e.Current); }
        return new(min, max);
    }
}

public static class Ssx3Coordinates
{
    // SSX 3 stores world data with +Z as up. Mountainizer/OpenGL uses +Y as up.
    // This is a right-handed +90 degree rotation around X: (x, y, z) -> (x, z, -y).
    public static Vector3 ToMountainizer(Vector3 value) => new(value.X, value.Z, -value.Y);
    public static Vector3 ToSsx3(Vector3 value) => new(value.X, -value.Z, value.Y);

    // MDR vertices remain in SSX-local coordinates, so an instance transform only
    // needs the world-space conversion appended to it.
    public static Matrix4x4 ToMountainizerWorldTransform(Matrix4x4 value) => value * new Matrix4x4(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);
}

public static class SceneTransforms
{
    public static Matrix4x4 NormalMatrix(Matrix4x4 transform) =>
        Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;

    public static Vector3 TransformNormal(Vector3 normal, Matrix4x4 normalMatrix)
    {
        var transformed = Vector3.TransformNormal(normal, normalMatrix);
        return transformed.LengthSquared() > 0.000001f
            && float.IsFinite(transformed.X) && float.IsFinite(transformed.Y) && float.IsFinite(transformed.Z)
            ? Vector3.Normalize(transformed)
            : Vector3.UnitY;
    }
}
