using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace A3Ctl;

public sealed record PidRecord(int Pid, long StartTicks, DateTime StartedAtUtc, string LogFile);

public sealed record Runtime(bool Running, PidRecord? Record, TimeSpan? Uptime);

public sealed record LaunchSpec(string Executable, List<string> Arguments, string WorkingDirectory, Dictionary<string, string> Environment);

// Servers and headless clients are started directly (no screen/tmux), in their own
// session so they outlive a3ctl, with stdout+stderr appended to a timestamped log file.
// The PID and the kernel's process start time are recorded so status and stop never act
// on a recycled PID.
public sealed partial class Servers(App app)
{
    [GeneratedRegex("""^\s*password\s*=\s*"((?:[^"]|"")*)"\s*;""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CfgPasswordPattern();

    // The join password from a server's server.cfg (Arma escapes " as ""), or null.
    public string? CfgPassword(string id)
    {
        var server = app.Server(id);
        var cfg = server.Config is null ? null : Path.Combine(app.ServerDir(id), server.Config);
        if (cfg is null || !File.Exists(cfg))
        {
            return null;
        }

        var match = CfgPasswordPattern().Match(File.ReadAllText(cfg));
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value.Replace("\"\"", "\"") : null;
    }

    // The non-headless server a headless client connecting to host:port would reach on
    // this box, if any.
    public string? LocalServerOn(string host, int port) =>
        host is "127.0.0.1" or "localhost"
            ? app.Config.Servers.Where(s => !s.Value.IsHeadless && s.Value.Port == port).Select(s => s.Key).FirstOrDefault()
            : null;

    // A starter server.cfg/basic.cfg when the server folder has none, so a new server can
    // start right away. Existing files are never touched. Returns the files written.
    public List<string> CreateStarterFiles(string id)
    {
        var s = app.Server(id);
        var dir = app.ServerDir(id);
        Directory.CreateDirectory(dir);
        var written = new List<string>();

        if (s.Config is not null && !File.Exists(Path.Combine(dir, s.Config)))
        {
            File.WriteAllText(Path.Combine(dir, s.Config), $$"""
                // Arma 3 server config, see https://community.bistudio.com/wiki/Arma_3:_Server_Config_File
                hostname = "{{id}}";
                password = "";
                passwordAdmin = "";
                serverCommandPassword = "";
                maxPlayers = 64;
                persistent = 1;
                verifySignatures = 2;
                BattlEye = 0;
                headlessClients[] = { "127.0.0.1" };
                localClient[] = { "127.0.0.1" };

                """);
            written.Add(Path.Combine(dir, s.Config));
        }

        if (s.Basic is not null && !File.Exists(Path.Combine(dir, s.Basic)))
        {
            File.WriteAllText(Path.Combine(dir, s.Basic), """
                // Network tuning, see https://community.bistudio.com/wiki/Arma_3:_Basic_Server_Config_File
                // Empty means Arma's defaults.

                """);
            written.Add(Path.Combine(dir, s.Basic));
        }

        return written;
    }

    public void ForgetRuntime(string id)
    {
        if (File.Exists(PidFile(id)))
        {
            File.Delete(PidFile(id));
        }
    }

    private string RunDir => Path.Combine(app.StateDir, "run");

    private string PidFile(string id) => Path.Combine(RunDir, $"{id}.json");

    public string LogDir(string id) => Path.Combine(app.LogsDir, id);

    public Runtime GetRuntime(string id)
    {
        var path = PidFile(id);
        if (!File.Exists(path))
        {
            return new Runtime(false, null, null);
        }

        var record = JsonSerializer.Deserialize<PidRecord>(File.ReadAllText(path), ConfigFile.Json);
        if (record is null || !OperatingSystem.IsLinux() || Native.StartTicks(record.Pid) != record.StartTicks)
        {
            return new Runtime(false, record, null);
        }

        return new Runtime(true, record, Native.Uptime(record.StartTicks));
    }

    public LaunchSpec BuildLaunch(string id)
    {
        var s = app.Server(id);
        var serverDir = app.ServerDir(id);
        var args = new List<string>();

        if (s.IsHeadless)
        {
            ConfigFile.TryParseEndpoint(s.Connect!, out var host, out var port);
            args.Add("-client");
            args.Add($"-connect={host}");
            args.Add($"-port={port}");
            // Joining a server on this box: its server.cfg has the password, so it is not
            // kept in two places.
            var password = s.Password ?? (LocalServerOn(host, port) is { } target ? CfgPassword(target) : null);
            if (!string.IsNullOrEmpty(password))
            {
                args.Add($"-password={password}");
            }
        }
        else
        {
            args.Add($"-port={s.Port}");
            if (s.Config is not null)
            {
                args.Add($"-config={Path.Combine(serverDir, s.Config)}");
            }
        }

        if (s.Basic is not null)
        {
            args.Add($"-cfg={Path.Combine(serverDir, s.Basic)}");
        }

        args.Add($"-profiles={Path.Combine(serverDir, "profile")}");
        args.Add($"-name={id}");

        var mods = new Mods(app);
        var clientMods = s.Dlc.Concat(app.ModsOf(s.Modsets).Select(mods.ModDir)).ToList();
        if (clientMods.Count > 0)
        {
            args.Add($"-mod={string.Join(';', clientMods)}");
        }

        var serverMods = app.ModsOf(s.ServerModsets).Select(mods.ModDir).ToList();
        if (serverMods.Count > 0)
        {
            args.Add($"-serverMod={string.Join(';', serverMods)}");
        }

        args.AddRange(s.Args);

        // Runs from the server folder; Instances links the binary and game data into it.
        var exe = Path.IsPathRooted(s.Exe) ? s.Exe : Path.Combine(serverDir, s.Exe);
        return new LaunchSpec(exe, args, serverDir, s.Env);
    }

    // Everything that would make a start fail, checked up front.
    public List<string> Preflight(string id, State state)
    {
        var s = app.Server(id);
        var launch = BuildLaunch(id);
        var problems = new List<string>();
        var mods = new Mods(app);

        // The server folder's links are created on start, so check the install itself.
        var installDir = app.InstallDir(s.Install);
        var installExe = Path.IsPathRooted(s.Exe) ? s.Exe : Path.Combine(installDir, s.Exe);
        if (!File.Exists(installExe)) problems.Add($"executable not found: {installExe} (`a3ctl install update {s.Install}`)");
        if (new Instances(app).Problem(id) is { } layout) problems.Add(layout);
        if (!s.IsHeadless && s.Config is not null && !File.Exists(Path.Combine(app.ServerDir(id), s.Config)))
        {
            problems.Add($"server config not found: {Path.Combine(app.ServerDir(id), s.Config)}");
        }

        if (s.Basic is not null && !File.Exists(Path.Combine(app.ServerDir(id), s.Basic)))
        {
            problems.Add($"basic config not found: {Path.Combine(app.ServerDir(id), s.Basic)}");
        }

        foreach (var dlc in s.Dlc.Where(d => !Directory.Exists(Path.Combine(installDir, d))))
        {
            problems.Add($"DLC folder '{dlc}' not found in {installDir} (is the install on the creatordlc branch?)");
        }

        foreach (var mod in app.ModsOf(s.Modsets.Concat(s.ServerModsets)).Where(m => !mods.IsInstalled(m, state)))
        {
            problems.Add($"mod {mod} is not installed (run `a3ctl mods update`)");
        }

        if (!s.IsHeadless)
        {
            foreach (var (otherId, other) in app.Config.Servers.Where(o => o.Key != id && !o.Value.IsHeadless))
            {
                if (Math.Abs(other.Port - s.Port) < 5 && GetRuntime(otherId).Running)
                {
                    problems.Add($"port {s.Port} clashes with running server '{otherId}' on port {other.Port} (Arma uses port..port+4)");
                }
            }
        }

        return problems;
    }

    public async Task StartAsync(string id, CancellationToken ct)
    {
        Native.RequireLinux("starting servers");
        if (GetRuntime(id).Running)
        {
            Ui.Ok($"{id} is already running");
            return;
        }

        var problems = Preflight(id, app.LoadState());
        if (problems.Count > 0)
        {
            throw new UserException($"cannot start {id}:\n  " + string.Join("\n  ", problems));
        }

        new Instances(app).Ensure(id);

        var launch = BuildLaunch(id);
        Directory.CreateDirectory(LogDir(id));
        Directory.CreateDirectory(Path.Combine(app.ServerDir(id), "profile"));
        Directory.CreateDirectory(RunDir);
        var logFile = Path.Combine(LogDir(id), $"{id}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

        // setsid detaches the server from our session (no SIGHUP when the SSH session ends)
        // and, because we are not a process group leader, execs in place so the PID we get
        // is the server's own PID. sh does the log redirection and then execs Arma.
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            WorkingDirectory = launch.WorkingDirectory,
        };
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$0\" \"$@\" >>\"$A3CTL_LOG\" 2>&1 </dev/null");
        psi.ArgumentList.Add(launch.Executable);
        foreach (var arg in launch.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in launch.Environment)
        {
            psi.Environment[key] = value;
        }

        psi.Environment["A3CTL_LOG"] = logFile;

        using var process = Process.Start(psi)!;
        var ticks = Native.StartTicks(process.Id)
            ?? throw new UserException($"{id} exited immediately, see {logFile}");
        var record = new PidRecord(process.Id, ticks, DateTime.UtcNow, logFile);
        Files.WriteAtomic(PidFile(id), JsonSerializer.Serialize(record, ConfigFile.Json));

        // Bad arguments, missing libraries and port clashes kill Arma within seconds.
        for (var i = 0; i < 10 && !process.HasExited; i++)
        {
            await Task.Delay(500, ct);
        }

        if (process.HasExited)
        {
            File.Delete(PidFile(id));
            var tail = File.Exists(logFile) ? Files.Tail(logFile, 15) : [];
            throw new UserException(
                $"{id} exited right after starting (code {process.ExitCode}). Last log lines from {logFile}:\n    " +
                string.Join("\n    ", tail));
        }

        Ui.Ok($"{id} started, pid {process.Id}, log {logFile}");
    }

    public async Task StopAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var runtime = GetRuntime(id);
        if (!runtime.Running)
        {
            if (File.Exists(PidFile(id)))
            {
                File.Delete(PidFile(id));
            }

            Ui.Ok($"{id} is not running");
            return;
        }

        Native.RequireLinux("stopping servers");
        var record = runtime.Record!;
        Native.Signal(record.Pid, Native.SIGTERM);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Native.StartTicks(record.Pid) == record.StartTicks)
        {
            await Task.Delay(500, ct);
        }

        if (Native.StartTicks(record.Pid) == record.StartTicks)
        {
            Ui.Warn($"{id} did not stop within {timeout.TotalSeconds:0}s, killing it");
            Native.Signal(record.Pid, Native.SIGKILL);
            for (var i = 0; i < 20 && Native.StartTicks(record.Pid) == record.StartTicks; i++)
            {
                await Task.Delay(250, ct);
            }

            if (Native.StartTicks(record.Pid) == record.StartTicks)
            {
                throw new UserException($"{id} (pid {record.Pid}) survived SIGKILL; check it by hand");
            }
        }

        File.Delete(PidFile(id));
        Ui.Ok($"{id} stopped");
    }

    public string? LatestLog(string id)
    {
        var dir = LogDir(id);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.log").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
    }

    // Servers before their headless clients when starting; the reverse when stopping.
    public List<string> Ordered(IEnumerable<string> ids, bool forStart)
    {
        var list = ids.Distinct().ToList();
        foreach (var id in list)
        {
            app.Server(id);
        }

        var ordered = list.OrderBy(id => app.Config.Servers[id].IsHeadless).ToList();
        if (!forStart)
        {
            ordered.Reverse();
        }

        return ordered;
    }
}
