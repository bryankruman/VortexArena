using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XonoticGodot.Launcher.Core;

public interface IReleaseFeed
{
    string Name { get; }
    Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct);
}

/// <summary>The default path (ADR-0015 §5): latest.json off the newest FULL release via the
/// /releases/latest/download redirect. Plain HTTP, no API quota. Returns null (not an error)
/// while no stable release carries a manifest — the API fallback covers that window.</summary>
public sealed class ManifestFeed(HttpClient http) : IReleaseFeed
{
    public string Name => "latest.json (stable channel)";

    public async Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct)
    {
        using var resp = await http.GetAsync(LauncherConfig.LatestManifestUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null; // no stable release yet, or it predates latest.json
        resp.EnsureSuccessStatusCode();
        return ReleaseManifest.Parse(await resp.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>The fallback (and the only path that sees prereleases): list releases via the GitHub
/// API and synthesize a manifest from the newest non-draft release's assets, taking checksums
/// from its SHA256SUMS file, else from GitHub's own per-asset sha256 digest.</summary>
public sealed partial class GitHubApiFeed(HttpClient http) : IReleaseFeed
{
    public string Name => "GitHub Releases API (fallback)";

    public async Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct)
    {
        var json = await http.GetStringAsync(LauncherConfig.ReleasesApiUrl, ct);
        var release = PickLatest(json);
        if (release is null)
            return null;

        // The SHA256SUMS file is small — fetch it for checksums when present.
        var sums = new Dictionary<string, string>();
        var sumsAsset = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("SHA256SUMS-", StringComparison.Ordinal));
        if (sumsAsset is not null)
        {
            try { sums = ChecksumFile.Parse(await http.GetStringAsync(sumsAsset.BrowserDownloadUrl, ct)); }
            catch (HttpRequestException) { /* digests below still cover us */ }
        }
        return Synthesize(release, sums);
    }

    /// <summary>Newest non-draft release from a /releases listing (prereleases included).</summary>
    public static ApiRelease? PickLatest(string releasesJson) =>
        JsonSerializer.Deserialize<List<ApiRelease>>(releasesJson, ReleaseManifest.JsonOptions)?
            .FirstOrDefault(r => !r.Draft);

    /// <summary>Build a manifest from bare release assets. Checksum precedence: SHA256SUMS entry,
    /// then the GitHub asset digest. A file with NO checksum from either source is dropped —
    /// the installer never installs unverified bits (ADR-0015 invariant #2).</summary>
    public static ReleaseManifest Synthesize(ApiRelease release, IReadOnlyDictionary<string, string> sums)
    {
        var version = release.TagName.TrimStart('v');
        var fat = new Dictionary<string, ManifestFile>();
        var core = new Dictionary<string, ManifestFile>();
        ManifestAssets? assetsPack = null;

        foreach (var a in release.Assets)
        {
            var sha = sums.TryGetValue(a.Name, out var s) ? s
                : a.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true
                    ? a.Digest["sha256:".Length..].ToLowerInvariant()
                    : null;
            if (sha is null)
                continue;

            if (AssetsPackName().Match(a.Name) is { Success: true } m)
            {
                assetsPack = new ManifestAssets(a.Name, m.Groups[1].Value, a.Size, sha, a.BrowserDownloadUrl);
                continue;
            }
            if (PlatformKey.TryParseZipName(a.Name, version, out var key, out var root, out var isCore))
                (isCore ? core : fat)[key] = new ManifestFile(a.Name, root, a.Size, sha, a.BrowserDownloadUrl);
        }

        var platforms = fat.Keys.Union(core.Keys).ToDictionary(
            k => k,
            k => new ManifestPlatform(fat.GetValueOrDefault(k), core.GetValueOrDefault(k)));

        return new ReleaseManifest
        {
            Version = version,
            Tag = release.TagName,
            Channel = release.Prerelease ? "prerelease" : "stable",
            NotesUrl = release.HtmlUrl,
            Assets = assetsPack,
            Platforms = platforms,
            NotesBody = release.Body,
            Prerelease = release.Prerelease,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^XonoticGodot-assets-([0-9a-f]{12})\.zip$")]
    private static partial System.Text.RegularExpressions.Regex AssetsPackName();

    public sealed record ApiRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<ApiAsset> Assets);

    public sealed record ApiAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

/// <summary>Manifest first, API fallback. Network failure → null + a reason the UI can show;
/// the caller NEVER blocks Play on this (ADR-0015 invariant #1).</summary>
public sealed class CompositeFeed(params IReleaseFeed[] feeds)
{
    public async Task<(ReleaseManifest? Manifest, string Detail)> FetchLatestAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (var feed in feeds)
        {
            try
            {
                var m = await feed.FetchLatestAsync(ct);
                if (m is not null)
                    return (m, $"via {feed.Name}");
                notes.Add($"{feed.Name}: no release found");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notes.Add($"{feed.Name}: timed out");
            }
            catch (HttpRequestException ex)
            {
                notes.Add($"{feed.Name}: {ex.Message}");
            }
        }
        return (null, string.Join("; ", notes));
    }
}
