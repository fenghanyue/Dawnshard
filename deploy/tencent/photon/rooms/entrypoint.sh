#!/usr/bin/env bash
set -uo pipefail
state_pid=''
luxon_pid=''
stop_children() {
    [ -z "$luxon_pid" ] || kill -TERM "$luxon_pid" 2>/dev/null || true
    [ -z "$state_pid" ] || kill -TERM "$state_pid" 2>/dev/null || true
}
trap 'stop_children; wait; exit 0' TERM INT
cd /app/state || exit 1
/usr/share/dotnet/dotnet DragaliaAPI.Photon.StateManager.dll &
state_pid=$!
cd /app || { stop_children; exit 1; }
/usr/local/bin/luxon_server &
luxon_pid=$!
wait -n "$state_pid" "$luxon_pid"
status=$?
stop_children
wait
# Either child stopping unexpectedly must restart the complete room service.
exit "$((status == 0 ? 1 : status))"
