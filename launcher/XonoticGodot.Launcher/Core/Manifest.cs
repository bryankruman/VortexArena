using System.Text.Json;
using System.Text.Json.Serialization;

namespace XonoticGodot.Launcher.Core;

/// <summary>One downloadable zip in the release manifest. <see cref="Root"/> is the zip's internal
/// top-level directory (tools/package.sh zips dist/&lt;target&gt;/, e.g. "windows-client").</summary>
public sealed record ManifestFile(string Name, string? Root, long Size, string Sha256, string Url);

/// <summary>A platform's packages: <see cref="Fat"/> = binary + runtime + all game data;
/// <see cref="Core"/> = binary + runtime only (pairs with the shared assets pack).</summary>
public sealed record ManifestPlatform(ManifestFile? Fat, ManifestFile? Core);

/// <summary>The content-addressed game-data pack. <see cref="Version"/> is the 12-char
/// download-assets.sh content hash (ADR-0015 §4) — the asset-store directory name.</summary>
public sealed record ManifestAssets(string Name, string Version, long Size, string Sha256, string Url);

/// <summary>latest.json (tools/make-manifest.py output — ADR-0015 §5), or the equivalent
/// synthesized from the GitHub Releases API by the fallback feed.</summary>
public sealed record ReleaseManifest
{
    public int Schema { get; init; } = 1;
    public required string Version { get; init; }
    public required string Tag { get; init; }
    public string Channel { get; init; } = "stable";
    public string? NotesUrl { get; init; }
    public ManifestAssets? Assets { get; init; }
    public Dictionary<string, ManifestPlatform> Platforms { get; init; } = new();

    /// <summary>Release-notes body — populated only by the API fallback feed (not in latest.json).</summary>
    [JsonIgnore] public string? NotesBody { get; init; }
    [JsonIgnore] public bool Prerelease { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ReleaseManifest? Parse(string json) =>
        JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOptions);

    public ManifestPlatform? PlatformFor(string platformKey) =>
        Platforms.TryGetValue(platformKey, out var p) ? p : null;
}
