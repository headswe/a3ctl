using System.CommandLine;

namespace A3Ctl;

// Options shared by `server add` and `server set`. Only options that were given change
// anything; list options given without values clear the list.
public sealed class ServerOptions
{
    public readonly Option<string?> For = new("--for") { Description = "Headless client of this local server: follows its install, mods and DLC ('none' detaches)" };
    public readonly Option<string?> Install = new("--install", "-i") { Description = "Install to run from" };
    public readonly Option<string?> Type = new("--type") { Description = "server or headless" };
    public readonly Option<int?> Port = new("--port", "-p") { Description = "Game port (servers)" };
    public readonly Option<string?> Dir = new("--dir") { Description = "Folder with server.cfg, basic.cfg and profile/ (default: servers/<name> next to the config)" };
    public readonly Option<string?> Exe = new("--exe") { Description = "Server binary, relative to the install (default: arma3server_x64)" };
    public readonly Option<string?> ServerCfg = new("--server-cfg") { Description = "server.cfg file name in the server folder, or 'none'" };
    public readonly Option<string?> BasicCfg = new("--basic-cfg") { Description = "basic.cfg file name in the server folder, or 'none'" };
    public readonly Option<string?> Connect = new("--connect") { Description = "Headless clients: host:port of the server to join" };
    public readonly Option<string?> Password = new("--password") { Description = "Headless clients: server password (default: read from the local server's server.cfg)" };
    public readonly Option<string[]> Modsets = List("--modset", "-m", "Modsets loaded with -mod (replaces the list)");
    public readonly Option<string[]> ServerModsets = List("--server-modset", null, "Modsets loaded with -serverMod (replaces the list)");
    public readonly Option<string[]> Dlc = List("--dlc", null, "DLC folders such as gm, vn, ws (replaces the list)");
    public readonly Option<string[]> Env = List("--env", null, "Environment variables as KEY=VALUE; KEY= removes one");

    private static Option<string[]> List(string name, string? alias, string description)
    {
        var option = alias is null ? new Option<string[]>(name) : new Option<string[]>(name, alias);
        option.Description = description;
        option.Arity = ArgumentArity.ZeroOrMore;
        option.AllowMultipleArgumentsPerToken = true;
        return option;
    }

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { For, Install, Type, Port, Dir, Exe, ServerCfg, BasicCfg, Connect, Password, Modsets, ServerModsets, Dlc, Env })
        {
            command.Options.Add(option);
        }

        Type.AcceptOnlyFromAmong("server", "headless");
    }

    public static bool Given(ParseResult parse, Option option) => parse.GetResult(option) is { Implicit: false };

    public void Apply(ParseResult p, ServerConfig s)
    {
        if (Given(p, For))
        {
            s.For = NoneToNull(p.GetValue(For));
            if (s.For is not null)
            {
                s.Type = "headless";
                s.Config = null;
            }
        }

        if (s.For is not null)
        {
            var followed = new Option[] { Install, Modsets, Dlc, Connect }.Where(o => Given(p, o)).Select(o => o.Name).ToList();
            if (followed.Count > 0)
            {
                throw new UserException($"this headless client follows {s.For} and takes its {string.Join(", ", followed)} from there; change {s.For} instead, or detach with --for none");
            }
        }

        if (Given(p, Install)) s.Install = p.GetValue(Install)!;
        if (Given(p, Type)) s.Type = p.GetValue(Type)!;
        if (Given(p, Port)) s.Port = p.GetValue(Port)!.Value;
        if (Given(p, Dir)) s.Dir = p.GetValue(Dir)!;
        if (Given(p, Exe)) s.Exe = p.GetValue(Exe)!;
        if (Given(p, ServerCfg)) s.Config = NoneToNull(p.GetValue(ServerCfg));
        if (Given(p, BasicCfg)) s.Basic = NoneToNull(p.GetValue(BasicCfg));
        if (Given(p, Connect)) s.Connect = p.GetValue(Connect);
        if (Given(p, Password)) s.Password = p.GetValue(Password) is { Length: > 0 } pw ? pw : null;
        if (Given(p, Modsets)) s.Modsets = p.GetValue(Modsets)!.ToList();
        if (Given(p, ServerModsets)) s.ServerModsets = p.GetValue(ServerModsets)!.ToList();
        if (Given(p, Dlc)) s.Dlc = p.GetValue(Dlc)!.ToList();
        if (Given(p, Env))
        {
            foreach (var pair in p.GetValue(Env)!)
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    throw new UserException($"--env needs KEY=VALUE, got '{pair}'");
                }

                var (key, value) = (pair[..eq], pair[(eq + 1)..]);
                if (value.Length == 0)
                {
                    s.Env.Remove(key);
                }
                else
                {
                    s.Env[key] = value;
                }
            }
        }
    }

    private static string? NoneToNull(string? value) => value is null or "none" or "" ? null : value;
}
