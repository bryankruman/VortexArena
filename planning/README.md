# XonoticGodot — planning

**XonoticGodot** is the codename for the port of **Xonotic** from QuakeC-on-Darkplaces to **C#-on-Godot 4**.

This folder is the **architecture & decision archive** — the durable design context behind the port:
the target architecture, the ADRs, the subsystem specs, and the working process. It turns the research in
[`XONOTIC_GODOT_MIGRATION_REPORT.md`](../../XONOTIC_GODOT_MIGRATION_REPORT.md) (repo root) into
actionable decisions and specifications.

> **Task tracking lives one level up, not here.** Active work is managed in
> [`../TODO.md`](../TODO.md) (the single source of truth) and summarized in [`../README.md`](../README.md).
> This folder is for *why* and *how it's designed*, not *what's left to do*.

## How to navigate

| Doc | Purpose |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | The target system architecture, solution layout, and how the pieces fit. Read this first. |
| [`GLOSSARY.md`](GLOSSARY.md) | Shared vocabulary (Quake/QuakeC/Darkplaces/Godot terms). Read this second. |
| [`RISK-REGISTER.md`](RISK-REGISTER.md) | Living list of risks, owners, status, and mitigations. |
| [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md) | Decisions that still need a human/stakeholder call. |
| [`decisions/`](decisions/) | **Architecture Decision Records (ADRs)** — the important, hard-to-reverse decisions, one per file. |
| [`specs/`](specs/) | Deep technical specs for each major subsystem (engine facade, assets, networking, determinism, entity model, modding). |
| [`process/`](process/) | How we work: coding standards for the port, testing strategy, track ownership. |
| [`legacy/`](legacy/) | **Superseded task-management docs** — the original phased `todo/`, the `REMAINING-WORK.md` gap list, and per-wave recon/review notes. Kept for historical reference; **not** used to manage active work. |

## The one-paragraph plan

Two separable axes of work. **Axis A** ports ~207k LOC of QuakeC gameplay to idiomatic C# — large but
low-risk, because the code is already object-oriented, deglobalized, and routes all engine access through a
single thin binding layer. **Axis B** replaces the Darkplaces engine with a Godot/C# runtime: an engine-services
facade, a deterministic 72 Hz simulation core, a custom collision/trace service, a custom authoritative netcode
layer, and an asset pipeline that loads Xonotic's existing Quake-format maps and models. We do an **idiomatic
rewrite with a "fidelity contract"** on the small load-bearing core (movement, collision, tick), and we
**front-load the three concentrated risks** (assets, deterministic sim, netcode) with vertical slices in Phases
0–3 before the large game-logic fan-out in Phases 4–5.

## Status legend (used across TODO + risk docs)

- ☐ not started · ◐ in progress · ☑ done · ⚠ blocked · ⏸ deferred

## Conventions

- ADRs are numbered and immutable once **Accepted**; supersede rather than edit.
- Every spec links back to the relevant ADR(s) and to source files under
  `Base/data/xonotic-data.pk3dir/qcsrc/` (the QuakeC) or `Base/darkplaces/` (the reference engine).
- "The report" = [`XONOTIC_GODOT_MIGRATION_REPORT.md`](../../XONOTIC_GODOT_MIGRATION_REPORT.md).
