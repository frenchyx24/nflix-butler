#!/bin/sh
# Library state after a music migration: duplicate album artists, embedded covers, lyrics through Jellyfin.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096
A="Authorization: MediaBrowser Token=\"$JK\""
echo "album artists (all):"
curl -s -H "$A" "$B/Artists/AlbumArtists?limit=2000" | grep -o '"Name":"[^"]*"' | cut -d'"' -f4 | sort > /tmp/aa.txt
wc -l < /tmp/aa.txt
echo "names with credit separators:"; grep -E ', |，| feat| ft\.' /tmp/aa.txt | head -20
echo "case/accent duplicates:"; tr 'A-Z' 'a-z' < /tmp/aa.txt | sed 's/[^a-z0-9]//g' | sort | uniq -d | head
for n in "Daft Punk" "Tyler, The Creator" "Ariana Grande"; do
  echo "== $n: $(grep -c "^$n" /tmp/aa.txt) entries starting with it: $(grep "^$n" /tmp/aa.txt | tr '\n' '|')"
done
ID=$(curl -s -G -H "$A" --data-urlencode "searchTerm=kiss me" "$B/Items?includeItemTypes=Audio&recursive=true&limit=5&fields=Path" | grep -o '"Id":"[^"]*","[^}]*petal' | head -1 | cut -d'"' -f4)
[ -z "$ID" ] && ID=$(curl -s -G -H "$A" --data-urlencode "searchTerm=kiss me" "$B/Items?includeItemTypes=Audio&recursive=true&limit=1" | grep -o '"Id":"[^"]*"' | head -1 | cut -d'"' -f4)
echo "kiss me item: $ID"
curl -s -H "$A" "$B/Items/$ID?fields=Path" | grep -o '"Path":"[^"]*"\|"AlbumArtist":"[^"]*"\|"Album":"[^"]*"' | tr '\n' ' '; echo
echo "jellyfin lyrics: $(curl -s -o /tmp/ly.json -w '%{http_code}' -H "$A" "$B/Audio/$ID/Lyrics") $(grep -o '"Start":[0-9]*' /tmp/ly.json | wc -l) synced lines"
echo "primary image: $(curl -s -o /dev/null -w '%{http_code} %{content_type}' -H "$A" "$B/Items/$ID/Images/Primary")"
