using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Particles;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Pushes the relevant cvars from the shared <see cref="MenuState.Cvars"/> store into the live Godot engine —
/// the C# successor to the engine reacting to <c>vid_restart</c> / <c>snd_restart</c> and honoring the
/// <c>bind</c> table. Xonotic's menu writes cvars (<c>vid_fullscreen</c>, <c>vid_width</c>, <c>mastervolume</c>,
/// …) and an "Apply immediately" command button issues the restart that makes them take effect; here those
/// command buttons route through <see cref="MenuCommand"/> to <see cref="ApplyVideo"/> / <see cref="ApplyAudio"/>.
///
/// <see cref="ApplyAll"/> runs once at boot (from <see cref="Shell"/>) so the saved preferences are live before
/// the menu is even shown. The cvar names match the ones the settings dialogs bind to, so a value set in the
/// menu and a value applied here are always the same cvar.
/// </summary>
public static class ClientSettings
{
    /// <summary>Apply every settings group to the live engine (video mode, audio buses).</summary>
    public static void ApplyAll()
    {
        ApplyVideo();
        ApplyAudio();

        // Register the client-effect cvar defaults (the vignette's cl_vignette_* set) into the shared menu/console
        // store at boot, so they're visible/bindable in the menu and console before a match's overlay registers
        // them. Idempotent — keeps any value the user's config already set.
        Client.VignetteOverlay.RegisterDefaults(MenuState.Cvars);
        Client.ReticleOverlay.RegisterDefaults(MenuState.Cvars); // zoom scope reticle (cl_reticle*)
        Client.FrameProfiler.RegisterDefaults(MenuState.Cvars);
        Client.ScreenshotService.RegisterDefaults(MenuState.Cvars); // scr_screenshot_* (the F12 screenshot command)

        // HUD skin + per-panel layout/behaviour cvar defaults (luma), so the menu HUD dialogs and the console
        // can see/drive them and panels read the luma defaults until a config overrides them.
        Hud.HudConfig.RegisterDefaults(MenuState.Cvars);

        RegisterTintDefaults(MenuState.Cvars);
        RegisterStairSmoothDefaults(MenuState.Cvars);
        RegisterEngineClientDefaults(MenuState.Cvars);
        RegisterParticleDefaults(MenuState.Cvars);
    }

    /// <summary>
    /// The dual particle system cvars (planning/particles-dual-system.md §overview): the renderer-mode control
    /// (<c>cl_particles_modern</c> 0/1/2, <c>cl_particles_sdf_*</c>) plus the stock Darkplaces particle/decal
    /// mirrors the faithful backend reads (<c>cl_particles</c>, <c>cl_particles_quality</c>, the per-type gates,
    /// <c>cl_decals_*</c>, <c>r_drawparticles_*</c>). Names + defaults are centralised in <see cref="ParticleCvars"/>;
    /// registering here makes them visible/bindable (the Effects dialog binds the renderer dropdown + SDF
    /// checkbox) and live before the first spawn. Idempotent — keeps any value a cfg already set.
    /// </summary>
    private static void RegisterParticleDefaults(CvarService c)
    {
        foreach ((string name, string def, bool archived) in ParticleCvars.Defaults)
            c.Register(name, def, archived ? CvarFlags.Save : CvarFlags.None);
    }

    /// <summary>
    /// The stock Darkplaces CLIENT engine-cvar defaults that no .cfg line assigns and no other subsystem
    /// registers — the same class as <see cref="RegisterEngineVideoDefaults"/> / <see cref="RegisterEngineAudioDefaults"/>.
    /// These are read across the client (HUD panels, input, net, console) but, being engine cvars registered in
    /// Darkplaces C (not the .cfg tree Xonotic ships), they never entered the cvar store — so they were invisible
    /// to <c>cvarlist</c>/<c>search</c>/Tab-completion and read as a silent 0/"" default. Every default below is
    /// stock-faithful AND preserves the port's current effective behaviour (so registering changes nothing but
    /// visibility): the two read with the <c>!= "0"</c> idiom (absent → ON) are registered "1" to match.
    /// </summary>
    private static void RegisterEngineClientDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // FPS / ping HUD readouts — the showfps/showping checkboxes sit right under vid_vsync on the video dialog
        // and were invisible for the identical reason. cl_show* are the DP-native fallbacks FpsPanel/PingPanel read.
        c.Register("showfps", "0", save);
        c.Register("showping", "0", save);
        c.Register("showposition", "0", save);
        c.Register("cl_showfps", "0", save);
        c.Register("cl_showping", "0", save);
        c.Register("cl_showposition", "0", save);
        // PVS-cull escape hatch (archived). WorldPvsCuller registers this in _Ready — which only runs inside a
        // match — so a menu-only session would leave it "allocated, default unknown" and re-save it to config.cfg
        // even at its default. Register it eagerly at boot (idempotent with WorldPvsCuller's own Register) so it's
        // a declared cvar with a known default and is persisted only when actually changed.
        c.Register("r_pvs_cull", "1", save);
        // (§12.8 A/B) Godot-native occlusion culling — orthogonal to r_pvs_cull, OFF by default (PVS is the
        // shipping path). Eager idempotent register (matches WorldOcclusion's own) so it's a declared cvar.
        c.Register("r_occlusion_cull", "0", save);
        // (§12.8) DP-faithful entity render culling: hide remote entities outside the camera's PVS — DP draws an
        // entity only when its cluster is in the view's PVS. ON by default (pairs with sv_cullentities_pvs so the
        // client also skips drawing anything that slips through); margin = the half-extent of the bounds box.
        c.Register("r_pvs_cull_entities", "1", save);
        c.Register("r_pvs_cull_entities_margin", "64", save);
        // (§12.5) Adaptive world-mesh cell size — scales the spatial split to map size to bound draw calls.
        // adaptive 0 = fixed r_world_cell_size (1024 = today). div ~= cells along the longest axis; min/max clamp.
        c.Register("r_world_cell_adaptive", "0", save);
        c.Register("r_world_cell_size", "1024", save);
        c.Register("r_world_cell_div", "8", save);
        c.Register("r_world_cell_min", "256", save);
        c.Register("r_world_cell_max", "4096", save);
        // Mouse pitch (only its SIGN is used here, for invert-look); DP default 1 (non-inverted) = current behaviour.
        c.Register("m_pitch", "1", save);
        // Server-browser auto-refresh pause toggle (DP default 0).
        c.Register("net_slist_pause", "0", save);
        // Local-player movement-prediction model (read live by NetGame). DEFAULT 1 = PATH A, the Base-faithful path:
        // predict the local player ONCE per RENDER frame at the real (clamped) frame dt — Xonotic Base's
        // Movetype_Physics_NoMatchTicrate — so the predicted origin lands at the exact render time and moves smoothly
        // at any fps (no fixed-tick→fps aliasing "lurch"). The physics is frametime-independent (half-step gravity →
        // dt-invariant apex; fps-independent strafe speed — see MovementTimingTests), so variable dt is safe; the
        // server stays a fixed 1/72 s authoritative tick. `set cl_movement_perframe 0` = the LEGACY fixed-tick path
        // (drain input in 1/72 s quanta + snap-to-latest render) — kept as an A/B fallback. See
        // [[camera-drift-render-smoothing]] / NET-DEBUGGING.md.
        c.Register("cl_movement_perframe", "1", save);
        // Sub-tic eye extrapolation (DP partial-final-frame approximation, NetGame.UpdateCamera). Default ON. This
        // LINEAR extrapolation by the leftover input accumulator beats with any fps that isn't a multiple of 72,
        // shoving the eye a few units per hop — a candidate for "inconsistent bunnyhop timing". `set
        // cl_movement_subtic_extrapolate 0` renders the eye at the last simulated tic (no extrapolation) for A/B
        // isolation; the gravity-correct partial-tic sub-step fix supersedes it. NOT gated by cl_movement_smoothing_*.
        c.Register("cl_movement_subtic_extrapolate", "1", save);
        // Path A send model (only consulted in per-frame / cl_movement_perframe 1 mode). cl_netfps (DP-faithful,
        // default 72) = how many input datagrams/s the client sends; the client still PREDICTS every render frame.
        // cl_movement_send_all (default 0 = Base-faithful): 0 = gate sends to cl_netfps with bounded redundancy
        // (intermediate frames coalesce above ~cl_netfps×redundancy fps, exactly like Darkplaces); 1 = send every
        // predicted frame so the server replays the IDENTICAL command sequence and reconcile stays ~0 (more
        // bandwidth, ideal on a listen server). Read live by NetGame so it A/B-toggles in-session.
        c.Register("cl_netfps", "72", save);
        c.Register("cl_movement_send_all", "0", save);
        // cl_netimmediatebuttons (DP-faithful, default ON): send a command IMMEDIATELY (bypassing the cl_netfps rate
        // gate) when it carries an impulse or a button-state change (fire/jump/crouch press/release), so above 72 fps
        // those reach the server with minimal latency instead of waiting up to one ~13.9ms interval; steady movement
        // input stays rate-limited to cl_netfps. `set cl_netimmediatebuttons 0` rate-limits everything uniformly.
        c.Register("cl_netimmediatebuttons", "1", save);
        // Render-clock damping (Path A #2): 1 (default) = free-run the render clock and gently creep it toward server
        // time (Base cl_nettimesyncboundmode), 0 = hard-rebase to the latest server time every snapshot (the old
        // behaviour, which jolts the camera/decay timeline when snapshots arrive in lumps). For A/B isolation.
        c.Register("cl_netclock_smooth", "1", save);
        // Post-hitch stall-aware reconcile (default ON). After a frame HITCH (GC / heavy streaming stalls the shared
        // listen-server thread), the server is transiently behind; this HOLDS a moderate reconcile correction for a
        // few snapshots instead of snapping the camera back, then resumes. Defensive (NOT the cause of any observed
        // bug — the spawn-stutter was the ENet throttle), so it's toggleable: `set cl_movement_hitch_hold 0` reverts
        // to immediate snapping. Rationale + the masking risk it carries: TROUBLESHOOTING.md.
        c.Register("cl_movement_hitch_hold", "1", save);
        c.Register("cl_predictfire", "1", save);       // intentionally default ON (NetGame: unset → on)
        // Client-side projectile prediction (CSQC Projectile_Draw): snap+extrapolate vs the old ease. Default
        // ON; `set cl_projectile_prediction 0` reverts for A/B feel-testing (ClientWorld polls it live).
        c.Register("cl_projectile_prediction", "1", save);
        // Master view-smoothing mode (default 1 = FAITHFUL): render the eye via the Base CSQCPlayer_ApplySmoothing
        // algorithm (stairsmoothz glide + viewheightavg eye-height blend, error compensation forced OFF so
        // corrections SNAP) so the camera matches stock Xonotic exactly and the only intentional divergence is the
        // stepheight processing. 0 = the port path (adaptive stair catch-up + error-comp/knockback glide). Read
        // live by NetGame.UpdateCamera; the Settings→Misc dialog can bind it.
        c.Register("cl_movement_smoothing_faithful", "1", save);
        // Prediction-error view smoothing strength (stock DP/Xonotic cvar; the Settings→Misc checkbox binds it).
        // Base default is 0 (snap to truth) — a correction the smoother would smear into a drifting camera lag
        // instead lands as a clean snap. Only consulted on the PORT path (faithful mode forces it 0). >0 re-enables
        // the port's decaying error glide. Read live by NetGame via ConfigureErrorSmoothing.
        c.Register("cl_movement_errorcompensation", "0", save);
        // Eye-height smoothing time, seconds (stock Xonotic cl_smoothviewheight, default 0.05): how fast the eye
        // blends to the new view offset on crouch/stand (faithful viewheightavg). 0 = snap. Read live by NetGame.
        c.Register("cl_smoothviewheight", "0.05", save);
        // Knockback view-smoothing window, seconds (PORT EXTENSION, read live by NetGame via ConfigureErrorSmoothing):
        // how long an explosion/blaster shove glides the view instead of popping it. Stock Xonotic discards the spike
        // (it predates predicted jumppads/teleporters); this port smooths it. 0 = pop like stock. See Reconciler.
        c.Register("cl_movement_errorcompensation_force_time", "0.12", save);
        // Precache ALL weapon view-models at map load (default ON) vs only this match's expected loadout.
        // Warming all (~24) costs a little extra load time, hidden by the loading screen, but removes the
        // 30–300 ms stall the first time the player switches to / picks up / sees an unanticipated weapon
        // (PERFORMANCE_REPORT.md A3). `set cl_precache_all_weapons 0` restores the smart expected-only warm
        // for memory-constrained machines (NetGame.PrecacheWeaponModelsAsync reads it).
        c.Register("cl_precache_all_weapons", "1", save);
        // Off-screen / distant pose-cull for skeletal player models (3.3): when ON, PlayerModel.PushBones is
        // skipped for a REMOTE player whose model is off-screen, and distant on-screen players refresh the
        // Skeleton3D at half rate. The CPU locomotion clock keeps running every frame, so a model going
        // on-screen mid-stride shows a fresh (not stale) pose. Default OFF preserves the port's current
        // behaviour byte-for-byte; it's a perf opt-in (the local player's own model is NEVER skipped).
        // (§13 flip, 2026-06-12) Default ON: off-screen remote players skip the ~50-60 bone-pose interop
        // calls/frame and distant on-screen ones refresh at half rate; the local player is never culled and
        // the locomotion clock always runs (fresh pose on re-entry). `cl_pose_cull 0` restores full-rate.
        c.Register("cl_pose_cull", "1", save);
        // Quake units: beyond this an ON-SCREEN remote player refreshes at half rate (a per-model phase stagger
        // spreads the work). 0 disables the distance half-rate, keeping only the off-screen skip.
        c.Register("cl_pose_cull_distance", "1500", save);
        // (§13 flip, 2026-06-12) Default ON: animated MD3s morph in a vertex shader (2 uniforms/frame)
        // instead of re-uploading lerped vertex+normal buffers every frame (the 3.3 Tier-3 item). Eligible
        // models only (StandardMaterial3D surfaces — others keep the CPU path automatically); visual parity
        // verified on the stormkeep item set. `cl_gpu_morph 0` restores the CPU path.
        c.Register("cl_gpu_morph", "1", save);
        // Spawn-point idle glow + player-spawn flash (Xonotic QC autocvars; the Effects dialog binds the
        // first, makeMulti pokes the second). Stock defaults ON — SpawnPointParticles / the SPAWN effect
        // read these (absent → 0 would silently disable both).
        c.Register("cl_spawn_point_particles", "1", save);
        c.Register("cl_spawn_event_particles", "1", save);
        // Console/diagnostics verbosity (DP CF_CLIENT, NOT archived — a debug toggle shouldn't persist).
        c.Register("developer", "0");
        // Net input→movement pipeline diagnostic (dormant; NOT archived — a debug toggle shouldn't persist). `set
        // net_input_trace 1` logs the [nettrace] line every ~0.25s: client push/send → server recv/enq/batch → ENet
        // throttle/loss/rtt → predicted-vs-authoritative origin → reconcile error. The end-to-end view for diagnosing
        // movement/networking issues (it found the ENet packet-throttle spawn-stutter). See NET-DEBUGGING.md.
        c.Register("net_input_trace", "0");
    }

    /// <summary>
    /// The stair-step view-smoothing cvars so the console/menu can see and drive them. <c>cl_stairsmoothspeed</c>
    /// (the Base default, 200) already ships in xonotic-client.cfg; the two <c>cl_stairsmooth_*</c> knobs are PORT
    /// EXTENSIONS that tame the stair "jitter" (NetGame reads all three live each frame via ClientNet.ConfigureStairSmoothing):
    /// <list type="bullet">
    ///   <item><c>cl_stairsmooth_snapspeed</c> (30): airborne <c>|velocity.z|</c> above which the camera snaps to the
    ///         real Z (a jump/fall) instead of smoothing — below it the smoother survives the one-tick onground
    ///         flicker a stair step produces. Kept low so a descent snaps fast: with <c>sv_step_upspeed_max</c>
    ///         capping a step launch, the eye briefly falls after the cap, and a high threshold smoothed-then-jolted
    ///         that fall on every step (the "jittery with the cap set" report).</item>
    ///   <item><c>cl_stairsmooth_catchuptime</c> (0.1): close a step-sized lag within this time so a fast climb stays
    ///         inside the one-step clamp and never yanks the camera. 0 = fixed-speed (old) behaviour.</item>
    /// </list>
    /// </summary>
    private static void RegisterStairSmoothDefaults(CvarService c)
    {
        c.Register("cl_stairsmoothspeed", "200");
        c.Register("cl_stairsmooth_snapspeed", "30");
        c.Register("cl_stairsmooth_catchuptime", "0.1");
    }

    /// <summary>
    /// The dynamic colour-tint cvars (<see cref="Game.WorldTint"/>), so the console/menu can see and drive them.
    /// <c>r_map_tint</c>/<c>r_scene_tint</c> are <c>"r g b"</c> colours (0..1) and the matching <c>_strength</c>
    /// cvars are 0..1, where 0 = off (the default — no tint until you opt in). NOT archived: a tint set for a quick
    /// test shouldn't silently survive a restart and override every map. Maps set their own baseline via worldspawn
    /// keys; a strength cvar &gt; 0 overrides it live (e.g. <c>set r_map_tint "1 0 0"; set r_map_tint_strength 0.6</c>).
    /// </summary>
    private static void RegisterTintDefaults(CvarService c)
    {
        c.Register("r_map_tint", "1 1 1");
        c.Register("r_map_tint_strength", "0");
        c.Register("r_scene_tint", "1 1 1");
        c.Register("r_scene_tint_strength", "0");
    }

    /// <summary>
    /// Resolution + fullscreen + borderless + vsync from the <c>vid_*</c> cvars onto the window (QC vid_restart).
    /// </summary>
    public static void ApplyVideo()
    {
        CvarService c = MenuState.Cvars;
        RegisterEngineVideoDefaults(c);

        // vid_fullscreen is extended to a mode index (like vid_vsync): 0 windowed / 1 fullscreen (Godot's
        // composited borderless — the desktop compositor stays in the present path) / 2 EXCLUSIVE fullscreen.
        // (§12.7) Exclusive takes the compositor OUT of the present path on Windows, which removes the
        // biggest OS-stall class the forensics tag [external?] (rest-dominated ~100 ms frames with quiet
        // game-side numbers — DWM hiccups). Trade-offs are the classic ones: slower alt-tab, overlays may
        // flicker. 0/1 keep their stock DP meaning, so existing configs are unaffected.
        int fsMode = (int)c.GetFloat("vid_fullscreen");
        bool fullscreen = fsMode != 0;
        bool borderless = c.GetFloat("vid_borderless") != 0f;
        int w = (int)c.GetFloat("vid_width");
        int h = (int)c.GetFloat("vid_height");

        DisplayServer.WindowMode mode = fsMode switch
        {
            <= 0 => DisplayServer.WindowMode.Windowed,
            1 => DisplayServer.WindowMode.Fullscreen,
            _ => DisplayServer.WindowMode.ExclusiveFullscreen,
        };
        DisplayServer.WindowSetMode(mode);

        if (!fullscreen && w > 0 && h > 0)
            DisplayServer.WindowSetSize(new Vector2I(w, h));

        // Borderless only matters in windowed mode.
        if (!fullscreen)
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, borderless);

        // Vsync mode (vid_vsync, extended beyond DP's 0/1 — PERFORMANCE_REPORT.md B1):
        //   0 = off            — no sync; lowest latency, tears.
        //   1 = on             — classic double-buffered vsync; on a high-refresh display the per-frame work sits
        //                        right at the vblank budget, so any jitter pushes a frame past it → it waits for
        //                        the NEXT vblank → the frame time DOUBLES (the measured "drops to 60 from 160"
        //                        beat).
        //   2 = mailbox        — triple-buffered: on Vulkan (Forward+) the GPU renders UNCAPPED and PRESENTS the
        //                        latest complete frame at vblank. No tearing AND no beat-doubling — the single
        //                        best pacing fix for high-refresh displays. Recommended.
        //   3 = adaptive       — vsync when fast enough, off (tear) when a frame is late, instead of stuttering.
        // Drivers that don't support a mode fall back gracefully.
        DisplayServer.VSyncMode vsync = (int)c.GetFloat("vid_vsync") switch
        {
            <= 0 => DisplayServer.VSyncMode.Disabled,
            1 => DisplayServer.VSyncMode.Enabled,
            2 => DisplayServer.VSyncMode.Mailbox,
            _ => DisplayServer.VSyncMode.Adaptive,
        };
        DisplayServer.WindowSetVsyncMode(vsync);
        // Log the REQUESTED vs ACTUAL vsync mode — drivers can silently reject a mode (esp. Mailbox in windowed
        // mode), and the FrameProfiler env banner reads the mode BEFORE this runs, so this is the authoritative line.
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[video] vid_vsync {(int)c.GetFloat("vid_vsync")} → requested {vsync}, actual {DisplayServer.WindowGetVsyncMode()}");

        // Framerate cap (DP cl_maxfps): 0 = unlimited. Previously read by the menu but never enforced, so the
        // game ran uncapped — variable frame times beat against the fixed 72 Hz input/prediction tick and showed
        // as micro-stutter. Honouring the cap lets the player pin a steady framerate (a multiple of 72 is ideal).
        // Guidance (B1): with vsync off, a cap slightly UNDER the worst-case sustainable rate is smoother than
        // uncapped, and it bounds the per-frame Godot-interop alloc rate (godot#105750) that an uncapped 300–600
        // fps would multiply into a Gen0 GC treadmill. With vid_vsync 2 (mailbox) leave it at 0 (uncapped).
        int maxFps = (int)c.GetFloat("cl_maxfps");
        Godot.Engine.MaxFps = maxFps > 0 ? maxFps : 0;

        // (§12.7) OS-stall resistance: lift the process to ABOVE_NORMAL so background work (AV scans, the
        // indexer, browsers) can't preempt the game's main/render threads mid-frame — the CPU-side half of
        // the [external?] rest-stall class (the compositor half is vid_fullscreen 2). Deliberately NOT High:
        // High can starve audio/driver threads and feels worse, not better. sys_priority_boost 0 opts out.
        // Idempotent per apply; silently ignored where the OS denies it.
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            int boost = (int)c.GetFloat("sys_priority_boost");
            var want = boost != 0
                ? System.Diagnostics.ProcessPriorityClass.AboveNormal
                : System.Diagnostics.ProcessPriorityClass.Normal;
            if (proc.PriorityClass != want)
                proc.PriorityClass = want;
            // Always log the EFFECTIVE priority + the cvar that drove it. The old "only log on change" hid the
            // state in the common cases (already-AboveNormal, or sys_priority_boost 0 → Normal-and-unchanged),
            // so a config that pinned the boost off read as silence — indistinguishable from "boost on". This
            // line now makes "is the boost on?" answerable straight from the boot log.
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"[video] process priority {proc.PriorityClass} (sys_priority_boost {boost})");
        }
        catch (Exception ex)
        {
            // Sandboxed / job-object-restricted environments can deny the change — run at stock priority,
            // but say so (an invisible no-op here would read as "the boost is on" when it isn't).
            XonoticGodot.Common.Diagnostics.Log.Info($"[video] process priority boost unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// The stock Darkplaces video-cvar defaults (vid_shared.c). Like the audio cvars these are ENGINE cvars —
    /// Darkplaces registers them in C, NOT in the .cfg tree Xonotic ships — so the port never picked up their
    /// defaults and they never entered the cvar store. The siblings (<c>vid_fullscreen</c>, <c>vid_width</c>,
    /// <c>vid_height</c>, …) only show up because xonotic-client.cfg happens to assign them as bare lines;
    /// <c>vid_vsync</c> and <c>vid_borderless</c> are assigned NOWHERE, so they were invisible to the console
    /// (cvarlist/apropos/Tab-completion all enumerate <see cref="CvarService.Names"/>) and <see cref="ApplyVideo"/>
    /// only ever read them with <see cref="ICvarService.GetFloat"/> (which returns 0 for an absent cvar without
    /// creating it — so vsync silently defaulted to OFF). Register them at boot (idempotent — keeps any value a
    /// cfg or config.cfg already set) so they're visible/bindable and reset correctly. Defaults + the archive
    /// flag (CF_ARCHIVE → Save) mirror vid_shared.c; "0" also preserves the current effective behaviour (vsync
    /// off, which matters for input latency).
    /// </summary>
    private static void RegisterEngineVideoDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // vid_vsync is extended to a mode index (see ApplyVideo): 0 off / 1 on / 2 mailbox / 3 adaptive. The port
        // default is 2 (mailbox — best frame pacing without a FIFO cascade on a missed present); it's already set
        // to 2 as a locked default in MenuState.Boot, so this idempotent Register keeps that value and just carries
        // the archive flag. A player can still set 0 (lowest input latency) / 1 / 3 from the console or video menu.
        c.Register("vid_vsync", "2", save);
        c.Register("vid_borderless", "0", save);
        // (§12.7) AboveNormal process priority by default — see ApplyVideo's priority block. 0 = stock priority.
        c.Register("sys_priority_boost", "1", save);
    }

    /// <summary>
    /// The <c>mastervolume</c> / <c>bgmvolume</c> / channel volumes (DP linear 0..1) onto the audio buses
    /// (QC snd_restart + the per-channel volume cvars). Creates per-channel buses (Weapon/Voice/Player/Ambient)
    /// on first call if they don't exist in the project layout.
    /// </summary>
    public static void ApplyAudio()
    {
        EnsureChannelBuses();

        CvarService c = MenuState.Cvars;
        RegisterEngineAudioDefaults(c);
        // All three go through ChannelVol's "unset → full" guard too: these are DP ENGINE cvars (registered in
        // C, not the .cfg tree), so without RegisterEngineAudioDefaults they'd read 0 → SetBusVolume would MUTE
        // the Master/Music/SFX buses (the "all volume defaults are 0" bug). The guard is belt-and-suspenders.
        SetBusVolume("Master", ChannelVol(c, "mastervolume"));
        SetBusVolume("Music", ChannelVol(c, "bgmvolume"));
        // The "effects" bus stands in for the weapon/voice/item channels; use the loudest typical channel.
        SetBusVolume("SFX", ChannelVol(c, "snd_channel0volume"));

        // Per-channel buses (DP snd_channel<N>volume cvars → dedicated buses).
        // Default to 1.0 (full volume) when the cvar is unset (Xonotic's stock default.cfg sets these to 1).
        SetBusVolume("Weapon", ChannelVol(c, "snd_channel1volume"));
        SetBusVolume("Voice", ChannelVol(c, "snd_channel2volume"));
        SetBusVolume("Player", ChannelVol(c, "snd_channel7volume"));
        // Ambient inherits from the general effects channel (snd_channel0volume).
        SetBusVolume("Ambient", ChannelVol(c, "snd_channel0volume"));
    }

    /// <summary>
    /// The stock Darkplaces audio-cvar defaults (snd_main.c). These are ENGINE cvars — Darkplaces registers them
    /// in C, NOT in the .cfg tree Xonotic ships — so this port never picked up their defaults and every volume
    /// cvar read 0, leaving the menu sliders at 0% and (because <see cref="SetBusVolume"/> mutes at ≤0) the audio
    /// buses muted. Register them at boot (idempotent — <see cref="ICvarService.Register"/> keeps any value a cfg
    /// or the user's config.cfg already set, so overrides still win) so the defaults are authentic and the menu
    /// shows/resets them correctly. Values + flags (CF_ARCHIVE → Save) mirror snd_main.c verbatim.
    /// </summary>
    private static void RegisterEngineAudioDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        c.Register("mastervolume", "0.7", save);   // master volume
        c.Register("volume", "0.7", save);         // sound-effects volume
        c.Register("bgmvolume", "1", save);        // background-music volume
        c.Register("snd_staticvolume", "1", save); // ambient/static sounds
        // Per-entity-channel multipliers snd_channel0volume..snd_channel9volume (8/9 are QC music/ambient).
        for (int ch = 0; ch <= 9; ch++)
            c.Register($"snd_channel{ch}volume", "1", save);
        // Output cvars the audio dialog also displays (cosmetic here — Godot drives its own mixer).
        c.Register("snd_speed", "48000", save);
        c.Register("snd_channels", "2", save);
        c.Register("snd_swapstereo", "0", save);
        c.Register("snd_spatialization_control", "0", save);
        c.Register("snd_mutewhenidle", "1", save);

        // Distance-attenuation curve (ClientWorld reads these live to spatialize 3D sounds — see DpDistanceGain).
        // Defaults = Xonotic's shipped "new style" method 1 (binds-xonotic.cfg `snd_attenuation_method_1`:
        // menu_snd_attenuation_method 1 → radius 2400 / exponent 4 / decibel 0), NOT the Quake default
        // (1200/1/0, a too-flat linear ramp). Exponent 4 makes distant sounds fall off steeply so far-away
        // explosions go quiet. Tunable at runtime: e.g. `set snd_attenuation_exponent 2` (gentler) or the
        // decibel method `set snd_attenuation_exponent 0; set snd_attenuation_decibel 10` (radius 1200).
        c.Register("snd_soundradius", "2400", save);
        c.Register("snd_attenuation_exponent", "4", save);
        c.Register("snd_attenuation_decibel", "0", save);
        c.Register("menu_snd_attenuation_method", "1", save);
    }

    /// <summary>
    /// Ensure per-channel audio buses exist as children of Master. Safe to call multiple times (no-op if
    /// the buses already exist). These provide independent volume control for weapon/voice/player sounds.
    /// </summary>
    private static void EnsureChannelBuses()
    {
        // The Godot project ships no bus layout (.tres), so only the default "Master" bus exists — every other
        // bus this client routes to (MusicPlayer → "Music"; BusForChannel → "SFX"/"Weapon"/"Voice"/"Player"/
        // "Ambient") must be created here. Without "Music"/"SFX" their volume sliders silently no-op and those
        // players route to Master with a Godot "invalid bus" warning, so create the full set, all under Master.
        EnsureBus("Music", "Master");
        EnsureBus("SFX", "Master");
        EnsureBus("Weapon", "Master");
        EnsureBus("Voice", "Master");
        EnsureBus("Player", "Master");
        EnsureBus("Ambient", "Master");
    }

    /// <summary>Create a bus with the given name as a child of <paramref name="parentBus"/>, if it doesn't already exist.</summary>
    private static void EnsureBus(string busName, string parentBus)
    {
        if (AudioServer.GetBusIndex(busName) >= 0)
            return; // already exists
        int count = AudioServer.BusCount;
        AudioServer.AddBus(count);
        AudioServer.SetBusName(count, busName);
        AudioServer.SetBusSend(count, parentBus);
    }

    /// <summary>Read a per-channel volume cvar, defaulting to 1.0 (full) when unset (Xonotic stock default).</summary>
    private static float ChannelVol(CvarService c, string cvarName)
    {
        float v = c.GetFloat(cvarName);
        // If the cvar was never set (returns 0 from an empty store), treat as full volume (DP default = 1).
        // A user who explicitly set 0 gets mute via SetBusVolume's mute logic — but the cvar string would be
        // "0" not "", so we check for a truly absent cvar (GetString returns "").
        if (v <= 0f && string.IsNullOrEmpty(c.GetString(cvarName)))
            return 1f;
        return v;
    }

    /// <summary>Convert a linear 0..1 volume to dB and set it on the named bus, if that bus exists (0 = mute).</summary>
    private static void SetBusVolume(string busName, float linear)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0)
            return;
        bool mute = linear <= 0.0001f;
        AudioServer.SetBusMute(idx, mute);
        if (!mute)
            AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(Mathf.Clamp(linear, 0f, 1f)));
    }
}
