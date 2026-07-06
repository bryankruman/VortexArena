// The engine `messagemode`/`messagemode2` chat prompt — the DarkPlaces engine-side chat input line the
// original game uses for ALL chat (Base binds ENTER/T → messagemode, Y/Z → messagemode2; there is NO
// QC-side chat input). Reproduces its user-visible behavior: a "say:" / "say_team:" prompt line, typed
// text with a caret, ENTER sends (`say <text>` / `say_team <text>` through the same command channel a
// console line takes), ESCAPE cancels. While open, movement keys are released (DP key_dest = message)
// but mouse-look stays captured — and the input layer raises PHYS_INPUT_BUTTON_CHAT so the server shows
// the chat bubble (playtest #46/#48).
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The messagemode chat input line. Owned by the Shell (created once, lives across matches); opened by the
/// <c>messagemode</c>/<c>messagemode2</c> commands the stock binds fire (ENTER/T public, Y/Z team). The
/// submitted line goes to <see cref="Submit"/> as a full command line ("say &lt;text&gt;" / "say_team &lt;text&gt;")
/// so the host routes it exactly like a console-typed chat command.
/// </summary>
public partial class ChatPrompt : Control
{
    /// <summary>True while the prompt is open (the DP <c>key_dest == key_message</c> state). Read by the
    /// gameplay input layer: movement/fire suppressed, PHYS_INPUT_BUTTON_CHAT raised.</summary>
    public static volatile bool IsOpen;

    /// <summary>Host-wired sink for the submitted command line ("say hello" / "say_team hello").</summary>
    public System.Action<string>? Submit { get; set; }

    // DP MAX_INPUTLINE is far larger; Base chat lines are engine-truncated well below this. 255 is safe.
    private const int MaxLength = 255;

    private bool _team;
    private string _text = "";
    private float _caretPhase;

    public override void _Ready()
    {
        // Full-rect passive overlay: draws its own line, never eats mouse events (mouse-look stays live).
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        // Keep processing while the tree is paused (a solo local game auto-pauses when a UI is up) so the
        // prompt still types/closes — same reasoning as the console overlay.
        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>Open the prompt (QC/DP <c>messagemode</c> = public, <c>messagemode2</c> = team chat).</summary>
    public void Open(bool team)
    {
        _team = team;
        _text = "";
        Visible = true;
        IsOpen = true;
        QueueRedraw();
    }

    public void Close()
    {
        Visible = false;
        IsOpen = false;
        _text = "";
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _caretPhase += (float)delta;
        QueueRedraw(); // caret blink (cheap: one small Control)
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey key || !key.Pressed)
            return;

        // The prompt owns the keyboard while open (DP key_dest = message): consume EVERYTHING key-shaped so
        // binds (fire/jump/console) can't fire underneath; mouse events are untouched (mouse-look stays live).
        GetViewport().SetInputAsHandled();

        switch (key.Keycode)
        {
            case Key.Escape:
                Close();
                return;
            case Key.Enter:
            case Key.KpEnter:
                string text = _text.Trim();
                if (text.Length > 0)
                    Submit?.Invoke((_team ? "say_team " : "say ") + text);
                Close();
                return;
            case Key.Backspace:
                if (_text.Length > 0)
                    _text = _text[..^1];
                QueueRedraw();
                return;
            case Key.V when key.CtrlPressed:
                string clip = DisplayServer.ClipboardGet();
                if (!string.IsNullOrEmpty(clip))
                {
                    clip = clip.Replace("\r", "").Replace("\n", " ");
                    _text = (_text + clip);
                    if (_text.Length > MaxLength) _text = _text[..MaxLength];
                }
                QueueRedraw();
                return;
        }

        // Printable characters (the engine takes any unicode the OS delivers).
        if (key.Unicode >= 32 && _text.Length < MaxLength)
        {
            _text += char.ConvertFromUtf32((int)key.Unicode);
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (!Visible) return;
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        int fs = Mathf.Clamp((int)(GetViewportRect().Size.Y / 40f), 14, 26);
        string prompt = _team ? "say_team:" : "say:";
        // DP draws the prompt at the top-left of the screen over the game view.
        var pos = new Vector2(8f, 8f + fs);
        string caret = (int)(_caretPhase * 2f) % 2 == 0 ? "_" : " ";
        // Faint backdrop band so the line reads over a bright sky.
        DrawRect(new Rect2(4f, 6f, GetViewportRect().Size.X * 0.6f, fs * 1.5f), new Color(0f, 0f, 0f, 0.35f));
        DrawString(font, pos, $"{prompt} {_text}{caret}", HorizontalAlignment.Left, -1f, fs,
            _team ? new Color(0.55f, 1f, 0.55f) : Colors.White);
    }
}
