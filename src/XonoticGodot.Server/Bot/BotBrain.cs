using System;
using System.Collections.Generic;
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

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(Bot_ForbidAttack, this, targ)</c> (server/bot/default/aim.qc:127): a gametype/
    /// mutator veto evaluated at the tail of <see cref="ShouldAttack"/>. Returns true to FORBID the bot from
    /// attacking the target (e.g. Survival forbids attacking a same-status ally so bots don't out their role —
    /// and, with teamkill punishment live, don't suicide). Set by the host on gametype activation; null = no veto.
    /// </summary>
    public static Func<Entity, Entity, bool>? ForbidAttackHook;

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

    // Per-bot wr_aim scratch (QC the actor fields a weapon's wr_aim stamps, e.g. rifle .bot_secondary_riflemooth).
    private BotAimState _botAimState;

    // QC timing fields
    private float _chooseEnemyTime;
    private float _stickEnemyTime;  // QC .havocbot_stickenemy_time (keep current enemy through a brief LOS break)
    private float _strategyTime;
    private float _aimTime;
    private float _chooseWeaponTime;
    private float _nextThink;       // QC .bot_nextthink (think throttle)
    private float _jumpTime;        // QC .bot_jump_time (jump stays held 0.2s for ramp jumps)
    private Entity? _ignoreGoal;    // QC .ignoregoal (a danger-unreachable goal, snubbed for a timeout)
    private float _ignoreGoalTime;  // QC .ignoregoaltime
    private bool _strategyForced;   // QC navigation_goalrating_timeout_force (re-rate on next token hold)

    /// <summary>
    /// QC <c>bot_strategytoken == this</c>: only the token holder may run its role (goal rating + routing)
    /// this frame — exactly one bot per frame across the population (bot.qc:786-813). Defaults to true so a
    /// standalone brain (tests/bench, no <see cref="BotPopulation"/>) keeps re-planning on its own clock.
    /// </summary>
    public bool StrategyTokenHeld = true;

    /// <summary>Fired when the holder actually consumes the token (QC <c>bot_strategytoken_taken = true</c>).</summary>
    public Action? OnStrategyTokenUsed;

    /// <summary>QC bot_think:153-157 latch: this bot already auto-readied during the current warmup stage.
    /// Maintained by <see cref="BotPopulation"/> (cleared when warmup ends so a later warmup re-readies).</summary>
    public bool AutoReadied;

    /// <summary>
    /// QC <c>havocbot_role_timeout</c> (sv_assault.qc:473/509): absolute time at which the Assault role
    /// resets. 0 = unset (will be stamped to <c>time + 120</c> on the next role invocation). Stored here
    /// (not in the static role function) because roles are stateless delegates; each bot owns its own timer.
    /// </summary>
    public float AssaultRoleTimeout;

    /// <summary>
    /// QC <c>havocbot_attack_time</c> (sv_assault.qc:457): absolute time until which the Assault role skips
    /// goal re-rating (commit window). Set to <c>time + 2</c> when the bot has PVS to both the approach
    /// waypoint and the objective wall; cleared to 0 on death. When <c>AssaultAttackTime &gt; now</c> the role
    /// returns early (QC <c>if(this.havocbot_attack_time &gt; time) return;</c>) so the bot keeps its current
    /// route rather than immediately re-rating.
    /// </summary>
    public float AssaultAttackTime;

    /// <summary>
    /// QC <c>havocbot_role_timeout</c> (sv_onslaught.qc:1461): absolute time at which the Onslaught offense role
    /// resets (re-evaluates strategy). 0 = unset (stamped to <c>time + 120</c> on the next role invocation). The
    /// defense/assistant roles are no-ops that reset straight back to offense in Base, so offense is the only role.
    /// </summary>
    public float OnslaughtRoleTimeout;

    /// <summary>
    /// QC <c>havocbot_attack_time</c> (sv_onslaught.qc:1470): absolute time until which the Onslaught offense role
    /// skips goal re-rating (commit window). Set to <c>time + 5</c> when the bot has PVS to both an approach
    /// waypoint and the enemy generator, or <c>time + 2</c> for a control-point approach; cleared to 0 on death.
    /// </summary>
    public float OnslaughtAttackTime;

    /// <summary>
    /// QC the pre-game movement holds (bot_think:80-83 campaign hold + :122-127 countdown): when this returns
    /// true the bot keeps its buttons but emits zero movement. Wired by <see cref="BotPopulation"/> to
    /// <c>time &lt; game_starttime || (g_campaign &amp;&amp; !campaign_bots_may_start)</c>; null = no hold.
    /// </summary>
    public Func<bool>? MovementHold;

    // last input emitted (useful for the host/tests); repeated between throttled thinks (QC's persisted CS movement)
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
    /// Advance the bot one frame (QC havocbot_ai + bot_think) AND apply the produced move via
    /// <see cref="Movement.Move"/> — the standalone/bench entry point (the live server instead pulls
    /// <see cref="ThinkProduce"/> through the per-tick input path so the SAME tick's physics consumes it).
    /// No-op while the bot is dead (the old standalone behavior, kept for BotNavTests/BotPerfBench).
    /// </summary>
    public void Think(Player bot, float dt)
    {
        if (bot.IsDead)
        {
            Nav.ClearRoute();
            bot.Enemy = null;
            return;
        }
        MovementInput input = ThinkProduce(bot, dt);
        Movement.Move(bot, input);
    }

    /// <summary>
    /// The produce-only think (QC <c>bot_think</c> + <c>havocbot_ai</c>, run from the per-client physics step
    /// like ecs sys_phys_ai): assembles this tick's <see cref="MovementInput"/> WITHOUT stepping physics — the
    /// caller (GameWorld's bot input branch) feeds it to the same-tick movement + weapon drivers exactly like a
    /// human usercmd. Throttled by skill (QC bot_think:71-75); between thinks the last command is repeated
    /// (QC's CS(this).movement/buttons persist on the entity). Also writes the bot's own view angles onto the
    /// entity (bots steer by writing Angles; the server never clobbers a bot's aim from input).
    /// </summary>
    public MovementInput ThinkProduce(Player bot, float dt)
    {
        float now = Now;

        // ---- think throttle (QC bot_think:62-75) ----
        if (now < _nextThink)
        {
            MovementInput repeat = LastInput;
            repeat.FrameTime = dt;
            return repeat; // QC: early return leaves the persisted movement/buttons in force
        }

        // [profiling] the heavy think (past the throttle) — bot AI cost attribution for the hitch bench.
        using var _thinkScope = XonoticGodot.Common.Diagnostics.Prof.Sample("bot.think");

        // QC bot_aimdir works on this.v_angle, the SINGLE shared aim state. The port forks the bot's aim into
        // BotAim.ViewAngles, which Emit() copies back onto bot.ViewAngles each think — so any EXTERNAL write to
        // bot.ViewAngles between thinks (notably the damage-pipeline bot-aim shake, PlayerDamage's
        // v_angle jitter) must be folded back into the aim state, or it is clobbered and dead. Re-seed
        // Aim.ViewAngles from the entity at think start: a no-op in the steady state (Emit left them equal),
        // it carries the damage shake into the next turn exactly as QC's bot_aimdir reads the jittered v_angle.
        // Guard the pre-first-Emit case where bot.ViewAngles is still zero: keep the spawn-seeded aim then.
        if (bot.ViewAngles != Vector3.Zero)
            Aim.ViewAngles = bot.ViewAngles;

        // QC bot_god → FL_GODMODE re-stamped every think.
        bot.Flags &= ~EntFlags.GodMode;
        if (Cvars.Bool("bot_god"))
            bot.Flags |= EntFlags.GodMode;

        // QC: SUPERBOT thinks at 0.005; others at bot_ai_thinkinterval * min(14/(skill+14), 1), floor 0.01.
        // (The per-bot bot_aiskill modifier rides on Skill here — the port folds bots.txt's "ai" column into it.)
        if (Skill > BotAim.SuperbotSkill)
            _nextThink = MathF.Max(now, _nextThink) + 0.005f;
        else
        {
            float interval = Cvars.FloatOr("bot_ai_thinkinterval", 0.05f);
            _nextThink = MathF.Max(now, _nextThink)
                + MathF.Max(0.01f, interval * MathF.Min(14f / (Skill + 14f), 1f));
        }

        // ---- button baseline (QC clears all buttons each think; JUMP stays held for ramp jumps) ----
        bool jumpHeld = !bot.IsDead && now < _jumpTime + 0.2f; // QC bot_think:112

        // ---- dead / observer (QC bot_think:129-149 + havocbot_ai:113-119) ----
        // QC havocbot_ai:103 sets bot_strategytoken_taken = true UNCONDITIONALLY when this bot holds the token,
        // BEFORE the dead/frozen early return at :113. So a dead/observer token-holder must STILL consume the
        // token here, or BotPopulation.RotateStrategyToken (gated on _tokenTaken) freezes it on the corpse and
        // the whole population stops re-rating goals (observer = permanently) until this bot respawns.
        if (bot.IsObserver)
        {
            if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
            return Emit(bot, default, jump: false, crouch: false, attack: false, attack2: false, dt);
        }
        if (bot.IsDead)
        {
            if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
            Nav.ClearRoute();
            bot.Enemy = null;
            _strategyForced = true; // QC navigation_goalrating_timeout_force while dead
            // QC havocbot_role_ast_offense/defense: clears havocbot_attack_time on death so the bot doesn't
            // remain committed to an assault push after respawning.
            AssaultAttackTime = 0f;
            // QC: jump must be RELEASED for a frame (DEAD_DYING) so PlayerThink sees the keydown edge, then
            // PRESSED while DEAD_DEAD — that's how a bot asks to respawn through the same DEAD_* machine.
            bool jump = bot.DeadState == DeadFlag.Dead;
            return Emit(bot, Vector3.Zero, jump, crouch: false, attack: false, attack2: false, dt);
        }

        // ---- pre-game / campaign movement hold (QC bot_think:80-83 + :122-127) ----
        if (MovementHold?.Invoke() == true)
            return Emit(bot, Vector3.Zero, jumpHeld, crouch: false, attack: false, attack2: false, dt);

        // 1) target selection (throttled; SUPERBOT reacts fast)
        ChooseEnemy(now);

        // 1b) weapon selection: pick the best owned weapon for the enemy's range (QC havocbot_chooseweapon).
        if (now >= _chooseWeaponTime)
        {
            _chooseWeaponTime = now + ChooseWeaponInterval;
            ChooseWeapon(bot.Enemy);
        }

        // 2) strategy (QC havocbot_ai:52-104): ONLY the strategy-token holder may run its role — one goal
        // search per server frame across all bots. The role re-rates when the slow clock expired, the route is
        // empty, or a clearroute forced a re-plan (QC navigation_goalrating_timeout/_force inside the roles).
        if (StrategyTokenHeld)
        {
            float strategyInterval = Cvars.FloatOr("bot_ai_strategyinterval", 7f);
            if (_strategyForced || !Nav.HasGoal || now >= _strategyTime)
            {
                _strategyTime = now + strategyInterval;
                _strategyForced = false;
                Role(this, _rater);
                if (_rater.HasGoal)
                {
                    var g = _rater.Best;
                    // QC .ignoregoal: skip a goal that danger marked unreachable, for ignoregoal_timeout secs.
                    if (!(g.Target is not null && ReferenceEquals(g.Target, _ignoreGoal) && now < _ignoreGoalTime))
                        using (XonoticGodot.Common.Diagnostics.Prof.Sample("bot.path")) // [profiling] A* route build
                            Nav.SetGoal(bot.Origin, g.Position, Network, g.Target);
                }
            }
            OnStrategyTokenUsed?.Invoke(); // QC bot_strategytoken_taken = true (used this frame)
        }

        // stale goal: the target entity was freed (QC havocbot_ai:106-111).
        if (Nav.GoalEntity is { IsFreed: true })
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3) navigation: steer toward current goal -> wish-move + jump/crouch
        bool onGround = bot.OnGround;
        Vector3 move = Nav.Steer(bot, Aim.ViewAngles.Y, onGround);

        // 3b) no-progress watchdog (QC havocbot_checkgoaldistance): >0.5s without closing on the goal →
        // drop the route and force a re-rate on the next token hold (covers navigation_unstuck's main value).
        if (Nav.HasGoal && Nav.CheckGoalProgress(bot, now))
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }

        // 3c) danger ahead (QC havocbot_movetogoal:1136-1182 → havocbot_checkdanger): probe the ground under
        // the point we're about to occupy; lava/slime/void/cliff → brake; a trigger_hurt under a high goal →
        // the goal is unreachable (clear + ignore it for bot_ai_ignoregoal_timeout).
        bool dangerBrakeEngaged = false; // QC do_break/evadedanger this frame (forbids bunnyhop, havocbot.qc:1315)
        if (Nav.Current is Vector3 cur)
        {
            Vector3 flat = new(cur.X - bot.Origin.X, cur.Y - bot.Origin.Y, 0f);
            Vector3 flatdir = flat.LengthSquared() > 0f ? QMath.Normalize(flat) : Vector3.Zero;
            Vector3 offset = bot.Velocity.Length() > 32f ? bot.Velocity * 0.2f : flatdir * 32f;
            Vector3 eye = bot.Origin + Aim.ViewOffset;
            int r = BotDanger.CheckDanger(bot, eye, eye + offset, cur.Z, Nav.Mins, Nav.Maxs,
                onGround, jumpHeld || Nav.WantJump, moving: move != Vector3.Zero, committed: false);
            bool danger = r is > 0 and < 4;
            if (r == 4)
            {
                if (cur.Z > bot.Origin.Z + BotNavigation.JumpStepHeightLive)
                {
                    // goal probably on an upper platform — unreachable (QC: clearroute + ignoregoal).
                    _ignoreGoal = Nav.GoalEntity;
                    _ignoreGoalTime = now + Cvars.FloatOr("bot_ai_ignoregoal_timeout", 3f);
                    Nav.ClearRoute();
                    _strategyForced = true;
                }
                else
                    danger = true;
            }
            if (danger)
            {
                dangerBrakeEngaged = true; // QC: do_break/evadedanger set → bunnyhop forbidden this frame
                // QC do_break: back off along -velocity (the port folds the AI_STATUS_DANGER_AHEAD evade into
                // the brake; the lateral evade vector is a documented simplification).
                move = Nav.WorldToLocalMove(-bot.Velocity, Aim.ViewAngles.Y);
                if (Nav.GoalEntity is Player) // QC: a player goal past danger is unreachable
                {
                    _ignoreGoal = Nav.GoalEntity;
                    _ignoreGoalTime = now + Cvars.FloatOr("bot_ai_ignoregoal_timeout", 3f);
                    Nav.ClearRoute();
                    _strategyForced = true;
                }
            }
        }

        // 4) aim
        bool wantAttack = false;
        bool wantAttack2 = false;
        Aim.UpdateShotVectors(bot.Origin);
        if (now >= _aimTime)
            _aimTime = now + AimInterval;

        var enemy = bot.Enemy;
        if (enemy is { IsFreed: false })
        {
            wantAttack = AimAndDecideFire(enemy, dt, now);
            // QC per-weapon wr_aim: some weapons route the bot's shot onto the SECONDARY fire by range (e.g. the
            // MachineGun presses its long-range no-spread burst beyond a skill-scaled distance). When the chosen
            // weapon wants secondary at this range, move the already-decided shot from ATCK to ATCK2 (Base wr_aim
            // sets exactly one of the two buttons).
            if (wantAttack && ChosenWeapon is { } w)
            {
                // QC wr_aim re-rolls random() each decision and persists its toggle on the actor (the rifle's
                // bot_secondary_riflemooth); carry that per-bot state + a fresh draw into the weapon's wr_aim.
                _botAimState.Random01 = (float)_rng.NextDouble();
                _botAimState.Actor = bot; // QC wr_aim's `actor` — an ammo-conditional wr_aim (Vaporizer) reads it
                if (w.BotWantsSecondary((enemy.Origin - bot.Origin).Length(), Skill, ref _botAimState))
                {
                    wantAttack = false;
                    wantAttack2 = true;
                }
            }
            // QC wr_aim auto-detonation (devastator.qc:360-450): regardless of whether the bot is taking a shot
            // this frame, a skill >= 2 bot may remote-detonate its in-flight rockets when the predicted splash
            // damage favours it. This runs independently of the primary-fire decision and, when it fires the
            // secondary, suppresses the primary ("don't fire a new shot at the same time", devastator.qc:447-449).
            if (ChosenWeapon is { } dw
                && dw.BotWantsDetonate(bot, new WeaponSlot(0), Skill, Players(), ShouldAttack))
            {
                wantAttack = false;
                wantAttack2 = true;
            }
            // QC havocbot_ai:142 `if (autocvar_bot_nofire || IS_INDEPENDENT_PLAYER(this))`: bot_nofire AND
            // independent-players modes (Race/CTS solo time-trial) suppress the fire button while leaving aim +
            // combat movement intact. The CTS gametype forces _independent_players=1 (Cts.Activate), so a CTS bot
            // runs the course without shooting at other runners.
            if (Cvars.Bool("bot_nofire") || Cvars.Bool("_independent_players"))
            {
                wantAttack = false;
                wantAttack2 = false;
            }
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

        // 4b) idle reload (QC havocbot_ai:181-211): when NOT attacking (no enemy this frame), keep the held
        // weapon topped up so it never runs dry mid-fight, and — for higher-skill bots — pre-rotate to an
        // owned reloadable weapon whose magazine isn't full so it can be reloaded next.
        if (enemy is not { IsFreed: false })
            IdleReload(bot, now);

        // 5) assemble the command (the caller's same-tick physics/weapon drivers consume it).
        // QC havocbot.qc:1315: bunnyhop is suppressed when the danger brake (do_break/evadedanger) engaged this
        // frame. Steer keeps its bunnyhop intent in WantBunnyhop (separate from WantJump) precisely so this
        // late, post-danger-probe gate can be applied — a braking bot no longer keeps the jump button held.
        bool wantJump = Nav.WantJump || (Nav.WantBunnyhop && !dangerBrakeEngaged);
        if (wantJump)
            _jumpTime = now;        // QC bot_jump_time: keep jump held ~0.2s so ramp jumps register
        return Emit(bot, move, wantJump || jumpHeld, Nav.WantCrouch, wantAttack, wantAttack2, dt);
    }

    /// <summary>Stamp the bot's view onto the entity and record + return the assembled command.</summary>
    private MovementInput Emit(Player bot, Vector3 move, bool jump, bool crouch, bool attack, bool attack2, float dt)
    {
        var input = new MovementInput
        {
            ViewAngles = Aim.ViewAngles,
            MoveValues = move,
            FrameTime = dt,
            ButtonJump = jump,
            ButtonCrouch = crouch,
            ButtonAttack1 = attack,
            ButtonAttack2 = attack2,
        };
        LastInput = input;

        // bots steer by writing their OWN angles (GameWorld's input path deliberately skips bots).
        bot.Angles = Aim.ViewAngles;
        bot.ViewAngles = Aim.ViewAngles;
        bot.ViewOfs = Aim.ViewOffset;
        return input;
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

        // QC wr_aim's shot_accurate argument: hitscan shots are accurate by default; a weapon may override (the
        // Devastator relaxes accuracy when its rockets are guidable — devastator.qc:356-357).
        bool accurate = ChosenWeapon?.BotAimAccurate() ?? (shotSpeed <= 0f);
        float maxDev = Aim.MaxFireDeviation(lead, Skill, accurate);
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
    /// <c>bot_ai_custom_weapon_priority_distances</c> "300 850"). Among the candidates of the preferred type
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
            float distClose = 300f, distFar = 850f; // bot_ai_custom_weapon_priority_distances ships "300 850"
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

    /// <summary>
    /// QC <c>havocbot_ai</c> idle-reload block (havocbot.qc:181-211), run only while NOT attacking: keep the
    /// bot's guns loaded between fights so a reloadable weapon doesn't run dry mid-combat. Two skill tiers,
    /// matching QC:
    /// <list type="bullet">
    /// <item>skill ≥ 2 — if the currently-held weapon's magazine isn't full, reload it now (drives the same
    /// reload impulse a human's +reload would, via <see cref="WeaponImpulses"/> impulse 20).</item>
    /// <item>skill ≥ 5 — if we're not mid-reload, pre-rotate to any owned reloadable weapon whose stored clip
    /// isn't full, so the held-weapon branch reloads it on the following think (QC sets <c>m_switchweapon</c>).</item>
    /// </list>
    /// </summary>
    private void IdleReload(Player bot, float now)
    {
        if (Skill < 2f) return; // bots below this skill never reload on purpose (QC skill >= 2 gate)

        var slot = new WeaponSlot(0);
        var st = bot.WeaponState(slot);

        // skill >= 2: reload the held weapon if its magazine isn't full. ClipLoad < 0 is QC's "scheduled for
        // reload" sentinel (a reload is already pending), so only act on a genuinely partial clip.
        if (st.ClipLoad >= 0 && st.ClipLoad < st.ClipSize)
        {
            WeaponImpulses.Handle(bot, ReloadImpulse); // QC CS(this).impulse = IMP_weapon_reload.impulse
            return;                                    // QC: don't also pre-rotate while a reload is starting
        }

        // skill >= 5: if we're not reloading a weapon already, switch to any owned reloadable weapon whose
        // stored clip isn't full, so the held-weapon branch reloads it next think (QC havocbot.qc:200-209).
        if (Skill < 5f) return;
        if (st.ClipLoad < 0) return; // already reloading (sentinel) — don't switch away mid-reload

        foreach (string netName in Bot.OwnedWeapons)
        {
            Weapon? w = Weapons.ByName(netName);
            if (w is null || (w.SpawnFlags & WeaponFlags.Reloadable) == 0) continue;
            if (ReferenceEquals(w, ChosenWeapon)) continue; // the held weapon is handled by the skill>=2 branch
            if (Weapon.GetWeaponLoad(st, w.RegistryId) < w.ReloadingAmmo())
            {
                if (Bot.OwnedWeaponSet.Has(w))
                    Inventory.SwitchWeapon(Bot, w);
                break; // QC: rotate to the first under-loaded weapon found, then stop
            }
        }
    }

    private const int ReloadImpulse = 20; // common/impulses/all.qh IMP_weapon_reload

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
        float s = Api.Cvars.GetFloat(BalanceNames(w).Speed);
        // QC wr_aim may override the speed the bot leads by (the Devastator leads as if its rocket flew much
        // faster, "simulating rocket guide" — devastator.qc:351-355). Default returns the cvar speed unchanged.
        return w.BotAimShotSpeed(s > 0f ? s : DefaultShotSpeed);
    }

    /// <summary>True if the chosen weapon lobs under gravity (mortar/nade), so the aim should arc the shot.</summary>
    private bool CurrentWeaponIsLobbed()
    {
        Weapon? w = ChosenWeapon;
        if (w is null) return false;
        // grenade/mortar-style splash weapons that aren't hitscan and have a gravity cvar set.
        if ((w.SpawnFlags & WeaponFlags.TypeHitscan) != 0) return false;
        return Api.Cvars.GetFloat(BalanceNames(w).Gravity) > 0f;
    }

    /// <summary>The gravity acting on the chosen weapon's projectile (QC its gravity factor × sv_gravity).</summary>
    private float ProjectileGravity()
    {
        Weapon? w = ChosenWeapon;
        float gravFactor = w is null ? 1f : Api.Cvars.GetFloat(BalanceNames(w).Gravity);
        if (gravFactor <= 0f) gravFactor = 1f;
        return gravFactor * Cvars.Gravity;
    }

    // Cached per-weapon balance-cvar names: the aim helpers above run inside EVERY bot think, and the
    // $"g_balance_{netname}_primary_*" interpolations allocated a fresh string per call (a steady per-bot
    // per-tick churn). Weapon registry names are fixed after boot, so the names are interned once. Shared by
    // all brains; sim-thread only (single-threaded by contract, like the Prof scopes).
    private static readonly Dictionary<string, (string Speed, string Gravity)> _balanceNameCache =
        new(StringComparer.Ordinal);

    private static (string Speed, string Gravity) BalanceNames(Weapon w)
    {
        if (!_balanceNameCache.TryGetValue(w.NetName, out (string Speed, string Gravity) names))
            _balanceNameCache[w.NetName] = names =
                ($"g_balance_{w.NetName}_primary_speed", $"g_balance_{w.NetName}_primary_gravity");
        return names;
    }

    /// <summary>
    /// Pick the nearest attackable enemy with line of sight (QC havocbot_chooseenemy). Keeps the current
    /// enemy if it's still valid; otherwise scans players on the detection clock. Stores the result on
    /// <c>Bot.Enemy</c>.
    /// </summary>
    private void ChooseEnemy(float now)
    {
        bool superbot = Skill > BotAim.SuperbotSkill;

        var current = Bot.Enemy;
        if (current is { IsFreed: false })
        {
            if (!ShouldAttack(Bot, current))
            {
                // enemy died / became invalid → drop it and re-scan now (QC havocbot_chooseenemy:1342-1349).
                Bot.Enemy = null;
                _chooseEnemyTime = now;
            }
            else if (_stickEnemyTime != 0f && now < _stickEnemyTime)
            {
                // QC sticky-enemy window (bot_ai_enemydetectioninterval_stickingtoenemy, default 4): keep
                // tracking the current enemy through a brief LOS break (rounding a pillar/corner) as long as
                // it's still close + still traces clear, so the bot doesn't instantly forget a target that
                // ducked behind cover for a moment (havocbot_chooseenemy:1350-1366).
                Vector3 eyeS = Bot.Origin + Aim.ViewOffset;
                var targPos = (current.AbsMin != current.AbsMax)
                    ? (current.AbsMin + current.AbsMax) * 0.5f : current.Origin + current.ViewOfs;
                var trS = Api.Trace.Trace(eyeS, Vector3.Zero, Vector3.Zero, targPos, MoveFilter.Normal, Bot);
                if ((trS.Fraction >= 1f || ReferenceEquals(trS.Ent, current))
                    && (targPos - Bot.Origin).Length() < 1000f)
                {
                    // remain on this enemy for a short window (QC: chooseenemy_finished = time + 0.5).
                    _chooseEnemyTime = now + 0.5f;
                    return;
                }
                _stickEnemyTime = 0f; // stop preferring this enemy
            }
        }
        else
        {
            Bot.Enemy = null;
        }

        if (now < _chooseEnemyTime)
            return;
        _chooseEnemyTime = now + (superbot ? 0.1f : EnemyDetectionInterval);

        Vector3 eye = Bot.Origin + Aim.ViewOffset;
        Entity? best = null;
        // QC: non-SUPERBOT rates by squared distance (nearest wins); SUPERBOT by bound(50,hp+armor,250)*dist
        // (prefer the weak/close kill) — a LOWER rating is better in both, so the radius² seeds the ceiling.
        float bestRating = EnemyDetectionRadius * EnemyDetectionRadius;

        foreach (var e in Players())
        {
            if (!ShouldAttack(Bot, e)) continue;
            var center = (e.AbsMin != e.AbsMax) ? (e.AbsMin + e.AbsMax) * 0.5f : e.Origin + e.ViewOfs;
            float d2 = (center - eye).LengthSquared();

            // QC SUPERBOT target rating: account for the target's health+armor so a skilled bot prefers
            // finishing a weak/close enemy over a healthy/far one (havocbot_chooseenemy:1409-1426).
            float rating = d2;
            if (superbot)
            {
                float hp = e.Health + e.GetResource(ResourceType.Armor);
                rating = QMath.Bound(50f, hp, 250f) * d2;
            }
            if (rating >= bestRating) continue;

            // PVS pre-filter (QC checkpvs): a target in a non-visible cluster can't possibly be seen, so skip
            // the expensive traceline. Conservative (no false negatives) and a no-op on an unvised map.
            if (!Api.Trace.CheckPvs(eye, center)) continue;

            // require line of sight (QC traceline; trace_ent == it || trace_fraction >= 1)
            var tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, center, MoveFilter.Normal, Bot);
            if (tr.Fraction >= 1f || ReferenceEquals(tr.Ent, e))
            {
                best = e;
                bestRating = rating;
            }
        }

        Bot.Enemy = best;
        // QC: arm the sticky-enemy window so a target acquired now is tracked through a brief LOS break.
        _stickEnemyTime = best is not null
            ? now + Cvars.FloatOr("bot_ai_enemydetectioninterval_stickingtoenemy", 4f)
            : 0f;
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

        // QC bot_shouldattack tail (aim.qc:127): a gametype/mutator may forbid the attack (e.g. Survival's
        // Bot_ForbidAttack vetoes same-status allies). Evaluated last so it only narrows the base eligibility.
        if (ForbidAttackHook is not null && ForbidAttackHook(self, targ)) return false;
        // The Common-side Bot_ForbidAttack mutator chain (QC MUTATOR_CALLHOOK(Bot_ForbidAttack, this, targ)):
        // the powerups mutator forbids attacking a player holding Invisibility (bot stealth).
        if (MutatorHooks.FireBotForbidAttack(self, targ)) return false;

        return true;
    }
}
