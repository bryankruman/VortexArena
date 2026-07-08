using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XonoticGodot.Launcher.Core;

namespace XonoticGodot.Launcher.ViewModels;

/// <summary>The launcher's one screen. State rules (ADR-0015 §6): Play is enabled whenever a
/// version is installed and nothing is mid-install — feed failures only change the status line,
/// never the Play button.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly CompositeFeed _feed;
    private readonly InstallService _installs;
    private readonly GameLauncher _game;
    private readonly SelfUpdateService _selfUpdate = new();
    private readonly string _platformKey = PlatformKey.Current;

    private ReleaseManifest? _latest;
    private CancellationTokenSource? _installCts;

    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _installedText = "not installed";
    [ObservableProperty] private string _latestText = "checking…";
    [ObservableProperty] private string _notesTitle = "Release notes";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _busy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private InstalledState? _installed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _updateAvailable;

    public MainWindowViewModel()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherConfig.UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var paths = new LauncherPaths();
        _feed = new CompositeFeed(new ManifestFeed(http), new GitHubApiFeed(http));
        _installs = new InstallService(paths, new DownloadService(http));
        _game = new GameLauncher(_installs);

        Installed = _installs.LoadCurrent();
        InstalledText = Installed is null ? "not installed" : $"{Installed.Version} ({Installed.Layout})";
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _ = RunSelfUpdateAsync(); // fire-and-forget; inert for unpackaged dev builds
        await RefreshAsync();
    }

    private async Task RunSelfUpdateAsync()
    {
        var msg = await _selfUpdate.CheckAndApplyAsync(CancellationToken.None);
        StatusText = $"{StatusText}  ·  {msg}";
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        StatusText = "Checking for updates…";
        LatestText = "checking…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (manifest, detail) = await _feed.FetchLatestAsync(cts.Token);
            _latest = manifest;

            if (manifest is null)
            {
                LatestText = "unknown (offline?)";
                StatusText = Installed is null
                    ? $"Can't reach the release feed ({detail})."
                    : $"Can't reach the release feed — you can still play {Installed.Version}.";
                UpdateAvailable = false;
                return;
            }

            LatestText = manifest.Version + (manifest.Prerelease ? " (pre-release)" : "");
            NotesTitle = $"Release notes — {manifest.Tag}";
            NotesText = string.IsNullOrWhiteSpace(manifest.NotesBody)
                ? $"Notes: {manifest.NotesUrl}"
                : manifest.NotesBody!;

            var plat = manifest.PlatformFor(_platformKey);
            if (plat is null || (plat.Fat is null && plat.Core is null))
            {
                UpdateAvailable = false;
                StatusText = $"{manifest.Tag} has no downloadable {_platformKey} package.";
                return;
            }

            UpdateAvailable = Installed is null || Installed.Version != manifest.Version;
            StatusText = UpdateAvailable
                ? Installed is null
                    ? $"Ready to install {manifest.Version}."
                    : $"Update available: {Installed.Version} → {manifest.Version}."
                : $"Up to date ({manifest.Version}).";
        }
        catch (Exception ex)
        {
            LatestText = "check failed";
            StatusText = $"Update check failed: {ex.Message}";
            UpdateAvailable = false;
        }
    }

    private bool CanRefresh() => !Busy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_latest is null)
            return;
        Busy = true;
        ProgressVisible = true;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(string Phase, double Fraction)>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = p.Fraction > 0
                    ? $"{p.Phase} {_latest.Version}… {p.Fraction:P0}"
                    : $"{p.Phase} {_latest.Version}…";
            });
            // Prefer the split payload when the release carries it (ADR-0015 §4); fat otherwise.
            Installed = await _installs.InstallAsync(_latest, _platformKey,
                preferCore: true, progress, _installCts.Token);
            InstalledText = $"{Installed.Version} ({Installed.Layout})";
            UpdateAvailable = false;
            StatusText = $"Installed {Installed.Version} — ready to play.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update cancelled (partial download kept — it resumes next time).";
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            ProgressVisible = false;
            _installCts = null;
        }
    }

    private bool CanUpdate() => UpdateAvailable && !Busy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _installCts?.Cancel();

    private bool CanCancel() => Busy;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (Installed is null)
            return;
        try
        {
            _game.Launch(Installed);
            StatusText = $"Launched XonoticGodot {Installed.Version}. Have fun!";
        }
        catch (Exception ex)
        {
            StatusText = $"Launch failed: {ex.Message}";
        }
    }

    private bool CanPlay() => Installed is not null && !Busy;
}
