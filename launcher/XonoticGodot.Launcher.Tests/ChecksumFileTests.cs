using XonoticGodot.Launcher.Core;
using Xunit;

namespace XonoticGodot.Launcher.Tests;

public class ChecksumFileTests
{
    [Fact]
    public void Parses_both_coreutils_formats_and_lowercases()
    {
        // Double-space (Linux runner) AND the binary-mode "*" marker (Git Bash on Windows —
        // exactly what tools/package.sh emits there), mixed line endings, junk line skipped.
        const string text =
            "50DDB15CB88EF6A25349EF62DF0F9E6B749D30CA90C1AA2D7112CEE819B5D101  XonoticGodot-0.1.0-linux-x86_64.zip\r\n" +
            "9d7e0c9dd561cf13a3ab1df8487d579c74a99b6102178f737d4ea19a27d6cc6d *XonoticGodot-0.1.0-windows-x86_64.zip\n" +
            "not a checksum line\n";

        var sums = ChecksumFile.Parse(text);

        Assert.Equal(2, sums.Count);
        Assert.Equal("50ddb15cb88ef6a25349ef62df0f9e6b749d30ca90c1aa2d7112cee819b5d101",
            sums["XonoticGodot-0.1.0-linux-x86_64.zip"]);
        Assert.Equal("9d7e0c9dd561cf13a3ab1df8487d579c74a99b6102178f737d4ea19a27d6cc6d",
            sums["XonoticGodot-0.1.0-windows-x86_64.zip"]);
    }

    [Fact]
    public async Task Hashes_files_streamed()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await File.WriteAllBytesAsync(path, []);
            // sha256 of the empty input — the canonical vector.
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                await ChecksumFile.Sha256OfFileAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class PlatformKeyTests
{
    [Theory]
    [InlineData(true, false, false, PlatformKey.Windows)]
    [InlineData(false, true, false, PlatformKey.Linux)]
    [InlineData(false, false, true, PlatformKey.MacOS)]
    public void Resolves_the_client_key(bool win, bool linux, bool mac, string expected) =>
        Assert.Equal(expected, PlatformKey.Resolve(win, linux, mac));

    [Theory]
    // Versions with hyphens (the real v0.1.0-alpha shape) must parse.
    [InlineData("XonoticGodot-0.1.0-alpha-windows-x86_64.zip", "0.1.0-alpha", PlatformKey.Windows, "windows-client", false)]
    [InlineData("XonoticGodot-0.2.0-linux-x86_64-core.zip", "0.2.0", PlatformKey.Linux, "linux-client", true)]
    [InlineData("XonoticGodot-0.2.0-linux-dedicated-x86_64.zip", "0.2.0", PlatformKey.LinuxDedicated, "linux-dedicated", false)]
    [InlineData("XonoticGodot-0.2.0-macos-universal-core.zip", "0.2.0", PlatformKey.MacOS, "macos-client", true)]
    public void Parses_release_zip_names(string zip, string version, string key, string root, bool core)
    {
        Assert.True(PlatformKey.TryParseZipName(zip, version, out var k, out var r, out var c));
        Assert.Equal(key, k);
        Assert.Equal(root, r);
        Assert.Equal(core, c);
    }

    [Theory]
    [InlineData("SHA256SUMS-v0.2.0.txt", "0.2.0")]
    [InlineData("XonoticGodot-assets-abc123def456.zip", "0.2.0")]
    [InlineData("XonoticGodot-0.3.0-windows-x86_64.zip", "0.2.0")] // wrong version
    public void Rejects_non_platform_zips(string zip, string version) =>
        Assert.False(PlatformKey.TryParseZipName(zip, version, out _, out _, out _));
}
