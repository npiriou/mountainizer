namespace Mountainizer.Core;

/// <summary>Resolves the decoded textures used by scene items without rescanning the scene on every selection.</summary>
public sealed class SceneTextureResolver
{
    private readonly Dictionary<(int TrackId, int ResourceId, TextureUsage Usage), List<TextureAsset>> _textures = [];
    private readonly Dictionary<(int ResourceId, TextureUsage Usage), List<TextureAsset>> _texturesByResource = [];
    private readonly Dictionary<long, MaterialAsset> _materials = [];
    private readonly Dictionary<int, MaterialAsset> _materialsByResource = [];
    private readonly Dictionary<long, ModelAsset> _models = [];
    private readonly Dictionary<ISceneItem, IReadOnlyList<TextureAsset>> _results = new(ReferenceEqualityComparer.Instance);

    public SceneTextureResolver(MountainizerScene scene)
    {
        for (var i = 0; i < scene.Textures.Count; i++)
        {
            var texture = scene.Textures[i];
            if (!texture.Decoded) continue;
            AddCandidate(_textures, (texture.TrackId, texture.ResourceId, texture.Usage), texture);
            AddCandidate(_texturesByResource, (texture.ResourceId, texture.Usage), texture);
        }
        for (var i = 0; i < scene.Materials.Count; i++)
        {
            var material = scene.Materials[i];
            _materials[Key(material.TrackId, material.ResourceId)] = material;
            _materialsByResource[material.ResourceId] = material;
        }
        for (var i = 0; i < scene.Models.Count; i++)
        {
            var model = scene.Models[i];
            _models[Key(Convert.ToInt32(model.Properties["TrackId"]), Convert.ToInt32(model.Properties["ResourceId"]))] = model;
        }
    }

    public IReadOnlyList<TextureAsset> Resolve(ISceneItem item)
    {
        if (_results.TryGetValue(item, out var cached)) return cached;
        var resolved = new List<TextureAsset>();
        Resolve(item, resolved);
        var result = resolved.ToArray();
        _results[item] = result;
        return result;
    }

    private void Resolve(ISceneItem item, List<TextureAsset> destination)
    {
        switch (item)
        {
            case TextureAsset texture when texture.Decoded:
                AddUnique(destination, texture);
                break;
            case TerrainPatch terrain:
                AddTexture(destination, terrain.TrackId, terrain.TextureResourceId, TextureUsage.Diffuse, GroupOf(terrain));
                if (terrain.LightmapResourceId >= 0)
                    AddTexture(destination, 255, terrain.LightmapResourceId, TextureUsage.Lightmap, GroupOf(terrain));
                break;
            case MaterialAsset material:
                AddTexture(destination, material.TrackId, material.TextureResourceId, TextureUsage.Diffuse, GroupOf(material));
                break;
            case ModelAsset model:
                for (var i = 0; i < model.Submeshes.Count; i++)
                {
                    var submesh = model.Submeshes[i];
                    if (_materials.TryGetValue(Key(submesh.MaterialTrackId, submesh.MaterialResourceId), out var submeshMaterial)
                        || _materialsByResource.TryGetValue(submesh.MaterialResourceId, out submeshMaterial))
                        AddTexture(destination, submeshMaterial.TrackId, submeshMaterial.TextureResourceId, TextureUsage.Diffuse,
                            GroupOf(submeshMaterial) ?? GroupOf(model));
                }
                break;
            case PropInstance prop when _models.TryGetValue(Key(prop.ModelTrackId, prop.ModelResourceId), out var propModel):
                var modelTextures = Resolve(propModel);
                for (var i = 0; i < modelTextures.Count; i++) AddUnique(destination, modelTextures[i]);
                break;
        }
    }

    private void AddTexture(List<TextureAsset> destination, int trackId, int resourceId, TextureUsage usage, int? referenceGroup)
    {
        if (_textures.TryGetValue((trackId, resourceId, usage), out var exact)) AddUnique(destination, Nearest(exact, referenceGroup));
        else if (_texturesByResource.TryGetValue((resourceId, usage), out var fallback)) AddUnique(destination, Nearest(fallback, referenceGroup));
    }

    private static TextureAsset Nearest(IReadOnlyList<TextureAsset> candidates, int? referenceGroup)
    {
        if (candidates.Count == 1 || referenceGroup is null) return candidates[^1];
        var best = candidates[0];
        var bestGroup = GroupOf(best) ?? int.MinValue;
        var bestDistance = Math.Abs((long)bestGroup - referenceGroup.Value);
        for (var i = 1; i < candidates.Count; i++)
        {
            var candidateGroup = GroupOf(candidates[i]) ?? int.MinValue;
            var distance = Math.Abs((long)candidateGroup - referenceGroup.Value);
            // On an equal distance prefer the preceding bank: streaming resources
            // are normally declared before the models which consume them.
            if (distance < bestDistance || distance == bestDistance && candidateGroup <= referenceGroup && bestGroup > referenceGroup)
            {
                best = candidates[i]; bestGroup = candidateGroup; bestDistance = distance;
            }
        }
        return best;
    }

    private static int? GroupOf(ISceneItem item) =>
        item.Properties.TryGetValue("GroupIndex", out var value) && value is not null ? Convert.ToInt32(value) : null;

    private static void AddCandidate<TKey>(Dictionary<TKey, List<TextureAsset>> destination, TKey key, TextureAsset texture)
        where TKey : notnull
    {
        if (!destination.TryGetValue(key, out var candidates)) destination[key] = candidates = [];
        candidates.Add(texture);
    }

    private static void AddUnique(List<TextureAsset> destination, TextureAsset texture)
    {
        for (var i = 0; i < destination.Count; i++)
            if (ReferenceEquals(destination[i], texture)) return;
        destination.Add(texture);
    }

    private static long Key(int trackId, int resourceId) => ((long)trackId << 32) | (uint)resourceId;
}
