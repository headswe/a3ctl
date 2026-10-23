using System.Text.RegularExpressions;

namespace A3Ctl;

// Arma 3 server installs, downloaded and updated with SteamCMD `app_update`.
public sealed partial class Installs(App app)
{
    // " Update state (0x61) downloading, progress: 42.17 (5402214 / 12810000)"
    [GeneratedRegex(@"Update state \(0x[0-9a-fA-F]+\) ([a-z ]+), progress: [\d.]+ \((\d+) / (\d+)\)")]
    private static partial Regex ProgressPattern();

    public InstallConfig Get(string id) =>
        app.Config.Installs.TryGetValue(id, out var install)
            ? install
            : throw new UserException($"unknown install '{id}' ({App.Known(app.Config.Installs.Keys, "installs")})");

    public List<string> ServersUsing(string id) =>
        app.Config.Servers.Where(s => s.Value.Install == id).Select(s => s.Key).ToList();

    public void Add(string id, string? dir, string? branch, string? branchPassword)
    {
        if (app.Config.Installs.ContainsKey(id))
        {
            throw new UserException($"install '{id}' already exists");
        }

        var install = new InstallConfig { Dir = dir ?? $"installs/{id}", Branch = branch, BranchPassword = branchPassword };
        app.Config.Installs[id] = install;
        app.SaveConfig();

        var path = app.InstallDir(id);
        if (LooksLikeInstall(path))
        {
            Ui.Ok($"added install {id}, using the existing Arma install in {path}");
        }
        else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            Ui.Warn($"{path} is not empty and has no Arma server in it; SteamCMD will install into it anyway");
            Ui.Ok($"added install {id}");
        }
        else
        {
            Ui.Ok($"added install {id} at {path}; download it with `a3ctl install update {id}`");
        }
    }

    public void Remove(string id, bool deleteFiles)
    {
        Get(id);
        var users = ServersUsing(id);
        if (users.Count > 0)
        {
            throw new UserException($"install {id} is used by {string.Join(", ", users)}; move or remove those servers first");
        }

        var path = app.InstallDir(id);
        if (deleteFiles && Directory.Exists(path))
        {
            // Only ever delete something that is clearly an Arma install, never a typo'd path.
            if (!LooksLikeInstall(path))
            {
                throw new UserException($"{path} does not look like an Arma server install; not deleting it");
            }

            Ui.Step($"deleting {path}");
            Directory.Delete(path, recursive: true);
        }

        app.Config.Installs.Remove(id);
        app.SaveConfig();
        Ui.Ok(deleteFiles ? $"removed install {id} and its files" : $"removed install {id} (files left in {path})");
    }

    public static bool LooksLikeInstall(string path) =>
        Directory.Exists(path) &&
        (Directory.EnumerateFiles(path, "arma3server*").Any() || Directory.Exists(Path.Combine(path, "steamapps")));

    // Downloads or updates one install. Returns false (after printing why) on failure.
    public async Task<bool> UpdateAsync(string id, bool validate, CancellationToken ct)
    {
        var cfg = Get(id);
        var servers = new Servers(app);
        var running = ServersUsing(id).Where(s => servers.GetRuntime(s).Running).ToList();
        if (running.Count > 0)
        {
            Ui.Error($"{id}: stop these first: {string.Join(", ", running)} (`a3ctl server stop {string.Join(' ', running)}`)");
            return false;
        }

        Ui.Step($"updating install {id} ({cfg.Branch ?? "public"} branch) in {app.InstallDir(id)}");
        var commands = new List<string> { "+app_update", cfg.AppId.ToString() };
        if (cfg.Branch is not null)
        {
            commands.AddRange(["-beta", cfg.Branch]);
            if (cfg.BranchPassword is not null)
            {
                commands.AddRange(["-betapassword", cfg.BranchPassword]);
            }
        }

        if (validate)
        {
            commands.Add("validate");
        }

        Directory.CreateDirectory(app.InstallDir(id));
        var success = $"Success! App '{cfg.AppId}'";
        var result = await Ui.WithProgressAsync(async progress =>
        {
            var row = progress?.AddTask($"{Ui.E(id),-12} [dim]{"starting",-18}[/]", maxValue: 1);
            if (row is not null)
            {
                row.IsIndeterminate = true;
            }

            void OnLine(string line)
            {
                var match = ProgressPattern().Match(line);
                if (row is null || !match.Success || !long.TryParse(match.Groups[3].Value, out var total) || total <= 0)
                {
                    return;
                }

                row.IsIndeterminate = false;
                row.MaxValue = total;
                row.Value = long.Parse(match.Groups[2].Value);
                row.Description = $"{Ui.E(id),-12} [blue]{Ui.E(match.Groups[1].Value.Trim().PadRight(18))}[/]";
            }

            var r = await new SteamCmd(app).RunAsync(app.InstallDir(id), commands, $"install-{id}", ct, OnLine);
            if (row is not null)
            {
                var ok = r.Output.Any(l => l.Contains(success));
                row.IsIndeterminate = false;
                row.Value = row.MaxValue;
                row.Description = $"{Ui.E(id),-12} " + (ok ? $"[green]{"up to date",-18}[/]" : $"[red]{"failed",-18}[/]");
                row.StopTask();
            }

            return r;
        });

        if (!result.Output.Any(l => l.Contains(success)))
        {
            Ui.Error($"{id}: SteamCMD did not report success, see {result.LogFile}");
            return false;
        }

        Ui.Ok($"{id} is up to date");
        return true;
    }
}
