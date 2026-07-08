using Avalonia;
using Velopack;
using XonoticGodot.Launcher.Core;

namespace XonoticGodot.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // MUST run first (Velopack docs): handles install/uninstall/update hooks when the
        // launcher is a packaged Velopack app; a no-op for plain dev builds.
        VelopackApp.Build().Run();

        if (args.Contains("--smoke"))
            return SmokeMode.RunAsync().GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Also used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

/// <summary>Headless sanity pass (`--smoke`): resolve paths + installed state, hit the release
/// feed, report what an install would do — no UI, no downloads, exit 0 even offline.</summary>
internal static class SmokeMode
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"XonoticGodot Launcher smoke — {LauncherConfig.UserAgent}");

        var paths = new LauncherPaths();
        var platformKey = PlatformKey.Current;
        Console.WriteLine($"platform key : {platformKey}");
        Console.WriteLine($"data root    : {paths.Root}");

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherConfig.UserAgent);
        var installs = new InstallService(paths, new DownloadService(http));

        var installed = installs.LoadCurrent();
        Console.WriteLine(installed is null
            ? "installed    : (nothing)"
            : $"installed    : {installed.Version} ({installed.Layout}) → {installs.GameDirOf(installed)}");

        var feed = new CompositeFeed(new ManifestFeed(http), new GitHubApiFeed(http));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (manifest, detail) = await feed.FetchLatestAsync(cts.Token);
        if (manifest is null)
        {
            Console.WriteLine($"feed         : unreachable/empty ({detail}) — Play would still work offline");
            return 0;
        }

        Console.WriteLine($"feed         : {manifest.Tag} ({manifest.Version})"
            + (manifest.Prerelease ? " [prerelease]" : "") + $" {detail}");
        Console.WriteLine($"assets pack  : {manifest.Assets?.Name ?? "(none in this release)"}");
        var plat = manifest.PlatformFor(platformKey);
        Console.WriteLine(plat is null
            ? $"platform     : no {platformKey} package on this release"
            : $"platform     : core={(plat.Core is null ? "-" : $"{plat.Core.Name} ({plat.Core.Size / (1 << 20)} MB)")} "
              + $"fat={(plat.Fat is null ? "-" : $"{plat.Fat.Name} ({plat.Fat.Size / (1 << 20)} MB)")}");
        return 0;
    }
}
