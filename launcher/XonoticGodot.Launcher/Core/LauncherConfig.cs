namespace XonoticGodot.Launcher.Core;

/// <summary>The one place the distribution endpoints live (ADR-0015 §5).</summary>
public static class LauncherConfig
{
    public const string Repo = "bryankruman/XonoticGodot";
    public const string RepoUrl = $"https://github.com/{Repo}";

    /// <summary>Stable channel: the newest FULL release's manifest via the /releases/latest
    /// redirect — a plain HTTP fetch, no API call, no rate limit.</summary>
    public const string LatestManifestUrl = $"{RepoUrl}/releases/latest/download/latest.json";

    /// <summary>Fallback/beta: the Releases API listing (rate-limited 60/hr unauthenticated —
    /// never the default path). Also the only path that sees prereleases.</summary>
    public const string ReleasesApiUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=10";

    public static string UserAgent =>
        $"XonoticGodot-Launcher/{typeof(LauncherConfig).Assembly.GetName().Version?.ToString(3) ?? "dev"}";
}

/// <summary>Launcher-owned disk layout (ADR-0015 §6), rooted under LocalApplicationData
/// (%LOCALAPPDATA% / ~/.local/share / ~/Library/Application Support).</summary>
public sealed class LauncherPaths
{
    public string Root { get; }

    public LauncherPaths(string? rootOverride = null) =>
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XonoticGodot", "Launcher");

    public string GameDir => Path.Combine(Root, "game");
    /// <summary>One extracted install per version — current + N-1 kept for rollback.</summary>
    public string VersionsDir => Path.Combine(GameDir, "versions");
    /// <summary>Download + extract scratch; safe to delete whole at any time the launcher isn't installing.</summary>
    public string StagingDir => Path.Combine(GameDir, "staging");
    public string CurrentJsonPath => Path.Combine(GameDir, "current.json");
    /// <summary>Shared content-addressed asset packs (core layout only): store/&lt;hash12&gt;/assets/data.</summary>
    public string AssetStoreDir => Path.Combine(Root, "assets", "store");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(AssetStoreDir);
    }
}
