#!/bin/sh
# Live checks of the Nflix Butler routes, run inside the Jellyfin container (sh tools/live-test.sh).
# Creates two throw-away users nt_a / nt_b; reads keys from the JellyfinEnhanced config, never prints them.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096
code() { curl -s -o /tmp/nt.body -w '%{http_code}' "$@"; }
A="Authorization: MediaBrowser Token=\"$JK\""
for n in nt_a nt_b; do
  if ! curl -s -H "$A" "$B/Users" | grep -q "\"Name\":\"$n\""; then
    curl -s -H "$A" -H 'Content-Type: application/json' -d '{"Name":"'$n'","Password":"Nt-test-2026!"}' $B/Users/New >/dev/null
  fi
done
tok() { curl -s -H 'Content-Type: application/json' -H 'Authorization: MediaBrowser Client="nt", Device="nt", DeviceId="nt-'$1'", Version="1"' -d '{"Username":"'$1'","Pw":"Nt-test-2026!"}' $B/Users/AuthenticateByName; }
RA=$(tok nt_a); TA=$(echo "$RA" | grep -o '"AccessToken":"[^"]*"' | cut -d'"' -f4); IA=$(echo "$RA" | grep -o '"UserId":"[^"]*"' | head -1 | cut -d'"' -f4)
RB=$(tok nt_b); TB=$(echo "$RB" | grep -o '"AccessToken":"[^"]*"' | cut -d'"' -f4); IB=$(echo "$RB" | grep -o '"UserId":"[^"]*"' | head -1 | cut -d'"' -f4)
UA="Authorization: MediaBrowser Token=\"$TA\""
UB="Authorization: MediaBrowser Token=\"$TB\""
echo "users a=$IA b=$IB"
P=$B/Plugins/NflixButler
echo "anon search: $(code "$P/yt/search?q=daft")"
echo "anon sync status: $(code "$P/sync/status?userId=$IA")"
echo "anon admin status: $(code "$P/api/status")"
echo "X-Emby-Token key /Users: $(code -H "X-Emby-Token: $JK" $B/Users)"
echo "api_key query /Users: $(code "$B/Users?api_key=$JK")"
echo "X-Emby-Token injection: $(code -H "X-Emby-Token: $JK\", UserId=\"x" $B/Users)"
echo "Authorization priority (bad auth + good emby): $(code -H 'Authorization: MediaBrowser Token="bad"' -H "X-Emby-Token: $JK" $B/Users)"
echo "search: $(code -H "$UA" "$P/yt/search?q=daft%20punk%20one%20more%20time&limit=5")"; VID=$(grep -o '"videoId":"[A-Za-z0-9_-]\{11\}"' /tmp/nt.body | head -1 | cut -d'"' -f4); echo " vid=$VID $(head -c 160 /tmp/nt.body)"
echo "search q &#: $(code -H "$UA" "$P/yt/search?q=a%26b%23c&limit=3")"
echo "info: $(code -H "$UA" "$P/yt/info?videoId=$VID")"; head -c 200 /tmp/nt.body; echo
echo "info bad id: $(code -H "$UA" "$P/yt/info?videoId=--exec%20id")"
echo "album bad id: $(code -H "$UA" "$P/yt/album?browseId=../x")"
echo "stream range: $(curl -s -o /dev/null -D /tmp/nt.h -w '%{http_code} %{size_download}' -H "$UA" -H 'Range: bytes=0-1023' "$P/yt/stream?videoId=$VID")"; grep -i 'content-range\|content-type\|accept-ranges' /tmp/nt.h | tr -d '\r' | tr '\n' ' '; echo
echo "stream ApiKey query: $(curl -s -o /dev/null -w '%{http_code} %{size_download}' -H 'Range: bytes=0-99' "$P/yt/stream?videoId=$VID&ApiKey=$TA")"
echo "history A: $(code -H "$UA" "$P/yt/history")"; head -c 200 /tmp/nt.body; echo
echo "history B: $(code -H "$UB" "$P/yt/history")"; head -c 200 /tmp/nt.body; echo
echo "recent A: $(code -H "$UA" "$P/yt/recent")"; head -c 160 /tmp/nt.body; echo
echo "recent B: $(code -H "$UB" "$P/yt/recent")"; head -c 160 /tmp/nt.body; echo
echo "feed A: $(code -H "$UA" "$P/yt/feed")"; head -c 160 /tmp/nt.body; echo
echo "downloaded: $(code -H "$UA" "$P/yt/downloaded?ids=$VID,bad")"; cat /tmp/nt.body; echo
echo "download non-admin: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"videoId":"'$VID'"}' $P/yt/download)"
echo "download bad id admin: $(code -H "$A" -H 'Content-Type: application/json' -d '{"videoId":"../../etc"}' $P/yt/download)"
echo "sync config: $(code -H "$UA" "$P/sync/config")"; cat /tmp/nt.body; echo
echo "sync status own: $(code -H "$UA" "$P/sync/status?userId=$IA")"; cat /tmp/nt.body; echo
echo "sync status other: $(code -H "$UA" "$P/sync/status?userId=$IB")"
echo "sync status none: $(code -H "$UA" "$P/sync/status")"
echo "sync status admin-key other: $(code -H "$A" "$P/sync/status?userId=$IB")"
echo "sync import other: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"service":"deezer","playlistId":"1","userId":"'$IB'"}' $P/sync/import)"
echo "sync import own unlinked: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"service":"deezer","playlistId":"1","userId":"'$IA'"}' $P/sync/import)"; cat /tmp/nt.body; echo
echo "sync import bad playlist: $(code -H "$UA" -H 'Content-Type: application/json' -d '{"service":"deezer","playlistId":"../x","userId":"'$IA'"}' $P/sync/import)"
echo "sync import GET: $(code -H "$UA" "$P/sync/import")"
echo "sync link own (no app): $(code -H "$UA" -H 'Content-Type: application/json' -d '{"service":"deezer","code":"x","userId":"'$IA'"}' $P/sync/link)"; cat /tmp/nt.body; echo
echo "admin status non-admin: $(code -H "$UA" "$P/api/status")"
echo "admin status key: $(code -H "$A" "$P/api/status")"
echo "web dashboard: $(code -H "$A" "$P/web/dashboard")"
echo "web config: $(code -H "$A" "$P/web/config")"
echo "theme.js: $(code "$P/theme.js")"
echo "index theme injected: $(curl -s $B/web/index.html | grep -c 'NflixButler/theme.js')"
echo "NflixAuth beta manifest: $(code "$B/NflixAuth/Store/nflix-beta/manifest.json")"; head -c 220 /tmp/nt.body; echo
echo "NflixAuth stable manifest: $(code "$B/NflixAuth/Store/nflix/manifest.json")"; head -c 160 /tmp/nt.body; echo
echo "plugins: $(curl -s -H "$A" $B/Plugins | grep -o '"Name":"\(Nflix[^"]*\|Jellyfin Enhanced\|Moonbase\)","Version":"[^"]*","[^"]*":[^,]*,"[^"]*":[^,]*,"[^"]*":[^,]*,"Status":"[^"]*"' | tr '\n' ' ')"
echo "seerr users perms: $(curl -s -H "X-Api-Key: $SK" "$U/api/v1/user?take=100" | grep -o '"permissions":[0-9]*' | sort -u | tr '\n' ' ')"
echo "seerr defaultPerm: $(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/main | grep -o '"defaultPermissions":[0-9]*')"
echo "seerr->jellyfin users (sync test): $(curl -s -o /dev/null -w '%{http_code}' -H "X-Api-Key: $SK" "$U/api/v1/settings/jellyfin/users")"
echo "JE seerr user via plugin: $(code -H "$UA" "$B/JellyfinEnhanced/jellyseerr/user-status")"
