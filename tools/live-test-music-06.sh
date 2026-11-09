#!/bin/sh
# Live checks of the 0.6 music routes (feed, lyrics, pictures, migration), run inside the Jellyfin container.
# Reads the server key from the JellyfinEnhanced config; never prints it.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096
P=$B/Plugins/NflixButler
A="Authorization: MediaBrowser Token=\"$JK\""
code() { curl -s -o /tmp/nm.body -w '%{http_code}' "$@"; }
MODE=${1:-all}
echo "anon feed: $(code "$P/music/feed") lyrics: $(code "$P/music/lyrics?artist=a&title=b") pictures: $(code "$P/music/pictures?names=a") migrate: $(code -X POST "$P/music/migrate")"
if [ "$MODE" = all ] || [ "$MODE" = feed ]; then
  echo "feed: $(code -H "$A" "$P/music/feed?refresh=true")"
  grep -o '"key":"[^"]*","title":"[^"]*"' /tmp/nm.body | tr '\n' ' '; echo
  echo " covers: $(grep -o '"cover":"https://[^"]*"' /tmp/nm.body | head -2 | tr '\n' ' ')"
  echo " ytimg in feed: $(grep -c 'ytimg' /tmp/nm.body)"
fi
if [ "$MODE" = all ] || [ "$MODE" = lyrics ]; then
  for t in "Stromae|Alors on danse" "Angèle|Balance ton quoi" "Indila|Dernière danse" "Daft Punk|Get Lucky (feat. Pharrell Williams and Nile Rodgers)" "Adele|Hello" "Billie Eilish|bad guy" "Tyler, The Creator|See You Again" "Aya Nakamura|Djadja" "Ariana Grande|kiss me" "Orelsan|La pluie"; do
    C=$(code -G -H "$A" --data-urlencode "artist=${t%%|*}" --data-urlencode "title=${t#*|}" "$P/music/lyrics")
    echo "lyrics $t: $C synced=$(grep -o '"synced":[a-z]*' /tmp/nm.body | cut -d: -f2) lines=$(grep -o '"startMs"' /tmp/nm.body | wc -l) first=$(grep -o '"text":"[^"]\+"' /tmp/nm.body | sed -n 3p | cut -c1-60)"
  done
  echo "lyrics bad: $(code -H "$A" "$P/music/lyrics?artist=&title=x")"
fi
if [ "$MODE" = all ] || [ "$MODE" = pictures ]; then
  echo "pictures: $(code -H "$A" "$P/music/pictures?names=Daft%20Punk|Ariana%20Grande|Tyler,%20The%20Creator|Nobody%20Zzqx")"; cat /tmp/nm.body | cut -c1-400; echo
fi
if [ "$MODE" = migrate ]; then
  echo "migrate: $(code -X POST -H "$A" "$P/music/migrate")"; cat /tmp/nm.body; echo
  find /media/plex/Musiques/YouTube | sort
fi
