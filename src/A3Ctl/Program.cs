using System.CommandLine;
using Spectre.Console;
using A3Ctl;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var configOption = new Option<string?>("--config", "-c")
{
    Description = "Config file (default: $A3CTL_CONFIG, ./a3ctl.json, ~/.config/a3ctl/a3ctl.json)",
    Recursive = true,
};

var verboseOption = new Option<bool>("--verbose", "-v")
{
    Description = "Show full SteamCMD output",
    Recursive = true,
};

var yesOption = new Option<bool>("--yes", "-y")
{
    Description = "Do not ask for confirmation",
    Recursive = true,
};

var root = new RootCommand("a3ctl: run Arma 3 servers, keep installs and Workshop mods up to date.");
root.Options.Add(configOption);
root.Options.Add(verboseOption);
root.Options.Add(yesOption);

// Wraps an action so user errors print as one clean message.
void Handle(Command command, Func<ParseResult, App, CancellationToken, Task<int>> action)
{
    command.SetAction(async (parse, ct) =>
    {
        Ui.Verbose = parse.GetValue(verboseOption);
        Ui.AssumeYes = parse.GetValue(yesOption);
        try
        {
            return await action(parse, App.Load(parse.GetValue(configOption)), ct);
        }
        catch (UserException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Ui.Error("cancelled");
            return 130;
        }
    });
}

Argument<string[]> ServerIds() => new("servers") { Description = "Server ids", Arity = ArgumentArity.ZeroOrMore };

List<string> PickServers(App app, string[] ids, bool all)
{
    if (all)
    {
        return app.Config.Servers.Keys.ToList();
    }

    if (ids.Length == 0)
    {
        throw new UserException("name one or more servers, or pass --all");
    }

    return ids.ToList();
}

// Asks before kicking players off running servers. False means the user said no.
bool ConfirmStop(Servers servers, IEnumerable<string> ids, string verb)
{
    var running = ids.Where(id => servers.GetRuntime(id).Running).ToList();
    return running.Count == 0 || Ui.Confirm($"{verb} {string.Join(", ", running)}? Players on them will be disconnected.");
}

async Task<int> StartAll(Servers servers, IEnumerable<string> ids, CancellationToken ct)
{
    var failed = 0;
    foreach (var id in servers.Ordered(ids, forStart: true))
    {
        try
        {
            await Ui.SpinAsync($"starting {id}", () => servers.StartAsync(id, ct));
        }
        catch (UserException ex)
        {
            Ui.Error(ex.Message);
            failed++;
        }
    }

    return failed == 0 ? 0 : 1;
}

async Task StopAll(Servers servers, IEnumerable<string> ids, int timeoutSeconds, CancellationToken ct)
{
    foreach (var id in servers.Ordered(ids, forStart: false))
    {
        await Ui.SpinAsync($"stopping {id}", () => servers.StopAsync(id, TimeSpan.FromSeconds(timeoutSeconds), ct));
    }
}

// ---------------------------------------------------------------- status

Spectre.Console.Rendering.IRenderable StatusView(App app)
{
    var servers = new Servers(app);
    var mods = new Mods(app);
    var state = app.LoadState();

    var table = Ui.BuildTable(
        ["server", "type", "port", "state", "pid", "uptime", "mods"],
        app.Config.Servers.Select(s =>
        {
            var rt = servers.GetRuntime(s.Key);
            var modCount = app.ModsOf(s.Value.Modsets).Count + app.ModsOf(s.Value.ServerModsets).Count;
            return new[]
            {
                Ui.Bold(s.Key),
                s.Value.Type,
                Ui.E(s.Value.IsHeadless ? s.Value.Connect! : s.Value.Port.ToString()),
                rt.Running ? Ui.Green("● running") : Ui.Dim("○ stopped"),
                rt.Running ? rt.Record!.Pid.ToString() : "",
                rt.Uptime is { } up ? Ui.Age(up) : "",
                $"{modCount} + {s.Value.Dlc.Count} dlc",
            };
        }));

    var missing = app.Config.Mods.Keys.Where(m => !mods.IsInstalled(m, state)).ToList();
    var modsLine = $"[bold]mods[/]      {app.Config.Mods.Count} in catalog, {app.Config.Mods.Count - missing.Count} installed" +
        (missing.Count > 0 ? ", " + Ui.Yellow($"{missing.Count} missing ({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", ..." : "")})") : "");
    var installsLine = "[bold]installs[/]  " + string.Join(", ", app.Config.Installs.Select(i =>
        $"{Ui.E(i.Key)} ({(Directory.Exists(app.InstallDir(i.Key)) ? "present" : Ui.Yellow("missing"))})"));

    return new Rows(
        app.Config.Servers.Count == 0 ? new Markup(Ui.Dim("no servers configured yet; add them under \"servers\" in the config")) : table,
        new Markup(modsLine),
        new Markup(installsLine),
        new Markup(Ui.Dim($"config    {app.ConfigPath}")));
}

async Task<int> ShowStatus(App app, bool watch, CancellationToken ct)
{
    if (!watch)
    {
        Ui.Out.Write(StatusView(app));
        return 0;
    }

    if (!Ui.Interactive)
    {
        throw new UserException("--watch needs a terminal");
    }

    try
    {
        await Ui.Out.Live(StatusView(app)).StartAsync(async live =>
        {
            while (true)
            {
                await Task.Delay(2000, ct);
                live.UpdateTarget(StatusView(app));
            }
        });
    }
    catch (OperationCanceledException)
    {
    }

    return 0;
}

var watchOption = new Option<bool>("--watch", "-w") { Description = "Keep refreshing every 2 seconds (Ctrl+C to quit)" };
var status = new Command("status", "Overview of servers, installs and mods") { watchOption };
Handle(status, (p, app, ct) => ShowStatus(app, p.GetValue(watchOption), ct));
root.Subcommands.Add(status);

// ---------------------------------------------------------------- server

var server = new Command("server", "Start, stop and inspect servers and headless clients");
root.Subcommands.Add(server);

{
    var listWatch = new Option<bool>("--watch", "-w") { Description = "Keep refreshing every 2 seconds (Ctrl+C to quit)" };
    var list = new Command("list", "List servers and whether they are running") { listWatch };
    Handle(list, (p, app, ct) => ShowStatus(app, p.GetValue(listWatch), ct));
    server.Subcommands.Add(list);

    var ids = ServerIds();
    var all = new Option<bool>("--all", "-a") { Description = "All servers and headless clients" };
    var start = new Command("start", "Start servers (servers first, then headless clients)") { ids, all };
    Handle(start, async (p, app, ct) =>
    {
        Native.RequireLinux("starting servers");
        using var _ = app.Lock("server start");
        var servers = new Servers(app);
        var targets = servers.Ordered(PickServers(app, p.GetValue(ids)!, p.GetValue(all)), forStart: true);
        SyncKeysFor(app, targets);
        return await StartAll(servers, targets, ct);
    });
    server.Subcommands.Add(start);

    var stopIds = ServerIds();
    var stopAll = new Option<bool>("--all", "-a") { Description = "All servers and headless clients" };
    var timeout = new Option<int>("--timeout", "-t") { Description = "Seconds to wait before SIGKILL", DefaultValueFactory = _ => 30 };
    var stop = new Command("stop", "Stop servers (SIGTERM, then SIGKILL after the timeout)") { stopIds, stopAll, timeout };
    Handle(stop, async (p, app, ct) =>
    {
        var servers = new Servers(app);
        var targets = PickServers(app, p.GetValue(stopIds)!, p.GetValue(stopAll));
        if (!ConfirmStop(servers, targets, "Stop"))
        {
            return 1;
        }

        await StopAll(servers, targets, p.GetValue(timeout), ct);
        return 0;
    });
    server.Subcommands.Add(stop);

    var restartIds = ServerIds();
    var restartAll = new Option<bool>("--all", "-a") { Description = "All running servers and headless clients" };
    var restart = new Command("restart", "Stop and start servers") { restartIds, restartAll, timeout };
    Handle(restart, async (p, app, ct) =>
    {
        Native.RequireLinux("restarting servers");
        using var _ = app.Lock("server restart");
        var servers = new Servers(app);
        var picked = p.GetValue(restartAll)
            ? app.Config.Servers.Keys.Where(id => servers.GetRuntime(id).Running).ToList()
            : PickServers(app, p.GetValue(restartIds)!, false);
        if (!ConfirmStop(servers, picked, "Restart"))
        {
            return 1;
        }

        await StopAll(servers, picked, p.GetValue(timeout), ct);
        SyncKeysFor(app, picked);
        return await StartAll(servers, picked, ct);
    });
    server.Subcommands.Add(restart);

    var logId = new Argument<string>("server");
    var lines = new Option<int>("--lines", "-n") { Description = "Lines to show", DefaultValueFactory = _ => 50 };
    var follow = new Option<bool>("--follow", "-f") { Description = "Keep printing new output (follows restarts)" };
    var logs = new Command("logs", "Show the latest log of a server") { logId, lines, follow };
    Handle(logs, async (p, app, ct) =>
    {
        var id = p.GetValue(logId)!;
        app.Server(id);
        var servers = new Servers(app);
        var file = servers.LatestLog(id) ?? throw new UserException($"no logs for {id} yet in {servers.LogDir(id)}");
        Ui.Err.MarkupLine(Ui.Dim($"==> {file}"));
        foreach (var line in Files.Tail(file, p.GetValue(lines)))
        {
            Console.WriteLine(line);
        }

        if (!p.GetValue(follow))
        {
            return 0;
        }

        var position = new FileInfo(file).Length;
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                var latest = servers.LatestLog(id);
                if (latest != file)
                {
                    file = latest!;
                    position = 0;
                    Ui.Err.MarkupLine(Ui.Dim($"==> {file}"));
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < position)
                {
                    position = 0;
                }

                stream.Position = position;
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(ct);
                position = stream.Length;
                Console.Write(text);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    });
    server.Subcommands.Add(logs);

    var syncIds = ServerIds();
    var syncAll = new Option<bool>("--all", "-a") { Description = "All servers and headless clients" };
    var sync = new Command("sync", "Build or refresh server folders (links to the install, keys) without starting them; start does this too") { syncIds, syncAll };
    Handle(sync, async (p, app, ct) =>
    {
        using var _ = app.Lock("server sync");
        var targets = PickServers(app, p.GetValue(syncIds)!, p.GetValue(syncAll));
        var instances = new Instances(app);
        foreach (var id in targets)
        {
            var result = instances.Ensure(id);
            if (result.Linked + result.Relinked + result.Pruned == 0)
            {
                Ui.Ok($"{id}: server folder up to date");
            }
        }

        SyncKeysFor(app, targets);
        return await Task.FromResult(0);
    });
    server.Subcommands.Add(sync);

    var cmdId = new Argument<string>("server");
    var cmd = new Command("cmd", "Print the exact command line a3ctl would launch, plus any problems") { cmdId };
    Handle(cmd, async (p, app, ct) =>
    {
        var id = p.GetValue(cmdId)!;
        var servers = new Servers(app);
        var launch = servers.BuildLaunch(id);
        Console.WriteLine($"cd {Quote(launch.WorkingDirectory)}");
        foreach (var (key, value) in launch.Environment)
        {
            Console.WriteLine($"export {key}={Quote(value)}");
        }

        Console.WriteLine(string.Join(" \\\n    ", launch.Arguments.Prepend(launch.Executable).Select(Quote)));
        var problems = servers.Preflight(id, app.LoadState());
        foreach (var problem in problems)
        {
            Ui.Warn(problem);
        }

        return await Task.FromResult(problems.Count == 0 ? 0 : 1);
    });
    server.Subcommands.Add(cmd);

    var addId = new Argument<string>("server") { Description = "Name for the server, e.g. main" };
    var addOptions = new ServerOptions();
    var noFiles = new Option<bool>("--no-files") { Description = "Do not create a starter server.cfg/basic.cfg" };
    var add = new Command("add", "Add a server, or a headless client with --for <server>") { addId, noFiles };
    addOptions.AddTo(add);
    Handle(add, async (p, app, ct) =>
    {
        using var _ = app.Lock("server add");
        var id = p.GetValue(addId)!;
        if (app.Config.Servers.ContainsKey(id))
        {
            throw new UserException($"server '{id}' already exists (change it with `a3ctl server set {id}`)");
        }

        // Next free port block; Arma uses port..port+4.
        var taken = app.Config.Servers.Values.Where(v => !v.IsHeadless).Select(v => v.Port).ToList();
        var s = new ServerConfig
        {
            Dir = $"servers/{id}",
            Install = app.Config.Installs.Count == 1 ? app.Config.Installs.Keys.First() : "",
            Port = taken.Count == 0 ? 2302 : taken.Max() + 100,
        };

        addOptions.Apply(p, s);
        if (s.IsHeadless && !ServerOptions.Given(p, addOptions.ServerCfg))
        {
            s.Config = null;
        }

        app.Config.Servers[id] = s;
        ConfigFile.FollowServers(app.Config);
        if (string.IsNullOrEmpty(s.Install))
        {
            throw new UserException(app.Config.Installs.Count == 0
                ? "add an install first with `a3ctl install add <name>`"
                : $"pass --install (one of: {string.Join(", ", app.Config.Installs.Keys)})");
        }

        app.SaveConfig();
        Ui.Ok($"added {s.Type} {id}" + (s.IsHeadless ? $", joins {s.Connect}" : $" on port {s.Port}") + $", folder {app.ServerDir(id)}");
        if (!p.GetValue(noFiles))
        {
            foreach (var file in new Servers(app).CreateStarterFiles(id))
            {
                Ui.Ok($"wrote starter {file}");
            }
        }

        if (s.Modsets.Count == 0 && s.For is null)
        {
            Ui.Line(Ui.Dim($"no mods yet: `a3ctl server set {id} --modset <modset>`"));
        }

        Ui.Line(Ui.Dim($"extra launch flags: `a3ctl server args {id} -- -exThreads=7 -hugepages`; check it with `a3ctl server cmd {id}`"));
        return await Task.FromResult(0);
    });
    server.Subcommands.Add(add);

    var setId = new Argument<string>("server");
    var setOptions = new ServerOptions();
    var set = new Command("set", "Change a server's settings (only the options you pass)") { setId };
    setOptions.AddTo(set);
    Handle(set, async (p, app, ct) =>
    {
        using var _ = app.Lock("server set");
        var id = p.GetValue(setId)!;
        setOptions.Apply(p, app.Server(id));
        app.SaveConfig();
        Ui.Ok($"updated {id}");
        if (new Servers(app).GetRuntime(id).Running)
        {
            Ui.Warn($"{id} is running; restart it to apply (`a3ctl server restart {id}`)");
        }

        return await Task.FromResult(0);
    });
    server.Subcommands.Add(set);

    var argsId = new Argument<string>("server");
    var argsValues = new Argument<string[]>("args") { Description = "Launch flags; put them after -- so they are not read as a3ctl options", Arity = ArgumentArity.ZeroOrMore };
    var argsClear = new Option<bool>("--clear") { Description = "Remove all extra flags" };
    var argsCmd = new Command("args", "Show or replace a server's extra launch flags, e.g. `a3ctl server args main -- -exThreads=7 -hugepages`") { argsId, argsValues, argsClear };
    Handle(argsCmd, async (p, app, ct) =>
    {
        var id = p.GetValue(argsId)!;
        var s = app.Server(id);
        var values = p.GetValue(argsValues)!;
        if (values.Length == 0 && !p.GetValue(argsClear))
        {
            foreach (var arg in s.Args)
            {
                Console.WriteLine(arg);
            }

            return 0;
        }

        using var _ = app.Lock("server args");
        s.Args = [.. values];
        app.SaveConfig();
        Ui.Ok(values.Length == 0 ? $"cleared extra flags of {id}" : $"{id} now launches with {string.Join(' ', values)}");
        return await Task.FromResult(0);
    });
    server.Subcommands.Add(argsCmd);

    var showId = new Argument<string>("server");
    var show = new Command("show", "Show a server's settings") { showId };
    Handle(show, async (p, app, ct) =>
    {
        var id = p.GetValue(showId)!;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(app.Server(id), ConfigFile.Json));
        Ui.Line(Ui.Dim($"folder {app.ServerDir(id)}; full command line: `a3ctl server cmd {id}`"));
        return await Task.FromResult(0);
    });
    server.Subcommands.Add(show);

    var removeId = new Argument<string>("server");
    var remove = new Command("remove", "Remove a stopped server from the config (its folder and logs are kept)") { removeId };
    Handle(remove, async (p, app, ct) =>
    {
        using var _ = app.Lock("server remove");
        var id = p.GetValue(removeId)!;
        app.Server(id);
        var servers = new Servers(app);
        if (servers.GetRuntime(id).Running)
        {
            throw new UserException($"{id} is running; stop it first");
        }

        if (!Ui.Confirm($"Remove server {id} from the config?"))
        {
            return 1;
        }

        var dir = app.ServerDir(id);
        app.Config.Servers.Remove(id);
        app.SaveConfig();
        servers.ForgetRuntime(id);
        var state = app.LoadState();
        state.ManagedKeys.Remove(id);
        app.SaveState(state);
        Ui.Ok($"removed {id}; its folder {dir} and logs are left in place");
        return await Task.FromResult(0);
    });
    server.Subcommands.Add(remove);
}

// ---------------------------------------------------------------- install

var install = new Command("install", "Arma 3 server installs (SteamCMD app 233780)");
root.Subcommands.Add(install);

{
    var list = new Command("list", "List installs and the servers that use them");
    Handle(list, async (_, app, ct) =>
    {
        var servers = new Servers(app);
        Ui.Table(
            ["install", "branch", "dir", "servers"],
            app.Config.Installs.Select(i => new[]
            {
                i.Key,
                i.Value.Branch ?? "public",
                Directory.Exists(app.InstallDir(i.Key)) ? Ui.E(app.InstallDir(i.Key)) : Ui.Yellow(app.InstallDir(i.Key) + " (missing)"),
                string.Join(", ", app.Config.Servers.Where(s => s.Value.Install == i.Key)
                    .Select(s => servers.GetRuntime(s.Key).Running ? Ui.Green(s.Key) : s.Key)),
            }));
        return await Task.FromResult(0);
    });
    install.Subcommands.Add(list);

    var installId = new Argument<string[]>("installs") { Description = "Install ids (default: all)", Arity = ArgumentArity.ZeroOrMore };
    var noValidate = new Option<bool>("--no-validate") { Description = "Skip SteamCMD file validation (faster)" };
    var update = new Command("update", "Install or update Arma 3 server files. Servers using the install must be stopped.") { installId, noValidate };
    Handle(update, async (p, app, ct) =>
    {
        using var _ = app.Lock("install update");
        var installs = new Installs(app);
        var ids = p.GetValue(installId) is { Length: > 0 } given ? given : app.Config.Installs.Keys.ToArray();
        foreach (var id in ids)
        {
            installs.Get(id);
        }

        var failed = 0;
        foreach (var id in ids)
        {
            if (!await installs.UpdateAsync(id, !p.GetValue(noValidate), ct))
            {
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    });
    install.Subcommands.Add(update);

    var addId = new Argument<string>("install") { Description = "Name for the install, e.g. profiling" };
    var addDir = new Option<string?>("--dir") { Description = "Install folder (default: installs/<name> next to the config). An existing Arma install there is reused." };
    var addBranch = new Option<string?>("--branch") { Description = "Steam branch, e.g. creatordlc (needed for gm, vn, ws, ...) or profiling (default: public)" };
    var addBranchPassword = new Option<string?>("--branch-password") { Description = "Password of a protected branch" };
    var addUpdate = new Option<bool>("--update") { Description = "Download it right away" };
    var add = new Command("add", "Add an Arma 3 server install") { addId, addDir, addBranch, addBranchPassword, addUpdate };
    Handle(add, async (p, app, ct) =>
    {
        using var _ = app.Lock("install add");
        var id = p.GetValue(addId)!;
        if (!ConfigFile.IdPattern().IsMatch(id))
        {
            throw new UserException($"install name '{id}' must be lowercase letters, digits, '-' or '_'");
        }

        var installs = new Installs(app);
        installs.Add(id, p.GetValue(addDir), p.GetValue(addBranch), p.GetValue(addBranchPassword));
        return p.GetValue(addUpdate) && !await installs.UpdateAsync(id, validate: true, ct) ? 1 : 0;
    });
    install.Subcommands.Add(add);

    var removeId = new Argument<string>("install");
    var removeFiles = new Option<bool>("--delete-files") { Description = "Also delete the install folder" };
    var remove = new Command("remove", "Remove an install no server uses") { removeId, removeFiles };
    Handle(remove, async (p, app, ct) =>
    {
        using var _ = app.Lock("install remove");
        var id = p.GetValue(removeId)!;
        var installs = new Installs(app);
        installs.Get(id);
        if (!Ui.Confirm(p.GetValue(removeFiles)
                ? $"Remove install {id} and delete {app.InstallDir(id)}?"
                : $"Remove install {id} from the config (files are kept)?"))
        {
            return 1;
        }

        installs.Remove(id, p.GetValue(removeFiles));
        return await Task.FromResult(0);
    });
    install.Subcommands.Add(remove);

    var setId = new Argument<string>("install");
    var setDir = new Option<string?>("--dir") { Description = "Install folder (the files are not moved)" };
    var setBranch = new Option<string?>("--branch") { Description = "Steam branch, or 'public'" };
    var setBranchPassword = new Option<string?>("--branch-password") { Description = "Password of a protected branch, or '' to clear" };
    var set = new Command("set", "Change an install's folder or branch") { setId, setDir, setBranch, setBranchPassword };
    Handle(set, async (p, app, ct) =>
    {
        using var _ = app.Lock("install set");
        var id = p.GetValue(setId)!;
        var install = new Installs(app).Get(id);
        if (ServerOptions.Given(p, setDir)) install.Dir = p.GetValue(setDir)!;
        if (ServerOptions.Given(p, setBranch)) install.Branch = p.GetValue(setBranch) is null or "public" or "" ? null : p.GetValue(setBranch);
        if (ServerOptions.Given(p, setBranchPassword)) install.BranchPassword = p.GetValue(setBranchPassword) is { Length: > 0 } pw ? pw : null;
        app.SaveConfig();
        Ui.Ok($"updated install {id} ({install.Branch ?? "public"} branch, {app.InstallDir(id)})");
        if (ServerOptions.Given(p, setBranch))
        {
            Ui.Line(Ui.Dim($"switch the files over with `a3ctl install update {id}`"));
        }

        return await Task.FromResult(0);
    });
    install.Subcommands.Add(set);
}

// ---------------------------------------------------------------- mods

var mods = new Command("mods", "Workshop mod catalog: add, download, update");
root.Subcommands.Add(mods);

{
    var setFilter = new Option<string?>("--modset", "-m") { Description = "Only mods in this modset" };
    var list = new Command("list", "List mods in the catalog") { setFilter };
    Handle(list, async (p, app, ct) =>
    {
        var m = new Mods(app);
        var state = app.LoadState();
        var names = app.Config.Mods.Keys.AsEnumerable();
        if (p.GetValue(setFilter) is { } set)
        {
            names = app.Config.Modsets.TryGetValue(set, out var members) ? members : throw new UserException($"unknown modset '{set}'");
        }

        Ui.Table(
            ["mod", "workshop", "title", "downloaded", "modsets"],
            names.Select(name =>
            {
                var cfg = app.Config.Mods[name];
                state.Mods.TryGetValue(name, out var st);
                var installed = m.IsInstalled(name, state);
                return new[]
                {
                    installed ? name : Ui.Yellow(name),
                    cfg.WorkshopId?.ToString() ?? Ui.Dim("local"),
                    Ui.E(st?.Title ?? ""),
                    cfg.WorkshopId is null ? (installed ? "present" : Ui.Yellow("missing")) :
                        installed ? st!.DownloadedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Ui.Yellow("not downloaded"),
                    string.Join(",", m.ModsetsUsing(name)),
                };
            }));
        return await Task.FromResult(0);
    });
    mods.Subcommands.Add(list);

    var addItems = new Argument<string[]>("items") { Description = "Workshop IDs or URLs", Arity = ArgumentArity.OneOrMore };
    var addName = new Option<string?>("--name") { Description = "Folder name, e.g. @cba_a3 (default: from the Workshop title; single item only)" };
    var addSets = new Option<string[]>("--modset", "-m") { Description = "Also add to these modsets (created if missing)", AllowMultipleArgumentsPerToken = true };
    var addDownload = new Option<bool>("--download", "-d") { Description = "Download right away" };
    var add = new Command("add", "Add Workshop mods to the catalog") { addItems, addName, addSets, addDownload };
    Handle(add, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods add");
        var ids = p.GetValue(addItems)!.Select(WorkshopApi.ParseId).Distinct().ToList();
        var name = p.GetValue(addName) is { } given ? ModName(given) : null;
        if (name is not null && ids.Count != 1)
        {
            throw new UserException("--name only works when adding a single item");
        }

        var added = AddToCatalog(app, ids, name, p.GetValue(addSets) ?? [],
            await WorkshopApi.GetDetailsAsync(ids, ct));
        app.SaveConfig();
        return await DownloadIfAsked(app, added, p.GetValue(addDownload), ct);
    });
    mods.Subcommands.Add(add);

    var collection = new Argument<string>("collection") { Description = "Workshop collection ID or URL" };
    var importSets = new Option<string[]>("--modset", "-m") { Description = "Also add to these modsets (created if missing)", AllowMultipleArgumentsPerToken = true };
    var importDownload = new Option<bool>("--download", "-d") { Description = "Download right away" };
    var import = new Command("import", "Add every mod of a Workshop collection") { collection, importSets, importDownload };
    Handle(import, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods import");
        var ids = await WorkshopApi.GetCollectionAsync(WorkshopApi.ParseId(p.GetValue(collection)!), ct);
        Ui.Step($"collection has {ids.Count} items");
        var added = AddToCatalog(app, ids, null, p.GetValue(importSets) ?? [], await WorkshopApi.GetDetailsAsync(ids, ct));
        app.SaveConfig();
        return await DownloadIfAsked(app, added, p.GetValue(importDownload), ct);
    });
    mods.Subcommands.Add(import);

    var localName = new Argument<string>("name") { Description = "Folder name in the mods dir, e.g. @our_missions" };
    var localSets = new Option<string[]>("--modset", "-m") { Description = "Also add to these modsets", AllowMultipleArgumentsPerToken = true };
    var addLocal = new Command("add-local", "Register a mod folder you put into the mods dir yourself (not from the Workshop)") { localName, localSets };
    Handle(addLocal, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods add-local");
        var name = ModName(p.GetValue(localName)!);
        if (app.Config.Mods.ContainsKey(name))
        {
            throw new UserException($"{name} is already in the catalog");
        }

        app.Config.Mods[name] = new ModConfig();
        AddToModsets(app, [name], p.GetValue(localSets) ?? []);
        app.SaveConfig();
        var dir = new Mods(app).ModDir(name);
        if (!Directory.Exists(dir))
        {
            Ui.Warn($"{dir} does not exist yet; copy the mod there (lowercase file names)");
        }

        Ui.Ok($"added local mod {name}");
        return await Task.FromResult(0);
    });
    mods.Subcommands.Add(addLocal);

    var removeNames = new Argument<string[]>("mods") { Arity = ArgumentArity.OneOrMore };
    var removeFiles = new Option<bool>("--delete-files") { Description = "Also delete the mod folder and the downloaded Workshop content" };
    var remove = new Command("remove", "Remove mods from the catalog and from every modset") { removeNames, removeFiles };
    Handle(remove, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods remove");
        var names = p.GetValue(removeNames)!.Select(ModName).ToList();
        if (!Ui.Confirm(p.GetValue(removeFiles)
                ? $"Remove {string.Join(", ", names)} and delete their files?"
                : $"Remove {string.Join(", ", names)} from the catalog and every modset?"))
        {
            return 1;
        }

        var m = new Mods(app);
        var servers = new Servers(app);
        var state = app.LoadState();
        foreach (var name in names)
        {
            if (!app.Config.Mods.ContainsKey(name))
            {
                throw new UserException($"unknown mod '{name}'");
            }

            var running = m.ServersUsing(name).Where(id => servers.GetRuntime(id).Running).ToList();
            if (running.Count > 0 && p.GetValue(removeFiles))
            {
                throw new UserException($"{name} is in use by running servers: {string.Join(", ", running)}");
            }

            if (p.GetValue(removeFiles))
            {
                m.RemoveFiles(name, includeWorkshopContent: true);
            }

            foreach (var set in app.Config.Modsets.Values)
            {
                set.Remove(name);
            }

            app.Config.Mods.Remove(name);
            state.Mods.Remove(name);
            Ui.Ok($"removed {name}");
        }

        app.SaveConfig();
        app.SaveState(state);
        return await Task.FromResult(0);
    });
    mods.Subcommands.Add(remove);

    var checkSet = new Option<string?>("--modset", "-m") { Description = "Only mods in this modset" };
    var check = new Command("check", "Compare downloaded mods with the Workshop and list what needs updating") { checkSet };
    Handle(check, async (p, app, ct) =>
    {
        var plan = await PlanUpdates(app, p.GetValue(checkSet), ct);
        Ui.Table(
            ["mod", "workshop", "local version", "workshop version", "status"],
            plan.Rows);
        Ui.Out.WriteLine();
        Ui.Line(plan.Needed.Count == 0
            ? Ui.Green("all mods are up to date")
            : Ui.Yellow($"{plan.Needed.Count} mod(s) need downloading; run `a3ctl mods update`"));
        return plan.Needed.Count == 0 ? 0 : 2;
    });
    mods.Subcommands.Add(check);

    var updateSet = new Option<string?>("--modset", "-m") { Description = "Only mods in this modset" };
    var attempts = new Option<int>("--attempts") { Description = "SteamCMD attempts per mod (large mods often time out)", DefaultValueFactory = _ => 5 };
    var publishAfter = new Option<bool>("--publish") { Description = "Publish the Swifty group afterwards if anything changed" };
    var update = new Command("update", "Download every mod that is missing or outdated") { updateSet, attempts, publishAfter };
    Handle(update, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods update");
        var plan = await PlanUpdates(app, p.GetValue(updateSet), ct);
        if (plan.Needed.Count == 0)
        {
            Ui.Ok("all mods are up to date");
            return 0;
        }

        var result = await Download(app, plan.Needed, p.GetValue(attempts), ct);
        if (result == 0 && p.GetValue(publishAfter))
        {
            await new Swifty(app).PublishAsync(null, ct);
        }

        return result;
    });
    mods.Subcommands.Add(update);

    var dlNames = new Argument<string[]>("mods") { Description = "Mods to (re)download", Arity = ArgumentArity.ZeroOrMore };
    var dlAll = new Option<bool>("--all", "-a") { Description = "Every Workshop mod in the catalog" };
    var dlAttempts = new Option<int>("--attempts") { Description = "SteamCMD attempts per mod", DefaultValueFactory = _ => 5 };
    var download = new Command("download", "Force (re)download of specific mods, validating files") { dlNames, dlAll, dlAttempts };
    Handle(download, async (p, app, ct) =>
    {
        using var _ = app.Lock("mods download");
        var names = p.GetValue(dlAll)
            ? app.Config.Mods.Where(m => m.Value.WorkshopId is not null).Select(m => m.Key).ToList()
            : p.GetValue(dlNames)!.Select(ModName).ToList();
        if (names.Count == 0)
        {
            throw new UserException("name one or more mods, or pass --all");
        }

        return await Download(app, names, p.GetValue(dlAttempts), ct);
    });
    mods.Subcommands.Add(download);
}

// ---------------------------------------------------------------- modset

var modset = new Command("modset", "Named lists of mods that servers load");
root.Subcommands.Add(modset);

{
    var list = new Command("list", "List modsets");
    Handle(list, async (_, app, ct) =>
    {
        Ui.Table(
            ["modset", "mods", "used by"],
            app.Config.Modsets.Select(s => new[]
            {
                s.Key,
                s.Value.Count.ToString(),
                string.Join(", ", app.Config.Servers.Where(sv => sv.Value.Modsets.Contains(s.Key) || sv.Value.ServerModsets.Contains(s.Key)).Select(sv => sv.Key)),
            }));
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(list);

    var showSet = new Argument<string>("modset");
    var show = new Command("show", "List the mods in a modset, in load order") { showSet };
    Handle(show, async (p, app, ct) =>
    {
        var set = p.GetValue(showSet)!;
        foreach (var mod in app.Config.Modsets.TryGetValue(set, out var members) ? members : throw new UserException($"unknown modset '{set}'"))
        {
            Console.WriteLine(mod);
        }

        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(show);

    var editSet = new Argument<string>("modset");
    var editMods = new Argument<string[]>("mods") { Arity = ArgumentArity.OneOrMore };
    var addTo = new Command("add", "Add mods to a modset (created if missing)") { editSet, editMods };
    Handle(addTo, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset add");
        foreach (var mod in p.GetValue(editMods)!.Select(ModName).Where(m => !app.Config.Mods.ContainsKey(m)))
        {
            throw new UserException($"unknown mod '{mod}' (add it with `a3ctl mods add` first)");
        }

        AddToModsets(app, p.GetValue(editMods)!.Select(ModName), [p.GetValue(editSet)!]);
        app.SaveConfig();
        Ui.Ok($"modset {p.GetValue(editSet)} now has {app.Config.Modsets[p.GetValue(editSet)!].Count} mods");
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(addTo);

    var rmSet = new Argument<string>("modset");
    var rmMods = new Argument<string[]>("mods") { Arity = ArgumentArity.OneOrMore };
    var removeFrom = new Command("remove", "Remove mods from a modset") { rmSet, rmMods };
    Handle(removeFrom, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset remove");
        var set = p.GetValue(rmSet)!;
        var members = app.Config.Modsets.TryGetValue(set, out var m) ? m : throw new UserException($"unknown modset '{set}'");
        foreach (var mod in p.GetValue(rmMods)!.Select(ModName))
        {
            if (!members.Remove(mod))
            {
                Ui.Warn($"{mod} was not in {set}");
            }
        }

        app.SaveConfig();
        Ui.Ok($"modset {set} now has {members.Count} mods");
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(removeFrom);

    var delSet = new Argument<string>("modset");
    var delete = new Command("delete", "Delete a modset that no server uses") { delSet };
    Handle(delete, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset delete");
        var set = p.GetValue(delSet)!;
        var users = app.Config.Servers.Where(s => s.Value.Modsets.Contains(set) || s.Value.ServerModsets.Contains(set)).Select(s => s.Key).ToList();
        if (users.Count > 0)
        {
            throw new UserException($"modset {set} is used by {string.Join(", ", users)}");
        }

        if (!app.Config.Modsets.Remove(set))
        {
            throw new UserException($"unknown modset '{set}'");
        }

        app.SaveConfig();
        Ui.Ok($"deleted modset {set}");
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(delete);

    var createSet = new Argument<string>("modset");
    var create = new Command("create", "Create an empty modset") { createSet };
    Handle(create, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset create");
        var set = p.GetValue(createSet)!;
        if (app.Config.Modsets.ContainsKey(set))
        {
            throw new UserException($"modset {set} already exists");
        }

        AddToModsets(app, [], [set]);
        app.SaveConfig();
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(create);

    var moveSet = new Argument<string>("modset");
    var moveMod = new Argument<string>("mod");
    var moveFirst = new Option<bool>("--first") { Description = "Load it first" };
    var moveLast = new Option<bool>("--last") { Description = "Load it last" };
    var moveBefore = new Option<string?>("--before") { Description = "Load it right before this mod" };
    var moveAfter = new Option<string?>("--after") { Description = "Load it right after this mod" };
    var move = new Command("move", "Change a mod's load order within a modset") { moveSet, moveMod, moveFirst, moveLast, moveBefore, moveAfter };
    Handle(move, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset move");
        var set = p.GetValue(moveSet)!;
        var members = app.Config.Modsets.TryGetValue(set, out var m) ? m : throw new UserException($"unknown modset '{set}'");
        var mod = ModName(p.GetValue(moveMod)!);
        var anchor = p.GetValue(moveBefore) ?? p.GetValue(moveAfter);
        var choices = new[] { p.GetValue(moveFirst), p.GetValue(moveLast), p.GetValue(moveBefore) is not null, p.GetValue(moveAfter) is not null }.Count(c => c);
        if (choices != 1)
        {
            throw new UserException("pass exactly one of --first, --last, --before <mod>, --after <mod>");
        }

        if (!members.Remove(mod))
        {
            throw new UserException($"{mod} is not in {set}");
        }

        int index;
        if (p.GetValue(moveFirst)) index = 0;
        else if (p.GetValue(moveLast)) index = members.Count;
        else
        {
            var at = members.IndexOf(ModName(anchor!));
            if (at < 0)
            {
                members.Add(mod);
                throw new UserException($"{ModName(anchor!)} is not in {set}");
            }

            index = p.GetValue(moveBefore) is not null ? at : at + 1;
        }

        members.Insert(index, mod);
        app.SaveConfig();
        Ui.Ok($"{set}: {string.Join(", ", members)}");
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(move);

    var renameFrom = new Argument<string>("modset");
    var renameTo = new Argument<string>("new-name");
    var rename = new Command("rename", "Rename a modset everywhere it is used") { renameFrom, renameTo };
    Handle(rename, async (p, app, ct) =>
    {
        using var _ = app.Lock("modset rename");
        var (from, to) = (p.GetValue(renameFrom)!, p.GetValue(renameTo)!);
        if (!app.Config.Modsets.Remove(from, out var members))
        {
            throw new UserException($"unknown modset '{from}'");
        }

        if (app.Config.Modsets.ContainsKey(to))
        {
            throw new UserException($"modset {to} already exists");
        }

        app.Config.Modsets[to] = members;
        static void Swap(List<string> list, string from, string to)
        {
            var i = list.IndexOf(from);
            if (i >= 0) list[i] = to;
        }

        foreach (var s in app.Config.Servers.Values)
        {
            Swap(s.Modsets, from, to);
            Swap(s.ServerModsets, from, to);
        }

        foreach (var repo in app.Config.Swifty?.Repos.Values.AsEnumerable() ?? [])
        {
            Swap(repo.Modsets, from, to);
            Swap(repo.OptionalModsets, from, to);
        }

        app.SaveConfig();
        Ui.Ok($"renamed modset {from} to {to}");
        return await Task.FromResult(0);
    });
    modset.Subcommands.Add(rename);
}

// ---------------------------------------------------------------- keys

var keys = new Command("keys", "Mod signature keys (.bikey) in each server's keys folder");
root.Subcommands.Add(keys);

{
    var keyServers = new Argument<string[]>("servers") { Description = "Servers (default: all)", Arity = ArgumentArity.ZeroOrMore };
    var sync = new Command("sync", "Give each server the install's base keys plus the keys of its mods, and remove keys of mods it no longer uses. Also runs on every server start.") { keyServers };
    Handle(sync, async (p, app, ct) =>
    {
        using var _ = app.Lock("keys sync");
        var ids = p.GetValue(keyServers) is { Length: > 0 } given ? given : app.Config.Servers.Keys.ToArray();
        foreach (var id in ids)
        {
            app.Server(id);
        }

        SyncKeysFor(app, ids);
        return await Task.FromResult(0);
    });
    keys.Subcommands.Add(sync);
}

// ---------------------------------------------------------------- swifty

var swifty = new Command("swifty", "Fill the Swifty repository files from modsets and publish the group");
root.Subcommands.Add(swifty);

{
    void PrintIssues(List<SwiftyIssue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.IsError) Ui.Error(issue.Message);
            else Ui.Warn(issue.Message);
        }
    }

    var check = new Command("check", "Check that swifty-group.json, its repositories, the servers and installed mods agree");
    Handle(check, async (_, app, ct) =>
    {
        var s = new Swifty(app);
        var issues = s.Check();
        PrintIssues(issues);
        var errors = issues.Count(i => i.IsError);
        if (File.Exists(s.GroupFile))
        {
            Ui.Table(
                ["repository", "file", "required", "optional", "dlcs", "servers"],
                s.Repos.Select(r =>
                {
                    var managed = app.Config.Swifty!.Repos.TryGetValue(r.Id, out var cfg);
                    return new[]
                    {
                        r.Id,
                        Ui.E(r.Source),
                        managed ? s.RequiredMods(cfg!).Count.ToString() : Ui.Dim("not managed"),
                        managed ? s.OptionalMods(cfg!).Count.ToString() : "",
                        managed ? string.Join(",", s.Dlcs(r.Id)) : "",
                        string.Join(", ", s.Servers.Where(sv => sv.Repository == r.Id)
                            .Select(sv => s.LocalServer(sv) is { } local && local != sv.Id ? $"{sv.Id} ({local})" : sv.Id)),
                    };
                }));
        }

        Ui.Line(errors == 0 ? Ui.Green("ready to publish") : Ui.Red($"{errors} error(s), fix them before publishing"));
        return await Task.FromResult(errors == 0 ? 0 : 1);
    });
    swifty.Subcommands.Add(check);

    var generate = new Command("generate", "Update the mod lists in the repository files without publishing");
    Handle(generate, async (p, app, ct) =>
    {
        using var _ = app.Lock("swifty generate");
        var s = new Swifty(app);
        PrintIssues(s.Check());
        var changed = s.Generate();
        Ui.Ok(changed.Count == 0 ? $"{s.GroupDir} is up to date" : $"updated {string.Join(", ", changed)} in {s.GroupDir}");
        return await Task.FromResult(0);
    });
    swifty.Subcommands.Add(generate);

    var files = new Option<string?>("--files") { Description = "How mod files reach the web folder (default: swifty.files)" };
    files.AcceptOnlyFromAmong("link", "symlink", "copy", "none");
    var publish = new Command("publish", "Check, update repository files and run `swifty admin publish` into swifty.publishDir") { files };
    Handle(publish, async (p, app, ct) =>
    {
        using var _ = app.Lock("swifty publish");
        var s = new Swifty(app);
        PrintIssues(s.Check().Where(i => !i.IsError).ToList());
        await s.PublishAsync(p.GetValue(files), ct);
        return 0;
    });
    swifty.Subcommands.Add(publish);

    var invite = new Command("invite", "Print the swifty:// invite link for players");
    Handle(invite, async (_, app, ct) =>
    {
        await new Swifty(app).InviteAsync(ct);
        return 0;
    });
    swifty.Subcommands.Add(invite);

    var groupDir = new Option<string?>("--group-dir") { Description = "Folder with swifty-group.json" };
    var publishDir = new Option<string?>("--publish-dir") { Description = "Web folder the group is served from" };
    var url = new Option<string?>("--url") { Description = "Public URL of the group, for invite links" };
    var filesMode = new Option<string?>("--files") { Description = "How mod files reach the web folder" };
    filesMode.AcceptOnlyFromAmong("link", "symlink", "copy", "none");
    var exe = new Option<string?>("--exe") { Description = "swifty CLI executable (default: swifty)" };
    var setup = new Command("setup", "Point a3ctl at your Swifty group folder and web folder") { groupDir, publishDir, url, filesMode, exe };
    Handle(setup, async (p, app, ct) =>
    {
        using var _ = app.Lock("swifty setup");
        if (app.Config.Swifty is null)
        {
            if (p.GetValue(groupDir) is null)
            {
                throw new UserException("pass --group-dir (the folder with swifty-group.json)");
            }

            app.Config.Swifty = new SwiftyConfig();
        }

        var cfg = app.Config.Swifty;
        if (ServerOptions.Given(p, groupDir)) cfg.GroupDir = p.GetValue(groupDir)!;
        if (ServerOptions.Given(p, publishDir)) cfg.PublishDir = p.GetValue(publishDir);
        if (ServerOptions.Given(p, url)) cfg.Url = p.GetValue(url);
        if (ServerOptions.Given(p, filesMode)) cfg.Files = p.GetValue(filesMode)!;
        if (ServerOptions.Given(p, exe)) cfg.Exe = p.GetValue(exe)!;
        app.SaveConfig();

        var s = new Swifty(app);
        Ui.Ok($"swifty group folder {s.GroupDir}" + (s.PublishDir is null ? "" : $", publishing to {s.PublishDir}"));
        if (!File.Exists(s.GroupFile))
        {
            Ui.Warn($"{s.GroupFile} does not exist yet; create it with `swifty admin init`");
        }
        else
        {
            var unmapped = s.Repos.Where(r => !cfg.Repos.ContainsKey(r.Id)).Select(r => r.Id).ToList();
            if (unmapped.Count > 0)
            {
                Ui.Line(Ui.Dim($"fill repositories from modsets with `a3ctl swifty repo set {unmapped[0]} --modset <modset>` ({string.Join(", ", unmapped)} are not mapped yet)"));
            }
        }

        return await Task.FromResult(0);
    });
    swifty.Subcommands.Add(setup);

    var repo = new Command("repo", "Which modsets fill which Swifty repository");
    swifty.Subcommands.Add(repo);

    var repoId = new Argument<string>("repository") { Description = "Repository id in swifty-group.json" };
    var repoSets = new Option<string[]>("--modset", "-m") { Description = "Modsets that become requiredMods (replaces the list)", Arity = ArgumentArity.ZeroOrMore, AllowMultipleArgumentsPerToken = true };
    var repoOptional = new Option<string[]>("--optional-modset", "-o") { Description = "Modsets that become optionalMods (replaces the list)", Arity = ArgumentArity.ZeroOrMore, AllowMultipleArgumentsPerToken = true };
    var repoDlcs = new Option<string[]>("--dlc") { Description = "DLCs players need (default: those of the servers using the repository); --dlc with nothing resets to the default", Arity = ArgumentArity.ZeroOrMore, AllowMultipleArgumentsPerToken = true };
    var repoSet = new Command("set", "Set the modsets of a Swifty repository") { repoId, repoSets, repoOptional, repoDlcs };
    Handle(repoSet, async (p, app, ct) =>
    {
        using var _ = app.Lock("swifty repo set");
        var cfg = app.Config.Swifty ?? throw new UserException("run `a3ctl swifty setup --group-dir <folder>` first");
        var id = p.GetValue(repoId)!;
        var s = new Swifty(app);
        if (File.Exists(s.GroupFile) && s.Repos.All(r => r.Id != id))
        {
            throw new UserException($"{s.GroupFile} has no repository '{id}' (it has: {string.Join(", ", s.Repos.Select(r => r.Id))})");
        }

        if (!cfg.Repos.TryGetValue(id, out var r))
        {
            cfg.Repos[id] = r = new SwiftyRepoConfig();
        }

        if (ServerOptions.Given(p, repoSets)) r.Modsets = [.. p.GetValue(repoSets)!];
        if (ServerOptions.Given(p, repoOptional)) r.OptionalModsets = [.. p.GetValue(repoOptional)!];
        if (ServerOptions.Given(p, repoDlcs)) r.Dlcs = p.GetValue(repoDlcs) is { Length: > 0 } d ? [.. d] : null;
        app.SaveConfig();
        Ui.Ok($"repository {id}: required {string.Join(", ", r.Modsets)}" + (r.OptionalModsets.Count > 0 ? $"; optional {string.Join(", ", r.OptionalModsets)}" : ""));
        Ui.Line(Ui.Dim("check it with `a3ctl swifty check`"));
        return await Task.FromResult(0);
    });
    repo.Subcommands.Add(repoSet);

    var repoRemoveId = new Argument<string>("repository");
    var repoRemove = new Command("remove", "Stop filling a repository from modsets (its file is left as it is)") { repoRemoveId };
    Handle(repoRemove, async (p, app, ct) =>
    {
        using var _ = app.Lock("swifty repo remove");
        var id = p.GetValue(repoRemoveId)!;
        if (app.Config.Swifty?.Repos.Remove(id) != true)
        {
            throw new UserException($"repository {id} is not managed by a3ctl");
        }

        app.SaveConfig();
        Ui.Ok($"a3ctl no longer manages repository {id}");
        return await Task.FromResult(0);
    });
    repo.Subcommands.Add(repoRemove);
}

// ---------------------------------------------------------------- config

var config = new Command("config", "Config file helpers");
root.Subcommands.Add(config);

{
    var check = new Command("check", "Validate the config and check that paths exist");
    Handle(check, async (_, app, ct) =>
    {
        Ui.Ok($"{app.ConfigPath} is valid");
        var warnings = 0;
        void Missing(string what, string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Ui.Warn($"{what} does not exist: {path}");
                warnings++;
            }
        }

        foreach (var id in app.Config.Installs.Keys)
        {
            Missing($"install {id}", app.InstallDir(id));
        }

        var servers = new Servers(app);
        var state = app.LoadState();
        foreach (var id in app.Config.Servers.Keys)
        {
            foreach (var problem in servers.Preflight(id, state))
            {
                Ui.Warn($"{id}: {problem}");
                warnings++;
            }
        }

        foreach (var group in app.Config.Servers.Where(s => !s.Value.IsHeadless).GroupBy(s => s.Value.Port).Where(g => g.Count() > 1))
        {
            Ui.Warn($"servers {string.Join(", ", group.Select(g => g.Key))} share port {group.Key}; only one of them can run at a time");
        }

        foreach (var (id, s) in app.Config.Servers)
        {
            var overlap = app.ModsOf(s.Modsets).Intersect(app.ModsOf(s.ServerModsets)).ToList();
            if (overlap.Count > 0)
            {
                Ui.Warn($"{id}: {string.Join(", ", overlap)} are in both modsets and serverModsets");
                warnings++;
            }
        }

        if (app.Config.SteamUser is null)
        {
            Ui.Warn("steamUser is not set; anonymous SteamCMD logins cannot download Arma 3 Workshop items");
        }

        Ui.Line(warnings == 0 ? Ui.Green("no problems found") : Ui.Yellow($"{warnings} warning(s)"));
        return await Task.FromResult(0);
    });
    config.Subcommands.Add(check);

    var settingKeys = new[] { "steamCmd", "steamUser", "stateDir", "logsDir", "workshopDir", "modsDir" };
    var setKey = new Argument<string>("setting") { Description = string.Join(", ", settingKeys) };
    setKey.AcceptOnlyFromAmong(settingKeys);
    var setValue = new Argument<string>("value") { Description = "New value ('' clears steamUser)" };
    var set = new Command("set", "Change a general setting, e.g. `a3ctl config set steamUser tmf_admin`") { setKey, setValue };
    Handle(set, async (p, app, ct) =>
    {
        using var _ = app.Lock("config set");
        var (key, value) = (p.GetValue(setKey)!, p.GetValue(setValue)!);
        var c = app.Config;
        switch (key)
        {
            case "steamCmd": c.SteamCmd = value; break;
            case "steamUser": c.SteamUser = value.Length == 0 ? null : value; break;
            case "stateDir": c.StateDir = value; break;
            case "logsDir": c.LogsDir = value; break;
            case "workshopDir": c.WorkshopDir = value; break;
            case "modsDir": c.ModsDir = value; break;
        }

        app.SaveConfig();
        Ui.Ok($"{key} = {(value.Length == 0 ? "(not set)" : value)}");
        if (key is "stateDir" or "workshopDir" or "modsDir")
        {
            Ui.Warn($"existing files are not moved; move them yourself or re-download with `a3ctl mods download --all`");
        }

        return await Task.FromResult(0);
    });
    config.Subcommands.Add(set);

    var showConfig = new Command("show", "Print the config file");
    Handle(showConfig, async (_, app, ct) =>
    {
        Ui.Err.MarkupLine(Ui.Dim($"==> {app.ConfigPath}"));
        Console.Write(await File.ReadAllTextAsync(app.ConfigPath, ct));
        return 0;
    });
    config.Subcommands.Add(showConfig);

    var path = new Command("path", "Print the config file in use");
    path.SetAction(p => Console.WriteLine(ConfigFile.Locate(p.GetValue(configOption))));
    config.Subcommands.Add(path);

    var initPath = new Argument<string?>("path") { Description = "Where to write it (default: ./a3ctl.json)", Arity = ArgumentArity.ZeroOrOne };
    var initSteamUser = new Option<string?>("--steam-user") { Description = "Steam account for SteamCMD (needed for Workshop downloads)" };
    var initSteamCmd = new Option<string?>("--steamcmd") { Description = "SteamCMD executable (default: steamcmd on PATH)" };
    var initInstallDir = new Option<string?>("--install-dir") { Description = "Arma 3 server install folder (default: arma3 next to the config)" };
    var init = new Command("init", "Write a minimal config: one Arma install, no mods or servers yet") { initPath, initSteamUser, initSteamCmd, initInstallDir };
    init.SetAction(p =>
    {
        var target = Path.GetFullPath(p.GetValue(initPath) ?? "a3ctl.json");
        if (File.Exists(target))
        {
            Ui.Error($"{target} already exists");
            return 1;
        }

        // Data folders are relative, so they live next to the config file.
        var config = new Config
        {
            SteamCmd = p.GetValue(initSteamCmd) ?? "steamcmd",
            SteamUser = p.GetValue(initSteamUser),
            Installs = { ["arma3"] = new InstallConfig { Dir = p.GetValue(initInstallDir) ?? "arma3", Branch = "creatordlc" } },
        };
        ConfigFile.Save(target, config);
        Ui.Ok($"wrote {target}");
        Ui.Line(Ui.Dim("next: `a3ctl install update`, `a3ctl server add <name>`, `a3ctl mods add <id> -m <modset>`, then `a3ctl config check`"));
        if (config.SteamUser is null)
        {
            Ui.Warn("no --steam-user given; set steamUser before downloading Workshop mods");
        }

        return 0;
    });
    config.Subcommands.Add(init);
}

// Mod names start with '@', which System.CommandLine would otherwise read as response files.
return await root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null }).InvokeAsync();

// ---------------------------------------------------------------- helpers

static void AddToModsets(App app, IEnumerable<string> mods, IEnumerable<string> sets)
{
    foreach (var set in sets)
    {
        if (!ConfigFile.IdPattern().IsMatch(set))
        {
            throw new UserException($"modset name '{set}' must be lowercase letters, digits, '-' or '_'");
        }

        if (!app.Config.Modsets.TryGetValue(set, out var members))
        {
            app.Config.Modsets[set] = members = [];
            Ui.Ok($"created modset {set}");
        }

        foreach (var mod in mods.Where(m => !members.Contains(m)))
        {
            members.Add(mod);
        }
    }
}

static List<string> AddToCatalog(App app, List<long> ids, string? explicitName, string[] modsets, Dictionary<long, WorkshopItem> details)
{
    var names = new List<string>();
    foreach (var id in ids)
    {
        var existing = app.Config.Mods.FirstOrDefault(m => m.Value.WorkshopId == id).Key;
        if (existing is not null)
        {
            Ui.Ok($"{existing} ({id}) is already in the catalog");
            names.Add(existing);
            continue;
        }

        if (!details.TryGetValue(id, out var item) || !item.Found)
        {
            throw new UserException($"Workshop item {id} was not found (removed, private or unlisted?)");
        }

        if (item.AppId != 0 && item.AppId != SteamCmd.ArmaClientAppId)
        {
            throw new UserException($"Workshop item {id} ('{item.Title}') is not an Arma 3 item");
        }

        var name = explicitName ?? WorkshopApi.SuggestName(item.Title);
        if (!ConfigFile.ModNamePattern().IsMatch(name))
        {
            throw new UserException($"'{name}' is not a valid mod folder name; pass --name @lowercase_name");
        }

        if (app.Config.Mods.ContainsKey(name))
        {
            throw new UserException($"{name} already exists for another item; add {id} again with --name");
        }

        app.Config.Mods[name] = new ModConfig { WorkshopId = id };
        names.Add(name);
        Ui.Ok($"added {name} ({id}): {item.Title}, {Ui.Size(item.FileSize)}");
    }

    AddToModsets(app, names, modsets);
    return names;
}

static async Task<int> DownloadIfAsked(App app, List<string> names, bool download, CancellationToken ct)
{
    if (!download)
    {
        Ui.Line(Ui.Dim("not downloaded yet; run `a3ctl mods update`"));
        return 0;
    }

    return await Download(app, names, 5, ct);
}

static async Task<int> Download(App app, List<string> names, int attempts, CancellationToken ct)
{
    var failed = await new Mods(app).DownloadAsync(names, Math.Max(1, attempts), ct);
    var succeeded = names.Except(failed).ToList();
    if (succeeded.Count > 0)
    {
        var mods = new Mods(app);
        var servers = new Servers(app);
        var affected = succeeded.SelectMany(mods.ServersUsing).Distinct().Where(id => servers.GetRuntime(id).Running).ToList();
        if (affected.Count > 0)
        {
            Ui.Warn($"running servers use updated mods and need a restart: {string.Join(", ", affected)}");
        }
    }

    if (failed.Count > 0)
    {
        Ui.Error($"could not download: {string.Join(", ", failed)}");
        return 1;
    }

    Ui.Ok($"{succeeded.Count} mod(s) downloaded");
    return 0;
}

static void SyncKeysFor(App app, IEnumerable<string> servers)
{
    var mods = new Mods(app);
    var state = app.LoadState();
    foreach (var id in servers.Distinct())
    {
        var (added, removed) = mods.SyncKeys(id, state);
        if (added.Count + removed.Count > 0)
        {
            Ui.Ok($"keys for {id}: {added.Count} added, {removed.Count} removed");
        }
    }

    app.SaveState(state);
}

static async Task<UpdatePlan> PlanUpdates(App app, string? modset, CancellationToken ct)
{
    var names = modset is null
        ? app.Config.Mods.Keys.ToList()
        : app.Config.Modsets.TryGetValue(modset, out var members) ? members : throw new UserException($"unknown modset '{modset}'");
    var workshop = names.Where(n => app.Config.Mods[n].WorkshopId is not null).ToList();
    var details = await WorkshopApi.GetDetailsAsync(workshop.Select(n => app.Config.Mods[n].WorkshopId!.Value).ToList(), ct);
    var state = app.LoadState();
    var mods = new Mods(app);
    var plan = new UpdatePlan();

    foreach (var name in workshop)
    {
        var id = app.Config.Mods[name].WorkshopId!.Value;
        details.TryGetValue(id, out var item);
        state.Mods.TryGetValue(name, out var local);
        string status;
        if (!mods.IsInstalled(name, state))
        {
            status = Ui.Yellow("missing");
            plan.Needed.Add(name);
        }
        else if (item is null || !item.Found)
        {
            status = Ui.Red("not on workshop");
        }
        else if (local!.TimeUpdated == 0 || item.TimeUpdated > local.TimeUpdated)
        {
            status = Ui.Yellow("outdated");
            plan.Needed.Add(name);
        }
        else
        {
            status = Ui.Green("ok");
        }

        plan.Rows.Add([name, id.ToString(), Ui.Date(local?.TimeUpdated ?? 0), Ui.Date(item?.TimeUpdated ?? 0), status]);
    }

    return plan;
}

static string Quote(string s) =>
    s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_=./:;@,+".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";

// Lets people type cba_a3 instead of @cba_a3 (the @ is awkward in some shells).
static string ModName(string name) => name.StartsWith('@') ? name : "@" + name;

sealed class UpdatePlan
{
    public List<string[]> Rows { get; } = [];
    public List<string> Needed { get; } = [];
}
