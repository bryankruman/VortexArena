using Godot;

namespace XonoticGodot.Game;

/// <summary>
/// The single owner of the OS mouse-grab state. FPS mouse-look wants the pointer <see cref="Input.MouseModeEnum.Captured"/>
/// (locked to the window); menus, the console, the pause screen, and minigame UI want it free
/// (<see cref="Input.MouseModeEnum.Visible"/>). Every subsystem declares its intent through
/// <see cref="SetWantCapture"/> instead of poking <see cref="Input.MouseMode"/> directly, so there is one place
/// that decides the real mode.
///
/// <para><b>Why this exists.</b> Capturing the cursor while the window is <em>unfocused</em> sets a system-wide
/// pointer confine (Win32 <c>ClipCursor</c>) that traps the mouse in the game's screen rectangle even while the
/// user is working in another app — and it isn't released until focus next changes. So a match started (or left
/// running) while Xonotic is in the background — a scripted/background <c>--host</c> launch, or a match that ends
/// while alt-tabbed away — would steal the cursor. This coordinator ANDs the game's desired capture with the
/// window's actual focus, so we only ever grab the pointer while Xonotic is the focused window and release it the
/// instant focus is lost. The focus edges are fed in by <see cref="Shell._Notification"/>.</para>
/// </summary>
public static class MouseCapture
{
    /// <summary>What the current game state wants: true = captured for gameplay, false = free for UI.</summary>
    private static bool _wantCapture;

    /// <summary>Window focus, tracked from the focus notifications. Defaults true; the live DisplayServer query
    /// in <see cref="Apply"/> covers the boot case where no focus edge has arrived yet (an app that starts
    /// unfocused).</summary>
    private static bool _focused = true;

    /// <summary>Declare whether the current game state wants the pointer captured (gameplay) or free (menu /
    /// console / pause / minigame UI). The pointer is actually grabbed only when this is true <em>and</em> the
    /// window is focused.</summary>
    public static void SetWantCapture(bool want)
    {
        _wantCapture = want;
        Apply();
    }

    /// <summary>Feed a window-focus edge (from <see cref="Shell._Notification"/>). Re-applies the mode so the
    /// grab is dropped the moment focus leaves and restored when it returns.</summary>
    public static void SetFocused(bool focused)
    {
        _focused = focused;
        Apply();
    }

    /// <summary>True when the game window is REALLY the focused/foreground window, per BOTH Godot's focus flag
    /// AND (on Windows) the OS itself. The Win32 cross-check matters for a window LAUNCHED into the background
    /// (a scripted <c>--host</c> while the user works in another app): Godot's internal flag can sit stale-TRUE
    /// (initialized focused; no <c>WM_KILLFOCUS</c> ever arrives because real focus was never gained), which let
    /// the spawn-frame capture set the system-wide <c>ClipCursor</c> confine and trap the pointer inside the
    /// game's window border while ANOTHER app was foreground (playtest #28). <see cref="Shell"/> polls this per
    /// frame so a stale confine also releases without needing a focus edge.</summary>
    public static bool WindowReallyFocused() => DisplayServer.WindowIsFocused() && OsForeground();

    private static bool OsForeground()
    {
        if (!System.OperatingSystem.IsWindows())
            return true;
        long h = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        return h == 0 || GetForegroundWindow() == (nint)h; // 0 = no native handle (headless) → no cross-check
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static void Apply()
    {
        // Release aggressively: the focus-out edge, the live Godot query, OR the OS foreground cross-check saying
        // "not focused" is enough to free the pointer. The live checks also cover a background launch, where
        // want-capture can be requested before any focus edge has been delivered.
        bool focused = _focused && WindowReallyFocused();
        Input.MouseModeEnum target = _wantCapture && focused
            ? Input.MouseModeEnum.Captured
            : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != target)
            Input.MouseMode = target;
    }
}
