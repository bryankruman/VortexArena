using System;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Game;
using XonoticGodot.Game.Net;

namespace XonoticGodot;

/// <summary>
/// Boot scene for the XonoticGodot Godot host. Prints the discovered registries (proving the C# libraries
/// load) and then launches the walkable <see cref="GameDemo"/> — which boots the gameplay/engine facade
/// and drives the ported player movement. This will grow into the real client/server bootstrap
/// (see planning/legacy/todo/phase-2..3).
/// </summary>
public partial class Main : Node
{
    public override void _Ready()
    {
        // Route the C# log facade (lib/log.qh successor, XonoticGodot.Common.Diagnostics.Log) to Godot's console so
        // every LOG_INFO/WARN/TRACE/DEBUG/SEVERE/FATAL is visible in the editor Output panel + the player
        // console — the verbosity gate is the `developer` cvar (set developer 1 → trace, 2 → debug). Wired
        // first so even boot-time logging lands. Faults (Fatal/Severe) go through GD.PushError so they also
        // register in the editor's Debugger → Errors dock; the rest render with GD.PrintRich, colour-tinted by
        // level (and any Quake `^` colour codes translated to BBCode) via Log.ToBBCode. Single Godot process =
        // single tag; "client" (a listen server shares this process — a future dedicated server would set
        // "server").
        Log.Program = "client";
        Log.Sink = static (level, line) =>
        {
            if (level is LogLevel.Fatal or LogLevel.Severe)
                GD.PushError(Log.StripColors(line));       // real fault → Errors dock + stderr/Output
            else
                GD.PrintRich(Log.ToBBCode(level, line));   // colour-tinted, leveled output in the Output panel
        };

        // Build the weapon/item/mutator/gametype catalogs by scanning the loaded assemblies
        // (reflection bootstrap; the source generator will replace this at compile time — ADR-0003).
        GameRegistries.Bootstrap();

        // Register the dynamic map/scene colour-tint global shader parameters (XonoticGodot.Game.WorldTint).
        // MUST happen before any world/skin shader that declares them compiles (i.e. before the first map loads),
        // else Godot rejects the unknown global and the surface fails to render — so it's done here, up front.
        WorldTint.EnsureRegistered();

        // Always-on frame-time + GC hitch monitor (session-wide: menu + match). Self-driving; the on-screen
        // graph + hitch logging are gated by cl_frameprofiler (debug-default-on). Added early so it spans boot.
        AddChild(new XonoticGodot.Game.Client.FrameProfiler { Name = "FrameProfiler" });

        // Boot banner through the new facade (a live end-to-end exercise of it): the summary at LOG_INFO
        // (always shown), the per-registry dump at LOG_TRACE — uncluttered at `developer 0`, and visible the
        // moment you `set developer 1`. Run with `developer 2` to also get DEBUG + source locations.
        Log.Info("=== XonoticGodot boot ===");
        Log.Info($"Weapons:   {Weapons.All.Count}");
        Log.Info($"Items:     {Items.All.Count}");
        Log.Info($"Mutators:  {Mutators.All.Count}");
        Log.Info($"GameTypes: {GameTypes.All.Count}");
        foreach (var w in Weapons.All)
            Log.Trace($"  weapon[{w.RegistryId}] {w.RegistryName} — {w.DisplayName}");

        string[] args = OS.GetCmdlineArgs();

        // `--net-loopback` runs the whole networked stack in-process (server+bot+client+render) — an
        // end-to-end exercise of the §2 netcode (auth handshake, delta snapshots, entity rendering, radar).
        if (Array.IndexOf(args, "--net-loopback") >= 0)
        {
            AddChild(new NetLoopback { Name = "NetLoopback" });
        }
        else
        {
            // The application shell: boots into the main menu front-end and owns the menu↔match lifecycle.
            // `--map <vpath>` boots straight into a no-net local match on that map, bypassing the menu (CI/dev/smoke).
            // `--connect <addr>` joins a REAL networked server (host[:port], default 26000) — the full predicted
            //   client + render + camera + HUD (NetGame, client mode).
            // `--host [map]` boots a listen server (ServerNet on a GameWorld) and self-connects a networked client;
            //   pair with `--map`/`--gametype`/`--bots N` to pick the map / gametype / bot count.
            // `--gametype <short>` (e.g. dm/ctf/rc) selects the boot gametype — drives the per-gametype
            //   map-entity filter (which conditional walls appear); defaults to "dm".
            var shell = new Shell { Name = "Shell" };
            int m = Array.IndexOf(args, "--map");
            if (m >= 0 && m + 1 < args.Length)
                shell.BootMap = args[m + 1];
            int gt = Array.IndexOf(args, "--gametype");
            if (gt >= 0 && gt + 1 < args.Length)
                shell.BootGametype = args[gt + 1];
            int ms = Array.IndexOf(args, "--menu-screen");
            if (ms >= 0 && ms + 1 < args.Length)
                shell.DebugScreen = args[ms + 1];

            // Networked boot paths (CI/dev): join a server, or host a listen server + self-connect.
            int c = Array.IndexOf(args, "--connect");
            if (c >= 0 && c + 1 < args.Length)
                shell.ConnectAddress = args[c + 1];
            if (Array.IndexOf(args, "--host") >= 0)
            {
                shell.BootHost = true;
                // `--host <map>` (an arg right after the flag that isn't another flag) is a convenient map shorthand.
                int h = Array.IndexOf(args, "--host");
                if (h + 1 < args.Length && !args[h + 1].StartsWith("--") && string.IsNullOrEmpty(shell.BootMap))
                    shell.BootMap = args[h + 1];
            }
            int b = Array.IndexOf(args, "--bots");
            if (b >= 0 && b + 1 < args.Length && int.TryParse(args[b + 1], out int bots))
                shell.BootBots = bots;
            AddChild(shell);
        }

        // Optional dev/CI visual capture: `--screenshot <path> [--screenshot-frames N]` lets the scene settle,
        // saves the rendered viewport to a PNG, and quits. Windowed only (headless renders blank — see
        // RUNNING.md "Visual capture"). An agent can then read the PNG to *see* the running game.
        MaybeCaptureScreenshot(args);
    }

    /// <summary>
    /// If launched with <c>--screenshot &lt;path&gt;</c>, attach a <see cref="ScreenshotHook"/> that waits a few
    /// rendered frames (override the count with <c>--screenshot-frames N</c>, default 90), writes the viewport
    /// to that PNG, and quits. No-op without the flag. Run WINDOWED — <c>--headless</c> captures blank.
    /// </summary>
    private void MaybeCaptureScreenshot(string[] args)
    {
        int i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 1 >= args.Length)
            return;

        int frames = 90;
        int f = Array.IndexOf(args, "--screenshot-frames");
        if (f >= 0 && f + 1 < args.Length && int.TryParse(args[f + 1], out int n) && n >= 0)
            frames = n;

        AddChild(new ScreenshotHook { Name = "ScreenshotHook", OutPath = args[i + 1], WarmupFrames = frames });
    }
}
