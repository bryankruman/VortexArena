using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Base monster descriptor (QC CLASS(Monster), common/monsters/). Registered into <see cref="Monsters"/>.</summary>
public abstract partial class Monster : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public float StartHealth;
    public float Damage;
    public float Speed;
    public string RegistryName => NetName;

    /// <summary>Initialize a spawned monster entity.</summary>
    public virtual void Spawn(Entity e) { }
    /// <summary>Per-think AI step.</summary>
    public virtual void Think(Entity e) { }
    /// <summary>Attack a target.</summary>
    public virtual void Attack(Entity e, Entity target) { }
}

/// <summary>Base turret descriptor (QC CLASS(Turret), common/turrets/). Registered into <see cref="Turrets"/>.</summary>
public abstract partial class Turret : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public float StartHealth;
    public float Range;
    public string RegistryName => NetName;

    public virtual void Spawn(Entity e) { }
    public virtual void Think(Entity e) { }
    public virtual bool ValidTarget(Entity self, Entity target) => true;
}

/// <summary>Base vehicle descriptor (QC CLASS(Vehicle), common/vehicles/). Registered into <see cref="Vehicles"/>.</summary>
public abstract partial class Vehicle : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public float StartHealth;
    public string RegistryName => NetName;

    public virtual void Spawn(Entity e) { }
    public virtual void Enter(Entity vehicle, Entity player) { }
    public virtual void Exit(Entity vehicle, Entity player) { }
    public virtual void Think(Entity vehicle) { }
}

// catalogs (the FOREACH targets)
public static class Monsters
{
    public static IReadOnlyList<Monster> All => Registry<Monster>.All;
    public static int Count => Registry<Monster>.Count;
    public static Monster? ByName(string name) => Registry<Monster>.ByName(name);
}

public static class Turrets
{
    public static IReadOnlyList<Turret> All => Registry<Turret>.All;
    public static int Count => Registry<Turret>.Count;
    public static Turret? ByName(string name) => Registry<Turret>.ByName(name);
}

public static class Vehicles
{
    public static IReadOnlyList<Vehicle> All => Registry<Vehicle>.All;
    public static int Count => Registry<Vehicle>.Count;
    public static Vehicle? ByName(string name) => Registry<Vehicle>.ByName(name);
}

/// <summary>
/// Registry of map-entity spawn functions (QC spawnfunc_CLASSNAME). The BSP entity lump maps a
/// "classname" to one of these setup delegates. Map objects (func_door, trigger_*, …) register here.
/// </summary>
public static class SpawnFuncs
{
    private static readonly Dictionary<string, Action<Entity>> _map = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string className, Action<Entity> setup) => _map[className] = setup;

    public static bool TrySpawn(string className, Entity e)
    {
        if (_map.TryGetValue(className, out var f)) { f(e); return true; }
        return false;
    }

    public static int Count => _map.Count;
    public static IReadOnlyDictionary<string, Action<Entity>> All => _map;
}
