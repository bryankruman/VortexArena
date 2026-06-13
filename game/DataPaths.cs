using Godot;

namespace XonoticGodot.Game;

/// <summary>
/// Resolves the configured content-data directory (the <c>res://assets/data</c> default, a <c>user://</c> path,
/// or an absolute OS path) to a concrete OS directory the VFS can mount. This is the single source of truth for
/// "where does the gamedir live" — the menu shell mounts it at boot (<see cref="Menu.MenuState"/>), and a
/// packaged build resolves it relative to the executable (ADR-0014).
///
/// <para>It lives in the host assembly (not <c>XonoticGodot.Formats</c>) because the resolution is
/// Godot-dependent: <see cref="ProjectSettings.GlobalizePath"/> for <c>res://</c>/<c>user://</c> and
/// <see cref="OS.GetExecutablePath"/> for the exported exe-relative layout.</para>
/// </summary>
public static class DataPaths
{
    /// <summary>
    /// Resolve the configured data path to an absolute OS directory. A <c>res://</c> path is rooted at the
    /// project directory (so <c>res://assets/data</c> finds the in-tree content repo regardless of where the
    /// checkout lives), <c>user://</c> via Godot, and an absolute path is used verbatim. Any <c>..</c> segments
    /// are collapsed. Falls back to the raw string if globalization fails.
    ///
    /// <para><b>Exported builds (the packaged-release path — ADR-0014):</b> in an exported game
    /// <c>GlobalizePath("res://")</c> returns <c>""</c>, so a default <c>res://assets/data</c> can no longer be
    /// project-rooted. The packaged layout (<c>tools/package.sh</c>) lays <c>assets/data</c> BESIDE the
    /// executable, so we resolve the relative remainder against the executable's own directory — NOT the process
    /// CWD. This is the durable fix for the "launched from the wrong directory → silent blank world" trap: a
    /// double-clicked binary, a file-manager launch, or a macOS <c>.app</c> all run with a CWD of <c>/</c> or
    /// <c>$HOME</c>, not the install dir. The macOS bundle keeps its data in
    /// <c>Contents/Resources/assets/data</c>, so <c>../Resources</c> (relative to <c>Contents/MacOS</c>) is also
    /// probed. An explicit absolute/<c>user://</c> path (e.g. the <c>--data</c> flag) always wins — only the
    /// default <c>res://</c> gets the exe-relative treatment.</para>
    /// </summary>
    public static string Resolve(string configured)
    {
        string p = string.IsNullOrWhiteSpace(configured) ? "res://assets/data" : configured.Trim();
        try
        {
            if (p.StartsWith("res://", System.StringComparison.OrdinalIgnoreCase))
            {
                string rel = p["res://".Length..];                         // e.g. "assets/data"
                string projectDir = ProjectSettings.GlobalizePath("res://");
                if (string.IsNullOrEmpty(projectDir))
                    return ResolveExported(rel);                            // exported build: no res:// root
                p = System.IO.Path.Combine(projectDir, rel);
            }
            else if (p.StartsWith("user://", System.StringComparison.OrdinalIgnoreCase))
            {
                p = ProjectSettings.GlobalizePath(p);
            }
            return System.IO.Path.GetFullPath(p);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[DataPaths] could not resolve data path '{configured}': {ex.Message}");
            return p;
        }
    }

    /// <summary>
    /// Resolve a <c>res://</c>-relative data path inside an EXPORTED build, where <c>res://</c> no longer maps to
    /// a real directory. Probes, in order: beside the executable (Windows/Linux packaged layout), the macOS
    /// <c>.app</c> <c>Contents/Resources</c> (executable lives in <c>Contents/MacOS</c>), then the CWD (the
    /// historical behaviour, kept as a back-compat last resort). Returns the first existing candidate, or — if
    /// none exists yet — the exe-relative path (the layout packaging produces), so the loader logs a sensible
    /// "mounted '…'" path even on a broken install.
    /// </summary>
    private static string ResolveExported(string rel)
    {
        string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        string[] candidates =
        {
            System.IO.Path.Combine(exeDir, rel),                                  // beside the binary
            System.IO.Path.Combine(exeDir, "..", "Resources", rel),               // macOS .app bundle
            rel,                                                                  // CWD-relative (legacy)
        };
        foreach (string cand in candidates)
        {
            string full = System.IO.Path.GetFullPath(cand);
            if (System.IO.Directory.Exists(full))
                return full;
        }
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(exeDir, rel));   // expected packaged layout
    }
}
