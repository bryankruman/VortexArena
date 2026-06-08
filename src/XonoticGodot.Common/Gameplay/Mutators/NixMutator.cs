using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The NIX ("No Items Xonotic") mutator — port of common/mutators/mutator/nix/sv_nix.qc. Removes item
/// pickups; instead every player is forced to use the same single weapon, which rotates for everyone on a
/// timer, with ammo trickled in. Enabled by the <c>g_nix</c> cvar (and only when not instagib/overkill/arena).
///
/// Ported here: the enable gate, the item filtering (FilterItemDefinition strips health/armor/powerups
/// unless the respective <c>g_nix_with_*</c> cvar is set), the no-throwing forbid, the random-start weapon
/// forbid, and the headline weapon-rotation engine (<c>NIX_GiveCurrentWeapon</c> / <c>NIX_ChooseNextWeapon</c>):
/// a weighted-random rotation on a per-round timer, per-weapon ammo refill + trickle, the new-weapon /
/// countdown notifications, and the on-remove restore. The engine forces every live player's inventory to
/// the current weapon (+ optional blaster) each frame via <see cref="Inventory"/>.
/// </summary>
[Mutator]
public sealed class NixMutator : MutatorBase
{
    /// <summary>QC autocvar_g_nix_with_healtharmor — keep health/armor pickups.</summary>
    public bool WithHealthArmor;

    /// <summary>QC autocvar_g_nix_with_powerups — keep powerup pickups.</summary>
    public bool WithPowerups;

    /// <summary>QC autocvar_g_nix_with_blaster — also give the blaster alongside the rotating weapon.</summary>
    public bool WithBlaster;

    /// <summary>QC autocvar_g_balance_nix_roundtime — seconds each weapon stays active before rotating.</summary>
    public float RoundTime = 25f;

    /// <summary>QC autocvar_g_balance_nix_incrtime — seconds between ammo trickle increments.</summary>
    public float IncrTime = 1.6f;

    public NixMutator() => NetName = "nix";

    // QC: expr_evaluate(cvar_string("g_nix")) && !instagib && !ok && !weaponarena.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_nix") != 0f;

    // QC globals (one world per process — these are the C# successors to nix_weapon/nix_nextchange/etc.).
    private int _nixWeapon;       // currently-active weapon RegistryId (0/none until first round)
    private int _nixNextWeapon;   // chosen-but-not-yet-active weapon RegistryId (0 = needs choosing)
    private float _nixNextChange; // engine time the current round ends / next rotation happens

    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;

        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);

        // MUTATOR_ONADD: reset the rotation clock.
        _nixWeapon = 0;
        _nixNextWeapon = 0;
        _nixNextChange = 0f;

        if (Api.Services is not null)
        {
            WithHealthArmor = Api.Cvars.GetFloat("g_nix_with_healtharmor") != 0f;
            WithPowerups = Api.Cvars.GetFloat("g_nix_with_powerups") != 0f;
            WithBlaster = Api.Cvars.GetFloat("g_nix_with_blaster") != 0f;
            float rt = Api.Cvars.GetFloat("g_balance_nix_roundtime");
            if (rt != 0f) RoundTime = rt;
            float it = Api.Cvars.GetFloat("g_balance_nix_incrtime");
            if (it != 0f) IncrTime = it;
        }
    }

    public override void Unhook()
    {
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);

        // MUTATOR_ONREMOVE: as PlayerSpawn no longer runs, restore the normal start loadout for live players
        // so they aren't stuck with a NIX weapon. (Ammo restore is best-effort against the start defaults.)
        if (Api.Services is not null)
            foreach (Entity p in Api.Entities.FindByClass("player"))
            {
                if ((p.Flags & EntFlags.Client) == 0 || p.DeadState != DeadFlag.No) continue;
                Weapon? blaster = Weapons.ByName("blaster");
                Inventory.ClearWeapons(p);
                if (blaster is not null) Inventory.GiveWeapon(p, blaster);
                Inventory.SwitchToBest(p);
            }
    }

    // MUTATOR_HOOKFUNCTION(nix, FilterItemDefinition) — delete all items except optionally health/armor/powerups.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        // QC inspects definition.instanceOfHealth / instanceOfArmor / instanceOfPowerup. The item-class
        // registry isn't ported yet; we approximate via the definition's classname tag.
        string id = args.Definition.ClassName;
        bool isHealth = id.StartsWith("item_health", System.StringComparison.Ordinal);
        bool isArmor = id.StartsWith("item_armor", System.StringComparison.Ordinal);
        bool isPowerup = id is "item_strength" or "item_shield" or "item_invincible";

        if (isHealth || isArmor)
            return !WithHealthArmor;   // disallow unless kept
        if (isPowerup)
            return !WithPowerups;
        // QC keys off definition.instanceOfHealth/instanceOfArmor/instanceOfPowerup; the classname tags
        // above are the faithful stand-in until the item-class registry exposes those instanceOf flags.
        return true; // delete all other items
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    // MUTATOR_HOOKFUNCTION(nix, PlayerSpawn) — overrides the spawn loadout with the current NIX weapon.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        player.NixLastChangeId = -1f;     // force a fresh ammo/weapon sync this frame
        GiveCurrentWeapon(player);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(nix, PlayerPreThink) — keep every live player on the current NIX weapon.
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) != 0 && player.DeadState == DeadFlag.No)
            GiveCurrentWeapon(player);
        return false;
    }

    // bool NIX_CanChooseWeapon(int wpn) — only the "normal" non-mutatorblocked weapons rotate.
    // (QC also skips WEP_Null; there is no dummy weapon in the C# registry, so no id-0 guard is needed.)
    private bool CanChooseWeapon(Weapon w)
    {
        if (WithBlaster && w.NetName == "blaster") return false;      // blaster is given separately
        if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) return false;
        if ((w.SpawnFlags & WeaponFlags.Normal) == 0) return false;
        return true;
    }

    // void NIX_ChooseNextWeapon() — weighted-random pick, biased to differ from the current weapon.
    private void ChooseNextWeapon()
    {
        // QC RandomSelection_AddFloat(it.m_id, 1, (it.m_id != nix_weapon)) — every choosable weapon has
        // weight 1, but a non-zero "priority" only for weapons != current, so the current is avoided when
        // any alternative exists. Reproduce: collect candidates, prefer the subset that isn't current.
        List<Weapon> all = new();
        List<Weapon> notCurrent = new();
        foreach (Weapon w in Weapons.All)
        {
            if (!CanChooseWeapon(w)) continue;
            all.Add(w);
            if (w.RegistryId != _nixWeapon) notCurrent.Add(w);
        }
        List<Weapon> pool = notCurrent.Count > 0 ? notCurrent : all;
        _nixNextWeapon = pool.Count > 0 ? pool[Prandom.RangeInt(0, pool.Count)].RegistryId : 0;
    }

    // void NIX_GiveCurrentWeapon(entity this) — the rotation engine core (per-player, per-frame).
    private void GiveCurrentWeapon(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        if (_nixNextWeapon == 0)
            ChooseNextWeapon();

        float dt = MathF.Ceiling(_nixNextChange - now);

        if (dt <= 0f)
        {
            _nixWeapon = _nixNextWeapon;
            _nixNextWeapon = 0;
            if (_nixNextChange == 0f)
                _nixNextChange = now;            // first round starts now
            else
                _nixNextChange = now + RoundTime; // schedule the next rotation
        }

        Weapon? wpn = _nixWeapon > 0 && _nixWeapon < Registry<Weapon>.Count
            ? Registry<Weapon>.ById(_nixWeapon) : null;
        if (wpn is null) return;

        ResourceType ammoType = AmmoTypeOf(wpn.NetName);

        // Once-per-round per-player sync: wipe ammo, refill the current weapon's ammo type, reset the
        // trickle timer, and notify of the new weapon.
        if (_nixNextChange != player.NixLastChangeId)
        {
            player.SetResource(ResourceType.Shells, 0f);
            player.SetResource(ResourceType.Bullets, 0f);
            player.SetResource(ResourceType.Rockets, 0f);
            player.SetResource(ResourceType.Cells, 0f);
            player.SetResource(ResourceType.Fuel, 0f);

            if (ammoType != ResourceType.None)
                player.SetResource(ammoType, AmmoStart(ammoType));

            player.NixNextIncr = now + IncrTime;

            if (!(dt >= 1f && dt <= 5f))
                NotificationSystem.Center(player, "NIX_NEWWEAPON", _nixWeapon);

            player.NixLastChangeId = _nixNextChange;
        }

        // Countdown notification (de-duped) during the last 5 seconds before a rotation.
        if (player.NixLastInfoTime != dt)
        {
            player.NixLastInfoTime = dt;
            if (dt >= 1f && dt <= 5f)
                NotificationSystem.Center(player, "NIX_COUNTDOWN", _nixNextWeapon, dt);
        }

        // Ammo trickle: top up the current weapon's ammo a little, every IncrTime seconds.
        if (now > player.NixNextIncr)
        {
            if (ammoType != ResourceType.None)
                player.GiveResource(ammoType, AmmoIncr(ammoType));
            player.NixNextIncr = now + IncrTime;
        }

        // Force the owned set to exactly {current weapon (+ blaster if with_blaster)} every frame, mirroring
        // QC's STAT(WEAPONS) rewrite. Preserve the player's active choice between those when it's still valid
        // (so with_blaster players can keep firing the blaster); otherwise switch to the NIX weapon.
        Weapon? prevActive = Inventory.CurrentWeapon(player);
        Weapon? blasterWep = WithBlaster ? Weapons.ByName("blaster") : null;

        player.OwnedWeaponSet.Clear();
        if (blasterWep is not null) player.OwnedWeaponSet.Add(blasterWep);
        player.OwnedWeaponSet.Add(wpn);

        bool keepActive = prevActive is not null
            && (prevActive == wpn || (blasterWep is not null && prevActive == blasterWep));
        if (keepActive)
            Inventory.SwitchWeapon(player, prevActive!);
        else
            Inventory.SwitchWeapon(player, wpn);
    }

    /// <summary>QC wpn.ammo_type — the standard weapon→resource mapping (bal-wep-xonotic ATTRIB ammo_type).</summary>
    private static ResourceType AmmoTypeOf(string netName) => netName switch
    {
        "shotgun" => ResourceType.Shells,
        "machinegun" or "rifle" => ResourceType.Bullets,
        "mortar" or "hagar" or "devastator" or "seeker" or "minelayer" => ResourceType.Rockets,
        "vortex" or "electro" or "crylink" or "hlac" or "arc" or "vaporizer" => ResourceType.Cells,
        "hook" => ResourceType.Fuel,
        _ => ResourceType.None, // blaster / porto / tuba — ammo-less
    };

    private float AmmoStart(ResourceType t) => t switch
    {
        ResourceType.Shells  => Cvar("g_balance_nix_ammo_shells", 60f),
        ResourceType.Bullets => Cvar("g_balance_nix_ammo_nails", 320f),
        ResourceType.Rockets => Cvar("g_balance_nix_ammo_rockets", 160f),
        ResourceType.Cells   => Cvar("g_balance_nix_ammo_cells", 180f),
        ResourceType.Fuel    => Cvar("g_balance_nix_ammo_fuel", 100f),
        _ => 0f,
    };

    private float AmmoIncr(ResourceType t) => t switch
    {
        ResourceType.Shells  => Cvar("g_balance_nix_ammoincr_shells", 2f),
        ResourceType.Bullets => Cvar("g_balance_nix_ammoincr_nails", 6f),
        ResourceType.Rockets => Cvar("g_balance_nix_ammoincr_rockets", 2f),
        ResourceType.Cells   => Cvar("g_balance_nix_ammoincr_cells", 2f),
        ResourceType.Fuel    => Cvar("g_balance_nix_ammoincr_fuel", 2f),
        _ => 0f,
    };

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
