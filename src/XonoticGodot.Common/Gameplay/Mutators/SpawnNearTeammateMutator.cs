// Port of common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc

using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Spawn Near Teammate mutator — port of
/// common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc. In team games it biases spawn selection
/// toward spots near a living teammate (so you respawn with backup, not alone), and — when
/// <c>g_spawn_near_teammate_ignore_spawnpoint</c> is set — actually relocates the spawning player to a clear spot
/// right beside a teammate instead of using a map spawnpoint. Enabled by the <c>g_spawn_near_teammate</c> cvar.
///
/// Ported:
///  - Spawn_Score (the implementable core): over the live teammates within
///    <c>g_spawn_near_teammate_distance</c> of a spot (but at least 48u away), pick one at random and raise the
///    spot's priority by SPAWN_PRIO_NEAR_TEAMMATE_FOUND (200), stashing it as the spot's "look-at" teammate; if
///    none, and the spot is the player's own team, raise by SPAWN_PRIO_NEAR_TEAMMATE_SAMETEAM (100). The
///    <c>checkpvs</c> visibility gate is approximated (NOTE).
///  - PlayerSpawn:
///      • the <c>else if(spawn_spot.msnt_lookat)</c> branch — aim the spawned player at the chosen teammate
///        (using <see cref="QMath.FixedVecToAngles"/>, honoring the pitch convention memo), roll-zeroed;
///      • the <c>ignore_spawnpoint</c> relocation branch — for each eligible teammate, trace 6 candidate offsets
///        (a tracebox sideways then a downward tracebox to find the floor) and, on the first clear pair, setorigin
///        the player beside them facing their angles. The "1-team-only-one-player" advantage guard and the
///        per-teammate <c>msnt_timer</c> cooldown are ported.
///
/// The sky-surface reject (Q3SURFACEFLAG_SKY) and the lava/slime/water <c>pointcontents</c> reject ARE now applied
/// (via the trace's surfaceflags + <c>Api.Trace.PointContents</c>), and both the look-at facing and the relocation
/// facing are latched into the networked FixAngle channel so the spawn orientation actually reaches the client.
///
/// NOTE (still deferred / approximated, all flagged inline): <c>checkpvs</c> (PVS visibility), the
/// <c>tracebox_hits_trigger_hurt</c> hurt-volume check, and the nade-in-range reject (needs the projectile entity
/// list) have no clean mutator-reachable equivalent yet, so the relocation does the traces it CAN and skips those
/// rejections — a faithful superset (it may keep a spot QC would have rejected for a hurt trigger / nearby nade).
/// The <c>closetodeath</c> sub-branch uses the player's current origin as the death origin (the port has no
/// <c>.death_origin</c> field reachable here).
/// QC kept <c>.msnt_lookat</c> / <c>.msnt_timer</c> on the spot/player edicts; adding Entity fields is out of this
/// task's edit scope, so both live in <see cref="ConditionalWeakTable{TKey,TValue}"/> maps keyed by the entity.
/// </summary>
[Mutator]
public sealed class SpawnNearTeammateMutator : MutatorBase
{
    /// <summary>QC SPAWN_PRIO_NEAR_TEAMMATE_FOUND (server/spawnpoints.qh:10).</summary>
    public const int PrioNearTeammateFound = 200;

    /// <summary>QC SPAWN_PRIO_NEAR_TEAMMATE_SAMETEAM (server/spawnpoints.qh:11).</summary>
    public const int PrioNearTeammateSameTeam = 100;

    /// <summary>DP SUPERCONTENTS_* water|slime|lava (Base/darkplaces/bspfile.h) — the "liquids" mask used to map
    /// QC <c>pointcontents(p) != CONTENT_EMPTY</c> onto the port's bitmask PointContents (matches PlayerPhysics).</summary>
    private const int SuperContentsLiquidsMask = 0x00000010 | 0x00000020 | 0x00000040;

    public SpawnNearTeammateMutator() => NetName = "spawn_near_teammate";

    // QC: REGISTER_MUTATOR(spawn_near_teammate, expr_evaluate(autocvar_g_spawn_near_teammate)).
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_spawn_near_teammate"));

    // Per-spot chosen teammate (QC .entity msnt_lookat on the spawn spot).
    private static readonly ConditionalWeakTable<Entity, LookRef> _lookAt = new();
    // Per-player relocation cooldown (QC .float msnt_timer).
    private static readonly ConditionalWeakTable<Entity, float[]> _timer = new();

    private sealed class LookRef { public Entity? Value; }

    private HookHandler<MutatorHooks.SpawnScoreArgs>? _onSpawnScore;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onSpawnScore ??= OnSpawnScore;
        _onPlayerSpawn ??= OnPlayerSpawn;
        MutatorHooks.SpawnScore.Add(_onSpawnScore);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
    }

    public override void Unhook()
    {
        if (_onSpawnScore is not null) MutatorHooks.SpawnScore.Remove(_onSpawnScore);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    private static float Timer(Entity e) => _timer.GetValue(e, static _ => new float[1])[0];
    private static void SetTimer(Entity e, float t) => _timer.GetValue(e, static _ => new float[1])[0] = t;

    // MUTATOR_HOOKFUNCTION(spawn_near_teammate, Spawn_Score)
    private bool OnSpawnScore(ref MutatorHooks.SpawnScoreArgs args)
    {
        if (!Scoring.GameScores.Teamplay) return false; // QC: if (!teamplay) return;
        if (Api.Services is null) return false;

        Entity player = args.Player;
        Entity spot = args.Spot;

        // QC: ignore_spawnpoint == 1, or == 2 && the player's cl_spawn_near_teammate cvar → don't bias the score
        // (the relocation branch in PlayerSpawn handles it instead).
        int ignoreMode = (int)Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint");
        if (ignoreMode == 1 || (ignoreMode == 2 && Api.Cvars.GetFloat("cl_spawn_near_teammate") != 0f))
            return false;

        // QC: spawn_spot.msnt_lookat = NULL;
        SetLookAt(spot, null);

        float distance = Api.Cvars.GetFloat("g_spawn_near_teammate_distance");

        // QC RandomSelection over the live teammates in range (between 48u and `distance`). Reservoir pick.
        Entity? chosen = null;
        int count = 0;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (!IsPlayer(it) || ReferenceEquals(it, player) || !SameTeam(it, player) || it.DeadState != DeadFlag.No)
                continue;
            float d = Vector3.Distance(spot.Origin, it.Origin);
            if (d > distance) continue;     // QC: vdist(.., >, distance) → skip
            if (d < 48f) continue;          // QC: vdist(.., <, 48) → skip
            // QC: if(!checkpvs(spawn_spot.origin, it)) continue; — PVS visibility (no mutator-reachable equivalent;
            // skipped, a faithful superset that may keep a not-yet-visible teammate's spot).
            count++;
            if (Prandom.RangeInt(0, count) == 0) chosen = it; // uniform reservoir (QC AddEnt weight 1)
        }

        if (chosen is not null)
        {
            SetLookAt(spot, chosen);
            args.Priority += PrioNearTeammateFound;            // QC spawn_score.x += SPAWN_PRIO_NEAR_TEAMMATE_FOUND
        }
        else if (player.Team == spot.Team)
        {
            args.Priority += PrioNearTeammateSameTeam;         // QC: prefer same team if no near-teammate spot
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(spawn_near_teammate, PlayerSpawn)
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (!Scoring.GameScores.Teamplay) return false; // QC: if (!teamplay) return;
        if (Api.Services is null) return false;

        Entity player = args.Player;
        Entity? spot = args.Spot;

        // QC advantage guard: if any team has exactly 1 player, don't relocate (don't over-help the bigger team).
        int[] perTeam = new int[Teams.All.Length];
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (!IsPlayer(it)) continue;
            for (int i = 0; i < Teams.All.Length; i++)
                if ((int)it.Team == Teams.All[i]) perTeam[i]++;
        }
        foreach (int n in perTeam)
            if (n == 1) return false;

        int ignoreMode = (int)Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint");
        bool ignore = ignoreMode == 1 || (ignoreMode == 2 && Api.Cvars.GetFloat("cl_spawn_near_teammate") != 0f);

        if (ignore)
        {
            RelocateNearTeammate(player);
        }
        else if (spot is not null && GetLookAt(spot) is { } mate)
        {
            // QC: player.angles = vectoangles(spawn_spot.msnt_lookat.origin - player.origin);
            //     player.angles_x = -player.angles.x; player.angles_z = 0;
            // vectoangles + the explicit pitch flip == fixedvectoangles (the round-trippable look-at facing).
            Vector3 ang = QMath.FixedVecToAngles(mate.Origin - player.Origin);
            FaceAngles(player, new Vector3(ang.X, ang.Y, 0f));
        }
        return false;
    }

    /// <summary>
    /// Port of the <c>ignore_spawnpoint</c> relocation branch: for each eligible teammate (passing the chat /
    /// team / health / dead / timer / spawn-shield / weapon-lock gates), trace 6 candidate offsets and, on the
    /// first clear pair, setorigin the player beside them facing their angles, then arm that teammate's cooldown.
    /// </summary>
    private void RelocateNearTeammate(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        float delay = Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint_delay");
        float delayDeath = Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint_delay_death");
        int maxTested = (int)Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint_max");
        bool checkHealth = Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint_check_health") != 0f;
        float regenStable = Api.Cvars.GetFloat("g_balance_health_regenstable");
        bool closeToDeath = Api.Cvars.GetFloat("g_spawn_near_teammate_ignore_spawnpoint_closetodeath") != 0f;

        // QC: if(delay_death) player.msnt_timer = time + delay_death;
        if (delayDeath != 0f) SetTimer(player, now + delayDeath);

        Vector3 plMin = SpawnSystem.PlayerMins, plMax = SpawnSystem.PlayerMaxs;

        Entity? bestMate = null;
        Vector3 bestPos = Vector3.Zero;
        float bestDist2 = float.MaxValue;
        int tested = 0;

        // QC FOREACH_CLIENT_RANDOM over players (random order so the bigger team doesn't always clump the same way).
        var clients = new List<Entity>();
        foreach (Entity it in Api.Entities.FindByClass("player"))
            if (IsPlayer(it)) clients.Add(it);
        Shuffle(clients);

        foreach (Entity mate in clients)
        {
            if (maxTested != 0 && tested >= maxTested) break;

            if (IsChatting(mate)) continue;                                  // QC: PHYS_INPUT_BUTTON_CHAT
            if (!SameTeam(mate, player)) continue;                           // QC: DIFF_TEAM → skip
            if (checkHealth && mate.GetResource(ResourceType.Health) < regenStable) continue;
            if (mate.DeadState != DeadFlag.No) continue;
            if (now < Timer(mate)) continue;
            if (IsSpawnShielded(mate)) continue;                            // QC: StatusEffects_active(SpawnShield)
            if (WeaponLocked(mate)) continue;
            if (ReferenceEquals(mate, player)) continue;

            tested++;

            // QC: when running fast, orient by movement direction, else by view/aim angles.
            Vector3 horizVel = new(mate.Velocity.X, mate.Velocity.Y, 0f);
            float maxSpeed = Api.Cvars.GetFloat("sv_maxspeed");
            Vector3 forward, right, up;
            if (horizVel.Length() > maxSpeed + 50f)
                QMath.AngleVectors(QMath.VecToAngles(horizVel), out forward, out right, out up);
            else
                QMath.AngleVectors(mate.Angles, out forward, out right, out up);

            // The 6 candidate offsets (QC snt_ofs[0..5]): pairs, behind/beside the teammate, never directly ahead.
            Vector3[] ofs =
            {
                up * 64 + right * 128 - forward * 64,
                up * 64 - right * 128 - forward * 64,
                up * 64 + right * 192,
                up * 64 - right * 192,
                up * 64 + right * 64 - forward * 128,
                up * 64 - right * 64 - forward * 128,
            };

            Entity? roundChosen = null;
            Vector3 roundChosenPos = Vector3.Zero;
            int rcount = 0;

            for (int i = 0; i < 6; i++)
            {
                // QC: trace this offset (sideways tracebox → downward tracebox to find floor → floor-ahead check).
                // A null result means the spot was rejected (QC's LABEL(skip) fall-through).
                Vector3? spotPos = TryOffset(mate, ofs[i], forward, up, plMin, plMax);
                if (spotPos is { } good)
                {
                    // QC: RandomSelection_Add(it, 0, null, vertEnd, 1, 1) — a good spot; uniform reservoir per round.
                    rcount++;
                    if (Prandom.RangeInt(0, rcount) == 0) { roundChosen = mate; roundChosenPos = good; }
                }

                // QC: test spots in pairs; after an odd index, if we found one for this teammate, commit/break.
                if (i % 2 == 1 && roundChosen is not null)
                {
                    if (closeToDeath)
                    {
                        // QC dist2 = vlen2(chosen.origin - player.death_origin); keep the nearest to death.
                        // The port has no .death_origin reachable here → use the player's current origin (NOTE).
                        float dist2 = Vector3.DistanceSquared(roundChosen.Origin, player.Origin);
                        if (dist2 < bestDist2)
                        {
                            bestDist2 = dist2;
                            bestPos = roundChosenPos;
                            bestMate = roundChosen;
                        }
                    }
                    else
                    {
                        Place(player, roundChosenPos, roundChosen);
                        SetTimer(roundChosen, now + delay);
                        return; // QC: return — done relocating
                    }
                    break; // QC: don't test the other spots near this teammate; go to the next one
                }
            }
        }

        // QC: closetodeath fallback — relocate to the best (nearest-to-death) spot found across all teammates.
        if (closeToDeath && bestMate is not null)
        {
            Place(player, bestPos, bestMate);
            SetTimer(bestMate, now + delay);
        }
    }

    /// <summary>
    /// Trace one candidate offset around a teammate (the body of QC's per-<c>snt_ofs[i]</c> loop iteration up to the
    /// LABEL(skip)): a sideways tracebox to the offset, a downward tracebox to find the floor, then a floor-ahead
    /// check. Returns the resolved floor position, or null if the spot is rejected. The sky/lava/hurt-trigger/nade
    /// rejections QC also applies here are deferred (see the class doc) — a faithful superset.
    /// </summary>
    private static Vector3? TryOffset(Entity mate, Vector3 ofs, Vector3 forward, Vector3 up, Vector3 plMin, Vector3 plMax)
    {
        if (Api.Services is null) return null;

        // QC: tracebox sideways from the teammate to the offset; needs a clear path.
        TraceResult horiz = Api.Trace.Trace(mate.Origin, plMin, plMax, mate.Origin + ofs, MoveFilter.NoMonsters, mate);
        if (horiz.Fraction != 1.0f) return null;
        Vector3 horizEnd = horiz.EndPos;

        // QC: tracebox down 400u (a laser-jump height) to find the floor; reject void/too-high/startsolid.
        TraceResult vert = Api.Trace.Trace(horizEnd, plMin, plMax, horizEnd - 400f * up, MoveFilter.Normal, mate);
        if (vert.StartSolid) return null;       // inside another player
        if (vert.Fraction == 1.0f) return null; // above void or too high
        Vector3 vertEnd = vert.EndPos;

        // QC: if (trace_dphitq3surfaceflags & Q3SURFACEFLAG_SKY) goto skip; — don't relocate onto a sky leak.
        if ((vert.DpHitQ3SurfaceFlags & MutatorConstants.Q3SurfaceFlagSky) != 0) return null;

        // QC: if (pointcontents(vectical_trace_endpos) != CONTENT_EMPTY) goto skip; — no lava/slime/water (the QC
        // comment names exactly those three as the "annoying" content to avoid). PointContents returns a
        // SUPERCONTENTS bitmask in the port, so test the liquids mask rather than the legacy CONTENT_EMPTY value.
        if ((Api.Trace.PointContents(vertEnd) & SuperContentsLiquidsMask) != 0) return null;

        // QC also rejects hurt-trigger volumes here (tracebox_hits_trigger_hurt) — no clean mutator-reachable
        // equivalent yet (NOTE in the class doc); the geometry + sky + liquids rejects are kept.

        // QC: make sure there's floor (or a wall) ahead so the player won't immediately fall.
        Vector3 floorStart = vertEnd + up * plMax.Z + forward * plMax.X;
        TraceResult floorAhead = Api.Trace.Trace(floorStart, Vector3.Zero, Vector3.Zero,
            floorStart + forward * 100f - up * 128f, MoveFilter.NoMonsters, mate);
        if (floorAhead.Fraction == 1.0f) return null;

        // QC nade-in-range reject (g_nades) needs the projectile entity list — deferred (NOTE).
        return vertEnd;
    }

    /// <summary>QC: setorigin(player, pos); player.angles = mate.angles; player.angles_z = 0 (never spawn tilted).</summary>
    private static void Place(Entity player, Vector3 pos, Entity mate)
    {
        if (Api.Services is not null) Api.Entities.SetOrigin(player, pos);
        else player.Origin = pos;
        FaceAngles(player, new Vector3(mate.Angles.X, mate.Angles.Y, 0f));
    }

    /// <summary>
    /// Force the spawned player to face <paramref name="angles"/> on the respawn edge. QC just writes
    /// <c>player.angles</c> here (PlayerSpawn runs while <c>fixangle</c> is already set from PutPlayerInServer,
    /// so the DP client honors it). The port split orientation into two channels: <c>Angles</c> alone never
    /// reaches the camera — it's overwritten by the client's input view angles next tick — so the spawn facing
    /// must be latched into the networked FixAngle/FixAngleAngles channel (SpawnSystem.PutPlayerInServer:559-560
    /// sets it to the SPAWNPOINT angles; we overwrite that here with the teammate-facing).
    /// </summary>
    private static void FaceAngles(Entity player, Vector3 angles)
    {
        player.Angles = angles;
        player.FixAngle = true;
        player.FixAngleAngles = angles;
    }

    private static void SetLookAt(Entity spot, Entity? mate) =>
        _lookAt.GetValue(spot, static _ => new LookRef()).Value = mate;

    private static Entity? GetLookAt(Entity spot) =>
        _lookAt.TryGetValue(spot, out LookRef? r) ? r.Value : null;

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    /// <summary>QC <c>SAME_TEAM</c> in teamplay: equal, non-zero teams (this mutator only runs when teamplay is on).</summary>
    private static bool SameTeam(Entity a, Entity b) => a.Team != 0f && a.Team == b.Team;

    private static bool IsSpawnShielded(Entity e) =>
        StatusEffectsCatalog.SpawnShield is { } sh && StatusEffectsCatalog.Has(e, sh);

    /// <summary>QC <c>weaponLocked(it)</c>: the player's weapon is locked (e.g. mid-switch / disabled). The port has
    /// no per-player weapon-lock flag reachable here, so this is always false (a faithful subset — it won't skip a
    /// teammate that QC would have for a weapon lock; flagged).</summary>
    private static bool WeaponLocked(Entity e) => false;

    /// <summary>QC <c>PHYS_INPUT_BUTTON_CHAT(it)</c>: the teammate has the chat console open. The headless sim has
    /// no chat-button input plumbed, so this is always false (a faithful subset — flagged like <see cref="WeaponLocked"/>).</summary>
    private static bool IsChatting(Entity e) => false;

    /// <summary>Deterministic Fisher–Yates shuffle (QC FOREACH_CLIENT_RANDOM order) via the shared <see cref="Prandom"/>.</summary>
    private static void Shuffle(List<Entity> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Prandom.RangeInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Faithful port of QC <c>expr_evaluate(string s)</c> (lib/cvar.qh:48). A boolean cvar-expression
    /// interpreter: an optional leading '+' (no-op) or '-' (negate the result); then each whitespace token is a
    /// predicate that must hold (logical AND) — either a comparison <c>var>=x</c> / <c>var&lt;=x</c> /
    /// <c>var&gt;</c> / <c>var&lt;</c> / <c>var==x</c> / <c>var!=x</c> (numeric, via cvar()) or
    /// <c>var===s</c> / <c>var!==s</c> (string, via cvar_string()), or a bare token which is either a literal
    /// number (its own truthiness) or a cvar name (cvar()'s truthiness), optionally '!'-prefixed to invert.
    /// If any predicate fails, the AND fails (and is NOT inverted by '-'); otherwise the running result flips.
    /// This is what lets the overkill ruleset value <c>g_spawn_near_teammate "!g_assault !g_freezetag"</c>
    /// correctly disable the mutator during assault / freezetag instead of always reading as enabled.
    /// </summary>
    private static bool ExprEvaluate(string s)
    {
        s ??= string.Empty;
        bool ret = false;
        if (s.Length > 0 && s[0] == '+') s = s.Substring(1);
        else if (s.Length > 0 && s[0] == '-') { ret = true; s = s.Substring(1); }

        bool exprFail = false;
        foreach (string tok in s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ExprToken(tok)) { exprFail = true; break; }
        }
        if (!exprFail) ret = !ret;
        return ret;
    }

    // One whitespace token of expr_evaluate's AND chain — returns true if the predicate holds (QC: continue).
    private static bool ExprToken(string s)
    {
        int o;
        // Operators tested in EXACTLY QC's BINOP source order (>= <= == != === !==, then > <). strstrofs finds
        // the FIRST occurrence, so for a "var===x" token the leading "==" matches before "===" is ever tried —
        // a faithful quirk of expr_evaluate (lib/cvar.qh:69-80), not reordered here.
        if ((o = s.IndexOf(">=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) >= Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("<=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) <= Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("==", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) == Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("!=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) != Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("===", System.StringComparison.Ordinal)) >= 0)
            return Cvar(s.Substring(0, o)) == s.Substring(o + 3);
        if ((o = s.IndexOf("!==", System.StringComparison.Ordinal)) >= 0)
            return Cvar(s.Substring(0, o)) != s.Substring(o + 3);
        if ((o = s.IndexOf('>')) >= 0)
            return CvarF(s.Substring(0, o)) > Stof(s.Substring(o + 1));
        if ((o = s.IndexOf('<')) >= 0)
            return CvarF(s.Substring(0, o)) < Stof(s.Substring(o + 1));

        // Bare token: literal number (its own value) or cvar name; optional leading '!' inverts.
        string k = s;
        bool b = true;
        if (k.Length > 0 && k[0] == '!') { k = k.Substring(1); b = false; }
        float f = Stof(k);
        // QC: boolean((ftos(f) == k) ? f : cvar(k)) — if k is a literal number use it, else read the cvar.
        float val = (Ftos(f) == k) ? f : CvarF(k);
        return (val != 0f) == b;
    }

    // cvar(name) / cvar_string(name) analogues; "" when services aren't up (cvar of a missing name is 0/"").
    private static float CvarF(string name) => Api.Services is null ? 0f : Api.Cvars.GetFloat(name);
    private static string Cvar(string name) => Api.Services is null ? string.Empty : Api.Cvars.GetString(name);

    // QC stof/ftos: parse a leading float (0 on failure), and the canonical float→string (used to detect literals).
    private static float Stof(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    private static string Ftos(float f) =>
        f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
