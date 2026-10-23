using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace A3Ctl;

public sealed class Config
{
    public string SteamCmd { get; set; } = "steamcmd";

    // Steam account used for app and Workshop downloads. Null means anonymous, which
    // cannot download Arma 3 Workshop items. Log in once interactively with
    // `steamcmd +login <user>` so SteamCMD caches the session; the password can also be
    // supplied through A3CTL_STEAM_PASSWORD but then shows up in the process list.
    public string? SteamUser { get; set; }

    public string StateDir { get; set; } = "state";
    public string LogsDir { get; set; } = "logs";
    public string WorkshopDir { get; set; } = "workshop";
    public string ModsDir { get; set; } = "mods";

    public Dictionary<string, InstallConfig> Installs { get; set; } = new();
    public SortedDictionary<string, ModConfig> Mods { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, List<string>> Modsets { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ServerConfig> Servers { get; set; } = new();

    public SwiftyConfig? Swifty { get; set; }
}

// The Swifty group folder is maintained by hand (or `swifty admin init`): swifty-group.json
// with its repositories and servers, images, news, events. a3ctl reads it, fills in the
// mod lists of the repository files from modsets, and runs `swifty admin publish`.
public sealed class SwiftyConfig
{
    public string Exe { get; set; } = "swifty";

    // Folder holding swifty-group.json, handed to `swifty admin publish`.
    public string GroupDir { get; set; } = "swifty";

    // Web root folder the group is published into.
    public string? PublishDir { get; set; }

    // link | symlink | copy | none, see `swifty admin publish --files`.
    public string Files { get; set; } = "link";

    // Public URL of the published group, used for invite links.
    public string? Url { get; set; }

    // Keyed by repository id in swifty-group.json.
    public Dictionary<string, SwiftyRepoConfig> Repos { get; set; } = new();
}

public sealed class SwiftyRepoConfig
{
    // Become requiredMods. Every mod a server using this repository loads must be in here.
    public List<string> Modsets { get; set; } = [];

    // Client-side mods players may pick (sounds, UI, ...). Their keys are added to the
    // servers so players running them are not kicked.
    public List<string> OptionalModsets { get; set; } = [];

    // Default: the DLC folders of the servers that use this repository.
    public List<string>? Dlcs { get; set; }
}

public sealed class InstallConfig
{
    public string Dir { get; set; } = "";
    public int AppId { get; set; } = 233780;
    public string? Branch { get; set; }
    public string? BranchPassword { get; set; }
}

public sealed class ModConfig
{
    // Null for local mods that are placed into the mods directory by hand.
    public long? WorkshopId { get; set; }
}

public sealed class ServerConfig
{
    public string Type { get; set; } = "server";
    public string Install { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Exe { get; set; } = "arma3server_x64";
    public int Port { get; set; } = 2302;
    // null means none; written out explicitly so it does not fall back to the default.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Config { get; set; } = "server.cfg";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Basic { get; set; } = "basic.cfg";

    // Headless client of a local server: install, modsets, DLC and connect are taken from
    // that server every time the config loads, so they never drift apart.
    public string? For { get; set; }

    // Headless client only: "host:port" of the server to join, and its password.
    public string? Connect { get; set; }
    public string? Password { get; set; }

    public List<string> Dlc { get; set; } = [];
    public List<string> Modsets { get; set; } = [];
    public List<string> ServerModsets { get; set; } = [];
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new();

    [JsonIgnore] public bool IsHeadless => Type == "headless";
}

public static partial class ConfigFile
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    public static partial Regex IdPattern();

    // Arma on Linux needs lowercase mod folders.
    [GeneratedRegex("^@[a-z0-9][a-z0-9_.-]{0,63}$")]
    public static partial Regex ModNamePattern();

    public static string Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var fromEnv = Environment.GetEnvironmentVariable("A3CTL_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var local = Path.GetFullPath("a3ctl.json");
        if (File.Exists(local))
        {
            return local;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "a3ctl", "a3ctl.json");
    }

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UserException($"config not found at {path} (create one with `a3ctl config init`, or pass --config / set A3CTL_CONFIG)");
        }

        Config? config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new UserException($"{path}: invalid JSON: {ex.Message}");
        }

        if (config is null)
        {
            throw new UserException($"{path}: config is empty");
        }

        // Re-wrap so lookups keep ordinal ordering regardless of how the deserializer built them.
        config.Mods = new SortedDictionary<string, ModConfig>(config.Mods, StringComparer.Ordinal);
        config.Modsets = new SortedDictionary<string, List<string>>(config.Modsets, StringComparer.Ordinal);
        FollowServers(config);

        var errors = Validate(config);
        if (errors.Count > 0)
        {
            throw new UserException($"{path} is invalid:\n  " + string.Join("\n  ", errors));
        }

        return config;
    }

    // Headless clients with "for" mirror their server.
    public static void FollowServers(Config config)
    {
        foreach (var hc in config.Servers.Values.Where(s => s.For is not null))
        {
            if (!config.Servers.TryGetValue(hc.For!, out var host) || host.IsHeadless)
            {
                continue;
            }

            hc.Install = host.Install;
            hc.Modsets = [.. host.Modsets];
            hc.Dlc = [.. host.Dlc];
            hc.Connect = $"127.0.0.1:{host.Port}";
        }
    }

    public static void Save(string path, Config config)
    {
        FollowServers(config);
        var errors = Validate(config);
        if (errors.Count > 0)
        {
            throw new UserException("refusing to save invalid config:\n  " + string.Join("\n  ", errors));
        }

        Files.WriteAtomic(path, JsonSerializer.Serialize(config, Json) + "\n");
    }

    public static List<string> Validate(Config c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.SteamCmd)) errors.Add("steamCmd must name the SteamCMD executable");
        if (c.Swifty is { Exe: var swiftyExe } && string.IsNullOrWhiteSpace(swiftyExe)) errors.Add("swifty.exe must name the swifty CLI");

        foreach (var (id, install) in c.Installs)
        {
            if (!IdPattern().IsMatch(id)) errors.Add($"install '{id}': id must be lowercase letters, digits, '-' or '_'");
            if (string.IsNullOrWhiteSpace(install.Dir)) errors.Add($"install '{id}': dir is required");
            if (install.AppId <= 0) errors.Add($"install '{id}': appId must be positive");
        }

        foreach (var (name, mod) in c.Mods)
        {
            if (!ModNamePattern().IsMatch(name)) errors.Add($"mod '{name}': name must look like @lowercase_name");
            if (mod.WorkshopId is <= 0) errors.Add($"mod '{name}': workshopId must be positive");
        }

        var byWorkshopId = c.Mods.Where(m => m.Value.WorkshopId is not null).GroupBy(m => m.Value.WorkshopId);
        foreach (var dup in byWorkshopId.Where(g => g.Count() > 1))
        {
            errors.Add($"workshop item {dup.Key} is listed more than once: {string.Join(", ", dup.Select(m => m.Key))}");
        }

        foreach (var (set, mods) in c.Modsets)
        {
            if (!IdPattern().IsMatch(set)) errors.Add($"modset '{set}': id must be lowercase letters, digits, '-' or '_'");
            foreach (var mod in mods.Where(m => !c.Mods.ContainsKey(m)))
            {
                errors.Add($"modset '{set}': unknown mod '{mod}'");
            }
        }

        foreach (var (id, s) in c.Servers)
        {
            var where = $"server '{id}'";
            if (!IdPattern().IsMatch(id)) errors.Add($"{where}: id must be lowercase letters, digits, '-' or '_'");
            if (s.Type is not ("server" or "headless")) errors.Add($"{where}: type must be 'server' or 'headless'");
            if (!c.Installs.ContainsKey(s.Install)) errors.Add($"{where}: unknown install '{s.Install}'");
            if (string.IsNullOrWhiteSpace(s.Dir)) errors.Add($"{where}: dir is required");
            if (string.IsNullOrWhiteSpace(s.Exe)) errors.Add($"{where}: exe is required");
            if (s.Port is < 1 or > 65535) errors.Add($"{where}: port must be 1-65535");

            if (s.For is { } target)
            {
                if (!s.IsHeadless) errors.Add($"{where}: only headless clients can have \"for\"");
                if (!c.Servers.TryGetValue(target, out var host) || host.IsHeadless) errors.Add($"{where}: \"for\" must name a server, got '{target}'");
            }

            if (s.IsHeadless)
            {
                if (s.Connect is null || !TryParseEndpoint(s.Connect, out _, out _))
                {
                    errors.Add($"{where}: headless clients need connect as \"host:port\"");
                }

                if (s.ServerModsets.Count > 0) errors.Add($"{where}: headless clients cannot have serverModsets");
            }

            foreach (var set in s.Modsets.Concat(s.ServerModsets).Where(m => !c.Modsets.ContainsKey(m)))
            {
                errors.Add($"{where}: unknown modset '{set}'");
            }

            foreach (var dlc in s.Dlc.Where(d => d.Contains('/') || d.Contains(';') || d.StartsWith('@')))
            {
                errors.Add($"{where}: dlc entries are install folder names like 'gm' or 'vn', got '{dlc}'");
            }

            var managed = new[] { "-port=", "-config=", "-cfg=", "-profiles=", "-mod=", "-servermod=", "-connect=", "-password=", "-client" };
            foreach (var arg in s.Args.Where(a => managed.Any(m => a.StartsWith(m, StringComparison.OrdinalIgnoreCase))))
            {
                errors.Add($"{where}: '{arg}' is generated by a3ctl, remove it from args");
            }
        }

        if (c.Swifty is { } swifty)
        {
            if (swifty.Files is not ("link" or "symlink" or "copy" or "none")) errors.Add("swifty.files must be link, symlink, copy or none");
            foreach (var (id, repo) in swifty.Repos)
            {
                foreach (var set in repo.Modsets.Concat(repo.OptionalModsets).Where(m => !c.Modsets.ContainsKey(m)))
                {
                    errors.Add($"swifty repo '{id}': unknown modset '{set}'");
                }
            }
        }

        return errors;
    }

    public static bool TryParseEndpoint(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out port) || port is < 1 or > 65535)
        {
            return false;
        }

        host = value[..colon];
        return true;
    }
}
