using System.Text.Json;

namespace A3Ctl;

public sealed class UserException(string message) : Exception(message);

// Loaded config plus resolved paths. Relative paths in the config resolve against the
// directory that contains the config file.
public sealed class App
{
    private App(string configPath, Config config)
    {
        ConfigPath = configPath;
        Config = config;
        var baseDir = Path.GetDirectoryName(configPath)!;
        StateDir = Path.GetFullPath(config.StateDir, baseDir);
        LogsDir = Path.GetFullPath(config.LogsDir, baseDir);
        WorkshopDir = Path.GetFullPath(config.WorkshopDir, baseDir);
        ModsDir = Path.GetFullPath(config.ModsDir, baseDir);
        _baseDir = baseDir;
    }

    private readonly string _baseDir;

    public string ConfigPath { get; }
    public Config Config { get; }
    public string StateDir { get; }
    public string LogsDir { get; }
    public string WorkshopDir { get; }
    public string ModsDir { get; }

    public static App Load(string? configPath)
    {
        var path = ConfigFile.Locate(configPath);
        return new App(path, ConfigFile.Load(path));
    }

    public string Resolve(string path) => Path.GetFullPath(path, _baseDir);

    public string InstallDir(string installId) => Resolve(Config.Installs[installId].Dir);

    public string ServerDir(string serverId) => Resolve(Config.Servers[serverId].Dir);

    public ServerConfig Server(string id) =>
        Config.Servers.TryGetValue(id, out var server)
            ? server
            : throw new UserException($"unknown server '{id}' ({App.Known(Config.Servers.Keys, "servers")})");

    public void SaveConfig() => ConfigFile.Save(ConfigPath, Config);

    // "known: a, b" or "no servers configured yet", for error messages.
    public static string Known(IEnumerable<string> ids, string what) =>
        ids.Any() ? $"known: {string.Join(", ", ids)}" : $"no {what} configured yet";

    // Mod names in launch order: every modset in order, deduplicated.
    public List<string> ModsOf(IEnumerable<string> modsets)
    {
        var result = new List<string>();
        foreach (var mod in modsets.SelectMany(set => Config.Modsets[set]))
        {
            if (!result.Contains(mod))
            {
                result.Add(mod);
            }
        }

        return result;
    }

    public State LoadState() => State.Load(Path.Combine(StateDir, "state.json"));

    public void SaveState(State state) => state.Save(Path.Combine(StateDir, "state.json"));

    // One a3ctl mutation at a time. Uses flock under the hood on Linux, so it is released
    // automatically if the process dies.
    public IDisposable Lock(string operation)
    {
        Directory.CreateDirectory(StateDir);
        var lockPath = Path.Combine(StateDir, "a3ctl.lock");
        var infoPath = lockPath + ".info";
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            var holder = File.Exists(infoPath) ? File.ReadAllText(infoPath).Trim() : "unknown";
            throw new UserException($"another a3ctl operation is running ({holder}); try again when it finishes");
        }

        File.WriteAllText(infoPath, $"pid {Environment.ProcessId}: {operation}, since {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return stream;
    }
}

public sealed class State
{
    public SortedDictionary<string, ModState> Mods { get; set; } = new(StringComparer.Ordinal);

    // Key files a3ctl copied into each server's keys folder, so it only ever removes its own.
    public SortedDictionary<string, SortedSet<string>> ManagedKeys { get; set; } = new(StringComparer.Ordinal);

    public static State Load(string path)
    {
        if (!File.Exists(path))
        {
            return new State();
        }

        return JsonSerializer.Deserialize<State>(File.ReadAllText(path), ConfigFile.Json) ?? new State();
    }

    public void Save(string path) => Files.WriteAtomic(path, JsonSerializer.Serialize(this, ConfigFile.Json) + "\n");
}

public sealed class ModState
{
    public long WorkshopId { get; set; }
    public string? Title { get; set; }

    // Workshop time_updated of the version we downloaded (unix seconds), 0 if unknown.
    public long TimeUpdated { get; set; }
    public DateTime DownloadedAtUtc { get; set; }
}

public static class Files
{
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    // Last `count` lines of a possibly huge file without reading it all.
    public static List<string> Tail(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int block = 64 * 1024;
        var position = stream.Length;
        var buffer = new List<byte>();
        var newlines = 0;
        while (position > 0 && newlines <= count)
        {
            var size = (int)Math.Min(block, position);
            position -= size;
            stream.Position = position;
            var chunk = new byte[size];
            stream.ReadExactly(chunk);
            newlines += chunk.Count(b => b == (byte)'\n');
            buffer.InsertRange(0, chunk);
        }

        var lines = System.Text.Encoding.UTF8.GetString(buffer.ToArray())
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
    }
}
