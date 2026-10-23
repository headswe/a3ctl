namespace A3Ctl;

// Each server runs from its own folder: the install's top-level files (binaries, .so
// libraries) are hardlinked in so the binary treats the server folder as its home, and its
// folders (addons, dta, gm, vn, ...) are symlinked so they follow install updates.
// keys, mpmissions, battleye, profile and the cfg files are the server's own.
public sealed class Instances(App app)
{
    // Never linked to the install.
    private static readonly HashSet<string> OwnEntries = new(StringComparer.Ordinal)
    {
        "keys", "mpmissions", "battleye", "profile", "steamapps",
    };

    public sealed record Result(int Linked, int Relinked, int Pruned);

    // Brings the server folder in line with its install. Safe to run any time: entries the
    // server already owns are left alone, and SteamCMD replacing files during an update
    // only means the stale hardlinks are refreshed here.
    public Result Ensure(string id)
    {
        Native.RequireLinux("building server folders (symlinks and hardlinks)");
        try
        {
            return EnsureLinux(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserException($"{id}: could not set up {app.ServerDir(id)}: {ex.Message}");
        }
    }

    private Result EnsureLinux(string id)
    {
        var server = app.Server(id);
        var install = Path.TrimEndingDirectorySeparator(app.InstallDir(server.Install));
        var dir = Path.TrimEndingDirectorySeparator(app.ServerDir(id));
        if (Problem(id) is { } problem)
        {
            throw new UserException(problem);
        }

        if (!Directory.Exists(install))
        {
            throw new UserException($"install {server.Install} is not downloaded yet (`a3ctl install update {server.Install}`)");
        }

        Directory.CreateDirectory(dir);
        var own = new HashSet<string>(OwnEntries, StringComparer.Ordinal);
        foreach (var cfg in new[] { server.Config, server.Basic }.OfType<string>())
        {
            own.Add(cfg);
        }

        int linked = 0, relinked = 0, pruned = 0;
        foreach (var entry in new DirectoryInfo(install).EnumerateFileSystemInfos())
        {
            if (own.Contains(entry.Name))
            {
                continue;
            }

            var dest = Path.Combine(dir, entry.Name);
            var existing = Entry(dest);
            if (existing?.LinkTarget is { } target)
            {
                if (!SamePath(Path.GetFullPath(target, dir), entry.FullName))
                {
                    Ui.Warn($"{dest} links to {target}, not the install; leaving it");
                }

                continue;
            }

            if (entry is DirectoryInfo)
            {
                if (existing is null)
                {
                    Directory.CreateSymbolicLink(dest, entry.FullName);
                    linked++;
                }
                else
                {
                    Ui.Warn($"{dest} is a local folder hiding the install's {entry.Name}; delete it to use the install's");
                }

                continue;
            }

            if (existing is null)
            {
                Link(entry.FullName, dest);
                linked++;
            }
            else if (existing is FileInfo file && !Same(file, (FileInfo)entry))
            {
                // SteamCMD replaced the install's file; the old hardlink still has the old one.
                var fresh = dest + ".a3ctl-new";
                File.Delete(fresh);
                Link(entry.FullName, fresh);
                File.Move(fresh, dest, overwrite: true);
                relinked++;
            }
        }

        // Links to things an update removed from the install.
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is { } target &&
                Path.GetFullPath(target, dir) is var full &&
                full.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.Exists(full))
            {
                entry.Delete();
                pruned++;
            }
        }

        Directory.CreateDirectory(Path.Combine(dir, "keys"));
        Directory.CreateDirectory(Path.Combine(dir, "mpmissions"));
        var battleye = Path.Combine(install, "battleye");
        if (Directory.Exists(battleye) && Entry(Path.Combine(dir, "battleye")) is null)
        {
            CopyTree(battleye, Path.Combine(dir, "battleye"));
        }

        if (linked + relinked + pruned > 0)
        {
            Ui.Ok($"{id}: server folder synced with install {server.Install} ({linked} linked, {relinked} refreshed, {pruned} removed)");
        }

        return new Result(linked, relinked, pruned);
    }

    // Why this server's folder cannot be an instance of its install, or null.
    public string? Problem(string id)
    {
        var server = app.Server(id);
        var install = Path.TrimEndingDirectorySeparator(app.InstallDir(server.Install));
        var dir = Path.TrimEndingDirectorySeparator(app.ServerDir(id));
        if (SamePath(dir, install) || dir.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return $"server {id}'s folder {dir} is inside install {server.Install}; give it its own folder (`a3ctl server set {id} --dir servers/{id}`)";
        }

        return null;
    }

    private static void Link(string source, string dest)
    {
        Native.LinkOrCopy(source, dest);
        // A copy (different filesystem) gets the source's time so it compares equal later.
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
    }

    private static bool Same(FileInfo a, FileInfo b) =>
        a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The entry at path, including symlinks whose target is missing.
    private static FileSystemInfo? Entry(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            return file;
        }

        return Directory.Exists(path) ? new DirectoryInfo(path) : null;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            CopyTree(sub, Path.Combine(destination, Path.GetFileName(sub)));
        }
    }
}
