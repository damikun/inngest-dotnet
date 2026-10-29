#!/usr/bin/env bash
# Runs the containerized sample in each mode: brings the compose stack up, checks every flow, drives the load, keeps
# the app's log and removes everything it created. Results go to docs/results/docker and docs/results/load.
#
#   benchmarks/Inngest.Soak/run-docker-checks.sh [dev|cloud ...]   (both modes when none is given)
#
# RUNS and CONCURRENCY size the load (2000 and 300); INNGEST_HOST_PORT is the server's port on the host (18288).
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
compose_dir="$root/samples/Inngest.Sample"
export INNGEST_HOST_PORT="${INNGEST_HOST_PORT:-18288}"
runs="${RUNS:-2000}"
concurrency="${CONCURRENCY:-300}"
modes=("$@")
[ ${#modes[@]} -eq 0 ] && modes=(dev cloud)

dotnet build -c Release "$root/benchmarks/Inngest.Soak/Inngest.Soak.csproj" > /dev/null
soak() { dotnet run -c Release --no-build --project "$root/benchmarks/Inngest.Soak" -- "$@"; }

for mode in "${modes[@]}"; do
  if [ "$mode" = cloud ]; then
    # inngest start wants the signing key as plain hex; the app and the server read the same two variables.
    export INNGEST_SIGNING_KEY="$(openssl rand -hex 32)"
    export INNGEST_EVENT_KEY="$(openssl rand -hex 16)"
    server=inngest-sample-inngest-1
    app=inngest-sample-sample-1
  else
    unset INNGEST_SIGNING_KEY INNGEST_EVENT_KEY
    server=inngest-sample-inngest-dev-1
    app=inngest-sample-sample-dev-1
  fi

  cleanup() { (cd "$compose_dir" && docker compose --profile "$mode" down --volumes --remove-orphans > /dev/null 2>&1) || true; }
  trap cleanup EXIT
  echo "== $mode: $(uptime)"
  (cd "$compose_dir" && docker compose --profile "$mode" up --build --detach --wait)

  status=0
  soak flows --mode "$mode" --inngest "http://127.0.0.1:$INNGEST_HOST_PORT" --event-key "${INNGEST_EVENT_KEY:-dev}" \
    --server-container "$server" --app-container "$app" --out "$root/docs/results/docker" || status=$?
  if [ $status -eq 0 ]; then
    soak load --mode "$mode" --runs "$runs" --concurrency "$concurrency" --inngest "http://127.0.0.1:$INNGEST_HOST_PORT" \
      --event-key "${INNGEST_EVENT_KEY:-dev}" --server-container "$server" --app-container "$app" --out "$root/docs/results/load" || status=$?
  fi

  # The app's whole log; the server's warnings and errors are in server-problems.log, its full log is too large.
  mkdir -p "$root/docs/results/load/$mode"
  docker logs "$app" > "$root/docs/results/load/$mode/app.log" 2>&1 || true
  echo "== $mode done (status $status): $(uptime)"
  cleanup
  trap - EXIT
  [ $status -eq 0 ] || exit $status
done
