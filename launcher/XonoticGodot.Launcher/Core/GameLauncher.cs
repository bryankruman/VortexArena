using System.Diagnostics;

namespace XonoticGodot.Launcher.Core;

/// <summary>Spawns the installed game. Fat installs need nothing (DataPaths.Resolve finds
/// assets/data beside the exe); core installs get --data pointing into the shared store
/// (Main.cs --data → Shell.DataPath).</summary>
public sealed class GameLauncher(InstallService installs)
{
    public Process Launch(InstalledState state, IReadOnlyList<string>? extraArgs = null)
    {
        var gameDir = installs.GameDirOf(state);
        var exe = Path.Combine(gameDir, PlatformKey.ExecutableRelativePath(state.PlatformKey));
        if (!File.Exists(exe))
            throw new FileNotFoundException($"game binary missing — reinstall from the launcher", exe);

        if (!OperatingSystem.IsWindows())
            TryMarkExecutable(exe); // ZipFile extraction does not restore the +x bit

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = false };
        var dataDir = installs.AssetsDataDirOf(state);
        if (dataDir is not null)
        {
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(dataDir);
        }
        foreach (var a in extraArgs ?? [])
            psi.ArgumentList.Add(a);

        return Process.Start(psi)
            ?? throw new InvalidOperationException("the game process failed to start");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // caller guards on IsWindows()
    private static void TryMarkExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException) { }
        catch (IOException) { }
    }
}
