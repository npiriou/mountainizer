using System.Numerics;
using Mountainizer.Core;
using OpenTK.Graphics.OpenGL4;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using Vector3Tk = OpenTK.Mathematics.Vector3;

namespace Mountainizer.Rendering;

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
    private MountainizerScene? _pendingScene;
    private MountainizerScene? _scene;
    private int _program, _vao, _vbo, _ibo, _gridVao, _gridVbo, _propVao, _propVbo, _modelVao, _modelVbo, _modelIbo, _debugVao, _debugVbo, _axisVao, _axisVbo;
    private int _indexCount, _gridVertexCount, _propCount, _collisionPropCount, _splineVertexCount, _curtainVertexCount, _triggerVertexCount;
    private readonly List<(int Offset, int Count)> _patchRanges = [];
    private readonly List<(int Offset, int Count, int TextureRid)> _drawRanges = [];
    private readonly Dictionary<int, int> _textures = [];
    private readonly Dictionary<long, List<(int Offset, int Count, int TextureRid)>> _modelRanges = [];
    private readonly List<(int Offset, int Count, int TextureRid, Matrix4 Transform, int PropIndex, bool GameplayProxy)> _modelInstances = [];
    private readonly List<(ISceneItem Item, SceneBounds Bounds)> _pickTargets = [];
    private int _isolatedPropIndex = -1;
    private int _selectedPropIndex = -1;
    private bool _initialized;
    public InspectionCamera Camera { get; } = new();
    public bool Wireframe { get; set; }
    public bool BackfaceCulling { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    public bool ShowProps { get; set; } = true;
    public bool ShowGameplayProxies { get; set; }
    public bool ShowSplines { get; set; }
    public bool ShowTriggers { get; set; }
    public bool ShowVisibilityCurtains { get; set; }
    public int SelectedPatch { get; set; } = -1;
    public bool IsIsolated => _isolatedPropIndex >= 0;

    public void SetScene(MountainizerScene scene) { _pendingScene = scene; _isolatedPropIndex = -1; _selectedPropIndex = -1; SelectedPatch = -1; BuildPickTargets(scene); Camera.Frame(scene.Bounds); }
    public bool FrameCourseStart(MountainizerScene scene, string courseCode)
    {
        if (!CourseCameraPlacement.TryFind(scene, courseCode, out var pose)) return false;
        Camera.SetPose(pose.Position, pose.Target, pose.ReferenceScale);
        return true;
    }
    public void SelectItem(MountainizerScene scene, ISceneItem? item)
    {
        SelectedPatch = item is TerrainPatch patch ? scene.Terrain.IndexOf(patch) : -1;
        _selectedPropIndex = item is PropInstance prop ? scene.Props.IndexOf(prop) : -1;
    }
    public void ClearSelection() { SelectedPatch = -1; _selectedPropIndex = -1; }
    public bool FrameItem(ISceneItem item)
    {
        var target = _pickTargets.FirstOrDefault(x => ReferenceEquals(x.Item, item));
        if (target.Item is null) return false;
        Camera.Frame(target.Bounds); return true;
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
        foreach (var target in _pickTargets)
            if (IsVisible(target.Item) && RayIntersectsBounds(Camera.Position, direction, target.Bounds, out var hitDistance) && hitDistance < distance)
            { item = target.Item; distance = hitDistance; }
        return item is not null;

        bool IsVisible(ISceneItem item) => item switch { TerrainPatch => ShowTerrain, PropInstance prop => prop.IsNonVisualGameplayProxy ? ShowGameplayProxies : ShowProps,
            Spline => ShowSplines, TriggerVolume => ShowTriggers, VisibilityCurtain => ShowVisibilityCurtains, _ => false };
    }
    public bool FrameProp(MountainizerScene scene, PropInstance prop)
    {
        var model = scene.Models.FirstOrDefault(x => x.Mesh is not null && ResourceKey(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])) == ResourceKey(prop.ModelTrackId, prop.ModelResourceId));
        if (model?.Mesh is null) return false;
        _isolatedPropIndex = scene.Props.IndexOf(prop);
        Camera.Frame(SceneBounds.FromPoints(model.Mesh.Positions.Select(x => Vector3.Transform(x, prop.Transform)))); return true;
    }
    public void ClearIsolation() => _isolatedPropIndex = -1;

    public void Render(int width, int height)
    {
        if (!_initialized) Initialize();
        if (_pendingScene is not null) { Upload(_pendingScene); _scene = _pendingScene; _pendingScene = null; }
        GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
        GL.ClearColor(0.035f, 0.045f, 0.06f, 1); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        if (BackfaceCulling) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); } else GL.Disable(EnableCap.CullFace);
        GL.UseProgram(_program);
        SetMatrices(width / (float)Math.Max(1, height));
        SetModel(Matrix4.Identity);
        GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), 0);
        if (ShowGrid) DrawGrid();
        GL.BindVertexArray(_vao); GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_program, "uTexture"), 0);
        GL.PolygonMode(TriangleFace.FrontAndBack, Wireframe ? PolygonMode.Line : PolygonMode.Fill);
        GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), Wireframe ? 0 : 1);
        GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), Wireframe ? 0.7f : 1f, Wireframe ? 0.86f : 1f, Wireframe ? 0.95f : 1f);
        foreach (var range in ShowTerrain && _isolatedPropIndex < 0 ? _drawRanges : [])
        {
            var texture = 0; var hasTexture = !Wireframe && _textures.TryGetValue(range.TextureRid, out texture);
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), hasTexture ? 1 : 0);
            if (hasTexture) GL.BindTexture(TextureTarget.Texture2D, texture);
            else GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.48f, 0.58f, 0.68f);
            GL.DrawElements(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt, range.Offset * sizeof(uint));
        }
        if (_modelInstances.Count > 0 && !Wireframe)
        {
            GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_modelVao);
            foreach (var instance in _modelInstances)
            {
                if (!IsPropVisible(instance.PropIndex)) continue;
                var textured = _textures.TryGetValue(instance.TextureRid, out var texture);
                GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), textured ? 1 : 0);
                GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), textured ? 1 : 0);
                if (textured) { GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture); }
                else GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.58f, 0.48f, 0.34f);
                SetModel(instance.Transform);
                GL.DrawElements(PrimitiveType.Triangles, instance.Count, DrawElementsType.UnsignedInt, instance.Offset * sizeof(uint));
            }
            SetModel(Matrix4.Identity); GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), 0);
        }
        if (_selectedPropIndex >= 0 && IsPropVisible(_selectedPropIndex))
        {
            GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_modelVao); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0); GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), 0);
            GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 1f, 0.55f, 0.08f); GL.LineWidth(2.5f);
            foreach (var instance in _modelInstances.Where(x => x.PropIndex == _selectedPropIndex))
            { SetModel(instance.Transform); GL.DrawElements(PrimitiveType.Triangles, instance.Count, DrawElementsType.UnsignedInt, instance.Offset * sizeof(uint)); }
            SetModel(Matrix4.Identity); GL.LineWidth(1f);
        }
        if ((ShowProps && _propCount > 0 || ShowGameplayProxies && _collisionPropCount > 0) && !Wireframe)
        {
            GL.BindVertexArray(_propVao); GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0);
            GL.PointSize(2f);
            if (ShowProps && _propCount > 0)
            {
                GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.95f, 0.52f, 0.15f);
                GL.DrawArrays(PrimitiveType.Points, 0, _propCount);
            }
            if (ShowGameplayProxies && _collisionPropCount > 0)
            {
                GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 1f, 0.25f, 0.05f);
                GL.DrawArrays(PrimitiveType.Points, _propCount, _collisionPropCount);
            }
            GL.PointSize(1f); GL.BindVertexArray(_vao);
        }
        if ((uint)SelectedPatch < (uint)_patchRanges.Count)
        {
            var range = _patchRanges[SelectedPatch]; GL.Disable(EnableCap.CullFace); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.LineWidth(2.5f); GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0); GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 1f, 0.65f, 0.1f);
            GL.DrawElements(PrimitiveType.Triangles, range.Count, DrawElementsType.UnsignedInt, range.Offset * sizeof(uint)); GL.LineWidth(1);
        }
        DrawDebugGeometry();
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        DrawAxisIndicator(width, height);
    }

    private void Initialize()
    {
        _program = CreateProgram(VertexShader, FragmentShader); _vao = GL.GenVertexArray(); _vbo = GL.GenBuffer(); _ibo = GL.GenBuffer();
        _propVao = GL.GenVertexArray(); _propVbo = GL.GenBuffer();
        _modelVao = GL.GenVertexArray(); _modelVbo = GL.GenBuffer(); _modelIbo = GL.GenBuffer();
        _debugVao = GL.GenVertexArray(); _debugVbo = GL.GenBuffer();
        CreateGrid(); CreateAxisIndicator(); _initialized = true;
    }

    private void Upload(MountainizerScene scene)
    {
        foreach (var texture in _textures.Values) GL.DeleteTexture(texture);
        _textures.Clear();
        foreach (var asset in scene.Textures.Where(x => x.Decoded))
        {
            var handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, asset.Width, asset.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, asset.RgbaPixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); _textures[asset.ResourceId] = handle;
        }
        var vertexFloats = new List<float>(); var indices = new List<uint>(); _patchRanges.Clear(); _drawRanges.Clear(); uint vertexBase = 0;
        foreach (var patch in scene.Terrain)
        {
            foreach (var (position, i) in patch.Mesh.Positions.Select((x, i) => (x, i)))
            {
                var normal = patch.Mesh.Normals[i]; var uv = patch.Mesh.TextureCoordinates[i];
                vertexFloats.AddRange([position.X, position.Y, position.Z, normal.X, normal.Y, normal.Z, uv.X, uv.Y]);
            }
            var offset = indices.Count; indices.AddRange(patch.Mesh.Indices.Select(x => x + vertexBase));
            var count = indices.Count - offset; _patchRanges.Add((offset, count));
            if (_drawRanges.Count > 0 && _drawRanges[^1].TextureRid == patch.TextureResourceId)
            {
                var previous = _drawRanges[^1]; _drawRanges[^1] = (previous.Offset, previous.Count + count, previous.TextureRid);
            }
            else _drawRanges.Add((offset, count, patch.TextureResourceId));
            vertexBase += (uint)patch.Mesh.Positions.Count;
        }
        _indexCount = indices.Count; GL.BindVertexArray(_vao); GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexFloats.Count * sizeof(float), vertexFloats.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo); GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), 6 * sizeof(float)); GL.EnableVertexAttribArray(2);
        var decodedModelIds = scene.Models.Where(x => x.Mesh is not null).Select(x => ResourceKey(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"]))).ToHashSet();
        var unresolvedProps = scene.Props.Where(x => !decodedModelIds.Contains(ResourceKey(x.ModelTrackId, x.ModelResourceId))).ToArray();
        var visibleUnresolvedProps = unresolvedProps.Where(x => !x.IsNonVisualGameplayProxy).ToArray();
        var collisionUnresolvedProps = unresolvedProps.Where(x => x.IsNonVisualGameplayProxy).ToArray();
        var propPositions = visibleUnresolvedProps.Concat(collisionUnresolvedProps).SelectMany(x => new[] { x.Transform.M41, x.Transform.M42, x.Transform.M43 }).ToArray();
        _propCount = visibleUnresolvedProps.Length; _collisionPropCount = collisionUnresolvedProps.Length; GL.BindVertexArray(_propVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _propVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, propPositions.Length * sizeof(float), propPositions, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f); GL.DisableVertexAttribArray(2); GL.VertexAttrib2(2, 0f, 0f);
        UploadModels(scene);
        UploadDebugGeometry(scene);
    }

    private void UploadDebugGeometry(MountainizerScene scene)
    {
        var vertices = new List<float>();
        static void AddLine(List<float> destination, Vector3 a, Vector3 b) => destination.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
        foreach (var spline in scene.Splines)
            for (var i = 1; i < spline.Points.Count; i++) AddLine(vertices, spline.Points[i - 1].Position, spline.Points[i].Position);
        _splineVertexCount = vertices.Count / 3;
        foreach (var curtain in scene.VisibilityCurtains)
            for (var i = 1; i < curtain.Points.Count; i++) AddLine(vertices, curtain.Points[i - 1], curtain.Points[i]);
        _curtainVertexCount = vertices.Count / 3 - _splineVertexCount;
        foreach (var trigger in scene.Triggers)
        {
            var a = trigger.Minimum; var b = trigger.Maximum;
            var p = new[] { new Vector3(a.X,a.Y,a.Z), new Vector3(b.X,a.Y,a.Z), new Vector3(b.X,b.Y,a.Z), new Vector3(a.X,b.Y,a.Z),
                new Vector3(a.X,a.Y,b.Z), new Vector3(b.X,a.Y,b.Z), new Vector3(b.X,b.Y,b.Z), new Vector3(a.X,b.Y,b.Z) };
            foreach (var (x, y) in new[] { (0,1),(1,2),(2,3),(3,0),(4,5),(5,6),(6,7),(7,4),(0,4),(1,5),(2,6),(3,7) }) AddLine(vertices, p[x], p[y]);
        }
        _triggerVertexCount = vertices.Count / 3 - _splineVertexCount - _curtainVertexCount;
        GL.BindVertexArray(_debugVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _debugVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); GL.VertexAttrib3(1, 0f, 1f, 0f); GL.DisableVertexAttribArray(2); GL.VertexAttrib2(2, 0f, 0f);
    }

    private void DrawDebugGeometry()
    {
        if (_splineVertexCount + _curtainVertexCount + _triggerVertexCount == 0) return;
        GL.Disable(EnableCap.CullFace); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill); GL.BindVertexArray(_debugVao);
        GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0); GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), 0); SetModel(Matrix4.Identity);
        GL.LineWidth(2f);
        var offset = 0;
        if (ShowSplines && _splineVertexCount > 0) { GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.15f, 0.95f, 1f); GL.DrawArrays(PrimitiveType.Lines, offset, _splineVertexCount); }
        offset += _splineVertexCount;
        if (ShowVisibilityCurtains && _curtainVertexCount > 0) { GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.75f, 0.3f, 1f); GL.DrawArrays(PrimitiveType.Lines, offset, _curtainVertexCount); }
        offset += _curtainVertexCount;
        if (ShowTriggers && _triggerVertexCount > 0) { GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 1f, 0.2f, 0.2f); GL.DrawArrays(PrimitiveType.Lines, offset, _triggerVertexCount); }
        GL.LineWidth(1f);
    }

    private void UploadModels(MountainizerScene scene)
    {
        var vertices = new List<float>(); var indices = new List<uint>(); uint vertexBase = 0; _modelRanges.Clear(); _modelInstances.Clear();
        var materialTextures = scene.Materials.GroupBy(x => ResourceKey(x.TrackId, x.ResourceId)).ToDictionary(x => x.Key, x => (int)x.Last().TextureResourceId);
        foreach (var model in scene.Models.Where(x => x.Mesh is not null))
        {
            var resourceKey = ResourceKey(Convert.ToInt32(model.Properties["TrackId"]), Convert.ToInt32(model.Properties["ResourceId"]));
            var ranges = new List<(int Offset, int Count, int TextureRid)>();
            foreach (var submesh in model.Submeshes)
            {
                var mesh = submesh.Mesh; var offset = indices.Count;
                for (var i = 0; i < mesh.Positions.Count; i++)
                {
                    var p = mesh.Positions[i]; var n = mesh.Normals[i]; var uv = mesh.TextureCoordinates[i];
                    vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, uv.X, uv.Y]);
                }
                var materialKey = ResourceKey(submesh.MaterialTrackId, submesh.MaterialResourceId);
                indices.AddRange(mesh.Indices.Select(x => x + vertexBase)); ranges.Add((offset, indices.Count - offset, materialTextures.GetValueOrDefault(materialKey, -1)));
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
        foreach (var (prop, propIndex) in scene.Props.Select((value, index) => (value, index)))
            if (_modelRanges.TryGetValue(ResourceKey(prop.ModelTrackId, prop.ModelResourceId), out var ranges))
                foreach (var range in ranges) _modelInstances.Add((range.Offset, range.Count, range.TextureRid, ToTk(prop.Transform), propIndex, prop.IsNonVisualGameplayProxy));
    }

    private bool IsPropVisible(int propIndex)
    {
        if (_isolatedPropIndex >= 0) return propIndex == _isolatedPropIndex;
        if (_scene is null || (uint)propIndex >= (uint)_scene.Props.Count) return false;
        return _scene.Props[propIndex].IsNonVisualGameplayProxy ? ShowGameplayProxies : ShowProps;
    }

    private void BuildPickTargets(MountainizerScene scene)
    {
        _pickTargets.Clear();
        foreach (var patch in scene.Terrain) Add(patch, SceneBounds.FromPoints(patch.Mesh.Positions));
        var modelBounds = scene.Models.Where(x => x.Mesh is not null)
            .GroupBy(x => ResourceKey(Convert.ToInt32(x.Properties["TrackId"]), Convert.ToInt32(x.Properties["ResourceId"])))
            .ToDictionary(x => x.Key, x => SceneBounds.FromPoints(x.Last().Mesh!.Positions));
        foreach (var prop in scene.Props)
            if (modelBounds.TryGetValue(ResourceKey(prop.ModelTrackId, prop.ModelResourceId), out var bounds)) Add(prop, TransformBounds(bounds, prop.Transform));
        foreach (var spline in scene.Splines) Add(spline, SceneBounds.FromPoints(spline.Points.Select(x => x.Position)));
        foreach (var trigger in scene.Triggers) Add(trigger, new(trigger.Minimum, trigger.Maximum));
        foreach (var curtain in scene.VisibilityCurtains) Add(curtain, SceneBounds.FromPoints(curtain.Points));
        return;

        void Add(ISceneItem item, SceneBounds bounds)
        {
            var padding = Math.Max(25f, bounds.Radius * 0.01f); var amount = new Vector3(padding);
            _pickTargets.Add((item, new(bounds.Minimum - amount, bounds.Maximum + amount)));
        }
    }

    private static SceneBounds TransformBounds(SceneBounds bounds, Matrix4x4 transform)
    {
        var a = bounds.Minimum; var b = bounds.Maximum;
        return SceneBounds.FromPoints(new[] { new Vector3(a.X,a.Y,a.Z), new Vector3(b.X,a.Y,a.Z), new Vector3(a.X,b.Y,a.Z), new Vector3(b.X,b.Y,a.Z),
            new Vector3(a.X,a.Y,b.Z), new Vector3(b.X,a.Y,b.Z), new Vector3(a.X,b.Y,b.Z), new Vector3(b.X,b.Y,b.Z) }.Select(x => Vector3.Transform(x, transform)));
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
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uView"), false, ref view); GL.UniformMatrix4(GL.GetUniformLocation(_program, "uProjection"), false, ref projection);
        SetModel(Matrix4.Identity); GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0); GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), 0); GL.LineWidth(3f);
        GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 1f, 0.18f, 0.12f); GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.18f, 1f, 0.25f); GL.DrawArrays(PrimitiveType.Lines, 2, 2);
        GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.2f, 0.5f, 1f); GL.DrawArrays(PrimitiveType.Lines, 4, 2); GL.LineWidth(1f);
        GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1)); SetMatrices(width / (float)Math.Max(1, height));
    }
    private void DrawGrid() { GL.Disable(EnableCap.CullFace); GL.BindVertexArray(_gridVao); GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0); GL.Uniform3(GL.GetUniformLocation(_program, "uColor"), 0.18f, 0.22f, 0.27f); GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount); }
    private void SetMatrices(float aspect) { var view = Camera.View; var projection = Camera.Projection(aspect); GL.UniformMatrix4(GL.GetUniformLocation(_program, "uView"), false, ref view); GL.UniformMatrix4(GL.GetUniformLocation(_program, "uProjection"), false, ref projection); }
    private void SetModel(Matrix4 model) => GL.UniformMatrix4(GL.GetUniformLocation(_program, "uModel"), false, ref model);
    private static Matrix4 ToTk(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    private static long ResourceKey(int trackId, int resourceId) => ((long)trackId << 32) | (uint)resourceId;
    private static int CreateProgram(string vertex, string fragment)
    {
        int Compile(ShaderType type, string source) { var s = GL.CreateShader(type); GL.ShaderSource(s, source); GL.CompileShader(s); GL.GetShader(s, ShaderParameter.CompileStatus, out var ok); if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(s)); return s; }
        var vs = Compile(ShaderType.VertexShader, vertex); var fs = Compile(ShaderType.FragmentShader, fragment); var p = GL.CreateProgram(); GL.AttachShader(p, vs); GL.AttachShader(p, fs); GL.LinkProgram(p); GL.GetProgram(p, GetProgramParameterName.LinkStatus, out var ok); GL.DeleteShader(vs); GL.DeleteShader(fs); if (ok == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(p)); return p;
    }
    public void Dispose() { if (!_initialized) return; foreach (var texture in _textures.Values) GL.DeleteTexture(texture); _textures.Clear(); GL.DeleteProgram(_program); GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); GL.DeleteBuffer(_ibo); GL.DeleteVertexArray(_gridVao); GL.DeleteBuffer(_gridVbo); GL.DeleteVertexArray(_propVao); GL.DeleteBuffer(_propVbo); GL.DeleteVertexArray(_modelVao); GL.DeleteBuffer(_modelVbo); GL.DeleteBuffer(_modelIbo); GL.DeleteVertexArray(_debugVao); GL.DeleteBuffer(_debugVbo); GL.DeleteVertexArray(_axisVao); GL.DeleteBuffer(_axisVbo); _initialized = false; }

    private const string VertexShader = """
#version 330 core
layout(location=0) in vec3 aPosition; layout(location=1) in vec3 aNormal; layout(location=2) in vec2 aTexCoord;
uniform mat4 uView; uniform mat4 uProjection; uniform mat4 uModel; out vec3 vNormal; out vec2 vTexCoord;
void main(){ vNormal=mat3(uModel)*aNormal; vTexCoord=vec2(aTexCoord.x,1.0-aTexCoord.y); gl_Position=uProjection*uView*uModel*vec4(aPosition,1.0); }
""";
    private const string FragmentShader = """
#version 330 core
in vec3 vNormal; in vec2 vTexCoord; uniform vec3 uColor; uniform sampler2D uTexture; uniform int uUseTexture; uniform int uAlphaTest; out vec4 color;
void main(){ float light=0.48+0.52*abs(dot(normalize(vNormal),normalize(vec3(0.35,0.8,0.45)))); vec4 texel=texture(uTexture,vTexCoord); if(uUseTexture!=0&&uAlphaTest!=0&&texel.a<0.08)discard; vec3 base=uUseTexture!=0?texel.rgb:uColor; color=vec4(base*light,1.0); }
""";
}
