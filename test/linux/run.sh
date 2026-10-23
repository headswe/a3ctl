#!/bin/bash
set -u
cp /test/a3ctl.json /etc/a3ctl.json; export A3CTL_CONFIG=/etc/a3ctl.json NO_COLOR=1
A=/bin/a3ctl; cp /pub/a3ctl $A; chmod +x $A
mkdir -p /srv/a3/servers/main/profile /srv/a3/servers/hc1 /srv/a3/servers/broken
printf 'hostname = "test";\npassword = "secret";\n' > /srv/a3/servers/main/server.cfg
touch /srv/a3/servers/main/basic.cfg /srv/a3/servers/hc1/basic.cfg /srv/a3/servers/broken/server.cfg /srv/a3/servers/broken/basic.cfg
mkdir -p /srv/a3/swifty/repos
cat > /srv/a3/swifty/swifty-group.json <<JSON
{ "id": "test-group", "name": "Test Group", "timeZone": "Europe/Berlin",
  "repositories": [ { "id": "main", "name": "Main", "source": "repos/main.json" } ],
  "servers": [ { "id": "main", "name": "Main Server", "game": "arma3", "host": "203.0.113.10", "port": 2302, "password": "secret", "repository": "main" } ] }
JSON
echo '{ "name": "Main", "game": "arma3", "requiredMods": ["@*"], "clientParameters": "-skipIntro" }' > /srv/a3/swifty/repos/main.json
t() { echo; echo "################ \$ a3ctl $*"; $A "$@"; echo "[exit $?]"; }
t config check
t install update
t mods add 450814997 https://steamcommunity.com/sharedfiles/filedetails/?id=463939057 -m main
t mods add 751965892 --name @acre2 -m main --download
t mods add 1779063631 -m optional
t mods add 123
t mods list
t mods check
t mods update
t mods check
echo "--- mods tree (lowercased, marker outside mod folders)"; find /srv/a3/mods | sort
echo "--- hardlink counts (expect 2)"; stat -c '%h %n' /srv/a3/mods/@cba_a3/addons/*
t server sync main hc1
echo "--- server folder: folders symlinked, binary hardlinked (link count 2), keys/mpmissions/battleye own"; ls -la /srv/a3/servers/main; stat -c "%h %n" /srv/a3/servers/main/arma3server_x64
t server sync main
t keys sync; echo "--- keys of main (expect a3 + gm + 4 mods incl. optional zeus)"; ls /srv/a3/servers/main/keys
echo "--- keys of hc1 (a3 + gm + 3 mods, no swifty optional)"; ls /srv/a3/servers/hc1/keys
t server cmd main
t server start main hc1 broken
t status
t server logs main -n 20
echo "--- expect cwd /srv/a3/servers/main in the log above"
t mods download cba_a3
t modset remove main @acre2
t keys sync; ls /srv/a3/servers/main/keys
t install update
echo "--- simulate an update replacing a file, adding a folder, removing one"
rm /srv/a3/install/arma3server_x64 && cp /test/fake-arma /srv/a3/install/arma3server_x64 && touch -d "2001-01-01" /srv/a3/install/arma3server_x64; mkdir -p /srv/a3/install/vn /srv/a3/install/dta; t server sync main; rmdir /srv/a3/install/dta; t server sync main
ls -la /srv/a3/servers/main; stat -c "%h %n" /srv/a3/servers/main/arma3server_x64
t server restart --all
t server stop --all
t status
t server logs main -n 5
t swifty check
t swifty publish
echo "--- repo file (mod lists filled, clientParameters kept)"; cat /srv/a3/swifty/repos/main.json
t swifty invite
t mods remove @acre2 --delete-files; ls /srv/a3/mods /srv/a3/workshop/steamapps/workshop/content/107410
t modset list
