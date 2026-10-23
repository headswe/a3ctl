# a3ctl

Command-line control for our Arma 3 server box: start/stop servers and headless clients,
update the server install, and manage Workshop mods (add, download, update, keys).
Single self-contained Linux binary, one JSON config file, no daemon.

## Build and deploy

```sh
dotnet publish src/A3Ctl -c Release -r linux-x64 -o publish/linux-x64
scp publish/linux-x64/a3ctl arma@server:~/bin/a3ctl
```

On the server, as the `arma` user:

```sh
mkdir -p ~/a3ctl && a3ctl config init ~/a3ctl/a3ctl.json \
    --steam-user <steam_account> --steamcmd ~/steamcmd/steamcmd.sh \
    --install-dir ~/arma3/install         # minimal config: one install, nothing else
export A3CTL_CONFIG=~/a3ctl/a3ctl.json    # put this in ~/.bashrc
steamcmd +login <steam_account> +quit     # once: caches the login / Steam Guard
a3ctl config check
```

Then `a3ctl install update`, add servers with `a3ctl server add` and mods with
`a3ctl mods add <id> -m <modset>` (see "Setting things up" below;
[`a3ctl.example.json`](a3ctl.example.json) shows what a full config looks like). State, logs, Workshop downloads and
mod folders go next to the config file unless you change `stateDir`, `logsDir`,
`workshopDir` and `modsDir`.

Requires `steamcmd` and `setsid` (util-linux) on the box. Run a3ctl as the account that
owns the Arma files.

## Everyday use

```sh
a3ctl status                              # what is running, what is missing
a3ctl status --watch                      # same, refreshing every 2s (Ctrl+C to quit)
a3ctl server start main hc1               # servers first, then headless clients
a3ctl server stop --all                   # SIGTERM, SIGKILL after 30s
a3ctl server restart main
a3ctl server logs main -f                 # follows across restarts
a3ctl server cmd main                     # exact command line + preflight problems

a3ctl mods add 450814997 https://steamcommunity.com/sharedfiles/filedetails/?id=463939057 -m main
a3ctl mods import <collection id or url> -m events
a3ctl mods check                          # compare with the Workshop (exit 2 = updates available)
a3ctl mods update                         # download missing + outdated, retries timeouts
a3ctl mods download @ace                  # force re-download / validate one mod
a3ctl mods remove @old_mod --delete-files

a3ctl modset add main @cba_a3 @ace
a3ctl modset show main

a3ctl install update                      # SteamCMD app_update 233780; servers must be stopped
a3ctl server sync --all                    # build/refresh server folders + keys (start does this too)
a3ctl keys sync                           # keys only; also runs on every start
```

Mod names can be typed with or without the `@`.

In a terminal you get live progress for downloads and install updates (one row per mod
with size, speed and retries), spinners while servers start and stop, and a confirmation
before stopping or restarting running servers or deleting mods. `-y` skips the questions,
`-v` shows the full SteamCMD output (always in `logsDir/steamcmd` too). When output is
piped or run from cron, everything falls back to plain lines with no prompts, so scripts
keep working; `NO_COLOR` is respected and `A3CTL_INTERACTIVE=1` forces the live displays
on terminals that are misdetected.

## Setting things up

Everything can be done from the CLI; you never have to edit the JSON by hand (you still
can, `a3ctl config show` prints it).

```sh
a3ctl install add arma3 --branch creatordlc --update     # add an install and download it
a3ctl install add profiling --branch profiling --branch-password <pw>
a3ctl install'set arma3 --branch public                  # then `a3ctl install update arma3`
a3ctl install remove profiling --delete-files

a3ctl server add main --port 2302                        # also writes a starter server.cfg/basic.cfg
a3ctl server add hc1 --for main                          # headless client that follows main
a3ctl server set main --modset main --server-modset server --dlc gm --env DISPLAY=:99
a3ctl server args main -- -exThreads=7 -enableHT -hugepages -limitFPS=1000
a3ctl server show main
a3ctl server remove old_server                           # folder and logs are kept

a3ctl modset create main
a3ctl modset move main @cba_a3 --first                   # load order: --first/--last/--before/--after
a3ctl modset rename main core                            # updates servers and Swifty repos too

a3ctl config set steamUser tmf_admin                     # steamCmd, steamUser, stateDir, logsDir, workshopDir, modsDir

a3ctl swifty setup --group-dir ~/swifty/t1t --publish-dir /var/www/swifty/t1t --url https://mods.example.com/t1t/
a3ctl swifty repo set main --modset main --optional-modset optional
```

`server set` only changes what you pass; list options (`--modset`, `--dlc`, ...) replace
the list, and given without values clear it. Launch flags go after `--` so they are not
read as a3ctl options. A headless client added with `--for main` takes its install, mods,
DLC and connect address from `main` whenever the config loads, and its password from
`main`'s `server.cfg` at launch, so it never drifts; `--for none` detaches it. The starter
`server.cfg` allows headless clients from 127.0.0.1 and checks mod signatures
(`verifySignatures = 2`).

## Swifty (publishing mods to players)

The Swifty group folder is yours: `swifty-group.json` (group details, `repositories`,
`servers`, TeamSpeak, links, ...), images, `news/` and `events/`. Create it with
`swifty admin init` or by hand and edit it as the Swifty docs describe; a3ctl never
writes it. a3ctl only fills the **mod lists of the repository files** from modsets, checks
that players get what the servers load, and runs the official `swifty` CLI to publish.

```sh
a3ctl swifty check                 # consistency checks, see below
a3ctl swifty publish               # check, update repository files, `swifty admin publish`
a3ctl mods update --publish        # update mods, then publish if everything downloaded
a3ctl swifty invite                # swifty:// link for Discord
a3ctl swifty generate              # only update the repository files (to look at them)
```

Config (the `swifty` section of [`a3ctl.example.json`](a3ctl.example.json)):

- `groupDir`: the folder with `swifty-group.json`; `publishDir`: web folder it is served
  from; `files`: `link` (default), `symlink`, `copy` or `none`, passed to
  `swifty admin publish --files` (`link` needs `publishDir` on the same filesystem as
  `modsDir`); `url`: public URL for invite links.
- `repos.<id>`, keyed by repository id in `swifty-group.json`: `modsets` become
  `requiredMods`, `optionalModsets` become `optionalMods` (client-side extras players can
  tick), `dlcs` defaults to the DLC of the servers using the repository. a3ctl sets only
  `modsDirectory` (its lowercased mod mirrors), `requiredMods`, `optionalMods` and `dlcs`
  in the file named by the repository's `source`; `name`, `game`, `clientParameters`,
  `icon`, `banner`, `optionalDlcs` etc. stay as you wrote them. Repositories not listed
  here are left alone.

Servers in `swifty-group.json` are matched to a3ctl servers by id, otherwise by port when
only one a3ctl server uses it. `swifty check` (also run by `publish`, which refuses on
errors) catches:

- a server loading mods or DLC its repository does not give players (nobody could join);
- a server password in `swifty-group.json` that differs from the server's `server.cfg`;
- repository mods that are not installed yet, or repositories/servers that point nowhere;
- as warnings: repository mods the server does not load, missing images, Swifty servers
  with no matching a3ctl server.

Key sync includes the mods of each server's Swifty repository, optional ones too, so
players running an optional mod are not kicked for an unknown signature.

Swifty publishes server passwords in the public `group.json`; protect the web folder
with basic auth if that matters.

## Config

See [`a3ctl.example.json`](a3ctl.example.json). Relative paths resolve against the config
file's folder. The CLI rewrites the file when you add/remove mods or edit modsets, so
don't put comments in it.

- **installs**: Arma 3 server installs managed by SteamCMD. Use `"branch": "creatordlc"`
  for CDLC (gm, vn, ws, ...). Add `"branchPassword"` for protected branches such as profiling.
- **servers**: each server/headless client points at an install and runs from its own
  `dir` (see "Server folders" below), which holds `server.cfg`, `basic.cfg`, `profile/`,
  `keys/`, `mpmissions/` and `battleye/`. `dlc` lists CDLC folders, `modsets`
  become `-mod`, `serverModsets` become `-serverMod`, `args` are extra flags, `env` extra
  environment variables (e.g. `DISPLAY`). a3ctl generates `-port`, `-config`, `-cfg`,
  `-profiles`, `-name`, `-mod`, `-serverMod` (and `-client -connect -password` for headless).
- **mods**: catalog of `@name` -> Workshop ID. `mods add-local @name` registers a mod you
  copied into the mods dir by hand.
- **modsets**: named, ordered mod lists; load order is the order in the list.

Steam: set `steamUser` and log in once interactively so SteamCMD caches the session. You
can also set `A3CTL_STEAM_PASSWORD`, but SteamCMD then gets the password on its command
line, visible in `ps`. Anonymous logins cannot download Arma 3 Workshop items.

## How it works

- **Workshop mods**: SteamCMD downloads into `workshopDir`, which a3ctl never modifies,
  so later updates stay incremental. Each mod is then mirrored to `modsDir/@name` with
  every file and folder name lowercased (needed by Arma on Linux) using hardlinks: no extra
  disk and seconds per mod. Keep `workshopDir` and `modsDir` on the same filesystem,
  otherwise it falls back to copying. The new mirror is built beside the old one and
  swapped in, so a running server keeps its open files; a3ctl tells you which running
  servers need a restart. Mod folders that a3ctl did not create are never touched.
- **Updates**: `mods check` asks the Steam Web API for each item's `time_updated` and
  compares it with the version that was downloaded. Downloads are batched in one SteamCMD
  session and failed items (typically timeouts on big mods) are retried.
- **Server folders**: each server runs from its own folder, so missions, keys and BattlEye
  settings are per server while the game files are shared. The install's top-level files
  (`arma3server_x64`, `.so` libraries, ...) are **hardlinked** into the folder, so the binary
  sees the server folder as its home; the install's folders (`addons`, `dta`, `gm`, `vn`,
  ...) are **symlinked**, so they follow install updates. `keys/`, `mpmissions/`, `battleye/`
  (copied from the install once), `profile/` and the cfg files belong to the server. Every
  start (and `a3ctl server sync`) refreshes the folder: hardlinks SteamCMD left stale by
  replacing a file are renewed, new install folders are linked, links to removed ones are
  deleted. Anything else you put in the folder is left alone, with a warning if it hides
  something from the install, so e.g. a symlinked shared `mpmissions` is fine. A server
  folder may not be inside its install. Hardlinks need the server folders on the same
  filesystem as the install; otherwise the files are copied (and refreshed the same way).
- **Keys**: each server's `keys/` gets the install's base keys (`a3.bikey`, CDLC keys) plus
  the `.bikey` files of its client mods and of its Swifty repository's mods (optional ones
  included). a3ctl remembers which keys it placed and only ever removes those; hand-placed
  keys are left alone.
- **Processes**: servers start detached in their own session (they survive logout),
  output goes to `logsDir/<server>/<server>_<timestamp>.log`. The PID and the kernel's
  process start time are stored in `stateDir/run`, so a recycled PID is never mistaken for
  a server. A server that dies within 5 seconds of starting is reported with its log tail.
  `start` refuses to run when the exe, configs, DLC or mods are missing or the port is
  taken by another running server.
- **Locking**: commands that change things take a lock in `stateDir`, so two people
  can't run `mods update` and `server start` against each other.

## Testing

`test/linux/` has an end-to-end run in a Linux container with fake SteamCMD and Arma
binaries; see its README.
