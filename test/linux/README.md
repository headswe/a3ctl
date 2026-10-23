# Linux end-to-end test

Runs the published binary against a fake SteamCMD and a fake `arma3server_x64` inside a
Linux container: install update, Workshop download with a simulated timeout + retry,
lowercase hardlink mirror, per-server folders (symlinked folders, hardlinked binary, refresh after
updates), per-server key sync (incl. optional Swifty mods), server/headless start, failed start detection,
mod update while running, restart, stop, Swifty generate + publish (fake swifty CLI). Workshop metadata comes from the real Steam API.

```sh
dotnet publish src/A3Ctl -c Release -r linux-x64 -o publish/linux-x64
docker run --rm -v "$PWD/test/linux:/test" -v "$PWD/publish/linux-x64:/pub:ro" \
  mcr.microsoft.com/dotnet/runtime-deps:10.0 /test/run.sh
```
