#!/usr/bin/env bash
# Load check and graceful-shutdown check, against the stack started from compose.yml (see compose.example.yml).
#
#   deploy/load-check.sh            MaxConcurrentContexts + N simultaneous fetches: the extra N must get a prompt
#                                   503 capacity-exceeded, the rest must be served, memory must return to baseline.
#   deploy/load-check.sh shutdown   SIGTERM during a running fetch: it must finish (or time out) cleanly.
#   RECYCLE_AFTER_CONTEXTS=2 deploy/load-check.sh   the same load, with the browser recycled mid-run, so its replacement
#                                   launches while fetches are running.
#
# A temporary high-limit key is added for the run (deploy/load-check.compose.yml) and removed afterwards.
# Uses httpbin.org/delay as a slow public target: a manual check, not an automated test.
set -euo pipefail
cd "$(dirname "$0")/.."

N="${N:-4}"
# Slow public targets on different origins, used in turn: with MaxConcurrentPerOrigin (2) per origin they offer more
# slots than there are browser contexts, so the global limit is the one measured. One origin alone would only ever
# measure the per-origin limit.
read -r -a TARGETS <<<"${TARGETS:-https://httpbin.org/delay/6 https://httpbingo.org/delay/6 https://postman-echo.com/delay/6}"
KEY="$(openssl rand -hex 20)"
export LOAD_KEY_SHA256
LOAD_KEY_SHA256="$(printf '%s' "$KEY" | sha256sum | cut -d' ' -f1)"
WORK="$(mktemp -d)"
trap 'docker compose up -d app >/dev/null 2>&1; rm -rf "$WORK"' EXIT

healthy() { until [ "$(docker inspect weblens-app --format '{{.State.Health.Status}}' 2>/dev/null)" = healthy ]; do sleep 2; done; }
memory_mib() { docker stats --no-stream --format '{{.MemUsage}}' weblens-app | awk '{v=$1; if (v ~ /GiB/) {sub("GiB","",v); v*=1024} else {sub("MiB","",v)}; printf "%d", v}'; }
fetch() { # $1 url, $2 output file: writes "status seconds"
    docker run --rm --network weblens-ingress curlimages/curl:8.16.0 -s -m 90 -o /dev/null -w '%{http_code} %{time_total}\n' \
        -H "X-Api-Key: $KEY" -H 'content-type: application/json' -X POST http://weblens-app:8080/v1/fetch \
        -d "{\"url\":\"$1\",\"options\":{\"bypassCache\":true}}" >"$2" 2>&1 || echo "000 0" >"$2"
}

docker compose -f compose.yml -f deploy/load-check.compose.yml up -d app >/dev/null
healthy
fetch "https://example.org/" "$WORK/warm" # launch the browser before measuring
CONTEXTS="$(docker exec weblens-app printenv WebLens__Fetch__Browser__MaxConcurrentContexts)"

if [ "${1:-load}" = shutdown ]; then
    fetch "${TARGETS[0]}?shutdown" "$WORK/running" &
    sleep 2
    started=$(date +%s)
    docker stop weblens-app >/dev/null
    wait
    echo "fetch during shutdown: $(cat "$WORK/running")   (expected 200 or 422: it finished, not 000)"
    echo "container stopped in $(( $(date +%s) - started )) s with exit code $(docker inspect weblens-app --format '{{.State.ExitCode}}')   (expected 0)"
    exit 0
fi

baseline=$(memory_mib)
total=$((CONTEXTS + N))
echo "MaxConcurrentContexts=$CONTEXTS, firing $total simultaneous fetches across ${#TARGETS[@]} origins; baseline ${baseline} MiB"
peak=$baseline
for i in $(seq 1 "$total"); do fetch "${TARGETS[$(( (i - 1) % ${#TARGETS[@]} ))]}?i=$i" "$WORK/r$i" & done
while jobs -r | grep -q .; do m=$(memory_mib); [ "$m" -gt "$peak" ] && peak=$m; sleep 1; done
wait

refused=0; served=0; slow_refusals=0
for i in $(seq 1 "$total"); do
    read -r code secs <"$WORK/r$i"
    echo "  fetch $i: HTTP $code in ${secs}s"
    if [ "$code" = 503 ]; then
        refused=$((refused + 1))
        awk -v s="$secs" 'BEGIN { exit !(s > 4) }' && slow_refusals=$((slow_refusals + 1))
    elif [ "$code" != 000 ] && [ "$code" != 429 ]; then
        served=$((served + 1))
    fi
done

sleep 15
after=$(memory_mib)
echo "served $served, refused $refused (expected $N), refusals slower than 4 s: $slow_refusals"
echo "memory: baseline ${baseline} MiB, peak ${peak} MiB, 15 s after ${after} MiB"
[ "$refused" -eq "$N" ] && [ "$served" -eq "$CONTEXTS" ] && [ "$slow_refusals" -eq 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
