#!/bin/sh
# Downloads one catalogue track and one track through the legacy single-track route, then shows
# the filed result (folder, tags, embedded cover, lyrics sidecar). $1 = "cleanup" removes them.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096; P=$B/Plugins/NflixButler
A="Authorization: MediaBrowser Token=\"$JK\""
R=/media/plex/Musiques/YouTube
if [ "$1" = cleanup ]; then rm -rf "$R/Stromae" "$R/Angèle"; ls "$R"; exit 0; fi
TID=$(curl -s -G -H "$A" --data-urlencode "q=Stromae Alors on danse" "$P/music/search?limit=3" | grep -o '"tracks":\[{"id":"[0-9]*"' | grep -o '[0-9]*"$' | tr -d '"')
echo "catalogue track $TID: $(curl -s -H "$A" -H 'Content-Type: application/json' -d "{\"trackIds\":[\"$TID\"]}" $P/music/download)"
for i in $(seq 1 60); do S=$(curl -s -H "$A" "$P/music/states?ids=$TID"); echo "$S" | grep -q '"state":"done"\|"state":"failed"' && break; sleep 5; done; echo "state: $S"
VID=$(curl -s -G -H "$A" --data-urlencode "q=Angèle Balance ton quoi" "$P/yt/search?limit=3" | grep -o '"videoId":"[A-Za-z0-9_-]\{11\}"' | head -1 | cut -d'"' -f4)
echo "legacy $VID: $(curl -s -m 600 -H "$A" -H 'Content-Type: application/json' -d "{\"videoId\":\"$VID\"}" $P/yt/download)"
echo "legacy bad id: $(curl -s -o /dev/null -w '%{http_code}' -H "$A" -H 'Content-Type: application/json' -d '{"videoId":"../../etc"}' $P/yt/download)"
for d in "$R/Stromae" "$R/Angèle"; do
  find "$d" -type f | sort
  F=$(find "$d" -name '*.m4a' | head -1)
  [ -n "$F" ] && /usr/lib/jellyfin-ffmpeg/ffprobe -v error -show_entries format_tags=title,artist,album_artist,album,track -show_entries stream=codec_name,width -of compact "$F"
  L=$(find "$d" -name '*.lrc' | head -1); [ -n "$L" ] && head -3 "$L"
done
