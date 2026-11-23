#!/usr/bin/env bash
set -euo pipefail
xvfb-run -a ./dist/app/PinterestDownload > /tmp/pinterest-download-smoke.log 2>&1 &
app_pid=$!
trap 'kill "$app_pid" 2>/dev/null || true' EXIT
status=000
for attempt in {1..30}; do
  status=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Origin: chrome-extension://alenpleailakepgbibnkkmhcmkoijmch' -H 'Content-Type: application/json' --data '[]' http://127.0.0.1:17849/pins || true)
  [ "$status" = 204 ] && break
  sleep 1
done
if [ "$status" != 204 ]; then cat /tmp/pinterest-download-smoke.log; echo "Application did not accept a browser connection: $status"; exit 1; fi
status=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Origin: https://example.com' -H 'Content-Type: application/json' --data '[]' http://127.0.0.1:17849/pins)
[ "$status" = 403 ] || { echo "Unexpected acceptance of web origin: $status"; exit 1; }
status=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Origin: moz-extension://12345678-1234-1234-1234-123456789abc' -H 'Content-Type: application/json' --data '[]' http://127.0.0.1:17849/pins)
[ "$status" = 204 ] || { echo "Firefox origin rejected: $status"; exit 1; }
echo 'Linux startup and browser connections: OK'
