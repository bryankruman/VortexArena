using Godot;
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
        SetBusVolume("Master", c.GetFloat("mastervolume"));
        SetBusVolume("Music", c.GetFloat("bgmvolume"));
        // The "effects" bus stands in for the weapon/voice/item channels; use the loudest typical channel.
        SetBusVolume("SFX", c.GetFloat("snd_channel0volume"));

        // Per-channel buses (DP snd_channel<N>volume cvars → dedicated buses).
        // Default to 1.0 (full volume) when the cvar is unset (Xonotic's stock default.cfg sets these to 1).
        SetBusVolume("Weapon", ChannelVol(c, "snd_channel1volume"));
        SetBusVolume("Voice", ChannelVol(c, "snd_channel2volume"));
        SetBusVolume("Player", ChannelVol(c, "snd_channel7volume"));
        // Ambient inherits from the general effects channel (snd_channel0volume).
        SetBusVolume("Ambient", ChannelVol(c, "snd_channel0volume"));
    }

    /// <summary>
    /// Ensure per-channel audio buses exist as children of Master. Safe to call multiple times (no-op if
    /// the buses already exist). These provide independent volume control for weapon/voice/player sounds.
    /// </summary>
    private static void EnsureChannelBuses()
    {
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
