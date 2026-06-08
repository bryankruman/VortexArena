# Wave A2 — review findings NOT fixed this wave (carried / refuted)

Review = 8 reviewers, refute-by-default. **0 blockers / 8 major / 14 minor / 3 nit.** Fixed: all 8 majors
(F1/F3 merged; F2 is a content gap, see below) + the high/medium-impact minors (F9, F13, F14, F15, F17,
F18, F19, F20, F21, F22). Below are the items deliberately carried or refuted, with why.

## Refuted (false positive — no change needed)
- **F10 (T30) — WallFriction doc "mis-reasons".** REFUTED after checking the actual post-edit code.
  Current state is already correct + faithful + safe: `MovementParameters.Defaults.WallFriction = 1` (the
  true Darkplaces engine cvar value) AND `PlayerPhysics.WallFriction()` is a **hard no-op** mirroring stock
  QC's commented-out `_Movetype_WallFriction` body (the math is preserved as comments with a "re-enable only
  to follow a modded server" warning). Net effect: no wall friction ever applies; `MovementParityTests` are
  byte-identical. `verify-against-dp.md §2.1` accurately describes this. The reviewer read a stale/pre-edit
  state (thought default=0 + live body). Nothing to fix.

## Carried follow-ups (low/nil live impact — documented, not fixed)
- **F2 (T28) — Localization inert at runtime: no `.po` files ship in `XonoticGodot.Assets`.** The PO engine +
  Tr seam + CtxTr + language picker + `prvm_language`/`menu_restart` wiring are all **complete and correct**;
  the gap is pure CONTENT — the runtime asset repo (`XonoticGodot.Assets/data/xonotic-data.pk3dir/`) ships zero
  `common.<lang>.po`. The unit tests pass because they fall through to the Base reference checkout's 60 .po.
  → To activate: copy `common.*.po` (≥ the languages in `languages.txt`) into `XonoticGodot.Assets`. That is a
  content-repo (git-LFS) change, out of scope for this code wave. **The code needs no change.**
- **F11 (T36) — `func_assault_wall` provides no collision** (should be an initially-SOLID_BSP gate that
  opens when its objective is destroyed). Recon explicitly deferred it (cosmetic collision-toggle). Most
  assault routes are still traversable; the win logic works. Carry to a T36 follow-up / T48 (map content).
- **F12 (T36) — Invasion STAGE round-end trigger is zero-sized** so it never fires via the live touch grid
  (STAGE is a minority invasion_type; ROUND, the default, is complete). Needs the placeholder to get a real
  brush volume (SetModel/SetSize) or a `.use` chain. Recon-flagged as a STAGE follow-up.
- **F16 (T43) — per-monster `mr_death`/`mr_pain` server effects** (Wyvern death-toss velocity; Zombie death
  RES_ARMOR restore; non-zombie pain_finished 0.5 vs the hardcoded 0.34). Damage math is unaffected (all 5
  `mr_pain` return `take` unchanged); this is corpse velocity/armor + anim timing. Needs a per-descriptor
  `Monster.OnDeath/OnPain` hook. Documented deviation; carry to the NPC-polish task.
- **F23 (T28, nit) — octal `\NNN` decode emits one UTF-16 char per byte**, mangling a multi-byte UTF-8 octal
  sequence. Purely theoretical: real `.po` store non-ASCII as raw UTF-8 (charset=UTF-8/8bit), and octal is
  used only for single-byte control chars (which round-trip correctly). Never exercised by shipped data.
- **F24 (T36, nit) — `invasion_wave` with empty `.spawnmob` is dropped** (QC IL_PUSHes every wave so an
  empty-spawnmob wave at round N resets that round to random). Edge case; a map deliberately placing an
  empty wave diverges. Carry with the STAGE/wave-table follow-up.
- **F25 (T37, nit) — gunner `VehicleEnter`/`VehicleExit` pass the bumblebee BODY as slot1** where QC passes
  the gun SLOT, and the gunner Exit fires after the link is cleared. **Zero current impact** (no stock
  mutator subscribes these chains). Fix when a vehicle-hook mutator that inspects the seat is ported.
