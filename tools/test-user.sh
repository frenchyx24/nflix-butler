#!/bin/sh
# Creates (create) or deletes (delete) the throw-away Jellyfin user "nflixqa" used by the app UI tests.
# The password is passed as $2; keys come from the JellyfinEnhanced config and are never printed.
X=/config/plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml
U=$(grep -o '<JellyseerrUrls>[^<]*' $X | cut -d'>' -f2 | cut -d, -f1); U=$(echo "$U" | sed "s#/\$##")
SK=$(grep -o '<JellyseerrApiKey>[^<]*' $X | cut -d'>' -f2)
JK=$(curl -s -H "X-Api-Key: $SK" $U/api/v1/settings/jellyfin | sed 's/.*"apiKey":"\([^"]*\)".*/\1/')
B=http://localhost:8096
A="Authorization: MediaBrowser Token=\"$JK\""
ID=$(curl -s -H "$A" "$B/Users" | grep -o '"Name":"nflixqa","ServerId":"[^"]*","Id":"[^"]*"' | grep -o '"Id":"[^"]*"' | cut -d'"' -f4)
if [ "$1" = delete ]; then
  [ -n "$ID" ] && curl -s -o /dev/null -w 'delete %{http_code}\n' -X DELETE -H "$A" "$B/Users/$ID"
  exit 0
fi
[ -z "$ID" ] && curl -s -o /dev/null -w 'create %{http_code}\n' -H "$A" -H 'Content-Type: application/json' -d "{\"Name\":\"nflixqa\",\"Password\":\"$2\"}" "$B/Users/New"
ID=$(curl -s -H "$A" "$B/Users" | grep -o '"Name":"nflixqa","ServerId":"[^"]*","Id":"[^"]*"' | grep -o '"Id":"[^"]*"' | cut -d'"' -f4)
echo "user id: $ID"
curl -s -o /dev/null -w 'login %{http_code}\n' -H 'Content-Type: application/json' -H 'Authorization: MediaBrowser Client="qa", Device="qa", DeviceId="qa-1", Version="1"' -d "{\"Username\":\"nflixqa\",\"Pw\":\"$2\"}" "$B/Users/AuthenticateByName"
