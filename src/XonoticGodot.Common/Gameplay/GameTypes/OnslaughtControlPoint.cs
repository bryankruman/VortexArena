// Port of common/gametypes/gametype/onslaught/sv_onslaught.qc — the control-point CAPTURE-BY-BUILD combat
// (ons_ControlPoint_Touch / ons_ControlPoint_Icon_Spawn / ons_ControlPoint_Icon_BuildThink /
// ons_ControlPoint_Icon_Think / ons_ControlPoint_Icon_Damage / ons_ControlPoint_Icon_Heal) and the generator
// damage pipeline (ons_GeneratorSetup / ons_GeneratorReset / ons_GeneratorDamage), plus the overtime
// generator-decay slice of Onslaught_CheckWinner.
//
// QC built an icon edict the moment a player touches an attackable control point; the icon ramps RES_HEALTH
// from g_onslaught_cp_buildhealth up to g_onslaught_cp_health, and ONLY THEN does the point flip to that
// team (and re-propagate power). The icon can be damaged/destroyed mid-build, in which case the point never
// captures. This file drives that loop headlessly onto real objective entities so weapon damage routes
// through the shared DamageSystem (Entity.GtEventDamage), reusing the existing Onslaught power graph
// (Onslaught.UpdateLinks / IsAttackable / CaptureControlPoint / DamageGenerator).
//
// Deferred vs QC (NOTE — genuinely client/networking): the CSQC icon/sprite networking (cpicon_send,
// WaypointSprite_*), particles/sounds, proximity-decap (g_onslaught_cp_proximitydecap), the build models
// (MDL_ONS_CP_PAD*), vehicle-touch, and the bot-target list — presentation / AI concerns. The capture
// outcome + the build/heal/destroy timing + the scoring are reproduced faithfully.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Onslaught control-point + generator combat layer (QC sv_onslaught.qc icon/generator funcs). Owned by
/// an <see cref="Onslaught"/> instance: it spawns the buildable capture icons + the generator entities, wires
/// their <see cref="Entity.GtEventDamage"/> into the shared damage pipeline, and runs the per-tick
/// build/heal/decay think. The point only flips (<see cref="Onslaught.CaptureControlPoint(int,int,Player?)"/>)
/// once an icon finishes building; the generator destruction routes through
/// <see cref="Onslaught.DamageGenerator(int,float,Player?)"/>.
/// </summary>
public sealed class OnslaughtControlPoint
{
    // ----- QC think rates (sv_onslaught.qh) -----
    /// <summary>QC ONS_CP_THINKRATE = 0.2 — the icon build/heal think interval.</summary>
    public const float CpThinkRate = 0.2f;
    /// <summary>QC GEN_THINKRATE = 1 — the generator alarm think interval (also the overtime decay step).</summary>
    public const float GenThinkRate = 1f;

    // ----- QC autocvar names + defaults (balance/gametypes cfgs) -----
    private const string CvarCpHealth      = "g_onslaught_cp_health";        // 200 — icon full health
    private const string CvarCpBuildHealth = "g_onslaught_cp_buildhealth";   // 100 — health an icon starts at
    private const string CvarCpBuildTime   = "g_onslaught_cp_buildtime";     // 5   — seconds to build from build→full
    private const string CvarCpRegen       = "g_onslaught_cp_regen";         // 10  — slow repair rate once built (hp/s)
    private const string CvarGenHealth     = "g_onslaught_gen_health";       // 2000
    private const string CvarSuddenDeath   = "timelimit_suddendeath";        // 5   — overtime decay window (minutes)

    private const float DefCpHealth      = 200f;
    private const float DefCpBuildHealth = 100f;
    private const float DefCpBuildTime   = 5f;
    private const float DefCpRegen       = 10f;
    private const float DefGenHealth     = 2000f;
    private const float DefSuddenDeath   = 5f;

    private readonly Onslaught _ons;

    /// <summary>Every live build icon (QC the onslaught_controlpoint_icon edicts), keyed by control-point id.</summary>
    private readonly Dictionary<int, Entity> _icons = new();

    /// <summary>Every generator world entity (QC ons_worldgeneratorlist), keyed by team.</summary>
    private readonly Dictionary<int, Entity> _generators = new();

    /// <summary>True once the overtime decay announcement has fired (QC wpforenemy_announced).</summary>
    public bool OvertimeAnnounced { get; private set; }

    public OnslaughtControlPoint(Onslaught ons) => _ons = ons;

    /// <summary>Clear the icon/generator entity maps + the overtime latch (QC per-map reset). The graph itself
    /// is owned by <see cref="Onslaught"/> and cleared in its OnInit.</summary>
    public void Reset()
    {
        _icons.Clear();
        _generators.Clear();
        OvertimeAnnounced = false;
    }

    private static float Now => Api.Services is null ? 0f : Api.Clock.Time;

    public float CpHealth      => GametypeEntities.Cvar(CvarCpHealth, DefCpHealth);
    public float CpBuildHealth => GametypeEntities.Cvar(CvarCpBuildHealth, DefCpBuildHealth);
    public float CpBuildTime   { get { float v = GametypeEntities.Cvar(CvarCpBuildTime, DefCpBuildTime); return v > 0f ? v : DefCpBuildTime; } }
    public float CpRegen       => GametypeEntities.Cvar(CvarCpRegen, DefCpRegen);
    public float GenHealth     => GametypeEntities.Cvar(CvarGenHealth, DefGenHealth);

    /// <summary>The live build icon for a control point (or null), QC cp.goalentity.</summary>
    public Entity? IconFor(int controlPointId) => _icons.TryGetValue(controlPointId, out var e) ? e : null;

    /// <summary>The generator world entity for a team (or null), QC the onslaught_generator edict.</summary>
    public Entity? GeneratorEntity(int team) => _generators.TryGetValue(team, out var e) ? e : null;

    // ============================================================================================
    //  Generator setup + damage (QC ons_GeneratorSetup / ons_GeneratorReset / ons_GeneratorDamage)
    // ============================================================================================

    /// <summary>
    /// QC ons_GeneratorSetup + ons_GeneratorReset (spawnfunc onslaught_generator): register a team's generator
    /// in the <see cref="Onslaught"/> power graph AND spawn its world entity with RES_HEALTH = g_onslaught_gen_health,
    /// TakeDamage = AIM, and <see cref="Entity.GtEventDamage"/> wired to <see cref="GeneratorDamage"/> so weapon
    /// fire routes through the shared pipeline. Returns the world entity (null when no facade is wired — the
    /// gametype still tracks the generator via <see cref="Onslaught.AddGenerator"/>).
    /// </summary>
    public Entity? SpawnGenerator(int team, Vector3 origin)
    {
        Onslaught.OnsNode node = _ons.GeneratorNode(team) ?? _ons.AddGenerator(team);
        float h = GenHealth;
        if (node.Gen is not null) { node.Gen.Health = h; node.Gen.MaxHealth = h; }

        Entity? e = GametypeEntities.SpawnObjective("onslaught_generator", origin, team,
            new Vector3(-52f, -52f, -14f), new Vector3(52f, 52f, 75f)); // QC generator bbox (approx)
        if (e is not null)
        {
            e.GtObjHealth = h;
            e.GtObjMaxHealth = h;
            e.GtHomeTeam = team;
            e.GtPointId = -team; // negative id namespace so it never collides with a control-point id
            e.TakeDamage = DamageMode.Aim;     // QC DAMAGE_AIM (shield is enforced inside GeneratorDamage)
            e.GtEventDamage = GeneratorDamage;
            _generators[team] = e;
        }
        return e;
    }

    /// <summary>
    /// QC ons_GeneratorDamage (sv_onslaught.qc:919): the generator's event_damage. Bails while warmup/round
    /// not started; a shielded generator ignores all non-self damage; otherwise subtract and, at &lt;=0 health,
    /// destroy it (the existing <see cref="Onslaught.DamageGenerator(int,float,Player?)"/> awards the attacker
    /// SCORE +100, re-propagates power, and ends the round). The DEATH_HURTTRIGGER self-damage of the overtime
    /// decay (attacker == this) bypasses the shield + the under-attack notify, exactly like QC.
    /// </summary>
    public void GeneratorDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        if (damage <= 0f) return;
        // QC: if(warmup_stage || game_stopped) return; if(!round_handler_IsRoundStarted()) return;
        if (!RoundStarted())
            return;

        int team = self.GtHomeTeam;
        bool selfDamage = ReferenceEquals(attacker, self); // overtime decay path
        Onslaught.OnsNode? node = _ons.GeneratorNode(team);

        bool unshieldedForSelf = false;
        if (!selfDamage)
        {
            // QC: a shielded generator ignores the damage entirely (only the under-attack hint plays).
            if (node is not null && node.Shielded)
                return;
            // QC under-attack notify debounce (pain_finished) — presentation, modeled as a timer tab only.
            if (Now > self.GtPainFinished)
                self.GtPainFinished = Now + 10f;
        }
        else if (node is { Shielded: true })
        {
            // QC ons_GeneratorDamage: the overtime self-damage (attacker == this) bypasses the shield. The
            // existing DamageGenerator gates on the shield, so drop it for this self-inflicted hit (the
            // subsequent UpdateLinks on damage/destroy recomputes it).
            node.Shielded = false;
            unshieldedForSelf = true;
        }

        Player? by = attacker as Player;
        bool destroyed = _ons.DamageGenerator(team, damage, by); // applies HP, scoring, links, round-end
        // restore the shield if the self-damage hit didn't destroy the generator (it'd be re-derived next
        // UpdateLinks anyway, but keep the state coherent between decay ticks).
        if (unshieldedForSelf && !destroyed && node is not null)
            node.Shielded = true;
        // mirror the graph health back onto the entity (so the entity stays the source of truth for damage gate)
        Onslaught.GeneratorState? gen = _ons.GeneratorFor(team);
        self.GtObjHealth = gen is not null ? gen.Health : 0f;

        if (destroyed)
        {
            // QC: takedamage = DAMAGE_NO; event_damage = func_null; setthink(func_null).
            self.TakeDamage = DamageMode.No;
            self.GtEventDamage = null;
            self.Think = null;
            self.NextThink = 0f;
        }
    }

    // ============================================================================================
    //  Control-point touch + icon build/heal/damage (QC ons_ControlPoint_*)
    // ============================================================================================

    /// <summary>
    /// QC ons_ControlPoint_Setup (spawnfunc onslaught_controlpoint): register a neutral control-point node in
    /// the <see cref="Onslaught"/> power graph AND spawn its world trigger so a player touching it can start a
    /// capture. Touch dispatches to <see cref="ControlPointTouch"/>. Returns the world entity (or null headless).
    /// </summary>
    public Entity? SpawnControlPoint(int controlPointId, Vector3 origin)
    {
        if (_ons.ControlPointNode(controlPointId) is null)
            _ons.AddControlPoint(controlPointId);

        Entity? e = GametypeEntities.SpawnObjective("onslaught_controlpoint", origin, Teams.None,
            new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 128f), touch: ControlPointTouchEntity);
        if (e is not null)
        {
            e.GtPointId = controlPointId;
            e.Solid = Solid.BBox; // QC SOLID_BBOX (the pad), the touch trigger volume is the bbox here
        }
        return e;
    }

    /// <summary>Entity touch trampoline (QC settouch ons_ControlPoint_Touch): a live player may start a build.</summary>
    private void ControlPointTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            ControlPointTouch(self, p);
    }

    /// <summary>
    /// QC ons_ControlPoint_Touch (sv_onslaught.qc:714): when a player touches a control point that is
    /// attackable by their team (QC ons_ControlPoint_Attackable returns 2 or 4 — a free/unshielded point with
    /// no icon yet), start building the capture icon (<see cref="SpawnIcon"/>). A point that already has a
    /// building/built icon, or is shielded, or is same-team, does nothing here.
    /// </summary>
    public void ControlPointTouch(Entity cpEnt, Player toucher)
    {
        int cpId = cpEnt.GtPointId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);
        if (node is null)
            return;
        int team = (int)toucher.Team;
        // QC attackable values 2/4 == "free point, touch it": no icon yet, unshielded, capturable by this team.
        // (A point that already has an icon returns -1/-2/1/3; a shielded one returns 0.)
        if (_icons.ContainsKey(cpId))
            return;
        if (node.Shielded || node.Team == team || team == Teams.None)
            return;
        if (!_ons.IsAttackable(node, team))
            return;

        SpawnIcon(cpEnt, toucher);
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Spawn (sv_onslaught.qc:620): create the buildable icon for <paramref name="cpEnt"/>,
    /// owned by <paramref name="player"/>'s team, starting at g_onslaught_cp_buildhealth and ramping toward
    /// g_onslaught_cp_health at a rate that finishes in g_onslaught_cp_buildtime seconds (QC e.count). Wires the
    /// icon's <see cref="Entity.GtEventDamage"/> (so it can be destroyed mid-build) and its build Think.
    /// </summary>
    public Entity? SpawnIcon(Entity cpEnt, Player player)
    {
        int cpId = cpEnt.GtPointId;
        int team = (int)player.Team;
        float maxH = CpHealth;
        float buildH = CpBuildHealth;
        // QC: e.count = (max_health - health) * ONS_CP_THINKRATE / buildtime — health gained per think tick.
        float perTick = (maxH - buildH) * CpThinkRate / CpBuildTime;

        Entity? icon = GametypeEntities.SpawnObjective("onslaught_controlpoint_icon",
            cpEnt.Origin + new Vector3(0f, 0f, 96f), team,
            new Vector3(-16f, -16f, -8f), new Vector3(16f, 16f, 8f), think: IconBuildThink);
        if (icon is not null)
        {
            icon.GtObjHealth = buildH;
            icon.GtObjMaxHealth = maxH;
            icon.GtBuildRate = perTick;
            icon.GtIconCpId = cpId;
            icon.GtIconBuilt = false;
            icon.GtHomeTeam = team;
            icon.Solid = Solid.Not;            // QC SOLID_NOT while building (becomes SOLID_BBOX when built)
            icon.TakeDamage = DamageMode.Aim;  // QC DAMAGE_AIM — bot/weapon targetable
            icon.GtEventDamage = IconDamage;
            icon.GtPainFinished = 0f;
            icon.NextThink = Now + CpThinkRate;
            _icons[cpId] = icon;
        }

        // QC: cp.goalentity = e; cp.team = e.team — the point shows the builder's color WHILE BUILDING, but the
        // power-graph capture (node.Team / node.Captured) only flips when the icon finishes (so the eventual
        // CaptureControlPoint still registers a real capture and credits the builder). We store the builder +
        // tentative team on the cp ENTITY only, leaving the graph node neutral until completion.
        cpEnt.GtCapturer = player;       // QC cp.ons_toucher — the builder, credited on completion
        cpEnt.GtPointTeam = team;        // QC cp.team while building (display color)
        return icon;
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:554): while there is still power to this point
    /// (the existing graph keeps it linkable), add g_onslaught build per tick; when it reaches max, finish the
    /// capture — flip the point to the builder's team via <see cref="Onslaught.CaptureControlPoint(int,int,Player?)"/>
    /// (which credits ONS_CAPS/ONS_TAKES + SCORE +10 and re-propagates power) and switch to the slow regen Think.
    /// </summary>
    public void IconBuildThink(Entity self)
    {
        self.NextThink = Now + CpThinkRate;
        int cpId = self.GtIconCpId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);
        if (node is null)
            return;

        // QC: only build if there is power (ons_ControlPoint_CanBeLinked); a point cut off mid-build just pauses.
        if (!CanBeLinked(node, self.GtHomeTeam))
            return;

        self.GtObjHealth += self.GtBuildRate;

        if (self.GtObjHealth >= self.GtObjMaxHealth)
        {
            self.GtObjHealth = self.GtObjMaxHealth;
            // QC: count = regen * ONS_CP_THINKRATE — slow repair from now on; switch to the steady-state Think.
            self.GtBuildRate = CpRegen * CpThinkRate;
            self.GtIconBuilt = true;
            self.Solid = Solid.BBox;           // QC SOLID_BBOX once built
            self.Think = IconThink;

            // QC: cp.iscaptured = true; capture credit to ons_toucher (SCORE +10 + ONS_CAPS); onslaught_updatelinks.
            Player? toucher = ControlPointBuilder(cpId);
            _ons.CaptureControlPoint(cpId, self.GtHomeTeam, toucher);
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Think (sv_onslaught.qc:477) — the steady-state icon think after the point is
    /// built: slowly regenerate RES_HEALTH back toward max (QC: 5s after the last hit). Proximity-decap is
    /// deferred (presentation/AI). Re-arms itself.
    /// </summary>
    public void IconThink(Entity self)
    {
        self.NextThink = Now + CpThinkRate;
        // QC: if (time > pain_finished + 5) regenerate up to max at the slow rate.
        if (Now > self.GtPainFinished + 5f && self.GtObjHealth < self.GtObjMaxHealth)
        {
            self.GtObjHealth += self.GtBuildRate;
            if (self.GtObjHealth > self.GtObjMaxHealth)
                self.GtObjHealth = self.GtObjMaxHealth;
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:365): the icon's event_damage. A shielded point ignores
    /// the damage; otherwise subtract, and at &lt;=0 health destroy the icon — the point reverts to neutral
    /// (QC owner.iscaptured/islinked = false, team = 0, onslaught_updatelinks) and the destroyer is credited
    /// ONS_TAKES +1 / SCORE +10. The build is aborted (the point never captured).
    /// </summary>
    public void IconDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        if (damage <= 0f) return;
        int cpId = self.GtIconCpId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);

        // QC: this is protected by a shield → ignore the damage (only the blocked-by-shield hint plays).
        if (node is not null && node.Shielded)
            return;

        self.GtObjHealth -= damage;
        self.GtPainFinished = Now + 1f;

        if (self.GtObjHealth <= 0f)
        {
            // QC: award the destroyer (unless self-inflicted), then reset the owner point to neutral.
            if (attacker is Player by && !ReferenceEquals(attacker, self))
            {
                AddCol(by, "ONS_TAKES", 1); // QC GameRules_scoring_add(attacker, ONS_TAKES, 1)
                by.ScoreFrags += 10;        // QC GameRules_scoring_add(attacker, SCORE, 10)
            }

            // QC: owner.goalentity = NULL; islinked = iscaptured = false; team = 0; onslaught_updatelinks().
            _icons.Remove(cpId);
            if (node is not null)
            {
                node.Team = Teams.None;
                node.Captured = false;
                node.Linked = false;
            }
            _ons.UpdateLinks();

            // delete(this): retire the icon entity.
            self.Think = null;
            self.NextThink = 0f;
            self.TakeDamage = DamageMode.No;
            self.GtEventDamage = null;
            if (Api.Services is not null)
                Api.Entities.Remove(self);
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Heal (sv_onslaught.qc:460): heal an icon's RES_HEALTH toward its limit (the
    /// Healer mutator / heal trigger). Returns true if any health was added. No-op once destroyed or full.
    /// </summary>
    public bool IconHeal(Entity icon, float amount, float limit = 0f)
    {
        float trueLimit = limit > 0f ? limit : icon.GtObjMaxHealth;
        if (icon.GtObjHealth <= 0f || icon.GtObjHealth >= trueLimit)
            return false;
        icon.GtObjHealth = MathF.Min(icon.GtObjHealth + amount, trueLimit);
        return true;
    }

    // ============================================================================================
    //  Overtime generator-decay (QC the stalemate branch of Onslaught_CheckWinner :1172)
    // ============================================================================================

    /// <summary>
    /// QC Onslaught_CheckWinner overtime slice (sv_onslaught.qc:1172): once the timelimit (or the round end
    /// time) elapses, every generator self-damages by <c>max_health / max(30, 60 * timelimit_suddendeath)</c>
    /// per second, scaled UP by the number of enemy-linked control points (so a team that controls more of the
    /// map decays the enemy generator faster). The self-damage runs through <see cref="GeneratorDamage"/> (the
    /// attacker == the generator path), so it bypasses the shield and ends the round when a generator falls.
    /// Call once per second from the round handler / EndFrame when the match is in overtime.
    /// </summary>
    public void OvertimeDecayTick()
    {
        OvertimeAnnounced = true;
        float window = MathF.Max(30f, 60f * GametypeEntities.Cvar(CvarSuddenDeath, DefSuddenDeath));

        // snapshot the generators (the dictionary may shrink as one is destroyed)
        var gens = new List<KeyValuePair<int, Entity>>(_generators);
        foreach (var kv in gens)
        {
            int team = kv.Key;
            Entity gen = kv.Value;
            if (gen.IsFreed || gen.TakeDamage == DamageMode.No)
                continue; // already destroyed

            // d starts at 1, +1 per enemy-linked control point (QC counts DIFF_TEAM linked CPs).
            float d = 1f;
            foreach (Onslaught.OnsNode n in _ons.Nodes)
                if (!n.IsGenerator && n.Linked && n.Team != team && n.Team != Teams.None)
                    d += 1f;

            d *= gen.GtObjMaxHealth / window;
            // QC: Damage(gen, gen, gen, d, DEATH_HURTTRIGGER, ...) — self-inflicted, routes via GeneratorDamage.
            // (DeathTypes.Void is the port's DEATH_HURTTRIGGER alias.)
            Combat.Damage(gen, gen, gen, d, DeathTypes.Void, gen.Origin, Vector3.Zero);
        }
    }

    /// <summary>Reset the overtime announcement latch when the match leaves overtime (QC else branch).</summary>
    public void ClearOvertime() => OvertimeAnnounced = false;

    // ============================================================================================
    //  helpers
    // ============================================================================================

    /// <summary>
    /// QC ons_ControlPoint_CanBeLinked: a point is buildable/holdable by <paramref name="team"/> only while it
    /// neighbors a powered (linked) node owned by that team. We derive it from the existing graph: any linked
    /// same-team neighbor (generator or captured CP). A point cut off from power pauses its build (QC).
    /// </summary>
    private static bool CanBeLinked(Onslaught.OnsNode node, int team)
    {
        foreach (Onslaught.OnsNode m in node.Neighbors)
            if (m.Linked && m.Captured && m.Team == team)
                return true;
        return false;
    }

    /// <summary>The player who started the build on a control point (QC cp.ons_toucher), or null.</summary>
    private Player? ControlPointBuilder(int cpId)
    {
        if (Api.Services is null)
            return null;
        // The builder was stashed on the control-point world entity's GtCapturer; find it by id.
        foreach (Entity e in Api.Entities.FindByClass("onslaught_controlpoint"))
            if (e.GtPointId == cpId)
                return e.GtCapturer as Player;
        return null;
    }

    /// <summary>QC: warmup_stage/game_stopped gate + round_handler_IsRoundStarted. Headlessly: the round handler
    /// (when present) must report the round started; with no handler wired (a bare unit test) treat it as started
    /// so the deterministic combat is exercisable.</summary>
    private bool RoundStarted()
        => _ons.Handler is null || _ons.Handler.IsRoundStarted;

    /// <summary>QC GameRules_scoring_add(player, SP_X, n) for an ONS column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }
}
