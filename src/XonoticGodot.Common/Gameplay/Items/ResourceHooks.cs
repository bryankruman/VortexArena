// The resource mutator-hook surface — the C# successor to the MUTATOR_CALLHOOK points in
// qcsrc/server/sv_resources.qc (GetResourceLimit, SetResource, GiveResource, ResourceAmountChanged,
// ResourceWasted). QC fires these so mutators can override per-resource caps, forbid changes, or react to
// gains/waste. The port keeps the resource math in Resources.cs and exposes these typed hook chains here
// (kept in the Items folder so the resource subsystem owns them); mutators subscribe via Hook()/Unhook().

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The resource hook chains (QC sv_resources.qc MUTATOR_CALLHOOK points). Empty by default — a handler is
/// added only by an active mutator. <see cref="Resources"/> calls them on the set/give/limit paths.
/// </summary>
public static class ResourceHooks
{
    /// <summary>QC GetResourceLimit hook: a mutator may override a resource's cap. Set <see cref="GetResourceLimitArgs.Limit"/>.</summary>
    public struct GetResourceLimitArgs { public Entity Entity; public ResourceType Resource; public float Limit; }
    public static readonly HookChain<GetResourceLimitArgs> GetResourceLimit = new();

    /// <summary>QC SetResource hook: a mutator may forbid (return true) or rewrite the about-to-be-set amount.</summary>
    public struct SetResourceArgs { public Entity Entity; public ResourceType Resource; public float Amount; public bool Forbid; }
    public static readonly HookChain<SetResourceArgs> SetResource = new();

    /// <summary>QC GiveResource hook: a mutator may rewrite the amount being given (e.g. resistance/vampire buffs).</summary>
    public struct GiveResourceArgs { public Entity Receiver; public ResourceType Resource; public float Amount; }
    public static readonly HookChain<GiveResourceArgs> GiveResource = new();

    /// <summary>QC ResourceAmountChanged hook: fired after a resource value actually changes (HUD/score reactions).</summary>
    public struct ResourceChangedArgs { public Entity Entity; public ResourceType Resource; public float Amount; }
    public static readonly HookChain<ResourceChangedArgs> ResourceAmountChanged = new();

    /// <summary>QC ResourceWasted hook: fired when a give exceeds the cap and the excess is dropped.</summary>
    public struct ResourceWastedArgs { public Entity Entity; public ResourceType Resource; public float Wasted; }
    public static readonly HookChain<ResourceWastedArgs> ResourceWasted = new();

    // --- thin call helpers used by Resources.cs (mirroring MUTATOR_CALLHOOK + M_ARGV read-back) ---

    /// <summary>Run the GetResourceLimit hook, returning the (possibly overridden) limit.</summary>
    internal static float CallGetResourceLimit(Entity e, ResourceType res, float limit)
    {
        if (GetResourceLimit.Count == 0) return limit;
        var args = new GetResourceLimitArgs { Entity = e, Resource = res, Limit = limit };
        GetResourceLimit.Call(ref args);
        return args.Limit;
    }

    /// <summary>Run the SetResource hook; returns true if a mutator forbids the change, with the (maybe rewritten) amount.</summary>
    internal static bool CallSetResource(Entity e, ResourceType res, ref float amount)
    {
        if (SetResource.Count == 0) return false;
        var args = new SetResourceArgs { Entity = e, Resource = res, Amount = amount, Forbid = false };
        SetResource.Call(ref args);
        amount = args.Amount;
        return args.Forbid;
    }

    /// <summary>Run the GiveResource hook; returns the (maybe rewritten) amount to give.</summary>
    internal static float CallGiveResource(Entity e, ResourceType res, float amount)
    {
        if (GiveResource.Count == 0) return amount;
        var args = new GiveResourceArgs { Receiver = e, Resource = res, Amount = amount };
        GiveResource.Call(ref args);
        return args.Amount;
    }

    internal static void CallResourceAmountChanged(Entity e, ResourceType res, float amount)
    {
        if (ResourceAmountChanged.Count == 0) return;
        var args = new ResourceChangedArgs { Entity = e, Resource = res, Amount = amount };
        ResourceAmountChanged.Call(ref args);
    }

    internal static void CallResourceWasted(Entity e, ResourceType res, float wasted)
    {
        if (wasted <= 0f || ResourceWasted.Count == 0) return;
        var args = new ResourceWastedArgs { Entity = e, Resource = res, Wasted = wasted };
        ResourceWasted.Call(ref args);
    }
}
