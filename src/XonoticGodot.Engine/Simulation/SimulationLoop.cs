using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The deterministic 72 Hz fixed-tick simulation loop — the C# successor to Darkplaces'
/// SV_Frame accumulator (sv_main.c:2600) driving SV_Physics (sv_phys.c:3013). Replaces the
/// frame-rate-coupled Godot physics with a re-runnable fixed timestep (ADR-0004).
///
/// Per tick it reproduces the observable SV_Physics order (spec §2):
///   StartFrame → (each client: PreThink / movement / PostThink) → non-client MOVETYPE integrators
///   (each of which runs its due think) → EndFrame, then advances <see cref="Time"/> by the tick.
///
/// The host supplies the gameplay callbacks (StartFrame/EndFrame hooks, the per-client move) because
/// player movement and frame logic live in the ported QuakeC layer, not the engine (spec §"Why this
/// is tractable"). The engine owns the tick, the integrators, the trace service, and the
/// FL_ONGROUND/groundentity bookkeeping.
/// </summary>
public sealed class SimulationLoop
{
    /// <summary>sys_ticrate = 1/72 s ≈ 0.0138889 (sv_main.c:166). The fixed tick length.</summary>
    public const float TicRate = 1f / 72f;

    /// <summary>DP sv_maxphysicsframesperserverframe-style HARD cap: never run more than this many ticks per
    /// Advance, and the spiral-of-death backlog threshold past which the accumulator is dropped.</summary>
    public const int MaxTicksPerAdvance = 16;

    /// <summary>
    /// SOFT per-render-frame catch-up cap (B3). Defaults to the hard <see cref="MaxTicksPerAdvance"/>; the
    /// interactive client-hosted path lowers it (e.g. 4) so a hitch isn't immediately FOLLOWED by a frame that
    /// runs many ticks of sim work — itself a second missed vblank. With a lower cap the small backlog instead
    /// drains over the next few render frames (a brief, near-invisible slow-motion) rather than spiking one
    /// frame. Headless dedicated servers keep the full cap (no vblank to miss, catch up fast). Always clamped to
    /// [1, MaxTicksPerAdvance].
    /// </summary>
    public int MaxTicksPerFrame { get; set; } = MaxTicksPerAdvance;

    private readonly EngineServices _services;
    private readonly EntityService _entities;
    private readonly PhysicsContext _physics;

    /// <summary>Cached <see cref="RunThink"/> delegate — a method-group argument would allocate a fresh
    /// <c>Func</c> per entity per tick (~19 KB/tick on a populated map).</summary>
    private readonly Func<Entity, bool> _runThink;

    private float _accumulator;

    /// <summary>Current simulation time in seconds (sv.time). Advances by <see cref="FrameTime"/> each tick.</summary>
    public float Time { get; private set; }

    /// <summary>The current tick length (sv.frametime). Equal to <see cref="TicRate"/> while running.</summary>
    public float FrameTime { get; private set; } = TicRate;

    /// <summary>The collision world (static map geometry) this loop traces against.</summary>
    public CollisionWorld World { get; }

    public EngineServices Services => _services;
    public PhysicsContext Physics => _physics;

    // --- host-supplied frame hooks (the QC StartFrame/EndFrame and per-client move) ---

    /// <summary>QC StartFrame — fired once at the top of each tick (self/other = world, time/frametime set).</summary>
    public Action? StartFrame { get; set; }

    /// <summary>QC EndFrame — fired once at the end of each tick after all entities moved.</summary>
    public Action? EndFrame { get; set; }

    /// <summary>
    /// Per-client move for one tick (SV_Physics_ClientEntity: PreThink → SV_PlayerPhysics/movement →
    /// PostThink). The host drives the ported QuakeC player physics here; the engine just calls it in
    /// order before the non-client integrators. The argument is the client entity.
    /// </summary>
    public Action<Entity>? ClientMove { get; set; }

    /// <summary>The set of client (player) entities, simulated first each tick. Host maintains this.</summary>
    public List<Entity> Clients { get; } = new();

    public SimulationLoop(CollisionWorld world)
    {
        World = world;
        _services = new EngineServices(world);
        _entities = _services.EntityTable;
        _physics = new PhysicsContext(_services.TraceImpl, _entities.LinkEdict)
        {
            FrameTime = TicRate,
            Gravity = 800f,
            Entities = () => _entities.All,
            EntitiesInBox = _entities.EntitiesInBox, // D1: grid broadphase for the trigger-touch pass
            SetOriginEpoch = () => _entities.SetOriginEpoch,
            PlaySound = (e, ch, sample) => _services.SoundImpl.Play(e, ch, sample),
        };
        FrameTime = TicRate;
        _runThink = RunThink;
    }

    /// <summary>Construct around an externally-built services facade (e.g. when the host owns it).</summary>
    public SimulationLoop(EngineServices services, CollisionWorld world)
    {
        World = world;
        _services = services;
        _entities = services.EntityTable;
        _physics = new PhysicsContext(services.TraceImpl, _entities.LinkEdict)
        {
            FrameTime = TicRate,
            Gravity = 800f,
            Entities = () => _entities.All,
            EntitiesInBox = _entities.EntitiesInBox, // D1: grid broadphase for the trigger-touch pass
            SetOriginEpoch = () => _entities.SetOriginEpoch,
        };
        FrameTime = TicRate;
        _runThink = RunThink;
    }

    /// <summary>Publish this loop's facade as the ambient <see cref="Api.Services"/> (one world per process).</summary>
    // S5 thread-affinity note: <see cref="Advance"/>/<see cref="Tick"/> are NOT internally synchronised — they
    // assume a single owning thread mutating the entity table. On the listen/host path with sv_threaded 1, the
    // ONLY thread that calls Advance/Tick is the dedicated server-sim worker (game/net/ServerThread.cs); the main
    // thread's client prediction is serialised against the whole ServerNet.Tick via the host's single _simGate
    // lock, so the two never touch this loop's state concurrently. With sv_threaded 0 (default) the host thread
    // calls Advance/Tick directly with no lock — unchanged. InstallAsAmbient is unused on the listen path (the
    // host wires Api.Services via GameWorld.Boot / Api.SetThreadServices instead).
    public void InstallAsAmbient() => Api.Services = _services;

    /// <summary>World gravity in u/s² (sv_gravity, default 800). Mirrors into the physics context.</summary>
    public float Gravity
    {
        get => _physics.Gravity;
        set => _physics.Gravity = value;
    }

    /// <summary>
    /// Accumulate <paramref name="realDelta"/> real seconds and run as many fixed 72 Hz ticks as have
    /// accumulated (capped by <see cref="MaxTicksPerAdvance"/> to avoid a spiral of death). Mirrors the
    /// SV_Frame loop: <c>sv_timer += delta; while (sv_timer &gt; 0 &amp;&amp; count &lt; limit) { sv_timer -= ticrate; Tick(); }</c>.
    /// </summary>
    public int Advance(float realDelta)
    {
        if (realDelta < 0f) realDelta = 0f;
        // clamp a single huge delta (DP caps the per-call timer at 100ms before sub-stepping)
        if (realDelta > 0.25f) realDelta = 0.25f;

        _accumulator += realDelta;

        // SOFT cap (B3): run at most MaxTicksPerFrame ticks this render frame, but DON'T drop the remainder —
        // it drains over the next frames (clamped to the hard MaxTicksPerAdvance so a stale-default can't exceed
        // the spiral guard). On the interactive path (cap 4) this turns a post-hitch 16-tick spike into a few
        // calm frames; with the default cap it's the original behaviour.
        int softLimit = System.Math.Clamp(MaxTicksPerFrame, 1, MaxTicksPerAdvance);
        int ticks = 0;
        while (_accumulator >= TicRate && ticks < softLimit)
        {
            _accumulator -= TicRate;
            Tick();
            ticks++;
        }

        // Spiral-of-death guard: drop the backlog only when it has grown past the HARD cap's worth of ticks —
        // i.e. the machine genuinely can't keep up, not a one-frame catch-up being smoothed by the soft cap.
        // A backlog within MaxTicksPerAdvance is preserved and drained over the next render frames.
        if (_accumulator > MaxTicksPerAdvance * TicRate)
            _accumulator = 0f;

        // The number of fixed ticks that ran this call (0 when the render rate outruns the tick rate). The caller
        // uses this to avoid network/broadcast work on a frame where the world didn't actually advance.
        return ticks;
    }

    /// <summary>Run exactly one fixed tick of SV_Physics. Public so tests can step deterministically.</summary>
    public void Tick()
    {
        FrameTime = TicRate;
        _physics.FrameTime = TicRate;
        _physics.Time = Time;

        // publish the clock for QC-facing code (time/frametime globals)
        _services.ClockImpl.Time = Time;
        _services.ClockImpl.FrameTime = TicRate;

        // 1) StartFrame
        using (Prof.Sample("sim.start"))
            StartFrame?.Invoke();

        // 2) client entities, in order (PreThink → movement → PostThink), via the host callback
        using (Prof.Sample("sim.move"))
        if (ClientMove != null)
        {
            var clients = Clients;
            for (int i = 0; i < clients.Count; i++)
            {
                Entity c = clients[i];
                if (!c.IsFreed)
                {
                    ClientMove(c);
                    MoveTypePhysics.CheckVelocity(c);
                    using (Prof.Sample("move.link"))
                        _entities.LinkEdict(c);
                    // SV_LinkEdict_TouchAreaGrid: the ported player physics (XonoticGodot.Common.PlayerPhysics) does
                    // its OWN slide-move and only dual-dispatches touch on the SOLID it collides with — it can't
                    // see SOLID_TRIGGER volumes (jumppads / teleporters / trigger_hurt / …), which are non-solid
                    // to the sweep. QC fires those via SV_TouchTriggers after the move; reproduce that here so a
                    // player walking through a trigger_push gets launched, a trigger_teleport relocates them, etc.
                    if (!c.IsFreed)
                        using (Prof.Sample("move.touch"))
                            _physics.TouchAreaGrid(c);
                }
            }
        }

        // 3) non-client entity MOVETYPE integrators (each runs its due think)
        var all = _entities.All;
        // snapshot count: entities spawned during this pass move next tick (DP's delayprojectiles
        // behavior — newly spawned ents don't run their move the frame they appear).
        int count = all.Count;
        using (Prof.Sample("sim.integrate"))
        for (int i = 0; i < count; i++)
        {
            Entity e = all[i];
            if (e.IsFreed) continue;
            if (IsClient(e)) continue; // clients handled above
            MoveTypePhysics.RunEntity(_physics, e, _runThink);
        }

        // 4) EndFrame
        using (Prof.Sample("sim.end"))
            EndFrame?.Invoke();

        // 5) advance time (sv.time += sv.frametime), at the very end like DP
        Time += FrameTime;
    }

    private bool IsClient(Entity e)
    {
        if ((e.Flags & EntFlags.Client) != 0) return true;
        // also treat anything in the Clients list as a client
        var clients = Clients;
        for (int i = 0; i < clients.Count; i++)
            if (clients[i] == e) return true;
        return false;
    }

    // =============================================================================================
    // SV_RunThink (sv_phys.c:1015) — fire the entity's think when due; allow multiple thinks/frame.
    // =============================================================================================

    private const int MaxThinkIterations = 128;

    /// <summary>
    /// Run the entity's think if its nextthink is due this tick. Returns false if the entity removed
    /// itself. Fires when <c>0 &lt; nextthink ≤ time + frametime</c>; sets the QC-visible time to
    /// <c>max(now, nextthink)</c> and clears nextthink before calling. Loops while the think keeps
    /// scheduling itself within this frame (capped), reproducing sv_gameplayfix_multiplethinksperframe.
    /// </summary>
    public bool RunThink(Entity ent)
    {
        float frameEnd = Time + FrameTime;

        // not due yet (or never scheduled)
        if (ent.NextThink <= 0f || ent.NextThink > frameEnd)
            return true;

        for (int iter = 0; iter < MaxThinkIterations && !ent.IsFreed; iter++)
        {
            float thinkTime = MathF.Max(Time, ent.NextThink);

            // publish the QC-visible clock as the think time (DP sets PRVM time = max(now, nextthink))
            _services.ClockImpl.Time = thinkTime;
            ent.NextThink = 0f;

            ent.Think?.Invoke(ent);

            // restore the frame clock for subsequent code this tick
            _services.ClockImpl.Time = Time;

            // exit unless the think rescheduled itself strictly later within this frame
            if (ent.NextThink <= thinkTime || ent.NextThink > frameEnd)
                break;
        }

        return !ent.IsFreed;
    }
}
