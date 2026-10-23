using System.Text.RegularExpressions;
using Spectre.Console;

namespace A3Ctl;

// Workshop items are downloaded by SteamCMD into workshopDir and left untouched there,
// so later updates stay incremental. Each mod is then mirrored into modsDir/@name with
// every file and folder name lowercased (Arma on Linux needs that). The mirror uses
// hardlinks, so it costs no extra disk space and takes seconds even for large mods.
public sealed partial class Mods(App app)
{
    // Marks mod folders a3ctl created. Kept outside the mod folders so Swifty does not
    // publish it to players.
    private string OwnedMarker(string name) => Path.Combine(app.ModsDir, ".a3ctl", name);

    [GeneratedRegex(@"Success\. Downloaded item (\d+)")]
    private static partial Regex DownloadedPattern();

    [GeneratedRegex(@"Downloading item (\d+)")]
    private static partial Regex DownloadingPattern();

    [GeneratedRegex(@"Download item (\d+) failed")]
    private static partial Regex FailedPattern();

    public string ContentDir(long workshopId) =>
        Path.Combine(app.WorkshopDir, "steamapps", "workshop", "content", SteamCmd.ArmaClientAppId.ToString(), workshopId.ToString());

    public string ModDir(string name) => Path.Combine(app.ModsDir, name);

    public bool IsInstalled(string name, State state)
    {
        var mod = app.Config.Mods[name];
        if (mod.WorkshopId is null)
        {
            return Directory.Exists(ModDir(name));
        }

        return state.Mods.ContainsKey(name) && File.Exists(OwnedMarker(name)) && Directory.Exists(ModDir(name));
    }

    public IEnumerable<string> ServersUsing(string name) =>
        app.Config.Servers
            .Where(s => app.ModsOf(s.Value.Modsets.Concat(s.Value.ServerModsets)).Contains(name))
            .Select(s => s.Key);

    public IEnumerable<string> ModsetsUsing(string name) =>
        app.Config.Modsets.Where(s => s.Value.Contains(name)).Select(s => s.Key);

    public string WorkshopName(string name) =>
        app.Config.Mods.TryGetValue(name, out var mod)
            ? mod.WorkshopId is null ? throw new UserException($"{name} is a local mod and has no Workshop item") : name
            : throw new UserException($"unknown mod '{name}'");

    // Downloads the given mods, retrying failures (SteamCMD regularly times out on large
    // items; a retry resumes). Returns the names that still failed.
    public async Task<List<string>> DownloadAsync(IReadOnlyList<string> names, int attempts, CancellationToken ct)
    {
        var pending = names.Select(n => (Name: WorkshopName(n), Id: app.Config.Mods[n].WorkshopId!.Value)).ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        Dictionary<long, WorkshopItem> details;
        try
        {
            details = await WorkshopApi.GetDetailsAsync(pending.Select(p => p.Id).ToList(), ct);
        }
        catch (UserException ex)
        {
            Ui.Warn($"{ex.Message}; downloading without version information");
            details = new();
        }

        foreach (var (name, id) in pending.Where(p => details.TryGetValue(p.Id, out var d) && !d.Found))
        {
            Ui.Warn($"{name} ({id}) is not visible on the Workshop (removed, private or unlisted?); trying anyway");
        }

        Directory.CreateDirectory(app.WorkshopDir);
        Directory.CreateDirectory(app.ModsDir);
        var size = pending.Sum(p => details.TryGetValue(p.Id, out var d) ? d.FileSize : 0);
        Ui.Step($"downloading {pending.Count} mod(s), {Ui.Size(size)} total");

        return await Ui.WithProgressAsync(async progress =>
        {
            var rows = progress is null ? null : pending.ToDictionary(p => p.Id, p =>
            {
                details.TryGetValue(p.Id, out var item);
                var row = progress.AddTask(RowLabel(p.Name, "queued"), autoStart: false, maxValue: Math.Max(1, item?.FileSize ?? 0));
                row.IsIndeterminate = item is not { FileSize: > 0 };
                return row;
            });

            for (var attempt = 1; attempt <= attempts && pending.Count > 0; attempt++)
            {
                if (attempt > 1 && progress is null)
                {
                    Ui.Step($"retrying {pending.Count} mod(s) (attempt {attempt}/{attempts})");
                }

                var failed = await DownloadOnceAsync(pending, details, rows, attempt, attempts, ct);
                pending = failed;
            }

            return pending.Select(p => p.Name).ToList();
        });
    }

    private static string RowLabel(string name, string state, string color = "dim") =>
        $"{Ui.E(name),-32} [{color}]{Ui.E(state.PadRight(24))}[/]";

    // One SteamCMD session for all pending items. Items download one after another, so the
    // row being downloaded is the one SteamCMD last announced; its bytes are read from
    // SteamCMD's staging folder.
    private async Task<List<(string Name, long Id)>> DownloadOnceAsync(
        List<(string Name, long Id)> pending,
        Dictionary<long, WorkshopItem> details,
        Dictionary<long, ProgressTask>? rows,
        int attempt,
        int attempts,
        CancellationToken ct)
    {
        var names = pending.ToDictionary(p => p.Id, p => p.Name);
        var resolved = new HashSet<long>();
        long? current = pending[0].Id;

        void SetCurrent(long? id)
        {
            current = id;
            if (id is { } active && rows is not null)
            {
                rows[active].StartTask();
                rows[active].Value = 0;
                rows[active].Description = RowLabel(names[active], attempt > 1 ? $"downloading (attempt {attempt})" : "downloading", "blue");
            }
        }

        void Resolve(long id, bool ok)
        {
            resolved.Add(id);
            if (rows is not null && rows.TryGetValue(id, out var row))
            {
                if (ok)
                {
                    row.Value = row.MaxValue;
                    row.Description = RowLabel(names[id], "installing");
                }
                else
                {
                    row.Description = RowLabel(names[id], attempt < attempts ? "failed, will retry" : "failed", "red");
                }
            }

            var next = pending.FirstOrDefault(p => !resolved.Contains(p.Id));
            SetCurrent(next.Name is null ? null : next.Id);
        }

        void OnLine(string line)
        {
            if (DownloadingPattern().Match(line) is { Success: true } starting && long.TryParse(starting.Groups[1].Value, out var id) && names.ContainsKey(id))
            {
                SetCurrent(id);
            }
            else if (DownloadedPattern().Match(line) is { Success: true } done && long.TryParse(done.Groups[1].Value, out id) && names.ContainsKey(id))
            {
                Resolve(id, true);
            }
            else if (FailedPattern().Match(line) is { Success: true } failed && long.TryParse(failed.Groups[1].Value, out id) && names.ContainsKey(id))
            {
                Resolve(id, false);
            }
        }

        SetCurrent(current);
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poller = rows is null ? Task.CompletedTask : PollStagingAsync(() => current, rows, polling.Token);

        var commands = pending.SelectMany(p => new[] { "+workshop_download_item", SteamCmd.ArmaClientAppId.ToString(), p.Id.ToString(), "validate" });
        SteamCmdResult result;
        try
        {
            result = await new SteamCmd(app).RunAsync(app.WorkshopDir, commands, "workshop", ct, OnLine);
        }
        finally
        {
            await polling.CancelAsync();
            await poller;
        }

        var succeeded = result.Output
            .Select(line => DownloadedPattern().Match(line))
            .Where(m => m.Success)
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToHashSet();

        var failedItems = new List<(string Name, long Id)>();
        foreach (var (name, id) in pending)
        {
            if (!succeeded.Contains(id) || !Directory.Exists(ContentDir(id)))
            {
                failedItems.Add((name, id));
                if (rows is not null && !resolved.Contains(id))
                {
                    rows[id].Description = RowLabel(name, attempt < attempts ? "failed, will retry" : "failed", "red");
                }

                continue;
            }

            Mirror(name, id);
            var state = app.LoadState();
            details.TryGetValue(id, out var item);
            state.Mods[name] = new ModState
            {
                WorkshopId = id,
                Title = item?.Title,
                TimeUpdated = item?.TimeUpdated ?? 0,
                DownloadedAtUtc = DateTime.UtcNow,
            };
            app.SaveState(state);
            if (rows is not null)
            {
                rows[id].Value = rows[id].MaxValue;
                rows[id].Description = RowLabel(name, "done", "green");
                rows[id].StopTask();
            }
            else
            {
                Ui.Ok($"{name} ({id}) {item?.Title}");
            }
        }

        if (failedItems.Count > 0)
        {
            Ui.Warn($"{failedItems.Count} failed: {string.Join(", ", failedItems.Select(f => f.Name))} (log: {result.LogFile})");
        }

        return failedItems;
    }

    private async Task PollStagingAsync(Func<long?> current, Dictionary<long, ProgressTask> rows, CancellationToken ct)
    {
        var staging = Path.Combine(app.WorkshopDir, "steamapps", "workshop", "downloads", SteamCmd.ArmaClientAppId.ToString());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (current() is not { } id || rows[id].IsIndeterminate)
                {
                    continue;
                }

                var dir = Path.Combine(staging, id.ToString());
                if (Directory.Exists(dir))
                {
                    long bytes;
                    try
                    {
                        bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    // Staging can briefly exceed the item size; never show a finished bar early.
                    rows[id].Value = Math.Min(bytes, rows[id].MaxValue * 0.99);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Rebuilds modsDir/@name from the pristine Workshop content and swaps it in. A running
    // server keeps reading the old files it already has open.
    public void Mirror(string name, long workshopId)
    {
        var source = ContentDir(workshopId);
        var target = ModDir(name);
        if (Directory.Exists(target) && !File.Exists(OwnedMarker(name)))
        {
            throw new UserException($"{target} exists but was not created by a3ctl; move it away and retry");
        }

        var fresh = Path.Combine(app.ModsDir, $".{name}.new");
        var old = Path.Combine(app.ModsDir, $".{name}.old");
        DeleteIfExists(fresh);
        DeleteIfExists(old);

        CopyLowercase(source, fresh);
        Directory.CreateDirectory(Path.GetDirectoryName(OwnedMarker(name))!);
        File.WriteAllText(OwnedMarker(name), $"{workshopId}\n");

        if (Directory.Exists(target))
        {
            Directory.Move(target, old);
        }

        Directory.Move(fresh, target);
        DeleteIfExists(old);
    }

    public void RemoveFiles(string name, bool includeWorkshopContent)
    {
        var target = ModDir(name);
        if (Directory.Exists(target))
        {
            if (!File.Exists(OwnedMarker(name)))
            {
                Ui.Warn($"leaving {target} alone, it was not created by a3ctl");
            }
            else
            {
                Directory.Delete(target, recursive: true);
                File.Delete(OwnedMarker(name));
            }
        }

        if (includeWorkshopContent && app.Config.Mods[name].WorkshopId is { } id)
        {
            DeleteIfExists(ContentDir(id));
        }
    }

    private static void CopyLowercase(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var lower = entry.Name.ToLowerInvariant();
            if (!seen.Add(lower))
            {
                throw new UserException($"{source} has two entries named '{lower}' once lowercased; the mod cannot run on Linux as-is");
            }

            if (entry.LinkTarget is not null)
            {
                continue;
            }

            var dest = Path.Combine(destination, lower);
            if (entry is DirectoryInfo)
            {
                CopyLowercase(entry.FullName, dest);
            }
            else
            {
                Native.LinkOrCopy(entry.FullName, dest);
            }
        }
    }

    private static void DeleteIfExists(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Fills a server's own keys folder: the install's base keys (a3.bikey, CDLC keys) plus
    // the .bikey files of every client mod the server accepts. Keys a3ctl added earlier that
    // are no longer needed are removed; keys it did not add are never touched.
    public (List<string> Added, List<string> Removed) SyncKeys(string serverId, State state)
    {
        var server = app.Server(serverId);
        var keysDir = Path.Combine(app.ServerDir(serverId), "keys");
        // Servers also accept the mods of the Swifty repositories players join them with,
        // optional ones included, so ticking an optional mod does not get a player kicked.
        var mods = app.ModsOf(server.Modsets)
            .Concat(Swifty.ModsAcceptedBy(app, [serverId]))
            .Distinct()
            .ToList();

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseKeys = Path.Combine(app.InstallDir(server.Install), "keys");
        if (Directory.Exists(baseKeys))
        {
            foreach (var key in Directory.EnumerateFiles(baseKeys, "*.bikey"))
            {
                wanted[Path.GetFileName(key).ToLowerInvariant()] = key;
            }
        }

        foreach (var mod in mods)
        {
            var dir = ModDir(mod);
            if (!Directory.Exists(dir))
            {
                Ui.Warn($"keys: {mod} is not installed, skipping its keys");
                continue;
            }

            foreach (var key in Directory.EnumerateFiles(dir, "*.bikey", SearchOption.AllDirectories))
            {
                var keyName = Path.GetFileName(key).ToLowerInvariant();
                if (wanted.TryGetValue(keyName, out var existing) && !SameContent(existing, key))
                {
                    Ui.Warn($"keys: {keyName} differs between {existing} and {key}; using the first");
                    continue;
                }

                wanted.TryAdd(keyName, key);
            }
        }

        Directory.CreateDirectory(keysDir);
        var managed = state.ManagedKeys.TryGetValue(serverId, out var m) ? m : new SortedSet<string>(StringComparer.Ordinal);
        var added = new List<string>();
        var removed = new List<string>();

        foreach (var (keyName, source) in wanted)
        {
            var dest = Path.Combine(keysDir, keyName);
            if (File.Exists(dest))
            {
                if (SameContent(source, dest))
                {
                    continue;
                }

                if (!managed.Contains(keyName))
                {
                    Ui.Warn($"keys: {dest} was not placed by a3ctl and differs from {source}; leaving it");
                    continue;
                }
            }

            File.Copy(source, dest, overwrite: true);
            managed.Add(keyName);
            added.Add(keyName);
        }

        foreach (var keyName in managed.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            var path = Path.Combine(keysDir, keyName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            managed.Remove(keyName);
            removed.Add(keyName);
        }

        state.ManagedKeys[serverId] = managed;
        return (added, removed);
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
