#!/bin/bash

if [ -z "$UNNATURAL_ROOT" ]; then
    echo "Error: UNNATURAL_ROOT is not set."
    exit 1
fi
if [ -z "$UNNATURAL_URL" ]; then
    echo "Error: UNNATURAL_URL is not set."
    exit 1
fi

while true; do
    find "$UNNATURAL_ROOT/replays" -type f -name '*.uwreplay' ! -name '*_uploaded.uwreplay' | while read -r file; do
        response=$(curl -L -H "Authorization: Bearer admin" -s -o /dev/null -w "%{http_code}" -F "file=@$file" "$UNNATURAL_URL/api/upload_replay")
        if [ "$response" -ge 200 ] && [ "$response" -lt 300 ]; then
            mv "$file" "${file%.uwreplay}_uploaded.uwreplay"
            echo "Successfully uploaded: $file"
        else
            echo "Failed to upload: $file"
        fi
    done
    
    sleep 60
done
