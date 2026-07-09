# UW-0046 — Draft: Some updates to make Invasion playable, various improvements to monster code and balance

- **Source:** `data:Mario/monster_updates@33edc21c46d8`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/gametypes/gametype/invasion/sv_invasion.qc`, `qcsrc/common/monsters/sv_monsters.qc`, `qcsrc/common/monsters/monster/mage.qc`, `qcsrc/common/monsters/monster/wyvern.qc`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Comprehensive Invasion mode refactor and monster AI improvements. Unifies three Invasion modes into round-based only; adds player elimination system, HUD monster count, loot/skill scaling, spawnpoint flying/swimming filters. Monster AI: fixes warpzone-aware aiming/targeting via WarpZone_RefSys_TransformOrigin in monster_makevectors and Monster_FindTarget; refactors state machine (MONSTER_STATE_NORMAL/NOMOVE/LEAP); simplifies Monster_Attack_Melee signature; adds turn rate tuning (g_monsters_turnrate). Per-monster: Mage health 400→150 + passive heal logic, Wyvern melee attack + health 150→350, Zombie/Golem/Spider attack timing via Monster_Delay chains. Balance: round time 120s→600s, monster counts scale by player count, individual damage/speed tuning.

## Portability
qc-gameplay — pure QuakeC gameplay subsystem

## Completeness (upstream)
Draft/WIP stage; logically sequenced commits, well-structured refactor, needs playtesting

## Quality
Good — clean state machine, proper warpzone API usage, consistent delay chains, minor TODOs

## Roadmap / design alignment
High — Invasion is planned; warpzone fixes essential; no design conflicts

## Recommendation
Port requires parity doc for warpzone transforms (WarpZone_RefSys_TransformOrigin), state machine (MONSTER_STATE_*), Invasion elimination/loot/skill systems. Key blocking: C# coordinate transform semantics for monster aiming. High priority — warpzone aiming fix solves broken AI on warpzone maps.
