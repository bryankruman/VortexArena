# Warpzones: Xonotic Base -> XonoticGodot Function-Level Audit

_Generated 2026-06-15 from a 25-agent cross-codebase audit (14 parallel readers of Base + port, synthesis, 10 adversarially-verified gaps). Base = `Base/data/xonotic-data.pk3dir/qcsrc`; port = `XonoticGodot/`._

## TL;DR

Server warpzones work (parse, link, Teleport rotates origin/vel/angles, combat traces/splash cross zones). Client half unported: no portal render, no view/prediction, so warpzones are opaque walls and crossing snaps the camera.

**Root cause:** Warpzones DO function on the server: a trigger_warpzone brush is parsed, registered in WarpzoneManager, linked to its partner, its plane is auto-derived from geometry, and the per-frame TouchAreaGrid pass fires the trigger's Touch -> Teleport, which rotates origin/velocity/angles through the seam (all covered by passing WarpzoneSpawnTests). Combat traces and splash also cross zones (T45, landed). The chain breaks at STEP 6 (client rendering) and the coupled client-side view/prediction handling — these were never ported (T45's explicitly-carried residual, TODO.md:524 + :429). Concretely, the missing pieces are: (1) the client portal render — there is no Godot SubViewport+Camera3D drawing the linked OUT side onto the portal surface; the surface is a dead static dark StandardMaterial3D mirror produced by HeroMaterials.BuildPortal (HeroMaterials.cs:168). game/net/NetGame.cs has NO warpzone code at all. (2) the client-side crossing handlers WarpZone_FixView / FixNearClip / View_Inside/Outside and the teleported view/input rotate are unported (TODO.md:1268); client prediction (TriggerTouch.PredictTeleportsAmbient) predicts trigger_teleport but NOT warpzones. Net effect at runtime: the portal looks like an opaque/wavy wall (you cannot see through it), and crossing it snaps/rubber-bands the camera with no seamless view because the server teleport is never rendered or predicted client-side. So warpzones 'don't work' visually/experientially even though the server transform is correct. The single named gap to fix is the absent portal-render + WarpZone_FixView client subsystem in game/net/NetGame.cs (and a Godot SubViewport portal-camera builder), NOT anything in Warpzone.cs / WarpzoneRadiusQuery.cs / TraceServiceWarpzoneExt.cs.

## Runtime chain status (where it breaks)

| Step | Status | Evidence |
|------|--------|----------|
| map/entity parse — .bsp entity lump trigger_warpzone -> Entity with classname + "*N" inline model | **PRESENT** | GameWorld.cs:2136-2158 SpawnMapEntities iterates _mapEntities, ApplyDictFields sets e.Model="*N", then SpawnFuncs.TrySpawn(cls,e); spawnfuncs registered at MapObjectsRegistry.cs:108-109 (trigger_warpzone / trigger_warpzone_position). |
| spawn/registration — dispatched to WarpzoneManager and registered in the zone list | **PRESENT** | WarpzoneSpawns.TriggerWarpzoneSetup (Warpzone.cs:449) -> Sink (wired in GameWorld.cs:393 to Warpzones.OnMapEntity) -> AddMapZone (Warpzone.cs:384) which SetModel()s the brush for real bounds + queues it; InitMapZones (Warpzone.cs:406, called GameWorld.cs:552) -> SpawnFromBrush -> Add() pushes into _zones. Verified by WarpzoneSpawnTests.cs:77 (2 zones registered). |
| link/reconnect — zone paired to partner zone + transform built (trigger_warpzone_reconnect equiv) | **PRESENT** | WarpzoneManager.Link (Warpzone.cs:139-160) two-pass target/targetname pairing -> LinkOneWay (Warpzone.cs:162) builds WarpzoneTransform; plane auto-derived from brush via DerivePlaneFromBrush/getsurface* (Warpzone.cs:229). trigger_warpzone_position aiment resolved in ResolveAiment (Warpzone.cs:420). Verified linked+oriented by WarpzoneSpawnTests.cs:78,102-125,128-137. |
| per-frame touch — player physics detects overlap and invokes teleport (server) | **PRESENT** | SimulationLoop.cs:227 runs _physics.TouchAreaGrid(c) after every client move -> TriggerTouch.Run (TriggerTouch.cs:38-49) fires .Touch of every SOLID_TRIGGER the player box overlaps; warpzone trigger's Touch (Warpzone.cs:306/320) calls Teleport. Trigger bounds are linked via SetModel/SetSize->LinkEdict (EngineServices.cs:149-178). |
| transform — origin/velocity/angles/view actually rotated through the seam | **PRESENT** | WarpzoneManager.Teleport (Warpzone.cs:186-206) applies TransformOrigin/Velocity/Angles (WarpzoneTransform, Warpzone.cs:47-65) and SetOrigin; OldOrigin reset to cancel interpolation. Momentum-preserving transform verified by WarpzoneSpawnTests.cs:94-99 (exit at partner plane, speed conserved). |
| rendering — portal surface drawn as a live view of the other side | **MISSING** | No SubViewport/Camera3D portal render anywhere in game/ (only unrelated GpuWarmPass.cs uses SubViewport); NetGame.cs has ZERO warpzone/portal/SubViewport/FixView references (grep empty). The warpzone surface is rendered as a static dark near-black StandardMaterial3D mirror in HeroMaterials.BuildPortal (HeroMaterials.cs:168-188). TODO.md:524 (T45) + :429 list 'portal SubViewport render' as the carried residual. |
| client view/prediction across the crossing — FixView/FixNearClip/View_Inside + teleported view/input rotate | **MISSING** | EntityMovementStep client prediction (game/net/EntityMovementStep.cs via TriggerTouch.PredictTeleportsAmbient, TriggerTouch.cs:139-169) predicts ONLY trigger_teleport, never warpzones; no WarpZone_FixView/FixNearClip/View_Inside port exists (TODO.md:1268). The seam is therefore not client-predicted and the view is not rotated client-side on crossing. |
| trace-through — weapon/aim/splash traces cross the portal (combat traversal) | **PRESENT** | WeaponFiring.cs:141,233,358 call Api.Trace.TraceLineWarpzone; WeaponSplash.cs:76 calls WarpzoneRadiusQuery.FindRadiusWarpzone; recursion in WarpzoneRadiusQuery.cs (TraceWarpzone:172 / Recurse:371, 16-zone guard). Manager published to the trace ambient via TraceService.SetWarpzoneManager (GameWorld.cs:558) -> TraceServiceWarpzoneBridge.Publish (TraceServiceWarpzoneExt.cs:29). This is T45, landed. |

**Break points:**

- game/net/NetGame.cs — no warpzone/portal/SubViewport/FixView handling whatsoever (grep returns nothing): the client never renders the other side and never rotates the view on a crossing. THIS is the primary runtime break.
- game/loaders/HeroMaterials.cs:168-188 BuildPortal — the warpzone/dpcamera surface is built as a static dark near-black StandardMaterial3D mirror instead of a live camera (SubViewport) view of the linked OUT plane, so the portal is opaque/wavy, not see-through.
- Missing client subsystem: WarpZone_FixView / WarpZone_FixNearClip / WarpZone_View_Inside-Outside and the ENT_CLIENT_WARPZONE[_CAMERA/_TELEPORTED] view/input rotate are unported (tracked TODO.md:1268), so no seamless view across the seam.
- game/net/EntityMovementStep.cs / TriggerTouch.PredictTeleportsAmbient (TriggerTouch.cs:139-169) — client prediction handles trigger_teleport only, never warpzones, so crossing a warpzone rubber-bands/snaps the local camera (server-authoritative teleport with no client predict + no FixView).

---

# Part 1 - How warpzones work in Base (function-level operational reference)

204 functions across 8 areas. The warpzone system is a shared client/server library (`lib/warpzone`) plus integration hooks across movement, weapons, bots, and rendering.

## lib/warpzone/anglestransform

**Role.** This is the rotation/orientation algebra layer underpinning Xonotic's warpzone (seamless portal) system. A "transform" here is NOT a full affine transform — it is a pure rotation represented as a Quake `vector` of Euler angles (pitch x, yaw y, roll z), interpreted in "fixedmakevectors/fixedvectoangles space" (a sign-corrected angle convention where makevectors and vectoangles are true inverses). Translation ("shift") is handled separately and only appears in the two GetPostShift helpers, which combine a rotation-transform with a translation vector. The layer provides: applying a rotation to a vector/point, composing two rotations (Multiply), inverting a rotation, the FU/FR 180-degree "turn the portal around" operations used to map a warpzone's in-direction to its out-direction, left/right division (compose-with-inverse) for computing the relative transform between two warpzone planes, range/canonicalization helpers (Normalize, CancelRoll), and a set of From/To/ApplyTo wrappers that bridge between this internal rotation space and the engine's player `angles` (entity orientation) vs `v_angles` (view/aim angles) conventions, which differ by a pitch-sign flip. Higher warpzone code (server-side teleport fixups and client-side CSQC prediction/view transforms) calls these to rotate player velocity, view angles, and positions across portal boundaries so the transition is seamless.

### `fixedmakevectors / FIXED_MAKE_VECTORS (.qh macros)`  _(common)_
`void fixedmakevectors(vector a); FIXED_MAKE_VECTORS(angles, forward, right, up)`

Provides a makevectors variant that is a true inverse of vectoangles. With POSITIVE_PITCH_IS_DOWN=1, fixedmakevectors is just the engine makevectors and FIXED_MAKE_VECTORS is MAKE_VECTORS (the default Quake convention already matches). With POSITIVE_PITCH_IS_DOWN=0, fixedmakevectors first negates pitch (a.x = -a.x) then calls makevectors, and FIXED_MAKE_VECTORS calls fixedmakevectors, copies v_forward/v_right/v_up into the supplied locals via GET_V_GLOBALS, then CLEAR_V_GLOBALS. The whole point is that fixedmakevectors and fixedvectoangles round-trip exactly.

- **Called by:** AnglesTransform_Apply, AnglesTransform_Multiply, AnglesTransform_Invert (every function that needs the basis vectors of a transform)
- **Gotchas:** Quake's native makevectors and vectoangles disagree on pitch sign by default; this macro family hides that. Right vector handedness: the engine's v_right points to the player's right which is a LEFT-handed-feeling convention, which is why -v.y appears in Apply (see Apply). Two compile paths exist; the rest of the file's sign conventions only make sense relative to the active POSITIVE_PITCH_IS_DOWN value.

### `fixedvectoangles / fixedvectoangles2 (.qh macros)`  _(common)_
`vector fixedvectoangles(vector fwd); vector fixedvectoangles2(vector fwd, vector up)`

Inverse of fixedmakevectors: turn basis vectors back into an angle-vector. POSITIVE_PITCH_IS_DOWN=1: call vectoangles(a) / vectoangles2(a,b), store in scratch global _fixedvectoangles(/2), negate the pitch component (.x *= -1), and return it (comma-expression macro). POSITIVE_PITCH_IS_DOWN=0: they are plain aliases of vectoangles / vectoangles2.

- **Called by:** AnglesTransform_Multiply, AnglesTransform_Invert
- **Gotchas:** Implemented as comma-expression macros writing to file-scope noref scratch globals (_fixedvectoangles, _fixedvectoangles2) — not reentrant in a meaningful threading sense but fine for single-threaded QC. The pitch negation here is the mirror of the negation buried in the =1 vs =0 makevectors paths, ensuring the inverse property.

### `AnglesTransform_Apply`  _(common)_
`vector AnglesTransform_Apply(vector transform, vector v)`

Rotates an arbitrary vector v by the rotation described by the angle-vector transform. Builds the rotation basis: FIXED_MAKE_VECTORS(transform, forward, right, up). Returns forward*v.x + right*(-v.y) + up*v.z. Effectively v expressed in the transform's local frame, i.e. matrix-times-vector where the matrix columns are (forward, -right, up).

- **Called by:** AnglesTransform_Multiply (twice, on forward and up), AnglesTransform_Multiply_GetPostShift, AnglesTransform_PrePostShift_GetPostShift; and warpzone code rotating velocities/offsets across portals
- **Gotchas:** The -v.y is the key handedness gotcha: v_right is negated because the engine's right vector convention is opposite to the +Y axis the math wants, so the basis used is (forward, -right, up) to get a consistent right-handed mapping. This applies a pure rotation only — no translation; callers add shift separately. v is treated as components in the standard axis frame (x=forward-axis, y=left/+Y-axis, z=up-axis).

### `AnglesTransform_Multiply`  _(common)_
`vector AnglesTransform_Multiply(vector t1, vector t2)  // result represents applying t1 then ... i.e. composition A B`

Composes two rotation-transforms into one angle-vector. Extract t2's basis via FIXED_MAKE_VECTORS(t2, forward, right, up). Rotate t2's forward and up vectors by t1 using AnglesTransform_Apply (forward = Apply(t1, forward); up = Apply(t1, up)). Reconstruct an angle-vector from the rotated forward+up pair with fixedvectoangles2(forward, up). The result is the single rotation equivalent to applying t2 then t1 to a vector (matrix product t1*t2).

- **Called by:** AnglesTransform_RightDivide, AnglesTransform_LeftDivide, AnglesTransform_ApplyToAngles, AnglesTransform_ApplyToVAngles, and warpzone relative-transform computations
- **Gotchas:** Only forward and up are re-derived; right is implied by the forward+up reconstruction (vectoangles2 ignores any inconsistent right). Composition order: this is t1 ∘ t2 (t1 outer). The .qh comment labels it 'A B'. Note: a pure-rotation composition only — mirror handling is not encoded here as a separate flag (see the Invert TODO and TurnDirection functions for the warpzone direction-flip semantics).

### `AnglesTransform_Invert`  _(common)_
`vector AnglesTransform_Invert(vector transform)`

Computes the inverse rotation. Get the basis (forward, right, up) of transform. Because these are orthonormal, the inverse rotation matrix is the transpose, so build the inverse's forward and up rows from the columns: i_forward = (forward.x, -right.x, up.x); i_up = (forward.z, -right.z, up.z). Return fixedvectoangles2(i_forward, i_up).

- **Called by:** AnglesTransform_RightDivide, AnglesTransform_LeftDivide; warpzone code computing the reverse-portal transform
- **Gotchas:** The -right.x / -right.z mirror the same -v.y handedness correction used in Apply (right column is stored negated). Only forward (column x->row) and up (column z->row) of the transpose are needed because fixedvectoangles2 reconstructs from forward+up. Source carries a TODO ('is this always -transform?') questioning whether the inverse is simply the component-negated angles — it is NOT in general, which is why the explicit transpose is done. Relies on the transform being a pure orthonormal rotation (no scale/shear).

### `AnglesTransform_TurnDirectionFR`  _(common)_
`vector AnglesTransform_TurnDirectionFR(vector transform)`

Turns a transform 180 degrees about its up axis, converting an in-direction orientation to the out-direction (and flipping forward and right while keeping up). Implemented purely as Euler arithmetic: transform.x = -transform.x; transform.y = 180 + transform.y; transform.z = -transform.z. The commented-out reference implementation is fixedvectoangles2(-v_forward, +v_up).

- **Called by:** warpzone setup mapping a portal plane's facing into the orientation a traveler should adopt on exit (FR = forward/right variant)
- **Gotchas:** The arithmetic shortcut (negate pitch, +180 yaw, negate roll) is an algebraic identity for a 180-deg up-axis turn in this Euler convention — comments tabulate the sign pattern (pitch -s+c, yaw -s-c, roll -s+c). FR vs FU differ only in how roll/up is treated (FR keeps up sign via z=-z to flip right; FU does 180-z). Choosing the wrong one mirrors the player's screen/roll across a warpzone.

### `AnglesTransform_TurnDirectionFU`  _(common)_
`vector AnglesTransform_TurnDirectionFU(vector transform)`

Also a 180-degree turn that maps in-direction to out-direction, but the forward/up variant. transform.x = -transform.x; transform.y = 180 + transform.y; transform.z = 180 - transform.z. The commented reference is again fixedvectoangles2(-v_forward, +v_up).

- **Called by:** warpzone setup, the forward/up variant of the in->out direction flip
- **Gotchas:** Differs from FR only in the roll term: here z = 180 - z instead of z = -z. The two variants correspond to two ways of resolving the 180-degree ambiguity (flip right vs flip up); picking the one that matches the engine's vectoangles2 reconstruction avoids an unwanted roll/mirror. Pure Euler arithmetic, no makevectors call, so it is cheap but convention-locked.

### `AnglesTransform_RightDivide`  _(common)_
`vector AnglesTransform_RightDivide(vector to_transform, vector from_transform)  // A B^-1`

Returns AnglesTransform_Multiply(to_transform, AnglesTransform_Invert(from_transform)). Computes the relative rotation that takes the from_transform frame to the to_transform frame when applied on the right: result = to * from^-1.

- **Called by:** warpzone code computing the transform from one portal endpoint's orientation to the other (right division)
- **Gotchas:** Order matters: this is A·B^-1 (the .qh annotates it). Pair with LeftDivide (A^-1·B) — using the wrong side yields the conjugate/opposite-handed relative transform and the portal exit orientation will be wrong.

### `AnglesTransform_LeftDivide`  _(common)_
`vector AnglesTransform_LeftDivide(vector from_transform, vector to_transform)  // A^-1 B`

Returns AnglesTransform_Multiply(AnglesTransform_Invert(from_transform), to_transform). Computes from^-1 · to: the relative rotation expressed in the from frame.

- **Called by:** warpzone relative-transform computation (left-division variant)
- **Gotchas:** Note the parameter naming/order is the reverse of RightDivide (from first, to second) to read naturally as 'from \ to'. Result equals A^-1·B; not commutative with RightDivide.

### `AnglesTransform_Normalize`  _(common)_
`vector AnglesTransform_Normalize(vector t, float minimize_roll)`

Canonicalizes an angle-vector to a preferred range. Step 1: wrap every component into (-180,180] using t.c -= 360*rint(t.c/360) for x,y,z. Step 2: decide if the orientation should be 'flipped' to the equivalent representation with smaller pitch (or roll). If minimize_roll: need_flip when roll t.z>90 or <=-90. Else (default, minimize pitch): need_flip when pitch t.x>90 or t.x<-90 (the asymmetric < vs <= deliberately allows exactly -90 for looking straight down). Step 3 if flipping: pitch -> (t.x>=0 ? 180-t.x : -180-t.x); yaw -> shift by -/+180 depending on sign; roll -> shift by -/+180 depending on sign. Returns t.

- **Called by:** warpzone code that needs canonical, range-limited angles before storing/sending or before display; general angle cleanup
- **Gotchas:** There are two equivalent Euler triples for any orientation (pitch p, yaw y, roll r) == (180-p, y+180, r+180); this picks the canonical one. The asymmetric pitch test (> 90 vs < -90, not <= ) is intentional so -90 (straight down) stays unflipped. minimize_roll swaps which of pitch/roll is kept small — used when roll must be the free parameter. rint (round-to-nearest-even) is used for the 360 wrap, so half-way cases round to even multiples.

### `AnglesTransform_CancelRoll`  _(common)_
`vector AnglesTransform_CancelRoll(vector t)`

Heuristically removes roll when the pitch is near a gimbal pole (+/-90), where yaw and roll become degenerate/interchangeable. epsilon=30. If pitch is within 30 deg of -90 (f=fabs(t.x-(-90))/epsilon < 1): fold roll into yaw as t.y += t.z, then t.z = 0. Else if pitch within 30 deg of +90: t.y -= t.z, t.z = 0. Otherwise leave t unchanged. Returns t.

- **Called by:** warpzone/view code that wants a roll-free orientation near vertical look angles (e.g. so the player's screen doesn't end up rolled after a portal)
- **Gotchas:** Self-described FIXME ('find a better method') — it is a soft, epsilon-based heuristic, not exact. Near the pole, a roll is equivalent to an opposite/same-sign yaw (sign depends on whether near +90 or -90), which is why one branch adds and the other subtracts t.z. The commented-out t.x assignments show pitch is intentionally NOT snapped to exactly +/-90, only roll is zeroed. Only acts within the 30-degree band; outside it roll is preserved.

### `AnglesTransform_ApplyToAngles`  _(common)_
`vector AnglesTransform_ApplyToAngles(vector transform, vector v)`

Applies a transform to an entity 'angles' value (object orientation), accounting for the pitch-sign difference between engine angles and the internal rotation space. POSITIVE_PITCH_IS_DOWN=1 path: negate v.x, Multiply(transform, v), negate v.x back, return. POSITIVE_PITCH_IS_DOWN=0 path: just Multiply(transform, v) (no flip needed).

- **Called by:** warpzone teleport code rotating a player's/entity's facing (.angles) across a portal
- **Gotchas:** angles vs v_angles differ by a pitch sign in the engine; this function and ApplyToVAngles are mirror images so that exactly one of the two flips pitch depending on the compile mode. The From/To wrappers exist to move raw angles into the rotation space; ApplyTo* are the combined convenience that wraps Multiply with the right flips.

### `AnglesTransform_ApplyToVAngles`  _(common)_
`vector AnglesTransform_ApplyToVAngles(vector transform, vector v)`

Applies a transform to a view-angles value (player aim, .v_angle). POSITIVE_PITCH_IS_DOWN=1 path: just Multiply(transform, v). POSITIVE_PITCH_IS_DOWN=0 path: negate v.x, Multiply, negate back. Exactly the opposite flip-pattern from ApplyToAngles.

- **Called by:** warpzone code rotating the player's view/aim direction across a portal (server fixup and CSQC view prediction)
- **Gotchas:** This is where view aim is carried through a warpzone seamlessly. The pitch flip is the inverse of the one in ApplyToAngles because angles and v_angles use opposite pitch sign conventions; getting this backwards inverts the player's vertical look after a portal.

### `AnglesTransform_FromAngles / AnglesTransform_ToAngles`  _(common)_
`vector AnglesTransform_FromAngles(vector v); vector AnglesTransform_ToAngles(vector v)`

Convert an engine entity 'angles' vector into the internal rotation space and back. POSITIVE_PITCH_IS_DOWN=1: both negate pitch (v.x = -v.x). POSITIVE_PITCH_IS_DOWN=0: both are identity (return v unchanged).

- **Called by:** warpzone code that needs to feed an entity's .angles into Apply/Multiply directly (rather than via ApplyToAngles)
- **Gotchas:** From and To are identical (both flip or both identity) because the pitch-flip is its own inverse — From and To exist as named pairs for readability/symmetry with the VAngles pair, which has the opposite behavior.

### `AnglesTransform_FromVAngles / AnglesTransform_ToVAngles`  _(common)_
`vector AnglesTransform_FromVAngles(vector v); vector AnglesTransform_ToVAngles(vector v)`

Convert engine view-angles (.v_angle) into the internal rotation space and back. POSITIVE_PITCH_IS_DOWN=1: both identity. POSITIVE_PITCH_IS_DOWN=0: both negate pitch (v.x = -v.x).

- **Called by:** warpzone code converting player aim angles to/from rotation space
- **Gotchas:** Mirror image of the From/ToAngles pair: in each compile mode, exactly one of the angles-pair / vangles-pair flips pitch and the other is identity. This is the single source of the angles-vs-vangles pitch-sign reconciliation that runs throughout warpzone view handling.

### `AnglesTransform_Multiply_GetPostShift`  _(common)_
`vector AnglesTransform_Multiply_GetPostShift(vector t0, vector st0, vector t1, vector st1)`

Composes two FULL affine transforms (rotation t + post-shift st) and returns the combined post-shift. Given outer (t0,st0) and inner (t1,st1), the composed map is t0*(t1*p+st1)+st0 = t0*t1*p + (t0*st1+st0). The combined rotation is AnglesTransform_Multiply(t0,t1) (computed by the caller); this returns only the combined shift: st0 + AnglesTransform_Apply(t0, st1).

- **Called by:** warpzone code that needs the full position transform (rotation+translation) across chained portals, where this function supplies the translation part
- **Gotchas:** Header annotation: 'transformed = original * transform + postshift'. Note the .qh prototype lists params as (sf0, st0, t1, st1) but the .qc uses (t0, st0, t1, st1) — t0 is a rotation transform, not a shift, so the .qh parameter name 'sf0' is a mild naming inconsistency. The shift st1 is rotated by t0 (the outer rotation) before being added to st0 — order is essential; rotating by t1 or adding before rotating would be wrong.

### `AnglesTransform_PrePostShift_GetPostShift`  _(common)_
`vector AnglesTransform_PrePostShift_GetPostShift(vector sf, vector t, vector st)`

Converts a pre-shift + rotation + post-shift specification into an equivalent rotation + post-shift only. For a map of form t*(p + sf) + st_pre style, it returns st - AnglesTransform_Apply(t, sf): the effective post-shift once the pre-shift sf is pushed through the rotation t. (Given the desired post-shift st and a pre-shift sf, the rotation-applied pre-shift is subtracted.)

- **Called by:** warpzone setup that defines a portal by a point/offset before rotation and needs to collapse it to the canonical (rotate-then-shift) form used by the rest of the pipeline
- **Gotchas:** The pre-shift is rotated by t and SUBTRACTED (st - Apply(t,sf)), the opposite sign from Multiply_GetPostShift's addition, because a pre-shift moves the input before rotation whereas a post-shift moves the output after. Keeping the canonical form (rotate then post-shift) consistent across all warpzone transforms is what lets Multiply_GetPostShift chain them.

**Entities / fields.** No spawnfuncs or entity classes are defined here; this is pure vector/angle math. The only stateful entities are two file-scope scratch globals declared in the .qh for the POSITIVE_PITCH_IS_DOWN=1 path: `noref vector _fixedvectoangles;` and `noref vector _fixedvectoangles2;`, used by the fixedvectoangles / fixedvectoangles2 macros to capture the builtin result, flip its pitch sign (.x *= -1), and return it. Consumers are warpzone entities elsewhere (warpzone_transform fields etc.) that store a transform angle-vector and a shift vector.

**Netcode.** No direct serialization in this file. The functions are pure (no entity/network reads or writes). They are, however, foundational to warpzone netcode: the same deterministic transform math runs on both server and client (CSQC) so that warpzone view/position/velocity transforms predicted client-side match the server, keeping portal traversal seamless under prediction. Because results feed networked player angles/origin/velocity, determinism (identical float behavior of makevectors/vectoangles across server and CSQC builds) is the implicit netcode contract; the POSITIVE_PITCH_IS_DOWN convention must be consistent between sides.

**Dependencies.** Engine builtins: makevectors (sets v_forward/v_right/v_up globals), vectoangles, vectoangles2 (two-arg forward+up -> angles). Engine math: rint, fabs. Macros from elsewhere in qcsrc/lib: MAKE_VECTORS, GET_V_GLOBALS, CLEAR_V_GLOBALS, MACRO_BEGIN/MACRO_END (these wrap the v_forward/v_right/v_up global handling so the algebra can be written with local vectors). Compile-time switch POSITIVE_PITCH_IS_DOWN (defaults to 1 in the .qh) selects which of two implementation blocks is compiled for the angle-space bridging functions and how fixedmakevectors/fixedvectoangles are defined.

## lib/warpzone/mathlib

**Role.** IMPORTANT FRAMING CORRECTION: Despite living under qcsrc/lib/warpzone/, mathlib.qc/.qh is NOT a matrix/vector/transform layer. It contains no matrix construction, no matrix multiply, no inverse, no point/normal transforms, and no eigen/solver helpers. It is a pure scalar/floating-point math shim — a QuakeC reimplementation of the C standard library <math.h>. Its explicit purpose (stated in the header comment) is to provide the C99 math.h functions that the DarkPlaces QCVM does NOT expose as builtins; functions DP already provides as builtins (acos, sin, cos, log, fabs, pow, sqrt, ceil, floor, rint, round) are left commented out for completeness. So while the file is physically located in the warpzone module's lib subtree, it does not implement the warpzone transform pipeline. The actual warpzone matrix/transform math (WarpZone_TransformOrigin, WarpZone_TransformVelocity, WarpZone_TransformAngles, makevectors/AnglesTransform_* etc.) lives elsewhere (e.g. common/util.qc AnglesTransform helpers and the warpzone server/client .qc files), NOT here. The relevance of this file to warpzones is only that warpzone code, like any QC, may call these C-math helpers (classification predicates like isnan/isfinite to guard against degenerate/NaN coordinates produced during transforms, and float utilities). The functions return scalars or pack multi-valued results into a vector (using .x/.y/.z as a tuple) purely as a return-value workaround for QC's lack of out-params/structs — these vectors are NOT spatial vectors. Every function below operates on plain floats.

### `fpclassify`  _(common)_
`int fpclassify(float e)`

Classifies a float: returns FP_NAN if isnan(e), FP_INFINITE if isinf(e), FP_ZERO if e==0, else FP_NORMAL. Note FP_SUBNORMAL is declared but never returned (subnormals are reported as FP_NORMAL).

- **Called by:** General float classification; not warpzone-specific.
- **Gotchas:** Order matters: NaN and Inf checked before the e==0 test. Never returns FP_SUBNORMAL.

### `isfinite`  _(common)_
`bool isfinite(float e)`

Returns true when e is neither NaN nor Inf, i.e. !(isnan(e) || isinf(e)).

- **Called by:** lgamma (guards non-finite input); general guards against degenerate floats from transforms.
- **Gotchas:** Depends on the isnan/isinf workarounds below.

### `isinf`  _(common)_
`bool isinf(float e)`

Detects infinity using the identity that for infinities e+e==e while e!=0 (finite nonzero doubles change under doubling; 0 is excluded explicitly).

- **Called by:** fpclassify, isfinite.
- **Gotchas:** Clever arithmetic trick rather than a builtin. Returns false for 0 because of the (e!=0) guard. Relies on IEEE overflow semantics in the QCVM.

### `isnan`  _(common)_
`bool isnan(float e)`

Copies e to f and returns (e != f); NaN is the only value not equal to itself.

- **Called by:** fpclassify, isfinite; sanity-checking coordinates.
- **Gotchas:** The self-inequality method is used deliberately because the alternative ftos()-based string check is unreliable across DP/GMQCC QCVMs and because DP was historically built with -ffinite-math-only (broke NaN compares); comment notes this is fixed in DP 0.8.5+. The commented-out string approach documents that '-nan' vs 'nan' differ between QCVMs.

### `isnormal`  _(common)_
`bool isnormal(float e)`

Returns isfinite(e) — treats every finite value as normal.

- **Called by:** general.
- **Gotchas:** Not a true isnormal: subnormals and zero are reported as normal since only finiteness is tested.

### `signbit`  _(common)_
`bool signbit(float e)`

Returns (e < 0).

- **Called by:** general.
- **Gotchas:** Cannot distinguish -0.0 from +0.0 (returns false for negative zero), unlike a true signbit.

### `acosh`  _(common)_
`float acosh(float e)`

Inverse hyperbolic cosine via log(e + sqrt(e*e - 1)).

- **Called by:** general math.
- **Gotchas:** Domain e>=1; produces NaN otherwise.

### `asinh`  _(common)_
`float asinh(float e)`

Inverse hyperbolic sine via log(e + sqrt(e*e + 1)).

- **Called by:** general math.
- **Gotchas:** Valid for all reals.

### `atanh`  _(common)_
`float atanh(float e)`

Inverse hyperbolic tangent via 0.5*log((1+e)/(1-e)).

- **Called by:** general math.
- **Gotchas:** Domain |e|<1; diverges at +/-1.

### `cosh`  _(common)_
`float cosh(float e)`

Hyperbolic cosine via 0.5*(exp(e)+exp(-e)).

- **Called by:** tanh; general.
- **Gotchas:** Uses local exp().

### `sinh`  _(common)_
`float sinh(float e)`

Hyperbolic sine via 0.5*(exp(e)-exp(-e)).

- **Called by:** tanh; general.
- **Gotchas:** Uses local exp().

### `tanh`  _(common)_
`float tanh(float e)`

Hyperbolic tangent as sinh(e)/cosh(e).

- **Called by:** general.
- **Gotchas:** Computes two exp pairs; potential overflow for large |e| before the ratio settles.

### `exp`  _(common)_
`float exp(float e)`

Natural exponential as pow(M_E, e).

- **Called by:** exp2 indirectly, expm1, cosh, sinh, erf, tgamma.
- **Gotchas:** Defined because DP has no exp builtin; accuracy is limited by pow's accuracy with the M_E constant.

### `exp2`  _(common)_
`float exp2(float e)`

Base-2 exponential as pow(2, e).

- **Called by:** general.
- **Gotchas:** Uses pow directly, not the local exp.

### `expm1`  _(common)_
`float expm1(float e)`

Computes exp(e)-1.

- **Called by:** general.
- **Gotchas:** Naive form loses precision for small e (the whole point of a real expm1 is to avoid this); here it is just exp(e)-1.

### `frexp`  _(common)_
`vector frexp(float e)`

Decomposes e into mantissa and exponent: sets .y = ilogb(e)+1 (the exponent), .x = e / 2^.y (the mantissa), .z = 0.

- **Called by:** general.
- **Gotchas:** Returns a tuple packed in a vector (NOT spatial): mantissa .x, exponent .y. Mantissa convention is [0.5,1) like C frexp because exponent is ilogb+1.

### `ilogb`  _(common)_
`int ilogb(float e)`

Returns floor(log2(fabs(e))) — the integer binary exponent.

- **Called by:** frexp.
- **Gotchas:** Undefined-ish for e==0 (log2(0) -> -inf). Duplicate of logb but returns int.

### `ldexp`  _(common)_
`float ldexp(float x, int e)`

Returns x * 2^e (inverse of frexp).

- **Called by:** general.
- **Gotchas:** Header prototype mislabels both params as 'e' (float ldexp(float e, int e)); the .qc uses (x, e).

### `logn`  _(common)_
`float logn(float e, float base)`

Logarithm of e in arbitrary base via log(e)/log(base).

- **Called by:** general.
- **Gotchas:** None beyond domain of log.

### `log10`  _(common)_
`float log10(float e)`

Base-10 log as log(e)*M_LOG10E.

- **Called by:** general.
- **Gotchas:** Multiplies natural log by precomputed 1/ln(10) constant.

### `log1p`  _(common)_
`float log1p(float e)`

Computes log(e+1).

- **Called by:** general.
- **Gotchas:** Naive; loses precision for small e unlike a true log1p.

### `log2`  _(common)_
`float log2(float e)`

Base-2 log as log(e)*M_LOG2E.

- **Called by:** ilogb, logb.
- **Gotchas:** Constant-scaled natural log.

### `logb`  _(common)_
`float logb(float e)`

Returns floor(log2(fabs(e))) as float.

- **Called by:** general.
- **Gotchas:** Functionally identical to ilogb but float-typed.

### `modf`  _(common)_
`vector modf(float f)`

Splits f into fractional and integer parts: .x = f - trunc(f) (fraction), .y = trunc(f) (integer), built via the basis-vector idiom '1 0 0'*frac + '0 1 0'*int.

- **Called by:** general.
- **Gotchas:** Tuple-in-vector (NOT spatial). Uses trunc (toward zero) so both parts share the sign of f, matching C modf.

### `scalbn`  _(common)_
`float scalbn(float e, int n)`

Returns e * 2^n.

- **Called by:** general.
- **Gotchas:** Same computation as ldexp with reordered/renamed params.

### `cbrt`  _(common)_
`float cbrt(float e)`

Cube root: copysign(pow(fabs(e), 1/3), e) so negative inputs return negative roots.

- **Called by:** general.
- **Gotchas:** pow on a negative base would be NaN, hence fabs+copysign. 1.0/3.0 written as float division.

### `hypot`  _(common)_
`float hypot(float e, float f)`

Euclidean magnitude sqrt(e*e+f*f).

- **Called by:** general; relevant to any 2D distance.
- **Gotchas:** Naive form can overflow/underflow for very large/small operands (no scaling), unlike a robust hypot.

### `erf`  _(common)_
`float erf(float e)`

Error function via a Wikipedia rational/exp approximation: f=e*e; copysign(sqrt(1 - exp(-f*(1.273239544735163 + 0.14001228868667*f)/(1 + 0.14001228868667*f))), e).

- **Called by:** erfc.
- **Gotchas:** Approximation, limited accuracy; magic numbers are the published fit coefficients (4/pi and the a-coefficient). copysign restores the odd symmetry of erf.

### `erfc`  _(common)_
`float erfc(float e)`

Complementary error function as 1.0 - erf(e).

- **Called by:** general.
- **Gotchas:** Inherits erf's approximation error; cancellation for large e.

### `lgamma`  _(common)_
`vector lgamma(float e)`

Log-gamma returning value in .x and sign of gamma in .y. Branches: (1) non-finite input -> returns |e| in .x and copysign(1,e) in .y; (2) non-positive integer -> NaN (poles of gamma); (3) e<0.1 -> recurses on lgamma(1-e) and applies the reflection formula lgamma(z)=log(pi)-log|sin(pi z)|-lgamma(1-z), tracking the sign through sin(pi*e); (4) e<1.1 -> recurses lgamma(e+1) minus log(e) (recurrence to shift argument up); (5) otherwise Stirling's approximation 0.5*log(2*pi*e)+e*(log(e)-1) after decrementing e, with sign +1.

- **Called by:** tgamma.
- **Gotchas:** Tuple-in-vector: .x magnitude, .y sign, .z scratch (set to 0 at end of branches). Comment marks accuracy as TODO. Reflection-branch reuses v.z temporarily to hold sin(pi*e). Recursion depth bounded by the argument-shifting branches.

### `tgamma`  _(common)_
`float tgamma(float e)`

True gamma function: takes lgamma(e), exponentiates the magnitude and multiplies by the sign: exp(v.x)*v.y.

- **Called by:** general.
- **Gotchas:** Overflows quickly for moderately large e; accuracy limited by lgamma approximation.

### `pymod`  _(common)_
`float pymod(float e, float f)`

Python-style modulo e - f*floor(e/f); result takes the sign of the divisor f.

- **Called by:** general; useful for angle wrapping (e.g. wrapping degrees into [0,360)).
- **Gotchas:** Documented truth table in comments: 1%2=1, -1%2=1, 1%-2=-1, -1%-2=-1. Differs from C fmod (which takes sign of dividend). TODO note about a possible %% operator.

### `nearbyint`  _(common)_
`float nearbyint(float e)`

Rounds to nearest integer by delegating to the rint builtin.

- **Called by:** general.
- **Gotchas:** Thin alias for rint; rounding mode is whatever rint uses (round-half-to-even typically).

### `trunc`  _(common)_
`float trunc(float e)`

Rounds toward zero: floor(e) for e>=0, ceil(e) for e<0.

- **Called by:** modf, fmod, remquo.
- **Gotchas:** Built from floor/ceil builtins.

### `fmod`  _(common)_
`float fmod(float e, float f)`

C-style modulo e - f*trunc(e/f); result takes the sign of the dividend e.

- **Called by:** general.
- **Gotchas:** Contrast with pymod (sign of divisor). Uses trunc, so it rounds the quotient toward zero.

### `remainder`  _(common)_
`float remainder(float e, float f)`

IEEE remainder e - f*rint(e/f) using round-to-nearest of the quotient.

- **Called by:** general.
- **Gotchas:** Differs from fmod by using rint (nearest) instead of trunc; result magnitude <= |f|/2.

### `remquo`  _(common)_
`vector remquo(float e, float f)`

Returns IEEE remainder in .x and the rounded quotient in .y: .y=rint(e/f), .x=e-f*.y, .z=0.

- **Called by:** general.
- **Gotchas:** Tuple-in-vector (NOT spatial). Quotient is full rint value, not the low-bits-only quotient that C remquo specifies.

### `copysign`  _(common)_
`float copysign(float e, float f)`

Returns |e| with the sign of f: fabs(e)*((f>0)?1:-1).

- **Called by:** cbrt, erf, lgamma.
- **Gotchas:** f==0 is treated as negative (the ternary's else branch), so copysign(x,0) yields -|x| — differs from real copysign which would consult the sign bit of zero.

### `nan`  _(common)_
`float nan(string tag)`

Produces a NaN via sqrt(-1); the tag string argument is ignored.

- **Called by:** lgamma, nextafter.
- **Gotchas:** Tag (NaN payload) is unused. Relies on sqrt(-1) yielding NaN in the QCVM.

### `nextafter`  _(common)_
`float nextafter(float e, float f)`

Crude next-representable-value: if e==f returns NaN; if e>f recurses negated to reduce to the e<f case; otherwise binary-searches downward from an initial step d=max(|e|,1e-23), halving d each iteration (b=a; a=e+d) until a==e, returning the last value b that was still distinguishable from e.

- **Called by:** nexttoward.
- **Gotchas:** Marked 'TODO very crude'. Magic epsilon 1e-23 seeds the search; loop terminates when the step underflows below ULP(e). Direction handled only by the e>f negation trick, so it always finds the next value toward larger magnitude in the e<f branch.

### `nexttoward`  _(common)_
`float nexttoward(float e, float f)`

Alias that forwards to nextafter(e, f).

- **Called by:** general.
- **Gotchas:** In C the second arg is long double; here it is just float, identical to nextafter.

### `fdim`  _(common)_
`float fdim(float e, float f)`

Positive difference max(e-f, 0).

- **Called by:** general.
- **Gotchas:** None.

### `fmax`  _(common)_
`float fmax(float e, float f)`

Returns max(e,f) (builtin).

- **Called by:** general.
- **Gotchas:** Thin wrapper; no NaN-propagation semantics of C fmax.

### `fmin`  _(common)_
`float fmin(float e, float f)`

Returns min(e,f) (builtin).

- **Called by:** general.
- **Gotchas:** Thin wrapper; no NaN semantics.

### `fma`  _(common)_
`float fma(float e, float f, float g)`

Fused multiply-add as e*f+g.

- **Called by:** general.
- **Gotchas:** NOT actually fused — computed in two ops, so it does not provide the single-rounding guarantee of a true fma.

### `isgreater`  _(common)_
`int isgreater(float e, float f)`

Returns e>f.

- **Called by:** general.
- **Gotchas:** Plain comparison; lacks the NaN-quiet semantics C's isgreater macro provides.

### `isgreaterequal`  _(common)_
`int isgreaterequal(float e, float f)`

Returns e>=f.

- **Called by:** general.
- **Gotchas:** Same NaN caveat.

### `isless`  _(common)_
`int isless(float e, float f)`

Returns e<f.

- **Called by:** general.
- **Gotchas:** Same NaN caveat.

### `islessequal`  _(common)_
`int islessequal(float e, float f)`

Returns e<=f.

- **Called by:** general.
- **Gotchas:** Same NaN caveat.

### `islessgreater`  _(common)_
`int islessgreater(float e, float f)`

Returns e<f || e>f (true unless equal or unordered).

- **Called by:** general.
- **Gotchas:** For NaN operands all comparisons are false, so it correctly returns false.

### `isunordered`  _(common)_
`int isunordered(float e, float f)`

Returns true when neither e<f, e==f, nor e>f holds — i.e. at least one operand is NaN.

- **Called by:** general.
- **Gotchas:** This is the canonical NaN-pair detector and the only comparison helper that meaningfully accounts for unordered values.

**Entities / fields.** No spawnfuncs, no entity fields, no classnames. Defines free functions only. The vector return type is abused as a 3-tuple container (frexp: mantissa in .x, exponent in .y; modf: fraction .x, integer .y; lgamma: value .x, sign .y; remquo: remainder .x, quotient .y) — these are NOT positions/directions despite the vector type.

**Netcode.** None. No CSQC serialization, no network reads/writes, no entity sync. Pure computational helpers callable on both server and client (common).

**Dependencies.** DarkPlaces QCVM builtins: log (natural log), pow, sqrt, fabs, floor, ceil, rint, exp is NOT a builtin (defined here via pow(M_E,...)), min, max, isnan-via-comparison. Constants from mathlib.qh: M_E, M_LOG2E, M_LOG10E, M_LN2, M_LN10, M_PI (macro), M_PI_2, M_PI_4, M_1_PI, M_2_PI, M_2_SQRTPI, M_SQRT2, M_SQRT1_2, and fpclassify codes FP_NAN/FP_INFINITE/FP_ZERO/FP_SUBNORMAL/FP_NORMAL. No dependency on any warpzone-specific code; this is leaf-level. Conversely, warpzone transform code and general QC code depend on these helpers (notably isnan/isfinite/isinf for sanity-checking floats).

## lib/warpzone/common

**Role.** This is the shared (common) core of the Xonotic warpzone system, compiled into BOTH the server (SVQC) and client (CSQC) via #ifdef guards. A warpzone is a portal: an "in" plane (entity-local, defined by warpzone_origin + warpzone_forward, derived from warpzone_angles) that maps onto an "out"/target plane (warpzone_targetorigin + warpzone_targetforward, from warpzone_targetangles). The mapping is encoded once at setup into an angles-transform pair (warpzone_transform, warpzone_shift) — a rotation/reflection plus a translation — using the AnglesTransform_* helper library. Every spatial quantity (origin, velocity/direction, angles, view angles, view) that crosses a zone is run through that transform. This file provides: (1) transform setup and the camera_transform callback used by the engine renderer/networking; (2) the family of Transform/UnTransform functions for the five quantity kinds; (3) warpzone-aware traces (TraceBox/TraceLine/TraceToss and their _ThroughZone variants) that follow a ray recursively across up to ~16 chained zones, accumulating the net transform into the global WarpZone_trace_transform and emitting first/last zone plus the normal engine trace_* globals in the FINAL zone's coordinate system; (4) warpzone-aware explosion targeting (WarpZone_FindRadius) that recurses through zones tracking a per-victim transform back to the blast frame; (5) "reference system" (refsys) bookkeeping to keep an entity's accumulated transform across frames as it passes chained zones; and (6) geometry/solidity helpers (BoxTouchesBrush, NearestPointOnBox, MoveOutOfSolid, ExactTrigger_Touch). It is the single source of truth both sides use so that prediction (client) and authority (server) agree on warp math.

### `WarpZone_Accumulator_Clear`  _(common)_
`void WarpZone_Accumulator_Clear(entity acc)`

Resets an accumulator entity to the identity transform: warpzone_transform = '0 0 0' (identity angles-transform) and warpzone_shift = '0 0 0' (no translation).

- **Called by:** WarpZone_Trace_InitTransform, WarpZone_RefSys_CheckCreate
- **Gotchas:** '0 0 0' is the AnglesTransform identity, not a literal nullity; it relies on the AnglesTransform convention that zero angles = identity rotation.

### `WarpZone_Accumulator_AddTransform`  _(common)_
`void WarpZone_Accumulator_AddTransform(entity acc, vector t, vector s)`

Left-multiplies (composes) transform [t,s] onto the accumulator: new transform tr = AnglesTransform_Multiply(t, acc.transform); new shift st = AnglesTransform_Multiply_GetPostShift(t, s, acc.transform, acc.shift). Stores both back. This is the core 'pass through one more zone' composition.

- **Called by:** WarpZone_Accumulator_Add, WarpZone_Accumulator_AddInverseTransform, WarpZone_RefSys_AddTransform, WarpZone_RefSys_AddIncrementally
- **Gotchas:** Order matters: [t,s] is applied as the OUTER/left transform (the newly-entered zone), acc is the inner/right (everything so far). Reversing the argument order silently produces a wrong-but-plausible transform — a classic source of mirror/offset bugs.

### `WarpZone_Accumulator_Add`  _(common)_
`void WarpZone_Accumulator_Add(entity acc, entity wz)`

Convenience wrapper: composes a warpzone's own (warpzone_transform, warpzone_shift) onto the accumulator via AddTransform.

- **Called by:** WarpZone_Trace_AddTransform, WarpZone_RefSys_Add, WarpZone_RefSys_AddIncrementally
- **Gotchas:** Reads the zone fields directly; assumes wz is a properly SetUp zone (or another accumulator reusing the same field names).

### `WarpZone_Accumulator_AddInverseTransform`  _(common)_
`void WarpZone_Accumulator_AddInverseTransform(entity acc, vector t, vector s)`

Composes the INVERSE of [t,s] onto the accumulator. Computes tt = AnglesTransform_Invert(t); ss = AnglesTransform_PrePostShift_GetPostShift(s, tt, '0 0 0') (the shift that makes [tt,ss] the true inverse), then calls AddTransform(acc, tt, ss).

- **Called by:** WarpZone_Accumulator_AddInverse, WarpZone_RefSys_AddInverseTransform, WarpZone_RefSys_AddIncrementally
- **Gotchas:** Inverting a transform is NOT just inverting the rotation — the shift must be recomputed (PrePostShift); the inline comment notes it 'probably can be done simpler' but is written for clarity.

### `WarpZone_Accumulator_AddInverse`  _(common)_
`void WarpZone_Accumulator_AddInverse(entity acc, entity wz)`

Convenience wrapper composing the inverse of a zone's transform onto the accumulator.

- **Called by:** WarpZone_RefSys_AddInverse
- **Gotchas:** Same ordering caveat as Add.

### `WarpZone_camera_transform`  _(common)_
`vector WarpZone_camera_transform(entity this, vector org, vector ang)`

Engine camera-transform callback registered by WarpZone_SetUp. (1) Fade short-circuit: if this.warpzone_fadestart is set AND the camera org is farther than warpzone_fadeend+400qu from the zone's box center (origin + 0.5*(mins+maxs)), return org unchanged (zone faded out; skip transform). (2) Save v_forward/right/up. (3) Transform org through this zone (TransformOrigin) and transform each of the saved basis vectors as velocities (TransformVelocity) to rotate the view basis. (4) Run traceline(warpzone_targetorigin -> transformed org, MOVE_NOMONSTERS) — sets up trace globals the renderer/engine uses (e.g. for near-clip/visibility). (5) Restore v_forward/right/up to the TRANSFORMED basis and return transformed org.

- **Called by:** engine (via setcamera_transform, during rendering/prediction); not called directly from QC
- **Gotchas:** The 400qu safety margin past fadeend is deliberate (typical speeds + latency) so the view doesn't pop. Clobbers the global trace_* via its traceline. v_forward/right/up are intentionally LEFT transformed on return (that's the point) — it does not restore them to the input basis.

### `WarpZone_SetUp`  _(common)_
`void WarpZone_SetUp(entity e, vector my_org, vector my_ang, vector other_org, vector other_ang)`

Bakes the portal mapping for zone e. (1) warpzone_transform = AnglesTransform_RightDivide(other_ang, AnglesTransform_TurnDirectionFR(my_ang)) — composes the out-plane orientation with the FLIPPED (turn-direction) in-plane orientation so a body entering the front of the in-plane exits the front of the out-plane facing outward. (2) warpzone_shift = PrePostShift_GetPostShift(my_org, transform, other_org) — the translation that maps my_org to other_org under that rotation. (3) Stores warpzone_origin=my_org, warpzone_targetorigin=other_org, warpzone_angles=my_ang, warpzone_targetangles=other_ang. (4) FIXED_MAKE_VECTORS(my_ang)->warpzone_forward and FIXED_MAKE_VECTORS(other_ang)->warpzone_targetforward (the in/out plane normals). (5) setcamera_transform(e, WarpZone_camera_transform) so the engine uses this zone's transform for rendering.

- **Called by:** warpzone spawn/init code (server and client zone setup, outside this file)
- **Gotchas:** The TurnDirectionFR on my_ang is the crucial 180-degree flip that makes a portal (you exit pointing OUT, not back into the wall). FIXED_MAKE_VECTORS is used instead of plain makevectors to avoid the engine's makevectors quirks; it also clobbers forward/right/up locals (reused twice). warpzone_forward/_targetforward are plane NORMALS used by PlaneDist/TargetPlaneDist.

### `WarpZone_Camera_camera_transform`  _(common)_
`vector WarpZone_Camera_camera_transform(entity this, vector org, vector ang)`

Engine callback for a FIXED security-camera view (not a portal). (1) Same fade short-circuit as WarpZone_camera_transform (return org if faded out beyond fadeend+400). (2) Set trace_endpos = this.warpzone_origin and makevectors(this.warpzone_angles) so the view is pinned to a fixed point/orientation. (3) Return warpzone_origin.

- **Called by:** engine (via setcamera_transform from WarpZone_Camera_SetUp)
- **Gotchas:** Ignores the incoming org/ang entirely (fixed camera). Writes trace_endpos directly and calls makevectors (clobbers v_forward/right/up) as the engine expects from a camera callback.

### `WarpZone_Camera_SetUp`  _(common)_
`void WarpZone_Camera_SetUp(entity e, vector my_org, vector my_ang)`

Configures a fixed-camera entity: stores warpzone_origin=my_org, warpzone_angles=my_ang, registers WarpZone_Camera_camera_transform via setcamera_transform.

- **Called by:** camera spawn/init code (outside this file)
- **Gotchas:** Header comment: assumes e.oldorigin and e.avelocity already point to view origin/direction (mapper/spawn convention).

### `WarpZoneLib_BoxTouchesBrush_Recurse`  _(common)_
`float WarpZoneLib_BoxTouchesBrush_Recurse(vector mi, vector ma, entity e, entity ig)`

Determines whether the box [mi,ma] (already world-space absolute corners) actually intersects the BSP brush of entity e, ignoring ig. (1) tracebox of a zero-size point from '0 0 0' to mi..ma (a degenerate box trace spanning the corners) with MOVE_NOMONSTERS, ignoring ig. (2) CSQC only: if trace_networkentity, abort (return 0) — a networked entity (player) blocks the test and cannot be un-solidified. (3) If nothing hit (!trace_ent) return 0. (4) If trace_ent == e, the box touches our brush -> return 1. (5) Otherwise some OTHER solid is in the way: temporarily set that entity SOLID_NOT, setorigin to unlink it, recurse, then restore its .solid and relink. Returns the recursive result.

- **Called by:** WarpZoneLib_BoxTouchesBrush
- **Gotchas:** Recursion depth = number of intervening solids along the corner span; each level mutates and restores a foreign entity's .solid + relinks via setorigin (side-effecting global linking). The tracebox uses the box corners as the trace endpoints (mi as start offset, ma as end) — a deliberate trick to test brush overlap with a point trace. Clobbers all trace_* globals. CSQC abort path silently returns 'no touch'.

### `WarpZoneLib_BoxTouchesBrush`  _(common)_
`float WarpZoneLib_BoxTouchesBrush(vector mi, vector ma, entity e, entity ig)`

Accurate brush-overlap test for box [mi,ma] vs entity e (ignoring ig). (1) Early-out: if e has no modelindex OR e.warpzone_isboxy, treat as full box -> return 1. (2) Q3 compat: if Q3COMPAT_COMMON and ig!=world, OR content bit 128 into ig.dphitcontentsmask (workaround for trigger_hurt on geit3ctf1 whose supercontents is oddly 128). (3) Force e to SOLID_BSP (saving/relinking) so tracebox sees its brush. (4) Call _Recurse. (5) Restore e.solid if changed (relink). (6) Clear the temporary 128 content bit. Return recurse result.

- **Called by:** WarpZone_Find (via IL_EACH), WarpZoneLib_ExactTrigger_Touch
- **Gotchas:** warpzone_isboxy short-circuits to a cheap AABB assumption. TODO in code: replace recursion with findbox_OrFallback for a single tracebox. Mutates ig.dphitcontentsmask and e.solid temporarily — not reentrant on the same entities; clobbers trace_* globals via _Recurse.

### `WarpZone_Find`  _(common)_
`entity WarpZone_Find(vector mi, vector ma)`

Returns the first warpzone whose brush overlaps box [mi,ma], or NULL. If !warpzone_warpzones_exist return NULL immediately. Otherwise IL_EACH over g_warpzones with predicate WarpZoneLib_BoxTouchesBrush(mi,ma,it,NULL); on first hit returns it.

- **Called by:** WarpZone_TraceBox_ThroughZone (initial 'are we starting inside a zone' check), WarpZone_TraceToss_ThroughZone (same)
- **Gotchas:** Used to detect 'started inside a warpzone' so the trace pre-transforms. Comment notes the move-away-from-near-clip intent. Each call runs full brush tests (clobbers trace_*).

### `WarpZone_MakeAllSolid`  _(common)_
`void WarpZone_MakeAllSolid()`

Sets every entity in g_warpzones to SOLID_BSP so that traces will actually HIT warpzone brushes. No-op if !warpzone_warpzones_exist.

- **Called by:** WarpZone_TraceBox_ThroughZone, WarpZone_TraceToss_ThroughZone (before the trace loop)
- **Gotchas:** Must be paired with WarpZone_MakeAllOther afterward, else zones stay solid and block normal gameplay traces. Note it does NOT call setorigin to relink — relies on the engine treating .solid change as sufficient here (in contrast to BoxTouchesBrush which relinks).

### `WarpZone_MakeAllOther`  _(common)_
`void WarpZone_MakeAllOther()`

Restores every warpzone in g_warpzones to SOLID_TRIGGER (their normal gameplay state). No-op if !warpzone_warpzones_exist.

- **Called by:** WarpZone_TraceBox_ThroughZone, WarpZone_TraceToss_ThroughZone (after the trace loop)
- **Gotchas:** NOT called on the early-fail 'goto fail' path that triggers BEFORE MakeAllSolid (the in-another-zone abort happens before zones are made solid, so they're still TRIGGER — consistent). But if MakeAllSolid ran and then code somehow jumps to fail, zones would be left solid; the actual fail label is positioned after MakeAllOther so the normal path is safe.

### `WarpZone_Trace_InitTransform`  _(common)_
`void WarpZone_Trace_InitTransform()`

Lazily creates the singleton accumulator entity WarpZone_trace_transform = new_pure(warpzone_trace_transform) if it doesn't exist, then clears it to identity via WarpZone_Accumulator_Clear.

- **Called by:** WarpZone_TraceBox_ThroughZone, WarpZone_TraceToss_ThroughZone (at start)
- **Gotchas:** Single shared global accumulator — a trace's net transform is only valid until the NEXT warpzone trace runs. Callers must read WarpZone_trace_transform (and firstzone/lastzone) immediately after the trace, before any other warp trace clobbers it.

### `WarpZone_Trace_AddTransform`  _(common)_
`void WarpZone_Trace_AddTransform(entity wz)`

Composes warpzone wz onto the global trace accumulator (WarpZone_Accumulator_Add(WarpZone_trace_transform, wz)).

- **Called by:** WarpZone_TraceBox_ThroughZone, WarpZone_TraceToss_ThroughZone (each time a zone is crossed)
- **Gotchas:** Accumulates in entry order; the final accumulator maps from the ORIGINAL frame to the FINAL frame the trace ended in.

### `WarpZone_TraceBox_ThroughZone`  _(common)_
`void WarpZone_TraceBox_ThroughZone(vector org, vector mi, vector ma, vector end, float nomonsters, entity forent, entity zone, WarpZone_trace_callback_t cb)`

The central warpzone-following box trace. Setup: WarpZone_trace_forent=forent; firstzone=lastzone=NULL; InitTransform. FAST PATH (no zones exist): if nomonsters==MOVE_NOTHING set trace_endpos=end, trace_fraction=1 (free flight), else plain tracebox; fire cb(org,trace_endpos,end); return. MAIN PATH: save v_forward/right/up. Map nomonsters: MOVE_WORLDONLY/MOVE_NOTHING -> nomonsters_adjusted=MOVE_NOMONSTERS (so warpzone brushes still register). contentshack: if forent has a dphitcontentsmask that lacks DPCONTENTS_SOLID, temporarily OR in DPCONTENTS_SOLID so warpzone brushes are hit. STARTING INSIDE A ZONE: wz=WarpZone_Find(org+mi,org+ma); if found set firstzone=lastzone=wz; if a target 'zone' was requested and wz!=zone -> we are in the WRONG zone: zero-length trace (sol=1, trace_fraction=0, trace_endpos=org) and goto fail; else AddTransform(wz) and transform org and end into that zone's frame. WarpZone_MakeAllSolid(). LOOP (i=16 budget, sol=-1, frac=0): decrement i, if <1 log 'Too many warpzones in sequence', trace_ent=NULL, break. tracebox(org,mi,ma,end,nomonsters_adjusted,forent); fire cb(org,trace_endpos,end). Capture startsolid on first iteration (sol<0 -> sol=trace_startsolid). Accumulate fraction in original parameterization: frac = trace_fraction = frac + (1-frac)*trace_fraction; if >=1 done -> break. If the hit entity is NOT a trigger_warpzone: if nomonsters==MOVE_NOTHING, or (MOVE_WORLDONLY && trace_ent), or (contentshack && (trace_dphitcontents & mask)==DPCONTENTS_SOLID) -> SKIP this hit and continue the trace from trace_endpos nudged forward by normalize(end-org) (so inner warpzones embedded in 'solid' can still be found; cannot use an inverted trace here or players could block portals); else break (real obstruction). If it IS a warpzone: wz=trace_ent; set firstzone if unset, lastzone=wz; if requested zone and wz!=zone break; AddTransform(wz); warp org=TransformOrigin(wz,trace_endpos) and end=TransformOrigin(wz,end); then STEP BACK: tracebox a short 32qu back along normalize(org-end) and set org=trace_endpos to nudge clear of the exit plane; loop. After loop: WarpZone_MakeAllOther(). fail label: if contentshack clear DPCONTENTS_SOLID again; restore trace_startsolid=sol; restore v_forward/right/up.

- **Called by:** WarpZone_TraceBox (cb=null), WarpZone_TrailParticles, WarpZone_TrailParticles_WithMultiplier; the _ThroughZone form is called directly by code that wants the callback and/or to restrict to a specific zone
- **Gotchas:** Hard recursion/iteration cap of 16 chained zones (i starts 16) — exceeding it aborts with trace_ent=NULL and a partial trace. trace_fraction returned is RE-PARAMETERIZED to the original org->end segment via the frac accumulation, NOT the last sub-trace's fraction. The final trace_endpos and trace_ent are in the FINAL zone's coordinate frame; to map back to the caller's frame use WarpZone_trace_transform / firstzone. Globals clobbered/SET as outputs: trace_endpos, trace_fraction, trace_ent (and the engine's trace_* in general), trace_startsolid (restored to first-iteration value), WarpZone_trace_firstzone, WarpZone_trace_lastzone, WarpZone_trace_transform, WarpZone_trace_forent. The forward-nudge by normalize(end-org) is only 1qu and the step-back is a fixed 32qu — both are epsilon/offset magic numbers tuned to avoid re-hitting the same plane. The early-fail (wrong zone) path leaves zones as SOLID_TRIGGER (MakeAllSolid not yet called) — correct, but means trace globals on that path are the zero-length values you set, not from an engine trace. contentshack mutates forent.dphitcontentsmask; always cleared at fail label. The commented-out 'transformed into same zone again' guard is intentionally disabled for the box path (still active in toss).

### `WarpZone_TraceBox`  _(common)_
`void WarpZone_TraceBox(vector org, vector mi, vector ma, vector end, float nomonsters, entity forent)`

Thin wrapper: WarpZone_TraceBox_ThroughZone with zone=NULL (follow through ALL zones) and cb=WarpZone_trace_callback_t_null (no callback).

- **Called by:** WarpZone_TraceLine, and general gameplay code wanting a warpzone-aware box trace
- **Gotchas:** Outputs all the same globals as _ThroughZone; read them immediately.

### `WarpZone_TraceLine`  _(common)_
`void WarpZone_TraceLine(vector org, vector end, float nomonsters, entity forent)`

Zero-size box trace: calls WarpZone_TraceBox with mi=ma='0 0 0' (a line/point trace through warpzones).

- **Called by:** gameplay code needing a warpzone-following line trace (hitscan, line-of-sight, etc.)
- **Gotchas:** Same globals as TraceBox. A point trace can still pass through and accumulate multiple zones.

### `WarpZone_TraceToss_ThroughZone`  _(common)_
`void WarpZone_TraceToss_ThroughZone(entity e, entity forent, entity zone, WarpZone_trace_callback_t cb)`

Warpzone-following ballistic (gravity) trace for entity e. Saves o0=e.origin, v0=e.velocity, g=PHYS_GRAVITY(NULL)*e.gravity. Sets forent, clears firstzone/lastzone, InitTransform, WarpZone_tracetoss_time=0. FAST PATH (no zones): tracetoss(e,forent); fire cb(e.origin,trace_endpos,trace_endpos); dt = dist/speed = vlen(e.origin-o0)/vlen(e.velocity); add dt to tracetoss_time; apply gravity to a copy (e.velocity_z -= dt*g); store WarpZone_tracetoss_velocity=e.velocity; restore e.velocity=v0; return. MAIN PATH: save v_forward/right/up. STARTING IN ZONE: wz=WarpZone_Find(e.origin+mins,e.origin+maxs); if found set firstzone=lastzone=wz; if requested zone mismatch -> tracetoss_time=0, trace_endpos=o0, goto fail; else AddTransform(wz), setorigin(e, TransformOrigin(wz,e.origin)), e.velocity=TransformVelocity(wz,e.velocity). MakeAllSolid. LOOP (i=16): budget check (abort -> trace_ent=NULL,break). tracetoss(e,forent); fire cb(e.origin,trace_endpos,trace_endpos). dt=vlen(trace_endpos-e.origin)/vlen(e.velocity); tracetoss_time+=dt; e.origin=trace_endpos; apply gravity e.velocity_z-=dt*g; if trace_fraction>=1 break; if hit ent not trigger_warpzone break; if trace_ent==wz (same zone again) log + trace_ent=NULL + break; wz=trace_ent; set firstzone/lastzone; if requested zone mismatch break; AddTransform(wz); warp e.origin=TransformOrigin(wz,e.origin), e.velocity=TransformVelocity(wz,e.velocity). STEP BACK: negate velocity, tracetoss again, dt back-out (tracetoss_time-=dt), e.origin=trace_endpos, negate velocity again. After loop MakeAllOther. fail: WarpZone_tracetoss_velocity=e.velocity; restore v_forward/right/up; RESTORE e.velocity=v0 and e.origin=o0 (entity left untouched; caller consumes trace_endpos, WarpZone_tracetoss_velocity, and the accumulated transform).

- **Called by:** WarpZone_TraceToss (cb=null); prediction/aim code that needs grenade/projectile landing through portals
- **Gotchas:** MUTATES e.origin and e.velocity DURING the trace, then restores both at the end — e must be a real entity whose transient mutation is acceptable, and this is NOT reentrant on the same e. 16-zone cap. Unlike the box trace, the 'same zone again' guard IS active here (FIXME comment questions if needed). tracetoss_time is APPROXIMATE (per-segment dist/speed, and the step-back subtracts an estimate). Gravity is integrated piecewise using PHYS_GRAVITY*e.gravity. trace_endpos and WarpZone_tracetoss_velocity are in the FINAL zone frame; combine with WarpZone_trace_transform to get caller-frame results. The step-back uses negated velocity + tracetoss (not a fixed 32qu like the box path).

### `WarpZone_TraceToss`  _(common)_
`void WarpZone_TraceToss(entity e, entity forent)`

Wrapper: WarpZone_TraceToss_ThroughZone(e, forent, NULL, WarpZone_trace_callback_t_null).

- **Called by:** gameplay/aim code (e.g. bot aiming, grenade prediction)
- **Gotchas:** Same e-mutation and global-output caveats as _ThroughZone.

### `WarpZone_TrailParticles_trace_callback`  _(common)_
`void WarpZone_TrailParticles_trace_callback(vector from, vector endpos, vector to)`

Trace callback that draws a particle trail for one elementary (per-zone) trace segment via __trailparticles(own, eff, from, endpos), using module globals WarpZone_TrailParticles_trace_callback_own/_eff.

- **Called by:** WarpZone_TraceBox_ThroughZone (as cb) when invoked through WarpZone_TrailParticles
- **Gotchas:** Uses file-global parameter passing (own/eff) because the callback signature is fixed — not reentrant across concurrent trail traces.

### `WarpZone_TrailParticles`  _(common)_
`void WarpZone_TrailParticles(entity own, float eff, vector org, vector end)`

Draws a warpzone-aware particle trail from org to end: stashes own/eff into the callback globals, then WarpZone_TraceBox_ThroughZone(org,'0 0 0','0 0 0',end,MOVE_NOMONSTERS,NULL,NULL,WarpZone_TrailParticles_trace_callback). The callback fires once per zone segment so the trail bends correctly through portals.

- **Called by:** weapon/effect code drawing tracers/trails (both sides)
- **Gotchas:** Runs a full warpzone trace as a side effect (clobbers all trace_* and the trace accumulator globals); zone-aware drawing relies on the per-segment callback rather than a single straight line.

### `WarpZone_TrailParticles_WithMultiplier_trace_callback`  _(client)_
`void WarpZone_TrailParticles_WithMultiplier_trace_callback(vector from, vector endpos, vector to)`

CSQC-only trail callback using boxparticles with count multiplier f and flags, owner velocity for stretch, drawn as a trail.

- **Called by:** WarpZone_TrailParticles_WithMultiplier
- **Gotchas:** #ifdef CSQC only. Uses file-global f/flags/own/eff; flags always include PARTICLES_DRAWASTRAIL (OR'd in by the caller).

### `WarpZone_TrailParticles_WithMultiplier`  _(client)_
`void WarpZone_TrailParticles_WithMultiplier(entity own, float eff, vector org, vector end, float f, int boxflags)`

CSQC-only: like WarpZone_TrailParticles but with a particle-count multiplier and boxparticles flags. Sets the callback globals (and ORs PARTICLES_DRAWASTRAIL into flags), then runs WarpZone_TraceBox_ThroughZone with the multiplier callback.

- **Called by:** client effect code (e.g. dense tracers)
- **Gotchas:** #ifdef CSQC only (declared with float boxflags in .qh, int boxflags in .qc — both widen the same). Same global-output side effects as TraceBox.

### `WarpZone_PlaneDist`  _(common)_
`float WarpZone_PlaneDist(entity wz, vector v)`

Signed distance of point v from the zone's IN plane: (v - warpzone_origin) dot warpzone_forward. Positive = in front of the entry plane (on the side you enter from).

- **Called by:** warpzone touch/side-test logic (outside this file) and any code deciding which side of the in-plane a point is on
- **Gotchas:** warpzone_forward is the in-plane NORMAL set at SetUp via FIXED_MAKE_VECTORS(my_ang). Sign convention is tied to that forward direction; getting it backwards inverts 'inside vs outside'.

### `WarpZone_TargetPlaneDist`  _(common)_
`float WarpZone_TargetPlaneDist(entity wz, vector v)`

Signed distance of v from the OUT/target plane: (v - warpzone_targetorigin) dot warpzone_targetforward.

- **Called by:** exit-side checks (e.g. has an entity fully emerged from the out plane)
- **Gotchas:** Uses warpzone_targetforward (out-plane normal from other_ang). Mirrors PlaneDist for the destination side.

### `WarpZone_TransformOrigin`  _(common)_
`vector WarpZone_TransformOrigin(entity wz, vector v)`

Maps a POSITION through the zone: warpzone_shift + AnglesTransform_Apply(warpzone_transform, v). Rotation/reflection plus translation.

- **Called by:** WarpZone_camera_transform, both trace loops (box & toss), WarpZone_FindRadius_Recurse, WarpZone_RefSys_TransformOrigin, and external warp code
- **Gotchas:** Includes the shift — do NOT use for directions/velocities (those must not be translated; use TransformVelocity). Confusing the two is a common bug.

### `WarpZone_TransformVelocity`  _(common)_
`vector WarpZone_TransformVelocity(entity wz, vector v)`

Maps a DIRECTION/velocity through the zone: AnglesTransform_Apply(warpzone_transform, v) — rotation only, NO shift.

- **Called by:** WarpZone_camera_transform (rotating view basis), WarpZone_TraceToss loops, WarpZone_RefSys_TransformVelocity, external code
- **Gotchas:** No translation by design. Also used to rotate basis vectors (forward/right/up) since they are directions.

### `WarpZone_TransformAngles`  _(common)_
`vector WarpZone_TransformAngles(entity wz, vector v)`

Maps an ENTITY-angles triple through the zone via AnglesTransform_ApplyToAngles(warpzone_transform, v).

- **Called by:** WarpZone_RefSys_TransformAngles, external warp code (model/entity orientation)
- **Gotchas:** Entity angles vs view angles differ in pitch sign convention; this is the entity-angles variant (use TransformVAngles for player view).

### `WarpZone_TransformVAngles`  _(common)_
`vector WarpZone_TransformVAngles(entity wz, vector ang)`

Maps VIEW angles (fixangle/player look) through the zone. If KEEP_ROLL: save roll, zero it, apply ApplyToVAngles, Normalize(true), CancelRoll, restore saved roll. Else: ApplyToVAngles then Normalize(false).

- **Called by:** WarpZone_RefSys_TransformVAngles, player view/fixangle warp code
- **Gotchas:** KEEP_ROLL is OFF by default (commented in .qh) so the default path discards/normalizes roll; mods that use roll in fixangle must #define KEEP_ROLL. Normalize's second arg differs by branch (true vs false) — controls roll handling.

### `WarpZone_UnTransformOrigin`  _(common)_
`vector WarpZone_UnTransformOrigin(entity wz, vector v)`

Inverse of TransformOrigin: AnglesTransform_Apply(Invert(transform), v - warpzone_shift). Subtract shift first, then apply inverse rotation.

- **Called by:** WarpZone_RefSys_TransformOrigin (from-side), external code mapping back through a zone
- **Gotchas:** Order is load-bearing: subtract shift BEFORE inverse-rotating. Inverting transform each call recomputes via AnglesTransform_Invert (not cached).

### `WarpZone_UnTransformVelocity`  _(common)_
`vector WarpZone_UnTransformVelocity(entity wz, vector v)`

Inverse direction map: AnglesTransform_Apply(Invert(transform), v). No shift.

- **Called by:** WarpZone_RefSys_TransformVelocity (from-side)
- **Gotchas:** No shift (direction). Pairs with TransformVelocity.

### `WarpZone_UnTransformAngles`  _(common)_
`vector WarpZone_UnTransformAngles(entity wz, vector v)`

Inverse entity-angles map: AnglesTransform_ApplyToAngles(Invert(transform), v).

- **Called by:** WarpZone_RefSys_TransformAngles (from-side)
- **Gotchas:** Entity-angles variant; see UnTransformVAngles for view angles.

### `WarpZone_UnTransformVAngles`  _(common)_
`vector WarpZone_UnTransformVAngles(entity wz, vector ang)`

Inverse view-angles map, mirroring TransformVAngles: KEEP_ROLL branch saves/zeros/restores roll with Normalize(true)+CancelRoll; else ApplyToVAngles(Invert) + Normalize(false).

- **Called by:** WarpZone_RefSys_TransformVAngles (from-side)
- **Gotchas:** Same KEEP_ROLL caveat as the forward version.

### `WarpZoneLib_NearestPointOnBox`  _(common)_
`vector WarpZoneLib_NearestPointOnBox(vector mi, vector ma, vector org)`

Clamps org componentwise into the AABB [mi,ma]: nearest = (bound(mi.x,org.x,ma.x), ...y, ...z). Returns the closest point on/in the box to org.

- **Called by:** WarpZone_FindRadius_Recurse (to find the nearest surface point of each candidate victim to the blast)
- **Gotchas:** Returns org itself if org is inside the box (distance 0). Pure utility, no globals.

### `WarpZoneLib_BadEntity`  _(common)_
`bool WarpZoneLib_BadEntity(entity e)`

Blacklist predicate for WarpZone_FindRadius: returns true (skip) if e is_pure, or classname is one of {weaponentity, exteriorweaponentity, sprite_waypoint, waypoint, spawnfunc, weaponchild, chatbubbleentity, buff_model, ""}, or classname startsWith "target_" or "info_".

- **Called by:** WarpZone_FindRadius_Recurse (FOREACH predicate and the wz-chain loop)
- **Gotchas:** Empty classname is excluded; the commented 'net_linked' note warns some real entities are linked without a classname, so empty-string exclusion can over-skip — a known limitation. Cosmetic/marker entities are filtered so explosions don't 'damage' them.

### `WarpZone_FindRadius_Recurse`  _(common)_
`void WarpZone_FindRadius_Recurse(vector org, float rad, vector org0, vector transform, vector shift, bool needlineofsight)`

Recursive warpzone-aware radius search (for splash damage through portals). org = current search center, rad = current radius, org0 = ORIGINAL blast origin (in this frame), (transform,shift) = how to map a victim's point back into the BLAST's coordinate system, needlineofsight toggles LOS. Returns if rad<=0. FOREACH_ENTITY_RADIUS(org,rad, !BadEntity): compute p = nearest point on the entity's box to org0; if needlineofsight, traceline(org->p) and skip if blocked (trace_fraction<1). If this is the entity's first hit OR the new distance |org0-p| is smaller than its stored dist: record WarpZone_findradius_nearest=p, dist=org0-p, findorigin=org, findradius=rad. Then by classname: 'warpzone_refsys' -> ignore (don't clobber refsys params); 'trigger_warpzone' -> push onto a local wz chain (WarpZone_findradius_next), mark hit=1, AND mark its paired zone (it.enemy) dist='0 0 0'/hit=1 so we never recurse back through the partner; otherwise -> store the current (transform,shift) onto the victim so WarpZone_TransformOrigin can later map blast<->victim, mark hit=1. After the loop, for each warpzone e in the collected chain: skip BadEntity; org0_new = TransformOrigin(e, org); traceline(e.warpzone_targetorigin -> org0_new) and take trace_endpos as org_new (push the search just past the exit plane); compose transform_new = Multiply(e.warpzone_transform, transform) and shift_new = Multiply_GetPostShift(...); recurse with org_new, reduced radius bound(0, rad - dist(org_new,org0_new), rad-8), org0_new, the new transform/shift; afterwards clear e.WarpZone_findradius_hit and e.enemy hit (so the zone can be reconsidered in sibling branches).

- **Called by:** WarpZone_FindRadius
- **Gotchas:** Recursion is bounded only by radius shrinkage (rad-8 cap per zone ensures strict decrease) — no fixed depth counter, so deeply nested zones rely on the radius reaching <=0. Marks the partner zone (.enemy) as already-hit to prevent immediate back-traversal (infinite loop guard). Stores the back-transform on EACH non-zone victim (overwrites warpzone_transform/warpzone_shift on those entities — acceptable because they're transient victims, but means those fields are clobbered on hit entities). The 8qu epsilon and the traceline-just-past-targetorigin are offsets to avoid re-entering the same zone. dist comparison uses vlen2 (squared) to avoid sqrt.

### `WarpZone_FindRadius`  _(common)_
`entity WarpZone_FindRadius(vector org, float rad, bool needlineofsight)`

Public entry for warpzone-aware splash targeting. Calls WarpZone_FindRadius_Recurse(org, rad, org, '0 0 0'(identity transform), '0 0 0'(no shift), needlineofsight). Then list_first = findchainfloat(WarpZone_findradius_hit, 1) to build the .chain list of all hit entities; clears each it.WarpZone_findradius_hit=0 via FOREACH_LIST over the chain; returns list_first.

- **Called by:** explosion/splash-damage code (RadiusDamage etc.) on both sides
- **Gotchas:** Returns a .chain-linked list head; caller must walk .chain. Each returned entity carries WarpZone_findradius_nearest (point to apply damage at), _dist (vector to blast through portals), and (for non-zone victims) warpzone_transform/_shift for mapping. The commented-out fast path (plain findradius when no zones and no LOS) is disabled because it 'sometimes finds nothing, breaking explosions' — so even the no-zone case goes through the recursion. Resets hit flags but NOT the dist/nearest/transform fields (they persist until next overwrite).

### `WarpZone_RefSys_GC`  _(common)_
`void WarpZone_RefSys_GC(entity this)`

Think function on a warpzone_refsys entity: reschedules nextthink=time+1; if its owner no longer points back at it (owner.WarpZone_refsys != this) it is orphaned, so delete(this).

- **Called by:** engine think scheduler (set via setthink in RefSys_CheckCreate)
- **Gotchas:** Self-deleting GC; runs every 1s. Relies on the owner back-pointer invariant being broken when a refsys is replaced/cleared.

### `WarpZone_RefSys_CheckCreate`  _(common)_
`void WarpZone_RefSys_CheckCreate(entity me)`

Ensures me has a valid refsys accumulator: if me.WarpZone_refsys.owner != me, spawn new(warpzone_refsys), set its owner=me, setthink=WarpZone_RefSys_GC, nextthink=time+1, and Accumulator_Clear it (identity).

- **Called by:** WarpZone_RefSys_AddTransform, _AddInverseTransform, _Copy
- **Gotchas:** Lazy creation; the owner-mismatch test also handles the case where me.WarpZone_refsys is NULL (NULL.owner != me).

### `WarpZone_RefSys_Clear`  _(common)_
`void WarpZone_RefSys_Clear(entity me)`

If me has a refsys, delete it and NULL the pointer (resets me's reference frame to identity).

- **Called by:** WarpZone_RefSys_Copy (when source has none), external reset code
- **Gotchas:** Immediate delete (not GC). After this me has no refsys so RefSys_Transform* treat it as identity.

### `WarpZone_RefSys_AddTransform`  _(common)_
`void WarpZone_RefSys_AddTransform(entity me, vector t, vector s)`

If [t,s] is non-identity (t!=0 or s!=0): CheckCreate me's refsys, then Accumulator_AddTransform(me.refsys, t, s). me.R := [t,s] me.R.

- **Called by:** WarpZone_RefSys_Add, WarpZone_RefSys_AddIncrementally
- **Gotchas:** Skips work entirely for identity transforms (avoids spawning a refsys needlessly).

### `WarpZone_RefSys_Add`  _(common)_
`void WarpZone_RefSys_Add(entity me, entity wz)`

me.R := wz me.R — composes a crossed zone's transform into me's reference frame via AddTransform.

- **Called by:** warp-handling code that updates an entity's accumulated frame when it passes a zone
- **Gotchas:** Header note: must NOT be mixed with AddIncrementally tracking on the same entity.

### `WarpZone_RefSys_AddInverseTransform`  _(common)_
`void WarpZone_RefSys_AddInverseTransform(entity me, vector t, vector s)`

If non-identity: CheckCreate then Accumulator_AddInverseTransform. me.R := [t,s]^-1 me.R.

- **Called by:** WarpZone_RefSys_AddInverse, WarpZone_RefSys_AddIncrementally
- **Gotchas:** Same identity short-circuit.

### `WarpZone_RefSys_AddInverse`  _(common)_
`void WarpZone_RefSys_AddInverse(entity me, entity wz)`

me.R := wz^-1 me.R via AddInverseTransform with wz's transform.

- **Called by:** warp-handling/un-warp code
- **Gotchas:** None beyond ordering.

### `WarpZone_RefSys_AddIncrementally`  _(common)_
`void WarpZone_RefSys_AddIncrementally(entity me, entity ref)`

Keeps me's reference frame tracking ref's refsys as ref changes. If me's cached incremental (transform,shift) already equals ref.refsys's current (transform,shift), nothing changed -> return. Otherwise: remove the OLD cached delta (AddInverseTransform of the cached incremental t/s), add ref's CURRENT refsys (Accumulator_Add(me.refsys, ref.refsys)), then update me's cached incremental shift/transform to ref's current values. Net: me.R := ref.R (me.Rref)^-1 me.R; me.Rref := ref.R.

- **Called by:** code that pins one entity's frame to a moving reference entity (e.g. a corpse/follower across zones)
- **Gotchas:** Header warning: ONLY sensible if RefSys_Add is NOT also called on me meanwhile, and me must not raise warpzone touch events (use a non-warping movetype like MOVETYPE_NONE/FOLLOW). The cached incremental fields are the dedup key; if they get out of sync the frame drifts.

### `WarpZone_RefSys_BeginAddingIncrementally`  _(common)_
`void WarpZone_RefSys_BeginAddingIncrementally(entity me, entity ref)`

Initializes the incremental tracking by caching ref.refsys's current (shift,transform) into me's WarpZone_refsys_incremental_shift/_transform, so the first AddIncrementally call computes a correct delta.

- **Called by:** setup code before a loop of AddIncrementally calls
- **Gotchas:** Must be called once before AddIncrementally, else the first delta is computed against stale/zero cache.

### `WarpZone_RefSys_TransformOrigin`  _(common)_
`vector WarpZone_RefSys_TransformOrigin(entity from, entity to, vector org)`

Maps a position from 'from's reference frame to 'to's: if from has a refsys, UnTransformOrigin(from.refsys, org) (into world/blast frame); then if to has a refsys, TransformOrigin(to.refsys, org). Returns to.R from.R^-1 org.

- **Called by:** cross-entity position math (e.g. relating two entities that have each crossed different zone chains)
- **Gotchas:** Either side missing a refsys is treated as identity for that side. Composes UnTransform(from) then Transform(to) — net change-of-basis.

### `WarpZone_RefSys_TransformVelocity`  _(common)_
`vector WarpZone_RefSys_TransformVelocity(entity from, entity to, vector vel)`

Velocity/direction change-of-basis between two refsys frames: UnTransformVelocity(from) then TransformVelocity(to). Returns to.R from.R^-1 vel (no shift).

- **Called by:** cross-entity velocity math
- **Gotchas:** No shift (direction).

### `WarpZone_RefSys_TransformAngles`  _(common)_
`vector WarpZone_RefSys_TransformAngles(entity from, entity to, vector ang)`

Entity-angles change-of-basis: UnTransformAngles(from) then TransformAngles(to).

- **Called by:** cross-entity orientation math
- **Gotchas:** Entity-angles variant.

### `WarpZone_RefSys_TransformVAngles`  _(common)_
`vector WarpZone_RefSys_TransformVAngles(entity from, entity to, vector ang)`

View-angles change-of-basis: UnTransformVAngles(from) then TransformVAngles(to).

- **Called by:** cross-entity view/look math
- **Gotchas:** Inherits KEEP_ROLL behavior of the underlying VAngles transforms.

### `WarpZone_RefSys_Copy`  _(common)_
`void WarpZone_RefSys_Copy(entity me, entity from)`

Copies from's reference frame into me: if from has a refsys, CheckCreate me's and copy warpzone_shift/transform; else Clear me's refsys (identity).

- **Called by:** WarpZone_RefSys_SpawnSameRefSys, frame-cloning code
- **Gotchas:** Deep-ish copy of just the two transform fields, not the whole entity.

### `WarpZone_RefSys_SpawnSameRefSys`  _(common)_
`entity WarpZone_RefSys_SpawnSameRefSys(entity me)`

spawn()s a new entity, RefSys_Copy(new, me) so it shares me's reference frame, returns it.

- **Called by:** code needing a helper entity in the same warp frame as an existing one
- **Gotchas:** Caller owns the returned entity's lifetime.

### `WarpZoneLib_ExactTrigger_Touch`  _(common)_
`bool WarpZoneLib_ExactTrigger_Touch(entity this, entity toucher, bool touchfunc)`

Precise trigger-overlap test used by EXACTTRIGGER_TOUCH. Take toucher's absmin/absmax; unless Q3COMPAT_COMMON, expand by 1qu on each side (legacy adjacent-player activation behavior; matches old DP SVQC behavior). If NOT called from a touch func, first do a cheap boxesoverlap(expanded toucher box, this.absmin/absmax) and return false if they don't overlap. Then do the accurate WarpZoneLib_BoxTouchesBrush(emin, emax, this, toucher).

- **Called by:** EXACTTRIGGER_TOUCH macro (in trigger touch functions), trigger code
- **Gotchas:** The 1qu expansion is skipped in Q3 compat (Q3 maps don't expect it). When touchfunc=true the cheap overlap is skipped (caller already knows boxes overlap from the engine touch dispatch). KILLS the trace globals (BoxTouchesBrush runs tracebox) — the .qh macro comment explicitly warns: 'WARNING: this kills the trace globals'. EXACTTRIGGER_TOUCH returns early from the CALLING function on no-overlap.

### `WarpZoneLib_MoveOutOfSolid_Expand`  _(common)_
`void WarpZoneLib_MoveOutOfSolid_Expand(entity e, vector by)`

Helper for un-sticking: tracebox e from its origin to origin+by, with mins/maxs each grown by eps=0.0625qu, MOVE_WORLDONLY. If trace_startsolid, return (can't expand this axis). If it hit something (trace_fraction<1), push e back the OTHER way by by*(1-trace_fraction) via setorigin (carving room on that axis).

- **Called by:** WarpZoneLib_MoveOutOfSolid (six times, once per ±axis)
- **Gotchas:** eps=0.0625 (1/16 qu) is the inflation epsilon. Moves e in the opposite direction of the probe by the un-traveled fraction. Operates with e's box temporarily zeroed by the caller (see MoveOutOfSolid).

### `WarpZoneLib_MoveOutOfSolid`  _(common)_
`int WarpZoneLib_MoveOutOfSolid(entity e)`

Try to nudge entity e out of solid geometry. (1) traceline(o,o,WORLDONLY) — if trace_startsolid, point is hopelessly stuck -> return 0. (2) tracebox(o,mins,maxs,o,WORLDONLY) — if NOT startsolid, e wasn't stuck -> return -1. (3) Save mins/maxs, zero the box, then for each of the six box faces call MoveOutOfSolid_Expand along that face's axis (eX*mins.x, eX*maxs.x, eY*mins.y, ...) while progressively restoring that component of the box (e.mins_x=m0.x, etc.) — expanding the collision box one face at a time and shoving e clear. setorigin(e). (4) Re-test tracebox at the new origin: if still startsolid, restore original origin and return 0 (can't fix); else return 1 (was stuck, now fixed).

- **Called by:** move_out_of_solid(e) macro; placement/teleport/spawn code
- **Gotchas:** Return codes are tri-state: 0 = couldn't fix (or hopeless), -1 = wasn't stuck (no action), 1 = fixed. The face-by-face box restoration order matters (each Expand runs with the box only partially restored, isolating one axis). Uses eps from Expand. Clobbers trace globals. Mutates e.origin/mins/maxs; restores origin on failure but the box is always fully restored by the per-face assignments. 'eps' identifier referenced via the const in Expand only — MoveOutOfSolid itself uses the full mins/maxs not eps.

**Entities / fields.** Entity classnames referenced/created: "trigger_warpzone" (the zone brush; trace loop only recurses on entities with this classname), "warpzone_refsys" (per-entity transform accumulator spawned by RefSys_CheckCreate, GC'd by RefSys_GC), "warpzone_trace_transform" (the singleton pure accumulator entity WarpZone_trace_transform). Globals (common.qh): IntrusiveList g_warpzones (STATIC_INIT'd), float warpzone_warpzones_exist, float warpzone_cameras_exist, entity WarpZone_trace_forent, entity WarpZone_trace_transform, entity WarpZone_trace_firstzone, entity WarpZone_trace_lastzone, vector WarpZone_tracetoss_velocity, float WarpZone_tracetoss_time, var WarpZone_trace_callback_t_null, #define MOVE_NOTHING -1. Zone fields (.vector unless noted): warpzone_transform, warpzone_shift, warpzone_origin, warpzone_angles, warpzone_forward, warpzone_targetorigin, warpzone_targetangles, warpzone_targetforward, .float warpzone_isboxy, .float warpzone_fadestart, .float warpzone_fadeend. Accumulator fields reuse warpzone_transform/warpzone_shift. FindRadius fields: .vector WarpZone_findradius_dist, .vector WarpZone_findradius_nearest, .vector WarpZone_findradius_findorigin, .float WarpZone_findradius_findradius, .float WarpZone_findradius_hit, .entity WarpZone_findradius_next. RefSys fields: .entity WarpZone_refsys, .vector WarpZone_refsys_incremental_shift, .vector WarpZone_refsys_incremental_transform. Also uses .entity enemy (a zone's paired/sibling zone), .entity owner, .entity oldorigin/.avelocity (Camera_SetUp doc). EXACTTRIGGER_TOUCH / EXACTTRIGGER_INIT macros and BITSET/BITCLR/BITXOR macro family defined in .qh.

**Netcode.** No explicit CSQC serialization (ReadByte/WriteEntity etc.) lives in this file — the warpzone network protocol is in the sibling client/server warpzone files. What this file IS responsible for, network-wise: setcamera_transform(e, WarpZone_camera_transform) registers a C-level camera transform callback the ENGINE invokes during rendering AND during entity prediction/networking, so the same warp math runs identically on both sides (the file is compiled into CSQC and SVQC alike). WarpZone_camera_transform additionally runs a traceline(targetorigin -> transformed org) to set up trace globals/visibility the renderer consults. In CSQC, WarpZoneLib_BoxTouchesBrush_Recurse aborts (returns 0) if trace_networkentity is set, because a networked entity (e.g. another player) the client cannot temporarily un-solid would corrupt the brush test — a CSQC-only correctness guard. WarpZone_TrailParticles_WithMultiplier (boxparticles path) is CSQC-only (#ifdef CSQC). Otherwise all functions are deterministic given identical zone fields, which is what keeps client prediction consistent with the server.

**Dependencies.** AnglesTransform_* library (AnglesTransform_Multiply, _Multiply_GetPostShift, _RightDivide, _TurnDirectionFR, _Invert, _PrePostShift_GetPostShift, _Apply, _ApplyToAngles, _ApplyToVAngles, _Normalize, _CancelRoll) supplies all transform algebra. Engine builtins: tracebox, traceline, tracetoss, setorigin, setcamera_transform, makevectors/FIXED_MAKE_VECTORS, boxesoverlap, findchainfloat, findradius-family. Engine trace_* globals (trace_endpos, trace_fraction, trace_startsolid, trace_ent, trace_dphitcontents, trace_networkentity) and v_forward/v_right/v_up. IntrusiveList (IL_NEW/IL_EACH) for g_warpzones; FOREACH_ENTITY_RADIUS / FOREACH_LIST / findchain for radius search. STATIC_INIT for g_warpzones. PHYS_GRAVITY macro for toss. checkextension.qh. CSQC pulls client/items; SVQC pulls weapons/_all. Q3COMPAT_COMMON flag and DPCONTENTS_SOLID/dphitcontentsmask for content masking. KEEP_ROLL compile flag (off by default) changes vangles handling. Spawn-side code (NOT in this file) sets up warpzone entities, sets classname "trigger_warpzone"/"warpzone_refsys", maintains g_warpzones and warpzone_warpzones_exist, and consumes the trace_* outputs.

## lib/warpzone/server

**Role.** This area is the server (SVQC) half of Xonotic's seamless-portal ("warpzone") system. It owns the full server-side lifecycle: it defines the map entities (spawnfuncs) that authors place — warpzone brushes, their orientation markers, cameras, and runtime reconnect triggers — and the deferred multi-step initialization that pairs each warpzone with its partner and derives the geometric portal plane/origin/angles from either the brush surfaces or a position marker. At runtime it owns the touch-driven player/entity teleport: it computes the transformed origin/velocity/view-angles using the common AnglesTransform math (in common.qc), un-sticks the entity from solid, registers the warp in the entity's reference-system chain, fixes view angles (either via engine fixangle or by spawning a short-lived networked "teleported" entity so the client can rotate its own moves smoothly), and fires the warpzone's target/killtarget triggers. It also serializes every warpzone and camera to clients (WarpZone_Send / WarpZone_Camera_Send) so CSQC can render the portal view and reproduce the same transform, runs the per-frame StartFrame pass (lazy init + observer/projectile fixup teleports), and supports moving warpzones via per-entity think functions that re-derive the transform when geometry changes. util_server.qc supplies the shared brush/box initializer (WarpZoneLib_ExactTrigger_Init) used by all of these triggers.

### `WarpZoneLib_ExactTrigger_Init`  _(server)_
`void WarpZoneLib_ExactTrigger_Init(entity this, bool unsetmodel) [util_server.qc]`

Shared brush/box initializer for all warpzone-style triggers. 1) If movedir is zero but angles are set, derives movedir = forward from angles (MAKE_VECTORS). 2) If model=="" treats it as an axis-aligned box (warpzone_isboxy=1, no exact brush matching). 3) Else precaches and _setmodel's the brush model; if the mapper supplied non-zero mins/maxs they override the model bounds AND force warpzone_isboxy=1. 4) setorigin; sets solid=SOLID_TRIGGER BEFORE setsize (comment: needed so area-grid linking is correct); setsize using scale if non-zero else raw mins/maxs; set_movetype MOVETYPE_NONE. 5) If unsetmodel, clears .model afterward (used so the brush isn't drawn but bounds remain).

- **Called by:** spawnfunc(trigger_warpzone) calls it with unsetmodel=false; the EXACTTRIGGER_INIT macro (common.qh) calls it with this/true.
- **Gotchas:** warpzone_isboxy is set both for modelless boxes and for any brush whose mins/maxs were overridden — boxy zones skip the accurate WarpZoneLib_BoxTouchesBrush check. Order matters: solid must be set before setsize for grid linking. trigger_warpzone passes unsetmodel=false (keeps model for surface-based plane derivation in InitStep_UpdateTransform); func_camera does NOT use this initializer at all (it builds its own).

### `WarpZone_TeleportPlayer`  _(common)_
`void WarpZone_TeleportPlayer(entity teleporter, entity player, vector to, vector to_angles, vector to_velocity) [server.qc]`

Low-level apply of a computed teleport to one entity. SVQC: records lastteleport_origin/lastteleporttime. Calls setorigin(player,to) (this also aborts any in-progress move when called from touch). Sets angles=to_angles. SVQC: oldorigin=to (DP unstick), fixangle=true; if bot, copies angles into v_angle and bot_aim_reset. Sets velocity=to_velocity. XORs EF_TELEPORT_BIT into effects (triggers client teleport puff). If player, clears FL_ONGROUND. Calls WarpZone_PostTeleportPlayer_Callback(player).

- **Called by:** WarpZone_Teleport (after origin/velocity/angle computation).
- **Gotchas:** fixangle=true is set here unconditionally, but WarpZone_Teleport later RESETS fixangle=false for real players when WARPZONE_USE_FIXANGLE is not defined (the smooth-client path). The setorigin side effect of aborting the move is relied upon by touch logic. teleporter param is unused in the body.

### `WarpZone_Teleported_Send`  _(server)_
`bool WarpZone_Teleported_Send(entity this, entity to, int sf) [server.qc, SVQC]`

CSQC send for the per-teleport carrier entity. Writes header ENT_CLIENT_WARPZONE_TELEPORTED then this.angles (which holds the warpzone_transform). Returns true.

- **Called by:** Engine, via setSendEntity on the warpzone_teleported entity created in WarpZone_Teleport.
- **Gotchas:** Carries the transform in .angles, not in any warpzone_* field. Only sent to drawonlytoclient (the teleported player).

### `WarpZone_Teleport`  _(common)_
`float WarpZone_Teleport(entity wz, entity player, float f0, float f1) [server.qc]`

Core teleport computation/commit, returns 1 on success, 0 if it would place the entity in solid. 1) Captures o0=origin+view_ofs, v0=velocity, a0=angles. 2) Computes target o1/o10=TransformOrigin(wz,o0), v1=TransformVelocity(wz,v0); for clients/bots a1=TransformVAngles(wz, PHYS_INPUT_ANGLES) else a1=TransformAngles(wz,a0). 3) If f0||f1 (retry-the-move path used for high-speed touches): tracebox backwards to worldonly, then forward through the warpzone (temporarily nulling player.owner) to land just past the plane; if landed before the target plane (d<0) nudges along v1 by d/dv. 4) Un-stick: tracebox at o1; if startsolid, setorigin and WarpZoneLib_MoveOutOfSolid — on success adopt the freed origin, on failure log 'would have to put player in solid' and return 0. 5) Commit: WarpZone_RefSys_Add(player,wz) (chain the transform), WarpZone_TeleportPlayer, WarpZone_StoreProjectileData, set warpzone_teleport_time=warpzone_teleport_zone bookkeeping. SVQC: extends warpzone_teleport_finishtime so the entity can't immediately teleport back (computes dt = how far along v1 it already moved past the plane; pads to one frametime). 6) Angle fixup: when WARPZONE_USE_FIXANGLE undefined — SVQC for players (vehicle->owner hax): sets fixangle=false and spawns a warpzone_teleported entity (MDL_Null, SendEntity=WarpZone_Teleported_Send, SendFlags=0xFFFFFF, drawonlytoclient=player, SUB_Remove after 1s, owner=player, enemy=wz, EF_NODEPTHTEST, angles=wz.warpzone_transform) so the client rotates its own moves smoothly; CSQC variant rotates VF_CL_VIEWANGLES directly.

- **Called by:** WarpZone_Touch (f=-d/... or -1, f1=0); WarpZone_StartFrame observer/SOLID_NOT fixup (-1,0); WarpZone_CheckProjectileImpact debug path (0,1).
- **Gotchas:** f0/f1 are frame-count multipliers for the back/forward retry trace, NOT booleans. The owner-nulling around the forward tracebox is needed so projectiles pass through their own shooter. RefSys_Add must happen before TeleportPlayer-induced touches to keep reference systems consistent. The finishtime padding is the anti-bounce-back guard; without it a fast entity straddling the plane re-teleports. Bots/clients use PHYS_INPUT_ANGLES (true look dir) not entity .angles for view-angle transform.

### `WarpZone_Touch`  _(server)_
`void WarpZone_Touch(entity this, entity toucher) [server.qc]`

Touch handler bound to every trigger_warpzone. Guards: ignore other trigger_warpzones; ignore if toucher already teleported this frame (time<=warpzone_teleport_finishtime); skip MOVETYPE_NONE/MOVETYPE_FOLLOW/tag_entity entities (can't safely teleport); skip if on the WRONG side of the plane (WarpZone_PlaneDist>=0, i.e. not yet through). EXACTTRIGGER_TOUCH macro re-confirms brush overlap (returns from the func if not). Computes back-trace frame count: d=24+max(vlen mins,vlen maxs); for non-clients f=-d/bound(...velocity...) (go back enough frames to be behind the zone), for clients f=-1 (one frame, less jarring). Calls WarpZone_Teleport(this,toucher,f,0); on success fires SUB_UseTargets_SkipTargets on this and this.enemy (BIT masks pick which target/killtarget sets to fire and skip), else logs a trace-level fail.

- **Called by:** Engine touch dispatch (settouch set in WarpZone_InitStep_FinalizeTransform).
- **Gotchas:** The >=0 plane-side check is what makes a warpzone one-directional per face: you only warp when crossing from the front (negative side). The 24qu constant is the ~16*sqrt2 trigger fudge converted to qu. Clients deliberately get only one frame of back-trace to avoid visible rubber-banding. SUB_UseTargets bit args differ between this and this.enemy so each zone's killtarget/target fire correctly.

### `WarpZone_Send`  _(server)_
`bool WarpZone_Send(entity this, entity to, int sf) [server.qc, SVQC]`

Serializes a trigger_warpzone to CSQC. Rebuilds sf from scratch: bit0=warpzone_isboxy, bit1=warpzone_fadestart present, bit2=origin nonzero. Writes header+sf byte, conditionally origin, then modelindex/mins/maxs/quantized-scale, then the four transform params (warpzone_origin/angles/targetorigin/targetangles), then fade range if present.

- **Called by:** Engine networking via setSendEntity (set in spawnfunc(trigger_warpzone)); resends forced by SendFlags=0xFFFFFF.
- **Gotchas:** Ignores the engine-passed sf and recomputes its own — the comment notes the flag must match clientside reconstruction exactly. scale is sent as bound(1,scale*16,255) so values are in 1/16 increments, clamped.

### `WarpZone_Camera_Send`  _(server)_
`bool WarpZone_Camera_Send(entity this, entity to, int sf) [server.qc, SVQC]`

Serializes a func_camera. Like WarpZone_Send but no boxy bit; sends enemy.origin and enemy.angles (the camera's view source) in place of the four-param transform set.

- **Called by:** Engine networking via setSendEntity (set in spawnfunc(func_camera)).
- **Gotchas:** Depends on enemy being resolved (WarpZoneCamera_InitStep_FindTarget); if enemy is null the sent origin/angles are zero. A camera is a fixed one-way view, so only origin+angles are needed, not a paired transform.

### `WarpZone_CheckProjectileImpact`  _(server)_
`float WarpZone_CheckProjectileImpact(entity player) [server.qc, SVQC, only under WARPZONELIB_KEEPDEBUG]`

Legacy debug safety net: if a projectile impacted without going through a warpzone it should have, this re-runs the previous move through the zone. Skips if teleported within last 0.1s; WarpZone_Find at the projectile box; logs diagnostics. Under WARPZONELIB_REMOVEHACK returns 0 (disabled). Otherwise restores warpzone_oldorigin/oldvelocity, retries WarpZone_Teleport(wz,player,0,1), fires targets on success or restores on failure, returns +1.

- **Called by:** WarpZone_Projectile_Touch (only in the KEEPDEBUG block, with full trace_* save/restore around it).
- **Gotchas:** Normally compiled out. Exists only to detect the (believed-fixed) engine bug where touch fires at the pre-teleport location. Saves/restores all trace_* globals around itself because WarpZone_Find/Teleport clobber them.

### `WarpZone_Projectile_Touch`  _(common)_
`float WarpZone_Projectile_Touch(entity this, entity toucher) [server.qc]`

Projectile-side touch helper for host weapon code to call. Returns true=ignore the impact, false=process normally. Ignores trigger_warpzone touchers; ignores impacts on the same frame as a teleport (time==warpzone_teleport_time, since the engine may raise stale touch events). SVQC: optional KEEPDEBUG re-check; then delegates to WarpZone_Projectile_Touch_ImpactFilter_Callback (host-defined) and returns true if it handled the impact. Else returns false.

- **Called by:** Host projectile/weapon touch code (declared in server.qh).
- **Gotchas:** This is a filter, not a teleporter — actual projectile warping happens through the normal WarpZone_Touch path; this just suppresses bogus impacts around teleport frames. The same-frame guard is the key correctness check.

### `WarpZone_InitStep_FindOriginTarget`  _(server)_
`void WarpZone_InitStep_FindOriginTarget(entity this) [server.qc, SVQC]`

Init step 1 for each trigger_warpzone: if killtarget set, resolves aiment = find target_position by targetname (errors if missing), then clears killtarget to string_null.

- **Called by:** WarpZone_StartFrame first-frame init loop.
- **Gotchas:** aiment becomes the orientation source used later by InitStep_UpdateTransform (it overrides surface-derived origin/angles). killtarget is consumed (nulled) so it won't be re-resolved.

### `WarpZonePosition_InitStep_FindTarget`  _(server)_
`void WarpZonePosition_InitStep_FindTarget(entity this) [server.qc, SVQC]`

Init step for each misc_warpzone_position: requires target; resolves enemy = the warpzone by targetname; errors if missing or if that zone already has an aiment (double-orientation). Sets enemy.aiment = this so the zone uses this marker for orientation.

- **Called by:** WarpZone_StartFrame first-frame init loop (warpzone_position_first chain).
- **Gotchas:** This is the alternative to killtarget for orienting a zone — a position entity targets the zone rather than the zone targeting a target_position. Errors hard if a zone is oriented twice.

### `WarpZoneCamera_Think`  _(server)_
`void WarpZoneCamera_Think(entity this) [server.qc, SVQC]`

Per-frame think for MOVING cameras (spawnflags&1). If camera origin/angles or its enemy's origin/angles changed since last save, re-runs WarpZone_Camera_SetUp(this, enemy.origin, enemy.angles) and updates the four save_* fields. nextthink=time (every frame).

- **Called by:** Scheduled by WarpZoneCamera_InitStep_FindTarget when spawnflags&1.
- **Gotchas:** Change-detection avoids recomputing the camera transform every frame when static. Only enabled for moving cameras.

### `WarpZoneCamera_InitStep_FindTarget`  _(server)_
`void WarpZoneCamera_InitStep_FindTarget(entity this) [server.qc, SVQC]`

Init/reconnect step for func_camera. Requires target; picks enemy uniformly at random among all entities with matching targetname (reservoir sampling: random()*++i<1); errors if none. Sets warpzone_cameras_exist=1, runs WarpZone_Camera_SetUp(this,enemy.origin,enemy.angles), forces SendFlags=0xFFFFFF. If spawnflags&1 schedules WarpZoneCamera_Think (nextthink=time) else nextthink=0.

- **Called by:** WarpZones_Reconnect; trigger_warpzone_reconnect_use (for reconnecting cameras).
- **Gotchas:** Random target selection means multiple entities sharing the camera's target name yield a non-deterministic view source. SendFlags reset forces a fresh network update after (re)linking.

### `WarpZone_InitStep_UpdateTransform`  _(server)_
`void WarpZone_InitStep_UpdateTransform(entity this) [server.qc, SVQC]`

Derives the portal plane origin (warpzone_origin) and angles (warpzone_angles) for one zone from its brush surfaces and/or its orientation marker. 1) Default org = origin, or box center if origin zero. 2) Iterates all surfaces/triangles (skipping 'textures/common/trigger' and 'trigger' faces), accumulating area-weighted face normal (norm) and centroid (point) via cross products. 3) If area>0: averages norm/point; if the summed normal length is too small (vdist(norm,<,0.99)) warns 'nonplanar' and disables autofixing (area=0); normalizes norm. 4) If aiment present (from killtarget or a position ent): take org/ang from aiment; if area>0 project org onto the plane, flip norm if it points into the zone (warn), set ang=vectoangles2(norm,up) keeping roll but facing exactly against the plane, negate pitch, and emit warnings if the marker had to be turned/moved. 5) Else if area>0: org=point, ang=vectoangles(norm), negate pitch. 6) Else error 'cannot infer origin/angles' (must use killtarget or position). Stores warpzone_origin/warpzone_angles.

- **Called by:** WarpZone_StartFrame init loop; WarpZone_Think (for moving zones, on both this and enemy).
- **Gotchas:** The plane normal is derived from brush geometry; an author marker (aiment) overrides/corrects the auto-derived plane. The pitch negation (ang.x=-ang.x) converts a direction vector into Quake angle convention. 'nonplanar' brushes lose autofixing. The negative-cross-product order (c-a,b-a) matters for normal direction; the forward-into-zone flip handles arrows pointed the wrong way. This is the geometric heart of plane/target derivation.

### `WarpZone_InitStep_ClearTarget`  _(server)_
`void WarpZone_InitStep_ClearTarget(entity this) [server.qc, SVQC]`

Breaks the bidirectional pairing: if this.enemy set, clears enemy.enemy then this.enemy. Resets pairing before re-finding.

- **Called by:** WarpZones_Reconnect, trigger_warpzone_reconnect_use (both first pass).
- **Gotchas:** Must run on all zones before any FindTarget pass so half-linked pairs don't survive a reconnect.

### `WarpZone_InitStep_FindTarget`  _(server)_
`void WarpZone_InitStep_FindTarget(entity this) [server.qc, SVQC]`

Pairs this zone with its partner. Returns early if already paired (enemy set). If target!="": unless autocvar_sv_warpzone_allow_selftarget, pre-sets enemy=this so the self gets skipped by the !e.enemy filter (one-IF optimization); reservoir-samples among entities with matching targetname that are unpaired (!e.enemy) and same classname (other targetnames may collide); errors if none found; sets this.enemy=e2 and e2.enemy=this (bidirectional).

- **Called by:** WarpZones_Reconnect, trigger_warpzone_reconnect_use.
- **Gotchas:** Only ONE of a pair needs to set target — whichever is processed first links both. The classname match prevents non-warpzones sharing a targetname from being picked. allow_selftarget cvar lets a zone teleport into itself (debug/special). Random selection if multiple candidate partners.

### `WarpZone_InitStep_FinalizeTransform`  _(server)_
`void WarpZone_InitStep_FinalizeTransform(entity this) [server.qc, SVQC]`

Finalizes a paired zone. Errors 'Invalid warp zone detected' if not mutually paired (enemy null or enemy.enemy!=this). Sets warpzone_warpzones_exist=1; calls WarpZone_SetUp(this, this.warpzone_origin, warpzone_angles, enemy.warpzone_origin, enemy.warpzone_angles) (builds warpzone_transform/shift/forward/targetforward and registers the camera transform in common.qc); binds settouch(this,WarpZone_Touch); SendFlags=0xFFFFFF. If spawnflags&1 (moving zone) schedules WarpZone_Think (nextthink=time) else nextthink=0.

- **Called by:** WarpZones_Reconnect (final pass), WarpZone_Think (recompute on movement), trigger_warpzone_reconnect_use (final pass).
- **Gotchas:** This is where the touch function is actually attached, so a zone is inert until finalized. Requires both sides' warpzone_origin/angles already computed by UpdateTransform. The mutual-pairing assertion guards against the FindTarget random selection leaving an asymmetric link.

### `spawnfunc(misc_warpzone_position) / spawnfunc(trigger_warpzone_position)`  _(server)_
`spawnfunc(misc_warpzone_position) [server.qc]; spawnfunc(trigger_warpzone_position) delegates`

Registers an orientation marker: pushes self onto the warpzone_position_first singly-linked list via warpzone_next. Uses keys target/angles/origin (resolved later). trigger_warpzone_position just calls the misc_ version.

- **Called by:** Engine map spawn.
- **Gotchas:** Does almost nothing at spawn — all real work deferred to WarpZonePosition_InitStep_FindTarget at first frame.

### `spawnfunc(trigger_warpzone)`  _(server)_
`spawnfunc(trigger_warpzone) [server.qc]`

Registers a portal brush. Defaults scale from modelscale then to 1. Calls WarpZoneLib_ExactTrigger_Init(this,false) (keeps model for surface plane derivation). setSendEntity(WarpZone_Send), SendFlags=0xFFFFFF, sets EF_NODEPTHTEST. Pushes onto warpzone_first (warpzone_next) and IL_PUSH(g_warpzones,this).

- **Called by:** Engine map spawn.
- **Gotchas:** Pairing/orientation/transform all deferred to the StartFrame init pipeline. EF_NODEPTHTEST so the portal renders through walls. unsetmodel=false is deliberate (UpdateTransform reads surfaces).

### `spawnfunc(func_camera)`  _(server)_
`spawnfunc(func_camera) [server.qc]`

Registers a one-way camera brush. Defaults scale from modelscale/1. If model set, precache + _setmodel. setorigin; setsize (scaled if scale set). solid defaults to SOLID_BSP, negative solid->SOLID_NOT. setSendEntity(WarpZone_Camera_Send), SendFlags=0xFFFFFF. Pushes onto warpzone_camera_first.

- **Called by:** Engine map spawn.
- **Gotchas:** Does NOT use WarpZoneLib_ExactTrigger_Init (unlike trigger_warpzone) — it manages its own model/solid/size and stays solid (a viewable surface), not a trigger. enemy/view target resolved later.

### `WarpZones_Reconnect`  _(server)_
`void WarpZones_Reconnect() [server.qc]`

Full relinking of all zones and cameras in four ordered passes over the lists: ClearTarget(all zones) -> FindTarget(all zones) -> CameraFindTarget(all cameras) -> FinalizeTransform(all zones). Re-pairs and rebuilds all transforms and touch bindings.

- **Called by:** WarpZone_StartFrame first-frame init (after UpdateTransform); can be called by host to relink.
- **Gotchas:** Pass ordering is mandatory: every pairing must be cleared before any re-find, and all transforms (warpzone_origin/angles) must already be computed by UpdateTransform before Reconnect runs.

### `WarpZone_Think`  _(server)_
`void WarpZone_Think(entity this) [server.qc, SVQC]`

Per-frame think for MOVING zones (spawnflags&1). If this or enemy origin/angles changed vs saved values, re-derives both sides: UpdateTransform(this), UpdateTransform(enemy), FinalizeTransform(this), FinalizeTransform(enemy); updates save_* fields. nextthink=time.

- **Called by:** Scheduled by WarpZone_InitStep_FinalizeTransform when spawnflags&1.
- **Gotchas:** Recomputes BOTH sides because the transform is relative to the pair. Change-detection avoids per-frame recompute for static zones. Only active for moving (func_door-driven etc.) zones.

### `WarpZone_StartFrame`  _(server)_
`void WarpZone_StartFrame() [server.qc]`

Per-frame entry called by host. (A) Lazy one-time init (warpzone_initialized): FindOriginTarget over all zones, WarpZonePosition FindTarget over markers, UpdateTransform over all zones, WarpZones_Reconnect, then WarpZone_PostInitialize_Callback. (B) If any warpzones exist, StoreProjectileData for every g_projectiles entry. (C) FOREACH_CLIENT: store projectile data; for observers / SOLID_NOT players run fixup teleports — WarpZone_Find at the client box, and if it overlaps a zone and is on the through side (PlaneDist<=0) WarpZone_Teleport(e,it,-1,0) WITHOUT firing targets; also handle plain teleporters via Teleport_Find/Simple_TeleportPlayer.

- **Called by:** Host per-frame StartFrame hook (declared in server.qh).
- **Gotchas:** This is where warpzone init actually happens (deferred from spawn to first frame so all entities exist). Observers/noclip players don't generate touch events, so they're teleported here manually with targets suppressed. StoreProjectileData snapshots origin/velocity/angles for the projectile-impact retry logic.

### `visible_to_some_client`  _(server)_
`bool visible_to_some_client(entity ent) [server.qc]`

Returns true if any real player client has ent in PVS (checkpvs from player eye). Used to avoid visibly snapping a warpzone while someone is looking through it.

- **Called by:** trigger_warpzone_reconnect_use (when spawnflags&1, skip-if-visible).
- **Gotchas:** Only counts IS_PLAYER && IS_REAL_CLIENT; uses origin+view_ofs as the eye point.

### `trigger_warpzone_reconnect_use`  _(server)_
`void trigger_warpzone_reconnect_use(entity this, entity actor, entity trigger) [server.qc]`

Runtime selective reconnect (.use handler). Marks warpzone_reconnecting on each zone/camera whose target matches this.target (or all if target empty), unless spawnflags&1 and the entity (or its partner, for zones) is currently visible to a client. Then runs the four-pass relink (ClearTarget/FindTarget/CameraFindTarget/FinalizeTransform) but only on entities flagged warpzone_reconnecting (Finalize also runs if the partner is reconnecting).

- **Called by:** Engine when a trigger_warpzone_reconnect/target_warpzone_reconnect is fired (set as .use in its spawnfunc).
- **Gotchas:** Matches on .target vs the entities' .target (note: not targetname — both target and targetname must be set on the zones). spawnflags&1 = don't reconnect zones currently visible (avoids visible pop). Finalize condition includes e.enemy.warpzone_reconnecting so a half-reconnected pair is still finalized on both sides.

### `spawnfunc(trigger_warpzone_reconnect) / spawnfunc(target_warpzone_reconnect)`  _(server)_
`spawnfunc(trigger_warpzone_reconnect) [server.qc]; target_warpzone_reconnect delegates`

Sets this.use = trigger_warpzone_reconnect_use so the entity, when triggered, relinks matching warpzones at runtime. target_ alias forwards to the trigger_ version.

- **Called by:** Engine map spawn.
- **Gotchas:** Both class names exist because either a 'trigger' or a 'target' naming reads naturally for a reconnect entity.

### `WarpZone_PlayerPhysics_FixVAngle`  _(server)_
`void WarpZone_PlayerPhysics_FixVAngle(entity this) [server.qc, SVQC]`

Antilag/late-input view-angle correction. Under !WARPZONE_DONT_FIX_VANGLE: for real clients whose v_angle.z<=360 (not yet adjusted), if the input timestamp (time - ping) predates the last teleport time, rotates v_angle by the teleport zone's transform (WarpZone_TransformVAngles) and adds 720 to v_angle.z as an 'already adjusted' sentinel.

- **Called by:** Host player physics / input processing (declared in server.qh).
- **Gotchas:** Compensates for client commands generated BEFORE the client learned about the teleport (their view angles are in the pre-teleport frame). The +720 on roll is a marker, not a real angle, decoded by checking <=360. Uses warpzone_teleport_zone and warpzone_teleport_time set in WarpZone_Teleport. Disabled by WARPZONE_DONT_FIX_VANGLE.

**Entities / fields.** SPAWNFUNCS (map classnames): misc_warpzone_position (alias trigger_warpzone_position) — orientation marker; pushes self onto warpzone_position_first list; uses fields target/angles/origin. trigger_warpzone — the portal brush; reads modelscale->scale, calls WarpZoneLib_ExactTrigger_Init, sets SendEntity=WarpZone_Send, EF_NODEPTHTEST, pushes onto warpzone_first list and IL_PUSH(g_warpzones); authoring keys: killtarget (target_position inside zone, arrow pointing AWAY), target (partner zone's targetname). func_camera — one-way camera view brush; resolves model, origin, size (scaled), solid (SOLID_BSP default, negative->SOLID_NOT), SendEntity=WarpZone_Camera_Send, pushes onto warpzone_camera_first; key: target (the view-origin entity), spawnflags&1 = moving (think). trigger_warpzone_reconnect (alias target_warpzone_reconnect) — runtime trigger whose .use = trigger_warpzone_reconnect_use; keys target (match), spawnflags&1 = skip-if-visible.\n\nINTERNAL CLASSNAMES spawned: warpzone_teleported (per-teleport networked angle-transform carrier), warpzone_refsys / warpzone_trace_transform (from common.qc).\n\nLINKED LISTS / GLOBALS: warpzone_first, warpzone_position_first, warpzone_camera_first (singly-linked via .warpzone_next); IntrusiveList g_warpzones; warpzone_initialized, warpzone_warpzones_exist, warpzone_cameras_exist; autocvar_sv_warpzone_allow_selftarget.\n\nENTITY FIELDS USED — pairing/orientation: .target, .targetname, .killtarget, .enemy (partner zone / camera view-target), .aiment (position marker bound to a zone), .warpzone_next. Transform/portal state (set by WarpZone_SetUp in common.qc, read here & serialized): .warpzone_origin, .warpzone_angles, .warpzone_targetorigin, .warpzone_targetangles, .warpzone_forward, .warpzone_targetforward, .warpzone_transform, .warpzone_shift, .warpzone_isboxy, .warpzone_fadestart, .warpzone_fadeend. Think-change detection: .warpzone_save_origin, .warpzone_save_angles, .warpzone_save_eorigin, .warpzone_save_eangles. Per-touched-entity teleport bookkeeping: .warpzone_oldorigin, .warpzone_oldvelocity, .warpzone_oldangles, .warpzone_teleport_time, .warpzone_teleport_finishtime, .warpzone_teleport_zone, .lastteleport_origin, .lastteleporttime. Reconnect: .warpzone_reconnecting. Generic engine/entity fields: .origin, .angles, .velocity, .view_ofs, .mins, .maxs, .scale, .modelscale, .model, .modelindex, .solid, .effects, .flags, .move_movetype/movetype, .tag_entity, .owner, .v_angle, .oldorigin, .fixangle, .teleportable, .gravity, .movedir, .dphitcontentsmask, .SendFlags, .drawonlytoclient, .spawnflags, .nextthink, .use, .think, .touch, CS(player).ping.

**Netcode.** Three CSQC-linked entity classes are serialized from this file via setSendEntity. (1) WarpZone_Send (header ENT_CLIENT_WARPZONE): forces sf=0 then sets bit0 if warpzone_isboxy, bit1 if warpzone_fadestart, bit2 if origin!=0; writes the sf byte; if bit2, writes origin (vector); always writes modelindex (short), mins, maxs (vectors), and bound(1, scale*16, 255) as a byte (scale quantized in 1/16 units); writes the four portal params warpzone_origin, warpzone_angles, warpzone_targetorigin, warpzone_targetangles (vectors) so the client can rebuild the same transform; if bit1, writes warpzone_fadestart, warpzone_fadeend (shorts). (2) WarpZone_Camera_Send (header ENT_CLIENT_WARPZONE_CAMERA): same shape but with bit1/bit2 only (no boxy bit), and instead of four params writes enemy.origin and enemy.angles (the camera's view origin/angles). (3) WarpZone_Teleported_Send (header ENT_CLIENT_WARPZONE_TELEPORTED): writes only this.angles (the warpzone_transform stored into the carrier's .angles) — a one-shot per-teleport message, drawonlytoclient=the teleported player, SendFlags=0xFFFFFF, self-removed after 1s, used so the client rotates its own predicted moves/view to match the warp. All zones/cameras set SendFlags=0xFFFFFF on (re)init to force a full resend. KEEP_ROLL compile option (common.qh) changes VAngles transform handling but is off by default.

**Dependencies.** Engine builtins: setorigin, setsize, setmodel/_setmodel, precache_model, set_movetype/MOVETYPE_*, settouch/setthink, setSendEntity, setcamera_transform (via SetUp), tracebox/traceline, find, random, getsurfacetexture/getsurfacenumtriangles/getsurfacetriangle/getsurfacepoint, cross/normalize/vlen/vectoangles/vectoangles2, MAKE_VECTORS, checkpvs, WriteHeader/WriteByte/WriteShort/WriteVector (MSG_ENTITY). Common warpzone subsystem (lib/warpzone/common.qc, .qh): WarpZone_SetUp, WarpZone_Camera_SetUp, WarpZone_TransformOrigin/Velocity/Angles/VAngles, WarpZone_PlaneDist, WarpZone_TargetPlaneDist, WarpZone_RefSys_Add, WarpZone_Find, WarpZoneLib_ExactTrigger_Touch, WarpZoneLib_MoveOutOfSolid, AnglesTransform_* (via SetUp/transforms). Mod-provided callbacks (declared in server.qh, must be defined by host QC): WarpZone_PostTeleportPlayer_Callback, WarpZone_Projectile_Touch_ImpactFilter_Callback, WarpZone_PostInitialize_Callback. Engine/game integration: SUB_UseTargets_SkipTargets, SUB_Remove, Teleport_Find/Simple_TeleportPlayer (teleporters), bot_aim_reset, IL_EACH/IL_PUSH(g_warpzones, g_projectiles), FOREACH_CLIENT, IS_PLAYER/IS_OBSERVER/IS_REAL_CLIENT/IS_BOT_CLIENT/IS_VEHICLE/IS_NOT_A_CLIENT, PHYS_INPUT_ANGLES/PHYS_INPUT_FRAMETIME, MDL_Null, EF_TELEPORT_BIT/EF_NODEPTHTEST, FL_ONGROUND. Net constants required from host: ENT_CLIENT_WARPZONE, ENT_CLIENT_WARPZONE_CAMERA, ENT_CLIENT_WARPZONE_TELEPORTED.

## lib/warpzone/client

**Role.** This is the CSQC (client-side) half of Xonotic's warpzone (seamless portal) system. It deserializes warpzone-portal entities, warpzone cameras, and teleport notifications off the network into client entities; it reconstructs the per-zone angles/shift transform locally (via the shared common.qc WarpZone_SetUp) so the client can match the server's transform exactly for prediction and rendering; it registers the DarkPlaces engine "camera_transform" callback on each zone so the engine knows how to mirror the view through the portal surface; it fades/culls the warp surface per-frame; and on every CSQC view update it detects whether the local view origin sits inside a warpzone and rewrites VF_ORIGIN/VF_ANGLES (and CL_RotateMoves the input) so the player sees and predicts movement seamlessly through the portal. Actual portal-view rendering and the warp surface draw are done by the DarkPlaces engine using the registered camera_transform plus the cvar hacks this code sets (r_water, r_drawexteriormodel); this QC layer only feeds the engine the entity, transform, and view fixups. Note that ENT_CLIENT_WARPZONE/CAMERA entities are CSQC networked entities (they go through R_AddEntity-equivalent CSQC drawmask handling: drawmask=MASK_NORMAL plus a predraw fade callback), not directly added via addentity in this file.

### `WarpZone_Fade_PreDraw`  _(client)_
`void WarpZone_Fade_PreDraw(entity this)`

Engine predraw callback registered via setpredraw on both warpzones and cameras. Reads the current view origin (getpropertyvec(VF_ORIGIN)). If the zone is not in the PVS (checkpvs) it sets alpha=0 (note comment: only valid because recursive warpzones unsupported). Otherwise, if warpzone_fadestart is set, computes alpha as a bounded linear ramp between fadeend and fadestart based on distance from view origin to the zone box center (origin + 0.5*(mins+maxs)); else alpha=1. Finally translates alpha into drawmask: alpha<=0 -> drawmask=0 (engine skips drawing), else drawmask=MASK_NORMAL (engine draws the warp surface).

- **Called by:** DarkPlaces engine each frame before drawing the entity (registered via setpredraw in both NET_HANDLE handlers).
- **Gotchas:** This is the only place the warp surface is culled/faded; drawmask gating here is what actually suppresses the portal surface render. PVS-based hard cull (alpha=0) is explicitly noted to break if recursive warpzones were ever added. Box-center distance uses unscaled mins/maxs.

### `NET_HANDLE(ENT_CLIENT_WARPZONE, bool isNew)`  _(client)_
`bool NET_HANDLE(ENT_CLIENT_WARPZONE, bool isNew) // entity is 'this'`

CSQC netread/entity handler for a warpzone portal. On first ever warpzone, sets DP render hacks via cvar_settemp: r_water=1 (force reflections) and r_water_resolutionmultiplier=1 (full quality through zones). Sets warpzone_warpzones_exist=1. Lazily creates this.enemy = new(warpzone_from) to hold source-side data. Sets classname="trigger_warpzone". If isNew, IL_PUSH into g_warpzones. Reads sf byte: bit1->warpzone_isboxy; if bit4 reads origin else origin='0 0 0'. Reads modelindex (Short), mins, maxs (Vectors), scale=ReadByte()/16. Reads four transform vectors into enemy.oldorigin, enemy.avelocity, oldorigin, avelocity (these are the wire source-origin/angles and target-origin/angles). If sf&2 reads fadestart/fadeend (Shorts), clamping fadeend>=fadestart+1; else both 0. Calls WarpZone_SetUp(this, enemy.oldorigin, enemy.avelocity, oldorigin, avelocity) to build the transform (and register the engine camera_transform). Links the entity: setorigin, setsize(mins,maxs). Registers setpredraw(WarpZone_Fade_PreDraw). Returns true.

- **Called by:** CSQC networking layer when the server sends/updates an ENT_CLIENT_WARPZONE entity (server WarpZone_Send).
- **Gotchas:** Field names on the client (oldorigin/avelocity/enemy.*) are scratch reuse; the binding to WarpZone_SetUp's (my_org,my_ang,other_org,other_ang) is what matters and must mirror server write order. setmodel is intentionally commented out — the surface is rendered as a brush via modelindex+camera_transform, not a model. IL_PUSH only on isNew so re-sends don't duplicate. settouch is commented out (client doesn't run warpzone touch). The r_water/resolution cvar hacks are only set once (guarded by warpzone_warpzones_exist being false) and via cvar_settemp so they revert.

### `NET_HANDLE(ENT_CLIENT_WARPZONE_CAMERA, bool isNew)`  _(client)_
`bool NET_HANDLE(ENT_CLIENT_WARPZONE_CAMERA, bool isNew) // entity is 'this'`

CSQC handler for a fixed warpzone camera (func_warpzone_camera). Same r_water/r_water_resolutionmultiplier cvar_settemp hack on first camera; sets warpzone_cameras_exist=1; classname="func_warpzone_camera". Reads sf: if bit4 origin else '0 0 0'; modelindex; mins; maxs; scale/16; then TWO vectors (camera origin, camera angles) into oldorigin, avelocity; optional fade (sf&2). Calls WarpZone_Camera_SetUp(this, oldorigin, avelocity) to register a fixed-view camera_transform. Sets drawmask=MASK_NORMAL, links (setorigin/setsize), and registers setpredraw(WarpZone_Fade_PreDraw). Returns true.

- **Called by:** CSQC networking layer for ENT_CLIENT_WARPZONE_CAMERA (server WarpZone_Camera_Send).
- **Gotchas:** Does NOT push into g_warpzones (cameras are one-way fixed security-camera-style views, never used for player teleport / WarpZone_Find). Reads only 2 transform vectors vs the warpzone's 4. drawmask=MASK_NORMAL is set explicitly then also gated by the predraw fade callback. No enemy entity created.

### `NET_HANDLE(ENT_CLIENT_WARPZONE_TELEPORTED, bool isNew)`  _(client)_
`bool NET_HANDLE(ENT_CLIENT_WARPZONE_TELEPORTED, bool isNew) // entity is 'this'`

Handles the server's notification that the local player was teleported through a warpzone, so the client view/input can be rotated to match instantly without waiting for prediction to discover the zone. Sets classname="warpzone_teleported". Reads one Vector v (the angles-transform). Sets return=true early. If not isNew, returns (only act on first receipt). Stores v into this.warpzone_transform. Immediately rewrites the client view angles: setproperty(VF_CL_VIEWANGLES, WarpZone_TransformVAngles(this, getpropertyvec(VF_CL_VIEWANGLES))). If the DP_CSQC_ROTATEMOVES extension is present, calls CL_RotateMoves(v) to rotate all pending predicted input moves by the same transform (so prediction stays consistent across the teleport).

- **Called by:** CSQC networking layer when server sends ENT_CLIENT_WARPZONE_TELEPORTED (server WarpZone_Teleported_Send writes this.angles).
- **Gotchas:** Uses 'this' (a transient entity) purely as a carrier for warpzone_transform so WarpZone_TransformVAngles can be reused. isNew guard prevents re-applying the rotation on a resend. CL_RotateMoves is the key to seamless predicted input after a hard server teleport; without the extension only the current view angles are rotated.

### `WarpZone_View_Outside`  _(client)_
`void WarpZone_View_Outside()`

Restores the saved r_drawexteriormodel cvar when the view leaves the inside of a warpzone. If warpzone_fixingview is not set, no-op. Otherwise clears warpzone_fixingview and restores r_drawexteriormodel to the previously saved value (warpzone_fixingview_drawexteriormodel).

- **Called by:** WarpZone_FixView (when view origin is not inside a zone), WarpZone_View_Inside (when chase_active), WarpZone_Shutdown.
- **Gotchas:** State machine paired with WarpZone_View_Inside via the warpzone_fixingview flag — restoring r_drawexteriormodel must be balanced, hence the guard. Saving/restoring rather than forcing avoids clobbering a user's setting.

### `WarpZone_View_Inside`  _(client)_
`void WarpZone_View_Inside()`

Marks that the view is inside a warpzone and disables the exterior (third-person) player model render so the local player's own body doesn't appear in front of/through the portal. If autocvar_chase_active (third-person chase cam), instead calls WarpZone_View_Outside and returns (don't suppress the model when chasing). If already fixing the view, no-op. Otherwise sets warpzone_fixingview=1, saves current r_drawexteriormodel into warpzone_fixingview_drawexteriormodel, and forces r_drawexteriormodel=0.

- **Called by:** WarpZone_FixView when the view origin is found inside a warpzone.
- **Gotchas:** In chase cam the exterior model must remain, so it deliberately reverts. Idempotent via warpzone_fixingview flag so it only saves the original cvar once.

### `WarpZone_FixNearClip`  _(client)_
`vector WarpZone_FixNearClip(vector o, vector c0, vector c1, vector c2, vector c3)`

Prevents the near clip plane from poking through a warpzone surface (which would reveal the unwarped geometry behind it). Builds an AABB enclosing the view origin o and the four near-clip-plane corners c0..c3. Finds a warpzone overlapping that box (WarpZone_Find). If found and the origin is on the correct side (WarpZone_PlaneDist(e,o) >= 0), computes the minimum signed plane distance pd over the four corners; if pd<0 (a corner is behind the zone plane) returns e.warpzone_forward * -pd, i.e. a push vector along the zone normal to nudge the camera forward enough that the near plane no longer crosses the portal. Otherwise returns '0 0 0'.

- **Called by:** WarpZone_FixView.
- **Gotchas:** If origin is behind the plane it returns zero with a comment that this means a different zone shares the same AABB (don't act). The returned offset is added to the (already transformed) view origin by the caller. Relies on warpzone_forward being the inward normal set by WarpZone_SetUp.

### `WarpZone_FixPMove`  _(client)_
`void WarpZone_FixPMove()`

Intended to transform the client prediction move (pmove_org / input_angles) when the predicted player position lands inside a warpzone: finds the zone via WarpZone_Find(pmove_org,pmove_org) and applies WarpZone_TransformOrigin to pmove_org and WarpZone_TransformVAngles to input_angles.

- **Called by:** Declared in client.qh; the call site in client/view.qc is commented out (//WarpZone_FixPMove();). Currently effectively unused — predicted warping is handled by the engine camera_transform + WarpZone_FixView + the TELEPORTED CL_RotateMoves path instead.
- **Gotchas:** Dead/disabled in the current build; documents the alternative prediction approach. Operates on engine pmove globals.

### `WarpZone_FixView`  _(client)_
`void WarpZone_FixView()`

Per-frame view fixup, the core client integration point. Saves current VF_ORIGIN/VF_ANGLES into warpzone_save_view_origin/angles (exported for other client code). Calls WarpZone_Find(org,org): if the view origin is inside a warpzone, transforms org via WarpZone_TransformOrigin and ang via WarpZone_TransformVAngles (moving the camera to the far side so the seamless view is from the right place) and calls WarpZone_View_Inside; else WarpZone_View_Outside. Unless KEEP_ROLL is defined, handles death-roll: when alive (HEALTH>0 or special spectator/observer sentinel values -666/-2342) it decays any roll toward 0 by factor f=max(0,1-frametime*cl_rollkillspeed); when dead it rolls the view in over time (v_deathtilt) using a static rollkill accumulator; it scales VF_CL_VIEWANGLES_Z and ang.z by f. Writes the (possibly transformed) org/ang back via setproperty(VF_ORIGIN)/setproperty(VF_ANGLES). Then computes the near-clip correction: unprojects the four screen corners at 1.125*r_nearclip depth (using vid_conwidth/conheight) via cs_unproject, calls WarpZone_FixNearClip, and if it returns nonzero adds the offset to VF_ORIGIN.

- **Called by:** client/view.qc CSQC_UpdateView (after spectator/event-chase/lock camera handling, before View_Ortho and viewmodel draw).
- **Gotchas:** Order matters: runs after chase/lock cameras so it transforms the final view origin. Reuses warpzone_transform machinery by passing the found zone entity e. The roll handling is interleaved with the warpzone transform and the special HEALTH sentinels must be kept in sync with player state codes. Near-clip fix uses 1.125x nearclip as a safety margin and reads live vid_conwidth/conheight cvars each frame. Saved view origin/angles are consumed elsewhere in client rendering (e.g. for placing the viewmodel/relative effects).

### `WarpZone_Shutdown`  _(client)_
`void WarpZone_Shutdown()`

Cleanup on client disconnect/level change: calls WarpZone_View_Outside to restore the r_drawexteriormodel cvar so the third-person model setting isn't left disabled after leaving a map with warpzones.

- **Called by:** client/main.qc (Shutdown path).
- **Gotchas:** Only restores the exterior-model cvar; the r_water/resolution hacks were set via cvar_settemp so they self-revert and aren't touched here.

**Entities / fields.** Classnames set on client: "trigger_warpzone" (ENT_CLIENT_WARPZONE), "func_warpzone_camera" (ENT_CLIENT_WARPZONE_CAMERA), "warpzone_teleported" (ENT_CLIENT_WARPZONE_TELEPORTED). g_warpzones: IntrusiveList of all live warpzone entities (declared/STATIC_INIT in common.qh); only warpzones (not cameras) are IL_PUSHed. this.enemy: per-warpzone auxiliary entity (classname "warpzone_from") holding the source-side oldorigin/avelocity received from the net. Networked/used entity fields populated by netread: warpzone_isboxy, origin, modelindex, mins, maxs, scale, oldorigin, avelocity, enemy.oldorigin, enemy.avelocity, warpzone_fadestart, warpzone_fadeend, warpzone_transform (teleport msg). Derived transform fields set by WarpZone_SetUp (common.qc): warpzone_transform, warpzone_shift, warpzone_origin, warpzone_angles, warpzone_targetorigin, warpzone_targetangles, warpzone_forward, warpzone_targetforward. Globals: warpzone_warpzones_exist, warpzone_cameras_exist (common.qh), warpzone_fixingview, warpzone_fixingview_drawexteriormodel (local to client.qc), warpzone_save_view_origin, warpzone_save_view_angles (exported in client.qh).

**Netcode.** CSQC entity serialization (CSQC networked entities via NET_HANDLE / ReadByte/ReadShort/ReadVector). Server senders are WarpZone_Send and WarpZone_Camera_Send in server.qc. WARPZONE entity wire format (must match exactly): WriteByte sf (bit1=isboxy, bit2=has-fade, bit4=origin!=0); if sf&4 WriteVector origin; WriteShort modelindex; WriteVector mins; WriteVector maxs; WriteByte scale*16 (bounded 1..255, client divides by 16); then the transform group WriteVector warpzone_origin, warpzone_angles, warpzone_targetorigin, warpzone_targetangles; if sf&2 WriteShort fadestart, WriteShort fadeend. CRITICAL ORDERING MISMATCH NOTE: server writes warpzone_origin/angles/targetorigin/targetangles, but the client netread (NET_HANDLE ENT_CLIENT_WARPZONE) reads them into this.enemy.oldorigin, this.enemy.avelocity, this.oldorigin, this.avelocity respectively, then calls WarpZone_SetUp(this, enemy.oldorigin, enemy.avelocity, oldorigin, avelocity) — i.e. (my_org=warpzone_origin, my_ang=warpzone_angles, other_org=warpzone_targetorigin, other_ang=warpzone_targetangles). The field names on the client are repurposed scratch storage; the wire VALUE order is the contract. CAMERA wire format: WriteByte sf (bit2=fade, bit4=origin); optional origin; modelindex; mins; maxs; scale*16; WriteVector enemy.origin, enemy.angles (read client-side into this.oldorigin, this.avelocity, fed to WarpZone_Camera_SetUp as my_org/my_ang); optional fadestart/fadeend. TELEPORTED message (WarpZone_Teleported_Send): WriteVector this.angles only (the transform); client reads one vector into this.warpzone_transform. The client needs from the server: the zone bounding box (mins/maxs/origin/scale) for cull+fade+nearclip, modelindex for the surface, and the four transform vectors (source origin/angles + target origin/angles) to rebuild the exact transform; for cameras the fixed camera origin/angles; for teleports the post-warp angles-transform vector.

**Dependencies.** DarkPlaces engine builtins/extensions: setcamera_transform (registers the per-entity portal mirror callback — invoked indirectly via WarpZone_SetUp in common.qc; this is how the renderer is told to draw the portal view, replacing manual R_AddEntity), setpredraw (registers WarpZone_Fade_PreDraw), setproperty/getproperty/getpropertyvec on VF_ORIGIN/VF_ANGLES/VF_CL_VIEWANGLES (view setup), cs_unproject (screen->world for nearclip corners), checkpvs, checkextension("DP_CSQC_ROTATEMOVES"), CL_RotateMoves = #638 (DP builtin, rotates pending predicted input moves), cvar/cvar_set/cvar_settemp (engine render hacks: r_water, r_water_resolutionmultiplier, r_drawexteriormodel, r_nearclip, vid_conwidth/conheight). Shared warpzone code: common.qc/common.qh — WarpZone_SetUp, WarpZone_Camera_SetUp, WarpZone_Find, WarpZone_TransformOrigin, WarpZone_TransformVAngles, WarpZone_PlaneDist, WarpZoneLib_BoxTouchesBrush; anglestransform.qh for the angle math. csqcmodel/cl_model.qh. STAT(HEALTH) for death-roll handling. autocvar_chase_active, autocvar_cl_rollkillspeed. Frame driver: client/view.qc CSQC_UpdateView calls WarpZone_FixView each frame; client/main.qc calls WarpZone_Shutdown on disconnect/restart.

## integration: movement/teleporters/physics

**Role.** This area is where the QC-side movement simulation, the generic map-object teleporter path, and the warpzone library meet. The custom movetype stepper (movetypes.qc) is the engine-independent reimplementation of DP's SV_Physics: as it slides an entity each frame via _Movetype_PushEntity, it fires touch functions on any SOLID_TRIGGER it overlaps. trigger_warpzone brushes carry WarpZone_Touch as their touch func, so a warpzone teleport is detected and applied mid-move through the ordinary trigger-touch machinery rather than by special-case code in the physics loop. teleporters.qc provides the legacy/instant teleporter path (trigger_teleport -> Teleport_Touch -> Simple_TeleportPlayer -> TeleportPlayer); warpzones reuse only the shared building blocks (EXACTTRIGGER_TOUCH, the post-teleport callback, Teleport_Find, the g_teleporters list) but have their own seamless transform-based teleport in the warpzone lib. player.qc is the per-frame physics entry point (SV_PlayerPhysics / CSQC_ClientMovement_PlayerMove_Frame) that drives the move; on CSQC it also re-validates an entity's last_pushed jumppad against warpzone-aware exact-trigger geometry for prediction. The defining difference vs a plain teleporter is that a warpzone applies a full rigid-body transform (origin shift + rotation) to origin, velocity AND view angles so the move appears continuous, whereas a teleporter snaps to a fixed destination and rebuilds velocity from the destination's facing.

### `_Movetype_PushEntity`  _(common)_
`bool _Movetype_PushEntity(entity this, vector push, bool dolink)`

Core mid-move teleport detection point. Traces the entity from current origin along push (_Movetype_PushEntityTrace), retries through MOVE_WORLDONLY if startsolid, commits this.origin = trace_endpos, calls _Movetype_LinkEdict to relink and fire touch triggers, and if it stopped short on a solid/trigger calls _Movetype_Impact(this, trace_ent). Returns (this.origin == last_origin): FALSE when a touch func teleported the entity. WarpZones rely on this: WarpZone_Touch calls setorigin during the move, so origin != last_origin and the caller learns the move was aborted/redirected.

- **Called by:** _Movetype_Physics_Walk/Toss/ClientFrame
- **Gotchas:** The return-value contract IS the mechanism by which a warpzone teleport mid-move is detected. trace_startsolid worldonly retry is a workaround for QC lacking a worldstartsolid trace param. If warpzones were absent this still works as a generic move+touch; the FALSE-on-teleport path just never gets exercised by a warpzone.

### `_Movetype_LinkEdict_TouchAreaGrid`  _(common)_
`void _Movetype_LinkEdict_TouchAreaGrid(entity this)`

Relink helper that walks every SOLID_TRIGGER whose bbox overlaps the (1-unit-expanded) entity box and invokes its touch func with faked trace globals. Dispatches WarpZone_Touch / Teleport_Touch when an entity is simply standing/relinked inside a warpzone/teleporter (vs running into it via PushEntity). Saves/restores all trace globals around dispatch.

- **Called by:** _Movetype_LinkEdict (when touch_triggers)
- **Gotchas:** The 1-unit bbox expansion is intentionally coarse and the code comment explicitly references WarpZoneLib_ExactTrigger_Touch, because the precise brush test is re-done inside each touch func. If warpzones were absent this still drives all triggers; only the warpzone touch dispatch and the cross-reference comment are moot.

### `_Movetype_Impact`  _(common)_
`void _Movetype_Impact(entity this, entity toucher)`

SV_Impact equivalent. Saves all trace globals, calls gettouch(this)(this,toucher) and the reciprocal gettouch(toucher)(toucher,this) with mirrored plane, then restores globals. Second route by which WarpZone_Touch fires mid-move: when PushEntity's trace stops against the warpzone brush.

- **Called by:** _Movetype_PushEntity (movetypes.qc:693)
- **Gotchas:** The save/restore is essential because WarpZone_Touch runs traceboxes (in WarpZone_Teleport) that clobber the globals; the physics loop must see consistent trace state afterward. Not warpzone-specific (jumppads also trace), so nothing breaks if warpzones are absent.

### `WarpZone_Touch`  _(common (server.qc, lib))_
`void WarpZone_Touch(entity this, entity toucher)`

trigger_warpzone touch func. Bails if toucher is itself a warpzone, already teleported this frame (time<=warpzone_teleport_finishtime), movetype NONE/FOLLOW or tag-attached, or eye point still on near side (WarpZone_PlaneDist>=0 means do not teleport yet). Runs EXACTTRIGGER_TOUCH. Computes rewind factor f (-1 for clients, multi-frame ~24qu for projectiles), calls WarpZone_Teleport(this,toucher,f,0), and on success fires both zones' targets. Counterpart to teleporters' Teleport_Touch.

- **Called by:** movetype touch dispatch (_Movetype_Impact / TouchAreaGrid)
- **Gotchas:** Clients always rewind exactly one frame to limit view jolt. finishtime gate prevents same-frame re-teleport. If warpzones absent this function/trigger_warpzone simply do not exist; the movement loop is unaffected since triggers dispatch generically.

### `WarpZone_Teleport`  _(common)_
`float WarpZone_Teleport(entity wz, entity player, float f0, float f1)`

Computes the rigid transform of the crossing: o1=TransformOrigin, v1=TransformVelocity, a1=TransformVAngles (clients, transforms input view angles) or TransformAngles (non-clients). With f0/f1 set, replays the last move behind the exit zone via two traceboxes (worldonly back then normal forward through the zone) to land on the far side without double-touch, nudging out along v1 if still behind the target plane. Pushes out of solid (tracebox + WarpZoneLib_MoveOutOfSolid) or aborts (returns 0, restoring origin). On success: WarpZone_RefSys_Add stacks the reference frame, calls WarpZone_TeleportPlayer with o1/a1/v1, snapshots projectile data, sets teleport_time/finishtime/zone, pads finishtime by a frame, and (no-fixangle path) spawns the warpzone_teleported netentity / rotates CSQC view angles.

- **Called by:** WarpZone_Touch; the f0/f1 retry variant; warpzone post-physics sweep (server.qc:765)
- **Gotchas:** THIS is where velocity AND view angles are preserved by ROTATION (vs teleporters rebuilding velocity from exit facing). finishtime padding (dt math) stops immediate bounce-back. Aborts rather than placing a player in solid. If warpzones absent none of this transform math exists.

### `WarpZone_TeleportPlayer`  _(common)_
`void WarpZone_TeleportPlayer(entity teleporter, entity player, vector to, vector to_angles, vector to_velocity)`

Applies the crossing state, analogous to teleporters.qc's TeleportPlayer but minimal and transform-aware. Records lastteleport_origin/time, setorigin(player,to) (also aborts the in-progress move when called from touch), angles=to_angles, oldorigin (DP unsticking), fixangle=true + bot v_angle reset, velocity=to_velocity, toggles EF_TELEPORT_BIT, clears FL_ONGROUND for players, then calls WarpZone_PostTeleportPlayer_Callback.

- **Called by:** WarpZone_Teleport (server.qc:137)
- **Gotchas:** fixangle=true here is later OVERRIDDEN to false by WarpZone_Teleport for players on the smooth-transform path. The setorigin-aborts-move behavior is exactly what _Movetype_PushEntity detects via origin==last_origin. Unused if warpzones absent.

### `WarpZone_PostTeleportPlayer_Callback`  _(common (SVQC full body; CSQC trims to projectile disown))_
`void WarpZone_PostTeleportPlayer_Callback(entity pl)  // teleporters.qc:303 / teleporters.qh:69`

Shared post-teleport fixup deliberately living in teleporters.qc so both the warpzone lib and the generic teleporter path converge on identical cleanup. Re-runs makevectors+Reset_ArcBeam (re-aim Arc beam), UpdateCSQCProjectileAfterTeleport, UpdateItemAfterTeleport, anticheat_fixangle; disowns projectiles whose owner==realowner (logging a 'got through a warpzone' message naming warpzone if a non-projectile slipped through); resets oldvelocity=velocity so the sudden velocity change is not read as impact/fall damage.

- **Called by:** WarpZone_TeleportPlayer (server.qc:68)
- **Gotchas:** Single most important warpzone touch point in teleporters.qc/.qh. The oldvelocity reset is what prevents bogus impact damage on a high-speed crossing. If warpzones absent, this callback is referenced only here; its Arc-beam/projectile/anticheat fixups already duplicate work TeleportPlayer does inline (lines 124-127, 155-176), so it could be folded in.

### `TeleportPlayer`  _(common)_
`void TeleportPlayer(entity teleporter, entity player, vector to, vector to_angles, vector to_velocity, vector telefragmin, vector telefragmax, float tflags)`

Generic/legacy teleporter applier and the contrast case to warpzones. Plays throttled teleport sound/particles, then snaps: setorigin(to), angles=to_angles, fixangle=true (NOT overridden, unlike warpzones), velocity=to_velocity, toggles EF_TELEPORT_BIT, re-aims Arc beam, updates CSQC projectile/items. SVQC also handles telefrag (tdeath), clears ONGROUND, resets oldvelocity, records pusher/istypefrag/lastteleport. CSQC sets IFLAG_TELEPORTED/V_ANGLE/ANGLES + csqcmodel_teleported and forces VF_ANGLES/VF_CL_VIEWANGLES for the local player.

- **Called by:** Simple_TeleportPlayer (teleporters.qc:241)
- **Gotchas:** Key difference from warpzone: velocity = whatever to_velocity the caller computed (Simple_TeleportPlayer builds v_forward*speed from the destination mangle, i.e. direction REBUILT from exit facing, not rotated), and angles hard-snap via fixangle. Predates warpzones; this is what remains if warpzones were removed.

### `Simple_TeleportPlayer`  _(common)_
`entity Simple_TeleportPlayer(entity teleporter, entity player)`

Resolves a teleporter to its destination (fixed .enemy, or random RandomSelection avoiding telefrag if TELEPORT_TELEFRAG_AVOID), clamps speed to dest .speed and STAT TELEPORT_MINSPEED/MAXSPEED unless KEEP_SPEED spawnflags set, computes locout above the destination, and calls TeleportPlayer with to_velocity = v_forward * vlen(player.velocity) (speed preserved, direction set to exit facing).

- **Called by:** Teleport_Touch, target_teleport_use; warpzone post-physics sweep
- **Gotchas:** This speed-preserve/direction-reset policy is the defining velocity difference from warpzones (which rotate the full vector). Independent of warpzones BUT also invoked from the warpzone post-physics sweep (server.qc:773) for teleportable entities found via Teleport_Find, for the stuck-in-teleporter-after-a-move case.

### `Teleport_Find`  _(common)_
`entity Teleport_Find(vector mi, vector ma)`

Iterates g_teleporters and returns the first whose brush precisely overlaps the box via WarpZoneLib_BoxTouchesBrush.

- **Called by:** warpzone post-physics sweep (server.qc:771)
- **Gotchas:** Itself a consumer of warpzone-lib brush math even though it serves plain teleporters (warpzones and exact triggers share that geometry code). Used by the warpzone post-physics safety sweep to catch entities ending a frame inside a teleporter without a touch firing. If warpzones absent the function works but WarpZoneLib_BoxTouchesBrush would need another home.

### `Teleport_Touch`  _(common)_
`void Teleport_Touch(entity this, entity toucher)  // trigger/teleport.qc:49`

trigger_teleport's touch func, the generic counterpart to WarpZone_Touch. Checks Teleport_Active (active flag, teleportable, not dead, team/observer gating), runs EXACTTRIGGER_TOUCH (precise overlap), removes grappling hooks, calls Simple_TeleportPlayer, fires SUB_UseTargets.

- **Called by:** movetype touch dispatch (mid-move) and TouchAreaGrid
- **Gotchas:** Shows the relationship: teleporters and warpzones both hang a touch func on a SOLID_TRIGGER and both gate via EXACTTRIGGER_TOUCH, but diverge in effect (instant snap vs rotated continuation). Only warpzone dependency is the EXACTTRIGGER_TOUCH macro/geometry; otherwise self-contained.

### `SV_PlayerPhysics / CSQC_ClientMovement_PlayerMove_Frame`  _(SVQC / CSQC (single ifdef'd source; warpzone branch is CSQC))_
`void SV_PlayerPhysics(entity this) / void CSQC_ClientMovement_PlayerMove_Frame(entity this)  // player.qc:858`

Per-frame physics entry that drives sys_phys_update (and the movetype stepper that fires warpzone touches). The warpzone-relevant code is CSQC-only: if(this.last_pushed && !WarpZoneLib_ExactTrigger_Touch(this.last_pushed, this, false)) this.last_pushed = NULL; uses the warpzone lib's exact trigger test to decide whether the player still stands on the jumppad it last touched, clearing the stale reference for correct client-side prediction of jumppad re-triggering.

- **Called by:** engine (before PlayerPreThink on SVQC; client movement frame on CSQC)
- **Gotchas:** The only direct warpzone reference in player.qc and it is purely prediction-correctness. If the warpzone lib were absent, last_pushed validation would fall back to a coarse bbox test, risking mispredicted jumppad re-fires near warped geometry.

### `WarpZoneLib_ExactTrigger_Touch`  _(common)_
`bool WarpZoneLib_ExactTrigger_Touch(entity this, entity toucher, bool touchfunc)  // common.qc:789; macro EXACTTRIGGER_TOUCH common.qh:118`

Precise brush-vs-box overlap test backing every warpzone/teleporter touch gate. The EXACTTRIGGER_TOUCH(e,t) macro early-returns from a touch func on miss (used by WarpZone_Touch and Teleport_Touch), compensating for the deliberately coarse 1-unit broadphase in TouchAreaGrid. The bool form (touchfunc=false) is reused by player.qc last_pushed validation and the post-physics sweep.

- **Called by:** WarpZone_Touch, Teleport_Touch (via macro), player.qc:869 last_pushed check, warpzone post-physics sweep
- **Gotchas:** Header WARNS it kills the trace globals - the reason _Movetype_Impact and TouchAreaGrid save/restore them around touch dispatch. Connective tissue between loose physics broadphase and exact trigger geometry. If warpzones absent this exactness layer vanishes and triggers fire on coarse bbox overlap, changing jumppad/teleporter feel and breaking the trace-global-clobber contract.

**Entities / fields.** trigger_warpzone (touch=WarpZone_Touch); trigger_teleport / target_teleporter / info_teleport_destination (Teleport_Touch / Simple_TeleportPlayer); g_teleporters IntrusiveList (iterated by Teleport_Find); .teleportable (TELEPORT_NORMAL/TELEPORT_SIMPLE) gates who can be teleported; .warpzone_oldorigin/.warpzone_oldvelocity/.warpzone_oldangles (StoreProjectileData snapshot); .warpzone_teleport_time/.warpzone_teleport_finishtime/.warpzone_teleport_zone (per-entity bookkeeping & re-teleport guard); .lastteleport_origin/.lastteleporttime; warpzone_teleported entity (CSQC-targeted transform packet); .last_pushed (jumppad ref, CSQC re-validated via warpzone exact-trigger touch); EF_TELEPORT_BIT effect; FL_ONGROUND cleared on teleport.

**Netcode.** Warpzones avoid hard fixangle for smoothness. On SVQC, WarpZone_Teleport (when WARPZONE_USE_FIXANGLE is undefined) sets player.fixangle=false and instead spawns a transient warpzone_teleported entity (WarpZone_Teleported_Send -> ENT_CLIENT_WARPZONE_TELEPORTED, writes warpzone_transform angles, drawonlytoclient=player, removed after 1s). On CSQC the NET_HANDLE for ENT_CLIENT_WARPZONE_TELEPORTED calls CL_RotateMoves(v) so the client's input/prediction history rotates into the new reference frame; WarpZone_Teleport on CSQC also rotates VF_CL_VIEWANGLES directly. By contrast the legacy teleporter (TeleportPlayer) uses fixangle=true on SVQC and on CSQC sets IFLAG_TELEPORTED/IFLAG_V_ANGLE/IFLAG_ANGLES + csqcmodel_teleported and force-sets VF_ANGLES/VF_CL_VIEWANGLES for the local player. trigger_teleport is network-linked (ENT_CLIENT_TRIGGER_TELEPORT) so CSQC can predict instant teleporters; trigger_warpzone is linked via ENT_CLIENT_WARPZONE carrying the four origin/angle vectors needed to reconstruct the transform clientside. warpzone_teleport_finishtime gates re-teleporting within the same frame (padded by PHYS_INPUT_FRAMETIME to stop immediate bounce-back), keeping client/server teleport timing consistent. Main prediction concerns: angle rotation must be replayed on the client (CL_RotateMoves) or the local view snaps; last_pushed jumppad validation on CSQC must use the warpzone exact-trigger test to avoid mispredicted re-fires near warped geometry.

**Dependencies.** DarkPlaces builtins: tracebox/traceline, setorigin (calls SV_LinkEdict), gettouch/settouch, makevectors, findradius, CL_RotateMoves (#638, DP_CSQC_ROTATEMOVES). Warpzone lib: WarpZone_TransformOrigin/Velocity/Angles/VAngles (AnglesTransform_Apply over warpzone_transform/warpzone_shift), WarpZone_RefSys_Add/Copy, WarpZoneLib_ExactTrigger_Touch (precise brush overlap backing EXACTTRIGGER_TOUCH), WarpZoneLib_BoxTouchesBrush, WarpZoneLib_MoveOutOfSolid, WarpZone_PlaneDist/TargetPlaneDist. teleporters.qc also depends on subs.qh (SUB_UseTargets), Reset_ArcBeam, UpdateCSQCProjectileAfterTeleport, UpdateItemAfterTeleport, RandomSelection, anticheat_fixangle, Damage (telefrag). The physics stepper depends on the engine trace globals which it manually saves/restores around every touch dispatch.

## integration: weapons/projectiles/tracing

**Role.** This area is the bridge between Xonotic's weapon-firing code and the warpzone library (qcsrc/lib/warpzone). Warpzones are non-Euclidean teleporter brushes: a trace or a moving entity that crosses one is geometrically continued on the far side via an affine transform (rotation+shift). Weapons interact with warpzones in three distinct ways. (1) Hitscan/aim traces use the warpzone-aware trace wrappers (WarpZone_TraceLine / WarpZone_TraceBox_ThroughZone / WarpZone_traceline_antilag) which walk the trace segment-by-segment across zone boundaries, accumulating a composite transform in the global WarpZone_trace_transform; callers then use WarpZone_UnTransformOrigin / WarpZone_TransformVelocity to map the far-side endpoint and the damage force/distance back into the shooter's coordinate frame. (2) Live projectiles move through the engine and are teleported by the warpzone touch handler; weapon code only participates via the PROJECTILE_TOUCH macro -> WarpZone_Projectile_Touch -> WarpZone_Projectile_Touch_ImpactFilter_Callback gate (deciding whether a touch is a real impact vs a warpzone pass-through), and via UpdateCSQCProjectileAfterTeleport which re-syncs the CSQC clientside copy after a warp. (3) Clientside CSQC projectiles re-simulate movement locally and must be reset on teleport to avoid spurious SUB_Stop touches. Key cross-zone semantics: trace endpoints come back in the FINAL zone's frame and must be un-transformed; damage force and railgun distance are computed in the transformed frame then mapped back; falloff distance is measured between un-transformed hit.origin and shooter origin so it stays correct across zones.

### `W_SetupShot_Dir_ProjectileSize_Range`  _(server)_
`void W_SetupShot_Dir_ProjectileSize_Range(entity ent, .entity weaponentity, vector s_forward, vector mi, vector ma, float antilag, float recoil, Sound snd, float chan, float maxdamage, float range, int deathtype)  [server/weapons/tracing.qc:24]`

Computes w_shotorg (muzzle origin), w_shotdir (firing direction), w_shotend (aim/trueaim endpoint) for every weapon. WARPZONE TOUCH POINT: it casts the trueaim trace through warpzones. With antilag it calls WarpZone_traceline_antilag(NULL, eye, eye + s_forward*range, MOVE_NORMAL, ent, lag); without antilag it calls WarpZone_TraceLine(eye, eye + s_forward*range, MOVE_NOMONSTERS, ent). These trace wrappers walk the segment across any warpzones and leave the composite transform in the global WarpZone_trace_transform and the raw far-side hit in trace_endpos. The key line is w_shotend = WarpZone_UnTransformOrigin(WarpZone_trace_transform, trace_endpos): trace_endpos is in the FINAL zone's coordinate frame, so it is un-transformed back into the SHOOTER's frame to give a meaningful aim point. v_forward/v_right/v_up are saved before and restored after the WarpZone trace because the trace builtins clobber them. The subsequent tracebox calls that nudge w_shotorg sideways/forward use plain tracebox/tracebox_antilag (NOT warpzone-aware) because that is a short local move at the muzzle.

- **Called by:** All weapon fire functions via the W_SetupShot* macros (tracing.qh).
- **Gotchas:** trace_endpos is returned in the last-zone frame; forgetting WarpZone_UnTransformOrigin would put the aim point on the wrong side of the warp. v_forward/right/up MUST be saved/restored around the warpzone trace. The trueaim-minrange clamp and the dual-wield branch (w_shotdir = s_forward) bypass the un-transformed endpoint. With WEP_FLAG_PENETRATEWALLS the dphitcontentsmask is changed so the trueaim trace passes through walls (interacts with the contentshack logic inside WarpZone_TraceBox_ThroughZone). NULL is passed as source to WarpZone_traceline_antilag specifically so the antilag wrapper does NOT touch dphitcontentsmask.

### `FireRailgunBullet`  _(server)_
`void FireRailgunBullet(entity this, .entity weaponentity, vector start, vector end, float bdamage, bool headshot_notify, float bforce, float mindist, float maxdist, float halflifedist, float forcehalflifedist, int deathtype)  [server/weapons/tracing.qc:231]`

Instant-hit beam (Vortex/railgun) that pierces multiple targets. Loops calling WarpZone_traceline_antilag(this, start, end, ...) each iteration. WARPZONE PASS-THROUGH HANDLING: after the first trace, if WarpZone_trace_firstzone is set AND o (forent) is still this, it sets o = NULL and continues - this restarts the trace allowing the beam to hit the shooter on the far side of a warpzone (self-damage only allowed after a warp). For each hit it records per-target damage data computed in the TRANSFORMED frame: it.railgundistance = vlen(WarpZone_UnTransformOrigin(WarpZone_trace_transform, trace_endpos) - start) maps the far-side hit point back to the shooter frame so the falloff distance is the true geometric distance; it.railgunforce = WarpZone_TransformVelocity(WarpZone_trace_transform, force) rotates the knock-back force into the victim's frame so the push direction is correct on the far side. Damage_DamageInfo for BSP hits also uses WarpZone_TransformVelocity(...) for the effect force vector. Targets are made SOLID_NOT to pierce, restored after, then damaged in a second pass.

- **Called by:** Vortex/railgun-style weapon attack code (e.g. WEP_VORTEX), Rocketminsta etc.
- **Gotchas:** Distance must be un-transformed (UnTransformOrigin) but force must be forward-transformed (TransformVelocity) - opposite directions because distance is a shooter-frame measurement of an endpoint while force is a vector applied in the victim's frame. The o=NULL/continue trick to enable post-warp self-hit relies on WarpZone_trace_firstzone being non-NULL. trace_endpos/trace_ent/trace_dphitq3surfaceflags are saved (endpoint/endent/endq3surfaceflags) and restored at the end so callers see the final wall hit, not the last pierced player.

### `fireBullet_falloff`  _(server)_
`void fireBullet_falloff(entity this, .entity weaponentity, vector start, vector dir, float spread, float max_solid_penetration, float damage, float falloff_halflife, float falloff_mindist, float falloff_maxdist, float headshot_multiplier, float force, float falloff_forcehalflife, float dtype, entity tracer_effect, bool do_antilag)  [server/weapons/tracing.qc:363]`

Core bullet/hitscan tracer (MG, Rifle, Shotgun, etc.) with wall penetration. WARPZONE-CENTRAL: the main loop calls WarpZone_TraceBox_ThroughZone(start, '0 0 0', '0 0 0', end, false, WarpZone_trace_forent, NULL, fireBullet_trace_callback). After each ThroughZone call it RE-TRANSFORMS its own working vectors into the new frame: dir = WarpZone_TransformVelocity(WarpZone_trace_transform, dir); end = WarpZone_TransformOrigin(WarpZone_trace_transform, end); start = trace_endpos. This is the manual continuation pattern - because the bullet can pierce solids (it keeps tracing past hits with traceline_inverted), the function drives the cross-zone walk itself one segment at a time and keeps dir/end in the current zone's frame so subsequent penetration traces and effect trails are geometrically correct. Damage is applied in the current (transformed) frame at start. Falloff distance uses dist = vlen(WarpZone_UnTransformOrigin(WarpZone_trace_transform, hit.origin) - this.origin) so it measures true shooter->target distance even across a warp. Self-damage guard: hit != WarpZone_trace_forent allows hitting self only after a warp changed forent.

- **Called by:** fireBullet_antilag -> fireBullet (tracing.qc:539,544); all bullet weapons.
- **Gotchas:** Order matters: TransformVelocity(dir) and TransformOrigin(end) must both use the SAME WarpZone_trace_transform from the just-completed ThroughZone call, BEFORE start is overwritten with trace_endpos. WarpZone_trace_forent is the global the lib uses as the ignore entity and the callback (fireBullet_trace_callback) sets it to NULL - this is how a self-hit becomes possible after a warp. Falloff uses entity origins, not trace endpoints, deliberately (comment: start/end/trace_endpos unreliable for falloff). traceline_inverted for penetration uses WarpZone_trace_forent (the possibly-NULLed ignore ent) and is plain (non-warpzone) - penetration through a solid does not itself re-warp. is_weapclip / world-bounds checks break the loop and thus stop cross-zone walking.

### `fireBullet_trace_callback`  _(server)_
`void fireBullet_trace_callback(vector start, vector hit, vector end)  [server/weapons/tracing.qc:355]`

Per-segment callback passed to WarpZone_TraceBox_ThroughZone. The warpzone lib invokes it for every elementary trace segment (once per zone crossing). It draws the tracer trail particle for the segment (start->hit) and, crucially, resets WarpZone_trace_forent = NULL and fireBullet_last_hit = NULL. Setting forent to NULL on each segment is what permits the bullet to hit the shooter (and re-hit entities) once it has passed through a warpzone into a new segment.

- **Called by:** WarpZone_TraceBox_ThroughZone (invoked internally per elementary trace) on behalf of fireBullet_falloff.
- **Gotchas:** It mutates the shared globals WarpZone_trace_forent and fireBullet_last_hit mid-trace - these are the same globals the outer loop reads, so callback and loop are tightly coupled. Draws trail only if the segment is longer than 16 units (avoids dots).

### `crosshair_trace / WarpZone_crosshair_trace`  _(server)_
`void crosshair_trace(entity pl); void WarpZone_crosshair_trace(entity pl)  [server/weapons/tracing.qc:549, 585]`

The aim/crosshair trace used by auto-aim, nade targeting, prydon cursor, vehicles, etc. crosshair_trace is the NON-warpzone version: traceline_antilag from cursor_trace_start toward cursor_trace_endpos*max_shot_distance. WarpZone_crosshair_trace is the warpzone-aware twin: WarpZone_traceline_antilag(pl, start, start+normalize(endpos-start)*max_shot_distance, ...) so the crosshair pick continues across warpzones. After this, callers read trace_ent/trace_endpos; if they need the far-side point in the shooter frame they apply WarpZone_UnTransformOrigin with WarpZone_trace_transform (as W_SetupShot does).

- **Called by:** crosshair_trace_plusvisibletriggers / WarpZone_crosshair_trace_plusvisibletriggers and direct callers in mutators/vehicles/nades.
- **Gotchas:** Two variants exist so callers choose whether the crosshair should see through a warpzone; defaulting to the non-WZ one keeps targeting cheap when zones are irrelevant. Both rely on CS(pl).cursor_trace_start/endpos already being populated.

### `crosshair_trace_plusvisibletriggers__is_wz`  _(server)_
`void crosshair_trace_plusvisibletriggers__is_wz(entity pl, bool is_wz)  [server/weapons/tracing.qc:564]`

Temporarily promotes all model-bearing SOLID_TRIGGER entities to SOLID_BSP (tracking them in g_ctrace_changed) so the crosshair trace can hit them, then dispatches to WarpZone_crosshair_trace(pl) when is_wz is true or crosshair_trace(pl) otherwise, then restores the triggers to SOLID_TRIGGER. The is_wz flag is the single switch that routes the crosshair pick through the warpzone-aware trace path.

- **Called by:** crosshair_trace_plusvisibletriggers (tracing.qc:554) and WarpZone_crosshair_trace_plusvisibletriggers (tracing.qc:559).
- **Gotchas:** Mutating trigger solidity globally during a trace is a shared-state side effect; the restore pass via g_ctrace_changed must always run. Two public wrappers (crosshair_trace_plusvisibletriggers vs WarpZone_crosshair_trace_plusvisibletriggers) only differ by this bool.

### `WarpZone_Projectile_Touch_ImpactFilter_Callback`  _(server)_
`bool WarpZone_Projectile_Touch_ImpactFilter_Callback(entity this, entity toucher)  [server/weapons/common.qc:150]`

The weapon-side hook that the warpzone library's WarpZone_Projectile_Touch calls to decide whether a projectile touch is a genuine impact (return true => caller's PROJECTILE_TOUCH macro returns/stops the touch handler) or should be ignored. Returns true (suppress impact) when: toucher is the projectile's own owner; or g_projectiles_interact==1 and toucher is a blasterbolt (deflection special-case); or SUB_NoImpactCheck passes (sky/noimpact surface or the sky-grapple bug) in which case it also tears down the projectile (RemoveHook for grapplinghook, delete otherwise, nades exempt). Otherwise, for a solid real hit it calls UpdateCSQCProjectile(this) and returns false so the normal impact code runs.

- **Called by:** WarpZone_Projectile_Touch (lib/warpzone/server.qc), which is invoked by the PROJECTILE_TOUCH(e,t) macro in every projectile touch function.
- **Gotchas:** This is the QC half; the warpzone half (WarpZone_Projectile_Touch in lib/warpzone/server.qc:344) FIRST returns true if toucher is a trigger_warpzone (so warping never counts as an impact) and returns true if time==this.warpzone_teleport_time (suppresses stale touch events from the engine in the same frame the projectile teleported). So projectile-vs-warpzone traversal is handled entirely inside the lib; this callback never sees the warpzone itself, only post-warp/normal touches.

### `PROJECTILE_TOUCH macro / WarpZone_Projectile_Touch`  _(server)_
`#define PROJECTILE_TOUCH(e,t) ... WarpZone_Projectile_Touch(e,t) ...  [server/weapons/common.qh:28]; float WarpZone_Projectile_Touch(entity this, entity toucher)  [lib/warpzone/server.qc:344]`

The standard guard every projectile touch handler begins with. The macro calls WarpZone_Projectile_Touch and returns from the touch func if it yields true. WarpZone_Projectile_Touch returns true (=> no impact) for trigger_warpzone touchers and for same-frame post-teleport touches, otherwise delegates to WarpZone_Projectile_Touch_ImpactFilter_Callback. This is THE mechanism by which a flying projectile passes through a warpzone instead of detonating on the warpzone brush: the actual geometric teleport of the live entity is performed by the engine/warpzone touch on the trigger_warpzone, and this guard ensures the projectile's own touch logic ignores that event.

- **Called by:** Every weapon projectile touch function (rockets, grenades, electro, crylink, hagar, etc.).
- **Gotchas:** warpzone_teleport_time guard exists because the engine may still raise touch events for the pre-teleport location in the same frame. PROJECTILE_MAKETRIGGER (common.qh:34) sets projectiles to SOLID_CORPSE + clipgroup so they collide correctly and interact with warpzones/each other as intended.

### `UpdateCSQCProjectileAfterTeleport`  _(server)_
`void UpdateCSQCProjectileAfterTeleport(entity e)  [server/weapons/csqcprojectile.qc:119]`

Called after a CSQC-networked projectile has been teleported by a warpzone. Forces SendFlags 0x01 (new origin), 0x02 (full re-send so the clientside copy is fully re-initialised/reset), and 0x08 (the teleport bit). The 0x08 bit travels to the client in CSQCProjectile_SendEntity and is read in the ENT_CLIENT_PROJECTILE handler to reset the trail and interpolation so the clientside projectile does not render a streak across the warp and does not erroneously trigger its clientside SUB_Stop touch.

- **Called by:** Warpzone projectile teleport handling (warpzone postteleport path) for networked projectiles.
- **Gotchas:** Comment explicitly states 0x02 (full data resend) is a workaround for client-side projectiles occasionally calling SUB_Stop when passing through a warpzone. Must be called by the warpzone teleport path for any projectile that uses CSQCProjectile networking, otherwise the client trail desyncs across the zone.

### `Ent_RemoveProjectile / SUB_Stop / NET_HANDLE(ENT_CLIENT_PROJECTILE) (CSQC)`  _(client)_
`void Ent_RemoveProjectile(entity this); void SUB_Stop(entity this, entity toucher); NET_HANDLE(ENT_CLIENT_PROJECTILE, bool isnew)  [client/weapons/projectile.qc:211, 20, 220]`

Clientside half of projectile traversal. The ENT_CLIENT_PROJECTILE receiver reads SendFlags; when the teleport bit (f & 0x08) is set it resets trail_oldorigin = origin (and InterpolateOrigin_Reset for interpolated projectiles) so no trail line is drawn across the warp. Clientanimated projectiles (count & 0x80) re-simulate movement locally via Movetype_Physics_* in Projectile_Draw and have settouch(SUB_Stop) - the server-side full resend (0x02) after a warp re-initialises these so a stale local touch does not freeze them. Ent_RemoveProjectile does a final tracebox along the remaining velocity to draw the last trail segment on removal.

- **Called by:** Engine CSQC entity update loop (NET_HANDLE), draw loop (Projectile_Draw), entremove hook (Ent_RemoveProjectile).
- **Gotchas:** Clientside projectiles do NOT run warpzone transform math themselves - they rely on the server resending a fresh post-warp origin/velocity (and the 0x08 reset) rather than transforming locally. The bit semantics in CSQCProjectile_SendEntity (0x08 = teleport/no-trail) must stay in sync between server send and client read. anglestransform.qh is included here only for projectile spin (AnglesTransform_* in Projectile_Draw), not for warp handling.

### `WarpZone_traceline_antilag / tracebox_antilag_force_wz`  _(server)_
`void WarpZone_traceline_antilag(entity source, vector v1, vector v2, float nomonst, entity forent, float lag); void tracebox_antilag_force_wz(entity source, vector v1, vector mi, vector ma, vector v2, float nomonst, entity forent, float lag, float wz)  [server/antilag.qc:221, 169]`

The antilag+warpzone trace wrapper used by W_SetupShot and FireRailgunBullet. WarpZone_traceline_antilag gates lag (zeroed unless g_antilag==2 and client allows it) then calls _force, which calls tracebox_antilag_force_wz with wz=true. That core: temporarily sets source.dphitcontentsmask to hit corpses, optionally antilag_takeback_all (rewinds all players to their past positions for lag compensation), then because wz is true calls WarpZone_TraceBox (the warpzone-aware trace that walks across zones and fills WarpZone_trace_transform), then antilag_restore_all, then restores the mask. The non-wz path (traceline_antilag/tracebox_antilag) calls plain tracebox and does NOT populate the warpzone transform.

- **Called by:** W_SetupShot_Dir_ProjectileSize_Range, FireRailgunBullet, WarpZone_crosshair_trace, and traceline_antilag_force usage in W_SetupShot antilag-ghost branch.
- **Gotchas:** Antilag rewind happens AROUND the whole multi-zone warpzone trace, so player positions are consistent across all zone segments. The wz boolean is the only difference between the warpzone and non-warpzone antilag traces; callers must pick the WarpZone_ variant to get cross-zone behavior AND a valid WarpZone_trace_transform. lag is forced to 0 for non-real-clients.

### `WarpZone_TraceBox_ThroughZone (lib reference)`  _(common)_
`void WarpZone_TraceBox_ThroughZone(vector org, vector mi, vector ma, vector end, float nomonsters, entity forent, entity zone, WarpZone_trace_callback_t cb)  [lib/warpzone/common.qc:192]`

The engine of cross-zone tracing that fireBullet_falloff drives directly. It resets WarpZone_trace_firstzone/lastzone, inits WarpZone_trace_transform (identity accumulator). If the start is already inside a warpzone it transforms org/end immediately and adds that zone's transform. It then loops (max 16 zones): tracebox; invoke cb per segment; if it hit a trigger_warpzone, accumulate that zone via WarpZone_Trace_AddTransform, transform org=TransformOrigin(wz,trace_endpos) and end=TransformOrigin(wz,end), step back slightly, and continue; otherwise stop. The cumulative transform left in WarpZone_trace_transform is exactly what callers feed to WarpZone_(Un)TransformOrigin/Velocity. A contentshack temporarily adds DPCONTENTS_SOLID to forent's mask so warpzone brushes register as hits even for body-only masks (relevant to PENETRATEWALLS shots).

- **Called by:** fireBullet_falloff (directly with cb=fireBullet_trace_callback), WarpZone_TraceBox/WarpZone_TraceLine (used by W_SetupShot and the antilag wrappers), WarpZone_TrailParticles.
- **Gotchas:** trace_endpos/trace_fraction returned are in the FINAL zone frame and trace_fraction is the composite fraction across all segments. v_forward/right/up are saved/restored by the lib. If the trace starts inside a DIFFERENT zone than the requested zone arg it returns a zero-length failed trace. The accumulator is a single shared global entity (warpzone_trace_transform) - only one such trace's transform is valid at a time, so callers must read WarpZone_trace_transform immediately after the trace and before any other warpzone trace runs.

**Entities / fields.** Trace-result globals consumed/restored by callers: trace_endpos, trace_ent, trace_fraction, trace_dphitq3surfaceflags, trace_dphitcontents, trace_startsolid, dphitcontentsmask (mask field). Warpzone trace globals: WarpZone_trace_transform (accumulator entity holding the composite warpzone_transform/warpzone_shift), WarpZone_trace_firstzone, WarpZone_trace_lastzone, WarpZone_trace_forent. Weapon globals: w_shotorg, w_shotdir, w_shotend (tracing.qh). fireBullet globals: fireBullet_trace_callback_eff, fireBullet_last_hit. Railgun per-target fields: .railgunhit, .railgunhitloc, .railgundistance, .railgunforce, .railgunhitsolidbackup (g_railgunhit IntrusiveList). Projectile fields: .csqcprojectile_type, .silent, .realowner, .warpzone_teleport_time (set by warpzone lib), SendFlags bits 0x01/0x02/0x08 used by UpdateCSQCProjectileAfterTeleport. Macros: PROJECTILE_TOUCH(e,t), PROJECTILE_MAKETRIGGER(e). Warpzone brush classname trigger_warpzone. g_ctrace_changed IntrusiveList (triggers temporarily made solid for crosshair traces).

**Netcode.** CSQCProjectile_SendEntity (server/weapons/csqcprojectile.qc) serializes projectiles to CSQC. SendFlags bit 0x08 (teleport bit) is set by UpdateCSQCProjectileAfterTeleport when a projectile passes a warpzone; the client treats 0x08 in the ENT_CLIENT_PROJECTILE NET_HANDLE (client/weapons/projectile.qc) as a reset signal (resets trail_oldorigin and InterpolateOrigin so the clientside trail/interpolation does not draw a line across the warp). Bit 0x02 forces a full re-send (model/type) to re-init a projectile that may have been mis-handled across the warp. Bit 0x80 = clientanimate (client re-simulates physics, sends velocity+gravity); 0x01 = origin update. The comment in UpdateCSQCProjectileAfterTeleport notes it is a workaround for clientside projectiles erroneously firing their SUB_Stop touch when passing a warpzone. There is no direct warpzone-transform serialization on the wire; the client re-receives a fresh post-warp origin/velocity rather than a transform. The crosshair/aim trace is purely server-side (uses CS(pl).cursor_trace_start/endpos which arrive via the prydon cursor/normal client aim networking, not warpzone-specific).

**Dependencies.** lib/warpzone (common.qc/qh, server.qc/qh): WarpZone_TraceLine, WarpZone_TraceBox, WarpZone_TraceBox_ThroughZone, WarpZone_TraceToss_ThroughZone, WarpZone_TransformOrigin/Velocity/Angles, WarpZone_UnTransformOrigin/Velocity, WarpZone_Find, WarpZone_MakeAllSolid/Other, the trace globals (WarpZone_trace_transform, WarpZone_trace_firstzone, WarpZone_trace_lastzone, WarpZone_trace_forent), the WarpZone_Accumulator (composite transform), AnglesTransform_* primitives, and WarpZone_Projectile_Touch. server/antilag.qc: WarpZone_traceline_antilag(_force), traceline_antilag(_force), tracebox_antilag, tracebox_antilag_force_wz, antilag_takeback_all/restore_all. Engine builtins: tracebox/traceline/tracetoss, traceline_inverted, makevectors, the trace_* result globals. server/damage.qc: Damage, Damage_DamageInfo. accuracy/hitplot subsystems.

## integration: bots/antilag/portals/view

**Role.** This area covers the four subsystems that consume warpzone primitives rather than implementing the warpzone library itself. Warpzones (entity classname "trigger_warpzone") are seamless space-folding portals: a player/trace passing through one is transformed (origin + angles) into the destination zone via the WarpZone library (lib/warpzone). The integrations are: (1) bot navigation/waypointing, which generates one-way TELEPORT waypoint links across each warpzone pair so havocbots can path through them, with special handling for oblique/downward zones; (2) antilag, which exposes WarpZone-aware trace variants so hitscan rewinds work across zone boundaries; (3) the Porto weapon's player-shootable portals, which are a parallel teleporter implementation built on the same AnglesTransform math from the warpzone library (not actual trigger_warpzones); and (4) the client view/crosshair, which use WarpZone_TraceLine/TraceBox and WarpZone_FixView so the camera, eventchase, and crosshair placement follow folded space correctly.

### `WarpZone_PostInitialize_Callback`  _(server)_
`void WarpZone_PostInitialize_Callback() [server/main.qc:518]`

Warpzone-library post-init hook fired once after all trigger_warpzone entities are spawned and paired. Spawns a temp tracetest_ent sized to the player bbox (PL_MIN_CONST/PL_MAX_CONST) with a hitcontents mask of SOLID|BODY|PLAYERCLIP|BOTCLIP, then iterates every entity with classname 'trigger_warpzone' via find() and calls waypoint_spawnforteleporter_wz on each to generate bot navigation links across it. Deletes the temp entity when done.

- **Called by:** warpzone library init sequence (engine/library PostInitialize)
- **Gotchas:** Iterates with find(classname) rather than the commented-out warpzone_first/warpzone_next linked list. Runs once at map load; warpzone waypoint links are static thereafter. The tracetest_ent's BOTCLIP/PLAYERCLIP mask matters so generated waypoints respect bot-only clip brushes.

### `waypoint_spawnforteleporter_wz`  _(server)_
`void waypoint_spawnforteleporter_wz(entity e, entity tracetest_ent) [waypoints.qc:2026]`

Generates the one-way bot waypoint link for a single warpzone e (e.enemy is its paired destination zone). Normalizes source and destination warpzone pitch angles (warpzone_angles.x) into [-180,180]. Early-returns (no waypoints) if either side points straight up (angle == -90) since bots cannot use upward zones. Computes a source point at the zone's bbox center, projected onto the zone plane via makevectors(e.warpzone_angles) and the warpzone_origin, nudged +16 on v_right; computes the destination point symmetrically on e.enemy nudged -16 on v_right. If the source zone is not pointing straight down (angle != 90) it snaps both points to the ground with waypoint_fixorigin_down_dir along each zone's -v_up, and if the source is oblique (angle != 0) it adds WAYPOINTFLAG_JUMP. Finally calls waypoint_spawnforteleporter_boxes with WAYPOINTFLAG_TELEPORT|extra_flag, timetaken 0.

- **Called by:** WarpZone_PostInitialize_Callback (server/main.qc); no-op stub in bot/null/bot_null.qc
- **Gotchas:** The +16/-16 v_right offsets place entry/exit points slightly to the side so bots don't immediately re-enter. timetaken passed as 0 (unlike jumppads which pass real travel time) so pathing cost across a warpzone is treated as ~instant. Three distinct angle cases (up = skip, down = no ground snap, oblique = add JUMP) are the main subtlety; the JUMP flag is what later makes the bot press jump (see havocbot.qc:1017) to clear oblique-zone geometry.

### `waypoint_spawnforteleporter_boxes`  _(server)_
`void waypoint_spawnforteleporter_boxes(entity e, int teleport_flag, vector org1, vector org2, vector destination1, vector destination2, float timetaken) [waypoints.qc:2010]`

Shared low-level helper used by both real teleporters and warpzones. Spawns a box source waypoint (org1..org2) carrying teleport_flag and a point destination waypoint, then hard-wires a one-way link w.wp00 = dw (also stored in wp00_original) with wp00mincost = timetaken. Records the teleporter's nearestwaypoint = source wp with an infinite timeout (-1).

- **Called by:** waypoint_spawnforteleporter_wz, waypoint_spawnforteleporter
- **Gotchas:** Link is deliberately one-way (only wp00 set), matching the directional nature of warpzones/teleporters. wp00mincost is only meaningful for jumppads; for warpzones timetaken is 0. nearestwaypointtimeout = -1 pins the association permanently.

### `navigation_poptouchedgoals`  _(server)_
`int navigation_poptouchedgoals(entity this) [navigation.qc:1630]`

Per-frame goal-reached detection that special-cases TELEPORT-flagged goals (which include warpzone source waypoints). For a TELEPORT goal that is NOT a box (wpisbox false => a warpzone, since warpzone source waypoints are spawned as non-box points by _wz) it pops both origin and destination goals immediately once the bot is closer to the next goal, resetting lastteleporttime. For box teleport goals (real teleporters) it waits for TELEPORT_USED confirmation plus jumppad delay logic. A separate branch handles the case where a jumppad/shot launched the bot so hard it skipped ahead and touched a later teleport/warpzone goal (goalstack01/02/03): it scans for that tele_ent, and if TELEPORT_USED, pops all goals up to and including it.

- **Called by:** bot navigation tick (havocbot movement/goal update path)
- **Gotchas:** The wpisbox check at line 1639 is the discriminator: warpzone source waypoints are point waypoints so they take the immediate distance-based pop path, whereas real teleporters (boxes) wait for TELEPORT_USED. The skip-ahead recovery (lines 1682-1715) exists specifically because warpzones/jumppads can teleport the bot past intermediate goals; without it bots would get stuck with a stale goal stack.

### `havocbot_ai`  _(server)_
`void havocbot_ai(entity this) [havocbot.qc:35]`

Top-level per-frame bot brain. Relevant warpzone touch point: immediately after clearing bot_aimdir_executed, it sets bot_aimdir_executed = true when (this.lastteleporttime && !this.jumppadcount), i.e. it suppresses aim-direction execution for one frame when the bot has just teleported through a warpzone or teleporter (but not a jumppad). This 'locks aim' so the bot doesn't snap its view based on pre-warp angles right after the transform.

- **Called by:** bot AI dispatch (this.bot_ai assigned in havocbot_setupbot)
- **Gotchas:** lastteleporttime is the unified marker for both teleporters and warpzone passage; the !jumppadcount condition is what distinguishes a warpzone/teleport (aim lock wanted) from a jumppad (no aim lock, since a jumppad doesn't reorient the player). Missing this would cause visible aim glitches/mis-steers right after a bot passes through an angled warpzone.

### `havocbot_movetogoal (oblique-warpzone jump)`  _(server)_
`(within havocbot movement, havocbot.qc ~line 1011-1019)`

During movement toward a goal, when the current goal is a TELEPORT/LADDER waypoint with no further stack and the bot is within the offset distance, if the goal also carries WAYPOINTFLAG_JUMP and there is a goalstack01, the bot presses PHYS_INPUT_BUTTON_JUMP. The inline comment 'oblique warpzones need a jump otherwise bots gets stuck' identifies this as the consumer of the JUMP flag that waypoint_spawnforteleporter_wz attaches to oblique zones.

- **Called by:** havocbot movement tick
- **Gotchas:** The JUMP flag is set on the source warpzone waypoint only for oblique zones (src_angle != 0 and not pure down). This is the runtime half of that contract; the two pieces (flag assignment in waypoints.qc and jump press here) must stay in sync or bots stall at angled warpzone mouths.

### `havocbot_moteto / havocbot_moveto (personal waypoint teleport tagging)`  _(server)_
`(havocbot.qc ~line 1729-1745)`

When spawning a bot 'personal' waypoint at pos, it checks whether pos lies inside any teleporter/warpzone brush by iterating IL_EACH(g_teleporters, WarpZoneLib_BoxTouchesBrush(pos, pos, it, NULL)). If so it ORs WAYPOINTFLAG_TELEPORT onto the new waypoint and resets this.lastteleporttime = 0, so the personal goal is treated as a teleport entry rather than a normal walk target.

- **Called by:** havocbot personal-waypoint routing (cmd_moveto path)
- **Gotchas:** g_teleporters is a shared intrusive list holding both real teleporters and trigger_warpzones; WarpZoneLib_BoxTouchesBrush is the precise brush-overlap test (not a bbox approximation) needed because warpzone brushes can be thin angled planes. Uses pos for both min and max (point test).

### `crosshair_trace_waypoints`  _(server)_
`void crosshair_trace_waypoints(entity pl) [waypoints.qc:2129]`

Editor/debug helper for the waypoint editor: temporarily makes all g_waypoints SOLID_BSP (and gives point waypoints a 16-unit box), runs WarpZone_crosshair_trace(pl) so the trace from the player's crosshair follows warpzones into folded space, then restores waypoints to SOLID_TRIGGER / zero size. Resolves trace_ent to the hit waypoint (or NULL) and snaps trace_endpos to the waypoint origin for non-box waypoints.

- **Called by:** waypoint editor commands (bot waypoint placement/inspection)
- **Gotchas:** Uses the warpzone-aware crosshair trace rather than a plain traceline so a mapper aiming through a warpzone selects the waypoint actually under the (transformed) crosshair on the far side. Mutating .solid globally on all waypoints for the duration of one trace is the trick that makes waypoints selectable; must be restored or normal navigation breaks.

### `tracebox_antilag_force_wz`  _(server)_
`void tracebox_antilag_force_wz(entity source, vector v1, vector mi, vector ma, vector v2, float nomonst, entity forent, float lag, float wz) [antilag.qc:169]`

Core antilag trace primitive. Optionally rewinds the world: if lag>=0.001 and forent is a real client, it temporarily sets source.dphitcontentsmask to SOLID|BODY|CORPSE (so the shot can hit corpses), calls antilag_takeback_all(forent, lag) to move all other players/monsters/nades back into the past, performs the trace, then antilag_restore_all and restores the shooter's contents mask. The wz flag selects WarpZone_TraceBox (warpzone-aware, follows the trace through zone transforms) when true, or plain tracebox when false.

- **Called by:** traceline_antilag_force, tracebox_antilag, WarpZone_traceline_antilag_force, WarpZone_tracebox_antilag
- **Gotchas:** The single wz boolean is the only difference between the warpzone and non-warpzone antilag families; both rewind identically. Crucial subtlety: takeback happens BEFORE the trace and is purely an origin rewrite (setorigin) on each lagged entity, so a WarpZone_TraceBox sees the past origins AND folds correctly across zones in one pass. lag is forced to 0 unless forent is a real client (bots/projectiles never antilag the world).

### `WarpZone_traceline_antilag / WarpZone_tracebox_antilag (+_force variants)`  _(server)_
`void WarpZone_traceline_antilag(entity source, vector v1, vector v2, float nomonst, entity forent, float lag) [antilag.qc:221]; WarpZone_tracebox_antilag(...) [antilag.qc:228]; *_force at :217`

Public warpzone-aware antilag entry points used by hitscan weapons that must shoot across warpzones. The non-force versions first gate antilag on cvar: if autocvar_g_antilag != 2 (or the shooter set cl_noantilag) lag is forced to 0, then they delegate to tracebox_antilag_force_wz with wz=true. The _force version skips the cvar gate and always passes lag through. traceline variants call with zero box extents.

- **Called by:** warpzone-traversing hitscan weapon fire code (weapons calling the WarpZone_*_antilag API)
- **Gotchas:** autocvar_g_antilag == 2 is 'full' antilag (trace-level); value 1 antilags movement only, so these trace wrappers zero out lag unless ==2. The parallel non-WarpZone family (traceline_antilag/tracebox_antilag) is byte-for-byte identical except wz=false — keep the two sets in lockstep when editing antilag behavior.

### `antilag_takeback / antilag_takeback_all / antilag_restore_all`  _(server)_
`void antilag_takeback(entity e, entity store, float t) [antilag.qc:86]; void antilag_takeback_all(entity ignore, float lag) [antilag.qc:125]; void antilag_restore_all(entity ignore) [antilag.qc:138]`

antilag_takeback saves e's current origin (once) into store.antilag_saved_origin, computes the interpolated past origin via antilag_takebackorigin (binary-ish ring-buffer search antilag_find + lerpv), and setorigins e there. *_all applies it across all players, monsters, and nade projectiles except the shooter. restore puts everything back.

- **Called by:** tracebox_antilag_force_wz (both warpzone and plain paths)
- **Gotchas:** Takeback is a flat origin rewind only — it does NOT re-transform origins per-warpzone; the warpzone correctness comes entirely from using WarpZone_TraceBox over the rewound positions. Vehicles recurse into the vehicle entity unless VHF_PLAYERSLOT. antilag_takenback guards prevent double-save. Ring buffer is ANTILAG_MAX_ORIGINS(64) deep; ANTILAG_LATENCY clamps lookback to 0.4s.

### `Portal_Connect`  _(server)_
`void Portal_Connect(entity teleporter, entity destination) [portals.qc:387]`

Links a Porto in-portal to an out-portal. Computes teleporter.portal_transform = AnglesTransform_RightDivide(AnglesTransform_TurnDirectionFR(destination.mangle), teleporter.mangle) — the exact same AnglesTransform machinery the warpzone library uses to fold orientation between two oriented planes. Sets reciprocal .enemy pointers, makes in/out portals, sets fade timers, and links the in-portal into the area grid as SOLID_TRIGGER (PORTALS_ARE_NOT_SOLID).

- **Called by:** Portal_SetInPortal, Portal_SetOutPortal
- **Gotchas:** This is where 'Porto reuses warpzone tech' is concrete: portal_transform is built from anglestransform.qh primitives, identical in spirit to a warpzone's plane-to-plane transform. TurnDirectionFR flips the destination so players come OUT facing away from the exit plane. Portals are SOLID_TRIGGER (not real BSP warpzones) so teleport is handled in touch/think, not by engine warpzone traversal.

### `Portal_TeleportPlayer`  _(server)_
`float Portal_TeleportPlayer(entity teleporter, entity player, entity portal_owner) [portals.qc:110]`

Performs the actual Porto teleport using warpzone-style transforms. Transforms the player's relative origin and velocity through teleporter.portal_transform via AnglesTransform_Apply; shifts the result out of the destination plane using fixedmakevectors(enemy.mangle) and PlayerEdgeDistance; clamps tangential offset to +/-48; runs two safety traceboxes from the destination's portal_safe_origin to find a non-solid spot; transforms the player's view/aim angles via Portal_ApplyTransformToPlayerAngle (players) or AnglesTransform_ApplyToAngles (non-players); negates and transforms player.right_vector (factor -1 allows portal chaining); fires the PortalTeleport mutator hook; then calls the shared TeleportPlayer with TELEPORT_FLAGS_PORTAL, handling telefrag (tdeath) and Amazing achievement.

- **Called by:** Portal_Touch, Portal_Think_TryTeleportPlayer
- **Gotchas:** All directional math is AnglesTransform_* from the warpzone library — the portal is essentially a dynamically-created, player-shootable warpzone whose transform is precomputed in Portal_Connect. The double safety-trace (portal_safe_origin -> step -> to) guards against spawning the player in solid; both can FAIL-return 0. right_vector negation is the chaining hack. Multiple defensive 'enemy got cleared mid-teleport' backtraces because mutators/telefrags can disconnect the portal during the call.

### `Portal_ApplyTransformToPlayerAngle`  _(server)_
`vector Portal_ApplyTransformToPlayerAngle(vector transform, vector vangle) [portals.qc:47]`

Re-derives a sane player view angle after applying a portal/warpzone transform. Flips pitch sign for Quake angle convention, builds forward/up/yawforward via fixedmakevectors, applies AnglesTransform_Apply to each, then reconstructs yaw: if the new forward points nearly straight up/down it derives yaw from the up vector (choosing the hemisphere via new_up*new_yawforward), otherwise straight from forward. Preserves roll (ang.z = vangle.z).

- **Called by:** Portal_TeleportPlayer
- **Gotchas:** This is the same gimbal-avoidance problem the warpzone library solves for view angles (WarpZone_TransformVAngles). The straight-up/down special case (>0.7 / <-0.7) prevents yaw ambiguity at the poles. POSITIVE_PITCH_IS_DOWN compile guards bracket the pitch sign flips — players use different pitch math than generic entities.

### `View_EventChase (warpzone-aware camera traces)`  _(client)_
`(within View_EventChase, client/view.qc ~lines 825-869)`

Third-person/death camera placement that must respect warpzones. Uses WarpZone_TraceLine for the vertical view-offset clearance check, WarpZone_TraceBox to pull the camera back to eventchase_target_origin behind the player (MOVE_WORLDONLY), falling back to WarpZone_TraceLine if the box trace starts solid. After positioning, sets VF_ORIGIN to trace_endpos and, when not a viewloc, sets VF_ANGLES = WarpZone_TransformVAngles(WarpZone_trace_transform, view_angles) so if the camera trace crossed a warpzone the view angles are folded to match.

- **Called by:** CSQC_UpdateView -> View_EventChase (client/view.qc:1730)
- **Gotchas:** WarpZone_trace_transform is a global side-output of the last WarpZone_Trace* call; it must be consumed immediately (here right after the box/line trace) before another warpzone trace overwrites it. Without the warpzone-aware traces and TransformVAngles, the chase camera would clip through or render the wrong side of a warpzone the player is standing in front of.

### `CSQC_UpdateView -> WarpZone_FixView`  _(client)_
`(client/view.qc:1735, WarpZone_FixView())`

Per-frame call into the warpzone client library that adjusts the final VF_ORIGIN/VF_ANGLES so the rendered scene is correct when the local view origin is inside or adjacent to a warpzone (the engine renders the folded geometry; FixView aligns the camera transform to it). Called after View_SpectatorCamera, View_EventChase, and View_Lock, and before viewmodel_draw / reading back view_origin/view_angles. The companion WarpZone_FixPMove is present but commented out.

- **Called by:** CSQC_UpdateView (client/view.qc main render entry)
- **Gotchas:** Ordering is load-bearing: FixView must run after all camera-positioning code (eventchase/lock) but BEFORE view_origin/view_angles are read back via getpropertyvec and before viewmodel_draw, per the inline comment 'run viewmodel_draw before updating view_angles to the angles calculated by WarpZone_FixView'. WarpZone_FixPMove being disabled means client-side prediction does not currently re-fold through warpzones.

### `HUD crosshair placement (WarpZone_TraceLine)`  _(client)_
`(client/hud/crosshair.qc ~line 275, within chase-cam crosshair logic)`

In chase_active mode with crosshair_chase, to decide whether the local player's own body is occluding the crosshair (and fade alpha accordingly), it first does a pointinsidebox test, and if that misses, casts WarpZone_TraceLine(view_origin, view_origin + max_shot_distance*view_forward, MOVE_NORMAL, NULL) and checks whether trace_ent == csqcplayer. This makes the self-occlusion/alpha-fade test correct even when the aim line passes through a warpzone.

- **Called by:** HUD_Crosshair / crosshair update (client render)
- **Gotchas:** Only this self-hit test uses the warpzone-aware trace; the subsequent crosshair-position trace at line 287 uses plain traceline(MOVE_WORLDONLY) + project_3d_to_2d, so the rendered crosshair dot itself does NOT follow warpzones — only the player-alpha occlusion check does. max_shot_distance bounds the cast. This is an intentional asymmetry worth noting if crosshair-through-warpzone placement is ever desired.

**Entities / fields.** trigger_warpzone (iterated in WarpZone_PostInitialize_Callback); fields .warpzone_angles, .warpzone_origin, .enemy used by waypoint_spawnforteleporter_wz. g_teleporters intrusive list (warpzones + teleporters) queried in havocbot_moveto. Waypoint entities with WAYPOINTFLAG_TELEPORT (and optional WAYPOINTFLAG_JUMP for oblique zones), wpisbox distinguishing box waypoints (warpzone/teleport) from point waypoints. Porto subsystem: classname "porto" (the flying portal-spawner projectile) and "portal" (placed portal bmodel) entities with fields .portal_transform, .portal_safe_origin, .portal_id, .aiment (owner), .enemy (paired portal), .mangle, .right_vector; owner fields .portal_in/.portal_out. Antilag store entities carry .antilag_origins[]/.antilag_times[]/.antilag_index/.antilag_saved_origin/.antilag_takenback.

**Netcode.** No warpzone-specific serialization lives in these files. The client integrations (view.qc, crosshair.qc) are CSQC rendering only: WarpZone_FixView and WarpZone_Trace* operate on the locally-replicated warpzone entities the warpzone library serializes elsewhere. Antilag traces run server-side. Porto portals are server entities sent to clients via standard csqcmodel networking (setmodel/savemodelindex + setcefc Portal_Customize controls per-client visibility/modelindex), not a bespoke warpzone protocol.

**Dependencies.** lib/warpzone/common.qh (WarpZone_TraceLine, WarpZone_TraceBox, WarpZone_crosshair_trace, WarpZone_trace_transform), lib/warpzone/util_server.qh (WarpZoneLib_BoxTouchesBrush, WarpZone_PostInitialize_Callback hook), lib/warpzone/client.qh (WarpZone_FixView, WarpZone_FixPMove), lib/warpzone/anglestransform.qh (AnglesTransform_Apply/_ApplyToAngles/_ApplyToVAngles/_RightDivide/_TurnDirectionFR). Warpzone entity fields read here: .warpzone_angles, .warpzone_origin, .enemy (paired zone), .absmin/.absmax, .mangle. Engine builtins: makevectors/fixedmakevectors, tracebox/traceline, setorigin, setproperty/getpropertyvec (VF_ORIGIN/VF_ANGLES), find/IL_EACH, project_3d_to_2d. Cross-subsystem: navigation waypoint flags (WAYPOINTFLAG_TELEPORT/JUMP/GENERATED), TeleportPlayer + tdeath, mutator hook PortalTeleport.
---

# Part 2 - The port today (what exists in XonoticGodot)

## port: Warpzone.cs

Warpzone.cs (C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs) is a substantially complete, Godot-free port of Xonotic's lib/warpzone. The deterministic transform core (WarpzoneTransform) and the actual teleport (WarpzoneManager.Teleport — moves the entity, transforms velocity, angles and avelocity, cancels interpolation, fires targets) are fully IMPLEMENTED with real math from QMath (AngleVectors / FixedVecToAngles / FixedVecToAngles2, all verified real). Plane auto-derivation from brush geometry (getsurface* analogue), map-entity spawnfunc wiring, two-way linking, and Porto-portal placement are all implemented. There are NO TODO/NotImplemented/empty-body stubs in the file. The main divergences from Base QC are: (1) roll about the forward axis is intentionally not modeled in the Inverse() path (AnglesOf drops the up reference); (2) there is no warpzone trace/radius recursion accumulator — GetAffine emits the affine form for a WarpzoneTransformChain that is NOT defined in this file (missing-but-referenced); (3) the crossing gate uses the entity's velocity vs InForward rather than QC's actual IN-plane side-crossing test, so a fast-strafing entity moving with a component along the surface but past the plane could be mis-gated; (4) warp is touch-driven only — there is no per-frame solid-trace WarpZone_Teleport sweep, so very fast movers can tunnel; (5) seamless client VIEW rendering through the portal is explicitly out of scope (server/transform port only).

- **`WarpzoneTransform (struct fields/props: InOrigin, OutOrigin, InForward, OutForward, Valid)`** [IMPLEMENTED] - Holds the precomputed in/out forward-right-up bases plus in/out origins and a Valid flag; exposes read-only accessors used by the manager (notably InForward for the crossing gate and the two origins for transforms).  _(Base: warpzone fields warpzone_transform/warpzone_shift + .warpzone_origin/_angles)_ **Gap:** Stores explicit bases instead of QC's AnglesTransform vector encoding; equivalent for pos/vel/forward but cannot represent roll independently.
- **`WarpzoneTransform(inOrigin, inAngles, outOrigin, outAngles) ctor`** [IMPLEMENTED] - Calls QMath.AngleVectors on both the IN and OUT Euler angles to build forward/right/up bases, stores both origins, sets Valid=true.  _(Base: WarpZone_SetUp(e, my_org, my_ang, other_org, other_ang))_ **Gap:** Faithful. Builds basis directly rather than the QC AnglesTransform_FromAngles path; no validation that the planes are sane.
- **`Rotate(v)`** [IMPLEMENTED] - Projects v onto the IN basis (a=Dot(inFwd,v), b=Dot(inRight,v), c=Dot(inUp,v)) and re-expands in the 180-flipped OUT basis: -outFwd*a - outRight*b + outUp*c.  _(Base: AnglesTransform_Apply (rotation part) used by WarpZone_TransformVelocity)_ **Gap:** Correct R·v with the 180 flip; no gap for direction math.
- **`TransformOrigin(v)`** [IMPLEMENTED] - Returns outOrigin + Rotate(v - inOrigin) — translate into IN-local, rotate, translate to OUT.  _(Base: WarpZone_TransformOrigin)_ **Gap:** Faithful.
- **`TransformVelocity(v)`** [IMPLEMENTED] - Returns Rotate(v) (rotation only, no translation).  _(Base: WarpZone_TransformVelocity)_ **Gap:** Faithful.
- **`TransformAngles(angles)`** [IMPLEMENTED] - Converts angles to a forward vector via AngleVectors, rotates that forward through the portal, then re-derives angles with FixedVecToAngles.  _(Base: WarpZone_TransformAngles)_ **Gap:** Reconstructs from forward only — pitch/yaw preserved correctly but roll about forward is lost (QC AnglesTransform carries the full orientation).
- **`Inverse()`** [PARTIAL] - Builds the reverse OUT→IN transform by constructing a new WarpzoneTransform from the OUT plane (origin+angles via AnglesOf) to the IN plane.  _(Base: WarpZone_BackTransform / the e.enemy reverse transform)_ **Gap:** Loses roll: AnglesOf reconstructs angles from forward only (its own comment: 'roll from up not modeled'), so the inverse is not bit-exact for rolled planes.
- **`AnglesOf(fwd, up) (private)`** [PARTIAL] - Returns FixedVecToAngles(fwd), ignoring the up parameter.  _(Base: AnglesTransform_FromAngles inverse / vectoangles2)_ **Gap:** Takes an up vector but discards it; should use FixedVecToAngles2(fwd, up) to preserve roll. Marked deliberately simplified.
- **`GetAffine(out row0, row1, row2, shift)`** [IMPLEMENTED] - Decomposes the transform into rows of R (so (R·v)[k]==Dot(rowK,v)) and shift = outOrigin - R·inOrigin, for an affine accumulator.  _(Base: warpzone_transform/warpzone_shift pair (for WarpZone_Accumulator_AddTransform))_ **Gap:** The math is correct, but the consumer it documents (WarpzoneTransformChain / the trace recursion accumulator) is NOT defined in this file — referenced but missing, so this method is currently unused dead-ish surface for an unported feature.
- **`Warpzone (class fields: Trigger, Transform, InOrigin/InAngles, OutOrigin/OutAngles, TargetName/Target, Linked)`** [IMPLEMENTED] - Plain data record for one portal: the touch trigger entity, the computed transform, in/out plane origin+angles, link targetnames, and a Linked => Transform.Valid convenience.  _(Base: trigger_warpzone edict + .warpzone_* fields + .target/.targetname/.enemy)_ **Gap:** Faithful data model; no .enemy back-pointer object (linking is done by recomputing transforms instead).
- **`WarpzoneManager.Zones / Add(wz)`** [IMPLEMENTED] - Holds the world's zones in a list; Add appends and returns the zone.  _(Base: trigger_warpzone spawnfunc registration (g_warpzones list))_ **Gap:** None of note.
- **`Link()`** [IMPLEMENTED] - Reads sv_warpzone_allow_selftarget cvar; forward pass links each zone carrying a .target to its partner (excluding self unless cvar set); reverse pass links zones that are only targeted-by others back to their targeter (asymmetric maps).  _(Base: WarpZone_InitStep_FindTarget + the two-way this.enemy=e2;e2.enemy=this wiring)_ **Gap:** Faithful including the self-target cvar and asymmetric reverse pass; cvar only honored when Api.Services present.
- **`LinkOneWay(wz, partner) (private)`** [IMPLEMENTED] - Sets wz.OutOrigin/OutAngles from partner's IN plane and builds wz.Transform = IN→partner-IN.  _(Base: WarpZone_SetUp(this, this.org/ang, enemy.org/ang))_ **Gap:** Faithful.
- **`FindByTargetName(name, excludeSelf) (private)`** [IMPLEMENTED] - Linear scan for a zone whose TargetName matches, optionally skipping a given zone (self-target guard).  _(Base: find(world, targetname, ...) in WarpZone_InitStep_FindTarget)_ **Gap:** Returns first match only; QC behavior on duplicate targetnames is also effectively first-found, so acceptable.
- **`Teleport(e, wz)`** [IMPLEMENTED] - If unlinked, returns false. Gate: if Dot(e.Velocity, InForward)>0 and speed^2>1 (moving back out), returns false. Otherwise computes new origin via TransformOrigin, sets transformed Velocity, Angles, and AVelocity; relocates via Api.Entities.SetOrigin (or direct Origin in headless), sets OldOrigin to cancel interpolation; fires the trigger's targets via MapMover.UseTargets when a .target is present; returns true.  _(Base: WarpZone_Teleport + WarpZone_Touch (SUB_UseTargets))_ **Gap:** THE ACTUAL TELEPORT IS FULLY IMPLEMENTED (move + velocity + angles + avelocity + interpolation kill + target fire). Gaps: crossing test is a velocity-direction heuristic rather than QC's plane-side crossing check (mis-gates entities skimming the plane and allows near-stationary warp since speed^2>1 must also hold); no teleport sound/effect; no per-frame trace sweep (touch-only) so very fast movers can tunnel; angles transform loses roll (see TransformAngles).
- **`Spawn(inOrigin, inAngles, targetName, target, mins, maxs)`** [IMPLEMENTED] - Constructs a Warpzone from explicit plane params, creates its touch volume via SpawnTriggerFor, registers it.  _(Base: spawnfunc(trigger_warpzone) explicit-params path)_ **Gap:** Faithful for the explicit-orientation case.
- **`DerivePlaneFromBrush(brush, aiment)`** [IMPLEMENTED] - Iterates the brush's surfaces via ISurfaceService (GetSurfaceTexture until empty, skipping 'trigger' textures), accumulates area-weighted triangle normals and centroids, normalizes; rejects non-planar brushes (normal length < 0.99 after averaging). With an aiment (target_position): projects it onto the plane, flips normal to face out, derives angles with FixedVecToAngles2 (keeping roll). Without aiment: uses centroid + FixedVecToAngles(normal). Returns ok=false when no usable surface (QC error case).  _(Base: WarpZone_InitStep_UpdateTransform (getsurface* area-weighted plane fit))_ **Gap:** Faithful port of the getsurface* auto-orientation including non-planar rejection and the aiment seed path; depends on a real ISurfaceService being wired (returns false headless).
- **`SpawnFromBrush(brush, targetName, target, aiment)`** [IMPLEMENTED] - Derives the plane (DerivePlaneFromBrush); on failure returns null. Otherwise builds the Warpzone with brush as Trigger, sets classname=trigger_warpzone, Solid.Trigger, and a Touch handler that warps any non-freed solid toucher; registers it.  _(Base: spawnfunc(trigger_warpzone) brush path)_ **Gap:** Faithful; touch filter (not freed, Solid!=Not) is a reasonable analogue of QC's toucher checks.
- **`SpawnTriggerFor(wz, mins, maxs) (private)`** [IMPLEMENTED] - If a facade exists, spawns a trigger entity, sets classname/Solid.Trigger/MoveType.None, size and origin, and a Touch handler that warps qualifying touchers; assigns it to wz.Trigger. No-op headless.  _(Base: the warpzone brush trigger setup)_ **Gap:** Faithful; headless path simply has no trigger (POJO transform still usable).
- **`PlacePortoPortal(origin, surfaceNormal, isInPortal, portalId, owner)`** [IMPLEMENTED] - Builds a Warpzone with forward=surface normal (FixedVecToAngles), names it porto_{id}_{in|out}, creates a 96^3 touch volume, registers it. In-portals are stashed in _pendingPorto keyed by (owner,id); when the matching out-portal arrives, LinkPair joins them two-way and the pending entry is removed.  _(Base: Portal_SpawnInPortalAtTrace / Portal_SpawnOutPortalAtTrace)_ **Gap:** Functionally present; QC has additional Porto lifetime/expiry, ownership, and effect logic not modeled here (just placement+link).
- **`LinkPair(a, b)`** [IMPLEMENTED] - Sets both zones' OutOrigin/OutAngles to each other's IN plane and builds both transforms (a→b and b→a) so the portal is walkable both ways.  _(Base: two-way WarpZone_SetUp (this.enemy/e2.enemy))_ **Gap:** Faithful for symmetric two-way portals.
- **`OnMapEntity(e)`** [IMPLEMENTED] - Dispatch bridge: routes trigger_warpzone_position edicts to AddMapPosition, everything else to AddMapZone.  _(Base: spawnfunc dispatch for trigger_warpzone / _position)_ **Gap:** Faithful registration bridge (static Sink → instance manager).
- **`AddMapZone(brush)`** [IMPLEMENTED] - If a facade exists and the brush has a Model, calls SetModel to resolve '*N' inline-model bounds and surfaces; queues the brush in _pendingZones for deferred init.  _(Base: spawnfunc(trigger_warpzone) registration)_ **Gap:** Plane/link deferred (correct, mirrors QC StartFrame deferral).
- **`AddMapPosition(pos)`** [IMPLEMENTED] - Registers the position entity in MapMover's index (so it's findable by targetname) and queues it in _pendingPositions.  _(Base: spawnfunc(trigger_warpzone_position) registration)_ **Gap:** Faithful.
- **`InitMapZones()`** [IMPLEMENTED] - Deferred init pass: for each pending zone, resolves its aiment (ResolveAiment) and calls SpawnFromBrush to derive the plane + build the trigger; then Link() pairs everything; clears both pending lists.  _(Base: WarpZone_StartFrame init pass)_ **Gap:** Faithful single-shot deferred init.
- **`ResolveAiment(zone) (private)`** [IMPLEMENTED] - Mechanism 1: if zone has a KillTarget, finds that target_position by targetname and clears KillTarget (QC clears after). Mechanism 2: a trigger_warpzone_position whose .target names this zone. Returns null to orient purely from the brush plane.  _(Base: WarpZone_InitStep_FindOriginTarget + trigger_warpzone_position reverse-attach)_ **Gap:** Faithful; covers both QC orientation-entity mechanisms.
- **`WarpzoneSpawns.Sink / TriggerWarpzoneSetup / TriggerWarpzonePositionSetup (static)`** [IMPLEMENTED] - Static spawnfunc entry points: set the classname and invoke the Sink delegate (wired by host Boot to WarpzoneManager.OnMapEntity); null Sink (tests) is a no-op.  _(Base: spawnfunc(trigger_warpzone) / spawnfunc(trigger_warpzone_position))_ **Gap:** Faithful static→instance bridge, mirroring Porto.PortalSpawner.

**Missing vs Base:** Relative to Base lib/warpzone, the following have no port counterpart in this file: (a) the warpzone trace/radius recursion — QC's WarpZone_TraceBox/TraceLine/tracetoss recursing across seams and the WarpZone_Accumulator/WarpZone_Trace_recurse chain; GetAffine's XML references a WarpzoneTransformChain accumulator that is not declared here (only the per-zone affine decomposition exists). (b) Per-frame solid sweep teleport — QC also teleports via a trace each frame for fast movers (anti-tunneling); here warp is purely Touch-driven. (c) Camera/view (warpzone_camera / trigger_warpzone_reconnect) and the entire client seamless-render path (drawing the OUT view through the IN surface, recursive portal rendering) — explicitly declared a client concern and omitted. (d) Roll preservation through the inverse transform (AnglesOf comment: 'roll from up not modeled'). (e) warpzone teleport effects/sound (tfldeath/teleport sound, particle effect) that QC WarpZone_Teleport can trigger. (f) Plane-side crossing detection (QC checks the entity actually crossed from the front side of the IN plane this frame) is replaced by a velocity-direction heuristic.

**Notes:** Verified that QMath.AngleVectors, FixedVecToAngles, FixedVecToAngles2, Normalize (QMath.cs lines 31-168) and MapMover.UseTargets/FindFirstByTargetName/IndexRegister (MapObjectsCommon.cs lines 217-344) are real implementations, so the transform math and target-firing are genuinely backed, not stubbed. The transform uses an explicit forward/right/up basis instead of QC's AnglesTransform vector-encoding, which is mathematically equivalent for position/velocity/forward-angle but is the reason roll cannot be carried through Inverse(). The 180-degree flip is correct (inFwd→−outFwd, inRight→−outRight, inUp→outUp). The directional gate (Teleport line 190) only blocks when Dot(velocity, InForward) > 0 AND speed^2 > 1 — a near-stationary entity is allowed to warp, which differs subtly from QC's strict crossing test. Api.Services null-guards mean the whole subsystem degrades to a pure-POJO transform path in headless tests.


## port: trace/surface/radius

The port has a REAL warpzone-aware trace and radius query that genuinely FOLLOW THROUGH portals — not mere detection. WarpzoneTrace.TraceWarpzone is a true iterative recursion: on a crossing it transforms the remaining segment through the portal, nudges past the exit plane, re-sweeps on the far side, and accumulates the transform; the combat consumers (FireBullet/FireRailgunBullet/SetupShot/RadiusDamage LOS) re-warp dir/end into the far frame and continue, so bullets/rails/splash actually hit far-side victims. The central fidelity gap is the CROSSING-DETECTION mechanism: QC does WarpZone_MakeAllSolid + a tracebox that HITS the now-solid zone and reads trace_endpos; the port instead detects the crossing ANALYTICALLY (segment centerline vs the zone's infinite IN plane, then an AABB containment test of the crossing point inside the padded trigger box). This is faithful for point traces against planar zones but diverges for box hulls straddling the plane, oblique/curved triggers, and the exact step-back behaviour. getsurface*/BSP queries are implemented and consumed ONLY at warpzone SETUP (plane auto-derivation), never during the runtime trace recursion. Client portal SubViewport render is entirely out of scope. No WarpZone_TraceToss port exists.

- **`WarpzoneTrace.TraceWarpzone (core recursion)`** [IMPLEMENTED] - Iterative warpzone-aware sweep: plain Trace from segStart→segEnd; if FindCrossedZone reports a portal crossing on the swept segment, Append the zone transform, warp the crossing point + segEnd through it, nudge segStart 0.03125qu past the exit plane along the post-warp direction, and re-sweep; loops up to the 16-zone guard. Returns the final TraceResult + accumulated start→final WarpzoneTransformChain + ZonesCrossed.  _(Base: WarpZone_TraceBox_ThroughZone (lib/warpzone/common.qc) — the i=16 loop, the WarpZone_MakeAllSolid hit, org/end = TransformOrigin(wz,...), the step-back trace)_ **Gap:** FOLLOWS THROUGH correctly (genuine re-sweep on the far side), but the crossing is detected ANALYTICALLY rather than via MakeAllSolid + a tracebox that hits the solid zone and reads trace_endpos. Consequences: (1) box traces (mins/maxs != 0) only test the segment CENTERLINE against the plane — a box whose corner straddles the plane while the centerline misses is not detected, unlike QC's solid-brush tracebox; (2) the 0.03125qu nudge is the port's own step-back, not QC's 32qu back-trace, so re-detection guarding differs in degenerate near-coplanar chains; (3) the final TraceResult's EndPos/PlaneNormal are in the FINAL frame only — there is no per-segment hit accounting, fine for the consumers but not a general WarpZone trace.
- **`WarpzoneTrace.TraceLineWarpzone (ITraceService ext)`** [IMPLEMENTED] - Point-trace overload: forwards to TraceBoxWarpzone with zero mins/maxs, resolving AmbientManager. Used by SetupShot trueaim, FireBullet, FireRailgunBullet, and the RadiusDamage LOS sample trace.  _(Base: WarpZone_TraceLine(org, end, nomonsters, forent))_ **Gap:** Same analytic-detection caveat as the core. Returns WarpzoneTraceResult, not a plain trace_t, so callers must opt into the transform; QC's global trace_* + WarpZone_trace_transform side-channel is replaced by an explicit return. nomonsters/forent map to MoveFilter/ignore but the per-segment MoveFilter is unchanged across crossings (QC re-issues the same flags too, so this matches).
- **`WarpzoneTrace.TraceBoxWarpzone (ITraceService ext)`** [IMPLEMENTED] - Box-sweep overload: resolves AmbientManager and calls TraceWarpzone with the supplied mins/maxs.  _(Base: WarpZone_TraceBox(org, mi, ma, end, nomonsters, forent))_ **Gap:** Box hull is NOT honoured in crossing detection — FindCrossedZone tests only the centerline + crossing-POINT AABB containment, so the box extent never widens the portal-entry test the way a real tracebox-vs-solid-zone would. No live consumer currently fires a non-zero-hull warpzone tracebox (all callers use TraceLineWarpzone), so the gap is latent.
- **`WarpzoneTrace.FindCrossedZone`** [PARTIAL] - Finds the nearest linked zone whose IN plane the segment crosses from the front (Dot(InForward, seg) < 0), with the segment-fraction crossing point inside the trigger bounds; skips the just-exited zone by reference to prevent immediate re-entry.  _(Base: implicit in WarpZone_TraceBox (the MakeAllSolid'd zone the tracebox hits) + the trace_ent==wz / step-back guard)_ **Gap:** Pure ray-vs-infinite-plane + AABB membership. Misses: oblique box-corner entry; a non-planar/curved trigger volume (QC's solid brush would still be hit); a zone entered from grazing angles where denom≈0 is silently skipped. The ignoreZone guard is reference-based (one zone) vs QC's positional step-back, so an A→B→A ping-pong relies on the front-side dot rather than geometry.
- **`WarpzoneTrace.WithinTriggerBounds`** [PARTIAL] - Tests the crossing point against the trigger entity's world AABB padded ±1qu; falls back to a 256qu slab around the plane center when the zone has no trigger entity (headless tests).  _(Base: the trigger_warpzone brush volume containment in the MakeAllSolid tracebox)_ **Gap:** Uses the trigger's AXIS-ALIGNED bounding box, not the actual (possibly rotated/angled) brush faces, so a slanted portal mouth accepts crossings in the AABB corners that QC's brush would reject. The 256qu test-only fallback is non-physical (would accept spurious crossings if ever hit in production with a trigger-less zone).
- **`WarpzoneTransformChain.Append / TransformPoint / TransformDirection / Identity`** [IMPLEMENTED] - Accumulates the affine portal map p→R·p+shift; Append composes the freshly-crossed zone as the OUTER transform (R = Rwz·Rthis, shift = Rwz·thisShift + wshift); TransformPoint/Direction apply the rotation rows ± shift.  _(Base: WarpZone_Accumulator_Clear/_Add + AnglesTransform_Multiply / _Multiply_GetPostShift (common.qc))_ **Gap:** Faithful affine equivalent; verified by Chain_Append_Matches_The_Zone_Transform test. Uses explicit forward/right/up basis instead of QC's AnglesTransform vector encoding — mathematically identical for position/velocity, but ROLL is not carried (see WarpzoneTransform.AnglesOf), so an accumulated VIEW-angle through a rolled portal chain would drift; irrelevant to the trace/radius math which only uses point+direction.
- **`WarpzoneRadiusQuery.FindRadiusWarpzone`** [IMPLEMENTED] - Clears results; no-manager fast path runs plain FindInRadius + BadEntity/trigger_warpzone filter with identity transform; otherwise seeds Recurse with a de-dup HashSet (nearest path wins).  _(Base: WarpZone_FindRadius(org, rad, ...) (common.qc))_ **Gap:** Faithful entry shape. Returns explicit WarpzoneRadiusHit list (entity + ToBlastFrame + LocalBlastOrigin) instead of QC's per-entity .warpzone_transform/.warpzone_shift side-effect fields — consumer WeaponSplash reads LocalBlastOrigin, equivalent. De-dup is by entity reference; QC's WarpZone_findradius_dist nearest-wins bookkeeping is approximated by first-seen in recursion order, which is BFS-nearest only because radius shrinks per hop, not a true distance sort.
- **`WarpzoneRadiusQuery.Recurse`** [PARTIAL] - Collects local FindInRadius entities (minus blacklist + zones) tagging each with the running transform/origin; then for each linked zone whose IN-plane center is within the remaining radius AND the blast is on the FRONT side, warps the origin through, reduces radius by the travelled distance clamped to [0, rad-8], appends the transform, and recurses (depth-capped at 16).  _(Base: WarpZone_FindRadius_Recurse (common.qc) — the rad-8 clamp, the per-zone traceline LOS, the FOREACH_ENTITY_RADIUS loop)_ **Gap:** Two deliberate substitutions vs QC, both flagged in comments: (1) distance to a zone is measured to the IN-plane ORIGIN (zone center), not via the trigger box/nearest mouth point, so a large off-center portal is gated by its center distance and may under- or over-reach; (2) the QC per-zone LOS traceline that discards wrong-side/occluded zones is replaced by a pure front-side dot test (Dot(org-InOrigin, InForward) >= 0) — same outcome for the simple two-room case but it does NOT check that the blast actually has line-of-sight to the portal mouth (a wall between blast and portal does not block propagation here).
- **`WarpzoneRadiusQuery.IsBadEntity`** [IMPLEMENTED] - Classname blacklist (weaponentity, waypoints, info_/target_ helpers, spawnfunc, view models, empty) + IsFreed guard, applied on both the fast and recursive paths.  _(Base: WarpZoneLib_BadEntity (common.qc:562))_ **Gap:** Static classname switch + prefix checks; faithful to the listed set. Does not model WarpZoneLib_BadEntity's any-dynamic checks beyond classname (QC's exact list may include a couple more spawn helpers), but covers the combat-relevant ones.
- **`WarpzoneRadiusHit (struct) / WarpzoneTraceResult (struct)`** [IMPLEMENTED] - Carry the explicit per-hit transform back to the blast frame + the portal-shifted local blast origin (radius), and the final trace + accumulated chain + zones-crossed (trace).  _(Base: the .warpzone_transform/.warpzone_shift entity fields + global WarpZone_trace_transform)_ **Gap:** Design substitution (explicit return vs QC global/entity side-channel), not a behavioural gap.
- **`TraceServiceWarpzoneBridge.Publish + TraceService.SetWarpzoneManager`** [IMPLEMENTED] - Engine-side bridge: forwards this world's WarpzoneManager to the Common-side WarpzoneTrace.AmbientManager so the Common trace extensions/radius query can resolve g_warpzones without Engine→Common reference inversion. Called from GameWorld.Boot after InitMapZones; null clears it.  _(Base: WarpZone_MakeAllSolid making the world's zones visible to the trace + the g_warpzones global)_ **Gap:** AmbientManager is a STATIC mutable global (one world at a time) — fine for a single listen-server match, but it makes the warpzone trace path non-reentrant across worlds and is the implicit dependency the otherwise-stateless Common functions carry. Publishing is a plain assignment, not the per-trace MakeAllSolid/MakeAllOther bracket QC uses, so there is no transient solidification window.
- **`TraceService.Trace / TraceUnlocked / TraceBrushVsBrush (the underlying sweep)`** [IMPLEMENTED] - The SAT box-vs-brush sweep (port of Collision_TraceBrushBrushFloat) that every warpzone segment re-invokes. Returns TraceResult with Fraction/EndPos/PlaneNormal/Ent/DpHitContents/SurfaceFlags/Texture; honours hit-contents mask, ConcurrencyGate, brush-model entity clip.  _(Base: SV_TraceBox / traceline / tracebox — the plain trace WarpZone_TraceBox issues per segment)_ **Gap:** The plain trace itself is mature and is the workhorse each warpzone segment calls; the warpzone layer adds nothing to its fidelity. The only warpzone-relevant interaction is that the recursion reads tr.EndPos (centerline endpoint) to bound where a crossing may occur — so a wall in front of the portal correctly blocks the bullet (the sweep stops short, FindCrossedZone sees no crossing up to EndPos). That LOS-through-solid blocking IS correct for the trace path (unlike the radius path's missing LOS).
- **`SurfaceService.GetSurfaceTexture / NumTriangles / Triangle / Point (+ NumPoints/Normal/NearPoint/ClippedPoint/PointAttribute)`** [IMPLEMENTED] - getsurface* mesh-query builtins resolving an entity model's ModelSurface list to world-space geometry (entity FLU matrix, scale 1). The four bolded ones are exactly what WarpzoneManager.DerivePlaneFromBrush iterates to area-weight the zone's IN plane normal+centroid.  _(Base: VM_getsurfacetexture / VM_getsurfacenumtriangles / VM_getsurfacetriangle / VM_getsurfacepoint (prvm_cmds.c) used by WarpZone_InitStep_UpdateTransform)_ **Gap:** Consumed ONLY at warpzone SETUP (plane auto-derivation), never during the runtime trace/radius recursion — so the recon's framing of 'BSP queries warpzones depend on' applies to orientation inference, not traversal. Faithful for the brush-plane derivation; S/T tangent axes are synthesized (not BSP-stored) and BSP face normals come from ModelSurface.PlaneNormal. GetSurfaceTexture('') is the loop terminator, matching QC's `if(!tex) break`.

**Missing vs Base:** No WarpZone_TraceToss port — QC has WarpZone_TraceToss (warpzone-aware ballistic/gravity trace for grenades/tossed projectiles); the port has no equivalent, so a thrown grenade does NOT arc through a portal (it would teleport via the trigger touch instead, losing the predicted-path through-portal toss). The recon (recon-A5.md:14) lists WarpZone_TraceToss among the missing extensions. Also missing on the radius path: the per-zone LOS traceline that QC's WarpZone_FindRadius_Recurse uses to confirm the blast can actually see the portal mouth (replaced by a front-side dot only — a wall between blast and portal does not block splash propagation through the seam). On the trace path, box-hull-aware crossing detection is absent (centerline-only). Entirely out of scope this pass: the CLIENT portal SubViewport render (portals render opaque), and post-teleport input-angle rotation for the local view. WarpZone_MakeAllSolid/MakeAllOther's transient world-solidification bracket has no port analogue — replaced by analytic detection, which is the intended design but means any QC behaviour that depended on the zone being a real solid mid-trace (e.g. a third entity's trace hitting the temporarily-solid zone) is not reproduced.

**Notes:** CRITICAL ASSESSMENT — does the trace FOLLOW THROUGH or just DETECT? It genuinely follows through. Evidence: (1) WarpzoneTrace.TraceWarpzone (WarpzoneRadiusQuery.cs:172-217) re-issues trace.Trace from segStart=exitPoint+fwd*0.03125 to the warped segEnd after every crossing — a real second sweep on the far side, not a flag; (2) consumers act on it: FireBullet (WeaponFiring.cs:233-242) and FireRailgunBullet (:358-367) rotate dir and translate end into the far frame and continue their penetration/pierce loops, so damage lands on far-side entities; SetupShot (:141-142) trueaims through the portal; WeaponSplash (:76, :161) finds far-side victims and traces LOS from the portal-shifted origin. So this is full combat traversal, not detection-only. The honest limits are: (a) crossing detection is analytic (plane + AABB), so it is exact for point traces against planar/axis-aligned zones but diverges from QC's solid-brush tracebox for box hulls, oblique entry, and non-AABB triggers; (b) the radius path drops QC's portal-mouth LOS trace in favour of a front-side dot, so splash can 'leak' through a portal even when a wall occludes the mouth; (c) no TraceToss, so grenades don't pre-arc through portals. Test coverage (WarpzoneTraceTests.cs) proves one-portal crossing + transform accumulation, the 16-zone guard terminates, and a victim reachable ONLY through the seam is hit (FindRadius_Reaches_A_Victim_Only_Through_A_Portal) — but all trace tests use a MissTrace that never reports a wall hit, so the wall-blocks-the-portal interaction and box-hull straddle cases are UNTESTED. Files: WarpzoneRadiusQuery.cs (recursion core), MapObjects/Warpzone.cs (transform + manager + getsurface-based plane derivation), TraceServiceWarpzoneExt.cs (bridge), TraceService.cs:62-63 (SetWarpzoneManager) + 120-175/372-539 (underlying sweep), SurfaceService.cs (getsurface*), WeaponFiring.cs / WeaponSplash.cs (consumers).


## port: mapobjects/registry/spawn

Warpzones are fully wired into the port's spawn/registration path, but the implementation lives almost entirely in a dedicated file, Warpzone.cs, NOT in the five files I was asked to grep — those files only REFERENCE warpzones (as a cross-cutting "out of scope for this concern" note or as the pattern-mirror for other sinks). MapObjectsRegistry.cs registers two warpzone classnames (trigger_warpzone, trigger_warpzone_position) whose stateless spawnfuncs (WarpzoneSpawns.TriggerWarpzoneSetup / TriggerWarpzonePositionSetup) tag the classname and route the edict through the static WarpzoneSpawns.Sink bridge (host-wired in GameWorld.Boot) to the per-match WarpzoneManager.OnMapEntity. Registration is deferred: brushes are stashed in _pendingZones/_pendingPositions, then GameWorld finalizes them in a single post-spawn pass (Warpzones.InitMapZones, mirroring QC WarpZone_StartFrame). For each zone, the IN plane is derived from the brush geometry via getsurface* (DerivePlaneFromBrush), an explicit orientation entity is resolved (killtarget target_position OR a trigger_warpzone_position whose .target names the zone), the touch volume is built, and the pairs are linked by targetname (Link). The brush volume IS parsed into a plane+target: SpawnFromBrush derives (origin, angles) and sets brush.Solid=Trigger with brush.Touch = teleport closure. Warpzone touch is wired through the SAME per-entity Entity.Touch field that every trigger in Triggers.cs uses (this_.Touch = ...), so it flows through the engine's normal trigger/touch dispatch — there is no separate warpzone touch path. The teleport itself (WarpzoneManager.Teleport) is a momentum-preserving transform via WarpzoneTransform (rotate origin/velocity/angles/avelocity), distinct from the player teleporter path in Teleporters.cs. The two subsystems intersect only in a documented GAP: Teleporters.TeleportPlayer explicitly omits "the warpzone post-teleport callback" (a player teleported by trigger_teleport does not re-run warpzone touch logic). Porto-weapon portals reuse the same WarpzoneManager (PlacePortoPortal/LinkPair) — the registry comments cite Porto.PortalSpawner as the sink-bridge pattern. NOT present anywhere in the port: trigger_warpzone_reconnect, misc_warpzone_position, and func_camera (zero matches across src). [T45] trace/radius-damage recursion through linked portals IS wired (TraceService.SetWarpzoneManager).

- **`MapObjectsRegistry (ctor/static registration block)`** [IMPLEMENTED] - SpawnFuncs.Register("trigger_warpzone", WarpzoneSpawns.TriggerWarpzoneSetup) and SpawnFuncs.Register("trigger_warpzone_position", WarpzoneSpawns.TriggerWarpzonePositionSetup) at MapObjectsRegistry.cs lines 108-109. A comment block (103-107) documents that plane derivation + pair linking are deferred to GameWorld.Warpzones.InitMapZones() (QC WarpZone_StartFrame) and that the stateless spawnfunc reaches the instance manager via the WarpzoneSpawns.Sink bridge (mirrors Porto.PortalSpawner).  _(Base: lib/warpzone/server.qc spawnfunc(trigger_warpzone) / spawnfunc(trigger_warpzone_position) registration)_ **Gap:** Only two warpzone classnames are registered. Base classnames trigger_warpzone_reconnect (forces re-link), and func_camera / misc_warpzone_position (non-seamless camera portals) are NOT registered anywhere in the port.
- **`WarpzoneSpawns.TriggerWarpzoneSetup`** [IMPLEMENTED] - Stateless spawnfunc (Warpzone.cs:449): sets e.ClassName="trigger_warpzone" then Sink?.Invoke(e). Null sink (unit tests / no manager) is a silent no-op.  _(Base: QC spawnfunc(trigger_warpzone))_ **Gap:** None for registration; all real work is deferred to the manager + InitMapZones.
- **`WarpzoneSpawns.TriggerWarpzonePositionSetup`** [IMPLEMENTED] - Stateless spawnfunc (Warpzone.cs:450): sets e.ClassName="trigger_warpzone_position" then Sink?.Invoke(e).  _(Base: QC spawnfunc(trigger_warpzone_position))_ **Gap:** None for registration.
- **`WarpzoneSpawns.Sink (static bridge) + GameWorld.Boot wiring`** [IMPLEMENTED] - static Action<Entity>? Sink (Warpzone.cs:447). GameWorld.Boot (GameWorld.cs:393) wires WarpzoneSpawns.Sink = Warpzones.OnMapEntity so a map's warpzone brushes register on THIS match's manager. Same Boot also wires Porto.PortalSpawner -> Warpzones.PlacePortoPortal (cs:388-389).  _(Base: QC IL_PUSH(g_warpzones) global accumulation in spawnfunc; the singleton global that the spawnfunc writes to.)_ **Gap:** Static mutable sink (per-process), acceptable for single-match host; would need care for multiple concurrent GameWorlds.
- **`WarpzoneManager.OnMapEntity`** [IMPLEMENTED] - Sink target (Warpzone.cs:376): dispatches by classname — trigger_warpzone_position -> AddMapPosition, else AddMapZone. Registration only; plane/link deferred.  _(Base: the per-classname branch the QC spawnfunc bodies take)_ **Gap:** None.
- **`WarpzoneManager.AddMapZone`** [IMPLEMENTED] - Warpzone.cs:384: if a facade is wired and the brush has a Model, SetModel(brush, brush.Model) to resolve the inline "*N" bounds (touch volume) + findable getsurface* surfaces; then _pendingZones.Add(brush). Plane/link finalized later in InitMapZones.  _(Base: QC trigger_warpzone brush model resolution (setmodel) prior to WarpZone_StartFrame)_ **Gap:** None substantive.
- **`WarpzoneManager.AddMapPosition`** [IMPLEMENTED] - Warpzone.cs:393: MapMover.IndexRegister(pos) (so it is findable by targetname) + _pendingPositions.Add(pos) for InitMapZones to attach as a zone's aiment.  _(Base: QC trigger_warpzone_position registration / targetname index)_ **Gap:** None.
- **`WarpzoneManager.InitMapZones`** [IMPLEMENTED] - Warpzone.cs:406: deferred post-spawn pass (QC WarpZone_StartFrame). For each pending zone: ResolveAiment, then SpawnFromBrush(zone, TargetName, Target, aiment); then Link(); clears both pending lists. Called by GameWorld.Boot at cs:552 after RunPostSpawn.  _(Base: QC WarpZone_StartFrame / the frame-1 InitStep_* sequence)_ **Gap:** Single one-shot pass; no support for runtime re-init (trigger_warpzone_reconnect not modeled).
- **`WarpzoneManager.ResolveAiment`** [IMPLEMENTED] - Warpzone.cs:420: finds the explicit-orientation entity for a zone — (1) killtarget naming a target_position (via MapMover.FindFirstByTargetName, then clears killtarget), else (2) a pending trigger_warpzone_position whose .target names this zone's TargetName. Returns null to orient purely from brush plane.  _(Base: QC WarpZone_InitStep_FindOriginTarget + the trigger_warpzone_position reverse-attach)_ **Gap:** None substantive.
- **`WarpzoneManager.DerivePlaneFromBrush`** [IMPLEMENTED] - Warpzone.cs:229: parses the BRUSH VOLUME into a PLANE — area-weighted average of every non-trigger triangle's normal+centroid via ISurfaceService getsurface* (GetSurfaceTexture/NumTriangles/Triangle/Point), skipping textures/common/trigger. Detects non-planar (norm length < 0.99 -> area=0). With an aiment (target_position): seeds origin/angles, projects onto plane, flips normal to face out, keeps roll (FixedVecToAngles2); otherwise origin=centroid, angles=FixedVecToAngles(norm). Returns ok=false when no usable surface (QC error).  _(Base: QC WarpZone_InitStep_UpdateTransform (getsurface* auto-orientation))_ **Gap:** Requires Api.Surfaces (BspSurfaceBuilder.BuildAndAttach in Boot) to be populated; returns false with no facade. Roll from up not fully modeled in some downstream helpers (AnglesOf).
- **`WarpzoneManager.SpawnFromBrush`** [IMPLEMENTED] - Warpzone.cs:299: derives plane via DerivePlaneFromBrush; if ok, creates Warpzone{InOrigin,InAngles,TargetName,Target,Trigger=brush}, sets brush.ClassName="trigger_warpzone", brush.Solid=Trigger, and brush.Touch = closure that calls Teleport(other, wz) for non-freed solid touchers; Add(wz). Returns null when plane can't be inferred. THIS is where the brush volume becomes a plane+target+touch-volume.  _(Base: QC trigger_warpzone setup (WarpZoneLib_ExactTrigger_Init + WarpZone_SetUp))_ **Gap:** Touch volume is the brush AABB/inline model bbox, not the exact-trigger clipped surface (the requested files explicitly note 'warpzone exact-trigger clipping' is out of scope — see Triggers.cs:19, MapObjectsCommon.cs:122).
- **`WarpzoneManager.SpawnTriggerFor`** [IMPLEMENTED] - Warpzone.cs:311: for non-brush (parametric/Porto) zones, spawns a fresh trigger entity (Solid.Trigger, MoveType.None, SetSize/SetOrigin) and sets its .Touch closure to Teleport(other, wz). Used by Spawn() and PlacePortoPortal().  _(Base: QC warpzone brush trigger creation)_ **Gap:** None for the parametric path.
- **`WarpzoneManager.Link / LinkOneWay / FindByTargetName`** [IMPLEMENTED] - Warpzone.cs:139/162/169: two-pass pair linking by .target/.targetname. Forward pass links each zone to its named partner; reverse pass links a zone that carries no .target but is targeted by another (asymmetric maps). Honors sv_warpzone_allow_selftarget (default 0 -> never self-link). LinkOneWay sets OutOrigin/OutAngles and builds the WarpzoneTransform.  _(Base: QC WarpZone_InitStep_FindTarget / the this.enemy<->e2.enemy two-way wiring)_ **Gap:** None substantive; this is a faithful port including the selftarget cvar.
- **`WarpzoneManager.Teleport`** [IMPLEMENTED] - Warpzone.cs:186: the seamless crossing. Bails if unlinked or if the entity is moving back OUT of the plane (Dot(Velocity, InForward) > 0 with speed). Transforms Origin/Velocity/Angles/AVelocity via WarpzoneTransform, sets origin (Api.Entities.SetOrigin), cancels interpolation (OldOrigin=new), and fires the zone's targets via MapMover.UseTargets when the trigger has a .target (QC WarpZone_Touch SUB_UseTargets).  _(Base: QC WarpZone_Teleport + WarpZone_Touch)_ **Gap:** No exact-trigger surface clipping; no CSQC seamless-view rendering (client concern, intentionally out of scope).
- **`Warpzone touch dispatch wiring (Triggers.cs touch model)`** [IMPLEMENTED] - Warpzone touch is set on the per-entity Entity.Touch delegate field (SpawnFromBrush:306, SpawnTriggerFor:320) — the SAME field every trigger in Triggers.cs uses (e.g. this_.Touch = MultiTouch/HurtTouch/GravityTouch). So warpzone touch flows through the engine's normal trigger/touch dispatch with no special-casing.  _(Base: QC .touch on the warpzone trigger edict, invoked by the physics touch pass)_ **Gap:** None — unified with the standard trigger touch path.
- **`Teleporters.TeleportPlayer (cross-link to warpzones)`** [PARTIAL] - Teleporters.cs:256: the trigger_teleport player relocation (sound debounce, origin/angles/velocity, clear ground, telefrag, kill-credit window). Its doc (cs:253) explicitly lists the warpzone post-teleport callback as out of scope.  _(Base: QC TeleportPlayer (which invokes WarpZone_PostTeleport_Callback))_ **Gap:** GAP: a player teleported by a trigger_teleport does not run the warpzone post-teleport callback (projectile re-owning / warpzone bookkeeping). The teleporter and warpzone subsystems are otherwise independent code paths.
- **`WarpzoneManager.PlacePortoPortal / LinkPair (Porto weapon)`** [IMPLEMENTED] - Warpzone.cs:335/356: realises a Porto-weapon portal as a warpzone (forward = wall normal); holds the in-portal pending until the out-portal lands, then LinkPair two-way. Host wires Porto.PortalSpawner -> this in Boot. Cited by the registry as the pattern-mirror for WarpzoneSpawns.Sink.  _(Base: QC Portal_SpawnIn/OutPortalAtTrace)_ **Gap:** Not a map-spawn entity; included because the registry comments reference it as the sink-bridge precedent.

**Missing vs Base:** trigger_warpzone_reconnect (forces a re-link / re-derivation at runtime) is absent — InitMapZones is a one-shot Boot pass with no re-init entry point. func_camera and misc_warpzone_position (the non-seamless camera-portal family from lib/warpzone) are absent entirely (zero grep matches). The warpzone exact-trigger surface clipping (WarpZoneLib_ExactTrigger using the real brush surface rather than the AABB) is explicitly declared out of scope in Triggers.cs:19 and MapObjectsCommon.cs:122 — touch uses the inline-model AABB. The CSQC seamless-view rendering and trigger_common_write/read networking are intentionally client-only and not ported (MapObjectsCommon.cs:122-123). The teleporter-side warpzone post-teleport callback is intentionally omitted (Teleporters.cs:253). Within the five FILES I was asked to read, there is essentially NO warpzone IMPLEMENTATION at all — they contain only registration (MapObjectsRegistry.cs) and out-of-scope/pattern-reference comments; the entire warpzone implementation lives in the sibling file Warpzone.cs plus the GameWorld.cs host wiring.

**Notes:** Key file paths: C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs (lines 103-109 register the two classnames; RunPostSpawn at 545). C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs (the ENTIRE warpzone implementation: WarpzoneTransform struct, Warpzone class, WarpzoneManager, WarpzoneSpawns static bridge — this is the file the task's real subject lives in, even though it was not in the requested grep list). C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Server/GameWorld.cs (Warpzones property at 186; Boot wires WarpzoneSpawns.Sink=Warpzones.OnMapEntity at 393 and Porto.PortalSpawner at 388; InitMapZones called at 552; TraceImpl.SetWarpzoneManager at 558). Of the requested files: MapObjectsRegistry.cs has the only real warpzone content (registration); MapObjectsCommon.cs (line 122) only notes warpzones+CSQC trigger networking as still client/networking-only; MapVolumes.cs only references WarpZoneLib_ExactTrigger_Init/BoxTouchesBox as the analogue naming for func_conveyor/ladder trigger init (no warpzone code); TargetUtilities.cs line 20 references WarpzoneSpawns.Sink only as the pattern-mirror for its own changelevel seam delegate; Triggers.cs line 19 only lists 'warpzone exact-trigger clipping' as out of scope; Teleporters.cs lines 12 & 253 list warpzones / the warpzone post-teleport callback as out of scope. Spawn order is correct: spawnfuncs register edicts during SpawnMapEntities -> RunPostSpawn -> InitMapZones (derive planes + link) -> SetWarpzoneManager on trace service. Touch model verified: warpzone uses the same Entity.Touch delegate as all Triggers.cs triggers, so it shares the engine touch dispatch.


## port: integration

Warpzone integration is sharply bimodal across the port. The COMBAT-TRAVERSAL seam (weapon hitscan/beam/projectile traces and radius damage) is genuinely warpzone-aware: WeaponFiring.SetupShot/FireBullet/FireRailgunBullet call TraceLineWarpzone and carry the accumulated portal transform into the far-side frame, and WeaponSplash.RadiusDamage calls WarpzoneRadiusQuery.FindRadiusWarpzone + warpzone-aware LOS, measuring each victim's distance/falloff from the portal-shifted blast origin. Porto realises its portals AS warpzones via PortalSpawner -> WarpzoneManager.PlacePortoPortal. The underlying helpers these call (WarpzoneTrace.TraceWarpzone, WarpzoneRadiusQuery, WarpzoneTransform, DerivePlaneFromBrush) are full faithful implementations with the 16-zone guard, NOT stubs. By contrast, every AI/navigation integration is portal-BLIND: MonsterAI (LOS targeting, find-target radius, wander, melee/ranged traces) uses only plain Api.Trace.Trace + Api.Entities.FindInRadius; turret hitscan (TurretCombat.FireBullet, TurretAI aim/acquire) uses plain traces though turret SPLASH inherits warpzone-awareness through WeaponSplash; VehicleBoarding uses plain FindInRadius where QC used WarpZone_FindRadius; the bot Waypoint graph models portals only as a single one-way Teleport-flagged link and never even auto-generates that for trigger_warpzone. QMath and Services are pure support: QMath provides the warpzone angle-transform primitives (FixedVecToAngles), Services declares ISurfaceService (consumed by DerivePlaneFromBrush) and ISoundService.PlayAt — neither does any warpzone routing itself. No integration calls a warpzone helper that is itself a stub; the gaps are call-SITES that omit the warpzone-aware variant, not broken helpers.

- **`WeaponFiring.SetupShot`** [IMPLEMENTED] - Trueaim trace from the eye to find the shot endpoint; nudges muzzle out of walls, recomputes shot dir.  _(Base: W_SetupShot_Dir_ProjectileSize_Range (server/weapons/tracing.qc))_ **Gap:** Warpzone-aware: calls Api.Trace.TraceLineWarpzone (WeaponFiring.cs:141) and, if any zone was crossed (aim.ZonesCrossed>0), keeps the straight aimEnd so the fired projectile re-warps the same portal itself; the shot DIRECTION is computed in the firing frame. Faithful to QC's WarpZone_TraceLine trueaim. Remaining deferred (per its own doc): antilag, the trueaim minrange clamp, accuracy hitplot.
- **`WeaponFiring.FireBullet`** [IMPLEMENTED] - Hitscan bullet sweep with spread, solid penetration multi-hit, falloff, headshot.  _(Base: fireBullet_falloff (server/weapons/tracing.qc))_ **Gap:** Warpzone-aware: per-segment TraceLineWarpzone (line 233); on a crossing (wzr.ZonesCrossed>0) rotates the running aim dir and segment end through wzr.Transform so the penetration loop continues straight on the far side. Plain trace on non-warpzone maps. Deferred: antilag takeback, EFFECT_BULLET tracer.
- **`WeaponFiring.FireRailgunBullet`** [IMPLEMENTED] - Instant beam that pierces every entity along the way, falloff + knockback, headshot.  _(Base: FireRailgunBullet (server/weapons/tracing.qc))_ **Gap:** Warpzone-aware: per-pierce TraceLineWarpzone (line 358); on a crossing transforms dir, end, and the headshot beamStart/beamEnd into the far frame so falloff distance + head box test stay consistent. Deferred: antilag, whoosh sound, beam particle.
- **`WeaponFiring.Headshot / TraceHitsBox`** [IMPLEMENTED] - Ray-vs-head-AABB slab test for the headshot multiplier.  _(Base: Headshot + trace_hits_box (tracing.qc / common/util.qc))_ **Gap:** Pure math, frame-agnostic. Callers pass beam points already transformed into the victim's post-warp frame, so it composes correctly with warpzones. No gap.
- **`WeaponSplash.RadiusDamage`** [IMPLEMENTED] - Radius damage + knockback to everything in radius, linear falloff, multi-sample LOS.  _(Base: RadiusDamageForSource (server/g_damage.qc))_ **Gap:** Warpzone-aware: WarpzoneRadiusQuery.FindRadiusWarpzone (line 76) returns hits tagged with per-victim LocalBlastOrigin; distance/force/LOS all measured from that portal-shifted origin, and the LOS samples use TraceLineWarpzone (line 161). Faithful to WarpZone_FindRadius + WarpZone_findradius_findorigin. Deferred: Damage_DamageInfo blast networking (client render only).
- **`Porto.PlacePortal / PortalSpawner`** [IMPLEMENTED] - Records an in/out portal placement request, realised as a warpzone by the host.  _(Base: Portal_SpawnIn/OutPortalAtTrace (porto.qc + warpzone subsystem))_ **Gap:** The warpzone ENTITY is created via the PortalSpawner delegate -> WarpzoneManager.PlacePortoPortal (GameWorld.cs:389), which is a real implementation that links the in/out pair two-way. Gap is upstream: the surface normal is derived from -velocity, not the actual contact plane (no per-contact plane data headless).
- **`Porto.OnTouch`** [PARTIAL] - Decide portal placement on surface contact; reflect off slick/clip, fail on noimpact.  _(Base: W_Porto_Touch (porto.qc))_ **Gap:** Stubbed surface-flag handling: 'the headless touch can't read [trace surface flags] per-contact, so we treat a world-brush hit as a valid flat surface' (Porto.cs:177). Q3SURFACEFLAG_SLICK reflect and Q3SURFACEFLAG_NOIMPACT fail are declared as consts but never applied; the right_vector reflection about the impact plane is approximated. Not a warpzone-helper stub — a missing trace-flag plumbing dependency.
- **`MonsterAI.ValidTarget`** [MISSING] - Validate a candidate enemy: alive, team, not no-target, line of sight, facing cone.  _(Base: Monster_ValidTarget (sv_monsters.qc))_ **Gap:** Portal-BLIND. LOS uses plain Api.Trace.Trace (MonsterAI.cs:572), not TraceLineWarpzone. Its own doc concedes 'Warpzone PVS ... remain host concerns (deferred)'. A target visible only through a warpzone is rejected; QC would see it via WarpZone_TraceLine.
- **`MonsterAI.FindTarget / EnemyCheck`** [MISSING] - Acquire the closest valid enemy within target range; drop it if it strays.  _(Base: Monster_FindTarget / Monster_Enemy_Check (sv_monsters.qc))_ **Gap:** Portal-BLIND. Uses plain Api.Entities.FindInRadius (line 615) with a Euclidean distance gate; a same-world-space radius cannot reach an enemy on the far side of a portal, and there is no warpzone-aware radius variant. No counterpart to QC reaching through-portal candidates.
- **`MonsterAI.WanderTarget / Move`** [MISSING] - Pick/steer toward a move destination; truncate the path against walls.  _(Base: Monster_WanderTarget / Monster_Move (sv_monsters.qc))_ **Gap:** Portal-BLIND. Path-truncation trace (line 841) and danger checks are plain traces; a monster will treat a warpzone mouth as open space / a wall rather than a traversable portal. No warpzone pathing.
- **`TurretCombat.FireBullet`** [MISSING] - Turret hitscan: spread cone, single trace, damage + knockback on first hit.  _(Base: fireBullet (server/weapons/tracing.qc, turret call path))_ **Gap:** Portal-BLIND. Plain Api.Trace.Trace (TurretCombat.cs:34); file header explicitly lists 'antilag/warpzones' as deferred. Unlike WeaponFiring.FireBullet, no TraceLineWarpzone — a turret cannot shoot a target through a portal.
- **`TurretAI.Aim / FindTarget`** [MISSING] - Turret target acquisition + aim validation trace.  _(Base: turret target scan + turret_validate_target (sv_turrets / turret aim))_ **Gap:** Portal-BLIND. Aim trace (TurretAI.cs:265) and acquisition FindInRadius (lines 340/367) are plain. Turret radius DEATH and FLAC/guided splash DO inherit warpzone-awareness because they route through WeaponSplash.RadiusDamage — so turret splash crosses portals but turret aim/hitscan does not (inconsistent with QC, which uses WarpZone_* throughout).
- **`VehicleBoarding.FindBoardableInRadius`** [PARTIAL] - Find the nearest boardable vehicle within g_vehicles_enter_radius for +use.  _(Base: PlayerUseKey WarpZone_FindRadius loop (server/client.qc))_ **Gap:** Doc cites 'the QC WarpZone_FindRadius loop' but the implementation uses plain Api.Entities.FindInRadius (VehicleBoarding.cs:117) with squared Euclidean distance. Behaviorally fine for the common case (you board what you stand next to), but cannot board a vehicle that is only within range through a portal as QC's WarpZone_FindRadius could.
- **`Waypoint / WaypointNetwork (bot nav)`** [PARTIAL] - Bot navigation graph: nodes, links, A* pathfinding, teleporter/jumppad waypointing.  _(Base: server/bot/default/waypoints.qc + navigation.qc)_ **Gap:** Warpzones modeled only as a generic WaypointFlags.Teleport bit (1<<21) shared with teleporters/jumppads (Waypoint.cs:20) — a single one-way wp00 destination link. GenerateFromEntities auto-waypoints trigger_teleport and trigger_push but NOT trigger_warpzone (lines 328-330), so on an auto-generated graph bots get ZERO warpzone links. A hand-authored .waypoints file with a warpzone box link would load, but nothing in the port creates warpzone links automatically. No portal-transform awareness in cost/path.
- **`QMath.FixedVecToAngles / VecToAngles`** [IMPLEMENTED] - vectoangles / fixedvectoangles angle-from-direction primitives.  _(Base: vectoangles builtin + lib/warpzone/anglestransform.qh fixedvectoangles)_ **Gap:** Support math only. FixedVecToAngles is the port of the warpzone anglestransform macro and is consumed by Warpzone.DerivePlaneFromBrush to orient portal planes. Faithful; does no warpzone routing itself. No gap.
- **`Services.ISurfaceService`** [IMPLEMENTED] - getsurface* builtins: per-surface points/triangles/normals in world space.  _(Base: VM_getsurface* (#434-#439 etc.))_ **Gap:** Interface consumed by Warpzone.DerivePlaneFromBrush for brush->plane auto-derivation. A NullSurfaceService returns empty for all queries; if the host wires it, warpzone brush orientation silently fails (DerivePlaneFromBrush returns ok=false). Host-wiring concern, not a stub in warpzone code.
- **`Services.ISoundService.PlayAt`** [IMPLEMENTED] - Play a sound at a world point with no emitter (impact/blast cues).  _(Base: sound() at a point (DP CSQC wr_impacteffect))_ **Gap:** Used by WeaponSplash impact sounds; no warpzone involvement. Listed only because Services.cs appeared in the grep (the warpzone reference there is a doc comment on getsurface*). No gap.

**Missing vs Base:** No AI/nav touch point ever calls a warpzone-aware query, so several QC behaviors have NO port counterpart: (1) Monster_FindTarget/Monster_ValidTarget in QC see/path to targets through portals (the file's own XML doc admits "Warpzone PVS ... remain host concerns (deferred)") — the port's FindTarget/ValidTarget/EnemyCheck use plain FindInRadius + plain LOS trace, so a monster never aggroes or shoots through a warpzone. (2) Turret acquisition+hitscan (TurretAI.FindTarget/Aim, TurretCombat.FireBullet, PhaserTurret/TeslaTurret/GuidedProjectile traces) are all plain — TurretCombat.cs explicitly lists "antilag/warpzones" as deferred. (3) VehicleBoarding.FindBoardableInRadius reduces QC's WarpZone_FindRadius to a plain FindInRadius (doc still cites the QC WarpZone_FindRadius loop). (4) Bot navigation has no warpzone pathing: Waypoint only has a generic Teleport flag shared with teleporters/jumppads, and Waypoint.GenerateFromEntities auto-waypoints trigger_teleport/trigger_push but NOT trigger_warpzone, so bots cannot route through warpzones at all. (5) Porto's per-contact surface-flag handling (slick reflect / noimpact fail) is stubbed in OnTouch (worldHit heuristic, no per-contact plane), and the in-portal->out-portal reflection of the right_vector is approximated.

**Notes:** Helper-stub audit (the task's explicit ask): NONE of the warpzone helpers invoked by these integrations is a stub. WarpzoneTrace.TraceWarpzone, WarpzoneRadiusQuery.FindRadiusWarpzone/Recurse, WarpzoneTransform (TransformOrigin/Velocity/Angles + chain Append), WarpzoneManager.Link/Teleport/PlacePortoPortal/InitMapZones, and Warpzone.DerivePlaneFromBrush are all complete implementations with the QC 16-zone recursion cap and identity fast-path. The only "soft" spot is WarpzoneRadiusQuery.WithinTriggerBounds, which falls back to a fixed 256qu box for a POJO zone that has no Trigger entity — this path is reachable only in headless tests (constructed zones without a trigger), never for a real map/Porto zone (both set Trigger), so it is not a live gap. ISurfaceService has a NullSurfaceService that returns empty for every query; if the host wires the null surface service, DerivePlaneFromBrush returns ok=false and a map's trigger_warpzone silently fails to orient — that is the one place a (legitimately-null, host-selected) dependency degrades warpzone setup, but it is a host-wiring concern, not a stub in the warpzone code.


## port: tests + server wiring

Warpzones are split across three concerns and tested at three levels. (1) The pure transform/teleport math (WarpzoneTransform, WarpzoneManager.Spawn/Link/Teleport) is well covered by WarpzoneTests. (2) Combat traversal — warpzone-aware hitscan/projectile traces and radius-damage recursion (WarpzoneTrace.TraceWarpzone, WarpzoneRadiusQuery.FindRadiusWarpzone, WarpzoneTransformChain) — is covered analytically by WarpzoneTraceTests using a MissTrace/synthetic world. (3) Map-entity spawn/auto-orient/link from BSP brush geometry (WarpzoneSpawns, WarpzoneManager.InitMapZones/DerivePlaneFromBrush/SpawnFromBrush, getsurface* SurfaceService) is covered by WarpzoneSpawnTests + the warpzone parts of SurfaceQueryTests. Server wiring in GameWorld.Boot is real and complete (Porto portal spawner, map-entity Sink bridge, InitMapZones after entity spawn, SetWarpzoneManager onto the trace service, and the cvar registration). The major UNTESTED gaps are: nothing exercises GameWorld.Boot end-to-end (all wiring lines are uncovered by tests), Porto weapon-portal placement (PlacePortoPortal) has zero tests, the TraceService→AmbientManager publish bridge and the per-map clear-between-maps behavior are untested, sv_warpzone_allow_selftarget actually changing link behavior is untested, and there is no `warpzone`-named command at all in Commands.cs.

- **`WarpzoneTests.Transform_MapsPlaneCenters`** [IMPLEMENTED] - Builds a 2-plane WarpzoneTransform (IN at origin yaw 0, OUT 100u away yaw 180) and asserts TransformOrigin(IN center) == OUT center within 0.01.  _(Base: lib/warpzone WarpZone_TransformOrigin (WarpZone_SetUp transform setup).)_ **Gap:** Proves only the plane-center mapping for one axis-aligned canonical pair; no off-center point, no non-axis-aligned (arbitrary yaw/pitch/roll) plane, no scale/asymmetric-size pair.
- **`WarpzoneTests.Transform_PreservesSpeed`** [IMPLEMENTED] - Asserts |TransformVelocity(v)| == |v| for an arbitrary velocity (rotation preserves length).  _(Base: lib/warpzone WarpZone_TransformVelocity.)_ **Gap:** Proves length preservation only; does not verify the rotated direction is correct for a general (non-canonical) plane pair.
- **`WarpzoneTests.Transform_MovingIntoIn_EmergesOutOfOut`** [IMPLEMENTED] - Velocity into the IN surface (-X) transforms to a vector aligned with OutForward (dot > 0.99).  _(Base: lib/warpzone WarpZone_TransformVelocity directionality.)_ **Gap:** Single canonical direction only; oblique incoming angles and the tangential components are not checked.
- **`WarpzoneTests.Teleport_WarpsEntityAndPreservesMomentum`** [IMPLEMENTED] - Spawns linked pair A/B via WarpzoneManager.Spawn+Link, asserts both Linked, teleports an Entity moving -X through A, asserts it emerges at B's plane and speed is preserved.  _(Base: WarpZone_Teleport / WarpZone_Send (the actual portal crossing of an entity).)_ **Gap:** Does not assert the resulting velocity DIRECTION, nor angle/view rotation of the entity, nor that origin is offset off the exit plane by the AABB (anti-stick); only origin==exit-center and speed-magnitude are checked.
- **`WarpzoneTests.Teleport_SkipsWhenMovingOutOfZone`** [IMPLEMENTED] - Entity moving +X (out of, not into, the IN surface) → Teleport returns false and Origin is unchanged.  _(Base: WarpZone_Teleport crossing-direction guard (only warp when crossing inward).)_ **Gap:** Covers the wrong-direction reject; does NOT cover the not-yet-reached / already-past / exactly-on-plane edge cases, nor an entity inside the trigger bounds but not crossing the plane this frame.
- **`WarpzoneTraceTests.Chain_Identity_Is_NoOp`** [IMPLEMENTED] - WarpzoneTransformChain.Identity has HasTransform==false and TransformPoint/TransformDirection are no-ops.  _(Base: WarpZone_trace_transform identity (no zones crossed).)_ **Gap:** None for identity; composition of >1 distinct transforms (associativity/order) is only indirectly exercised by the pathological-chain test which asserts only termination, not the composed value.
- **`WarpzoneTraceTests.Chain_Append_Matches_The_Zone_Transform_For_One_Zone`** [IMPLEMENTED] - Identity.Append(zone.Transform) reproduces that zone's TransformOrigin (point) and TransformVelocity (direction) for the IN center and an arbitrary point/dir.  _(Base: WarpZone_TraceBox transform accumulation for a single zone.)_ **Gap:** Only ONE append is value-checked. A genuine MULTI-zone chained transform value (point mapped through 2+ distinct zones) is never asserted — the chain-composition correctness across zones is unproven.
- **`WarpzoneTraceTests.TraceLine_Crosses_One_Portal_And_Accumulates_The_Transform`** [IMPLEMENTED] - TraceWarpzone with a MissTrace, ray -X across the IN plane: asserts ZonesCrossed==1, Transform.HasTransform, and the transform maps the crossing point to the OUT center.  _(Base: lib/warpzone/common.qc WarpZone_TraceBox / _TraceLine recursion (single crossing).)_ **Gap:** Relies on the analytic plane-crossing fallback (no real geometry hit). Does not assert the returned EndPos, and uses a trace that never hits — interaction between a real solid hit BEFORE the portal vs at the portal mouth is untested.
- **`WarpzoneTraceTests.TraceLine_No_Portal_On_The_Segment_Is_A_Plain_Trace`** [IMPLEMENTED] - Ray parallel to and far from the IN plane: ZonesCrossed==0, no transform, exactly one underlying trace.Calls.  _(Base: WarpZone_TraceBox degenerating to tracebox when no portal is on the segment.)_ **Gap:** Confirms the no-cross fast path and call count; fine.
- **`WarpzoneTraceTests.TraceLine_No_Manager_Is_A_Single_Plain_Trace`** [IMPLEMENTED] - manager==null → ZonesCrossed==0, one plain trace call.  _(Base: Guard for maps with no g_warpzones.)_ **Gap:** Covers the null-manager guard. Does NOT cover a non-null manager with zero zones (Zones empty) taking the same path — that branch is unverified.
- **`WarpzoneTraceTests.TraceLine_Pathological_Self_Recrossing_Chain_Stops_At_The_Guard`** [IMPLEMENTED] - 8 stacked zone pairs along -X; a long -X ray that could recross endlessly must terminate: asserts ZonesCrossed <= MaxZoneDepth and trace.Calls <= MaxZoneDepth+1.  _(Base: lib/warpzone 16-zone recursion guard (WARPZONE_MAX recursion cap).)_ **Gap:** Proves termination/cap only. Does not assert the cap value is exactly 16 (uses the constant), nor that a LEGITIMATE chain of exactly MaxZoneDepth zones still produces a correct (not truncated) result.
- **`WarpzoneTraceTests.FindRadius_No_Warpzones_Is_The_Plain_FindRadius_With_Identity`** [IMPLEMENTED] - manager==null radius query: only the near entity (within radius) is returned, with identity ToBlastFrame and unchanged LocalBlastOrigin.  _(Base: WarpZone_FindRadius with no warpzones == plain findradius.)_ **Gap:** Covers the no-warpzone base case + plain radius filtering.
- **`WarpzoneTraceTests.FindRadius_Reaches_A_Victim_Only_Through_A_Portal`** [IMPLEMENTED] - Victim at (150,0,0) is outside the straight-line 80u radius from blast (50,0,0) but reachable through the portal mouth; asserts it is found, its hit carries HasTransform and the portal-shifted LocalBlastOrigin (150,0,0).  _(Base: lib/warpzone/common.qc WarpZone_FindRadius_Recurse (radius damage through a seam).)_ **Gap:** Single-portal recursion only. Multi-hop radius recursion, remaining-radius decrement correctness across >1 zone, and PVS/visibility checks are not asserted. The victim AABB is set but no clipping/partial-overlap case is tested.
- **`WarpzoneTraceTests.FindRadius_Skips_Blacklisted_Classnames`** [IMPLEMENTED] - waypoint and info_player_deathmatch are excluded; only the real player is returned.  _(Base: WarpZone_FindRadius classname blacklist.)_ **Gap:** Tests two blacklisted classnames; the full blacklist set and case-sensitivity are not enumerated.
- **`WarpzoneSpawnTests.MapWarpzones_Spawn_Orient_And_Link_FromBrushGeometry`** [IMPLEMENTED] - Two trigger_warpzone brushes (*1 +X, *2 -X) via WarpzoneSpawns.TriggerWarpzoneSetup; InitMapZones derives origin=centroid + forward=face normal, links both, marks the Trigger entity (ClassName trigger_warpzone, Solid.Trigger), and an entity crossing A emerges at B with speed preserved.  _(Base: lib/warpzone/server.qc trigger_warpzone spawnfunc + WarpZone_StartFrame init pass (auto-orient from geometry).)_ **Gap:** Only the canonical two-portal axis-aligned case. Does not cover >2 zones, multi-face brushes, non-planar/curved brushes, or a brush whose geometry can't yield a plane during the live InitMapZones pass (DerivePlaneFromBrush failure path is only tested in isolation in SurfaceQueryTests).
- **`WarpzoneSpawnTests.MapWarpzone_PositionEntity_Overrides_Orientation`** [PARTIAL] - A trigger_warpzone_position (yaw 33) targeting portal A is attached as aiment; asserts the zone still Linked and stayed axis-aligned (|f.X|==1).  _(Base: lib/warpzone/server.qc trigger_warpzone_position override (WarpZone_InitStep aiment correction).)_ **Gap:** WEAK assertion: because the quad is axis-aligned the test only confirms the override RAN and the plane stayed planar — it does NOT prove the 33-degree orientation was actually applied or that the aiment changed anything. The real override effect (non-axis-aligned exit orientation) is effectively unproven.
- **`WarpzoneSpawnTests.MapWarpzones_TwoWayLink_WhenOnlyOneCarriesTarget`** [IMPLEMENTED] - Asymmetric: only A sets .target (B has only targetname); InitMapZones still links both (two-way enemy link).  _(Base: lib/warpzone two-way enemy linking (WarpZone_InitStep_FindTarget reciprocal link).)_ **Gap:** Covers the one-sided-target reciprocal link. Does NOT cover: neither side targeting (no link), a target naming a nonexistent partner, or self-target (which is gated by sv_warpzone_allow_selftarget — untested).
- **`SurfaceQueryTests.Warpzone_AutoDerivesPlaneFromBrush`** [IMPLEMENTED] - WarpzoneManager.DerivePlaneFromBrush on a quad brush returns ok with origin=centroid and forward=+X face normal; a trigger-textured brush (textures/common/trigger) returns not-ok.  _(Base: WarpZone_InitStep_UpdateTransform plane derivation from getsurface* + the trigger-texture reject.)_ **Gap:** Only +X axis-aligned quad and the trigger-texture reject. Non-axis-aligned face normals, multi-surface brushes, and degenerate/zero-area faces are not exercised.
- **`SurfaceQueryTests.SpawnFromBrush_BuildsAWorkingPortal`** [IMPLEMENTED] - WarpzoneManager.SpawnFromBrush sets ClassName trigger_warpzone + Solid.Trigger, then pairing with an explicit Spawn + Link yields wz.Linked.  _(Base: trigger_warpzone spawnfunc → ExactTrigger setup → link.)_ **Gap:** Mixes a brush-derived IN with a manually-Spawned OUT; a fully brush-derived linked PAIR via SpawnFromBrush on both sides is covered instead by the WarpzoneSpawnTests path, not here.
- **`GameWorld.Boot (warpzone wiring block, lines 386-558)`** [IMPLEMENTED] - Wires Porto.PortalSpawner -> Warpzones.PlacePortoPortal; sets WarpzoneSpawns.Sink = Warpzones.OnMapEntity; BuildAndAttach(MapBsp) for getsurface*; calls Warpzones.InitMapZones() after all entities spawn; calls Services.TraceImpl.SetWarpzoneManager(Warpzones) to publish the ambient manager for combat traces/radius.  _(Base: QC world.qc boot order: spawnfuncs -> WarpZone_StartFrame -> g_warpzones global for trace/radius.)_ **Gap:** NO TEST exercises GameWorld.Boot at all for warpzones. Every wiring line here (Porto spawner hookup, Sink bridge install, InitMapZones-after-spawn ordering, SetWarpzoneManager publish) is verified only indirectly via the unit-level helpers the tests call manually (WarpzoneSpawns.Sink set by hand in WarpzoneSpawnTests.Setup). The end-to-end boot path is unproven.

**Missing vs Base:** Behavior present/wired in the server or Common but with NO test counterpart:

1. Porto weapon-portal placement: WarpzoneManager.PlacePortoPortal(origin, normal, isInPortal, portalId, owner) is wired in GameWorld.Boot via Porto.PortalSpawner but has ZERO tests. The entire Porto in/out portal lifecycle (placing the in portal, then the out portal, two-way linking landed portals, portal expiry/replacement on a new shot, ownership) is untested. The class doc on WarpzoneManager calls portals a first-class use ("map warpzones + Porto-weapon portals") yet only map warpzones are tested.

2. The TraceService -> Common ambient bridge: TraceService.SetWarpzoneManager (TraceService.cs:62) -> TraceServiceWarpzoneExt.Publish -> WarpzoneTrace.AmbientManager. Tests always pass the manager EXPLICITLY into TraceWarpzone/FindRadiusWarpzone; the ambient-resolution overloads (TraceLineWarpzone/TraceBoxWarpzone and FindRadiusWarpzone(AmbientManager,...)) and the publish/clear-between-maps semantics are never tested. WeaponFiring.cs (lines 141/233/358) and WeaponSplash.cs (line 76) consume the ambient manager in real fire paths — none of those call sites are covered by these warpzone tests.

3. sv_warpzone_allow_selftarget (registered in Cvars.cs:105, default 0): no test verifies that 0 prevents a zone self-linking or that 1 permits it. The cvar's effect on InitMapZones linking is entirely unproven (Cvars doc even notes behavior is "already correct" — i.e., relied upon without a regression test).

4. No `warpzone` console command exists in Commands.cs. The only "WarpZone" hits there are comments on monster-spawn/look-at traces (TraceLookedAtMonster / TraceSpawnPoint) that reference QC WarpZone_TraceLine/_TraceBox as the origin of the technique but do NOT route through the warpzone manager — so those traces do NOT recurse through portals. There is no debug/admin command to list, dump, or toggle warpzones.

5. Entity exit-pose details: angle/view rotation of a teleported player, the AABB anti-stick offset off the exit plane, and dragging attached entities (held objects, projectiles owned by the player) through the seam are not asserted anywhere.

6. Multi-zone (>=2 hops) traversal VALUE correctness for both trace and radius (only termination at the guard and single-hop value are tested).

**Notes:** Test file locations: C:/Users/Bryan/Projects/Xonotic/XonoticGodot/tests/XonoticGodot.Tests/WarpzoneTests.cs (transform/teleport), WarpzoneTraceTests.cs (combat traversal — in [Collection("GlobalState")] because it swaps Api.Services), WarpzoneSpawnTests.cs (map-entity spawn/orient/link), SurfaceQueryTests.cs (getsurface* + DerivePlaneFromBrush/SpawnFromBrush).

Production under test: C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/Warpzone/WarpzoneRadiusQuery.cs holds WarpzoneTrace (TraceWarpzone, MaxZoneDepth, AmbientManager) and WarpzoneRadiusQuery (FindRadiusWarpzone). Server wiring: GameWorld.cs:185-186 (Warpzones property), 386-393 (Porto + Sink), 549-558 (InitMapZones + SetWarpzoneManager). Bridge: src/XonoticGodot.Engine/Collision/TraceService.cs:62 and TraceServiceWarpzoneExt.cs. Cvar: Cvars.cs:105.

Real combat consumers (proving the wiring is load-bearing, themselves not in these warpzone tests): WeaponFiring.cs:141,233,358 use TraceLineWarpzone; WeaponSplash.cs:76 uses FindRadiusWarpzone(WarpzoneTrace.AmbientManager, ...).

Note on a possible reading artifact: a raw grep context render showed line 560 of GameWorld.cs as "\\ 6b)", but reading the file confirms it is a correct "// 6b)" comment — not a bug.

Net: the math/geometry layers are solidly proven at unit granularity; the integration layer (GameWorld.Boot ordering, ambient publish, Porto portals, selftarget cvar) is wired but effectively untested. The biggest real-coverage risk is Porto weapon portals (zero tests despite being a headline feature of the manager) and the absence of any end-to-end boot test.


---

# Part 3 - Gap analysis (what to shore up / reimplement)

Each gap was adversarially verified against the actual source. **Effective severity** reflects the verifier's correction where it differed from the synthesis.

| # | Area | Synth sev | Verified | Eff. severity | What to do |
|---|------|-----------|----------|---------------|------------|
| G1-render | far-side render | blocker | confirmed | **major** | SubViewport+Camera3D per portal at TransformOrigin(cam) facing TransformVelocity(camDir); bind ViewportTexture; replace BuildPortal; wire from NetGame |
| G2-view-fix | client view | blocker | confirmed | **major** | per-frame FixView after camera before viewmodel; set Camera3D via Transform; nudge InForward; hide local model; needs client zone registry |
| G3-predict | client prediction + view/input rotate | major | confirmed | **major** | predict warpzone crossings; per-teleport transform msg; rotate view angles + pending input moves by it |
| G5-crossing | crossing detection | major | confirmed | **major** | teleport gate: track prev plane side, warp on front-to-back; trace: test box corners or make zones solid; drop 256qu fallback |
| G4-roll | angle roll math | minor | confirmed | **minor** | rotate fwd AND up, reconstruct via FixedVecToAngles2; AnglesOf use up |
| G6-radius-los | radius LOS | minor | confirmed | **minor** | add LOS traceline to portal mouth, skip if occluded; measure dist to nearest box point |
| G8-ai-blind | monster/turret/vehicle AI | minor | CORRECTED | **minor** | swap to TraceLineWarpzone/FindRadiusWarpzone at each site; helpers already exist |
| G9-bot-nav | bot navigation | minor | confirmed | **minor** | port waypoint_spawnforteleporter_wz after InitMapZones; add one-way IN->partner OUT link with offset+JUMP; hook GenerateFromEntities |
| G10-classes | missing entity classes | minor | confirmed | **minor** | alias misc_warpzone_position; add func_camera (depends on G1); add trigger_warpzone_reconnect use-handler re-running ClearTarget/FindTarget/FinalizeTransform (make InitMapZones re-callable) |
| G7-tracetoss | ballistic toss | minor | CORRECTED | **trivial** | add TraceTossWarpzone: gravity toss per segment, transform org/vel on each crossing, cap at MaxZoneDepth |

### [G1-render] MAJOR - far-side render
- **Base behavior:** client.qc setcamera_transform draws OUT live view onto IN surface
- **Port state:** none; static placeholder
- **Fix:** SubViewport+Camera3D per portal at TransformOrigin(cam) facing TransformVelocity(camDir); bind ViewportTexture; replace BuildPortal; wire from NetGame
- **Files:** HeroMaterials.cs:168-188; game/net/NetGame.cs
- **Verifier note:** The GAP HOLDS UP in substance — the port genuinely has no live far-side portal render (verified in C#: HeroMaterials.cs BuildPortal is a static dark-metallic placeholder, no SubViewport/Camera3D/ViewportTexture portal path anywhere), while Base+engine do render the far side. The proposed fix (per-portal SubViewport+Camera3D bound to a ViewportTexture, wired from NetGame, replacing BuildPortal) is the correct approach. BUT the "Claimed Base behavior" line is factually mis-stated on two points: (1) setcamera_transform is in common.qc:73,93, NOT client.qc; (2) it does NOT "draw the OUT live view onto the IN surface" — it is a SELFWRAP_SET macro (self.qh:96-97) that assigns a coordinate-transform callback (common.qc:39-58) returning a transformed vector and running a traceline; it never renders to a surface. The actual on-surface live render is done by the DarkPlaces ENGINE via the dp_camera shader keyword (effects_warpzone.shader:13) plus the r_water refraction system (client.qc:29); the QC only supplies the camera transform and adjusts the player's own view (WarpZone_FixView, client.qc:216-278). The relevant-port-files path is also slightly off: HeroMaterials.cs lives at game/loaders/HeroMaterials.cs, not src/HeroMaterials.cs (the :168-188 range is correct). Severity: downgrade "blocker" to "major" — the game is playable; this is a high-impact visual-fidelity gap (portals/warpzones look opaque/reflective instead of showing the far side), not a crash or unplayable blocker.

### [G2-view-fix] MAJOR - client view
- **Base behavior:** FixView rewrites view via Transform; FixNearClip nudges off plane; View_Inside hides body
- **Port state:** unported (TODO.md:1268)
- **Fix:** per-frame FixView after camera before viewmodel; set Camera3D via Transform; nudge InForward; hide local model; needs client zone registry
- **Files:** game/net/NetGame.cs
- **Verifier note:** CONFIRMED, with a severity nuance. (a) Base behaves exactly as claimed in Base/data/xonotic-data.pk3dir/qcsrc/lib/warpzone/client.qc: WarpZone_FixView (line 216) reads VF_ORIGIN/VF_ANGLES, finds the zone, transforms origin+angles through the portal and writes them back via setproperty(VF_ORIGIN/VF_ANGLES) (lines 220-266); WarpZone_FixNearClip (line 174) returns the off-plane nudge e.warpzone_forward * -pd which FixView applies as org+o (lines 275-277); WarpZone_View_Inside (line 160) sets r_drawexteriormodel=0 to hide the local exterior body (lines 167-171). All three claimed behaviors are accurate. (b) The port is genuinely unported — verified by reading the C#, not assuming. A grep for FixView/FixNearClip/View_Inside/FixPMove/r_drawexteriormodel/warpzone_fixingview across the port source returns zero hits outside TODO.md and a planning doc. The ported Warpzone.cs:19 explicitly states the seamless VIEW rendering is out of scope (a client concern); WarpzoneRadiusQuery.cs:8 states the client portal SubViewport render is OUT OF SCOPE. The claimed relevant file game/net/NetGame.cs contains zero warpzone references — it is correctly named as where the work WOULD go, not where it exists. No client-side warpzone zone registry exists (no WarpZone_Find/g_warpzones client analog), so the proposed-fix note 'needs client zone registry' is accurate. The only near-match, FirstPersonView.cs:329, is the death-cam event-chase wall-clearance trace (it merely borrows the 'WarpZone_TraceBox MOVE_WORLDONLY' technique name in a comment) and is unrelated to warpzone view-fixing. What IS ported is the server-side transform/teleport (Warpzone.cs) and the T45 combat-traversal trace/radius half (TraceServiceWarpzoneExt.cs, WarpzoneRadiusQuery.cs); the client view-fix is a distinct, untouched piece. (c) Severity: 'blocker' is slightly overstated. The view-fix is purely a local-client cosmetic concern — without it the view does not render seamlessly across a warpzone seam and the player's own exterior body may flicker, but the teleport, momentum, and combat traversal still work and the match still runs. Recommend 'major' (degrades, does not prevent, play on warpzone maps) rather than 'blocker'. The gap itself is real, correctly identified, and fully unported.

### [G3-predict] MAJOR - client prediction + view/input rotate
- **Base behavior:** server sends ENT_CLIENT_WARPZONE_TELEPORTED; client rotates view angles + CL_RotateMoves input history; predicts crossings
- **Port state:** only trigger_teleport predicted (TriggerTouch.cs:139-169); crossings rubber-band
- **Fix:** predict warpzone crossings; per-teleport transform msg; rotate view angles + pending input moves by it
- **Files:** TriggerTouch.cs:139-169; game/net/NetGame.cs
- **Verifier note:** Gap holds up. (a) Base really sends ENT_CLIENT_WARPZONE_TELEPORTED with a transform and the client rotates both view angles (VF_CL_VIEWANGLES) and the input-move history (CL_RotateMoves) to predict the crossing — confirmed in client.qc:133-147 and server.qc:71-78/150-176. (b) The port predicts only trigger_teleport (and jump-pads); warpzone teleportation is purely server-side (WarpzoneManager.Teleport in the .Touch handler), with no prediction, no per-teleport transform message, and no view/input rotation — verified by reading the C#, not assuming. (c) Severity major is right: warpzones are a core movement feature; an unpredicted crossing rubber-bands the camera/origin on every pass, and the proposed fix (predict the crossing + per-teleport transform msg + rotate view angles and pending input moves) matches Base's mechanism exactly. One minor mis-statement to note: the relevant port path is src/XonoticGodot.Engine/Simulation/TriggerTouch.cs (the report's qcsrc-style path was approximate), though the cited line range 139-169 is exactly correct (PredictTeleportsAmbient). NetGame.cs is correctly cited as the integration point for the missing view/input rotation, but it currently contains no warpzone handling at all.

### [G5-crossing] MAJOR - crossing detection
- **Base behavior:** QC MakeAllSolid+tracebox hits solid zone brush; Touch uses plane-side gate; honors box hull
- **Port state:** analytic: velocity heuristic (Warpzone.cs:190) + centerline+AABB (WarpzoneRadiusQuery.cs:226-278); misses box hulls/oblique mouths
- **Fix:** teleport gate: track prev plane side, warp on front-to-back; trace: test box corners or make zones solid; drop 256qu fallback
- **Files:** Warpzone.cs:190; WarpzoneRadiusQuery.cs:226-278
- **Verifier note:** Holds up. Base behaves as claimed (MakeAllSolid+tracebox against the solid zone hull in common.qc; plane-side gate + EXACTTRIGGER box-hull touch in server.qc). The port is in the claimed state, verified by reading the C#: the teleport gate is a velocity dot test (Warpzone.cs:190), and crossing detection for the trace path is a single centerline ray gated by an axis-aligned AABB (WarpzoneRadiusQuery.cs:226-278) with a 256qu fallback (:277). Two clarifications, neither of which invalidates the gap: (1) the gap's 'Relevant port files' paths are inaccurate — the files exist under src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs and .../Gameplay/Warpzone/WarpzoneRadiusQuery.cs, though the cited line numbers are correct within those files; (2) the 256qu fallback only fires for trigger-less POJO zones (headless tests) — in a real match the trigger-AABB branch runs — so 'drop 256qu fallback' is cosmetic, while the AABB-vs-true-hull and velocity-vs-plane-side divergences are the substantive issues. Severity 'major' is appropriate: real fidelity divergence on warpzone maps (teleport side gate and box-hull crossing), but the core transform/momentum math is correct and unit-tested, and warpzones are uncommon, so not critical.

### [G4-roll] MINOR - angle roll math
- **Base behavior:** ApplyToAngles carries pitch/yaw/roll via fixedvectoangles2(fwd,up)
- **Port state:** TransformAngles/AnglesOf use fwd only, drop up; roll lost (Warpzone.cs:61-70)
- **Fix:** rotate fwd AND up, reconstruct via FixedVecToAngles2; AnglesOf use up
- **Files:** Warpzone.cs:61-70
- **Verifier note:** Gap holds up as stated. (a) Base: WarpZone_TransformAngles -> AnglesTransform_ApplyToAngles -> AnglesTransform_Multiply rotates BOTH forward and up and reconstructs via fixedvectoangles2(forward, up), carrying pitch/yaw/roll, exactly as claimed (anglestransform.qc:12-19, :160-164; common.qc:496-499; fixedvectoangles2 macro at anglestransform.qh:12-13). This is the .angles path Base applies to teleported non-client entities (server.qc:93), which is what the port mirrors. (Nuance, not a defect in the claim: for actual players Base uses WarpZone_TransformVAngles, which itself strips roll by default unless KEEP_ROLL is defined - common.qc:501-517. So even Base only preserves roll on the .angles/non-client path, the very path the port ports.) (b) Port: verified by reading the C#. TransformAngles (Warpzone.cs:61-65) does AngleVectors(angles, out fwd, out _, out _) then FixedVecToAngles(Rotate(fwd)) - up discarded, single-arg reconstruction, roll lost. AnglesOf (Warpzone.cs:70) takes (fwd, up) but ignores up: returns FixedVecToAngles(fwd), with literal comment "roll from up not modeled". Claimed line range 61-70 is exact. The roll-keeping primitive the fix proposes (FixedVecToAngles2(forward, up)) already exists at QMath.cs:163 and is even used in this same file at Warpzone.cs:277, so the fix is feasible. (c) Severity "minor" is correct, arguably generous: roll only deviates from zero for rolled/banked warpzone pairs (uncommon), and only affects entity .angles orientation - origin/velocity physics use the full basis correctly via Rotate (lines 47-58), so movement is unaffected. Additionally AnglesOf is reached only from Inverse() (line 68), which has NO callers anywhere in src (dead API), so that half of the gap is currently inert. No test exercises roll. Latent correctness gap, narrow impact. Note: the file path in the task prompt was wrong - the actual file is src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs, not src/...root; line numbers match.

### [G6-radius-los] MINOR - radius LOS
- **Base behavior:** FindRadius_Recurse tracelines blast to portal-shifted origin, discards unseen zones
- **Port state:** front-side dot only (WarpzoneRadiusQuery.cs:408); walls do not block splash
- **Fix:** add LOS traceline to portal mouth, skip if occluded; measure dist to nearest box point
- **Files:** WarpzoneRadiusQuery.cs:393-422
- **Verifier note:** HOLDS UP as a real (if narrow) divergence, but the claim is mis-stated on both sides and over-broad. Severity "minor" is correct (arguably trivial).

WHAT'S REAL: Base WarpZone_FindRadius_Recurse, in its per-zone recursion loop, runs an UNCONDITIONAL traceline on the FAR side of each portal: common.qc:644-647 does `org0_new = WarpZone_TransformOrigin(e, org); traceline(e.warpzone_targetorigin, org0_new, MOVE_NOMONSTERS, e); org_new = trace_endpos;` — i.e. from the OUT/exit mouth (warpzone_targetorigin) to the portal-shifted blast point, then CLAMPS the next search origin to the trace endpoint, and reduces radius by the clamped distance (common.qc:652). The port omits exactly this: WarpzoneRadiusQuery.cs:412 uses `orgNew = wz.Transform.TransformOrigin(org)` directly with NO OUT-mouth traceline and no clamp, and reduces radius by the IN-side distToZone only (cs:400,414-416), substituting a front-side dot gate (cs:408). The port's OWN comment (cs:405-407) admits it drops "the LOS trace + radius reduction" in favor of the dot gate. So the port IS in the claimed missing/partial state — verified in the C#, not assumed.

WHERE THE CLAIM IS WRONG:
1. The relevant Base traceline is NOT the `needlineofsight` LOS check (common.qc:609-614) — that one is OPTIONAL and splash damage passes needlineofsight=FALSE (damage.qc:746). The load-bearing traceline is the per-zone OUT-mouth clamp at common.qc:645, which is always run. The claim's "discards unseen zones" mis-describes it: it CLAMPS the recursion origin to the wall on the far side; it does not discard/skip the zone.
2. "walls do not block splash" is FALSE as written. Same-room splash wall-occlusion is fully implemented at the CALL SITE: WeaponSplash.cs:132-180 does the multi-sample box LOS test with g_throughfloor blend (the faithful port of damage.qc:838-905). Even FAR-SIDE victims get a warpzone-aware LOS trace from the shifted origin (WeaponSplash.cs:161, TraceLineWarpzone(blastOrg, sample,...)). What's genuinely missing is ONLY the far-side ORIGIN CLAMP at the OUT mouth before that — not LOS in general.
3. The proposed fix "measure dist to nearest box point" is already done: WeaponSplash.cs:92 (`Vector3.Clamp(blastOrg, e.Origin+e.Mins, e.Origin+e.Maxs)`) is the port of WarpZoneLib_NearestPointOnBox (common.qc:608) at the call-site.

NET: a true but narrow gap — only affects splash that chains THROUGH a warpzone where a far-side occluder sits behind the OUT mouth (warpzone maps only; same-room splash LOS is fine). Minor stands. A precise fix is: in WarpzoneRadiusQuery.Recurse, before line 412, trace from wz.Transform.OutOrigin to the shifted origin and clamp orgNew to the endpoint, then reduce radius by the clamped travel — mirroring common.qc:644-652.</correction>
<parameter name="evidence">Base far-side clamp: C:/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/qcsrc/lib/warpzone/common.qc:644-647 and :652 (radius reduction by clamped dist). Optional LOS (NOT used by splash): common.qc:609-614; nearest-point: common.qc:608/552-559. Splash caller passes needlineofsight=false: qcsrc/server/damage.qc:746; same-room multi-sample box LOS: damage.qc:838-905. Port missing clamp + dot-gate substitute: C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/Warpzone/WarpzoneRadiusQuery.cs:400-416 (esp. :408 dot gate, :412 direct TransformOrigin, :414-416 IN-side radius reduction) with self-admitting comment :405-407. Port DOES have splash wall-occlusion + nearest-point at call site: C:/Users/Bryan/Projects/Xonotic/XonoticGodot/src/XonoticGodot.Common/Gameplay/Weapons/WeaponSplash.cs:92 (nearest box point), :132-180 (multi-sample box LOS), :161 (warpzone-aware far-side LOS trace). Transform OUT origin available for a fix: WarpzoneTransform.OutOrigin at MapObjects/Warpzone.cs:29.

### [G8-ai-blind] MINOR - monster/turret/vehicle AI  (synthesis claim CORRECTED on verification)
- **Base behavior:** AI LOS/find/board use warpzone-aware traces/radius, see+shoot through portals
- **Port state:** portal-blind: plain Trace/FindInRadius (MonsterAI 5 calls; TurretCombat; TurretAI; VehicleBoarding)
- **Fix:** swap to TraceLineWarpzone/FindRadiusWarpzone at each site; helpers already exist
- **Files:** MonsterAI.cs; TurretCombat.cs; TurretAI.cs; VehicleBoarding.cs
- **Verifier note:** MIS-STATED / over-claimed. The blanket premise "Base AI LOS/find/board use warpzone-aware traces/radius, see+shoot through portals" is false for most of the enumerated sites — verified by reading Base source. Per subsystem:

MONSTERS (Base sv_monsters.qc): MIXED, mostly plain. Monster_ValidTarget — the acquisition LOS — uses PLAIN traceline (line 124, MOVE_NOMONSTERS) plus checkpvs (line 118), NOT warpzone (there is even a "TODO: maybe we can rely on PVS" comment). Monster_FindTarget scans an INTRUSIVE LIST (IL_EACH g_monster_targets, line 147), not findradius and not warpzone. Monster_Attack_Melee uses plain traceline (line 1178); wander uses plain traceline (line 620). The ONLY warpzone-aware monster site is Monster_Enemy_Check (the enemy RE-validation), lines 1248-1249: WarpZone_RefSys_TransformOrigin + WarpZone_TraceLine. So the port's 5 plain MonsterAI sites (MonsterAI.cs:572 ValidTarget LOS, :615 FindTarget radius, :841 wander, :1081 melee, :1138 leap toss) all FAITHFULLY match plain Base sites — they are NOT gaps. The genuine divergence is the opposite of what's claimed: the port's EnemyCheck (MonsterAI.cs:644-667) drops the LOS re-validation trace ENTIRELY (no trace at all), whereas Base does a warpzone traceline there.

TURRETS (Base sv_turrets.qc): NOT warpzone-aware at all. turret_validate_target LOS = plain traceline (line 790); turret_select_target + the HITALLVALID loop = plain findradius (lines 825, 1025). Zero WarpZone_* calls in the whole file (grep confirmed). So the port's TurretAI.cs (plain trace line 265 / FindInRadius lines 340,367) and TurretCombat.cs (plain trace line 34) are FAITHFUL — not gaps. The claim that Base turret AI is warpzone-aware is simply wrong.

VEHICLES: this one is real. Base PlayerUseKey (client.qc:2638) uses WarpZone_FindRadius(origin, g_vehicles_enter_radius, true); the port's FindBoardableInRadius (VehicleBoarding.cs:117) uses plain Api.Entities.FindInRadius. So the vehicle-board site IS a faithful warpzone gap.

PROPOSED FIX: "helpers already exist" is TRUE — TraceLineWarpzone (WarpzoneTrace.cs:148), TraceBoxWarpzone (:157), FindRadiusWarpzone (WarpzoneRadiusQuery.cs:341) exist (built for weapons/splash in T45). Caveat: FindRadiusWarpzone returns List<WarpzoneRadiusHit>, not a plain entity list, so it is NOT a trivial 1-line swap at the find sites.

NET: the gap as written is inaccurate (only ~2 of the named sites genuinely diverge: vehicle board, and monster enemy re-check which is actually a dropped-trace not a plain-trace). Severity minor is fine for that real subset. holdsUp=false because the gap mis-attributes warpzone-awareness to Base turret AI and monster acquisition/melee, where Base itself uses plain traces, making most of the listed port sites faithful rather than gaps.

### [G9-bot-nav] MINOR - bot navigation
- **Base behavior:** PostInitialize_Callback auto-links a one-way Teleport waypoint per warpzone pair (JUMP for oblique)
- **Port state:** no auto warpzone waypoints; GenerateFromEntities skips trigger_warpzone
- **Fix:** port waypoint_spawnforteleporter_wz after InitMapZones; add one-way IN->partner OUT link with offset+JUMP; hook GenerateFromEntities
- **Files:** Waypoint.cs:328-330
- **Verifier note:** Gap holds up. (a) Base behaves exactly as claimed: one-way Teleport waypoint per warpzone pair auto-linked in PostInitialize_Callback, with JUMP for oblique zones and skip for upward-pointing zones. (b) Port really is missing it: GenerateFromEntities (XonoticGodot.Server/Bot/Waypoint.cs:328-330) matches only trigger_teleport/trigger_push/trigger_push_velocity and has no trigger_warpzone case; no warpzone-waypoint code exists anywhere in src/, and Teleporters.cs:12 names it out of scope. The required port data (Warpzone.InAngles/OutAngles/Linked/Transform + WarpzoneManager) already exists, so the proposed fix is feasible at the cited insertion point. (c) Severity 'minor' is correct: bots work fine without it; only warpzone maps lacking hand-authored .waypoints lose auto bot routing through portals. Two non-substantive nits: the claimed port file path 'src/Waypoint.cs:328-330' is actually src/XonoticGodot.Server/Bot/Waypoint.cs:328-330 (line numbers correct); and the proposed-fix phrase 'after InitMapZones' doesn't match Base naming (Base hooks it in WarpZone_StartFrame after WarpZones_Reconnect, not an 'InitMapZones'). Neither affects the validity of the gap.

### [G10-classes] MINOR - missing entity classes
- **Base behavior:** server.qc registers func_camera, misc_warpzone_position, trigger_warpzone_reconnect (runtime re-link)
- **Port state:** only trigger_warpzone_position registered; others ZERO matches; InitMapZones one-shot
- **Fix:** alias misc_warpzone_position; add func_camera (depends on G1); add trigger_warpzone_reconnect use-handler re-running ClearTarget/FindTarget/FinalizeTransform (make InitMapZones re-callable)
- **Files:** MapObjectsRegistry.cs:103-109; Warpzone.cs
- **Verifier note:** Holds up — the gap is real and correctly stated in substance. All three Base spawnfuncs exist (server.qc 642/677/807) and none of them (nor func_camera's WarpZoneCamera support, nor any reconnect/re-link path) exist in the port; InitMapZones is verifiably one-shot and self-clearing. Two wording nits, neither changing the verdict: (1) The gap frames misc_warpzone_position and trigger_warpzone_position as peers, but in Base misc_warpzone_position is the CANONICAL spawnfunc (line 642) and trigger_warpzone_position is its ALIAS (line 648 calls spawnfunc_misc_warpzone_position). The port registered the alias and not the canonical name. The proposed fix 'alias misc_warpzone_position' is therefore backwards in naming but functionally correct — both classnames must route to the same TriggerWarpzonePositionSetup. (2) target_warpzone_reconnect (server.qc line 812, an alias of trigger_warpzone_reconnect) is also missing and should be added alongside if the reconnect handler is ported. Severity minor is appropriate: func_camera (scenery camera warpzones) and the reconnect/re-link mechanism (dynamically reoriented or mover-mounted warpzones) are rare mapping features; the canonical-name miss for misc_warpzone_position is the most concrete compat hole but stock maps overwhelmingly use the trigger_ spelling or pure brush-derived orientation.

### [G7-tracetoss] TRIVIAL - ballistic toss  (synthesis claim CORRECTED on verification)
- **Base behavior:** WarpZone_TraceToss arcs a gravity toss through portals for grenade/bot prediction
- **Port state:** absent; grenades only teleport reactively
- **Fix:** add TraceTossWarpzone: gravity toss per segment, transform org/vel on each crossing, cap at MaxZoneDepth
- **Files:** WarpzoneRadiusQuery.cs
- **Verifier note:** Mis-stated, severity wrong. (a) The Base function does mechanically do what's described — WarpZone_TraceToss_ThroughZone (common.qc:333-442) computes g = PHYS_GRAVITY(NULL)*e.gravity (line 337), runs tracetoss per segment subtracting e.velocity_z -= dt*g (lines 391-397), transforms org/velocity on each portal crossing (lines 417-418), capped at i=16 zones (line 382). BUT the claim frames this as a live 'grenade/bot prediction' feature, which is false: a full-tree search shows WarpZone_TraceToss (capital-W, warpzone-aware) has ZERO callers anywhere in Base — the only hits are its declaration (common.qh:48-49) and definition (common.qc:333,439). Every actual ballistic-prediction site uses the raw, NON-warpzone builtin tracetoss instead: bot aim (server/bot/default/aim.qc:63), porto fail (common/weapons/weapon/porto.qc:139), monster throwing (common/monsters/sv_monsters.qc:424), jumppad waypointing (common/mapobjects/trigger/jumppads.qc:479-515,763), raptor dropmark (common/vehicles/vehicle/raptor.qc:797). So WarpZone_TraceToss is dead code in Base — no grenade or bot prediction actually arcs through warpzones via it. (b) Port state correctly observed as absent: WarpzoneRadiusQuery.cs has WarpzoneTrace (straight-line TraceBox/TraceLine analog) and WarpzoneRadiusQuery (FindRadius analog) but no gravity-toss variant; no WarpzoneTraceToss/TraceTossWarpzone exists in the port (only MonsterAI.cs:1129 TraceToss, a non-warpzone helper); projectiles cross portals via reactive Teleport-on-touch (Warpzone.cs:186). 'grenades only teleport reactively' is accurate. (c) Severity 'minor' is too high — porting dead, never-called Base code yields zero observable gameplay difference; this is dead-code parity at best (trivial/non-gap). The proposed fix would add an unused path. Gap does not hold up: it is mis-stated and over-rated.
