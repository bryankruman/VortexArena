using System.IO.Compression;
using XonoticGodot.Launcher.Core;
using Xunit;

namespace XonoticGodot.Launcher.Tests;

/// <summary>The install lifecycle against a real filesystem, with the network replaced by a
/// url→local-file copy stub: extract → swap → current.json flip → prune → core asset store.</summary>
public sealed class InstallServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "xglauncher-tests", Path.GetRandomFileName());
    private readonly LauncherPaths _paths;
    private readonly StubDownloader _net = new();
    private readonly InstallService _installs;

    public InstallServiceTests()
    {
        _paths = new LauncherPaths(Path.Combine(_tmp, "root"));
        _installs = new InstallService(_paths, _net);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Fat_install_extracts_swaps_and_records_current()
    {
        var m = MakeManifest("0.2.0", MakeFatZip("0.2.0"));

        var state = await _installs.InstallAsync(m, PlatformKey.Windows,
            preferCore: false, progress: null, CancellationToken.None);

        Assert.Equal("0.2.0", state.Version);
        Assert.Equal(InstalledState.LayoutFat, state.Layout);
        Assert.True(File.Exists(Path.Combine(_installs.GameDirOf(state), "XonoticGodot.exe")));
        Assert.Null(_installs.AssetsDataDirOf(state));

        // current.json round-trips, and staging holds no leftovers.
        Assert.Equal(state, _installs.LoadCurrent());
        Assert.Empty(Directory.GetFileSystemEntries(_paths.StagingDir));
    }

    [Fact]
    public async Task Corrupt_download_is_rejected_before_anything_is_swapped()
    {
        var zip = MakeFatZip("0.2.0");
        var m = MakeManifest("0.2.0", zip with { Sha256 = new string('0', 64) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _installs.InstallAsync(
            m, PlatformKey.Windows, preferCore: false, progress: null, CancellationToken.None));

        Assert.Null(_installs.LoadCurrent());
        Assert.False(Directory.Exists(Path.Combine(_paths.VersionsDir, "0.2.0")));
    }

    [Fact]
    public async Task Prune_keeps_current_plus_one_for_rollback()
    {
        foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0" })
        {
            await _installs.InstallAsync(MakeManifest(v, MakeFatZip(v)), PlatformKey.Windows,
                preferCore: false, progress: null, CancellationToken.None);
            await Task.Delay(20); // separate LastWriteTime ticks for the prune ordering
        }

        var kept = Directory.GetDirectories(_paths.VersionsDir).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(["0.2.0", "0.3.0"], kept.Order());
        Assert.Equal("0.3.0", _installs.LoadCurrent()!.Version);
    }

    [Fact]
    public async Task Core_install_populates_the_shared_store_and_reuses_it()
    {
        var m = MakeManifest("0.2.0", core: MakeCoreZip("0.2.0"), assets: MakeAssetsPack());

        var state = await _installs.InstallAsync(m, PlatformKey.Windows,
            preferCore: true, progress: null, CancellationToken.None);

        Assert.Equal(InstalledState.LayoutCore, state.Layout);
        var dataDir = _installs.AssetsDataDirOf(state);
        Assert.NotNull(dataDir);
        Assert.True(File.Exists(Path.Combine(dataDir!, "somefile.txt")));

        // A second core install (new game version, same assets hash) downloads NO assets.
        _net.Downloads.Clear();
        var m2 = MakeManifest("0.3.0", core: MakeCoreZip("0.3.0"), assets: MakeAssetsPack());
        var state2 = await _installs.InstallAsync(m2, PlatformKey.Windows,
            preferCore: true, progress: null, CancellationToken.None);

        Assert.Equal("abc123def456", state2.AssetsVersion);
        Assert.DoesNotContain(_net.Downloads, u => u.Contains("assets"));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private ManifestFile MakeFatZip(string version) =>
        MakeZip($"XonoticGodot-{version}-windows-x86_64.zip",
            ("windows-client/XonoticGodot.exe", $"fake exe {version}"),
            ("windows-client/assets/data/xonotic-data.pk3dir/somefile.txt", "fake data"));

    private ManifestFile MakeCoreZip(string version) =>
        MakeZip($"XonoticGodot-{version}-windows-x86_64-core.zip",
            ("windows-client/XonoticGodot.exe", $"fake exe {version}"));

    private ManifestAssets MakeAssetsPack()
    {
        var f = MakeZip("XonoticGodot-assets-abc123def456.zip",
            ("assets/data/somefile.txt", "fake shared data"));
        return new ManifestAssets(f.Name, "abc123def456", f.Size, f.Sha256, f.Url);
    }

    private ManifestFile MakeZip(string name, params (string Path, string Content)[] entries)
    {
        var src = Path.Combine(_tmp, "srczips", name);
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.Delete(src); // same fixture zip may be built twice (e.g. one assets pack, two versions)
        using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        var url = $"https://test.invalid/{name}";
        _net.Map[url] = src;
        var sha = ChecksumFile.Sha256OfFileAsync(src).GetAwaiter().GetResult();
        return new ManifestFile(name, "windows-client", new FileInfo(src).Length, sha, url);
    }

    private static ReleaseManifest MakeManifest(string version, ManifestFile? fat = null,
        ManifestFile? core = null, ManifestAssets? assets = null) => new()
    {
        Version = version,
        Tag = $"v{version}",
        Assets = assets,
        Platforms = { [PlatformKey.Windows] = new ManifestPlatform(fat, core) },
    };

    /// <summary>IDownloader double: copies a mapped local file, honoring the real service's
    /// verify-then-hand-over contract (mismatch deletes + throws, like DownloadService).</summary>
    private sealed class StubDownloader : IDownloader
    {
        public Dictionary<string, string> Map { get; } = new();
        public List<string> Downloads { get; } = new();

        public async Task DownloadAsync(string url, string destPath, long expectedSize,
            string? expectedSha256, IProgress<double>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(Map[url], destPath, overwrite: true);
            progress?.Report(1.0);
            var actual = await ChecksumFile.Sha256OfFileAsync(destPath, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destPath);
                throw new InvalidOperationException("checksum mismatch (stub)");
            }
        }
    }
}
