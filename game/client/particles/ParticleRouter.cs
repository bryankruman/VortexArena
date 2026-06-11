using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Particles;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client.Particles;

// =====================================================================================================
//  Particle ROUTER (planning/particles-dual-system.md §D.2). Resolves the backend for one effectinfo
//  spawn from (cl_particles_modern mode 0/1/2) × (per-effect authored style, §D.1) × (SDF coverage for
//  modern collision, §A.6), then drives the chosen backend:
//
//    * mode 0 (default): everything -> FaithfulParticleBackend. A modern-authored effect is translated to
//      faithful EffectInfoEmitter blocks (ParticleTranslation.ToFaithful) so mode 0 still renders it.
//    * mode 2: everything -> ModernParticleBackend. An effectinfo-defined effect renders through the §B
//      custom shader (its math IS the original->modern translation); preset derived if none authored.
//    * mode 1: per-effect style decides (Auto = original for effectinfo-defined, modern for modern-authored;
//      Original -> faithful; Modern -> modern). SDF service active.
//
//  Modern collision gate (§A.6): when a spawn routes modern but no SDF chunk covers it, cl_particles_modern_nosdf
//  picks 1 = spawn modern collisionless, or 0 = reroute that spawn to the faithful backend (translated).
//  Checked per-spawn, so late-arriving SDF chunks upgrade behaviour mid-map.
//
//  Coordinates are QUAKE space throughout (the sim's convention); both backends convert internally.
// =====================================================================================================

/// <summary>Routes an effectinfo spawn to the faithful or modern backend per cl_particles_modern + style + SDF.</summary>
public sealed class ParticleRouter
{
    public FaithfulParticleBackend? Faithful { get; set; }
    public ModernParticleBackend? Modern { get; set; }
    public EffectStyleRegistry? Registry { get; set; }
    public SdfCollisionService? Sdf { get; set; }

    public ParticleRouter(FaithfulParticleBackend? faithful, ModernParticleBackend? modern,
        EffectStyleRegistry? registry, SdfCollisionService? sdf)
    {
        Faithful = faithful;
        Modern = modern;
        Registry = registry;
        Sdf = sdf;
    }

    /// <summary>
    /// Route one effectinfo-defined spawn. Returns true if a backend handled it (the caller must NOT also run
    /// the legacy GPU path); false if no backend is available (caller falls back). <paramref name="origin"/>/
    /// <paramref name="velocity"/> are Quake space; for trails <paramref name="velocity"/> is the END point.
    /// </summary>
    public bool Route(string effectName, IReadOnlyList<EffectInfoEmitter> blocks,
        NVec3 origin, NVec3 velocity, int count, bool isTrail, Color? color)
    {
        if (Faithful is null && Modern is null)
            return false;
        if (blocks is null || blocks.Count == 0)
            return false;

        int mode = (int)Api.Cvars.GetFloat(ParticleCvars.Modern);
        EffectStyleEntry style = Registry?.GetStyle(effectName) ?? EffectStyleEntry.Default;
        ParticleBackendKind kind = ResolveKind(mode, style.Style);

        if (kind == ParticleBackendKind.Modern && Modern is not null)
        {
            ModernPreset preset = ResolvePreset(style, blocks);

            // §A.6 nosdf gate: no covering chunk + nosdf 0 => reroute this spawn to the faithful backend.
            bool coverage = Sdf?.HasCoverage(origin) ?? false;
            bool nosdf = Api.Cvars.GetFloat(ParticleCvars.ModernNoSdf) != 0f;
            if (!coverage && !nosdf && Faithful is not null)
            {
                SpawnFaithful(effectName, blocks, style, preset, origin, velocity, count, isTrail, color);
                return true;
            }

            Modern.Spawn(blocks, origin, velocity, count, preset, collisionEnabled: coverage);
            return true;
        }

        if (Faithful is not null)
        {
            SpawnFaithful(effectName, blocks, style, ResolvePreset(style, blocks),
                origin, velocity, count, isTrail, color);
            return true;
        }

        // Chosen modern but no modern backend wired — fall back to faithful if present, else legacy.
        if (Modern is null && Faithful is not null)
        {
            SpawnFaithful(effectName, blocks, style, ResolvePreset(style, blocks),
                origin, velocity, count, isTrail, color);
            return true;
        }
        return false;
    }

    private void SpawnFaithful(string effectName, IReadOnlyList<EffectInfoEmitter> blocks,
        EffectStyleEntry style, ModernPreset preset, NVec3 origin, NVec3 velocity, int count, bool isTrail, Color? color)
    {
        // A modern-authored effect routed to the faithful backend (mode 0, or the nosdf=0 reroute) is
        // translated to faithful-shaped blocks; an effectinfo-defined effect uses its real blocks verbatim.
        IReadOnlyList<EffectInfoEmitter> faithBlocks = blocks;
        if (style.Style == ParticleStyle.Modern)
        {
            IReadOnlyList<EffectInfoEmitter>? authored = null;
            if (Registry is not null && Registry.TryGetOverlayBlocks(effectName, out var ab))
                authored = ab;
            faithBlocks = ParticleTranslation.ToFaithful(effectName, preset, authored);
        }

        uint tint = PackTint(color);
        if (isTrail)
            Faithful!.Trail(faithBlocks, origin, velocity, NVec3.Zero, count, tint);
        else
            Faithful!.Spawn(faithBlocks, origin, velocity, count, tint);
    }

    /// <summary>Resolve the backend per §D.2: mode 0 faithful, mode 2 modern, mode 1 by authored style.</summary>
    private static ParticleBackendKind ResolveKind(int mode, ParticleStyle style) => mode switch
    {
        0 => ParticleBackendKind.Faithful,
        2 => ParticleBackendKind.Modern,
        _ => style == ParticleStyle.Modern ? ParticleBackendKind.Modern : ParticleBackendKind.Faithful,
    };

    /// <summary>Authored preset by id, else a preset derived from the effectinfo blocks (§B/§D.1).</summary>
    private static ModernPreset ResolvePreset(EffectStyleEntry style, IReadOnlyList<EffectInfoEmitter> blocks)
    {
        if (style.ModernPresetId is { Length: > 0 } id && ModernPresetLibrary.TryGet(id, out ModernPreset p))
            return p;
        return ParticleTranslation.DerivePreset(blocks);
    }

    /// <summary>Godot Color (0..1) -> DP tint RGBA bytes (R in the high byte; 0xFFFFFFFF = no tint).</summary>
    private static uint PackTint(Color? color)
    {
        if (color is not { } c)
            return 0xFFFFFFFFu;
        uint r = (uint)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
        uint g = (uint)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
        uint b = (uint)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
        return (r << 24) | (g << 16) | (b << 8) | 0xFFu;
    }
}
