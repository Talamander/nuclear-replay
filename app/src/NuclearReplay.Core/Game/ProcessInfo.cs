using System.Diagnostics;

namespace NuclearReplay.Core.Game;

public static class ProcessInfo
{
    /// <summary>Nuclear Option is running (natively, or under Wine where Linux shows a shortened name such as "NuclearOption.e").</summary>
    public static bool IsGameRunning() => AnyProcess(name => name.StartsWith("NuclearOption", StringComparison.OrdinalIgnoreCase));

    public static bool IsSteamRunning() => AnyProcess(name =>
        name.Equals("steam", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Closes Nuclear Option (used when a render nr started is canceled). Returns how many processes were asked to stop.
    /// nr only starts a render when the game is not running, so the game it closes is the one filming the clip.
    /// </summary>
    public static int StopGame()
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception) { return 0; }
        int stopped = 0;
        foreach (var process in processes)
        {
            try
            {
                if (process.ProcessName.StartsWith("NuclearOption", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    stopped++;
                }
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return stopped;
    }

    private static bool AnyProcess(Func<string, bool> match)
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception) { return false; }
        bool found = false;
        foreach (var process in processes)
        {
            try { if (!found && match(process.ProcessName)) found = true; }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return found;
    }
}
