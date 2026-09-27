#!/usr/bin/env bash
# Uncached fetch benchmark: a fixed page list fetched with bypassCache, RUNS times each, reporting
# per page the median of each stage. A manual check against public pages, not an automated test.
#
#   deploy/benchmark.sh [result.json]            run it; prints a table and writes the result (default: a dated file
#                                                under deploy/benchmarks/)
#   deploy/benchmark.sh compare OLD.json NEW.json  per-page change in median total time, and every page whose
#                                                markdownChars or qualityScore went down
#
# BROWSER=1 renders every page in the browser (a ready selector of "body" skips the HTTP-first attempt): for trials of
# the browser path, which otherwise answers only app shells.
#
# BASE is the WebLens to measure and KEY an API key with the fetch scope, e.g. against a local development host:
#   BASE=http://127.0.0.1:5195 KEY=weblens-dev-key deploy/benchmark.sh
# The default key profile allows 20 fetches a minute (one token every 3 s, burst 5; cache hits are free, but every fetch here
# bypasses the cache). Fetches are spaced PACE seconds apart to stay under that; a 429 in the output means the pacing is
# wrong, not that the page is slow. Timings are medians of the runs that answered 200.
set -euo pipefail
cd "$(dirname "$0")/.."

# Aligns tab-separated columns (util-linux `column` is not on every box).
align() { awk -F'\t' '{ for (i = 1; i <= NF; i++) { cell[NR, i] = $i; if (length($i) > w[i]) w[i] = length($i) } if (NF > n) n = NF }
    END { for (r = 1; r <= NR; r++) { line = ""; for (i = 1; i <= n; i++) line = line sprintf("%-" w[i] + 2 "s", cell[r, i]); sub(/ +$/, "", line); print line } }'; }


if [ "${1:-}" = compare ]; then
    old="$2" new="$3"
    jq -r -n --slurpfile old "$old" --slurpfile new "$new" '
        ($old[0].pages | map({key: .url, value: .}) | from_entries) as $o
        | ["page", "old s", "new s", "change", "flags"], ($new[0].pages[] | . as $n | $o[$n.url] as $p
            | [$n.url,
               ($p.totalSeconds // "-" | tostring),
               ($n.totalSeconds // "-" | tostring),
               (if $p.totalSeconds and $n.totalSeconds then ((($n.totalSeconds - $p.totalSeconds) * 100 / $p.totalSeconds) | round | tostring) + "%" else "-" end),
               ([if $p == null then "new page" else empty end,
                 if $p and $p.status != $n.status then "status \($p.status) -> \($n.status)" else empty end,
                 if $p.markdownChars and $n.markdownChars and $n.markdownChars < $p.markdownChars then "markdownChars \($p.markdownChars) -> \($n.markdownChars)" else empty end,
                 if $p.qualityScore and $n.qualityScore and $n.qualityScore < $p.qualityScore then "qualityScore \($p.qualityScore) -> \($n.qualityScore)" else empty end]
                | join(", "))])
        | @tsv' | align
    exit 0
fi

BASE="${BASE:-http://127.0.0.1:5195}"
KEY="${KEY:?set KEY to an API key with the fetch scope}"
RUNS="${RUNS:-3}"
PACE="${PACE:-3.2}"
OPTIONS='"bypassCache":true'
[ "${BROWSER:-}" = 1 ] && OPTIONS="$OPTIONS"',"readySelector":"body"'
read -r -a PAGES <<<"${PAGES:-https://example.com/ https://en.wikipedia.org/wiki/Markdown https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview https://github.com/microsoft/playwright https://www.bbc.com/news https://news.ycombinator.com/ https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/429 https://www.theverge.com/ https://blog.cloudflare.com/ https://docs.python.org/3/library/asyncio.html https://www.twitch.tv/directory}"
OUT="${1:-deploy/benchmarks/$(date -u +%Y%m%dT%H%M%SZ).json}"
mkdir -p "$(dirname "$OUT")"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "benchmark: ${#PAGES[@]} pages x $RUNS runs against $BASE, one fetch every ${PACE}s${BROWSER:+, browser-forced}" >&2
for page in "${PAGES[@]}"; do
    for run in $(seq "$RUNS"); do
        body="$WORK/body.json"
        meta="$(curl -s -m 90 -o "$body" -w '%{http_code} %{time_total}' -H "X-Api-Key: $KEY" -H 'content-type: application/json' \
            -X POST "$BASE/v1/fetch" -d "{\"url\":\"$page\",\"options\":{$OPTIONS}}" || echo "000 0")"
        read -r status seconds <<<"$meta"
        jq -c -n --arg url "$page" --argjson run "$run" --argjson status "$status" --argjson seconds "$seconds" \
            --slurpfile body <(jq -c . "$body" 2>/dev/null || echo '{}') \
            '{url: $url, run: $run, status: $status, totalSeconds: $seconds, problem: ($body[0].type // null)} + ($body[0].diagnostics // {})' \
            >>"$WORK/runs.jsonl"
        echo "  $status ${seconds}s  $page (run $run)" >&2
        sleep "$PACE"
    done
done

# One line per page: the median of every number across its runs, the most common status.
jq -s '
    def median: map(select(. != null)) | sort | if length == 0 then null else .[(length - 1) / 2 | floor] end;
    group_by(.url) | map((map(select(.status == 200))) as $ok | {
        url: .[0].url,
        status: (if ($ok | length) > 0 then 200 else (map(.status) | group_by(.) | max_by(length)[0]) end),
        problem: (if ($ok | length) > 0 then null else (map(.problem) | map(select(. != null)) | first) end),
        renderer: ($ok | map(.renderer) | map(select(. != null)) | first),
        totalSeconds: (if ($ok | length) > 0 then ($ok | map(.totalSeconds) | median) else (map(.totalSeconds) | median) end),
        renderMs: ($ok | map(.renderMs) | median),
        navigationMs: ($ok | map(.navigationMs) | median),
        readinessMs: ($ok | map(.readinessMs) | median),
        extractMs: ($ok | map(.extractMs) | median),
        convertMs: ($ok | map(.convertMs) | median),
        readinessTimedOut: ($ok | map(.readinessTimedOut) | any),
        markdownChars: ($ok | map(.markdownChars) | median),
        qualityScore: ($ok | map(.qualityScore) | median),
        runs: length,
        ok: ($ok | length)})' "$WORK/runs.jsonl" \
    | jq --arg base "$BASE" --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --argjson runs "$RUNS" \
        '{at: $at, base: $base, runs: $runs, medianTotalSeconds: (map(select(.status == 200) | .totalSeconds) | sort | if length == 0 then null else .[(length - 1) / 2 | floor] end), pages: .}' \
        >"$OUT"

jq -r '["page", "status", "renderer", "total s", "nav ms", "ready ms", "extract ms", "timed out", "chars", "quality"],
    (.pages[] | [.url, (.status | tostring) + (if .problem then " " + (.problem | sub("urn:weblens:problem:"; "")) else "" end),
        .renderer // "-", .totalSeconds, .navigationMs // "-", .readinessMs // "-", .extractMs // "-", .readinessTimedOut, .markdownChars // "-", .qualityScore // "-"]
        | map(tostring)) | @tsv' "$OUT" | align
echo "median total of successful pages: $(jq .medianTotalSeconds "$OUT")s   written to $OUT"
