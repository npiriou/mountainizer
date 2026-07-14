namespace Mountainizer.Core;

public enum PropRenderCategory
{
    Visual,
    Collision,
    ResetPlane,
    GameplayVolume,
    Trigger,
    RideState,
    StreamingMarker,
    EffectMarker,
    Proxy
}

public readonly record struct PropClassification(PropRenderCategory Category, string Reason)
{
    public bool IsVisual => Category == PropRenderCategory.Visual;
}

public static class PropClassifier
{
    public static PropClassification Classify(string name)
    {
        if (Contains(name, "collision")) return Hidden(PropRenderCategory.Collision, "collision marker");
        if (Contains(name, "reset_plane", "resetplane")) return Hidden(PropRenderCategory.ResetPlane, "reset plane");
        if (Contains(name, "heightplane")) return Hidden(PropRenderCategory.ResetPlane, "height plane");
        if (Contains(name, "volume")) return Hidden(PropRenderCategory.GameplayVolume, "gameplay volume");
        if (Contains(name, "ridestate")) return Hidden(PropRenderCategory.RideState, "ride-state marker");
        if (Contains(name, "trigger", "trig")) return Hidden(PropRenderCategory.Trigger, "trigger marker");
        if (Contains(name, "teleport", "_challenge_")) return Hidden(PropRenderCategory.GameplayVolume, "gameplay-state marker");
        if (Contains(name, "_load_", "_unload_")) return Hidden(PropRenderCategory.StreamingMarker, "streaming boundary");
        if (Contains(name, "_proxy_", "_nis_")) return Hidden(PropRenderCategory.Proxy, "proxy marker");
        if (Contains(name, "emitter", "spray", "smoke", "firefly", "roadflare", "impact", "charge"))
            return Hidden(PropRenderCategory.EffectMarker, "effect or particle marker");
        return new(PropRenderCategory.Visual, "decoded visual model instance");
    }

    private static PropClassification Hidden(PropRenderCategory category, string reason) => new(category, reason);

    private static bool Contains(string value, params string[] terms)
    {
        for (var i = 0; i < terms.Length; i++)
            if (value.Contains(terms[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
