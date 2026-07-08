using System.Net;
using System.Net.Http.Headers;

namespace XonoticGodot.Launcher.Core;

/// <summary>Seam between the installer and the network — the test suite substitutes a local-copy
/// double so install/swap logic is testable without HTTP.</summary>
public interface IDownloader
{
    /// <summary>Download url → destPath, resuming a partial file if present, then verify sha256.
    /// A missing checksum is a hard error: unverified bits are never handed to the installer
    /// (ADR-0015 invariant #2). On mismatch the file is deleted and the call throws.</summary>
    Task DownloadAsync(string url, string destPath, long expectedSize, string? expectedSha256,
        IProgress<double>? progress, CancellationToken ct);
}

public sealed class DownloadService(HttpClient http) : IDownloader
{
    public async Task DownloadAsync(string url, string destPath, long expectedSize,
        string? expectedSha256, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha256))
            throw new InvalidOperationException(
                $"no published checksum for {Path.GetFileName(destPath)} — refusing an unverifiable download");

        var have = File.Exists(destPath) ? new FileInfo(destPath).Length : 0;
        if (have > expectedSize && expectedSize > 0)
        {
            File.Delete(destPath); // stale junk from some other release — start over
            have = 0;
        }

        if (have < expectedSize || expectedSize <= 0)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0)
                req.Headers.Range = new RangeHeaderValue(have, null);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                have = 0; // server ignored the Range — restart from scratch
            resp.EnsureSuccessStatusCode();

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using var fs = new FileStream(destPath,
                have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20, useAsync: true);
            await using var body = await resp.Content.ReadAsStreamAsync(ct);

            var buf = new byte[1 << 16];
            long total = have;
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                if (expectedSize > 0)
                    progress?.Report(Math.Min(1.0, (double)total / expectedSize));
            }
        }

        var actual = await ChecksumFile.Sha256OfFileAsync(destPath, ct);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destPath);
            throw new InvalidOperationException(
                $"checksum mismatch for {Path.GetFileName(destPath)} " +
                $"(expected {expectedSha256[..12]}…, got {actual[..12]}…) — download discarded, try again");
        }
    }
}
