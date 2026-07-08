using Velopack;
using Velopack.Sources;

namespace XonoticGodot.Launcher.Core;

/// <summary>The launcher's OWN update path — Velopack against the same GitHub repo (launcher
/// packages ship on the same v* release train as the game, ADR-0015 §7). Deliberately inert
/// for unpackaged dev builds: UpdateManager.IsInstalled is false under `dotnet run`.</summary>
public sealed class SelfUpdateService
{
    public async Task<string> CheckAndApplyAsync(CancellationToken ct)
    {
        try
        {
            var mgr = new UpdateManager(
                new GithubSource(LauncherConfig.RepoUrl, accessToken: null, prerelease: true));
            if (!mgr.IsInstalled)
                return "dev build — self-update inert";

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
                return "launcher up to date";

            await mgr.DownloadUpdatesAsync(update, cancelToken: ct);
            mgr.ApplyUpdatesAndRestart(update); // exits this process
            return "restarting to update…";
        }
        catch (Exception ex)
        {
            // Self-update must never take the launcher down with it.
            return $"self-update check failed: {ex.Message}";
        }
    }
}
