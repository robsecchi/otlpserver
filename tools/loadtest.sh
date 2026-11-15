#!/usr/bin/env bash
# -----------------------------------------------------------------------------------------------
# Baseline load test: runs a fixed set of scenarios against a Release build of the server and
# prints one markdown table row per scenario.
#
#   tools/loadtest.sh                 # all scenarios, 15 s each (+5 s warmup)
#   DURATION=30 tools/loadtest.sh     # longer runs
#
# The server is restarted for every scenario, so each peak-memory number is its own.
# Refused exports are retried after the server's retry delay, like real OTel exporters, except
# in the scenario marked "NO backoff". GC columns come from the server's runtime counters.
# Both server and load generator run on THIS machine: they compete for the same CPU cores,
# so absolute numbers are a lower bound of what a dedicated server would do.
# -----------------------------------------------------------------------------------------------
set -euo pipefail
cd "$(dirname "$0")/.."

DURATION="${DURATION:-15}"
WARMUP="${WARMUP:-5}"
SERVER_DIR="src/OtlpServer/bin/Release/net10.0"
LOADGEN="tools/OtlpLoadGen/bin/Release/net10.0/OtlpLoadGen.dll"
API_KEY="loadtest-key"
API_KEY_HASH="$(printf %s "$API_KEY" | shasum -a 256 | cut -d' ' -f1)"

dotnet build -c Release src/OtlpServer >/dev/null
dotnet build -c Release tools/OtlpLoadGen >/dev/null

# run_scenario "<server env vars>" <loadgen args...>
run_scenario() {
  local server_env="$1"; shift
  (cd "$SERVER_DIR" && exec env ASPNETCORE_ENVIRONMENT=Production $server_env dotnet OtlpServer.dll >/dev/null 2>&1) &
  local pid=$!
  # Wait until both ports accept connections.
  for _ in $(seq 1 100); do
    if nc -z localhost 4317 2>/dev/null && nc -z localhost 4318 2>/dev/null; then break; fi
    sleep 0.1
  done
  dotnet "$LOADGEN" --duration "$DURATION" --warmup "$WARMUP" --server-pid "$pid" --row "$@" || true
  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null || true
}

AUTH_ENV="Otlp__Security__Authentication__Mode=ApiKey Otlp__Security__Authentication__Clients__0__Name=loadtest Otlp__Security__Authentication__Clients__0__KeyHashes__0=$API_KEY_HASH"
QUIET_ENV="Logging__LogLevel__OtlpServer=Warning"

echo "| scenario | exports/s | records/s | wire MB/s | p50 ms | p95 ms | p99 ms | errors | server cores | server peak RSS MB | GC heap peak MB | GC committed peak MB | % time in GC | KB alloc/export | gen-2 GCs | loadgen cores |"
echo "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"

# gRPC protobuf, batch 512: scaling the number of concurrent exports (one HTTP/2 connection).
run_scenario "" --transport grpc --batch 512 --concurrency 1
run_scenario "" --transport grpc --batch 512 --concurrency 8
run_scenario "" --transport grpc --batch 512 --concurrency 32
run_scenario "" --transport grpc --batch 512 --concurrency 64
run_scenario "" --transport grpc --batch 512 --concurrency 128 --label "grpc/protobuf b512 c128 (over limit)"
# Worst case: a client that ignores the retry delay and retries at once (a retry storm).
run_scenario "" --transport grpc --batch 512 --concurrency 128 --no-backoff --label "grpc/protobuf b512 c128, NO backoff (retry storm)"

# Same load, per-export Information log line silenced: what does that log line cost?
run_scenario "$QUIET_ENV" --transport grpc --batch 512 --concurrency 32 --label "grpc/protobuf b512 c32, quiet logs"

# Other encodings / transports at the same concurrency.
run_scenario "" --transport http --encoding protobuf --batch 512 --concurrency 32
run_scenario "" --transport http --encoding json --batch 512 --concurrency 32
run_scenario "" --transport http --encoding protobuf --gzip --batch 512 --concurrency 32
run_scenario "" --transport grpc --gzip --batch 512 --concurrency 32

# Big batches (Collector-sized): fewer, heavier exports.
run_scenario "" --transport grpc --batch 8192 --concurrency 8

# Authentication overhead.
run_scenario "$AUTH_ENV" --transport grpc --batch 512 --concurrency 32 --api-key "$API_KEY" --label "grpc/protobuf b512 c32, API key"
