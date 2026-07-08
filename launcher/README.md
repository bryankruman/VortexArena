# XonoticGodot Launcher

The install/update/play shell for XonoticGodot — design + rationale in
[ADR-0015](../planning/decisions/ADR-0015-launcher-updater.md). Avalonia UI, .NET 8,
Velopack for the launcher's *own* updates; game installs are launcher-managed plain zips
pulled from [GitHub Releases](https://github.com/bryankruman/XonoticGodot/releases).

## Run (dev)

```bash
dotnet run --project launcher/XonoticGodot.Launcher                 # the UI
dotnet run --project launcher/XonoticGodot.Launcher -- --smoke      # headless feed/paths check
dotnet test launcher/XonoticGodot.Launcher.Tests                    # unit tests
```

Dev builds are NOT Velopack-installed, so self-update is inert (`UpdateManager.IsInstalled`
guard) — everything else works, including real game installs into
`%LOCALAPPDATA%/XonoticGodot/Launcher` (`~/.local/share/…` on Linux).

## Map

| Piece | File | Job |
|---|---|---|
| Feeds | `Core/ReleaseFeeds.cs` | `latest.json` via `/releases/latest/download` (no API quota) → GitHub API fallback (sees prereleases) |
| Manifest | `Core/Manifest.cs` | `latest.json` model (emitted by `tools/make-manifest.py` in the release job) |
| Download | `Core/DownloadService.cs` | resumable (Range), sha256-verified — refuses checksum-less files |
| Install | `Core/InstallService.cs` | staging extract → atomic move → `current.json` flip; keeps N-1 for rollback; shared content-addressed asset store for `-core` installs |
| Launch | `Core/GameLauncher.cs` | spawns the game; `--data <store>` for core installs (fat installs self-resolve) |
| Self-update | `Core/SelfUpdateService.cs` | Velopack against the same repo's releases |

Invariants (ADR-0015 §6): never gate Play on the network; verify before swap; resume
interrupted downloads; keep the previous version.

## Packaging the launcher itself (deferred — ADR-0015 §7)

Velopack packages ship on the same `v*` release as the game (not wired into release.yml yet):

```bash
dotnet publish launcher/XonoticGodot.Launcher -c Release -r win-x64 --self-contained -o pub
vpk pack -u XonoticGodotLauncher -v <ver> -p pub -e XonoticGodotLauncher.exe
```

## Known prototype gaps

- macOS: `System.IO.Compression` doesn't restore symlinks, and the fat/core macOS zips contain
  an `.app` with framework symlinks — the macOS install path needs `ditto`/`unzip` before it's real.
- No settings UI (channel pinning, install-root override) — `LauncherPaths` accepts an override, nothing exposes it.
- Release notes render as plain text (no markdown).
- Manifest signing (minisign) is the gate before this becomes the default install path — ADR-0015 cut list.
