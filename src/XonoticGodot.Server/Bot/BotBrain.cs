using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The per-bot brain — the C# port of server/bot/default/havocbot/havocbot.qc's <c>havocbot_ai</c> think
/// loop, glued to <see cref="BotNavigation"/>, <see cref="BotAim"/> and <see cref="BotRoles"/>.
///
/// Each tick <see cref="Think"/>:
///  1. evaluates the situation — picks the nearest attackable enemy with line of sight (QC havocbot_chooseenemy);
///  2. on a slower strategy clock, runs the current role to rate goals and routes to the best (QC role + navigation_routetogoal);
///  3. aims — at the enemy (with projectile lead + skill error) if one exists, else along the move direction (QC havocbot_aim/bot_aimdir);
///  4. navigates — steers toward the current goal, producing wish-move + jump/crouch (QC havocbot_movetogoal);
///  5. emits a <see cref="MovementInput"/>, sets the attack button when the enemy is in sight+range and the
///     aim is on target, and calls <see cref="Movement.Move"/> to advance the bot one physics tick.
///
/// <see cref="Skill"/> (0..10, or &gt;100 = SUPERBOT) scales aim error, turn rate, reaction interval and
/// aggression — the single knob the QC <c>skill</c> cvar controls.
/// </summary>
public sealed class BotBrain
{
    // ---- tuning (QC autocvar_bot_ai_*, defaults from xonotic-server.cfg) ----
    private const float EnemyDetectionRadius = 10000f;       // bot_ai_enemydetectionradius
    private const float EnemyDetectionInterval = 2f;         // bot_ai_enemydetectioninterval
    private const float ChooseWeaponInterval = 0.5f;         // bot_ai_chooseweaponinterval
    private const float AimInterval = 0.1f;                  // havocbot_aim cadence
    private const float DefaultShotSpeed = 0f;               // 0 => treat unknown weapons as hitscan (no lead)

    /// <summary>The player entity this brain controls (QC the bot edict).</summary>
    public readonly Player Bot;

    /// <summary>The shared map waypoint graph (QC g_waypoints), or null for graphless roaming.</summary>
    public WaypointNetwork? Network;

    /// <summary>Skill level 0..10 (QC <c>skill</c>); &gt;100 = SUPERBOT (perfect aim/reflexes).</summary>
    public float Skill = 5f;

    /// <summary>The active role (QC <c>.havocbot_role</c>), selectable by gametype via <see cref="BotRoles.ChooseRole"/>.</summary>
    public BotRole Role = BotRoles.RoleGeneric;

    /// <summary>
    /// Supplies the set of players the bot can see/fight (QC the client list). Set by
    /// <see cref="BotController"/> to its roster, because in this port clients are not in the engine entity
    /// table so <c>FindByClass("player")</c> can't find them. When null, falls back to the entity table.
    /// </summary>
    public Func<IEnumerable<Player>>? PlayerProvider;

    /// <summary>The active gametype NetName (QC GetGametype), used to pick objective roles. Set by the controller.</summary>
    public string? GameTypeNetName;

    /// <summary>
    /// The active gametype singleton (QC the registered gametype), so objective roles can read its state
    /// (CTF flag carriers, KH keys, …). Optional — null falls the team roles back to generic DM behaviour.
    /// </summary>
    public GameType? GameType;

    public readonly BotNavigation Nav;
    public readonly BotAim Aim;

    private readonly GoalRater _rater = new();
    private readonly Random _rng;

    // QC timing fields
    private float _chooseEnemyTime;
    private float _strategyTime;
    private float _aimTime;
    private float _chooseWeaponTime;

    // last input emitted (useful for the host/tests)
    public MovementInput LastInput { get; private set; }

    /// <summary>The bot's current target (QC <c>.enemy</c>).</summary>
    public Entity? Enemy => Bot.Enemy;

    public BotBrain(Player bot, WaypointNetwork? network = null, float skill = 5f, int seed = 0)
    {
        Bot = bot;
        Network = network;
        Skill = skill;
        _rng = seed == 0 ? new Random() : new Random(seed);
        Aim = new BotAim(seed);
        Nav = new BotNavigation();

        // sync hull/view from the entity (QC PL_MIN/PL_MAX, view_ofs)
        Nav.Mins = bot.Mins != Vector3.Zero ? bot.Mins : Nav.Mins;
        Nav.Maxs = bot.Maxs != Vector3.Zero ? bot.Maxs : Nav.Maxs;
        Nav.Skill = Skill;         // gates bunnyhop tuning (QC bot_ai_bunnyhop_skilloffset)
        Nav.MaxSpeed = Cvars.MaxSpeed;
        Aim.ViewOffset = bot.ViewOfs != Vector3.Zero ? bot.ViewOfs : Aim.ViewOffset;
        Aim.ViewAngles = bot.Angles;
        Aim.Reset(Now);
    }

    private static float Now => Api.Clock.Time;

    /// <summary>The players this bot is aware of (QC FOREACH_CLIENT), via <see cref="PlayerProvider"/> or the entity table.</summary>
    internal IEnumerable<Entity> Players()
    {
        if (PlayerProvider is not null)
        {
            foreach (var p in PlayerProvider())
                if (!p.IsFreed)
                    yield return p;
            yield break;
        }
        foreach (var e in Api.Entities.FindByClass("player"))
            if (!e.IsFreed)
                yield return e;
    }

    /// <summary>
    /// Advance the bot one frame (QC havocbot_ai + bot_think). <paramref name="dt"/> is the tick length in
    /// seconds (PHYS_INPUT_TIMELENGTH). Produces and applies a <see cref="MovementInput"/> via
    /// <see cref="Movement.Move"/>. No-op while the bot is dead (QC IS_DEAD early-out clears the route).
    /// </summary>
    public void Think(Player bot, float dt)
    {
        float now = Now;

        if (bot.IsDead)
        {
            Nav.ClearRoute();
            bot.Enemy = null;
            return;
        }

        // 1) target selection (throttled; SUPERBOT reacts fast)
        ChooseEnemy(now);

        // 1b) weapon selection: pick the best owned weapon for the enemy's range (QC havocbot_chooseweapon).
        if (now >= _chooseWeaponTime)
        {
            _chooseWeaponTime = now + ChooseWeaponInterval;
            ChooseWeapon(bot.Enemy);
        }

        // 2) strategy: rate goals via the role and route to the best (slower clock). A little random jitter
        // staggers bots so they don't all re-plan on the same frame (QC bot_strategytoken rotation effect).
        float strategyInterval = System.Math.Max(0.1f, 2f - System.Math.Min(Skill, 10f) * 0.1f);
        if (now >= _strategyTime)
        {
            _strategyTime = now + strategyInterval + (float)_rng.NextDouble() * 0.1f;
            Role(this, _rater);
            if (_rater.HasGoal)
            {
                var g = _rater.Best;
                Nav.SetGoal(bot.Origin, g.Position, Network, g.Target);
            }
        }

        // 3) navigation: steer toward current goal -> wish-move + jump/crouch
        bool onGround = bot.OnGround;
        Vector3 move = Nav.Steer(bot, Aim.ViewAngles.Y, onGround);

        // 4) aim
        bool wantAttack = false;
        Aim.UpdateShotVectors(bot.Origin);
        if (now >= _aimTime)
            _aimTime = now + AimInterval;

        var enemy = bot.Enemy;
        if (enemy is { IsFreed: false })
        {
            wantAttack = AimAndDecideFire(enemy, dt, now);
            // combat movement (QC havocbot_dodge + the retreat-when-outgunned behaviour): strafe to dodge
            // incoming fire and, if much weaker than the enemy, back away while still facing it.
            move = CombatMovement(enemy, move, now);
        }
        else if (Nav.Current is Vector3 goalPos)
        {
            // no enemy: aim along the (flattened) move direction toward the goal (QC bot_aimdir with 0 deviation)
            Vector3 lookDir = goalPos - (bot.Origin + Aim.ViewOffset);
            lookDir.Z = 0f;
            Aim.AimAt(lookDir, bot.Origin, Skill, dt, now, 0f, hasEnemy: false);
        }

        // 5) assemble input and step physics
        var input = new MovementInput
        {
            ViewAngles = Aim.ViewAngles,
            MoveValues = move,
            FrameTime = dt,
            ButtonJump = Nav.WantJump,
            ButtonCrouch = Nav.WantCrouch,
            ButtonAttack1 = wantAttack,
            ButtonAttack2 = false,
        };
        LastInput = input;

        bot.Angles = Aim.ViewAngles;
        bot.ViewOfs = Aim.ViewOffset;

        Movement.Move(bot, input);
    }

    /// <summary>
    /// Aim at the current enemy with projectile lead + skill error, and decide whether to fire this frame
    /// (QC bot_aim + the attack-button block of havocbot_ai). Fires only when the enemy is in range, the aim
    /// is within the deviation cone (the fire timer is armed), and there's a clear line of fire.
    /// </summary>
    private bool AimAndDecideFire(Entity enemy, float dt, float now)
    {
        var enemyCenter = (enemy.AbsMin != enemy.AbsMax)
            ? (enemy.AbsMin + enemy.AbsMax) * 0.5f
            : enemy.Origin + enemy.ViewOfs;

        float shotSpeed = CurrentShotSpeed();
        Vector3 lead = Aim.ShotLead(enemyCenter, enemy.Velocity, shotSpeed);
        Vector3 dir = lead - Aim.ShotOrigin;

        // Lobbed weapons (mortar/nade): arc the shot up to account for gravity drop over the flight time
        // (QC findtrajectorywithleading). Adds the vertical compensation onto the straight-line lead dir.
        if (shotSpeed > 0f && CurrentWeaponIsLobbed())
            dir = Aim.BallisticArc(lead, shotSpeed, ProjectileGravity());

        float maxDev = Aim.MaxFireDeviation(lead, Skill, accurate: shotSpeed <= 0f);
        Aim.AimAt(dir, Bot.Origin, Skill, dt, now, maxDev, hasEnemy: true);

        // line of fire check (QC traceline shotorg -> enemy center): don't shoot a wall/teammate
        var tr = Api.Trace.Trace(Aim.ShotOrigin, Vector3.Zero, Vector3.Zero, enemyCenter, MoveFilter.Normal, Bot);
        bool clear = tr.Fraction >= 1f || ReferenceEquals(tr.Ent, enemy) || (tr.Ent is not null && ShouldAttack(Bot, tr.Ent));
        if (!clear)
            return false;

        return Aim.ShouldFire(now);
    }

    // combat-movement state (QC havocbot_dodge: a strafe direction that flips on a clock).
    private float _dodgeFlipTime;
    private float _dodgeSign = 1f;

    /// <summary>
    /// Adjust the wish-move while engaging an enemy (QC <c>havocbot_dodge</c> + the retreat behaviour): add a
    /// perpendicular strafe (dodging) whose direction flips on a skill-scaled clock, and bias the move toward
    /// or away from the enemy by health advantage — a healthier bot closes in, an outgunned one backs off.
    /// Returns the blended local-frame move (X forward, Y side), preserving the navigation's vertical/jump.
    /// </summary>
    private Vector3 CombatMovement(Entity enemy, Vector3 navMove, float now)
    {
        // flip the strafe direction periodically (lower skill = slower, more predictable dodging).
        if (now >= _dodgeFlipTime)
        {
            _dodgeFlipTime = now + 0.4f + (float)_rng.NextDouble() * (1.2f - System.Math.Min(Skill, 10f) * 0.1f);
            _dodgeSign = _rng.Next(2) == 0 ? -1f : 1f;
        }

        // health advantage: >0 means we're stronger (close in), <0 weaker (retreat).
        float myHp = Bot.Health + Bot.GetResource(ResourceType.Armor);
        float enHp = enemy.Health + enemy.GetResource(ResourceType.Armor);
        float advantage = (myHp - enHp) / 150f; // QC-style 150 normalizer
        float closeBias = System.Math.Clamp(advantage, -1f, 1f); // +1 charge, -1 flee

        // base combat move: forward = closeBias (toward/away), side = strafe; keep some navigation pull so the
        // bot still drifts toward its goal/items while fighting.
        float fwd = closeBias;
        float side = _dodgeSign * 0.8f;

        var combat = new Vector3(fwd, side, 0f);
        if (combat != Vector3.Zero) combat = QMath.Normalize(combat);

        // blend: mostly combat movement, a little of the navigation move, scaled to run speed. Preserve the
        // navigation's vertical component (jump-up onto ledges) untouched.
        Vector3 navLocal = navMove == Vector3.Zero ? Vector3.Zero : QMath.Normalize(navMove);
        Vector3 blended = QMath.Normalize(combat * 0.75f + new Vector3(navLocal.X, navLocal.Y, 0f) * 0.25f);
        float speed = Nav.MaxSpeed;
        return new Vector3(blended.X * speed, blended.Y * speed, navMove.Z);
    }

    /// <summary>The weapon the bot has chosen to fire (QC <c>.switchweapon</c>), set by <see cref="ChooseWeapon"/>.</summary>
    public Weapon? ChosenWeapon { get; private set; }

    /// <summary>
    /// Pick the best owned weapon for the current engagement range (QC <c>havocbot_chooseweapon</c>): with no
    /// enemy, hold a mid-range weapon; with an enemy, prefer a hitscan weapon at long range and a splash
    /// weapon up close (the QC close/mid/far distance buckets, default thresholds from
    /// <c>bot_ai_custom_weapon_priority_distances</c> "300 1000"). Among the candidates of the preferred type
    /// it takes the highest-impulse (strongest) owned weapon. Sets <see cref="ChosenWeapon"/> and equips it
    /// via <see cref="Inventory"/> so the weapon-frame + shot-speed read use it.
    /// </summary>
    public void ChooseWeapon(Entity? enemy)
    {
        // Resolve the bot's owned weapons to Weapon descriptors (OwnedWeapons is the spawn-filled NetName set;
        // also fold in the WepSet in case the inventory path granted any).
        EnsureWepSetSynced();
        bool wantHitscan, wantSplash;

        if (enemy is null)
        {
            wantHitscan = false; wantSplash = false; // mid-range: any usable weapon
        }
        else
        {
            float dist = (enemy.Origin - Bot.Origin).Length();
            float distClose = 300f, distFar = 1000f;
            ReadDistances(ref distClose, ref distFar);
            if (dist > distFar) { wantHitscan = true; wantSplash = false; }       // far: hitscan
            else if (dist <= distClose) { wantHitscan = false; wantSplash = true; } // close: splash
            else { wantHitscan = false; wantSplash = false; }                       // mid: any
        }

        // Pass 1: prefer the requested type; Pass 2: any owned weapon (fallback).
        Weapon? best = PickOwned(wantHitscan, wantSplash) ?? PickOwned(false, false);

        if (best is not null)
        {
            ChosenWeapon = best;
            if (Bot.OwnedWeaponSet.Has(best))
                Inventory.SwitchWeapon(Bot, best);
        }
    }

    /// <summary>Pick the highest-impulse owned weapon matching the type preference (hitscan/splash/any).</summary>
    private Weapon? PickOwned(bool wantHitscan, bool wantSplash)
    {
        Weapon? best = null;
        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is null) continue;
            bool isHitscan = (w.SpawnFlags & WeaponFlags.TypeHitscan) != 0;
            bool isSplash = (w.SpawnFlags & WeaponFlags.TypeSplash) != 0;
            if (wantHitscan && !isHitscan) continue;
            if (wantSplash && !isSplash) continue;
            if (best is null || w.Impulse > best.Impulse) best = w;
        }
        return best;
    }

    /// <summary>Make sure the WepSet mirrors the spawn-filled OwnedWeapons NetName set (so Inventory works).</summary>
    private void EnsureWepSetSynced()
    {
        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is not null && !Bot.OwnedWeaponSet.Has(w))
                Bot.OwnedWeaponSet.Add(w);
        }
    }

    private static void ReadDistances(ref float close, ref float far)
    {
        string s = Api.Cvars.GetString("bot_ai_custom_weapon_priority_distances");
        if (string.IsNullOrWhiteSpace(s)) return;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
        {
            close = MathF.Min(a, b);
            far = MathF.Max(a, b);
        }
    }

    /// <summary>
    /// Projectile speed of the bot's chosen weapon (QC W_WeaponSpeedFactor * WEP_CVAR speed): read from the
    /// weapon's primary-fire balance cvar. Returns 0 (hitscan, no lead) for a hitscan weapon or an unknown
    /// projectile speed. Drives the aim lead in <see cref="AimAndDecideFire"/>.
    /// </summary>
    private float CurrentShotSpeed()
    {
        Weapon? w = ChosenWeapon;
        if (w is null)
            return DefaultShotSpeed;
        if ((w.SpawnFlags & WeaponFlags.TypeHitscan) != 0)
            return 0f; // hitscan: aim straight at the target
        float s = Api.Cvars.GetFloat($"g_balance_{w.NetName}_primary_speed");
        return s > 0f ? s : DefaultShotSpeed;
    }

    /// <summary>True if the chosen weapon lobs under gravity (mortar/nade), so the aim should arc the shot.</summary>
    private bool CurrentWeaponIsLobbed()
    {
        Weapon? w = ChosenWeapon;
        if (w is null) return false;
        // grenade/mortar-style splash weapons that aren't hitscan and have a gravity cvar set.
        if ((w.SpawnFlags & WeaponFlags.TypeHitscan) != 0) return false;
        return Api.Cvars.GetFloat($"g_balance_{w.NetName}_primary_gravity") > 0f;
    }

    /// <summary>The gravity acting on the chosen weapon's projectile (QC its gravity factor × sv_gravity).</summary>
    private float ProjectileGravity()
    {
        Weapon? w = ChosenWeapon;
        float gravFactor = w is null ? 1f : Api.Cvars.GetFloat($"g_balance_{w.NetName}_primary_gravity");
        if (gravFactor <= 0f) gravFactor = 1f;
        return gravFactor * Cvars.Gravity;
    }

    /// <summary>
    /// Pick the nearest attackable enemy with line of sight (QC havocbot_chooseenemy). Keeps the current
    /// enemy if it's still valid; otherwise scans players on the detection clock. Stores the result on
    /// <c>Bot.Enemy</c>.
    /// </summary>
    private void ChooseEnemy(float now)
    {
        var current = Bot.Enemy;
        if (current is { IsFreed: false } && ShouldAttack(Bot, current))
        {
            // keep tracking; re-evaluate on the slower clock
            if (now < _chooseEnemyTime)
                return;
        }
        else
        {
            Bot.Enemy = null;
        }

        if (now < _chooseEnemyTime)
            return;
        _chooseEnemyTime = now + (Skill > BotAim.SuperbotSkill ? 0.1f : EnemyDetectionInterval);

        Vector3 eye = Bot.Origin + Aim.ViewOffset;
        Entity? best = null;
        float bestRating = EnemyDetectionRadius * EnemyDetectionRadius;

        foreach (var e in Players())
        {
            if (!ShouldAttack(Bot, e)) continue;
            var center = (e.AbsMin != e.AbsMax) ? (e.AbsMin + e.AbsMax) * 0.5f : e.Origin + e.ViewOfs;
            float d2 = (center - eye).LengthSquared();
            if (d2 >= bestRating) continue;

            // PVS pre-filter (QC checkpvs): a target in a non-visible cluster can't possibly be seen, so skip
            // the expensive traceline. Conservative (no false negatives) and a no-op on an unvised map.
            if (!Api.Trace.CheckPvs(eye, center)) continue;

            // require line of sight (QC traceline; trace_ent == it || trace_fraction >= 1)
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, center, MoveFilter.Normal, Bot);
            if (tr.Fraction >= 1f || ReferenceEquals(tr.Ent, e))
            {
                best = e;
                bestRating = d2;
            }
        }

        Bot.Enemy = best;
    }

    /// <summary>
    /// Should <paramref name="self"/> attack <paramref name="targ"/>? — a faithful port of QC
    /// <c>bot_shouldattack</c> (server/bot/default/aim.qc): filters self and teammates (in a team game), the
    /// neutral team in a team game (no FFA targets), bots when <c>bot_ignore_bots</c> is set, and
    /// dead/frozen/non-damageable/notarget/alpha-invisible targets. A target "in chat" is spared unless
    /// <c>bot_typefrag</c> is on. Items are never attacked.
    /// </summary>
    public static bool ShouldAttack(Entity self, Entity targ)
    {
        bool teamplay = Cvars.Teamplay;

        // same team: never attack self; in a team game never attack a real teammate.
        if (targ.Team == self.Team)
        {
            if (ReferenceEquals(targ, self)) return false;
            if (teamplay && targ.Team != 0f) return false;
        }

        if (teamplay)
        {
            // in a team game, only attack players that have a (different) team — no neutral targets.
            if (targ.Team == 0f) return false;
        }
        else if (Cvars.Bool("bot_ignore_bots") && targ is Player { IsBot: true })
        {
            return false; // FFA + ignore-bots: leave other bots alone
        }

        if (targ.IsFreed) return false;
        if (targ.TakeDamage == DamageMode.No) return false;
        if (targ.DeadState != DeadFlag.No) return false;
        if ((targ.Flags & EntFlags.NoTarget) != 0) return false;
        if ((targ.Flags & EntFlags.Item) != 0) return false; // only players/monsters

        // frozen targets (QC STAT(FROZEN)) aren't worth shooting.
        if (StatusEffectsCatalog.Frozen is { } fr && StatusEffectsCatalog.Has(targ, fr)) return false;

        return true;
    }
}
