#!/bin/sh
# Live checks of the 0.5 music catalogue routes, run inside the Jellyfin container.
# Creates a throw-away user nt_m (deleted at the end); reads keys from the JellyfinEnhanced config, never prints them.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096
P=$B/Plugins/NflixButler
A="Authorization: MediaBrowser Token=\"$JK\""
code() { curl -s -o /tmp/nm.body -w '%{http_code}' "$@"; }
curl -s -H "$A" -H 'Content-Type: application/json' -d '{"Name":"nt_m","Password":"Nt-test-2026!"}' $B/Users/New >/dev/null
R=$(curl -s -H 'Content-Type: application/json' -H 'Authorization: MediaBrowser Client="nt", Device="nt", DeviceId="nt-m", Version="1"' -d '{"Username":"nt_m","Pw":"Nt-test-2026!"}' $B/Users/AuthenticateByName)
T=$(echo "$R" | grep -o '"AccessToken":"[^"]*"' | cut -d'"' -f4); I=$(echo "$R" | grep -o '"UserId":"[^"]*"' | head -1 | cut -d'"' -f4)
UA="Authorization: MediaBrowser Token=\"$T\""
echo "anon music search: $(code "$P/music/search?q=daft")"
echo "search: $(code -H "$UA" "$P/music/search?q=daft%20punk&limit=5")"
TID=$(grep -o '"tracks":\[{"id":"[0-9]*"' /tmp/nm.body | grep -o '[0-9]*"$' | tr -d '"')
echo " track=$TID artists=$(grep -o '"artists":\[[^]]*' /tmp/nm.body | grep -o '"name":"[^"]*"' | head -3 | tr '\n' ' ') cover=$(grep -o '"cover":"[^"]*"' /tmp/nm.body | head -1 | cut -c1-60)"
echo "artist by name: $(code -H "$UA" "$P/music/artist?name=Daft%20Punk")"; echo " $(grep -o '"artist":{[^}]*}' /tmp/nm.body | cut -c1-160) albums=$(grep -o '"type":"album"' /tmp/nm.body | wc -l) top=$(grep -o '"trackNumber"' /tmp/nm.body | wc -l)"
AID=$(grep -o '"albums":\[{"id":"[0-9]*"' /tmp/nm.body | grep -o '[0-9]*"$' | tr -d '"')
echo "album $AID: $(code -H "$UA" "$P/music/album?id=$AID")"; echo " $(head -c 220 /tmp/nm.body)"
echo "album bad id: $(code -H "$UA" "$P/music/album?id=../1")"
echo "stream range: $(curl -s -o /dev/null -D /tmp/nm.h -w '%{http_code} %{size_download}' -H "$UA" -H 'Range: bytes=0-1023' "$P/music/stream?trackId=$TID")"; grep -i 'content-range\|content-type' /tmp/nm.h | tr -d '\r' | tr '\n' ' '; echo
echo "stream ApiKey: $(curl -s -o /dev/null -w '%{http_code} %{size_download}' -H 'Range: bytes=0-99' "$P/music/stream?trackId=$TID&ApiKey=$T")"
echo "download non-admin: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"trackIds":["'$TID'"]}' $P/music/download)"
echo "download admin: $(code -H "$A" -H 'Content-Type: application/json' -d '{"trackIds":["'$TID'"]}' $P/music/download)"; cat /tmp/nm.body; echo
for i in $(seq 1 40); do S=$(curl -s -H "$A" "$P/music/states?ids=$TID"); echo "$S" | grep -q '"state":"done"' && break; echo "$S" | grep -q '"state":"failed"' && break; sleep 5; done
echo "states: $S"
F=$(find /media/plex/Musiques/YouTube -newer /tmp/nm.h -name '*.m4a' 2>/dev/null | head -1); echo "file: $F"
[ -n "$F" ] && ls -la "$(dirname "$F")" | tail -n +2 && /usr/lib/jellyfin-ffmpeg/ffprobe -v error -show_entries format_tags=title,artist,album,track,date -show_streams -select_streams v -of compact "$F" 2>&1 | head -5
echo "settings page: $(curl -s "$B/web/ConfigurationPage?name=nflixbutler" | grep -c 'data-role="page"')"
echo "sync config: $(code -H "$UA" "$P/sync/config")"; cat /tmp/nm.body; echo
echo "google start own: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"userId":"'$I'"}' $P/sync/google/start)"; cat /tmp/nm.body; echo
echo "google start other: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"userId":"00000000000000000000000000000001"}' $P/sync/google/start)"
echo "google poll own: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"userId":"'$I'"}' $P/sync/google/poll)"; cat /tmp/nm.body; echo
echo "legacy yt search: $(code -H "$UA" "$P/yt/search?q=daft&limit=3")"
echo "legacy anon: $(code "$P/yt/search?q=daft")"
sleep 40
echo "states after scan: $(curl -s -H "$A" "$P/music/states?ids=$TID")"
curl -s -o /dev/null -w "cleanup %{http_code}\n" -X DELETE -H "$A" $B/Users/$I
rm -f /tmp/nm.*
