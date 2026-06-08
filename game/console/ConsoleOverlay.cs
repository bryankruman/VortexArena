using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Menu;

namespace XonoticGodot.Game.Console;

/// <summary>
/// The in-game developer console overlay — the C# successor to DP's drop-down console (<c>con_*</c> +
/// <c>Key_Console</c>). A high <see cref="CanvasLayer"/> (above the menu/HUD) holding a translucent top
/// drop-down with a scrollback <see cref="RichTextLabel"/> and an input <see cref="LineEdit"/>. Backtick
/// (<c>`</c>) toggles it; typed lines run through the shared <see cref="ConfigInterpreter"/> (the same buffer
/// that loads the <c>.cfg</c> tree), so the console interprets commands EXACTLY as a config file would —
/// <c>set</c>/<c>seta</c>/<c>alias</c>/<c>exec</c>/<c>$cvar</c> + the console/cvar builtins
/// (<see cref="ConsoleCommands"/>) + gameplay commands routed to the live server.
///
/// <para>It mirrors the whole <see cref="Log"/> stream into the scrollback (so every <c>LOG_*</c> line is
/// visible like DP), supports command history (Up/Down) and Tab completion (cvars + commands + aliases), and
/// renders Quake <c>^</c> colour codes via <see cref="Log.ToBBCode"/>.</para>
///
/// <para>Input model: <see cref="_Input"/> grabs backtick (toggle) and, while open, Escape (close) — consuming
/// both so they don't reach the game / pause menu. Other keys are left to the focused input field; the play path
/// (<c>NetGame</c>) independently freezes gameplay input on <see cref="ConsoleState.IsOpen"/> (its polled WASD is
/// not stopped by event consumption). The mouse is freed while open and restored via the host's
/// <c>shouldCaptureOnClose</c> on close.</para>
/// </summary>
public partial class ConsoleOverlay : CanvasLayer
{
    private const int MaxParagraphs = 1024;

    private PanelContainer _panel = null!;
    private RichTextLabel _output = null!;
    private ConsoleLineEdit _input = null!;

    private ConfigInterpreter? _interp;
    private ConsoleCommands? _commands;
    private Func<bool>? _shouldCaptureOnClose;

    private readonly List<string> _history = new();
    private int _histIndex = -1;                 // -1 = editing a fresh line (not navigating history)

    private Action<LogLevel, string>? _prevSink; // the sink we wrapped (restored on teardown)
    private Input.MouseModeEnum _savedMouseMode = Input.MouseModeEnum.Visible;

    /// <summary>True while the drop-down is showing (mirrors <see cref="ConsoleState.IsOpen"/>).</summary>
    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 128;                              // above the menu (10), HUD (5), connect overlay (100)
        ProcessMode = ProcessModeEnum.Always;     // usable even while the in-game menu pauses the tree
        BuildUi();

        // Mirror the whole log stream into the scrollback while keeping the existing sink (GD.PrintRich → the
        // editor Output panel). Deferred because logs can be emitted off the main thread / mid-frame.
        _prevSink = Log.Sink;
        Log.Sink = (level, line) =>
        {
            _prevSink?.Invoke(level, line);
            string bb = Log.ToBBCode(level, line);
            Callable.From(() => AppendBuffer(bb)).CallDeferred();
        };
    }

    public override void _ExitTree()
    {
        if (_prevSink != null)
            Log.Sink = _prevSink;                 // stop mirroring into a freed node
    }

    /// <summary>
    /// Wire the console to the shared command buffer + cvar store and the host hooks. Called once by
    /// <see cref="Shell"/> after the overlay is in the tree. <paramref name="localRouter"/> runs a gameplay
    /// command on the in-process listen-server world (null on a pure client → falls to
    /// <paramref name="remoteSender"/>); <paramref name="shouldCaptureOnClose"/> tells the console whether to
    /// recapture the mouse when it closes (true only when a match is live and not paused).
    /// </summary>
    public void Initialize(
        ConfigInterpreter interp,
        CvarService cvars,
        Func<string, string?>? localRouter,
        Action<string>? remoteSender,
        Func<bool> shouldCaptureOnClose)
    {
        _interp = interp;
        _shouldCaptureOnClose = shouldCaptureOnClose;
        _commands = new ConsoleCommands(interp, cvars, Print, Clear, localRouter, remoteSender);
        RegisterHostCommands(interp);
    }

    /// <summary>Engine/host actions that need the Godot front-end (DP engine commands the console exposes). Wired
    /// here (not in the Godot-free <see cref="ConsoleCommands"/>) through the menu's existing host hooks.</summary>
    private void RegisterHostCommands(ConfigInterpreter interp)
    {
        interp.RegisterCommand("quit", _ => MenuCommand.Quit?.Invoke());
        interp.RegisterCommand("exit", _ => MenuCommand.Quit?.Invoke());
        interp.RegisterCommand("disconnect", _ => MenuCommand.Disconnect?.Invoke());
        interp.RegisterCommand("connect", a =>
        {
            if (a.Count >= 2) MenuCommand.Connect?.Invoke(a[1]);
            else Print("usage: connect <address>");
        });
        interp.RegisterCommand("map", a =>
        {
            if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]);
            else Print("usage: map <name>");
        });
        interp.RegisterCommand("devmap", a => { if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]); });
        interp.RegisterCommand("vid_restart", _ => MenuCommand.VideoRestart?.Invoke());
        interp.RegisterCommand("snd_restart", _ => MenuCommand.AudioRestart?.Invoke());
        interp.RegisterCommand("togglemenu", a =>
        {
            int mode = (a.Count >= 2 && a[1] == "0") ? 0 : -1;
            MenuCommand.ToggleMenu?.Invoke(mode);
        });
    }

    // =============================================================================================
    //  UI construction
    // =============================================================================================

    private void BuildUi()
    {
        _panel = new PanelContainer { Name = "ConsolePanel", Visible = false };
        // Top half of the screen, full width, auto-resizing with the window.
        _panel.AnchorLeft = 0f; _panel.AnchorTop = 0f; _panel.AnchorRight = 1f; _panel.AnchorBottom = 0.5f;
        _panel.OffsetLeft = 0f; _panel.OffsetTop = 0f; _panel.OffsetRight = 0f; _panel.OffsetBottom = 0f;
        var bg = new StyleBoxFlat { BgColor = new Color(0.04f, 0.04f, 0.06f, 0.92f) };
        bg.ContentMarginLeft = bg.ContentMarginRight = 8f;
        bg.ContentMarginTop = bg.ContentMarginBottom = 6f;
        _panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(_panel);

        var vbox = new VBoxContainer();
        _panel.AddChild(vbox);

        _output = new RichTextLabel
        {
            Name = "ConsoleOutput",
            BbcodeEnabled = true,
            ScrollActive = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            FocusMode = Control.FocusModeEnum.None,    // never steal focus from the input line
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(_output);

        _input = new ConsoleLineEdit
        {
            Name = "ConsoleInput",
            PlaceholderText = "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClearButtonEnabled = false,
        };
        _input.TextSubmitted += OnSubmit;
        _input.HistoryPrev = HistoryPrev;
        _input.HistoryNext = HistoryNext;
        _input.CompleteTab = OnTab;
        vbox.AddChild(_input);

        AppendBuffer("[color=#888888]XonoticGodot console. Type [/color][color=#cccccc]help[/color][color=#888888] for a hint, [/color][color=#cccccc]`[/color][color=#888888] to close.[/color]");
    }

    // =============================================================================================
    //  open / close
    // =============================================================================================

    public override void _Input(InputEvent @event)
    {
        // Backtick toggles the console anywhere — consume it so it doesn't type a `, open the pause menu, or
        // leak into gameplay.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Quoteleft })
        {
            Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!IsOpen)
            return;
        // While open, Escape closes the console (instead of opening the pause menu — Shell never sees it).
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen)
            return;
        _panel.Visible = true;
        ConsoleState.IsOpen = true;               // freezes the play path's polled input + fires release-all
        _savedMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _input.Clear();
        _histIndex = -1;
        _input.GrabFocus();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        _panel.Visible = false;
        ConsoleState.IsOpen = false;
        _input.ReleaseFocus();
        // Recapture the mouse only if a match is live and not paused; otherwise restore what we saved (menu).
        bool capture = _shouldCaptureOnClose?.Invoke() ?? false;
        Input.MouseMode = capture ? Input.MouseModeEnum.Captured : _savedMouseMode;
    }

    // =============================================================================================
    //  output
    // =============================================================================================

    /// <summary>Append a line of command/server output (may carry Quake <c>^</c> colour codes).</summary>
    public void Print(string line) => AppendBuffer(Log.ToBBCode(LogLevel.Info, line));

    /// <summary>Clear the scrollback (the <c>clear</c> command).</summary>
    public void Clear() => _output.Clear();

    /// <summary>Append one already-BBCode-formatted line, trimming the oldest paragraphs past the cap.</summary>
    private void AppendBuffer(string bbcode)
    {
        if (_output == null || !GodotObject.IsInstanceValid(_output))
            return; // a deferred log line raced node teardown
        _output.AppendText(bbcode + "\n");
        while (_output.GetParagraphCount() > MaxParagraphs)
            _output.RemoveParagraph(0);
    }

    // =============================================================================================
    //  submit / history / completion
    // =============================================================================================

    private void OnSubmit(string text)
    {
        _input.Clear();
        _histIndex = -1;
        _input.GrabFocus();
        if (string.IsNullOrWhiteSpace(text))
            return;

        AppendBuffer($"[color=#7faaff]]{EscapeBb(text)}[/color]"); // DP echoes the typed line as ]command
        PushHistory(text);

        if (_interp == null)
        {
            Print("console not initialised");
            return;
        }
        try
        {
            _interp.ExecuteLine(text);
        }
        catch (Exception ex)
        {
            AppendBuffer($"[color=#ff5555]error: {EscapeBb(ex.Message)}[/color]");
        }
    }

    private void PushHistory(string line)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
            _history.Add(line);
        if (_history.Count > 256)
            _history.RemoveAt(0);
    }

    private void HistoryPrev()
    {
        if (_history.Count == 0)
            return;
        _histIndex = Math.Max(0, (_histIndex < 0 ? _history.Count : _histIndex) - 1);
        SetInputText(_history[_histIndex]);
    }

    private void HistoryNext()
    {
        if (_history.Count == 0 || _histIndex < 0)
            return;
        _histIndex++;
        if (_histIndex >= _history.Count)
        {
            _histIndex = -1;
            SetInputText("");
        }
        else
        {
            SetInputText(_history[_histIndex]);
        }
    }

    private bool OnTab()
    {
        if (_commands == null)
            return false;
        string text = _input.Text;
        int lastSpace = text.LastIndexOf(' ');
        string head = lastSpace < 0 ? "" : text.Substring(0, lastSpace + 1);
        string token = lastSpace < 0 ? text : text.Substring(lastSpace + 1);
        if (token.Length == 0)
            return true;

        CompletionResult r = ConsoleCommands.Complete(token, _commands.CompletionNames());
        if (r.Matches.Count == 0)
            return true;
        if (r.Matches.Count == 1)
        {
            SetInputText(head + r.Completed + " ");
            return true;
        }
        SetInputText(head + r.Completed);
        AppendBuffer(string.Join("  ", r.Matches.Take(48).Select(EscapeBb)));
        if (r.Matches.Count > 48)
            AppendBuffer($"[color=#888888]…and {r.Matches.Count - 48} more[/color]");
        return true;
    }

    private void SetInputText(string s)
    {
        _input.Text = s;
        _input.CaretColumn = s.Length;
    }

    /// <summary>Escape literal <c>[</c> so user text / command echoes aren't parsed as BBCode tags.</summary>
    private static string EscapeBb(string s) => s.IndexOf('[') < 0 ? s : s.Replace("[", "[lb]");
}

/// <summary>
/// The console input line — a <see cref="LineEdit"/> that intercepts Up/Down (history), Tab (completion), and
/// lets Enter submit. Handled in <see cref="_GuiInput"/> with <see cref="Control.AcceptEvent"/> so Tab doesn't
/// move focus and the arrows don't just move the caret.
/// </summary>
public partial class ConsoleLineEdit : LineEdit
{
    public Action? HistoryPrev;
    public Action? HistoryNext;
    public Func<bool>? CompleteTab;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } k)
            return;
        switch (k.Keycode)
        {
            case Key.Up:
                HistoryPrev?.Invoke();
                AcceptEvent();
                break;
            case Key.Down:
                HistoryNext?.Invoke();
                AcceptEvent();
                break;
            case Key.Tab:
                if (CompleteTab?.Invoke() ?? false)
                    AcceptEvent();
                break;
        }
    }
}
