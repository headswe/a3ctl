using System.Runtime.InteropServices;

namespace A3Ctl;

// Linux-only process and filesystem helpers.
public static class Native
{
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
    private const int EXDEV = 18;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int SysLink(string oldPath, string newPath);

    public static void RequireLinux(string what)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new UserException($"{what} only works on Linux");
        }
    }

    public static bool Signal(int pid, int signal) => SysKill(pid, signal) == 0;

    // Hardlink when possible (no extra disk, instant); copy when the two paths are on
    // different filesystems or we are not on Linux.
    public static void LinkOrCopy(string source, string destination)
    {
        if (OperatingSystem.IsLinux())
        {
            if (SysLink(source, destination) == 0)
            {
                return;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != EXDEV)
            {
                throw new IOException($"link {source} -> {destination} failed (errno {errno})");
            }
        }

        File.Copy(source, destination);
    }

    // Process start time in clock ticks since boot (field 22 of /proc/<pid>/stat), or null
    // if the process does not exist. Stored with the PID so a recycled PID is never
    // mistaken for our server.
    public static long? StartTicks(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // The command name (field 2) is in parentheses and may contain spaces.
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        if (fields.Length < 20 || fields[0] == "Z")
        {
            return null;
        }

        return long.TryParse(fields[19], out var ticks) ? ticks : null;
    }

    public static TimeSpan? Uptime(long startTicks)
    {
        try
        {
            var systemUptime = double.Parse(
                File.ReadAllText("/proc/uptime").Split(' ')[0],
                System.Globalization.CultureInfo.InvariantCulture);
            // USER_HZ is 100 on every mainstream Linux build.
            return TimeSpan.FromSeconds(Math.Max(0, systemUptime - startTicks / 100.0));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
