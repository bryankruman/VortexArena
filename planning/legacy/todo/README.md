# Phased TODO

The execution plan. **One file per phase.** Each phase is split into **sections by parallel track** (Infra `I`,
Assets `A`, Engine `E`, Gameplay `G`, Net `N`, Client/UI `U` — see
[`../process/tracks-and-ownership.md`](../../process/tracks-and-ownership.md)). Tasks within a track's section can
generally run in parallel; cross-track dependencies are called out per phase.

Phases 0–3 build the spine sequentially (each retires a top risk via a vertical slice); Phases 4–5 fan out widely.

| Phase | Theme | Vertical slice (exit demo) | Retires |
|---|---|---|---|
| [0](phase-0-foundations.md) | Foundations & spikes | 3 green spikes (load a BSP, an IQM model, a golden-trace match) | de-risks R1, R3 |
| [1](phase-1-asset-slice.md) | Asset pipeline | **Walk around a real Xonotic map** (materials, lightmaps, collision) + a real player model | R1, R2 |
| [2](phase-2-runtime-gameplay-slice.md) | Runtime + gameplay | **Local deathmatch vs a bot-stub** with real movement feel | R3, framework port |
| [3](phase-3-networking.md) | Networking | **Responsive LAN deathmatch** (prediction + reconciliation) | R4 |
| [4](phase-4-fanout.md) | Fan-out | Most weapons/gametypes/mutators + full HUD + Menu, online | R8 (partial), volume |
| [5](phase-5-long-tail.md) | Long tail & polish | Bots, warpzones, effects parity, server browser, perf — shippable parity | R8, R9, R11, R12 |

## Task conventions

- `☐` not started · `◐` in progress · `☑` done · `⚠` blocked · `⏸` deferred
- `[track]` tag on cross-references; `↳ depends:` notes prerequisites.
- "DoD" = Definition of Done for the phase.
- Tasks reference specs (`../specs/…`), ADRs (`../decisions/…`), and source (`Base/…/qcsrc/…`).

## A note on estimates

These files intentionally avoid calendar dates (team size is undecided — OPEN Q12). Sequencing and
dependencies are explicit; convert to a schedule once staffing is set. Relative effort proportions are in the
report (§11) and summarized per phase.
