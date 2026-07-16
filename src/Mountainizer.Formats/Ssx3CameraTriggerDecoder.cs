using System.Numerics;
using Mountainizer.Core;

namespace Mountainizer.Formats;

public static class Ssx3CameraTriggerDecoder
{
    private const int HeaderSize = 16;
    private const int MaximumTriggers = 10_000;
    private const uint FillWord = 0xDEADBEEF;

    public static string VolumeKindName(int kind) => kind switch
    {
        0 => "Ellipse",
        1 => "Box",
        _ => "Unknown"
    };

    public static string ActionKindName(int kind) => kind switch
    {
        0 => "Switch Camera",
        1 => "Bounded Camera",
        2 => "Spline Camera",
        3 => "None",
        _ => "Unknown"
    };

    public static string BoundKindName(int kind) => kind switch
    {
        0 => "Ellipse",
        1 => "Box",
        2 => "Line",
        3 => "Point",
        _ => "Unknown"
    };

    public static string TriggerFlagNames(uint flags)
    {
        var names = new List<string>(2);
        if ((flags & 1) != 0) names.Add("Replay");
        if ((flags & 2) != 0) names.Add("InGame");
        return names.Count == 0 ? "None" : string.Join(" | ", names);
    }

    public static CameraTriggerTableAsset Decode(ReadOnlySpan<byte> data, SourceByteRange source, int trackId, int resourceId)
    {
        if (data.Length < HeaderSize)
            throw new FormatException("Camera-trigger table is truncated", source.LogicalOffset ?? 0, HeaderSize, data.Length);
        var reader = new BinarySpanReader(data, source.LogicalOffset ?? 0);
        var version = checked((int)reader.ReadUInt32Little()); var scale = reader.ReadSingleLittle();
        var triggerCount = checked((int)reader.ReadUInt32Little()); var nextTriggerId = reader.ReadUInt32Little();
        if (version != 7 || !float.IsFinite(scale) || scale <= 0 || triggerCount is < 0 or > MaximumTriggers
            || (triggerCount > 0 && nextTriggerId == 0) || (uint)triggerCount > nextTriggerId)
            throw new FormatException("Camera-trigger header is inconsistent", source.LogicalOffset ?? 0, HeaderSize, data.Length);

        var records = new CameraTriggerRecord[triggerCount]; var ids = new HashSet<uint>();
        for (var i = 0; i < records.Length; i++)
        {
            var recordOffset = reader.Position; var triggerId = reader.ReadUInt32Little(); var flags = reader.ReadUInt32Little();
            if (triggerId >= nextTriggerId || !ids.Add(triggerId) || (flags & ~3u) != 0)
                throw new FormatException($"Camera trigger {i} has an invalid ID or flags", reader.AbsolutePosition - 8, 8, reader.Remaining + 8);
            var shape = ReadShape(ref reader, i); var action0 = ReadAction(ref reader, i, 0); var action1 = ReadAction(ref reader, i, 1);
            var recordSize = reader.Position - recordOffset;
            var recordSource = source with
            {
                SectionName = $"{source.SectionName}/camera trigger {triggerId}", OriginalIndex = i,
                LogicalOffset = (source.LogicalOffset ?? 0) + recordOffset, SourceLength = recordSize,
                Confidence = SupportConfidence.Medium
            };
            records[i] = new(triggerId, flags, shape, action0, action1, recordOffset, recordSize, recordSource);
        }

        if ((reader.Remaining & 3) != 0)
            throw new FormatException("Camera-trigger fill is not word aligned", reader.AbsolutePosition, 4, reader.Remaining);
        var fillWordCount = reader.Remaining / 4;
        for (var i = 0; i < fillWordCount; i++)
            if (reader.ReadUInt32Little() != FillWord)
                throw new FormatException("Camera-trigger trailing fill is unknown", reader.AbsolutePosition - 4, 4, 4);

        var properties = new Dictionary<string, object?>
        {
            ["ParsedType"] = "SSX3 Camera Trigger Table", ["TrackId"] = trackId, ["ResourceId"] = resourceId,
            ["Version"] = version, ["Scale"] = scale, ["TriggerCount"] = records.Length,
            ["NextTriggerId"] = nextTriggerId, ["MissingTriggerIds"] = nextTriggerId - (uint)records.Length,
            ["VolumeKinds"] = Summarize(records.Select(x => VolumeKindName(x.Shape.Kind))),
            ["TriggerFlags"] = Summarize(records.Select(x => TriggerFlagNames(x.Flags))),
            ["ActionKinds"] = Summarize(records.SelectMany(x => new[] { ActionKindName(x.Action0.Kind), ActionKindName(x.Action1.Kind) })),
            ["BoundKinds"] = Summarize(records.SelectMany(x => new[] { x.Action0.BoundObject, x.Action1.BoundObject })
                .Where(x => x is not null).Select(x => BoundKindName(x!.Kind))),
            ["RuntimeEllipseBoundsFunction"] = $"0x{CameraTriggerShape.RuntimeEllipseBoundsFunction:X8}",
            ["RuntimeEllipseContainmentFunction"] = $"0x{CameraTriggerShape.RuntimeEllipseContainmentFunction:X8}",
            ["RuntimeEllipseInverseTransform"] = CameraTriggerShape.RuntimeEllipseInverseTransform,
            ["RuntimeEllipseContainmentEquation"] = CameraTriggerShape.RuntimeEllipseContainmentEquation,
            ["RuntimeBoxBoundsFunction"] = $"0x{CameraTriggerShape.RuntimeBoxBoundsFunction:X8}",
            ["RuntimeBoxContainmentFunction"] = $"0x{CameraTriggerShape.RuntimeBoxContainmentFunction:X8}",
            ["RuntimeBoxInverseTransform"] = CameraTriggerShape.RuntimeBoxInverseTransform,
            ["RuntimeBoxContainmentEquation"] = CameraTriggerShape.RuntimeBoxContainmentEquation,
            ["RuntimeBoxConsumesSerializedRotationZ"] = false,
            ["RuntimeActionDispatchFunction"] = $"0x{CameraTriggerAction.RuntimeDispatchFunction:X8}",
            ["RuntimeCameraControllerSelectFunction"] = $"0x{CameraTriggerAction.RuntimeControllerSelectFunction:X8}",
            ["RuntimeCreateCameraBlendFunction"] = $"0x{CameraTriggerAction.RuntimeCreateBlendFunction:X8}",
            ["RuntimeActionBlendFractionEquation"] = CameraTriggerAction.RuntimeBlendFractionEquation,
            ["RuntimeBoundedCameraAlgorithmId"] = $"0x{CameraTriggerAction.RuntimeBoundedCameraAlgorithmId:X2}",
            ["RuntimeBoundedCameraConstructorFunction"] = $"0x{CameraTriggerAction.RuntimeBoundedCameraConstructorFunction:X8}",
            ["RuntimeBoundedCameraInitializeFunction"] = $"0x{CameraTriggerAction.RuntimeBoundedCameraInitializeFunction:X8}",
            ["RuntimeBoundedCameraUpdateFunction"] = $"0x{CameraTriggerAction.RuntimeBoundedCameraUpdateFunction:X8}",
            ["RuntimeBoundedFocusEquation"] = CameraTriggerAction.RuntimeBoundedFocusEquation,
            ["RuntimeBoundedPitchEquation"] = CameraTriggerAction.RuntimeBoundedPitchEquation,
            ["RuntimeBoundedCameraPositionEquation"] = CameraTriggerAction.RuntimeBoundedCameraPositionEquation,
            ["RuntimeBoundedFieldOfViewClampEquation"] = CameraTriggerAction.RuntimeBoundedFieldOfViewClampEquation,
            ["RuntimeSplineCameraAlgorithmId"] = $"0x{CameraTriggerAction.RuntimeSplineCameraAlgorithmId:X2}",
            ["RuntimeSplineCameraObjectAlgorithmId"] = $"0x{CameraTriggerAction.RuntimeSplineCameraObjectAlgorithmId:X2}",
            ["RuntimeSplineCameraConstructorFunction"] = $"0x{CameraTriggerAction.RuntimeSplineCameraConstructorFunction:X8}",
            ["RuntimeSplineCameraMotionFunction"] = $"0x{CameraTriggerAction.RuntimeSplineCameraMotionFunction:X8}",
            ["RuntimeSplineCameraUpdateFunction"] = $"0x{CameraTriggerAction.RuntimeSplineCameraUpdateFunction:X8}",
            ["RuntimeSplineControlTimesEquation"] = CameraTriggerAction.RuntimeSplineControlTimesEquation,
            ["RuntimeSplineApproximateSpeedEquation"] = CameraTriggerAction.RuntimeSplineApproximateSpeedEquation,
            ["RuntimeSplineParameterAdvanceEquation"] = CameraTriggerAction.RuntimeSplineParameterAdvanceEquation,
            ["RuntimeSplineCameraPositionEquation"] = CameraTriggerAction.RuntimeSplineCameraPositionEquation,
            ["RuntimeSplineFocusEquation"] = CameraTriggerAction.RuntimeSplineFocusEquation,
            ["SerializedRecordBytes"] = records.Sum(x => x.SerializedSize), ["FillWordCount"] = fillWordCount,
            ["PayloadSize"] = data.Length
        };
        return new($"Camera Trigger Table {trackId}:{resourceId}", source with { Confidence = SupportConfidence.Medium },
            trackId, resourceId, version, scale, nextTriggerId, records, fillWordCount, properties);
    }

    public static IReadOnlyList<TriggerVolume> CreateDebugVolumes(CameraTriggerTableAsset table, int firstIndex)
    {
        var result = new TriggerVolume[table.Records.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var record = table.Records[i]; var shape = record.Shape;
            var debugSource = record.Source with { OriginalIndex = firstIndex + i };
            result[i] = new($"Camera Trigger {firstIndex + i:D4} (ID {record.TriggerId})", debugSource,
                shape.Center - shape.HalfExtents, shape.Center + shape.HalfExtents,
                new Dictionary<string, object?>
                {
                    ["ParsedType"] = "SSX3 Camera Trigger Volume", ["TrackId"] = table.TrackId,
                    ["ResourceId"] = table.ResourceId, ["TriggerId"] = record.TriggerId, ["Flags"] = record.Flags,
                    ["FlagNames"] = TriggerFlagNames(record.Flags),
                    ["VolumeKind"] = VolumeKindName(shape.Kind), ["Center"] = shape.Center,
                    ["HalfExtents"] = shape.HalfExtents, ["SerializedExtentsSsx"] = shape.SerializedExtentsSsx,
                    ["RotationRadiansSsx"] = shape.RotationRadiansSsx,
                    ["RuntimeBoundsFunction"] = $"0x{shape.RuntimeBoundsFunction:X8}",
                    ["RuntimeContainmentFunction"] = $"0x{shape.RuntimeContainmentFunction:X8}",
                    ["RuntimeInverseTransform"] = shape.RuntimeInverseTransform,
                    ["RuntimeContainsCenter"] = shape.ContainsRuntimePointSsx(Ssx3Coordinates.ToSsx3(shape.Center)),
                    ["RuntimeConsumesSerializedRotationZ"] = shape.Kind == 0,
                    ["Action0"] = ActionSummary(record.Action0), ["Action1"] = ActionSummary(record.Action1),
                    ["Action0BlendDurationSeconds"] = record.Action0.BlendDurationSeconds,
                    ["Action0RuntimeBlendFractionPerFrame"] = record.Action0.RuntimeBlendFractionPerFrame,
                    ["Action0RuntimeCameraAlgorithmId"] = record.Action0.RuntimeCameraAlgorithmId,
                    ["Action0BoundedCameraDistance"] = record.Action0.BoundedCameraDistance,
                    ["Action0BoundedFieldOfViewRadians"] = record.Action0.BoundedFieldOfViewRadians,
                    ["Action0BoundedVerticalTargetOffset"] = record.Action0.BoundedVerticalTargetOffset,
                    ["Action0BoundedPitchOffsetDegrees"] = record.Action0.BoundedPitchOffsetDegrees,
                    ["Action0BoundedForwardTargetOffset"] = record.Action0.BoundedForwardTargetOffset,
                    ["Action0BoundedReferenceMode"] = record.Action0.BoundedReferenceMode,
                    ["Action0BoundedExplicitReferencePointSsx"] = record.Action0.BoundedExplicitReferencePointSsx,
                    ["Action0SplineFieldOfViewRadians"] = record.Action0.SplineFieldOfViewRadians,
                    ["Action0SplineForwardTargetOffset"] = record.Action0.SplineForwardTargetOffset,
                    ["Action0SplineDurationSeconds"] = record.Action0.SplineDurationSeconds,
                    ["Action0SplineVerticalTargetOffset"] = record.Action0.SplineVerticalTargetOffset,
                    ["Action1BlendDurationSeconds"] = record.Action1.BlendDurationSeconds,
                    ["Action1RuntimeBlendFractionPerFrame"] = record.Action1.RuntimeBlendFractionPerFrame,
                    ["Action1RuntimeCameraAlgorithmId"] = record.Action1.RuntimeCameraAlgorithmId,
                    ["Action1BoundedCameraDistance"] = record.Action1.BoundedCameraDistance,
                    ["Action1BoundedFieldOfViewRadians"] = record.Action1.BoundedFieldOfViewRadians,
                    ["Action1BoundedVerticalTargetOffset"] = record.Action1.BoundedVerticalTargetOffset,
                    ["Action1BoundedPitchOffsetDegrees"] = record.Action1.BoundedPitchOffsetDegrees,
                    ["Action1BoundedForwardTargetOffset"] = record.Action1.BoundedForwardTargetOffset,
                    ["Action1BoundedReferenceMode"] = record.Action1.BoundedReferenceMode,
                    ["Action1BoundedExplicitReferencePointSsx"] = record.Action1.BoundedExplicitReferencePointSsx,
                    ["Action1SplineFieldOfViewRadians"] = record.Action1.SplineFieldOfViewRadians,
                    ["Action1SplineForwardTargetOffset"] = record.Action1.SplineForwardTargetOffset,
                    ["Action1SplineDurationSeconds"] = record.Action1.SplineDurationSeconds,
                    ["Action1SplineVerticalTargetOffset"] = record.Action1.SplineVerticalTargetOffset,
                    ["TableVersion"] = table.Version, ["TableScale"] = table.Scale,
                    ["SerializedOffset"] = record.SerializedOffset, ["SerializedSize"] = record.SerializedSize,
                    ["HasNegativeSerializedExtents"] = shape.SerializedExtentsSsx.X < 0
                        || shape.SerializedExtentsSsx.Y < 0 || shape.SerializedExtentsSsx.Z < 0,
                    ["DebugBoundsIgnoreRotation"] = shape.RotationRadiansSsx != Vector3.Zero
                });
        }
        return result;
    }

    private static CameraTriggerShape ReadShape(ref BinarySpanReader reader, int triggerIndex)
    {
        var kind = checked((int)reader.ReadUInt32Little());
        if (kind is not (0 or 1))
            throw new FormatException($"Camera trigger {triggerIndex} has unknown volume kind {kind}", reader.AbsolutePosition - 4, 4, reader.Remaining + 4);
        var centerSsx = reader.ReadVector3(); var halfExtentsSsx = reader.ReadVector3(); var rotationSsx = reader.ReadVector3();
        if (!IsFinite(centerSsx) || !IsFinite(halfExtentsSsx) || !IsFinite(rotationSsx)
            || halfExtentsSsx.X == 0 || halfExtentsSsx.Y == 0 || halfExtentsSsx.Z == 0)
            throw new FormatException($"Camera trigger {triggerIndex} has invalid volume geometry", reader.AbsolutePosition - 36, 36, reader.Remaining + 36);
        var center = Ssx3Coordinates.ToMountainizer(centerSsx);
        var halfExtents = Vector3.Abs(Ssx3Coordinates.ToMountainizer(halfExtentsSsx));
        return new(kind, center, halfExtents, halfExtentsSsx, rotationSsx);
    }

    private static CameraTriggerAction ReadAction(ref BinarySpanReader reader, int triggerIndex, int actionIndex)
    {
        var kind = checked((int)reader.ReadUInt32Little());
        if (kind is < 0 or > 3)
            throw new FormatException($"Camera trigger {triggerIndex} action {actionIndex} has unknown kind {kind}", reader.AbsolutePosition - 4, 4, reader.Remaining + 4);
        var scalars = new List<float>(); var vectors = new List<Vector3>(); uint? value = null;
        CameraTriggerBoundObject? boundObject = null; CameraTriggerSpline? spline = null;
        switch (kind)
        {
            case 0:
                scalars.Add(ReadFinite(ref reader, triggerIndex, actionIndex)); value = reader.ReadUInt32Little();
                break;
            case 1:
                for (var i = 0; i < 6; i++) scalars.Add(ReadFinite(ref reader, triggerIndex, actionIndex));
                value = reader.ReadUInt32Little(); vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
                boundObject = ReadBoundObject(ref reader, triggerIndex, actionIndex);
                break;
            case 2:
                for (var i = 0; i < 5; i++) scalars.Add(ReadFinite(ref reader, triggerIndex, actionIndex));
                var splineVectors = new List<Vector3>(6) { ReadFiniteVector(ref reader, triggerIndex, actionIndex), ReadFiniteVector(ref reader, triggerIndex, actionIndex) };
                var splineScalars = new List<float>(3);
                for (var i = 0; i < 3; i++) splineScalars.Add(ReadFinite(ref reader, triggerIndex, actionIndex));
                for (var i = 0; i < 4; i++) splineVectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
                spline = new(splineVectors, splineScalars);
                break;
        }
        return new(kind, scalars, value, vectors, boundObject, spline);
    }

    private static CameraTriggerBoundObject ReadBoundObject(ref BinarySpanReader reader, int triggerIndex, int actionIndex)
    {
        var kind = checked((int)reader.ReadUInt32Little());
        if (kind is < 0 or > 3)
            throw new FormatException($"Camera trigger {triggerIndex} action {actionIndex} has unknown bound kind {kind}", reader.AbsolutePosition - 4, 4, reader.Remaining + 4);
        var vectors = new List<Vector3>(); var scalars = new List<float>();
        if (kind is 0 or 1 or 2)
        {
            vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
            vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
            for (var i = 0; i < 3; i++) scalars.Add(ReadFinite(ref reader, triggerIndex, actionIndex));
            if (kind == 2)
            {
                vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
                vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
            }
        }
        else vectors.Add(ReadFiniteVector(ref reader, triggerIndex, actionIndex));
        return new(kind, vectors, scalars);
    }

    private static float ReadFinite(ref BinarySpanReader reader, int triggerIndex, int actionIndex)
    {
        var value = reader.ReadSingleLittle();
        if (!float.IsFinite(value))
            throw new FormatException($"Camera trigger {triggerIndex} action {actionIndex} contains a non-finite scalar", reader.AbsolutePosition - 4, 4, reader.Remaining + 4);
        return value;
    }

    private static Vector3 ReadFiniteVector(ref BinarySpanReader reader, int triggerIndex, int actionIndex)
    {
        var value = reader.ReadVector3();
        if (!IsFinite(value))
            throw new FormatException($"Camera trigger {triggerIndex} action {actionIndex} contains a non-finite vector", reader.AbsolutePosition - 12, 12, reader.Remaining + 12);
        return value;
    }

    private static string ActionSummary(CameraTriggerAction action)
    {
        var suffix = action.BoundObject is not null ? $" / {BoundKindName(action.BoundObject.Kind)} bound"
            : action.Spline is not null ? " / trigger spline" : string.Empty;
        return ActionKindName(action.Kind) + suffix;
    }

    private static string Summarize(IEnumerable<string> values) => string.Join(", ", values.GroupBy(x => x)
        .OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count()}"));
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
