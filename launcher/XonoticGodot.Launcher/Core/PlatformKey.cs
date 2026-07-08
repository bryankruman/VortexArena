namespace XonoticGodot.Launcher.Core;

/// <summary>Manifest platform keys and their per-OS facts (zip-name suffixes are identical to the
/// keys; roots mirror tools/package.sh + tools/make-manifest.py — keep the three in sync).</summary>
public static class PlatformKey
{
    public const string Windows = "windows-x86_64";
    public const string Linux = "linux-x86_64";
    public const string LinuxDedicated = "linux-dedicated-x86_64";
    public const string MacOS = "macos-universal";

    /// <summary>zip-name suffix → (platform key, zip internal root dir). Used by the API-fallback
    /// feed to synthesize a manifest from bare release-asset names.</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, string Root)> ZipSuffixMap =
        new Dictionary<string, (string, string)>
        {
            [Windows] = (Windows, "windows-client"),
            [Linux] = (Linux, "linux-client"),
            [LinuxDedicated] = (LinuxDedicated, "linux-dedicated"),
            [MacOS] = (MacOS, "macos-client"),
        };

    /// <summary>The key for the machine we're running on (the launcher targets clients only).</summary>
    public static string Current => Resolve(
        OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS());

    public static string Resolve(bool windows, bool linux, bool macos) =>
        windows ? Windows
        : linux ? Linux
        : macos ? MacOS
        : throw new PlatformNotSupportedException("XonoticGodot ships Windows/Linux/macOS clients only");

    /// <summary>Game binary path relative to the install's root dir (RELEASING.md "What ships").</summary>
    public static string ExecutableRelativePath(string key) => key switch
    {
        Windows => "XonoticGodot.exe",
        Linux => "XonoticGodot.x86_64",
        LinuxDedicated => "xonoticgodot-dedicated.x86_64",
        MacOS => Path.Combine("XonoticGodot.app", "Contents", "MacOS", "XonoticGodot"),
        _ => throw new ArgumentException($"unknown platform key '{key}'", nameof(key)),
    };

    /// <summary>Parse a release zip name ("XonoticGodot-&lt;version&gt;-&lt;suffix&gt;[-core].zip") given the
    /// release's version (versions can contain hyphens — 0.1.0-alpha — so the suffix can't be
    /// split out by pattern alone).</summary>
    public static bool TryParseZipName(string zipName, string version,
        out string key, out string root, out bool isCore)
    {
        key = root = ""; isCore = false;
        var prefix = $"XonoticGodot-{version}-";
        if (!zipName.StartsWith(prefix, StringComparison.Ordinal) ||
            !zipName.EndsWith(".zip", StringComparison.Ordinal))
            return false;
        var suffix = zipName[prefix.Length..^4];
        if (suffix.EndsWith("-core", StringComparison.Ordinal))
        {
            isCore = true;
            suffix = suffix[..^5];
        }
        if (!ZipSuffixMap.TryGetValue(suffix, out var m))
            return false;
        (key, root) = m;
        return true;
    }
}
