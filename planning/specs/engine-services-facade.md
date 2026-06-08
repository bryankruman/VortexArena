# Spec — Engine-Services Facade (the `dpdefs` reimplementation)

Implements [ADR-0009](../decisions/ADR-0009-engine-services-facade.md). This is the C# layer that provides the
~420 distinct builtins the QuakeC calls. Source of truth: `Base/data/xonotic-data.pk3dir/qcsrc/dpdefs/` and the
Darkplaces implementations in `Base/darkplaces/*vm_cmds.c`.

## Design

- Logic in `XonoticGodot.Common` depends on **interfaces** (`IEntityService`, `ITraceService`, …), not Godot, so it is
  headless-testable. `XonoticGodot.Engine.Services` provides the Godot-backed implementations.
- Globals-mutating builtins return **structs** instead of writing `v_forward`/`trace_*` globals (the QC already
  hides these behind `MAKE_VECTORS`/`GET_TAG_INFO` macros, so call sites adapt cleanly).
- Implement in **hot-call order** (counts below from the report). Skip the confirmed-unused set.

## Service catalog (priority = hot-call count)

| Service / interface | Builtins (examples) | Hot calls | Difficulty | Notes |
|---|---|---:|---|---|
| **Strings** | strcat, substring, sprintf, strzone/unzone, tokenize, strreplace, strconv, ftos/vtos | strcat 1637, sprintf 732, substring 621 | Trivial–Mod | C# `string`; strzone/unzone **delete**; match DP `sprintf` extensions, `^x` color codes, UTF-8 counting. |
| **Entity mgmt** | spawn, remove, setorigin, setmodel, find*, findradius, nextent, copyentity | spawn 627, setorigin 436, remove 335 | Moderate | `setorigin`/`setmodel` must relink the area grid + set absmin/absmax/cullbox. |
| **Cvars** | cvar, cvar_set, cvar_string, registercvar, autocvars | cvar 533, cvar_string 220 | Moderate | Dictionary + autocvar binding (via source-gen); honor cvar *names/semantics* (OPEN Q5). |
| **Sound** | sound, sound7, ambientsound, pointsound, getsoundtime, soundlength, precache_sound | sound 433 | Moderate | `AudioStreamPlayer3D` + channel manager (CHAN_* override rules, pitch, playback cursor). |
| **Console / commands** | localcmd, stuffcmd, registercommand, clientcommand, tokenizebyseparator | localcmd 182, stuffcmd 54 | Moderate | Command bus; `stuffcmd` injects into a client's console (control channel → an RPC). |
| **Net write** | WriteByte/Short/Long/Coord/Angle/String/Entity, WriteUnterminatedString | WriteByte 410 | **Very hard** | The protocol primitives; the `.SendEntity`/`CSQC_Ent_Update` contract. See [networking spec](networking.md). |
| **Net read** | ReadByte/Short/Long/Coord/Angle/String/Float | ReadByte 343 | **Very hard** | Mirror of write; CSQC entity dispatch by entnum. |
| **Collision & trace** | traceline, tracebox, tracetoss, pointcontents, nudgeoutofsolid | traceline 213, tracebox 221 | **Hard (fidelity)** | Full DP `trace_t` (contents, surfaceflags, texture name). See [determinism spec](determinism-and-physics.md). |
| **2D draw (HUD)** | drawpic, drawstring, drawcolorcodedstring, drawfill, drawsubpic, drawsetcliparea, stringwidth | drawpic 156, drawstring 121 | Moderate | Godot `CanvasItem`/`RenderingServer` 2D; font metrics via `stringwidth`. |
| **Math/vectors** | makevectors, vectoangles, vectorvectors, normalize, vlen, sin/cos/etc., random | makevectors 151 | Trivial | `System.Numerics`/Godot math; return structs not globals. |
| **Model & tags** | setattachment, gettagindex, gettaginfo, frameforname, frameduration, setmodelindex | gettaginfo 84, setattachment 87, gettagindex 64 | **Hard** | Named-tag world-transform query; drives all weapon/effect attachment (R10). |
| **Filesystem** | fopen, fgets, fputs, fclose, search_begin/end, whichpack, buf_loadfile | fopen 86 | Moderate | C# streams over the **pk3 VFS** with gamedir search + `override/` precedence. |
| **Particles/effects** | pointparticles, trailparticles, particleeffectnum, te_* family, boxparticles | pointparticles 79 | **Hard** | Parse `effectinfo.txt`; build particle system matching DP spawn/trail. Defer to Phase 5. |
| **String buffers** | buf_create, bufstr_add/get/set, buf_sort, buf_implode, matchpattern | buf_create 40, bufstr 167 | Trivial–Mod | `List<string>`; glob/`matchpattern` semantics. |
| **Skeletal** | skel_create/build/get_boneabs/set_bone/mul_bones/delete | skel_ 27 | **Very hard** | CPU software-skeleton (build pose, read/write bone matrices). Parallel to Godot `Skeleton3D`. |
| **HTTP/URI** | uri_get, uri_post, uri_escape, URI_Get_Callback, netaddress_resolve | uri_get 20 | Moderate | `HttpClient` + callback dispatcher. |
| **Input/keys (CSQC/menu)** | getinputstate, getmousepos, keynumtostring, findkeysforcommand, getkeybind | findkeysforcommand 19 | Moderate | Keybinding registry + cursor/keydest; reproduce keynum↔string tables for config compat. |
| **Visibility/PVS** | checkpvs, getlight, adddynamiclight, lightstyle | checkpvs 31 | **Very hard** | BSP PVS query; ship/recompute PVS or approximate (R9). `getlight` ~unused. |
| **BSP surface queries** | getsurfacenumpoints/point/normal/texture/triangle | getsurface* 17 | **Very hard** | Expose parsed BSP/model mesh to game code; powers warpzones/portals. Phase 5. |
| **Crypto** | crypto_* (d0_blind_id) | crypto_ 132 | **Very hard** | **Drop** per [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md); replace auth. |
| **Menu host-cache** | gethostcache*/sethostcache*/refreshhostcache | gethostcache 46 | Hard | Server browser; replace, don't port (OPEN Q9). Phase 5. |
| **Misc/time** | gettime, strftime, error, coredump, loadfont | — | Trivial–Mod | Clocks, formatting, debug. |

## Confirmed unused — do NOT implement

ODE physics (`physics_enable/addforce/addtorque` = 0), CSQC particle-theme spawner (`spawnparticle/particletheme`
= 0), manual CSQC scene rendering (`addentity` 0, `renderscene` 1), `getlight` 0, `runstandardplayerphysics` 0,
`altstr_*` 0, `serverkey` 0, `getgamedirinfo` 0, `ReadEntity` 0 (replaced by `findfloat(entnum)`).

## Implementation notes

- **The integration win:** the game funnels everything through `lib/net.qh` wrappers and the deglobalization
  macros, so you only need the ~16 primitive Write/Read, the trace builtins, and the tag/skeleton queries to be
  truly faithful — the rest of the game code ports against the wrappers unchanged.
- Scaffold the **interface signatures** mechanically from `dpdefs` declarations; hand-write the bodies.
- Each "Very hard" service is a candidate for its own mini-spec and its own owner.
