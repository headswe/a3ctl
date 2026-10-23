using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace A3Ctl;

public sealed record SwiftyIssue(bool IsError, string Message);

public sealed record SwiftyRepo(string Id, string Name, string Source);

public sealed record SwiftyServer(string Id, int Port, string? Repository, string? Password);

// swifty-group.json belongs to the admins: group details, repositories and servers live
// there and only there. a3ctl reads it, writes the mod lists into the repository files from
// modsets, checks that players get what the servers load, and runs `swifty admin publish`.
public sealed partial class Swifty(App app)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private JsonObject? _group;

    private SwiftyConfig Cfg => app.Config.Swifty
        ?? throw new UserException("the config has no swifty section (see a3ctl.example.json)");

    public string GroupDir => app.Resolve(Cfg.GroupDir);

    public string GroupFile => Path.Combine(GroupDir, "swifty-group.json");

    public string? PublishDir => Cfg.PublishDir is null ? null : app.Resolve(Cfg.PublishDir);

    public JsonObject Group => _group ??= LoadObject(GroupFile)
        ?? throw new UserException($"{GroupFile} not found; create the group with `swifty admin init` and point swifty.groupDir at its folder");

    public List<SwiftyRepo> Repos => Objects(Group["repositories"])
        .Select(r => new SwiftyRepo(Str(r, "id") ?? "", Str(r, "name") ?? Str(r, "id") ?? "", Str(r, "source") ?? ""))
        .ToList();

    public List<SwiftyServer> Servers => Objects(Group["servers"])
        .Select(s => new SwiftyServer(Str(s, "id") ?? "", int.TryParse(Str(s, "port"), out var port) ? port : 0, Str(s, "repository"), Str(s, "password")))
        .ToList();

    public string RepoFile(SwiftyRepo repo) => Path.GetFullPath(repo.Source, GroupDir);

    public List<string> RequiredMods(SwiftyRepoConfig repo) => app.ModsOf(repo.Modsets);

    public List<string> OptionalMods(SwiftyRepoConfig repo) =>
        app.ModsOf(repo.OptionalModsets).Except(RequiredMods(repo)).ToList();

    // The a3ctl server behind a Swifty server entry: same id, otherwise the only server on
    // that port. Null when there is none or it is ambiguous.
    public string? LocalServer(SwiftyServer server)
    {
        if (app.Config.Servers.TryGetValue(server.Id, out var byId) && !byId.IsHeadless)
        {
            return server.Id;
        }

        var byPort = app.Config.Servers.Where(s => !s.Value.IsHeadless && s.Value.Port == server.Port).Select(s => s.Key).ToList();
        return byPort.Count == 1 ? byPort[0] : null;
    }

    public List<string> Dlcs(string repoId) =>
        Cfg.Repos[repoId].Dlcs
        ?? Servers.Where(s => s.Repository == repoId)
            .Select(LocalServer)
            .OfType<string>()
            .SelectMany(id => app.Config.Servers[id].Dlc)
            .Distinct()
            .ToList();

    // Mods of the Swifty repositories players use to join these a3ctl servers, optional
    // ones included. Empty when Swifty is not set up.
    public static List<string> ModsAcceptedBy(App app, IReadOnlyCollection<string> serverIds)
    {
        if (app.Config.Swifty is null)
        {
            return [];
        }

        var swifty = new Swifty(app);
        if (!File.Exists(swifty.GroupFile))
        {
            return [];
        }

        try
        {
            var repoIds = swifty.Servers
                .Where(s => s.Repository is not null && swifty.LocalServer(s) is { } local && serverIds.Contains(local))
                .Select(s => s.Repository!)
                .Where(swifty.Cfg.Repos.ContainsKey)
                .Distinct();
            return repoIds.SelectMany(id => swifty.RequiredMods(swifty.Cfg.Repos[id]).Concat(swifty.OptionalMods(swifty.Cfg.Repos[id]))).Distinct().ToList();
        }
        catch (UserException ex)
        {
            Ui.Warn($"keys: ignoring Swifty repositories: {ex.Message}");
            return [];
        }
    }

    public string? CfgPasswordOf(string serverId) => new Servers(app).CfgPassword(serverId);

    // Repository file path -> new content, for the repositories a3ctl manages. Only
    // modsDirectory, requiredMods, optionalMods and dlcs are set; other keys (name, game,
    // clientParameters, icon, banner, optionalDlcs, ...) are kept as they are.
    public Dictionary<string, string> Build()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupRepos = Repos.ToDictionary(r => r.Id);
        foreach (var (id, repo) in Cfg.Repos)
        {
            var entry = groupRepos.TryGetValue(id, out var r)
                ? r
                : throw new UserException($"swifty repo '{id}' is not listed under repositories in {GroupFile}");
            var path = RepoFile(entry);
            var file = LoadObject(path) ?? new JsonObject { ["name"] = entry.Name, ["game"] = "arma3" };
            file["modsDirectory"] = app.ModsDir;
            file["requiredMods"] = Array(RequiredMods(repo));
            file["optionalMods"] = Array(OptionalMods(repo));
            file["dlcs"] = Array(Dlcs(id));
            files[path] = file.ToJsonString(Json) + "\n";
        }

        return files;
    }

    // Problems that would give players a broken or unjoinable setup.
    public List<SwiftyIssue> Check()
    {
        var issues = new List<SwiftyIssue>();
        void Error(string m) => issues.Add(new SwiftyIssue(true, m));
        void Warn(string m) => issues.Add(new SwiftyIssue(false, m));

        try
        {
            _ = Group;
        }
        catch (UserException ex)
        {
            Error(ex.Message);
            return issues;
        }

        foreach (var key in new[] { "id", "name" }.Where(k => Group[k] is null))
        {
            Error($"{GroupFile}: {key} is required");
        }

        var groupRepos = Repos.ToDictionary(r => r.Id);
        var state = app.LoadState();
        var mods = new Mods(app);

        foreach (var (id, repo) in Cfg.Repos)
        {
            if (!groupRepos.ContainsKey(id))
            {
                Error($"swifty repo '{id}' is not listed under repositories in {GroupFile}");
                continue;
            }

            if (RequiredMods(repo).Count == 0)
            {
                Warn($"repo {id} has no required mods");
            }

            foreach (var mod in RequiredMods(repo).Concat(OptionalMods(repo)).Where(m => !mods.IsInstalled(m, state)))
            {
                Error($"repo {id}: {mod} is not installed (run `a3ctl mods update`)");
            }

            foreach (var overlap in app.ModsOf(repo.OptionalModsets).Intersect(RequiredMods(repo)))
            {
                Warn($"repo {id}: {overlap} is both required and optional; it is published as required");
            }
        }

        foreach (var repo in groupRepos.Values.Where(r => !Cfg.Repos.ContainsKey(r.Id)))
        {
            Warn($"repository {repo.Id} is not in swifty.repos, so a3ctl leaves {repo.Source} as it is");
        }

        foreach (var server in Servers)
        {
            var where = $"swifty server {server.Id} (port {server.Port})";
            if (server.Repository is null || !groupRepos.ContainsKey(server.Repository))
            {
                Error($"{where} uses repository '{server.Repository}', which is not in {GroupFile}");
                continue;
            }

            if (LocalServer(server) is not { } local)
            {
                Warn($"{where} matches no a3ctl server by id or unique port; not checked");
                continue;
            }

            var localServer = app.Config.Servers[local];
            if (Cfg.Repos.TryGetValue(server.Repository, out var repo))
            {
                var required = RequiredMods(repo);
                var missing = app.ModsOf(localServer.Modsets).Except(required).ToList();
                if (missing.Count > 0)
                {
                    Error($"{where}: server {local} loads mods repository {server.Repository} does not give players: {string.Join(", ", missing)}");
                }

                var extra = required.Except(app.ModsOf(localServer.Modsets)).ToList();
                if (extra.Count > 0)
                {
                    Warn($"{where}: repository {server.Repository} makes players load mods server {local} does not run: {string.Join(", ", extra)}");
                }

                var missingDlc = localServer.Dlc.Except(Dlcs(server.Repository)).ToList();
                if (missingDlc.Count > 0)
                {
                    Error($"{where}: server {local} runs DLC {string.Join(", ", missingDlc)} that repository {server.Repository} does not list in dlcs");
                }
            }

            var cfgPassword = CfgPasswordOf(local);
            if (cfgPassword != server.Password && cfgPassword is not null)
            {
                Error($"{where}: password differs from {local}'s {localServer.Config}; players would be refused");
            }
            else if (cfgPassword is null && server.Password is not null)
            {
                Warn($"{where} has a password but {local}'s server config has none");
            }
        }

        var images = new List<string?> { Str(Group, "logo") };
        if (Group["banners"] is JsonArray banners)
        {
            images.AddRange(banners.Select(b => b?.ToString()));
        }

        var paths = images.OfType<string>().Select(i => Path.GetFullPath(i, GroupDir)).ToList();
        foreach (var repo in groupRepos.Values)
        {
            var file = LoadObject(RepoFile(repo));
            var dir = Path.GetDirectoryName(RepoFile(repo))!;
            paths.AddRange(new[] { Str(file, "icon"), Str(file, "banner") }.OfType<string>().Select(i => Path.GetFullPath(i, dir)));
        }

        foreach (var missing in paths.Where(p => !File.Exists(p)))
        {
            Warn($"image {missing} does not exist");
        }

        return issues;
    }

    // Writes repository files whose mod lists changed. Returns the changed paths.
    public List<string> Generate()
    {
        var changed = new List<string>();
        foreach (var (path, content) in Build())
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                continue;
            }

            Files.WriteAtomic(path, content);
            changed.Add(Path.GetRelativePath(GroupDir, path));
        }

        return changed;
    }

    public async Task PublishAsync(string? filesMode, CancellationToken ct)
    {
        var target = PublishDir ?? throw new UserException("set swifty.publishDir to the web folder the group is served from");
        var errors = Check().Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            throw new UserException("not publishing:\n  " + string.Join("\n  ", errors.Select(e => e.Message)));
        }

        var changed = Generate();
        Ui.Ok(changed.Count == 0 ? "repository files unchanged" : $"updated {string.Join(", ", changed)}");
        Directory.CreateDirectory(target);
        Ui.Step($"publishing {GroupDir} -> {target}");
        await RunAsync(["admin", "publish", GroupDir, target, "--files", filesMode ?? Cfg.Files], ct);
        Ui.Ok("published");
    }

    public async Task InviteAsync(CancellationToken ct)
    {
        var url = Cfg.Url ?? throw new UserException("set swifty.url to the public URL of the published group");
        await RunAsync(["admin", "invite", url], ct);
    }

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Cfg.Exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UserException($"could not run the swifty CLI '{Cfg.Exe}': {ex.Message} (install it or set swifty.exe)");
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new UserException($"swifty {string.Join(' ', psi.ArgumentList)} failed with exit code {process.ExitCode}");
            }
        }
    }

    private static JsonObject? LoadObject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new UserException($"{path} does not contain a JSON object");
        }
        catch (JsonException ex)
        {
            throw new UserException($"{path}: invalid JSON: {ex.Message}");
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    // Strings and numbers both come back as text; Swifty files are hand-written.
    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.ToJsonString(),
                _ => null,
            }
            : null;

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
