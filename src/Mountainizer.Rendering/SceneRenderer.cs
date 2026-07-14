using System.Numerics;
using Mountainizer.Core;
using OpenTK.Graphics.OpenGL4;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using Vector3Tk = OpenTK.Mathematics.Vector3;

namespace Mountainizer.Rendering;

public static class TextureCoordinateConvention
{
    public static Vector2 TerrainToOpenGl(Vector2 uv) => new(uv.X, 1f - uv.Y);
    public static Vector2 TerrainToOpenGl(Vector2 uv, int textureResourceId) =>
        // Ramp patches use adjacent left/right atlas halves. Flip the transverse
        // coordinate while turning the longitudinal axis so the two halves meet
        // at the seam instead of appearing mirrored at the outside edges.
        IsRampTerrainTexture(textureResourceId) ? new(1f - uv.Y, 1f - uv.X) : TerrainToOpenGl(uv);
    public static Vector2 ModelToOpenGl(Vector2 uv) => uv;

    // Left, centre, and right tiles of the orange-arrow snow ramps.
    public static bool IsRampTerrainTexture(int resourceId) =>
        resourceId is 109 or 112 or 114 or 235 or 238 or 241 or 378 or 383 or 384;
}

public sealed class InspectionCamera
{
    private float _navigationScale = 5_000;
    public const float FieldOfView = 0.72f;
    public Vector3 Target { get; private set; }
    public float Distance { get; private set; } = 50_000;
    public float Yaw { get; private set; } = -0.8f;
    public float Pitch { get; private set; } = -0.65f;
    public float MoveSpeed => Math.Max(250, Math.Max(Distance * 0.3f, _navigationScale * 0.5f));

    public Vector3 Position => Target - Forward * Distance;
    public Vector3 Forward => Vector3.Normalize(new(MathF.Cos(Pitch) * MathF.Cos(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Sin(Yaw)));
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    public void Frame(SceneBounds bounds)
    {
        Target = bounds.Center; Distance = Math.Max(bounds.Radius * 2.9f, 100); _navigationScale = Math.Max(bounds.Radius, 500); Yaw = -0.8f; Pitch = -0.65f;
    }
    public void SetOrbitPivot(Vector3 pivot)
    {
        var position = Position;
        var direction = pivot - position;
        var distance = direction.Length();
        if (distance < 0.01f) return;
        direction /= distance;
        Target = pivot;
        Distance = distance;
        Pitch = Math.Clamp(MathF.Asin(direction.Y), -1.52f, 1.52f);
        Yaw = MathF.Atan2(direction.Z, direction.X);
    }
    public void SetPose(Vector3 position, Vector3 target, float navigationScale = 0)
    {
        var direction = target - position;
        var distance = direction.Length();
        if (distance < 0.01f) return;
        direction /= distance;
        Target = target;
        Distance = distance;
        _navigationScale = navigationScale > 0 ? Math.Max(navigationScale, 500) : Math.Max(distance * 0.2f, 500);
        Pitch = Math.Clamp(MathF.Asin(direction.Y), -1.52f, 1.52f);
        Yaw = MathF.Atan2(direction.Z, direction.X);
    }
    public void Rotate(float dx, float dy) { Yaw += dx * 0.0022f; Pitch = Math.Clamp(Pitch - dy * 0.0022f, -1.52f, 1.52f); }
    public void Pan(float dx, float dy, float viewportHeight)
    {
        var unitsPerPixel = 2f * Distance * MathF.Tan(FieldOfView * 0.5f) / Math.Max(viewportHeight, 1);
        Target += (-Right * dx + Up * dy) * unitsPerPixel;
    }
    public void Zoom(float wheelSteps, float speed = 1) => Distance = Math.Max(0.25f, Distance * MathF.Exp(-wheelSteps * 0.08f * speed));
    public void Fly(float right, float up, float forward, float elapsedSeconds, float multiplier = 1)
    {
        var direction = Right * right + Vector3.UnitY * up + Forward * forward;
        if (direction.LengthSquared() > 1) direction = Vector3.Normalize(direction);
        Target += direction * MoveSpeed * Math.Clamp(elapsedSeconds, 0, 0.1f) * multiplier;
    }
    public Matrix4 View => Matrix4.LookAt(ToTk(Position), ToTk(Target), ToTk(Up));
    public Matrix4 Projection(float aspect) => Matrix4.CreatePerspectiveFieldOfView(FieldOfView, Math.Max(aspect, 0.01f), Math.Max(0.05f, Distance / 20000f), Math.Max(1_000_000f, Distance * 20));
    private static Vector3Tk ToTk(Vector3 x) => new(x.X, x.Y, x.Z);
}

public readonly record struct CourseCameraPose(Vector3 Position, Vector3 Target, bool UsedStartGate, float ReferenceScale);

public static class InspectionFrustum
{
    public static bool Contains(InspectionCamera camera, SceneBounds bounds, float aspect)
    {
        var radius = bounds.Radius; var offset = bounds.Center - camera.Position;
        var forward = Vector3.Dot(offset, camera.Forward);
        var near = Math.Max(0.05f, camera.Distance / 20000f); var far = Math.Max(1_000_000f, camera.Distance * 20f);
        if (forward + radius < near || forward - radius > far) return false;
        var halfVertical = Math.Max(forward, 0) * MathF.Tan(InspectionCamera.FieldOfView * 0.5f);
        var halfHorizontal = halfVertical * Math.Max(aspect, 0.01f);
        return MathF.Abs(Vector3.Dot(offset, camera.Up)) <= halfVertical + radius
            && MathF.Abs(Vector3.Dot(offset, camera.Right)) <= halfHorizontal + radius;
    }
}

public static class InspectionPicking
{
    public static bool RayIntersectsMesh(Vector3 origin, Vector3 direction, MeshData mesh, out float distance)
    {
        const float epsilon = 0.00001f;
        distance = float.PositiveInfinity; var hit = false;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var ia = mesh.Indices[i]; var ib = mesh.Indices[i + 1]; var ic = mesh.Indices[i + 2];
            if (ia >= mesh.Positions.Count || ib >= mesh.Positions.Count || ic >= mesh.Positions.Count) continue;
            var a = mesh.Positions[(int)ia]; var edge1 = mesh.Positions[(int)ib] - a; var edge2 = mesh.Positions[(int)ic] - a;
            var p = Vector3.Cross(direction, edge2); var determinant = Vector3.Dot(edge1, p);
            if (MathF.Abs(determinant) < epsilon) continue;
            var inverse = 1f / determinant; var fromA = origin - a; var u = Vector3.Dot(fromA, p) * inverse;
            if (u < -epsilon || u > 1 + epsilon) continue;
            var q = Vector3.Cross(fromA, edge1); var v = Vector3.Dot(direction, q) * inverse;
            if (v < -epsilon || u + v > 1 + epsilon) continue;
            var candidate = Vector3.Dot(edge2, q) * inverse;
            if (candidate < 0 || candidate >= distance) continue;
            distance = candidate; hit = true;
        }
        return hit;
    }
}

public static class CourseCameraPlacement
{
    public static bool TryFind(MountainizerScene scene, string courseCode, out CourseCameraPose pose)
    {
        var named = scene.Props.Where(x => x.Name.Contains(courseCode, StringComparison.OrdinalIgnoreCase)).ToArray();
        var gateStarts = named.Where(x => ContainsAny(x.Name, "startgate", "start_gate")).ToArray();
        var buildingStarts = named.Where(x => x.Name.Contains("start_bldg", StringComparison.OrdinalIgnoreCase)).ToArray();
        var starts = gateStarts.Length > 0 ? gateStarts : buildingStarts.Length > 0 ? buildingStarts : named.Where(x => x.Name.Contains("start", StringComparison.OrdinalIgnoreCase)
            && !x.Name.Contains("finish", StringComparison.OrdinalIgnoreCase)).ToArray();

        var usedStartGate = starts.Length > 0;
        Vector3 start;
        PropInstance[] clusteredStarts = [];
        if (usedStartGate)
        {
            start = Average(DensestCluster(starts.Select(Position).ToArray(), 5000));
            clusteredStarts = starts.Where(x => Vector3.DistanceSquared(Position(x), start) <= 5000 * 5000).ToArray();
        }
        else
        {
            var patchCenters = scene.Terrain.Select(PatchCenter).Where(IsFinite).ToArray();
            if (patchCenters.Length == 0) { pose = default; return false; }
            var topCount = Math.Max(1, patchCenters.Length / 200);
            start = Average(patchCenters.OrderByDescending(x => x.Y).Take(topCount));
        }

        var finishes = named.Where(x => x.Name.Contains("finish", StringComparison.OrdinalIgnoreCase)).Select(Position).ToArray();
        var destination = finishes.Length > 0 ? Average(DensestCluster(finishes, 5000)) : scene.Bounds.Center;
        var forward = Horizontal(destination - start);

        var gateForward = Horizontal(Average(clusteredStarts.Select(x => new Vector3(x.Transform.M21, x.Transform.M22, x.Transform.M23))));
        if (gateForward.LengthSquared() > 0.0001f)
        {
            gateForward = Vector3.Normalize(gateForward);
            var hasPlusHeight = TryTerrainHeight(scene, start + gateForward * 10000, out var plusHeight);
            var hasMinusHeight = TryTerrainHeight(scene, start - gateForward * 10000, out var minusHeight);
            if (hasPlusHeight && hasMinusHeight && Math.Abs(plusHeight - minusHeight) > 100)
            {
                if (plusHeight > minusHeight) gateForward = -gateForward;
            }
            else if (forward.LengthSquared() > 0.0001f && Vector3.Dot(gateForward, forward) < 0) gateForward = -gateForward;
            forward = gateForward;
        }

        if (forward.LengthSquared() < 0.0001f)
        {
            var lower = scene.Terrain.Select(PatchCenter).Where(x => IsFinite(x) && x.Y < start.Y - 100).ToArray();
            if (lower.Length > 0) forward = Horizontal(Average(lower) - start);
        }
        if (forward.LengthSquared() < 0.0001f) forward = Vector3.UnitZ;
        else forward = Vector3.Normalize(forward);

        var structure = StartStructureMetrics(scene, clusteredStarts, start, forward);
        var referenceScale = structure.Scale;
        var cameraHeight = Math.Clamp(referenceScale * 0.25f, 1200, 3000);
        var gateClearance = Math.Max(1500, referenceScale * 0.2f);
        var forwardOffset = Math.Clamp(structure.MaximumForward + gateClearance, 5000, 15000);
        var lookAhead = Math.Clamp(referenceScale * 3f, 12000, 25000);
        var position = start + forward * forwardOffset;
        if (TryTerrainHeight(scene, position, out var cameraGround)) position.Y = cameraGround + cameraHeight;
        else position.Y = start.Y + cameraHeight;
        var target = position + forward * lookAhead - Vector3.UnitY * Math.Min(cameraHeight * 0.45f, lookAhead * 0.08f);
        pose = new(position, target, usedStartGate, referenceScale);
        return true;
    }

    private readonly record struct StructureMetrics(float Scale, float MaximumForward);
    private static StructureMetrics StartStructureMetrics(MountainizerScene scene, IReadOnlyList<PropInstance> starts, Vector3 origin, Vector3 forward)
    {
        var hasPoint = false; var minimum = Vector3.Zero; var maximum = Vector3.Zero; var maximumForward = 0f;
        foreach (var prop in starts)
        {
            var model = scene.Models.FirstOrDefault(x => x.Mesh is not null
                && Convert.ToInt32(x.Properties["TrackId"]) == prop.ModelTrackId
                && Convert.ToInt32(x.Properties["ResourceId"]) == prop.ModelResourceId);
            if (model?.Mesh is null) continue;
            foreach (var local in model.Mesh.Positions)
            {
                var point = Vector3.Transform(local, prop.Transform);
                if (!IsFinite(point)) continue;
                maximumForward = Math.Max(maximumForward, Vector3.Dot(point - origin, forward));
                if (!hasPoint) { minimum = maximum = point; hasPoint = true; }
                else { minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point); }
            }
        }
        if (!hasPoint) return new(2500, 0);
        var size = maximum - minimum;
        return new(Math.Max(500, Math.Max(size.Y, Math.Max(size.X, size.Z) * 0.5f)), maximumForward);
    }

    private static Vector3 Position(PropInstance prop) => new(prop.Transform.M41, prop.Transform.M42, prop.Transform.M43);
    private static Vector3 PatchCenter(TerrainPatch patch) => patch.ControlPoints.Count > 0
        ? Average(patch.ControlPoints) : SceneBounds.FromPoints(patch.Mesh.Positions).Center;
    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0, value.Z);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool TryTerrainHeight(MountainizerScene scene, Vector3 position, out float height)
    {
        var bestDistance = float.PositiveInfinity; height = 0; var found = false;
        foreach (var patch in scene.Terrain)
        foreach (var point in patch.ControlPoints.Count > 0 ? patch.ControlPoints : patch.Mesh.Positions)
        {
            var dx = point.X - position.X; var dz = point.Z - position.Z; var distance = dx * dx + dz * dz;
            if (distance >= bestDistance) continue;
            bestDistance = distance; height = point.Y; found = true;
        }
        return found;
    }
    private static bool ContainsAny(string value, params string[] terms) => terms.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static Vector3 Average(IEnumerable<Vector3> values)
    {
        var sum = Vector3.Zero; var count = 0;
        foreach (var value in values) { sum += value; count++; }
        return count == 0 ? Vector3.Zero : sum / count;
    }
    private static IReadOnlyList<Vector3> DensestCluster(IReadOnlyList<Vector3> points, float radius)
    {
        if (points.Count <= 1) return points;
        var radiusSquared = radius * radius;
        IReadOnlyList<Vector3> best = [points[0]];
        foreach (var center in points)
        {
            var cluster = points.Where(x => Vector3.DistanceSquared(x, center) <= radiusSquared).ToArray();
            if (cluster.Length > best.Count) best = cluster;
        }
        return best;
    }
}

public sealed class SceneRenderer : IDisposable
{
    private sealed record PickTarget(ISceneItem Item, SceneBounds Bounds, MeshData? Mesh, Matrix4x4 InverseTransform);
    private readonly record struct ModelRange(int Offset, int Count, int TextureHandle);
    private readonly record struct ModelInstance(Matrix4 Transform, int PropIndex, PropRenderCategory Category);
    private sealed class ModelBatch(ModelRange range)
    {
        public ModelRange Range { get; } = range;
        public List<ModelInstance> Instances { get; } = [];
    }
    private readonly record struct VisibleBatch(ModelBatch Batch, int MatrixOffset, int InstanceCount);
    private readonly record struct VisibleSetKey(Vector3 Target, float Distance, float Yaw, float Pitch, float Aspect,
        uint PropCategoryMask, int VisibilityRevision, int IsolatedPropIndex);

    private MountainizerScene? _pendingScene;
    private MountainizerScene? _scene;
    private int _program, _vao, _vbo, _ibo, _gridVao, _gridVbo, _propVao, _propVbo, _modelVao, _modelVbo, _modelIbo, _modelInstanceVbo, _debugVao, _debugVbo, _axisVao, _axisVbo;
    private int _uView, _uProjection, _uModel, _uColor, _uTexture, _uLightmap, _uUseTexture, _uUseLightmap, _uAlphaTest, _uInstanced;
    private int _indexCount, _gridVertexCount, _splineVertexCount, _curtainVertexCount, _triggerVertexCount;
    private int _lightVertexCount, _haloVertexCount, _particleEmitterVertexCount;
    private readonly List<(int Offset, int Count)> _patchRanges = [];
    private readonly List<(int Offset, int Count, int TextureHandle, int LightmapHandle)> _drawRanges = [];
    private readonly List<(int PropIndex, int Offset, PropRenderCategory Category)> _propPointEntries = [];
    private readonly Dictionary<TextureAsset, int> _textureHandles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, List<ModelRange>> _modelRanges = [];
    private readonly List<ModelBatch> _modelBatches = [];
    private readonly List<Matrix4> _visibleModelTransforms = [];
    private readonly List<VisibleBatch> _visibleModelBatches = [];
    private List<ModelRange>[] _propModelRanges = [];
    private SceneBounds[] _propBounds = [];
    private readonly List<PickTarget> _pickTargets = [];
    private readonly Dictionary<long, ModelAsset> _modelsByResource = [];
    private readonly Dictionary<PropInstance, int> _propIndices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ISceneItem, SceneBounds> _boundsByItem = new(ReferenceEqualityComparer.Instance);
    private Matrix4 _lastModel;
    private Matrix4 _lastView, _lastProjection;
    private Vector3Tk _lastColor;
    private bool _hasLastModel;
    private bool _hasLastMatrices;
    private bool _hasLastColor;
    private int _lastUseTexture = -1, _lastUseLightmap = -1, _lastAlphaTest = -1, _lastInstanced = -1, _boundTexture = -1, _boundLightmap = -1;
    private VisibleSetKey _visibleSetKey;
    private bool _visibleSetValid;
    private uint _visiblePropCategoryMask = 1u << (int)PropRenderCategory.Visual;
    private readonly HashSet<int> _hiddenPropIndices = [];
    private int _visibilityRevision;
    private int _isolatedPropIndex = -1;
    private int _selectedPropIndex = -1;
    private bool _initialized;
    public InspectionCamera Camera { get; } = new();
    public bool Wireframe { get; set; }
    public bool BackfaceCulling { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    // Type-10 atlases are decoded and inspectable, but their original PS2 blend
    // equation/channel semantics are not established. Keep them out of the normal
    // viewport until applying them no longer creates colored patch boundaries.
    public bool ShowLightmaps { get; set; }
    public bool ShowProps { get => IsPropCategoryVisible(PropRenderCategory.Visual); set => SetPropCategoryVisible(PropRenderCategory.Visual, value); }
    public bool ShowGameplayProxies
    {
        get => Enum.GetValues<PropRenderCategory>().Any(x => x != PropRenderCategory.Visual && IsPropCategoryVisible(x));
        set
        {
            var mask = _visiblePropCategoryMask;
            foreach (var category in Enum.GetValues<PropRenderCategory>().Where(x => x != PropRenderCategory.Visual))
                if (value) mask |= CategoryBit(category); else mask &= ~CategoryBit(category);
            SetPropCategoryMask(mask);
        }
    }
    public bool ShowSplines { get; set; }
    public bool ShowTriggers { get; set; }
    public bool ShowVisibilityCurtains { get; set; }
    public bool ShowLights { get; set; }
    public bool ShowHalos { get; set; }
    public bool ShowParticleEmitters { get; set; }
    public int SelectedPatch { get; set; } = -1;
    public bool IsIsolated => _isolatedPropIndex >= 0;
    public int VisibleModelBatchCount => _visibleModelBatches.Count;
    public int VisibleModelInstanceCount => _visibleModelTransforms.Count;

    public bool IsPropCategoryVisible(PropRenderCategory category) => (_visiblePropCategoryMask & CategoryBit(category)) != 0;
    public void SetPropCategoryVisible(PropRenderCategory category, bool visible)
    {
        var bit = CategoryBit(category); SetPropCategoryMask(visible ? _visiblePropCategoryMask | bit : _visiblePropCategoryMask & ~bit);
    }
    public void ShowOnlyPropCategory(PropRenderCategory category) => SetPropCategoryMask(CategoryBit(category));
    public void ShowAllPropCategories() => SetPropCategoryMask(Enum.GetValues<PropRenderCategory>().Aggregate(0u, (mask, category) => mask | CategoryBit(category)));
    public bool HideProp(PropInstance prop)
    {
        if (!_propIndices.TryGetValue(prop, out var index) || !_hiddenPropIndices.Add(index)) return false;
        if (_isolatedPropIndex == index) _isolatedPropIndex = -1;
        VisibilityChanged(); return true;
    }
    public void ShowAllHiddenProps() { if (_hiddenPropIndices.Count == 0) return; _hiddenPropIndices.Clear(); VisibilityChanged(); }
    public bool IsPropHidden(PropInstance prop) => _propIndices.TryGetValue(prop, out var index) && _hiddenPropIndices.Contains(index);
    private static uint CategoryBit(PropRenderCategory category) => 1u << (int)category;
    private void SetPropCategoryMask(uint mask)
    {
        if (_visiblePropCategoryMask == mask) return;
        _visiblePropCategoryMask = mask; VisibilityChanged();
    }
    private void VisibilityChanged() { _visibilityRevision++; _visibleSetValid = false; }

    public void SetScene(MountainizerScene scene)
    {
        if (ReferenceEquals(scene, _pendingScene) || (_pendingScene is null && ReferenceEquals(scene, _scene))) return;
        _pendingScene = scene; _isolatedPropIndex = -1; _selectedPropIndex = -1; SelectedPatch = -1; _hiddenPropIndices.Clear(); VisibilityChanged();
        BuildSceneCache(scene); Camera.Frame(scene.Bounds);
    }
    public bool FrameCourseStart(MountainizerScene scene, string courseCode)
    {
        if (!CourseCameraPlacement.TryFind(scene, courseCode, out var pose)) return false;
        Camera.SetPose(pose.Position, pose.Target, pose.ReferenceScale);
        return true;
    }
    public void SelectItem(MountainizerScene scene, ISceneItem? item)
    {
        SelectedPatch = item is TerrainPatch patch ? scene.Terrain.IndexOf(patch) : -1;
        _selectedPropIndex = item is PropInstance prop && _propIndices.TryGetValue(prop, out var propIndex) ? propIndex : -1;
    }
    public void ClearSelection() { SelectedPatch = -1; _selectedPropIndex = -1; }
    public bool FrameItem(ISceneItem item)
    {
        if (!_boundsByItem.TryGetValue(item, out var bounds)) return false;
        Camera.Frame(bounds); return true;
    }
    public ISceneItem? Pick(float screenX, float screenY, int width, int height)
    {
        return TryPickHit(screenX, screenY, width, height, out var item, out _, out _) ? item : null;
    }
    public bool TrySetOrbitPivot(float screenX, float screenY, int width, int height)
    {
        if (!TryPickHit(screenX, screenY, width, height, out _, out var direction, out var distance)) return false;
        Camera.SetOrbitPivot(Camera.Position + direction * distance);
        return true;
    }
    private bool TryPickHit(float screenX, float screenY, int width, int height, out ISceneItem? item, out Vector3 direction, out float distance)
    {
        item = null; direction = Camera.Forward; distance = float.PositiveInfinity;
        if (width <= 0 || height <= 0) return false;
        var ndcX = 2f * screenX / width - 1f; var ndcY = 1f - 2f * screenY / height;
        var tanHalfFov = MathF.Tan(InspectionCamera.FieldOfView * 0.5f);
        direction = Vector3.Normalize(Camera.Forward + Camera.Right * (ndcX * tanHalfFov * width / height) + Camera.Up * (ndcY * tanHalfFov));
        var rayDirection = direction;
        for (var i = 0; i < _pickTargets.Count; i++)
        {
            var target = _pickTargets[i];
            if (!IsVisible(target.Item) || !RayIntersectsBounds(Camera.Position, direction, target.Bounds, out _) || !TryExactHit(target, out var hitDistance) || hitDistance >= distance) continue;
            item = target.Item; distance = hitDistance;
        }
        return item is not null;

        bool TryExactHit(PickTarget target, out float hitDistance)
        {
            if (target.Mesh is null) return RayIntersectsBounds(Camera.Position, rayDirection, target.Bounds, out hitDistance);
            var localOrigin = Vector3.Transform(Camera.Position, target.InverseTransform);
            var localDirection = Vector3.TransformNormal(rayDirection, target.InverseTransform);
            return InspectionPicking.RayIntersectsMesh(localOrigin, localDirection, target.Mesh, out hitDistance);
        }
        bool IsVisible(ISceneItem item) => item switch { TerrainPatch => ShowTerrain, PropInstance prop => IsPropVisible(prop),
            Spline => ShowSplines, TriggerVolume => ShowTriggers, VisibilityCurtain => ShowVisibilityCurtains, _ => false };
    }
    public bool FrameProp(MountainizerScene scene, PropInstance prop)
    {
        if (!_modelsByResource.TryGetValue(ResourceKey(prop.ModelTrackId, prop.ModelResourceId), out var model) || model.Mesh is null) return false;
        if (!_propIndices.TryGetValue(prop, out _isolatedPropIndex)) return false;
        if (_boundsByItem.TryGetValue(prop, out var bounds)) Camera.Frame(bounds);
        else Camera.Frame(TransformBounds(SceneBounds.FromPoints(model.Mesh.Positions), prop.Transform));
        return true;
    }
    public void ClearIsolation() => _isolatedPropIndex = -1;

    public void Render(int width, int height)
    {
        if (!_initialized) Initialize();
        if (_pendingScene is not null) { Upload(_pendingScene); _scene = _pendingScene; _pendingScene = null; }
        GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
        GL.ClearColor(0.035f, 0.045f, 0.06f, 1); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        ApplyBackfaceCulling();
        GL.UseProgram(_program);
        var aspect = width / (float)Math.Max(1, height);
        SetMatrices(aspect); SetModel(Matrix4.Identity); SetInstanced(false);
        SetAlphaTest(false); SetUseLightmap(false);
        if (ShowGrid) { DrawGrid(); ApplyBackfaceCulling(); }
        GL.BindVertexArray(_vao); GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(_uTexture, 0); GL.Uniform1(_uLightmap, 1);
        GL.PolygonMode(TriangleFace.FrontAndBack, Wireframe ? PolygonMode.Line : PolygonMode.Fill);
        SetUseTexture(!Wireframe);
        SetColor(Wireframe ? 0.7f : 1f, Wireframe ? 0.86f : 1f, Wireframe ? 0.95f : 1f);
        if (ShowTerrain && _isolatedPropIndex < 0)
        {
            for (var i = 0; i < _drawRanges.Count; i++)
            {
                var range = _drawRanges[i];
                var hasTexture = !Wireframe && range.TextureHandle > 0;
                var hasLightmap = ShowLightmaps && hasTexture && range.LightmapHandle > 0;
                SetUseTexture(hasTexture);
                SetUseLightmap(hasLightmap);
                if (hasTexture) BindTexture(range.TextureHandle);
                if (hasLightmap) BindLightmap(range.LightmapHandle);
                if (!hasTexture) SetColor(0.48f, 0.58f, 0.68f);
                GL.DrawElements(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt, range.Offset * sizeof(uint));
            }
        }
        SetUseLightmap(false);
        PrepareVisibleModelBatches(aspect);
        if (_visibleModelBatches.Count > 0)
        {
            // MDR material sidedness is not decoded yet. Drawing props two-sided keeps
            // thin foliage, signs and rails visible from both directions.
            GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_modelVao); SetInstanced(true);
            for (var i = 0; i < _visibleModelBatches.Count; i++)
            {
                var visible = _visibleModelBatches[i]; var range = visible.Batch.Range;
                var textured = !Wireframe && range.TextureHandle > 0;
                SetUseTexture(textured); SetAlphaTest(textured);
                if (textured) BindTexture(range.TextureHandle);
                else SetColor(Wireframe ? 0.75f : 0.58f, Wireframe ? 0.88f : 0.48f, Wireframe ? 1f : 0.34f);
                ConfigureInstanceAttributes(visible.MatrixOffset);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt,
                    range.Offset * sizeof(uint), visible.InstanceCount);
            }
            SetInstanced(false); SetModel(Matrix4.Identity); SetAlphaTest(false);
        }
        if (_selectedPropIndex >= 0 && IsPropVisible(_selectedPropIndex))
        {
            GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_modelVao); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            SetUseTexture(false); SetAlphaTest(false);
            SetColor(1f, 0.55f, 0.08f); GL.LineWidth(2.5f);
            if ((uint)_selectedPropIndex < (uint)_propModelRanges.Length && _scene is not null)
            {
                var transform = ToTk(_scene.Props[_selectedPropIndex].Transform); var ranges = _propModelRanges[_selectedPropIndex];
                SetModel(transform);
                for (var i = 0; i < ranges.Count; i++)
                {
                    var range = ranges[i];
                    GL.DrawElements(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt, range.Offset * sizeof(uint));
                }
            }
            SetModel(Matrix4.Identity); GL.LineWidth(1f);
        }
        if (_propPointEntries.Count > 0 && !Wireframe)
        {
            GL.BindVertexArray(_propVao); SetUseTexture(false);
            GL.PointSize(2f);
            for (var i = 0; i < _propPointEntries.Count; i++)
            {
                var point = _propPointEntries[i];
                if (!IsPropVisible(point.PropIndex, point.Category)) continue;
                if (point.Category == PropRenderCategory.Visual) SetColor(0.95f, 0.52f, 0.15f);
                else SetColor(1f, 0.25f, 0.05f);
                GL.DrawArrays(PrimitiveType.Points, point.Offset, 1);
            }
            GL.PointSize(1f); GL.BindVertexArray(_vao);
        }
        if ((uint)SelectedPatch < (uint)_patchRanges.Count)
        {
            var range = _patchRanges[SelectedPatch]; GL.Disable(EnableCap.CullFace); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.LineWidth(2.5f); SetUseTexture(false); SetColor(1f, 0.65f, 0.1f);
            GL.DrawElements(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt, range.Offset * sizeof(uint)); GL.LineWidth(1);
        }
        DrawDebugGeometry();
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        DrawAxisIndicator(width, height);
    }

    private void Initialize()
    {
        _program = CreateProgram(VertexShader, FragmentShader); _vao = GL.GenVertexArray(); _vbo = GL.GenBuffer(); _ibo = GL.GenBuffer();
        _uView = GL.GetUniformLocation(_program, "uView"); _uProjection = GL.GetUniformLocation(_program, "uProjection");
        _uModel = GL.GetUniformLocation(_program, "uModel"); _uColor = GL.GetUniformLocation(_program, "uColor");
        _uTexture = GL.GetUniformLocation(_program, "uTexture"); _uUseTexture = GL.GetUniformLocation(_program, "uUseTexture");
        _uLightmap = GL.GetUniformLocation(_program, "uLightmap"); _uUseLightmap = GL.GetUniformLocation(_program, "uUseLightmap");
        _uAlphaTest = GL.GetUniformLocation(_program, "uAlphaTest"); _uInstanced = GL.GetUniformLocation(_program, "uInstanced");
        _propVao = GL.GenVertexArray(); _propVbo = GL.GenBuffer();
        _modelVao = GL.GenVertexArray(); _modelVbo = GL.GenBuffer(); _modelIbo = GL.GenBuffer(); _modelInstanceVbo = GL.GenBuffer();
        _debugVao = GL.GenVertexArray(); _debugVbo = GL.GenBuffer();
        CreateGrid(); CreateAxisIndicator(); _initialized = true;
    }

    private void Upload(MountainizerScene scene)
    {
        foreach (var texture in _textureHandles.Values) GL.DeleteTexture(texture);
        _textureHandles.Clear(); var textureResolver = new SceneTextureResolver(scene);
        var usedTextures = new HashSet<TextureAsset>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < scene.Terrain.Count; i++) AddResolvedTextures(scene.Terrain[i]);
        for (var i = 0; i < scene.Materials.Count; i++) AddResolvedTextures(scene.Materials[i]);
        for (var assetIndex = 0; assetIndex < scene.Textures.Count; assetIndex++)
        {
            var asset = scene.Textures[assetIndex];
            if (!asset.Decoded || !usedTextures.Contains(asset)) continue;
            var handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, asset.Width, asset.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, asset.RgbaPixels);
            var isLightmap = asset.IsLightmap;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(isLightmap ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(isLightmap ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(isLightmap ? TextureMinFilter.Linear : TextureMinFilter.LinearMipmapLinear));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            if (!isLightmap) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            _textureHandles[asset] = handle;
        }
        void AddResolvedTextures(ISceneItem item)
        {
            var resolved = textureResolver.Resolve(item);
            for (var i = 0; i < resolved.Count; i++) usedTextures.Add(resolved[i]);
        }
        _boundTexture = -1; _boundLightmap = -1;
        var vertexFloats = new List<float>(); var indices = new List<uint>(); _patchRanges.Clear(); _drawRanges.Clear(); uint vertexBase = 0;
        for (var patchIndex = 0; patchIndex < scene.Terrain.Count; patchIndex++)
        {
            var patch = scene.Terrain[patchIndex]; var mesh = patch.Mesh;
            var patchTextures = textureResolver.Resolve(patch);
            var diffuseHandle = patchTextures.FirstOrDefault(x => !x.IsLightmap) is { } diffuse && _textureHandles.TryGetValue(diffuse, out var decodedDiffuse) ? decodedDiffuse : -1;
            var lightmapHandle = patchTextures.FirstOrDefault(x => x.IsLightmap) is { } lightmap && _textureHandles.TryGetValue(lightmap, out var decodedLightmap) ? decodedLightmap : -1;
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                var position = mesh.Positions[i]; var normal = mesh.Normals[i];
                var uv = TextureCoordinateConvention.TerrainToOpenGl(mesh.TextureCoordinates[i], patch.TextureResourceId);
                var lightmapUv = mesh.LightmapTextureCoordinates is { } lightmapUvs && i < lightmapUvs.Count
                    ? TextureCoordinateConvention.TerrainToOpenGl(lightmapUvs[i]) : Vector2.Zero;
                // Terrain UVs use the opposite vertical convention from MDR model UVs.
                // Keep the established terrain appearance while allowing signs and
                // other prop atlases to use their stored coordinates verbatim.
                AddTerrainVertex(vertexFloats, position, normal, uv, lightmapUv);
            }
            var offset = indices.Count;
            for (var i = 0; i < mesh.Indices.Count; i++) indices.Add(mesh.Indices[i] + vertexBase);
            var count = indices.Count - offset; _patchRanges.Add((offset, count));
            if (_drawRanges.Count > 0 && _drawRanges[^1].TextureHandle == diffuseHandle && _drawRanges[^1].LightmapHandle == lightmapHandle)
            {
                var previous = _drawRanges[^1]; _drawRanges[^1] = (previous.Offset, previous.Count + count, previous.TextureHandle, previous.LightmapHandle);
            }
            else _drawRanges.Add((offset, count, diffuseHandle, lightmapHandle));
            vertexBase += (uint)mesh.Positions.Count;
        }
        _indexCount = indices.Count; GL.BindVertexArray(_vao); GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexFloats.Count * sizeof(float), vertexFloats.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo); GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 10 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 10 * sizeof(float), 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 10 * sizeof(float), 6 * sizeof(float)); GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(7, 2, VertexAttribPointerType.Float, false, 10 * sizeof(float), 8 * sizeof(float)); GL.EnableVertexAttribArray(7);
        var decodedModelIds = new HashSet<long>();
        for (var i = 0; i < scene.Models.Count; i++)
        {
            var model = scene.Models[i];
            if (model.Mesh is not null) decodedModelIds.Add(ResourceKey(Convert.ToInt32(model.Properties["TrackId"]), Convert.ToInt32(model.Properties["ResourceId"])));
        }
        var propPositions = new List<float>(); _propPointEntries.Clear();
        for (var i = 0; i < scene.Props.Count; i++)
        {
            var prop = scene.Props[i];
            if (decodedModelIds.Contains(ResourceKey(prop.ModelTrackId, prop.ModelResourceId))) continue;
            _propPointEntries.Add((i, propPositions.Count / 3, prop.Classification.Category));
            propPositions.Add(prop.Transform.M41); propPositions.Add(prop.Transform.M42); propPositions.Add(prop.Transform.M43);
        }
        GL.BindVertexArray(_propVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _propVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, propPositions.Count * sizeof(float), propPositions.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f); GL.DisableVertexAttribArray(2); GL.VertexAttrib2(2, 0f, 0f);
        UploadModels(scene, textureResolver);
        UploadDebugGeometry(scene);
    }

    private void UploadDebugGeometry(MountainizerScene scene)
    {
        var vertices = new List<float>();
        static void AddLine(List<float> destination, Vector3 a, Vector3 b)
        {
            destination.Add(a.X); destination.Add(a.Y); destination.Add(a.Z);
            destination.Add(b.X); destination.Add(b.Y); destination.Add(b.Z);
        }
        for (var splineIndex = 0; splineIndex < scene.Splines.Count; splineIndex++)
        {
            var spline = scene.Splines[splineIndex];
            for (var i = 1; i < spline.Points.Count; i++) AddLine(vertices, spline.Points[i - 1].Position, spline.Points[i].Position);
        }
        _splineVertexCount = vertices.Count / 3;
        for (var curtainIndex = 0; curtainIndex < scene.VisibilityCurtains.Count; curtainIndex++)
        {
            var curtain = scene.VisibilityCurtains[curtainIndex];
            for (var i = 1; i < curtain.Points.Count; i++) AddLine(vertices, curtain.Points[i - 1], curtain.Points[i]);
        }
        _curtainVertexCount = vertices.Count / 3 - _splineVertexCount;
        for (var triggerIndex = 0; triggerIndex < scene.Triggers.Count; triggerIndex++)
        {
            var trigger = scene.Triggers[triggerIndex];
            var a = trigger.Minimum; var b = trigger.Maximum;
            var p0 = new Vector3(a.X,a.Y,a.Z); var p1 = new Vector3(b.X,a.Y,a.Z); var p2 = new Vector3(b.X,b.Y,a.Z); var p3 = new Vector3(a.X,b.Y,a.Z);
            var p4 = new Vector3(a.X,a.Y,b.Z); var p5 = new Vector3(b.X,a.Y,b.Z); var p6 = new Vector3(b.X,b.Y,b.Z); var p7 = new Vector3(a.X,b.Y,b.Z);
            AddLine(vertices, p0, p1); AddLine(vertices, p1, p2); AddLine(vertices, p2, p3); AddLine(vertices, p3, p0);
            AddLine(vertices, p4, p5); AddLine(vertices, p5, p6); AddLine(vertices, p6, p7); AddLine(vertices, p7, p4);
            AddLine(vertices, p0, p4); AddLine(vertices, p1, p5); AddLine(vertices, p2, p6); AddLine(vertices, p3, p7);
        }
        _triggerVertexCount = vertices.Count / 3 - _splineVertexCount - _curtainVertexCount;
        var debugVertexBase = vertices.Count / 3;
        AddResourcePoints(scene, vertices, 6); _lightVertexCount = vertices.Count / 3 - debugVertexBase;
        debugVertexBase = vertices.Count / 3;
        AddResourcePoints(scene, vertices, 7); _haloVertexCount = vertices.Count / 3 - debugVertexBase;
        debugVertexBase = vertices.Count / 3;
        AddResourcePoints(scene, vertices, 5); _particleEmitterVertexCount = vertices.Count / 3 - debugVertexBase;
        GL.BindVertexArray(_debugVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _debugVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f); GL.DisableVertexAttribArray(2); GL.VertexAttrib2(2, 0f, 0f);

        static void AddResourcePoints(MountainizerScene source, List<float> destination, int resourceType)
        {
            foreach (var resource in source.UnknownSections.Where(x => x.ResourceType == resourceType))
            {
                if (!resource.Properties.TryGetValue("Position", out var value) || value is not Vector3 position) continue;
                destination.Add(position.X); destination.Add(position.Y); destination.Add(position.Z);
            }
        }
    }

    private void DrawDebugGeometry()
    {
        if (_splineVertexCount + _curtainVertexCount + _triggerVertexCount + _lightVertexCount + _haloVertexCount + _particleEmitterVertexCount == 0) return;
        GL.Disable(EnableCap.CullFace); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill); GL.BindVertexArray(_debugVao);
        SetUseTexture(false); SetAlphaTest(false); SetModel(Matrix4.Identity);
        GL.LineWidth(2f);
        var offset = 0;
        if (ShowSplines && _splineVertexCount > 0) { SetColor(0.15f, 0.95f, 1f); GL.DrawArrays(PrimitiveType.Lines, offset, _splineVertexCount); }
        offset += _splineVertexCount;
        if (ShowVisibilityCurtains && _curtainVertexCount > 0) { SetColor(0.75f, 0.3f, 1f); GL.DrawArrays(PrimitiveType.Lines, offset, _curtainVertexCount); }
        offset += _curtainVertexCount;
        if (ShowTriggers && _triggerVertexCount > 0) { SetColor(1f, 0.2f, 0.2f); GL.DrawArrays(PrimitiveType.Lines, offset, _triggerVertexCount); }
        offset += _triggerVertexCount;
        if (ShowLights && _lightVertexCount > 0) { GL.PointSize(5f); SetColor(1f, 0.92f, 0.42f); GL.DrawArrays(PrimitiveType.Points, offset, _lightVertexCount); }
        offset += _lightVertexCount;
        if (ShowHalos && _haloVertexCount > 0) { GL.PointSize(9f); SetColor(1f, 0.65f, 0.2f); GL.DrawArrays(PrimitiveType.Points, offset, _haloVertexCount); }
        offset += _haloVertexCount;
        if (ShowParticleEmitters && _particleEmitterVertexCount > 0) { GL.PointSize(7f); SetColor(0.25f, 0.9f, 1f); GL.DrawArrays(PrimitiveType.Points, offset, _particleEmitterVertexCount); }
        GL.PointSize(1f);
        GL.LineWidth(1f);
    }

    private void UploadModels(MountainizerScene scene, SceneTextureResolver textureResolver)
    {
        var vertices = new List<float>(); var indices = new List<uint>(); uint vertexBase = 0;
        _modelRanges.Clear(); _modelBatches.Clear(); _visibleModelBatches.Clear(); _visibleModelTransforms.Clear(); _visibleSetValid = false;
        var materialTextures = new Dictionary<long, int>();
        for (var i = 0; i < scene.Materials.Count; i++)
        {
            var material = scene.Materials[i];
            var key = ResourceKey(material.TrackId, material.ResourceId);
            var diffuse = textureResolver.Resolve(material).FirstOrDefault(x => !x.IsLightmap);
            materialTextures[key] =
                diffuse is not null && _textureHandles.TryGetValue(diffuse, out var handle) ? handle : -1;
        }
        for (var modelIndex = 0; modelIndex < scene.Models.Count; modelIndex++)
        {
            var model = scene.Models[modelIndex];
            if (model.Mesh is null) continue;
            var resourceKey = ResourceKey(Convert.ToInt32(model.Properties["TrackId"]), Convert.ToInt32(model.Properties["ResourceId"]));
            var ranges = new List<ModelRange>();
            for (var submeshIndex = 0; submeshIndex < model.Submeshes.Count; submeshIndex++)
            {
                var submesh = model.Submeshes[submeshIndex];
                var mesh = submesh.Mesh; var offset = indices.Count;
                var materialKey = ResourceKey(submesh.MaterialTrackId, submesh.MaterialResourceId);
                for (var i = 0; i < mesh.Positions.Count; i++)
                {
                    var p = mesh.Positions[i]; var n = mesh.Normals[i];
                    var uv = TextureCoordinateConvention.ModelToOpenGl(mesh.TextureCoordinates[i]);
                    AddVertex(vertices, p, n, uv);
                }
                for (var i = 0; i < mesh.Indices.Count; i++) indices.Add(mesh.Indices[i] + vertexBase);
                ranges.Add(new(offset, indices.Count - offset, materialTextures.GetValueOrDefault(materialKey, -1)));
                vertexBase += (uint)mesh.Positions.Count;
            }
            _modelRanges[resourceKey] = ranges;
        }
        GL.BindVertexArray(_modelVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _modelVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _modelIbo); GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), 6 * sizeof(float)); GL.EnableVertexAttribArray(2);
        GL.DisableVertexAttribArray(7); GL.VertexAttrib2(7, 0f, 0f);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _modelInstanceVbo); GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.StreamDraw);
        ConfigureInstanceAttributes(0);
        var batchesByRange = new Dictionary<ModelRange, ModelBatch>();
        _propModelRanges = new List<ModelRange>[scene.Props.Count];
        for (var propIndex = 0; propIndex < scene.Props.Count; propIndex++)
        {
            var prop = scene.Props[propIndex]; _propModelRanges[propIndex] = [];
            if (_modelRanges.TryGetValue(ResourceKey(prop.ModelTrackId, prop.ModelResourceId), out var ranges))
            {
                _propModelRanges[propIndex] = ranges;
                for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
                {
                    var range = ranges[rangeIndex];
                    if (!batchesByRange.TryGetValue(range, out var batch))
                    {
                        batch = new(range); batchesByRange.Add(range, batch); _modelBatches.Add(batch);
                    }
                    batch.Instances.Add(new(ToTk(prop.Transform), propIndex, prop.Classification.Category));
                }
            }
        }
    }

    private bool IsPropVisible(int propIndex)
    {
        if (_scene is null || (uint)propIndex >= (uint)_scene.Props.Count) return false;
        return IsPropVisible(propIndex, _scene.Props[propIndex].Classification.Category);
    }
    private bool IsPropVisible(PropInstance prop) => _propIndices.TryGetValue(prop, out var index) && IsPropVisible(index, prop.Classification.Category);
    private bool IsPropVisible(int propIndex, PropRenderCategory category) =>
        !_hiddenPropIndices.Contains(propIndex) && (_isolatedPropIndex >= 0 ? propIndex == _isolatedPropIndex : IsPropCategoryVisible(category));

    private void PrepareVisibleModelBatches(float aspect)
    {
        var key = new VisibleSetKey(Camera.Target, Camera.Distance, Camera.Yaw, Camera.Pitch, aspect, _visiblePropCategoryMask, _visibilityRevision, _isolatedPropIndex);
        if (_visibleSetValid && key.Equals(_visibleSetKey)) return;
        _visibleSetKey = key; _visibleSetValid = true; _visibleModelTransforms.Clear(); _visibleModelBatches.Clear();
        for (var batchIndex = 0; batchIndex < _modelBatches.Count; batchIndex++)
        {
            var batch = _modelBatches[batchIndex]; var matrixOffset = _visibleModelTransforms.Count;
            for (var instanceIndex = 0; instanceIndex < batch.Instances.Count; instanceIndex++)
            {
                var instance = batch.Instances[instanceIndex];
                if (!IsPropVisible(instance.PropIndex, instance.Category)) continue;
                if ((uint)instance.PropIndex >= (uint)_propBounds.Length || !InspectionFrustum.Contains(Camera, _propBounds[instance.PropIndex], aspect)) continue;
                _visibleModelTransforms.Add(instance.Transform);
            }
            var count = _visibleModelTransforms.Count - matrixOffset;
            if (count > 0) _visibleModelBatches.Add(new(batch, matrixOffset, count));
        }
        var matrices = _visibleModelTransforms.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _modelInstanceVbo);
        if (matrices.Length == 0) GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.StreamDraw);
        else GL.BufferData(BufferTarget.ArrayBuffer, matrices.Length * 16 * sizeof(float), matrices, BufferUsageHint.StreamDraw);
    }

    private void ConfigureInstanceAttributes(int matrixOffset)
    {
        const int vectorSize = 4 * sizeof(float); const int matrixSize = 4 * vectorSize;
        var baseOffset = matrixOffset * matrixSize;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _modelInstanceVbo);
        for (var column = 0; column < 4; column++)
        {
            var location = 3 + column;
            GL.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, matrixSize, baseOffset + column * vectorSize);
            GL.EnableVertexAttribArray(location); GL.VertexAttribDivisor(location, 1);
        }
    }

    private void BuildSceneCache(MountainizerScene scene)
    {
        _pickTargets.Clear(); _modelsByResource.Clear(); _propIndices.Clear(); _boundsByItem.Clear();
        _propBounds = new SceneBounds[scene.Props.Count];
        for (var i = 0; i < scene.Terrain.Count; i++)
        {
            var patch = scene.Terrain[i]; Add(patch, SceneBounds.FromPoints(patch.Mesh.Positions), patch.Mesh, Matrix4x4.Identity);
        }
        var modelBounds = new Dictionary<long, SceneBounds>();
        for (var i = 0; i < scene.Models.Count; i++)
        {
            var model = scene.Models[i];
            if (model.Mesh is null) continue;
            var key = ResourceKey(Convert.ToInt32(model.Properties["TrackId"]), Convert.ToInt32(model.Properties["ResourceId"]));
            _modelsByResource[key] = model; modelBounds[key] = SceneBounds.FromPoints(model.Mesh.Positions);
        }
        for (var i = 0; i < scene.Props.Count; i++)
        {
            var prop = scene.Props[i]; _propIndices[prop] = i;
            if (modelBounds.TryGetValue(ResourceKey(prop.ModelTrackId, prop.ModelResourceId), out var bounds))
            {
                var key = ResourceKey(prop.ModelTrackId, prop.ModelResourceId);
                _propBounds[i] = TransformBounds(bounds, prop.Transform);
                Add(prop, _propBounds[i], _modelsByResource.GetValueOrDefault(key)?.Mesh, prop.Transform);
            }
        }
        for (var i = 0; i < scene.Splines.Count; i++)
        {
            var spline = scene.Splines[i]; Add(spline, BoundsFromSpline(spline));
        }
        for (var i = 0; i < scene.Triggers.Count; i++)
        {
            var trigger = scene.Triggers[i]; Add(trigger, new(trigger.Minimum, trigger.Maximum));
        }
        for (var i = 0; i < scene.VisibilityCurtains.Count; i++)
        {
            var curtain = scene.VisibilityCurtains[i]; Add(curtain, SceneBounds.FromPoints(curtain.Points));
        }
        return;

        void Add(ISceneItem item, SceneBounds bounds, MeshData? mesh = null, Matrix4x4? transform = null)
        {
            var padding = Math.Max(25f, bounds.Radius * 0.01f); var amount = new Vector3(padding);
            var padded = new SceneBounds(bounds.Minimum - amount, bounds.Maximum + amount);
            var inverse = Matrix4x4.Identity;
            if (mesh is not null && !Matrix4x4.Invert(transform ?? Matrix4x4.Identity, out inverse)) mesh = null;
            _pickTargets.Add(new(item, padded, mesh, inverse)); _boundsByItem[item] = padded;
        }
    }

    private static SceneBounds BoundsFromSpline(Spline spline)
    {
        if (spline.Points.Count == 0) return new(Vector3.Zero, Vector3.Zero);
        var minimum = spline.Points[0].Position; var maximum = minimum;
        for (var i = 1; i < spline.Points.Count; i++)
        {
            minimum = Vector3.Min(minimum, spline.Points[i].Position); maximum = Vector3.Max(maximum, spline.Points[i].Position);
        }
        return new(minimum, maximum);
    }

    private static SceneBounds TransformBounds(SceneBounds bounds, Matrix4x4 transform)
    {
        var a = bounds.Minimum; var b = bounds.Maximum;
        Span<Vector3> corners =
        [
            new(a.X,a.Y,a.Z), new(b.X,a.Y,a.Z), new(a.X,b.Y,a.Z), new(b.X,b.Y,a.Z),
            new(a.X,a.Y,b.Z), new(b.X,a.Y,b.Z), new(a.X,b.Y,b.Z), new(b.X,b.Y,b.Z)
        ];
        var minimum = Vector3.Transform(corners[0], transform); var maximum = minimum;
        for (var i = 1; i < corners.Length; i++)
        {
            var point = Vector3.Transform(corners[i], transform);
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        return new(minimum, maximum);
    }

    private static bool RayIntersectsBounds(Vector3 origin, Vector3 direction, SceneBounds bounds, out float distance)
    {
        var tMin = 0f; var tMax = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var min = axis == 0 ? bounds.Minimum.X : axis == 1 ? bounds.Minimum.Y : bounds.Minimum.Z;
            var max = axis == 0 ? bounds.Maximum.X : axis == 1 ? bounds.Maximum.Y : bounds.Maximum.Z;
            if (MathF.Abs(d) < 0.000001f) { if (o < min || o > max) { distance = 0; return false; } continue; }
            var a = (min - o) / d; var b = (max - o) / d; if (a > b) (a, b) = (b, a);
            tMin = Math.Max(tMin, a); tMax = Math.Min(tMax, b); if (tMin > tMax) { distance = 0; return false; }
        }
        distance = tMin; return tMax >= 0;
    }

    private void CreateGrid()
    {
        var values = new List<float>(); const int lines = 40; const float spacing = 5000;
        for (var i = -lines; i <= lines; i++) { var v = i * spacing; values.AddRange([-lines * spacing, 0, v, lines * spacing, 0, v, v, 0, -lines * spacing, v, 0, lines * spacing]); }
        _gridVertexCount = values.Count / 3; _gridVao = GL.GenVertexArray(); _gridVbo = GL.GenBuffer(); GL.BindVertexArray(_gridVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, values.Count * sizeof(float), values.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0); GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f);
    }
    private void CreateAxisIndicator()
    {
        float[] vertices = [0,0,0, 1,0,0, 0,0,0, 0,1,0, 0,0,0, 0,0,1];
        _axisVao = GL.GenVertexArray(); _axisVbo = GL.GenBuffer(); GL.BindVertexArray(_axisVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _axisVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f); GL.DisableVertexAttribArray(2); GL.VertexAttrib2(2, 0f, 0f);
    }
    private void DrawAxisIndicator(int width, int height)
    {
        const int size = 92; const int margin = 12;
        GL.Enable(EnableCap.ScissorTest); GL.Scissor(margin, margin, size, size); GL.Clear(ClearBufferMask.DepthBufferBit); GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(margin, margin, size, size); GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_axisVao);
        var forward = Camera.Forward; var up = Camera.Up;
        var view = Matrix4.LookAt(new Vector3Tk(-forward.X * 3, -forward.Y * 3, -forward.Z * 3), Vector3Tk.Zero, new Vector3Tk(up.X, up.Y, up.Z));
        var projection = Matrix4.CreateOrthographic(2.8f, 2.8f, 0.1f, 10f);
        GL.UniformMatrix4(_uView, false, ref view); GL.UniformMatrix4(_uProjection, false, ref projection);
        SetModel(Matrix4.Identity); SetUseTexture(false); SetAlphaTest(false); GL.LineWidth(3f);
        SetColor(1f, 0.18f, 0.12f); GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        SetColor(0.18f, 1f, 0.25f); GL.DrawArrays(PrimitiveType.Lines, 2, 2);
        SetColor(0.2f, 0.5f, 1f); GL.DrawArrays(PrimitiveType.Lines, 4, 2); GL.LineWidth(1f);
        GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1)); SetMatrices(width / (float)Math.Max(1, height), true);
    }
    private void DrawGrid() { GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_gridVao); SetUseTexture(false); SetColor(0.18f, 0.22f, 0.27f); GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount); }
    private void ApplyBackfaceCulling()
    {
        if (BackfaceCulling) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }
        else GL.Disable(EnableCap.CullFace);
    }
    private void SetMatrices(float aspect, bool force = false)
    {
        var view = Camera.View; var projection = Camera.Projection(aspect);
        if (force || !_hasLastMatrices || !view.Equals(_lastView)) GL.UniformMatrix4(_uView, false, ref view);
        if (force || !_hasLastMatrices || !projection.Equals(_lastProjection)) GL.UniformMatrix4(_uProjection, false, ref projection);
        _lastView = view; _lastProjection = projection; _hasLastMatrices = true;
    }
    private void SetModel(Matrix4 model)
    {
        if (_hasLastModel && model.Equals(_lastModel)) return;
        GL.UniformMatrix4(_uModel, false, ref model); _lastModel = model; _hasLastModel = true;
    }
    private void SetUseTexture(bool value)
    {
        var numeric = value ? 1 : 0;
        if (_lastUseTexture == numeric) return;
        GL.Uniform1(_uUseTexture, numeric); _lastUseTexture = numeric;
    }
    private void SetUseLightmap(bool value)
    {
        var numeric = value ? 1 : 0;
        if (_lastUseLightmap == numeric) return;
        GL.Uniform1(_uUseLightmap, numeric); _lastUseLightmap = numeric;
    }
    private void SetAlphaTest(bool value)
    {
        var numeric = value ? 1 : 0;
        if (_lastAlphaTest == numeric) return;
        GL.Uniform1(_uAlphaTest, numeric); _lastAlphaTest = numeric;
    }
    private void SetInstanced(bool value)
    {
        var numeric = value ? 1 : 0;
        if (_lastInstanced == numeric) return;
        GL.Uniform1(_uInstanced, numeric); _lastInstanced = numeric;
    }
    private void SetColor(float red, float green, float blue)
    {
        var color = new Vector3Tk(red, green, blue);
        if (_hasLastColor && color.Equals(_lastColor)) return;
        GL.Uniform3(_uColor, red, green, blue); _lastColor = color; _hasLastColor = true;
    }
    private void BindTexture(int texture)
    {
        if (_boundTexture == texture) return;
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture); _boundTexture = texture;
    }
    private void BindLightmap(int texture)
    {
        if (_boundLightmap == texture) return;
        GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, texture); _boundLightmap = texture;
    }
    private static void AddVertex(List<float> destination, Vector3 position, Vector3 normal, Vector2 uv)
    {
        destination.Add(position.X); destination.Add(position.Y); destination.Add(position.Z);
        destination.Add(normal.X); destination.Add(normal.Y); destination.Add(normal.Z);
        destination.Add(uv.X); destination.Add(uv.Y);
    }
    private static void AddTerrainVertex(List<float> destination, Vector3 position, Vector3 normal, Vector2 uv, Vector2 lightmapUv)
    {
        AddVertex(destination, position, normal, uv);
        destination.Add(lightmapUv.X); destination.Add(lightmapUv.Y);
    }
    private static Matrix4 ToTk(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    private static long ResourceKey(int trackId, int resourceId) => ((long)trackId << 32) | (uint)resourceId;
    private static int CreateProgram(string vertex, string fragment)
    {
        int Compile(ShaderType type, string source) { var s = GL.CreateShader(type); GL.ShaderSource(s, source); GL.CompileShader(s); GL.GetShader(s, ShaderParameter.CompileStatus, out var ok); if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(s)); return s; }
        var vs = Compile(ShaderType.VertexShader, vertex); var fs = Compile(ShaderType.FragmentShader, fragment); var p = GL.CreateProgram(); GL.AttachShader(p, vs); GL.AttachShader(p, fs); GL.LinkProgram(p); GL.GetProgram(p, GetProgramParameterName.LinkStatus, out var ok); GL.DeleteShader(vs); GL.DeleteShader(fs); if (ok == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(p)); return p;
    }
    public void Dispose() { if (!_initialized) return; foreach (var texture in _textureHandles.Values) GL.DeleteTexture(texture); _textureHandles.Clear(); GL.DeleteProgram(_program); GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); GL.DeleteBuffer(_ibo); GL.DeleteVertexArray(_gridVao); GL.DeleteBuffer(_gridVbo); GL.DeleteVertexArray(_propVao); GL.DeleteBuffer(_propVbo); GL.DeleteVertexArray(_modelVao); GL.DeleteBuffer(_modelVbo); GL.DeleteBuffer(_modelIbo); GL.DeleteBuffer(_modelInstanceVbo); GL.DeleteVertexArray(_debugVao); GL.DeleteBuffer(_debugVbo); GL.DeleteVertexArray(_axisVao); GL.DeleteBuffer(_axisVbo); _initialized = false; }

    private const string VertexShader = """
#version 330 core
    layout(location=0) in vec3 aPosition; layout(location=1) in vec3 aNormal; layout(location=2) in vec2 aTexCoord; layout(location=7) in vec2 aLightmapTexCoord;
    layout(location=3) in vec4 iModel0; layout(location=4) in vec4 iModel1; layout(location=5) in vec4 iModel2; layout(location=6) in vec4 iModel3;
    uniform mat4 uView; uniform mat4 uProjection; uniform mat4 uModel; uniform int uInstanced; out vec3 vNormal; out vec2 vTexCoord; out vec2 vLightmapTexCoord;
    void main(){ mat4 model=uInstanced!=0?mat4(iModel0,iModel1,iModel2,iModel3):uModel; vNormal=mat3(model)*aNormal; vTexCoord=aTexCoord; vLightmapTexCoord=aLightmapTexCoord; gl_Position=uProjection*uView*model*vec4(aPosition,1.0); }
""";
    private const string FragmentShader = """
#version 330 core
in vec3 vNormal; in vec2 vTexCoord; in vec2 vLightmapTexCoord; uniform vec3 uColor; uniform sampler2D uTexture; uniform sampler2D uLightmap; uniform int uUseTexture; uniform int uUseLightmap; uniform int uAlphaTest; out vec4 color;
void main(){ float directional=abs(dot(normalize(vNormal),normalize(vec3(0.35,0.8,0.45)))); float light=uUseLightmap!=0?0.88+0.12*directional:0.48+0.52*directional; vec4 texel=texture(uTexture,vTexCoord); if(uUseTexture!=0&&uAlphaTest!=0&&texel.a<0.08)discard; vec3 base=uUseTexture!=0?texel.rgb:uColor; if(uUseLightmap!=0){vec3 baked=texture(uLightmap,vLightmapTexCoord).rgb; base*=0.60+0.75*baked;} color=vec4(base*light,1.0); }
""";
}
