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

    private static void Apply()
    {
        // Release aggressively: either the focus-out edge OR the live query saying "not focused" is enough to
        // free the pointer. The live query also covers a background launch, where want-capture can be requested
        // before any focus-out edge has been delivered.
        bool focused = _focused && DisplayServer.WindowIsFocused();
        Input.MouseModeEnum target = _wantCapture && focused
            ? Input.MouseModeEnum.Captured
            : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != target)
            Input.MouseMode = target;
    }
}
