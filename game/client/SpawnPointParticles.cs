using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Idle particle glow at every spawn point — the port of CSQC <c>Spawn_Draw</c>
/// (qcsrc/client/spawnpoints.qc:20): while <c>cl_spawn_point_particles</c> is on, each spawn point
/// continuously emits the <c>EFFECT_SPAWNPOINT</c> burst (effectinfo <c>spawn_point_neutral</c>) so players
/// can read spawn locations at a glance. Upstream emits per draw frame via <c>boxparticles</c>; here a small
/// repeat timer feeds <see cref="EffectSystem.Spawn"/> — the faithful backend's fractional accumulator
/// shapes the per-point rate exactly like DP's count scaling.
///
/// Spawn points are discovered from the ambient entity table (<c>info_player_deathmatch</c> /
/// <c>info_player_start</c> / <c>info_player_team*</c> — the same classnames SpawnSystem searches), lazily
/// and re-scanned occasionally so late entity registration is picked up. Distance-culled to the camera so a
/// big map doesn't burn spawns nobody can see (upstream culls via the render loop reaching Spawn_Draw).
/// </summary>
public sealed partial class SpawnPointParticles : Node3D
{
    /// <summary>Seconds between emission pulses per point (upstream emits per-frame with frametime-scaled
    /// counts; a 0.2s pulse at count 1 through the accumulator reads the same).</summary>
    [Export] public float PulseInterval { get; set; } = 0.2f;

    /// <summary>Only points within this range of the camera emit (qu).</summary>
    [Export] public float DrawDistance { get; set; } = 2000f;

    /// <summary>The effect player (EffectSystem) — wired by ClientWorld.</summary>
    public EffectSystem? Effects { get; set; }

    private static readonly string[] SpawnClasses =
    {
        "info_player_deathmatch", "info_player_start",
        "info_player_team1", "info_player_team2", "info_player_team3", "info_player_team4",
    };

    private readonly List<NVec3> _points = new();
    private float _pulseTimer;
    private float _rescanTimer;

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("clientmisc");
        if (Effects is null || Api.Services is null)
            return;
        if (XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat("cl_spawn_point_particles") == 0f)
            return;

        // Lazy + periodic rescan (entities register during load; gametype filters can add/remove).
        _rescanTimer -= (float)delta;
        if (_points.Count == 0 || _rescanTimer <= 0f)
        {
            _rescanTimer = 5f;
            _points.Clear();
            foreach (string cls in SpawnClasses)
                foreach (Entity e in Api.Entities.FindByClass(cls))
                    _points.Add(e.Origin);
        }
        if (_points.Count == 0)
            return;

        _pulseTimer -= (float)delta;
        if (_pulseTimer > 0f)
            return;
        _pulseTimer = PulseInterval;

        // Camera position for the draw-distance cull (Quake space).
        NVec3 eye = default;
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is not null)
            eye = Coords.ToQuake(cam.GlobalTransform.Origin);

        float dd2 = DrawDistance * DrawDistance;
        foreach (NVec3 p in _points)
        {
            NVec3 d = p - eye;
            if (NVec3.Dot(d, d) > dd2)
                continue;
            // Slight lift so the glow sits above the floor (upstream uses the spawnpoint bbox).
            Effects.Spawn("SPAWNPOINT", p + new NVec3(0f, 0f, 8f));
        }
    }
}
