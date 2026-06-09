using Godot;
using XonoticGodot.Common.Services;
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

        RegisterTintDefaults(MenuState.Cvars);
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

        bool fullscreen = c.GetFloat("vid_fullscreen") != 0f;
        bool borderless = c.GetFloat("vid_borderless") != 0f;
        int w = (int)c.GetFloat("vid_width");
        int h = (int)c.GetFloat("vid_height");

        DisplayServer.WindowMode mode = fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed;
        DisplayServer.WindowSetMode(mode);

        if (!fullscreen && w > 0 && h > 0)
            DisplayServer.WindowSetSize(new Vector2I(w, h));

        // Borderless only matters in windowed mode.
        if (!fullscreen)
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, borderless);

        DisplayServer.WindowSetVsyncMode(c.GetFloat("vid_vsync") != 0f
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);

        // Framerate cap (DP cl_maxfps): 0 = unlimited. Previously read by the menu but never enforced, so the
        // game ran uncapped — variable frame times beat against the fixed 72 Hz input/prediction tick and showed
        // as micro-stutter. Honouring the cap lets the player pin a steady framerate (a multiple of 72 is ideal).
        int maxFps = (int)c.GetFloat("cl_maxfps");
        Godot.Engine.MaxFps = maxFps > 0 ? maxFps : 0;
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
