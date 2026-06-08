# Risk Register (living)

Update status as the project moves. Severity = Impact × Likelihood at the *start* of the project; the goal of
the phasing is to retire the High risks early via vertical slices.

Status legend: ☐ open · ◐ mitigating · ☑ retired · ⚠ realized

| # | Risk | Sev | Track | Status | Mitigation / retire-by |
|---|------|-----|-------|--------|------------------------|
| R1 | **Asset pipeline is DIY** — no mature Godot importer for compiled IBSP v46, MD3, IQM, or DPM; Func_Godot/Qodot target `.map` *source*, not BSP; GDExtension can't be called from C#. | **High** | Assets | ☐ | Write importers in C#; study/fork `ballerburg9005/godot-bsp-map-loader`. **Retire in Phase 1** ("walk around a real map"). |
| R2 | **Q3 `.shader` translation** — multi-pass/tcMod/deformVertexes + gameplay `surfaceparm`. "The hard part nobody finished." | **High** | Assets | ☐ | Build the `.shader`→material compiler *first* (maps + skins depend on it); parser + GDShader template library. Retire in Phase 1. |
| R3 | **Movement/collision determinism & feel** — must reproduce 72 Hz tick + AABB-vs-brush traces; Godot physics is non-deterministic. | **High** | Engine | ☐ | Custom deterministic sim + brush collision; **golden-trace regression harness** captured from Darkplaces. Retire in Phase 2 ("feels like Xonotic"). |
| R4 | **Custom netcode required** — no built-in prediction/reconciliation/lag-comp; determinism is a prerequisite. | **High** | Net | ☐ | Port the existing CSQC predict-reconcile design onto ENet; reuse message registry + quantization; consider MonkeNet/Netfox bootstrap. Retire in Phase 3. |
| R5 | **C# GC stutter** in a twitch FPS — per-frame allocations cause hitches (seen even in recent Godot dev builds). | Med | Cross | ☐ | Zero-alloc hot paths; cache `StringName`/`NodePath`; object pools; ban LINQ/boxing in the tick; external .NET profiling. Enforce via [coding standards](process/coding-standards.md). |
| R6 | **No stable C# web export** in Godot — only a brittle prototype. | Med | Cross | ⏸ | Scope to desktop + dedicated server; defer web. See [ADR-0012](decisions/ADR-0012-platform-scope.md). |
| R7 | **200k-LOC C# Godot codebase unproven at this scale.** | Low-Med | Cross | ☐ | Desktop C# tooling handles this; de-risk with the Phase 0–2 vertical slice; keep build times sane (incremental). |
| R8 | **Long tail** — havocbot AI, warpzones (need `getsurface*`), 44 mutators, 20 gametypes, minigames, vehicles. | Med | Gameplay | ☐ | Registry/hook framework makes content pluggable; sequence last (Phase 5). Warpzones need the BSP-surface-query facade. |
| R9 | **PVS / `checkpvs`** — bots/gametypes use BSP visibility with no Godot equivalent. | Low-Med | Engine | ☐ | Ship/recompute BSP PVS, or approximate with raycast/occlusion (slight, usually acceptable, behavior change). |
| R10 | **Skeletal `skel_*` + tag attachment fidelity** — CPU bone manipulation and named-tag world transforms drive all weapon/effect attachments. | Med | Engine/Assets | ☐ | Build a CPU skeleton/tag query layer parallel to the Godot `Skeleton3D`; validate with weapon-attach in Phase 1/2. |
| R11 | **Effects parity** — particle behavior is driven by `effectinfo.txt` + DP spawn/trail semantics. | Low-Med | Engine | ☐ | Parse effectinfo.txt; map to Godot particles; accept "close enough" cosmetics. Phase 5. |
| R12 | **Scope creep across 20 gametypes / 44 mutators** — pressure to ship "everything" v1. | Med | Product | ☐ | Define a v1 content subset (see [OPEN-QUESTIONS](OPEN-QUESTIONS.md) Q7); the rest is incremental. |
| R13 | **Determinism across CPU architectures** (x64 vs ARM) for cross-play prediction. | Low-Med | Net/Engine | ☐ | Lean on the existing error-compensation/smoothing (Xonotic already tolerates prediction error); only require *low-divergence* determinism, not lockstep. See [ADR-0010](decisions/ADR-0010-determinism-and-numerics.md). |
| R14 | **Licensing** — Xonotic code is GPL; assets carry mixed licenses. The C# rewrite's license + asset redistribution must be settled. | Med | Legal | ☐ | Decide license up front (see [OPEN-QUESTIONS](OPEN-QUESTIONS.md) Q1). Likely GPLv3+ to stay compatible. |

## How risks map to phases

- **Phase 0** spikes touch R1, R3 (prove the scary parsing/trace approaches in isolation).
- **Phase 1** retires R1, R2 (and exercises R10).
- **Phase 2** retires R3 (and most of the framework port).
- **Phase 3** retires R4 (and exercises R13).
- **Phases 4–5** burn down R8, R9, R11, R12.
- R5, R6, R7, R14 are **cross-cutting** and managed continuously.
