# Asset reference check

_Generated 2026-07-02 by `tools/parity-asset-check.py`. 161 literal asset refs from src/+game resolved against 8 mounts (15898 files); 75 interpolated refs skipped (not statically checkable)._

Findings are LEADS: a missing ref may be dead code or a write-path. Confirm, then fix the
path/asset — or record it in [asset-check-known.yaml](asset-check-known.yaml) if accepted.

## Missing (6) — referenced by code, no file resolves

| ref | referenced at |
|---|---|
| `gfx/hud/default/nopreview_map` | game/hud/MapVotePanel.cs:569 |
| `gfx/hud/default/weaponvortex` | game/hud/TextureCache.cs:26 |
| `gfx/menu/default/nopreview_map` | game/hud/MapVotePanel.cs:572 |
| `gfx/vehicles/axh-ring` | game/hud/VehicleHud.cs:171 |
| `gfx/vehicles/axh-target` | game/hud/VehicleHud.cs:154; game/hud/VehicleHud.cs:190; game/net/NetGame.cs:3662 |
| `models/sphere.md3` | game/client/NadeOrbRenderer.cs:78; game/client/NadeOrbRenderer.cs:79 |

## Present but unsupported format (2) — magic sniff failed

| ref | resolved file | referenced at |
|---|---|---|
| `models/casing_shell.mdl` | `models/casing_shell.mdl` | game/client/ShellCasings.cs:134 |
| `models/gibs/chunk.mdl` | `models/gibs/chunk.mdl` | game/client/ModelGibs.cs:72; game/client/ModelGibs.cs:165 |

_135 refs resolved ok. Full data in `_asset-check.json`._
