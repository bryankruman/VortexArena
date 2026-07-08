using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace XonoticGodot.Launcher.Core;

/// <summary>SHA256SUMS-&lt;ver&gt;.txt parsing + file hashing. Handles both coreutils formats:
/// "&lt;hex&gt;  &lt;name&gt;" and the binary-mode "&lt;hex&gt; *&lt;name&gt;" (Git Bash on the Windows
/// runner emits the latter — see tools/package.sh output).</summary>
public static partial class ChecksumFile
{
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex LineRegex();

    /// <summary>name → lowercase hex sha256. Unparseable lines are skipped.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = LineRegex().Match(raw.TrimEnd('\r').Trim());
            if (m.Success)
                sums[m.Groups[2].Value] = m.Groups[1].Value.ToLowerInvariant();
        }
        return sums;
    }

    /// <summary>Streamed sha256 of a file, lowercase hex.</summary>
    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
